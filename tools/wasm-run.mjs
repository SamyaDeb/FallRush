// tools/wasm-run.mjs — loads rush_core_wasm.wasm (C ABI, no wasm-bindgen) in Node and checks every golden vector
// file against the digest native wrote:   node tools/wasm-run.mjs <rush_core_wasm.wasm> <vectors dir>
// Exit 0 when every file matches. Also smoke-tests rc_init / rc_step / rc_hash on the embedded vector map.
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

const [wasmPath, dir] = process.argv.slice(2);
if (!wasmPath || !dir) { console.error('usage: node tools/wasm-run.mjs <wasm> <vectors dir>'); process.exit(2); }
const { instance } = await WebAssembly.instantiate(readFileSync(wasmPath), {});
const x = instance.exports;
const mem = () => new Uint8Array(x.memory.buffer);
const put = (bytes) => { const p = x.rc_alloc(bytes.length); mem().set(bytes, p); return p; };

console.log(`rc_version 0x${x.rc_version().toString(16)} rc_world_size ${x.rc_world_size()}`);
let bad = 0;
for (const f of readdirSync(dir).filter((n) => /_v\d+\.bin$/.test(n)).sort()) {
  const b = readFileSync(join(dir, f));
  const dv = new DataView(b.buffer, b.byteOffset, b.byteLength);
  if (b.toString('latin1', 0, 4) !== 'RCV1') { console.log(`${f}: bad magic`); bad++; continue; }
  const kind = b[4], seed = dv.getBigUint64(5, true), count = dv.getUint32(13, true), digest = dv.getBigUint64(17, true);
  const ml = dv.getUint32(25, true);
  let mp = 0;
  if (ml > 0) mp = put(b.subarray(29, 29 + ml));
  const t0 = performance.now();
  const got = BigInt.asUintN(64, x.rc_vectors(kind, seed, count, mp, ml));
  const ms = (performance.now() - t0).toFixed(0);
  const ok = got === digest;
  console.log(`${f.padEnd(16)} ${String(count).padStart(6)} cases wasm ${got.toString(16).padStart(16, '0')} file ${digest.toString(16).padStart(16, '0')} ${ok ? 'OK' : 'MISMATCH'} (${ms} ms)`);
  if (!ok) bad++;
  if (mp) x.rc_dealloc(mp, ml);
}
// ABI smoke: 12 players on the vector map, 100 steps, all missing inputs.
const map = readFileSync(join(dir, 'vector_map.mapblob'));
const mp = put(map);
const cfg = new Uint8Array(24); const cv = new DataView(cfg.buffer);
cv.setUint32(0, 20, true); cv.setUint32(4, 0xfff, true); cv.setUint32(8, 0, true); cv.setBigUint64(16, 42n, true);
const h = x.rc_init(mp, map.length, put(cfg));
for (let i = 0; i < 100; i++) x.rc_step(h, 0);
console.log(`abi smoke: handle ${h}, 100 steps, world hash ${BigInt.asUintN(64, x.rc_hash(h)).toString(16)}`);
process.exit(bad ? 1 : 0);
