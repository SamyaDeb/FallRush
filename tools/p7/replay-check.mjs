#!/usr/bin/env node
// Replay a Fall Rush match yourself (P7, README "Replay a match yourself"): downloads the archived inputs + rollup
// Worlds of a match from the public indexer, re-simulates the on-chain Final tick by tick with rush_core.wasm (the same
// rush-core the program runs as sBPF on the MagicBlock rollup) and checks every tick byte for byte, plus the receipt's
// Final transcript in the last World. No dependencies beyond Node 20+.
//   node tools/p7/replay-check.mjs <match address> [--base https://fallrush.fun] [--wasm path] [--map path]
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const opt = (k, d) => { const i = process.argv.indexOf('--' + k); return i > 0 ? process.argv[i + 1] : d; };
const id = process.argv[2];
if (!id || id.startsWith('--')) { console.error('usage: node tools/p7/replay-check.mjs <match address> [--base https://fallrush.fun]'); process.exit(2); }
const BASE = opt('base', 'https://fallrush.fun');
const wasm = readFileSync(opt('wasm', join(ROOT, 'client-unity/Assets/StreamingAssets/rush_core.wasm')));
const mapBlob = readFileSync(opt('map', join(ROOT, 'maps/CrumbleShelf.mapblob')));
const { instance } = await WebAssembly.instantiate(wasm, {});
const e = instance.exports;
const put = (b) => { const p = e.rc_alloc(b.length); new Uint8Array(e.memory.buffer, p, b.length).set(b); return p; };
const cfg = new Uint8Array(24); new DataView(cfg.buffer).setUint32(0, 30, true);
const h = e.rc_init(put(mapBlob), mapBlob.length, put(cfg));
const cap = e.rc_world_size() + 4096, buf = e.rc_alloc(cap);
const setW = (w) => { new Uint8Array(e.memory.buffer, buf, w.length).set(w); return e.rc_set_world(h, buf, w.length) === 0; };
const bytes = () => new Uint8Array(e.memory.buffer, buf, e.rc_world_bytes(h, buf, cap)).slice();
const step = (inp) => { const p = put(inp); e.rc_step(h, p); e.rc_dealloc(p, inp.length); };

const sum = await (await fetch(`${BASE}/api/match/${id}`)).json();
if (!sum.ok) { console.error('indexer:', sum.error ?? 'match not archived'); process.exit(2); }
const raw = new Uint8Array(await (await fetch(`${BASE}/api/replay/${id}/world2.bin`)).arrayBuffer());
// world2.bin: [u32 tick][u8 part][u16 len][80 B WorldAcc header (tick, len, phase, cur[12] = the inputs the program
// latched for this tick, cur_some, …)][World bytes]
const frames = [];
for (let o = 0; o + 87 <= raw.length;) {
  const dv = new DataView(raw.buffer, raw.byteOffset + o, 7), tick = dv.getUint32(0, true), part = raw[o + 4], len = dv.getUint16(5, true);
  const hdr = raw.subarray(o + 7, o + 87), world = raw.subarray(o + 87, o + 87 + len);
  if (world.length < len) break;
  if (part === 0 && hdr[11] === 4) frames.push({ tick, hdr, world });
  o += 87 + len;
}
const inputsOf = (hdr) => { const some = hdr[64] | (hdr[65] << 8), b = new Uint8Array(72); for (let s = 0; s < 12; s++) if ((some >> s) & 1) { const c = hdr.subarray(16 + 4 * s, 20 + 4 * s); b.set([1, c[0], c[1], c[2], 0, c[3]], 6 * s); } return b; };
const eq = (a, b) => a.length === b.length && a.every((x, i) => x === b[i]);
const buys = sum.summary?.drops?.buys ?? [];
let pairs = 0, ok = 0, gaps = 0, first = -1, drops = 0, mapLoad = 0;
for (let i = 1; i < frames.length; i++) {
  if (frames[i].tick !== frames[i - 1].tick + 1) { gaps++; continue; }
  // the Final's tick 0 → 1 is the program's map load (load_round inside tick(), outside rc_step): start after it
  if (frames[i - 1].tick === 0) { mapLoad++; continue; }
  pairs++;
  setW(frames[i].world); const q1 = e.rc_drops_queued(h);
  setW(frames[i - 1].world); const q0 = e.rc_drops_queued(h);
  for (let j = q0; j < q1; j++) { const k = buys[j]?.kind; if (k) { e.rc_ingest_drop(h, k); drops++; } }
  step(inputsOf(frames[i].hdr));
  if (eq(bytes(), frames[i].world)) ok++; else if (first < 0) first = frames[i].tick;
}
const ft = sum.summary?.receipt?.finalTranscript;
const hex = (s) => Uint8Array.from(s.match(/../g).map((x) => parseInt(x, 16)));
const last = frames[frames.length - 1]?.world ?? new Uint8Array();
const tOk = ft && ft !== '0'.repeat(64) ? (() => { const n = hex(ft); outer: for (let i = 0; i + n.length <= last.length; i++) { for (let j = 0; j < n.length; j++) if (last[i + j] !== n[j]) continue outer; return true; } return false; })() : null;
const pass = pairs > 0 && ok === pairs && tOk !== false;
console.log(JSON.stringify({ match: id, snapshots: frames.length, ticks: frames.length ? [frames[0].tick, frames[frames.length - 1].tick] : null, pairs, byteExact: ok, gaps, mapLoadTickSkipped: mapLoad > 0, firstMismatchTick: first, dropsIngested: drops, finalTranscriptInLastWorld: tOk, pass }, null, 1));
console.log(pass ? 'replay = on-chain ✓' : 'replay ≠ on-chain ✗');
process.exit(pass ? 0 : 1);
