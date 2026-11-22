//! The VRF deal (port of `Shared/Deal.cs` = `packages/shared/src/deal.ts` = the program's deal): maps, variants and
//! modifiers per round from 32 bytes of randomness, and the per-round hazard seed. sha256 comes through
//! [`crate::world::Hasher`]. `map_pool_v1.json` tables are mirrored here (as `MapPoolV1` mirrors them in C#).

// Fixed slot loops index several parallel arrays (fixed iteration order, P1 §1.2).
#![allow(clippy::needless_range_loop, clippy::manual_range_contains)]

use crate::world::Hasher;

pub const ROUNDS: usize = 4;

/// `MapPoolV1.RoundMaps`: eligible map ids per round.
pub const ROUND_MAPS: [&[u8]; ROUNDS] = [&[0], &[1], &[2], &[3]];
/// `MapPoolV1.Variants`.
pub const VARIANTS: [u8; 4] = [2, 2, 2, 2];
/// `MapPoolV1.Weights`: (modifier, weight) per map.
pub const WEIGHTS: [&[(u8, u32)]; 4] = [
    &[(0, 40), (1, 15), (2, 15), (3, 15), (5, 15)],
    &[(0, 40)],
    &[(0, 40), (1, 15), (3, 15), (4, 15)],
    &[(0, 40), (1, 15), (2, 15)],
];
pub const LOBBY: u8 = 254;
pub const SANDBOX: u8 = 255;

pub fn variant_count(map: u8) -> u32 {
    if (map as usize) < VARIANTS.len() {
        VARIANTS[map as usize] as u32
    } else {
        2
    }
}

pub fn weights(map: u8) -> &'static [(u8, u32)] {
    if (map as usize) < WEIGHTS.len() {
        WEIGHTS[map as usize]
    } else {
        WEIGHTS[0]
    }
}

/// `MapPoolV1.ModifierAllowed`.
pub fn modifier_allowed(map: u8, m: u8) -> bool {
    weights(map).iter().any(|w| w.0 == m)
}

/// `Deal.RndRange`: unbiased integer in [0, n) from sha256(r ‖ label), rejection-sampled over u32 words, rehashing.
/// `n` in 1…2^32 − 1 (0 returns 0).
pub fn rnd_range<H: Hasher>(h: &mut H, r: &[u8], label: &[u8], n: u64) -> u32 {
    if n < 1 || n > 0xFFFF_FFFF {
        return 0;
    }
    let zone = (0x1_0000_0000u64 / n) * n;
    let mut d = h.sha256(&[r, label]);
    let mut guard = 0;
    while guard < 64 {
        let mut i = 0;
        while i < 8 {
            let x = u32::from_le_bytes([d[i * 4], d[i * 4 + 1], d[i * 4 + 2], d[i * 4 + 3]]) as u64;
            if x < zone {
                return (x % n) as u32;
            }
            i += 1;
        }
        d = h.sha256(&[&d]);
        guard += 1;
    }
    0 // unreachable in practice (probability < 2^-2048)
}

/// `Deal.Weighted`.
pub fn weighted<H: Hasher>(h: &mut H, r: &[u8], label: &[u8], table: &[(u8, u32)]) -> u8 {
    let total: u64 = table.iter().map(|e| e.1 as u64).sum();
    let mut k = rnd_range(h, r, label, total) as i64;
    for e in table {
        if k < e.1 as i64 {
            return e.0;
        }
        k -= e.1 as i64;
    }
    0
}

/// "prefix" + decimal round (1…4) as ASCII.
fn label(prefix: &[u8], round: usize, buf: &mut [u8; 8]) -> usize {
    buf[..prefix.len()].copy_from_slice(prefix);
    buf[prefix.len()] = b'0' + round as u8;
    prefix.len() + 1
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct DealResult {
    pub maps: [u8; ROUNDS],
    pub variants: [u8; ROUNDS],
    pub modifiers: [u8; ROUNDS],
}

/// `Deal.Derive`.
pub fn derive<H: Hasher>(h: &mut H, randomness: &[u8; 32]) -> DealResult {
    let mut res = DealResult::default();
    let mut lb = [0u8; 8];
    for i in 0..ROUNDS {
        let round = i + 1;
        let choices = ROUND_MAPS[i];
        let n = label(b"map", round, &mut lb);
        let map = choices[rnd_range(h, randomness, &lb[..n], choices.len() as u64) as usize];
        res.maps[i] = map;
        let n = label(b"var", round, &mut lb);
        res.variants[i] = rnd_range(h, randomness, &lb[..n], variant_count(map) as u64) as u8;
        let n = label(b"mod", round, &mut lb);
        res.modifiers[i] = weighted(h, randomness, &lb[..n], weights(map));
    }
    res
}

/// `Deal.HazardSeed`: the first 8 bytes (LE) of sha256("fallrush-hazard-v1" ‖ seed ‖ round).
pub fn hazard_seed<H: Hasher>(h: &mut H, seed: &[u8; 32], round: u8) -> u64 {
    let d = h.sha256(&[b"fallrush-hazard-v1", seed, &[round]]);
    u64::from_le_bytes([d[0], d[1], d[2], d[3], d[4], d[5], d[6], d[7]])
}

/// `MatchDirector.TierFor`: bot tier from sha256("fallrush-bot-tier" ‖ seed ‖ slot): 1 Easy (30 %), 2 Normal (50 %),
/// 3 Good (20 %).
pub fn bot_tier<H: Hasher>(h: &mut H, seed: &[u8; 32], slot: u8) -> u8 {
    let d = h.sha256(&[b"fallrush-bot-tier", seed, &[slot]]);
    let x = d[0] % 10;
    if x < 3 {
        1
    } else if x < 8 {
        2
    } else {
        3
    }
}
