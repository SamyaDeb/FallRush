//! `rush-sim counters --map <blob> [--rate R] [--secs S] [--seed N]`: per-tick work counters (feature `counters`) on a
//! map with 12 bots in free play: narrowphase tests, sweeps, sweep sub-steps, TOI bisections, slides, bot raycasts.
//! Prints p50 / p95 / max per tick (the input for compute-unit pricing in P0 / P2). Also used by the proof table.

use rush_core::collide::ctr;
use rush_core::config::MovementParams;
use rush_core::mapblob::MapView;
use rush_core::world::{self, pose_scene, NoHasher, StepEvents, World, MAX_PLAYERS};

pub struct Stats {
    pub per_tick: Vec<[u32; 6]>,
    pub wall_us_per_tick: f64,
}

/// Runs `secs` of 12 bots (all slots) and returns the counter samples per tick (counted on a re-posed scene per tick).
pub fn measure(blob: &[u8], rate: u32, secs: u32, seed: u64) -> Stats {
    let m = MapView::new(blob).expect("blob");
    let mut w = World::new(&m, rate);
    w.hazard_seed = seed;
    for s in 0..MAX_PLAYERS {
        w.add_player(&m, s, true);
    }
    let params = MovementParams::default();
    let mut ev = StepEvents::default();
    // a real round when the map has a mode (all 12 take part); counted from RUSH only
    let ranked = m.mode() != rush_core::mapblob::mode::SANDBOX as i32 && m.mode() != rush_core::mapblob::mode::LOBBY as i32;
    if ranked {
        rush_core::rules::begin_round(&mut w, &m, 0, 0, 0xFFF, rush_core::rules::ROUND_INTRO_30, rush_core::rules::ROUND_COUNTDOWN_30, &mut ev);
    }
    let mut per = Vec::new();
    let t0 = std::time::Instant::now();
    let mut guard = 0;
    while per.len() < (rate * secs) as usize && guard < rate * (secs + 30) {
        guard += 1;
        ev.n = 0;
        world::step(&mut w, &[None; MAX_PLAYERS], &m, &params, &mut NoHasher, &mut ev);
        let c = rush_core::collide::take_tick_counters();
        if !ranked || w.round.phase == rush_core::rules::phase::RUSH {
            per.push(c);
        }
        if ranked && rush_core::rules::is_over(&w) {
            break;
        }
    }
    let dt = t0.elapsed().as_secs_f64() * 1e6 / per.len().max(1) as f64;
    let _ = pose_scene;
    Stats { per_tick: per, wall_us_per_tick: dt }
}

pub fn pct(v: &mut [u32], p: f64) -> u32 {
    v.sort_unstable();
    if v.is_empty() {
        return 0;
    }
    v[((v.len() - 1) as f64 * p).round() as usize]
}

pub const NAMES: [(&str, usize); 6] = [("narrowphase", ctr::NARROW), ("sweeps", ctr::SWEEPS), ("sub-steps", ctr::SUBSTEPS), ("bisections", ctr::BISECT), ("slides", ctr::SLIDES), ("bot rays", ctr::BOT_RAYS)];

pub fn cmd(args: &[String]) -> i32 {
    let map = crate::arg(args, "--map").expect("--map <blob>");
    let rate: u32 = crate::arg(args, "--rate").and_then(|v| v.parse().ok()).unwrap_or(20);
    let secs: u32 = crate::arg(args, "--secs").and_then(|v| v.parse().ok()).unwrap_or(60);
    let seed: u64 = crate::arg(args, "--seed").and_then(|v| v.parse().ok()).unwrap_or(1);
    let blob = std::fs::read(&map).unwrap();
    let s = measure(&blob, rate, secs, seed);
    println!("{map} @ {rate} Hz, 12 bots, {secs} s ({} ticks), native {:.1} µs/tick", s.per_tick.len(), s.wall_us_per_tick);
    println!("| counter / tick | p50 | p95 | max |\n|---|---|---|---|");
    for (n, k) in NAMES {
        let mut v: Vec<u32> = s.per_tick.iter().map(|c| c[k]).collect();
        println!("| {n} | {} | {} | {} |", pct(&mut v.clone(), 0.5), pct(&mut v.clone(), 0.95), pct(&mut v, 1.0));
    }
    0
}

/// `rush-sim player-counters --map <blob> [--secs S] [--seeds N]`: narrowphase tests / sweep sub-steps / bisections
/// per *player*-tick (one `step_part` per slot), RUSH only, 12 bots. Picks the deterministic per-player work cap.
pub fn player_cmd(args: &[String]) -> i32 {
    use rush_core::collide::Scene;
    let map = crate::arg(args, "--map").expect("--map <blob>");
    let secs: u32 = crate::arg(args, "--secs").and_then(|v| v.parse().ok()).unwrap_or(120);
    let seeds: u64 = crate::arg(args, "--seeds").and_then(|v| v.parse().ok()).unwrap_or(5);
    let blob = std::fs::read(&map).unwrap();
    let m = MapView::new(&blob).expect("blob");
    let params = MovementParams::default();
    let mut narrow = Vec::new();
    for seed in 1..=seeds {
        let mut w = World::new(&m, 30);
        w.hazard_seed = seed.wrapping_mul(0x9E37_79B9_7F4A_7C15);
        for s in 0..MAX_PLAYERS {
            w.add_player(&m, s, true);
        }
        let mut ev = StepEvents::default();
        let ranked = m.mode() != rush_core::mapblob::mode::SANDBOX as i32 && m.mode() != rush_core::mapblob::mode::LOBBY as i32;
        if ranked {
            rush_core::rules::begin_round(&mut w, &m, 0, 0, 0xFFF, rush_core::rules::ROUND_INTRO_30, rush_core::rules::ROUND_COUNTDOWN_30, &mut ev);
        }
        let mut sc = Scene::new(m);
        let mut t = 0;
        while t < 30 * (secs + 10) {
            t += 1;
            ev.n = 0;
            for p in 0..12u8 {
                rush_core::collide::take_tick_counters();
                world::step_part(&mut w, &[None; MAX_PLAYERS], &m, &params, &mut NoHasher, &mut ev, &mut sc, p, 12);
                let c = rush_core::collide::take_tick_counters();
                if !ranked || w.round.phase == rush_core::rules::phase::RUSH {
                    narrow.push(c[ctr::NARROW]);
                }
            }
            if ranked && rush_core::rules::is_over(&w) {
                break;
            }
        }
    }
    let n = narrow.len();
    println!("{map}: {n} player-ticks; narrowphase tests per player-tick p50 {} p99 {} p99.9 {} p99.99 {} max {}",
        pct(&mut narrow.clone(), 0.5), pct(&mut narrow.clone(), 0.99), pct(&mut narrow.clone(), 0.999), pct(&mut narrow.clone(), 0.9999), pct(&mut narrow, 1.0));
    0
}
