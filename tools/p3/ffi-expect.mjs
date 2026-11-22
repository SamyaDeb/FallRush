// tools/p3/ffi-expect.mjs — the P1 FFI check's expected hashes for the core in use (P3 builds it from P2's branch):
// every map blob × seeds 1, 2, 3; rate 20, 12 players (odd slots bots), 200 steps with missing inputs; World hash.
// Same recipe as `rush-sim ffi-expect` (docs/proof/p1/ffi_expected.txt), computed with rush_core.wasm.
//   node tools/p3/ffi-expect.mjs <rush_core.wasm> <blob dir> <out.txt>
import { readFileSync, readdirSync, writeFileSync } from 'node:fs';
const [wasm, dir, out] = process.argv.slice(2);
const { instance } = await WebAssembly.instantiate(readFileSync(wasm), {});
const x = instance.exports;
const put = (b) => { const p = x.rc_alloc(b.length); new Uint8Array(x.memory.buffer).set(b, p); return p; };
const lines = [];
for (const f of readdirSync(dir).filter((n) => n.endsWith('.bytes')).sort()) {
  const key = f.replace(/\.bytes$/, '');
  const blob = readFileSync(`${dir}/${f}`);
  for (const seed of [1n, 2n, 3n]) {
    const cfg = new Uint8Array(24); const v = new DataView(cfg.buffer);
    v.setUint32(0, 20, true); v.setUint32(4, 0xfff, true); v.setUint32(8, 0xaaa, true); v.setBigUint64(16, seed, true);
    const h = x.rc_init(put(blob), blob.length, put(cfg));
    for (let i = 0; i < 200; i++) x.rc_step(h, 0);
    lines.push(`${key} ${seed} ${BigInt.asUintN(64, x.rc_hash(h)).toString(16).padStart(16, '0')}`);
    x.rc_free(h);
  }
}
writeFileSync(out, lines.join('\n') + '\n');
console.log(`${lines.length} hashes -> ${out}`);
