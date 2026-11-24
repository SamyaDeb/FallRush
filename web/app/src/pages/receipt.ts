// /receipt/:match (P7 A2: the receipt card). The champion and the 4 rounds, the minted assets, a "Verified" stepper
// (Rollup ✓ → Solana ✓ → Replay ✓; the replay check runs in this page with rush_core.wasm), market results with payouts,
// sealed bets, achievements, Chaos Drops, and a "Proof" drawer with every explorer link and hash. "Download card"
// draws the receipt as a PNG. Trust labels: ● rounds 1–3 by the game authority, ◆ the Final by the program.
import { api, account, BASE_HTTP, ER_HTTP, explorer, short, rush, PROGRAM } from '../lib/net';
import { decodeMatch, MARKET_NAMES, LABELS, RARITY, DROPS, MATCH_STATUS, coreAttrs, seasonName, ACH } from '../lib/decode';
import { icon, capsuleArt, RARITY_COLOUR } from '../lib/icons';
import { verifyMatch } from '../lib/verify';
import { drawCard } from '../lib/card';
import { esc } from '../lib/chain';

const MAPS = ['Capsule Run', 'Goo Tower', 'Star Fountain', 'Lid Flip'];
const MODES = ['Race', 'Survival', 'Skill', 'Final'];
const HUES = ['hue-soda', 'hue-grape', 'hue-lemon', 'hue-bubble'];
const SIG_LABELS: [string, string, boolean][] = [
  ['create', 'create_match (Solana)', false], ['delegate', 'delegate the Match to the rollup', false], ['openMarkets', 'open the Rush Markets', true],
  ['requestDeal', 'MagicBlock VRF deal request', true], ['escrowOpen', 'sealed escrow open', true], ['bookOpen', 'private book open (TEE)', false],
  ['startFinal', 'start the on-chain Final', true], ['sealedClose', 'sealed market close', true], ['bookClose', 'private book close (TEE)', false],
  ['sealedFinish', 'sealed reveal + settle', true], ['writeReceipt', 'write_receipt (rollup)', true], ['commitEcon', 'commit the economy', true],
  ['requestCapsules', 'capsule VRF request', true], ['endSim', 'end the simulation', true], ['finalize', 'finalize: commit + undelegate + Magic Action', true],
];
const small = (svg: string) => svg.replace('<svg', '<svg style="width:20px;height:20px;vertical-align:-4px"');

export function receipt(root: HTMLElement, params: string[]) {
  const id = params[0];
  let stop = false;
  root.innerHTML = `<div class="card hue-tang enter"><div class="ribbon">${icon('check')} MATCH RECEIPT <span class="sub mono">${short(id)}</span><span id="ab"></span></div><div id="r" class="muted">reading the Match on Solana…</div></div>`;
  const $ = (q: string) => root.querySelector(q) as HTMLElement;
  (async () => {
    const [b, e, arc] = await Promise.all([account(BASE_HTTP, id).catch(() => null), account(ER_HTTP, id).catch(() => null), api(`/match/${id}`).catch(() => ({ ok: false }))]);
    const onSolana = !!b && b.owner === PROGRAM;
    const d = onSolana ? b!.data : e?.data;
    if (!d) { $('#r').innerHTML = '<span class="bad">Match not found</span>: this address holds no Fall Rush match on devnet.'; return; }
    const m = decodeMatch(d), rc = m.receipt, s = arc.ok ? arc.summary : null, sig = s?.sigs ?? {};
    const written = (rc.flags & 1) !== 0, aborted = m.status === 6;
    const human = (slot: number) => ((rc.humanMask >> slot) & 1) === 1;
    const tag = (slot: number) => (slot < 12 ? `#${slot + 1}${human(slot) ? '' : ' (bot)'}` : '—');
    const crown = rc.flags & 4 ? rc.crown : null;
    const when = s?.doneAt ? new Date(s.doneAt).toUTCString().slice(5, 22) + ' UTC' : '';
    // the 4 rounds: rounds 1–3 from the Classic server's log (● recorded by the game authority), the Final from the receipt (◆)
    const classic = await fetch(`/api/replay/${id}/classic.jsonl`).then((r) => (r.ok ? r.text() : '')).catch(() => '');
    const ev = classic.split('\n').filter(Boolean).map((l) => { try { return JSON.parse(l); } catch { return null; } }).filter(Boolean);
    const head = ev.find((x: any) => x.type === 'match');
    const q = [1, 2, 3, 4].map((r) => ev.filter((x: any) => x.type === 'event' && x.round === r && x.name === 'QUALIFIED').length);
    const players = head?.players ?? 12;
    const rounds = [0, 1, 2, 3].map((r) => ({
      map: MAPS[head?.deal?.maps?.[r] ?? r] ?? MAPS[r], mode: MODES[r],
      line: r < 3 ? (q[r] ? `${q[r]} of ${r === 0 ? players : q[r - 1] || '?'} advanced` : 'Classic round') : m.champion < 12 ? `${tag(m.champion)} wins` : aborted ? 'aborted' : 'in progress',
      trust: r < 3 ? '● game server' : '◆ program',
    }));
    // assets (the Crown + the capsules the keeper archived)
    const assetIds = [...new Set([...(crown ? [crown] : []), ...((s?.assets ?? []) as string[])])];
    const assets = (await Promise.all(assetIds.map(async (a) => { const ai = await account(BASE_HTTP, a).catch(() => null); return ai ? { id: a, at: coreAttrs(ai.data) } : null; }))).filter(Boolean) as { id: string; at: Record<string, string> }[];
    if (stop) return;
    const champHuman = m.champion < 12 && human(m.champion);
    const assetTiles = assets.map((a, i) => {
      const isCrown = !a.at.shell;
      const rIx = Math.max(0, RARITY.indexOf(a.at.rarity ?? 'Common'));
      return `<a class="tile asset enter ${isCrown ? 'crown' : ''}" style="--i:${i};${isCrown ? '' : `--rim:${RARITY_COLOUR[rIx]};--pastel:#fff;`}text-decoration:none;color:inherit" target="_blank" href="${explorer('address', a.id)}">
        ${capsuleArt(isCrown ? '#ffd21f' : RARITY_COLOUR[rIx], isCrown)}<div><div class="nm">${esc(a.at.name ?? 'asset')}</div><div class="muted">${isCrown ? 'the Crown' : esc((a.at.rarity ?? '') + (a.at.shell ? ' · shell ' + a.at.shell : ''))}</div><div class="muted">Metaplex Core · <span class="mono">${short(a.id)}</span></div></div></a>`;
    }).join('');
    // market results with payouts (pari-mutuel: the winning stake shares the pool)
    const marketRows = rc.markets.map((mk, k) => {
      if (!mk.status) return '';
      const sm = s?.[`market${k}`];
      const winners = [...Array(12).keys()].filter((x) => (mk.winners >> x) & 1);
      const nWin = (sm?.bets ?? []).filter((bt: any) => (mk.winners >> bt.outcome) & 1).length;
      const pay = sm?.refund === 1 ? 'refunded' : sm?.paidTotal && nWin ? `${rush(sm.paidTotal)} RUSH to ${nWin} bet${nWin === 1 ? '' : 's'}` : '—';
      return `<tr><td><b>${MARKET_NAMES[k]}</b>${k === 0 && rc.sealedN ? ` <span class="chip sealed">${icon('lock')} sealed</span>` : ''}</td><td>${rush(mk.pool)} RUSH</td><td>${winners.map((x) => '#' + (x + 1)).join(' ') || '—'}</td><td>${pay}</td><td><span class="chip ${mk.label === 1 ? 'grape' : ''}">${LABELS[mk.label] ?? ''}</span></td></tr>`;
    }).join('');
    const sealedLine = rc.sealedN
      ? `${rc.sealedN} sealed bet(s) · ${rush(rc.sealedPool)} RUSH · ${rc.sealedRevealed} revealed ${(s?.sealed?.reveals ?? []).length ? 'from the private book' : s?.sealed ? 'by the bettors themselves (commit-reveal fallback: the private book was not read)' : ''}${rc.sealedForfeits ? ` · <b>${rc.sealedForfeits} forfeited</b> (${rush(rc.sealedForfeit)} RUSH to the pool)` : ''} · ◆ settled by the program with the Champion market`
      : '<span class="muted">no sealed bets in this match</span>';
    const pools = rc.markets.filter((mk) => mk.status).map((mk) => rush(mk.pool)).join(' / ');
    $('#ab').innerHTML = aborted ? ' <span class="chip pink">aborted · every stake refunded</span>' : '';
    $('#r').className = '';
    $('#r').innerHTML = `
      <div class="two">
        <div class="tile ${champHuman ? 'crown' : 'hue-lemon'} asset enter" style="--i:0">${capsuleArt('#ffd21f', champHuman)}
          <div><div class="muted">${aborted ? 'NO CHAMPION' : 'CHAMPION'}</div><div class="word" style="font-size:36px">${aborted ? 'Aborted' : tag(m.champion)}</div>
          <div>${aborted ? 'the match was aborted: every stake is refunded' : champHuman ? `<span class="mono">${short(rc.wallets[m.champion])}</span> won the on-chain Final${crown ? ' and holds the Crown' : ''}` : 'a bot won the on-chain Final (no Crown)'}</div><div class="muted">${when} · arena ${s?.arena ?? '?'} · ${MATCH_STATUS[m.status] ?? m.status}</div></div></div>
        <div>
          <div class="muted" style="margin-bottom:6px">Verified</div>
          <div class="verify">
            <a class="step ${written ? 'done' : ''}" style="--i:0" target="_blank" href="${sig.writeReceipt ? explorer('tx', sig.writeReceipt, true) : explorer('address', id, true)}"><span class="dot">${icon('check')}</span>Rollup</a><span class="line ${written && onSolana ? 'done' : ''}" style="--i:0"><i></i></span>
            <a class="step ${onSolana ? 'done' : ''}" style="--i:1" target="_blank" href="${explorer('address', id)}"><span class="dot">${icon('check')}</span>Solana</a><span class="line" id="l2" style="--i:1"><i></i></span>
            <a class="step" id="rp" style="--i:2" href="/replay/${id}" data-link><span class="dot">${icon('check')}</span>Replay</a>
          </div>
          <div class="muted" id="rpst" style="margin-top:8px">${aborted ? 'no Final to replay' : 'replaying the Final with rush_core.wasm…'}</div>
          <div class="row" style="margin-top:12px"><button class="btn small" id="dl">${icon('capsule')} Download card</button><a class="btn small mint" href="/replay/${id}" data-link>${icon('play')} Replay it yourself</a><button class="btn small ghost" id="share">Copy link</button></div>
        </div>
      </div>
      <h3 style="margin-top:16px">The 4 rounds</h3>
      <div class="grid" style="grid-template-columns:repeat(auto-fit,minmax(150px,1fr))">${rounds.map((r, i) => `<div class="tile ${HUES[i]} enter" style="--i:${i + 1}"><div class="muted">${i === 3 ? 'FINAL' : 'ROUND ' + (i + 1)}</div><h3>${r.map}</h3><div>${r.line}</div><div class="muted">${r.mode} · ${r.trust}</div></div>`).join('')}</div>
      <h3 style="margin-top:16px">Minted to the players</h3>
      <div class="grid">${assetTiles || `<div class="muted">${aborted ? 'nothing minted (aborted)' : 'no asset minted for this match'}</div>`}</div>
      <h3 style="margin-top:16px">Rush Markets</h3>
      <div class="scroll"><table><tr><th>market</th><th>pool</th><th>winners</th><th>payout</th><th>settled by</th></tr>${marketRows || '<tr><td class="muted" colspan="5">no market opened</td></tr>'}</table></div>
      <h3 style="margin-top:16px">${small(icon('lock'))} Sealed Champion bets</h3><div>${sealedLine}</div>
      <div class="muted">${rc.sealedN ? `the picks were hidden in MagicBlock's Private Ephemeral Rollup (TEE, <code>devnet-tee</code>) until the market closed; each reveal is checked against its commitment sha256(pick ‖ amount ‖ salt ‖ wallet)` : ''}</div>
      <div class="muted">${(s?.sealed?.entries ?? []).map((x: any) => `${short(x.wallet)} ${rush(x.amount)} on ${x.state === 1 ? '#' + (x.outcome + 1) : x.state === 2 ? '(forfeited)' : x.state === 3 ? '(refunded)' : '(sealed)'}`).join(' · ')}</div>
      <h3 style="margin-top:16px">Season &amp; achievements</h3><div id="ach">${aborted ? '<span class="muted">aborted: not recorded</span>' : s?.seasons ? Object.keys(s.seasons).map((k) => `recorded into <a href="/season/${k}" data-link>${seasonName(Number(k))}</a>`).join(' · ') : '<span class="muted">season record pending</span>'}</div>
      <h3 style="margin-top:16px">Chaos Drops</h3><div>${rc.drops ? `${rc.drops} drop(s) · ${rush(rc.dropSpent)} RUSH ` : '<span class="muted">no Chaos Drop in this Final</span> '}${(s?.drops?.buys ?? []).map((x: any) => `<span class="chip pink">${icon('bolt')} ${DROPS[x.kind]} · tick ${x.tick} · ${short(x.by)}</span>`).join(' ')}</div>
      <details class="drawer"><summary>${small(icon('link'))} Proof: explorer links &amp; hashes</summary><div><dl>
        <dt>Match account</dt><dd><a target="_blank" href="${explorer('address', id)}" class="mono">${id}</a> (Solana) · <a target="_blank" href="${explorer('address', id, true)}">on the rollup</a></dd>
        <dt>Program</dt><dd><a target="_blank" href="${explorer('address', PROGRAM)}" class="mono">${PROGRAM}</a></dd>
        ${SIG_LABELS.filter(([k]) => sig[k]).map(([k, label, er]) => `<dt>${label}</dt><dd><a target="_blank" href="${explorer('tx', sig[k], er)}" class="mono">${short(sig[k])}</a></dd>`).join('')}
        ${(s?.sealed?.reveals ?? []).map((x: string, i: number) => `<dt>sealed reveal ${i + 1}</dt><dd><a target="_blank" href="${explorer('tx', x, true)}" class="mono">${short(x)}</a></dd>`).join('')}
        ${Object.entries(s?.seasons ?? {}).flatMap(([k, v]: [string, any]) => (v.sigs ?? []).map((x: string) => `<dt>season ${seasonName(Number(k))}</dt><dd><a target="_blank" href="${explorer('tx', x)}" class="mono">${short(x)}</a></dd>`)).join('')}
        <dt>trust</dt><dd>${rc.trust & 1 ? '● rounds 1–3 recorded by the game authority (Classic server) · ' : ''}${rc.trust & 2 ? '◆ the Final computed by the program (rush-core on the rollup)' : ''} · core v${rc.coreVersion & 0xff}</dd>
        <dt>map hash</dt><dd class="mono">${rc.mapHash}</dd>
        <dt>rounds 1–3 transcript</dt><dd class="mono">${rc.preFinal}</dd>
        <dt>Final transcript</dt><dd class="mono">${rc.finalTranscript}</dd>
        <dt>drop log hash</dt><dd class="mono">${rc.dropLog}</dd>
        <dt>capsule VRF seed</dt><dd class="mono">${rc.capsuleSeed}</dd>
        ${rc.sealedN ? `<dt>sealed entries hash</dt><dd class="mono">${rc.sealedHash}</dd>` : ''}
        ${s?.final?.cu ? `<dt>tick() compute units</dt><dd>max ${s.final.cu.max.toLocaleString()} · p95 ${s.final.cu.p95.toLocaleString()} over ${s.final.cu.n} sampled ticks · ${s.final.ticks} ticks · real time ×${Number(s.final.realTime).toFixed(4)}</dd>` : ''}
      </dl></div></details>`;
    // achievements earned in this match (from the receipt + archive; first crown from the wallet's lifetime profile)
    if (s?.seasons && m.status === 5) {
      const sid = Object.keys(s.seasons)[0];
      const humans = [...Array(12).keys()].filter((x) => human(x));
      const bettors = [...new Set([0, 1, 2, 3, 4].flatMap((k) => (s[`market${k}`]?.bets ?? []).map((bt: any) => bt.wallet)))].filter((x) => (s.seasons[sid].walletList ?? []).includes(x));
      const per = (w: string) => {
        let bits = 0;
        const sl = humans.find((x) => rc.wallets[x] === w);
        if (sl != null && ((s.chainWorker?.finalists ?? []).includes(sl) || m.champion === sl)) bits |= 2;
        let won = 0, lost = 0;
        for (let k = 0; k < 5; k++) { const mk = s[`market${k}`]; if (!mk || mk.refund === 1) continue; for (const bt of mk.bets) if (bt.wallet === w) ((mk.winners >> bt.outcome) & 1 ? won++ : lost++); }
        if (won + lost >= 2 && lost === 0) bits |= 4;
        if ((s.drops?.buys ?? []).some((bt: any) => bt.by === w) && s.final) bits |= 8;
        return bits;
      };
      const ICONS = [icon('crown'), icon('trophy'), icon('eye'), icon('bolt')];
      const rowsA: string[] = [];
      for (const w of [...new Set([...humans.map((x) => rc.wallets[x]), ...bettors])]) {
        let bits = per(w);
        if (m.champion < 12 && rc.wallets[m.champion] === w && human(m.champion)) {
          const pr = await api(`/season?id=${sid}&wallet=${w}`).catch(() => null);
          if (pr?.player && (pr.player.achievements & 1)) bits |= 1;
        }
        const bs = ACH.map((a, i) => (bits & a.bit ? `<span class="badge" title="${a.why}">${ICONS[i] ?? ''}${a.name}</span>` : '')).join('');
        rowsA.push(`<div class="row" style="margin-top:6px"><span class="mono">${short(w)}</span> ${bs || '<span class="muted">no achievement this match</span>'}</div>`);
      }
      if (!stop) $('#ach').insertAdjacentHTML('beforeend', rowsA.join(''));
    }
    // the card + share
    let replayOk: boolean | null = null;
    $('#dl').onclick = async () => {
      ($('#dl') as HTMLButtonElement).disabled = true;
      const blob = await drawCard({
        match: id, champion: aborted ? 'Aborted' : tag(m.champion), championHuman: champHuman, when,
        rounds: rounds.map((r) => ({ map: r.map, mode: r.mode, line: r.line })),
        assets: assets.map((a) => ({ name: a.at.name ?? '', color: !a.at.shell ? '#ffd21f' : RARITY_COLOUR[Math.max(0, RARITY.indexOf(a.at.rarity ?? 'Common'))], crown: !a.at.shell })),
        rollup: written, solana: onSolana, replay: replayOk, sealed: rc.sealedN, drops: rc.drops, pools: pools || '0',
      });
      const a = document.createElement('a');
      a.href = URL.createObjectURL(blob); a.download = `fallrush-receipt-${id.slice(0, 8)}.png`; a.click();
      setTimeout(() => URL.revokeObjectURL(a.href), 4000);
      ($('#dl') as HTMLButtonElement).disabled = false;
    };
    $('#share').onclick = () => navigator.clipboard?.writeText(location.href).then(() => ($('#share').textContent = 'link copied ✓'));
    // the replay check, in this page (on a phone: on a tap, the archive is a few MB)
    if (!aborted && s?.replay !== false) {
      const run = async () => {
        $('#rpst').textContent = 'replaying the Final with rush_core.wasm…';
        try {
          const v = await verifyMatch(id, s, (i, n) => { if (!stop) $('#rpst').textContent = `re-simulating the Final: ${Math.round((100 * i) / n)}%`; });
          if (stop) return;
          replayOk = v.error ? null : v.pass;
          if (!v.error) { $('#rp').classList.add(v.pass ? 'done' : 'fail'); if (v.pass && onSolana) $('#l2').classList.add('done'); }
          $('#rpst').innerHTML = v.error ? `<span class="muted">${esc(v.error)}</span>` : `${v.pass ? '<span class="ok">replay = on-chain ✓</span>' : '<span class="bad">replay ≠ on-chain</span>'} · ${v.ok}/${v.pairs} ticks re-simulated byte-exact${v.transcript ? ' · Final transcript ✓' : ''}`;
          (window as any).__receiptReplay = { pass: v.pass, ok: v.ok, pairs: v.pairs };
        } catch (err) { $('#rpst').textContent = 'replay unavailable: ' + err; }
      };
      if (window.innerWidth < 600) { $('#rpst').innerHTML = `<button class="btn small mint" id="rpgo">${icon('play')} Verify the replay here</button> <span class="muted">(downloads the match archive)</span>`; $('#rpgo').onclick = run; }
      else setTimeout(run, 600);
    }
  })();
  return () => { stop = true; };
}
