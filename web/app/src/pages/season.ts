// /season[/:id] — the weekly Season board (P6): top champions (crowns, then Finals), top predictors (net RUSH from the
// Rush Markets) and top drop MVPs (Chaos Drops bought in on-chain Finals), read from the on-chain `Season` account on
// Solana; the indexer's rebuild from the archived receipts is shown next to it as a cross-check. Your rank and stats
// come from your `PlayerSeason` account. At the end the program mints a Season Trophy (Metaplex Core) to each top 1.
// P7: a top-3 podium per board, then the table, your rank, a live countdown.
import { api, explorer, short, rush, PROGRAM } from '../lib/net';
import { badges, seasonName } from '../lib/decode';
import { chainFor } from '../lib/chain';
import { icon, type IconName } from '../lib/icons';

// followups: /api/season says `paused` (the keepers stopped recording at the MagicBlock commit cap, or SEASONS=0)
const esc = (x: unknown) => String(x ?? '').replace(/[&<>"']/g, (c) => `&#${c.charCodeAt(0)};`);
export const pausedChip = (r: any) => (r?.paused ? `<span class="chip pink" title="${esc(r.pausedReason ?? 'paused')}">paused</span>` : '');
export const pausedNote = (r: any) => (r?.paused ? `<div class="muted" style="margin-top:6px">season recording paused${r.pausedReason ? ` (${esc(r.pausedReason)})` : ''}: matches still play and settle on-chain; the board resumes when recording does</div>` : '');

const B: [string, string, IconName, string, (s: string) => string][] = [
  ['champions', 'Champions', 'crown', 'hue-lemon', (s) => `${Math.floor(Number(s) / 1000)} crown${Math.floor(Number(s) / 1000) === 1 ? '' : 's'} · ${Number(s) % 1000} Final${Number(s) % 1000 === 1 ? '' : 's'}`],
  ['predictors', 'Predictors', 'eye', 'hue-soda', (s) => `${Number(s) > 0 ? '+' : ''}${rush(s)} RUSH`],
  ['dropMvps', 'Drop MVPs', 'bolt', 'hue-bubble', (s) => `${s} drop${s === '1' ? '' : 's'}`],
];

export function season(root: HTMLElement, params: string[]) {
  let stop = false, endsAt = 0;
  root.innerHTML = `<div class="card hue-grape enter"><div class="ribbon">${icon('trophy')} <span id="t">SEASON</span> <span id="paused"></span> <span class="sub" id="cd"></span></div><div id="meta" class="muted">reading the Season account on Solana…</div><div id="list" class="row" style="margin-top:8px"></div></div>
    <div class="grid" id="boards" style="grid-template-columns:repeat(auto-fit,minmax(290px,1fr))"></div>
    <div class="card hue-apple"><div class="ribbon">${icon('capsule')} YOU</div><div id="you" class="muted">…</div></div>`;
  const $ = (q: string) => root.querySelector(q) as HTMLElement;
  let me: string | null = null;
  const countdown = () => {
    if (stop) return;
    const left = endsAt - Date.now() / 1000;
    if (endsAt) $('#cd').textContent = left > 0 ? `ends in ${Math.floor(left / 86400)}d ${Math.floor((left % 86400) / 3600)}h ${Math.floor((left % 3600) / 60)}m ${Math.floor(left % 60)}s` : 'ended';
    setTimeout(countdown, 1000);
  };
  countdown();
  (async () => {
    const ss = await api('/seasons').catch(() => ({ seasons: [] }));
    const id = params[0] ? Number(params[0]) : ss.current;
    $('#list').innerHTML = (ss.seasons ?? []).map((x: any) => `<a class="btn small ${x.id === id ? 'grape' : 'ghost'}" href="/season/${x.id}" data-link>${seasonName(x.id)}</a>`).join(' ');
    me = await chainFor().then((c: any) => JSON.parse(c.wallet()).burner as string).catch(() => null);
    let built = false;
    const tick = async () => {
      const r = await api(`/season?id=${id}${me ? `&wallet=${me}` : ''}`).catch(() => null);
      if (stop) return;
      // beta audit: keep polling after a failed fetch (one error used to stop the board for good)
      if (!r?.ok) { $('#meta').innerHTML = '<span class="bad">season unavailable: retrying…</span>'; if (!stop) setTimeout(tick, 10_000); return; }
      const s = r.onChain;
      $('#t').textContent = `SEASON · ${seasonName(id).toUpperCase()}`;
      $('#paused').innerHTML = pausedChip(r);
      if (!s) { $('#meta').innerHTML = `no Season account yet (it opens with the first match of the week) · <a target="_blank" href="${explorer('address', r.account)}">account</a>${pausedNote(r)}`; }
      else {
        endsAt = s.endsAt;
        $('#meta').innerHTML = `${s.nMatches} match${s.nMatches === 1 ? '' : 'es'} · ${s.nPlayers} player${s.nPlayers === 1 ? '' : 's'} · from the on-chain <a target="_blank" href="${explorer('address', r.account)}">Season account</a> (program <a target="_blank" href="${explorer('address', PROGRAM)}">${short(PROGRAM)}</a>) · indexer rebuild from ${r.rebuild.matches} archived receipt(s): ${r.matches ? '<span class="ok">matches ✓</span>' : r.rebuild.matches ? '<span class="bad">differs</span>' : '—'}${s.status ? ` · trophies minted: ${[0, 1, 2].filter((k) => (s.trophies >> k) & 1).length}/3` : ' · a Season Trophy (Metaplex Core) to each top 1 at the end'}${pausedNote(r)}`;
        $('#boards').innerHTML = B.map(([k, name, ic, hue, fmt], bi) => {
          const rows = s[k] as any[];
          const pl = (i: number, cls: string) => { const e = rows[i]; return `<div class="pl ${cls} ${e ? '' : 'empty'}"><div class="who mono">${e ? short(e.wallet) + (e.wallet === me ? ' (you)' : '') : '—'}</div><div class="sc">${e ? fmt(e.score) : ''}</div><div class="block">${i + 1}</div></div>`; };
          return `<div class="card ${hue} ${built ? '' : 'enter'}" style="margin:0;--i:${bi}"><div class="ribbon">${icon(ic)} ${name.toUpperCase()}</div>
            <div class="podium">${pl(1, 'p2')}${pl(0, 'p1')}${pl(2, 'p3')}</div>
            <table>${rows.slice(3).map((e, i) => `<tr class="${e.wallet === me ? 'me' : ''}"><td>${i + 4}</td><td class="mono">${short(e.wallet)}${e.wallet === me ? ' (you)' : ''}</td><td>${fmt(e.score)}</td></tr>`).join('') || (rows.length ? '' : '<tr><td class="muted">no one yet</td></tr>')}</table></div>`;
        }).join('');
        built = true;
      }
      const p = r.player;
      $('#you').innerHTML = !me ? 'play or bet once to get a guest wallet' : p ? `<div class="stats">
          <div class="stat hue-lemon"><div class="v">${p.crowns}</div><div class="k">crowns</div></div><div class="stat hue-soda"><div class="v">${p.matches}</div><div class="k">matches</div></div>
          <div class="stat hue-apple"><div class="v">${p.roundsSurvived}</div><div class="k">rounds survived</div></div><div class="stat hue-bubble"><div class="v">${Number(p.net) >= 0 ? '+' : ''}${rush(p.net)}</div><div class="k">RUSH from markets (${p.betsWon} won / ${p.betsLost} lost)</div></div></div>
          <div style="margin-top:10px">${badges(p.achievements) || '<span class="muted">no achievements yet</span>'}</div>
          <div class="muted" style="margin-top:8px">your <a target="_blank" href="${explorer('address', r.playerAccount)}">PlayerSeason account</a> · rank: ${B.map(([k, name]) => { const i = (s?.[k] ?? []).findIndex((e: any) => e.wallet === me); return `${name} ${i >= 0 ? '#' + (i + 1) : '—'}`; }).join(' · ')}</div>` : `<span class="muted">no matches recorded for ${short(me)} in this season yet</span>`;
      if (!stop) setTimeout(tick, 5000);
    };
    tick();
  })();
  return () => { stop = true; };
}
