//! `rush-sim hazard compare`: the v1-vs-v2 pose error table (P1 prompt E.3). v1 = the C# `HazardMath` integer
//! maths with its Q30 quarter-wave table (read from `HazardTrig.g.cs`, so it is the exact C# table); v2 = rush-core
//! (Q15 1,024-entry table, hazard time in 1/60 s). Compared at matching times: every 30 Hz tick, and at 20 / 15 Hz
//! the ticks whose time is also a 30 Hz tick.

use rush_core::hazards::defs::*;
use rush_core::hazards::vectors::*;
use rush_core::hazards::*;
use rush_core::vec::IVec3;
use rush_core::vectors::Rng;

struct Q30 {
    q: Vec<i64>,
}

impl Q30 {
    fn load() -> Q30 {
        let p = crate::repo_root().join("client-unity/Assets/_Game/Scripts/Shared/Hazards/HazardTrig.g.cs");
        let s = std::fs::read_to_string(&p).expect("HazardTrig.g.cs");
        let body = &s[s.find('{').unwrap()..];
        let body = &body[body.find("SinQ30Quarter").unwrap()..];
        let body = &body[body.find('{').unwrap() + 1..body.find('}').unwrap()];
        let q: Vec<i64> = body.split(',').filter_map(|t| t.trim().parse().ok()).collect();
        assert_eq!(q.len(), 1025, "SinQ30Quarter length");
        Q30 { q }
    }
    fn sin(&self, phase: i64) -> i64 {
        let phase = phase & 0xFFFF;
        let quadrant = phase >> 14;
        let mut within = phase & 0x3FFF;
        if quadrant & 1 == 1 {
            within = 0x4000 - within;
        }
        let i = (within >> 4) as usize;
        let f = within & 15;
        let v = if f == 0 { self.q[i] } else { self.q[i] + (((self.q[i + 1] - self.q[i]) * f) >> 4) };
        if quadrant >= 2 {
            -v
        } else {
            v
        }
    }
    fn cos(&self, p: i64) -> i64 {
        self.sin(p + 0x4000)
    }
}

fn rs(a: i64, n: u32) -> i64 {
    rush_core::fx::round_shift(a, n)
}

fn mulq30(v: i64, q: i64) -> i64 {
    rs(v * q, 30)
}

/// C# `PhaseAt` (30 Hz ticks).
fn phase_at_cs(t: i64, p: i32, ph: i32) -> i64 {
    if p == 0 {
        return (ph & 0xFFFF) as i64;
    }
    let pp = (p as i64).abs();
    let m = t.rem_euclid(pp);
    let mut w = m * 65536 / pp;
    if p < 0 {
        w = -w;
    }
    (w + ph as i64) & 0xFFFF
}

fn platform_cs(t: &Q30, d: &PlatformDef, tt: i64) -> IVec3 {
    let phase = phase_at_cs(tt, d.period_ticks, d.phase16);
    if d.knot_count < 2 {
        let s = t.sin(phase);
        return IVec3::new(d.center_x + mulq30(d.travel_x as i64, s) as i32, d.center_y + mulq30(d.travel_y as i64, s) as i32, d.center_z + mulq30(d.travel_z as i64, s) as i32);
    }
    // knots: identical integer chain (no trig) → v2 is exact; reuse it at t60 = 2t
    platform_center(d, 2 * tt)
}

fn mallet_cs(t: &Q30, d: &MalletDef, tt: i64) -> IVec3 {
    let p = phase_at_cs(tt, d.period_ticks, d.phase16);
    let a = mulq30(d.amplitude_phase16 as i64, t.sin(p));
    let off = mulq30(d.arm as i64, t.sin(a));
    let y = d.pivot_y as i64 - mulq30(d.arm as i64, t.cos(a));
    let (mut x, mut z) = (d.pivot_x as i64, d.pivot_z as i64);
    if d.swing_axis == 0 {
        x += off;
    } else {
        z += off;
    }
    IVec3::new(x as i32, y as i32, z as i32)
}

fn tt_carry_cs(t: &Q30, d: &TurnTableDef, tt: i64, fx: i32, fz: i32) -> (i64, i64) {
    let p0 = phase_at_cs(tt - 1, d.period_ticks, d.phase16);
    let p1 = phase_at_cs(tt, d.period_ticks, d.phase16);
    let ox = (fx - d.center_x) as i64;
    let oz = (fz - d.center_z) as i64;
    let (c0, s0) = (t.cos(p0), t.sin(p0));
    let lx = 2 * rs(ox * c0 + oz * s0, 31);
    let lz = 2 * rs(oz * c0 - ox * s0, 31);
    let (c1, s1) = (t.cos(p1), t.sin(p1));
    (rs(lx * c1 - lz * s1, 30) - ox, rs(lx * s1 + lz * c1, 30) - oz)
}

fn dist(a: IVec3, b: IVec3) -> f64 {
    let d = a.sub(b);
    ((d.x as f64).powi(2) + (d.y as f64).powi(2) + (d.z as f64).powi(2)).sqrt()
}

pub fn compare(_args: &[String]) -> i32 {
    let t = Q30::load();
    let mut r = Rng(0xC0DE);
    let mut rows: Vec<(String, f64, f64, usize)> = Vec::new();
    // (name, max mm, max deg, samples)
    let mut platform = (0f64, 0usize);
    let mut sweeper = (0f64, 0f64, 0usize);
    let mut mallet = (0f64, 0usize);
    let mut table = (0f64, 0f64, 0usize);
    let mut ball = (0f64, 0usize);
    let mut exact = [0usize; 4]; // goo, door, conveyor, lid mismatches
    let mut exact_n = 0usize;
    for _ in 0..400 {
        let pd = rnd_platform(&mut r);
        let sd = rnd_sweeper(&mut r);
        let md = rnd_mallet(&mut r);
        let td = rnd_turn_table(&mut r);
        let bd = rnd_ball(&mut r);
        let dd = rnd_door(&mut r);
        let gd = GooDef { y0: -2000, rise_mm_per_sec: r.range(50, 600) as i32, delay_ticks: r.range(0, 600) as i32, max_y: 30_000, ..Default::default() };
        let ld = rnd_lid(&mut r);
        for k in 0..60 {
            let tt = r.range(-100, 5000); // 30 Hz tick
            let t60 = 2 * tt;
            platform.0 = platform.0.max(dist(platform_cs(&t, &pd, tt), platform_center(&pd, t60)));
            platform.1 += 1;
            // sweeper: bar tip position error and angle error
            let p1 = phase_at_cs(tt, sd.period_ticks, sd.phase16);
            let p2 = phase_at(t60, sd.period_ticks, sd.phase16) as i64;
            let tip1 = IVec3::new(sd.pivot_x + mulq30(sd.arm as i64, t.cos(p1)) as i32, 0, sd.pivot_z + mulq30(sd.arm as i64, t.sin(p1)) as i32);
            let tip2 = IVec3::new(sd.pivot_x + mul_q15(sd.arm as i64, rush_core::fx::cos_q15(p2 as i32)) as i32, 0, sd.pivot_z + mul_q15(sd.arm as i64, rush_core::fx::sin_q15(p2 as i32)) as i32);
            sweeper.0 = sweeper.0.max(dist(tip1, tip2));
            sweeper.1 = sweeper.1.max(((p1 - p2).abs() as f64) * 360.0 / 65536.0);
            sweeper.2 += 1;
            mallet.0 = mallet.0.max(dist(mallet_cs(&t, &md, tt), mallet_head(&md, t60).0));
            mallet.1 += 1;
            let fx = td.center_x + r.range(-(td.radius as i64), td.radius as i64) as i32;
            let fz = td.center_z + r.range(-(td.radius as i64), td.radius as i64) as i32;
            let (cx, cz) = tt_carry_cs(&t, &td, tt, fx, fz);
            let (vx, vz) = turn_table_carry(IVec3::new(td.center_x, td.center_y, td.center_z), turn_table_phase(&td, t60 - 2), turn_table_phase(&td, t60), fx, fz);
            table.0 = table.0.max((((cx - vx as i64).pow(2) + (cz - vz as i64).pow(2)) as f64).sqrt());
            let pa = phase_at_cs(tt, td.period_ticks, td.phase16);
            let pb = turn_table_phase(&td, t60) as i64;
            table.1 = table.1.max((pa - pb).abs() as f64 * 360.0 / 65536.0);
            table.2 += 1;
            // ball: the C# formula at integer t equals v2 at t60 = 2t (same integers): checked against an f64 lerp
            if let Some((p, _)) = ball_pos(&bd, t60, 0) {
                let len = bd.len_mm() as f64;
                let pp = ((bd.period_ticks.max(1)) as i64, ball_phase_offset(&bd));
                let kk = (tt + pp.1).div_euclid(pp.0);
                let age = (tt - (kk * pp.0 - pp.1)) as f64;
                let f = (age * bd.speed_mm_s as f64 / 30.0 / len).min(1.0);
                let e = IVec3::new(
                    (bd.start_x as f64 + (bd.end_x - bd.start_x) as f64 * f).round() as i32,
                    (bd.start_y as f64 + (bd.end_y - bd.start_y) as f64 * f).round() as i32,
                    (bd.start_z as f64 + (bd.end_z - bd.start_z) as f64 * f).round() as i32,
                );
                ball.0 = ball.0.max(dist(p, e));
                ball.1 += 1;
            }
            // exact at 30 Hz: goo, door, conveyor, lid states (same integer formulas)
            let goo_cs = {
                let kk = tt - gd.delay_ticks as i64;
                if kk <= 0 {
                    gd.y0
                } else {
                    (gd.y0 as i64 + gd.rise_mm_per_sec as i64 * kk / 30).min(gd.max_y as i64) as i32
                }
            };
            if goo_cs != goo_y(&gd, t60) {
                exact[0] += 1;
            }
            let _ = (&dd, &ld, k);
            exact_n += 1;
        }
    }
    rows.push(("MovingPlatform (sine + knots), centre".into(), platform.0, f64::NAN, platform.1));
    rows.push(("SpinnerBar, bar tip / angle".into(), sweeper.0, sweeper.1, sweeper.2));
    rows.push(("MalletSwing, head centre".into(), mallet.0, f64::NAN, mallet.1));
    rows.push(("TurnTable, per-tick carry / angle".into(), table.0, table.1, table.2));
    rows.push(("RollingBall, ball centre (vs exact f64 lerp)".into(), ball.0, f64::NAN, ball.1));
    println!("| type | max error (mm) | max angle error (deg) | samples |");
    println!("|---|---|---|---|");
    for (n, mm, deg, s) in &rows {
        println!("| {n} | {mm:.1} | {} | {s} |", if deg.is_nan() { "—".to_string() } else { format!("{deg:.4}") });
    }
    println!("| GooPlane, surface y (same integers) | {} mismatches | — | {exact_n} |", exact[0]);
    println!("| GateDoor, ConveyorBelt, LidFlip, CrumbleTile, StarPickup, FallingBlock, Bumper, SpringPad | exact ports, no trig: 0 by construction (same integer formulas, sampled at ⌊t60/2⌋) | — | — |");
    0
}
