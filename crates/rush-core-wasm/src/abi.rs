// The rush-core C ABI, shared verbatim by rush-core-wasm (wasm32 cdylib) and rush-core-ffi (native cdylib /
// staticlib for the Unity Editor and the Mac player). No wasm-bindgen: plain `extern "C"` over pointers + lengths.
//
// Handles index a thread-local table of contexts (map bytes + World). All pointers are caller-owned buffers.
//
//   rc_version() -> u32
//   rc_world_size() -> u32                       canonical World bytes upper bound
//   rc_init(map_ptr, map_len, cfg_ptr) -> i32    handle (≥ 0) or −1; cfg = 24 B (see `Cfg`)
//   rc_step(h, inputs_ptr) -> u32                inputs = 12 × 6 B { present, move_x, move_y, buttons, cam_yaw LE }; returns event count
//   rc_predict_player(h, slot, input_ptr, out_ptr) -> i32   steps only `slot` on a copy; writes 33 B MotorState
//   rc_hash(h) -> u64                            FNV-1a 64 of the canonical World (parity only)
//   rc_world_bytes(h, out_ptr, cap) -> u32 / rc_set_world(h, ptr, len) -> i32
//   rc_player(h, slot, out_ptr) -> i32           33 B MotorState + flags + respawn (36 B)
//   rc_vectors(kind, seed, n, map_ptr, map_len) -> u64
//   rc_set_player(h, slot, ptr) / rc_set_rate(h, rate) / rc_respawn(h, slot) / rc_clock(h)
//   rc_free(h)
//   rc_lids(h, out_ptr, cap) -> n                P5 views: every lid of every LID_FLIP grid at the World's tick, 9 B each {x i32, z i32, state u8}
//   rc_sweepers(h, out_ptr, cap) -> n            P5 views: 20 B each {pivot_x, pivot_z, arm, cos_q15, sin_q15} i32
//
// fullchain (a whole match through the director, the chain's `start_sim`; additive, the exports above are unchanged):
//   rc_chain_map(h, slot, ptr, len) -> i32       registers the map blob of program map slot 0..=3 (deal ids 0 Capsule Run,
//                                                1 Goo Tower, 2 Star Fountain, 3 Lid Flip) or 4 (the lobby); once a handle
//                                                has one, its current map follows the World (as the program's tick() picks it)
//   rc_chain_load(h) -> i32                      the program's map load before a tick (director::needs_map → load_round):
//                                                the deal id loaded, −1 none needed, −2 that map is not registered
//   rc_step_part(h, inputs_ptr, from, budget) -> i32   ONE program execution: `world::step_slice` from part `from` with
//                                                `budget` work units (map load first when from = 0); the next part, −2 no map
//   rc_step_chain(h, inputs_ptr, budget) -> i32  one whole tick exactly as the chain steps it (step_slice from part 0 until
//                                                it returns 0; any budget gives the same World); event count, −2 no map
//   rc_round(h) -> u32                           director round | phase << 8 | round_index << 16 | map_key << 24
use rush_core::codec::{Codec, Enc};
use rush_core::config::MovementParams;
use rush_core::input::InputCmd;
use rush_core::mapblob::MapView;
use rush_core::world::{self, Hasher, StepEvents, World, MAX_PLAYERS};
use std::cell::RefCell;

pub const ABI_VERSION: u32 = 1;

struct Ctx {
    map: Vec<u8>,
    w: World,
    params: MovementParams,
    /// fullchain: the map blobs of the program's map slots (0..=3 round maps, 4 = lobby), set by `rc_chain_map`.
    chain: [Option<Vec<u8>>; CHAIN_SLOTS],
    /// The chain slot `map` currently holds (`NO_SLOT` = the map given to `rc_init`).
    cur: usize,
}

/// The program's map slots (programs/fall_rush/src/sim.rs `MAP_SLOTS`, lobby = `MAP_LOBBY_SLOT` = 4).
const CHAIN_SLOTS: usize = 5;
const NO_SLOT: usize = usize::MAX;

thread_local! {
    static CTX: RefCell<Vec<Option<Ctx>>> = const { RefCell::new(Vec::new()) };
}

struct Sha;
impl Hasher for Sha {
    fn sha256(&mut self, parts: &[&[u8]]) -> [u8; 32] {
        use sha2::{Digest, Sha256};
        let mut h = Sha256::new();
        for p in parts {
            h.update(p);
        }
        h.finalize().into()
    }
}

unsafe fn slice<'a>(p: *const u8, n: usize) -> &'a [u8] {
    if p.is_null() || n == 0 {
        &[]
    } else {
        std::slice::from_raw_parts(p, n)
    }
}

fn with<R>(h: i32, f: impl FnOnce(&mut Ctx) -> R) -> Option<R> {
    CTX.with(|c| c.borrow_mut().get_mut(h as usize).and_then(|x| x.as_mut()).map(f))
}

fn read_cmd(b: &[u8]) -> Option<InputCmd> {
    if b[0] == 0 {
        None
    } else {
        Some(InputCmd::from_bytes(&b[1..6]))
    }
}

#[no_mangle]
pub extern "C" fn rc_version() -> u32 {
    (ABI_VERSION << 16) | rush_core::VERSION
}

#[no_mangle]
pub extern "C" fn rc_world_size() -> u32 {
    world::WORLD_MAX_BYTES as u32
}

/// cfg (24 B LE): rate u32, active mask u32, bot mask u32, flags u32 (reserved), hazard seed u64.
///
/// # Safety
/// `map_ptr` / `cfg_ptr` must point to `map_len` / 24 readable bytes.
#[no_mangle]
pub unsafe extern "C" fn rc_init(map_ptr: *const u8, map_len: u32, cfg_ptr: *const u8) -> i32 {
    let map = slice(map_ptr, map_len as usize).to_vec();
    let cfg = slice(cfg_ptr, 24);
    if cfg.len() < 24 {
        return -1;
    }
    let u = |o: usize| u32::from_le_bytes([cfg[o], cfg[o + 1], cfg[o + 2], cfg[o + 3]]);
    let rate = match u(0) {
        15 | 20 | 30 => u(0),
        _ => return -1,
    };
    let (active, bots) = (u(4), u(8));
    let seed = u64::from_le_bytes(cfg[16..24].try_into().unwrap());
    let view = match MapView::new(&map) {
        Ok(v) => v,
        Err(_) => return -1,
    };
    let mut w = World::new(&view, rate);
    w.hazard_seed = seed;
    for s in 0..MAX_PLAYERS {
        if active >> s & 1 == 1 {
            w.add_player(&view, s, bots >> s & 1 == 1);
        }
    }
    let ctx = Ctx { map, w, params: MovementParams::default(), chain: Default::default(), cur: NO_SLOT };
    CTX.with(|c| {
        let mut v = c.borrow_mut();
        if let Some(i) = v.iter().position(|x| x.is_none()) {
            v[i] = Some(ctx);
            i as i32
        } else {
            v.push(Some(ctx));
            (v.len() - 1) as i32
        }
    })
}

#[no_mangle]
pub extern "C" fn rc_free(h: i32) {
    CTX.with(|c| {
        if let Some(x) = c.borrow_mut().get_mut(h as usize) {
            *x = None;
        }
    });
}

/// # Safety
/// `inputs_ptr` must point to 72 readable bytes (or be null = all missing).
#[no_mangle]
pub unsafe extern "C" fn rc_step(h: i32, inputs_ptr: *const u8) -> u32 {
    let b = slice(inputs_ptr, MAX_PLAYERS * 6);
    with(h, |c| {
        let mut inputs = [None; MAX_PLAYERS];
        if b.len() == MAX_PLAYERS * 6 {
            for (s, inp) in inputs.iter_mut().enumerate() {
                *inp = read_cmd(&b[s * 6..s * 6 + 6]);
            }
        }
        let map = MapView::new(&c.map).unwrap();
        let mut ev = StepEvents::default();
        world::step(&mut c.w, &inputs, &map, &c.params, &mut Sha, &mut ev);
        ev.n as u32
    })
    .unwrap_or(0)
}

fn motor_bytes(w: &World, slot: usize, out: &mut [u8]) -> usize {
    let mut e = Enc::new(out);
    w.players[slot].m.enc(&mut e);
    e.u8(w.players[slot].flags);
    e.u8(w.players[slot].respawn_ticks);
    e.u8(w.players[slot].last_step);
    e.n
}

/// Prediction: steps only `slot` (motor + hazards, no separation, no rules) one tick ahead on a copy.
///
/// # Safety
/// `input_ptr` → 6 bytes, `out_ptr` → 36 writable bytes.
#[no_mangle]
pub unsafe extern "C" fn rc_predict_player(h: i32, slot: u32, input_ptr: *const u8, out_ptr: *mut u8) -> i32 {
    let slot = slot as usize;
    if slot >= MAX_PLAYERS || out_ptr.is_null() {
        return -1;
    }
    let inb = slice(input_ptr, 6);
    let out = std::slice::from_raw_parts_mut(out_ptr, 36);
    with(h, |c| {
        let map = MapView::new(&c.map).unwrap();
        let mut w = c.w;
        let tick = w.tick + 1;
        let cfg = w.motor_config(&c.params);
        let sc = world::pose_scene(&w, &map, &cfg, tick);
        let cmd = if inb.len() == 6 { read_cmd(inb).unwrap_or_default() } else { InputCmd::NEUTRAL };
        let mut m = w.players[slot].m;
        world::player_step(&cfg, &mut m, &cmd, &w, &map, &sc, tick);
        w.players[slot].m = m;
        motor_bytes(&w, slot, out) as i32
    })
    .unwrap_or(-1)
}

#[no_mangle]
pub extern "C" fn rc_hash(h: i32) -> u64 {
    with(h, |c| c.w.hash()).unwrap_or(0)
}

/// # Safety
/// `out_ptr` → `cap` writable bytes.
#[no_mangle]
pub unsafe extern "C" fn rc_world_bytes(h: i32, out_ptr: *mut u8, cap: u32) -> u32 {
    if out_ptr.is_null() {
        return 0;
    }
    let out = std::slice::from_raw_parts_mut(out_ptr, cap as usize);
    with(h, |c| c.w.to_bytes(out) as u32).unwrap_or(0)
}

/// # Safety
/// `ptr` → `len` readable bytes.
#[no_mangle]
pub unsafe extern "C" fn rc_set_world(h: i32, ptr: *const u8, len: u32) -> i32 {
    let b = slice(ptr, len as usize);
    with(h, |c| match World::from_bytes(b) {
        Some(w) => {
            c.w = w;
            sync_map(c); // fullchain handles only (no chain map registered = unchanged)
            0
        }
        None => -1,
    })
    .unwrap_or(-1)
}

/// # Safety
/// `out_ptr` → 36 writable bytes.
#[no_mangle]
pub unsafe extern "C" fn rc_player(h: i32, slot: u32, out_ptr: *mut u8) -> i32 {
    if slot as usize >= MAX_PLAYERS || out_ptr.is_null() {
        return -1;
    }
    let out = std::slice::from_raw_parts_mut(out_ptr, 36);
    with(h, |c| motor_bytes(&c.w, slot as usize, out) as i32).unwrap_or(-1)
}

/// Overwrites `slot`'s motor state from 33 bytes (the `rc_player` layout without the trailing 3 bytes). Feel Lab:
/// carries the player across an engine switch.
///
/// # Safety
/// `ptr` → 33 readable bytes.
#[no_mangle]
pub unsafe extern "C" fn rc_set_player(h: i32, slot: u32, ptr: *const u8) -> i32 {
    use rush_core::codec::Dec;
    if slot as usize >= MAX_PLAYERS {
        return -1;
    }
    let b = slice(ptr, 33);
    if b.len() < 33 {
        return -1;
    }
    with(h, |c| {
        let mut d = Dec::new(b);
        let m = rush_core::motor::MotorState::dec(&mut d);
        c.w.players[slot as usize].m = m;
        c.w.players[slot as usize].respawn_ticks = 0;
        0
    })
    .unwrap_or(-1)
}

/// The current tick and rate (Feel Lab overlay / interpolation): `tick | rate << 32`.
#[no_mangle]
pub extern "C" fn rc_clock(h: i32) -> u64 {
    with(h, |c| c.w.tick as u64 | (c.w.rate as u64) << 32).unwrap_or(0)
}

/// Sets the tick rate of a running world (Feel Lab F8). Returns 0 or −1.
#[no_mangle]
pub extern "C" fn rc_set_rate(h: i32, rate: u32) -> i32 {
    if !matches!(rate, 15 | 20 | 30) {
        return -1;
    }
    with(h, |c| {
        c.w.rate = rate as u8;
        0
    })
    .unwrap_or(-1)
}

/// Teleports `slot` to its spawn (Feel Lab F4).
#[no_mangle]
pub extern "C" fn rc_respawn(h: i32, slot: u32) -> i32 {
    if slot as usize >= MAX_PLAYERS {
        return -1;
    }
    with(h, |c| {
        let map = MapView::new(&c.map).unwrap();
        c.w.players[slot as usize].m = c.w.spawn_state(&map, slot as usize);
        0
    })
    .unwrap_or(-1)
}

/// # Safety
/// `map_ptr` → `map_len` readable bytes (may be null for fx / hazard kinds).
#[no_mangle]
pub unsafe extern "C" fn rc_vectors(kind: u32, seed: u64, n: u32, map_ptr: *const u8, map_len: u32) -> u64 {
    let m = slice(map_ptr, map_len as usize);
    rush_core::vectors::run(kind as u8, seed, n, if m.is_empty() { None } else { Some(m) })
}

/// WASM helpers: allocate / free a byte buffer in linear memory.
#[no_mangle]
pub extern "C" fn rc_alloc(len: u32) -> *mut u8 {
    let mut v = vec![0u8; len as usize].into_boxed_slice();
    let p = v.as_mut_ptr();
    std::mem::forget(v);
    p
}

/// # Safety
/// `p` must come from `rc_alloc(len)`.
#[no_mangle]
pub unsafe extern "C" fn rc_dealloc(p: *mut u8, len: u32) {
    if !p.is_null() {
        drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(p, len as usize)));
    }
}

/// P5 views (/live, /replay): lid centres + states at the World's current tick (the same states the step uses).
///
/// # Safety
/// `out_ptr` → `cap` writable bytes.
#[no_mangle]
pub unsafe extern "C" fn rc_lids(h: i32, out_ptr: *mut u8, cap: u32) -> u32 {
    use rush_core::hazards::{defs::HazardType, lid};
    if out_ptr.is_null() {
        return 0;
    }
    let out = std::slice::from_raw_parts_mut(out_ptr, cap as usize);
    with(h, |c| {
        let map = MapView::new(&c.map).unwrap();
        let cfg = c.w.motor_config(&c.params);
        let sc = world::pose_scene(&c.w, &map, &cfg, c.w.tick);
        let mut hz = c.w.hazards(&map);
        hz.ph = Some(&sc.phases);
        let t30 = rush_core::hazards::t30(c.w.t60(c.w.tick));
        let mut n = 0usize;
        let mut st = [lid::LidState::Idle; lid::MAX_LIDS_PER_GRID];
        for g in 0..hz.count(HazardType::LidFlip) {
            let d = hz.lid_grid(g);
            sc.lid_states(g, &d, t30, &mut st);
            for (k, s) in st.iter().enumerate().take((d.count() as usize).min(lid::MAX_LIDS_PER_GRID)) {
                if (n + 1) * 9 > out.len() {
                    return n as u32;
                }
                let (x, _, z) = d.lid_center(k as i32);
                out[n * 9..n * 9 + 4].copy_from_slice(&x.to_le_bytes());
                out[n * 9 + 4..n * 9 + 8].copy_from_slice(&z.to_le_bytes());
                out[n * 9 + 8] = *s as u8;
                n += 1;
            }
        }
        n as u32
    })
    .unwrap_or(0)
}

/// P5 views: every spinner bar (sweeper) at the World's current tick.
///
/// # Safety
/// `out_ptr` → `cap` writable bytes.
#[no_mangle]
pub unsafe extern "C" fn rc_sweepers(h: i32, out_ptr: *mut u8, cap: u32) -> u32 {
    use rush_core::hazards::defs::HazardType;
    if out_ptr.is_null() {
        return 0;
    }
    let out = std::slice::from_raw_parts_mut(out_ptr, cap as usize);
    with(h, |c| {
        let map = MapView::new(&c.map).unwrap();
        let cfg = c.w.motor_config(&c.params);
        let sc = world::pose_scene(&c.w, &map, &cfg, c.w.tick);
        let mut hz = c.w.hazards(&map);
        hz.ph = Some(&sc.phases);
        let t60 = c.w.t60(c.w.tick);
        let mut n = 0usize;
        for i in 0..hz.count(HazardType::SpinnerBar) {
            if (n + 1) * 20 > out.len() {
                break;
            }
            let d = hz.sweeper(i);
            let (co, si) = rush_core::hazards::sweeper_dir(&d, t60);
            for (j, v) in [d.pivot_x, d.pivot_z, d.arm, co, si].iter().enumerate() {
                out[n * 20 + j * 4..n * 20 + j * 4 + 4].copy_from_slice(&v.to_le_bytes());
            }
            n += 1;
        }
        n as u32
    })
    .unwrap_or(0)
}

/// P5 replay: the number of Chaos Drops queued in the World.
#[no_mangle]
pub extern "C" fn rc_drops_queued(h: i32) -> u32 {
    with(h, |c| rush_core::drops::queued(&c.w.drop_queue) as u32).unwrap_or(0)
}

/// P5 replay: what the program's `tick()` does when a DropQueue purchase enters the World (sim.rs `ingest_drops`):
/// queue drop `kind` with the seeded arg, triggering at tick + 1 + TELEGRAPH. Returns 1 if queued.
#[no_mangle]
pub extern "C" fn rc_ingest_drop(h: i32, kind: u32) -> u32 {
    with(h, |c| {
        let i = rush_core::drops::queued(&c.w.drop_queue) as u8;
        let trigger = c.w.tick.wrapping_add(1).wrapping_add(rush_core::drops::TELEGRAPH);
        let arg = rush_core::drops::arg_for(c.w.hazard_seed, i, kind as u8);
        rush_core::drops::enqueue(&mut c.w.drop_queue, kind as u8, arg, trigger) as u32
    })
    .unwrap_or(0)
}

/// P6 D-CROWN harness (feature `harness`, never in the shipped rush_core.wasm): the input rush-core's own bot brain
/// (tier REFERENCE) would send for `slot` on the next tick of the handle's World, written as 5 B
/// {move_x, move_y, buttons, cam_yaw LE}. The slot's bot memory is kept per handle across `rc_set_world` calls (the
/// chain's World has none for a human slot). No simulation change: the same `bots::bot_input` the program runs.
///
/// # Safety
/// `out_ptr` → 5 writable bytes.
#[cfg(feature = "harness")]
#[no_mangle]
pub unsafe extern "C" fn rc_bot_input(h: i32, slot: u32, out_ptr: *mut u8) -> i32 {
    thread_local! {
        static KEEP: RefCell<Vec<[Option<rush_core::bots::BotState>; MAX_PLAYERS]>> = const { RefCell::new(Vec::new()) };
    }
    let s = slot as usize;
    if s >= MAX_PLAYERS || out_ptr.is_null() {
        return -1;
    }
    let cmd = with(h, |c| {
        let map = MapView::new(&c.map).unwrap();
        let mut sc = rush_core::collide::Scene::new(map);
        let cfg = c.w.motor_config(&c.params);
        let tick = c.w.tick.wrapping_add(1);
        world::pose_scene_into(&mut sc, &c.w, &map, &cfg, tick);
        let kept = KEEP.with(|k| { let mut k = k.borrow_mut(); while k.len() <= h as usize { k.push([None; MAX_PLAYERS]); } k[h as usize][s] });
        let mut b = kept.unwrap_or_default();
        if kept.is_none() {
            b.tier = rush_core::bots::tier::REFERENCE;
        }
        c.w.players[s].bot = b;
        let cmd = rush_core::bots::bot_input(s, &mut c.w, &map, &sc);
        let nb = c.w.players[s].bot;
        KEEP.with(|k| k.borrow_mut()[h as usize][s] = Some(nb));
        cmd
    });
    match cmd {
        Some(c) => {
            let o = std::slice::from_raw_parts_mut(out_ptr, 5);
            o[0] = c.move_x as u8;
            o[1] = c.move_y as u8;
            o[2] = c.buttons;
            o[3..5].copy_from_slice(&c.cam_yaw.to_le_bytes());
            0
        }
        None => -1,
    }
}

// ------------------------------------------------------------------ fullchain: the tick exactly as the chain runs it

/// The program's map slot for this World (sim.rs `map_slot`): the round map once the director has loaded it, else
/// the lobby.
fn chain_slot(w: &World) -> usize {
    let id = if w.round_index >= 1 { w.map_key } else { rush_core::deal::LOBBY };
    if (id as usize) < 4 {
        id as usize
    } else {
        CHAIN_SLOTS - 1
    }
}

/// A handle with chain maps: makes `map` the one the program would step this World on. False if it is missing.
fn sync_map(c: &mut Ctx) -> bool {
    if c.chain.iter().all(|m| m.is_none()) {
        return true;
    }
    let s = chain_slot(&c.w);
    if c.cur != s {
        match &c.chain[s] {
            Some(b) => {
                c.map = b.clone();
                c.cur = s;
            }
            None => return false,
        }
    }
    true
}

/// sim.rs `tick_inner` at part 0: the director asks for the round's map → `load_round` before the tick.
fn chain_load(c: &mut Ctx) -> i32 {
    let Some(id) = rush_core::rules::director::needs_map(&c.w) else { return -1 };
    let ms = if (id as usize) < 4 { id as usize } else { CHAIN_SLOTS - 1 };
    let Some(b) = c.chain[ms].as_ref() else { return -2 };
    let Ok(v) = MapView::new(b) else { return -2 };
    rush_core::rules::director::load_round(&mut c.w, &v, &mut Sha);
    id as i32
}

/// One program execution (sim.rs `tick_inner` from `part`): map load at part 0, then one `step_slice` call with a fresh
/// scene and event buffer. Returns the next part, or −2 when a needed map is not registered.
fn chain_part(c: &mut Ctx, inputs: &[Option<InputCmd>; MAX_PLAYERS], part: u8, budget: u32, n_ev: &mut u32) -> i32 {
    if part == 0 && chain_load(c) == -2 {
        return -2;
    }
    if !sync_map(c) {
        return -2;
    }
    let map = match MapView::new(&c.map) {
        Ok(v) => v,
        Err(_) => return -2,
    };
    let mut sc = rush_core::collide::Scene::new(map);
    let mut ev = StepEvents::default();
    let next = world::step_slice(&mut c.w, inputs, &map, &c.params, &mut Sha, &mut ev, &mut sc, part, budget);
    *n_ev += ev.n as u32;
    next as i32
}

unsafe fn read_inputs(p: *const u8) -> [Option<InputCmd>; MAX_PLAYERS] {
    let b = slice(p, MAX_PLAYERS * 6);
    let mut inputs = [None; MAX_PLAYERS];
    if b.len() == MAX_PLAYERS * 6 {
        for (s, inp) in inputs.iter_mut().enumerate() {
            *inp = read_cmd(&b[s * 6..s * 6 + 6]);
        }
    }
    inputs
}

/// Registers the map blob of program map slot `slot` (0..=3 = deal ids 0..=3, 4 = the lobby). 0 or −1.
///
/// # Safety
/// `ptr` → `len` readable bytes.
#[no_mangle]
pub unsafe extern "C" fn rc_chain_map(h: i32, slot: u32, ptr: *const u8, len: u32) -> i32 {
    let s = slot as usize;
    let b = slice(ptr, len as usize);
    if s >= CHAIN_SLOTS || MapView::new(b).is_err() {
        return -1;
    }
    with(h, |c| {
        c.chain[s] = Some(b.to_vec());
        if c.cur == s {
            c.cur = NO_SLOT;
        }
        sync_map(c);
        0
    })
    .unwrap_or(-1)
}

/// The map load the program runs before a tick (part 0). The deal id loaded, −1 none needed, −2 map not registered.
/// `rc_step_part(…, 0, …)` / `rc_step_chain` do it themselves; call it first only to act between the load and the
/// step (Chaos Drops are ingested there, after the load).
#[no_mangle]
pub extern "C" fn rc_chain_load(h: i32) -> i32 {
    with(h, |c| {
        let r = chain_load(c);
        sync_map(c);
        r
    })
    .unwrap_or(-2)
}

/// One program execution: `step_slice` from `from` (0 = a new tick, 1..=11 = that slot, 12 = the end of the tick)
/// within `budget` work units (0 = one player per call). Returns the next part (0 = the tick is complete) or −2.
///
/// # Safety
/// `inputs_ptr` must point to 72 readable bytes (or be null = all missing).
#[no_mangle]
pub unsafe extern "C" fn rc_step_part(h: i32, inputs_ptr: *const u8, from: u32, budget: u32) -> i32 {
    let inputs = read_inputs(inputs_ptr);
    with(h, |c| {
        let mut n = 0;
        chain_part(c, &inputs, from.min(MAX_PLAYERS as u32) as u8, budget, &mut n)
    })
    .unwrap_or(-2)
}

/// One whole tick as the chain steps it: `step_slice` from part 0 until it returns 0, map load first (World version 1
/// and 2 alike; `rc_step`'s unsliced `world::step` differs on full matches). Returns the event count or −2.
///
/// # Safety
/// `inputs_ptr` must point to 72 readable bytes (or be null = all missing).
#[no_mangle]
pub unsafe extern "C" fn rc_step_chain(h: i32, inputs_ptr: *const u8, budget: u32) -> i32 {
    let inputs = read_inputs(inputs_ptr);
    with(h, |c| {
        let (mut part, mut n) = (0u8, 0u32);
        // every call steps at least one player or finishes the tick: ≤ MAX_PLAYERS + 1 calls
        for _ in 0..=MAX_PLAYERS + 1 {
            let next = chain_part(c, &inputs, part, budget, &mut n);
            if next < 0 {
                return next;
            }
            if next == 0 {
                return n as i32;
            }
            part = next as u8;
        }
        -3
    })
    .unwrap_or(-2)
}

/// The director's position: round | phase << 8 | World round_index << 16 | map_key << 24.
#[no_mangle]
pub extern "C" fn rc_round(h: i32) -> u32 {
    with(h, |c| c.w.dir.round as u32 | (c.w.dir.phase as u32) << 8 | (c.w.round_index as u32) << 16 | (c.w.map_key as u32) << 24).unwrap_or(0)
}
