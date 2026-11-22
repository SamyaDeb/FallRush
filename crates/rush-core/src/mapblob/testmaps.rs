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

// ------------------------------------------------------------------ synthetic rule maps (P1 prompt I.2 / I.3)

use super::mode;
use crate::hazards::defs::*;
use crate::mapblob::dyn_kind;

fn rules_meta(md: u8, timer_s: i32) -> [i32; meta::WORDS] {
    let mut m = meta_words(md);
    m[meta::TIMER_CAP_S] = timer_s;
    m[meta::KILL_Y] = -10_000;
    m
}

fn hz<F: FnOnce(&mut [i32])>(ty: HazardType, id: &str, f: F) -> HazardSpec {
    let mut w = vec![0i32; ty.words()];
    f(&mut w);
    HazardSpec { ty, phase_min: 0, phase_max: 65535, id: id.as_bytes().to_vec(), words: w, baked: id.is_empty() }
}

/// Race strip: 10 m × 92 m, start gate, 3 checkpoints, a 1.5 m gap after the first one, a course polyline.
pub fn race_strip_spec() -> MapSpec {
    let cp = |z: i32, course: i32, rz: i32| CheckpointSpec {
        center: IVec3::new(0, 1500, z),
        half: IVec3::new(5000, 2000, 1000),
        course_mm: course,
        respawns: vec![(IVec3::new(-3000, 50, rz), 0), (IVec3::new(0, 50, rz), 0), (IVec3::new(3000, 50, rz), 0)],
    };
    let gate = GateDoorDef { center_x: 0, center_y: 1250, center_z: 2000, half_x: 5000, half_y: 1250, half_z: 200, lift: 2800, opening_ticks: 10, mode: 1, ..Default::default() };
    let gate_col = ColliderSpec { shape: aabb_box(IVec3::ZERO, IVec3::new(5000, 1250, 200)), surface: -1, dyn_kind: dyn_kind::DOOR, dyn_index: 0 };
    MapSpec {
        name: b"SynthRace".to_vec(),
        meta: rules_meta(mode::RACE, 120),
        colliders: vec![boxc(0, -500, 10_000, 5000, 500, 20_000), boxc(0, -500, 56_000, 5000, 500, 24_500), gate_col],
        spawns: (0..12).map(|i| (IVec3::new(-4000 + (i % 6) * 1600, 50, -8000 + (i / 6) * 2000), 0u8)).collect(),
        checkpoints: vec![cp(20_000, 28_000, 23_000), cp(45_000, 53_000, 48_000), cp(70_000, 78_000, 73_000)],
        course: vec![IVec3::new(0, 0, -8000), IVec3::new(0, 0, 80_000)],
        hazards: [vec![hz(HazardType::GateDoor, "", |w| gate.write(w))], Vec::new()],
        cell_mm: 2000,
        ..Default::default()
    }
}

/// Goo tower: a 200 mm-rise staircase (walkable steps) to 10 m, a summit pad (the last checkpoint), rising goo.
pub fn goo_tower_spec() -> MapSpec {
    let mut cols = vec![ground(30_000)];
    for k in 0..50 {
        cols.push(boxc(0, 100 + 200 * k, 3000 + 600 * k + 300, 4000, 100 + 200 * k, 300));
    }
    let top = 200 * 50;
    cols.push(boxc(0, top / 2, 35_000, 4000, top / 2, 2000));
    let goo = GooDef { y0: -1500, rise_mm_per_sec: 170, delay_ticks: 90, max_y: top + 1000, half_x: 22_000, half_z: 22_000, warn_ticks: 90, ..Default::default() };
    MapSpec {
        name: b"SynthGoo".to_vec(),
        meta: rules_meta(mode::SURVIVAL, 150),
        colliders: cols,
        spawns: row_spawns(50, 0),
        checkpoints: vec![CheckpointSpec { center: IVec3::new(0, top + 1000, 35_000), half: IVec3::new(4000, 1000, 2000), course_mm: 0, respawns: Vec::new() }],
        hazards: [vec![hz(HazardType::GooPlane, "goo", |w| goo.write(w))], Vec::new()],
        cell_mm: 2000,
        ..Default::default()
    }
}

/// Turn table + star field: 16 anchors (4 on the table), 60 s timer.
pub fn star_field_spec() -> MapSpec {
    let mut f = StarFieldDef { anchor_count: 16, ..Default::default() };
    for a in 0..16usize {
        let (x, z, att) = if a < 12 { (-11_000 + 2000 * a as i32, 10_000, -1) } else { (1500 * ((a as i32 % 2) * 2 - 1), 1500 * (((a as i32 / 2) % 2) * 2 - 1), 1000) };
        f.anchors[a * 3] = x;
        f.anchors[a * 3 + 1] = 300;
        f.anchors[a * 3 + 2] = z;
        f.attach[a] = att;
    }
    f.golden_count = 1;
    f.golden[0] = 11;
    f.wave_every_ticks = 150;
    f.wave_size = 8;
    f.wave_life_ticks = 300;
    f.golden_every_ticks = 450;
    f.golden_life_ticks = 240;
    f.golden_value = 3;
    f.pickup_radius = 400;
    f.drop_pop_ticks = 15;
    f.drop_life_ticks = 240;
    f.drop_distance = 1000;
    let tt = TurnTableDef { center_x: 0, center_y: 300, center_z: -8000, radius: 3500, thickness: 300, period_ticks: 360, phase16: 0 };
    let verts: Vec<(i32, i32)> = (0..32).map(|k| {
        let t = k * 65536 / 32;
        (tt.center_x + cos_q15(t) * tt.radius / ONE_Q15, tt.center_z + sin_q15(t) * tt.radius / ONE_Q15)
    }).collect();
    let table = ColliderSpec { shape: ShapeSpec::Prism { verts, y0: 0, y1: 300 }, surface: 0, dyn_kind: dyn_kind::STATIC, dyn_index: 0 };
    MapSpec {
        name: b"SynthStars".to_vec(),
        meta: rules_meta(mode::SKILL, 60),
        colliders: vec![ground(40_000), table],
        spawns: row_spawns(50, 0),
        hazards: [vec![hz(HazardType::TurnTable, "tt", |w| tt.write(w)), hz(HazardType::StarPickup, "", |w| f.write(w))], Vec::new()],
        cell_mm: 2000,
        ..Default::default()
    }
}

/// Lid grid 9 × 9 at 6 m over a 5 × 5 one at 0 m (the Lid Flip shape), 120 s timer.
pub fn lid_grids_spec() -> MapSpec {
    let mut hzs = Vec::new();
    let mut cols = Vec::new();
    let mut base = 0;
    for (g, (n, top)) in [(9, 6000), (5, 0)].iter().enumerate() {
        let mut d = LidGridDef {
            n: *n, lid: 1800, gap: 60, thickness: 400, top: *top, lit_ticks: 30, flipped_ticks: 45, first_wave_ticks: 90,
            wave_interval_count: 4, waves_per_stage: 8, flip_permille_count: 4, ring_retire_count: if *n >= 9 { 3 } else { 1 },
            retire_warn_ticks: 90, min_inner_solid: 5, ..Default::default()
        };
        d.wave_intervals[..4].copy_from_slice(&[90, 75, 60, 45]);
        d.flip_permille[..4].copy_from_slice(&[200, 250, 300, 350]);
        d.ring_retire_ticks[..3].copy_from_slice(&[750, 1500, 2250]);
        hzs.push(hz(HazardType::LidFlip, if g == 0 { "lid_top" } else { "lid_low" }, |w| d.write(w)));
        for lid in 0..d.count() {
            let (x, y, z) = d.lid_center(lid);
            cols.push(ColliderSpec { shape: aabb_box(IVec3::new(x, y, z), IVec3::new(900, 200, 900)), surface: -1, dyn_kind: dyn_kind::LID, dyn_index: (base + lid) as u16 });
        }
        base += d.count();
    }
    MapSpec {
        name: b"SynthLids".to_vec(),
        meta: rules_meta(mode::FINAL, 120),
        colliders: cols,
        spawns: (0..12).map(|i| (IVec3::new(-3720 + 1860 * (i % 5), 6010, -3720 + 1860 * (i / 5)), 0u8)).collect(),
        hazards: [hzs, Vec::new()],
        cell_mm: 2000,
        ..Default::default()
    }
}
