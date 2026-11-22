// P0.6 (WASM in Node): world hashes for seeds 1..S x 200 steps on both maps.
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { LOGS, ROOT } from "./lib.ts";
const S = Number(process.argv[2] ?? 100), STEPS = Number(process.argv[3] ?? 200);
const wasm = readFileSync(join(ROOT, "target/wasm32-unknown-unknown/release/p0_sim_wasm.wasm"));
const { instance } = await WebAssembly.instantiate(wasm, {});
const ex = instance.exports as any;
const out: Record<string, string[]> = {};
for (const name of ["lid", "run"]) {
  const blob = readFileSync(join(LOGS, `map_${name}.bin`));
  const ptr = ex.p0_alloc(Math.ceil(blob.length / 4));
  new Uint8Array(ex.memory.buffer, ptr, blob.length).set(blob);
  out[name] = [];
  for (let seed = 1; seed <= S; seed++) out[name].push(`${seed} ${BigInt.asUintN(64, ex.p0_hash(ptr, blob.length, BigInt(seed), STEPS)).toString(16).padStart(16, "0")}`);
}
writeFileSync(join(LOGS, "wasm_node.json"), JSON.stringify(out));
for (const name of ["lid", "run"]) {
  const nat = readFileSync(join(LOGS, `native_${name}.txt`), "utf8").split("\n").filter((l) => l && !l.startsWith("#"));
  const same = out[name].filter((l, i) => l === nat[i]).length;
  console.log(`wasm-node ${name}: ${same}/${out[name].length} identical to native`);
}
