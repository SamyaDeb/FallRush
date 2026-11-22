//! Hazard vector cases (`hazard_v2.bin`): random defs of every kit type evaluated at random hazard times and rates.
//! Integers only; the same generator feeds `rush-sim hazard compare` (the v1-vs-v2 error table against the C# maths).

use super::defs::*;
use super::*;
use crate::vectors::{Rng, W};

/// Kit type of case `i` (round robin over the 14 types).
pub fn case_type(i: u32) -> u8 {
    (i % 14) as u8 + 1
}

pub fn rnd_sweeper(r: &mut Rng) -> SweeperDef {
    SweeperDef {
        pivot_x: r.range(-30_000, 30_000) as i32,
        pivot_y: r.range(0, 10_000) as i32,
        pivot_z: r.range(-30_000, 30_000) as i32,
        arm: r.range(1000, 9000) as i32,
        bar_radius: r.range(100, 400) as i32,
        bar_height: r.range(200, 1500) as i32,
        period_ticks: r.range(-300, 300) as i32,
        phase16: r.range(0, 65535) as i32,
        knockback_cms: 600,
        knock_up_cms: 350,
        stun_ticks: 11,
        immunity_ticks: 26,
        tangential: r.range(0, 1) as i32,
    }
}

pub fn rnd_platform(r: &mut Rng) -> PlatformDef {
    let mut d = PlatformDef {
        center_x: r.range(-30_000, 30_000) as i32,
        center_y: r.range(0, 10_000) as i32,
        center_z: r.range(-30_000, 30_000) as i32,
        half_x: 1500,
        half_y: 200,
        half_z: 1500,
        travel_x: r.range(-6000, 6000) as i32,
        travel_y: r.range(-2000, 2000) as i32,
        travel_z: r.range(-6000, 6000) as i32,
        period_ticks: r.range(30, 400) as i32,
        phase16: r.range(0, 65535) as i32,
        knot_count: if r.range(0, 1) == 0 { 0 } else { r.range(2, 8) as i32 },
        knots: [0; MAX_KNOTS * 3],
    };
    for k in 0..(d.knot_count as usize * 3) {
        d.knots[k] = r.range(-8000, 8000) as i32;
    }
    d
}

pub fn rnd_turn_table(r: &mut Rng) -> TurnTableDef {
    TurnTableDef {
        center_x: r.range(-30_000, 30_000) as i32,
        center_y: r.range(0, 5000) as i32,
        center_z: r.range(-30_000, 30_000) as i32,
        radius: r.range(2000, 9000) as i32,
        thickness: 400,
        period_ticks: r.range(-600, 600) as i32,
        phase16: r.range(0, 65535) as i32,
    }
}

pub fn rnd_door(r: &mut Rng) -> GateDoorDef {
    GateDoorDef {
        center_x: r.range(-30_000, 30_000) as i32,
        center_y: r.range(0, 5000) as i32,
        center_z: r.range(-30_000, 30_000) as i32,
        half_x: 2000,
        half_y: 1500,
        half_z: 250,
        lift: r.range(1000, 4000) as i32,
        closed_ticks: r.range(10, 120) as i32,
        opening_ticks: r.range(5, 30) as i32,
        open_ticks: r.range(10, 120) as i32,
        closing_ticks: r.range(5, 30) as i32,
        offset_ticks: r.range(0, 100) as i32,
        telegraph_ticks: 15,
        phase16: r.range(0, 65535) as i32,
        mode: r.range(0, 1) as i32,
    }
}

pub fn rnd_mallet(r: &mut Rng) -> MalletDef {
    MalletDef {
        pivot_x: r.range(-30_000, 30_000) as i32,
        pivot_y: r.range(3000, 9000) as i32,
        pivot_z: r.range(-30_000, 30_000) as i32,
        swing_axis: if r.range(0, 1) == 0 { 0 } else { 2 },
        arm: r.range(2000, 6000) as i32,
        head_radius: r.range(300, 800) as i32,
        head_half_width: r.range(500, 1500) as i32,
        amplitude_phase16: r.range(4000, 15000) as i32,
        period_ticks: r.range(-200, 200) as i32,
        phase16: r.range(0, 65535) as i32,
        telegraph_ticks: 15,
        knockback_cms: 900,
        knock_up_cms: 500,
        stun_ticks: 18,
        immunity_ticks: 33,
    }
}

pub fn rnd_ball(r: &mut Rng) -> RollingBallDef {
    let sx = r.range(-40_000, 40_000) as i32;
    let sy = r.range(0, 12_000) as i32;
    let sz = r.range(-40_000, 40_000) as i32;
    RollingBallDef {
        start_x: sx,
        start_y: sy,
        start_z: sz,
        end_x: sx + r.range(-40_000, 40_000) as i32,
        end_y: sy - r.range(0, 6000) as i32,
        end_z: sz + r.range(-40_000, 40_000) as i32,
        radius: r.range(600, 1200) as i32,
        speed_mm_s: r.range(2000, 7000) as i32,
        period_ticks: r.range(30, 300) as i32,
        phase16: r.range(0, 65535) as i32,
        telegraph_ticks: r.range(9, 30) as i32,
        knockback_cms: 900,
        knock_up_cms: 500,
        stun_ticks: 18,
        immunity_ticks: 33,
    }
}

pub fn rnd_lid(r: &mut Rng) -> LidGridDef {
    let n = [5, 7, 9, 11][r.range(0, 3) as usize];
    let mut d = LidGridDef {
        n,
        lid: 1800,
        gap: 60,
        thickness: 400,
        top: r.range(0, 4000) as i32,
        lit_ticks: r.range(15, 45) as i32,
        flipped_ticks: r.range(30, 60) as i32,
        first_wave_ticks: r.range(30, 120) as i32,
        wave_interval_count: 4,
        waves_per_stage: r.range(4, 10) as i32,
        flip_permille_count: 4,
        ring_retire_count: ((n - 3) / 2).clamp(0, 3),
        retire_warn_ticks: 90,
        min_inner_solid: 5,
        loop_ticks: if r.range(0, 3) == 0 { r.range(1500, 4000) as i32 } else { 0 },
        seed: r.next() as u32,
        ..Default::default()
    };
    d.wave_intervals[..4].copy_from_slice(&[90, 75, 60, 45]);
    d.flip_permille[..4].copy_from_slice(&[200, 250, 300, 350]);
    d.ring_retire_ticks[..3].copy_from_slice(&[750, 1500, 2250]);
    d
}

pub fn rnd_stars(r: &mut Rng) -> StarFieldDef {
    let mut d = StarFieldDef { anchor_count: r.range(8, 40) as i32, ..Default::default() };
    for a in 0..d.anchor_count as usize {
        d.anchors[a * 3] = r.range(-20_000, 20_000) as i32;
        d.anchors[a * 3 + 1] = r.range(0, 5000) as i32;
        d.anchors[a * 3 + 2] = r.range(-20_000, 20_000) as i32;
        d.attach[a] = -1;
    }
    d.golden_count = 3;
    d.golden[..3].copy_from_slice(&[0, 1, 2]);
    d.wave_every_ticks = 150;
    d.wave_size = r.range(3, 6) as i32;
    d.wave_life_ticks = 300;
    d.golden_every_ticks = 600;
    d.golden_life_ticks = 240;
    d.golden_value = 3;
    d.pickup_radius = 400;
    d.drop_pop_ticks = 15;
    d.drop_life_ticks = 240;
    d.drop_distance = 1000;
    d
}

/// One hazard case.
pub fn case(seed: u64, i: u32, out: &mut [u8]) -> usize {
    let mut r = Rng(seed ^ 0x4A2A_0000_0000_0000 ^ (i as u64).wrapping_mul(0x9E37_79B9_7F4A_7C15));
    r.next();
    let mut w = W { b: out, n: 0 };
    let ty = case_type(i);
    let rate = [15i64, 20, 30][r.range(0, 2) as usize];
    let tick = r.range(-200, 6000);
    let t60 = tick * (60 / rate);
    let f = IVec3::new(r.range(-30_000, 30_000) as i32, r.range(0, 8000) as i32, r.range(-30_000, 30_000) as i32);
    w.u8(ty);
    w.i64(t60);
    w.v3(f);
    match ty {
        1 => {
            let d = rnd_sweeper(&mut r);
            // probe near the bar so contacts happen
            let p = phase_at(t60, d.period_ticks, d.phase16);
            let along = r.range(-(d.arm as i64), d.arm as i64);
            let ff = IVec3::new(
                d.pivot_x + mul_q15(along, sin_q15(p + 16384)) as i32 + r.range(-500, 500) as i32,
                d.pivot_y + d.bar_height - 500,
                d.pivot_z + mul_q15(along, sin_q15(p)) as i32 + r.range(-500, 500) as i32,
            );
            w.i32(p);
            match sweeper_contact(&d, t60, ff, 310, 1000) {
                Some(v) => {
                    w.i32(v[0]);
                    w.i32(v[1]);
                }
                None => w.u8(0),
            }
        }
        2 => {
            let d = rnd_platform(&mut r);
            w.v3(platform_center(&d, t60));
            w.v3(platform_delta(&d, t60, 60 / rate));
        }
        3 => {
            let d = ConveyorDef { belt_x: r.range(-5000, 5000) as i32, belt_y: r.range(-1000, 1000) as i32, belt_z: r.range(-5000, 5000) as i32, ..Default::default() };
            w.v3(conveyor_delta(&d, t60, 60 / rate));
        }
        4 => {
            let d = SpringPadDef { center_x: f.x, center_y: f.y, center_z: f.z, half_x: 1000, half_z: 1000, ..Default::default() };
            let q = IVec3::new(f.x + r.range(-1200, 1200) as i32, f.y + r.range(-80, 80) as i32, f.z + r.range(-1200, 1200) as i32);
            w.u8(on_spring_pad(&d, q) as u8);
        }
        5 => {
            let d = rnd_turn_table(&mut r);
            let p0 = turn_table_phase(&d, t60 - 60 / rate);
            let p1 = turn_table_phase(&d, t60);
            let fx = d.center_x + r.range(-(d.radius as i64), d.radius as i64) as i32;
            let fz = d.center_z + r.range(-(d.radius as i64), d.radius as i64) as i32;
            let (dx, dz) = turn_table_carry(IVec3::new(d.center_x, d.center_y, d.center_z), p0, p1, fx, fz);
            w.i32(p1);
            w.i32(dx);
            w.i32(dz);
        }
        6 => {
            let d = rnd_door(&mut r);
            w.i32(door_lift(&d, t30(t60)));
            w.i64(door_ticks_until_closing(&d, t30(t60)));
        }
        7 => {
            let d = rnd_mallet(&mut r);
            let (hd, vs) = mallet_head(&d, t60);
            w.v3(hd);
            w.i32(vs);
            let ff = IVec3::new(hd.x + r.range(-900, 900) as i32, hd.y - 600, hd.z + r.range(-900, 900) as i32);
            match mallet_contact(&d, t60, ff, 310, 1000) {
                Some(v) => {
                    w.i32(v[0]);
                    w.i32(v[1]);
                }
                None => w.u8(0),
            }
        }
        8 => {
            let d = BumperDef { center_x: f.x, center_y: f.y, center_z: f.z, radius: 800, height: 1500, contact_margin: 60, ..Default::default() };
            let q = IVec3::new(f.x + r.range(-1300, 1300) as i32, f.y + r.range(-900, 1400) as i32, f.z + r.range(-1300, 1300) as i32);
            match bumper_contact(&d, q, 310, 1000) {
                Some(v) => {
                    w.i32(v[0]);
                    w.i32(v[1]);
                }
                None => w.u8(0),
            }
        }
        9 => {
            let d = GooDef { y0: r.range(-3000, 0) as i32, rise_mm_per_sec: r.range(50, 600) as i32, delay_ticks: r.range(0, 600) as i32, max_y: r.range(10_000, 40_000) as i32, ..Default::default() };
            w.i32(goo_y(&d, t60));
            w.i64(goo_ticks_to_reach(&d, r.range(-1000, 30_000) as i32));
        }
        10 => {
            let mut d = TileGridDef { origin_x: -8000, origin_z: -8000, tile: 1600, gap: 60, cols: 10, rows: 10, shelf_count: 3, thickness: 400, wobble_ticks: 30, respawn_ticks: 150, respawn_shelves: 1, ..Default::default() };
            d.shelf_tops[..3].copy_from_slice(&[12_000, 6000, 0]);
            let q = IVec3::new(r.range(-9000, 9000) as i32, [12_000, 6000, 0][r.range(0, 2) as usize] + r.range(-100, 100) as i32, r.range(-9000, 9000) as i32);
            let id = d.tile_under(q.x, q.y, q.z);
            w.i32(id);
            w.i32(d.respawn_for(id.max(0)));
        }
        11 => {
            let d = rnd_stars(&mut r);
            let sd = r.next();
            let id = if r.range(0, 4) == 0 { STAR_GOLDEN_BASE + r.range(1, 8) as i32 } else { r.range(0, 200) as i32 };
            match star_slot(&d, sd, id) {
                Some(s) => {
                    w.i32(s.anchor);
                    w.i32(s.spawn_t);
                    w.i32(s.expire_t);
                    w.i32(s.value);
                }
                None => w.u8(0),
            }
            w.v3(drop_pos(&d, f, sd as u32, r.range(0, 30)));
            w.u8(star_touch(f, IVec3::new(f.x + r.range(-800, 800) as i32, f.y - 200, f.z + r.range(-800, 800) as i32), 310, 1000, 400) as u8);
        }
        12 => {
            let d = FallingBlockDef { delay_ticks: r.range(10, 60) as i32, gone_ticks: r.range(30, 200) as i32, ..Default::default() };
            let trig = r.range(1, 3000);
            let t = r.range(0, 3500);
            let solid = !(t >= trig + d.delay_ticks as i64 && t < trig + (d.delay_ticks + d.gone_ticks) as i64);
            w.u8(solid as u8);
        }
        13 => {
            let d = rnd_ball(&mut r);
            let mut j = 0;
            while j < 3 {
                match ball_pos(&d, t60, j) {
                    Some((p, k)) => {
                        w.v3(p);
                        w.i64(k);
                    }
                    None => w.u8(0),
                }
                j += 1;
            }
            w.i32(ball_telegraph1000(&d, t30(t60)));
            if let Some((p, _)) = ball_pos(&d, t60, 0) {
                let ff = IVec3::new(p.x + r.range(-1200, 1200) as i32, p.y - 800, p.z + r.range(-1200, 1200) as i32);
                match ball_contact(&d, t60, ff, 310, 1000) {
                    Some(v) => {
                        w.i32(v[0]);
                        w.i32(v[1]);
                    }
                    None => w.u8(0),
                }
            }
        }
        _ => {
            let d = rnd_lid(&mut r);
            let mut st = [lid::LidState::Idle; lid::MAX_LIDS_PER_GRID];
            let mut sc = [0i32; lid::MAX_LIDS_PER_GRID];
            lid::states(&d, t30(t60), &mut st, &mut sc);
            // pack 81..121 states as a 3-bit stream digest (fold into 8 bytes)
            let mut acc = 0u64;
            for (k, s) in st.iter().take(d.count() as usize).enumerate() {
                acc = acc.rotate_left(3) ^ (*s as u64) ^ (k as u64) << 40;
            }
            w.i64(acc as i64);
            w.i32(d.lid_under(f.x % 9000, d.top + 50, f.z % 9000));
        }
    }
    w.n
}
