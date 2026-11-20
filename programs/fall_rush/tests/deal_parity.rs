//! Deal parity (doc 12 §1): the on-chain derivation must equal the TS port on the shared vectors,
//! and the compiled pool tables / config hashes must equal the canonical JSON files.

use solana_sha256_hasher::hashv;
use fall_rush::deal::{derive_deal, rnd_range, MAPS, ROUND_MAPS};
use fall_rush::state::{round_targets, MAP_POOL_V1_HASH, RULESET_V1_HASH};
use serde_json::Value;
use std::path::PathBuf;

fn shared(path: &str) -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../packages/shared")
        .join(path)
}

fn load(path: &str) -> Value {
    serde_json::from_str(&std::fs::read_to_string(shared(path)).expect("read file")).expect("json")
}

fn hex32(s: &str) -> [u8; 32] {
    let mut out = [0u8; 32];
    for i in 0..32 {
        out[i] = u8::from_str_radix(&s[2 * i..2 * i + 2], 16).expect("hex");
    }
    out
}

fn to_hex(b: &[u8]) -> String {
    b.iter().map(|x| format!("{x:02x}")).collect()
}

/// serde_json (without `preserve_order`) stores objects in a BTreeMap, so `to_string` yields
/// sorted keys with no whitespace: the canonical form from doc 03 §5 (integers/ASCII only).
fn canonical_hash(path: &str) -> [u8; 32] {
    let canon = serde_json::to_string(&load(path)).unwrap();
    hashv(&[canon.as_bytes()]).to_bytes()
}

fn bytes(v: &Value) -> Vec<u8> {
    v.as_array().unwrap().iter().map(|x| x.as_u64().unwrap() as u8).collect()
}

#[test]
fn config_hashes_match_canonical_json() {
    assert_eq!(to_hex(&canonical_hash("config/ruleset_v1.json")), to_hex(&RULESET_V1_HASH));
    assert_eq!(to_hex(&canonical_hash("config/map_pool_v1.json")), to_hex(&MAP_POOL_V1_HASH));
}

#[test]
fn compiled_pool_matches_map_pool_json() {
    let pool = load("config/map_pool_v1.json");
    let maps = pool["maps"].as_array().unwrap();
    assert_eq!(maps.len(), MAPS.len());
    for (json, def) in maps.iter().zip(MAPS.iter()) {
        assert_eq!(json["id"].as_u64().unwrap() as u8, def.id);
        assert_eq!(json["variants"].as_u64().unwrap() as u32, def.variants);
        let w: Vec<(u8, u32)> = json["modifierWeights"]
            .as_array()
            .unwrap()
            .iter()
            .map(|e| (e["modifier"].as_u64().unwrap() as u8, e["weight"].as_u64().unwrap() as u32))
            .collect();
        assert_eq!(w.as_slice(), def.modifier_weights);
    }
    let rounds = pool["rounds"].as_array().unwrap();
    assert_eq!(rounds.len(), ROUND_MAPS.len());
    for (json, compiled) in rounds.iter().zip(ROUND_MAPS.iter()) {
        assert_eq!(bytes(&json["maps"]).as_slice(), *compiled);
    }
}

#[test]
fn deal_vectors_match() {
    let v = load("test-vectors/deal_vectors.json");
    assert_eq!(v["mapPoolHash"].as_str().unwrap(), to_hex(&MAP_POOL_V1_HASH));
    let deals = v["deals"].as_array().unwrap();
    assert_eq!(deals.len(), 100);
    for d in deals {
        let r = hex32(d["randomness"].as_str().unwrap());
        let got = derive_deal(&r);
        assert_eq!(got.maps.to_vec(), bytes(&d["maps"]), "maps {}", d["randomness"]);
        assert_eq!(got.variants.to_vec(), bytes(&d["variants"]), "variants {}", d["randomness"]);
        assert_eq!(got.modifiers.to_vec(), bytes(&d["modifiers"]), "modifiers {}", d["randomness"]);
    }
}

#[test]
fn rnd_edge_vectors_match() {
    let v = load("test-vectors/deal_vectors.json");
    let edges = v["rnd"].as_array().unwrap();
    assert!(!edges.is_empty());
    for e in edges {
        let r = hex32(e["randomness"].as_str().unwrap());
        let n = e["n"].as_u64().unwrap() as u32;
        let label = e["label"].as_str().unwrap().as_bytes();
        assert_eq!(rnd_range(&r, label, n) as u64, e["out"].as_u64().unwrap(), "{e}");
    }
}

#[test]
fn round_targets_match_gdd() {
    assert_eq!(round_targets(12).unwrap(), [8, 5, 3, 1]);
    assert_eq!(round_targets(4).unwrap(), [3, 2, 1, 1]);
    assert_eq!(round_targets(20).unwrap(), [14, 9, 3, 1]);
    assert!(round_targets(3).is_err());
    assert!(round_targets(21).is_err());
}
