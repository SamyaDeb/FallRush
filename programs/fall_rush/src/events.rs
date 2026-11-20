use anchor_lang::prelude::*;

/// doc 06 §6. Indexing aid only; account state stays authoritative.
#[event]
pub struct MatchCreated {
    pub match_id: [u8; 16],
    pub player_count: u8,
    pub round_targets: [u8; 4],
}

#[event]
pub struct MatchDelegated {
    pub match_id: [u8; 16],
    pub validator: Option<Pubkey>,
}

#[event]
pub struct DealRequested {
    pub match_id: [u8; 16],
    pub caller_seed: [u8; 32],
}

#[event]
pub struct DealReady {
    pub match_id: [u8; 16],
    pub vrf_seed: [u8; 32],
    pub maps: [u8; 4],
    pub variants: [u8; 4],
    pub modifiers: [u8; 4],
}

#[event]
pub struct RoundStarted {
    pub match_id: [u8; 16],
    pub round: u8,
    pub seq: u32,
}

#[event]
pub struct PlayerQualified {
    pub match_id: [u8; 16],
    pub round: u8,
    pub slot: u8,
    pub placement: u8,
    pub seq: u32,
}

#[event]
pub struct PlayerEliminated {
    pub match_id: [u8; 16],
    pub round: u8,
    pub slot: u8,
    pub reason: u8,
    pub seq: u32,
}

#[event]
pub struct RoundCompleted {
    pub match_id: [u8; 16],
    pub round: u8,
    pub qualified: u8,
    pub seq: u32,
}

#[event]
pub struct PickSubmitted {
    pub match_id: [u8; 16],
    pub picker: Pubkey,
    pub round: u8,
    pub slot: u8,
}

#[event]
pub struct MatchCompleted {
    pub match_id: [u8; 16],
    pub champion_slot: u8,
    pub transcript_hash: [u8; 32],
}

#[event]
pub struct MatchAborted {
    pub match_id: [u8; 16],
    pub reason: u8,
    pub seq: u32,
}
