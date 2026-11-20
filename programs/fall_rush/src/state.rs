use anchor_lang::prelude::*;
use solana_sha256_hasher::hashv;

use crate::errors::FallRushError;

pub const CONFIG_SEED: &[u8] = b"config";
pub const MATCH_SEED: &[u8] = b"match";

pub const MAX_SLOTS: usize = 20;
pub const MAX_PICKS: usize = 32;
pub const ROUNDS: usize = 4;
pub const MIN_PLAYERS: u8 = 4;
pub const MAX_PLAYERS: u8 = 20;
pub const NO_SLOT: u8 = 255;

/// sha256 of canonical `packages/shared/config/ruleset_v1.json` (asserted by `tests/deal_parity.rs`).
pub const RULESET_V1_VERSION: u16 = 1;
pub const RULESET_V1_HASH: [u8; 32] = [
    0x4b, 0xdf, 0xb4, 0xef, 0x6a, 0x73, 0x93, 0x22, 0x1a, 0x83, 0x04, 0x6b, 0x48, 0xca, 0xdb, 0xb7,
    0xd2, 0x4d, 0x44, 0x54, 0xe8, 0x39, 0xd4, 0xfd, 0x90, 0xad, 0x4c, 0x4b, 0xa5, 0x26, 0xbe, 0x32,
];
/// sha256 of canonical `packages/shared/config/map_pool_v1.json` (asserted by `tests/deal_parity.rs`).
pub const MAP_POOL_V1_VERSION: u16 = 1;
pub const MAP_POOL_V1_HASH: [u8; 32] = [
    0x67, 0xd6, 0x53, 0x72, 0x24, 0x94, 0x26, 0x90, 0x97, 0x1d, 0x2f, 0x6b, 0x25, 0xfa, 0x6f, 0x6f,
    0x49, 0x49, 0x47, 0x93, 0x52, 0x0f, 0x64, 0xe8, 0xe7, 0x8c, 0x47, 0x43, 0x07, 0xc2, 0xc8, 0x96,
];

/// Slot states (doc 06 §2.2).
pub const SLOT_EMPTY: u8 = 0;
pub const SLOT_ALIVE: u8 = 1;
pub const SLOT_QUALIFIED: u8 = 2;
pub const SLOT_ELIMINATED: u8 = 3;

/// Transcript tags (mirrored in `packages/shared/src/transcript.ts`).
pub const TAG_DEAL: u8 = 1;
pub const TAG_START_ROUND: u8 = 2;
pub const TAG_QUALIFIED: u8 = 3;
pub const TAG_ELIMINATED: u8 = 4;
pub const TAG_COMPLETE_ROUND: u8 = 5;
pub const TAG_COMPLETE_MATCH: u8 = 6;
pub const TAG_ABORT: u8 = 7;

#[account]
#[derive(InitSpace)]
pub struct GameConfig {
    pub version: u8,
    pub bump: u8,
    pub admin: Pubkey,
    pub game_authority: Pubkey,
    pub ruleset_version: u16,
    pub ruleset_hash: [u8; 32],
    pub map_pool_version: u16,
    pub map_pool_hash: [u8; 32],
    pub paused: bool,
    pub match_count: u64,
}

/// Canonical match status (doc 05 §4). `RoundActive(r)` / `RoundResolved(r)` are represented as
/// the status plus `Match::current_round == r`, which keeps the enum fieldless.
#[derive(AnchorSerialize, AnchorDeserialize, Clone, Copy, PartialEq, Eq, Debug, InitSpace)]
pub enum MatchStatus {
    Created,
    Dealing,
    Dealt,
    RoundActive,
    RoundResolved,
    Completed,
    Aborted,
}

#[derive(AnchorSerialize, AnchorDeserialize, Clone, Copy, Default, PartialEq, Eq, Debug, InitSpace)]
pub struct SlotResult {
    /// 0 empty, 1 alive, 2 qualified (this round), 3 eliminated
    pub state: u8,
    /// last round this slot qualified in, or the round it was eliminated in
    pub round: u8,
    pub placement: u8,
    pub reason: u8,
}

#[derive(AnchorSerialize, AnchorDeserialize, Clone, Copy, Default, PartialEq, Eq, Debug, InitSpace)]
pub struct Pick {
    pub picker: Pubkey,
    pub round: u8,
    pub slot: u8,
}

/// Single match PDA, layout per doc 06 §2.2 (fixed arrays, no Vec) so later milestones need no
/// migration. M0 does not touch `picks` / `pick_count`.
#[account]
#[derive(InitSpace)]
pub struct Match {
    pub version: u8,
    pub bump: u8,
    pub match_id: [u8; 16],
    pub status: MatchStatus,
    /// bit0 = unranked
    pub flags: u8,
    pub ruleset_version: u16,
    pub ruleset_hash: [u8; 32],
    pub map_pool_version: u16,
    pub map_pool_hash: [u8; 32],
    pub roster_hash: [u8; 32],
    pub player_count: u8,
    /// 0 = none, 1..=4
    pub current_round: u8,
    pub round_targets: [u8; ROUNDS],
    /// next expected sequence number
    pub event_seq: u32,
    pub transcript_hash: [u8; 32],
    pub vrf_seed: [u8; 32],
    pub deal_maps: [u8; ROUNDS],
    pub deal_variants: [u8; ROUNDS],
    pub deal_modifiers: [u8; ROUNDS],
    pub slots: [SlotResult; MAX_SLOTS],
    /// 255 = none
    pub champion_slot: u8,
    pub picks: [Pick; MAX_PICKS],
    pub pick_count: u8,
    pub created_at: i64,
    pub dealt_at: i64,
    pub completed_at: i64,
}

/// Round targets from player count (GDD §4.1, ruleset_v1): integer-only ceil so the TS port
/// (`packages/shared/src/targets.ts`) is identical.
pub fn round_targets(player_count: u8) -> Result<[u8; ROUNDS]> {
    require!(
        (MIN_PLAYERS..=MAX_PLAYERS).contains(&player_count),
        FallRushError::InvalidPlayerCount
    );
    let p = player_count as u32;
    let r1 = (p * 66 + 99) / 100;
    let r2 = (r1 * 62 + 99) / 100;
    let r3 = core::cmp::min(3, r2 - 1);
    Ok([r1 as u8, r2 as u8, r3 as u8, 1])
}

pub fn transcript_init(match_id: &[u8; 16], roster_hash: &[u8; 32]) -> [u8; 32] {
    hashv(&[b"fallrush-transcript-v1", match_id, roster_hash]).to_bytes()
}

pub fn deal_caller_seed(m: &Match) -> [u8; 32] {
    hashv(&[b"fallrush-deal", &m.match_id, &m.ruleset_hash, &m.map_pool_hash]).to_bytes()
}

impl Match {
    /// Sequence rule (doc 06 §3): checked first in every event handler so a retried event
    /// fails with `InvalidSequence` before any other precondition.
    pub fn check_seq(&self, seq: u32) -> Result<()> {
        require!(seq == self.event_seq, FallRushError::InvalidSequence);
        Ok(())
    }

    /// transcript = sha256(prev ‖ tag ‖ seq_le ‖ round ‖ slot ‖ arg); then event_seq += 1.
    pub fn append(&mut self, tag: u8, round: u8, slot: u8, arg: &[u8]) -> Result<()> {
        let seq_le = self.event_seq.to_le_bytes();
        self.transcript_hash =
            hashv(&[&self.transcript_hash, &[tag], &seq_le, &[round, slot], arg]).to_bytes();
        self.event_seq = self
            .event_seq
            .checked_add(1)
            .ok_or(FallRushError::Overflow)?;
        Ok(())
    }

    pub fn count_slots(&self, state: u8) -> u8 {
        self.slots[..self.player_count as usize]
            .iter()
            .filter(|s| s.state == state)
            .count() as u8
    }

    pub fn target(&self, round: u8) -> Result<u8> {
        require!(
            round >= 1 && (round as usize) <= ROUNDS,
            FallRushError::InvalidRound
        );
        Ok(self.round_targets[(round - 1) as usize])
    }

    pub fn require_active_round(&self, round: u8) -> Result<()> {
        require!(
            self.status == MatchStatus::RoundActive,
            FallRushError::InvalidStatus
        );
        require!(round == self.current_round, FallRushError::InvalidRound);
        Ok(())
    }

    pub fn alive_slot(&self, slot: u8) -> Result<usize> {
        require!(slot < self.player_count, FallRushError::InvalidSlot);
        let i = slot as usize;
        require!(
            self.slots[i].state == SLOT_ALIVE,
            FallRushError::SlotNotAlive
        );
        Ok(i)
    }
}
