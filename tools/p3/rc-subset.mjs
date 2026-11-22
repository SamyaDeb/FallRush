// tools/p3/rc-subset.mjs — P3-A parity: checks every golden vector file in full against rush_core.wasm (as
// tools/wasm-run.mjs), then writes the 200-case subset (40 per file) the Unity EditMode test (native bundle) and the
// WebGL ?rcselftest=1 page (browser WASM) must reproduce:   file kind seed n digest
//   node tools/p3/rc-subset.mjs <rush_core.wasm> <vectors dir> <out.txt>
import { readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';
import { join } from 'node:path';

const [wasmPath, dir, outPath] = process.argv.slice(2);
const { instance } = await WebAssembly.instantiate(readFileSync(wasmPath), {});
const x = instance.exports;
const put = (bytes) => { const p = x.rc_alloc(bytes.length); new Uint8Array(x.memory.buffer).set(bytes, p); return p; };
const hex = (v) => BigInt.asUintN(64, v).toString(16).padStart(16, '0');
const PER_FILE = 40;
let bad = 0;
const lines = [`# rc_version 0x${x.rc_version().toString(16)}  (P3-A subset: ${PER_FILE} cases per file)`];
for (const f of readdirSync(dir).filter((n) => /_v\d+\.bin$/.test(n)).sort()) {
  const b = readFileSync(join(dir, f));
  const dv = new DataView(b.buffer, b.byteOffset, b.byteLength);
  const kind = b[4], seed = dv.getBigUint64(5, true), count = dv.getUint32(13, true), digest = dv.getBigUint64(17, true);
  const ml = dv.getUint32(25, true);
  const mp = ml > 0 ? put(b.subarray(29, 29 + ml)) : 0;
  const full = BigInt.asUintN(64, x.rc_vectors(kind, seed, count, mp, ml));
  const ok = full === digest;
  if (!ok) bad++;
  const n = Math.min(PER_FILE, count);
  const sub = x.rc_vectors(kind, seed, n, mp, ml);
  console.log(`${f.padEnd(14)} full ${count} cases ${hex(full)} ${ok ? 'OK' : 'MISMATCH vs ' + hex(digest)}; subset ${n} ${hex(sub)}`);
  if (ml > 0) writeFileSync(join(dirname(outPath), f + '.map'), b.subarray(29, 29 + ml));
  lines.push(`${f} ${kind} ${seed} ${n} ${hex(sub)} ${ml > 0 ? f + '.map' : '-'}`);
  if (mp) x.rc_dealloc(mp, ml);
}
writeFileSync(outPath, lines.join('\n') + '\n');
process.exit(bad ? 1 : 0);
