//! P0 spike program (throw-away, kept for the record). Measures the on-chain cost and timing of
//! running the integer sim (`p0_sim`) inside a MagicBlock Ephemeral Rollup:
//! - `tick()` reads 12 input buffers, runs one sim step, writes World + TickLog, records CU;
//! - `schedule_ticks()` drives `tick()` from the rollup's timed task (ScheduleTask CPI);
//! - `run_steps()` runs n steps from a seed for the sBPF = native = WASM hash check.
#![allow(unexpected_cfgs)]
#![allow(deprecated)]

use anchor_lang::prelude::*;
use anchor_lang::solana_program::instruction::{AccountMeta, Instruction};
use anchor_lang::InstructionData;
use ephemeral_rollups_sdk::anchor::{commit, delegate, ephemeral};
use ephemeral_rollups_sdk::cpi::DelegateConfig;
use magicblock_magic_program_api::args::ScheduleTaskArgs;
use magicblock_magic_program_api::instruction::MagicBlockInstruction;
use ephemeral_rollups_sdk::ephem::MagicIntentBundleBuilder;
use p0_sim::{bot_input, init_world as sim_init, map::Map, step, world_hash, Input, Stats, World, N};

declare_id!("8z4ofj7GeGLArXaYjbV2QXrdUqDdbutavqQSWXqpQBxy");

pub const MAP_MAGIC: [u8; 8] = *b"P0MAPBLB";
pub const MAP_HDR: usize = 48; // magic 8 | authority 32 | len 4 | pad 4
pub const LOG_N: usize = 400;
pub const RING: usize = 8;

pub const F_BOTS: u32 = 1; // slots that never submitted input run the scripted bot inside tick()

/// CU probe. The MagicBlock ER (local 0.14.10 / devnet 1.1.1) fails to clone a program that
/// imports `sol_remaining_compute_units` (InvalidAccountData on clone), so the default build logs
/// "Program consumption: N units remaining" via `sol_log_compute_units` and the client parses logs.
/// Feature `remaining-cu` restores the syscall (for solana-program-test / base layer only).
#[inline(always)]
fn cu() -> u64 {
    #[cfg(all(target_os = "solana", feature = "remaining-cu"))]
    {
        extern "C" {
            fn sol_remaining_compute_units() -> u64;
        }
        unsafe { sol_remaining_compute_units() }
    }
    #[cfg(not(all(target_os = "solana", feature = "remaining-cu")))]
    {
        #[cfg(target_os = "solana")]
        {
            extern "C" {
                fn sol_log_compute_units_();
            }
            unsafe { sol_log_compute_units_() }
        }
        0
    }
}

#[ephemeral]
#[program]
pub mod p0_tick {
    use super::*;

    /// Solana. World + TickLog PDAs.
    pub fn init_world(ctx: Context<InitWorld>, wid: u8, flags: u32, min_slot_gap: u32) -> Result<()> {
        let mut w = ctx.accounts.world.load_init()?;
        w.authority = ctx.accounts.payer.key();
        w.wid = wid;
        w.flags = flags;
        w.min_slot_gap = min_slot_gap;
        let mut l = ctx.accounts.log.load_init()?;
        l.authority = ctx.accounts.payer.key();
        Ok(())
    }

    /// Solana. One InputBuf per slot, written only by `owner`.
    pub fn init_input(ctx: Context<InitInput>, wid: u8, slot: u8, owner: Pubkey) -> Result<()> {
        require!((slot as usize) < N, P0Error::BadSlot);
        let _ = wid;
        let mut ib = ctx.accounts.input.load_init()?;
        ib.authority = ctx.accounts.payer.key();
        ib.owner = owner;
        ib.slot = slot;
        let mut w = ctx.accounts.world.load_mut()?;
        require_keys_eq!(w.authority, ctx.accounts.payer.key(), P0Error::Unauthorized);
        w.inputs[slot as usize] = ctx.accounts.input.key();
        Ok(())
    }

    /// Solana. Spectator "bet" account (64 B payload), for P0.3 load.
    pub fn init_bet(ctx: Context<InitBet>, owner: Pubkey) -> Result<()> {
        let mut b = ctx.accounts.bet.load_init()?;
        b.authority = ctx.accounts.payer.key();
        b.owner = owner;
        Ok(())
    }

    /// Solana. Writes a chunk of the map blob into a program-owned account created by the client
    /// (size = MAP_HDR + blob len). First call claims the account for the signer.
    pub fn upload_map_chunk(ctx: Context<UploadMap>, offset: u32, total_len: u32, data: Vec<u8>) -> Result<()> {
        let info = ctx.accounts.map.to_account_info();
        require_keys_eq!(*info.owner, crate::ID, P0Error::Unauthorized);
        let mut d = info.try_borrow_mut_data()?;
        if d[0..8] != MAP_MAGIC {
            require!(d[0..8].iter().all(|b| *b == 0), P0Error::Unauthorized);
            d[0..8].copy_from_slice(&MAP_MAGIC);
            d[8..40].copy_from_slice(ctx.accounts.authority.key().as_ref());
        }
        require!(d[8..40] == *ctx.accounts.authority.key().as_ref(), P0Error::Unauthorized);
        d[40..44].copy_from_slice(&total_len.to_le_bytes());
        let o = MAP_HDR + offset as usize;
        require!(o + data.len() <= d.len(), P0Error::BadMap);
        d[o..o + data.len()].copy_from_slice(&data);
        Ok(())
    }

    /// Solana. Generic delegation of one of this program's PDAs.
    /// kind 0 world [b"world", wid] · 1 log [b"log", wid] · 2 input [b"input", wid, slot] · 3 bet [b"bet", owner]
    pub fn delegate_pda(ctx: Context<DelegatePda>, kind: u8, wid: u8, slot: u8, owner: Pubkey) -> Result<()> {
        let validator = ctx.remaining_accounts.first().map(|a| a.key());
        let w = [wid];
        let s = [slot];
        let cfg = DelegateConfig { validator, ..Default::default() };
        match kind {
            0 => ctx.accounts.delegate_pda(&ctx.accounts.payer, &[b"world", &w], cfg)?,
            1 => ctx.accounts.delegate_pda(&ctx.accounts.payer, &[b"log", &w], cfg)?,
            2 => ctx.accounts.delegate_pda(&ctx.accounts.payer, &[b"input", &w, &s], cfg)?,
            3 => ctx.accounts.delegate_pda(&ctx.accounts.payer, &[b"bet", owner.as_ref()], cfg)?,
            _ => return err!(P0Error::BadSlot),
        }
        Ok(())
    }

    /// ER. Commit + undelegate every account in remaining_accounts (cleanup / refunds).
    pub fn undelegate<'info>(ctx: Context<'info, Undelegate<'info>>) -> Result<()> {
        let accs: Vec<AccountInfo> = ctx.remaining_accounts.to_vec();
        MagicIntentBundleBuilder::new(
            ctx.accounts.payer.to_account_info(),
            ctx.accounts.magic_context.to_account_info(),
            ctx.accounts.magic_program.to_account_info(),
        )
        .commit_and_undelegate(&accs)
        .build_and_invoke()?;
        Ok(())
    }

    /// Solana. Close a program-owned account whose first 32 data bytes (after the 8-byte
    /// discriminator, or after the 8-byte magic for map blobs) name the signer.
    pub fn close_any(ctx: Context<CloseAny>) -> Result<()> {
        let info = ctx.accounts.target.to_account_info();
        require_keys_eq!(*info.owner, crate::ID, P0Error::Unauthorized);
        {
            let d = info.try_borrow_data()?;
            require!(d.len() >= 40 && d[8..40] == *ctx.accounts.authority.key().as_ref(), P0Error::Unauthorized);
        }
        let dest = ctx.accounts.authority.to_account_info();
        let lamports = info.lamports();
        **dest.try_borrow_mut_lamports()? += lamports;
        **info.try_borrow_mut_lamports()? = 0;
        info.assign(&anchor_lang::solana_program::system_program::ID);
        info.resize(0)?;
        Ok(())
    }

    /// ER. Point the world at a map blob and reset the sim from `seed`.
    pub fn reset_world(ctx: Context<ResetWorld>, seed: u64, flags: u32, min_slot_gap: u32) -> Result<()> {
        let map_info = ctx.accounts.map.to_account_info();
        let md = map_info.try_borrow_data()?;
        let m = parse_map(&md)?;
        let mut w = ctx.accounts.world.load_mut()?;
        require_keys_eq!(w.authority, ctx.accounts.authority.key(), P0Error::Unauthorized);
        w.sim = sim_init(&m, seed);
        w.map = map_info.key();
        w.flags = flags;
        w.min_slot_gap = min_slot_gap;
        w.applied_tick = [0; N];
        w.hash = world_hash(&w.sim);
        w.ticks_run = 0;
        Ok(())
    }

    /// ER. Player input: 2 ticks per batch, stamped `start_tick`.
    pub fn submit_input(ctx: Context<SubmitInput>, start_tick: u32, inputs: [u8; 8]) -> Result<()> {
        let inputs: [Input; 2] = bytemuck::cast(inputs);
        let mut ib = ctx.accounts.input.load_mut()?;
        require_keys_eq!(ib.owner, ctx.accounts.owner.key(), P0Error::Unauthorized);
        for (k, inp) in inputs.iter().enumerate() {
            let t = start_tick.wrapping_add(k as u32);
            let e = &mut ib.ring[t as usize % RING];
            e.tick = t;
            e.input = *inp;
        }
        ib.latest = ib.latest.max(start_tick + 1);
        ib.count += 1;
        Ok(())
    }

    /// ER. Dummy spectator bet: writes 64 B.
    pub fn place_bet(ctx: Context<PlaceBet>, payload: [u8; 64]) -> Result<()> {
        let mut b = ctx.accounts.bet.load_mut()?;
        require_keys_eq!(b.owner, ctx.accounts.owner.key(), P0Error::Unauthorized);
        b.payload = payload;
        b.n += 1;
        Ok(())
    }

    /// ER. One sim step. Permissionless; when `min_slot_gap > 0` it only runs once the slot has
    /// advanced by that much since the last tick (the "anyone may tick when due" fallback).
    /// remaining_accounts = the 12 InputBufs in slot order (keys checked against World).
    pub fn tick<'info>(ctx: Context<'info, Tick<'info>>) -> Result<()> {
        do_tick(&ctx.accounts.world, &ctx.accounts.log, &ctx.accounts.map, ctx.remaining_accounts, 2)
    }

    /// ER. Half of a tick (2-instruction split, same transaction / same task):
    /// part 0 = guard + inputs + bots + players 0..6; part 1 = players 6..12 + pairs + tick log.
    pub fn tick_part<'info>(ctx: Context<'info, Tick<'info>>, part: u8) -> Result<()> {
        require!(part < 2, P0Error::BadSlot);
        do_tick(&ctx.accounts.world, &ctx.accounts.log, &ctx.accounts.map, ctx.remaining_accounts, part)
    }

    /// ER (or Solana). Runs `n` steps of scripted bots from the current World (reset with `seed`
    /// when `reset`), then stores the World hash. Used for sBPF = native = WASM.
    pub fn run_steps(ctx: Context<RunSteps>, n: u32, seed: u64, reset: bool) -> Result<()> {
        let map_info = ctx.accounts.map.to_account_info();
        let md = map_info.try_borrow_data()?;
        let m = parse_map(&md)?;
        let mut w = ctx.accounts.world.load_mut()?;
        let mut sim: World = if reset { sim_init(&m, seed) } else { w.sim };
        for _ in 0..n {
            let mut inputs = [Input::default(); N];
            for s in 0..N {
                inputs[s] = bot_input(s, sim.tick, sim.seed, &m, &sim.players[s]);
            }
            let mut st = Stats::default();
            step(&mut sim, &inputs, &m, &mut st);
        }
        w.sim = sim;
        w.hash = world_hash(&w.sim);
        w.map = map_info.key();
        Ok(())
    }

    /// ER/Solana. CU per collision primitive: `n` probes of the first collider of each kind, with
    /// CU logs between groups (box, prism, sphere, capsule, then isqrt). Client parses the logs.
    pub fn bench_prims(ctx: Context<BenchPrims>, n: u32) -> Result<()> {
        let map_info = ctx.accounts.map.to_account_info();
        let md = map_info.try_borrow_data()?;
        let m = parse_map(&md)?;
        let mut acc = 0i64;
        for kind in [p0_sim::map::K_BOX, p0_sim::map::K_PRISM, p0_sim::map::K_SPHERE, p0_sim::map::K_CAPSULE] {
            let ci = m.cols.iter().position(|c| c.kind == kind).unwrap_or(0);
            let c = m.cols[ci];
            cu();
            for k in 0..n as i32 {
                let s = p0_sim::collide::Seg { x: c.c[0] + 400 + k, z: c.c[2] + 300, ya: c.c[1] + 310, yb: c.c[1] + 690 };
                let r = p0_sim::collide::probe_static(&m, ci, &s);
                acc += r.touch(320) as i64 + r.e[0];
            }
            cu();
            for k in 0..n as i32 {
                let s = p0_sim::collide::Seg { x: c.c[0] + 400 + k, z: c.c[2] + 300, ya: c.c[1] + 310, yb: c.c[1] + 690 };
                let r = p0_sim::collide::probe_static(&m, ci, &s);
                acc += r.finish().dist as i64;
            }
        }
        cu();
        for k in 0..n as u64 {
            acc += p0_sim::fx::isqrt(1_000_000 + k * 7919) as i64;
        }
        cu();
        msg!("acc {}", acc);
        Ok(())
    }

    /// ER. Timed task that calls `tick()` every `interval_ms`, `iterations` times.
    /// task_id = FNV-1a(program id | world | nonce) & i64::MAX (unique across programs).
    pub fn schedule_ticks<'info>(ctx: Context<'info, ScheduleTicks<'info>>, interval_ms: i64, iterations: i64, nonce: u64, split: bool) -> Result<()> {
        let world_key = ctx.accounts.world.key();
        let mut h = p0_sim::fx::fnv1a(p0_sim::fx::FNV_OFFSET, crate::ID.as_ref());
        h = p0_sim::fx::fnv1a(h, world_key.as_ref());
        h = p0_sim::fx::fnv1a(h, &nonce.to_le_bytes());
        let task_id = (h & (i64::MAX as u64)) as i64;
        {
            let mut w = ctx.accounts.world.load_mut()?;
            require_keys_eq!(w.authority, ctx.accounts.payer.key(), P0Error::Unauthorized);
            w.task_id = task_id;
        }
        let mut metas = vec![
            AccountMeta::new(world_key, false),
            AccountMeta::new(ctx.accounts.log.key(), false),
            AccountMeta::new_readonly(ctx.accounts.map.key(), false),
        ];
        let mut infos = vec![
            ctx.accounts.payer.to_account_info(),
            ctx.accounts.world.to_account_info(),
            ctx.accounts.log.to_account_info(),
            ctx.accounts.map.to_account_info(),
        ];
        for a in ctx.remaining_accounts.iter() {
            metas.push(AccountMeta::new_readonly(a.key(), false));
            infos.push(a.clone());
        }
        let ixs = if split {
            vec![
                Instruction { program_id: crate::ID, accounts: metas.clone(), data: crate::instruction::TickPart { part: 0 }.data() },
                Instruction { program_id: crate::ID, accounts: metas, data: crate::instruction::TickPart { part: 1 }.data() },
            ]
        } else {
            vec![Instruction { program_id: crate::ID, accounts: metas, data: crate::instruction::Tick {}.data() }]
        };
        // Hand-built CPI (same bytes as ScheduleCrankCpi::instruction(); docs crank/implementation).
        let mut cpi_metas = vec![AccountMeta::new(ctx.accounts.payer.key(), true)];
        for ai in infos.iter().skip(1) {
            cpi_metas.push(AccountMeta { pubkey: ai.key(), is_signer: false, is_writable: ai.is_writable });
        }
        let data = bincode::serialize(&MagicBlockInstruction::ScheduleTask(ScheduleTaskArgs {
            task_id,
            execution_interval_millis: interval_ms,
            iterations,
            instructions: ixs,
        }))
        .map_err(|_| error!(P0Error::BadSlot))?;
        let cpi = Instruction { program_id: ctx.accounts.magic_program.key(), accounts: cpi_metas, data };
        anchor_lang::solana_program::program::invoke(&cpi, &infos)?;
        msg!("p0 task_id {}", task_id);
        Ok(())
    }

    /// ER. Cancel a task. remaining_accounts[0] = task context account (validator-specific).
    pub fn cancel_ticks<'info>(ctx: Context<'info, CancelTicks<'info>>, task_id: i64) -> Result<()> {
        let tc = ctx.remaining_accounts.first().ok_or(P0Error::BadSlot)?;
        let data = bincode::serialize(&MagicBlockInstruction::CancelTask { task_id }).map_err(|_| error!(P0Error::BadSlot))?;
        let cpi = Instruction {
            program_id: ctx.accounts.magic_program.key(),
            accounts: vec![AccountMeta::new(ctx.accounts.payer.key(), true), AccountMeta::new(tc.key(), false)],
            data,
        };
        anchor_lang::solana_program::program::invoke(&cpi, &[ctx.accounts.payer.to_account_info(), tc.clone()])?;
        Ok(())
    }
}

/// part: 0 = first half, 1 = second half, 2 = whole tick.
fn do_tick<'info>(
    world: &AccountLoader<'info, WorldAcc>,
    log: &AccountLoader<'info, TickLog>,
    map: &UncheckedAccount<'info>,
    rem: &[AccountInfo<'info>],
    part: u8,
) -> Result<()> {
    let c0 = cu();
    let clock = Clock::get()?;
    let map_info = map.to_account_info();
    let md = map_info.try_borrow_data()?;
    let m = parse_map(&md)?;
    let mut w = world.load_mut()?;
    require_keys_eq!(w.map, map_info.key(), P0Error::BadMap);
    let c1;
    let c2;
    let c3;
    if part != 1 {
        if w.min_slot_gap > 0 {
            require!(clock.slot >= w.last_slot + w.min_slot_gap as u64, P0Error::NotDue);
        }
        require!(rem.len() >= N, P0Error::BadSlot);
        c1 = cu();
        let tick_now = w.sim.tick;
        let mut inputs = [Input::default(); N];
        let mut human = [false; N];
        for s in 0..N {
            let a = &rem[s];
            require_keys_eq!(a.key(), w.inputs[s], P0Error::BadSlot);
            let d = a.try_borrow_data()?;
            let ib: &InputBuf = bytemuck::from_bytes(&d[8..8 + core::mem::size_of::<InputBuf>()]);
            if ib.count == 0 {
                continue;
            }
            human[s] = true;
            let mut best: Option<&InEntry> = None;
            for e in ib.ring.iter() {
                if e.tick <= tick_now && best.map_or(true, |b| e.tick > b.tick) {
                    best = Some(e);
                }
            }
            if let Some(e) = best {
                inputs[s] = e.input;
                if e.tick > w.applied_tick[s] {
                    w.applied_tick[s] = e.tick;
                }
            }
        }
        c2 = cu();
        if w.flags & F_BOTS != 0 {
            for s in 0..N {
                if !human[s] {
                    inputs[s] = bot_input(s, tick_now, w.sim.seed, &m, &w.sim.players[s]);
                }
            }
        }
        w.inputs_cur = inputs;
        c3 = cu();
    } else {
        require!(w.part_pending == 1, P0Error::NotDue);
        c1 = cu();
        c2 = c1;
        c3 = c1;
    }
    let inputs = w.inputs_cur;
    let mut st = Stats::default();
    let mut sim: World = w.sim;
    match part {
        0 => p0_sim::step_part(&mut sim, &inputs, &m, &mut st, 0, 6, false),
        1 => p0_sim::step_part(&mut sim, &inputs, &m, &mut st, 6, N, true),
        _ => step(&mut sim, &inputs, &m, &mut st),
    }
    w.sim = sim;
    let c4 = cu();
    w.narrow = st.narrow;
    if part == 0 {
        w.part_pending = 1;
        return Ok(());
    }
    w.part_pending = 0;
    w.last_slot = clock.slot;
    w.ticks_run += 1;
    let mut l = log.load_mut()?;
    let i = (l.head as usize) % LOG_N;
    l.e[i] = LogE { tick: w.sim.tick, cu_step: (c3 - c4) as u32, slot: clock.slot, unix_s: clock.unix_timestamp };
    l.head = l.head.wrapping_add(1);
    let c5 = cu();
    w.cu_total = (c0 - c5) as u32;
    let _ = (c2, c1);
    Ok(())
}

fn parse_map(d: &[u8]) -> Result<Map<'_>> {
    require!(d.len() > MAP_HDR && d[0..8] == MAP_MAGIC, P0Error::BadMap);
    let len = u32::from_le_bytes([d[40], d[41], d[42], d[43]]) as usize;
    require!(MAP_HDR + len <= d.len(), P0Error::BadMap);
    Map::parse(&d[MAP_HDR..MAP_HDR + len]).map_err(|_| error!(P0Error::BadMap))
}

// ---------------------------------------------------------------- accounts

#[account(zero_copy)]
#[repr(C)]
pub struct WorldAcc {
    pub authority: Pubkey,
    pub map: Pubkey,
    pub sim: World,
    pub inputs: [Pubkey; N],
    pub applied_tick: [u32; N],
    pub hash: u64,
    pub last_slot: u64,
    pub ticks_run: u64,
    pub task_id: i64,
    pub flags: u32,
    pub min_slot_gap: u32,
    pub cu_inputs: u32,
    pub cu_bots: u32,
    pub cu_step: u32,
    pub cu_total: u32,
    pub narrow: u32,
    pub wid: u8,
    pub part_pending: u8,
    pub _pad: [u8; 2],
    pub inputs_cur: [Input; N],
}

#[zero_copy]
#[repr(C)]
pub struct LogE {
    pub tick: u32,
    pub cu_step: u32,
    pub slot: u64,
    pub unix_s: i64,
}

#[account(zero_copy)]
#[repr(C)]
pub struct TickLog {
    pub authority: Pubkey,
    pub head: u32,
    pub _pad: u32,
    pub e: [LogE; LOG_N],
}

#[zero_copy]
#[repr(C)]
pub struct InEntry {
    pub tick: u32,
    pub input: Input,
}

#[account(zero_copy)]
#[repr(C)]
pub struct InputBuf {
    pub authority: Pubkey,
    pub owner: Pubkey,
    pub latest: u32,
    pub count: u32,
    pub slot: u8,
    pub _pad: [u8; 7],
    pub ring: [InEntry; RING],
}

#[account(zero_copy)]
#[repr(C)]
pub struct BetAcc {
    pub authority: Pubkey,
    pub owner: Pubkey,
    pub n: u64,
    pub payload: [u8; 64],
}

#[derive(Accounts)]
#[instruction(wid: u8)]
pub struct InitWorld<'info> {
    #[account(mut)]
    pub payer: Signer<'info>,
    #[account(init, payer = payer, space = 8 + core::mem::size_of::<WorldAcc>(), seeds = [b"world".as_ref(), &[wid]], bump)]
    pub world: AccountLoader<'info, WorldAcc>,
    #[account(init, payer = payer, space = 8 + core::mem::size_of::<TickLog>(), seeds = [b"log".as_ref(), &[wid]], bump)]
    pub log: AccountLoader<'info, TickLog>,
    pub system_program: Program<'info, System>,
}

#[derive(Accounts)]
#[instruction(wid: u8, slot: u8)]
pub struct InitInput<'info> {
    #[account(mut)]
    pub payer: Signer<'info>,
    #[account(mut, seeds = [b"world".as_ref(), &[wid]], bump)]
    pub world: AccountLoader<'info, WorldAcc>,
    #[account(init, payer = payer, space = 8 + core::mem::size_of::<InputBuf>(), seeds = [b"input".as_ref(), &[wid], &[slot]], bump)]
    pub input: AccountLoader<'info, InputBuf>,
    pub system_program: Program<'info, System>,
}

#[derive(Accounts)]
#[instruction(owner: Pubkey)]
pub struct InitBet<'info> {
    #[account(mut)]
    pub payer: Signer<'info>,
    #[account(init, payer = payer, space = 8 + core::mem::size_of::<BetAcc>(), seeds = [b"bet", owner.as_ref()], bump)]
    pub bet: AccountLoader<'info, BetAcc>,
    pub system_program: Program<'info, System>,
}

#[derive(Accounts)]
pub struct UploadMap<'info> {
    pub authority: Signer<'info>,
    /// CHECK: program-owned raw blob account; claimed on first write
    #[account(mut)]
    pub map: UncheckedAccount<'info>,
}

#[delegate]
#[derive(Accounts)]
pub struct DelegatePda<'info> {
    #[account(mut)]
    pub payer: Signer<'info>,
    /// CHECK: one of this program's PDAs; seeds given in the handler are checked by the delegation program
    #[account(mut, del)]
    pub pda: UncheckedAccount<'info>,
}

#[commit]
#[derive(Accounts)]
pub struct Undelegate<'info> {
    #[account(mut)]
    pub payer: Signer<'info>,
}

#[derive(Accounts)]
pub struct CloseAny<'info> {
    #[account(mut)]
    pub authority: Signer<'info>,
    /// CHECK: program-owned account whose stored authority must be the signer
    #[account(mut)]
    pub target: UncheckedAccount<'info>,
}

#[derive(Accounts)]
pub struct ResetWorld<'info> {
    pub authority: Signer<'info>,
    #[account(mut)]
    pub world: AccountLoader<'info, WorldAcc>,
    /// CHECK: map blob, parsed + magic-checked
    pub map: UncheckedAccount<'info>,
}

#[derive(Accounts)]
pub struct SubmitInput<'info> {
    pub owner: Signer<'info>,
    #[account(mut)]
    pub input: AccountLoader<'info, InputBuf>,
}

#[derive(Accounts)]
pub struct PlaceBet<'info> {
    pub owner: Signer<'info>,
    #[account(mut)]
    pub bet: AccountLoader<'info, BetAcc>,
}

#[derive(Accounts)]
pub struct Tick<'info> {
    #[account(mut)]
    pub world: AccountLoader<'info, WorldAcc>,
    #[account(mut)]
    pub log: AccountLoader<'info, TickLog>,
    /// CHECK: map blob, key checked against World
    pub map: UncheckedAccount<'info>,
}

#[derive(Accounts)]
pub struct RunSteps<'info> {
    #[account(mut)]
    pub world: AccountLoader<'info, WorldAcc>,
    /// CHECK: map blob, parsed + magic-checked
    pub map: UncheckedAccount<'info>,
}

#[derive(Accounts)]
pub struct BenchPrims<'info> {
    /// CHECK: map blob
    pub map: UncheckedAccount<'info>,
}

#[derive(Accounts)]
pub struct ScheduleTicks<'info> {
    #[account(mut)]
    pub payer: Signer<'info>,
    #[account(mut)]
    pub world: AccountLoader<'info, WorldAcc>,
    #[account(mut)]
    pub log: AccountLoader<'info, TickLog>,
    /// CHECK: map blob
    pub map: UncheckedAccount<'info>,
    /// CHECK: MagicBlock magic program
    #[account(address = ephemeral_rollups_sdk::consts::MAGIC_PROGRAM_ID)]
    pub magic_program: UncheckedAccount<'info>,
}

#[derive(Accounts)]
pub struct CancelTicks<'info> {
    #[account(mut)]
    pub payer: Signer<'info>,
    /// CHECK: MagicBlock magic program
    #[account(address = ephemeral_rollups_sdk::consts::MAGIC_PROGRAM_ID)]
    pub magic_program: UncheckedAccount<'info>,
}

#[error_code]
pub enum P0Error {
    #[msg("unauthorized")]
    Unauthorized,
    #[msg("bad slot / account list")]
    BadSlot,
    #[msg("bad map blob")]
    BadMap,
    #[msg("tick not due yet")]
    NotDue,
}
