// Generates test-vectors/deal_vectors.json (100 seeds + rejection-sampling edge vectors).
// Seeds are deterministic: seed_i = sha256("fallrush-vector-v1" ‖ u32le(i)).
import { createHash } from "node:crypto";
import { writeFileSync } from "node:fs";
import { join } from "node:path";
import { deriveDeal, rndRange, mapPoolV1Hash } from "../src";

const seed = (i: number) => {
  const b = Buffer.alloc(4);
  b.writeUInt32LE(i);
  return createHash("sha256").update("fallrush-vector-v1").update(b).digest();
};

const deals = Array.from({ length: 100 }, (_, i) => {
  const r = seed(i);
  return { randomness: r.toString("hex"), ...deriveDeal(r) };
});

// Large ranges make rejection likely (n = 3e9 rejects ~30% of words), so these vectors exercise
// the rejection + rehash path that small game tables almost never hit.
const ranges = [1, 2, 3, 7, 100, 2147483649, 3000000000, 4294967295];
const rnd = Array.from({ length: 40 }, (_, i) => {
  const r = seed(1000 + i);
  const n = ranges[i % ranges.length];
  const label = `edge${i}`;
  return { randomness: r.toString("hex"), label, n, out: rndRange(r, Buffer.from(label, "ascii"), n) };
});

const out = { version: 1, mapPoolHash: mapPoolV1Hash().toString("hex"), deals, rnd };
const path = join(__dirname, "..", "test-vectors", "deal_vectors.json");
writeFileSync(path, JSON.stringify(out, null, 1) + "\n");
console.log(`wrote ${deals.length} deal vectors + ${rnd.length} rnd vectors to ${path}`);
