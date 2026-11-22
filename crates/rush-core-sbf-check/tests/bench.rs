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

fn setup(m: &MapView, rate: u32, seed: u64, _scenario: &str) -> World {
    let mut w = World::new(m, rate);
    w.hazard_seed = seed;
    for s in 0..MAX_PLAYERS {
        w.add_player(m, s, true);
    }
    let ranked = m.mode() != mode::SANDBOX as i32 && m.mode() != mode::LOBBY as i32;
    let mut ev = StepEvents::default();
    if ranked {
        rush_core::rules::begin_round(&mut w, m, 0, 0, 0xFFF, rush_core::rules::ROUND_INTRO_30, rush_core::rules::ROUND_COUNTDOWN_30, &mut ev);
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
        let mut parity = true;
        let mut ev = StepEvents::default();
        let mut guard = 0u32;
        let mut codec_cu = 0u64;
        let mut max_prof = String::new();
        while cus.len() < (rate * secs) as usize && guard < rate * (secs + 40) {
            guard += 1;
            let profile = std::env::var("BENCH_PROFILE").is_ok();
            let what = if guard == 1 { 1u8 } else if profile { 3u8 } else { 0u8 };
            let ix = Instruction { program_id: pid, accounts: vec![AccountMeta::new_readonly(map_key, false), AccountMeta::new(world_key, false)], data: vec![0x20, what] };
            let msg = Message::new(&[ix], Some(&payer.pubkey()));
            let tx = Transaction::new(&[&payer], msg, svm.latest_blockhash());
            let meta = svm.send_transaction(tx).unwrap_or_else(|e| panic!("{stem} tick {}: {:?} {:?}", w.tick, e.err, e.meta.logs));
            svm.expire_blockhash();
            if what == 1 {
                codec_cu = meta.compute_units_consumed;
                continue;
            }
            ev.n = 0;
            world::step(&mut w, &[None; MAX_PLAYERS], &m, &params, &mut NoHasher, &mut ev);
            let acc = svm.get_account(&world_key).unwrap();
            let mut sw = World::new(&m, rate);
            if !sw.load_from(&acc.data) || sw.hash() != w.hash() {
                parity = false;
                eprintln!("{stem}: sBPF World differs from native at tick {}", w.tick);
                break;
            }
            let in_rush = !ranked || w.round.phase == rush_core::rules::phase::RUSH;
            if in_rush || scenario == "pileup" {
                let cu = meta.compute_units_consumed;
                cus.push(cu);
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
        let line = format!("| {stem} | {n} | {} | {} | {} | {max_tick} | {codec_cu} | {} |\n", pct(&mut cus.clone(), 0.5), pct(&mut cus.clone(), 0.95), max_cu, if parity { "OK" } else { "MISMATCH" });
        print!("{line}");
        report += &line;
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
