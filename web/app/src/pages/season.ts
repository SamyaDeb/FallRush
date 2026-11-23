// /season[/:id] — the weekly Season board (P6): top champions (crowns, then Finals), top predictors (net RUSH from the
// Rush Markets) and top drop MVPs (Chaos Drops bought in on-chain Finals), read from the on-chain `Season` account on
// Solana; the indexer's rebuild from the archived receipts is shown next to it as a cross-check. Your rank and stats
// come from your `PlayerSeason` account. At the end the program mints a Season Trophy (Metaplex Core) to each top 1.
import { api, explorer, short, rush, PROGRAM } from '../lib/net';
import { badges, seasonName } from '../lib/decode';
import { chainFor } from '../lib/chain';

const B = [['champions', 'Champions', (s: string) => `${Math.floor(Number(s) / 1000)} 👑 · ${Number(s) % 1000} Final${Number(s) % 1000 === 1 ? '' : 's'}`], ['predictors', 'Predictors', (s: string) => `${Number(s) > 0 ? '+' : ''}${rush(s)} RUSH`], ['dropMvps', 'Drop MVPs', (s: string) => `${s} drop${s === '1' ? '' : 's'}`]] as const;

export function season(root: HTMLElement, params: string[]) {
  let stop = false;
  root.innerHTML = `<div class="card"><h2 id="t">Season</h2><div id="meta" class="muted">reading the Season account on Solana…</div><div id="list" class="row" style="margin-top:6px"></div></div><div class="two" id="boards"></div><div class="card"><h3>You</h3><div id="you" class="muted">…</div></div>`;
  const $ = (q: string) => root.querySelector(q) as HTMLElement;
  let me: string | null = null;
  (async () => {
    const ss = await api('/seasons').catch(() => ({ seasons: [] }));
    const id = params[0] ? Number(params[0]) : ss.current;
    $('#list').innerHTML = (ss.seasons ?? []).map((x: any) => `<a class="btn small ${x.id === id ? 'grape' : ''}" href="/season/${x.id}" data-link>${seasonName(x.id)}</a>`).join(' ');
    me = await chainFor().then((c: any) => JSON.parse(c.wallet()).burner as string).catch(() => null);
    const tick = async () => {
      const r = await api(`/season?id=${id}${me ? `&wallet=${me}` : ''}`).catch(() => null);
      if (!r?.ok) { $('#meta').innerHTML = '<span class="bad">season unavailable</span>'; return; }
      const s = r.onChain;
      $('#t').textContent = `Season · ${seasonName(id)}`;
      if (!s) { $('#meta').innerHTML = `no Season account yet (opens with the first match of the week) · <a target="_blank" href="${explorer('address', r.account)}">account</a>`; }
      else {
        const left = s.endsAt - Date.now() / 1000;
        const cd = left > 0 ? `${Math.floor(left / 86400)}d ${Math.floor((left % 86400) / 3600)}h ${Math.floor((left % 3600) / 60)}m ${Math.floor(left % 60)}s left` : s.status ? 'closed' : 'ended (closing)';
        $('#meta').innerHTML = `${s.nMatches} match${s.nMatches === 1 ? '' : 'es'} · ${s.nPlayers} player${s.nPlayers === 1 ? '' : 's'} · ${cd}<br>the numbers come from the on-chain <a target="_blank" href="${explorer('address', r.account)}">Season account</a> (program <a target="_blank" href="${explorer('address', PROGRAM)}">${short(PROGRAM)}</a>) · indexer rebuild from ${r.rebuild.matches} archived receipt(s): ${r.matches ? '<span class="ok">matches ✓</span>' : r.rebuild.matches ? '<span class="bad">differs</span>' : '—'}${s.status ? ` · trophies minted: ${[0, 1, 2].filter((k) => (s.trophies >> k) & 1).length}/3` : ''}`;
        $('#boards').innerHTML = B.map(([k, name, fmt]) => `<div class="card" style="margin-top:0"><h3>${name}</h3><table>${(s[k] as any[]).map((e, i) => `<tr${e.wallet === me ? ' style="background:#fff3"' : ''}><td>${i + 1}</td><td class="mono">${short(e.wallet)}${e.wallet === me ? ' (you)' : ''}</td><td>${fmt(e.score)}</td></tr>`).join('') || '<tr><td class="muted">no one yet</td></tr>'}</table></div>`).join('');
      }
      const p = r.player;
      $('#you').innerHTML = !me ? 'play or bet once to get a guest wallet' : p ? `${p.matches} match(es) · ${p.crowns} 👑 · ${p.finals} Final(s) · ${p.roundsSurvived} rounds survived · markets ${Number(p.net) >= 0 ? '+' : ''}${rush(p.net)} RUSH (${p.betsWon} won / ${p.betsLost} lost) · ${p.drops} drop(s)<br>${badges(p.achievements) || '<span class="muted">no achievements yet</span>'}<br><span class="muted">your <a target="_blank" href="${explorer('address', r.playerAccount)}">PlayerSeason account</a>; rank: ${B.map(([k, name]) => { const i = (s?.[k] ?? []).findIndex((e: any) => e.wallet === me); return `${name} ${i >= 0 ? '#' + (i + 1) : '—'}`; }).join(' · ')}</span>` : `<span class="muted">no matches recorded for ${short(me)} in this season yet</span>`;
      if (!stop) setTimeout(tick, 5000);
    };
    tick();
  })();
  return () => { stop = true; };
}
