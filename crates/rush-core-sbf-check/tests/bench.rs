//! P2 0.2 CU bench (LiteSVM): one transaction per tick, each running decode + `step()` + encode of a real `World`
//! (12 bots) on a real map blob, exactly like the on-chain `tick()` will. Checks the sBPF World hash against a native
//! step every tick and reports CU per tick p50 / p95 / max per map.
//! Run (heavy): `scripts/p2-cu.sh` (cargo build-sbf + `cargo test --release -p rush-core-sbf-check --test bench`).
//! Env: `BENCH_SECS` (default 120 s of RUSH), `BENCH_RATE` (default 30), `BENCH_MAPS` (comma list of blob stems),
//! `BENCH_SCENARIO` (`round` default, or `pileup`), `BENCH_OUT` (report path).

use litesvm::LiteSVM;
use rush_core::config::MovementParams;
use rush_core::mapblob::{mode, MapView};
use rush_core::world::{self, NoHasher, StepEvents, World, MAX_PLAYERS};
use solana_sdk::account::Account;
use solana_sdk::instruction::{AccountMeta, Instruction};
use solana_sdk::message::Message;
use solana_sdk::pubkey::Pubkey;
use solana_sdk::signature::{Keypair, Signer};
use solana_sdk::transaction::Transaction;

fn pct(v: &mut [u64], p: f64) -> u64 {
    v.sort_unstable();
    if v.is_empty() {
        return 0;
    }
    v[((v.len() - 1) as f64 * p).round() as usize]
}

/// Pile-up inputs: every active player runs (and jumps now and then) toward the first player's spawn + 6 m, so they
/// all crowd one spot (12 × 4 bytes; [0,0,0,0xFF] = no input).
fn pile_bytes(w: &World) -> Vec<u8> {
    let mut out = vec![0u8; MAX_PLAYERS * 4];
    let target = (w.players[0].m.pos.x as i64 + 0, w.players[0].m.pos.z as i64 + 0);
    let _ = target;
    for k in 0..MAX_PLAYERS {
        let p = &w.players[k];
        if !p.active() {
            out[k * 4 + 3] = 0xFF;
            continue;
        }
        // a fixed point near the arena centre (map origin) on every map
        let (dx, dz) = (-(p.m.pos.x as i64), -(p.m.pos.z as i64));
        let l = ((dx * dx + dz * dz) as f64).sqrt().max(1.0);
        out[k * 4] = ((dx as f64 / l) * 127.0) as i8 as u8;
        out[k * 4 + 1] = ((dz as f64 / l) * 127.0) as i8 as u8;
        out[k * 4 + 2] = if (w.tick + k as u32) % 23 == 0 { 3 } else { 0 };
    }
    out
}

/// "tag:+CU" deltas between consecutive marks of one profiled tick.
fn profile_of(logs: &[String]) -> String {
    let mut out = String::new();
    let mut tag: Option<u64> = None;
    let mut last: Option<u64> = None;
    for l in logs {
        if let Some(rest) = l.strip_prefix("Program log: 0xc0, ") {
            tag = u64::from_str_radix(rest.split(',').next().unwrap().trim().trim_start_matches("0x"), 16).ok();
        } else if let Some(rest) = l.strip_prefix("Program consumption: ") {
            let rem: u64 = rest.split_whitespace().next().unwrap().parse().unwrap();
            if let (Some(t), Some(p)) = (tag, last) {
                let name = match t { 1 => "dir".to_string(), 2 => "scene".to_string(), 3 => "sep".to_string(), 4 => "end".to_string(), 16..=31 => format!("bot{}", t - 16), _ => format!("mot{}", t - 32) };
                out += &format!("{name}:{} ", p - rem);
            }
            last = Some(rem);
        }
    }
    out
}

fn setup(m: &MapView, rate: u32, seed: u64, scenario: &str) -> World {
    let mut w = World::new(m, rate);
    w.hazard_seed = seed;
    let np: usize = std::env::var("BENCH_PLAYERS").ok().and_then(|v| v.parse().ok()).unwrap_or(MAX_PLAYERS);
    for s in 0..np {
        // pile-up: human slots (driven by the harness), else bots
        w.add_player(m, s, scenario != "pileup");
    }
    let ranked = m.mode() != mode::SANDBOX as i32 && m.mode() != mode::LOBBY as i32;
    let mut ev = StepEvents::default();
    if ranked {
        let mask = ((1u32 << np) - 1) as u16;
        let target = if np <= 3 { 1 } else { 0 };
        rush_core::rules::begin_round(&mut w, m, 0, target, mask, rush_core::rules::ROUND_INTRO_30, rush_core::rules::ROUND_COUNTDOWN_30, &mut ev);
    }
    w
}

#[test]
fn cu_per_tick_real_maps() {
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
    let so = root.join("crates/target/deploy/rush_core_sbf_check.so");
    let bytes = std::fs::read(&so).unwrap_or_else(|e| panic!("{}: {e} (run cargo build-sbf first)", so.display()));
    let secs: u32 = std::env::var("BENCH_SECS").ok().and_then(|v| v.parse().ok()).unwrap_or(120);
    let rate: u32 = std::env::var("BENCH_RATE").ok().and_then(|v| v.parse().ok()).unwrap_or(30);
    let scenario = std::env::var("BENCH_SCENARIO").unwrap_or_else(|_| "round".into());
    let maps = std::env::var("BENCH_MAPS").unwrap_or_else(|_| "CrumbleShelf,StarScramble,GooTower,BlockPartyDash,Lobby".into());
    let mut cb = solana_compute_budget::compute_budget::ComputeBudget::new_with_defaults(false, false);
    cb.compute_unit_limit = 200_000_000; // measure, don't cap (the task budget is checked in the report)
    let mut svm = LiteSVM::new().with_compute_budget(cb);
    let pid = Pubkey::new_unique();
    svm.add_program(pid, &bytes).unwrap();
    let payer = Keypair::new();
    svm.airdrop(&payer.pubkey(), 1_000_000_000_000).unwrap();
    let params = MovementParams::default();
    let mut report = format!("scenario {scenario}, {rate} Hz, up to {secs} s, 12 bots, CU per tick = decode + step + encode (LiteSVM {})\n\n| map | ticks | CU p50 | CU p95 | CU max | max at tick | codec only | parity |\n|---|---|---|---|---|---|---|---|\n", "0.9.1");
    let mut all_max = 0u64;
    for stem in maps.split(',') {
        let blob = std::fs::read(root.join(format!("maps/{stem}.mapblob"))).unwrap();
        let m = MapView::new(&blob).unwrap();
        let mut w = setup(&m, rate, 0x51ED ^ stem.len() as u64, &scenario);
        let map_key = Pubkey::new_unique();
        let world_key = Pubkey::new_unique();
        svm.set_account(map_key, Account { lamports: 1_000_000_000, data: blob.clone(), owner: pid, executable: false, rent_epoch: 0 }).unwrap();
        let mut wb = vec![0u8; 4096];
        let n = w.to_bytes(&mut wb);
        wb.truncate(n);
        svm.set_account(world_key, Account { lamports: 1_000_000_000, data: wb, owner: pid, executable: false, rent_epoch: 0 }).unwrap();
        let ranked = m.mode() != mode::SANDBOX as i32 && m.mode() != mode::LOBBY as i32;
        let mut cus = Vec::new();
        let (mut max_cu, mut max_tick) = (0u64, 0u32);
        let (mut drop_max, mut drop_ticks, mut drop_cus) = (0u64, 0u32, Vec::new());
        let mut parity = true;
        let mut ev = StepEvents::default();
        let mut guard = 0u32;
        let mut codec_cu = 0u64;
        let mut max_prof = String::new();
        let mut slices_total = 0u64;
        while cus.len() < (rate * secs) as usize && guard < rate * (secs + 40) {
            guard += 1;
            let profile = std::env::var("BENCH_PROFILE").is_ok();
            let parts: u8 = std::env::var("BENCH_PARTS").ok().and_then(|v| v.parse().ok()).unwrap_or(1);
            let slice: u16 = std::env::var("BENCH_SLICE").ok().and_then(|v| v.parse().ok()).unwrap_or(0);
            let pile = scenario == "pileup";
            let what = if guard == 1 { 1u8 } else if pile { 6u8 } else if slice > 0 { 5u8 } else if parts > 1 { 4u8 } else if profile { 3u8 } else { 0u8 };
            // split tick: one transaction per part; the tick's CU is the most expensive part
            let mut meta_cu = 0u64;
            let mut meta = None;
            let mut from = 0u8;
            let mut calls = 0;
            for part in 0..(if what == 4 { parts } else if what == 5 { 64 } else { 1 }) {
                let b = slice.to_le_bytes();
                let data = if what == 6 { let mut d = vec![0x20, 6]; d.extend_from_slice(&pile_bytes(&w)); d } else if what == 5 { vec![0x20, 5, from, b[0], b[1]] } else { vec![0x20, what, part, parts] };
                let ix = Instruction { program_id: pid, accounts: vec![AccountMeta::new_readonly(map_key, false), AccountMeta::new(world_key, false)], data };
                let msg = Message::new(&[ix], Some(&payer.pubkey()));
                let tx = Transaction::new(&[&payer], msg, svm.latest_blockhash());
                let m = svm.send_transaction(tx).unwrap_or_else(|e| panic!("{stem} tick {}: {:?} {:?}", w.tick, e.err, e.meta.logs));
                svm.expire_blockhash();
                if let Ok(path) = std::env::var("BENCH_CAL") {
                    if let Some(l) = m.logs.iter().find(|l| l.starts_with("Program log: 0xbeef, ")) {
                        let v: Vec<u64> = l["Program log: 0xbeef, ".len()..].split(", ").map(|x| u64::from_str_radix(x.trim_start_matches("0x"), 16).unwrap_or(0)).collect();
                        use std::io::Write;
                        let mut f = std::fs::OpenOptions::new().create(true).append(true).open(&path).unwrap();
                        writeln!(f, "{stem} {} {} {} {}", m.compute_units_consumed, v[0], v[1], v[2]).unwrap();
                    }
                }
                calls += 1;
                let next = m.return_data.data.first().copied();
                if m.compute_units_consumed >= meta_cu {
                    meta_cu = m.compute_units_consumed;
                    meta = Some(m);
                }
                if what == 5 {
                    from = next.unwrap_or(0);
                    if from == 0 {
                        break;
                    }
                }
            }
            if what == 5 {
                slices_total += calls;
            }
            let meta = meta.unwrap();
            if what == 1 {
                codec_cu = meta.compute_units_consumed;
                continue;
            }
            ev.n = 0;
            if what == 6 {
                let b = pile_bytes(&w);
                let mut inputs = [None; MAX_PLAYERS];
                for (k, inp) in inputs.iter_mut().enumerate() {
                    let q = &b[k * 4..k * 4 + 4];
                    if q != [0, 0, 0, 0xFF] {
                        *inp = Some(rush_core::input::InputCmd { move_x: q[0] as i8, move_y: q[1] as i8, buttons: q[2], cam_yaw: (q[3] as u16) << 8 });
                    }
                }
                world::step(&mut w, &inputs, &m, &params, &mut NoHasher, &mut ev);
            } else if what == 5 {
                let mut sc = rush_core::collide::Scene::new(m);
                let mut f = 0u8;
                loop {
                    f = world::step_slice(&mut w, &[None; MAX_PLAYERS], &m, &params, &mut NoHasher, &mut ev, &mut sc, f, slice as u32);
                    if f == 0 {
                        break;
                    }
                }
            } else if what == 4 {
                let mut sc = rush_core::collide::Scene::new(m);
                for part in 0..parts {
                    world::step_part(&mut w, &[None; MAX_PLAYERS], &m, &params, &mut NoHasher, &mut ev, &mut sc, part, parts);
                }
            } else {
                world::step(&mut w, &[None; MAX_PLAYERS], &m, &params, &mut NoHasher, &mut ev);
            }
            let acc = svm.get_account(&world_key).unwrap();
            let mut sw = World::new(&m, rate);
            if !sw.load_from(&acc.data) || sw.hash() != w.hash() {
                parity = false;
                eprintln!("{stem}: sBPF World differs from native at tick {}", w.tick);
                break;
            }
            // P4 ⛳ CU with Chaos Drops (BENCH_DROPS=1): a stress schedule above the program's caps (upper bound):
            // Lid Frenzy + Bumper Rain + Wind Gust on one trigger 2 s into RUSH, then Sweeper Rush + Lid Frenzy + Wind
            // once the sweeper runs; queued as `tick()` does (trigger = next tick + TELEGRAPH) and written back into
            // the account so native and sBPF stay identical
            if std::env::var("BENCH_DROPS").is_ok() && ranked && w.round.phase == rush_core::rules::phase::RUSH {
                use rush_core::drops::{self, kind};
                let t30 = w.tick as i64 - w.round_start as i64;
                let plan: &[(i64, u8)] = &[(60, kind::LID_FRENZY), (60, kind::BUMPER_RAIN), (60, kind::WIND_GUST), (640, kind::SWEEPER_RUSH), (640, kind::LID_FRENZY), (640, kind::WIND_GUST), (900, kind::BUMPER_RAIN)];
                let mut changed = false;
                for (k, &(at, kd)) in plan.iter().enumerate() {
                    if t30 == at {
                        let trig = w.tick + 1 + drops::TELEGRAPH;
                        let arg = drops::arg_for(w.hazard_seed, k as u8, kd);
                        changed |= drops::enqueue(&mut w.drop_queue, kd, arg, trig);
                    }
                }
                if changed {
                    let mut wb = vec![0u8; 4096];
                    let n = w.to_bytes(&mut wb);
                    wb.truncate(n);
                    svm.set_account(world_key, Account { lamports: 1_000_000_000, data: wb, owner: pid, executable: false, rent_epoch: 0 }).unwrap();
                }
            }
            let in_rush = !ranked || w.round.phase == rush_core::rules::phase::RUSH;
            if in_rush || scenario == "pileup" {
                let cu = meta.compute_units_consumed;
                cus.push(cu);
                let active = (0..rush_core::world::DROP_QUEUE).any(|i| {
                    let q = w.drop_queue[i];
                    q.kind != 0 && w.tick >= q.tick && w.tick < q.tick + rush_core::drops::duration(q.kind).max(1)
                });
                if active {
                    drop_max = drop_max.max(cu);
                    drop_ticks += 1;
                    drop_cus.push(cu);
                }
                if cu > max_cu {
                    max_cu = cu;
                    max_tick = w.tick;
                    if what == 3 {
                        max_prof = profile_of(&meta.logs);
                    }
                }
            }
            if ranked && rush_core::rules::is_over(&w) {
                break;
            }
        }
        let n = cus.len();
        let line = format!("| {stem} | {n} | {} | {} | {} | {max_tick} | {codec_cu} | {} | {:.2} |\n", pct(&mut cus.clone(), 0.5), pct(&mut cus.clone(), 0.95), max_cu, if parity { "OK" } else { "MISMATCH" }, slices_total as f64 / guard.max(1) as f64);
        print!("{line}");
        report += &line;
        if drop_ticks > 0 {
            let l2 = format!("  with a Chaos Drop active: {drop_ticks} ticks, CU p50 {} p95 {} max {drop_max}\n", pct(&mut drop_cus.clone(), 0.5), pct(&mut drop_cus.clone(), 0.95));
            print!("{l2}");
            report += &l2;
        }
        all_max = all_max.max(max_cu);
        if !max_prof.is_empty() {
            report += &format!("  profile of the max tick ({stem}): {max_prof}\n");
        }
        assert!(parity, "{stem}: parity");
    }
    report += &format!("\nmax over maps: {all_max}\n");
    if let Ok(out) = std::env::var("BENCH_OUT") {
        std::fs::write(out, &report).unwrap();
    }
    println!("{report}");
}
