// /live/:arena — the on-chain Final drawn from the WorldAcc subscription (rush_core.wasm decodes the same World the
// program steps), the live odds of every Rush Market, Bet + Chaos Drop buttons (session key, no popup), event feed,
// chain chip. During rounds 1–3: the Classic server's status + the Qualify markets.
import { api, ARENAS, ER_HTTP, accounts, subscribe, explorer, short, rush, PROGRAM } from '../lib/net';
import { decodeWorldAcc, decodeArena, decodeMatch, MARKET_NAMES, LABELS, odds, DROPS, DROP_PRICE } from '../lib/decode';
import { CoreWorld } from '../lib/core';
import { draw } from '../lib/view2d';
import { chainFor, ensureGuest, J, esc } from '../lib/chain';
import { icon, type IconName } from '../lib/icons';
import { countUp, restart } from '../lib/motion';

const DROP_ICON: IconName[] = ['bolt', 'tiles', 'ball', 'wind', 'ring'];
const DROP_HUE = ['', 'hue-lemon', 'hue-bubble', 'hue-soda', 'hue-tang'];
const STAKES = [10, 25, 50, 100, 250];


export function live(root: HTMLElement, params: string[]) {
  let stop = false;
  const unsub: (() => void)[] = [];
  root.innerHTML = `<div class="card hue-grape enter"><div class="ribbon">${icon('eye')} LIVE <span class="sub">watch, bet and drop chaos from your phone</span></div><div id="pick" class="row muted">loading arenas…</div></div>`;
  (async () => {
    const s = await api('/status').catch(() => ({ servers: [] }));
    // beta audit: only arenas (the Classic room server gs3 has no on-chain arena: it showed as a dead "arena 0" pill)
    const servers = (s.servers ?? []).filter((x: any) => x.arena != null && !x.classic);
    const id = params[0] != null ? Number(params[0]) : servers.find((x: any) => x.keeper?.stage === 'final')?.arena ?? servers[0]?.arena ?? 2;
    root.querySelector('#pick')!.innerHTML = servers.map((x: any) => `<a class="btn small ${x.arena === id ? 'grape' : 'ghost'}" href="/live/${Number(x.arena)}" data-link>${esc(String(x.id).toUpperCase())} · arena ${Number(x.arena)}</a>`).join(' ') || 'no arena online';
    const A = ARENAS[id];
    if (!A || stop) return;
    root.insertAdjacentHTML('beforeend', `
      <div class="two">
        <div><div class="card hue-soda" style="margin-top:0"><div class="ribbon">${icon('tiles')} <span id="phase">…</span></div><canvas class="view" id="cv"></canvas><div id="chip" class="muted mono" style="margin-top:8px;min-height:36px"></div></div></div>
        <div>
          <div class="card" style="margin-top:0"><div id="classic" class="muted"></div><div id="me" class="muted" style="margin-top:4px"></div><div id="yourbet"></div></div>
          <div class="card hue-bubble"><div class="ribbon">${icon('bolt')} CHAOS DROPS <span class="sub">≤ 3 per Final · telegraphed 0.77 s</span></div><div class="drops" id="drops"></div><div class="muted" id="dropinfo" style="margin-top:8px">map-wide · telegraphed 0.77 s · ≤ 3 per round</div></div>
        </div>
      </div>
      <div class="card hue-lemon"><div class="ribbon">${icon('chip')} RUSH MARKETS <span class="sub">shared pool, 0 % fee · odds = pool ÷ the stake on that pick</span></div>
        <div class="row" style="margin-bottom:6px"><span class="muted">stake</span><div class="stepper"><button id="sm" aria-label="less">–</button><output id="amtv">10 RUSH</output><button id="sp" aria-label="more">+</button></div><input id="amt" type="hidden" value="10"><span class="muted" id="ready"></span></div>
        <div id="markets"></div></div>
      <div class="card"><div class="ribbon" style="--rim:#6b628a">Event feed</div><div class="feed" id="feed"></div></div>`);
    const $ = (q: string) => root.querySelector(q) as HTMLElement;
    let stakeIx = 0;
    const setStake = (d: number) => { stakeIx = Math.max(0, Math.min(STAKES.length - 1, stakeIx + d)); ($('#amt') as HTMLInputElement).value = String(STAKES[stakeIx]); $('#amtv').textContent = `${STAKES[stakeIx]} RUSH`; restart($('#amtv'), 'bump'); };
    $('#sm').onclick = () => setStake(-1); $('#sp').onclick = () => setStake(1);
    const yourBet = (html: string) => { $('#yourbet').innerHTML = `<div class="yourbet">${icon('check')}<div>${html}</div></div>`; };
    let toastT = 0;
    const toast = (html: string, ic: IconName) => { document.querySelector('.toast')?.remove(); const d = document.createElement('div'); d.className = 'toast'; d.innerHTML = `${icon(ic)}<span>${html}</span>`; document.body.appendChild(d); clearTimeout(toastT); toastT = window.setTimeout(() => d.remove(), 3200); };
    const feed = (t: string) => { const d = document.createElement('div'); d.innerHTML = `<span class="muted">${new Date().toLocaleTimeString()}</span> ${t}`; $('#feed').prepend(d); while ($('#feed').children.length > 80) $('#feed').lastChild!.remove(); };
    const cw = await CoreWorld.create('CrumbleShelf');
    const lobby = await CoreWorld.create('Lobby');
    let wa: ReturnType<typeof decodeWorldAcc> | null = null, humans = 0, lastPhase = '', bannerUntil = 0, banner = '';
    let state: any = null, arena: ReturnType<typeof decodeArena> | null = null, match: ReturnType<typeof decodeMatch> | null = null;
    if (stop) return; // beta audit: left the page during the awaits: never open the World socket (it reconnected forever)
    unsub.push(subscribe(A.world, (d) => { wa = decodeWorldAcc(d); }, (st) => { $('#chip').dataset.ws = st; }));
    // guest wallet for betting (no popup); spectating works without it
    let ready = false, setupFailed = false;
    // beta audit: a failed setup (RPC busy, gas limit) gets a Retry button instead of a spinner that never ends
    const setup = () => {
      setupFailed = false;
      ensureGuest((m) => { $('#me').textContent = m; }, id)
        .then((g) => { ready = !g.error; if (g.error) fail(g.error); else $('#me').textContent = `you: ${short(g.burner)} · ${g.chips ? rush(g.chips) : '?'} RUSH chips${g.note ? ' · ' + g.note : ''}`; })
        .catch((e) => fail(String(e?.message ?? e)));
    };
    const fail = (why: string) => {
      setupFailed = true;
      $('#me').innerHTML = `betting unavailable: ${esc(why.slice(0, 140))} <button class="btn small" id="retry">Retry</button>`;
      ($('#retry') as HTMLButtonElement).onclick = () => setup();
    };
    setup();
    let c: any;
    try { c = await chainFor(id); } catch (e: any) { fail(String(e?.message ?? e)); return; }
    // drop buttons
    $('#drops').innerHTML = [1, 2, 3, 4].map((k) => `<button class="drop ${DROP_HUE[k]}" data-drop="${k}"><span class="ring" style="--p:1"><span>${icon(DROP_ICON[k])}</span></span><span><span class="nm">${DROPS[k]}</span><span class="pr">${icon('chip').replace('<svg', '<svg style="width:13px;height:13px;vertical-align:-2px"')} ${DROP_PRICE[k]} RUSH</span></span></button>`).join('');
    $('#drops').onclick = async (e) => {
      const b = (e.target as HTMLElement).closest('button[data-drop]') as HTMLButtonElement | null;
      if (!b) return;
      b.disabled = true;
      const r = await J(c.econ('drop', JSON.stringify({ kind: Number(b.dataset.drop) })));
      feed(r.ok ? `<b>you</b> bought <b>${DROPS[Number(b.dataset.drop)]}</b> ${r.session ? '(session key)' : ''} · <a target="_blank" href="${explorer('tx', r.sig, true)}">tx</a>` : `<span class="bad">drop refused</span> ${esc(String(r.error).slice(0, 120))}`);
      if (r.ok) { restart(b.querySelector('.ring'), 'flash'); yourBet(`you dropped <b>${DROPS[Number(b.dataset.drop)]}</b> · <a target="_blank" href="${explorer('tx', r.sig, true)}">tx</a>`); } else restart(b, 'shake');
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
        if (!attested) { attested = true; J(c.econ('teeAttest', JSON.stringify({ base: '/chain/' }))).then((a) => { teeOk = a.ok; feed(a.ok ? `TEE attested in this browser (Intel TDX quote, ${a.ms} ms)` : `<span class="muted">TEE attestation not verified: ${String(a.error).slice(0, 80)}</span>`); }); }
        const r = await J(c.econ('sealedBet', JSON.stringify({ outcome: Number(b.dataset.o), amount: amt })));
        feed(r.ok ? `<b>you</b> sealed ${amt} RUSH on a Champion pick (pick ${r.pick?.ok ? 'in the private book' : '<span class="bad">not in the book: you will reveal it yourself</span>'}, ${r.ms + (r.pick?.ms ?? 0)} ms) · <a target="_blank" href="${explorer('tx', r.sig, true)}">commit tx</a>` : `<span class="bad">sealed bet refused</span> ${esc(String(r.error).slice(0, 120))}`);
        if (r.ok) yourBet(`you bet <b>${amt} RUSH on #${Number(b.dataset.o) + 1}</b> to be Champion · sealed ${icon('lock').replace('<svg', '<svg style="width:16px;height:16px;vertical-align:-3px"')} until the market closes`); else restart(b, 'shake');
        setTimeout(() => (b.disabled = false), 1200);
        return;
      }
      const r = await J(c.econ('bet', JSON.stringify({ k: Number(b.dataset.k), outcome: Number(b.dataset.o), amount: amt })));
      // beta audit: a confirmation timeout is not a refusal (the bet may have landed): say so and hold the button,
      // so nobody bets twice by accident
      const unconfirmed = !r.ok && /unconfirmed/i.test(String(r.error));
      feed(r.ok ? `<b>you</b> bet ${amt} RUSH on slot ${Number(b.dataset.o) + 1} · ${MARKET_NAMES[Number(b.dataset.k)]} ${r.session ? '(session key)' : ''} · <a target="_blank" href="${explorer('tx', r.sig, true)}">tx</a>` : unconfirmed ? `<span class="muted">bet sent, not confirmed yet: watch the pool before betting again</span>` : `<span class="bad">bet refused</span> ${esc(String(r.error).slice(0, 120))}`);
      if (unconfirmed) { setTimeout(() => (b.disabled = false), 6000); return; }
      if (r.ok) { yourBet(`you bet <b>${amt} RUSH on #${Number(b.dataset.o) + 1}</b> · ${MARKET_NAMES[Number(b.dataset.k)]} · <a target="_blank" href="${explorer('tx', r.sig, true)}">tx</a>`); restart(b, 'bump'); } else restart(b, 'shake');
      setTimeout(() => (b.disabled = false), 1200);
    };
    // P7: the markets are built once per (market set, outcome set) and then updated in place, so the odds bars animate
    // and nothing jumps; the buttons keep the data-k / data-o hooks
    let mSig = '';
    const renderMarkets = (ms: any[], slots: number[]) => {
      const sig = ms.map((m) => m.k).join(',') + '|' + slots.join(',');
      if (sig !== mSig) {
        mSig = sig;
        $('#markets').innerHTML = ms.map((m) => `<div class="market" data-m="${m.k}"><div class="head"><b>${MARKET_NAMES[m.k]}</b>${m.k === 0 ? `<span class="chip sealed" data-sealed hidden>${icon('lock')} sealed · revealed at close</span>` : ''}<span class="chip" data-st></span><span class="spacer"></span><span class="muted">pool <b data-pool>0</b> RUSH · <span data-n>0</span> bets</span></div>
          <div class="muted" data-sl></div>
          ${slots.map((sl) => `<div class="mrow" data-row="${sl}"><span class="name">#${sl + 1}${(humans >> sl) & 1 ? ' ★' : ''}</span><span class="oddsbar"><i></i></span><span class="x">—</span><button class="btn small" data-k="${m.k}" data-o="${sl}" disabled>Bet</button></div>`).join('')}</div>`).join('') || '<div class="muted">no market open for this arena right now: they open with the next match</div>';
      }
      for (const m of ms) {
        const el = root.querySelector(`.market[data-m="${m.k}"]`) as HTMLElement | null;
        if (!el) continue;
        const open = marketOpen(state, m.k);
        const st = el.querySelector('[data-st]') as HTMLElement;
        st.textContent = m.status === 1 ? (open ? 'open' : 'closed') : m.status === 2 ? 'settled' : m.status === 3 ? 'paid' : 'idle';
        st.className = 'chip ' + (open ? 'mint' : m.status >= 2 ? 'grape' : '');
        countUp(el.querySelector('[data-pool]'), Number(BigInt(m.pool) / 1000000n));
        (el.querySelector('[data-n]') as HTMLElement).textContent = String(m.nBets);
        const pool = BigInt(m.pool);
        for (const row of el.querySelectorAll<HTMLElement>('.mrow')) {
          const sl = Number(row.dataset.row), stake = BigInt(m.stake[sl] ?? '0');
          const o = odds({ pool, stake: m.stake.map((x: string) => BigInt(x)) } as any, sl);
          (row.querySelector('.oddsbar i') as HTMLElement).style.width = pool > 0n ? `${Math.max(2, Number((stake * 1000n) / pool) / 10)}%` : '2%';
          (row.querySelector('.x') as HTMLElement).textContent = o ? 'x' + o.toFixed(1) : '—';
          row.classList.toggle('win', m.status >= 2 && ((m.winners >> sl) & 1) === 1);
          (row.querySelector('button') as HTMLButtonElement).disabled = !(open && ready);
        }
        if (m.k === 0) {
          const sc = el.querySelector('[data-sealed]') as HTMLElement;
          sc.hidden = !sealed?.escrow;
          (el.querySelector('[data-sl]') as HTMLElement).innerHTML = sealed?.escrow ? `<b>sealed</b>: picks are hidden in MagicBlock's private rollup (TEE) and revealed at close · ${sealed.escrow.n} sealed bet(s), ${rush(sealed.escrow.pool)} RUSH staked${sealed.escrow.state >= 2 ? ` · revealed ${sealed.escrow.nRevealed}/${sealed.escrow.n}${sealed.escrow.nForfeit ? `, ${sealed.escrow.nForfeit} forfeited` : ''}` : ''}${(sealed.mine ?? []).length ? ` · yours: ${(sealed.mine as any[]).map((x) => `${rush(x.amount)} on #${(x.saved?.outcome ?? 0) + 1}${x.state === 1 ? ' ✓revealed' : x.state === 2 ? ' ✗forfeited' : ''}`).join(', ')}` : ''}${sealed.escrow.state === 1 && (sealed.mine ?? []).length ? ' · keep this tab open until the reveal' : ''}${teeOk ? ' · TEE attested ✓' : ''}` : '';
        }
      }
    };
    let prevBets = -1, prevDrops = -1, sealed: any = null, attested = false, teeOk = false, selfRevealAt = 0, wasSealedOpen = false;
    const poll = async () => {
      try {
        state = await J(c.econ('state', '{}'));
        sealed = await J(c.econ('sealedState', '{}')).catch(() => null);
        if (sealed?.escrow) {
          if (wasSealedOpen && sealed.escrow.state >= 2) feed(`Champion market closed: ${sealed.escrow.nRevealed}/${sealed.escrow.n} sealed bets revealed from the private book`);
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
        $('#classic').innerHTML = `${sv?.up ? `${Number(sv.humans)} human(s) on ${esc(String(sv.id).toUpperCase())} · server phase ${esc(sv.phase)}` : ''}${match ? ` · match <a target="_blank" href="${explorer('address', arena!.match, true)}">${short(arena!.match)}</a> ${['Created', 'Dealing', 'Dealt', 'Round active', 'Round resolved', 'Completed', 'Aborted'][match.status]}` : ''}`;
        const ph = `${finalOn}-${wa?.roundPhase}-${sv?.round}`;
        if (ph !== lastPhase) { lastPhase = ph; feed($('#phase').textContent!); }
        // markets
        const ms = (state.markets ?? []) as any[];
        const nb = ms.reduce((a, m) => a + (m.nBets ?? 0), 0);
        if (prevBets >= 0 && nb > prevBets) feed(`${nb - prevBets} new bet(s) on-chain`);
        prevBets = nb;
        const slots: number[] = match ? match.slots.map((x, i) => (x === 1 || x === 2) && i < match!.players ? i : -1).filter((i) => i >= 0) : [];
        $('#ready').textContent = ready ? '' : setupFailed ? '' : '(setting up your guest wallet…)';
        renderMarkets(ms.filter((m) => m.status >= 1), slots);
        const dq = state.drops;
        if (dq && prevDrops >= 0 && dq.count > prevDrops) { const b = dq.buys[dq.count - 1]; banner = `${DROPS[b.kind]}!`; bannerUntil = Date.now() + 2500; feed(`Chaos Drop <b>${DROPS[b.kind]}</b> by ${short(b.by)}`); toast(`${b.by === state.wallet ? 'You' : short(b.by)} dropped ${DROPS[b.kind]}!`, DROP_ICON[b.kind] ?? 'bolt'); restart(root.querySelector(`button[data-drop="${b.kind}"] .ring`), 'flash'); }
        if (dq) prevDrops = dq.count;
        const why = dropOpen(state);
        const cdLeft = state.drops && state.drops.count > 0 && state.world ? Math.max(0, state.drops.lastTick + 150 - state.world.tick) / 150 : 0;
        root.querySelectorAll<HTMLButtonElement>('button[data-drop]').forEach((b) => { b.disabled = !!why || !ready || (Number(b.dataset.drop) === 4 && !sweeperOnSale(state)); (b.querySelector('.ring') as HTMLElement).style.setProperty('--p', (1 - cdLeft).toFixed(3)); });
        $('#dropinfo').textContent = why ? `drops: ${why}` : `drops open · ${3 - (state.drops?.count ?? 0)} left this round · map-wide, telegraphed 0.77 s`;
      } catch (e) { /* transient */ }
      // beta audit: ~14 rollup reads per poll: 2.5 s while visible, 10 s in a background tab (20 tabs hit the public RPC)
      if (!stop) setTimeout(poll, document.hidden ? 10_000 : 2500);
    };
    poll();
    let chipAt = 0;
    const frame = () => {
      if (stop) return;
      const cv = root.querySelector('#cv') as HTMLCanvasElement | null;
      if (cv && wa) {
        const w = wa.round === 4 ? cw : lobby;
        if (w.set(wa.world)) draw(cv, w, { humans, banner: Date.now() < bannerUntil ? banner : undefined, label: `tick ${wa.tick} · ${wa.round === 4 ? 'Final (Lid Flip)' : 'lobby'}` });
        // beta audit: rebuilt at most twice a second (every frame made its links unclickable and cost battery)
        if (Date.now() - chipAt > 500) chipAt = Date.now(), $('#chip').innerHTML = `ER devnet-as · World <a target="_blank" href="${explorer('address', A.world, true)}">${short(A.world)}</a> · tick ${wa.tick} · core v${wa.coreVersion & 0xff} · ws ${$('#chip').dataset.ws ?? ''} · <a target="_blank" href="${explorer('address', PROGRAM)}">program</a>`;
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
