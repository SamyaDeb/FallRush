//! The rolling match transcript (port of `Shared/Transcript.cs` = `packages/shared/src/transcript.ts` = the
//! program's `Match::append`) and the roster hash (`RosterHash`).
//!
//! ```text
//! init = sha256("fallrush-transcript-v1" ‖ match_id[16] ‖ roster_hash[32])
//! next = sha256(prev ‖ tag u8 ‖ seq u32-LE ‖ round u8 ‖ slot u8 ‖ arg bytes)
//! roster_hash = sha256("fallrush-roster-v1" ‖ u8 P ‖ per slot: u8 slot ‖ u8 kind ‖ sha256(utf8 user_id) ‖ u8 hasWallet ‖ [wallet])
//! match_id = sha256("fallrush-match-id" ‖ seed[32])[..16]
//! ```

use crate::world::Hasher;

pub const DEAL: u8 = 1;
pub const START_ROUND: u8 = 2;
pub const QUALIFIED: u8 = 3;
pub const ELIMINATED: u8 = 4;
pub const COMPLETE_ROUND: u8 = 5;
pub const COMPLETE_MATCH: u8 = 6;
pub const ABORT: u8 = 7;
/// Reserved for P4 (`DROP_APPLIED`, `MARKET_CLOSED`, `MARKET_SETTLED`).
pub const DROP_APPLIED: u8 = 8;
pub const MARKET_CLOSED: u8 = 9;
pub const MARKET_SETTLED: u8 = 10;
pub const NO_SLOT: u8 = 255;

pub fn init<H: Hasher>(h: &mut H, match_id: &[u8; 16], roster_hash: &[u8; 32]) -> [u8; 32] {
    h.sha256(&[b"fallrush-transcript-v1", match_id, roster_hash])
}

pub fn next<H: Hasher>(h: &mut H, prev: &[u8; 32], tag: u8, seq: u32, round: u8, slot: u8, arg: &[u8]) -> [u8; 32] {
    let s = seq.to_le_bytes();
    let mid = [tag, s[0], s[1], s[2], s[3], round, slot];
    h.sha256(&[prev, &mid, arg])
}

/// `match_id = sha256("fallrush-match-id" ‖ seed)[..16]`.
pub fn match_id<H: Hasher>(h: &mut H, seed: &[u8; 32]) -> [u8; 16] {
    let d = h.sha256(&[b"fallrush-match-id", seed]);
    let mut m = [0u8; 16];
    m.copy_from_slice(&d[..16]);
    m
}

/// One roster entry for [`roster_hash`].
#[derive(Clone, Copy, Debug)]
pub struct RosterEntry<'a> {
    pub bot: bool,
    pub user_id: &'a [u8],
    pub wallet: Option<&'a [u8; 32]>,
}

/// `RosterHash.Compute` (slots 0…P−1 in order).
pub fn roster_hash<H: Hasher>(h: &mut H, roster: &[RosterEntry]) -> [u8; 32] {
    // Built incrementally: sha256 is not streaming through the trait, so the preimage is assembled in a bounded buffer
    // (≤ 20 entries × 67 B + 19 B).
    let mut buf = [0u8; 19 + 20 * 67];
    let mut n = 0;
    let tag = b"fallrush-roster-v1";
    buf[..tag.len()].copy_from_slice(tag);
    n += tag.len();
    buf[n] = roster.len().min(20) as u8;
    n += 1;
    for (i, e) in roster.iter().take(20).enumerate() {
        buf[n] = i as u8;
        buf[n + 1] = e.bot as u8;
        n += 2;
        let d = h.sha256(&[e.user_id]);
        buf[n..n + 32].copy_from_slice(&d);
        n += 32;
        match e.wallet {
            Some(w) => {
                buf[n] = 1;
                buf[n + 1..n + 33].copy_from_slice(w);
                n += 33;
            }
            None => {
                buf[n] = 0;
                n += 1;
            }
        }
    }
    h.sha256(&[&buf[..n]])
}
