// The replay check (moved out of pages/replay.ts in P7 so /receipt can run it too): plays the archived on-chain Final
// through rush_core.wasm. Every recorded World snapshot carries the inputs the program latched for that tick
// (WorldAcc.cur); the check steps snapshot T with the inputs of T + 1 and compares the bytes with the recorded T + 1.
// "replay = on-chain ✓" when every consecutive pair matches byte for byte and the replayed final World holds the
// receipt's Final transcript (D-REPLAY: the hash check decides).
// fullchain: a full on-chain match (receipt trust bit 4, or a World of version 2) is checked from the lobby through all
// 4 rounds with the chain-exact step (verifyFull below; the same algorithm as tools/p7/replay-check.mjs).
import { CoreWorld, CHAIN_MAPS } from './core';
import { account, BASE_HTTP } from './net';

export interface Frame { tick: number; part: number; hdr: Uint8Array; world: Uint8Array }
export function parseWorld2(buf: Uint8Array): Frame[] {
  const out: Frame[] = [], dv = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
  for (let o = 0; o + 87 <= buf.length;) {
    const tick = dv.getUint32(o, true), part = buf[o + 4], len = dv.getUint16(o + 5, true);
    const hdr = buf.subarray(o + 7, o + 87), world = buf.subarray(o + 87, o + 87 + len);
    if (world.length < len) break;
    out.push({ tick, part, hdr, world });
    o += 87 + len;
  }
  return out;
}
export const inputsOf = (hdr: Uint8Array) => {
  // hdr = WorldAcc[128..208]: cur[12] × 4 B at +16, cur_some u16 at +64
  const some = hdr[64] | (hdr[65] << 8), b = new Uint8Array(72);
  for (let s = 0; s < 12; s++) if ((some >> s) & 1) { const c = hdr.subarray(16 + 4 * s, 20 + 4 * s); b.set([1, c[0], c[1], c[2], 0, c[3]], 6 * s); }
  return b;
};
const eq = (a: Uint8Array, b: Uint8Array) => a.length === b.length && a.every((x, i) => x === b[i]);
const has = (hay: Uint8Array, hex: string) => { const n = Uint8Array.from(hex.match(/../g)!.map((h) => parseInt(h, 16))); outer: for (let i = 0; i + n.length <= hay.length; i++) { for (let j = 0; j < n.length; j++) if (hay[i + j] !== n[j]) continue outer; return true; } return false; };

export interface Verdict { pass: boolean; pairs: number; ok: number; gaps: number; first: number; dropsIngested: number; mapLoad?: number; transcript: boolean | null; frames: Frame[]; cw: CoreWorld; error?: string;
  /** fullchain: every round re-simulated (the chain-exact step); pairs per round; mid-tick pairs among `pairs`. */
  full?: boolean; perRound?: Record<number, { pairs: number; ok: number }>; midTick?: number; mapLoads?: number }

/** A full on-chain match: every round computed by the program (receipt trust bit 4 ALL_PROGRAM, or a version 2 World). */
export const isFull = (summary: any, frames: Frame[] = []) => ((summary?.receipt?.trust ?? 0) & 4) !== 0 || frames.some((f) => f.world[0] === 2);

/** Downloads the archive of `id` and re-simulates the Final; `progress(i, n)` while it runs. */
export async function verifyMatch(id: string, summary: any, progress: (i: number, n: number) => void = () => {}): Promise<Verdict> {
  const r = await fetch(`/api/replay/${id}/world2.bin`);
  const cw = await CoreWorld.create('CrumbleShelf');
  const empty = { pass: false, pairs: 0, ok: 0, gaps: 0, first: -1, dropsIngested: 0, transcript: null, frames: [] as Frame[], cw };
  if (!r.ok) return { ...empty, error: 'no World recording for this match' };
  const all = parseWorld2(new Uint8Array(await r.arrayBuffer()));
  if (isFull(summary, all)) return verifyFull(all, summary, progress, empty);
  const frames = all.filter((f) => f.part === 0 && f.hdr[11] === 4); // round 4 (the Final)
  if (frames.length < 2) return { ...empty, frames, error: 'the recording has no Final' };
  let pairs = 0, ok = 0, gaps = 0, first = -1, dropsIngested = 0, mapLoad = 0;
  const buys: { kind: number }[] = summary?.drops?.buys ?? [];
  for (let i = 1; i < frames.length; i++) {
    if (frames[i].tick !== frames[i - 1].tick + 1) { gaps++; continue; }
    // P7 fix (match 3's "first-tick mismatch"): the Final's tick 0 → 1 is the program's map load (director::needs_map →
    // load_round, run in `tick()` before stepping), which rush_core.wasm's per-tick rc_step does not perform; the check
    // starts at the first tick after the load (archives that begin later never contained this pair)
    if (frames[i - 1].tick === 0) { mapLoad++; continue; }
    pairs++;
    // Chaos Drops: the program copies new DropQueue purchases into the World before stepping (sim.rs ingest_drops);
    // the archived purchase kinds are applied at the tick the recording shows them entering the World
    cw.set(frames[i].world); const q1 = cw.queuedDrops();
    cw.set(frames[i - 1].world); const q0 = cw.queuedDrops();
    for (let j = q0; j < q1; j++) { const k = buys[j]?.kind; if (k) { cw.ingestDrop(k); dropsIngested++; } }
    cw.step(inputsOf(frames[i].hdr));
    if (eq(cw.bytes(), frames[i].world)) ok++; else if (first < 0) first = frames[i].tick;
    if (i % 400 === 0) { progress(i, frames.length); await new Promise((x) => setTimeout(x, 0)); }
  }
  const ft = summary?.receipt?.finalTranscript;
  const last = frames[frames.length - 1];
  const transcript = ft && /^[0-9a-f]{64}$/.test(ft) && ft !== '0'.repeat(64) ? has(last.world, ft) : null;
  return { pass: pairs > 0 && ok === pairs && transcript !== false, pairs, ok, gaps, first, dropsIngested, mapLoad, transcript, frames, cw };
}

/** The sealed map blobs the arena's tick() read (summary.maps: 5 map accounts on Solana), else null. */
async function sealedMaps(summary: any): Promise<Uint8Array[] | null> {
  const keys: string[] = summary?.maps ?? [];
  if (keys.length !== 5) return null;
  try {
    const out: Uint8Array[] = [];
    for (const k of keys) {
      const a = await account(BASE_HTTP, k);
      if (!a || a.data.length < 80) return null;
      const len = new DataView(a.data.buffer, a.data.byteOffset).getUint32(40, true); // sim.rs map_bytes: 80 B header, len @40
      out.push(a.data.slice(80, 80 + len));
    }
    return out;
  } catch { return null; }
}

/** fullchain: every recorded snapshot (mid-tick parts included), lobby → 4 rounds, stepped exactly as the chain does:
 *  a complete tick = step_slice parts until done (rc_step_chain), a mid-tick part = one player per call, each round's
 *  map load (director::needs_map → load_round) before the tick, Chaos Drops ingested after the load. */
async function verifyFull(all: Frame[], summary: any, progress: (i: number, n: number) => void, empty: Verdict): Promise<Verdict> {
  const cw = await CoreWorld.createChain((await sealedMaps(summary)) ?? undefined);
  if (!cw) return { ...empty, full: true, error: "this page's rush_core.wasm predates full on-chain matches (no rc_step_chain)" };
  const frames = all.filter((f, i) => i === 0 || f.tick !== all[i - 1].tick || f.part !== all[i - 1].part);
  const reach = (a: Frame, b: Frame) => (a.part === 0 ? b.tick === a.tick + 1 : b.tick === a.tick && (b.part === 0 || b.part > a.part));
  const buys: { kind: number }[] = summary?.drops?.buys ?? [];
  let pairs = 0, ok = 0, gaps = 0, first = -1, dropsIngested = 0, midTick = 0, mapLoads = 0, error: string | undefined;
  const perRound: Record<number, { pairs: number; ok: number }> = {};
  for (let i = 1; i < frames.length; i++) {
    const A = frames[i - 1], B = frames[i];
    if (!reach(A, B)) { gaps++; continue; }
    if (!cw.set(A.world)) { error = `the World at tick ${A.tick} does not decode`; break; }
    const inp = inputsOf(B.hdr);
    let part = A.part;
    if (part === 0) {
      if (cw.chainLoad() >= 0) mapLoads++;
      if (buys.length) {
        const q0 = cw.queuedDrops(), mid = cw.bytes();
        cw.set(B.world); const q1 = cw.queuedDrops(); cw.set(mid);
        for (let j = q0; j < q1; j++) { const k = buys[j]?.kind; if (k) { cw.ingestDrop(k); dropsIngested++; } }
      }
    }
    if (part === 0 && B.part === 0) {
      if (cw.stepChain(inp) < 0) { error = `a map of tick ${B.tick} is missing`; break; }
    } else {
      let k = 0;
      do { part = cw.stepPart(inp, part, 0); k++; } while (part >= 0 && part !== B.part && k < 16);
      if (part !== B.part) { error = `part ${B.part} of tick ${B.tick} not reached`; break; }
      midTick++;
    }
    pairs++;
    const pr = (perRound[cw.round().round] ??= { pairs: 0, ok: 0 });
    pr.pairs++;
    if (eq(cw.bytes(), B.world)) { ok++; pr.ok++; } else if (first < 0) first = B.tick;
    if (i % 2000 === 0) { progress(i, frames.length); await new Promise((x) => setTimeout(x, 0)); }
  }
  const ft = summary?.receipt?.finalTranscript;
  const complete = frames.filter((f) => f.part === 0);
  const last = frames[frames.length - 1];
  const transcript = last && ft && /^[0-9a-f]{64}$/.test(ft) && ft !== '0'.repeat(64) ? has(last.world, ft) : null;
  return { pass: !error && pairs > 0 && ok === pairs && transcript !== false, pairs, ok, gaps, first, dropsIngested, transcript, frames: complete, cw, error, full: true, perRound, midTick, mapLoads };
}
