//! P2 0.2 sBPF profile: runs ONE real tick (decode + step + encode) under LiteSVM register tracing and leaves the
//! trace in `$SBF_TRACE_DIR` for `tools/p2/sbf-prof.py` (PC histogram → functions of the unstripped .so).
//! Env: `PROF_MAP` (blob stem), `PROF_RUSH_TICKS` (ticks into RUSH before the traced tick, default 300).
//! Run (heavy): `SBF_TRACE_DIR=... cargo test --release -p rush-core-sbf-check --test prof -- --ignored --nocapture`.

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

#[test]
#[ignore]
fn profile_one_tick() {
    assert!(std::env::var("SBF_TRACE_DIR").is_ok(), "set SBF_TRACE_DIR");
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
    let bytes = std::fs::read(root.join("crates/target/deploy/rush_core_sbf_check.so")).unwrap();
    let stem = std::env::var("PROF_MAP").unwrap_or_else(|_| "CrumbleShelf".into());
    let after: u32 = std::env::var("PROF_RUSH_TICKS").ok().and_then(|v| v.parse().ok()).unwrap_or(300);
    let blob = std::fs::read(root.join(format!("maps/{stem}.mapblob"))).unwrap();
    let m = MapView::new(&blob).unwrap();
    let mut w = World::new(&m, 30);
    w.hazard_seed = 0x51ED ^ stem.len() as u64;
    for s in 0..MAX_PLAYERS {
        w.add_player(&m, s, true);
    }
    let mut ev = StepEvents::default();
    let ranked = m.mode() != mode::SANDBOX as i32 && m.mode() != mode::LOBBY as i32;
    if ranked {
        rush_core::rules::begin_round(&mut w, &m, 0, 0, 0xFFF, rush_core::rules::ROUND_INTRO_30, rush_core::rules::ROUND_COUNTDOWN_30, &mut ev);
    }
    let params = MovementParams::default();
    let mut in_rush = 0;
    while in_rush < after {
        ev.n = 0;
        world::step(&mut w, &[None; MAX_PLAYERS], &m, &params, &mut NoHasher, &mut ev);
        if !ranked || w.round.phase == rush_core::rules::phase::RUSH {
            in_rush += 1;
        }
    }
    let mut cb = solana_compute_budget::compute_budget::ComputeBudget::new_with_defaults(false, false);
    cb.compute_unit_limit = 200_000_000;
    let mut svm = LiteSVM::new().with_compute_budget(cb);
    let pid = Pubkey::new_unique();
    svm.add_program(pid, &bytes).unwrap();
    let payer = Keypair::new();
    svm.airdrop(&payer.pubkey(), 1_000_000_000_000).unwrap();
    let (mk, wk) = (Pubkey::new_unique(), Pubkey::new_unique());
    svm.set_account(mk, Account { lamports: 1_000_000_000, data: blob.clone(), owner: pid, executable: false, rent_epoch: 0 }).unwrap();
    let mut wb = vec![0u8; 4096];
    let n = w.to_bytes(&mut wb);
    wb.truncate(n);
    svm.set_account(wk, Account { lamports: 1_000_000_000, data: wb, owner: pid, executable: false, rent_epoch: 0 }).unwrap();
    let ix = Instruction { program_id: pid, accounts: vec![AccountMeta::new_readonly(mk, false), AccountMeta::new(wk, false)], data: vec![0x20, 0] };
    let tx = Transaction::new(&[&payer], Message::new(&[ix], Some(&payer.pubkey())), svm.latest_blockhash());
    let meta = svm.send_transaction(tx).unwrap();
    println!("{stem} tick {} CU {}", w.tick + 1, meta.compute_units_consumed);
}
