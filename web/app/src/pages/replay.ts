// /replay/:match — plays the archived on-chain Final through rush_core.wasm. Every recorded World snapshot carries the
// inputs the program latched for that tick (WorldAcc.cur); the replay steps snapshot T with the inputs of T + 1 and
// compares the bytes with the recorded T + 1. "replay = on-chain ✓" when every consecutive pair matches byte for byte
// and the replayed final World holds the receipt's Final transcript (D-REPLAY: the hash check decides; the drawing
// between snapshots is not interpolated).
import { api, short, explorer } from '../lib/net';
import { CoreWorld } from '../lib/core';
import { draw } from '../lib/view2d';

interface Frame { tick: number; part: number; hdr: Uint8Array; world: Uint8Array }
function parse(buf: Uint8Array): Frame[] {
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
const inputsOf = (hdr: Uint8Array) => {
  // hdr = WorldAcc[128..208]: cur[12] × 4 B at +16, cur_some u16 at +64
  const some = hdr[64] | (hdr[65] << 8), b = new Uint8Array(72);
  for (let s = 0; s < 12; s++) if ((some >> s) & 1) { const c = hdr.subarray(16 + 4 * s, 20 + 4 * s); b.set([1, c[0], c[1], c[2], 0, c[3]], 6 * s); }
  return b;
};
const eq = (a: Uint8Array, b: Uint8Array) => a.length === b.length && a.every((x, i) => x === b[i]);
const has = (hay: Uint8Array, hex: string) => { const n = Uint8Array.from(hex.match(/../g)!.map((h) => parseInt(h, 16))); outer: for (let i = 0; i + n.length <= hay.length; i++) { for (let j = 0; j < n.length; j++) if (hay[i + j] !== n[j]) continue outer; return true; } return false; };

export function replay(root: HTMLElement, params: string[]) {
  const id = params[0];
  let stop = false;
  root.innerHTML = `<div class="card"><h2>Replay <span class="mono">${short(id)}</span></h2><div id="st" class="muted">loading the archive…</div>
    <div class="row" style="margin:8px 0"><button class="btn small" id="pp">⏸</button><input type="range" id="sl" style="flex:1"><span id="tk" class="mono"></span></div>
    <canvas class="view" id="cv"></canvas><p><a href="/receipt/${id}" data-link>receipt</a> · <a target="_blank" href="${explorer('address', id)}">Match on Solana</a></p></div>`;
  const $ = (q: string) => root.querySelector(q) as HTMLElement;
  (async () => {
    const m = await api(`/match/${id}`);
    if (!m.ok) { $('#st').innerHTML = `<span class="bad">${m.error}</span>`; return; }
    const r = await fetch(`/api/replay/${id}/world2.bin`);
    if (!r.ok) { $('#st').innerHTML = `<span class="bad">no World recording for this match</span>`; return; }
    const frames = parse(new Uint8Array(await r.arrayBuffer())).filter((f) => f.part === 0 && f.hdr[11] === 4); // round 4 (the Final)
    if (frames.length < 2) { $('#st').textContent = 'the recording has no Final'; return; }
    // verify: snapshot T + the inputs latched for T+1 → must equal snapshot T+1 (byte for byte)
    const cw = await CoreWorld.create('CrumbleShelf');
    let pairs = 0, ok = 0, gaps = 0, first = -1, dropsIngested = 0;
    const buys: { kind: number }[] = m.summary?.drops?.buys ?? [];
    for (let i = 1; i < frames.length; i++) {
      if (frames[i].tick !== frames[i - 1].tick + 1) { gaps++; continue; }
      pairs++;
      // Chaos Drops: the program copies new DropQueue purchases into the World before stepping (sim.rs
      // ingest_drops); the archived purchase kinds (the keeper's drops log) are applied at the tick the
      // recording shows them entering the World
      cw.set(frames[i].world); const q1 = cw.queuedDrops();
      cw.set(frames[i - 1].world); const q0 = cw.queuedDrops();
      for (let j = q0; j < q1; j++) { const k = buys[j]?.kind; if (k) { cw.ingestDrop(k); dropsIngested++; } }
      cw.step(inputsOf(frames[i].hdr));
      if (eq(cw.bytes(), frames[i].world)) ok++; else if (first < 0) first = frames[i].tick;
      if (i % 400 === 0) { $('#st').textContent = `checking ${i}/${frames.length}…`; await new Promise((x) => setTimeout(x, 0)); }
    }
    const ft = m.summary?.receipt?.finalTranscript;
    const last = frames[frames.length - 1];
    const tOk = ft && /^[0-9a-f]{64}$/.test(ft) && ft !== '0'.repeat(64) ? has(last.world, ft) : null;
    const pass = pairs > 0 && ok === pairs && tOk !== false;
    $('#st').innerHTML = `${pass ? '<span class="ok">replay = on-chain ✓</span>' : '<span class="bad">replay ≠ on-chain ✗</span>'} · ${ok}/${pairs} consecutive ticks re-simulated byte-exact with rush_core.wasm${gaps ? ` (${gaps} gaps in the recording skipped)` : ''}${dropsIngested ? ` · ${dropsIngested} Chaos Drop(s) ingested from the archived DropQueue` : ''}${first >= 0 ? ` · first mismatch at tick ${first}` : ''} · receipt Final transcript ${tOk == null ? 'n/a' : tOk ? 'found in the final World ✓' : 'NOT found ✗'} · ${frames.length} snapshots, ticks ${frames[0].tick}–${last.tick}`;
    (window as any).__replay = { pass, pairs, ok, gaps, transcript: tOk };
    const sl = $('#sl') as HTMLInputElement; sl.max = String(frames.length - 1); sl.value = '0';
    let i = 0, playing = true;
    $('#pp').onclick = () => { playing = !playing; $('#pp').textContent = playing ? '⏸' : '▶'; };
    sl.oninput = () => { i = Number(sl.value); };
    const hm = m.summary?.receipt?.humanMask ?? 0;
    let lastT = 0;
    const loop = (t: number) => {
      if (stop) return;
      if (playing && t - lastT > 33) { lastT = t; i = Math.min(frames.length - 1, i + 1); sl.value = String(i); }
      const f = frames[i];
      if (cw.set(f.world)) draw($('#cv') as HTMLCanvasElement, cw, { humans: hm, label: `tick ${f.tick}` });
      $('#tk').textContent = `tick ${f.tick}`;
      requestAnimationFrame(loop);
    };
    requestAnimationFrame(loop);
  })();
  return () => { stop = true; };
}
