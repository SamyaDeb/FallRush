// /arenas — the web app home (the landing at / belongs to web/site): live arenas, this week's season, recent matches,
// and what runs on MagicBlock. P7: soft-look cards, status updates in place (no layout jumps).
import { api, explorer, PROGRAM, RUSH_MINT, VALIDATOR, short, rush } from '../lib/net';
import { seasonName } from '../lib/decode';
import { icon } from '../lib/icons';

export function home(root: HTMLElement) {
  root.innerHTML = `
  <div class="hero-title word">FALL <span class="yellow">RUSH</span></div>
  <p class="lede"><b>12 Rushlings. 4 rounds. 1 crown.</b> A 3D knockout party game whose 4 rounds all run inside a MagicBlock Ephemeral Rollup: the program steps the physics, settles the bets and mints the Crown. No wallet, no popups.</p>
  <div class="row"><a class="btn" href="/play" data-link>${icon('play')} Play now</a><a class="btn mint" href="/live" data-link>${icon('eye')} Watch live</a></div>
  <div class="card hue-soda"><div class="ribbon">${icon('tiles')} LIVE ARENAS</div><div id="arenas" class="grid" style="min-height:150px"><span class="muted">loading…</span></div></div>
  <div class="card hue-lemon"><div class="ribbon">${icon('trophy')} THIS WEEK'S SEASON <span class="spacer"></span><a class="btn small ghost" href="/season" data-link>full board</a></div><div id="season" class="muted" style="min-height:90px">loading…</div></div>
  <div class="card hue-tang"><div class="ribbon">${icon('check')} RECENT MATCHES <span class="sub">every one has a receipt</span></div><div id="matches" class="scroll" style="min-height:120px"><span class="muted">loading…</span></div></div>
  <div class="card hue-grape"><div class="ribbon">${icon('bolt')} HOW IT RUNS ON MAGICBLOCK</div><div class="grid">
    <div class="tile hue-soda"><h3>Ephemeral Rollup</h3><span class="muted">The Final (Lid Flip) is simulated by the <code>fall_rush</code> program at 30 Hz on <code>devnet-as</code>; a timed task calls <code>tick()</code>.</span><div class="row" style="margin-top:6px"><a href="${explorer('address', PROGRAM)}" target="_blank">program</a><a href="${explorer('address', VALIDATOR)}" target="_blank">validator</a></div></div>
    <div class="tile hue-lemon"><h3>VRF</h3><span class="muted">MagicBlock VRF seeds the on-chain Final and rolls the capsule rarity.</span></div>
    <div class="tile hue-apple"><h3>Session keys</h3><span class="muted">A guest wallet signs once; a session key signs your Final moves, bets and Chaos Drops. No popups.</span></div>
    <div class="tile hue-bubble"><h3>Private ER + Magic Actions</h3><span class="muted">Sealed Champion bets live in a TEE-backed Private Ephemeral Rollup; the Crown and capsules are minted by a Magic Action as the Match returns to Solana.</span><div class="row" style="margin-top:6px"><a href="${explorer('address', RUSH_MINT)}" target="_blank">RUSH mint</a></div></div>
  </div><p class="muted" style="margin-bottom:0">All 4 rounds are program-computed ◆ (if the rollup is unavailable, the Classic game server plays the match and records it on-chain ●). Devnet test chips only (no real-money value).</p></div>`;
  let stop = false, first = true;
  const tick = async () => {
    try {
      const s = await api('/status');
      if (stop) return;
      const el = root.querySelector('#arenas')!;
      el.innerHTML = (s.servers ?? []).map((x: any, i: number) => `<div class="tile ${i % 2 ? 'hue-grape' : 'hue-soda'} ${first ? 'enter' : ''}" style="--i:${i}">
        <h3>${x.id.toUpperCase()} <span class="chip ${x.up ? (x.inMatch ? 'pink' : 'mint') : ''}">${x.up ? (x.inMatch ? 'in a match' : 'lobby open') : 'offline'}</span></h3>
        <div>${x.up ? `${x.humans} human${x.humans === 1 ? '' : 's'} · ${x.inMatch ? 'round ' + x.round + '/4' : 'bots fill the empty slots'}` : '—'}</div>
        <div class="muted">arena ${x.arena ?? '?'} · keeper ${x.keeper?.stage ?? '?'}${x.keeper?.stage === 'final' && x.keeper?.worldTick != null ? ' · Final tick ' + x.keeper.worldTick : ''}</div>
        <div class="row" style="margin-top:8px"><a class="btn small" href="/play" data-link>Play</a><a class="btn small mint" href="/live/${x.arena}" data-link>Watch</a></div></div>`).join('') || '<span class="muted">no arena online</span>';
      const ss = await api('/season').catch(() => null);
      const sv = ss?.onChain;
      root.querySelector('#season')!.innerHTML = sv ? `<div class="grid">${([['champions', 'crown', 'Champions', (x: string) => Math.floor(Number(x) / 1000) + ' crown(s)'], ['predictors', 'eye', 'Predictors', (x: string) => (Number(x) > 0 ? '+' : '') + rush(x) + ' RUSH'], ['dropMvps', 'bolt', 'Drop MVPs', (x: string) => x + ' drop(s)']] as const).map(([k, ic, t, f]) => `<div><b class="row">${icon(ic as any).replace('<svg', '<svg style="width:20px;height:20px"')} ${t}</b>${(sv[k] as any[]).slice(0, 3).map((e, i) => `<div>${i + 1}. <span class="mono">${short(e.wallet)}</span> ${f(e.score)}</div>`).join('') || '<span class="muted">no one yet</span>'}</div>`).join('')}</div><div class="muted" style="margin-top:8px">${seasonName(sv.id)} · from the on-chain <a target="_blank" href="${explorer('address', ss.account)}">Season account</a> · ends ${new Date(sv.endsAt * 1000).toUTCString().slice(0, 22)} UTC · a Season Trophy (Metaplex Core) to each top 1</div>` : '<span class="muted">the season opens with the first match of the week</span>';
      const m = await api('/matches?n=8');
      root.querySelector('#matches')!.innerHTML = (m.matches ?? []).length ? `<table><tr><th>when</th><th>match</th><th>champion</th><th>pools (RUSH)</th><th></th></tr>${m.matches.map((x: any) => `<tr>
        <td>${new Date(x.doneAt).toLocaleTimeString()}</td><td class="mono">${short(x.match)}</td><td>${x.champion != null && x.champion < 12 ? '#' + (x.champion + 1) + ((x.humanMask >> x.champion) & 1 ? ' <span class="chip sun">' + icon('crown') + ' human</span>' : ' (bot)') : '—'}</td>
        <td>${x.pools.map((p: string) => rush(p)).join(' / ')}${x.sealed?.n ? ` <span class="chip sealed">${icon('lock')} ${x.sealed.n}</span>` : ''}${x.aborted ? ' <span class="chip pink">aborted</span>' : ''}${x.allOnChain ? ' <span class="chip grape" title="every round computed by the program">◆ all 4 rounds on-chain</span>' : ''}</td><td><a class="btn small" href="/receipt/${x.match}" data-link>receipt</a>${x.replay ? ` <a class="btn small ghost" href="/replay/${x.match}" data-link>replay</a>` : ''}</td></tr>`).join('')}</table>` : '<span class="muted">no matches archived yet</span>';
      first = false;
    } catch (e) { root.querySelector('#arenas')!.innerHTML = `<span class="bad">status unavailable</span>`; }
    if (!stop) setTimeout(tick, 3000);
  };
  tick();
  return () => { stop = true; };
}
