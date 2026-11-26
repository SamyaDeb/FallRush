//! P4 economy tests on the real `fall_rush.so` (LiteSVM 0.9.1): RUSH chips + faucet, Rush Markets (windows by
//! program state, shared-pool payouts vs a hand calculation, refunds, idempotency, hostile accounts), Chaos Drop
//! purchases (phase / caps / cooldown / on sale), the SPL cash-out, the receipt and the Metaplex Core rewards (the
//! devnet `mpl-core` program loaded from `tests/fixtures/mpl_core.so`, `scripts/p4-fixtures.sh`).
//! Run (heavy, after `anchor build`): `cargo test --offline --release -p rush-core-sbf-check --test econ -- --nocapture`.

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
const TOKEN: Pubkey = Pubkey::from_str_const("TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA");
const ATA: Pubkey = Pubkey::from_str_const("ATokenGPvbdGVxr1b2hvZbsiqW5xWH25efTNsLJA8knL");
const SYS: Pubkey = Pubkey::from_str_const("11111111111111111111111111111111");
const ONE: u64 = 1_000_000;
const ARENA_ID: u8 = 9;

mod op {
    pub const INIT_CHIPS: u8 = 1;
    pub const FAUCET: u8 = 2;
    pub const MINT_CASHOUT: u8 = 4;
    pub const INIT_ECON: u8 = 5;
    pub const OPEN_MARKETS: u8 = 6;
    pub const PLACE_BET: u8 = 7;
    pub const SETTLE: u8 = 8;
    pub const BUY_DROP: u8 = 9;
    pub const WRITE_RECEIPT: u8 = 10;
    pub const MINT_REWARDS: u8 = 11;
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

struct T {
    svm: LiteSVM,
    auth: Keypair,
    arena: Pubkey,
    world: Pubkey,
    dq: Pubkey,
    markets: [Pubkey; 5],
    maps: [Pubkey; 5],
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
        clk.unix_timestamp = 1_795_000_000; // 2026-11
        svm.set_sysvar(&clk);
        // GameConfig (initialize_config needs an upgradeable program; written directly)
        let mut cfg = d8("account", "GameConfig").to_vec();
        cfg.push(1);
        cfg.push(Pubkey::find_program_address(&[b"config"], &PID).1);
        cfg.extend_from_slice(auth.pubkey().as_ref()); // admin
        cfg.extend_from_slice(auth.pubkey().as_ref()); // game authority
        cfg.extend_from_slice(&1u16.to_le_bytes());
        cfg.extend_from_slice(&[0x4b, 0xdf, 0xb4, 0xef, 0x6a, 0x73, 0x93, 0x22, 0x1a, 0x83, 0x04, 0x6b, 0x48, 0xca, 0xdb, 0xb7, 0xd2, 0x4d, 0x44, 0x54, 0xe8, 0x39, 0xd4, 0xfd, 0x90, 0xad, 0x4c, 0x4b, 0xa5, 0x26, 0xbe, 0x32]);
        cfg.extend_from_slice(&1u16.to_le_bytes());
        cfg.extend_from_slice(&[0x67, 0xd6, 0x53, 0x72, 0x24, 0x94, 0x26, 0x90, 0x97, 0x1d, 0x2f, 0x6b, 0x25, 0xfa, 0x6f, 0x6f, 0x49, 0x49, 0x47, 0x93, 0x52, 0x0f, 0x64, 0xe8, 0xe7, 0x8c, 0x47, 0x43, 0x07, 0xc2, 0xc8, 0x96]);
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
        let mut t = T { svm, auth, arena, world, dq, markets, maps, m: Pubkey::default() };
        t.m = t.create_match();
        t.write_arena(sim_running(), 0, &[]);
        t.write_world(0, 0, 0, 0);
        for k in 0..6u64 {
            let target = if k < 5 { t.markets[k as usize] } else { t.dq };
            let a = t.auth.insecure_clone();
            t.econ(&a, op::INIT_ECON, ARENA_ID as u64, k, vec![ro(pda(&[b"config"], &PID)), ro(t.arena), w(target), ro(SYS)]).unwrap();
        }
        t.open_markets().unwrap();
        t
    }

    fn send(&mut self, ixs: &[Instruction], signers: &[&Keypair]) -> Result<u64, String> {
        let tx = Transaction::new(signers, Message::new(ixs, Some(&signers[0].pubkey())), self.svm.latest_blockhash());
        let r = self.svm.send_transaction(tx).map(|m| m.compute_units_consumed).map_err(|e| format!("{:?} {}", e.err, e.meta.logs.join(" | ").chars().rev().take(400).collect::<String>().chars().rev().collect::<String>()));
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
        let ix = Instruction { program_id: PID, accounts: metas, data };
        self.send(&[ix], &[signer])
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

    fn open_markets(&mut self) -> Result<u64, String> {
        let mut accs = vec![ro(self.arena), w(self.dq)];
        accs.extend(self.markets.iter().map(|k| w(*k)));
        accs.push(w(self.m)); // P5 0.5: open_markets flags the Match (MATCH_ECON)
        let a = self.auth.insecure_clone();
        self.econ(&a, op::OPEN_MARKETS, 0, 0, accs)
    }

    /// Arena: status, human mask, wallets by slot; match = self.m.
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

    /// WorldAcc: tick, director round, round phase, World round_start (hazard time 0); a real World (Lobby) body.
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

    /// Match fields by raw offset (borsh layout, discriminator included).
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

    fn bet(&mut self, who: &Keypair, k: usize, outcome: u64, amount: u64) -> Result<u64, String> {
        let accs = vec![w(chips_of(&who.pubkey())), w(self.markets[k]), ro(self.m), ro(self.arena), ro(self.world)];
        self.econ(who, op::PLACE_BET, outcome, amount, accs)
    }

    fn settle(&mut self, k: usize, chips: &[Pubkey]) -> Result<u64, String> {
        let mut accs = vec![w(self.markets[k]), ro(self.m)];
        accs.extend(chips.iter().map(|x| w(chips_of(x))));
        let a = self.auth.insecure_clone();
        self.econ(&a, op::SETTLE, 0, 0, accs)
    }

    fn market_u8(&self, k: usize, off: usize) -> u8 {
        self.svm.get_account(&self.markets[k]).unwrap().data[8 + off]
    }

    fn warp(&mut self, secs: i64) {
        let mut c: Clock = self.svm.get_sysvar();
        c.unix_timestamp += secs;
        self.svm.set_sysvar(&c);
    }
}

fn w(k: Pubkey) -> AccountMeta {
    AccountMeta::new(k, false)
}
fn ro(k: Pubkey) -> AccountMeta {
    AccountMeta::new_readonly(k, false)
}
fn sim_running() -> u8 {
    2
}

// Match borsh offsets (discriminator included)
const M_STATUS: usize = 26;
const M_FLAGS: usize = 27;
const M_ROUND: usize = 129;
const M_SLOTS: usize = 214;
const M_CHAMP: usize = 294;
const M_RECEIPT: usize = 295;
const ST_CREATED: u8 = 0;
const ST_ROUND_ACTIVE: u8 = 3;
const ST_ROUND_RESOLVED: u8 = 4;
const ST_COMPLETED: u8 = 5;
const ST_ABORTED: u8 = 6;
const FINAL_MODE: u8 = 0x40;
// Market field offsets (after the discriminator): status @197, label @198, n_bets @199, refund @201
const MK_STATUS: usize = 32 + 32 + 32 + 96 + 2 + 3;
const MK_LABEL: usize = MK_STATUS + 1;

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
fn chips_faucet_and_session_keys() {
    let mut t = T::new();
    let a = t.wallet();
    let ak = a.pubkey();
    assert_eq!(t.balance(&ak), 1_000 * ONE, "faucet credits 1,000 RUSH");
    assert!(t.econ(&a, op::INIT_CHIPS, 0, 0, vec![w(chips_of(&ak)), ro(SYS)]).is_err(), "init twice");
    assert!(t.econ(&a, op::FAUCET, 0, 0, vec![w(chips_of(&ak))]).is_err(), "faucet rate limit (24 h)");
    t.warp(86_399);
    assert!(t.econ(&a, op::FAUCET, 0, 0, vec![w(chips_of(&ak))]).is_err(), "still inside 24 h");
    t.warp(1);
    t.econ(&a, op::FAUCET, 0, 0, vec![w(chips_of(&ak))]).unwrap();
    assert_eq!(t.balance(&ak), 2_000 * ONE);
    // a stranger cannot spend / claim for the wallet
    let x = Keypair::new();
    t.svm.airdrop(&x.pubkey(), 1_000_000_000).unwrap();
    t.warp(86_400);
    assert!(t.econ(&x, op::FAUCET, 0, 0, vec![w(chips_of(&ak))]).is_err(), "only the owner's wallet or session key");
    // session key: valid token → ok; another signer with that token → no; expired → no
    let s = Keypair::new();
    t.svm.airdrop(&s.pubkey(), 1_000_000_000).unwrap();
    let now: Clock = t.svm.get_sysvar();
    let tok = session_token(&mut t, &ak, &s.pubkey(), now.unix_timestamp + 3600);
    assert!(t.econ(&x, op::FAUCET, 0, 0, vec![w(chips_of(&ak)), ro(tok)]).is_err(), "token of another signer");
    t.econ(&s, op::FAUCET, 0, 0, vec![w(chips_of(&ak)), ro(tok)]).unwrap();
    assert_eq!(t.balance(&ak), 3_000 * ONE);
    t.warp(86_400);
    let old = session_token(&mut t, &ak, &s.pubkey(), now.unix_timestamp + 10);
    assert!(t.econ(&s, op::FAUCET, 0, 0, vec![w(chips_of(&ak)), ro(old)]).is_err(), "expired session token");
    println!("chips + faucet + session keys: OK");
}

#[test]
fn markets_windows_payouts_refunds() {
    let mut t = T::new();
    let (a, b, c) = (t.wallet(), t.wallet(), t.wallet());
    let (ak, bk, ck) = (a.pubkey(), b.pubkey(), c.pubkey());
    // ---- Qualify(1): open before round 1, closes at start_round(1)
    t.bet(&a, 2, 3, 100 * ONE).unwrap();
    t.bet(&b, 2, 5, 50 * ONE).unwrap();
    t.bet(&c, 2, 3, 30 * ONE).unwrap();
    t.bet(&a, 2, 3, 20 * ONE).unwrap(); // aggregated with the first
    assert!(t.bet(&a, 2, 3, ONE / 2).is_err(), "below 1 RUSH");
    assert!(t.bet(&a, 2, 12, ONE).is_err(), "outcome beyond the roster");
    assert!(t.bet(&a, 2, 3, 5_000 * ONE).is_err(), "more than the balance");
    // ---- Champion: open from create_match
    t.bet(&a, 0, 3, 200 * ONE).unwrap();
    t.bet(&b, 0, 7, 100 * ONE).unwrap();
    // round 1 starts → Qualify(1) closed
    t.patch_match(|d| { d[M_ROUND] = 1; d[M_STATUS] = ST_ROUND_ACTIVE; });
    assert!(t.bet(&a, 2, 3, ONE).is_err(), "Qualify(1) closes at start_round(1)");
    t.bet(&c, 0, 3, 10 * ONE).unwrap(); // Champion still open in round 1
    // settle Qualify(1) only after round 1 resolved
    assert!(t.settle(2, &[ak, bk, ck]).is_err(), "not settled before the round is resolved");
    // round 1 result (authority-recorded): slot 5 eliminated in round 1, 3 qualified
    t.patch_match(|d| { d[M_STATUS] = ST_ROUND_RESOLVED; d[M_SLOTS + 4 * 5] = 3; d[M_SLOTS + 4 * 5 + 1] = 1; d[M_SLOTS + 4 * 3 + 1] = 1; });
    let (a0, b0, c0) = (t.balance(&ak), t.balance(&bk), t.balance(&ck));
    t.settle(2, &[ak, bk, ck]).unwrap();
    // pool 200; winning stake (slot 3) = 120 (a) + 30 (c): a gets 120·200/150 = 160, c 30·200/150 = 40, b 0
    assert_eq!(t.balance(&ak) - a0, 160 * ONE);
    assert_eq!(t.balance(&ck) - c0, 40 * ONE);
    assert_eq!(t.balance(&bk), b0);
    assert_eq!(t.market_u8(2, MK_STATUS), 3, "Qualify(1) paid");
    assert_eq!(t.market_u8(2, MK_LABEL), 2, "● authority-recorded");
    // double settle / payout: no-op
    t.settle(2, &[ak, bk, ck]).unwrap();
    assert_eq!(t.balance(&ak) - a0, 160 * ONE, "double settle is a no-op");
    // ---- the Final on-chain: Champion + FinalWinner close exactly at the RUSH tick
    t.patch_match(|d| { d[M_ROUND] = 4; d[M_STATUS] = ST_ROUND_ACTIVE; d[M_FLAGS] |= FINAL_MODE; for s in 0..12 { if ![3usize, 7, 9].contains(&s) { d[M_SLOTS + 4 * s] = 3; } } });
    t.write_world(500, 4, 2, 0); // countdown
    t.bet(&b, 1, 7, 40 * ONE).unwrap();
    t.bet(&a, 1, 3, 60 * ONE).unwrap();
    assert!(t.bet(&a, 1, 5, ONE).is_err(), "FinalWinner outcomes are the finalists");
    t.bet(&a, 0, 9, 5 * ONE).unwrap();
    t.write_world(501, 4, 3, 501); // the RUSH tick
    assert!(t.bet(&a, 0, 3, ONE).is_err(), "Champion closes at the RUSH tick");
    assert!(t.bet(&a, 1, 3, ONE).is_err(), "FinalWinner closes at the RUSH tick");
    assert!(t.settle(0, &[ak]).is_err(), "Champion not settled before the Final ends");
    // the program's Final: champion 3
    t.patch_match(|d| { d[M_STATUS] = ST_COMPLETED; d[M_CHAMP] = 3; });
    let (a1, b1, c1) = (t.balance(&ak), t.balance(&bk), t.balance(&ck));
    t.settle(0, &[ak, bk, ck]).unwrap();
    // Champion pool 315 (a 200 on 3 + 5 on 9, b 100 on 7, c 10 on 3); winning stake 210: a 200·315/210 = 300, c 10·315/210 = 15
    assert_eq!(t.balance(&ak) - a1, 300 * ONE);
    assert_eq!(t.balance(&ck) - c1, 15 * ONE);
    assert_eq!(t.balance(&bk), b1);
    assert_eq!(t.market_u8(0, MK_LABEL), 1, "◆ program-settled");
    t.settle(1, &[ak, bk]).unwrap();
    assert_eq!(t.balance(&ak) - a1, 400 * ONE, "FinalWinner: a takes the 100 pool");
    // ---- hostile: a fake ChipBalance (right data, wrong owner) and someone else's PDA for a bet
    let fake = Pubkey::new_unique();
    let mut d = t.svm.get_account(&chips_of(&bk)).unwrap();
    d.owner = Pubkey::new_unique();
    t.svm.set_account(fake, d).unwrap();
    let mut accs = vec![w(t.markets[1]), ro(t.m), w(fake)];
    accs.truncate(3);
    let au = t.auth.insecure_clone();
    assert!(t.econ(&au, op::SETTLE, 0, 0, accs).is_err(), "payout to an account not owned by the program");
    let accs = vec![w(chips_of(&ak)), w(t.markets[0]), ro(t.m), ro(t.arena), ro(t.world)];
    assert!(t.econ(&b, op::PLACE_BET, 3, ONE, accs).is_err(), "bet from another wallet's chips");
    // ---- next match: refunds (aborted; Classic fallback; no winner)
    let m2 = t.create_match();
    t.m = m2;
    t.write_arena(sim_running(), 0, &[]);
    t.open_markets().unwrap();
    t.bet(&a, 0, 3, 50 * ONE).unwrap();
    t.bet(&b, 0, 4, 25 * ONE).unwrap();
    t.bet(&a, 2, 3, 10 * ONE).unwrap();
    t.patch_match(|d| { d[M_STATUS] = ST_COMPLETED; d[M_CHAMP] = 3; }); // Completed WITHOUT the on-chain Final
    let (a2, b2) = (t.balance(&ak), t.balance(&bk));
    t.settle(0, &[ak, bk]).unwrap();
    assert_eq!((t.balance(&ak) - a2, t.balance(&bk) - b2), (50 * ONE, 25 * ONE), "Classic fallback refunds");
    assert_eq!(t.market_u8(0, MK_LABEL), 3, "● Classic fallback");
    t.patch_match(|d| { d[M_STATUS] = ST_ABORTED; });
    t.settle(2, &[ak]).unwrap();
    assert_eq!(t.balance(&ak) - a2, 60 * ONE, "aborted refunds");
    assert_eq!(t.market_u8(2, MK_LABEL), 4);
    // a market holding unpaid bets refuses to reopen
    let m3 = t.create_match();
    t.m = m3;
    t.write_arena(sim_running(), 0, &[]);
    t.open_markets().unwrap();
    t.bet(&c, 0, 1, 7 * ONE).unwrap();
    t.patch_match(|d| { d[M_FLAGS] |= FINAL_MODE; d[M_STATUS] = ST_COMPLETED; d[M_CHAMP] = 2; d[M_ROUND] = 4; });
    let c3 = t.balance(&ck);
    t.settle(0, &[]).unwrap(); // settled, nobody paid yet
    let m4 = t.create_match();
    t.m = m4;
    t.write_arena(sim_running(), 0, &[]);
    assert!(t.open_markets().is_err(), "unpaid bets block the next match");
    t.m = m3;
    t.settle(0, &[ck]).unwrap();
    assert_eq!(t.balance(&ck) - c3, 7 * ONE, "no winner (nobody on slot 2): refund");
    assert_eq!(t.market_u8(0, MK_LABEL), 5);
    t.m = m4;
    t.open_markets().unwrap();
    println!("markets: windows, payouts = hand calculation, refunds, idempotent, hostile accounts: OK");
}

#[test]
fn chaos_drop_purchases() {
    let mut t = T::new();
    let a = t.wallet();
    let ak = a.pubkey();
    let buy = |t: &mut T, k: u64| {
        let accs = vec![w(chips_of(&ak)), w(t.dq), ro(t.arena), ro(t.world)];
        let ac = a.insecure_clone();
        t.econ(&ac, op::BUY_DROP, k, 0, accs)
    };
    t.write_world(300, 4, 2, 0);
    assert!(buy(&mut t, 1).is_err(), "not before RUSH");
    t.write_world(1000, 4, 3, 700); // RUSH, hazard t30 = 300
    assert!(buy(&mut t, 4).is_err(), "Sweeper Rush only once the sweeper runs");
    assert!(buy(&mut t, 0).is_err() && buy(&mut t, 5).is_err(), "unknown kinds");
    let b0 = t.balance(&ak);
    buy(&mut t, 1).unwrap();
    assert_eq!(b0 - t.balance(&ak), 50 * ONE, "Lid Frenzy costs 50 RUSH");
    t.write_world(1149, 4, 3, 700);
    assert!(buy(&mut t, 2).is_err(), "5 s cooldown");
    t.write_world(1150, 4, 3, 400); // t30 750: sweeper on
    buy(&mut t, 4).unwrap();
    t.write_world(1300, 4, 3, 400);
    buy(&mut t, 3).unwrap();
    t.write_world(1450, 4, 3, 400);
    assert!(buy(&mut t, 2).is_err(), "3 per round");
    let dq = t.svm.get_account(&t.dq).unwrap().data;
    assert_eq!(dq[8 + 84], 3, "3 queued");
    assert_eq!(u64::from_le_bytes(dq[8 + 64..8 + 72].try_into().unwrap()), 120 * ONE, "pot = 50 + 40 + 30");
    t.write_arena(4, 0, &[]);
    assert!(buy(&mut t, 1).is_err(), "arena not running");
    println!("chaos drops: phase, sale, caps, cooldown, price: OK");
}

#[test]
fn cashout_mints_committed_spl_once() {
    let mut t = T::new();
    let a = t.wallet();
    let ak = a.pubkey();
    // the RUSH mint (created off-chain by the setup script with the PDA as mint authority)
    let mint = Pubkey::from_str_const("3s8KstqicXn1kgFyWPVq3AZXkMprDtEtT7BNTxtQccEw");
    let auth = pda(&[b"rush_mint_auth"], &PID);
    let mut md = vec![0u8; 82];
    md[0..4].copy_from_slice(&1u32.to_le_bytes());
    md[4..36].copy_from_slice(auth.as_ref());
    md[44] = 6;
    md[45] = 1;
    t.svm.set_account(mint, Account { lamports: 10_000_000, data: md, owner: TOKEN, executable: false, rent_epoch: 0 }).unwrap();
    // a committed cash-out of 250 (cashout itself commits through the Magic program: rollup only)
    let mut c = t.svm.get_account(&chips_of(&ak)).unwrap();
    c.data[56..64].copy_from_slice(&(250 * ONE).to_le_bytes());
    t.svm.set_account(chips_of(&ak), c).unwrap();
    let ata = pda(&[ak.as_ref(), TOKEN.as_ref(), mint.as_ref()], &ATA);
    let accs = |t: &T| vec![ro(chips_of(&ak)), w(pda(&[b"cashout", ak.as_ref()], &PID)), w(mint), ro(auth), ro(ak), w(ata), ro(TOKEN), ro(ATA), ro(SYS)];
    let au = t.auth.insecure_clone();
    let x = accs(&t);
    t.econ(&au, op::MINT_CASHOUT, 0, 0, x).unwrap();
    let amount = |t: &T| u64::from_le_bytes(t.svm.get_account(&ata).unwrap().data[64..72].try_into().unwrap());
    assert_eq!(amount(&t), 250 * ONE);
    let x = accs(&t);
    assert!(t.econ(&au, op::MINT_CASHOUT, 0, 0, x).is_err(), "nothing new to mint");
    let mut c = t.svm.get_account(&chips_of(&ak)).unwrap();
    c.data[56..64].copy_from_slice(&(300 * ONE).to_le_bytes());
    t.svm.set_account(chips_of(&ak), c).unwrap();
    let x = accs(&t);
    t.econ(&au, op::MINT_CASHOUT, 0, 0, x).unwrap();
    assert_eq!(amount(&t), 300 * ONE, "mints only the difference");
    println!("cash-out: committed chips → RUSH SPL exactly once: OK");
}

#[test]
fn receipt_and_core_rewards() {
    let mut t = T::new();
    let (a, b) = (t.wallet(), t.wallet());
    let (ak, bk) = (a.pubkey(), b.pubkey());
    t.write_arena(sim_running(), (1 << 3) | (1 << 5), &[(3, ak), (5, bk)]);
    t.write_world(900, 4, 4, 300);
    t.bet(&a, 0, 3, 10 * ONE).unwrap();
    t.patch_match(|d| { d[M_ROUND] = 4; d[M_FLAGS] |= FINAL_MODE; d[M_STATUS] = ST_COMPLETED; d[M_CHAMP] = 3; });
    let mut accs = vec![w(t.m), ro(t.arena), ro(t.world), ro(t.dq)];
    accs.extend(t.markets.iter().map(|k| ro(*k)));
    accs.extend(t.maps.iter().map(|k| ro(*k)));
    let au = t.auth.insecure_clone();
    assert!(t.econ(&au, op::WRITE_RECEIPT, 0, 0, accs.clone()).is_err(), "markets must be settled first");
    // P5 0.5: settled but with an unpaid winner (a's ChipBalance not passed) → no receipt (so no finalize)
    t.settle(0, &[]).unwrap();
    assert!(t.econ(&au, op::WRITE_RECEIPT, 0, 0, accs.clone()).is_err(), "an unpaid bet blocks the receipt");
    for k in 0..5 {
        t.settle(k, &[ak]).unwrap();
    }
    t.econ(&au, op::WRITE_RECEIPT, 0, 0, accs).unwrap();
    println!("receipt refuses unpaid winners, then writes once all are paid: OK");
    let r = t.svm.get_account(&t.m).unwrap().data;
    assert_eq!(r[M_RECEIPT] & 1, 1, "written");
    assert_eq!(r[M_RECEIPT + 1], 3, "trust: rounds by the authority + Final by the program");
    assert_eq!(&r[M_RECEIPT + 305..M_RECEIPT + 337], ak.as_ref(), "wallet of slot 3");
    // the capsule VRF callback can only be signed by the VRF identity: set its result here
    t.patch_match(|d| { d[M_RECEIPT] |= 2; d[M_RECEIPT + 605 + 3] = 2; d[M_RECEIPT + 605 + 5] = 0; });
    let tre = pda(&[b"treasury"], &PID);
    t.svm.airdrop(&tre, 1_000_000_000).unwrap();
    let mint = |t: &mut T, what: u64, asset: Pubkey, owner: Pubkey| {
        let accs = vec![w(t.m), w(asset), ro(owner), w(tre), ro(pda(&[b"assets"], &PID)), ro(CORE), ro(SYS)];
        let au = t.auth.insecure_clone();
        t.econ(&au, op::MINT_REWARDS, what, 0, accs)
    };
    let crown = pda(&[b"crown", t.m.as_ref()], &PID);
    assert!(mint(&mut t, 0, crown, bk).is_err(), "the Crown only to the champion's wallet");
    let cu = mint(&mut t, 0, crown, ak).unwrap();
    let ca = t.svm.get_account(&crown).unwrap();
    assert_eq!(ca.owner, CORE);
    assert_eq!(&ca.data[1..33], ak.as_ref(), "Core asset owner = the champion");
    assert!(mint(&mut t, 0, crown, ak).is_err(), "double mint impossible");
    let cap3 = pda(&[b"capsule", t.m.as_ref(), &[3]], &PID);
    let cap5 = pda(&[b"capsule", t.m.as_ref(), &[5]], &PID);
    mint(&mut t, 4, cap3, ak).unwrap();
    mint(&mut t, 6, cap5, bk).unwrap();
    assert!(mint(&mut t, 6, cap5, bk).is_err(), "capsule twice");
    let cap7 = pda(&[b"capsule", t.m.as_ref(), &[7]], &PID);
    assert!(mint(&mut t, 8, cap7, bk).is_err(), "bots get no capsule");
    let s = String::from_utf8_lossy(&t.svm.get_account(&cap3).unwrap().data).to_string();
    assert!(s.contains("Rushling Capsule (Epic)") && s.contains("rarity") && s.contains("Epic"), "capsule metadata");
    println!("receipt + Crown + capsules (Metaplex Core CPI, {cu} CU for the Crown): OK");
}

const SIM_STARTED: u8 = 0x20;
const MAGIC_PROGRAM: Pubkey = Pubkey::from_str_const("Magic11111111111111111111111111111111111111");
const MAGIC_CONTEXT: Pubkey = Pubkey::from_str_const("MagicContext1111111111111111111111111111111");

impl T {
    /// `finalize_match` (commit + undelegate through the Magic program, which LiteSVM does not have: a stand-in
    /// executable sits at its id, so the call gets past the program's own checks and then the CPI fails).
    fn finalize(&mut self) -> Result<u64, String> {
        if self.svm.get_account(&MAGIC_PROGRAM).map_or(true, |a| !a.executable) {
            let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
            self.svm.add_program(MAGIC_PROGRAM, &std::fs::read(root.join("crates/target/deploy/rush_core_sbf_check.so")).expect("cargo build-sbf of rush-core-sbf-check")).unwrap();
        }
        let ix = Instruction {
            program_id: PID,
            accounts: vec![AccountMeta::new(self.auth.pubkey(), true), ro(pda(&[b"config"], &PID)), w(self.m), ro(MAGIC_PROGRAM), w(MAGIC_CONTEXT)],
            data: d8("global", "finalize_match").to_vec(),
        };
        let a = self.auth.insecure_clone();
        self.send(&[ix], &[&a])
    }

    fn receipt(&mut self) -> Result<u64, String> {
        let mut accs = vec![w(self.m), ro(self.arena), ro(self.world), ro(self.dq)];
        accs.extend(self.markets.iter().map(|k| ro(*k)));
        accs.extend(self.maps.iter().map(|k| ro(*k)));
        let au = self.auth.insecure_clone();
        self.econ(&au, op::WRITE_RECEIPT, 0, 0, accs)
    }
}

/// A fullchain match after round 2 with only `alive` left in (the others out in round 1).
fn after_round2(t: &mut T, status: u8, alive: &[usize]) {
    let alive = alive.to_vec();
    t.patch_match(move |d| {
        d[M_FLAGS] |= SIM_STARTED;
        d[M_ROUND] = 2;
        d[M_STATUS] = status;
        for s in 0..12 {
            if !alive.contains(&s) {
                d[M_SLOTS + 4 * s] = 3;
                d[M_SLOTS + 4 * s + 1] = 1;
            }
        }
    });
}

/// harden #12 (1): a fullchain match crowned at round 2 (≤ 1 alive) with bets on Qualify(3): the unplayed round's
/// market refunds (NO_WINNER) and is PAID, so the receipt is written and `finalize_match` gets past ReceiptNotReady.
#[test]
fn early_champion_refunds_unplayed_qualify() {
    let mut t = T::new();
    let (a, b) = (t.wallet(), t.wallet());
    let (ak, bk) = (a.pubkey(), b.pubkey());
    // ROUND_RESULT of round 2 (two alive): Qualify(3) open, the market maker and a player bet
    after_round2(&mut t, ST_ROUND_RESOLVED, &[3, 5]);
    t.bet(&a, 4, 3, 40 * ONE).unwrap();
    t.bet(&b, 4, 5, 15 * ONE).unwrap();
    let (a0, b0) = (t.balance(&ak), t.balance(&bk));
    assert!(t.settle(4, &[ak, bk]).is_err(), "round 3 still to come: not settleable");
    // slot 5 falls out at the end of round 2: the director crowns slot 3 (round 3 is never played)
    t.patch_match(|d| { d[M_STATUS] = ST_COMPLETED; d[M_CHAMP] = 3; d[M_SLOTS + 4 * 5] = 3; d[M_SLOTS + 4 * 5 + 1] = 2; d[M_SLOTS + 4 * 3 + 2] = 1; });
    assert!(t.finalize().unwrap_err().contains("ReceiptNotReady"), "no receipt yet");
    for k in 0..5 {
        t.settle(k, &[ak, bk]).unwrap();
    }
    assert_eq!((t.balance(&ak) - a0, t.balance(&bk) - b0), (40 * ONE, 15 * ONE), "Qualify(3) refunds every bet");
    assert_eq!(t.market_u8(4, MK_STATUS), 3, "Qualify(3) PAID");
    assert_eq!(t.market_u8(4, MK_LABEL), 5, "NO_WINNER");
    t.receipt().unwrap();
    assert_eq!(t.svm.get_account(&t.m).unwrap().data[M_RECEIPT] & 1, 1, "receipt written");
    let e = t.finalize().err().unwrap_or_default();
    assert!(!e.contains("ReceiptNotReady") && !e.contains("InvalidStatus") && !e.contains("ConstraintAddress"), "finalize passes the program's checks (only the Magic CPI is not real in LiteSVM): {e}");
    assert!(e.is_empty() || e.contains(&MAGIC_PROGRAM.to_string()), "it failed inside the Magic CPI: {e}");
    // the next match on the arena opens its markets (no MarketBusy cascade)
    t.m = t.create_match();
    t.write_arena(sim_running(), 0, &[]);
    t.open_markets().unwrap();
    println!("early champion at round 2: Qualify(3) refunded (NO_WINNER, PAID), receipt written, finalize past ReceiptNotReady, next match opens: OK");
}

/// harden #12 (2): Qualify(3) never opens when round 2 leaves ≤ 1 player alive.
#[test]
fn qualify_bet_needs_two_alive() {
    let mut t = T::new();
    let a = t.wallet();
    after_round2(&mut t, ST_ROUND_RESOLVED, &[3]);
    assert!(t.bet(&a, 4, 3, ONE).unwrap_err().contains("MarketClosed"), "one alive: Qualify(3) closed");
    t.patch_match(|d| { d[M_SLOTS + 4 * 5] = 1; d[M_SLOTS + 4 * 5 + 1] = 2; });
    t.bet(&a, 4, 3, ONE).unwrap();
    println!("Qualify(3) refused with 1 alive, open with 2: OK");
}

/// harden #12 (5): a `submit_input` batch for already-run ticks succeeds but does not clobber ring[t + 16].
#[test]
fn submit_input_past_ticks_skipped() {
    let mut t = T::new();
    t.write_world(100, 1, 3, 0);
    let p = Keypair::new();
    t.svm.airdrop(&p.pubkey(), 1_000_000_000).unwrap();
    let ib = pda(&[b"input", &[ARENA_ID], &[4]], &PID);
    let mut d = vec![0u8; 8 + 240];
    d[..8].copy_from_slice(&d8("account", "InputBuf"));
    d[8..40].copy_from_slice(t.arena.as_ref());
    d[40..72].copy_from_slice(p.pubkey().as_ref());
    d[8 + 104] = 4;
    t.svm.set_account(ib, Account { lamports: 10_000_000, data: d, owner: PID, executable: false, rent_epoch: 0 }).unwrap();
    let submit = |t: &mut T, start: u32, v: u8| {
        let mut data = d8("global", "submit_input").to_vec();
        data.push(4);
        data.extend_from_slice(&start.to_le_bytes());
        data.extend_from_slice(&[v, v, 0, 1, v, v, 0, 1, v, v, 0, 1]);
        let ix = Instruction { program_id: PID, accounts: vec![AccountMeta::new_readonly(p.pubkey(), true), w(ib), ro(t.world), ro(PID)], data };
        let pk = p.insecure_clone();
        t.send(&[ix], &[&pk])
    };
    let entry = |t: &T, tick: u32| {
        let d = t.svm.get_account(&ib).unwrap().data;
        let o = 8 + 112 + 8 * (tick as usize % 16);
        (u32::from_le_bytes(d[o..o + 4].try_into().unwrap()), d[o + 4])
    };
    submit(&mut t, 108, 20).unwrap(); // ticks 108..=110 queued (the default lead 8)
    submit(&mut t, 92, 100).unwrap(); // late: 92..=94 share the ring entries of 108..=110
    for tk in 108..=110 {
        assert_eq!(entry(&t, tk), (tk, 20), "future input kept");
    }
    submit(&mut t, 100, 50).unwrap(); // tick 100 already ran: only 101, 102 written
    assert_eq!(entry(&t, 100).0, 0);
    assert_eq!(entry(&t, 101), (101, 50));
    assert_eq!(entry(&t, 102), (102, 50));
    assert!(submit(&mut t, 91, 1).is_err(), "window unchanged: start < tick - 8 refused");
    println!("submit_input: late batch accepted, queued future inputs kept: OK");
}

// Match.event_seq (borsh, discriminator included)
const M_SEQ: usize = 134;

impl T {
    fn seq(&self) -> u32 {
        let d = self.svm.get_account(&self.m).unwrap().data;
        u32::from_le_bytes(d[M_SEQ..M_SEQ + 4].try_into().unwrap())
    }

    /// A legacy authority-recorded event (`start_round` / `record_*` / `complete_*`), signed by the game authority.
    fn legacy(&mut self, name: &str, args: &[u8]) -> Result<u64, String> {
        let mut data = d8("global", name).to_vec();
        data.extend_from_slice(args);
        data.extend_from_slice(&self.seq().to_le_bytes());
        let mut accounts = vec![AccountMeta::new(self.auth.pubkey(), true), ro(pda(&[b"config"], &PID)), w(self.m)];
        if name == "complete_round" {
            if self.svm.get_account(&MAGIC_PROGRAM).map_or(true, |a| !a.executable) {
                let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
                self.svm.add_program(MAGIC_PROGRAM, &std::fs::read(root.join("crates/target/deploy/rush_core_sbf_check.so")).expect("cargo build-sbf of rush-core-sbf-check")).unwrap();
            }
            accounts.extend([ro(MAGIC_PROGRAM), w(MAGIC_CONTEXT)]);
        }
        let a = self.auth.insecure_clone();
        self.send(&[Instruction { program_id: PID, accounts, data }], &[&a])
    }

    /// The game authority forges round 1: every slot but `champ` eliminated, `champ` qualified, the round completed,
    /// the match completed (champion = `champ`). `complete_round` ends in its Magic commit CPI (no Magic program in
    /// LiteSVM): once it passed the program's own checks, its state change is applied by hand.
    fn forge_round1(&mut self, champ: u8) -> Result<(), String> {
        for s in 0..12u8 {
            if s != champ {
                self.legacy("record_eliminated", &[1, s, 1])?;
            }
        }
        self.legacy("record_qualified", &[1, champ, 1])?;
        let e = self.legacy("complete_round", &[1]).err().unwrap_or_default();
        if !(e.is_empty() || e.contains(&MAGIC_PROGRAM.to_string())) {
            return Err(e);
        }
        let c = champ as usize;
        self.patch_match(move |d| {
            d[M_SLOTS + 4 * c] = 1;
            d[M_STATUS] = ST_ROUND_RESOLVED;
            let s = u32::from_le_bytes(d[M_SEQ..M_SEQ + 4].try_into().unwrap()) + 1;
            d[M_SEQ..M_SEQ + 4].copy_from_slice(&s.to_le_bytes());
        });
        self.legacy("complete_match", &[])?;
        Ok(())
    }
}

/// Security: in a fullchain match (`start_sim`, every round computed by the program) the game authority cannot
/// record round results with the legacy Classic events, so it cannot pick the champion that `settle` pays and the
/// Crown goes to. Control: the same events still work in a Classic match.
#[test]
fn legacy_events_blocked_in_fullchain() {
    let mut t = T::new();
    let (a, b) = (t.wallet(), t.wallet());
    let (ak, bk) = (a.pubkey(), b.pubkey());
    t.write_arena(sim_running(), (1 << 3) | (1 << 5), &[(3, ak), (5, bk)]);
    t.bet(&a, 0, 3, 100 * ONE).unwrap();
    t.bet(&b, 0, 5, 10 * ONE).unwrap();
    // start_sim + the director's first mirror (ROUND_INTRO of round 1): RoundActive, round 1, SIM_STARTED
    t.patch_match(|d| { d[M_FLAGS] |= SIM_STARTED; d[M_ROUND] = 1; d[M_STATUS] = ST_ROUND_ACTIVE; });
    let forged = t.forge_round1(5);
    let d = t.svm.get_account(&t.m).unwrap().data;
    println!("fullchain forge: {forged:?}; status {} champion {}", d[M_STATUS], d[M_CHAMP]);
    if forged.is_ok() {
        let (a0, b0) = (t.balance(&ak), t.balance(&bk));
        for k in 0..5 {
            t.settle(k, &[ak, bk]).unwrap();
        }
        println!("EXPLOIT: markets settled: a {:+} RUSH, b {:+} RUSH", (t.balance(&ak) as i64 - a0 as i64) / ONE as i64, (t.balance(&bk) as i64 - b0 as i64) / ONE as i64);
        t.receipt().unwrap();
        let tre = pda(&[b"treasury"], &PID);
        t.svm.airdrop(&tre, 1_000_000_000).unwrap();
        let crown = pda(&[b"crown", t.m.as_ref()], &PID);
        let accs = vec![w(t.m), w(crown), ro(bk), w(tre), ro(pda(&[b"assets"], &PID)), ro(CORE), ro(SYS)];
        let au = t.auth.insecure_clone();
        let r = t.econ(&au, op::MINT_REWARDS, 0, 0, accs);
        let owner = t.svm.get_account(&crown).map(|c| c.data[1..33] == bk.to_bytes()[..]);
        println!("EXPLOIT: Crown mint {r:?}, Crown owned by the forged champion's wallet: {owner:?}");
        panic!("the game authority forged a fullchain match result");
    }
    assert!(forged.unwrap_err().contains("LegacyEventInChainMode"), "refused by the chain-mode gate");
    assert_eq!((d[M_STATUS], d[M_CHAMP]), (ST_ROUND_ACTIVE, 255), "Match untouched");
    // every legacy event is refused in fullchain, whatever its other preconditions
    t.patch_match(|d| { d[M_STATUS] = ST_ROUND_RESOLVED; });
    for (n, args) in [("start_round", vec![2u8]), ("complete_match", vec![])] {
        assert!(t.legacy(n, &args).unwrap_err().contains("LegacyEventInChainMode"), "{n}");
    }
    t.patch_match(|d| { d[M_STATUS] = ST_ROUND_ACTIVE; });
    assert!(t.legacy("record_qualified", &[1, 5, 1]).unwrap_err().contains("LegacyEventInChainMode"), "record_qualified");
    assert!(t.legacy("complete_round", &[1]).unwrap_err().contains("LegacyEventInChainMode"), "complete_round");
    // hybrid Final mode (start_final sets SIM_STARTED | FINAL_MODE): the on-chain Final is gated too
    t.patch_match(|d| { d[M_FLAGS] |= FINAL_MODE; d[M_ROUND] = 4; });
    assert!(t.legacy("record_eliminated", &[4, 3, 1]).unwrap_err().contains("LegacyEventInChainMode"), "Final mode");
    assert!(t.settle(0, &[ak, bk]).is_err(), "Champion market not settleable (match not over)");
    // control: a Classic match (no SIM_STARTED) still records its rounds with the same events
    t.m = t.create_match();
    t.write_arena(sim_running(), 0, &[]);
    t.patch_match(|d| { d[M_STATUS] = 2; }); // Dealt (the VRF callback is signed by the VRF identity only)
    t.legacy("start_round", &[1]).unwrap();
    t.forge_round1(5).unwrap();
    assert_eq!(t.svm.get_account(&t.m).unwrap().data[M_CHAMP], 5, "Classic: authority-recorded champion");
    println!("legacy events refused in fullchain + Final mode, still accepted in Classic: OK");
}
