// /live/:arena — the on-chain Final drawn from the WorldAcc subscription (rush_core.wasm decodes the same World the
// program steps), the live odds of every Rush Market, Bet + Chaos Drop buttons (session key, no popup), event feed,
// chain chip. During rounds 1–3: the Classic server's status + the Qualify markets.
import { api, ARENAS, ER_HTTP, accounts, subscribe, explorer, short, rush, PROGRAM } from '../lib/net';
import { decodeWorldAcc, decodeArena, decodeMatch, MARKET_NAMES, LABELS, odds, DROPS, DROP_PRICE } from '../lib/decode';
import { CoreWorld } from '../lib/core';
import { draw } from '../lib/view2d';
import { chainFor, ensureGuest, J } from '../lib/chain';


export function live(root: HTMLElement, params: string[]) {
  let stop = false;
  const unsub: (() => void)[] = [];
  root.innerHTML = `<div class="card"><h2>Live</h2><div id="pick" class="row muted">loading arenas…</div></div>`;
  (async () => {
    const s = await api('/status').catch(() => ({ servers: [] }));
    const servers = s.servers ?? [];
    const id = params[0] != null ? Number(params[0]) : servers.find((x: any) => x.keeper?.stage === 'final')?.arena ?? servers[0]?.arena ?? 2;
    root.querySelector('#pick')!.innerHTML = servers.map((x: any) => `<a class="btn small ${x.arena === id ? 'grape' : ''}" href="/live/${x.arena}" data-link>${x.id.toUpperCase()} · arena ${x.arena}</a>`).join(' ') || 'no arena online';
    const A = ARENAS[id];
    if (!A) return;
    root.insertAdjacentHTML('beforeend', `
      <div class="two">
        <div><canvas class="view" id="cv"></canvas><div id="chip" class="muted mono" style="margin-top:6px"></div></div>
        <div>
          <div class="card" style="margin-top:0"><h3 id="phase">…</h3><div id="classic" class="muted"></div><div id="me" class="muted"></div></div>
          <div class="card"><h3>Chaos Drops</h3><div class="row" id="drops"></div><div class="muted" id="dropinfo">map-wide · telegraphed 0.77 s · ≤ 3 per round</div></div>
        </div>
      </div>
      <div class="card"><h3>Rush Markets <span class="muted">(shared pool, fee 0 %; odds = pool ÷ stake on that pick)</span></h3><div id="markets"></div></div>
      <div class="card"><h3>Event feed</h3><div class="feed" id="feed"></div></div>`);
    const $ = (q: string) => root.querySelector(q) as HTMLElement;
    const feed = (t: string) => { const d = document.createElement('div'); d.innerHTML = `<span class="muted">${new Date().toLocaleTimeString()}</span> ${t}`; $('#feed').prepend(d); while ($('#feed').children.length > 80) $('#feed').lastChild!.remove(); };
    const cw = await CoreWorld.create('CrumbleShelf');
    const lobby = await CoreWorld.create('Lobby');
    let wa: ReturnType<typeof decodeWorldAcc> | null = null, humans = 0, lastPhase = '', bannerUntil = 0, banner = '';
    let state: any = null, arena: ReturnType<typeof decodeArena> | null = null, match: ReturnType<typeof decodeMatch> | null = null;
    unsub.push(subscribe(A.world, (d) => { wa = decodeWorldAcc(d); }, (st) => { $('#chip').dataset.ws = st; }));
    // guest wallet for betting (no popup); spectating works without it
    let ready = false;
    ensureGuest((m) => { $('#me').textContent = m; }, id).then((g) => { ready = !g.error; $('#me').textContent = g.error ? 'betting unavailable: ' + g.error : `you: ${short(g.burner)} · ${g.chips ? rush(g.chips) : '?'} RUSH chips`; }).catch((e) => { $('#me').textContent = 'betting unavailable: ' + e; });
    const c: any = await chainFor(id);
    // drop buttons
    $('#drops').innerHTML = [1, 2, 3, 4].map((k) => `<button class="btn small pink" data-drop="${k}">${DROPS[k]} · ${DROP_PRICE[k]}</button>`).join('');
    $('#drops').onclick = async (e) => {
      const b = (e.target as HTMLElement).closest('button[data-drop]') as HTMLButtonElement | null;
      if (!b) return;
      b.disabled = true;
      const r = await J(c.econ('drop', JSON.stringify({ kind: Number(b.dataset.drop) })));
      feed(r.ok ? `<b>you</b> bought <b>${DROPS[Number(b.dataset.drop)]}</b> ${r.session ? '(session key)' : ''} · <a target="_blank" href="${explorer('tx', r.sig, true)}">tx</a>` : `<span class="bad">drop refused</span> ${String(r.error).slice(0, 120)}`);
      setTimeout(() => (b.disabled = false), 1500);
    };
    $('#markets').onclick = async (e) => {
      const b = (e.target as HTMLElement).closest('button[data-k]') as HTMLButtonElement | null;
      if (!b) return;
      b.disabled = true;
      const amt = Number((root.querySelector('#amt') as HTMLInputElement)?.value ?? 10) || 10;
      if (Number(b.dataset.k) === 0 && sealed?.open) {
        // P6: the Champion market is sealed — the pick goes to the private book (MagicBlock TEE), the stake + a commitment
        // to the public rollup; nobody (not even us) can read the pick until the market closes
        if (!attested) { attested = true; J(c.econ('teeAttest', JSON.stringify({ base: '/chain/' }))).then((a) => { teeOk = a.ok; feed(a.ok ? `🛡 TEE attested in this browser (Intel TDX quote, ${a.ms} ms)` : `<span class="muted">TEE attestation not verified: ${String(a.error).slice(0, 80)}</span>`); }); }
        const r = await J(c.econ('sealedBet', JSON.stringify({ outcome: Number(b.dataset.o), amount: amt })));
        feed(r.ok ? `<b>you</b> sealed ${amt} RUSH on a Champion pick 🔒 (pick ${r.pick?.ok ? 'in the private book' : '<span class="bad">not in the book: you will reveal it yourself</span>'}, ${r.ms + (r.pick?.ms ?? 0)} ms) · <a target="_blank" href="${explorer('tx', r.sig, true)}">commit tx</a>` : `<span class="bad">sealed bet refused</span> ${String(r.error).slice(0, 120)}`);
        setTimeout(() => (b.disabled = false), 1200);
        return;
      }
      const r = await J(c.econ('bet', JSON.stringify({ k: Number(b.dataset.k), outcome: Number(b.dataset.o), amount: amt })));
      feed(r.ok ? `<b>you</b> bet ${amt} RUSH on slot ${Number(b.dataset.o) + 1} · ${MARKET_NAMES[Number(b.dataset.k)]} ${r.session ? '(session key)' : ''} · <a target="_blank" href="${explorer('tx', r.sig, true)}">tx</a>` : `<span class="bad">bet refused</span> ${String(r.error).slice(0, 120)}`);
      setTimeout(() => (b.disabled = false), 1200);
    };
    let prevBets = -1, prevDrops = -1, sealed: any = null, attested = false, teeOk = false, selfRevealAt = 0, wasSealedOpen = false;
    const poll = async () => {
      try {
        state = await J(c.econ('state', '{}'));
        sealed = await J(c.econ('sealedState', '{}')).catch(() => null);
        if (sealed?.escrow) {
          if (wasSealedOpen && sealed.escrow.state >= 2) feed(`🔓 Champion market closed: ${sealed.escrow.nRevealed}/${sealed.escrow.n} sealed bets revealed from the private book`);
          wasSealedOpen = sealed.escrow.state === 1;
          // commit-reveal fallback: if my pick is still unrevealed a few seconds into the reveal window, reveal it myself
          const mineOpen = (sealed.mine ?? []).filter((x: any) => x.state === 0 && x.saved);
          if (sealed.escrow.state === 2 && mineOpen.length && Date.now() / 1000 > sealed.escrow.closedAt + 6 && Date.now() > selfRevealAt + 4000) {
            selfRevealAt = Date.now();
            const rv = await J(c.econ('sealedReveal', '{}'));
            feed(`revealed your own sealed pick(s): ${JSON.stringify((rv.revealed ?? []).map((x: any) => x.ok))}`);
          }
        }
        const st = await api('/status').catch(() => null);
        const sv = st?.servers?.find((x: any) => x.arena === id);
        const ad = (await accounts(ER_HTTP, [A.arena]))[0];
        arena = ad ? decodeArena(ad) : null;
        humans = arena?.humanMask ?? 0;
        const md = arena && arena.match !== '11111111111111111111111111111111' ? (await accounts(ER_HTTP, [arena.match]))[0] : null;
        match = md ? decodeMatch(md) : null;
        const finalOn = wa && wa.round === 4 && arena?.status === 2;
        $('#phase').textContent = finalOn ? `On-chain Final · ${wa!.roundPhase < 3 ? 'get ready' : wa!.roundPhase === 3 ? 'RUSH!' : 'ended'}` : sv?.inMatch ? `Classic round ${sv.round}/3 on ${sv.id.toUpperCase()}` : sv?.up ? 'Lobby open — join from /play' : 'arena idle';
        $('#classic').innerHTML = `${sv?.up ? `${sv.humans} human(s) on ${sv.id.toUpperCase()} · server phase ${sv.phase}` : ''}${match ? ` · match <a target="_blank" href="${explorer('address', arena!.match, true)}">${short(arena!.match)}</a> ${['Created', 'Dealing', 'Dealt', 'Round active', 'Round resolved', 'Completed', 'Aborted'][match.status]}` : ''}`;
        const ph = `${finalOn}-${wa?.roundPhase}-${sv?.round}`;
        if (ph !== lastPhase) { lastPhase = ph; feed($('#phase').textContent!); }
        // markets
        const ms = (state.markets ?? []) as any[];
        const nb = ms.reduce((a, m) => a + (m.nBets ?? 0), 0);
        if (prevBets >= 0 && nb > prevBets) feed(`${nb - prevBets} new bet(s) on-chain`);
        prevBets = nb;
        const slots: number[] = match ? match.slots.map((x, i) => (x === 1 || x === 2) && i < match!.players ? i : -1).filter((i) => i >= 0) : [];
        $('#markets').innerHTML = `<div class="row muted">stake <input id="amt" type="number" min="1" value="${(root.querySelector('#amt') as HTMLInputElement)?.value ?? 10}"> RUSH ${ready ? '' : '(set up your guest wallet first)'}</div>` + ms.filter((m) => m.status >= 1).map((m) => {
          const mk = { pool: BigInt(m.pool), stake: m.stake.map((x: string) => BigInt(x)) } as any;
          const open = marketOpen(state, m.k);
          const sl = m.k === 0 && sealed?.escrow ? `<div class="muted">🔒 <b>sealed</b> — picks are hidden in MagicBlock's private rollup (TEE) and revealed at close · ${sealed.escrow.n} sealed bet(s), ${rush(sealed.escrow.pool)} RUSH staked${sealed.escrow.state >= 2 ? ` · revealed ${sealed.escrow.nRevealed}/${sealed.escrow.n}${sealed.escrow.nForfeit ? `, ${sealed.escrow.nForfeit} forfeited` : ''}` : ''}${(sealed.mine ?? []).length ? ` · yours: ${(sealed.mine as any[]).map((x) => `${rush(x.amount)} on #${(x.saved?.outcome ?? 0) + 1}${x.state === 1 ? ' ✓revealed' : x.state === 2 ? ' ✗forfeited' : ''}`).join(', ')}` : ''}${sealed.escrow.state === 1 && (sealed.mine ?? []).length ? ' · keep this tab open until the reveal' : ''}${teeOk ? ' · 🛡 TEE attested' : ''}</div>` : '';
          return `<div style="margin:8px 0">${sl}<b>${MARKET_NAMES[m.k]}${m.k === 0 ? ' 🔒' : ''}</b> <span class="pill">${m.status === 1 ? (open ? 'open' : 'closed') : m.status === 2 ? 'settled' : m.status === 3 ? 'paid' : 'idle'}</span> pool ${rush(m.pool)} · ${m.nBets} bets ${m.label ? `· ${LABELS[m.label]}` : ''}
            <div class="row" style="margin-top:4px">${slots.map((s) => { const o = odds(mk, s); return `<button class="btn small ${(m.winners >> s) & 1 && m.status >= 2 ? 'mint' : ''}" data-k="${m.k}" data-o="${s}" ${open && ready ? '' : 'disabled'}>#${s + 1}${(humans >> s) & 1 ? '★' : ''} ${o ? 'x' + o.toFixed(1) : '—'}</button>`; }).join('')}</div></div>`;
        }).join('');
        const dq = state.drops;
        if (dq && prevDrops >= 0 && dq.count > prevDrops) { const b = dq.buys[dq.count - 1]; banner = `${DROPS[b.kind]}!`; bannerUntil = Date.now() + 2500; feed(`Chaos Drop <b>${DROPS[b.kind]}</b> by ${short(b.by)}`); }
        if (dq) prevDrops = dq.count;
        const why = dropOpen(state);
        root.querySelectorAll<HTMLButtonElement>('button[data-drop]').forEach((b) => (b.disabled = !!why || !ready || (Number(b.dataset.drop) === 4 && !sweeperOnSale(state))));
        $('#dropinfo').textContent = why ? `drops: ${why}` : `drops open · ${3 - (state.drops?.count ?? 0)} left this round · map-wide, telegraphed 0.77 s`;
      } catch (e) { /* transient */ }
      if (!stop) setTimeout(poll, 1500);
    };
    poll();
    const frame = () => {
      if (stop) return;
      const cv = root.querySelector('#cv') as HTMLCanvasElement | null;
      if (cv && wa) {
        const w = wa.round === 4 ? cw : lobby;
        if (w.set(wa.world)) draw(cv, w, { humans, banner: Date.now() < bannerUntil ? banner : undefined, label: `tick ${wa.tick} · ${wa.round === 4 ? 'Final (Lid Flip)' : 'lobby'}` });
        $('#chip').innerHTML = `⛓ ER devnet-as · World <a target="_blank" href="${explorer('address', A.world, true)}">${short(A.world)}</a> · tick ${wa.tick} · core v${wa.coreVersion & 0xff} · ws ${$('#chip').dataset.ws ?? ''} · <a target="_blank" href="${explorer('address', PROGRAM)}">program</a>`;
      }
      requestAnimationFrame(frame);
    };
    frame();
  })();
  return () => { stop = true; unsub.forEach((f) => f()); };
}
// the program's windows, mirrored exactly from ChainEconomy.MarketOpen / DropOpen (C#); the program decides, the UI greys out
function marketOpen(st: any, k: number): boolean {
  const m = st.match, w = st.world, mk = st.markets?.[k];
  if (!m || !mk || mk.status !== 1 || mk.match !== m.pda) return false;
  const final = (m.flags & 0x40) !== 0;
  const live = final && w && w.round === 4 && w.roundPhase < 3;
  if (m.status >= 5) return false;
  if (k === 0) return !(m.round >= 4 || final) || !!live;
  if (k === 1) return !!live;
  const r = k - 1;
  return r === 1 ? m.round === 0 && m.status <= 2 : m.round === r - 1 && m.status === 4;
}
function dropOpen(st: any): string | null {
  const m = st.match, w = st.world, d = st.drops;
  if (!w || !m || (m.flags & 0x40) === 0 || w.round !== 4 || w.roundPhase !== 3) return "during the on-chain Final's RUSH only";
  if (d && d.count >= 3) return '3 drops this round';
  if (d && d.count > 0 && w.tick < d.lastTick + 150) return `cooldown ${((d.lastTick + 150 - w.tick) / 30).toFixed(1)} s`;
  return null;
}
function sweeperOnSale(st: any) { return st.world && st.world.tick - st.world.roundStart >= 600; }
