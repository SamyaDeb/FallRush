//! Bot tests (synthetic maps): a race bot follows the course to the finish; route waypoints with a Jump cross a gap;
//! determinism; Reference vs tiered noise.

use super::*;
use crate::config::MovementParams;
use crate::mapblob::builder::*;
use crate::mapblob::testmaps::*;
use crate::mapblob::{meta, mode, MapView};
use crate::vec::IVec3;
use crate::world::{step, NoHasher, StepEvents, World};
use std::vec::Vec;

/// A straight 60 m race lane along +z with 3 checkpoints and a course polyline; optional 1.6 m gap at z = 30 m.
fn lane_spec(gap: bool, routes: bool) -> MapSpec {
    let mut s = flat_arena_spec();
    s.meta[meta::MODE] = mode::RACE as i32;
    if gap {
        // replace the ground by two slabs with a 1.6 m gap at z 29.2 … 30.8 m, kill plane below
        s.colliders.clear();
        s.colliders.push(boxc(0, -500, -20_000 + 24_600, 20_000, 500, 24_600)); // z −20 … 29.2 m
        s.colliders.push(boxc(0, -500, 30_800 + 20_000, 20_000, 500, 20_000)); // z 30.8 … 70.8 m
    }
    s.spawns = (0..12).map(|i| (IVec3::new(-5500 + 1000 * i, 10, 0), 0u8)).collect();
    s.course = vec![IVec3::new(0, 0, 0), IVec3::new(0, 0, 20_000), IVec3::new(0, 0, 40_000), IVec3::new(0, 0, 60_000)];
    s.checkpoints = [20_000, 40_000, 60_000]
        .iter()
        .map(|z| CheckpointSpec { center: IVec3::new(0, 1000, *z), half: IVec3::new(8000, 2000, 1000), course_mm: *z, respawns: vec![(IVec3::new(0, 10, *z), 0)] })
        .collect();
    if routes {
        // one route per section; section 1 crosses the gap with a Jump point at z = 28.6 m
        let mut w = vec![3];
        for (sec, pts) in [
            (0, vec![(0, 0, 10_000, 1500, 0), (0, 0, 20_500, 1500, 0)]),
            (1, vec![(0, 0, 28_600, 700, 1), (0, 0, 32_500, 1500, 7), (0, 0, 40_500, 1500, 0)]),
            (2, vec![(0, 0, 60_500, 1500, 0)]),
        ] {
            w.push(sec);
            w.push(pts.len() as i32);
            for (x, y, z, r, a) in pts {
                w.extend_from_slice(&[x, y, z, r, a, 0]);
            }
        }
        s.bot_routes = w;
    }
    s
}

/// Runs a race with `n` bots on `blob` for `secs` at `rate`; returns the world.
fn run(blob: &[u8], n: usize, rate: u32, secs: u32, t: u8, seed: u64) -> (World, Vec<u64>) {
    let m = MapView::new(blob).unwrap();
    let mut w = World::new(&m, rate);
    w.hazard_seed = seed;
    for s in 0..n {
        w.add_player(&m, s, true);
        set_tier(&mut w, s, t);
    }
    // a real race round (RaceRules): every bot must finish (target = n), no intro / countdown
    let mut ev = StepEvents::default();
    crate::rules::begin_round(&mut w, &m, 0, n as u8, (1u16 << n) - 1, 0, 0, &mut ev);
    let mut hashes = Vec::new();
    for _ in 0..rate * secs {
        ev.n = 0;
        step(&mut w, &[None; 12], &m, &MovementParams::default(), &mut NoHasher, &mut ev);
        if crate::rules::is_over(&w) {
            break;
        }
        hashes.push(w.hash());
    }
    (w, hashes)
}

#[test]
fn race_bot_follows_course_to_finish() {
    let b = build(&lane_spec(false, false));
    for rate in [15, 20, 30] {
        let (w, _) = run(&b, 4, rate, 20, tier::REFERENCE, 1);
        for s in 0..4 {
            assert_eq!(w.players[s].r.checkpoint, 3, "{rate} Hz slot {s} reached {} checkpoints at {:?}", w.players[s].r.checkpoint, w.players[s].m.pos);
        }
    }
}

#[test]
fn route_bots_jump_the_gap() {
    let b = build(&lane_spec(true, true));
    for rate in [15, 20, 30] {
        let (w, _) = run(&b, 6, rate, 25, tier::REFERENCE, 2);
        let done = (0..6).filter(|&s| w.players[s].r.checkpoint == 3).count();
        let falls: u32 = (0..6).map(|s| w.players[s].falls as u32).sum();
        assert!(done >= 5, "{rate} Hz: {done}/6 finished, {falls} falls");
    }
}

#[test]
fn tiered_bots_finish_and_are_deterministic() {
    let b = build(&lane_spec(true, true));
    let (w1, h1) = run(&b, 12, 20, 30, tier::AUTO, 77);
    let (_, h2) = run(&b, 12, 20, 30, tier::AUTO, 77);
    assert_eq!(h1, h2, "same seed → same world every tick");
    let done = (0..12).filter(|&s| w1.players[s].r.checkpoint == 3).count();
    assert!(done >= 8, "tiered: {done}/12 finished");
    let (_, h3) = run(&b, 12, 20, 30, tier::AUTO, 78);
    assert_ne!(h1.last(), h3.last(), "another seed differs");
    // tiers rolled 30/50/20 over many slots/seeds
    let mut cnt = [0u32; 5];
    for seed in 0..200u64 {
        for s in 0..12 {
            cnt[roll_tier(seed, s) as usize] += 1;
        }
    }
    // of 2,400: Easy 30 % ± 4 %, Normal 50 % ± 4 %
    assert!((624..=816).contains(&cnt[1]) && (1104..=1296).contains(&cnt[2]), "{cnt:?}");
}

#[test]
fn bot_state_is_26_bytes() {
    let mut buf = [0u8; 64];
    let mut e = crate::codec::Enc::new(&mut buf);
    BotState::default().enc(&mut e);
    assert_eq!(e.n, 26);
}

#[test]
fn idle_outside_rush() {
    let b = build(&lane_spec(false, false));
    let m = MapView::new(&b).unwrap();
    let mut w = World::new(&m, 20);
    w.add_player(&m, 0, true);
    w.round.mode = mode::RACE;
    w.round.phase = crate::rules::phase::COUNTDOWN;
    let sc = crate::collide::Scene::new(m);
    assert_eq!(bot_input(0, &mut w, &m, &sc), InputCmd::NEUTRAL);
}

/// A 9 × 9 lid grid (lid 1.8 m, top y = 0) over a kill plane, Final mode.
fn lid_spec() -> MapSpec {
    use crate::hazards::defs::{HazardType, LidGridDef};
    let mut d = LidGridDef {
        n: 9,
        lid: 1800,
        gap: 60,
        thickness: 400,
        lit_ticks: 30,
        flipped_ticks: 45,
        first_wave_ticks: 90,
        wave_interval_count: 4,
        waves_per_stage: 8,
        flip_permille_count: 4,
        ring_retire_count: 3,
        retire_warn_ticks: 90,
        min_inner_solid: 5,
        ..Default::default()
    };
    d.wave_intervals[..4].copy_from_slice(&[90, 75, 60, 45]);
    d.flip_permille[..4].copy_from_slice(&[200, 250, 300, 350]);
    d.ring_retire_ticks[..3].copy_from_slice(&[750, 1500, 2250]);
    let mut s = flat_arena_spec();
    s.meta[meta::MODE] = mode::FINAL as i32;
    s.colliders.clear();
    for lid in 0..81 {
        let (x, y, z) = d.lid_center(lid);
        s.colliders.push(ColliderSpec { shape: aabb_box(IVec3::new(x, y, z), IVec3::new(900, 200, 900)), surface: -1, dyn_kind: crate::mapblob::dyn_kind::LID, dyn_index: lid as u16 });
    }
    let mut words = vec![0i32; HazardType::LidFlip.words()];
    d.write(&mut words);
    s.hazards[0].push(HazardSpec { ty: HazardType::LidFlip, phase_min: 0, phase_max: 65535, id: b"lid".to_vec(), words, baked: false });
    s.spawns = (0..12).map(|i| (IVec3::new(-5000 + 1000 * (i % 6), 10, -1000 + 2000 * (i / 6)), 0u8)).collect();
    s.meta[meta::KILL_Y] = -5000;
    s
}

#[test]
fn lid_bots_outlast_standing_players() {
    let b = build(&lid_spec());
    let m = MapView::new(&b).unwrap();
    for rate in [15, 20, 30] {
        let mut w = World::new(&m, rate);
        w.hazard_seed = 5;
        for s in 0..12 {
            w.add_player(&m, s, s < 6); // 0..5 reference bots, 6..11 stand still
            set_tier(&mut w, s, tier::REFERENCE);
            w.players[s].r.flags |= crate::rules::sf::IN;
        }
        w.round.mode = mode::FINAL;
        w.round.phase = crate::rules::phase::RUSH;
        let mut ev = StepEvents::default();
        for _ in 0..rate * 60 {
            ev.n = 0;
            step(&mut w, &[None; 12], &m, &MovementParams::default(), &mut NoHasher, &mut ev);
        }
        let bot_falls: u32 = (0..6).map(|s| w.players[s].falls as u32).sum();
        let idle_falls: u32 = (6..12).map(|s| w.players[s].falls as u32).sum();
        std::println!("{rate} Hz lid grid 60 s: bot falls {bot_falls}, standing falls {idle_falls}");
        assert!(bot_falls * 2 < idle_falls.max(1), "{rate} Hz: bots {bot_falls} vs standing {idle_falls}");
    }
}
