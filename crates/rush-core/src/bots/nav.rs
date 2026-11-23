//! Route navigation: port of `Shared/WaypointBot.cs` (`Next`, `EnterSection`, `Reacquire`, `Ride`, `PathClear`,
//! `PathClearAhead`, `DoorPassable`, `PlatformInReach`, `ReactToBars`, `DodgeBalls`) and `BotBrain`'s Race / Survival
//! plans. Integers only; 30 Hz tick constants converted with [`Ctx::t`].

use super::{before, lo, since, steer_dir, steer_to, BotState, Ctx, Plan, bf};
use crate::fx::{div_round, isqrt_i64};
use crate::hazards::{self, defs::HazardType};
use crate::input::{InputCmd, DIVE_HELD, DIVE_PRESSED, JUMP_HELD, JUMP_PRESSED};
use crate::motor::flag;

pub const MAX_ROUTES: usize = 64;
pub const MAX_POINTS: usize = 64;
/// Words per waypoint: x, y, z, radius, action, arg.
pub const PW: usize = 6;

/// `WaypointAction`.
pub mod act {
    pub const RUN: i32 = 0;
    pub const JUMP: i32 = 1;
    pub const WAIT_CLEAR: i32 = 2;
    pub const DOOR: i32 = 3;
    pub const JUMP_TO_PLATFORM: i32 = 4;
    pub const PLATFORM: i32 = 5;
    pub const DIVE: i32 = 6;
    pub const LAND: i32 = 7;
}

#[derive(Clone, Copy, Debug, Default)]
pub struct Wp {
    pub x: i32,
    pub y: i32,
    pub z: i32,
    pub radius: i32,
    pub action: i32,
    pub arg: i32,
}

/// A route in BOT_ROUTES: word offset of its first point, section, point count.
#[derive(Clone, Copy, Debug)]
pub struct Route {
    pub off: usize,
    pub section: i32,
    pub n: usize,
}

pub fn route_count(c: &Ctx) -> usize {
    if c.map.bot_words() == 0 {
        0
    } else {
        (c.map.bot_word(0).max(0) as usize).min(MAX_ROUTES)
    }
}

/// The route directory of this map (built once per scene; same values as walking the blob).
fn table(c: &Ctx) -> crate::collide::RouteTable {
    {
        let t = c.sc.routes.borrow();
        if t.built {
            return *t;
        }
    }
    let mut t = crate::collide::RouteTable::default();
    let n = route_count(c);
    let mut o = 1usize;
    let mut i = 0;
    while i < n {
        let pts = (c.map.bot_word(o + 1).max(0) as usize).min(MAX_POINTS);
        t.off[i] = (o + 2).min(u16::MAX as usize) as u16;
        t.section[i] = c.map.bot_word(o).clamp(i16::MIN as i32, i16::MAX as i32) as i16;
        t.pts[i] = pts as u8;
        o += 2 + pts * PW;
        i += 1;
    }
    t.count = n as u8;
    // survival_section: sections 0.. while some route has that section.
    let mut sec = 0;
    while sec < 32 {
        let mut start = i32::MAX;
        let mut any = false;
        let mut i = 0;
        while i < n {
            if t.pts[i] > 0 && t.section[i] as i32 == sec as i32 {
                any = true;
                start = start.min(c.map.bot_word(t.off[i] as usize + 1));
            }
            i += 1;
        }
        if !any {
            break;
        }
        t.sec_start[sec] = start;
        sec += 1;
    }
    t.sec_count = sec as u8;
    t.built = true;
    *c.sc.routes.borrow_mut() = t;
    t
}

pub fn route(c: &Ctx, r: usize) -> Option<Route> {
    let n = route_count(c);
    if r >= n {
        return None;
    }
    let t = c.sc.routes.borrow();
    let (off, sec, pts) = if t.built {
        (t.off[r] as usize, t.section[r] as i32, t.pts[r] as usize)
    } else {
        drop(t);
        let t = table(c);
        (t.off[r] as usize, t.section[r] as i32, t.pts[r] as usize)
    };
    if pts == 0 {
        return None;
    }
    // the directory clamps the section to i16; blob sections are small (≤ 32 in every map)
    Some(Route { off, section: sec, n: pts })
}

pub fn point(c: &Ctx, r: &Route, k: usize) -> Wp {
    let b = r.off + k.min(r.n - 1) * PW;
    let w = |i: usize| c.map.bot_word(b + i);
    Wp { x: w(0), y: w(1), z: w(2), radius: w(3), action: w(4), arg: w(5) }
}

/// `BotBrainSet.UsableRoutes`: a route through a gate the variant keeps (practically) closed is left out.
fn usable(c: &Ctx, r: &Route) -> bool {
    let hz = c.sc.hz(c.w);
    let nd = hz.count(HazardType::GateDoor);
    let mut k = 0;
    while k < r.n {
        let p = point(c, r, k);
        if p.action == act::DOOR && (p.arg as usize) < nd {
            let d = hz.door(p.arg as usize);
            if d.mode == 0 && d.open_ticks < 30 {
                return false;
            }
        }
        k += 1;
    }
    true
}

fn cur(c: &Ctx, b: &BotState) -> Option<Route> {
    if b.route == 255 {
        None
    } else {
        route(c, b.route as usize)
    }
}

/// `EnterSection`.
fn enter_section(c: &Ctx, b: &mut BotState, section: i32, start_at_first: bool) {
    b.section = section.clamp(0, 255) as u8;
    b.route = 255;
    let n = route_count(c);
    let mut count = 0u32;
    let mut i = 0;
    while i < n {
        if let Some(r) = route(c, i) {
            if r.section == section && usable(c, &r) {
                count += 1;
            }
        }
        i += 1;
    }
    if count == 0 {
        return;
    }
    let mut pick = b.rand() % count;
    let mut i = 0;
    while i < n {
        if let Some(r) = route(c, i) {
            if r.section == section && usable(c, &r) {
                if pick == 0 {
                    b.route = i as u8;
                    break;
                }
                pick -= 1;
            }
        }
        i += 1;
    }
    b.wp = 0;
    if start_at_first {
        if let Some(r) = cur(c, b) {
            let p0 = point(c, &r, 0);
            let (dx, dz) = ((p0.x - c.s.pos.x) as i64, (p0.z - c.s.pos.z) as i64);
            if dx * dx + dz * dz < 6000 * 6000 && (p0.y - c.s.pos.y).abs() < 1500 {
                reset_progress(c, b);
                return;
            }
        }
    }
    reacquire(c, b);
}

fn reset_progress(c: &Ctx, b: &mut BotState) {
    b.best_d = u16::MAX;
    b.progress_tick = lo(c.tick);
}

/// `Reacquire`: the nearest waypoint on our level (points far above weigh more), or the one after it if passed.
fn reacquire(c: &Ctx, b: &mut BotState) {
    reset_progress(c, b);
    let r = match cur(c, b) {
        Some(r) => r,
        None => return,
    };
    let s = &c.s;
    let mut best = i64::MAX;
    let mut bi = 0usize;
    let mut i = 0;
    while i < r.n {
        let p = point(c, &r, i);
        let (dx, dz, dy) = ((p.x - s.pos.x) as i64, (p.z - s.pos.z) as i64, (p.y - s.pos.y) as i64);
        let d = dx * dx + dz * dz + if dy > 1300 { 25 } else { 4 } * dy * dy;
        if d < best {
            best = d;
            bi = i;
        }
        i += 1;
    }
    if bi + 1 < r.n {
        let (p, q) = (point(c, &r, bi), point(c, &r, bi + 1));
        let (ax, az) = ((q.x - p.x) as i64, (q.z - p.z) as i64);
        let (px, pz) = ((s.pos.x - p.x) as i64, (s.pos.z - p.z) as i64);
        if ax * px + az * pz > 0 && p.action == act::RUN && q.y - s.pos.y <= 1300 {
            bi += 1;
        }
    }
    b.wp = bi as u8;
}

fn advance(c: &Ctx, b: &mut BotState) {
    b.wp = b.wp.saturating_add(1);
    reset_progress(c, b);
}

/// Target of a waypoint this tick: a Platform point follows the platform; wait / take-off points after a Platform
/// point ride with it (`Ride`).
fn target(c: &Ctx, r: &Route, k: usize) -> (i32, i32) {
    let p = point(c, r, k);
    let hz = c.sc.hz(c.w);
    let np = hz.count(HazardType::MovingPlatform);
    if p.action == act::PLATFORM && (p.arg as usize) < np {
        let ctr = hazards::platform_center(&hz.platform(p.arg as usize), c.t60(0));
        return (ctr.x, ctr.z);
    }
    let (mut tx, mut tz) = (p.x, p.z);
    if p.action == act::JUMP_TO_PLATFORM || p.action == act::WAIT_CLEAR || p.action == act::JUMP {
        let mut i = k as i64 - 1;
        while i >= 0 && i >= k as i64 - 2 {
            let q = point(c, r, i as usize);
            if q.action == act::PLATFORM && (q.arg as usize) < np {
                let d = hz.platform(q.arg as usize);
                let ctr = hazards::platform_center(&d, c.t60(0));
                tx += ctr.x - d.center_x;
                tz += ctr.z - d.center_z;
                break;
            }
            if q.action != act::WAIT_CLEAR {
                break;
            }
            i -= 1;
        }
    }
    (tx, tz)
}

/// Race (`WaypointBot.Next` with the checkpoints reached as the section).
#[inline(never)] // keeps bot_input under the 4 KB sBPF stack frame
pub(crate) fn race(c: &Ctx, b: &mut BotState) -> Plan {
    let cp = c.w.players[c.slot].r.checkpoint as i32;
    waypoint_next(c, b, cp, false)
}

/// Survival: the section (spiral side) from the height of the last ground contact (`SurvivalSection`).
#[inline(never)] // keeps bot_input under the 4 KB sBPF stack frame
pub(crate) fn survival(c: &Ctx, b: &mut BotState) -> Plan {
    let mut section = b.section as i32;
    if c.s.has(flag::GROUNDED) || !b.has(bf::NAV_LIVE) {
        section = survival_section(c, c.s.pos.y);
    }
    waypoint_next(c, b, section, true)
}

/// The last section whose start (its routes' lowest first waypoint) is at or below y + 0.3 m.
pub fn survival_section(c: &Ctx, y: i32) -> i32 {
    let t = table(c);
    let mut k = 0;
    let mut sec = 0;
    while sec < t.sec_count as usize {
        if t.sec_start[sec] <= y + 300 {
            k = sec as i32;
        }
        sec += 1;
    }
    k
}

fn waypoint_next(c: &Ctx, b: &mut BotState, section: i32, start_at_first: bool) -> Plan {
    let s = c.s;
    let mut plan = Plan::default();
    if s.has(flag::RESPAWNING) {
        b.set(bf::WAS_RESPAWNING, true);
        return plan;
    }
    if !b.has(bf::NAV_LIVE) || b.section as i32 != section || (b.route == 255 && route_count(c) > 0) {
        b.set(bf::NAV_LIVE, true);
        enter_section(c, b, section, start_at_first);
    }
    if b.has(bf::WAS_RESPAWNING) {
        b.set(bf::WAS_RESPAWNING, false);
        reacquire(c, b);
    }
    if s.stun > 0 {
        b.set(bf::WAS_STUNNED, true);
        return plan;
    }
    if b.has(bf::WAS_STUNNED) {
        b.set(bf::WAS_STUNNED, false);
        reacquire(c, b);
    }
    let r = match cur(c, b) {
        Some(r) => r,
        None => return course_fallback(c, b),
    };
    if b.wp as usize >= r.n {
        b.wp = (r.n - 1) as u8;
    }
    let mut k = b.wp as usize;
    let mut p = point(c, &r, k);
    let (mut tx, mut tz) = target(c, &r, k);
    let grounded = s.has(flag::GROUNDED);
    let mut dx = tx as i64 - s.pos.x as i64;
    let mut dz = tz as i64 - s.pos.z as i64;
    let mut d2 = dx * dx + dz * dz;
    // Knocked down below a Run / Land point out of hop reach: go back along the route.
    if grounded && s.surface == 0 && (p.action == act::RUN || p.action == act::LAND) && p.y - s.pos.y > 1500 && d2 < 2200 * 2200 && k > 0 {
        reacquire(c, b);
        k = b.wp as usize;
        p = point(c, &r, k);
        let t = target(c, &r, k);
        tx = t.0;
        tz = t.1;
        dx = tx as i64 - s.pos.x as i64;
        dz = tz as i64 - s.pos.z as i64;
        d2 = dx * dx + dz * dz;
    }
    let mut arrived = d2 <= p.radius as i64 * p.radius as i64 && (s.pos.y - p.y).abs() < 1500;
    let next_out_of_reach = grounded && k + 1 < r.n && point(c, &r, k + 1).y - s.pos.y > 1500;
    if !arrived && !next_out_of_reach && (p.action == act::RUN || p.action == act::JUMP) && k > 0 && (s.pos.y - p.y).abs() < 1500 {
        let q = point(c, &r, k - 1);
        let (ax, az) = ((p.x - q.x) as i64, (p.z - q.z) as i64);
        let (px, pz) = ((s.pos.x - p.x) as i64, (s.pos.z - p.z) as i64);
        let al2 = ax * ax + az * az;
        if al2 > 0 && ax * px + az * pz > 0 {
            let cross = (ax * pz - az * px).abs() / isqrt_i64(al2).max(1);
            arrived = cross < p.radius as i64 * 2 + 500;
        }
    }
    if p.action == act::LAND {
        arrived = grounded && arrived;
    }
    if p.action == act::PLATFORM {
        // platforms are surfaces 0..np−1: Surface = id + 1
        arrived = grounded && s.surface as i32 == p.arg + 1 && d2 < 900 * 900;
    }
    let mut mv = true;
    let mut jump = false;
    let mut dive = false;
    if arrived {
        match p.action {
            act::JUMP => {
                if grounded {
                    jump = true;
                    advance(c, b);
                }
            }
            act::DIVE => {
                dive = true;
                advance(c, b);
            }
            act::WAIT_CLEAR => {
                if b.aux_tick == 0 {
                    b.aux_tick = lo(c.tick).max(1);
                }
                // glance on this bot's staggered tick only (WAIT_GLANCE_TICKS); otherwise keep waiting
                let glance = (c.tick + c.slot as u32) % super::WAIT_GLANCE_TICKS == 0;
                let clear = glance && if p.arg > 0 { path_clear_ahead(c, &r, k, p.arg as usize) } else { path_clear(c, &r, k) };
                if clear {
                    b.aux_tick = 0;
                    advance(c, b);
                } else if p.arg > 0 && since(c.tick, b.aux_tick) > c.t(240) {
                    b.aux_tick = 0; // no clear window for 8 s: go anyway (a player would)
                    advance(c, b);
                } else {
                    mv = false;
                    plan.waiting = true;
                }
            }
            act::DOOR => {
                if door_passable(c, p.arg, &r, k) {
                    advance(c, b);
                } else {
                    mv = false;
                    plan.waiting = true;
                }
            }
            act::JUMP_TO_PLATFORM => {
                if grounded && platform_in_reach(c, p.arg) {
                    jump = true;
                    advance(c, b);
                } else {
                    mv = false;
                    plan.waiting = true;
                }
            }
            _ => advance(c, b),
        }
        if mv && (b.wp as usize) < r.n {
            let t = target(c, &r, b.wp as usize);
            dx = t.0 as i64 - s.pos.x as i64;
            dz = t.1 as i64 - s.pos.z as i64;
        }
    }
    // Progress watchdog: no 1 m of progress toward the target for 6 s → re-acquire.
    let d16 = (isqrt_i64(d2) / 16).min(u16::MAX as i64 - 1) as u16;
    if (d16 as i64) + 62 < b.best_d as i64 {
        b.best_d = d16;
        b.progress_tick = lo(c.tick);
    } else if mv && since(c.tick, b.progress_tick) > c.t(180) {
        reacquire(c, b);
    }
    let mut cmd = InputCmd::NEUTRAL;
    if mv {
        cmd = steer_dir(&s, dx, dz, 30);
    }
    if mv && !jump && !dive && grounded && s.land == 0 && !s.has(flag::DIVING) {
        react_to_bars(c, &mut cmd, &mut jump, &mut dive);
    }
    if !jump && !dive && grounded {
        dodge_balls(c, &mut cmd, mv);
    }
    if jump {
        cmd.buttons |= JUMP_PRESSED | JUMP_HELD;
        b.jump_held_until = lo(c.tick + c.t(8) as u32);
    } else if before(c.tick, b.jump_held_until) {
        cmd.buttons |= JUMP_HELD;
    }
    if dive {
        cmd.buttons |= DIVE_PRESSED | DIVE_HELD;
    }
    // Precise: the current or next point is a jump / landing / platform / dive / wait / door.
    let mut q = b.wp as usize;
    while q <= b.wp as usize + 1 && q < r.n {
        let a = point(c, &r, q).action;
        if a != act::RUN {
            plan.precise = true;
        }
        q += 1;
    }
    plan.cmd = cmd;
    plan
}

/// No routes: steer along the course polyline (next point past the nearest), else toward the next checkpoint.
fn course_fallback(c: &Ctx, b: &mut BotState) -> Plan {
    let s = c.s;
    let n = c.map.course_len().min(256);
    let mut cmd = if n >= 2 {
        let mut best = i64::MAX;
        let mut bi = 0usize;
        let mut i = (b.target.max(0) as usize).min(n - 1);
        // only look forward from the last target (never back along the course)
        while i < n {
            let p = c.map.course_point(i);
            let (dx, dy, dz) = ((p.x - s.pos.x) as i64, (p.y - s.pos.y) as i64, (p.z - s.pos.z) as i64);
            let d = dx * dx + dz * dz + 4 * dy * dy;
            if d < best {
                best = d;
                bi = i;
            }
            i += 1;
        }
        let near = c.map.course_point(bi);
        let (ndx, ndz) = ((near.x - s.pos.x) as i64, (near.z - s.pos.z) as i64);
        if ndx * ndx + ndz * ndz < 1500 * 1500 && bi + 1 < n {
            bi += 1;
        }
        b.target = bi as i16;
        let p = c.map.course_point(bi);
        steer_to(&s, p.x, p.z, 30)
    } else {
        let cp = c.w.players[c.slot].r.checkpoint as usize;
        if cp < c.map.checkpoint_count() {
            let ck = c.map.checkpoint(cp);
            steer_to(&s, ck.center.x, ck.center.z, 30)
        } else {
            InputCmd::NEUTRAL
        }
    };
    let (mut j, mut d) = (false, false);
    if s.has(flag::GROUNDED) {
        react_to_bars(c, &mut cmd, &mut j, &mut d);
    }
    if j {
        cmd.buttons |= JUMP_PRESSED | JUMP_HELD;
    }
    if d {
        cmd.buttons |= DIVE_PRESSED | DIVE_HELD;
    }
    Plan::of(cmd)
}

// ------------------------------------------------------------------ hazard reading (pure poses: what a player sees)

/// Any knock hazard (spinner from its start time, mallet head, rolling ball within `ball_margin`) at (x, y, z)
/// `ahead` ticks from now, for a capsule of radius `r`.
fn hazard_at(c: &Ctx, x: i32, y: i32, z: i32, ahead: i64, r: i32, ball_margin: i32) -> bool {
    let hz = c.sc.hz(c.w);
    let left = c.probes.get();
    if left == 0 || !c.sc.spend(12) {
        return false; // work budget spent: not sampled = clear (see BOT_PROBES)
    }
    c.probes.set(left - 1);
    let t60 = c.t60(ahead);
    let f = crate::vec::IVec3::new(x, y, z);
    let h = c.cfg.height;
    let ss = hz.sweeper_start();
    if ss == i32::MIN || hazards::t30(t60) >= ss as i64 {
        let n = hz.count(HazardType::SpinnerBar);
        let mut i = 0;
        while i < n {
            if hazards::sweeper_contact(&hz.sweeper(i), t60 + c.sweep_warp(ahead), f, r, h).is_some() {
                return true;
            }
            i += 1;
        }
    }
    let n = hz.count(HazardType::MalletSwing);
    let mut i = 0;
    while i < n {
        if hazards::mallet_contact(&hz.mallet(i), t60, f, r, h).is_some() {
            return true;
        }
        i += 1;
    }
    ball_near(c, x, y, z, t60, ball_margin)
}

/// `BallNear`: a rolling ball within (ball r + capsule r + margin) of a standing capsule at (x, y, z) at `t60`.
fn ball_near(c: &Ctx, x: i32, y: i32, z: i32, t60: i64, margin: i32) -> bool {
    let hz = c.sc.hz(c.w);
    let n = hz.count(HazardType::RollingBall);
    let mut i = 0;
    while i < n {
        let bk = if (c.sc.balls_n as usize) == n { c.sc.balls[i] } else { hazards::BallK::of(&hz.ball(i)) };
        let reach = (bk.radius + c.cfg.radius + margin) as i64;
        let d = &bk;
        let alive = bk.alive.min(16);
        let mut j = 0;
        while j < alive {
            match bk.pos(t60, j) {
                None => break,
                Some((p, _)) => {
                    if (p.y - (y + 500)).abs() <= d.radius + 900 {
                        let (dx, dz) = ((x - p.x) as i64, (z - p.z) as i64);
                        if dx * dx + dz * dz < reach * reach {
                            return true;
                        }
                    }
                }
            }
            j += 1;
        }
        i += 1;
    }
    false
}

/// `PathClear`: the straight run to the next waypoint is free of bars, mallets and balls for its duration + margin.
fn path_clear(c: &Ctx, r: &Route, k: usize) -> bool {
    if k + 1 >= r.n {
        return true;
    }
    let n = point(c, r, k + 1);
    let s = c.s;
    let (dx, dz) = ((n.x - s.pos.x) as i64, (n.z - s.pos.z) as i64);
    let len = isqrt_i64(dx * dx + dz * dz);
    let run = (220 * 30 / c.cfg.rate as i64).max(20); // C# RunMmPerTick 220 mm at 30 Hz (≈ 6.6 m/s)
    let ticks = (len / run + c.t(12) as i64).min(120);
    let accel = c.t(3) as i64;
    let rr = c.cfg.radius + 120;
    let mut q = 0;
    while q <= ticks {
        let along = (q - accel).max(0) * run;
        let along = along.min(len);
        let (x, z) = if len > 0 { (s.pos.x + div_round(dx * along, len) as i32, s.pos.z + div_round(dz * along, len) as i32) } else { (s.pos.x, s.pos.z) };
        if hazard_at(c, x, s.pos.y, z, q, rr, 120) {
            return false;
        }
        q += 1;
    }
    true
}

/// `PathClearAhead` (simplified): sample the next `n` waypoints at run speed; each sample must be clear over a window
/// around its predicted time (0.8× − 2 + slop … + 6 − 2·slop ticks at 30 Hz).
fn path_clear_ahead(c: &Ctx, r: &Route, k: usize, n: usize) -> bool {
    let s = c.s;
    let run = c.run_per_tick();
    let rr = c.cfg.radius + (150 - 30 * c.sk.timing_slop).max(0);
    let (mut x, mut y, mut z) = (s.pos.x as i64, s.pos.y as i64, s.pos.z as i64);
    let mut kt = c.t(3) as i64;
    let mut samples = 0;
    let mut i = k + 1;
    while i <= k + n && i < r.n && samples < 64 {
        let p = point(c, r, i);
        let (dx, dy, dz) = (p.x as i64 - x, p.y as i64 - y, p.z as i64 - z);
        let len = isqrt_i64(dx * dx + dz * dz);
        let steps = ((len + run - 1) / run).clamp(1, 32);
        let mut j = 1;
        while j <= steps && samples < 64 {
            let sx = (x + dx * j / steps) as i32;
            let sy = (y + dy * j / steps) as i32;
            let sz = (z + dz * j / steps) as i32;
            let kk = kt + j;
            let lo_t = (kk * 8 / 10 - c.t(2) as i64 + c.sk.timing_slop as i64).max(0);
            let hi_t = (kk + c.t(6) as i64 - 2 * c.sk.timing_slop as i64).max(lo_t).min(lo_t + 16);
            let mut q = lo_t;
            while q <= hi_t {
                if hazard_at(c, sx, sy, sz, q, rr, 150) {
                    return false;
                }
                q += 1;
            }
            samples += 1;
            j += 1;
        }
        kt += steps + if p.action == act::RUN || p.action == act::WAIT_CLEAR { 0 } else { c.t(2) as i64 };
        x = p.x as i64;
        y = p.y as i64;
        z = p.z as i64;
        i += 1;
    }
    true
}

/// `DoorPassable`: open, and stays open long enough to reach the next point (+ 10 ticks).
fn door_passable(c: &Ctx, door: i32, r: &Route, k: usize) -> bool {
    let hz = c.sc.hz(c.w);
    if door < 0 || door as usize >= hz.count(HazardType::GateDoor) {
        return true;
    }
    let d = hz.door(door as usize);
    let t30 = hazards::t30(c.t60(0));
    if hazards::door_lift(&d, t30) < d.lift {
        return false;
    }
    let tg = point(c, r, if k + 1 < r.n { k + 1 } else { k });
    let (dx, dz) = ((tg.x - c.s.pos.x) as i64, (tg.z - c.s.pos.z) as i64);
    let need = isqrt_i64(dx * dx + dz * dz) / 220 + 10; // 30 Hz ticks (RunMmPerTick 220)
    hazards::door_ticks_until_closing(&d, t30) > need
}

/// `PlatformInReach`: where the block will be when we land (~18 ticks at 30 Hz of flight).
fn platform_in_reach(c: &Ctx, pl: i32) -> bool {
    let hz = c.sc.hz(c.w);
    if pl < 0 || pl as usize >= hz.count(HazardType::MovingPlatform) {
        return true;
    }
    let d = hz.platform(pl as usize);
    let ctr = hazards::platform_center(&d, c.w.t60(c.tick) + 36);
    let (dx, dz) = ((ctr.x - c.s.pos.x) as i64, (ctr.z - c.s.pos.z) as i64);
    let dist = isqrt_i64(dx * dx + dz * dz) - (d.half_x.min(d.half_z) as i64 * 6 / 10);
    let top = ctr.y + d.half_y;
    dist < 3600 && top - c.s.pos.y < 900 && top - c.s.pos.y > -1500
}

/// `ReactToBars` (BotBrain form): a spinner bar about to hit us in 3–6 ticks (30 Hz) → jump a low bar, dive under a
/// high one (bar bottom ≥ 650 mm above the feet).
pub(crate) fn react_to_bars(c: &Ctx, _cmd: &mut InputCmd, jump: &mut bool, dive: &mut bool) {
    let s = &c.s;
    let hz = c.sc.hz(c.w);
    let n = hz.count(HazardType::SpinnerBar);
    if n == 0 || s.immunity > 0 || !s.has(flag::GROUNDED) {
        return;
    }
    let ss = hz.sweeper_start();
    c.sc.counters.add(crate::collide::ctr::BOT_RAYS, 1);
    let mut k30 = 3;
    while k30 <= 6 {
        let t60 = c.w.t60(c.tick) + 2 * k30;
        if ss != i32::MIN && hazards::t30(t60) < ss as i64 {
            k30 += 1;
            continue;
        }
        // velocity mm/s · k / 30
        let x = s.pos.x + (s.vel.x as i64 * k30 / 30) as i32;
        let z = s.pos.z + (s.vel.z as i64 * k30 / 30) as i32;
        let f = crate::vec::IVec3::new(x, s.pos.y, z);
        let mut i = 0;
        while i < n {
            let d = hz.sweeper(i);
            if hazards::sweeper_contact(&d, t60 + c.sweep_warp(k30), f, c.cfg.radius, c.cfg.height).is_some() {
                let bottom = d.pivot_y + d.bar_height - d.bar_radius - s.pos.y;
                if bottom >= 650 {
                    if k30 >= 4 && s.dive_cooldown == 0 {
                        *dive = true;
                    }
                } else if k30 <= 5 {
                    *jump = true;
                }
                return;
            }
            i += 1;
        }
        k30 += 1;
    }
}

/// Ball reflex (`WaypointBot.DodgeBalls`): if a ball reaches our predicted position within 20 ticks (30 Hz; looser
/// tiers see it later) on the current heading, take the heading among the plan, stop and ±45°, ±90°, ±135°, back whose
/// first contact is latest (plan preferred, then forward-leaning headings), odd slots trying the right side first.
/// Lanes farther than 16 m are ignored. Integer port: headings from the Q15 table, positions in mm per 30 Hz tick.
fn dodge_balls(c: &Ctx, cmd: &mut InputCmd, moving: bool) {
    use crate::fx::{cos_q15, deg_to_turn_q16, normalize_q15, sin_q15, ONE_Q15};
    let hz = c.sc.hz(c.w);
    let n = hz.count(HazardType::RollingBall);
    if n == 0 {
        return;
    }
    let s = c.s;
    if !s.has(flag::GROUNDED) {
        return;
    }
    // lanes in range (each near ball's kinematics computed once: P2 0.2)
    let mut near = [hazards::BallK::default(); 16];
    let mut lane_d = [0i64; 16];
    let mut nn = 0usize;
    let mut i = 0;
    while i < n && nn < near.len() {
        let d = hz.ball(i);
        let (ux, uz) = ((d.end_x - d.start_x) as i64, (d.end_z - d.start_z) as i64);
        let l2 = ux * ux + uz * uz;
        let (px, pz) = ((s.pos.x - d.start_x) as i64, (s.pos.z - d.start_z) as i64);
        let f = if l2 > 0 { ((px * ux + pz * uz) as i128 * 4096 / l2 as i128).clamp(0, 4096) as i64 } else { 0 };
        let (cx, cz) = (d.start_x as i64 + ux * f / 4096 - s.pos.x as i64, d.start_z as i64 + uz * f / 4096 - s.pos.z as i64);
        let vertical_ok = (d.start_y - s.pos.y).abs() <= 9000 || (d.end_y - s.pos.y).abs() <= 9000;
        if cx * cx + cz * cz <= 16_000i64 * 16_000 && vertical_ok {
            near[nn] = if (c.sc.balls_n as usize) == n { c.sc.balls[i] } else { hazards::BallK::of(&d) };
            lane_d[nn] = isqrt_i64(cx * cx + cz * cz);
            nn += 1;
        }
        i += 1;
    }
    if nn == 0 {
        return;
    }
    c.sc.counters.add(crate::collide::ctr::BOT_RAYS, 1);
    let horizon = (20 - 2 * c.sk.timing_slop).max(6) as i64;
    let run = (c.cfg.max_speed as i64 / 30).max(60); // RunPerTick (mm per 30 Hz tick)
    let t0 = c.w.t60(c.tick);
    // First tick a ball reaches us on each of up to 9 Q15 headings (hx, hz, stop = brake), all simulated together so
    // the ball positions of a tick are computed once (`WaypointBot.FirstHit` per heading; same values).
    let first_hits = |hd: &[(i64, i64, bool)], out: &mut [i64]| {
        let m = hd.len();
        let (vx, vz) = (s.vel.x as i64 / 30, s.vel.z as i64 / 30);
        let vmax = vx.abs().max(vz.abs());
        let mut x = [s.pos.x as i64; 9];
        let mut z = [s.pos.z as i64; 9];
        let mut open = m;
        let mut h = 0;
        while h < m {
            out[h] = horizon + 1;
            h += 1;
        }
        let mut k = 1;
        while k <= horizon && open > 0 {
            let a = k.min(5);
            let mut h = 0;
            while h < m {
                let top = if hd[h].2 { 0 } else { run };
                x[h] += vx + (hd[h].0 * top / ONE_Q15 as i64 - vx) * a / 5;
                z[h] += vz + (hd[h].1 * top / ONE_Q15 as i64 - vz) * a / 5;
                h += 1;
            }
            let t60 = t0 + 2 * k;
            let mut q = 0;
            while q < nn && open > 0 {
                let d = &near[q];
                // ≈ 100 CU per ball position (up to 16) checked against up to 9 headings
                if !c.sc.spend(2 + d.alive.min(16) as u32 * 2) {
                    break; // work budget spent: the rest of this tick's ball look-ahead is skipped
                }
                // Exact reject (P2 0.2): balls stay on their lane, and each tick a heading moves us by
                // v + (h·top − v)·a/5, at most 2|v|/30 + run per axis; so a lane farther than k times that (×2 ≥ √2,
                // + reach + the lane-point rounding) is never within reach at tick k.
                let reach_q = (d.radius + c.cfg.radius + 150) as i64;
                if lane_d[q] > 2 * k * (2 * vmax + run) + reach_q + 64 {
                    q += 1;
                    continue;
                }
                let alive = d.alive.min(16);
                let reach = (d.radius + c.cfg.radius + 150) as i64;
                let mut j = 0;
                while j < alive {
                    match d.pos(t60, j) {
                        None => break,
                        Some((p, _)) => {
                            if (p.y - (s.pos.y + 500)).abs() <= d.radius + 1200 {
                                let mut h = 0;
                                while h < m {
                                    if out[h] > horizon {
                                        let (dx, dz) = (x[h] - p.x as i64, z[h] - p.z as i64);
                                        if dx * dx + dz * dz < reach * reach {
                                            out[h] = k;
                                            open -= 1;
                                        }
                                    }
                                    h += 1;
                                }
                            }
                        }
                    }
                    j += 1;
                }
                q += 1;
            }
            k += 1;
        }
    };
    let (mx, mz) = if moving { (cmd.move_x as i64, cmd.move_y as i64) } else { (0, 0) };
    let has_plan = mx * mx + mz * mz > 1;
    let (ux, uz) = if has_plan {
        let u = normalize_q15(mx, 0, mz);
        (u[0] as i64, u[2] as i64)
    } else {
        (0, ONE_Q15 as i64)
    };
    let mut one = [0i64; 1];
    first_hits(&[if has_plan { (ux, uz, false) } else { (0, 0, true) }], &mut one);
    let plan_hit = one[0];
    if plan_hit > horizon {
        return;
    }
    // The 7 turned headings (+ the stop when there is a plan) together.
    let flip: i64 = if c.slot & 1 == 1 { -1 } else { 1 };
    let mut hd = [(0i64, 0i64, false); 9];
    let mut cas = [0i64; 8];
    let mut m = 0;
    for deg in [45i64, -45, 90, -90, 135, -135, 180] {
        let t = deg_to_turn_q16(flip * deg);
        let (ca, sa) = (cos_q15(t) as i64, sin_q15(t) as i64);
        hd[m] = ((ux * ca - uz * sa) / ONE_Q15 as i64, (ux * sa + uz * ca) / ONE_Q15 as i64, false);
        cas[m] = ca;
        m += 1;
    }
    if has_plan {
        hd[m] = (0, 0, true);
        m += 1;
    }
    let mut hits = [0i64; 9];
    first_hits(&hd[..m], &mut hits[..m]);
    let mut best = plan_hit * 100 + if has_plan { 40 } else { 0 };
    let (mut bx, mut bz) = if has_plan { (ux, uz) } else { (0, 0) };
    let stop_hit = if has_plan { hits[7] } else { plan_hit };
    if stop_hit * 100 + 10 > best {
        best = stop_hit * 100 + 10;
        bx = 0;
        bz = 0;
    }
    let mut h = 0;
    while h < 7 {
        let score = hits[h] * 100 + cas[h] * 30 / ONE_Q15 as i64;
        if score > best {
            best = score;
            bx = hd[h].0;
            bz = hd[h].1;
        }
        h += 1;
    }
    let ox = div_round(bx * 127, ONE_Q15 as i64);
    let oz = div_round(bz * 127, ONE_Q15 as i64);
    cmd.move_x = ox.clamp(-127, 127) as i8;
    cmd.move_y = oz.clamp(-127, 127) as i8;
}
