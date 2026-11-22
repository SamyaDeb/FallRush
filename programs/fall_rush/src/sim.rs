//! P2: the on-chain simulation (doc 19 §5 P2). The program runs `rush-core` as the game server inside the
//! Ephemeral Rollup: a timed task calls `tick()`; each call runs the next slice of the tick (`world::step_slice`:
//! players in slot order while the deterministic work meter stays under `SLICE_WORK`), so one task execution stays
//! inside its 400k CU budget and a heavy tick simply takes more executions.
//!
//! Accounts (zero-copy, all ≤ 10,240 B, delegated and reused across matches): `Arena` (roster, map keys, task),
//! `WorldAcc` (rush-core `World` bytes + tick bookkeeping), one `InputBuf` per slot, `EventLog`. Map blobs are plain
//! program-owned accounts (not delegated; the rollup clones them read-only). The per-match `Match` receives the
//! round results and is the only account committed to Solana.
//!
//! **The hot tick never fails**: every account is checked by hand and any problem is a logged no-op (`Ok`), so a
//! bad account list or an early call can never end the timed task (P0 §4.4).

use anchor_lang::prelude::*;
use anchor_lang::solana_program::instruction::{AccountMeta, Instruction};
use anchor_lang::InstructionData;
use magicblock_magic_program_api::args::ScheduleTaskArgs;
use magicblock_magic_program_api::instruction::MagicBlockInstruction;
use rush_core::collide::Scene;
use rush_core::config::MovementParams;
use rush_core::input::InputCmd;
use rush_core::mapblob::MapView;
use rush_core::world::{StepEvents, World, MAX_PLAYERS};

use crate::errors::FallRushError;
use crate::state::*;

pub const ARENA_SEED: &[u8] = b"arena";
pub const WORLD_SEED: &[u8] = b"world";
pub const INPUT_SEED: &[u8] = b"in";
pub const EVLOG_SEED: &[u8] = b"ev";

/// Map blob account header: magic 8 | authority 32 | len 4 | sealed 1 | map_id 1 | pad 2 | sha256 32 = 80 B.
pub const MAP_MAGIC: [u8; 8] = *b"FRMAPBLB";
pub const MAP_HDR: usize = 80;
/// Upload chunk size (≤ 900 B per transaction, P0).
pub const MAP_CHUNK: usize = 900;

pub const N: usize = MAX_PLAYERS;
pub const RING: usize = 16;
pub const EV_N: usize = 400;
pub const WORLD_BYTES: usize = 2048;
/// Map slots in `Arena::maps`: deal map ids 0..3, then the lobby (254).
pub const MAP_SLOTS: usize = 5;
pub const MAP_LOBBY_SLOT: usize = 4;
/// Inputs per `submit_input` (30 Hz: 3 ticks per transaction, 120 tx/s for 12 players; P2 0.1).
pub const INPUT_BATCH: usize = 3;
/// `submit_input` accepts ticks in [World.tick − 8, World.tick + 8 + batch].
pub const INPUT_WINDOW: u32 = 8;
/// A new tick (part 0) starts at most once per this many slots (devnet-as slots are 10 ms → ≤ 33.3 Hz; the task
/// interval sets the real pace). Parts 1.. run as soon as they are pending.
pub const MIN_SLOTS_PER_TICK: u64 = 3;

/// `Match::flags` bit set by `start_sim` (bits 1..=4 are `commit_round`'s per-round marks, bit 0 = unranked).
pub const MATCH_SIM_STARTED: u8 = 0x20;

pub mod arena_status {
    pub const IDLE: u8 = 0;
    pub const LOBBY: u8 = 1;
    pub const RUNNING: u8 = 2;
    pub const DONE: u8 = 3;
    pub const ABORTED: u8 = 4;
}

/// Tick outcome codes, logged (and counted) for the keeper and the proof.
pub mod tick_code {
    pub const RAN: u8 = 0;
    pub const NOT_DUE: u8 = 1;
    pub const NOT_RUNNING: u8 = 2;
    pub const BAD_ACCOUNTS: u8 = 3;
    pub const BAD_STATE: u8 = 4;
}

#[account(zero_copy)]
#[repr(C)]
pub struct Arena {
    pub authority: Pubkey,
    /// The current match's `Match` PDA.
    pub match_account: Pubkey,
    /// Map blob accounts: deal map ids 0..3, then the lobby.
    pub maps: [Pubkey; MAP_SLOTS],
    /// Slot owners (the key allowed to write that slot's InputBuf); default = bot.
    pub owners: [Pubkey; N],
    /// InputBuf of each slot (checked in `tick` instead of re-deriving PDAs).
    pub inputs: [Pubkey; N],
    pub task_id: i64,
    pub nonce: u64,
    pub matches_run: u64,
    pub id: u8,
    pub status: u8,
    /// Parts per tick (P2 split tick; 1..=12).
    pub parts: u8,
    pub bump: u8,
    pub human_mask: u16,
    pub cosmetic: [u8; N],
    pub _pad: [u8; 6],
}

#[account(zero_copy)]
#[repr(C)]
pub struct WorldAcc {
    pub arena: Pubkey,
    /// Slot of the last part 0 (tick start).
    pub last_slot: u64,
    pub ticks_run: u64,
    pub executions: u64,
    pub not_due: u64,
    /// Players that hit their work budget (rush-core `StepEvents::capped`), total.
    pub budget_hit: u64,
    pub applied_tick: [u32; N],
    /// rush-core World tick (mirror) and canonical World byte length.
    pub tick: u32,
    pub len: u32,
    pub part_pending: u8,
    pub parts: u8,
    /// Director phase / round mirrors (cheap reads for the keeper and clients).
    pub phase: u8,
    pub round: u8,
    /// Last round whose result was written into `Match` (255 = none), last director phase seen.
    pub results_round: u8,
    pub last_code: u8,
    pub _pad: [u8; 2],
    /// The inputs latched for the tick in progress (part 0 reads the InputBufs; later parts reuse them).
    pub cur: [[u8; 4]; N],
    pub cur_some: u16,
    pub _pad2: [u8; 6],
    pub world: [u8; WORLD_BYTES],
}

#[zero_copy]
#[repr(C)]
pub struct InEntry {
    pub tick: u32,
    pub input: [u8; 4],
}

#[account(zero_copy)]
#[repr(C)]
pub struct InputBuf {
    pub arena: Pubkey,
    pub owner: Pubkey,
    pub latest: u32,
    pub count: u32,
    pub slot: u8,
    pub _pad: [u8; 7],
    pub ring: [InEntry; RING],
}

#[zero_copy]
#[repr(C)]
pub struct EvE {
    pub tick: u32,
    pub seq: u32,
    pub arg: i32,
    pub kind: u8,
    pub tag: u8,
    pub slot: u8,
    pub _pad: u8,
}

#[account(zero_copy)]
#[repr(C)]
pub struct EventLog {
    pub arena: Pubkey,
    pub head: u32,
    pub _pad: u32,
    pub e: [EvE; EV_N],
}

// ------------------------------------------------------------------ hashing / heap helpers

/// rush-core's `Hasher` on the sha256 syscall.
pub struct ProgHasher;

impl rush_core::world::Hasher for ProgHasher {
    fn sha256(&mut self, parts: &[&[u8]]) -> [u8; 32] {
        solana_sha256_hasher::hashv(parts).to_bytes()
    }
}

/// A heap `World` (≈ 2 KB; sBPF stack frames are 4 KB). Every field of `World` is an integer / bool / array of
/// them, so the zeroed bytes are a valid value; `load_from` then fills it.
#[inline(never)]
pub fn heap_world() -> Box<World> {
    let layout = std::alloc::Layout::new::<World>();
    // SAFETY: World is plain integer data (no references / niches beyond bool, for which 0 is `false`).
    unsafe {
        let p = std::alloc::alloc_zeroed(layout) as *mut World;
        if p.is_null() {
            std::alloc::handle_alloc_error(layout);
        }
        Box::from_raw(p)
    }
}

/// A heap `Scene` (≈ 3.8 KB), built in its own frame.
#[inline(never)]
pub fn heap_scene(v: MapView) -> Box<Scene> {
    Box::new(Scene::new(v))
}

/// Map blob bytes after the header, if the account is a sealed blob of this program.
pub fn map_bytes<'a>(info: &AccountInfo, d: &'a [u8]) -> Option<&'a [u8]> {
    if info.owner != &crate::ID || d.len() < MAP_HDR || d[0..8] != MAP_MAGIC || d[44] != 1 {
        return None;
    }
    let len = u32::from_le_bytes([d[40], d[41], d[42], d[43]]) as usize;
    d.get(MAP_HDR..MAP_HDR + len)
}

fn zero_copy_mut<'a, T: bytemuck::Pod + anchor_lang::Discriminator>(info: &'a AccountInfo) -> Option<core::cell::RefMut<'a, T>> {
    if info.owner != &crate::ID || !info.is_writable {
        return None;
    }
    let d = info.try_borrow_mut_data().ok()?;
    if d.len() < 8 + core::mem::size_of::<T>() || &d[..8] != T::DISCRIMINATOR {
        return None;
    }
    Some(core::cell::RefMut::map(d, |d| bytemuck::from_bytes_mut(&mut d[8..8 + core::mem::size_of::<T>()])))
}

fn zero_copy_ref<'a, T: bytemuck::Pod + anchor_lang::Discriminator>(info: &'a AccountInfo) -> Option<core::cell::Ref<'a, T>> {
    if info.owner != &crate::ID {
        return None;
    }
    let d = info.try_borrow_data().ok()?;
    if d.len() < 8 + core::mem::size_of::<T>() || &d[..8] != T::DISCRIMINATOR {
        return None;
    }
    Some(core::cell::Ref::map(d, |d| bytemuck::from_bytes(&d[8..8 + core::mem::size_of::<T>()])))
}

// ------------------------------------------------------------------ tick

/// `tick()`: accounts `[arena, world, event_log, match, map_0, map_1, map_2, map_3, map_lobby, input_0 … input_11]`.
/// Runs the next pending part of the current tick. Never returns an error (see the module doc).
pub fn tick(accs: &[AccountInfo]) -> Result<()> {
    let code = tick_inner(accs);
    if let Some(w) = accs.get(1).and_then(zero_copy_mut::<WorldAcc>) {
        let mut w = w;
        w.executions = w.executions.wrapping_add(1);
        w.last_code = code;
        if code == tick_code::NOT_DUE {
            w.not_due = w.not_due.wrapping_add(1);
        }
    }
    if code != tick_code::RAN {
        msg!("tick no-op {}", code);
    }
    Ok(())
}

#[inline(never)]
fn tick_inner(accs: &[AccountInfo]) -> u8 {
    if accs.len() < 9 + N {
        return tick_code::BAD_ACCOUNTS;
    }
    let arena = match zero_copy_ref::<Arena>(&accs[0]) {
        Some(a) => a,
        None => return tick_code::BAD_ACCOUNTS,
    };
    if arena.status != arena_status::RUNNING {
        return tick_code::NOT_RUNNING;
    }
    let mut wa = match zero_copy_mut::<WorldAcc>(&accs[1]) {
        Some(w) => w,
        None => return tick_code::BAD_ACCOUNTS,
    };
    if wa.arena != accs[0].key() || arena.match_account != accs[3].key() {
        return tick_code::BAD_ACCOUNTS;
    }
    let mut k = 0;
    while k < MAP_SLOTS {
        if accs[4 + k].key() != arena.maps[k] {
            return tick_code::BAD_ACCOUNTS;
        }
        k += 1;
    }
    let clock = match Clock::get() {
        Ok(c) => c,
        Err(_) => return tick_code::BAD_STATE,
    };
    // where the tick stands (rush-core `step_slice`): 0 = start a new tick, 1..=11 = continue at that slot,
    // 12 = the end-of-tick stage
    let part = wa.part_pending;
    if part == 0 && wa.ticks_run > 0 && clock.slot < wa.last_slot + MIN_SLOTS_PER_TICK {
        return tick_code::NOT_DUE;
    }
    let mut w = heap_world();
    if !w.load_from(&wa.world[..wa.len as usize]) {
        return tick_code::BAD_STATE;
    }
    let mut h = ProgHasher;
    // the map of this tick: the round map once the director asks for / has loaded it, else the lobby
    let map_slot = |w: &World| -> usize {
        let id = if w.round_index >= 1 { w.map_key } else { rush_core::deal::LOBBY };
        if (id as usize) < 4 {
            id as usize
        } else {
            MAP_LOBBY_SLOT
        }
    };
    if part == 0 {
        // latch inputs for tick T + 1
        let next = w.tick.wrapping_add(1);
        let mut some = 0u16;
        let mut s = 0;
        while s < N {
            let info = &accs[9 + s];
            if info.key() != arena.inputs[s] {
                return tick_code::BAD_ACCOUNTS;
            }
            wa.cur[s] = [0; 4];
            if arena.human_mask >> s & 1 == 1 {
                if let Some(ib) = zero_copy_ref::<InputBuf>(info) {
                    let e = &ib.ring[next as usize % RING];
                    if e.tick == next {
                        wa.cur[s] = e.input;
                        some |= 1 << s;
                        wa.applied_tick[s] = next;
                    }
                }
            }
            s += 1;
        }
        wa.cur_some = some;
        // the director asks for the round's map: load it before the tick
        if let Some(id) = rush_core::rules::director::needs_map(&w) {
            let ms = if (id as usize) < 4 { id as usize } else { MAP_LOBBY_SLOT };
            let d = match accs[4 + ms].try_borrow_data() {
                Ok(d) => d,
                Err(_) => return tick_code::BAD_STATE,
            };
            let mb = match map_bytes(&accs[4 + ms], &d) {
                Some(b) => b,
                None => return tick_code::BAD_STATE,
            };
            let v = match MapView::new(mb) {
                Ok(v) => v,
                Err(_) => return tick_code::BAD_STATE,
            };
            rush_core::rules::director::load_round(&mut w, &v, &mut h);
        }
    }
    let ms = map_slot(&w);
    let d = match accs[4 + ms].try_borrow_data() {
        Ok(d) => d,
        Err(_) => return tick_code::BAD_STATE,
    };
    let mb = match map_bytes(&accs[4 + ms], &d) {
        Some(b) => b,
        None => return tick_code::BAD_STATE,
    };
    let v = match MapView::new(mb) {
        Ok(v) => v,
        Err(_) => return tick_code::BAD_STATE,
    };
    let mut inputs = [None; N];
    let mut s = 0;
    while s < N {
        if wa.cur_some >> s & 1 == 1 {
            let b = wa.cur[s];
            // rush-core sanitises in `InputTrack::consume`
            inputs[s] = Some(InputCmd { move_x: b[0] as i8, move_y: b[1] as i8, buttons: b[2], cam_yaw: (b[3] as u16) << 8 });
        }
        s += 1;
    }
    let mut sc = heap_scene(v);
    let mut ev: Box<StepEvents> = Box::default();
    let params = MovementParams::default();
    let phase_before = w.dir.phase;
    let next = rush_core::world::step_slice(&mut w, &inputs, &v, &params, &mut h, &mut ev, &mut sc, part, rush_core::world::SLICE_WORK);
    drop(sc);
    let n = w.to_bytes(&mut wa.world);
    wa.len = n as u32;
    wa.tick = w.tick;
    wa.budget_hit = wa.budget_hit.wrapping_add(ev.capped as u64);
    if part == 0 {
        wa.last_slot = clock.slot;
    }
    wa.part_pending = next;
    if next == 0 {
        wa.ticks_run = wa.ticks_run.wrapping_add(1);
    }
    wa.phase = w.dir.phase;
    wa.round = w.dir.round;
    // events → EventLog ring
    if ev.n > 0 {
        if let Some(mut log) = zero_copy_mut::<EventLog>(&accs[2]) {
            let mut i = 0;
            while i < ev.n.min(rush_core::world::MAX_EVENTS) {
                let e = ev.ev[i];
                let hd = log.head as usize % EV_N;
                log.e[hd] = EvE { tick: e.tick, seq: e.seq, arg: e.arg, kind: e.kind, tag: e.tag, slot: e.slot, _pad: e.round };
                log.head = log.head.wrapping_add(1);
                i += 1;
            }
        }
    }
    // director results → Match (only when the phase moved: rare)
    if w.dir.phase != phase_before {
        mirror_match(&accs[3], &w);
    }
    tick_code::RAN
}

/// Writes the director's state into the `Match` account: round, alive / eliminated / champion, transcript, deal,
/// status. Done on director phase changes only (a `Match` deserialize is ≈ 10k CU).
fn mirror_match(info: &AccountInfo, w: &World) {
    use rush_core::rules::director::phase as dp;
    if info.owner != &crate::ID || !info.is_writable {
        return;
    }
    let mut data = match info.try_borrow_mut_data() {
        Ok(d) => d,
        Err(_) => return,
    };
    let mut m: Match = match Match::try_deserialize(&mut &data[..]) {
        Ok(m) => m,
        Err(_) => return,
    };
    let d = &w.dir;
    m.current_round = d.round;
    m.player_count = d.player_count.max(m.player_count);
    m.transcript_hash = d.transcript;
    m.deal_maps = d.deal.maps;
    m.deal_variants = d.deal.variants;
    m.deal_modifiers = d.deal.modifiers;
    m.round_targets = d.targets;
    let mut i = 0;
    while i < N && i < MAX_SLOTS {
        let e = d.elim[i];
        m.slots[i] = if d.roster >> i & 1 == 0 {
            SlotResult::default()
        } else if e >> 4 != 0 {
            SlotResult { state: SLOT_ELIMINATED, round: e >> 4, placement: 0, reason: e & 0xF }
        } else {
            SlotResult { state: SLOT_ALIVE, round: d.round, placement: 0, reason: 0 }
        };
        i += 1;
    }
    m.status = match d.phase {
        dp::ROUND_INTRO | dp::ROUND_ACTIVE => MatchStatus::RoundActive,
        dp::ROUND_RESULT | dp::INTERMISSION => MatchStatus::RoundResolved,
        dp::CHAMPION | dp::FINALIZING | dp::DONE => {
            m.champion_slot = d.champion;
            if d.champion != 255 && (d.champion as usize) < MAX_SLOTS {
                m.slots[d.champion as usize].placement = 1;
            }
            MatchStatus::Completed
        }
        _ => m.status,
    };
    if m.status == MatchStatus::Completed && m.completed_at == 0 {
        m.completed_at = Clock::get().map(|c| c.unix_timestamp).unwrap_or(0);
    }
    let mut out: &mut [u8] = &mut data[..];
    let _ = m.try_serialize(&mut out);
}

// ------------------------------------------------------------------ inputs

/// `submit_input`: writes `INPUT_BATCH` inputs for ticks `start .. start + batch` (4 B each: move x, move y, buttons,
/// cam yaw high byte) into the signer's InputBuf. Rejects ticks outside the window (not the hot tick: a real error).
pub fn submit_input(ib: &mut InputBuf, owner: &Pubkey, world_tick: u32, start: u32, batch: &[u8; 4 * INPUT_BATCH]) -> Result<()> {
    require_keys_eq!(ib.owner, *owner, FallRushError::Unauthorized);
    require!(start + INPUT_WINDOW >= world_tick && start <= world_tick + INPUT_WINDOW, FallRushError::InputOutOfWindow);
    let mut k = 0;
    while k < INPUT_BATCH {
        let t = start + k as u32;
        let e = &mut ib.ring[t as usize % RING];
        e.tick = t;
        let raw = [batch[k * 4], batch[k * 4 + 1], batch[k * 4 + 2], batch[k * 4 + 3]];
        // sanitise on write too (rush-core rules), so a stored input is always a legal command
        let mut c = InputCmd { move_x: raw[0] as i8, move_y: raw[1] as i8, buttons: raw[2], cam_yaw: (raw[3] as u16) << 8 };
        rush_core::input::sanitize(&mut c);
        e.input = [c.move_x as u8, c.move_y as u8, c.buttons, (c.cam_yaw >> 8) as u8];
        k += 1;
    }
    ib.latest = ib.latest.max(start + INPUT_BATCH as u32 - 1);
    ib.count = ib.count.wrapping_add(1);
    Ok(())
}

// ------------------------------------------------------------------ match start

/// Resets the World to the lobby and starts the rush-core director for the arena's current match: roster from the
/// arena (owners = humans, the rest bots), seed = the match's VRF randomness.
pub fn start_sim(arena: &mut Arena, wa: &mut WorldAcc, m: &Match, lobby: &[u8]) -> Result<()> {
    require!(m.status == MatchStatus::Dealt, FallRushError::InvalidStatus);
    let v = MapView::new(lobby).map_err(|_| error!(FallRushError::BadMap))?;
    let mut w = heap_world();
    w.reset(&v, rush_core::config_data::MOVEMENT_V4.tick_rate_hz as u32);
    let mut h = ProgHasher;
    // roster: humans keep their slot; ids "bot:<slot>" / the owner's wallet (RosterHash)
    // "bot:<slot>" in decimal, as `rush-sim run` / Classic (RosterHash)
    let mut ids = [[0u8; 8]; N];
    let mut id_len = [0usize; N];
    let mut s = 0;
    while s < N {
        ids[s][..4].copy_from_slice(b"bot:");
        if s < 10 {
            ids[s][4] = b'0' + s as u8;
            id_len[s] = 5;
        } else {
            ids[s][4] = b'1';
            ids[s][5] = b'0' + (s - 10) as u8;
            id_len[s] = 6;
        }
        if arena.human_mask >> s & 1 == 1 {
            w.add_player(&v, s, false);
        }
        s += 1;
    }
    let wallets: [[u8; 32]; N] = core::array::from_fn(|i| arena.owners[i].to_bytes());
    let entries: [rush_core::transcript::RosterEntry; N] = core::array::from_fn(|i| {
        let human = arena.human_mask >> i & 1 == 1;
        rush_core::transcript::RosterEntry { bot: !human, user_id: if human { &wallets[i][..] } else { &ids[i][..id_len[i]] }, wallet: if human { Some(&wallets[i]) } else { None } }
    });
    let rh = rush_core::transcript::roster_hash(&mut h, &entries);
    rush_core::rules::director::start_match(&mut w, &v, &m.vrf_seed, N as u8, &rh, &mut h);
    let n = w.to_bytes(&mut wa.world);
    wa.len = n as u32;
    wa.tick = w.tick;
    wa.part_pending = 0;
    wa.parts = arena.parts.clamp(1, N as u8);
    wa.ticks_run = 0;
    wa.applied_tick = [0; N];
    wa.results_round = 255;
    wa.phase = w.dir.phase;
    wa.round = w.dir.round;
    arena.status = arena_status::RUNNING;
    arena.matches_run = arena.matches_run.wrapping_add(1);
    Ok(())
}

// ------------------------------------------------------------------ timed task (P0 §4.3, hand-built CPI)

/// task_id = FNV-1a64(program id ‖ world ‖ nonce LE) & i64::MAX.
pub fn task_id(world: &Pubkey, nonce: u64) -> i64 {
    let mut h: u64 = 0xcbf2_9ce4_8422_2325;
    for b in crate::ID.as_ref().iter().chain(world.as_ref().iter()).chain(nonce.to_le_bytes().iter()) {
        h ^= *b as u64;
        h = h.wrapping_mul(0x0100_0000_01b3);
    }
    (h & (i64::MAX as u64)) as i64
}

/// Schedules `tick()` every `interval_ms` for `iterations` executions. `accs` = the tick's account list (arena,
/// world, event log, match, 5 maps, 12 inputs) after the payer; the scheduled instruction has no signers.
pub fn schedule<'info>(payer: &AccountInfo<'info>, magic_program: &AccountInfo<'info>, tick_accs: &[AccountInfo<'info>], task_id: i64, interval_ms: i64, iterations: i64) -> Result<()> {
    let metas: Vec<AccountMeta> = tick_accs.iter().map(|a| AccountMeta { pubkey: a.key(), is_signer: false, is_writable: a.is_writable }).collect();
    let ix = Instruction { program_id: crate::ID, accounts: metas, data: crate::instruction::Tick {}.data() };
    let mut cpi_metas = vec![AccountMeta::new(payer.key(), true)];
    let mut infos = vec![payer.clone()];
    for a in tick_accs {
        cpi_metas.push(AccountMeta { pubkey: a.key(), is_signer: false, is_writable: a.is_writable });
        infos.push(a.clone());
    }
    let data = bincode::serialize(&MagicBlockInstruction::ScheduleTask(ScheduleTaskArgs { task_id, execution_interval_millis: interval_ms, iterations, instructions: vec![ix] }))
        .map_err(|_| error!(FallRushError::BadTask))?;
    anchor_lang::solana_program::program::invoke(&Instruction { program_id: magic_program.key(), accounts: cpi_metas, data }, &infos)?;
    msg!("fall_rush task_id {}", task_id);
    Ok(())
}

pub fn cancel<'info>(payer: &AccountInfo<'info>, magic_program: &AccountInfo<'info>, task_ctx: &AccountInfo<'info>, task_id: i64) -> Result<()> {
    let data = bincode::serialize(&MagicBlockInstruction::CancelTask { task_id }).map_err(|_| error!(FallRushError::BadTask))?;
    anchor_lang::solana_program::program::invoke(
        &Instruction { program_id: magic_program.key(), accounts: vec![AccountMeta::new(payer.key(), true), AccountMeta::new(task_ctx.key(), false)], data },
        &[payer.clone(), task_ctx.clone()],
    )?;
    Ok(())
}
