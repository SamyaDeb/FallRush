//! P6 tests on the real `fall_rush.so` (LiteSVM 0.9.1): sealed Champion bets (commit → close → reveal → finish →
//! settle; tampering, double reveal, reveal before close, forfeits, refunds on abort, payouts = hand calculation),
//! the private book's `PICK` (session key; the TEE permission CPIs run on devnet only: no Permission Program here),
//! seasons (receipt bets hash → `RECORD` from the committed copies, idempotency, achievements, boards, test season
//! close + trophy mint, weekly-season rules).
//! Run (heavy, after `anchor build`): `cargo test --offline --release -p rush-core-sbf-check --test p6 -- --nocapture`.

use litesvm::LiteSVM;
use solana_sdk::account::Account;
use solana_sdk::clock::Clock;
use solana_sdk::hash::hashv;
use solana_sdk::instruction::{AccountMeta, Instruction};
use solana_sdk::message::Message;
use solana_sdk::pubkey::Pubkey;
use solana_sdk::signature::{Keypair, Signer};
use solana_sdk::transaction::Transaction;

const PID: Pubkey = Pubkey::from_str_const("4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4");
const SESSION: Pubkey = Pubkey::from_str_const("KeyspM2ssCJbqUhQ4k7sveSiY4WjnYsrXkC8oDbwde5");
const CORE: Pubkey = Pubkey::from_str_const("CoREENxT6tW1HoK8ypY1SxRMZTcVPm7R94rH4PZNhX7d");
const SYS: Pubkey = Pubkey::from_str_const("11111111111111111111111111111111");
const ONE: u64 = 1_000_000;
const ARENA_ID: u8 = 9;
const NOW: i64 = 1_795_442_400; // 2026-11-23 14:00 UTC (ISO 2026-W48)

mod op {
    pub const INIT_CHIPS: u8 = 1;
    pub const FAUCET: u8 = 2;
    pub const INIT_ECON: u8 = 5;
    pub const OPEN_MARKETS: u8 = 6;
    pub const PLACE_BET: u8 = 7;
    pub const SETTLE: u8 = 8;
    pub const BUY_DROP: u8 = 9;
    pub const WRITE_RECEIPT: u8 = 10;
}
mod p6 {
    pub const INIT: u8 = 1;
    pub const ESCROW_OPEN: u8 = 2;
    pub const COMMIT: u8 = 3;
    pub const PICK: u8 = 5;
    pub const CLOSE: u8 = 7;
    pub const REVEAL: u8 = 8;
    pub const FINISH: u8 = 9;
    pub const OPEN_SEASON: u8 = 10;
    pub const RECORD: u8 = 11;
    pub const INIT_PSEASON: u8 = 12;
    pub const INIT_PROFILE: u8 = 13;
    pub const CLOSE_SEASON: u8 = 14;
    pub const MINT_TROPHY: u8 = 15;
}

fn d8(pre: &str, name: &str) -> [u8; 8] {
    hashv(&[format!("{pre}:{name}").as_bytes()]).to_bytes()[..8].try_into().unwrap()
}
fn pda(seeds: &[&[u8]], prog: &Pubkey) -> Pubkey {
    Pubkey::find_program_address(seeds, prog).0
}
fn chips_of(w: &Pubkey) -> Pubkey {
    pda(&[b"chips", w.as_ref()], &PID)
}
fn w(k: Pubkey) -> AccountMeta {
    AccountMeta::new(k, false)
}
fn ro(k: Pubkey) -> AccountMeta {
    AccountMeta::new_readonly(k, false)
}
fn commitment(outcome: u8, amount: u64, salt: &[u8; 16], wallet: &Pubkey) -> [u8; 32] {
    solana_sdk::hash::hashv(&[&[outcome], &amount.to_le_bytes(), salt, wallet.as_ref()]).to_bytes()
}
fn salt_data(s: &[u8; 16]) -> [u8; 32] {
    let mut d = [0u8; 32];
    d[..16].copy_from_slice(s);
    d
}

// Match borsh offsets (discriminator included)
const M_STATUS: usize = 26;
const M_FLAGS: usize = 27;
const M_ROUND: usize = 129;
const M_SLOTS: usize = 214;
const M_CHAMP: usize = 294;
const M_RECEIPT: usize = 295;
const M_COMPLETED_AT: usize = 295 + 1089 + 16;
const ST_ROUND_ACTIVE: u8 = 3;
const ST_COMPLETED: u8 = 5;
const ST_ABORTED: u8 = 6;
const FINAL_MODE: u8 = 0x40;
const MK_STATUS: usize = 197;
// SealedEscrow (after the discriminator)
const E_N: usize = 88;
const E_REVEALED: usize = 89;
const E_STATE: usize = 90;
const E_NFORFEIT: usize = 91;

struct T {
    svm: LiteSVM,
    auth: Keypair,
    arena: Pubkey,
    world: Pubkey,
    dq: Pubkey,
    markets: [Pubkey; 5],
    maps: [Pubkey; 5],
    escrow: Pubkey,
    book: Pubkey,
    m: Pubkey,
}

impl T {
    fn new() -> T {
        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
        let mut svm = LiteSVM::new();
        svm.add_program(PID, &std::fs::read(root.join("programs/target/deploy/fall_rush.so")).expect("anchor build first")).unwrap();
        svm.add_program(CORE, &std::fs::read(root.join("crates/rush-core-sbf-check/tests/fixtures/mpl_core.so")).expect("scripts/p4-fixtures.sh")).unwrap();
        let auth = Keypair::new();
        svm.airdrop(&auth.pubkey(), 100_000_000_000).unwrap();
        let mut clk: Clock = svm.get_sysvar();
        clk.unix_timestamp = NOW;
        svm.set_sysvar(&clk);
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
        let dq = pda(&[b"drops", arena.as_ref()], &PID);
        let markets: [Pubkey; 5] = core::array::from_fn(|k| pda(&[b"market", &[ARENA_ID], &[k as u8]], &PID));
        let maps: [Pubkey; 5] = core::array::from_fn(|_| Pubkey::new_unique());
        for (k, mk) in maps.iter().enumerate() {
            let mut d = vec![0u8; 80];
            d[..8].copy_from_slice(b"FRMAPBLB");
            d[44] = 1;
            d[48..80].copy_from_slice(&[k as u8 + 1; 32]);
            svm.set_account(*mk, Account { lamports: 10_000_000, data: d, owner: PID, executable: false, rent_epoch: 0 }).unwrap();
        }
        let escrow = pda(&[b"sealed", arena.as_ref()], &PID);
        let book = pda(&[b"sbook", arena.as_ref()], &PID);
        let mut t = T { svm, auth, arena, world, dq, markets, maps, escrow, book, m: Pubkey::default() };
        t.m = t.create_match();
        t.write_arena(2, 0, &[]);
        t.write_world(0, 0, 0, 0);
        let cfgk = pda(&[b"config"], &PID);
        for k in 0..6u64 {
            let target = if k < 5 { t.markets[k as usize] } else { t.dq };
            let a = t.auth.insecure_clone();
            t.econ(&a, op::INIT_ECON, ARENA_ID as u64, k, vec![ro(cfgk), ro(t.arena), w(target), ro(SYS)]).unwrap();
        }
        let a = t.auth.insecure_clone();
        t.p6(&a, p6::INIT, ARENA_ID as u64, 0, [0; 32], vec![ro(cfgk), ro(t.arena), w(t.escrow), ro(SYS)]).unwrap();
        t.p6(&a, p6::INIT, ARENA_ID as u64, 1, [0; 32], vec![ro(cfgk), ro(t.arena), w(t.book), ro(SYS)]).unwrap();
        assert!(t.p6(&a, p6::INIT, ARENA_ID as u64, 0, [0; 32], vec![ro(cfgk), ro(t.arena), w(t.escrow), ro(SYS)]).is_err(), "init twice");
        t.open_markets();
        t
    }

    fn send(&mut self, ixs: &[Instruction], signers: &[&Keypair]) -> Result<u64, String> {
        let tx = Transaction::new(signers, Message::new(ixs, Some(&signers[0].pubkey())), self.svm.latest_blockhash());
        let r = self.svm.send_transaction(tx).map(|m| m.compute_units_consumed).map_err(|e| format!("{:?} {}", e.err, e.meta.logs.join(" | ").chars().rev().take(300).collect::<String>().chars().rev().collect::<String>()));
        self.svm.expire_blockhash();
        r
    }
    fn econ(&mut self, signer: &Keypair, o: u8, a: u64, b: u64, accs: Vec<AccountMeta>) -> Result<u64, String> {
        let mut data = d8("global", "econ").to_vec();
        data.push(o);
        data.extend_from_slice(&a.to_le_bytes());
        data.extend_from_slice(&b.to_le_bytes());
        let mut metas = vec![AccountMeta::new(signer.pubkey(), true)];
        metas.extend(accs);
        self.send(&[Instruction { program_id: PID, accounts: metas, data }], &[signer])
    }
    fn p6(&mut self, signer: &Keypair, o: u8, a: u64, b: u64, d: [u8; 32], accs: Vec<AccountMeta>) -> Result<u64, String> {
        let mut data = d8("global", "p6").to_vec();
        data.push(o);
        data.extend_from_slice(&a.to_le_bytes());
        data.extend_from_slice(&b.to_le_bytes());
        data.extend_from_slice(&d);
        let mut metas = vec![AccountMeta::new(signer.pubkey(), true)];
        metas.extend(accs);
        self.send(&[Instruction { program_id: PID, accounts: metas, data }], &[signer])
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
    fn open_markets(&mut self) {
        let mut accs = vec![ro(self.arena), w(self.dq)];
        accs.extend(self.markets.iter().map(|k| w(*k)));
        accs.push(w(self.m));
        let a = self.auth.insecure_clone();
        self.econ(&a, op::OPEN_MARKETS, 0, 0, accs).unwrap();
        self.p6(&a, p6::ESCROW_OPEN, 0, 0, [0; 32], vec![ro(self.arena), w(self.escrow), ro(self.markets[0]), ro(self.m)]).unwrap();
    }
    fn write_arena(&mut self, status: u8, human_mask: u16, wallets: &[(usize, Pubkey)]) {
        let mut d = vec![0u8; 1432];
        d[..8].copy_from_slice(&d8("account", "Arena"));
        d[8..40].copy_from_slice(self.auth.pubkey().as_ref());
        d[40..72].copy_from_slice(self.m.as_ref());
        for (k, mk) in self.maps.iter().enumerate() {
            d[72 + 32 * k..104 + 32 * k].copy_from_slice(mk.as_ref());
        }
        for (s, wk) in wallets {
            d[616 + 32 * s..648 + 32 * s].copy_from_slice(wk.as_ref());
        }
        d[1408] = ARENA_ID;
        d[1409] = status;
        d[1412..1414].copy_from_slice(&human_mask.to_le_bytes());
        self.svm.set_account(self.arena, Account { lamports: 100_000_000, data: d, owner: PID, executable: false, rent_epoch: 0 }).unwrap();
    }
    fn write_world(&mut self, tick: u32, round: u8, round_phase: u8, round_start: u32) {
        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
        let lobby = std::fs::read(root.join("maps/Lobby.mapblob")).unwrap();
        let v = rush_core::mapblob::MapView::new(&lobby).unwrap();
        let mut wd = rush_core::world::World::new(&v, 30);
        wd.tick = tick;
        wd.round_start = round_start;
        let mut wb = [0u8; 2048];
        let n = wd.to_bytes(&mut wb);
        let mut d = vec![0u8; 2256];
        d[..8].copy_from_slice(&d8("account", "WorldAcc"));
        d[8..40].copy_from_slice(self.arena.as_ref());
        d[128..132].copy_from_slice(&tick.to_le_bytes());
        d[132..136].copy_from_slice(&(n as u32).to_le_bytes());
        d[138] = 5;
        d[139] = round;
        d[142..144].copy_from_slice(&2u16.to_le_bytes());
        d[199] = round_phase;
        d[208..208 + n].copy_from_slice(&wb[..n]);
        self.svm.set_account(self.world, Account { lamports: 100_000_000, data: d, owner: PID, executable: false, rent_epoch: 0 }).unwrap();
    }
    fn patch_match(&mut self, f: impl FnOnce(&mut Vec<u8>)) {
        let mut a = self.svm.get_account(&self.m).unwrap();
        f(&mut a.data);
        self.svm.set_account(self.m, a).unwrap();
    }
    fn wallet(&mut self) -> Keypair {
        let k = Keypair::new();
        self.svm.airdrop(&k.pubkey(), 10_000_000_000).unwrap();
        self.econ(&k, op::INIT_CHIPS, 0, 0, vec![w(chips_of(&k.pubkey())), ro(SYS)]).unwrap();
        self.econ(&k, op::FAUCET, 0, 0, vec![w(chips_of(&k.pubkey()))]).unwrap();
        k
    }
    fn balance(&self, wk: &Pubkey) -> u64 {
        let d = self.svm.get_account(&chips_of(wk)).unwrap().data;
        u64::from_le_bytes(d[40..48].try_into().unwrap())
    }
    fn commit(&mut self, who: &Keypair, amount: u64, hash: [u8; 32]) -> Result<u64, String> {
        let accs = vec![w(chips_of(&who.pubkey())), w(self.escrow), w(self.markets[0]), ro(self.m), ro(self.arena), ro(self.world)];
        self.p6(who, p6::COMMIT, amount, 0, hash, accs)
    }
    fn close(&mut self) -> Result<u64, String> {
        let a = self.auth.insecure_clone();
        let accs = vec![w(self.escrow), ro(self.markets[0]), ro(self.m), ro(self.arena), ro(self.world)];
        self.p6(&a, p6::CLOSE, 0, 0, [0; 32], accs)
    }
    fn reveal(&mut self, idx: u64, outcome: u64, salt: &[u8; 16]) -> Result<u64, String> {
        let x = Keypair::new(); // permissionless: any payer
        self.svm.airdrop(&x.pubkey(), 1_000_000_000).unwrap();
        let accs = vec![w(self.escrow), w(self.markets[0]), ro(self.m)];
        self.p6(&x, p6::REVEAL, idx, outcome, salt_data(salt), accs)
    }
    fn finish(&mut self) -> Result<u64, String> {
        let a = self.auth.insecure_clone();
        let accs = vec![w(self.escrow), w(self.markets[0]), ro(self.m)];
        self.p6(&a, p6::FINISH, 0, 0, [0; 32], accs)
    }
    fn settle(&mut self, k: usize, chips: &[Pubkey]) -> Result<u64, String> {
        let mut accs = vec![w(self.markets[k]), ro(self.m)];
        accs.extend(chips.iter().map(|x| w(chips_of(x))));
        let a = self.auth.insecure_clone();
        self.econ(&a, op::SETTLE, 0, 0, accs)
    }
    fn esc(&self, off: usize) -> u8 {
        self.svm.get_account(&self.escrow).unwrap().data[8 + off]
    }
    fn warp(&mut self, secs: i64) {
        let mut c: Clock = self.svm.get_sysvar();
        c.unix_timestamp += secs;
        self.svm.set_sysvar(&c);
    }
    /// Rounds 1–3 done, finalists 3, 7, 9 (others out in round 2), the on-chain Final in countdown.
    fn to_final(&mut self) {
        self.patch_match(|d| { d[M_ROUND] = 4; d[M_STATUS] = ST_ROUND_ACTIVE; d[M_FLAGS] |= FINAL_MODE; for s in 0..12 { if ![3usize, 7, 9].contains(&s) { d[M_SLOTS + 4 * s] = 3; d[M_SLOTS + 4 * s + 1] = 2; } } });
        self.write_world(500, 4, 2, 0);
    }
}

fn session_token(t: &mut T, wallet: &Pubkey, signer: &Pubkey, valid_until: i64) -> Pubkey {
    let k = pda(&[b"session_token_v2", PID.as_ref(), signer.as_ref(), wallet.as_ref()], &SESSION);
    let mut d = d8("account", "SessionTokenV2").to_vec();
    d.extend_from_slice(wallet.as_ref());
    d.extend_from_slice(PID.as_ref());
    d.extend_from_slice(signer.as_ref());
    d.extend_from_slice(wallet.as_ref());
    d.extend_from_slice(&valid_until.to_le_bytes());
    t.svm.set_account(k, Account { lamports: 10_000_000, data: d, owner: SESSION, executable: false, rent_epoch: 0 }).unwrap();
    k
}

#[test]
fn sealed_champion_bets_happy_path_tampering_forfeits() {
    let mut t = T::new();
    let (a, b, c, d) = (t.wallet(), t.wallet(), t.wallet(), t.wallet());
    let (ak, bk, ck, dk) = (a.pubkey(), b.pubkey(), c.pubkey(), d.pubkey());
    // an open bet (the market maker) next to the sealed ones
    let accs = vec![w(chips_of(&dk)), w(t.markets[0]), ro(t.m), ro(t.arena), ro(t.world)];
    t.econ(&d, op::PLACE_BET, 7, 30 * ONE, accs).unwrap();
    // sealed commits: a 100 on 3, b 50 on 7, c 40 on 3 (c never reveals), a again 20 on 9
    let (sa, sb, sc, sa2) = ([1u8; 16], [2u8; 16], [3u8; 16], [4u8; 16]);
    let a0 = t.balance(&ak);
    let cu = t.commit(&a, 100 * ONE, commitment(3, 100 * ONE, &sa, &ak)).unwrap();
    assert_eq!(a0 - t.balance(&ak), 100 * ONE, "the stake is debited at commit");
    t.commit(&b, 50 * ONE, commitment(7, 50 * ONE, &sb, &bk)).unwrap();
    t.commit(&c, 40 * ONE, commitment(3, 40 * ONE, &sc, &ck)).unwrap();
    t.commit(&a, 20 * ONE, commitment(9, 20 * ONE, &sa2, &ak)).unwrap();
    assert!(t.commit(&a, ONE / 2, [9; 32]).is_err(), "below 1 RUSH");
    assert!(t.commit(&a, 5_000 * ONE, [9; 32]).is_err(), "more than the balance");
    // the commitment hides the pick: the Market's stakes show only the open bet
    let mk = t.svm.get_account(&t.markets[0]).unwrap().data;
    assert_eq!(u64::from_le_bytes(mk[8 + 64..8 + 72].try_into().unwrap()), 30 * ONE, "pool = open bets only while sealed");
    assert_eq!(mk[8 + 202], 1, "market flagged sealed-pending");
    // reveal before close: refused
    assert!(t.reveal(0, 3, &sa).is_err(), "no reveal before close");
    assert!(t.close().is_err(), "close only once the Champion window closed");
    t.to_final();
    t.commit(&b, 10 * ONE, commitment(7, 10 * ONE, &[5u8; 16], &bk)).unwrap(); // still open in the countdown
    t.write_world(501, 4, 3, 501); // the RUSH tick
    assert!(t.commit(&b, 10 * ONE, [1; 32]).is_err(), "sealed commits close at the RUSH tick");
    t.close().unwrap();
    assert!(t.close().is_err(), "close once");
    // tampering: wrong salt / wrong outcome / wrong entry
    assert!(t.reveal(0, 3, &[9u8; 16]).is_err(), "wrong salt");
    assert!(t.reveal(0, 7, &sa).is_err(), "wrong outcome");
    assert!(t.reveal(1, 3, &sa).is_err(), "another bettor's entry");
    assert!(t.reveal(0, 12, &sa).is_err(), "outcome beyond the roster");
    let rcu = t.reveal(0, 3, &sa).unwrap();
    assert!(t.reveal(0, 3, &sa).is_err(), "double reveal");
    t.reveal(1, 7, &sb).unwrap();
    t.reveal(3, 9, &sa2).unwrap();
    t.reveal(4, 7, &[5u8; 16]).unwrap();
    assert_eq!(t.esc(E_REVEALED), 4);
    // finish with c unrevealed: only after the window AND the end of the match
    assert!(t.finish().is_err(), "pending entry, window open, match running");
    t.warp(25);
    assert!(t.finish().is_err(), "match still running");
    t.patch_match(|d| { d[M_STATUS] = ST_COMPLETED; d[M_CHAMP] = 3; });
    assert!(t.settle(0, &[ak, bk, ck, dk]).is_err(), "settle refused while sealed bets are pending");
    t.finish().unwrap();
    assert!(t.reveal(2, 3, &sc).is_err(), "reveal after finish refused");
    assert_eq!((t.esc(E_STATE), t.esc(E_NFORFEIT)), (3, 1));
    let (a1, b1, c1, d1) = (t.balance(&ak), t.balance(&bk), t.balance(&ck), t.balance(&dk));
    t.settle(0, &[ak, bk, ck, dk]).unwrap();
    // pool = open 30 (d on 7) + revealed 100 (a on 3) + 50 + 10 (b on 7) + 20 (a on 9) + forfeited 40 (c) = 250;
    // winning stake (slot 3) = 100 → a gets 100·250/100 = 250; b, c, d nothing
    assert_eq!(t.balance(&ak) - a1, 250 * ONE, "a: hand calculation");
    assert_eq!((t.balance(&bk), t.balance(&ck), t.balance(&dk)), (b1, c1, d1));
    assert_eq!(t.svm.get_account(&t.markets[0]).unwrap().data[8 + MK_STATUS], 3, "paid");
    println!("sealed: commit {cu} CU, reveal {rcu} CU; tamper / double / early / late reveals refused; forfeit to the pool; payouts = hand calc: OK");
}

#[test]
fn sealed_refunds_on_abort_and_book_pick() {
    let mut t = T::new();
    let (a, b) = (t.wallet(), t.wallet());
    let (ak, bk) = (a.pubkey(), b.pubkey());
    let a0 = t.balance(&ak);
    t.commit(&a, 60 * ONE, commitment(4, 60 * ONE, &[1; 16], &ak)).unwrap();
    t.commit(&b, 25 * ONE, commitment(5, 25 * ONE, &[2; 16], &bk)).unwrap();
    // aborted before the Final: close, then finish after the reveal window refunds (nobody revealed)
    t.patch_match(|d| { d[M_STATUS] = ST_ABORTED; });
    assert!(t.finish().is_err(), "not closed yet");
    t.close().unwrap(); // an aborted match closes the Champion window
    assert!(t.finish().is_err(), "unrevealed: waits for the reveal window");
    t.warp(21);
    t.finish().unwrap();
    t.settle(0, &[ak, bk]).unwrap();
    assert_eq!(t.balance(&ak), a0, "aborted: the sealed stake comes back");
    assert_eq!(t.esc(E_N), 2);
    // the private book (TEE in production): PICK by the wallet's session key only, into an open book
    let s = Keypair::new();
    t.svm.airdrop(&s.pubkey(), 1_000_000_000).unwrap();
    let tok = session_token(&mut t, &ak, &s.pubkey(), NOW + 3600);
    let pick = |t: &mut T, who: &Keypair, wallet: Pubkey, tok: Option<Pubkey>| {
        let mut accs = vec![w(t.book), ro(wallet)];
        if let Some(k) = tok { accs.push(ro(k)); }
        let wk = who.insecure_clone();
        t.p6(&wk, p6::PICK, 3, 10 * ONE, salt_data(&[7; 16]), accs)
    };
    assert!(pick(&mut t, &s, ak, Some(tok)).is_err(), "book not opened (no match)");
    let mut bkacc = t.svm.get_account(&t.book).unwrap();
    bkacc.data[8 + 32..8 + 64].copy_from_slice(t.m.as_ref());
    t.svm.set_account(t.book, bkacc).unwrap();
    assert!(pick(&mut t, &b, ak, None).is_err(), "someone else cannot pick for a");
    pick(&mut t, &s, ak, Some(tok)).unwrap();
    pick(&mut t, &a, ak, None).unwrap();
    let d = t.svm.get_account(&t.book).unwrap().data;
    assert_eq!(d[8 + 64], 2, "two picks");
    assert_eq!(&d[8 + 72..8 + 104], ak.as_ref());
    assert_eq!(d[8 + 72 + 56], 3, "outcome");
    let mut bkacc = t.svm.get_account(&t.book).unwrap();
    bkacc.data[8 + 65] = 1; // closed
    t.svm.set_account(t.book, bkacc).unwrap();
    assert!(pick(&mut t, &a, ak, None).is_err(), "closed book");
    println!("sealed: abort refunds; book picks by wallet / session key only, closed book refuses: OK");
}

#[test]
fn seasons_record_achievements_trophy() {
    let mut t = T::new();
    let (a, b, c) = (t.wallet(), t.wallet(), t.wallet());
    let (ak, bk, ck) = (a.pubkey(), b.pubkey(), c.pubkey());
    // humans: a in slot 3 (wins the Final), b in slot 7 (finalist); c a spectator who bets twice and wins both
    t.write_arena(2, (1 << 3) | (1 << 7), &[(3, ak), (7, bk)]);
    let bet = |t: &mut T, who: &Keypair, k: usize, o: u64, amt: u64| {
        let accs = vec![w(chips_of(&who.pubkey())), w(t.markets[k]), ro(t.m), ro(t.arena), ro(t.world)];
        let wk = who.insecure_clone();
        t.econ(&wk, op::PLACE_BET, o, amt, accs)
    };
    bet(&mut t, &c, 0, 3, 40 * ONE).unwrap();
    bet(&mut t, &b, 0, 7, 60 * ONE).unwrap();
    t.to_final();
    bet(&mut t, &c, 1, 3, 10 * ONE).unwrap();
    t.write_world(1000, 4, 3, 700); // RUSH: a buys a Chaos Drop
    let accs = vec![w(chips_of(&ak)), w(t.dq), ro(t.arena), ro(t.world)];
    t.econ(&a, op::BUY_DROP, 1, 0, accs).unwrap();
    t.close().unwrap();
    t.patch_match(|d| { d[M_STATUS] = ST_COMPLETED; d[M_CHAMP] = 3; d[M_COMPLETED_AT..M_COMPLETED_AT + 8].copy_from_slice(&(NOW + 60).to_le_bytes()); });
    t.finish().unwrap();
    for k in 0..5 {
        t.settle(k, &[ak, bk, ck]).unwrap();
    }
    let mut accs = vec![w(t.m), ro(t.arena), ro(t.world), ro(t.dq)];
    accs.extend(t.markets.iter().map(|k| ro(*k)));
    accs.extend(t.maps.iter().map(|k| ro(*k)));
    accs.push(ro(t.escrow));
    let au = t.auth.insecure_clone();
    t.econ(&au, op::WRITE_RECEIPT, 0, 0, accs).unwrap();
    let r = t.svm.get_account(&t.m).unwrap().data;
    assert_ne!(&r[M_RECEIPT + 737..M_RECEIPT + 769], &[0u8; 32], "bets hash in the receipt");
    t.warp(120);
    // seasons: the weekly one (anyone), a 2-hour test season (authority only)
    let cfgk = pda(&[b"config"], &PID);
    let week = 202648u32;
    let sw = pda(&[b"season", &week.to_le_bytes()], &PID);
    t.p6(&c, p6::OPEN_SEASON, week as u64, 0, [0; 32], vec![ro(cfgk), w(sw), ro(SYS)]).unwrap();
    let wrong = pda(&[b"season", &202650u32.to_le_bytes()], &PID);
    assert!(t.p6(&c, p6::OPEN_SEASON, 202650, 0, [0; 32], vec![ro(cfgk), w(wrong), ro(SYS)]).is_err(), "only the current / next week");
    let test_id = 1_000_001u32;
    let st = pda(&[b"season", &test_id.to_le_bytes()], &PID);
    assert!(t.p6(&c, p6::OPEN_SEASON, test_id as u64, 7200, [0; 32], vec![ro(cfgk), w(st), ro(SYS)]).is_err(), "test seasons: authority only");
    t.warp(-120); // the test season starts before the match ended
    t.p6(&au, p6::OPEN_SEASON, test_id as u64, 7200, [0; 32], vec![ro(cfgk), w(st), ro(SYS)]).unwrap();
    t.warp(120);
    let ps = |id: u32, x: &Pubkey| pda(&[b"pseason", &id.to_le_bytes(), x.as_ref()], &PID);
    for x in [ak, bk, ck] {
        for id in [week, test_id] {
            t.p6(&au, p6::INIT_PSEASON, id as u64, 0, [0; 32], vec![w(ps(id, &x)), ro(x), ro(SYS)]).unwrap();
        }
    }
    let prof = pda(&[b"profile", ak.as_ref()], &PID);
    t.p6(&au, p6::INIT_PROFILE, 0, 0, [0; 32], vec![w(prof), ro(ak), ro(SYS)]).unwrap();
    let record = |t: &mut T, season: Pubkey, id: u32, who: &[Pubkey]| {
        let mut accs = vec![ro(t.m), w(season), ro(t.dq)];
        accs.extend(t.markets.iter().map(|k| ro(*k)));
        accs.push(w(prof));
        accs.extend(who.iter().map(|x| w(ps(id, x))));
        let x = Keypair::new();
        t.svm.airdrop(&x.pubkey(), 1_000_000_000).unwrap();
        t.p6(&x, p6::RECORD, 0, 0, [0; 32], accs)
    };
    // a tampered committed copy (a bet amount changed) is refused
    let saved = t.svm.get_account(&t.markets[1]).unwrap();
    let mut bad = saved.clone();
    bad.data[8 + 208 + 32] ^= 1;
    t.svm.set_account(t.markets[1], bad).unwrap();
    assert!(record(&mut t, sw, week, &[ak, bk, ck]).is_err(), "copies must match the receipt's bets hash");
    t.svm.set_account(t.markets[1], saved).unwrap();
    let rcu = record(&mut t, sw, week, &[ak, bk, ck]).unwrap();
    record(&mut t, sw, week, &[ak, bk, ck]).unwrap(); // idempotent
    record(&mut t, st, test_id, &[ak]).unwrap();
    let p = |t: &T, id: u32, x: &Pubkey| t.svm.get_account(&ps(id, x)).unwrap().data;
    let (pa, pb, pc) = (p(&t, week, &ak), p(&t, week, &bk), p(&t, week, &ck));
    let u16at = |d: &Vec<u8>, o: usize| u16::from_le_bytes(d[8 + o..8 + o + 2].try_into().unwrap());
    let i64at = |d: &Vec<u8>, o: usize| i64::from_le_bytes(d[8 + o..8 + o + 8].try_into().unwrap());
    let ach = |d: &Vec<u8>| u32::from_le_bytes(d[8 + 36..8 + 40].try_into().unwrap());
    assert_eq!((u16at(&pa, 48), u16at(&pa, 50), u16at(&pa, 54), u16at(&pa, 60)), (1, 1, 1, 1), "a: 1 crown, 1 final, 1 match, 1 drop");
    assert_eq!(ach(&pa) & 0b11011, 0b11011, "a: first crown + 3-round survivor + chaos agent + crown");
    assert_eq!(u16at(&pb, 50), 1, "b: a finalist");
    assert_eq!(ach(&pb) & 1, 0, "b: no crown");
    // b bet 60 on 7 (lost): net -60. c: Champion 40 on 3 → 40·100/40 = 100 (net +60); FinalWinner 10 on 3 → 10 (net 0)
    assert_eq!(i64at(&pb, 40), -60 * ONE as i64);
    assert_eq!(i64at(&pc, 40), 60 * ONE as i64);
    assert_eq!((u16at(&pc, 56), u16at(&pc, 58)), (2, 0));
    assert_eq!(ach(&pc) & 4, 4, "c: perfect predictor (2 bets, all won)");
    let s = t.svm.get_account(&sw).unwrap().data;
    assert_eq!(u32::from_le_bytes(s[8 + 24..8 + 28].try_into().unwrap()), 1, "1 match counted once");
    assert_eq!(&s[8 + 32..8 + 64], ak.as_ref(), "top champion = a");
    assert_eq!(&s[8 + 432..8 + 464], ck.as_ref(), "top predictor = c");
    assert_eq!(&s[8 + 832..8 + 864], ak.as_ref(), "top drop MVP = a");
    // close + trophies (test season: 2 h)
    let tre = pda(&[b"treasury"], &PID);
    t.svm.airdrop(&tre, 1_000_000_000).unwrap();
    assert!(t.p6(&c, p6::CLOSE_SEASON, 0, 0, [0; 32], vec![w(st)]).is_err(), "not before ends_at");
    t.warp(7200);
    t.p6(&c, p6::CLOSE_SEASON, 0, 0, [0; 32], vec![w(st)]).unwrap();
    let trophy = |k: u8| pda(&[b"trophy", st.as_ref(), &[k]], &PID);
    let mint = |t: &mut T, k: u8, owner: Pubkey| {
        let accs = vec![w(st), w(trophy(k)), ro(owner), w(tre), ro(pda(&[b"assets"], &PID)), ro(CORE), ro(SYS)];
        let x = Keypair::new();
        t.svm.airdrop(&x.pubkey(), 1_000_000_000).unwrap();
        t.p6(&x, p6::MINT_TROPHY, k as u64, 0, [0; 32], accs)
    };
    assert!(mint(&mut t, 0, bk).is_err(), "trophy only to the top 1");
    let tcu = mint(&mut t, 0, ak).unwrap();
    assert!(mint(&mut t, 0, ak).is_err(), "once");
    assert!(mint(&mut t, 1, ak).is_err(), "nobody on the test season's predictor board");
    let ta = t.svm.get_account(&trophy(0)).unwrap();
    assert_eq!(ta.owner, CORE);
    assert_eq!(&ta.data[1..33], ak.as_ref());
    assert!(String::from_utf8_lossy(&ta.data).contains("Season Trophy (Champion)"));
    assert!(record(&mut t, st, test_id, &[bk]).is_err(), "closed season refuses records");
    println!("seasons: record {rcu} CU (3 wallets), idempotent, tampered copies refused, achievements, boards; test season closed, trophy {tcu} CU: OK");
}

/// harden #12 (4): `start_final` (hybrid) refreshes the WorldAcc round phase, so the stale ENDED of the last match
/// does not make the Final look rushed: sealed Champion commits, plain Champion and FinalWinner bets are accepted
/// before the first tick, and `CLOSE` (permissionless) is refused until the RUSH tick.
#[test]
fn start_final_round_phase_keeps_final_window_open() {
    let mut t = T::new();
    let (a, b) = (t.wallet(), t.wallet());
    let ak = a.pubkey();
    // a real lobby blob in the arena's lobby slot (start_final resets the World on it)
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
    let blob = std::fs::read(root.join("maps/Lobby.mapblob")).unwrap();
    let mut d = vec![0u8; 80];
    d[..8].copy_from_slice(b"FRMAPBLB");
    d[40..44].copy_from_slice(&(blob.len() as u32).to_le_bytes());
    d[44] = 1;
    d[48..80].copy_from_slice(&[5u8; 32]);
    d.extend_from_slice(&blob);
    t.svm.set_account(t.maps[4], Account { lamports: 1_000_000_000, data: d, owner: PID, executable: false, rent_epoch: 0 }).unwrap();
    // Classic rounds 1–3 done: finalists 3, 7, 9; arena in LOBBY; the WorldAcc still holds the last match (ENDED)
    t.patch_match(|d| { d[M_ROUND] = 3; d[M_STATUS] = 4; for s in 0..12 { if ![3usize, 7, 9].contains(&s) { d[M_SLOTS + 4 * s] = 3; d[M_SLOTS + 4 * s + 1] = 2; } } });
    t.write_arena(1, 0, &[]);
    t.write_world(9000, 4, 4, 8000);
    let mut data = d8("global", "start_final").to_vec();
    data.extend_from_slice(&[3, 7, 9]);
    let ix = Instruction { program_id: PID, accounts: vec![AccountMeta::new_readonly(t.auth.pubkey(), true), w(t.arena), w(t.world), w(t.m), ro(t.maps[4])], data };
    let au = t.auth.insecure_clone();
    t.send(&[ix], &[&au]).unwrap();
    let wd = t.svm.get_account(&t.world).unwrap().data;
    assert_eq!((wd[139], wd[199]), (4, 0), "round 4, round phase WAITING (not the stale ENDED)");
    // before the first tick: the Final's windows are open
    t.commit(&a, 10 * ONE, commitment(3, 10 * ONE, &[1u8; 16], &ak)).unwrap();
    let accs = vec![w(chips_of(&b.pubkey())), w(t.markets[1]), ro(t.m), ro(t.arena), ro(t.world)];
    t.econ(&b, op::PLACE_BET, 7, 5 * ONE, accs).unwrap();
    let accs = vec![w(chips_of(&b.pubkey())), w(t.markets[0]), ro(t.m), ro(t.arena), ro(t.world)];
    t.econ(&b, op::PLACE_BET, 9, 5 * ONE, accs).unwrap();
    assert!(t.close().is_err(), "sealed window not closable before RUSH");
    // the RUSH tick closes them as before
    t.write_world(9001, 4, 3, 9001);
    assert!(t.commit(&a, 10 * ONE, [1; 32]).is_err(), "closed at RUSH");
    t.close().unwrap();
    println!("start_final: round_phase WAITING; sealed commit + Champion + FinalWinner open until RUSH: OK");
}
