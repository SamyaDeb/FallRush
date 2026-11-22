//! `World` and `step()` (P1 prompt F; ports of `Shared/SimCore.cs` (`Step`, `Separate`, `Bump`, spawns),
//! `HazardSet.cs` effects and `PlayerStep.Run`, `Net/PhysxMotorWorld.BeginTick` posing).
//!
//! `World` is the whole match state in a canonical little-endian layout ([`World::to_bytes`], version byte, ≤ 2 KB).
//! `step(world, inputs[12], map, …)` advances one tick: pose the scene → rules begin-tick → every slot in ascending
//! order (input or bot → player step → fall / goo / respawn) → soft separation (66 pairs, fixed order) → rules
//! end-tick. No allocation; every loop is bounded.

use crate::codec::{Codec, Dec, Enc};
use crate::collide::{Scene, SurfaceMotion, MAX_BLOCKS, MAX_DOORS, MAX_LIDS, MAX_PLATFORMS, MAX_SURFACES, MAX_TILES};
use crate::config::{MotorConfig, MovementParams};
use crate::fx::{div_round, isqrt_i64, normalize_q15, yaw_dir_q15, ONE_Q15};
use crate::hazards::defs::HazardType;
use crate::hazards::{self, knock, Hazards};
use crate::input::{InputCmd, InputTrack};
use crate::mapblob::MapView;
use crate::modifiers;
use crate::motor::{self, flag, MotorState};
use crate::vec::IVec3;

pub const MAX_PLAYERS: usize = 12;
/// Canonical layout version.
pub const WORLD_VERSION: u8 = 1;
/// The size budget (doc 19 P2: `World` ≤ 2 KB).
pub const WORLD_MAX_BYTES: usize = 2048;
/// Crumble tiles with reactive state in `World` (bigger grids need their own account in P2).
pub const MAX_RX_TILES: usize = 16;
/// Falling blocks with reactive state.
pub const MAX_RX_BLOCKS: usize = 8;
/// Dropped stars kept (ring).
pub const MAX_DROPS: usize = 8;
/// Timeline star ids with a collected bit: 0..512 normal, 512..576 golden.
pub const STAR_BITS: usize = 576;
/// Chaos Drop queue (P4; empty in P1).
pub const DROP_QUEUE: usize = 8;

/// `PlayerStep.Result` bits.
pub mod step_result {
    pub const HAZARD_HIT: u8 = 1;
    pub const FELL_OUT: u8 = 2;
    pub const LAUNCHED: u8 = 4;
    pub const DOOR_PUSHED: u8 = 8;
    pub const GOOED: u8 = 16;
}

/// [`Player::flags`] bits.
pub mod pf {
    pub const ACTIVE: u8 = 1;
    pub const BOT: u8 = 2;
    pub const DETACHED: u8 = 4;
}

/// One slot.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Player {
    pub m: MotorState,
    pub flags: u8,
    pub respawn_ticks: u8,
    pub last_step: u8,
    pub input: InputTrack,
    pub falls: u16,
    pub hits: u16,
    pub r: crate::rules::SlotRules,
    pub bot: crate::bots::BotState,
}

impl Player {
    #[inline]
    pub const fn active(&self) -> bool {
        self.flags & pf::ACTIVE != 0
    }
    #[inline]
    pub const fn is_bot(&self) -> bool {
        self.flags & pf::BOT != 0
    }
}

impl Codec for MotorState {
    fn enc(&self, e: &mut Enc) {
        for v in [self.pos.x, self.pos.y, self.pos.z, self.vel.x, self.vel.y, self.vel.z] {
            e.i32(v);
        }
        for v in [self.yaw, self.flags, self.stun, self.coyote, self.jump_buffer, self.dive_cooldown, self.land, self.immunity, self.surface] {
            e.u8(v);
        }
    }
    fn dec(d: &mut Dec) -> Self {
        let pos = IVec3::new(d.i32(), d.i32(), d.i32());
        let vel = IVec3::new(d.i32(), d.i32(), d.i32());
        MotorState {
            pos,
            vel,
            yaw: d.u8(),
            flags: d.u8(),
            stun: d.u8(),
            coyote: d.u8(),
            jump_buffer: d.u8(),
            dive_cooldown: d.u8(),
            land: d.u8(),
            immunity: d.u8(),
            surface: d.u8(),
        }
    }
}

impl Codec for InputTrack {
    fn enc(&self, e: &mut Enc) {
        e.put(&self.last.to_bytes());
        e.u8(self.missing);
    }
    fn dec(d: &mut Dec) -> Self {
        let b: [u8; 5] = d.take();
        InputTrack { last: InputCmd::from_bytes(&b), missing: d.u8() }
    }
}

impl Codec for Player {
    fn enc(&self, e: &mut Enc) {
        self.m.enc(e);
        e.u8(self.flags);
        e.u8(self.respawn_ticks);
        e.u8(self.last_step);
        self.input.enc(e);
        e.u16(self.falls);
        e.u16(self.hits);
        self.r.enc(e);
        self.bot.enc(e);
    }
    fn dec(d: &mut Dec) -> Self {
        Player {
            m: MotorState::dec(d),
            flags: d.u8(),
            respawn_ticks: d.u8(),
            last_step: d.u8(),
            input: InputTrack::dec(d),
            falls: d.u16(),
            hits: d.u16(),
            r: crate::rules::SlotRules::dec(d),
            bot: crate::bots::BotState::dec(d),
        }
    }
}

/// A dropped star (`RxEntry` StarDrop): tick, slot, where it dropped (mm), collected flag.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct DroppedStar {
    pub tick: u32,
    pub slot: u8,
    pub taken: bool,
    pub pos: IVec3,
}

/// Server-owned reactive state (`ReactiveSet`): crumble contacts, falling-block triggers, stars.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Reactive {
    /// Latest contact tick per crumble tile (0 = never touched).
    pub tile_contact: [u32; MAX_RX_TILES],
    /// Latest trigger tick per falling block (0 = never).
    pub block_trigger: [u32; MAX_RX_BLOCKS],
    /// Timeline stars collected.
    pub star_taken: [u64; STAR_BITS / 64],
    pub drops: [DroppedStar; MAX_DROPS],
    /// Dropped stars ever (the next drop id is `STAR_DROP_BASE + drop_count`).
    pub drop_count: u16,
    pub seq: u32,
}

impl Default for Reactive {
    fn default() -> Self {
        Reactive {
            tile_contact: [0; MAX_RX_TILES],
            block_trigger: [0; MAX_RX_BLOCKS],
            star_taken: [0; STAR_BITS / 64],
            drops: [DroppedStar::default(); MAX_DROPS],
            drop_count: 0,
            seq: 0,
        }
    }
}

impl Codec for Reactive {
    fn enc(&self, e: &mut Enc) {
        for v in self.tile_contact {
            e.u32(v);
        }
        for v in self.block_trigger {
            e.u32(v);
        }
        for v in self.star_taken {
            e.u64(v);
        }
        for d in self.drops {
            e.u32(d.tick);
            e.u8(d.slot);
            e.bool(d.taken);
            e.i32(d.pos.x);
            e.i32(d.pos.y);
            e.i32(d.pos.z);
        }
        e.u16(self.drop_count);
        e.u32(self.seq);
    }
    fn dec(d: &mut Dec) -> Self {
        let mut r = Reactive::default();
        for v in r.tile_contact.iter_mut() {
            *v = d.u32();
        }
        for v in r.block_trigger.iter_mut() {
            *v = d.u32();
        }
        for v in r.star_taken.iter_mut() {
            *v = d.u64();
        }
        for s in r.drops.iter_mut() {
            s.tick = d.u32();
            s.slot = d.u8();
            s.taken = d.bool();
            s.pos = IVec3::new(d.i32(), d.i32(), d.i32());
        }
        r.drop_count = d.u16();
        r.seq = d.u32();
        r
    }
}

/// A queued Chaos Drop (P4). Empty in P1.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct QueuedDrop {
    pub tick: u32,
    pub kind: u8,
    pub arg: u8,
}

/// The whole match state.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct World {
    pub version: u8,
    /// Last simulated tick (0 before the first step).
    pub tick: u32,
    /// Tick rate (15, 20 or 30).
    pub rate: u8,
    /// Round index (1..4; 0 = free play).
    pub round_index: u8,
    /// `map_pool` map id of the loaded map (254 lobby, 255 sandbox).
    pub map_key: u8,
    pub variant: u8,
    pub modifier: u8,
    pub hazard_seed: u64,
    /// Tick at which hazard time is 0 (the RUSH tick).
    pub round_start: u32,
    pub players: [Player; MAX_PLAYERS],
    pub rx: Reactive,
    pub drop_queue: [QueuedDrop; DROP_QUEUE],
    pub round: crate::rules::RoundState,
    pub dir: crate::rules::director::DirectorState,
    /// Step event sequence (every emitted event).
    pub event_seq: u32,
}

impl World {
    /// An empty world on `map` at `rate` Hz (free play until the rules begin a round).
    pub fn new(map: &MapView, rate: u32) -> World {
        let mut w = World {
            version: WORLD_VERSION,
            tick: 0,
            rate: rate as u8,
            round_index: 0,
            map_key: map.map_id(),
            variant: 0,
            modifier: 0,
            hazard_seed: 0,
            round_start: 0,
            players: [Player::default(); MAX_PLAYERS],
            rx: Reactive::default(),
            drop_queue: [QueuedDrop::default(); DROP_QUEUE],
            round: crate::rules::RoundState::default(),
            dir: crate::rules::director::DirectorState::default(),
            event_seq: 0,
        };
        w.round.mode = crate::mapblob::mode::SANDBOX;
        w.round.phase = crate::rules::phase::SANDBOX;
        w
    }

    /// Resets an existing world in place (no 2 KB temporary: the sBPF stack frame is 4 KB).
    pub fn reset(&mut self, map: &MapView, rate: u32) {
        self.version = WORLD_VERSION;
        self.tick = 0;
        self.rate = rate as u8;
        self.round_index = 0;
        self.map_key = map.map_id();
        self.variant = 0;
        self.modifier = 0;
        self.hazard_seed = 0;
        self.round_start = 0;
        for p in self.players.iter_mut() {
            *p = Player::default();
        }
        self.rx.tile_contact = [0; MAX_RX_TILES];
        self.rx.block_trigger = [0; MAX_RX_BLOCKS];
        self.rx.star_taken = [0; STAR_BITS / 64];
        self.rx.drops = [DroppedStar::default(); MAX_DROPS];
        self.rx.drop_count = 0;
        self.rx.seq = 0;
        self.drop_queue = [QueuedDrop::default(); DROP_QUEUE];
        self.round = crate::rules::RoundState::default();
        self.dir = crate::rules::director::DirectorState::default();
        self.event_seq = 0;
        self.round.mode = crate::mapblob::mode::SANDBOX;
        self.round.phase = crate::rules::phase::SANDBOX;
    }

    /// Activates `slot` (human or bot) at its spawn.
    pub fn add_player(&mut self, map: &MapView, slot: usize, bot: bool) {
        let (p, yaw) = map.spawn(slot);
        let pl = &mut self.players[slot];
        *pl = Player::default();
        pl.flags = pf::ACTIVE | if bot { pf::BOT } else { 0 };
        pl.m = MotorState::at(p, yaw);
        if bot {
            pl.m.flags |= flag::BOT;
        }
    }

    /// `SimCore.SpawnState`.
    pub fn spawn_state(&self, map: &MapView, slot: usize) -> MotorState {
        let (p, yaw) = map.spawn(slot);
        let mut m = MotorState::at(p, yaw);
        if self.players[slot].is_bot() {
            m.flags |= flag::BOT;
        }
        m
    }

    /// Motor constants for the current modifier and rate.
    pub fn motor_config(&self, params: &MovementParams) -> MotorConfig {
        MotorConfig::new(params, self.rate as u32, modifiers::get(self.modifier).motor)
    }

    /// The hazards of the current round.
    pub fn hazards<'m>(&self, map: &MapView<'m>) -> Hazards<'m> {
        Hazards { map: *map, variant: self.variant, seed: self.hazard_seed, m: modifiers::get(self.modifier).hazard, ph: None }
    }

    /// Hazard time (1/60 s) of `tick`.
    pub fn t60(&self, tick: u32) -> i64 {
        (tick as i64 - self.round_start as i64) * (60 / self.rate.max(1) as i64)
    }

    /// Canonical bytes; returns the length (asserted ≤ [`WORLD_MAX_BYTES`] by a test).
    pub fn to_bytes(&self, out: &mut [u8]) -> usize {
        let mut e = Enc::new(out);
        self.enc(&mut e);
        e.n
    }

    pub fn from_bytes(b: &[u8]) -> Option<World> {
        let mut d = Dec::new(b);
        let w = World::dec(&mut d);
        if d.err || w.version != WORLD_VERSION {
            None
        } else {
            Some(w)
        }
    }

    /// Decodes canonical bytes into `self` in place (no 2 KB temporary: sBPF stack frames are 4 KB). Returns false
    /// (and leaves `self` partly written) on short input or a wrong version.
    pub fn load_from(&mut self, b: &[u8]) -> bool {
        let mut d = Dec::new(b);
        self.version = d.u8();
        self.tick = d.u32();
        self.rate = d.u8();
        self.round_index = d.u8();
        self.map_key = d.u8();
        self.variant = d.u8();
        self.modifier = d.u8();
        self.hazard_seed = d.u64();
        self.round_start = d.u32();
        for p in self.players.iter_mut() {
            *p = Player::dec(&mut d);
        }
        self.rx = Reactive::dec(&mut d);
        for q in self.drop_queue.iter_mut() {
            q.tick = d.u32();
            q.kind = d.u8();
            q.arg = d.u8();
        }
        self.round = crate::rules::RoundState::dec(&mut d);
        self.dir = crate::rules::director::DirectorState::dec(&mut d);
        self.event_seq = d.u32();
        !d.err && self.version == WORLD_VERSION
    }

    /// FNV-1a 64 over the canonical bytes (parity only; the program uses sha256 where it matters).
    pub fn hash(&self) -> u64 {
        let mut buf = [0u8; WORLD_MAX_BYTES + 512];
        let n = self.to_bytes(&mut buf);
        let mut h = crate::vectors::Fnv::default();
        h.bytes(&buf[..n.min(buf.len())]);
        h.0
    }
}

impl Codec for World {
    fn enc(&self, e: &mut Enc) {
        e.u8(self.version);
        e.u32(self.tick);
        e.u8(self.rate);
        e.u8(self.round_index);
        e.u8(self.map_key);
        e.u8(self.variant);
        e.u8(self.modifier);
        e.u64(self.hazard_seed);
        e.u32(self.round_start);
        for p in &self.players {
            p.enc(e);
        }
        self.rx.enc(e);
        for q in &self.drop_queue {
            e.u32(q.tick);
            e.u8(q.kind);
            e.u8(q.arg);
        }
        self.round.enc(e);
        self.dir.enc(e);
        e.u32(self.event_seq);
    }
    fn dec(d: &mut Dec) -> Self {
        let mut w = World {
            version: d.u8(),
            tick: d.u32(),
            rate: d.u8(),
            round_index: d.u8(),
            map_key: d.u8(),
            variant: d.u8(),
            modifier: d.u8(),
            hazard_seed: d.u64(),
            round_start: d.u32(),
            players: [Player::default(); MAX_PLAYERS],
            rx: Reactive::default(),
            drop_queue: [QueuedDrop::default(); DROP_QUEUE],
            round: crate::rules::RoundState::default(),
            dir: crate::rules::director::DirectorState::default(),
            event_seq: 0,
        };
        for p in w.players.iter_mut() {
            *p = Player::dec(d);
        }
        w.rx = Reactive::dec(d);
        for q in w.drop_queue.iter_mut() {
            q.tick = d.u32();
            q.kind = d.u8();
            q.arg = d.u8();
        }
        w.round = crate::rules::RoundState::dec(d);
        w.dir = crate::rules::director::DirectorState::dec(d);
        w.event_seq = d.u32();
        w
    }
}

// ------------------------------------------------------------------ events

/// Event kinds in [`StepEvents`].
pub mod ek {
    /// A round event (`RaceEventType` in `tag`).
    pub const ROUND: u8 = 1;
    /// A transcript event (tags 1–7: DEAL … ABORT; 8–10 reserved for P4).
    pub const TRANSCRIPT: u8 = 2;
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Event {
    pub kind: u8,
    pub tag: u8,
    pub slot: u8,
    pub round: u8,
    pub arg: i32,
    pub tick: u32,
    pub seq: u32,
}

/// Most events one step can report (extra events are counted in `dropped`).
pub const MAX_EVENTS: usize = 64;

#[derive(Clone, Copy, Debug)]
pub struct StepEvents {
    pub n: usize,
    pub ev: [Event; MAX_EVENTS],
    pub dropped: u32,
    /// Players whose work budget ran out this step (they kept their last safe state; P2 0.2 step 5). Not reset by
    /// the step: the caller clears it.
    pub capped: u32,
    /// Largest bot-brain and motor work (units) of one player this step (diagnostics; the caller clears them).
    pub bot_work_max: u32,
    pub motor_work_max: u32,
}

impl Default for StepEvents {
    fn default() -> Self {
        StepEvents { n: 0, ev: [Event::default(); MAX_EVENTS], dropped: 0, capped: 0, bot_work_max: 0, motor_work_max: 0 }
    }
}

impl StepEvents {
    pub fn push(&mut self, e: Event) {
        if self.n < MAX_EVENTS {
            self.ev[self.n] = e;
            self.n += 1;
        } else {
            self.dropped += 1;
        }
    }
    pub fn as_slice(&self) -> &[Event] {
        &self.ev[..self.n]
    }
}

/// Emits a round event (`Emit` in the C# rules): bumps the round's event seq.
pub fn emit_round(w: &mut World, ev: &mut StepEvents, tag: u8, tick: u32, slot: u8, arg: i32) {
    w.round.event_seq = w.round.event_seq.wrapping_add(1);
    w.event_seq = w.event_seq.wrapping_add(1);
    ev.push(Event { kind: ek::ROUND, tag, slot, round: w.round_index, arg, tick, seq: w.round.event_seq });
}

/// sha256 provider (the program: `solana-sha256-hasher`; tests and the off-chain runners: `sha2`).
pub trait Hasher {
    fn sha256(&mut self, parts: &[&[u8]]) -> [u8; 32];
    /// Profiling hook at fixed points of [`step`] (P2 CU pass; the program logs compute units here). Never changes
    /// the simulation. Tags: 1 after `before_tick`, 2 after `pose_scene` + `begin_tick`, 16 + slot after that slot's
    /// input / bot brain, 32 + slot after its `player_step`, 3 after `separate`, 4 at the end.
    #[inline(always)]
    fn mark(&mut self, _tag: u8) {}
}

/// A hasher that is never called (worlds without a match director, e.g. sandbox and QA single rounds).
pub struct NoHasher;

impl Hasher for NoHasher {
    fn sha256(&mut self, _parts: &[&[u8]]) -> [u8; 32] {
        [0; 32]
    }
}

// ------------------------------------------------------------------ scene posing (PhysxMotorWorld.BeginTick)

/// Poses platforms, doors, lids, tiles and blocks for `tick` and fills the surface motions.
pub fn pose_scene<'m>(w: &World, map: &MapView<'m>, cfg: &MotorConfig, tick: u32) -> Scene<'m> {
    let mut sc = Scene::new(*map);
    pose_scene_into(&mut sc, w, map, cfg, tick);
    sc
}

/// [`pose_scene`] into existing storage (the program keeps the ≈ 3 KB `Scene` on the heap).
#[inline(never)]
pub fn pose_scene_into<'m>(sc: &mut Scene<'m>, w: &World, map: &MapView<'m>, cfg: &MotorConfig, tick: u32) {
    sc.reset(*map);
    sc.phases.build(&w.hazards(map));
    let tab = sc.phases;
    let mut hz = w.hazards(map);
    hz.ph = Some(&tab);
    let nb = hz.count(HazardType::RollingBall);
    if nb <= crate::collide::MAX_BALLK {
        let mut i = 0;
        while i < nb {
            sc.balls[i] = hazards::BallK::of(&hz.ball(i));
            i += 1;
        }
        sc.balls_n = nb as u8;
    }
    let t60 = w.t60(tick);
    let step60 = cfg.t60_per_tick as i64;
    let t30 = hazards::t30(t60);
    let np = hz.count(HazardType::MovingPlatform).min(MAX_PLATFORMS);
    let mut i = 0;
    while i < np {
        let d = hz.platform(i);
        sc.platform_off[i] = hazards::platform_center(&d, t60);
        i += 1;
    }
    let nd = hz.count(HazardType::GateDoor).min(MAX_DOORS);
    let mut i = 0;
    while i < nd {
        let d = hz.door(i);
        sc.door_off[i] = hazards::door_center(&d, t30);
        i += 1;
    }
    // Lids (all grids, grid by grid).
    let ng = hz.count(HazardType::LidFlip);
    let mut base = 0usize;
    let mut g = 0;
    let mut states = [hazards::lid::LidState::Idle; hazards::lid::MAX_LIDS_PER_GRID];
    while g < ng {
        let d = hz.lid_grid(g);
        sc.lid_states(g, &d, t30, &mut states);
        let n = (d.count() as usize).min(hazards::lid::MAX_LIDS_PER_GRID);
        let mut k = 0;
        while k < n {
            let id = base + k;
            if id < MAX_LIDS && !hazards::lid::lid_solid(states[k]) {
                sc.lid_solid[id / 64] &= !(1u64 << (id % 64));
            }
            k += 1;
        }
        base += n;
        g += 1;
    }
    // Crumble tiles (reactive).
    if hz.count(HazardType::CrumbleTile) > 0 {
        let d = hz.tile_grid(0);
        let n = (d.count() as usize).min(MAX_TILES).min(MAX_RX_TILES);
        let mut k = 0;
        while k < n {
            if !tile_solid(w, &d, k, tick) {
                sc.tile_solid[k / 64] &= !(1u64 << (k % 64));
            }
            k += 1;
        }
    }
    // Falling blocks (reactive; nothing triggers them in the C# maps today).
    let nb = hz.count(HazardType::FallingBlock).min(MAX_BLOCKS).min(MAX_RX_BLOCKS);
    let mut k = 0;
    while k < nb {
        let d = hz.block(k);
        let t = w.rx.block_trigger[k];
        if t != 0 && tick >= t + d.delay_ticks as u32 && tick < t + (d.delay_ticks + d.gone_ticks) as u32 {
            sc.block_solid[k / 64] &= !(1u64 << (k % 64));
        }
        k += 1;
    }
    // Surfaces: platforms, turn tables, conveyors (spring pads have no motion).
    let mut id = 0usize;
    let mut i = 0;
    while i < np && id < MAX_SURFACES {
        let d = hz.platform(i);
        sc.surfaces[id] = SurfaceMotion { kind: 1, delta: hazards::platform_delta(&d, t60, step60), ..Default::default() };
        id += 1;
        i += 1;
    }
    let nt = hz.count(HazardType::TurnTable);
    let mut i = 0;
    while i < nt && id < MAX_SURFACES {
        let d = hz.turn_table(i);
        sc.surfaces[id] = SurfaceMotion {
            kind: 2,
            delta: IVec3::ZERO,
            center: IVec3::new(d.center_x, d.center_y, d.center_z),
            p0: hazards::turn_table_phase(&d, t60 - step60),
            p1: hazards::turn_table_phase(&d, t60),
        };
        id += 1;
        i += 1;
    }
    let nc = hz.count(HazardType::Conveyor);
    let mut i = 0;
    while i < nc && id < MAX_SURFACES {
        let d = hz.conveyor(i);
        sc.surfaces[id] = SurfaceMotion { kind: 3, delta: hazards::conveyor_delta(&d, t60, step60), ..Default::default() };
        id += 1;
        i += 1;
    }
}

/// `ReactiveSet.TileSolid` (one contact cycle kept per tile; see [`MAX_RX_TILES`]).
pub fn tile_solid(w: &World, d: &crate::hazards::defs::TileGridDef, k: usize, tick: u32) -> bool {
    let c = w.rx.tile_contact[k];
    if c == 0 || tick < c {
        return true;
    }
    let back = d.respawn_for(k as i32) as u32;
    if back != 0 && tick >= c + d.wobble_ticks as u32 + back {
        return true; // came back
    }
    tick < c + d.wobble_ticks as u32
}

// ------------------------------------------------------------------ hazard effects (HazardSet)

/// `HazardSet.Knock`: velocity override + stun + immunity; ends any dive / landing / ride.
pub fn knock(s: &mut MotorState, n: [i32; 2], push_cms: i32, up_cms: i32, stun30: i32, imm30: i32, cfg: &MotorConfig) {
    s.vel.x = div_round(n[0] as i64 * push_cms as i64 * 10, ONE_Q15 as i64) as i32;
    s.vel.y = up_cms * 10;
    s.vel.z = div_round(n[1] as i64 * push_cms as i64 * 10, ONE_Q15 as i64) as i32;
    s.stun = cfg.ticks30(stun30).clamp(0, 255) as u8;
    s.immunity = cfg.ticks30(imm30).clamp(0, 255) as u8;
    s.flags &= !(flag::GROUNDED | flag::DIVING | flag::DIVE_LANDING | flag::STUMBLING);
    s.land = 0;
    s.coyote = 0;
    s.jump_buffer = 0;
    s.surface = 0;
}

/// `HazardSet.HitHeightMm`.
pub fn hit_height(s: &MotorState, cfg: &MotorConfig) -> i32 {
    if s.has(flag::DIVING) || s.has(flag::DIVE_LANDING) {
        knock::DIVE_HIT_HEIGHT_MM
    } else {
        cfg.height
    }
}

/// `HazardSet.ApplyKnocks`: spinners (from `SweeperStartT`), mallets, rolling balls, bumpers; at most one hit.
pub fn apply_knocks(s: &mut MotorState, hz: &Hazards, t60: i64, cfg: &MotorConfig) -> bool {
    if s.immunity > 0 || s.has(flag::RESPAWNING) {
        return false;
    }
    let r = cfg.radius;
    let h = hit_height(s, cfg);
    let ss = hz.sweeper_start();
    if ss == i32::MIN || hazards::t30(t60) >= ss as i64 {
        let n = hz.count(HazardType::SpinnerBar);
        let mut i = 0;
        while i < n {
            let d = hz.sweeper(i);
            if let Some(p) = hazards::sweeper_contact(&d, t60, s.pos, r, h) {
                knock(s, p, d.knockback_cms, d.knock_up_cms, d.stun_ticks, d.immunity_ticks, cfg);
                return true;
            }
            i += 1;
        }
    }
    let n = hz.count(HazardType::MalletSwing);
    let mut i = 0;
    while i < n {
        let d = hz.mallet(i);
        if let Some(p) = hazards::mallet_contact(&d, t60, s.pos, r, h) {
            knock(s, p, d.knockback_cms, d.knock_up_cms, d.stun_ticks, d.immunity_ticks, cfg);
            return true;
        }
        i += 1;
    }
    let n = hz.count(HazardType::RollingBall);
    let mut i = 0;
    while i < n {
        let d = hz.ball(i);
        if let Some(p) = hazards::ball_contact(&d, t60, s.pos, r, h) {
            knock(s, p, d.knockback_cms, d.knock_up_cms, d.stun_ticks, d.immunity_ticks, cfg);
            return true;
        }
        i += 1;
    }
    let n = hz.count(HazardType::Bumper);
    let mut i = 0;
    while i < n {
        let d = hz.bumper(i);
        if let Some(p) = hazards::bumper_contact(&d, s.pos, r, h) {
            knock(s, p, d.knockback_cms, d.knock_up_cms, d.stun_ticks, d.immunity_ticks, cfg);
            return true;
        }
        i += 1;
    }
    false
}

/// `HazardSet.ApplySprings`: grounded on a pad at the end of the step → launch.
pub fn apply_springs(s: &mut MotorState, hz: &Hazards) -> bool {
    if !s.has(flag::GROUNDED) || s.surface == 0 {
        return false;
    }
    let (kind, i) = hz.surface_kind(s.surface as i32 - 1);
    if kind != 4 {
        return false;
    }
    let d = hz.pad(i);
    s.vel = IVec3::new(d.launch_x_cms * 10, d.launch_y_cms * 10, d.launch_z_cms * 10);
    s.flags &= !(flag::GROUNDED | flag::DIVING | flag::DIVE_LANDING | flag::STUMBLING);
    s.land = 0;
    s.coyote = 0;
    s.jump_buffer = 0;
    s.surface = 0;
    true
}

/// `HazardSet.ApplyDoors`: a door that closed onto the player pushes them out along its normal.
pub fn apply_doors(s: &mut MotorState, hz: &Hazards, t60: i64, cfg: &MotorConfig) -> bool {
    let n = hz.count(HazardType::GateDoor);
    if n == 0 {
        return false;
    }
    let t30 = hazards::t30(t60);
    let (r, h) = (cfg.radius, cfg.height);
    let mut pushed = false;
    let mut i = 0;
    while i < n {
        let d = hz.door(i);
        let lift = hazards::door_lift(&d, t30);
        let bottom = d.center_y + lift - d.half_y;
        let top = d.center_y + lift + d.half_y;
        if s.pos.y >= top || s.pos.y + h <= bottom {
            i += 1;
            continue;
        }
        let dx = s.pos.x - d.center_x;
        let dz = s.pos.z - d.center_z;
        if dx.abs() >= d.half_x + r || dz.abs() >= d.half_z + r {
            i += 1;
            continue;
        }
        let clear = r + cfg.skin + 2;
        if d.normal_axis() == 0 {
            let side = if dx >= 0 { 1 } else { -1 };
            s.pos.x = d.center_x + side * (d.half_x + clear);
            if s.vel.x * side < 0 {
                s.vel.x = 0;
            }
        } else {
            let side = if dz >= 0 { 1 } else { -1 };
            s.pos.z = d.center_z + side * (d.half_z + clear);
            if s.vel.z * side < 0 {
                s.vel.z = 0;
            }
        }
        pushed = true;
        i += 1;
    }
    pushed
}

/// `PlayerStep.Run`: doors → motor → knocks (else springs) → fall volume / goo. Returns `step_result` bits.
pub fn player_step(cfg: &MotorConfig, s: &mut MotorState, cmd: &InputCmd, w: &World, map: &MapView, sc: &Scene, tick: u32) -> u8 {
    if s.has(flag::RESPAWNING) {
        return 0;
    }
    let hz = sc.hz(w);
    let t60 = w.t60(tick);
    let mut r = 0u8;
    if apply_doors(s, &hz, t60, cfg) {
        r |= step_result::DOOR_PUSHED;
    }
    motor::step(cfg, s, cmd, sc);
    if apply_knocks(s, &hz, t60, cfg) {
        r |= step_result::HAZARD_HIT;
    } else if apply_springs(s, &hz) {
        r |= step_result::LAUNCHED;
    }
    let ky = map.kill_y();
    let killed = if ky != i32::MIN && ky != 0 { s.pos.y < ky } else { s.pos.y < cfg.kill_y };
    if !killed && hz.count(HazardType::GooPlane) > 0 && s.pos.y < hazards::goo_y(&hz.goo(0), t60) {
        s.vel = IVec3::ZERO;
        s.flags = flag::RESPAWNING | (s.flags & flag::BOT);
        s.surface = 0;
        return r | step_result::GOOED;
    }
    if killed {
        s.vel = IVec3::ZERO;
        s.flags = flag::RESPAWNING | (s.flags & flag::BOT);
        s.surface = 0;
        r |= step_result::FELL_OUT;
    }
    r
}

// ------------------------------------------------------------------ step

/// Advances `w` by one tick. `inputs[slot]` is the command that arrived for this tick (`None` = missing: the last one
/// is repeated with edges cleared for 3 ticks, then neutral). Bots ignore `inputs` and run [`crate::bots::bot_input`].
#[inline(never)] // sBPF: keep step's frame separate from its callers (4 KB frames)
pub fn step<H: Hasher>(w: &mut World, inputs: &[Option<InputCmd>; MAX_PLAYERS], map: &MapView, params: &MovementParams, h: &mut H, ev: &mut StepEvents) {
    let mut sc = Scene::new(*map);
    step_with(w, inputs, map, params, h, ev, &mut sc);
}

/// Work units (≈ 100 sBPF CU each) a bot brain may spend per tick; past it the bot plans with what it has sampled
/// (P2 0.2 deterministic work budget).
pub const BOT_WORK: u32 = 1200;

/// Work units a part may spend per slot it steps (the part's hard total: slots × this). Once a part's total is
/// spent, its remaining players keep their state this tick (stopped). Bounds every part's compute units.
pub const PART_WORK_PER_SLOT: u32 = 600;

/// Work units one player's motor step may spend per tick; past it the player keeps its last safe state (stopped).
pub const MOTOR_WORK: u32 = 1500;

/// [`step`] with caller-provided `Scene` storage (reset and posed here). The program passes a heap `Scene`, so no
/// frame holds it next to the step's own locals.
#[inline(never)]
pub fn step_with<'m, H: Hasher>(w: &mut World, inputs: &[Option<InputCmd>; MAX_PLAYERS], map: &MapView<'m>, params: &MovementParams, h: &mut H, ev: &mut StepEvents, scene: &mut Scene<'m>) {
    step_part(w, inputs, map, params, h, ev, scene, 0, 1);
}


/// One player's step inside a tick (bot brain or input, motor, budgets): the body shared by [`step_part`] and
/// [`step_slice`].
#[inline(never)]
#[allow(clippy::too_many_arguments)]
fn step_player<'m, H: Hasher>(w: &mut World, inputs: &[Option<InputCmd>; MAX_PLAYERS], map: &MapView<'m>, cfg: &MotorConfig, sc: &Scene<'m>, h: &mut H, ev: &mut StepEvents, tick: u32, slot: usize, part_limit: u32) {
        // the bot brain's work budget (BOT_WORK): past it the bot plans with what it has looked at (no revert)
        let w0 = sc.work.get();
        sc.work_cap.set(w0.saturating_add(BOT_WORK).min(part_limit));
        let mut cmd = if w.players[slot].is_bot() {
            crate::bots::bot_input(slot, w, map, sc)
        } else {
            let mut t = w.players[slot].input;
            let c = t.consume(inputs[slot]);
            w.players[slot].input = t;
            c
        };
        h.mark(16 + slot as u8);
        if !crate::rules::accepts_input(w, slot) {
            cmd = InputCmd { cam_yaw: cmd.cam_yaw, ..InputCmd::NEUTRAL };
        }
        w.players[slot].last_step = 0;
        if !crate::rules::simulates(w, slot) {
            return;
        }
        if w.players[slot].m.has(flag::RESPAWNING) {
            let rt = w.players[slot].respawn_ticks.saturating_sub(1);
            w.players[slot].respawn_ticks = rt;
            if rt == 0 && !crate::rules::respawn(w, map, slot, ev) {
                w.players[slot].m = w.spawn_state(map, slot);
            }
            return;
        }
        let mut m = w.players[slot].m;
        // the motor's work budget (MOTOR_WORK): past it the step is reverted below
        let w1 = sc.work.get();
        ev.bot_work_max = ev.bot_work_max.max(w1 - w0);
        sc.work_cap.set(w1.saturating_add(MOTOR_WORK).min(part_limit.max(w1 + MOTOR_WORK / 4)));
        let mut r = player_step(cfg, &mut m, &cmd, w, map, sc, tick);
        ev.motor_work_max = ev.motor_work_max.max(sc.work.get() - w1);
        h.mark(32 + slot as u8);
        if sc.over() {
            // Work budget spent: queries reported nothing past the limit, so the step is not trusted. The player keeps
            // its last safe state, stopped (deterministic on every target).
            m = w.players[slot].m;
            m.vel = IVec3::ZERO;
            r = 0;
            ev.capped = ev.capped.saturating_add(1);
        }
        let p = &mut w.players[slot];
        p.m = m;
        p.last_step = r;
        if r & step_result::HAZARD_HIT != 0 {
            p.hits = p.hits.saturating_add(1);
        }
        if r & (step_result::FELL_OUT | step_result::GOOED) != 0 {
            p.respawn_ticks = cfg.respawn_ticks.clamp(1, 255) as u8;
            p.falls = p.falls.saturating_add(1);
        }
}

/// Part `part` of `parts` of one tick (P2 split tick): part 0 advances the tick and runs the director / round
/// start-of-tick rules, every part re-poses the scene for that tick and steps its slots
/// `[part·12/parts, (part+1)·12/parts)`, the last part separates and runs the end-of-tick rules. Running parts
/// 0..parts in order gives exactly the unsplit step (the slots are stepped in the same order on the same state;
/// the scene of a tick depends only on state the player loop does not change). Proven by `rush-sim split-check`.
#[allow(clippy::too_many_arguments)]
pub fn step_part<'m, H: Hasher>(w: &mut World, inputs: &[Option<InputCmd>; MAX_PLAYERS], map: &MapView<'m>, params: &MovementParams, h: &mut H, ev: &mut StepEvents, scene: &mut Scene<'m>, part: u8, parts: u8) {
    let parts = parts.clamp(1, MAX_PLAYERS as u8) as usize;
    let part = (part as usize).min(parts - 1);
    if part == 0 {
        w.tick = w.tick.wrapping_add(1);
        crate::rules::director::before_tick(w, map, w.tick, h, ev);
    }
    let tick = w.tick;
    h.mark(1);
    let cfg = w.motor_config(params);
    pose_scene_into(scene, w, map, &cfg, tick);
    let sc: &Scene<'m> = scene;
    if part == 0 {
        crate::rules::begin_tick(w, map, tick, ev);
    }
    h.mark(2);

    let mut slot = part * MAX_PLAYERS / parts;
    let end = (part + 1) * MAX_PLAYERS / parts;
    let part_limit = sc.work.get().saturating_add(PART_WORK_PER_SLOT * (end - slot) as u32);
    while slot < end {
        if !w.players[slot].active() {
            slot += 1;
            continue;
        }
        if sc.work.get() >= part_limit && crate::rules::simulates(w, slot) && !w.players[slot].m.has(flag::RESPAWNING) {
            // the part's work total is spent: this player holds still this tick (inputs are not consumed)
            w.players[slot].m.vel = IVec3::ZERO;
            w.players[slot].last_step = 0;
            ev.capped = ev.capped.saturating_add(1);
            slot += 1;
            continue;
        }
        step_player(w, inputs, map, &cfg, sc, h, ev, tick, slot, part_limit);
        slot += 1;
    }
    sc.work_cap.set(u32::MAX);
    if part + 1 == parts {
        separate(w, &cfg, sc);
        h.mark(3);
        crate::rules::end_tick(w, map, sc, tick, ev);
        crate::rules::director::after_tick(w, map, tick, h, ev);
    }
    h.mark(4);
}


/// Work units one [`step_slice`] call may spend on players before it stops (the chain: one timed-task execution).
pub const SLICE_WORK: u32 = 1000;

/// Fixed work units charged per player stepped in a slice (its cost outside the metered queries).
pub const PLAYER_BASE_WORK: u32 = 150;

/// The P2 tick as the chain runs it: a slice of one tick, sized by the deterministic work meter. `from` is where the
/// tick stands: 0 = start a new tick (tick + 1, director, start-of-tick rules, then players from slot 0), 1..=11 =
/// continue with that slot, 12 = only the end of the tick (separation, end-of-tick rules). Players are stepped in
/// slot order while this call's work stays under `budget` (at least one player per call); the end-of-tick stage runs
/// when the players are done and budget is left. Returns the next `from` (0 = the tick is complete).
/// A heavy tick therefore takes more calls; every call's work is bounded. Same results on every target.
#[allow(clippy::too_many_arguments)]
pub fn step_slice<'m, H: Hasher>(w: &mut World, inputs: &[Option<InputCmd>; MAX_PLAYERS], map: &MapView<'m>, params: &MovementParams, h: &mut H, ev: &mut StepEvents, scene: &mut Scene<'m>, from: u8, budget: u32) -> u8 {
    let from = (from as usize).min(MAX_PLAYERS);
    if from == 0 {
        w.tick = w.tick.wrapping_add(1);
        crate::rules::director::before_tick(w, map, w.tick, h, ev);
    }
    let tick = w.tick;
    h.mark(1);
    let cfg = w.motor_config(params);
    pose_scene_into(scene, w, map, &cfg, tick);
    let sc: &Scene<'m> = scene;
    if from == 0 {
        crate::rules::begin_tick(w, map, tick, ev);
    }
    h.mark(2);
    let w_start = sc.work.get();
    let limit = w_start.saturating_add(budget);
    let mut slot = from;
    let mut stepped = 0;
    while slot < MAX_PLAYERS {
        if !w.players[slot].active() {
            slot += 1;
            continue;
        }
        if stepped > 0 && sc.work.get() >= limit {
            sc.work_cap.set(u32::MAX);
            h.mark(4);
            return slot as u8;
        }
        // the per-player cost the meter does not see (input / bot bookkeeping, hazard contacts, motor set-up)
        sc.spend(PLAYER_BASE_WORK);
        step_player(w, inputs, map, &cfg, sc, h, ev, tick, slot, u32::MAX);
        stepped += 1;
        slot += 1;
    }
    sc.work_cap.set(u32::MAX);
    if stepped > 0 && sc.work.get() >= limit {
        h.mark(4);
        return MAX_PLAYERS as u8; // the end of the tick runs in the next call
    }
    separate(w, &cfg, sc);
    h.mark(3);
    crate::rules::end_tick(w, map, sc, tick, ev);
    crate::rules::director::after_tick(w, map, tick, h, ev);
    h.mark(4);
    0
}

/// `SimCore.Separate`: overlapping capsules pushed apart at up to `push.maxSpeed`; 66 pairs in slot order.
pub fn separate(w: &mut World, cfg: &MotorConfig, sc: &Scene) {
    let min_d = 2 * cfg.radius as i64;
    let max_step = div_round(cfg.push_max_speed as i64, cfg.rate as i64);
    let mut push = [[0i64; 2]; MAX_PLAYERS];
    let mut any = false;
    let mut i = 0;
    while i < MAX_PLAYERS {
        if !sep_eligible(w, i) {
            i += 1;
            continue;
        }
        let mut j = i + 1;
        while j < MAX_PLAYERS {
            if !sep_eligible(w, j) {
                j += 1;
                continue;
            }
            let (a, b) = (w.players[i].m, w.players[j].m);
            let dy = a.pos.y - b.pos.y;
            if dy >= 1000 || dy <= -1000 {
                j += 1;
                continue;
            }
            let dx = (a.pos.x - b.pos.x) as i64;
            let dz = (a.pos.z - b.pos.z) as i64;
            let d2 = dx * dx + dz * dz;
            if d2 >= min_d * min_d {
                j += 1;
                continue;
            }
            let d = isqrt_i64(d2);
            let (nx, nz) = if d >= 1 {
                let n = normalize_q15(dx, 0, dz);
                (n[0] as i64, n[2] as i64)
            } else {
                let n = yaw_dir_q15((i * 37 + j * 11) as u8);
                (n[0] as i64, n[1] as i64)
            };
            if cfg.bump_enabled {
                bump(w, i, j, nx, nz, cfg);
            }
            let m = div_round(min_d - d, 2).min(div_round(max_step, 2));
            let px = div_round(nx * m, ONE_Q15 as i64);
            let pz = div_round(nz * m, ONE_Q15 as i64);
            push[i][0] += px;
            push[i][1] += pz;
            push[j][0] -= px;
            push[j][1] -= pz;
            any = true;
            j += 1;
        }
        i += 1;
    }
    if !any {
        return;
    }
    let mut i = 0;
    while i < MAX_PLAYERS {
        let (mut vx, mut vz) = (push[i][0], push[i][1]);
        let l2 = vx * vx + vz * vz;
        if l2 > 0 {
            if l2 > max_step * max_step {
                let l = isqrt_i64(l2).max(1);
                vx = div_round(vx * max_step, l);
                vz = div_round(vz * max_step, l);
            }
            let mut m = w.players[i].m;
            motor::displace(cfg, &mut m, IVec3::new(vx as i32, 0, vz as i32), sc);
            w.players[i].m = m;
        }
        i += 1;
    }
}

fn sep_eligible(w: &World, i: usize) -> bool {
    let p = &w.players[i];
    p.active() && !p.m.has(flag::RESPAWNING) && crate::rules::collides(w, i)
}

/// `SimCore.Bump` (G4.1c, flag off in movement_v3/v4).
fn bump(w: &mut World, i: usize, j: usize, nx: i64, nz: i64, cfg: &MotorConfig) {
    let (a, b) = (w.players[i].m, w.players[j].m);
    // closing speed along n (b → a), mm/s: −(va − vb)·n
    let closing = -div_round((a.vel.x - b.vel.x) as i64 * nx + (a.vel.z - b.vel.z) as i64 * nz, ONE_Q15 as i64);
    if closing < cfg.bump_min_closing as i64 {
        return;
    }
    let sa = a.vel.x as i64 * a.vel.x as i64 + a.vel.z as i64 * a.vel.z as i64;
    let sb = b.vel.x as i64 * b.vel.x as i64 + b.vel.z as i64 * b.vel.z as i64;
    if sa <= sb {
        stagger(&mut w.players[i].m, cfg);
    }
    if sb <= sa {
        stagger(&mut w.players[j].m, cfg);
    }
}

fn stagger(s: &mut MotorState, cfg: &MotorConfig) -> bool {
    if s.stun != 0 || s.immunity != 0 || s.flags & (flag::STUMBLING | flag::DIVING | flag::DIVE_LANDING | flag::RESPAWNING) != 0 {
        return false;
    }
    s.set(flag::STUMBLING, true);
    s.land = cfg.bump_ticks.clamp(0, 255) as u8;
    true
}

// ------------------------------------------------------------------ vectors

/// World vector case `i` (`world_v1.bin`): 12 players (half bots) for 40 ticks of random inputs on `map` at a
/// random rate; writes the World hash.
/// The 40 random ticks of a world vector case (own frame: sBPF stack frames are 4 KB).
#[inline(never)]
fn vector_ticks(w: &mut World, map: &MapView, r: &mut crate::vectors::Rng) {
    let mut sc = Scene::new(*map);
    vector_ticks_with(w, map, r, &mut sc);
}

/// [`vector_ticks`] on caller `Scene` storage (the sBPF parity harness keeps it on the heap).
#[inline(never)]
fn vector_ticks_with<'m>(w: &mut World, map: &MapView<'m>, r: &mut crate::vectors::Rng, sc: &mut Scene<'m>) {
    let params = MovementParams::default();
    let mut ev = StepEvents::default();
    let mut k = 0;
    while k < 40 {
        let mut inputs = [None; MAX_PLAYERS];
        let mut s = 0;
        while s < MAX_PLAYERS {
            if r.range(0, 9) != 0 {
                inputs[s] = Some(InputCmd { move_x: r.range(-127, 127) as i8, move_y: r.range(-127, 127) as i8, buttons: (r.next() as u8) & 0x0F, cam_yaw: r.next() as u16 });
            }
            s += 1;
        }
        ev.n = 0;
        step_with(w, &inputs, map, &params, &mut NoHasher, &mut ev, sc);
        k += 1;
    }
}

/// [`vector_case`] with caller `Scene` storage (`sc` must be a scene of `map`).
pub fn vector_case_with<'m>(w: &mut World, sc: &mut Scene<'m>, map: &MapView<'m>, seed: u64, i: u32, out: &mut [u8]) -> usize {
    let mut r = crate::vectors::Rng(seed ^ 0x5017_0000 ^ (i as u64).wrapping_mul(0xD6E8_FEB8_6659_FD93));
    r.next();
    let rate = [15u32, 20, 30][r.range(0, 2) as usize];
    w.reset(map, rate);
    w.hazard_seed = r.next();
    let mut s = 0;
    while s < MAX_PLAYERS {
        w.add_player(map, s, s % 2 == 1);
        s += 1;
    }
    vector_ticks_with(w, map, &mut r, sc);
    let mut wr = crate::vectors::W { b: out, n: 0 };
    wr.u8(rate as u8);
    wr.i64(w.hash() as i64);
    wr.n
}

pub fn vector_case(w: &mut World, map_bytes: &[u8], seed: u64, i: u32, out: &mut [u8]) -> usize {
    let map = match MapView::new(map_bytes) {
        Ok(m) => m,
        Err(_) => return 0,
    };
    let mut r = crate::vectors::Rng(seed ^ 0x5017_0000 ^ (i as u64).wrapping_mul(0xD6E8_FEB8_6659_FD93));
    r.next();
    let rate = [15u32, 20, 30][r.range(0, 2) as usize];
    w.reset(&map, rate);
    w.hazard_seed = r.next();
    let mut s = 0;
    while s < MAX_PLAYERS {
        w.add_player(&map, s, s % 2 == 1);
        s += 1;
    }
    vector_ticks(w, &map, &mut r);
    let mut wr = crate::vectors::W { b: out, n: 0 };
    wr.u8(rate as u8);
    wr.i64(w.hash() as i64);
    wr.n
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn world_fits_2kb_and_roundtrips() {
        let b = crate::mapblob::builder::build(&crate::mapblob::testmaps::flat_arena_spec());
        let m = MapView::new(&b).unwrap();
        let mut w = World::new(&m, 20);
        for s in 0..MAX_PLAYERS {
            w.add_player(&m, s, s >= 6);
        }
        let mut buf = [0u8; 4096];
        let n = w.to_bytes(&mut buf);
        std::println!("World canonical size: {n} bytes");
        assert!(n <= WORLD_MAX_BYTES, "World is {n} bytes > {WORLD_MAX_BYTES}");
        let back = World::from_bytes(&buf[..n]).unwrap();
        assert_eq!(back, w);
    }

    #[test]
    fn step_is_deterministic() {
        let b = crate::mapblob::builder::build(&crate::mapblob::testmaps::flat_arena_spec());
        let m = MapView::new(&b).unwrap();
        let run = || {
            let mut w = World::new(&m, 20);
            for s in 0..MAX_PLAYERS {
                w.add_player(&m, s, false);
            }
            let mut ev = StepEvents::default();
            for t in 0..200u32 {
                let mut inputs = [None; MAX_PLAYERS];
                for (s, inp) in inputs.iter_mut().enumerate() {
                    *inp = Some(InputCmd { move_x: ((t * 7 + s as u32 * 13) % 255) as u8 as i8, move_y: 100, buttons: if t % 17 == 0 { 3 } else { 0 }, cam_yaw: 0 });
                }
                step(&mut w, &inputs, &m, &MovementParams::default(), &mut NoHasher, &mut ev);
            }
            w.hash()
        };
        assert_eq!(run(), run());
    }

    #[test]
    fn separation_pushes_apart() {
        let b = crate::mapblob::builder::build(&crate::mapblob::testmaps::flat_arena_spec());
        let m = MapView::new(&b).unwrap();
        let mut w = World::new(&m, 20);
        w.add_player(&m, 0, false);
        w.add_player(&m, 1, false);
        w.players[0].m.pos = IVec3::new(0, 10, 0);
        w.players[1].m.pos = IVec3::new(100, 10, 0);
        let mut ev = StepEvents::default();
        for _ in 0..20 {
            step(&mut w, &[None; MAX_PLAYERS], &m, &MovementParams::default(), &mut NoHasher, &mut ev);
        }
        let d = (w.players[1].m.pos.x - w.players[0].m.pos.x).abs();
        assert!(d >= 610, "separated to {d} mm");
    }
}
