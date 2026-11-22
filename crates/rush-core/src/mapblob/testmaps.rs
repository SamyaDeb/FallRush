//! Synthetic maps (P1 prompt I.2): flat arena, motor course (ramps, steps, ledges, gaps, walls), and helpers for the
//! hazard / rules maps that `rush-sim` builds. Off-chain only (feature `builder`).

extern crate alloc;
use alloc::vec;
use alloc::vec::Vec;

use super::builder::*;
use super::meta;
use crate::fx::{cos_q15, deg_to_turn_q16, div_round, sin_q15, ONE_Q15};
use crate::vec::IVec3;

/// Ground slab: top at y = 0, `half` mm in x and z.
pub fn ground(half: i32) -> ColliderSpec {
    ColliderSpec::fixed(aabb_box(IVec3::new(0, -500, 0), IVec3::new(half, 500, half)))
}

pub fn boxc(cx: i32, cy: i32, cz: i32, hx: i32, hy: i32, hz: i32) -> ColliderSpec {
    ColliderSpec::fixed(aabb_box(IVec3::new(cx, cy, cz), IVec3::new(hx, hy, hz)))
}

/// A ramp rising toward +Z at `deg`, lane centred on x = `cx`, starting at z = `z0` on the ground (y = 0), with a
/// rise of `rise` mm, then a flat top. Returns the colliders.
pub fn ramp(cx: i32, z0: i32, deg: i32, rise: i32) -> Vec<ColliderSpec> {
    let t = deg_to_turn_q16(deg as i64);
    let (s, c) = (sin_q15(t) as i64, cos_q15(t) as i64);
    let hy = 200i64;
    // length along the slope so that the top rises `rise`: L = rise / sin
    let len = div_round(rise as i64 * ONE_Q15 as i64, s.max(1));
    let hz = len / 2;
    let hx = 1500i64;
    // top-low-end at (cx, 0, z0): cy = hz·s − hy·c, cz = z0 + hy·s + hz·c   (Q15 → mm)
    let q = ONE_Q15 as i64;
    let cy = div_round(hz * s - hy * c, q);
    let cz = z0 as i64 + div_round(hy * s + hz * c, q);
    let shape = rotated_box(IVec3::new(cx, cy as i32, cz as i32), IVec3::new(hx as i32, hy as i32, hz as i32), 0, -t);
    // top end of the slope
    let z1 = z0 as i64 + div_round(len * c, q);
    let plat = boxc(cx, rise / 2, (z1 + 2000) as i32, 1500, rise / 2, 2000);
    vec![ColliderSpec::fixed(shape), plat]
}

fn meta_words(mode: u8) -> [i32; meta::WORDS] {
    let mut m = [0i32; meta::WORDS];
    m[meta::MAP_ID] = 255;
    m[meta::MODE] = mode as i32;
    m[meta::VARIANTS] = 1;
    m[meta::KILL_Y] = -20000;
    m[meta::SWEEPER_START_A] = i32::MIN;
    m[meta::SWEEPER_START_B] = i32::MIN;
    m[meta::BOT_AREA_HX] = 10000;
    m[meta::BOT_AREA_HZ] = 10000;
    m
}

/// Twelve spawns in a row at y = `y`, facing +Z.
pub fn row_spawns(y: i32, z: i32) -> Vec<(IVec3, u8)> {
    (0..12).map(|i| (IVec3::new(-11000 + 2000 * i, y, z), 0u8)).collect()
}

/// A flat 200 m arena with low walls.
pub fn flat_arena_spec() -> MapSpec {
    let mut cols = vec![ground(100_000)];
    for (cx, cz, hx, hz) in [(0, 100_500, 100_000, 500), (0, -100_500, 100_000, 500), (100_500, 0, 500, 100_000), (-100_500, 0, 500, 100_000)] {
        cols.push(boxc(cx, 1000, cz, hx, 1000, hz));
    }
    MapSpec { name: b"FlatArena".to_vec(), meta: meta_words(super::mode::SANDBOX), colliders: cols, spawns: row_spawns(10, 0), cell_mm: 2000, ..Default::default() }
}

/// Lanes along +Z from z = 5 m, 6 m apart in x:
/// 0..=5 ramps 15°, 30°, 44°, 45°, 46°, 50° (rise 2 m); 6..=9 steps 200, 250, 260, 300 mm; 10 a 1.15 m block, 11 a 1.65 m block (the rounded bottom rides over edges below the sphere centre, so the reach is apex + r ≈ 1.5 m);
/// 12 a 3 m ledge (drop-off toward +Z at z = 10 m), 13 a 3.5 m tower, 14 a wall across the lane at
/// z = 10 m, 15 a gap (1.5 m) between two 1 m platforms.
pub fn motor_course_spec() -> MapSpec {
    let mut cols = vec![ground(150_000)];
    let lane = |i: i32| -60_000 + i * 6000;
    for (i, deg) in [15, 30, 44, 45, 46, 50].iter().enumerate() {
        cols.extend(ramp(lane(i as i32), 5000, *deg, 2000));
    }
    for (k, h) in [200, 250, 260, 300].iter().enumerate() {
        cols.push(boxc(lane(6 + k as i32), h / 2, 9000, 1500, h / 2, 4000));
    }
    cols.push(boxc(lane(10), 575, 9000, 1500, 575, 2000));
    cols.push(boxc(lane(11), 825, 9000, 1500, 825, 2000));
    // ledge: a 3 m platform from z = −5 m to z = 10 m (start on it, walk off toward +Z)
    cols.push(boxc(lane(12), 1500, 2500, 1500, 1500, 7500));
    cols.push(boxc(lane(13), 1750, 0, 1500, 1750, 1500));
    cols.push(boxc(lane(14), 2000, 10_500, 3000, 2000, 500));
    cols.push(boxc(lane(15), 500, 3000, 1500, 500, 3000));
    cols.push(boxc(lane(15), 500, 10_500, 1500, 500, 3000));
    MapSpec { name: b"MotorCourse".to_vec(), meta: meta_words(super::mode::SANDBOX), colliders: cols, spawns: row_spawns(10, 0), cell_mm: 2000, ..Default::default() }
}

/// The x of lane `i` of the motor course.
pub const fn course_lane(i: i32) -> i32 {
    -60_000 + i * 6000
}
