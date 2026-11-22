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
    let mut per = Vec::new();
    let t0 = std::time::Instant::now();
    for _ in 0..rate * secs {
        ev.n = 0;
        world::step(&mut w, &[None; MAX_PLAYERS], &m, &params, &mut NoHasher, &mut ev);
        per.push(rush_core::collide::take_tick_counters());
    }
    let dt = t0.elapsed().as_secs_f64() * 1e6 / (rate * secs) as f64;
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
