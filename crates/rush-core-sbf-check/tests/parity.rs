//! sBPF parity harness (LiteSVM): loads `target/deploy/rush_core_sbf_check.so` (built by `cargo build-sbf`), runs
//! every golden vector file in chunks and compares the digest with the file. Also records compute units per case.
//! Run: `scripts/p1-check.sh --sbf` (heavy, under the lock).

use litesvm::LiteSVM;
use solana_sdk::account::Account;
use solana_sdk::instruction::{AccountMeta, Instruction};
use solana_sdk::message::Message;
use solana_sdk::pubkey::Pubkey;
use solana_sdk::signature::{Keypair, Signer};
use solana_sdk::transaction::Transaction;

const CHUNK: [(u8, u32); 5] = [(1, 1000), (2, 200), (3, 50), (4, 1000), (5, 4)];

#[test]
fn sbf_matches_native() {
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
    let so = root.join("crates/target/deploy/rush_core_sbf_check.so");
    let bytes = std::fs::read(&so).unwrap_or_else(|e| panic!("{}: {e} (run cargo build-sbf first)", so.display()));
    // A large compute limit so long vector chunks run in one transaction (parity, not pricing); CU per case is reported.
    let mut cb = solana_compute_budget::compute_budget::ComputeBudget::new_with_defaults(false, false);
    cb.compute_unit_limit = 2_000_000_000;
    let mut svm = LiteSVM::new().with_compute_budget(cb);
    let pid = Pubkey::new_unique();
    svm.add_program(pid, &bytes).unwrap();
    let payer = Keypair::new();
    svm.airdrop(&payer.pubkey(), 1_000_000_000_000).unwrap();
    let dir = root.join("crates/rush-core/tests/vectors");
    let mut report = String::new();
    for name in ["fx_v1", "collide_v1", "motor_v1", "hazard_v2", "world_v1"] {
        let f = std::fs::read(dir.join(format!("{name}.bin"))).unwrap();
        let kind = f[4];
        let seed = u64::from_le_bytes(f[5..13].try_into().unwrap());
        let count = u32::from_le_bytes(f[13..17].try_into().unwrap());
        let digest = u64::from_le_bytes(f[17..25].try_into().unwrap());
        let ml = u32::from_le_bytes(f[25..29].try_into().unwrap()) as usize;
        let map_key = Pubkey::new_unique();
        svm.set_account(map_key, Account { lamports: 1_000_000_000, data: f[29..29 + ml].to_vec(), owner: pid, executable: false, rent_epoch: 0 }).unwrap();
        let chunk = CHUNK.iter().find(|c| c.0 == kind).unwrap().1;
        let mut state: u64 = 0xcbf2_9ce4_8422_2325;
        let (mut start, mut cu_total, mut cu_max) = (0u32, 0u64, 0u64);
        while start < count {
            let n = chunk.min(count - start);
            let mut data = vec![kind];
            data.extend_from_slice(&seed.to_le_bytes());
            data.extend_from_slice(&start.to_le_bytes());
            data.extend_from_slice(&n.to_le_bytes());
            data.extend_from_slice(&state.to_le_bytes());
            // ComputeBudgetInstruction::SetComputeUnitLimit(1.4M): tag 2 + u32 LE
            let mut cbd = vec![2u8];
            cbd.extend_from_slice(&1_400_000u32.to_le_bytes());
            let cb = Instruction { program_id: "ComputeBudget111111111111111111111111111111".parse().unwrap(), accounts: vec![], data: cbd };
            let ix = Instruction { program_id: pid, accounts: vec![AccountMeta::new_readonly(map_key, false)], data };
            let msg = Message::new(&[cb, ix], Some(&payer.pubkey()));
            let tx = Transaction::new(&[&payer], msg, svm.latest_blockhash());
            let meta = svm.send_transaction(tx).unwrap_or_else(|e| panic!("{name} chunk {start}: {:?}", e.err));
            state = u64::from_le_bytes(meta.return_data.data[..8].try_into().unwrap());
            cu_total += meta.compute_units_consumed;
            cu_max = cu_max.max(meta.compute_units_consumed);
            svm.expire_blockhash();
            start += n;
        }
        let line = format!("{name:10} {count:6} cases sbf {state:016x} file {digest:016x} {} — CU {cu_total} total, {:.0} per case, max chunk {cu_max}\n",
            if state == digest { "OK" } else { "MISMATCH" }, cu_total as f64 / count as f64);
        print!("{line}");
        report += &line;
        assert_eq!(state, digest, "{name}: sBPF digest differs from native");
    }
    std::fs::write(root.join("docs/proof/p1/sbf-parity.txt"), report).ok();
}
