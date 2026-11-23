// The replay check (moved out of pages/replay.ts in P7 so /receipt can run it too): plays the archived on-chain Final
// through rush_core.wasm. Every recorded World snapshot carries the inputs the program latched for that tick
// (WorldAcc.cur); the check steps snapshot T with the inputs of T + 1 and compares the bytes with the recorded T + 1.
// "replay = on-chain ✓" when every consecutive pair matches byte for byte and the replayed final World holds the
// receipt's Final transcript (D-REPLAY: the hash check decides).
import { CoreWorld } from './core';

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

export interface Verdict { pass: boolean; pairs: number; ok: number; gaps: number; first: number; dropsIngested: number; mapLoad?: number; transcript: boolean | null; frames: Frame[]; cw: CoreWorld; error?: string }

/** Downloads the archive of `id` and re-simulates the Final; `progress(i, n)` while it runs. */
export async function verifyMatch(id: string, summary: any, progress: (i: number, n: number) => void = () => {}): Promise<Verdict> {
  const r = await fetch(`/api/replay/${id}/world2.bin`);
  const cw = await CoreWorld.create('CrumbleShelf');
  const empty = { pass: false, pairs: 0, ok: 0, gaps: 0, first: -1, dropsIngested: 0, transcript: null, frames: [] as Frame[], cw };
  if (!r.ok) return { ...empty, error: 'no World recording for this match' };
  const frames = parseWorld2(new Uint8Array(await r.arrayBuffer())).filter((f) => f.part === 0 && f.hdr[11] === 4); // round 4 (the Final)
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
