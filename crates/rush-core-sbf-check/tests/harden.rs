//! harden #12 on the real `fall_rush.so` (LiteSVM 0.9.1), with the real map blobs: `start_sim` refreshes the
//! WorldAcc round phase (to the new World's), the director's phase changes mirror into the `Match`, and an aborted `Match` never ticks
//! again (a leaked task / anyone's `crank` cannot flip Aborted back to RoundActive or Completed).
//! Run (heavy, after `cargo build-sbf` of programs/fall_rush):
//! `cargo test --offline --release -p rush-core-sbf-check --test harden -- --nocapture`.

use litesvm::LiteSVM;
use solana_sdk::account::Account;
use solana_sdk::hash::hashv;
use solana_sdk::instruction::{AccountMeta, Instruction};
use solana_sdk::message::Message;
use solana_sdk::pubkey::Pubkey;
use solana_sdk::signature::{Keypair, Signer};
use solana_sdk::transaction::Transaction;

const PID: Pubkey = Pubkey::from_str_const("4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4");
const SYS: Pubkey = Pubkey::from_str_const("11111111111111111111111111111111");
const ARENA_ID: u8 = 7;

// Match borsh offsets (discriminator included)
const M_STATUS: usize = 26;
const M_VRF: usize = 170;
const ST_DEALT: u8 = 2;
const ST_ROUND_ACTIVE: u8 = 3;
const ST_ABORTED: u8 = 6;
// WorldAcc offsets (discriminator included)
const W_TICK: usize = 128;
const W_PHASE: usize = 138;
const W_LAST_CODE: usize = 141;
const W_ROUND_PHASE: usize = 199;
// director phases
const DP_ROSTER_LOCKED: u8 = 2;
const DP_ROUND_INTRO: u8 = 5;

fn d8(pre: &str, name: &str) -> [u8; 8] {
    hashv(&[format!("{pre}:{name}").as_bytes()]).to_bytes()[..8].try_into().unwrap()
}
fn pda(seeds: &[&[u8]], prog: &Pubkey) -> Pubkey {
    Pubkey::find_program_address(seeds, prog).0
}

struct T {
    svm: LiteSVM,
    auth: Keypair,
    arena: Pubkey,
    world: Pubkey,
    log: Pubkey,
    maps: [Pubkey; 5],
    inputs: [Pubkey; 12],
    m: Pubkey,
    slot: u64,
}

impl T {
    fn new() -> T {
        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
        let mut cb = solana_compute_budget::compute_budget::ComputeBudget::new_with_defaults(false, false);
        cb.compute_unit_limit = 1_400_000; // a keeper crank transaction
        let mut svm = LiteSVM::new().with_compute_budget(cb);
        svm.add_program(PID, &std::fs::read(root.join("programs/target/deploy/fall_rush.so")).expect("cargo build-sbf first")).unwrap();
        let auth = Keypair::new();
        svm.airdrop(&auth.pubkey(), 100_000_000_000).unwrap();
        let mut cfg = d8("account", "GameConfig").to_vec();
        cfg.push(1);
        cfg.push(Pubkey::find_program_address(&[b"config"], &PID).1);
        cfg.extend_from_slice(auth.pubkey().as_ref());
        cfg.extend_from_slice(auth.pubkey().as_ref());
        cfg.extend_from_slice(&1u16.to_le_bytes());
        cfg.extend_from_slice(&[0u8; 32]);
        cfg.extend_from_slice(&1u16.to_le_bytes());
        cfg.extend_from_slice(&[0u8; 32]);
        cfg.push(0);
        cfg.extend_from_slice(&0u64.to_le_bytes());
        svm.set_account(pda(&[b"config"], &PID), Account { lamports: 10_000_000, data: cfg, owner: PID, executable: false, rent_epoch: 0 }).unwrap();
        let arena = pda(&[b"arena", &[ARENA_ID]], &PID);
        let world = pda(&[b"world", arena.as_ref()], &PID);
        let log = pda(&[b"events", arena.as_ref()], &PID);
        // the real map blobs, each in its arena slot (deal map id 0..3, the lobby in slot 4)
        let maps: [Pubkey; 5] = core::array::from_fn(|_| Pubkey::new_unique());
        for stem in ["CrumbleShelf", "StarScramble", "GooTower", "BlockPartyDash", "Lobby"] {
            let blob = std::fs::read(root.join(format!("maps/{stem}.mapblob"))).unwrap();
            let id = rush_core::mapblob::MapView::new(&blob).unwrap().map_id();
            let k = if id < 4 { id as usize } else { 4 };
            let mut d = vec![0u8; 80];
            d[..8].copy_from_slice(b"FRMAPBLB");
            d[8..40].copy_from_slice(auth.pubkey().as_ref());
            d[40..44].copy_from_slice(&(blob.len() as u32).to_le_bytes());
            d[44] = 1;
            d[45] = id;
            d[48..80].copy_from_slice(&hashv(&[&blob]).to_bytes());
            d.extend_from_slice(&blob);
            svm.set_account(maps[k], Account { lamports: 1_000_000_000, data: d, owner: PID, executable: false, rent_epoch: 0 }).unwrap();
        }
        let inputs: [Pubkey; 12] = core::array::from_fn(|s| pda(&[b"input", &[ARENA_ID], &[s as u8]], &PID));
        for (s, k) in inputs.iter().enumerate() {
            let mut d = vec![0u8; 8 + 240];
            d[..8].copy_from_slice(&d8("account", "InputBuf"));
            d[8..40].copy_from_slice(arena.as_ref());
            d[8 + 104] = s as u8;
            svm.set_account(*k, Account { lamports: 10_000_000, data: d, owner: PID, executable: false, rent_epoch: 0 }).unwrap();
        }
        let mut ld = vec![0u8; 8 + 40 + 400 * 16];
        ld[..8].copy_from_slice(&d8("account", "EventLog"));
        ld[8..40].copy_from_slice(arena.as_ref());
        svm.set_account(log, Account { lamports: 100_000_000, data: ld, owner: PID, executable: false, rent_epoch: 0 }).unwrap();
        let mut t = T { svm, auth, arena, world, log, maps, inputs, m: Pubkey::default(), slot: 1000 };
        t.svm.warp_to_slot(t.slot);
        t.m = t.create_match();
        t
    }

    fn send(&mut self, ixs: &[Instruction], signers: &[&Keypair]) -> Result<u64, String> {
        let tx = Transaction::new(signers, Message::new(ixs, Some(&signers[0].pubkey())), self.svm.latest_blockhash());
        let r = self.svm.send_transaction(tx).map(|m| m.compute_units_consumed).map_err(|e| format!("{:?} {}", e.err, e.meta.logs.join(" | ").chars().rev().take(400).collect::<String>().chars().rev().collect::<String>()));
        self.svm.expire_blockhash();
        r
    }

    fn create_match(&mut self) -> Pubkey {
        let id: [u8; 16] = Pubkey::new_unique().to_bytes()[..16].try_into().unwrap();
        let mut data = d8("global", "create_match").to_vec();
        data.extend_from_slice(&id);
        data.extend_from_slice(&[7u8; 32]);
        data.push(12);
        let m = pda(&[b"match", &id], &PID);
        let ix = Instruction { program_id: PID, accounts: vec![AccountMeta::new(self.auth.pubkey(), true), AccountMeta::new(pda(&[b"config"], &PID), false), AccountMeta::new(m, false), AccountMeta::new_readonly(SYS, false)], data };
        let a = self.auth.insecure_clone();
        self.send(&[ix], &[&a]).unwrap();
        m
    }

    /// Arena in LOBBY for `self.m`: real maps, the InputBufs, all bots, 1 part, 10 ms slots.
    fn write_arena(&mut self, status: u8) {
        let mut d = vec![0u8; 1432];
        d[..8].copy_from_slice(&d8("account", "Arena"));
        d[8..40].copy_from_slice(self.auth.pubkey().as_ref());
        d[40..72].copy_from_slice(self.m.as_ref());
        for (k, mk) in self.maps.iter().enumerate() {
            d[72 + 32 * k..104 + 32 * k].copy_from_slice(mk.as_ref());
        }
        for (s, ik) in self.inputs.iter().enumerate() {
            d[1000 + 32 * s..1032 + 32 * s].copy_from_slice(ik.as_ref());
        }
        d[1408] = ARENA_ID;
        d[1409] = status;
        d[1410] = 1; // parts
        d[1426] = 10; // slot_ms
        self.svm.set_account(self.arena, Account { lamports: 100_000_000, data: d, owner: PID, executable: false, rent_epoch: 0 }).unwrap();
    }

    /// A WorldAcc left over from an earlier match: stale director phase / round / round phase (ENDED).
    fn write_stale_world(&mut self) {
        let mut d = vec![0u8; 2256];
        d[..8].copy_from_slice(&d8("account", "WorldAcc"));
        d[8..40].copy_from_slice(self.arena.as_ref());
        d[W_TICK..W_TICK + 4].copy_from_slice(&9000u32.to_le_bytes());
        d[W_PHASE] = 11;
        d[139] = 4;
        d[W_ROUND_PHASE] = 4; // ENDED
        self.svm.set_account(self.world, Account { lamports: 100_000_000, data: d, owner: PID, executable: false, rent_epoch: 0 }).unwrap();
    }

    fn patch_match(&mut self, f: impl FnOnce(&mut Vec<u8>)) {
        let mut a = self.svm.get_account(&self.m).unwrap();
        f(&mut a.data);
        self.svm.set_account(self.m, a).unwrap();
    }

    fn start_sim(&mut self) -> Result<u64, String> {
        let ix = Instruction {
            program_id: PID,
            accounts: vec![AccountMeta::new_readonly(self.auth.pubkey(), true), AccountMeta::new(self.arena, false), AccountMeta::new(self.world, false), AccountMeta::new(self.m, false), AccountMeta::new_readonly(self.maps[4], false)],
            data: d8("global", "start_sim").to_vec(),
        };
        let a = self.auth.insecure_clone();
        self.send(&[ix], &[&a])
    }

    /// One `crank(budget)` (permissionless: a stranger pays), 100 ms of rollup time later (always due).
    fn crank(&mut self, payer: &Keypair) -> Result<u64, String> {
        self.slot += 10;
        self.svm.warp_to_slot(self.slot);
        let mut accounts = vec![AccountMeta::new(self.arena, false), AccountMeta::new(self.world, false), AccountMeta::new(self.log, false), AccountMeta::new(self.m, false)];
        accounts.extend(self.maps.iter().map(|k| AccountMeta::new_readonly(*k, false)));
        accounts.extend(self.inputs.iter().map(|k| AccountMeta::new_readonly(*k, false)));
        let mut data = d8("global", "crank").to_vec();
        data.extend_from_slice(&6000u32.to_le_bytes());
        self.send(&[Instruction { program_id: PID, accounts, data }], &[payer])
    }

    fn abort_match(&mut self) -> Result<u64, String> {
        let mut data = d8("global", "abort_match").to_vec();
        data.push(1);
        let ix = Instruction { program_id: PID, accounts: vec![AccountMeta::new_readonly(self.auth.pubkey(), true), AccountMeta::new_readonly(pda(&[b"config"], &PID), false), AccountMeta::new(self.m, false)], data };
        let a = self.auth.insecure_clone();
        self.send(&[ix], &[&a])
    }

    fn wbyte(&self, off: usize) -> u8 {
        self.svm.get_account(&self.world).unwrap().data[off]
    }
    fn wtick(&self) -> u32 {
        let d = self.svm.get_account(&self.world).unwrap().data;
        u32::from_le_bytes(d[W_TICK..W_TICK + 4].try_into().unwrap())
    }
    fn mstatus(&self) -> u8 {
        self.svm.get_account(&self.m).unwrap().data[M_STATUS]
    }
}

#[test]
fn start_sim_round_phase_and_aborted_match_never_ticks() {
    let mut t = T::new();
    t.write_arena(1);
    t.write_stale_world();
    t.patch_match(|d| { d[M_STATUS] = ST_DEALT; d[M_VRF..M_VRF + 32].copy_from_slice(&[9u8; 32]); });
    t.start_sim().unwrap();
    // (4) start_sim: the WorldAcc round phase is the new World's (the lobby: SANDBOX), not the stale ENDED
    assert_eq!(t.wbyte(W_ROUND_PHASE), 5, "start_sim refreshes round_phase");
    assert_eq!(t.wbyte(W_PHASE), DP_ROSTER_LOCKED);
    // control: ticks run (anyone may crank) and the director's ROUND_INTRO is mirrored into the Match
    let x = Keypair::new();
    t.svm.airdrop(&x.pubkey(), 100_000_000_000).unwrap();
    let mut calls = 0;
    let mut max_cu = 0;
    while t.wbyte(W_PHASE) != DP_ROUND_INTRO {
        max_cu = max_cu.max(t.crank(&x).unwrap());
        assert_eq!(t.wbyte(W_LAST_CODE), 0, "tick ran");
        calls += 1;
        assert!(calls < 5000, "ROUND_INTRO not reached");
    }
    assert_eq!(t.mstatus(), ST_ROUND_ACTIVE, "director phase mirrored: RoundActive");
    for _ in 0..3 {
        t.crank(&x).unwrap();
    }
    // (3) abort (the keeper's Classic fallback), then a leaked task / anyone keeps cranking
    t.abort_match().unwrap();
    assert_eq!(t.mstatus(), ST_ABORTED);
    let tick0 = t.wtick();
    let world0 = t.svm.get_account(&t.world).unwrap().data[208..].to_vec();
    for _ in 0..200 {
        t.crank(&x).unwrap(); // never fails
        assert_eq!(t.wbyte(W_LAST_CODE), 2, "NOT_RUNNING");
    }
    assert_eq!(t.wtick(), tick0, "an aborted match does not tick");
    assert_eq!(t.svm.get_account(&t.world).unwrap().data[208..], world0[..], "World bytes untouched");
    assert_eq!(t.mstatus(), ST_ABORTED, "the Match stays Aborted");
    println!("start_sim round_phase = the new World's (SANDBOX); {calls} cranks to ROUND_INTRO (max {max_cu} CU) mirrored RoundActive; after abort_match 200 cranks: NOT_RUNNING, Match Aborted: OK");
}
