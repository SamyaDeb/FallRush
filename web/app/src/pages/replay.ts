// /replay/:match — plays the archived on-chain Final through rush_core.wasm and checks it byte for byte against the
// recorded rollup Worlds (lib/verify.ts): "replay = on-chain ✓" when every consecutive pair matches and the replayed
// final World holds the receipt's Final transcript. P7: card layout, fixed-size controls (no layout jumps).
// fullchain: a full on-chain match (receipt trust bit 4) replays all 4 rounds with the chain-exact step; the view
// follows the World's round map.
import { api, short, explorer } from '../lib/net';
import { draw } from '../lib/board';
import { verifyMatch } from '../lib/verify';
import { icon } from '../lib/icons';
import { esc } from '../lib/chain';
import { ROUND_MAP } from '../lib/core';
import { TICKS_LINE } from '../lib/decode';

export function replay(root: HTMLElement, params: string[]) {
  const id = params[0];
  let stop = false;
  root.innerHTML = `<div class="card hue-soda enter"><div class="ribbon">${icon('play')} REPLAY <span class="sub mono">${short(id)}</span></div>
    <div class="verify" style="margin-bottom:8px"><span class="step" id="vs"><span class="dot">${icon('check')}</span>Replay = on-chain</span></div>
    <div id="st" class="muted" style="min-height:42px">loading the archive…</div>
    <div class="two" style="margin-top:8px"><canvas class="view" id="cv"></canvas>
      <div><div class="row" style="margin:4px 0 10px"><button class="btn small" id="pp" style="width:54px">${icon('play')}</button><input type="range" id="sl" style="flex:1;min-width:140px" value="0"><span id="tk" class="mono" style="min-width:86px;text-align:right">tick —</span></div>
      <p class="muted">The page downloads the match's recorded rollup Worlds and the inputs the program latched each tick, steps every tick again with <code>rush_core.wasm</code> (the same code the program runs as sBPF) and compares the bytes.</p>
      <div class="row"><a class="btn small" href="/receipt/${id}" data-link>${icon('check')} Receipt</a><a class="btn small ghost" target="_blank" href="${explorer('address', id)}">${icon('link')} Match on Solana</a></div>
      <p class="muted" style="margin-top:12px">Do it yourself offline: <code>node tools/p7/replay-check.mjs ${id}</code> (see the README).</p></div></div></div>`;
  const $ = (q: string) => root.querySelector(q) as HTMLElement;
  (async () => {
    // beta audit: any failure says so (it stayed on "loading the archive…"); an aborted match has nothing to replay
    const m = await api(`/match/${id}`).catch((e: any) => ({ ok: false, error: 'archive unavailable: ' + (e?.message ?? e) }));
    if (!m.ok) { $('#st').innerHTML = `<span class="bad">${esc(m.error)}</span>`; return; }
    if (m.summary?.receipt?.status === 6) { $('#st').innerHTML = 'This match was <b>aborted</b> (every bet refunded): there is no on-chain Final to replay.'; return; }
    const full = ((m.summary?.receipt?.trust ?? 0) & 4) !== 0;
    if (!m.summary?.final && !full) { $('#st').innerHTML = 'This match has no on-chain Final recording (rounds 1–3 run on the Classic server).'; return; }
    const v = await verifyMatch(id, m.summary, (i, n) => { if (!stop) $('#st').textContent = `checking ${i}/${n} ${full ? 'Worlds (4 rounds)' : 'ticks'}…`; });
    if (stop) return;
    if (v.error) { $('#st').innerHTML = `<span class="bad">${esc(v.error)}</span>`; return; }
    const frames = v.frames, last = frames[frames.length - 1];
    $('#vs').classList.add(v.pass ? 'done' : 'fail');
    const rounds = v.full && v.perRound ? ' · ' + Object.entries(v.perRound).filter(([r]) => r !== '0').map(([r, x]) => `R${r} ${x.ok}/${x.pairs}`).join(' · ') + ` (◆ every round computed by the program; ${TICKS_LINE})` : '';
    $('#st').innerHTML = `${v.pass ? '<span class="ok">replay = on-chain ✓</span>' : '<span class="bad">replay ≠ on-chain ✗</span>'}${rounds} · ${v.ok}/${v.pairs} consecutive ${v.full ? 'Worlds (mid-tick parts included)' : 'ticks'} re-simulated byte-exact with rush_core.wasm${v.gaps ? ` (${v.gaps} gaps in the recording skipped)` : ''}${v.mapLoad ? ' · starts after the map-load tick (0 → 1)' : ''}${v.dropsIngested ? ` · ${v.dropsIngested} Chaos Drop(s) ingested from the archived DropQueue` : ''}${v.first >= 0 ? ` · first mismatch at tick ${v.first}` : ''} · receipt ${v.full ? 'match' : 'Final'} transcript ${v.transcript == null ? 'n/a' : v.transcript ? 'found in the final World ✓' : 'NOT found ✗'} · ${frames.length} snapshots, ticks ${frames[0].tick}–${last.tick}`;
    (window as any).__replay = { pass: v.pass, pairs: v.pairs, ok: v.ok, gaps: v.gaps, transcript: v.transcript };
    const sl = $('#sl') as HTMLInputElement; sl.max = String(frames.length - 1); sl.value = '0';
    let i = 0, playing = true;
    const ppIcon = () => { $('#pp').innerHTML = playing ? '❚❚' : icon('play'); };
    ppIcon();
    $('#pp').onclick = () => { playing = !playing; ppIcon(); };
    sl.oninput = () => { i = Number(sl.value); };
    const hm = m.summary?.receipt?.humanMask ?? 0;
    let lastT = 0;
    const loop = (t: number) => {
      if (stop) return;
      if (playing && t - lastT > 33) { lastT = t; i = Math.min(frames.length - 1, i + 1); sl.value = String(i); }
      const f = frames[i];
      if (v.cw.set(f.world)) {
        const r = v.full ? v.cw.round() : null;
        draw($('#cv') as HTMLCanvasElement, v.cw, { humans: hm, label: r ? `tick ${f.tick} · ${r.roundIndex >= 1 && r.round >= 1 ? `R${r.round} ${ROUND_MAP[r.mapKey] ?? ''}` : 'lobby'}` : `tick ${f.tick}` });
      }
      $('#tk').textContent = `tick ${f.tick}`;
      requestAnimationFrame(loop);
    };
    requestAnimationFrame(loop);
  })().catch((e: any) => { if (!stop) $("#st").innerHTML = `<span class="bad">replay failed: ${esc(e?.message ?? e)}</span>`; });
  return () => { stop = true; };
}
