import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { deriveDeal, rndRange, mapPoolV1Hash, roundTargets, canonicalize } from "../src";

const vectors = JSON.parse(readFileSync(join(__dirname, "..", "test-vectors", "deal_vectors.json"), "utf8"));

test("vectors were generated from the current map pool", () => {
  assert.equal(vectors.mapPoolHash, mapPoolV1Hash().toString("hex"));
});

test("deal parity: 100 seeds", () => {
  assert.equal(vectors.deals.length, 100);
  for (const v of vectors.deals) {
    const d = deriveDeal(Buffer.from(v.randomness, "hex"));
    assert.deepEqual(d, { maps: v.maps, variants: v.variants, modifiers: v.modifiers }, v.randomness);
  }
});

test("rnd parity: rejection-sampling edge vectors", () => {
  for (const v of vectors.rnd) {
    assert.equal(rndRange(Buffer.from(v.randomness, "hex"), Buffer.from(v.label, "ascii"), v.n), v.out);
  }
});

test("deal outputs stay in range and use every R1 variant", () => {
  const seen = new Set<number>();
  for (const v of vectors.deals) {
    assert.deepEqual(v.maps, [0, 1, 2, 3]);
    v.variants.forEach((x: number) => assert.ok(x === 0 || x === 1));
    v.modifiers.forEach((x: number) => assert.ok(x >= 0 && x <= 5));
    assert.equal(v.modifiers[1], 0, "Goo Tower allows only None");
    seen.add(v.variants[0]);
  }
  assert.equal(seen.size, 2);
});

test("round targets (GDD §4.1)", () => {
  assert.deepEqual(roundTargets(12), [8, 5, 3, 1]);
  assert.deepEqual(roundTargets(4), [3, 2, 1, 1]);
  assert.deepEqual(roundTargets(20), [14, 9, 3, 1]);
  assert.throws(() => roundTargets(3));
  assert.throws(() => roundTargets(21));
});

test("canonical JSON sorts keys and strips whitespace", () => {
  assert.equal(canonicalize({ b: [2, { d: 1, c: "x" }], a: true }), '{"a":true,"b":[2,{"c":"x","d":1}]}');
  assert.throws(() => canonicalize({ x: 0.5 }));
});
