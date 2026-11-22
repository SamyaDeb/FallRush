//! The kinematic capsule motor: an integer port of `Shared/KinematicMotor.cs` (`Step`, `Displace`, `Depenetrate`,
//! `SlideMove`, `TryStepUp`, `IsWalkableContact`, `MoveTowards`) in the **same order**:
//! depenetrate → surface carry → input, timers, stun / dive-landing → facing → jump (buffer + coyote) → dive →
//! inherit carry → horizontal accel / decel / air control → gravity (exact per-step integration) → collide-and-slide
//! with step-up → ground probe + snap → landing transitions (belly slide, stumble) → state.
//!
//! **Intentional differences** (listed in the proof §8):
//! - integers: positions mm, velocity **mm/s** (C#: cm/s), normals Q15, `dt = 1/rate` applied by division;
//! - the C# float thresholds become their mm / Q15 equivalents: `1e-10 m²` → zero vector, `1e-4 m` → 1 mm,
//!   `0.01 m` → 10 mm, `0.001 m` → 1 mm, normal `y > −0.01` → `y > −328`, `y ≤ 0.01` → `≤ 328`;
//! - the end-of-step quantisation is implicit (everything is already integer).

use crate::collide::{ctr, Hit, Scene};
use crate::config::MotorConfig;
use crate::fx::{atan2_yaw256, div_round, isqrt_i64, isqrt_round, normalize_q15, yaw_dir_q15, yaw_from_cam, ONE_Q15};
use crate::input::{InputCmd, DIVE_PRESSED, JUMP_PRESSED};
use crate::vec::IVec3;

pub mod flag {
    pub const GROUNDED: u8 = 1 << 0;
    pub const DIVING: u8 = 1 << 1;
    pub const DIVE_LANDING: u8 = 1 << 2;
    pub const RESPAWNING: u8 = 1 << 3;
    pub const STUMBLING: u8 = 1 << 4;
    pub const BOT: u8 = 1 << 7;
}

/// Per-player motor state (`MotorState`). Feet position (mm) and velocity (mm/s).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct MotorState {
    pub pos: IVec3,
    pub vel: IVec3,
    pub yaw: u8,
    pub flags: u8,
    pub stun: u8,
    pub coyote: u8,
    pub jump_buffer: u8,
    pub dive_cooldown: u8,
    pub land: u8,
    pub immunity: u8,
    /// Moving surface stood on at the end of the last step: 0 none, else surface id + 1.
    pub surface: u8,
}

impl MotorState {
    #[inline]
    pub const fn has(&self, f: u8) -> bool {
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

    pub fn at(pos: IVec3, yaw: u8) -> MotorState {
        MotorState { pos, yaw, ..Default::default() }
    }
}

/// What happened in one step (diagnostics / visuals; not state).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct StepEvents {
    pub jumped: bool,
    pub dived: bool,
    pub landed: bool,
    pub stepped_up: bool,
    pub jump_cancelled: bool,
    pub carried: bool,
    pub inherited: bool,
    pub stumbled: bool,
}

const EDGE_PROBE_INSET: i64 = 20;
const EDGE_PROBE_RISE: i64 = 50;
/// −0.01 and 0.01 in Q15.
const NY_STEP_MIN: i32 = -328;
const NY_EDGE_MIN: i32 = 328;

/// `KinematicMotor.Step`.
pub fn step(c: &MotorConfig, s: &mut MotorState, cmd: &InputCmd, sc: &Scene) -> StepEvents {
    let mut ev = StepEvents::default();
    if s.has(flag::RESPAWNING) {
        return ev;
    }
    let rate = c.rate as i64;
    let mut pos = s.pos;
    let mut vel = s.vel;
    let was_grounded = s.has(flag::GROUNDED);

    depenetrate(c, &mut pos, sc);

    // ---- surface carry ----
    let mut carry = IVec3::ZERO;
    if was_grounded && s.surface != 0 {
        if let Some(d) = sc.surface_delta(s.surface as i32 - 1, pos) {
            if !d.is_zero() {
                let mut v0 = vel;
                slide_move(c, &mut pos, &mut v0, d, true, false, sc, &mut ev);
                ev.carried = true;
                carry = d;
            }
        }
    }

    // ---- input (Q15 wish direction, length ≤ 1) ----
    let (mx, my) = (cmd.move_x as i64, cmd.move_y as i64);
    let (mut wx, mut wz) = if mx * mx + my * my > 127 * 127 {
        let n = normalize_q15(mx, 0, my);
        (n[0] as i64, n[2] as i64)
    } else {
        (div_round(mx * ONE_Q15 as i64, 127), div_round(my * ONE_Q15 as i64, 127))
    };
    let mut has_move = mx != 0 || my != 0;
    let mut jump_press = cmd.has(JUMP_PRESSED);
    let mut dive_press = cmd.has(DIVE_PRESSED);

    if s.dive_cooldown > 0 {
        s.dive_cooldown -= 1;
    }
    if s.immunity > 0 {
        s.immunity -= 1;
    }
    let stunned = s.stun > 0;
    if stunned {
        s.stun -= 1;
        wx = 0;
        wz = 0;
        has_move = false;
        jump_press = false;
        dive_press = false;
    }

    let mut cancel_jump = false;
    let mut landing = s.land > 0 && s.has(flag::DIVE_LANDING);
    let stumbling = s.land > 0 && s.has(flag::STUMBLING);
    if landing && !was_grounded {
        end_landing(s);
        landing = false;
    }
    if landing {
        let since = c.dive_land_stun_ticks - s.land as i32;
        if jump_press && since < c.dive_cancel_window_ticks {
            end_landing(s);
            cancel_jump = true;
            landing = false;
        } else {
            s.land -= 1;
            if s.land == 0 {
                s.set(flag::DIVE_LANDING, false);
            }
            wx = 0;
            wz = 0;
            has_move = false;
            jump_press = false;
            dive_press = false;
        }
    } else if stumbling {
        s.land -= 1;
        if s.land == 0 {
            s.set(flag::STUMBLING, false);
        }
    } else if s.land > 0 || s.has(flag::STUMBLING) {
        end_landing(s);
    }

    let mut diving = s.has(flag::DIVING);
    if has_move && !diving {
        s.yaw = atan2_yaw256(cmd.move_x as i32, cmd.move_y as i32, s.yaw);
    }

    // ---- jump ----
    if jump_press {
        s.jump_buffer = c.jump_buffer_ticks.clamp(0, 255) as u8;
    }
    let mut jumped = false;
    let can_ground_jump = was_grounded || s.coyote > 0;
    if cancel_jump || (!diving && !landing && !stunned && s.jump_buffer > 0 && can_ground_jump) {
        vel.y = c.jump_speed;
        if s.has(flag::STUMBLING) {
            end_landing(s);
        }
        s.jump_buffer = 0;
        s.coyote = 0;
        jumped = true;
        ev.jumped = true;
        ev.jump_cancelled = cancel_jump;
    }

    // ---- dive ----
    let mut dived = false;
    if !jumped && dive_press && !diving && !landing && !stunned && s.dive_cooldown == 0 {
        if !has_move {
            s.yaw = yaw_from_cam(cmd.cam_yaw);
        }
        let d = yaw_dir_q15(s.yaw);
        let along = div_round(vel.x as i64 * d[0] as i64 + vel.z as i64 * d[1] as i64, ONE_Q15 as i64);
        let hs = along.max(c.dive_horizontal as i64);
        vel.x = div_round(d[0] as i64 * hs, ONE_Q15 as i64) as i32;
        vel.z = div_round(d[1] as i64 * hs, ONE_Q15 as i64) as i32;
        vel.y = c.dive_vertical;
        s.set(flag::DIVING, true);
        s.dive_cooldown = c.dive_cooldown_ticks.clamp(0, 255) as u8;
        s.jump_buffer = 0;
        s.coyote = 0;
        diving = true;
        dived = true;
        ev.dived = true;
        if s.has(flag::STUMBLING) {
            end_landing(s);
        }
    }

    // Jump-off inheritance (M3): carry per tick → mm/s.
    if (jumped || dived) && ev.carried {
        vel.x += carry.x * rate as i32;
        vel.z += carry.z * rate as i32;
        ev.inherited = true;
    }

    let grounded = was_grounded && !jumped && !dived;

    // ---- horizontal velocity ----
    let (mut vx, mut vz) = (vel.x as i64, vel.z as i64);
    if diving {
        // committed
    } else if grounded {
        if landing {
            move_towards(&mut vx, &mut vz, 0, 0, div_round(c.slide_decel as i64, rate));
        } else if wx == 0 && wz == 0 {
            move_towards(&mut vx, &mut vz, 0, 0, div_round(c.ground_decel as i64, rate));
        } else {
            let top = if stumbling { div_round(c.max_speed as i64 * c.stumble_speed_permille as i64, 1000) } else { c.max_speed as i64 };
            let tx = div_round(wx * top, ONE_Q15 as i64);
            let tz = div_round(wz * top, ONE_Q15 as i64);
            move_towards(&mut vx, &mut vz, tx, tz, div_round(c.ground_accel as i64, rate));
        }
    } else if wx != 0 || wz != 0 {
        let mut target = c.max_speed as i64;
        let cur2 = vx * vx + vz * vz;
        if cur2 > target * target {
            target = isqrt_round(cur2);
        }
        let tx = div_round(wx * target, ONE_Q15 as i64);
        let tz = div_round(wz * target, ONE_Q15 as i64);
        move_towards(&mut vx, &mut vz, tx, tz, div_round(c.air_accel as i64, rate));
    }

    // ---- vertical: exact integration under constant gravity for the step ----
    let dy;
    if grounded {
        vel.y = 0;
        dy = 0;
    } else {
        let g = if vel.y <= 0 { div_round(c.gravity as i64 * c.fall_multiplier_permille as i64, 1000) } else { c.gravity as i64 };
        // dy = vy·dt − ½·g·dt² = (2·rate·vy − g) / (2·rate²)
        dy = div_round(2 * rate * vel.y as i64 - g, 2 * rate * rate);
        let mut vy = vel.y as i64 - div_round(g, rate);
        if vy < -(c.max_fall_speed as i64) {
            vy = -(c.max_fall_speed as i64);
        }
        vel.y = vy as i32;
    }
    vel.x = vx as i32;
    vel.z = vz as i32;
    let fall_speed = -(vel.y as i64);

    // ---- move ----
    let disp = IVec3::new(div_round(vx, rate) as i32, dy as i32, div_round(vz, rate) as i32);
    slide_move(c, &mut pos, &mut vel, disp, grounded, grounded, sc, &mut ev);

    // ---- ground probe + snap ----
    let mut grounded_now = false;
    let mut surface = 0u8;
    if !jumped && !dived && vel.y <= 0 {
        let probe = if was_grounded { c.snap_distance } else { 0 } as i64 + c.skin as i64 * 2;
        let max_rise = if was_grounded { c.step_offset } else { c.ledge_tolerance } as i64;
        if let Some(g) = sc.capsule_sweep(pos, c.radius, c.height, IVec3::DOWN, probe + c.skin as i64) {
            if let Some(face_y) = walkable(c, &g, pos, sc) {
                let drop = g.distance - c.skin as i64;
                if face_y as i64 - (pos.y as i64 - drop) <= max_rise {
                    pos.y -= drop as i32;
                    grounded_now = true;
                    vel.y = 0;
                    surface = if g.surface >= 0 && g.surface < 255 { (g.surface + 1) as u8 } else { 0 };
                }
            }
        }
    }

    // ---- transitions ----
    if grounded_now && !was_grounded {
        ev.landed = true;
        if s.has(flag::DIVING) {
            s.set(flag::DIVING, false);
            s.set(flag::STUMBLING, false);
            s.set(flag::DIVE_LANDING, true);
            s.land = c.dive_land_stun_ticks.clamp(0, 255) as u8;
            s.jump_buffer = 0;
        } else if fall_speed > c.stumble_impact_speed as i64 && c.stumble_ticks > 0 {
            s.set(flag::STUMBLING, true);
            s.land = c.stumble_ticks.clamp(0, 255) as u8;
            ev.stumbled = true;
        }
    }
    if grounded_now {
        s.coyote = c.coyote_ticks.clamp(0, 255) as u8;
    } else if !was_grounded && s.coyote > 0 {
        s.coyote -= 1;
    }
    if !jump_press && s.jump_buffer > 0 {
        s.jump_buffer -= 1;
    }
    s.set(flag::GROUNDED, grounded_now);
    s.surface = surface;
    s.pos = pos;
    s.vel = vel;
    ev
}

fn end_landing(s: &mut MotorState) {
    s.land = 0;
    s.set(flag::DIVE_LANDING, false);
    s.set(flag::STUMBLING, false);
}

/// `KinematicMotor.Displace`: external displacement (player separation). Collides and slides, never steps up,
/// keeps velocity.
pub fn displace(c: &MotorConfig, s: &mut MotorState, d: IVec3, sc: &Scene) {
    if s.has(flag::RESPAWNING) {
        return;
    }
    let mut pos = s.pos;
    let mut vel = s.vel;
    let mut ev = StepEvents::default();
    slide_move(c, &mut pos, &mut vel, d, true, false, sc, &mut ev);
    s.pos = pos;
}

fn depenetrate(c: &MotorConfig, pos: &mut IVec3, sc: &Scene) {
    let mut i = 0;
    while i < 2 {
        match sc.penetration(*pos, c.radius, c.height) {
            Some(corr) => *pos = pos.add(corr),
            None => return,
        }
        i += 1;
    }
}

#[allow(clippy::too_many_arguments)]
fn slide_move(c: &MotorConfig, pos: &mut IVec3, vel: &mut IVec3, disp0: IVec3, grounded_mode: bool, allow_step: bool, sc: &Scene, ev: &mut StepEvents) {
    let mut disp = disp0;
    let mut stepped = false;
    let mut i = 0;
    while i < c.slide_iterations {
        sc.counters.add(ctr::SLIDES, 1);
        if disp.is_zero() {
            return;
        }
        let len = disp.len().max(1);
        let dir = disp.unit_q15();
        let hit = match sc.capsule_sweep(*pos, c.radius, c.height, dir, len + c.skin as i64) {
            None => {
                *pos = pos.add(disp);
                return;
            }
            Some(h) => h,
        };
        let travel = (hit.distance - c.skin as i64).clamp(0, len);
        let moved = if travel == len { disp } else { dir.scale_q15(travel) };
        *pos = pos.add(moved);
        let mut rem = disp.sub(moved);
        let mut n = hit.normal;
        if n.y < c.max_slope_cos {
            if allow_step && !stepped && n.y > NY_STEP_MIN && try_step_up(c, pos, rem, sc) {
                stepped = true;
                ev.stepped_up = true;
                let _ = stepped;
                return;
            }
            if n.y > 0 && (grounded_mode || disp.y >= 0) {
                let h = IVec3::new(n.x, 0, n.z).unit_q15();
                if !h.is_zero() {
                    n = h;
                }
            }
        }
        rem = rem.reject_q15(n);
        let vn = vel.dot_q15(n);
        if vn < 0 && !(grounded_mode && n.y >= c.max_slope_cos) {
            *vel = vel.sub(n.scale_q15(vn));
        }
        disp = rem;
        i += 1;
    }
}

/// `TryStepUp`: up by the step offset, forward by the blocked remainder, down onto walkable ground ≤ step offset.
fn try_step_up(c: &MotorConfig, pos: &mut IVec3, rem: IVec3, sc: &Scene) -> bool {
    let fwd = rem.horizontal();
    let fl = fwd.len();
    if fl < 1 {
        return false;
    }
    let fdir = fwd.unit_q15();
    let skin = c.skin as i64;
    let mut up = c.step_offset as i64;
    if let Some(h1) = sc.capsule_sweep(*pos, c.radius, c.height, IVec3::UP, up + skin) {
        up = h1.distance - skin;
    }
    if up < 10 {
        return false;
    }
    let mut p = pos.add(IVec3::new(0, up as i32, 0));
    if let Some(h2) = sc.capsule_sweep(p, c.radius, c.height, fdir, fl + skin) {
        let t = h2.distance - skin;
        if t < 1 {
            return false;
        }
        p = p.add(fdir.scale_q15(t));
    } else {
        p = p.add(fwd);
    }
    let h3 = match sc.capsule_sweep(p, c.radius, c.height, IVec3::DOWN, up + skin) {
        Some(h) => h,
        None => return false,
    };
    let face_y = match walkable(c, &h3, p, sc) {
        Some(y) => y,
        None => return false,
    };
    let foot_before = pos.y as i64 - skin;
    if face_y as i64 - foot_before > c.step_offset as i64 + 1 {
        return false;
    }
    p.y -= (h3.distance - skin) as i32;
    if p.y as i64 - (pos.y as i64) < 1 {
        return false;
    }
    *pos = p;
    true
}

/// `IsWalkableContact`: walkable if the normal is within the slope limit or, for an edge contact, the face just past
/// the edge is. Returns that face's height.
pub fn walkable(c: &MotorConfig, hit: &Hit, feet: IVec3, sc: &Scene) -> Option<i32> {
    if hit.normal.y >= c.max_slope_cos {
        return Some(hit.point.y);
    }
    if hit.normal.y <= NY_EDGE_MIN {
        return None;
    }
    let away = IVec3::new(hit.point.x - feet.x, 0, hit.point.z - feet.z).unit_q15();
    let origin = hit.point.add(away.scale_q15(EDGE_PROBE_INSET)).add(IVec3::new(0, EDGE_PROBE_RISE as i32, 0));
    if let Some(rh) = sc.raycast(origin, IVec3::DOWN, EDGE_PROBE_RISE * 2) {
        if rh.normal.y >= c.max_slope_cos {
            return Some(rh.point.y);
        }
    }
    None
}

/// `MoveTowards` on (x, z) with integer step `max_delta`.
fn move_towards(x: &mut i64, z: &mut i64, tx: i64, tz: i64, max_delta: i64) {
    let dx = tx - *x;
    let dz = tz - *z;
    let d2 = dx * dx + dz * dz;
    if d2 <= max_delta * max_delta {
        *x = tx;
        *z = tz;
        return;
    }
    let d = isqrt_i64(d2).max(1);
    *x += div_round(dx * max_delta, d);
    *z += div_round(dz * max_delta, d);
}
