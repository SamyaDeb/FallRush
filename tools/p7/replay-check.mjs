#!/usr/bin/env node
// Replay a Fall Rush match yourself (P7, README "Replay a match yourself"): downloads the archived inputs + rollup
// Worlds of a match from the public indexer, re-simulates it tick by tick with rush_core.wasm (the same rush-core the
// program runs as sBPF on the MagicBlock rollup) and checks every recorded World byte for byte, plus the receipt's
// transcript in the last World. No dependencies beyond Node 20+.
//   node tools/p7/replay-check.mjs <match address | path/to/world.bin | path/to/world2.bin>
//        [--base https://fallrush.fun] [--wasm path] [--maps dir] [--map-<Name> path] [--sealed [--rpc url]] [--summary path]
// Two paths, chosen per match:
//   - full on-chain match (receipt trust bit 4 ALL_PROGRAM, a World of version 2, or a local world.bin of the dev
//     runner): every snapshot, mid-tick parts included, from the lobby through all 4 rounds. Steps exactly as the
//     chain does (rc_step_chain / rc_step_part: world::step_slice part by part, each round's map load with
//     director::needs_map → load_round, Chaos Drops ingested after the load). Needs the fullchain rush_core.wasm.
//   - hybrid (rounds 1–3 Classic, the Final on-chain): the Final's complete ticks with rc_step, as before.
// Local recordings: world.bin of tools/keeper/run.ts ([u32 tick][u8 part][u16 len][World], no inputs: bot-only runs)
// or world2.bin of the daemon ([u32 tick][u8 part][u16 len][80 B WorldAcc header][World]); summary.json next to it is read.
// Map blobs: maps/<Name>.mapblob, or --maps dir, or --map-GooTower path, or --sealed (the sealed blobs the arena's
// tick() read: summary.maps fetched from Solana via --rpc). A replay is only exact with the blobs the program used.
import { readFileSync, existsSync } from 'node:fs';
import { join, dirname, basename } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const opt = (k, d) => { const i = process.argv.indexOf('--' + k); return i > 0 ? process.argv[i + 1] : d; };
const flag = (k) => process.argv.includes('--' + k);
const id = process.argv[2];
if (!id || id.startsWith('--')) { console.error('usage: node tools/p7/replay-check.mjs <match address | world.bin path> [--base https://fallrush.fun]'); process.exit(2); }
const BASE = opt('base', 'https://fallrush.fun');
const local = existsSync(id);
// ---- the archive
let sum = {}, raw;
if (local) {
  raw = new Uint8Array(readFileSync(id));
  const sp = opt('summary', join(dirname(id), 'summary.json'));
  if (existsSync(sp)) sum = { ok: true, summary: JSON.parse(readFileSync(sp, 'utf8')) };
} else {
  sum = await (await fetch(`${BASE}/api/match/${id}`)).json();
  if (!sum.ok) { console.error('indexer:', sum.error ?? 'match not archived'); process.exit(2); }
  raw = new Uint8Array(await (await fetch(`${BASE}/api/replay/${id}/world2.bin`)).arrayBuffer());
}
const S = sum.summary ?? {};
const withHdr = !local || basename(id).startsWith('world2');
// world2.bin: [u32 tick][u8 part][u16 len][80 B WorldAcc header (tick, len, part, phase, round @11, cur[12] @16 = the
// inputs the program latched for this tick, cur_some @64, …)][World bytes]; world.bin: the same without the header
const H = withHdr ? 80 : 0;
const all = [];
for (let o = 0; o + 7 + H <= raw.length;) {
  const dv = new DataView(raw.buffer, raw.byteOffset + o, 7), tick = dv.getUint32(0, true), part = raw[o + 4], len = dv.getUint16(5, true);
  const hdr = withHdr ? raw.subarray(o + 7, o + 87) : null, world = raw.subarray(o + 7 + H, o + 7 + H + len);
  if (world.length < len) break;
  all.push({ tick, part, hdr, world });
  o += 7 + H + len;
}
const inputsOf = (hdr) => { const b = new Uint8Array(72); if (!hdr) return b; const some = hdr[64] | (hdr[65] << 8); for (let s = 0; s < 12; s++) if ((some >> s) & 1) { const c = hdr.subarray(16 + 4 * s, 20 + 4 * s); b.set([1, c[0], c[1], c[2], 0, c[3]], 6 * s); } return b; };
const eq = (a, b) => a.length === b.length && a.every((x, i) => x === b[i]);
const hex = (s) => Uint8Array.from(s.match(/../g).map((x) => parseInt(x, 16)));
const holds = (hay, hx) => { const n = hex(hx); outer: for (let i = 0; i + n.length <= hay.length; i++) { for (let j = 0; j < n.length; j++) if (hay[i + j] !== n[j]) continue outer; return true; } return false; };
const trust = Number(S.receipt?.trust ?? 0);
// World byte 0 = its version (2 = a start_sim world); the dev runner's world.bin is always a full match
const full = flag('full') || (!flag('final') && ((trust & 4) !== 0 || all.some((f) => f.world[0] === 2) || !withHdr));
const buys = S.drops?.buys ?? [];
const transcriptHex = S.receipt?.finalTranscript ?? S.onSolana?.transcript;
const tcheck = (last) => (transcriptHex && /^[0-9a-f]{64}$/.test(transcriptHex) && transcriptHex !== '0'.repeat(64) ? holds(last, transcriptHex) : null);

// ---- the wasm: --wasm, else the Unity copy, else this repo's crate build (a full match needs the fullchain exports)
const wasmPaths = opt('wasm') ? [opt('wasm')] : [join(ROOT, 'client-unity/Assets/StreamingAssets/rush_core.wasm'), join(ROOT, 'crates/target/wasm32-unknown-unknown/release/rush_core_wasm.wasm')].filter((p) => existsSync(p));
let e, wasmUsed;
for (const p of wasmPaths) { const x = (await WebAssembly.instantiate(readFileSync(p), {})).instance.exports; if (!full || x.rc_step_chain) { e = x; wasmUsed = p; break; } }
if (!e) { console.error(full ? 'a full on-chain match needs a rush_core.wasm with rc_step_chain (docs/proof/fullchain/WEB.md): --wasm path' : 'no rush_core.wasm found: --wasm path'); process.exit(2); }
const put = (b) => { const p = e.rc_alloc(b.length); new Uint8Array(e.memory.buffer, p, b.length).set(b); return p; };
const cfg = new Uint8Array(24); new DataView(cfg.buffer).setUint32(0, 30, true);
// ---- map blobs (program map slots 0..3 = deal ids 0..3, 4 = the lobby)
const NAMES = ['BlockPartyDash', 'GooTower', 'StarScramble', 'CrumbleShelf', 'Lobby'];
const blobs = [];
if (full && flag('sealed')) {
  const keys = S.maps ?? [];
  if (keys.length !== 5) { console.error('--sealed: the summary lists no 5 map accounts'); process.exit(2); }
  const rpc = opt('rpc', 'https://api.devnet.solana.com');
  for (const k of keys) {
    const j = await (await fetch(rpc, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'getAccountInfo', params: [k, { encoding: 'base64' }] }) })).json();
    const d = Buffer.from(j.result?.value?.data?.[0] ?? '', 'base64');
    if (d.length < 80) { console.error('--sealed: map account', k, 'unreadable'); process.exit(2); }
    blobs.push(new Uint8Array(d.subarray(80, 80 + d.readUInt32LE(40)))); // sim.rs map_bytes: 80 B header, len @40
  }
} else if (full) {
  const dir = opt('maps', join(ROOT, 'maps'));
  for (const n of NAMES) blobs.push(new Uint8Array(readFileSync(opt('map-' + n, join(dir, `${n}.mapblob`)))));
}
const initMap = full ? blobs[4] : new Uint8Array(readFileSync(opt('map', join(ROOT, 'maps/CrumbleShelf.mapblob'))));
const h = e.rc_init(put(initMap), initMap.length, put(cfg));
if (full) blobs.forEach((b, s) => { if (e.rc_chain_map(h, s, put(b), b.length) !== 0) { console.error('bad map blob', NAMES[s]); process.exit(2); } });
const cap = e.rc_world_size() + 4096, buf = e.rc_alloc(cap), inBuf = e.rc_alloc(72);
const setW = (w) => { new Uint8Array(e.memory.buffer, buf, w.length).set(w); return e.rc_set_world(h, buf, w.length) === 0; };
const bytes = () => new Uint8Array(e.memory.buffer, buf, e.rc_world_bytes(h, buf, cap)).slice();
const setIn = (inp) => { new Uint8Array(e.memory.buffer, inBuf, 72).set(inp); return inBuf; };

if (!full) {
  // ---- hybrid: the on-chain Final (round 4) only, complete ticks, rc_step (unchanged since P7)
  const frames = all.filter((f) => f.part === 0 && f.hdr && f.hdr[11] === 4);
  let pairs = 0, ok = 0, gaps = 0, first = -1, drops = 0, mapLoad = 0;
  for (let i = 1; i < frames.length; i++) {
    if (frames[i].tick !== frames[i - 1].tick + 1) { gaps++; continue; }
    // the Final's tick 0 → 1 is the program's map load (load_round inside tick(), outside rc_step): start after it
    if (frames[i - 1].tick === 0) { mapLoad++; continue; }
    pairs++;
    setW(frames[i].world); const q1 = e.rc_drops_queued(h);
    setW(frames[i - 1].world); const q0 = e.rc_drops_queued(h);
    for (let j = q0; j < q1; j++) { const k = buys[j]?.kind; if (k) { e.rc_ingest_drop(h, k); drops++; } }
    e.rc_step(h, setIn(inputsOf(frames[i].hdr)));
    if (eq(bytes(), frames[i].world)) ok++; else if (first < 0) first = frames[i].tick;
  }
  const tOk = tcheck(frames[frames.length - 1]?.world ?? new Uint8Array());
  const pass = pairs > 0 && ok === pairs && tOk !== false;
  console.log(JSON.stringify({ match: id, mode: 'final', snapshots: frames.length, ticks: frames.length ? [frames[0].tick, frames[frames.length - 1].tick] : null, pairs, byteExact: ok, gaps, mapLoadTickSkipped: mapLoad > 0, firstMismatchTick: first, dropsIngested: drops, finalTranscriptInLastWorld: tOk, pass }, null, 1));
  console.log(pass ? 'replay = on-chain ✓' : 'replay ≠ on-chain ✗');
  process.exit(pass ? 0 : 1);
}

// ---- full on-chain match: every snapshot, mid-tick parts included, lobby → 4 rounds
// A pair A → B is checked when B is one run of program executions away: A complete (part 0) → B in tick A.tick + 1,
// or A mid-tick (part q) → B later in the same tick (part p > q, or 0 = completed). Anything else is a recording gap
// (the subscription missed updates): the check restarts from B. Every pair starts from the recorded A (a mismatch
// never cascades). Complete ticks use rc_step_chain; mid-tick targets are reached one player per call (budget 0):
// the slice size never changes the World, only where a call stops, and a recorded part is a slot boundary.
const frames = all.filter((f, i) => i === 0 || f.tick !== all[i - 1].tick || f.part !== all[i - 1].part);
const reach = (a, b) => (a.part === 0 ? b.tick === a.tick + 1 : b.tick === a.tick && (b.part === 0 || b.part > a.part));
let pairs = 0, ok = 0, gaps = 0, first = null, drops = 0, mapLoads = 0, fullTicks = 0, partPairs = 0, err = null;
const perRound = {};
const t0 = Date.now();
for (let i = 1; i < frames.length; i++) {
  const A = frames[i - 1], B = frames[i];
  if (!reach(A, B)) { gaps++; continue; }
  if (!setW(A.world)) { err = `the World at tick ${A.tick} does not decode`; break; }
  const inp = setIn(inputsOf(B.hdr));
  let part = A.part;
  if (part === 0) {
    // sim.rs tick_inner at part 0: the map load, then the DropQueue purchases enter the World, then the slice
    if (e.rc_chain_load(h) >= 0) mapLoads++;
    if (buys.length) {
      const q0 = e.rc_drops_queued(h), mid = bytes();
      setW(B.world); const q1 = e.rc_drops_queued(h); setW(mid);
      for (let j = q0; j < q1; j++) { const k = buys[j]?.kind; if (k) { e.rc_ingest_drop(h, k); drops++; } }
    }
  }
  if (part === 0 && B.part === 0) {
    const n = e.rc_step_chain(h, inp, 6000);
    if (n < 0) { err = `rc_step_chain ${n} at tick ${B.tick} (map not registered?)`; break; }
    fullTicks++;
  } else {
    let k = 0;
    do { part = e.rc_step_part(h, inp, part, 0); k++; } while (part >= 0 && part !== B.part && k < 16);
    if (part !== B.part) { err = `rc_step_part did not reach part ${B.part} of tick ${B.tick} (${part})`; break; }
    partPairs++;
  }
  pairs++;
  const r = (perRound[`r${e.rc_round(h) & 0xff}`] ??= { pairs: 0, byteExact: 0 });
  r.pairs++;
  if (eq(bytes(), B.world)) { ok++; r.byteExact++; } else if (!first) first = { tick: B.tick, part: B.part, fromPart: A.part };
}
const last = frames[frames.length - 1]?.world ?? new Uint8Array();
let lastWorld = null;
if (last.length && setW(last)) { const r = e.rc_round(h); lastWorld = { round: r & 0xff, directorPhase: (r >> 8) & 0xff }; }
const tOk = tcheck(last);
const pass = !err && pairs > 0 && ok === pairs && tOk !== false;
console.log(JSON.stringify({
  match: id, mode: 'full', worldVersions: [...new Set(frames.map((f) => f.world[0]))], trust, wasm: wasmUsed.replace(ROOT + '/', ''),
  maps: Object.fromEntries(NAMES.map((n, i) => [n, createHash('sha256').update(blobs[i]).digest('hex').slice(0, 16)])),
  snapshots: frames.length, ticks: frames.length ? [frames[0].tick, frames[frames.length - 1].tick] : null,
  pairs, byteExact: ok, fullTickPairs: fullTicks, midTickPairs: partPairs, gaps, mapLoads, perRound,
  firstMismatch: first, dropsIngested: drops, lastWorld, transcriptInLastWorld: tOk, error: err, seconds: (Date.now() - t0) / 1000, pass,
}, null, 1));
console.log(pass ? 'replay = on-chain ✓' : 'replay ≠ on-chain ✗');
process.exit(pass ? 0 : 1);
