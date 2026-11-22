//! P2 0.2 primitive CU bench (LiteSVM): CU per call of the rush-core primitives on sBPF, and per collider shape.
//! Run (heavy): `scripts/p2-cu.sh ops` with `BENCH_TEST=ops`.

use litesvm::LiteSVM;
use rush_core::mapblob::MapView;
use solana_sdk::account::Account;
use solana_sdk::instruction::{AccountMeta, Instruction};
use solana_sdk::message::Message;
use solana_sdk::pubkey::Pubkey;
use solana_sdk::signature::{Keypair, Signer};
use solana_sdk::transaction::Transaction;

#[test]
fn cu_per_primitive() {
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
    let bytes = std::fs::read(root.join("crates/target/deploy/rush_core_sbf_check.so")).unwrap();
    let mut cb = solana_compute_budget::compute_budget::ComputeBudget::new_with_defaults(false, false);
    cb.compute_unit_limit = 200_000_000;
    let mut svm = LiteSVM::new().with_compute_budget(cb);
    let pid = Pubkey::new_unique();
    svm.add_program(pid, &bytes).unwrap();
    let payer = Keypair::new();
    svm.airdrop(&payer.pubkey(), 1_000_000_000_000).unwrap();
    let mut out = String::new();
    let maps = std::env::var("BENCH_MAPS").unwrap_or_else(|_| "CrumbleShelf,StarScramble,GooTower,BlockPartyDash,Lobby".into());
    for stem in maps.split(',') {
        let blob = std::fs::read(root.join(format!("maps/{stem}.mapblob"))).unwrap();
        let m = MapView::new(&blob).unwrap();
        let key = Pubkey::new_unique();
        svm.set_account(key, Account { lamports: 1_000_000_000, data: blob.clone(), owner: pid, executable: false, rent_epoch: 0 }).unwrap();
        let mut run = |op: u8, n: u16, arg: u32| -> u64 {
            let mut d = vec![0x21, op];
            d.extend_from_slice(&n.to_le_bytes());
            d.extend_from_slice(&arg.to_le_bytes());
            let ix = Instruction { program_id: pid, accounts: vec![AccountMeta::new_readonly(key, false)], data: d };
            let tx = Transaction::new(&[&payer], Message::new(&[ix], Some(&payer.pubkey())), svm.latest_blockhash());
            let r = svm.send_transaction(tx).unwrap_or_else(|e| panic!("op {op}: {:?}", e.err)).compute_units_consumed;
            svm.expire_blockhash();
            r
        };
        let base1 = run(0, 1, 0);
        let base = run(0, 101, 0);
        let per = |r: u64, n: u64| (r as f64 - base1 as f64 - (base - base1) as f64 * (n - 1) as f64 / 100.0) / n as f64;
        out += &format!("\n## {stem} ({} colliders)\n| op | CU / call |\n|---|---|\n", m.collider_count);
        let names = [(1, "div_round i64"), (2, "mul_div (i128)"), (3, "isqrt_i64"), (4, "normalize_q15"), (5, "map.collider(i) decode"), (7, "capsule_sweep down 100 mm at spawn"), (8, "raycast down 3 m at spawn"), (9, "penetration at spawn"), (10, "gather 1x2x1 m box at spawn"), (11, "capsule_sweep fwd 300 mm at spawn"), (12, "sin_q15"), (13, "i64 / i64"), (14, "u64 / u64"), (15, "i64 / 3 (const)"), (16, "idiv(x, 3)"), (17, "i32 / 10 (const)"), (18, "x*7/1000 (const)"), (19, "div_round(x, 1000)")];
        for (op, name) in names {
            let n: u16 = if op >= 7 && op <= 11 { 5 } else { 100 };
            let r = run(op, n, 0);
            out += &format!("| {name} | {:.0} |\n", per(r, n as u64));
        }
        // near() per shape kind: mean / max over every collider.
        let mut by: [(u64, u64, u64); 5] = [(0, 0, 0); 5];
        let mut flags_general = (0u64, 0u64);
        for i in 0..m.collider_count {
            let c = m.collider(i);
            let r = per(run(6, 1, i), 1) as u64;
            let e = &mut by[c.kind as usize % 5];
            e.0 += 1;
            e.1 += r;
            e.2 = e.2.max(r);
            if c.kind == rush_core::mapblob::shape::OBB && c.flags & 1 == 0 {
                flags_general.0 += 1;
                flags_general.1 = flags_general.1.max(r);
            }
        }
        out += "| near() by shape | count, mean, max |\n";
        for (k, e) in by.iter().enumerate() {
            if e.0 > 0 {
                out += &format!("| shape {k} | {} colliders, mean {}, max {} |\n", e.0, e.1 / e.0, e.2);
            }
        }
        out += &format!("| tilted (general) OBBs | {} colliders, max {} |\n", flags_general.0, flags_general.1);
    }
    println!("{out}");
    std::fs::write(root.join("docs/proof/p2/cu-ops.md"), &out).unwrap();
}
