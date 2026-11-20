/**
 * Deal derivation (doc 06 §4). Bit-identical port of `programs/fall_rush/src/deal.rs`.
 * Cross-checked against `test-vectors/deal_vectors.json` by both the TS and the Rust tests.
 *
 *   rnd(r, label, n):  d = sha256(r ‖ label); read d as eight little-endian u32 words;
 *                      the first word x < zone (zone = floor(2^32 / n) * n) yields x % n;
 *                      if all eight are rejected, d = sha256(d) and repeat.  (rejection sampling)
 *   weighted(r, label, table): k = rnd(r, label, Σweights); pick by cumulative weight.
 *   for round = 1..4 (label suffix is the ASCII digit of the round):
 *     deal_maps[i]      = pool.rounds[i].maps[ rnd(r, "map<round>", len) ]
 *     deal_variants[i]  = rnd(r, "var<round>", variants[map])
 *     deal_modifiers[i] = weighted(r, "mod<round>", modifierWeights[map])
 */
import { createHash } from "node:crypto";
import { loadConfigJson, MAP_POOL_V1_FILE } from "./canonical";

export const ROUNDS = 4;

export interface MapPool {
  version: number;
  modifiers: { id: number; key: string }[];
  maps: { id: number; key: string; mode: string; variants: number; modifierWeights: { modifier: number; weight: number }[] }[];
  rounds: { round: number; maps: number[] }[];
}

export interface Deal {
  maps: number[];
  variants: number[];
  modifiers: number[];
}

const sha256 = (...parts: Uint8Array[]): Buffer => {
  const h = createHash("sha256");
  for (const p of parts) h.update(p);
  return h.digest();
};

export function rndRange(r: Uint8Array, label: Uint8Array, n: number): number {
  if (!Number.isInteger(n) || n < 1 || n > 0xffffffff) throw new Error(`invalid range ${n}`);
  const zone = Math.floor(2 ** 32 / n) * n; // ≤ 2^32, exact in a double
  let d = sha256(r, label);
  for (;;) {
    for (let i = 0; i < 8; i++) {
      const x = d.readUInt32LE(i * 4);
      if (x < zone) return x % n;
    }
    d = sha256(d);
  }
}

export function weighted(r: Uint8Array, label: Uint8Array, table: { modifier: number; weight: number }[]): number {
  const total = table.reduce((s, e) => s + e.weight, 0);
  let k = rndRange(r, label, total);
  for (const e of table) {
    if (k < e.weight) return e.modifier;
    k -= e.weight;
  }
  throw new Error("unreachable");
}

const label = (prefix: string, round: number) => Buffer.from(`${prefix}${round}`, "ascii");

export function deriveDeal(randomness: Uint8Array, pool: MapPool = loadMapPoolV1()): Deal {
  if (randomness.length !== 32) throw new Error("randomness must be 32 bytes");
  const out: Deal = { maps: [], variants: [], modifiers: [] };
  for (let i = 0; i < ROUNDS; i++) {
    const round = i + 1;
    const choices = pool.rounds[i].maps;
    const mapId = choices[rndRange(randomness, label("map", round), choices.length)];
    const map = pool.maps.find((m) => m.id === mapId);
    if (!map) throw new Error(`map ${mapId} missing from pool`);
    out.maps.push(mapId);
    out.variants.push(rndRange(randomness, label("var", round), map.variants));
    out.modifiers.push(weighted(randomness, label("mod", round), map.modifierWeights));
  }
  return out;
}

export function loadMapPoolV1(): MapPool {
  return loadConfigJson(MAP_POOL_V1_FILE) as MapPool;
}
