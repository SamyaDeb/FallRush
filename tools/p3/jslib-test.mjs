// tools/p3/jslib-test.mjs — runs Plugins/WebGL/RushCoreWasm.jslib in Node against StreamingAssets/rush_core.wasm with
// a fake Emscripten heap, so the bridge is checked before a WebGL build: boot, the 200-case vector subset, and
// rcw_run (batched set_world + n steps + player records) = stepping the core directly.
//   node tools/p3/jslib-test.mjs
import { readFileSync } from 'node:fs';
const U = 'client-unity/Assets';
const lib = {};
globalThis.LibraryManager = { library: lib };
globalThis.mergeInto = (a, b) => Object.assign(a, b);
const HEAP = new Uint8Array(1 << 20);
globalThis.HEAPU8 = HEAP;
let top = 16;
const alloc = (b) => { const p = top; HEAP.set(b, p); top += (b.length + 15) & ~15; return p; };
const str = (s) => alloc(new TextEncoder().encode(s + '\0'));
globalThis.UTF8ToString = (p) => { let e = p; while (HEAP[e]) e++; return new TextDecoder().decode(HEAP.subarray(p, e)); };
globalThis.fetch = async (u) => ({ ok: true, arrayBuffer: async () => readFileSync(u).buffer.slice(0) });
new Function(readFileSync(`${U}/_Game/Plugins/WebGL/RushCoreWasm.jslib`, 'utf8'))();
const RCW = lib.$RCW;
globalThis.RCW = RCW;
const f = {};
for (const k of Object.keys(lib)) if (k.startsWith('rcw_') && !k.includes('__')) f[k] = lib[k];
f.rcw_boot(str(`${U}/StreamingAssets/rush_core.wasm`));
while (f.rcw_state() === 0) await new Promise((r) => setTimeout(r, 5));
console.log('state', f.rcw_state(), 'version 0x' + f.rcw_version().toString(16));
let bad = 0;
const out8 = alloc(new Uint8Array(8));
const u64 = () => new DataView(HEAP.buffer, out8, 8).getBigUint64(0, true).toString(16).padStart(16, '0');
for (const line of readFileSync(`${U}/StreamingAssets/rcvectors/subset_expected.txt`, 'utf8').split('\n')) {
  if (!line || line[0] === '#') continue;
  const [file, kind, seed, n, want, mapf] = line.split(' ');
  const mb = mapf && mapf !== '-' ? readFileSync(`${U}/StreamingAssets/rcvectors/${mapf}`) : null;
  const ml = mb ? mb.length : 0;
  const mp = mb ? alloc(mb) : 0;
  const s = BigInt(seed);
  f.rcw_vectors(+kind, Number(s & 0xffffffffn), Number(s >> 32n), +n, mp, ml, out8);
  const ok = u64() === want; if (!ok) bad++;
  console.log(file, ok ? 'PASS' : `FAIL ${u64()} != ${want}`);
}
// rcw_run vs direct: CrumbleShelf, 3 players (slot 0 scripted, 1-2 bots), 90 ticks.
const map = readFileSync(`${U}/_Game/Scripts/FeelLab/Resources/feellab/CrumbleShelf.bytes`);
const cfg = new Uint8Array(24); const cv = new DataView(cfg.buffer);
cv.setUint32(0, 30, true); cv.setUint32(4, 0b111, true); cv.setUint32(8, 0b110, true); cv.setBigUint64(16, 7n, true);
const mp = alloc(map), cp = alloc(cfg);
const h1 = f.rcw_init(mp, map.length, cp), h2 = f.rcw_init(mp, map.length, cp);
const N = 60, inputs = new Uint8Array(N * 72);
for (let k = 0; k < N; k++) { inputs[k * 72] = 1; inputs[k * 72 + 1] = 100; inputs[k * 72 + 3] = k % 20 === 0 ? 3 : 0; }
const ip = alloc(inputs), op = alloc(new Uint8Array(N * 12 * 36));
const t0 = performance.now();
f.rcw_run(h1, 0, 0, N, ip, inputs.length, 0b111, op, N * 12 * 36);
const ms = performance.now() - t0;
const x = RCW.x;
for (let k = 0; k < N; k++) { new Uint8Array(x.memory.buffer).set(inputs.subarray(k * 72, k * 72 + 72), RCW.I); x.rc_step(h2, RCW.I); }
f.rcw_hash(h1, out8); const a = u64(); f.rcw_hash(h2, out8); const b2 = u64();
const okRun = a === b2; if (!okRun) bad++;
// rebase: world bytes of h2 → set on h1 via rcw_run(world, 0 ticks)
const wb = alloc(new Uint8Array(4096)); const wl = f.rcw_world_bytes(h2, wb, 4096);
f.rcw_run(h1, wb, wl, 0, 0, 0, 0, 0, 0); f.rcw_hash(h1, out8);
const okRebase = u64() === b2; if (!okRebase) bad++;
const p0x = new DataView(HEAP.buffer, op + ((N - 1) * 12) * 36).getInt32(0, true);
console.log(`rcw_run ${N} ticks ${okRun ? 'PASS' : 'FAIL'} (${(ms / N).toFixed(3)} ms/tick, slot0 x ${p0x} mm), rebase ${okRebase ? 'PASS' : 'FAIL'}, world ${wl} B`);
console.log(bad ? `FAIL (${bad})` : 'ALL PASS');
process.exit(bad ? 1 : 0);
