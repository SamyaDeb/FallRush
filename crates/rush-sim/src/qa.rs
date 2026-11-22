//! `rush-sim qa <blob> --runs N --rate R [--tier normal|reference|mixed] [--json]` (P1 prompt M.1) and
//! `rush-sim run --maps <dir> --seed <n> --rate R [--rounds all] [--json]` / `rush-sim soak --runs 20 --rate R` (M.2).
//!
//! QA: N seeded single rounds of the blob's mode with all-bot players: Race 12 (8 qualify), Survival 8 (5), Skill 5 (3),
//! Final 3 (1) — `RoundTargets(12)`. Variant alternates A / B by seed, modifier None. Per run: duration (RUSH → end),
//! qualified, timer ending, valid (the result has every participant once), soft-lock (a player in play that moved
//! < 0.5 m for 5 s; not counted on Final maps, where standing on a solid lid is the strategy), tunnelling (a tick-to-tick jump > the max course speed without a respawn), and determinism (the
//! same seed twice → the same World hash at every round end). Bots read only player-visible information.

use rush_core::config::MovementParams;
use rush_core::mapblob::{mode, MapView};
use rush_core::motor::flag;
use rush_core::rules::{self, sf};
use rush_core::world::{self, Hasher, StepEvents, World, MAX_PLAYERS};
use serde_json::json;

pub struct Sha;

impl Hasher for Sha {
    fn sha256(&mut self, parts: &[&[u8]]) -> [u8; 32] {
        use sha2::{Digest, Sha256};
        let mut h = Sha256::new();
        for p in parts {
            h.update(p);
        }
        h.finalize().into()
    }
}

#[derive(Clone, Debug, Default)]
#[allow(dead_code)]
pub struct RunResult {
    pub seed: u64,
    pub secs: f64,
    pub qualified: u32,
    pub players: u32,
    pub timer: bool,
    pub valid: bool,
    pub softlocks: u32,
    pub tunnels: u32,
    pub falls: u32,
    pub hits: u32,
    pub hash: u64,
    /// Skill: top score, 3rd-place score. Survival: escaped. Final: falls of the champion.
    pub extra: [i64; 3],
}

pub fn players_for(m: u8) -> (u32, u8) {
    match m {
        mode::SURVIVAL => (8, 5),
        mode::SKILL => (5, 3),
        mode::FINAL => (3, 1),
        _ => (12, 8),
    }
}

/// One single-round run.
pub fn run_round(blob: &[u8], seed: u64, rate: u32, tier: u8) -> RunResult {
    let m = MapView::new(blob).expect("blob");
    let md = m.mode() as u8;
    let (np, target) = players_for(md);
    let mut w = World::new(&m, rate);
    w.hazard_seed = seed.wrapping_mul(0x9E37_79B9_7F4A_7C15) ^ 0xFA11;
    w.variant = (seed % m.variants().max(1) as u64) as u8;
    w.modifier = 0;
    for s in 0..np as usize {
        w.add_player(&m, s, true);
        rush_core::bots::set_tier(&mut w, s, if tier == 255 { 0 } else { tier });
    }
    let mut ev = StepEvents::default();
    let parts = ((1u32 << np) - 1) as u16;
    rules::begin_round(&mut w, &m, 0, target, parts, rules::ROUND_INTRO_30, rules::ROUND_COUNTDOWN_30, &mut ev);
    let params = MovementParams::default();
    let cap = rate as u64 * 400; // 400 s hard cap
    let still = (rate * 5) as usize;
    let mut last = [w.players[0].m.pos; MAX_PLAYERS];
    let mut anchor = [w.players[0].m.pos; MAX_PLAYERS];
    let mut since = [0usize; MAX_PLAYERS];
    let mut stuck = [false; MAX_PLAYERS];
    let mut tunnels = 0;
    for i in 0..MAX_PLAYERS {
        last[i] = w.players[i].m.pos;
        anchor[i] = last[i];
    }
    // max legal per-tick displacement: 15 m/s course speed + launch / knock + fall cap 30 m/s → 35 m/s
    let max_step = 35_000i64 / rate as i64 + 50;
    let mut t = 0u64;
    while !rules::is_over(&w) && t < cap {
        ev.n = 0;
        world::step(&mut w, &[None; MAX_PLAYERS], &m, &params, &mut world::NoHasher, &mut ev);
        t += 1;
        if w.round.phase != rules::phase::RUSH {
            for i in 0..np as usize {
                last[i] = w.players[i].m.pos;
                anchor[i] = last[i];
                since[i] = 0;
            }
            continue;
        }
        for i in 0..np as usize {
            let p = &w.players[i];
            let pos = p.m.pos;
            let inplay = rules::simulates(&w, i);
            if inplay && p.m.flags & flag::RESPAWNING == 0 && p.last_step & (world::step_result::FELL_OUT | world::step_result::GOOED) == 0 {
                let d = pos.sub(last[i]);
                let dl = d.len();
                if dl > max_step && p.respawn_ticks == 0 && last[i] != w.spawn_state(&m, i).pos {
                    // a respawn teleport lands on a spawn / checkpoint spot; anything else is a tunnel
                    let respawned = (0..m.checkpoint_count()).any(|c| {
                        let cp = m.checkpoint(c);
                        (0..cp.respawn_count).any(|k| m.respawn(cp.respawn_start + k).0.sub(pos).len() < 1500)
                    }) || w.spawn_state(&m, i).pos.sub(pos).len() < 1500;
                    if !respawned {
                        tunnels += 1;
                    }
                }
            }
            last[i] = pos;
            if !inplay {
                since[i] = 0;
                continue;
            }
            if pos.sub(anchor[i]).len() > 500 {
                anchor[i] = pos;
                since[i] = 0;
            } else {
                since[i] += 1;
                if since[i] >= still && md != mode::FINAL {
                    stuck[i] = true;
                }
            }
        }
    }
    let r = &w.round.result;
    let mut seen = [false; MAX_PLAYERS];
    let mut valid = w.round.has_result && r.n as u32 == np;
    for k in 0..r.n as usize {
        let s = r.order[k] as usize;
        if s >= MAX_PLAYERS || seen[s] {
            valid = false;
        } else {
            seen[s] = true;
        }
    }
    let secs = (r.end_tick.saturating_sub(r.start_tick)) as f64 / rate as f64;
    let mut extra = [0i64; 3];
    match md {
        mode::SKILL => {
            let mut sc: Vec<i64> = (0..np as usize).map(|i| w.players[i].r.score as i64).collect();
            sc.sort_unstable_by(|a, b| b.cmp(a));
            extra = [sc[0], sc.get(2).copied().unwrap_or(0), sc.iter().sum()];
        }
        mode::SURVIVAL => {
            extra[0] = (0..np as usize).filter(|&i| w.players[i].r.has(sf::DONE)).count() as i64;
        }
        mode::FINAL => {
            extra[0] = r.order[0] as i64;
            extra[1] = w.players[r.order[0] as usize].r.lid_falls as i64;
        }
        _ => {}
    }
    RunResult {
        seed,
        secs,
        qualified: r.qualified_count() as u32,
        players: np,
        timer: r.timer_expired,
        valid,
        softlocks: stuck.iter().take(np as usize).filter(|x| **x).count() as u32,
        tunnels,
        falls: (0..np as usize).map(|i| w.players[i].falls as u32).sum(),
        hits: (0..np as usize).map(|i| w.players[i].hits as u32).sum(),
        hash: w.hash(),
        extra,
    }
}

fn median(v: &mut [f64]) -> f64 {
    if v.is_empty() {
        return 0.0;
    }
    v.sort_by(|a, b| a.partial_cmp(b).unwrap());
    let n = v.len();
    if n % 2 == 1 {
        v[n / 2]
    } else {
        (v[n / 2 - 1] + v[n / 2]) / 2.0
    }
}

fn p90(v: &mut [f64]) -> f64 {
    v.sort_by(|a, b| a.partial_cmp(b).unwrap());
    v[((v.len() as f64 - 1.0) * 0.9).round() as usize]
}

pub fn tier_arg(args: &[String]) -> u8 {
    match crate::arg(args, "--tier").as_deref() {
        Some("reference") => rush_core::bots::tier::REFERENCE,
        Some("mixed") => 255,
        Some("easy") => rush_core::bots::tier::EASY,
        Some("good") => rush_core::bots::tier::GOOD,
        _ => rush_core::bots::tier::NORMAL,
    }
}

pub fn qa(args: &[String]) -> i32 {
    let path = &args[1];
    let runs: u64 = crate::arg(args, "--runs").and_then(|v| v.parse().ok()).unwrap_or(100);
    let rate: u32 = crate::arg(args, "--rate").and_then(|v| v.parse().ok()).unwrap_or(20);
    let tier = tier_arg(args);
    let blob = std::fs::read(path).unwrap();
    let m = MapView::new(&blob).unwrap();
    let name = String::from_utf8_lossy(m.name()).to_string();
    let t0 = std::time::Instant::now();
    let res: Vec<RunResult> = (1..=runs).map(|s| run_round(&blob, s, rate, tier)).collect();
    // determinism: re-run the first 5 seeds
    let det = (1..=runs.min(5)).all(|s| run_round(&blob, s, rate, tier).hash == res[(s - 1) as usize].hash);
    let mut secs: Vec<f64> = res.iter().map(|r| r.secs).collect();
    let med = median(&mut secs.clone());
    let p9 = p90(&mut secs);
    let valid = res.iter().filter(|r| r.valid).count();
    let timer = res.iter().filter(|r| r.timer).count();
    let soft: u32 = res.iter().map(|r| r.softlocks).sum();
    let tun: u32 = res.iter().map(|r| r.tunnels).sum();
    let q = res.iter().map(|r| r.qualified as f64).sum::<f64>() / runs as f64;
    let ex0 = res.iter().map(|r| r.extra[0] as f64).sum::<f64>() / runs as f64;
    let ex1 = res.iter().map(|r| r.extra[1] as f64).sum::<f64>() / runs as f64;
    let falls = res.iter().map(|r| r.falls as f64).sum::<f64>() / runs as f64;
    let hits = res.iter().map(|r| r.hits as f64).sum::<f64>() / runs as f64;
    let j = json!({
        "map": name, "mode": m.mode(), "rate": rate, "runs": runs, "tier": tier,
        "median_s": med, "p90_s": p9, "qualified_avg": q, "players": res[0].players,
        "valid": valid, "timer_endings": timer, "softlocked_players": soft, "tunnels": tun, "deterministic": det,
        "extra0_avg": ex0, "extra1_avg": ex1, "falls_avg": falls, "hits_avg": hits,
        "wall_s": t0.elapsed().as_secs_f64(),
    });
    if args.iter().any(|a| a == "--json") {
        println!("{j}");
    } else {
        println!("{}", serde_json::to_string_pretty(&j).unwrap());
    }
    0
}

/// One full match through the director: lobby blob + round blobs by map id. Returns (game seconds, rounds played,
/// completed, transcript hash hex).
pub fn run_match(maps: &[(u8, Vec<u8>)], lobby: &[u8], seed: u64, rate: u32) -> (f64, u8, bool, String) {
    let lm = MapView::new(lobby).unwrap();
    let mut h = Sha;
    let mut w = World::new(&lm, rate);
    let mut s32 = [0u8; 32];
    s32[..8].copy_from_slice(&seed.to_le_bytes());
    let ids: Vec<String> = (0..12).map(|s| format!("bot:{s}")).collect();
    let e: Vec<rush_core::transcript::RosterEntry> = (0..12).map(|s| rush_core::transcript::RosterEntry { bot: true, user_id: ids[s].as_bytes(), wallet: None }).collect();
    let rh = rush_core::transcript::roster_hash(&mut h, &e);
    rules::director::start_match(&mut w, &lm, &s32, 12, &rh, &mut h);
    let params = MovementParams::default();
    let mut cur: Vec<u8> = lobby.to_vec();
    let t_start = w.tick;
    let mut ev = StepEvents::default();
    for _ in 0..rate as u64 * 1500 {
        if let Some(id) = rules::director::needs_map(&w) {
            cur = maps.iter().find(|(k, _)| *k == id).map(|x| x.1.clone()).unwrap_or_else(|| panic!("no blob for map id {id}"));
            let v = MapView::new(&cur).unwrap();
            rules::director::load_round(&mut w, &v, &mut h);
        }
        let v = MapView::new(&cur).unwrap();
        ev.n = 0;
        world::step(&mut w, &[None; MAX_PLAYERS], &v, &params, &mut h, &mut ev);
        if w.dir.phase >= rules::director::phase::DONE {
            break;
        }
    }
    let secs = (w.tick - t_start) as f64 / rate as f64;
    let hex: String = w.dir.transcript.iter().map(|b| format!("{b:02x}")).collect();
    (secs, w.dir.round, w.dir.phase == rules::director::phase::DONE, hex)
}

pub fn load_maps(dir: &std::path::Path) -> (Vec<(u8, Vec<u8>)>, Vec<u8>) {
    let mut maps = Vec::new();
    let mut lobby = Vec::new();
    for e in std::fs::read_dir(dir).unwrap().flatten() {
        let p = e.path();
        if p.extension().is_some_and(|x| x == "mapblob") {
            let b = std::fs::read(&p).unwrap();
            let v = MapView::new(&b).unwrap();
            if v.mode() as u8 == mode::LOBBY {
                lobby = b.clone();
            }
            maps.push((v.map_id(), b));
        }
    }
    (maps, lobby)
}

pub fn run(args: &[String]) -> i32 {
    let dir = crate::arg(args, "--maps").map(std::path::PathBuf::from).unwrap_or_else(|| crate::repo_root().join("maps"));
    let seed: u64 = crate::arg(args, "--seed").and_then(|v| u64::from_str_radix(v.trim_start_matches("0x"), 16).ok()).unwrap_or(1);
    let rate: u32 = crate::arg(args, "--rate").and_then(|v| v.parse().ok()).unwrap_or(20);
    let (maps, lobby) = load_maps(&dir);
    let (secs, rounds, done, hex) = run_match(&maps, &lobby, seed, rate);
    let j = json!({ "seed": seed, "rate": rate, "game_s": secs, "rounds": rounds, "complete": done, "transcript": hex });
    println!("{j}");
    if done {
        0
    } else {
        1
    }
}

pub fn soak(args: &[String]) -> i32 {
    let dir = crate::arg(args, "--maps").map(std::path::PathBuf::from).unwrap_or_else(|| crate::repo_root().join("maps"));
    let runs: u64 = crate::arg(args, "--runs").and_then(|v| v.parse().ok()).unwrap_or(20);
    let rate: u32 = crate::arg(args, "--rate").and_then(|v| v.parse().ok()).unwrap_or(20);
    let (maps, lobby) = load_maps(&dir);
    let mut lens = Vec::new();
    let mut ok = 0;
    for s in 1..=runs {
        let (secs, rounds, done, hex) = run_match(&maps, &lobby, s, rate);
        println!("match {s}: {secs:.1} s, {rounds} rounds, complete {done}, transcript {}", &hex[..16]);
        if done {
            ok += 1;
            lens.push(secs);
        }
    }
    println!("soak: {ok}/{runs} complete, median {:.1} s (min {:.1}, max {:.1})", median(&mut lens.clone()), lens.iter().cloned().fold(f64::MAX, f64::min), lens.iter().cloned().fold(0.0, f64::max));
    if ok == runs {
        0
    } else {
        1
    }
}
