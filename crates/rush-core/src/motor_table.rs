//! Motor analytic table (P1 prompt D.3) at 15 / 20 / 30 Hz on the synthetic motor course. `rush-sim movement table`
//! prints the same measurements for the proof; these tests hold the pass bars.

use crate::collide::Scene;
use crate::config::MotorConfig;
use crate::input::{InputCmd, DIVE_HELD, DIVE_PRESSED, JUMP_HELD, JUMP_PRESSED};
use crate::mapblob::builder::build;
use crate::mapblob::testmaps::*;
use crate::motor::{flag, step, MotorState};
use crate::vec::IVec3;
extern crate alloc;
use alloc::vec::Vec;

pub const RATES: [u32; 3] = [15, 20, 30];

fn fwd() -> InputCmd {
    InputCmd { move_x: 0, move_y: 127, buttons: 0, cam_yaw: 0 }
}

fn jump(mut c: InputCmd) -> InputCmd {
    c.buttons |= JUMP_PRESSED | JUMP_HELD;
    c
}

fn dive(mut c: InputCmd) -> InputCmd {
    c.buttons |= DIVE_PRESSED | DIVE_HELD;
    c
}

fn settle(c: &MotorConfig, s: &mut MotorState, sc: &Scene) {
    for _ in 0..c.rate {
        step(c, s, &InputCmd::NEUTRAL, sc);
    }
}

/// Jump apex (mm above the start) on flat ground.
pub fn apex(c: &MotorConfig, sc: &Scene) -> i32 {
    let mut s = MotorState::at(IVec3::new(0, 10, 0), 0);
    settle(c, &mut s, sc);
    let y0 = s.pos.y;
    step(c, &mut s, &jump(InputCmd::NEUTRAL), sc);
    let mut top = s.pos.y;
    for _ in 0..c.rate * 2 {
        step(c, &mut s, &InputCmd::NEUTRAL, sc);
        top = top.max(s.pos.y);
    }
    top - y0
}

/// Ticks from rest until the speed reaches 99 % of max, and that time in ms.
pub fn time_to_max(c: &MotorConfig, sc: &Scene) -> (u32, i64) {
    let mut s = MotorState::at(IVec3::new(0, 10, 0), 0);
    settle(c, &mut s, sc);
    for k in 1..200u32 {
        step(c, &mut s, &fwd(), sc);
        if s.vel.z as i64 * 100 >= c.max_speed as i64 * 99 {
            return (k, k as i64 * 1000 / c.rate as i64);
        }
    }
    (0, 0)
}

/// Dive from a full-speed run: horizontal distance from the dive tick to the landing, and the belly-slide distance
/// after it until the slide ends.
pub fn dive_distance(c: &MotorConfig, sc: &Scene) -> (i32, i32, u32) {
    let mut s = MotorState::at(IVec3::new(0, 10, -20_000), 0);
    settle(c, &mut s, sc);
    for _ in 0..c.rate {
        step(c, &mut s, &fwd(), sc);
    }
    let z0 = s.pos.z;
    step(c, &mut s, &dive(fwd()), sc);
    let mut z_land = s.pos.z;
    let mut slide_ticks = 0u32;
    for _ in 0..c.rate * 2 {
        let ev = step(c, &mut s, &fwd(), sc);
        if ev.landed {
            z_land = s.pos.z;
        }
        if s.has(flag::DIVE_LANDING) {
            slide_ticks += 1;
        }
        if !s.has(flag::DIVING) && !s.has(flag::DIVE_LANDING) && z_land != z0 {
            break;
        }
    }
    (z_land - z0, s.pos.z - z_land, slide_ticks)
}

/// Walk up lane `lane` of the motor course for `secs`: the most height gained while grounded.
pub fn climb(c: &MotorConfig, sc: &Scene, lane: i32, secs: u32) -> i32 {
    let mut s = MotorState::at(IVec3::new(course_lane(lane), 10, 2000), 0);
    settle(c, &mut s, sc);
    let y0 = s.pos.y;
    let mut top = y0;
    for _ in 0..c.rate * secs {
        step(c, &mut s, &fwd(), sc);
        if s.has(flag::GROUNDED) {
            top = top.max(s.pos.y);
        }
    }
    top - y0
}

/// Jump onto the block of lane `lane` (run up, jump at `jump_z`): grounded on top at the end?
pub fn jump_onto(c: &MotorConfig, sc: &Scene, lane: i32, top: i32) -> bool {
    let mut s = MotorState::at(IVec3::new(course_lane(lane), 10, 2000), 0);
    settle(c, &mut s, sc);
    let mut jumped = false;
    let mut on_top = false;
    for _ in 0..c.rate * 3 {
        let near = s.pos.z >= 9000 - 2000 - 310 - 1800;
        let cmd = if near && !jumped {
            jumped = true;
            jump(fwd())
        } else {
            fwd()
        };
        step(c, &mut s, &cmd, sc);
        on_top |= s.has(flag::GROUNDED) && s.pos.y >= top - 5;
    }
    on_top
}

/// Walk off the 3 m ledge (lane 12, edge at z = 10 m) and press jump `k` ticks after leaving the ground: jumped?
pub fn coyote_jump(c: &MotorConfig, sc: &Scene, k: u32) -> bool {
    let mut s = MotorState::at(IVec3::new(course_lane(12), 3010, 8000), 0);
    settle(c, &mut s, sc);
    let mut off = None;
    for t in 0..c.rate * 3 {
        let mut cmd = fwd();
        if let Some(t0) = off {
            if t == t0 + k {
                cmd = jump(cmd);
            }
        }
        let ev = step(c, &mut s, &cmd, sc);
        if ev.jumped {
            return true;
        }
        if off.is_none() && !s.has(flag::GROUNDED) {
            off = Some(t + 1);
            if k == 0 {
                // press on the very next tick
            }
        }
    }
    false
}

/// Fall from the 3.5 m tower (lane 13) and press jump `k` ticks before touching down: did it jump on landing?
pub fn buffer_jump(c: &MotorConfig, sc: &Scene, k: u32) -> bool {
    // First find the landing tick without pressing.
    let mut s = MotorState::at(IVec3::new(course_lane(13), 6000, 0), 0);
    let mut land_tick = None;
    for t in 0..c.rate * 3 {
        let ev = step(c, &mut s, &InputCmd::NEUTRAL, sc);
        if ev.landed {
            land_tick = Some(t);
            break;
        }
    }
    let lt = match land_tick {
        Some(t) => t,
        None => return false,
    };
    if k > lt {
        return false;
    }
    let mut s = MotorState::at(IVec3::new(course_lane(13), 6000, 0), 0);
    for t in 0..=lt + 2 {
        let cmd = if t == lt - k { jump(InputCmd::NEUTRAL) } else { InputCmd::NEUTRAL };
        let ev = step(c, &mut s, &cmd, sc);
        if ev.jumped {
            return t >= lt;
        }
    }
    false
}

/// Drop from `height` onto flat ground: stumbled on landing?
pub fn stumble_from(c: &MotorConfig, sc: &Scene, height: i32) -> bool {
    let mut s = MotorState::at(IVec3::new(0, height, 0), 0);
    for _ in 0..c.rate * 3 {
        let ev = step(c, &mut s, &InputCmd::NEUTRAL, sc);
        if ev.landed {
            return ev.stumbled;
        }
    }
    false
}

/// Run into the wall of lane 14 diagonally: slides along it (keeps moving in x) without entering it.
pub fn wall_slide(c: &MotorConfig, sc: &Scene) -> (i32, i32) {
    let mut s = MotorState::at(IVec3::new(course_lane(14), 10, 7000), 0);
    settle(c, &mut s, sc);
    let x0 = s.pos.x;
    // 0.8 s: stays within the 6 m wall
    for _ in 0..c.rate * 8 / 10 {
        step(c, &mut s, &InputCmd { move_x: 60, move_y: 110, buttons: 0, cam_yaw: 0 }, sc);
    }
    (s.pos.x - x0, s.pos.z)
}

pub fn scenes() -> (Vec<u8>, Vec<u8>) {
    (build(&flat_arena_spec()), build(&motor_course_spec()))
}

#[cfg(test)]
#[test]
fn motor_table_all_rates() {
    let (fb, cb) = scenes();
    let flat = Scene::new(crate::mapblob::MapView::new(&fb).unwrap());
    let course = Scene::new(crate::mapblob::MapView::new(&cb).unwrap());
    for rate in RATES {
        let c = MotorConfig::default_at(rate);
        let a = apex(&c, &flat);
        assert!((a - 1200).abs() <= 24, "{rate} Hz apex {a}");
        let (tk, ms) = time_to_max(&c, &flat);
        assert!(tk > 0 && (120..=260).contains(&ms), "{rate} Hz time to max {ms} ms");
        let (dd, slide, _) = dive_distance(&c, &flat);
        assert!((1300..=2300).contains(&dd), "{rate} Hz dive {dd} mm (slide {slide})");
        // slopes: 44°, 45° walkable (gain the 2 m rise), 46°, 50° not
        for (lane, deg, ok) in [(0, 15, true), (1, 30, true), (2, 44, true), (3, 45, true), (4, 46, false), (5, 50, false)] {
            let g = climb(&c, &course, lane, 3);
            if ok {
                assert!(g >= 1900, "{rate} Hz {deg}° should climb, gained {g}");
            } else {
                assert!(g <= 300, "{rate} Hz {deg}° should block, gained {g}");
            }
        }
        // steps
        for (lane, h, ok) in [(6, 200, true), (7, 250, true), (8, 260, false), (9, 300, false)] {
            let g = climb(&c, &course, lane, 3);
            if ok {
                assert!((g - h).abs() <= 3, "{rate} Hz step {h} should climb, gained {g}");
            } else {
                assert!(g <= 5, "{rate} Hz step {h} should block, gained {g}");
            }
        }
        assert!(jump_onto(&c, &course, 10, 1150), "{rate} Hz jump onto 1.15 m");
        assert!(!jump_onto(&c, &course, 11, 1650), "{rate} Hz not onto 1.65 m");
        // coyote: a press within the window jumps; one well after does not
        assert!(coyote_jump(&c, &course, 0), "{rate} Hz coyote k=0");
        assert!(coyote_jump(&c, &course, (c.coyote_ticks as u32).saturating_sub(1)), "{rate} Hz coyote at the window edge");
        assert!(!coyote_jump(&c, &course, c.coyote_ticks as u32 + 2), "{rate} Hz coyote past the window");
        // buffer
        assert!(buffer_jump(&c, &course, 1), "{rate} Hz buffer 1 tick");
        assert!(!buffer_jump(&c, &course, c.jump_buffer_ticks as u32 + 2), "{rate} Hz buffer past the window");
        // stumble
        assert!(stumble_from(&c, &flat, 3500), "{rate} Hz stumble from 3.5 m");
        assert!(!stumble_from(&c, &flat, 2500), "{rate} Hz no stumble from 2.5 m");
        // wall slide: keeps moving along x, stays out of the wall (face at z = 10 m − r)
        let (dx, z) = wall_slide(&c, &course);
        assert!(dx > 1000 && z <= 10_000 - 310, "{rate} Hz wall slide dx {dx} z {z}");
    }
}

#[cfg(test)]
#[test]
#[ignore]
fn debug_climb() {
    let (_, cb) = scenes();
    let course = Scene::new(crate::mapblob::MapView::new(&cb).unwrap());
    let lane: i32 = std::env::var("LANE").ok().and_then(|v| v.parse().ok()).unwrap_or(1);
    let rate: u32 = std::env::var("RATE").ok().and_then(|v| v.parse().ok()).unwrap_or(15);
    let c = MotorConfig::default_at(rate);
    let mut s = MotorState::at(IVec3::new(course_lane(lane), 10, 2000), 0);
    settle(&c, &mut s, &course);
    for t in 0..c.rate * 3 {
        let ev = step(&c, &mut s, &fwd(), &course);
        std::println!("{t} pos {:?} vel {:?} fl {:x} surf {} ev {:?}", s.pos, s.vel, s.flags, s.surface, ev);
    }
}

#[cfg(test)]
#[test]
#[ignore]
fn debug_jump_onto() {
    let (_, cb) = scenes();
    let course = Scene::new(crate::mapblob::MapView::new(&cb).unwrap());
    let rate: u32 = std::env::var("RATE").ok().and_then(|v| v.parse().ok()).unwrap_or(15);
    let c = MotorConfig::default_at(rate);
    let mut s = MotorState::at(IVec3::new(course_lane(std::env::var("LANE").ok().and_then(|v| v.parse().ok()).unwrap_or(10)), 10, 2000), 0);
    settle(&c, &mut s, &course);
    let mut jumped = false;
    for t in 0..c.rate * 3 {
        let near = s.pos.z >= 9000 - 2000 - 310 - 1800;
        let cmd = if near && !jumped { jumped = true; jump(fwd()) } else { fwd() };
        let ev = step(&c, &mut s, &cmd, &course);
        std::println!("{t} pos {:?} vel {:?} fl {:x} ev j{} l{} su{}", s.pos, s.vel, s.flags, ev.jumped, ev.landed, ev.stepped_up);
    }
}
