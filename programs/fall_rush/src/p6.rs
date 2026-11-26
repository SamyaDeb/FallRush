//! P6 (doc 19 P6, prompt `docs/prompts/P6-PROMPT.md`): sealed Champion bets, seasons and achievements.
//!
//! **Sealed bets (D-SEAL, doc 15 OC-21): a MagicBlock Private Ephemeral Rollup holds the pick; a commitment on the
//! public rollup holds the stake.**
//! - `SealedBook` (`["sbook", arena]`) is delegated to the **TEE validator** (`devnet-tee`) and made private with an
//!   `EphemeralPermission { is_private: true, members: [] }` (Permission Program CPI signed by the book PDA): nobody can
//!   read it, not even us. `PICK` stores (wallet, outcome, amount, salt) there, signed by the bettor's session key.
//! - `SealedEscrow` (`["sealed", arena]`) lives on the public rollup (devnet-as) next to the chips: `COMMIT` debits the
//!   stake from the `ChipBalance` and stores `sha256(outcome ‖ amount ‖ salt ‖ wallet)`; the amount is public, the
//!   pick is not.
//! - At close (the Champion market's window, by program state) the game authority's `BOOK_CLOSE` turns the book
//!   public; anyone then `REVEAL`s each entry on devnet-as with the preimage from the book (or the bettor from their
//!   own saved salt: the commit-reveal path B needs no TEE). The program checks the hash and moves the stake into the
//!   Champion `Market` as an ordinary bet, so settlement / payouts are unchanged. `FINISH` after the reveal window:
//!   unrevealed stakes are **forfeited to the pool** (on-chain Final) or refunded (abort / Classic fallback).
//!
//! **Seasons (D-SEASON): on Solana, from committed program state only.** `COMMIT_ECON` commits the arena's 5 markets
//! + drop queue to Solana after the receipt; `RECORD` (permissionless) reads the undelegated `Match` (receipt) and
//! those committed copies (checked against the receipt's `bets_hash`), updates each passed `PlayerSeason` (idempotent
//! per wallet: a ring of recorded matches) and the `Season` boards; `CLOSE_SEASON` + `MINT_TROPHY` mint a Core trophy
//! to each board's top 1. Achievements are bits in `PlayerSeason` (+ `PlayerProfile` for "first crown ever").
//!
//! Nothing here runs in `tick()`.

use anchor_lang::prelude::*;
use anchor_lang::solana_program::instruction::{AccountMeta, Instruction};
use anchor_lang::solana_program::program::invoke_signed;
use solana_sha256_hasher::hashv;

use crate::econ::{self, acts_for, Market, DropQueue, ChipBalance, MARKETS, SEALED_PAD, sealed_state, mkind, mstatus};
use crate::errors::FallRushError;
use crate::sim;
use crate::state::*;

pub const ESCROW_SEED: &[u8] = b"sealed";
pub const BOOK_SEED: &[u8] = b"sbook";
pub const SEASON_SEED: &[u8] = b"season";
pub const PSEASON_SEED: &[u8] = b"pseason";
pub const PROFILE_SEED: &[u8] = b"profile";
pub const TROPHY_SEED: &[u8] = b"trophy";

pub const PERMISSION_PROGRAM: Pubkey = Pubkey::from_str_const("ACLseoPoyC3cBqoUtkbjZ4aDrkurZW86v19pXz2XQnp1");
pub const EPHEMERAL_VAULT: Pubkey = Pubkey::from_str_const("MagicVau1t999999999999999999999999999999999");
pub const PERMISSION_SEED: &[u8] = b"permission:";
/// ER rent for the book's `EphemeralPermission` (no members: 35 + 1 default member × 33 B; `(len + 60) × 32`).
pub const PERMISSION_RENT: u64 = (35 + 33 + 60) * 32;

pub const SEALED_MAX: usize = 32;
/// Reveal window after close (s): `FINISH` with unrevealed entries waits this long (D-REVEAL: ≤ 20 s).
pub const REVEAL_WINDOW_S: i64 = 20;
/// Test seasons (`OPEN_SEASON` by the authority, explicit length) have ids ≥ this; weekly ids are `year*100 + week`.
pub const TEST_SEASON_BASE: u64 = 1_000_000;
pub const BOARD: usize = 10;
pub const RING: usize = 4;

pub mod ach {
    pub const FIRST_CROWN: u32 = 1;
    pub const SURVIVOR3: u32 = 2;
    pub const PERFECT_PREDICTOR: u32 = 4;
    pub const CHAOS_AGENT: u32 = 8;
    pub const CROWN: u32 = 16;
}
pub mod estate {
    pub const IDLE: u8 = 0;
    pub const OPEN: u8 = 1;
    pub const CLOSED: u8 = 2;
    pub const DONE: u8 = 3;
}
pub mod entry {
    pub const COMMITTED: u8 = 0;
    pub const REVEALED: u8 = 1;
    pub const FORFEITED: u8 = 2;
    pub const REFUNDED: u8 = 3;
}

// ------------------------------------------------------------------ accounts

#[zero_copy]
#[repr(C)]
pub struct SealedEntry {
    pub wallet: Pubkey,
    pub amount: u64,
    pub hash: [u8; 32],
    pub outcome: u8,
    pub state: u8,
    pub _pad: [u8; 6],
}

/// Public stakes + commitments of the arena's sealed Champion bets (devnet-as, delegated, reused per match).
#[account(zero_copy)]
#[repr(C)]
pub struct SealedEscrow {
    pub arena: Pubkey,
    pub match_account: Pubkey,
    pub closed_at: i64,
    pub pool: u64,
    pub forfeit: u64,
    pub n: u8,
    pub n_revealed: u8,
    pub state: u8,
    pub n_forfeit: u8,
    pub _pad: [u8; 4],
    pub entries: [SealedEntry; SEALED_MAX],
}

#[zero_copy]
#[repr(C)]
pub struct BookEntry {
    pub wallet: Pubkey,
    pub amount: u64,
    pub salt: [u8; 16],
    pub outcome: u8,
    pub _pad: [u8; 7],
}

/// The private picks (TEE validator, delegated, private until `BOOK_CLOSE`).
#[account(zero_copy)]
#[repr(C)]
pub struct SealedBook {
    pub arena: Pubkey,
    pub match_account: Pubkey,
    pub n: u8,
    pub closed: u8,
    pub _pad: [u8; 6],
    pub entries: [BookEntry; SEALED_MAX],
}

#[zero_copy]
#[repr(C)]
pub struct BoardEntry {
    pub wallet: Pubkey,
    pub score: i64,
}

#[account(zero_copy)]
#[repr(C)]
pub struct Season {
    pub id: u32,
    pub status: u8,
    pub trophies_minted: u8,
    pub recent_i: u8,
    pub test: u8,
    pub starts_at: i64,
    pub ends_at: i64,
    pub n_matches: u32,
    pub n_players: u32,
    pub top_champions: [BoardEntry; BOARD],
    pub top_predictors: [BoardEntry; BOARD],
    pub top_drop_mvps: [BoardEntry; BOARD],
    pub recent: [Pubkey; 8],
}

#[account(zero_copy)]
#[repr(C)]
pub struct PlayerSeason {
    pub wallet: Pubkey,
    pub season: u32,
    pub achievements: u32,
    pub net_market_rush: i64,
    pub crowns: u16,
    pub finals: u16,
    pub rounds_survived: u16,
    pub matches: u16,
    pub bets_won: u16,
    pub bets_lost: u16,
    pub drops_bought: u16,
    pub ring_i: u8,
    pub _pad: u8,
    pub ring: [Pubkey; RING],
    pub ring_at: [i64; RING],
}

#[account(zero_copy)]
#[repr(C)]
pub struct PlayerProfile {
    pub wallet: Pubkey,
    pub crowns: u32,
    pub achievements: u32,
    pub first_crown_match: Pubkey,
}

const _: () = assert!(core::mem::size_of::<SealedEntry>() == 80);
const _: () = assert!(core::mem::size_of::<SealedEscrow>() == 96 + 80 * SEALED_MAX);
const _: () = assert!(core::mem::size_of::<BookEntry>() == 64);
const _: () = assert!(core::mem::size_of::<SealedBook>() == 72 + 64 * SEALED_MAX);
const _: () = assert!(core::mem::size_of::<Season>() == 32 + 3 * 40 * BOARD + 256);
const _: () = assert!(core::mem::size_of::<PlayerSeason>() == 64 + 40 * RING);
const _: () = assert!(core::mem::size_of::<PlayerProfile>() == 72);

// ------------------------------------------------------------------ pure helpers (unit-tested)

/// `sha256(outcome ‖ amount_le ‖ salt ‖ wallet)` (doc 19 P6.1: commit = outcome ‖ amount ‖ salt ‖ bettor).
pub fn commitment(outcome: u8, amount: u64, salt: &[u8; 16], wallet: &Pubkey) -> [u8; 32] {
    hashv(&[&[outcome], &amount.to_le_bytes(), salt, wallet.as_ref()]).to_bytes()
}

/// Days since 1970-01-01 → (year, month, day) (H. Hinnant's civil_from_days).
pub fn civil(days: i64) -> (i64, u32, u32) {
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let m = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    (if m <= 2 { y + 1 } else { y }, m, d)
}

fn days_from_civil(y: i64, m: u32, d: u32) -> i64 {
    let y = if m <= 2 { y - 1 } else { y };
    let era = y.div_euclid(400);
    let yoe = y - era * 400;
    let mp = if m > 2 { m - 3 } else { m + 9 } as i64;
    let doy = (153 * mp + 2) / 5 + d as i64 - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146_097 + doe - 719_468
}

/// ISO week of a unix time: (season id = year·100 + week, Monday 00:00 UTC, next Monday).
pub fn iso_week(ts: i64) -> (u32, i64, i64) {
    let days = ts.div_euclid(86_400);
    let wd = (days + 3).rem_euclid(7); // Monday = 0 (1970-01-01 was a Thursday)
    let monday = days - wd;
    let thursday = monday + 3;
    let (y, _, _) = civil(thursday);
    let jan1 = days_from_civil(y, 1, 1);
    let week = (thursday - jan1) / 7 + 1;
    ((y * 100 + week) as u32, monday * 86_400, (monday + 7) * 86_400)
}

/// Board update: `wallet`'s new `score` (higher = better; ≤ 0 removes it), sorted descending, ties keep order.
pub fn board_put(b: &mut [BoardEntry; BOARD], wallet: &Pubkey, score: i64) {
    let mut i = 0;
    while i < BOARD && b[i].wallet != *wallet {
        i += 1;
    }
    if i == BOARD {
        if score <= 0 {
            return;
        }
        // replace the last (lowest) entry if better or empty
        if b[BOARD - 1].wallet != Pubkey::default() && b[BOARD - 1].score >= score {
            return;
        }
        i = BOARD - 1;
    }
    b[i] = BoardEntry { wallet: *wallet, score };
    if score <= 0 {
        b[i] = BoardEntry { wallet: Pubkey::default(), score: 0 };
    }
    // bubble into place (empty entries sort last)
    let key = |e: &BoardEntry| if e.wallet == Pubkey::default() { i64::MIN } else { e.score };
    while i > 0 && key(&b[i]) > key(&b[i - 1]) {
        b.swap(i, i - 1);
        i -= 1;
    }
    while i + 1 < BOARD && key(&b[i + 1]) > key(&b[i]) {
        b.swap(i, i + 1);
        i += 1;
    }
}

/// The hash over every bet of the match's markets (k order, bet order: wallet ‖ amount ‖ outcome) and every drop
/// buy (by ‖ kind) that `write_receipt` stores and `RECORD` re-computes from the committed copies.
pub fn bets_hash(markets: &[&Market], dq: Option<&DropQueue>) -> [u8; 32] {
    let mut h = [0u8; 32];
    for (k, mk) in markets.iter().enumerate() {
        let mut i = 0;
        while i < mk.n_bets as usize {
            let b = mk.bets[i];
            h = hashv(&[&h, &[k as u8], b.wallet.as_ref(), &b.amount.to_le_bytes(), &[b.outcome]]).to_bytes();
            i += 1;
        }
    }
    if let Some(d) = dq {
        let mut i = 0;
        while i < d.count as usize && i < d.buys.len() {
            h = hashv(&[&h, b"drop", d.buys[i].by.as_ref(), &[d.buys[i].kind]]).to_bytes();
            i += 1;
        }
    }
    h
}

fn sealed_hash(e: &SealedEscrow) -> [u8; 32] {
    let mut h = [0u8; 32];
    let mut i = 0;
    while i < e.n as usize {
        let x = e.entries[i];
        h = hashv(&[&h, x.wallet.as_ref(), &x.amount.to_le_bytes(), &x.hash, &[x.outcome, x.state]]).to_bytes();
        i += 1;
    }
    h
}

/// `write_receipt` (econ.rs) extras: the bets hash over this match's markets + drop queue, and the sealed summary.
pub fn receipt_extras<'info>(r: &mut Receipt, markets: &'info [AccountInfo<'info>], dq: &DropQueue, mk: Pubkey, escrow: Option<&'info AccountInfo<'info>>) -> Result<()> {
    let mut loaders = Vec::with_capacity(MARKETS);
    for info in markets.iter().take(MARKETS) {
        loaders.push(AccountLoader::<Market>::try_from(info)?);
    }
    let refs: Vec<_> = loaders.iter().map(|l| l.load()).collect::<Result<Vec<_>>>()?;
    let mine: Vec<&Market> = refs.iter().filter(|m| m.match_account == mk).map(|m| &**m).collect();
    r.bets_hash = bets_hash(&mine, if dq.match_account == mk { Some(dq) } else { None });
    if let Some(info) = escrow {
        if info.owner == &crate::ID {
            let l = AccountLoader::<SealedEscrow>::try_from(info)?;
            let e = l.load()?;
            if e.match_account == mk {
                r.sealed_n = e.n;
                r.sealed_revealed = e.n_revealed;
                r.sealed_forfeits = e.n_forfeit;
                r.sealed_pool = e.pool;
                r.sealed_forfeit = e.forfeit;
                r.sealed_hash = sealed_hash(&e);
            }
        }
    }
    Ok(())
}

// ------------------------------------------------------------------ Permission Program CPIs (hand-encoded, SDK 0.18 layout)

/// `CreateEphemeralPermission` (6) / `UpdateEphemeralPermission` (7) with no members, signed by the book PDA (payer,
/// permissioned account and, for update, the authority). Bytes = SDK 0.18 `EphemeralMembersArgs::to_bytes`.
fn permission_cpi<'a>(create: bool, private: bool, book: &AccountInfo<'a>, permission: &AccountInfo<'a>, vault: &AccountInfo<'a>, magic: &AccountInfo<'a>, perm_prog: &AccountInfo<'a>, seeds: &[&[u8]]) -> Result<()> {
    keq!(perm_prog.key(), PERMISSION_PROGRAM, FallRushError::Unauthorized);
    keq!(vault.key(), EPHEMERAL_VAULT, FallRushError::Unauthorized);
    keq!(magic.key(), ephemeral_rollups_sdk::consts::MAGIC_PROGRAM_ID, FallRushError::Unauthorized);
    let (pk, _) = Pubkey::find_program_address(&[PERMISSION_SEED, book.key.as_ref()], &PERMISSION_PROGRAM);
    keq!(pk, permission.key(), FallRushError::Unauthorized);
    let mut data = (if create { 6u64 } else { 7u64 }).to_le_bytes().to_vec();
    data.push(private as u8);
    // payer = the book (its lamports came along into the rollup); create: permissioned account (signer);
    // update: authority (not a signer) + permissioned account (signer), as `authority_is_signer: false`
    let mut accounts = vec![AccountMeta::new(book.key(), true)];
    let mut infos = vec![book.clone()];
    if !create {
        accounts.push(AccountMeta::new_readonly(book.key(), false));
    }
    accounts.push(AccountMeta::new_readonly(book.key(), true));
    accounts.push(AccountMeta::new(permission.key(), false));
    accounts.push(AccountMeta::new(vault.key(), false));
    accounts.push(AccountMeta::new_readonly(magic.key(), false));
    infos.extend_from_slice(&[permission.clone(), vault.clone(), magic.clone(), perm_prog.clone()]);
    invoke_signed(&Instruction { program_id: PERMISSION_PROGRAM, accounts, data }, &infos, &[seeds])?;
    Ok(())
}

// ------------------------------------------------------------------ the `p6` entry point

pub mod op {
    /// Solana, game authority; a = arena id, b = 0 escrow / 1 book: `[config, arena, target (w), system]`.
    pub const INIT: u8 = 1;
    /// devnet-as, game authority: `[arena, escrow (w), market 0, match]` (after `open_markets`).
    pub const ESCROW_OPEN: u8 = 2;
    /// devnet-as, bettor / session; a = amount, data = commitment: `[chips (w), escrow (w), market 0 (w), match, arena,
    /// world, (st)]`.
    pub const COMMIT: u8 = 3;
    /// TEE, game authority; data = the match key: `[config, book (w), permission (w), vault (w), magic, permission
    /// program]`; (re)makes the book private (no members).
    pub const BOOK_OPEN: u8 = 4;
    /// TEE, bettor / session; a = outcome, b = amount, data[0..16] = salt: `[book (w), wallet, (st)]`.
    pub const PICK: u8 = 5;
    /// TEE, game authority: `[config, book (w), permission (w), vault (w), magic, permission program]`; book public.
    pub const BOOK_CLOSE: u8 = 6;
    /// devnet-as, permissionless: `[escrow (w), market 0, match, arena, world]` once the Champion window closed.
    pub const CLOSE: u8 = 7;
    /// devnet-as, permissionless; a = entry, b = outcome, data[0..16] = salt: `[escrow (w), market 0 (w), match]`.
    pub const REVEAL: u8 = 8;
    /// devnet-as, permissionless: `[escrow (w), market 0 (w), match]`.
    pub const FINISH: u8 = 9;
    /// Solana; a = season id, b = test-season length (s): `[config, season (w), system]`.
    pub const OPEN_SEASON: u8 = 10;
    /// Solana, permissionless: `[match, season (w), drop copy, market copies 0..4, profile (w) or any, player seasons
    /// (w)…]`.
    pub const RECORD: u8 = 11;
    /// Solana, any payer; a = season id: `[player season (w), wallet, system]`.
    pub const INIT_PSEASON: u8 = 12;
    /// Solana, any payer: `[profile (w), wallet, system]`.
    pub const INIT_PROFILE: u8 = 13;
    /// Solana, permissionless: `[season (w)]` after `ends_at`.
    pub const CLOSE_SEASON: u8 = 14;
    /// Solana, permissionless; a = board (0 champions, 1 predictors, 2 drop MVPs): `[season (w), asset (w), owner,
    /// treasury (w), assets, mpl_core, system]`.
    pub const MINT_TROPHY: u8 = 15;
    /// devnet-as, any payer: `[markets / drop queue of the arena (w)…, magic_context (w), magic_program]`; commits them
    /// to Solana (the copies `RECORD` reads); a few per transaction (validator intent size limit).
    pub const COMMIT_ECON: u8 = 16;
}

fn acc<'a, 'info>(accs: &'a [AccountInfo<'info>], i: usize) -> Result<&'a AccountInfo<'info>> {
    accs.get(i).ok_or(error!(FallRushError::InvalidSlot))
}
fn session_at<'info>(accs: &'info [AccountInfo<'info>], i: usize) -> Result<Option<Account<'info, session_keys::SessionTokenV2>>> {
    match accs.get(i) {
        Some(info) if info.owner == &session_keys::ID => Ok(Some(Account::try_from(info)?)),
        _ => Ok(None),
    }
}
fn config<'info>(info: &'info AccountInfo<'info>) -> Result<Account<'info, GameConfig>> {
    keq!(econ::pda(&[CONFIG_SEED]).0, info.key(), FallRushError::Unauthorized);
    Account::try_from(info)
}
#[inline(never)]
fn load_match<'info>(info: &'info AccountInfo<'info>) -> Result<Box<Account<'info, Match>>> {
    let m: Box<Account<Match>> = Box::new(Account::try_from(info)?);
    keq!(econ::pda(&[MATCH_SEED, &m.match_id]).0, info.key(), FallRushError::Unauthorized);
    Ok(m)
}

pub fn dispatch<'info>(signer: &Signer<'info>, accs: &'info [AccountInfo<'info>], o: u8, a: u64, b: u64, data: [u8; 32]) -> Result<()> {
    match o {
        op::INIT => op_init(signer, accs, a, b),
        op::ESCROW_OPEN => op_escrow_open(signer, accs),
        op::COMMIT => op_commit(signer, accs, a, data),
        op::BOOK_OPEN => op_book(signer, accs, true, data),
        op::PICK => op_pick(signer, accs, a, b, data),
        op::BOOK_CLOSE => op_book(signer, accs, false, data),
        op::CLOSE => op_close(accs),
        op::REVEAL => op_reveal(accs, a, b, data),
        op::FINISH => op_finish(accs),
        op::OPEN_SEASON => op_open_season(signer, accs, a, b),
        op::RECORD => op_record(accs),
        op::INIT_PSEASON => op_init_small(signer, accs, a, true),
        op::INIT_PROFILE => op_init_small(signer, accs, a, false),
        op::CLOSE_SEASON => op_close_season(accs),
        op::MINT_TROPHY => op_mint_trophy(accs, a),
        op::COMMIT_ECON => op_commit_econ(signer, accs, a),
        _ => err!(FallRushError::InvalidSlot),
    }
}

#[inline(never)]
fn op_init<'info>(signer: &Signer<'info>, accs: &'info [AccountInfo<'info>], a: u64, b: u64) -> Result<()> {
    let cfg = config(acc(accs, 0)?)?;
    keq!(signer.key(), cfg.game_authority, FallRushError::Unauthorized);
    let id = a as u8;
    let arena = acc(accs, 1)?;
    keq!(econ::pda(&[sim::ARENA_SEED, &[id]]).0, arena.key(), FallRushError::Unauthorized);
    let (target, system) = (acc(accs, 2)?, acc(accs, 3)?);
    let ak = arena.key();
    let seed = if b == 0 { ESCROW_SEED } else { BOOK_SEED };
    let (k, bump) = econ::pda(&[seed, ak.as_ref()]);
    keq!(k, target.key(), FallRushError::Unauthorized);
    let space = 8 + if b == 0 { core::mem::size_of::<SealedEscrow>() } else { core::mem::size_of::<SealedBook>() } as u64;
    econ::create_pda(&signer.to_account_info(), target, system, &[seed, ak.as_ref(), &[bump]], space, &crate::ID)?;
    if b == 0 {
        target.try_borrow_mut_data()?[..8].copy_from_slice(SealedEscrow::DISCRIMINATOR);
        AccountLoader::<SealedEscrow>::try_from(target)?.load_mut()?.arena = ak;
    } else {
        target.try_borrow_mut_data()?[..8].copy_from_slice(SealedBook::DISCRIMINATOR);
        AccountLoader::<SealedBook>::try_from(target)?.load_mut()?.arena = ak;
        // the book pays its EphemeralPermission's ER rent from its own lamports (it carries them into the rollup)
        anchor_lang::solana_program::program::invoke(
            &anchor_lang::solana_program::system_instruction::transfer(signer.key, target.key, PERMISSION_RENT * 4),
            &[signer.to_account_info(), target.clone(), system.clone()],
        )?;
    }
    Ok(())
}

#[inline(never)]
fn op_escrow_open<'info>(signer: &Signer<'info>, accs: &'info [AccountInfo<'info>]) -> Result<()> {
    let arena_i = acc(accs, 0)?;
    let al = AccountLoader::<sim::Arena>::try_from(arena_i)?;
    let ar = al.load()?;
    keq!(ar.authority, signer.key(), FallRushError::Unauthorized);
    let el = AccountLoader::<SealedEscrow>::try_from(acc(accs, 1)?)?;
    let mut e = el.load_mut()?;
    keq!(e.arena, arena_i.key(), FallRushError::BadArenaState);
    require!(e.state == estate::IDLE || e.state == estate::DONE || e.n == 0, FallRushError::SealedState);
    let ml = AccountLoader::<Market>::try_from(acc(accs, 2)?)?;
    let mk = ml.load()?;
    keq!(econ::pda(&[econ::MARKET_SEED, &[ar.id], &[0]]).0, acc(accs, 2)?.key(), FallRushError::InvalidSlot);
    let m = acc(accs, 3)?.key();
    keq!(ar.match_account, m, FallRushError::BadArenaState);
    keq!(mk.match_account, m, FallRushError::MarketClosed);
    e.match_account = m;
    e.closed_at = 0;
    e.pool = 0;
    e.forfeit = 0;
    e.n = 0;
    e.n_revealed = 0;
    e.n_forfeit = 0;
    e.state = estate::OPEN;
    e.entries = [SealedEntry { wallet: Pubkey::default(), amount: 0, hash: [0; 32], outcome: 0, state: 0, _pad: [0; 6] }; SEALED_MAX];
    Ok(())
}

#[inline(never)]
fn op_commit<'info>(signer: &Signer<'info>, accs: &'info [AccountInfo<'info>], amount: u64, hash: [u8; 32]) -> Result<()> {
    let me = signer.key();
    let cl = AccountLoader::<ChipBalance>::try_from(acc(accs, 0)?)?;
    let mut c = cl.load_mut()?;
    acts_for(&me, &c.wallet, session_at(accs, 6)?.as_ref())?;
    let el = AccountLoader::<SealedEscrow>::try_from(acc(accs, 1)?)?;
    let mut e = el.load_mut()?;
    let ml = AccountLoader::<Market>::try_from(acc(accs, 2)?)?;
    let mut mk = ml.load_mut()?;
    let m = load_match(acc(accs, 3)?)?;
    require!(mk.kind == mkind::CHAMPION, FallRushError::InvalidSlot);
    require!(e.state == estate::OPEN && e.match_account == m.key() && mk.match_account == m.key() && e.arena == mk.arena, FallRushError::SealedState);
    let arena_i = acc(accs, 4)?;
    keq!(mk.arena, arena_i.key(), FallRushError::BadArenaState);
    let al = AccountLoader::<sim::Arena>::try_from(arena_i)?;
    let ar = al.load()?;
    let wl = AccountLoader::<sim::WorldAcc>::try_from(acc(accs, 5)?)?;
    let w = wl.load()?;
    keq!(w.arena, arena_i.key(), FallRushError::BadArenaState);
    let live = ar.match_account == m.key() && ar.status == sim::arena_status::RUNNING && w.round == 4;
    let f = econ::FinalView { live, rushed: live && w.round_phase >= rush_core::rules::phase::RUSH };
    econ::bet_window(&mk, &m, f, 255)?;
    require!(amount >= econ::MIN_BET, FallRushError::BetTooSmall);
    require!((e.n as usize) < SEALED_MAX, FallRushError::SealedFull);
    let wallet = c.wallet;
    econ::debit(&mut c, amount)?;
    c.bets_n = c.bets_n.wrapping_add(1);
    let i = e.n as usize;
    e.entries[i] = SealedEntry { wallet, amount, hash, outcome: 255, state: entry::COMMITTED, _pad: [0; 6] };
    e.n += 1;
    e.pool = e.pool.saturating_add(amount);
    mk._pad[SEALED_PAD] = sealed_state::PENDING;
    Ok(())
}

#[inline(never)]
fn op_book<'info>(signer: &Signer<'info>, accs: &'info [AccountInfo<'info>], open: bool, data: [u8; 32]) -> Result<()> {
    let cfg = config(acc(accs, 0)?)?;
    keq!(signer.key(), cfg.game_authority, FallRushError::Unauthorized);
    let book = acc(accs, 1)?;
    let (permission, vault, magic, perm_prog) = (acc(accs, 2)?, acc(accs, 3)?, acc(accs, 4)?, acc(accs, 5)?);
    let arena = {
        let l = AccountLoader::<SealedBook>::try_from(book)?;
        let mut bk = l.load_mut()?;
        if open {
            bk.match_account = Pubkey::new_from_array(data);
            bk.n = 0;
            bk.closed = 0;
            bk.entries = [BookEntry { wallet: Pubkey::default(), amount: 0, salt: [0; 16], outcome: 0, _pad: [0; 7] }; SEALED_MAX];
        } else {
            bk.closed = 1;
        }
        bk.arena
    };
    let (k, bump) = econ::pda(&[BOOK_SEED, arena.as_ref()]);
    keq!(k, book.key(), FallRushError::Unauthorized);
    let bb = [bump];
    let seeds: [&[u8]; 3] = [BOOK_SEED, arena.as_ref(), &bb];
    // an existing ephemeral permission has 0 lamports (its rent sits in the vault): check owner + data instead
    let create = open && (permission.owner != &PERMISSION_PROGRAM || permission.data_is_empty());
    permission_cpi(create, open, book, permission, vault, magic, perm_prog, &seeds)?;
    if create {
        // created public: make it private right away (no members: nobody can read the book)
        permission_cpi(false, true, book, permission, vault, magic, perm_prog, &seeds)?;
    }
    Ok(())
}

#[inline(never)]
fn op_pick<'info>(signer: &Signer<'info>, accs: &'info [AccountInfo<'info>], outcome: u64, amount: u64, data: [u8; 32]) -> Result<()> {
    let l = AccountLoader::<SealedBook>::try_from(acc(accs, 0)?)?;
    let mut bk = l.load_mut()?;
    let wallet = acc(accs, 1)?.key();
    acts_for(&signer.key(), &wallet, session_at(accs, 2)?.as_ref())?;
    require!(bk.closed == 0 && bk.match_account != Pubkey::default(), FallRushError::SealedState);
    require!((bk.n as usize) < SEALED_MAX, FallRushError::SealedFull);
    require!(outcome < MAX_SLOTS as u64, FallRushError::InvalidSlot);
    let mut salt = [0u8; 16];
    salt.copy_from_slice(&data[..16]);
    let i = bk.n as usize;
    bk.entries[i] = BookEntry { wallet, amount, salt, outcome: outcome as u8, _pad: [0; 7] };
    bk.n += 1;
    Ok(())
}

/// Is the Champion window of `mk` closed now (by program state)?
fn champion_closed<'info>(mk: &Market, m: &Match, arena_i: &'info AccountInfo<'info>, world_i: &'info AccountInfo<'info>) -> Result<bool> {
    let al = AccountLoader::<sim::Arena>::try_from(arena_i)?;
    let ar = al.load()?;
    let wl = AccountLoader::<sim::WorldAcc>::try_from(world_i)?;
    let w = wl.load()?;
    keq!(w.arena, arena_i.key(), FallRushError::BadArenaState);
    keq!(mk.arena, arena_i.key(), FallRushError::BadArenaState);
    let live = ar.match_account == mk.match_account && ar.status == sim::arena_status::RUNNING && w.round == 4;
    let f = econ::FinalView { live, rushed: live && w.round_phase >= rush_core::rules::phase::RUSH };
    Ok(econ::bet_window(mk, m, f, 255).is_err())
}

#[inline(never)]
fn op_close<'info>(accs: &'info [AccountInfo<'info>]) -> Result<()> {
    let el = AccountLoader::<SealedEscrow>::try_from(acc(accs, 0)?)?;
    let mut e = el.load_mut()?;
    let ml = AccountLoader::<Market>::try_from(acc(accs, 1)?)?;
    let mk = ml.load()?;
    let m = load_match(acc(accs, 2)?)?;
    require!(e.state == estate::OPEN && e.match_account == m.key() && mk.match_account == m.key() && e.arena == mk.arena, FallRushError::SealedState);
    require!(champion_closed(&mk, &m, acc(accs, 3)?, acc(accs, 4)?)?, FallRushError::SealedState);
    e.state = estate::CLOSED;
    e.closed_at = Clock::get()?.unix_timestamp;
    Ok(())
}

#[inline(never)]
fn op_reveal<'info>(accs: &'info [AccountInfo<'info>], idx: u64, outcome: u64, data: [u8; 32]) -> Result<()> {
    let el = AccountLoader::<SealedEscrow>::try_from(acc(accs, 0)?)?;
    let mut e = el.load_mut()?;
    let ml = AccountLoader::<Market>::try_from(acc(accs, 1)?)?;
    let mut mk = ml.load_mut()?;
    let m = load_match(acc(accs, 2)?)?;
    require!(e.state == estate::CLOSED && e.match_account == m.key() && mk.match_account == m.key() && e.arena == mk.arena, FallRushError::SealedState);
    let i = idx as usize;
    require!(i < e.n as usize && e.entries[i].state == entry::COMMITTED, FallRushError::SealedState);
    require!(outcome < (m.player_count as u64).min(econ::MAX_SLOTS_E as u64), FallRushError::InvalidSlot);
    let mut salt = [0u8; 16];
    salt.copy_from_slice(&data[..16]);
    let x = e.entries[i];
    require!(commitment(outcome as u8, x.amount, &salt, &x.wallet) == x.hash, FallRushError::BadReveal);
    econ::add_bet(&mut mk, &x.wallet, outcome as u8, x.amount)?;
    e.entries[i].state = entry::REVEALED;
    e.entries[i].outcome = outcome as u8;
    e.n_revealed += 1;
    Ok(())
}

#[inline(never)]
fn op_finish<'info>(accs: &'info [AccountInfo<'info>]) -> Result<()> {
    let el = AccountLoader::<SealedEscrow>::try_from(acc(accs, 0)?)?;
    let mut e = el.load_mut()?;
    let ml = AccountLoader::<Market>::try_from(acc(accs, 1)?)?;
    let mut mk = ml.load_mut()?;
    let m = load_match(acc(accs, 2)?)?;
    require!(e.match_account == m.key() && mk.match_account == m.key(), FallRushError::SealedState);
    let over = matches!(m.status, MatchStatus::Completed | MatchStatus::Aborted);
    // `CLOSE` first (it also accepts an aborted match): the reveal window runs from there
    require!(e.state == estate::CLOSED, FallRushError::SealedState);
    let pending = e.n - e.n_revealed;
    if pending > 0 {
        // unrevealed commits: only once the window passed AND the match is over (so the rule below is final)
        require!(over && Clock::get()?.unix_timestamp >= e.closed_at + REVEAL_WINDOW_S, FallRushError::SealedPending);
        let forfeit = m.status == MatchStatus::Completed && sim::chain_final(&m);
        let mut i = 0;
        while i < e.n as usize {
            if e.entries[i].state == entry::COMMITTED {
                let x = e.entries[i];
                if forfeit {
                    // forfeited to the pool (what stops committing both sides and revealing only the winner)
                    mk.pool = mk.pool.checked_add(x.amount).ok_or(FallRushError::Overflow)?;
                    e.forfeit = e.forfeit.saturating_add(x.amount);
                    e.n_forfeit += 1;
                    e.entries[i].state = entry::FORFEITED;
                } else {
                    // abort / Classic fallback: the market refunds every bet, so the stake goes back with it
                    econ::add_bet(&mut mk, &x.wallet, 0, x.amount)?;
                    e.entries[i].state = entry::REFUNDED;
                }
            }
            i += 1;
        }
    }
    e.state = estate::DONE;
    mk._pad[SEALED_PAD] = sealed_state::DONE;
    Ok(())
}

// ------------------------------------------------------------------ seasons (Solana)

fn season_pda(id: u32) -> (Pubkey, u8) {
    econ::pda(&[SEASON_SEED, &id.to_le_bytes()])
}

#[inline(never)]
fn op_open_season<'info>(signer: &Signer<'info>, accs: &'info [AccountInfo<'info>], a: u64, b: u64) -> Result<()> {
    let cfg = config(acc(accs, 0)?)?;
    let (season, system) = (acc(accs, 1)?, acc(accs, 2)?);
    let id = a as u32;
    let now = Clock::get()?.unix_timestamp;
    let (starts, ends, test) = if a >= TEST_SEASON_BASE {
        // test seasons: game authority only, explicit length (≤ 7 days), starting now
        keq!(signer.key(), cfg.game_authority, FallRushError::Unauthorized);
        require!(b > 0 && b <= 7 * 86_400, FallRushError::SeasonState);
        (now, now + b as i64, 1u8)
    } else {
        // weekly: anyone, for the current or next ISO week only
        let (cur, s, e) = iso_week(now);
        let (nxt, s2, e2) = iso_week(e);
        require!(id == cur || id == nxt, FallRushError::NotInSeason);
        if id == cur { (s, e, 0u8) } else { (s2, e2, 0u8) }
    };
    let (k, bump) = season_pda(id);
    keq!(k, season.key(), FallRushError::Unauthorized);
    require!(season.owner != &crate::ID, FallRushError::AlreadyJoined);
    let idb = id.to_le_bytes();
    econ::create_pda(&signer.to_account_info(), season, system, &[SEASON_SEED, &idb, &[bump]], 8 + core::mem::size_of::<Season>() as u64, &crate::ID)?;
    season.try_borrow_mut_data()?[..8].copy_from_slice(Season::DISCRIMINATOR);
    let l = AccountLoader::<Season>::try_from(season)?;
    let mut s = l.load_mut()?;
    s.id = id;
    s.starts_at = starts;
    s.ends_at = ends;
    s.test = test;
    Ok(())
}

#[inline(never)]
fn op_init_small<'info>(signer: &Signer<'info>, accs: &'info [AccountInfo<'info>], a: u64, pseason: bool) -> Result<()> {
    let (target, wallet, system) = (acc(accs, 0)?, acc(accs, 1)?, acc(accs, 2)?);
    let w = wallet.key();
    require!(target.owner != &crate::ID, FallRushError::AlreadyJoined);
    if pseason {
        let id = (a as u32).to_le_bytes();
        let (k, bump) = econ::pda(&[PSEASON_SEED, &id, w.as_ref()]);
        keq!(k, target.key(), FallRushError::Unauthorized);
        econ::create_pda(&signer.to_account_info(), target, system, &[PSEASON_SEED, &id, w.as_ref(), &[bump]], 8 + core::mem::size_of::<PlayerSeason>() as u64, &crate::ID)?;
        target.try_borrow_mut_data()?[..8].copy_from_slice(PlayerSeason::DISCRIMINATOR);
        let l = AccountLoader::<PlayerSeason>::try_from(target)?;
        let mut p = l.load_mut()?;
        p.wallet = w;
        p.season = a as u32;
    } else {
        let (k, bump) = econ::pda(&[PROFILE_SEED, w.as_ref()]);
        keq!(k, target.key(), FallRushError::Unauthorized);
        econ::create_pda(&signer.to_account_info(), target, system, &[PROFILE_SEED, w.as_ref(), &[bump]], 8 + core::mem::size_of::<PlayerProfile>() as u64, &crate::ID)?;
        target.try_borrow_mut_data()?[..8].copy_from_slice(PlayerProfile::DISCRIMINATOR);
        AccountLoader::<PlayerProfile>::try_from(target)?.load_mut()?.wallet = w;
    }
    Ok(())
}

/// A committed copy of a delegated zero-copy account (owned by us, or by the delegation program while delegated),
/// copied to the heap (8-byte aligned: `Market` is 3.3 KB, an sBPF stack frame 4 KB).
fn copy_of<T: bytemuck::Pod + anchor_lang::Discriminator>(info: &AccountInfo) -> Result<Vec<u64>> {
    require!(info.owner == &crate::ID || info.owner == &econ::DELEGATION_PROGRAM, FallRushError::BadCopies);
    let d = info.try_borrow_data()?;
    let n = core::mem::size_of::<T>();
    require!(n % 8 == 0 && d.len() >= 8 + n && d[..8] == *T::DISCRIMINATOR, FallRushError::BadCopies);
    let mut v = vec![0u64; n / 8];
    bytemuck::cast_slice_mut::<u64, u8>(&mut v).copy_from_slice(&d[8..8 + n]);
    Ok(v)
}
fn view<T: bytemuck::Pod>(v: &[u64]) -> &T {
    bytemuck::from_bytes(bytemuck::cast_slice(v))
}

/// What one wallet earned in one match (from the receipt + committed copies).
#[derive(Default, Clone, Copy)]
pub struct Earned {
    pub human: bool,
    pub crown: bool,
    pub final_: bool,
    pub rounds: u16,
    pub net: i64,
    pub won: u16,
    pub lost: u16,
    pub drops: u16,
}

pub fn earned(m: &Match, markets: &[&Market], dq: Option<&DropQueue>, w: &Pubkey) -> Earned {
    let mut x = Earned::default();
    let r = &m.receipt;
    let final_mode = sim::chain_final(m);
    let mut s = 0;
    while s < 12 {
        if r.human_mask >> s & 1 == 1 && r.wallets[s] == *w {
            x.human = true;
            let sl = m.slots[s];
            let out_early = sl.state == SLOT_ELIMINATED && sl.round <= 3;
            if m.current_round >= 4 && !out_early {
                x.final_ = true;
                x.rounds = 3;
            } else if sl.state == SLOT_ELIMINATED {
                x.rounds = sl.round.saturating_sub(1) as u16;
            }
            if m.champion_slot as usize == s && m.status == MatchStatus::Completed {
                x.crown = true;
                x.rounds = 4;
            }
        }
        s += 1;
    }
    for mk in markets {
        if mk.status != mstatus::PAID || mk.refund == 1 {
            continue;
        }
        let mut i = 0;
        while i < mk.n_bets as usize {
            if mk.bets[i].wallet == *w {
                let o = econ::owed(mk, i);
                x.net = x.net.saturating_add(o as i64 - mk.bets[i].amount as i64);
                if o > 0 { x.won += 1 } else { x.lost += 1 }
            }
            i += 1;
        }
    }
    if let (Some(d), true) = (dq, final_mode) {
        let mut i = 0;
        while i < d.count as usize && i < d.buys.len() {
            if d.buys[i].by == *w {
                x.drops += 1;
            }
            i += 1;
        }
    }
    x
}

#[inline(never)]
fn op_record<'info>(accs: &'info [AccountInfo<'info>]) -> Result<()> {
    let mi = acc(accs, 0)?;
    require!(mi.owner == &crate::ID, FallRushError::ReceiptNotReady); // undelegated: back on Solana
    let m = load_match(mi)?;
    let mkey = mi.key();
    require!(m.status == MatchStatus::Completed && m.receipt.flags & econ::rflag::WRITTEN != 0 && m.receipt.bets_hash != [0; 32], FallRushError::ReceiptNotReady);
    let sl = AccountLoader::<Season>::try_from(acc(accs, 1)?)?;
    let mut season = sl.load_mut()?;
    require!(season_pda(season.id).0 == acc(accs, 1)?.key() && season.status == 0, FallRushError::SeasonState);
    require!(m.completed_at >= season.starts_at && m.completed_at < season.ends_at, FallRushError::NotInSeason);
    // the committed copies: drop queue + 5 markets of this match's arena, checked against the receipt's hash
    let dqv = copy_of::<DropQueue>(acc(accs, 2)?)?;
    let dq: &DropQueue = view(&dqv);
    let arena = dq.arena;
    keq!(econ::pda(&[econ::DROPQ_SEED, arena.as_ref()]).0, acc(accs, 2)?.key(), FallRushError::BadCopies);
    let mut mkv: Vec<Vec<u64>> = Vec::with_capacity(MARKETS);
    for k in 0..MARKETS {
        let info = acc(accs, 3 + k)?;
        let v = copy_of::<Market>(info)?;
        let x: &Market = view(&v);
        keq!(econ::pda(&[econ::MARKET_SEED, &[x.id], &[k as u8]]).0, info.key(), FallRushError::BadCopies);
        keq!(x.arena, arena, FallRushError::BadCopies);
        if x.match_account == mkey {
            require!(x.status == mstatus::PAID, FallRushError::BadCopies);
            mkv.push(v);
        }
    }
    let refs: Vec<&Market> = mkv.iter().map(|v| view::<Market>(v)).collect();
    let dqm = if dq.match_account == mkey { Some(dq) } else { None };
    require!(bets_hash(&refs, dqm) == m.receipt.bets_hash, FallRushError::BadCopies);
    // match counter (best effort ring)
    if !season.recent.contains(&mkey) {
        season.n_matches = season.n_matches.saturating_add(1);
        let i = season.recent_i as usize % 8;
        season.recent[i] = mkey;
        season.recent_i = ((i + 1) % 8) as u8;
    }
    let profile_i = acc(accs, 8)?;
    let mut n = 0u32;
    for info in accs.iter().skip(9) {
        let l = AccountLoader::<PlayerSeason>::try_from(info)?;
        let mut p = l.load_mut()?;
        require!(p.season == season.id, FallRushError::NotInSeason);
        // idempotent per wallet: skip a match already in the ring; refuse one older than a full ring
        if p.ring.contains(&mkey) {
            continue;
        }
        let full = p.ring_at.iter().all(|&t| t != 0);
        let oldest = p.ring_at.iter().copied().min().unwrap_or(0);
        require!(!full || m.completed_at > oldest, FallRushError::AlreadyRecorded);
        let w = p.wallet;
        let x = earned(&m, &refs, dqm, &w);
        if p.matches == 0 && p.bets_won == 0 && p.bets_lost == 0 && p.drops_bought == 0 {
            season.n_players = season.n_players.saturating_add(1);
        }
        let mut a = 0u32;
        if x.human {
            p.matches = p.matches.saturating_add(1);
            p.rounds_survived = p.rounds_survived.saturating_add(x.rounds);
            if x.final_ {
                p.finals = p.finals.saturating_add(1);
                a |= ach::SURVIVOR3;
            }
            if x.crown {
                p.crowns = p.crowns.saturating_add(1);
                a |= ach::CROWN;
                // first crown ever: the wallet's lifetime profile (required for a human champion)
                require!(profile_i.owner == &crate::ID, FallRushError::ReceiptNotReady);
                let pl = AccountLoader::<PlayerProfile>::try_from(profile_i)?;
                let mut pr = pl.load_mut()?;
                keq!(pr.wallet, w, FallRushError::Unauthorized);
                if pr.crowns == 0 {
                    a |= ach::FIRST_CROWN;
                    pr.first_crown_match = mkey;
                }
                pr.crowns = pr.crowns.saturating_add(1);
                pr.achievements |= a;
            }
        }
        if x.won + x.lost >= 2 && x.lost == 0 {
            a |= ach::PERFECT_PREDICTOR;
        }
        if x.drops > 0 {
            a |= ach::CHAOS_AGENT;
        }
        p.net_market_rush = p.net_market_rush.saturating_add(x.net);
        p.bets_won = p.bets_won.saturating_add(x.won);
        p.bets_lost = p.bets_lost.saturating_add(x.lost);
        p.drops_bought = p.drops_bought.saturating_add(x.drops);
        p.achievements |= a;
        let i = p.ring_i as usize % RING;
        p.ring[i] = mkey;
        p.ring_at[i] = m.completed_at;
        p.ring_i = ((i + 1) % RING) as u8;
        board_put(&mut season.top_champions, &w, p.crowns as i64 * 1000 + p.finals as i64);
        board_put(&mut season.top_predictors, &w, p.net_market_rush);
        board_put(&mut season.top_drop_mvps, &w, p.drops_bought as i64);
        n += 1;
    }
    let _ = n;
    Ok(())
}

#[inline(never)]
fn op_close_season<'info>(accs: &'info [AccountInfo<'info>]) -> Result<()> {
    let l = AccountLoader::<Season>::try_from(acc(accs, 0)?)?;
    let mut s = l.load_mut()?;
    require!(s.status == 0 && Clock::get()?.unix_timestamp >= s.ends_at, FallRushError::SeasonState);
    s.status = 1;
    Ok(())
}

#[inline(never)]
fn op_mint_trophy<'info>(accs: &'info [AccountInfo<'info>], kind: u64) -> Result<()> {
    let si = acc(accs, 0)?;
    let l = AccountLoader::<Season>::try_from(si)?;
    let mut s = l.load_mut()?;
    require!(s.status == 1 && kind < 3 && s.trophies_minted >> kind & 1 == 0, FallRushError::NothingToMint);
    let top = [s.top_champions[0], s.top_predictors[0], s.top_drop_mvps[0]][kind as usize];
    require!(top.wallet != Pubkey::default() && top.score > 0, FallRushError::NothingToMint);
    let (asset, owner, treasury, assets_auth, core, system) = (acc(accs, 1)?, acc(accs, 2)?, acc(accs, 3)?, acc(accs, 4)?, acc(accs, 5)?, acc(accs, 6)?);
    keq!(owner.key(), top.wallet, FallRushError::Unauthorized);
    keq!(core.key(), econ::MPL_CORE, FallRushError::Unauthorized);
    let sk = si.key();
    let kb = [kind as u8];
    let (ak, ab) = econ::pda(&[TROPHY_SEED, sk.as_ref(), &kb]);
    keq!(ak, asset.key(), FallRushError::Unauthorized);
    let (tk, tb) = econ::pda(&[econ::TREASURY_SEED]);
    keq!(tk, treasury.key(), FallRushError::Unauthorized);
    keq!(econ::pda(&[econ::ASSETS_SEED]).0, assets_auth.key(), FallRushError::Unauthorized);
    let mut b1 = [0u8; 16];
    let id = dec_u32(s.id, &mut b1);
    let mut b2 = [0u8; 24];
    let score = dec_i64(top.score, &mut b2);
    let names = ["Fall Rush Season Trophy (Champion)", "Fall Rush Season Trophy (Predictor)", "Fall Rush Season Trophy (Drop MVP)"];
    let boards = ["champions", "predictors", "dropMvps"];
    let abs = [ab];
    let tbs = [tb];
    let a_seeds: [&[u8]; 4] = [TROPHY_SEED, sk.as_ref(), &kb, &abs];
    let t_seeds: [&[u8]; 2] = [econ::TREASURY_SEED, &tbs];
    econ::core_create(asset, treasury, owner, assets_auth, system, core, names[kind as usize], "data:application/json,{\"name\":\"Fall Rush Season Trophy\"}",
        &[("season", id), ("board", boards[kind as usize]), ("score", score), ("test", if s.test == 1 { "true" } else { "false" })], &[&a_seeds, &t_seeds])?;
    s.trophies_minted |= 1 << kind;
    Ok(())
}

#[inline(never)]
fn op_commit_econ<'info>(signer: &Signer<'info>, accs: &'info [AccountInfo<'info>], a: u64) -> Result<()> {
    // a = 0: `[market k / drop queue (w)…, magic_context (w), magic_program]` (sponsored commits: MagicBlock core 2.0
    // stops these after 10 per account).
    // a = 1 (fee vault): `[market k / drop queue (w)…, drop queue payer (w), magic_context (w), magic_fee_vault (w),
    // magic_program]`: the arena's delegated DropQueue PDA pays through the validator's fee vault (no 10-commit cap).
    // One intent per transaction must fit a base transaction (validator limit), so the keeper commits a few at a time.
    let n = accs.len();
    let fixed = if a == 1 { 4 } else { 2 };
    require!(a <= 1 && n >= fixed + 1 && n <= fixed + MARKETS + 1, FallRushError::InvalidSlot);
    for info in &accs[..n - fixed] {
        let d = info.try_borrow_data()?;
        require!(d.len() >= 8, FallRushError::InvalidSlot);
        if d[..8] == *Market::DISCRIMINATOR {
            drop(d);
            let l = AccountLoader::<Market>::try_from(info)?;
            let x = l.load()?;
            let k = match x.kind { mkind::CHAMPION => 0u8, mkind::FINAL_WINNER => 1, _ => x.round + 1 };
            keq!(econ::pda(&[econ::MARKET_SEED, &[x.id], &[k]]).0, info.key(), FallRushError::InvalidSlot);
        } else {
            require!(d[..8] == *DropQueue::DISCRIMINATOR, FallRushError::InvalidSlot);
            drop(d);
            let l = AccountLoader::<DropQueue>::try_from(info)?;
            let arena = l.load()?.arena;
            keq!(econ::pda(&[econ::DROPQ_SEED, arena.as_ref()]).0, info.key(), FallRushError::InvalidSlot);
        }
    }
    if a == 0 {
        return crate::magic::commit(&signer.to_account_info(), &accs[n - 2], &accs[n - 1], &accs[..n - 2], false);
    }
    let payer = &accs[n - 4];
    let arena = {
        let d = payer.try_borrow_data()?;
        require!(d.len() >= 40 && d[..8] == *DropQueue::DISCRIMINATOR, FallRushError::InvalidSlot);
        Pubkey::new_from_array(d[8..40].try_into().unwrap())
    };
    let (pk, bump) = econ::pda(&[econ::DROPQ_SEED, arena.as_ref()]);
    keq!(pk, payer.key(), FallRushError::InvalidSlot);
    let bs = [bump];
    let seeds: [&[u8]; 3] = [econ::DROPQ_SEED, arena.as_ref(), &bs];
    crate::magic::commit_with_fee_vault(payer, &seeds, &accs[n - 3], &accs[n - 2], &accs[n - 1], &accs[..n - 4])
}

fn dec_u32(v: u32, out: &mut [u8; 16]) -> &str {
    let mut t = [0u8; 16];
    let (mut n, mut x) = (0, v);
    loop {
        t[n] = b'0' + (x % 10) as u8;
        n += 1;
        x /= 10;
        if x == 0 { break; }
    }
    for i in 0..n { out[i] = t[n - 1 - i]; }
    core::str::from_utf8(&out[..n]).unwrap_or("0")
}

fn dec_i64(v: i64, out: &mut [u8; 24]) -> &str {
    let neg = v < 0;
    let mut x = v.unsigned_abs();
    let mut t = [0u8; 24];
    let mut n = 0;
    loop {
        t[n] = b'0' + (x % 10) as u8;
        n += 1;
        x /= 10;
        if x == 0 { break; }
    }
    let mut o = 0;
    if neg { out[0] = b'-'; o = 1; }
    for i in 0..n { out[o + i] = t[n - 1 - i]; }
    core::str::from_utf8(&out[..o + n]).unwrap_or("0")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn iso_weeks() {
        // 2026-11-23 (Mon) 14:00 UTC → week 48 of 2026
        let (id, s, e) = iso_week(1_795_442_400);
        assert_eq!(id, 202648);
        assert_eq!(e - s, 7 * 86_400);
        assert_eq!(s % 86_400, 0);
        // 2021-01-03 (Sun) is ISO 2020-W53; 2021-01-04 (Mon) is 2021-W01
        assert_eq!(iso_week(1_609_632_000).0, 202053);
        assert_eq!(iso_week(1_609_718_400).0, 202101);
        // 2024-12-30 (Mon) is ISO 2025-W01
        assert_eq!(iso_week(1_735_516_800).0, 202501);
        assert_eq!(civil(0), (1970, 1, 1));
    }

    #[test]
    fn board_sorted_and_capped() {
        let mut b = [BoardEntry { wallet: Pubkey::default(), score: 0 }; BOARD];
        for i in 1..=15u8 {
            board_put(&mut b, &Pubkey::new_from_array([i; 32]), i as i64 * 10);
        }
        assert_eq!(b[0].score, 150);
        assert_eq!(b[9].score, 60);
        board_put(&mut b, &Pubkey::new_from_array([7; 32]), 1000); // not on the board (70 fell off)
        assert_eq!(b[0].score, 1000);
        board_put(&mut b, &Pubkey::new_from_array([15; 32]), 5); // drops from the top to the bottom
        assert_eq!(b[9].wallet, Pubkey::new_from_array([15; 32]));
        board_put(&mut b, &Pubkey::new_from_array([15; 32]), -3); // ≤ 0 removes it
        assert!(b.iter().all(|e| e.wallet != Pubkey::new_from_array([15; 32])));
        for i in 1..BOARD {
            let k = |e: &BoardEntry| if e.wallet == Pubkey::default() { i64::MIN } else { e.score };
            assert!(k(&b[i - 1]) >= k(&b[i]));
        }
    }

    #[test]
    fn commitment_binds_every_field() {
        let w = Pubkey::new_from_array([9; 32]);
        let s = [3u8; 16];
        let h = commitment(2, 5_000_000, &s, &w);
        assert_ne!(h, commitment(3, 5_000_000, &s, &w));
        assert_ne!(h, commitment(2, 5_000_001, &s, &w));
        assert_ne!(h, commitment(2, 5_000_000, &[4u8; 16], &w));
        assert_ne!(h, commitment(2, 5_000_000, &s, &Pubkey::new_from_array([8; 32])));
        let mut d = [0u8; 16];
        assert_eq!(dec_u32(202648, &mut d), "202648");
        let mut d2 = [0u8; 24];
        assert_eq!(dec_i64(-1_500_000, &mut d2), "-1500000");
    }
}
