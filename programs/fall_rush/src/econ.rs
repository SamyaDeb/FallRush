//! P4: the economy (doc 19 P4, prompt `docs/prompts/P4-PROMPT.md`): RUSH chips, Rush Markets, Chaos Drop purchases,
//! the match receipt and its rewards (Crown Capsule + VRF capsule drops as Metaplex Core assets).
//!
//! **Chips (doc 15 OC-12):** the Ephemeral SPL spike failed (0/5 round trips, `docs/proof/p4/espl-spike.json`), so
//! chips are a program-internal [`ChipBalance`] PDA per wallet, delegated to the rollup. The faucet credits it;
//! bets and drops move chips between balances and program-held vaults (a [`Market`]'s pool, the [`DropQueue`] pot)
//! inside the rollup. `cashout` moves chips out and commits the balance; `mint_cashout` on Solana mints exactly the
//! committed, not yet minted amount of the real SPL `RUSH` mint (mint authority = PDA `["rush_mint_auth"]`).
//! Devnet test chips only, no real-money value (OC-2).
//!
//! **Hot path:** nothing here runs inside `tick()` except reading the drop queue (`sim::tick`). Settlement, payouts
//! and minting are separate, permissionless, idempotent instructions (keeper-driven).

use anchor_lang::prelude::*;
use anchor_lang::solana_program::instruction::{AccountMeta, Instruction};
use anchor_lang::solana_program::program::{invoke, invoke_signed};
use solana_sha256_hasher::hashv;

use crate::errors::FallRushError;
use crate::sim;
use crate::state::*;

pub const RUSH_MINT_AUTH_SEED: &[u8] = b"rush_mint_auth";
pub const CHIPS_SEED: &[u8] = b"chips";
pub const CASHOUT_SEED: &[u8] = b"cashout";
pub const MARKET_SEED: &[u8] = b"market";
pub const DROPQ_SEED: &[u8] = b"drops";
pub const TREASURY_SEED: &[u8] = b"treasury";
pub const ASSETS_SEED: &[u8] = b"assets";
pub const CROWN_SEED: &[u8] = b"crown";
pub const CAPSULE_SEED: &[u8] = b"capsule";

pub const TOKEN_PROGRAM: Pubkey = Pubkey::from_str_const("TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA");
pub const ATA_PROGRAM: Pubkey = Pubkey::from_str_const("ATokenGPvbdGVxr1b2hvZbsiqW5xWH25efTNsLJA8knL");
pub const MPL_CORE: Pubkey = Pubkey::from_str_const("CoREENxT6tW1HoK8ypY1SxRMZTcVPm7R94rH4PZNhX7d");
/// The RUSH devnet test mint (6 decimals, mint authority = PDA `["rush_mint_auth"]`, no freeze authority), created
/// off-chain by `tools/p4/init-mint.ts` from a keypair kept outside the repo. Pinned here so `mint_cashout` can never
/// be pointed at another mint that shares the authority PDA.
pub const RUSH_MINT: Pubkey = Pubkey::from_str_const("3s8KstqicXn1kgFyWPVq3AZXkMprDtEtT7BNTxtQccEw");
pub const DELEGATION_PROGRAM: Pubkey = Pubkey::from_str_const("DELeGGvXpWV2fqJUhqcF5ZSYMS4JTLjteaAMARRSaeSh");

pub const RUSH_DECIMALS: u8 = 6;
pub const ONE_RUSH: u64 = 1_000_000;
/// `claim_faucet`: 1,000 RUSH per wallet per 24 h.
pub const FAUCET_AMOUNT: u64 = 1_000 * ONE_RUSH;
pub const FAUCET_PERIOD_S: i64 = 86_400;
pub const MIN_BET: u64 = ONE_RUSH;
pub const MAX_BETS: usize = 64;
/// Markets per arena: Champion, FinalWinner, Qualify(1..=3).
pub const MARKETS: usize = 5;
pub const MAX_SLOTS_E: usize = 12;

pub mod mkind {
    pub const CHAMPION: u8 = 0;
    pub const FINAL_WINNER: u8 = 1;
    /// `QUALIFY + r - 1` for round r = 1..=3.
    pub const QUALIFY: u8 = 2;
}

/// P6: `Market::_pad[SEALED_PAD]` = the market's sealed state (layout unchanged: pad space, doc 15 D-LAYOUT).
pub const SEALED_PAD: usize = 0;
pub mod sealed_state {
    pub const NONE: u8 = 0;
    /// Sealed commits in the arena's `SealedEscrow` not yet revealed / finished into this market.
    pub const PENDING: u8 = 1;
    pub const DONE: u8 = 2;
}

pub mod mstatus {
    pub const IDLE: u8 = 0;
    pub const OPEN: u8 = 1;
    pub const SETTLED: u8 = 2;
    pub const PAID: u8 = 3;
}

/// Who settled a market (shown in the UI: ◆ program-settled, ● server-recorded).
pub mod label {
    pub const NONE: u8 = 0;
    /// ◆ the program computed the result (the on-chain Final).
    pub const PROGRAM: u8 = 1;
    /// ● the game authority recorded the result (Classic rounds 1–3 through the chain worker).
    pub const AUTHORITY: u8 = 2;
    /// ● refund: the Final fell back to the Classic server (P3 failure path).
    pub const CLASSIC_FALLBACK: u8 = 3;
    /// refund: the match was aborted.
    pub const ABORTED: u8 = 4;
    /// refund: nobody backed a winning outcome.
    pub const NO_WINNER: u8 = 5;
}

pub mod rflag {
    pub const WRITTEN: u8 = 1;
    pub const SEEDED: u8 = 2;
    pub const CROWN: u8 = 4;
    pub const MARKETS: u8 = 8;
    /// The champion was a bot: no Crown.
    pub const NO_CROWN: u8 = 16;
    pub const REQUESTED: u8 = 32;
}

pub mod trust {
    /// Rounds 1–3 recorded by the game authority (Classic server → chain worker).
    pub const ROUNDS_AUTHORITY: u8 = 1;
    /// The Final computed by the program (rush-core on the rollup).
    pub const FINAL_PROGRAM: u8 = 2;
    /// fullchain: every round computed by the program (`start_sim`); no round recorded by the game authority.
    pub const ALL_PROGRAM: u8 = 4;
}

/// Capsule rarity (Common 70 / Rare 22 / Epic 7 / Legendary 1).
pub mod rarity {
    pub const COMMON: u8 = 0;
    pub const RARE: u8 = 1;
    pub const EPIC: u8 = 2;
    pub const LEGENDARY: u8 = 3;
    pub const NAMES: [&str; 4] = ["Common", "Rare", "Epic", "Legendary"];
}

/// Capsule shells (`CosmeticPresets` 0..8 + 4 new P4 variants).
pub const SHELLS: [&str; 12] = ["Bolt", "Bubblegum", "Tangerine", "SodaBlue", "GrapeFizz", "SourApple", "Lemon", "Mint", "Starlight", "Lava", "Glacier", "Aurora"];

// ------------------------------------------------------------------ accounts

/// A wallet's chips on the rollup (`["chips", wallet]`, delegated).
#[account(zero_copy)]
#[repr(C)]
pub struct ChipBalance {
    pub wallet: Pubkey,
    pub balance: u64,
    pub faucet_total: u64,
    /// Chips moved out for SPL (cumulative; `mint_cashout` mints the part not yet minted).
    pub cashed_out: u64,
    pub won_total: u64,
    pub spent_total: u64,
    /// Unix time of the last faucet claim (0 = never).
    pub faucet_at: i64,
    pub bets_n: u32,
    pub drops_n: u32,
    pub bump: u8,
    pub _pad: [u8; 7],
}

#[zero_copy]
#[repr(C)]
pub struct Bet {
    pub wallet: Pubkey,
    pub amount: u64,
    pub outcome: u8,
    pub paid: u8,
    pub _pad: [u8; 6],
}

/// A Rush Market (`["market", arena id, k]`, delegated, reused across the arena's matches). Shared pool: winners split
/// the whole pool in proportion to their stake on the winning outcomes (fee 0 %), rounded down; the dust stays in
/// `vault`.
#[account(zero_copy)]
#[repr(C)]
pub struct Market {
    pub match_account: Pubkey,
    pub arena: Pubkey,
    pub pool: u64,
    pub winning_stake: u64,
    pub paid_total: u64,
    /// Rounding dust kept by the market (cumulative over matches).
    pub vault: u64,
    pub stake: [u64; MAX_SLOTS_E],
    pub winners: u16,
    pub id: u8,
    pub kind: u8,
    pub round: u8,
    pub status: u8,
    pub label: u8,
    pub n_bets: u8,
    pub n_paid: u8,
    pub refund: u8,
    pub _pad: [u8; 6],
    pub bets: [Bet; MAX_BETS],
}

#[zero_copy]
#[repr(C)]
pub struct DropBuy {
    pub by: Pubkey,
    pub tick: u32,
    pub kind: u8,
    pub _pad: [u8; 3],
}

/// Chaos Drops bought for the arena's current Final (`["drops", arena]`, delegated). `tick()` reads it (read-only) and
/// queues new entries into the World; `buy_drop` never write-locks the World. The chips go to `pot` (the drop vault,
/// reported in the receipt).
#[account(zero_copy)]
#[repr(C)]
pub struct DropQueue {
    pub arena: Pubkey,
    pub match_account: Pubkey,
    pub pot: u64,
    pub spent_total: u64,
    pub last_tick: u32,
    pub count: u8,
    pub _pad: [u8; 3],
    pub buys: [DropBuy; 8],
}

const _: () = assert!(core::mem::size_of::<ChipBalance>() == 96);
const _: () = assert!(core::mem::size_of::<Market>() == 3280);
const _: () = assert!(core::mem::size_of::<DropQueue>() == 408);

// ------------------------------------------------------------------ signer = wallet or its session key

/// The signer acts for `wallet`: it is the wallet, or a session signer with a valid `SessionTokenV2` for it.
pub fn acts_for(signer: &Pubkey, wallet: &Pubkey, token: Option<&Account<session_keys::SessionTokenV2>>) -> Result<()> {
    match token {
        Some(t) => crate::sim_session_check(t, signer, wallet),
        None => {
            keq!(*signer, *wallet, FallRushError::Unauthorized);
            Ok(())
        }
    }
}

// ------------------------------------------------------------------ chips

pub fn claim_faucet(c: &mut ChipBalance, now: i64) -> Result<()> {
    require!(c.faucet_total == 0 || now - c.faucet_at >= FAUCET_PERIOD_S, FallRushError::FaucetCooldown);
    c.balance = c.balance.checked_add(FAUCET_AMOUNT).ok_or(FallRushError::Overflow)?;
    c.faucet_total = c.faucet_total.checked_add(FAUCET_AMOUNT).ok_or(FallRushError::Overflow)?;
    c.faucet_at = now;
    Ok(())
}

pub fn debit(c: &mut ChipBalance, amount: u64) -> Result<()> {
    require!(amount > 0 && c.balance >= amount, FallRushError::InsufficientChips);
    c.balance -= amount;
    c.spent_total = c.spent_total.saturating_add(amount);
    Ok(())
}

/// Offset of `ChipBalance::cashed_out` in the account data (discriminator included).
pub const CASHED_OUT_OFFSET: usize = 8 + 32 + 16;

/// Reads a `ChipBalance` committed to Solana (owned by our program, or by the delegation program while delegated):
/// (wallet, cashed_out). The address is checked by the caller (PDA of the wallet).
pub fn committed_cashout(info: &AccountInfo) -> Option<(Pubkey, u64)> {
    if info.owner != &crate::ID && info.owner != &DELEGATION_PROGRAM {
        return None;
    }
    let d = info.try_borrow_data().ok()?;
    if d.len() < 8 + core::mem::size_of::<ChipBalance>() || d[..8] != *ChipBalance::DISCRIMINATOR {
        return None;
    }
    let w = Pubkey::new_from_array(d[8..40].try_into().ok()?);
    let v = u64::from_le_bytes(d[CASHED_OUT_OFFSET..CASHED_OUT_OFFSET + 8].try_into().ok()?);
    Some((w, v))
}

// ------------------------------------------------------------------ markets

pub fn market_reset(m: &mut Market, match_account: Pubkey, arena: Pubkey, id: u8, k: u8) {
    m.match_account = match_account;
    m.arena = arena;
    m.pool = 0;
    m.winning_stake = 0;
    m.paid_total = 0;
    m.stake = [0; MAX_SLOTS_E];
    m.winners = 0;
    m.id = id;
    m.kind = if k == 0 { mkind::CHAMPION } else if k == 1 { mkind::FINAL_WINNER } else { mkind::QUALIFY };
    m.round = if k >= 2 { k - 1 } else { 4 };
    m.status = mstatus::OPEN;
    m.label = label::NONE;
    m.n_bets = 0;
    m.n_paid = 0;
    m.refund = 0;
    m._pad = [0; 6];
    m.bets = [Bet { wallet: Pubkey::default(), amount: 0, outcome: 0, paid: 0, _pad: [0; 6] }; MAX_BETS];
}

/// What `place_bet` needs to know about the live Final (from the arena / WorldAcc, read-only).
#[derive(Clone, Copy, Default)]
pub struct FinalView {
    /// The arena runs this match's Final.
    pub live: bool,
    /// The Final reached its RUSH tick (round phase ≥ RUSH).
    pub rushed: bool,
}

/// Is the market open for a bet on `outcome` now? By program state only (Match + the arena's World), never the
/// client's clock or the keeper's choice.
pub fn bet_window(mk: &Market, m: &Match, f: FinalView, outcome: u8) -> Result<()> {
    require!(mk.status == mstatus::OPEN, FallRushError::MarketClosed);
    // P6: outcome 255 = a sealed commit (the pick is hidden): only the market's window is checked
    let sealed = outcome == 255;
    require!(sealed || (outcome as usize) < (m.player_count as usize).min(MAX_SLOTS_E), FallRushError::InvalidSlot);
    let final_mode = sim::chain_final(m);
    let over = matches!(m.status, MatchStatus::Completed | MatchStatus::Aborted);
    require!(!over, FallRushError::MarketClosed);
    let st = if sealed { SLOT_ALIVE } else { m.slots[outcome as usize].state };
    match mk.kind {
        mkind::CHAMPION => {
            // open from create_match; closes at the Final's RUSH tick (on-chain Final) or when round 4 starts
            // (hybrid: exactly as before; fullchain `start_sim`: closes when the arena's World is in round 4, so the
            // on-chain rounds 1–3 stay open)
            if m.current_round >= 4 || m.flags & sim::MATCH_FINAL_MODE != 0 || (sim::chain_all(m) && f.live) {
                require!(final_mode && f.live && !f.rushed, FallRushError::MarketClosed);
            }
            require!(st == SLOT_ALIVE || st == SLOT_QUALIFIED, FallRushError::SlotNotAlive);
        }
        mkind::FINAL_WINNER => {
            // opens at start_final, closes at the RUSH tick; outcomes = the 3 finalists
            require!(final_mode && f.live && !f.rushed, FallRushError::MarketClosed);
            require!(st == SLOT_ALIVE, FallRushError::SlotNotAlive);
        }
        _ => {
            // Qualify(r): opens when round r − 1 is resolved (r = 1: before round 1), closes at start_round(r)
            let r = mk.round;
            let open = if r == 1 {
                m.current_round == 0 && matches!(m.status, MatchStatus::Created | MatchStatus::Dealing | MatchStatus::Dealt)
            } else {
                m.current_round == r - 1 && m.status == MatchStatus::RoundResolved
            };
            require!(open, FallRushError::MarketClosed);
            require!(st == SLOT_ALIVE, FallRushError::SlotNotAlive);
        }
    }
    Ok(())
}

/// Adds a bet (aggregated per wallet and outcome; ≤ 64 records).
pub fn add_bet(mk: &mut Market, wallet: &Pubkey, outcome: u8, amount: u64) -> Result<()> {
    require!(amount >= MIN_BET, FallRushError::BetTooSmall);
    let n = mk.n_bets as usize;
    let mut i = 0;
    while i < n {
        if mk.bets[i].wallet == *wallet && mk.bets[i].outcome == outcome {
            mk.bets[i].amount = mk.bets[i].amount.checked_add(amount).ok_or(FallRushError::Overflow)?;
            break;
        }
        i += 1;
    }
    if i == n {
        require!(n < MAX_BETS, FallRushError::MarketFull);
        mk.bets[n] = Bet { wallet: *wallet, amount, outcome, paid: 0, _pad: [0; 6] };
        mk.n_bets += 1;
    }
    mk.pool = mk.pool.checked_add(amount).ok_or(FallRushError::Overflow)?;
    mk.stake[outcome as usize] = mk.stake[outcome as usize].checked_add(amount).ok_or(FallRushError::Overflow)?;
    Ok(())
}

/// Settles from recorded results (idempotent: an already settled market is a no-op `Ok(false)`).
pub fn settle(mk: &mut Market, m: &Match) -> Result<bool> {
    if mk.status != mstatus::OPEN {
        return Ok(false);
    }
    // P6: sealed commits still pending (not revealed / finished into this market)
    require!(mk._pad[SEALED_PAD] != sealed_state::PENDING, FallRushError::SealedPending);
    let final_mode = sim::chain_final(m);
    let mut winners = 0u16;
    let mut lab;
    if m.status == MatchStatus::Aborted {
        lab = label::ABORTED;
    } else {
        match mk.kind {
            mkind::CHAMPION | mkind::FINAL_WINNER => {
                require!(m.status == MatchStatus::Completed, FallRushError::RoundNotResolved);
                if final_mode && (m.champion_slot as usize) < MAX_SLOTS_E {
                    winners = 1 << m.champion_slot;
                    lab = label::PROGRAM;
                } else {
                    lab = label::CLASSIC_FALLBACK;
                }
            }
            _ => {
                let r = mk.round;
                let done = m.current_round > r || (m.current_round == r && matches!(m.status, MatchStatus::RoundResolved | MatchStatus::Completed));
                require!(done, FallRushError::RoundNotResolved);
                let mut s = 0;
                while s < (m.player_count as usize).min(MAX_SLOTS_E) {
                    let x = m.slots[s];
                    if x.state != SLOT_EMPTY && !(x.state == SLOT_ELIMINATED && x.round <= r) {
                        winners |= 1 << s;
                    }
                    s += 1;
                }
                lab = label::AUTHORITY;
            }
        }
    }
    let mut ws = 0u64;
    let mut o = 0;
    while o < MAX_SLOTS_E {
        if winners >> o & 1 == 1 {
            ws = ws.saturating_add(mk.stake[o]);
        }
        o += 1;
    }
    mk.winners = winners;
    mk.winning_stake = ws;
    mk.refund = (winners == 0 || ws == 0) as u8;
    if mk.refund == 1 && lab == label::PROGRAM || mk.refund == 1 && lab == label::AUTHORITY {
        lab = label::NO_WINNER;
    }
    mk.label = lab;
    mk.status = mstatus::SETTLED;
    if mk.n_bets == 0 {
        mk.status = mstatus::PAID;
    }
    Ok(true)
}

/// Chips owed to bet `i` of a settled market.
pub fn owed(mk: &Market, i: usize) -> u64 {
    let b = mk.bets[i];
    if mk.refund == 1 {
        return b.amount;
    }
    if mk.winners >> b.outcome & 1 == 0 || mk.winning_stake == 0 {
        return 0;
    }
    ((b.amount as u128 * mk.pool as u128) / mk.winning_stake as u128) as u64
}

/// Pays every unpaid bet whose wallet's `ChipBalance` is in `chips` (idempotent per bet).
pub fn payout<'a>(mk: &mut Market, chips: &'a [AccountInfo<'a>]) -> Result<u32> {
    require!(mk.status == mstatus::SETTLED || mk.status == mstatus::PAID, FallRushError::RoundNotResolved);
    let mut paid = 0u32;
    for info in chips {
        let loader = AccountLoader::<ChipBalance>::try_from(info)?;
        let mut c = loader.load_mut()?;
        let (pda, _) = Pubkey::find_program_address(&[CHIPS_SEED, c.wallet.as_ref()], &crate::ID);
        keq!(pda, info.key(), FallRushError::Unauthorized);
        let mut i = 0;
        while i < mk.n_bets as usize {
            if mk.bets[i].paid == 0 && mk.bets[i].wallet == c.wallet {
                let v = owed(mk, i);
                c.balance = c.balance.checked_add(v).ok_or(FallRushError::Overflow)?;
                c.won_total = c.won_total.saturating_add(v);
                mk.bets[i].paid = 1;
                mk.paid_total = mk.paid_total.saturating_add(v);
                mk.n_paid += 1;
                paid += 1;
            }
            i += 1;
        }
    }
    if mk.n_paid == mk.n_bets && mk.status == mstatus::SETTLED {
        let dust = mk.pool.saturating_sub(mk.paid_total);
        mk.vault = mk.vault.saturating_add(dust);
        mk.status = mstatus::PAID;
    }
    Ok(paid)
}

// ------------------------------------------------------------------ Chaos Drops

/// `buy_drop` checks against the arena's live Final (WorldAcc mirrors: round 4, round phase RUSH), the caps and the
/// cooldown; records the purchase. Returns the price (chips).
pub fn buy_drop(dq: &mut DropQueue, wa: &sim::WorldAcc, by: &Pubkey, k: u8) -> Result<u64> {
    use rush_core::drops;
    require!(k >= 1 && k <= drops::kind::COUNT, FallRushError::InvalidDrop);
    require!(wa.round == 4 && wa.round_phase == rush_core::rules::phase::RUSH, FallRushError::DropClosed);
    require!(dq.count < drops::MAX_PER_ROUND && (dq.count as usize) < dq.buys.len(), FallRushError::DropCap);
    require!(dq.count == 0 || wa.tick >= dq.last_tick.saturating_add(drops::COOLDOWN_TICKS), FallRushError::DropCooldown);
    let rs = sim::world_round_start(wa);
    let t30 = wa.tick as i64 - rs as i64;
    require!(drops::on_sale(k, t30), FallRushError::DropClosed);
    let price = drops::PRICE_RUSH[k as usize] * ONE_RUSH;
    let i = dq.count as usize;
    dq.buys[i] = DropBuy { by: *by, tick: wa.tick, kind: k, _pad: [0; 3] };
    dq.count += 1;
    dq.last_tick = wa.tick;
    dq.pot = dq.pot.saturating_add(price);
    dq.spent_total = dq.spent_total.saturating_add(price);
    Ok(price)
}

pub fn drop_log_hash(dq: &DropQueue) -> [u8; 32] {
    let mut h = [0u8; 32];
    let mut i = 0;
    while i < dq.count as usize && i < dq.buys.len() {
        let b = dq.buys[i];
        h = hashv(&[&h, b.by.as_ref(), &b.tick.to_le_bytes(), &[b.kind]]).to_bytes();
        i += 1;
    }
    h
}

// ------------------------------------------------------------------ capsules

/// Rarity of slot `s`'s capsule from the second VRF draw: Common 70 / Rare 22 / Epic 7 / Legendary 1 (per 100).
pub fn rarity_of(seed: &[u8; 32], s: u8) -> u8 {
    let h = hashv(&[b"fallrush-capsule", seed, &[s]]).to_bytes();
    let r = u32::from_le_bytes([h[0], h[1], h[2], h[3]]) % 100;
    if r < 70 {
        rarity::COMMON
    } else if r < 92 {
        rarity::RARE
    } else if r < 99 {
        rarity::EPIC
    } else {
        rarity::LEGENDARY
    }
}

/// Shell variant of slot `s`'s capsule (12 shells).
pub fn shell_of(seed: &[u8; 32], s: u8) -> u8 {
    let h = hashv(&[b"fallrush-shell", seed, &[s]]).to_bytes();
    h[0] % SHELLS.len() as u8
}

// ------------------------------------------------------------------ hand-built CPIs (no extra crates; D-BLOCKED)

/// System `create_account` for a PDA (signed by its seeds), paid by `payer`.
pub fn create_pda<'a>(payer: &AccountInfo<'a>, pda: &AccountInfo<'a>, system: &AccountInfo<'a>, seeds: &[&[u8]], space: u64, owner: &Pubkey) -> Result<()> {
    let lamports = Rent::get()?.minimum_balance(space as usize).saturating_sub(pda.lamports());
    let ix = anchor_lang::solana_program::system_instruction::create_account(payer.key, pda.key, lamports.max(1), space, owner);
    if pda.lamports() == 0 {
        invoke_signed(&ix, &[payer.clone(), pda.clone(), system.clone()], &[seeds])?;
    } else {
        // pre-funded address: transfer the rest, allocate, assign
        if lamports > 0 {
            invoke(&anchor_lang::solana_program::system_instruction::transfer(payer.key, pda.key, lamports), &[payer.clone(), pda.clone(), system.clone()])?;
        }
        invoke_signed(&anchor_lang::solana_program::system_instruction::allocate(pda.key, space), &[pda.clone(), system.clone()], &[seeds])?;
        invoke_signed(&anchor_lang::solana_program::system_instruction::assign(pda.key, owner), &[pda.clone(), system.clone()], &[seeds])?;
    }
    Ok(())
}

pub fn token_initialize_mint2<'a>(mint: &AccountInfo<'a>, token: &AccountInfo<'a>, authority: &Pubkey, decimals: u8) -> Result<()> {
    let mut data = vec![20u8, decimals];
    data.extend_from_slice(authority.as_ref());
    data.push(0); // no freeze authority
    invoke(&Instruction { program_id: TOKEN_PROGRAM, accounts: vec![AccountMeta::new(mint.key(), false)], data }, &[mint.clone(), token.clone()])?;
    Ok(())
}

pub fn ata_of(wallet: &Pubkey, mint: &Pubkey) -> Pubkey {
    Pubkey::find_program_address(&[wallet.as_ref(), TOKEN_PROGRAM.as_ref(), mint.as_ref()], &ATA_PROGRAM).0
}

#[allow(clippy::too_many_arguments)]
pub fn ata_create_idempotent<'a>(payer: &AccountInfo<'a>, ata: &AccountInfo<'a>, wallet: &AccountInfo<'a>, mint: &AccountInfo<'a>, system: &AccountInfo<'a>, token: &AccountInfo<'a>, ata_prog: &AccountInfo<'a>) -> Result<()> {
    let ix = Instruction {
        program_id: ATA_PROGRAM,
        accounts: vec![
            AccountMeta::new(payer.key(), true),
            AccountMeta::new(ata.key(), false),
            AccountMeta::new_readonly(wallet.key(), false),
            AccountMeta::new_readonly(mint.key(), false),
            AccountMeta::new_readonly(system.key(), false),
            AccountMeta::new_readonly(token.key(), false),
        ],
        data: vec![1],
    };
    invoke(&ix, &[payer.clone(), ata.clone(), wallet.clone(), mint.clone(), system.clone(), token.clone(), ata_prog.clone()])?;
    Ok(())
}

pub fn token_mint_to<'a>(mint: &AccountInfo<'a>, to: &AccountInfo<'a>, auth: &AccountInfo<'a>, token: &AccountInfo<'a>, amount: u64, seeds: &[&[u8]]) -> Result<()> {
    let mut data = vec![7u8];
    data.extend_from_slice(&amount.to_le_bytes());
    let ix = Instruction {
        program_id: TOKEN_PROGRAM,
        accounts: vec![AccountMeta::new(mint.key(), false), AccountMeta::new(to.key(), false), AccountMeta::new_readonly(auth.key(), true)],
        data,
    };
    invoke_signed(&ix, &[mint.clone(), to.clone(), auth.clone(), token.clone()], &[seeds])?;
    Ok(())
}

fn borsh_str(out: &mut Vec<u8>, s: &str) {
    out.extend_from_slice(&(s.len() as u32).to_le_bytes());
    out.extend_from_slice(s.as_bytes());
}

/// Metaplex Core `CreateV1` (discriminator 0) with an `Attributes` plugin (plugin type 6), built by hand from the
/// mpl-core 1.1 instruction layout (the `mpl-core` crate needs Solana 2.x crates; doc 15 OC-13). `asset` and `payer`
/// sign through their PDA seeds; `update_authority` is the program's `["assets"]` PDA.
#[allow(clippy::too_many_arguments)]
pub fn core_create<'a>(
    asset: &AccountInfo<'a>,
    payer: &AccountInfo<'a>,
    owner: &AccountInfo<'a>,
    update_authority: &AccountInfo<'a>,
    system: &AccountInfo<'a>,
    core: &AccountInfo<'a>,
    name: &str,
    uri: &str,
    attrs: &[(&str, &str)],
    signers: &[&[&[u8]]],
) -> Result<()> {
    let mut data = Vec::with_capacity(256);
    data.push(0); // CreateV1
    data.push(0); // DataState::AccountState
    borsh_str(&mut data, name);
    borsh_str(&mut data, uri);
    data.push(1); // plugins: Some
    data.extend_from_slice(&1u32.to_le_bytes()); // one PluginAuthorityPair
    data.push(6); // Plugin::Attributes
    data.extend_from_slice(&(attrs.len() as u32).to_le_bytes());
    for (k, v) in attrs {
        borsh_str(&mut data, k);
        borsh_str(&mut data, v);
    }
    data.push(0); // authority: None (default: the update authority)
    let ix = Instruction {
        program_id: MPL_CORE,
        accounts: vec![
            AccountMeta::new(asset.key(), true),
            AccountMeta::new_readonly(MPL_CORE, false), // collection: none
            AccountMeta::new_readonly(MPL_CORE, false), // authority: none (= payer)
            AccountMeta::new(payer.key(), true),
            AccountMeta::new_readonly(owner.key(), false),
            AccountMeta::new_readonly(update_authority.key(), false),
            AccountMeta::new_readonly(system.key(), false),
            AccountMeta::new_readonly(MPL_CORE, false), // log wrapper: none
        ],
        data,
    };
    invoke_signed(&ix, &[asset.clone(), payer.clone(), owner.clone(), update_authority.clone(), system.clone(), core.clone()], signers)?;
    Ok(())
}

/// Lower-case hex of `b` into `out` (no `core::fmt`: it costs ~100 KB of sBPF).
pub fn hex_into<'a>(b: &[u8], out: &'a mut [u8]) -> &'a str {
    const H: &[u8; 16] = b"0123456789abcdef";
    let mut i = 0;
    while i < b.len() && 2 * i + 1 < out.len() {
        out[2 * i] = H[(b[i] >> 4) as usize];
        out[2 * i + 1] = H[(b[i] & 15) as usize];
        i += 1;
    }
    core::str::from_utf8(&out[..2 * i]).unwrap_or("")
}

/// Decimal of a u8 into `out`.
pub fn dec_into(v: u8, out: &mut [u8; 3]) -> &str {
    let (a, b, c) = (v / 100, v / 10 % 10, v % 10);
    let mut n = 0;
    if a > 0 {
        out[n] = b'0' + a;
        n += 1;
    }
    if a > 0 || b > 0 {
        out[n] = b'0' + b;
        n += 1;
    }
    out[n] = b'0' + c;
    n += 1;
    core::str::from_utf8(&out[..n]).unwrap_or("0")
}

/// Base58 (Bitcoin alphabet) of a 32-byte key into `out` (≤ 44 chars), no `core::fmt`.
pub fn b58_into<'a>(key: &[u8; 32], out: &'a mut [u8; 44]) -> &'a str {
    const A: &[u8; 58] = b"123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    let mut digits = [0u8; 44];
    let mut n = 0usize;
    for &byte in key.iter() {
        let mut carry = byte as u32;
        let mut j = 0;
        while j < n {
            carry += (digits[j] as u32) << 8;
            digits[j] = (carry % 58) as u8;
            carry /= 58;
            j += 1;
        }
        while carry > 0 && n < 44 {
            digits[n] = (carry % 58) as u8;
            n += 1;
            carry /= 58;
        }
    }
    let mut z = 0;
    while z < 32 && key[z] == 0 {
        z += 1;
    }
    let mut o = 0;
    while o < z && o < 44 {
        out[o] = b'1';
        o += 1;
    }
    let mut j = n;
    while j > 0 && o < 44 {
        j -= 1;
        out[o] = A[digits[j] as usize];
        o += 1;
    }
    core::str::from_utf8(&out[..o]).unwrap_or("")
}

// ------------------------------------------------------------------ the `econ` entry point

/// `econ(op, a, b)` operations. Accounts (remaining_accounts) per op; `(st)` = an optional trailing
/// `SessionTokenV2` when the signer is the wallet's session key.
pub mod op {
    /// signer = the wallet (pays): `[chips (w), system]`.
    pub const INIT_CHIPS: u8 = 1;
    /// `[chips (w), (st)]`.
    pub const FAUCET: u8 = 2;
    /// a = amount: `[chips (w), magic_context (w), magic_program, (st)]`; commits the balance.
    pub const CASHOUT: u8 = 3;
    /// Solana, any payer: `[chips, stamp (w), rush_mint (w), mint_auth, wallet, wallet_ata (w), token, ata, system]`.
    pub const MINT_CASHOUT: u8 = 4;
    /// Solana, game authority; a = arena id, b = 0..4 market k / 5 drop queue: `[config, arena, account (w), system]`.
    pub const INIT_ECON: u8 = 5;
    /// ER, game authority: `[arena, drop_queue (w), market 0..4 (w), match]`.
    pub const OPEN_MARKETS: u8 = 6;
    /// a = outcome (slot), b = amount: `[chips (w), market (w), match, arena, world, (st)]`.
    pub const PLACE_BET: u8 = 7;
    /// Permissionless: `[market (w), match, chips… (w)]` settles (once) and pays those wallets' bets.
    pub const SETTLE: u8 = 8;
    /// a = kind: `[chips (w), drop_queue (w), arena, world, (st)]`.
    pub const BUY_DROP: u8 = 9;
    /// Permissionless: `[match (w), arena, world, drop_queue, market 0..4, map 0..4]`.
    pub const WRITE_RECEIPT: u8 = 10;
    /// Solana, permissionless; a = 0 crown / 1 + slot capsule: `[match (w), asset (w), owner, treasury (w),
    /// assets_auth, mpl_core, system]`.
    pub const MINT_REWARDS: u8 = 11;
}

fn acc<'a, 'info>(accs: &'a [AccountInfo<'info>], i: usize) -> Result<&'a AccountInfo<'info>> {
    accs.get(i).ok_or(error!(FallRushError::InvalidSlot))
}

pub fn pda(seeds: &[&[u8]]) -> (Pubkey, u8) {
    Pubkey::find_program_address(seeds, &crate::ID)
}

/// The optional session token at `accs[i]` (owned by the session-keys program).
fn session_at<'info>(accs: &'info [AccountInfo<'info>], i: usize) -> Result<Option<Account<'info, session_keys::SessionTokenV2>>> {
    match accs.get(i) {
        Some(info) if info.owner == &session_keys::ID => Ok(Some(Account::try_from(info)?)),
        _ => Ok(None),
    }
}

/// The `Match` on the heap (≈ 1.4 KB: sBPF frames are 4 KB), in its own frame.
#[inline(never)]
fn load_match<'info>(info: &'info AccountInfo<'info>) -> Result<Box<Account<'info, Match>>> {
    let m: Box<Account<Match>> = Box::new(Account::try_from(info)?);
    keq!(pda(&[MATCH_SEED, &m.match_id]).0, info.key(), FallRushError::Unauthorized);
    Ok(m)
}

fn config<'info>(info: &'info AccountInfo<'info>) -> Result<Account<'info, GameConfig>> {
    keq!(pda(&[CONFIG_SEED]).0, info.key(), FallRushError::Unauthorized);
    Account::try_from(info)
}

pub fn dispatch<'info>(signer: &Signer<'info>, accs: &'info [AccountInfo<'info>], o: u8, a: u64, b: u64) -> Result<()> {
    // one non-inlined function per op: each keeps its own 4 KB sBPF stack frame
    match o {
        op::INIT_CHIPS => op_init_chips(signer, accs, a, b),
        op::FAUCET => op_faucet(signer, accs, a, b),
        op::CASHOUT => op_cashout(signer, accs, a, b),
        op::MINT_CASHOUT => op_mint_cashout(signer, accs, a, b),
        op::INIT_ECON => op_init_econ(signer, accs, a, b),
        op::OPEN_MARKETS => op_open_markets(signer, accs, a, b),
        op::PLACE_BET => op_place_bet(signer, accs, a, b),
        op::SETTLE => op_settle(signer, accs, a, b),
        op::BUY_DROP => op_buy_drop(signer, accs, a, b),
        op::WRITE_RECEIPT => write_receipt(accs),
        op::MINT_REWARDS => mint_rewards(signer, accs, a.min(255) as u8),
        _ => err!(FallRushError::InvalidSlot),
    }
}

#[inline(never)]
fn op_init_chips<'info>(signer: &Signer<'info>, accs: &'info [AccountInfo<'info>], a: u64, b: u64) -> Result<()> {
    let me = signer.key();
    let _ = (&me, a, b);
    let chips = acc(accs, 0)?;
    let (k, bump) = pda(&[CHIPS_SEED, me.as_ref()]);
    keq!(k, chips.key(), FallRushError::Unauthorized);
    require!(chips.owner != &crate::ID, FallRushError::AlreadyJoined);
    create_pda(&signer.to_account_info(), chips, acc(accs, 1)?, &[CHIPS_SEED, me.as_ref(), &[bump]], 8 + core::mem::size_of::<ChipBalance>() as u64, &crate::ID)?;
    let mut d = chips.try_borrow_mut_data()?;
    d[..8].copy_from_slice(ChipBalance::DISCRIMINATOR);
    d[8..40].copy_from_slice(me.as_ref());
    d[8 + 88] = bump;
    Ok(())
}

#[inline(never)]
fn op_faucet<'info>(signer: &Signer<'info>, accs: &'info [AccountInfo<'info>], a: u64, b: u64) -> Result<()> {
    let me = signer.key();
    let _ = (&me, a, b);
    let l = AccountLoader::<ChipBalance>::try_from(acc(accs, 0)?)?;
    let mut c = l.load_mut()?;
    acts_for(&me, &c.wallet, session_at(accs, 1)?.as_ref())?;
    claim_faucet(&mut c, Clock::get()?.unix_timestamp)
}

#[inline(never)]
fn op_cashout<'info>(signer: &Signer<'info>, accs: &'info [AccountInfo<'info>], a: u64, b: u64) -> Result<()> {
    let me = signer.key();
    let _ = (&me, a, b);
    let chips = acc(accs, 0)?;
    {
        let l = AccountLoader::<ChipBalance>::try_from(chips)?;
        let mut c = l.load_mut()?;
        acts_for(&me, &c.wallet, session_at(accs, 3)?.as_ref())?;
        require!(a > 0 && c.balance >= a, FallRushError::InsufficientChips);
        c.balance -= a;
        c.cashed_out = c.cashed_out.saturating_add(a);
    }
    let ctx_acc = acc(accs, 1)?;
    let prog = acc(accs, 2)?;
    crate::magic::commit(&signer.to_account_info(), ctx_acc, prog, &[chips.clone()], false)?;
    Ok(())
}

#[inline(never)]
fn op_mint_cashout<'info>(signer: &Signer<'info>, accs: &'info [AccountInfo<'info>], a: u64, b: u64) -> Result<()> {
    let me = signer.key();
    let _ = (&me, a, b);
    let (chips, stamp, mint, auth, wallet, ata, token, atap, system) = (acc(accs, 0)?, acc(accs, 1)?, acc(accs, 2)?, acc(accs, 3)?, acc(accs, 4)?, acc(accs, 5)?, acc(accs, 6)?, acc(accs, 7)?, acc(accs, 8)?);
    let w = wallet.key();
    keq!(pda(&[CHIPS_SEED, w.as_ref()]).0, chips.key(), FallRushError::Unauthorized);
    let (wk, cashed) = committed_cashout(chips).ok_or(error!(FallRushError::Unauthorized))?;
    keq!(wk, w, FallRushError::Unauthorized);
    let (sk, sb) = pda(&[CASHOUT_SEED, w.as_ref()]);
    keq!(sk, stamp.key(), FallRushError::Unauthorized);
    keq!(RUSH_MINT, mint.key(), FallRushError::Unauthorized);
    let (ak, ab) = pda(&[RUSH_MINT_AUTH_SEED]);
    keq!(ak, auth.key(), FallRushError::Unauthorized);
    keq!(ata.key(), ata_of(&w, &mint.key()), FallRushError::Unauthorized);
    keq!(token.key(), TOKEN_PROGRAM, FallRushError::Unauthorized);
    if stamp.owner != &crate::ID {
        create_pda(&signer.to_account_info(), stamp, system, &[CASHOUT_SEED, w.as_ref(), &[sb]], 16, &crate::ID)?;
    }
    let minted = u64::from_le_bytes(stamp.try_borrow_data()?[8..16].try_into().unwrap());
    require!(cashed > minted, FallRushError::NothingToMint);
    ata_create_idempotent(&signer.to_account_info(), ata, wallet, mint, system, token, atap)?;
    token_mint_to(mint, ata, auth, token, cashed - minted, &[RUSH_MINT_AUTH_SEED, &[ab]])?;
    let mut d = stamp.try_borrow_mut_data()?;
    d[0..8].copy_from_slice(b"FRCASHOT");
    d[8..16].copy_from_slice(&cashed.to_le_bytes());
    Ok(())
}

#[inline(never)]
fn op_init_econ<'info>(signer: &Signer<'info>, accs: &'info [AccountInfo<'info>], a: u64, b: u64) -> Result<()> {
    let me = signer.key();
    let _ = (&me, a, b);
    let cfg = config(acc(accs, 0)?)?;
    keq!(me, cfg.game_authority, FallRushError::Unauthorized);
    let (id, k) = (a as u8, b as u8);
    let arena = acc(accs, 1)?;
    keq!(pda(&[sim::ARENA_SEED, &[id]]).0, arena.key(), FallRushError::Unauthorized);
    let target = acc(accs, 2)?;
    let system = acc(accs, 3)?;
    let ak = arena.key();
    if k < MARKETS as u8 {
        let (mk, mb) = pda(&[MARKET_SEED, &[id], &[k]]);
        keq!(mk, target.key(), FallRushError::Unauthorized);
        create_pda(&signer.to_account_info(), target, system, &[MARKET_SEED, &[id], &[k], &[mb]], 8 + core::mem::size_of::<Market>() as u64, &crate::ID)?;
        target.try_borrow_mut_data()?[..8].copy_from_slice(Market::DISCRIMINATOR);
        let l = AccountLoader::<Market>::try_from(target)?;
        let mut m = l.load_mut()?;
        market_reset(&mut m, Pubkey::default(), ak, id, k);
        m.status = mstatus::IDLE;
    } else {
        let (dk, db) = pda(&[DROPQ_SEED, ak.as_ref()]);
        keq!(dk, target.key(), FallRushError::Unauthorized);
        create_pda(&signer.to_account_info(), target, system, &[DROPQ_SEED, ak.as_ref(), &[db]], 8 + core::mem::size_of::<DropQueue>() as u64, &crate::ID)?;
        target.try_borrow_mut_data()?[..8].copy_from_slice(DropQueue::DISCRIMINATOR);
        let l = AccountLoader::<DropQueue>::try_from(target)?;
        l.load_mut()?.arena = ak;
    }
    Ok(())
}

#[inline(never)]
fn op_open_markets<'info>(signer: &Signer<'info>, accs: &'info [AccountInfo<'info>], a: u64, b: u64) -> Result<()> {
    let me = signer.key();
    let _ = (&me, a, b);
    let arena_i = acc(accs, 0)?;
    let al = AccountLoader::<sim::Arena>::try_from(arena_i)?;
    let ar = al.load()?;
    keq!(ar.authority, me, FallRushError::Unauthorized);
    let id = ar.id;
    let mk = acc(accs, 7)?.key();
    keq!(ar.match_account, mk, FallRushError::BadArenaState);
    for k in 0..MARKETS {
        let info = acc(accs, 2 + k)?;
        keq!(pda(&[MARKET_SEED, &[id], &[k as u8]]).0, info.key(), FallRushError::InvalidSlot);
        let l = AccountLoader::<Market>::try_from(info)?;
        let mut m = l.load_mut()?;
        require!(m.status == mstatus::IDLE || m.status == mstatus::PAID || m.n_bets == 0, FallRushError::MarketBusy);
        market_reset(&mut m, mk, arena_i.key(), id, k as u8);
    }
    let dl = AccountLoader::<DropQueue>::try_from(acc(accs, 1)?)?;
    let mut d = dl.load_mut()?;
    keq!(d.arena, arena_i.key(), FallRushError::BadArenaState);
    d.match_account = mk;
    d.count = 0;
    d.last_tick = 0;
    d.buys = [DropBuy { by: Pubkey::default(), tick: 0, kind: 0, _pad: [0; 3] }; 8];
    drop(d);
    // P5 0.5: the Match carries an economy flag from here on, so `finalize_match` refuses it until the receipt is
    // written, and `write_receipt` refuses while any of its markets still holds an unpaid bet (keeper crash safety)
    let mut m = load_match(acc(accs, 7)?)?;
    m.flags |= sim::MATCH_ECON;
    m.exit(&crate::ID)
}

#[inline(never)]
fn op_place_bet<'info>(signer: &Signer<'info>, accs: &'info [AccountInfo<'info>], a: u64, b: u64) -> Result<()> {
    let me = signer.key();
    let _ = (&me, a, b);
    let cl = AccountLoader::<ChipBalance>::try_from(acc(accs, 0)?)?;
    let mut c = cl.load_mut()?;
    acts_for(&me, &c.wallet, session_at(accs, 5)?.as_ref())?;
    let ml = AccountLoader::<Market>::try_from(acc(accs, 1)?)?;
    let mut mk = ml.load_mut()?;
    let m = load_match(acc(accs, 2)?)?;
    keq!(mk.match_account, m.key(), FallRushError::MarketClosed);
    let arena_i = acc(accs, 3)?;
    keq!(mk.arena, arena_i.key(), FallRushError::BadArenaState);
    let al = AccountLoader::<sim::Arena>::try_from(arena_i)?;
    let ar = al.load()?;
    let wl = AccountLoader::<sim::WorldAcc>::try_from(acc(accs, 4)?)?;
    let w = wl.load()?;
    keq!(w.arena, arena_i.key(), FallRushError::BadArenaState);
    let live = ar.match_account == m.key() && ar.status == sim::arena_status::RUNNING && w.round == 4;
    let f = FinalView { live, rushed: live && w.round_phase >= rush_core::rules::phase::RUSH };
    let outcome = a.min(255) as u8;
    bet_window(&mk, &m, f, outcome)?;
    let wallet = c.wallet;
    debit(&mut c, b)?;
    c.bets_n = c.bets_n.wrapping_add(1);
    add_bet(&mut mk, &wallet, outcome, b)
}

#[inline(never)]
fn op_settle<'info>(signer: &Signer<'info>, accs: &'info [AccountInfo<'info>], a: u64, b: u64) -> Result<()> {
    let me = signer.key();
    let _ = (&me, a, b);
    let ml = AccountLoader::<Market>::try_from(acc(accs, 0)?)?;
    let mut mk = ml.load_mut()?;
    let m = load_match(acc(accs, 1)?)?;
    keq!(mk.match_account, m.key(), FallRushError::MarketClosed);
    settle(&mut mk, &m)?;
    payout(&mut mk, &accs[2..])?;
    Ok(())
}

#[inline(never)]
fn op_buy_drop<'info>(signer: &Signer<'info>, accs: &'info [AccountInfo<'info>], a: u64, b: u64) -> Result<()> {
    let me = signer.key();
    let _ = (&me, a, b);
    let arena_i = acc(accs, 2)?;
    let al = AccountLoader::<sim::Arena>::try_from(arena_i)?;
    let ar = al.load()?;
    require!(ar.status == sim::arena_status::RUNNING, FallRushError::DropClosed);
    let wl = AccountLoader::<sim::WorldAcc>::try_from(acc(accs, 3)?)?;
    let w = wl.load()?;
    keq!(w.arena, arena_i.key(), FallRushError::BadArenaState);
    let dl = AccountLoader::<DropQueue>::try_from(acc(accs, 1)?)?;
    let mut dq = dl.load_mut()?;
    keq!(dq.arena, arena_i.key(), FallRushError::BadArenaState);
    keq!(dq.match_account, ar.match_account, FallRushError::DropClosed);
    let cl = AccountLoader::<ChipBalance>::try_from(acc(accs, 0)?)?;
    let mut c = cl.load_mut()?;
    acts_for(&me, &c.wallet, session_at(accs, 4)?.as_ref())?;
    let wallet = c.wallet;
    let price = buy_drop(&mut dq, &w, &wallet, a.min(255) as u8)?;
    debit(&mut c, price)?;
    c.drops_n = c.drops_n.wrapping_add(1);
    Ok(())
}

#[inline(never)]
fn write_receipt<'info>(accs: &'info [AccountInfo<'info>]) -> Result<()> {
    let mi = acc(accs, 0)?;
    let mut m = load_match(mi)?;
    let mk = mi.key();
    let arena_i = acc(accs, 1)?;
    let al = AccountLoader::<sim::Arena>::try_from(arena_i)?;
    let ar = al.load()?;
    let wl = AccountLoader::<sim::WorldAcc>::try_from(acc(accs, 2)?)?;
    let w = wl.load()?;
    let dl = AccountLoader::<DropQueue>::try_from(acc(accs, 3)?)?;
    let dq = dl.load()?;
    keq!(w.arena, arena_i.key(), FallRushError::BadArenaState);
    keq!(dq.arena, arena_i.key(), FallRushError::BadArenaState);
    require!(accs.len() >= 4 + MARKETS + sim::MAP_SLOTS, FallRushError::InvalidSlot);
    require!(matches!(m.status, MatchStatus::Completed | MatchStatus::Aborted), FallRushError::InvalidStatus);
    keq!(ar.match_account, mk, FallRushError::BadArenaState);
    for k in 0..MARKETS {
        let info = &accs[4 + k];
        keq!(pda(&[MARKET_SEED, &[ar.id], &[k as u8]]).0, info.key(), FallRushError::InvalidSlot);
        let l = AccountLoader::<Market>::try_from(info)?;
        let x = l.load()?;
        if x.match_account == mk {
            // P5 0.5: every bet paid (status PAID), not only settled: a receipt (and so `finalize_match`, which
            // undelegates the Match that `settle` reads) never leaves unpaid winners behind
            require!(x.status == mstatus::PAID, FallRushError::ReceiptNotReady);
            m.receipt.markets[k] = MarketResult { pool: x.pool, winning_stake: x.winning_stake, winners: x.winners, label: x.label, status: x.status };
        }
    }
    let mut mh = [0u8; 32];
    for k in 0..sim::MAP_SLOTS {
        let info = &accs[4 + MARKETS + k];
        keq!(info.key(), ar.maps[k], FallRushError::BadMap);
        let d = info.try_borrow_data()?;
        require!(d.len() >= sim::MAP_HDR, FallRushError::BadMap);
        mh = hashv(&[&mh, &d[48..80]]).to_bytes();
    }
    m.receipt.map_hash = mh;
    // P6: the bets hash (what `record_season` checks the committed market copies against) + the sealed entries
    // (optional trailing `SealedEscrow`, so a P5 keeper's account list still works)
    crate::p6::receipt_extras(&mut m.receipt, &accs[4..4 + MARKETS], &dq, mk, accs.get(4 + MARKETS + sim::MAP_SLOTS))?;
    m.receipt.core_version = u16::from_le_bytes(w.core_version);
    m.receipt.human_mask = ar.human_mask;
    m.receipt.wallets = ar.wallets;
    if dq.match_account == mk {
        m.receipt.drops = dq.count;
        m.receipt.drop_log = drop_log_hash(&dq);
        let mut sp = 0u64;
        let mut i = 0;
        while i < dq.count as usize && i < dq.buys.len() {
            sp += rush_core::drops::PRICE_RUSH[(dq.buys[i].kind as usize).min(4)] * ONE_RUSH;
            i += 1;
        }
        m.receipt.drop_spent = sp;
    }
    let mut wd = sim::heap_world();
    if wd.load_from(&w.world[..w.len as usize]) {
        m.receipt.final_transcript = wd.dir.transcript;
        let res = &wd.round.result;
        let mut i = 0;
        while i < 12 {
            m.receipt.placements[i] = if i < res.n as usize { res.order[i] } else { 255 };
            i += 1;
        }
    }
    m.receipt.trust = if sim::chain_all(&m) {
        trust::FINAL_PROGRAM | trust::ALL_PROGRAM
    } else {
        trust::ROUNDS_AUTHORITY | if sim::chain_final(&m) { trust::FINAL_PROGRAM } else { 0 }
    };
    m.receipt.flags |= rflag::WRITTEN | rflag::MARKETS;
    if (m.champion_slot as usize) < 12 && ar.human_mask >> m.champion_slot & 1 == 0 {
        m.receipt.flags |= rflag::NO_CROWN;
    }
    m.exit(&crate::ID)
}

#[inline(never)]
fn mint_rewards<'info>(signer: &Signer<'info>, accs: &'info [AccountInfo<'info>], what: u8) -> Result<()> {
    let _ = signer;
    mint_one(accs, what)
}

/// P5 0.3: the escrow PDA authority (`["escrow_auth"]`, this program) that signs the finalize CPI's Magic Action, and
/// the escrow index. The escrow itself is the delegation program's `["balance", escrow_auth, [255]]` (System-owned,
/// funded on Solana by `TopUpEphemeralBalance`); `CallHandlerV2` injects it as the action's only signer.
pub const ESCROW_AUTH_SEED: &[u8] = b"escrow_auth";
pub const ESCROW_INDEX: u8 = 255;
/// Assets minted by one Magic Action at most (Crown + 3 capsules: the base transaction must fit 1,232 B); the rest is
/// minted by the keeper's permissionless `mint_rewards` retry.
pub const ACTION_MAX_ITEMS: usize = 4;

/// What `mint_rewards(what)` would still mint for this match (pre-checks, so the action never fails: a failed action
/// could revert the undelegation it rides with).
fn still_owed<'info>(part: &'info [AccountInfo<'info>], what: u8) -> bool {
    let Ok(m) = load_match(&part[0]) else { return false };
    let (f, h, minted) = (m.receipt.flags, m.receipt.human_mask, m.receipt.minted_mask);
    if f & rflag::WRITTEN == 0 || m.status != MatchStatus::Completed {
        return false;
    }
    let slot = if what == 0 { m.champion_slot } else { what - 1 };
    if slot as usize >= 12 || h >> slot & 1 == 0 || part[2].key() != m.receipt.wallets[slot as usize] {
        return false;
    }
    let ok = if what == 0 { f & (rflag::CROWN | rflag::NO_CROWN) == 0 } else { f & rflag::SEEDED != 0 && minted >> slot & 1 == 0 };
    ok && part[1].data_len() == 0 && part[1].lamports() == 0 && part[3].lamports() >= 10_000_000
}

/// P5 0.3 Magic Action handler (`mint_rewards_action`), run by the validator on Solana right after the Match's
/// undelegation. Accounts: 7 per item (`mint_rewards`' list: match, asset, owner, treasury, assets, Core, System),
/// then the injected `source_program`, `escrow_auth`, `escrow` (signer). Checks the injected escrow, then mints every
/// item still owed (the same program decision from the committed receipt as the keeper's `mint_rewards`).
#[inline(never)]
pub fn mint_rewards_action<'info>(accs: &'info [AccountInfo<'info>], whats: &[u8]) -> Result<()> {
    let n = accs.len();
    require!(!whats.is_empty() && whats.len() <= ACTION_MAX_ITEMS + 9 && n == 3 + 7 * whats.len(), FallRushError::InvalidSlot);
    let (src, auth, escrow) = (&accs[n - 3], &accs[n - 2], &accs[n - 1]);
    keq!(src.key(), crate::ID, FallRushError::Unauthorized);
    keq!(auth.key(), pda(&[ESCROW_AUTH_SEED]).0, FallRushError::Unauthorized);
    let (ek, _) = Pubkey::find_program_address(&[b"balance", auth.key.as_ref(), &[ESCROW_INDEX]], &DELEGATION_PROGRAM);
    keq!(escrow.key(), ek, FallRushError::Unauthorized);
    require!(escrow.is_signer, FallRushError::Unauthorized);
    let mut i = 0;
    while i < whats.len() {
        let part = &accs[7 * i..7 * i + 7];
        if still_owed(part, whats[i]) {
            mint_one(part, whats[i])?;
        }
        i += 1;
    }
    Ok(())
}

#[inline(never)]
fn mint_one<'info>(accs: &'info [AccountInfo<'info>], what: u8) -> Result<()> {
    let mi = acc(accs, 0)?;
    let mut m = load_match(mi)?;
    let mk = mi.key();
    let (asset, owner, treasury, assets_auth, core, system) = (acc(accs, 1)?, acc(accs, 2)?, acc(accs, 3)?, acc(accs, 4)?, acc(accs, 5)?, acc(accs, 6)?);
    keq!(core.key(), MPL_CORE, FallRushError::Unauthorized);
    let (rflags, hmask, minted) = (m.receipt.flags, m.receipt.human_mask, m.receipt.minted_mask);
    require!(rflags & rflag::WRITTEN != 0 && m.status == MatchStatus::Completed, FallRushError::ReceiptNotReady);
    let slot = if what == 0 { m.champion_slot } else { what - 1 };
    require!((slot as usize) < 12 && hmask >> slot & 1 == 1, FallRushError::NothingToMint);
    if what == 0 {
        require!(rflags & (rflag::CROWN | rflag::NO_CROWN) == 0, FallRushError::NothingToMint);
    } else {
        require!(rflags & rflag::SEEDED != 0 && minted >> slot & 1 == 0, FallRushError::NothingToMint);
    }
    keq!(owner.key(), m.receipt.wallets[slot as usize], FallRushError::Unauthorized);
    let sb = [slot];
    let (asset_pda, ab) = if what == 0 { pda(&[CROWN_SEED, mk.as_ref()]) } else { pda(&[CAPSULE_SEED, mk.as_ref(), &sb]) };
    keq!(asset_pda, asset.key(), FallRushError::Unauthorized);
    let (tk, tb) = pda(&[TREASURY_SEED]);
    keq!(tk, treasury.key(), FallRushError::Unauthorized);
    keq!(pda(&[ASSETS_SEED]).0, assets_auth.key(), FallRushError::Unauthorized);
    let (mut b1, mut b2, mut b3, mut b4, mut b5) = ([0u8; 16], [0u8; 44], [0u8; 3], [0u8; 3], [0u8; 3]);
    let id8 = hex_into(&m.match_id[..8], &mut b1);
    let mpda = b58_into(&mk.to_bytes(), &mut b2);
    let players = dec_into(m.player_count, &mut b3);
    let rounds = dec_into(m.current_round, &mut b4);
    let slot_s = dec_into(slot, &mut b5);
    let abs = [ab];
    let tbs = [tb];
    let crown_seeds: [&[u8]; 3] = [CROWN_SEED, mk.as_ref(), &abs];
    let cap_seeds: [&[u8]; 4] = [CAPSULE_SEED, mk.as_ref(), &sb, &abs];
    let tre_seeds: [&[u8]; 2] = [TREASURY_SEED, &tbs];
    if what == 0 {
        let on = if sim::chain_final(&m) { "true" } else { "false" };
        core_create(asset, treasury, owner, assets_auth, system, core, "Fall Rush Crown Capsule", "data:application/json,{\"name\":\"Fall Rush Crown Capsule\"}",
            &[("match", id8), ("matchPda", mpda), ("season", "0"), ("rounds", rounds), ("players", players), ("finalOnChain", on)], &[&crown_seeds, &tre_seeds])?;
        m.receipt.flags |= rflag::CROWN;
        m.receipt.crown = asset_pda;
    } else {
        let ri = (m.receipt.rarity[slot as usize] as usize).min(3);
        let name = ["Rushling Capsule (Common)", "Rushling Capsule (Rare)", "Rushling Capsule (Epic)", "Rushling Capsule (Legendary)"][ri];
        let shell = SHELLS[shell_of(&m.receipt.capsule_seed, slot) as usize];
        core_create(asset, treasury, owner, assets_auth, system, core, name, "data:application/json,{\"name\":\"Rushling Capsule\"}",
            &[("match", id8), ("matchPda", mpda), ("slot", slot_s), ("rarity", rarity::NAMES[ri]), ("shell", shell), ("season", "0")], &[&cap_seeds, &tre_seeds])?;
        m.receipt.minted_mask |= 1 << slot;
    }
    m.exit(&crate::ID)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rarity_distribution_within_1pct_over_10k_seeds() {
        let mut n = [0u32; 4];
        for i in 0..10_000u32 {
            let seed = hashv(&[b"seed", &i.to_le_bytes()]).to_bytes();
            n[rarity_of(&seed, (i % 12) as u8) as usize] += 1;
        }
        let want = [7000, 2200, 700, 100];
        for k in 0..4 {
            assert!((n[k] as i64 - want[k]).abs() <= 100, "rarity {k}: {} vs {}", n[k], want[k]);
        }
    }

    #[test]
    fn b58_matches_pubkey_display() {
        for k in [Pubkey::default(), crate::ID, Pubkey::new_from_array([255; 32]), Pubkey::new_from_array([0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30])] {
            let mut b = [0u8; 44];
            assert_eq!(b58_into(&k.to_bytes(), &mut b), k.to_string());
        }
        let mut d = [0u8; 3];
        assert_eq!(dec_into(0, &mut d), "0");
        assert_eq!(dec_into(12, &mut d), "12");
        assert_eq!(dec_into(255, &mut d), "255");
    }

    #[test]
    fn shared_pool_payouts_match_hand_calc() {
        // property: 500 random markets, payouts = floor(stake · pool / winning stake), Σ ≤ pool, dust < winners
        let mut s = 0x1234_5678_9abc_def0u64;
        let mut rnd = || { s ^= s << 13; s ^= s >> 7; s ^= s << 17; s };
        for _ in 0..500 {
            let mut mk: Market = bytemuck::Zeroable::zeroed();
            mk.status = mstatus::OPEN;
            let n = (rnd() % 64 + 1) as usize;
            for i in 0..n {
                let w = Pubkey::new_from_array([(i % 200) as u8 + 1; 32]);
                let o = (rnd() % 12) as u8;
                let amt = ONE_RUSH + rnd() % (500 * ONE_RUSH);
                let _ = add_bet(&mut mk, &w, o, amt);
            }
            let win = (rnd() % 12) as u16;
            mk.winners = 1 << win;
            mk.winning_stake = mk.stake[win as usize];
            mk.refund = (mk.winning_stake == 0) as u8;
            let mut total = 0u64;
            let mut nw = 0u64;
            for i in 0..mk.n_bets as usize {
                let v = owed(&mk, i);
                let b = mk.bets[i];
                if mk.refund == 1 {
                    assert_eq!(v, b.amount);
                } else if b.outcome as u16 == win {
                    assert_eq!(v as u128, b.amount as u128 * mk.pool as u128 / mk.winning_stake as u128);
                    nw += 1;
                } else {
                    assert_eq!(v, 0);
                }
                total += v;
            }
            assert!(total <= mk.pool);
            if mk.refund == 0 {
                assert!(mk.pool - total < nw.max(1), "dust {} with {} winners", mk.pool - total, nw);
            } else {
                assert_eq!(total, mk.pool);
            }
        }
    }
}
