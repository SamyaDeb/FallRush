//! Bots (port of `Shared/BotBrain.cs` + `Shared/WaypointBot.cs` + the `Net/BotBrainSet.cs` glue). One function,
//! [`bot_input`], serves both the on-chain tick (inside `world::step`) and off-chain runners. Bots read only what a
//! player can see: their own motor state, the other Rushlings' positions, the pure hazard poses, the replicated
//! reactive state (stars, tiles) and the map's routes. Integers only: C# floats become mm / Q15 / yaw256, and every
//! tick constant authored at 30 Hz is converted with `MotorConfig::ticks30`.
//!
//! ## `BOT_ROUTES` blob section (written by `Editor/MapBlobExport.cs` from `MapInfo.botRoutes`)
//! ```text
//! word 0   route count R (≤ 64)
//! per route (in MapInfo.botRoutes order):
//!   section            checkpoints reached when it starts (Race) / spiral side (Survival)
//!   n                  waypoint count (≤ 64)
//!   n × { x, y, z, radius (mm), action, arg }     action = WaypointAction (0 Run … 7 Land)
//! ```
//! A section without routes (or a map without any) falls back to the course polyline (Race) or the mode heuristics.
//!
//! ## Per-bot memory ([`BotState`], 26 bytes in `World`)
//! Ticks are kept as their low 16 bits (wrap-safe comparisons; every window is ≪ 65,536 ticks).
//!
//! ## Simplifications vs C# (listed in the proof)
//! - steering *reaction delay* is a first-order lag (2 bytes) instead of the C# 16-tick move history; wobble, speed scale, late jumps
//!   and hesitation are kept;
//! - Skill: island stars (spring-pad trips), the burst look-ahead and the camper probe are not ported; pads and bumpers
//!   are avoided with a simpler side push;
//! - WaitClear with `Arg ≥ 1` samples the next `Arg` points at run speed (no per-point dwell / jump arc); the G4.2 run-up
//!   before jumps is dropped; the 7-heading rolling-ball dodge becomes "side-step or stop".

mod nav;
mod plans;

use crate::codec::{Codec, Dec, Enc};
use crate::collide::Scene;
use crate::config::MotorConfig;
use crate::fx::{cos_q15, div_round, isqrt_i64, sin_q15, ONE_Q15};
use crate::input::{sanitize, InputCmd, DIVE_HELD, DIVE_PRESSED, JUMP_HELD, JUMP_PRESSED};
use crate::mapblob::{mode, MapView};
use crate::motor::{flag, MotorState};
use crate::world::{World, MAX_PLAYERS};

/// `BotTier` (Reference = 4 here; 0 = not chosen yet, rolled from the seed on the first tick: 30/50/20).
pub mod tier {
    pub const AUTO: u8 = 0;
    pub const EASY: u8 = 1;
    pub const NORMAL: u8 = 2;
    pub const GOOD: u8 = 3;
    pub const REFERENCE: u8 = 4;
}

/// [`BotState::flags`].
pub mod bf {
    pub const INIT: u8 = 1;
    pub const WAS_STUNNED: u8 = 2;
    pub const WAS_RESPAWNING: u8 = 4;
    pub const PENDING_JUMP: u8 = 8;
    pub const WAS_WAITING: u8 = 16;
    pub const NAV_LIVE: u8 = 32;
    pub const LID_IGNORE: u8 = 64;
}

/// Per-bot memory kept in `World` (26 bytes canonical).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct BotState {
    pub tier: u8,
    pub flags: u8,
    pub rng: u32,
    /// Route index in BOT_ROUTES (255 = none / course fallback).
    pub route: u8,
    pub wp: u8,
    /// Race: checkpoints at route choice; Survival: spiral side.
    pub section: u8,
    pub blocked: u8,
    /// Skill star id / Final lid or tile / Race course index (−1 none).
    pub target: i16,
    pub jump_held_until: u16,
    pub hesitate_until: u16,
    pub pending_jump_at: u16,
    pub progress_tick: u16,
    /// WaitClear start (Race / Survival) or lid re-score / ignore-until tick (Final).
    pub aux_tick: u16,
    /// Best distance to the current waypoint (mm / 16, saturating).
    pub best_d: u16,
    /// Steering after the reaction lag (the last move sent).
    pub lag_x: i8,
    pub lag_y: i8,
}

impl Codec for BotState {
    fn enc(&self, e: &mut Enc) {
        e.u8(self.tier);
        e.u8(self.flags);
        e.u32(self.rng);
        e.u8(self.route);
        e.u8(self.wp);
        e.u8(self.section);
        e.u8(self.blocked);
        e.i16(self.target);
        for v in [self.jump_held_until, self.hesitate_until, self.pending_jump_at, self.progress_tick, self.aux_tick, self.best_d] {
            e.u16(v);
        }
        e.i8(self.lag_x);
        e.i8(self.lag_y);
    }
    fn dec(d: &mut Dec) -> Self {
        BotState {
            tier: d.u8(),
            flags: d.u8(),
            rng: d.u32(),
            route: d.u8(),
            wp: d.u8(),
            section: d.u8(),
            blocked: d.u8(),
            target: d.i16(),
            jump_held_until: d.u16(),
            hesitate_until: d.u16(),
            pending_jump_at: d.u16(),
            progress_tick: d.u16(),
            aux_tick: d.u16(),
            best_d: d.u16(),
            lag_x: d.i8(),
            lag_y: d.i8(),
        }
    }
}

impl BotState {
    #[inline]
    pub fn has(&self, f: u8) -> bool {
        self.flags & f != 0
    }
    #[inline]
    pub fn set(&mut self, f: u8, on: bool) {
        if on {
            self.flags |= f;
        } else {
            self.flags &= !f;
        }
    }
    /// xorshift32 (`BotBrain.Next`).
    pub fn rand(&mut self) -> u32 {
        let mut r = self.rng;
        r ^= r << 13;
        r ^= r >> 17;
        r ^= r << 5;
        self.rng = r;
        r
    }
}

/// `BotBrain.Skill` per tier (30 Hz tick values) + the tier knobs from WaypointBot / the lid plan.
#[derive(Clone, Copy, Debug)]
pub struct Skill {
    pub wobble_deg: i64,
    /// Steering reaction delay (30 Hz ticks; `BotBrain` ReactionTicks), as a first-order lag of that time constant.
    pub reaction_ticks: i64,
    pub late_jump_ticks: i32,
    pub speed_permille: i64,
    pub hesitate_pct: u32,
    pub hesitate_ticks: i32,
    pub star_pick_range_mm: i64,
    pub timing_slop: i32,
    pub lid_lag: i32,
    pub lid_misjudge_pct: u32,
}

pub fn skill(t: u8) -> Skill {
    match t {
        tier::EASY => Skill { reaction_ticks: 8, wobble_deg: 22, late_jump_ticks: 3, speed_permille: 820, hesitate_pct: 35, hesitate_ticks: 40, star_pick_range_mm: 12_000, timing_slop: 5, lid_lag: 12, lid_misjudge_pct: 45 },
        tier::NORMAL => Skill { reaction_ticks: 5, wobble_deg: 12, late_jump_ticks: 2, speed_permille: 900, hesitate_pct: 18, hesitate_ticks: 25, star_pick_range_mm: 18_000, timing_slop: 4, lid_lag: 9, lid_misjudge_pct: 30 },
        tier::GOOD => Skill { reaction_ticks: 2, wobble_deg: 5, late_jump_ticks: 1, speed_permille: 970, hesitate_pct: 6, hesitate_ticks: 12, star_pick_range_mm: 30_000, timing_slop: 2, lid_lag: 3, lid_misjudge_pct: 10 },
        _ => Skill { reaction_ticks: 0, wobble_deg: 0, late_jump_ticks: 0, speed_permille: 1000, hesitate_pct: 0, hesitate_ticks: 0, star_pick_range_mm: 40_000, timing_slop: 0, lid_lag: 0, lid_misjudge_pct: 0 },
    }
}

/// Tier from the seed (30 % Easy, 50 % Normal, 20 % Good), stable per (seed, slot).
pub fn roll_tier(seed: u64, slot: usize) -> u8 {
    let r = mix(seed ^ (slot as u64).wrapping_mul(0x9E37_79B9_7F4A_7C15)) % 100;
    if r < 30 {
        tier::EASY
    } else if r < 80 {
        tier::NORMAL
    } else {
        tier::GOOD
    }
}

pub(crate) fn mix(mut z: u64) -> u64 {
    z = z.wrapping_add(0x9E37_79B9_7F4A_7C15);
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
    z ^ (z >> 31)
}

// ------------------------------------------------------------------ 16-bit tick helpers

#[inline]
pub(crate) fn lo(t: u32) -> u16 {
    t as u16
}

/// `now < until` for 16-bit tick stamps.
#[inline]
pub(crate) fn before(now: u32, until: u16) -> bool {
    (until.wrapping_sub(now as u16) as i16) > 0
}

/// Ticks since stamp `t`.
#[inline]
pub(crate) fn since(now: u32, t: u16) -> i32 {
    (now as u16).wrapping_sub(t) as i16 as i32
}

/// Everything a bot reads this tick.
pub(crate) struct Ctx<'a, 'm> {
    pub w: &'a World,
    pub map: &'a MapView<'m>,
    pub sc: &'a Scene<'m>,
    pub cfg: MotorConfig,
    pub slot: usize,
    pub tick: u32,
    pub s: MotorState,
    pub sk: Skill,
    pub reference: bool,
    /// Hazard samples left for this tick's path checks (P2 0.2 deterministic work budget, [`BOT_PROBES`]).
    pub probes: core::cell::Cell<u16>,
}

/// Most hazard samples (`nav::hazard_at`) one bot's path checks may take in a tick. When a check runs out, the part
/// not sampled is taken as clear (a player does not look further ahead either). Counted, so identical on every target.
pub const BOT_PROBES: u16 = 96;

/// A bot waiting for a clear path re-checks every this many ticks (30 Hz), on its own slot-staggered tick.
pub const WAIT_GLANCE_TICKS: u32 = 3;

/// Race rounds: bots' (non-precise) stick scale ‰ on top of the tier's (P2 0.3 pacing; 1000 = P1).
pub const RACE_PACE_PERMILLE: i32 = 875;

impl Ctx<'_, '_> {
    /// A 30 Hz tick count at the configured rate (≥ 1 for positive counts).
    #[inline]
    pub fn t(&self, t30: i32) -> i32 {
        if t30 <= 0 {
            0
        } else {
            self.cfg.ticks30(t30).max(1)
        }
    }
    /// Hazard time (1/60 s) `ahead` ticks from now.
    #[inline]
    pub fn t60(&self, ahead: i64) -> i64 {
        self.w.t60(self.tick) + ahead * self.cfg.t60_per_tick as i64
    }
    /// P4 Sweeper Rush: the extra sweeper time `ahead` ticks from now (0 with no drop queued), so bots read the bar
    /// where it really is.
    pub fn sweep_warp(&self, ahead: i64) -> i64 {
        if self.w.drop_queue[0].kind == crate::drops::kind::NONE {
            return 0;
        }
        crate::drops::sweeper_warp60(&self.w.drop_queue, (self.tick as i64 + ahead.max(0)) as u32, self.cfg.t60_per_tick as i64)
    }
    /// Run speed in mm per tick.
    #[inline]
    pub fn run_per_tick(&self) -> i64 {
        (self.cfg.max_speed as i64 / self.cfg.rate as i64).max(20)
    }
    /// Another Rushling the bot can see (active, simulated, not respawning).
    pub fn other(&self, i: usize) -> Option<MotorState> {
        if i == self.slot || i >= MAX_PLAYERS || !self.w.players[i].active() {
            return None;
        }
        let m = self.w.players[i].m;
        if m.has(flag::RESPAWNING) || !crate::rules::simulates(self.w, i) {
            return None;
        }
        Some(m)
    }
}

/// A planned command before the tier noise.
#[derive(Clone, Copy, Debug, Default)]
pub(crate) struct Plan {
    pub cmd: InputCmd,
    /// A precise move (gap jump, landing, platform, bar, door): no wobble, full speed, ≤ 1 tick late jumps.
    pub precise: bool,
    /// Waiting at a door / bar / lift this tick (hesitation trigger).
    pub waiting: bool,
}

impl Plan {
    pub fn of(cmd: InputCmd) -> Plan {
        Plan { cmd, precise: false, waiting: false }
    }
}

/// `SteerTo`: full stick toward (tx, tz) when farther than `deadzone` mm.
pub(crate) fn steer_to(s: &MotorState, tx: i32, tz: i32, deadzone: i64) -> InputCmd {
    steer_dir(s, tx as i64 - s.pos.x as i64, tz as i64 - s.pos.z as i64, deadzone)
}

pub(crate) fn steer_dir(s: &MotorState, dx: i64, dz: i64, deadzone: i64) -> InputCmd {
    let mut c = InputCmd::NEUTRAL;
    let l = isqrt_i64(dx * dx + dz * dz);
    if l > deadzone && l > 0 {
        c.move_x = div_round(dx * 127, l) as i8;
        c.move_y = div_round(dz * 127, l) as i8;
        let (x, z) = (div_round(dx * 1000, l) as i32, div_round(dz * 1000, l) as i32);
        c.cam_yaw = (crate::fx::atan2_yaw256(x, z, s.yaw) as u16) << 8;
    }
    c
}

/// The input bot `slot` sends this tick.
pub fn bot_input(slot: usize, w: &mut World, map: &MapView, sc: &Scene) -> InputCmd {
    let mut b = w.players[slot].bot;
    let tick = w.tick;
    if !b.has(bf::INIT) {
        if b.tier == tier::AUTO {
            b.tier = roll_tier(w.hazard_seed, slot);
        }
        // BotBrainSet seed: (uint)(seed · 2654435761) + slot · 7919; BotBrain: · 0x9E3779B1 + slot · 0x85EBCA6B + 7.
        let seed = (w.hazard_seed.wrapping_mul(2_654_435_761) as u32).wrapping_add(slot as u32 * 7919);
        b.rng = seed.wrapping_mul(0x9E37_79B1).wrapping_add((slot as u32).wrapping_mul(0x85EB_CA6B)).wrapping_add(7);
        if b.rng == 0 {
            b.rng = 1;
        }
        b.route = 255;
        b.target = -1;
        b.set(bf::INIT, true);
    }
    // Rounds: idle outside Rush and forget the navigation (the next round starts fresh). Free play (sandbox / lobby)
    // runs the race navigation on the course.
    let free = w.round.mode == mode::SANDBOX || w.round.mode == mode::LOBBY;
    if !free && w.round.phase != crate::rules::phase::RUSH {
        b.set(bf::NAV_LIVE, false);
        b.route = 255;
        b.target = -1;
        w.players[slot].bot = b;
        return InputCmd::NEUTRAL;
    }
    let cmd = {
        let w: &World = w;
        let ctx = Ctx {
            w,
            map,
            sc,
            cfg: w.motor_config(&crate::config::MovementParams::default()),
            slot,
            tick,
            s: w.players[slot].m,
            sk: skill(b.tier),
            reference: b.tier == tier::REFERENCE,
            probes: core::cell::Cell::new(BOT_PROBES),
        };
        let m = if free { mode::RACE } else { w.round.mode };
        let mut plan = match m {
            mode::RACE => nav::race(&ctx, &mut b),
            mode::SURVIVAL => nav::survival(&ctx, &mut b),
            mode::SKILL => plans::skill(&ctx, &mut b),
            mode::FINAL => plans::final_plan(&ctx, &mut b),
            _ => Plan::default(),
        };
        unstick(&ctx, &mut b, &mut plan.cmd);
        noise(&ctx, &mut b, plan)
    };
    w.players[slot].bot = b;
    cmd
}

/// `Unstick`: pushing but not moving while grounded for 2/3 s → hop.
fn unstick(c: &Ctx, b: &mut BotState, cmd: &mut InputCmd) {
    let s = &c.s;
    let pushing = cmd.move_x != 0 || cmd.move_y != 0;
    let still = s.vel.x.abs() + s.vel.z.abs() < 600; // C#: 60 cm/s
    if pushing && still && s.has(flag::GROUNDED) && s.stun == 0 {
        b.blocked = b.blocked.saturating_add(1);
    } else {
        b.blocked = 0;
    }
    if (b.blocked as i32) < c.t(20) {
        return;
    }
    b.blocked = 0;
    cmd.buttons |= JUMP_PRESSED | JUMP_HELD;
}

/// `BotBrain.Noise`: hesitation, wobble, speed scale, late jumps (Reference: none).
fn noise(c: &Ctx, b: &mut BotState, plan: Plan) -> InputCmd {
    let tick = c.tick;
    let mut p = plan.cmd;
    if c.reference {
        sanitize(&mut p);
        return p;
    }
    let mut cmd = InputCmd { cam_yaw: p.cam_yaw, ..InputCmd::NEUTRAL };
    if b.has(bf::WAS_WAITING) && !plan.waiting && b.rand() % 100 < c.sk.hesitate_pct {
        let ht = c.t(c.sk.hesitate_ticks).max(1) as u32;
        b.hesitate_until = lo(tick + ht / 2 + b.rand() % ht);
    }
    b.set(bf::WAS_WAITING, plan.waiting);
    if before(tick, b.hesitate_until) {
        return cmd;
    }
    let precise = plan.precise || c.s.surface != 0 || !c.s.has(flag::GROUNDED) || c.w.round.mode == mode::FINAL;
    let (mx, my) = (p.move_x as i64, p.move_y as i64);
    if mx != 0 || my != 0 {
        // Slow wobble: deg · sin(0.07 rad per 30 Hz tick + phase); 0.07 rad / 30 Hz tick = 365 Q16 turns per 1/60 s.
        let deg = if precise { 0 } else { c.sk.wobble_deg };
        let phase = (mix(c.w.hazard_seed ^ 0x0B07 ^ c.slot as u64) & 0xFFFF) as i64;
        let wob = sin_q15(((c.t60(0) * 365 + phase) & 0xFFFF) as i32) as i64;
        let a = div_round(deg * 65536 * wob, 360 * ONE_Q15 as i64) as i32;
        let (cs, sn) = (cos_q15(a) as i64, sin_q15(a) as i64);
        let rx = div_round(mx * cs - my * sn, ONE_Q15 as i64);
        let ry = div_round(mx * sn + my * cs, ONE_Q15 as i64);
        let k = if precise { 1000 } else { c.sk.speed_permille };
        // P2 0.3: race bots pace (on-chain bots define the round length; Capsule Run Normal median 85–110 s)
        let k = if c.w.round.mode == mode::RACE && !c.reference { div_round(k * RACE_PACE_PERMILLE as i64, 1000) } else { k };
        let mut ox = div_round(rx * k, 1000);
        let mut oy = div_round(ry * k, 1000);
        let l = isqrt_i64(ox * ox + oy * oy);
        if l > 127 {
            ox = div_round(ox * 127, l);
            oy = div_round(oy * 127, l);
        }
        // Reaction delay: a first-order lag with a time constant of the tier's reaction ticks (30 Hz), so steering
        // changes arrive ≈ that late (C#: a 16-tick move history; 2 bytes here). Precise moves: no lag.
        let r = if precise { 0 } else { c.sk.reaction_ticks };
        if r > 0 {
            let k = (30_000 / (c.w.rate.max(1) as i64 * r)).min(1000);
            ox = b.lag_x as i64 + div_round((ox - b.lag_x as i64) * k, 1000);
            oy = b.lag_y as i64 + div_round((oy - b.lag_y as i64) * k, 1000);
        }
        cmd.move_x = ox.clamp(-127, 127) as i8;
        cmd.move_y = oy.clamp(-127, 127) as i8;
    }
    b.lag_x = cmd.move_x;
    b.lag_y = cmd.move_y;
    if p.buttons & JUMP_PRESSED != 0 && !b.has(bf::PENDING_JUMP) {
        b.set(bf::PENDING_JUMP, true);
        let late = if precise { c.sk.late_jump_ticks.min(1) } else { c.sk.late_jump_ticks };
        let late = c.t(late) as u32;
        b.pending_jump_at = lo(tick + if late > 0 { b.rand() % (late + 1) } else { 0 });
    }
    if b.has(bf::PENDING_JUMP) && !before(tick, b.pending_jump_at) {
        b.set(bf::PENDING_JUMP, false);
        cmd.buttons |= JUMP_PRESSED | JUMP_HELD;
        b.jump_held_until = lo(tick + c.t(8) as u32);
    } else if before(tick, b.jump_held_until) || (p.buttons & JUMP_HELD != 0 && !b.has(bf::PENDING_JUMP)) {
        cmd.buttons |= JUMP_HELD;
    }
    if p.buttons & DIVE_PRESSED != 0 {
        cmd.buttons |= DIVE_PRESSED | DIVE_HELD;
    }
    p = cmd;
    sanitize(&mut p);
    p
}

/// Sets the tier of bot `slot` before its first tick (QA: `tier::REFERENCE`; the director: the roster tier).
pub fn set_tier(w: &mut World, slot: usize, t: u8) {
    if slot < MAX_PLAYERS {
        w.players[slot].bot.tier = t;
    }
}

#[cfg(test)]
mod tests;
