import { api, explorer, PROGRAM, RUSH_MINT, VALIDATOR, short, rush } from '../lib/net';
export function home(root: HTMLElement) {
  root.innerHTML = `
  <h1>FALL RUSH</h1>
  <p style="color:#fff;font-size:19px;max-width:640px"><b>12 Rushlings. 4 rounds. 1 crown.</b> A 3D knockout party game whose Final runs inside a
  MagicBlock Ephemeral Rollup: the program steps the physics, settles the bets and mints the crown. No wallet, no popups.</p>
  <p><a class="btn" href="/play" data-link>▶ Play now</a> &nbsp; <a class="btn small mint" href="/live" data-link>Watch live</a></p>
  <div class="card"><h2>Live arenas</h2><div id="arenas" class="grid"><span class="muted">loading…</span></div></div>
  <div class="card"><h2>Recent matches</h2><div id="matches"><span class="muted">loading…</span></div></div>
  <div class="card"><h2>How it works on MagicBlock</h2><div class="strip">
    <div><b>Ephemeral Rollup</b><br><span class="muted">The Final (Lid Flip) is simulated by the <code>fall_rush</code> program at 30 Hz on <code>devnet-as</code>; a timed task calls <code>tick()</code>.</span><br><a href="${explorer('address', PROGRAM)}" target="_blank">program</a> · <a href="${explorer('address', VALIDATOR)}" target="_blank">validator</a></div>
    <div><b>VRF</b><br><span class="muted">The deal (maps + hazard seeds) and the capsule rarity come from MagicBlock VRF callbacks.</span></div>
    <div><b>Session keys</b><br><span class="muted">A guest wallet signs once; a session key signs every move, bet and Chaos Drop. No popups.</span></div>
    <div><b>Metaplex Core</b><br><span class="muted">The receipt is committed to Solana and the program mints the Crown Capsule + capsule drops.</span><br><a href="${explorer('address', RUSH_MINT)}" target="_blank">RUSH mint</a></div>
  </div><p class="muted">Rounds 1–3 run on the Classic game server (recorded on-chain ●); the Final is program-computed ◆. Devnet test chips only (no real-money value).</p></div>`;
  let stop = false;
  const tick = async () => {
    try {
      const s = await api('/status');
      const el = root.querySelector('#arenas')!;
      el.innerHTML = (s.servers ?? []).map((x: any) => `<div class="card" style="margin:0">
        <h3>${x.id.toUpperCase()} <span class="pill">${x.up ? x.phase : 'offline'}</span></h3>
        <div>${x.up ? `${x.humans} human${x.humans === 1 ? '' : 's'} · ${x.inMatch ? 'round ' + x.round + '/4' : 'lobby open'}` : '—'}</div>
        <div class="muted">arena ${x.arena ?? '?'} · keeper ${x.keeper?.stage ?? '?'}${x.keeper?.worldPhase != null && x.keeper?.stage === 'final' ? ' · Final tick ' + x.keeper.worldTick : ''}</div>
        <div class="row" style="margin-top:6px"><a class="btn small" href="/play" data-link>Play</a><a class="btn small mint" href="/live/${x.arena}" data-link>Watch</a></div></div>`).join('') || '<span class="muted">no arena online</span>';
      const m = await api('/matches?n=8');
      root.querySelector('#matches')!.innerHTML = (m.matches ?? []).length ? `<table><tr><th>when</th><th>match</th><th>champion</th><th>pools</th><th></th></tr>${m.matches.map((x: any) => `<tr>
        <td>${new Date(x.doneAt).toLocaleTimeString()}</td><td class="mono">${short(x.match)}</td><td>${x.champion != null && x.champion < 12 ? 'slot ' + (x.champion + 1) + ((x.humanMask >> x.champion) & 1 ? ' 👑' : ' (bot)') : '—'}</td>
        <td>${x.pools.map((p: string) => rush(p)).join(' / ')}</td><td><a href="/receipt/${x.match}" data-link>receipt</a>${x.replay ? ` · <a href="/replay/${x.match}" data-link>replay</a>` : ''}</td></tr>`).join('')}</table>` : '<span class="muted">no matches archived yet</span>';
    } catch (e) { root.querySelector('#arenas')!.innerHTML = `<span class="bad">status unavailable</span>`; }
    if (!stop) setTimeout(tick, 3000);
  };
  tick();
  return () => { stop = true; };
}
