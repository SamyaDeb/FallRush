//! On-chain deal derivation (doc 06 §4). Bit-identical to `packages/shared/src/deal.ts`;
//! both are checked against `packages/shared/test-vectors/deal_vectors.json`.
//!
//! rnd(r, label, n): d = sha256(r ‖ label); read d as eight little-endian u32 words; the first
//! word x < zone (zone = floor(2^32 / n) * n) yields x % n; if all eight are rejected,
//! d = sha256(d) and repeat (unbiased rejection sampling).
//! Labels are `map<round>`, `var<round>`, `mod<round>` with the ASCII digit of the round (1..=4).

use solana_sha256_hasher::hashv;

use crate::state::ROUNDS;

/// Map pool v1 (must match `packages/shared/config/map_pool_v1.json`; asserted in tests).
pub struct MapDef {
    pub id: u8,
    pub variants: u32,
    /// (modifier id, weight)
    pub modifier_weights: &'static [(u8, u32)],
}

pub const MAP_BLOCK_PARTY_DASH: u8 = 0;
pub const MAP_GOO_TOWER: u8 = 1;
pub const MAP_STAR_SCRAMBLE: u8 = 2;
pub const MAP_CRUMBLE_SHELF: u8 = 3;

pub const MAPS: [MapDef; 4] = [
    MapDef {
        id: MAP_BLOCK_PARTY_DASH,
        variants: 2,
        modifier_weights: &[(0, 40), (1, 15), (2, 15), (3, 15), (5, 15)],
    },
    MapDef {
        id: MAP_GOO_TOWER,
        variants: 2,
        modifier_weights: &[(0, 40)],
    },
    MapDef {
        id: MAP_STAR_SCRAMBLE,
        variants: 2,
        modifier_weights: &[(0, 40), (1, 15), (3, 15), (4, 15)],
    },
    MapDef {
        id: MAP_CRUMBLE_SHELF,
        variants: 2,
        modifier_weights: &[(0, 40), (1, 15), (2, 15)],
    },
];

/// Eligible maps per round (MVP: R1 = [BlockPartyDash]; Bonk Factory joins in a later pool version).
pub const ROUND_MAPS: [&[u8]; ROUNDS] = [
    &[MAP_BLOCK_PARTY_DASH],
    &[MAP_GOO_TOWER],
    &[MAP_STAR_SCRAMBLE],
    &[MAP_CRUMBLE_SHELF],
];

pub fn rnd_range(r: &[u8; 32], label: &[u8], n: u32) -> u32 {
    assert!(n >= 1, "range must be >= 1");
    let zone: u64 = ((1u64 << 32) / n as u64) * n as u64;
    let mut d = hashv(&[r, label]).to_bytes();
    loop {
        for i in 0..8 {
            let x = u32::from_le_bytes([d[4 * i], d[4 * i + 1], d[4 * i + 2], d[4 * i + 3]]);
            if (x as u64) < zone {
                return x % n;
            }
        }
        d = hashv(&[&d]).to_bytes();
    }
}

pub fn weighted(r: &[u8; 32], label: &[u8], table: &[(u8, u32)]) -> u8 {
    let total: u32 = table.iter().map(|(_, w)| *w).sum();
    let mut k = rnd_range(r, label, total);
    for (id, w) in table {
        if k < *w {
            return *id;
        }
        k -= *w;
    }
    unreachable!()
}

fn map_def(id: u8) -> &'static MapDef {
    MAPS.iter().find(|m| m.id == id).expect("map in pool")
}

pub struct Deal {
    pub maps: [u8; ROUNDS],
    pub variants: [u8; ROUNDS],
    pub modifiers: [u8; ROUNDS],
}

pub fn derive_deal(r: &[u8; 32]) -> Deal {
    let mut deal = Deal {
        maps: [0; ROUNDS],
        variants: [0; ROUNDS],
        modifiers: [0; ROUNDS],
    };
    for i in 0..ROUNDS {
        let digit = b'1' + i as u8;
        let choices = ROUND_MAPS[i];
        let map_id = choices[rnd_range(r, &[b'm', b'a', b'p', digit], choices.len() as u32) as usize];
        let def = map_def(map_id);
        deal.maps[i] = map_id;
        deal.variants[i] = rnd_range(r, &[b'v', b'a', b'r', digit], def.variants) as u8;
        deal.modifiers[i] = weighted(r, &[b'm', b'o', b'd', digit], def.modifier_weights);
    }
    deal
}
