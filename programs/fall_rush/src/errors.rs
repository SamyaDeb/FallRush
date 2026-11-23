use anchor_lang::prelude::*;

/// doc 06 §5, plus `InvalidRuleset`, `InvalidMapPool`, `Overflow` (added in M0).
#[error_code]
pub enum FallRushError {
    #[msg("Signer is not authorized for this instruction")]
    Unauthorized,
    #[msg("Game config is paused")]
    Paused,
    #[msg("Instruction not allowed in the current match status")]
    InvalidStatus,
    #[msg("Round number does not match the match state")]
    InvalidRound,
    #[msg("seq must equal match.event_seq")]
    InvalidSequence,
    #[msg("Slot index is outside the roster")]
    InvalidSlot,
    #[msg("Slot is not alive")]
    SlotNotAlive,
    #[msg("Qualified count would exceed the round target")]
    TargetExceeded,
    #[msg("Placement must equal qualified_count + 1")]
    InvalidPlacement,
    #[msg("Round cannot be resolved: slots still alive or nobody qualified")]
    RoundNotResolved,
    #[msg("player_count must be within 4..=20")]
    InvalidPlayerCount,
    #[msg("Exactly one remaining slot is required to crown a champion")]
    InvalidChampion,
    #[msg("Pick window is closed")]
    PickWindowClosed,
    #[msg("Duplicate pick")]
    DuplicatePick,
    #[msg("Pick list is full")]
    PicksFull,
    #[msg("VRF callback not expected in the current match status")]
    InvalidVrfCallback,
    #[msg("Match is already final")]
    AlreadyFinal,
    #[msg("Ruleset version/hash does not match the ruleset compiled into this program")]
    InvalidRuleset,
    #[msg("Map pool version/hash does not match the pool compiled into this program")]
    InvalidMapPool,
    #[msg("Arithmetic overflow")]
    Overflow,
    // ---- P2 on-chain simulation
    #[msg("Input tick outside the accepted window")]
    InputOutOfWindow,
    #[msg("Map blob invalid, unsealed or hash mismatch")]
    BadMap,
    #[msg("Timed task instruction could not be built")]
    BadTask,
    #[msg("Slot already taken")]
    SlotTaken,
    #[msg("Signer already holds a slot in this arena")]
    AlreadyJoined,
    #[msg("Arena is not in the required state")]
    BadArenaState,
    #[msg("Round result not ready or already committed")]
    NothingToCommit,
    #[msg("Session token missing, revoked, expired or not for this signer / wallet / program")]
    SessionInvalid,
    // ---- P4 economy
    #[msg("Faucet already claimed in the last 24 h")]
    FaucetCooldown,
    #[msg("Not enough RUSH chips")]
    InsufficientChips,
    #[msg("Market is closed for bets")]
    MarketClosed,
    #[msg("Market has 64 bet records")]
    MarketFull,
    #[msg("Bet below the minimum (1 RUSH)")]
    BetTooSmall,
    #[msg("Market still holds the previous match (settle and pay it first)")]
    MarketBusy,
    #[msg("Unknown Chaos Drop kind")]
    InvalidDrop,
    #[msg("Chaos Drops are sold during the on-chain Final's RUSH phase only")]
    DropClosed,
    #[msg("Chaos Drop cap reached for this round")]
    DropCap,
    #[msg("Chaos Drop cooldown (5 s)")]
    DropCooldown,
    #[msg("Receipt not written / not ready")]
    ReceiptNotReady,
    #[msg("Reward already minted or not owed")]
    NothingToMint,
}
