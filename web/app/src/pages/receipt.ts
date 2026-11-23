// /receipt/:match — Rollup ✓ → Solana ✓ timeline, trust labels (● rounds 1–3 by the game authority, ◆ the Final by the
// program), placements, market results, the drops log, the Crown + capsule assets, explorer links.
import { api, account, BASE_HTTP, ER_HTTP, explorer, short, rush, PROGRAM } from '../lib/net';
import { decodeMatch, MARKET_NAMES, LABELS, RARITY, DROPS, MATCH_STATUS, coreAttrs, badges, seasonName } from '../lib/decode';

export function receipt(root: HTMLElement, params: string[]) {
  const id = params[0];
  root.innerHTML = `<div class="card"><h2>Match receipt <span id="ab"></span></h2><div id="r" class="muted">reading the Match on Solana…</div></div>`;
  const $ = (q: string) => root.querySelector(q) as HTMLElement;
  (async () => {
    const [b, e, arc] = await Promise.all([account(BASE_HTTP, id).catch(() => null), account(ER_HTTP, id).catch(() => null), api(`/match/${id}`).catch(() => ({ ok: false }))]);
    const onSolana = !!b && b.owner === PROGRAM;
    const d = onSolana ? b!.data : e?.data;
    if (!d) { $('#r').innerHTML = '<span class="bad">Match not found</span>'; return; }
    const m = decodeMatch(d), rc = m.receipt, s = arc.ok ? arc.summary : null, sig = s?.sigs ?? {};
    const written = (rc.flags & 1) !== 0;
    const name = (slot: number) => `#${slot + 1}${(rc.humanMask >> slot) & 1 ? ' ★ ' + short(rc.wallets[slot]) : ' (bot)'}`;
    const link = (k: string, label: string, er = true) => (sig[k] ? `<a target="_blank" href="${explorer('tx', sig[k], er)}">${label}</a>` : label);
    const crown = rc.flags & 4 ? rc.crown : null;
    const rows = (rc.placements ?? []).map((sl, i) => (sl < 12 ? `<tr><td>${i + 1}</td><td>${name(sl)}</td><td>${(rc.humanMask >> sl) & 1 ? (rc.rarity[sl] < 4 ? RARITY[rc.rarity[sl]] + ((rc.mintedMask >> sl) & 1 ? ' ✓ minted' : ' (minting…)') : '—') : '—'}</td></tr>` : '')).join('');
    $('#r').innerHTML = `
      <div class="strip">
        <div>${written ? '<span class="ok">Rollup ✓</span>' : 'Rollup …'}<br><span class="muted">receipt written on devnet-as by <code>write_receipt</code></span><br>${link('writeReceipt', 'write_receipt tx')} · ${link('finalize', 'finalize (commit + undelegate)')}</div>
        <div>${onSolana ? '<span class="ok">Solana ✓</span>' : 'Solana …'}<br><span class="muted">the Match is back on Solana (owner = the program)</span><br><a target="_blank" href="${explorer('address', id)}">Match account</a></div>
        <div><b>Trust</b><br>${rc.trust & 1 ? '● rounds 1–3 recorded by the game authority (Classic server)' : ''}<br>${rc.trust & 2 ? '◆ the Final computed by the program (rush-core on the rollup)' : ''}<br><span class="muted">core v${rc.coreVersion & 0xff} · status ${MATCH_STATUS[m.status] ?? m.status}</span></div>
        <div><b>Rewards</b><br>${crown ? `👑 <a target="_blank" href="${explorer('address', crown)}">Crown Capsule</a>` : rc.flags & 16 ? 'a bot won: no Crown' : 'Crown: pending'}<br>${s?.finalizeMode ? `<span class="muted">minted by ${s.finalizeMode === 'action' && s.action?.owedAfter === 0 ? 'the Magic Action (post-undelegate)' : s.finalizeMode === 'action' ? 'the Magic Action + keeper retry' : 'the keeper (mint_rewards)'}</span>` : ''}</div>
      </div>
      <h3 style="margin-top:12px">Placements (the Final)</h3><table><tr><th>#</th><th>slot</th><th>capsule</th></tr>${rows}</table>
      <h3 style="margin-top:12px">Rush Markets</h3><table><tr><th>market</th><th>pool</th><th>winners</th><th>settled by</th></tr>${rc.markets.map((mk, k) => mk.status ? `<tr><td>${MARKET_NAMES[k]}</td><td>${rush(mk.pool)} RUSH</td><td>${[...Array(12).keys()].filter((x) => (mk.winners >> x) & 1).map((x) => '#' + (x + 1)).join(' ') || '—'}</td><td>${LABELS[mk.label] ?? ''}</td></tr>` : '').join('')}</table>
      <h3 style="margin-top:12px">Sealed Champion bets 🔒</h3><div>${rc.sealedN ? `${rc.sealedN} sealed bet(s) · ${rush(rc.sealedPool)} RUSH · ${rc.sealedRevealed} revealed from the private book${rc.sealedForfeits ? ` · <b>${rc.sealedForfeits} forfeited</b> (${rush(rc.sealedForfeit)} RUSH to the pool)` : ''} · ◆ settled by the program with the Champion market` : '<span class="muted">none</span>'}</div>
      <div class="muted">${rc.sealedN ? `picks were hidden in MagicBlock's Private Ephemeral Rollup (TEE, <code>devnet-tee</code>) until the market closed${sig.bookClose ? `: <a target="_blank" href="${explorer('tx', sig.sealedClose ?? '', true)}">close</a>` : ''}; each reveal is checked against the commitment sha256(pick ‖ amount ‖ salt ‖ wallet) · entries hash <span class="mono">${rc.sealedHash.slice(0, 16)}…</span>` : ''}</div>
      <div class="muted">${(s?.sealed?.entries ?? []).map((x: any) => `${short(x.wallet)} ${rush(x.amount)} on ${x.state === 1 ? '#' + (x.outcome + 1) : x.state === 2 ? '(forfeited)' : x.state === 3 ? '(refunded)' : '(sealed)'}`).join(' · ')}</div>
      <h3 style="margin-top:12px">Season &amp; achievements</h3><div id="ach" class="muted">${m.status === 6 ? 'aborted: not recorded' : s?.seasons ? Object.keys(s.seasons).map((k) => `recorded into <a href="/season/${k}" data-link>${seasonName(Number(k))}</a>`).join(' · ') : 'season record pending'}</div>
      <h3 style="margin-top:12px">Chaos Drops</h3><div>${rc.drops} drop(s) · ${rush(rc.dropSpent)} RUSH · log hash <span class="mono">${rc.dropLog.slice(0, 16)}…</span></div>
      <div class="muted">${(s?.drops?.buys ?? []).map((x: any) => `${DROPS[x.kind]} at tick ${x.tick} by ${short(x.by)}`).join(' · ')}</div>
      <h3 style="margin-top:12px">Assets</h3><div id="assets" class="muted">${rc.flags & 4 || rc.mintedMask ? '' : 'none minted yet'}</div>
      <h3 style="margin-top:12px">Hashes</h3><div class="mono">map ${rc.mapHash}<br>rounds 1–3 transcript ${rc.preFinal}<br>Final transcript ${rc.finalTranscript}<br>capsule VRF seed ${rc.capsuleSeed}</div>
      <p><a href="/replay/${id}" data-link class="btn small mint">Replay it yourself</a></p>`;
    if (m.status === 6) $('#ab').innerHTML = '<span class="pill">aborted · every stake refunded</span>';
    // P6: achievements earned in this match (from the receipt + archive; first crown from the wallet's lifetime profile)
    if (s?.seasons && m.status === 5) {
      const sid = Object.keys(s.seasons)[0];
      const humans = [...Array(12).keys()].filter((x) => (rc.humanMask >> x) & 1);
      const bettors = [...new Set([0, 1, 2, 3, 4].flatMap((k) => (s[`market${k}`]?.bets ?? []).map((b: any) => b.wallet)))].filter((x) => (s.seasons[sid].walletList ?? []).includes(x));
      const per = (w: string) => {
        let bits = 0;
        const sl = humans.find((x) => rc.wallets[x] === w);
        if (sl != null && ((s.chainWorker?.finalists ?? []).includes(sl) || m.champion === sl)) bits |= 2;
        let won = 0, lost = 0;
        for (let k = 0; k < 5; k++) { const mk = s[`market${k}`]; if (!mk || mk.refund === 1) continue; for (const b of mk.bets) if (b.wallet === w) ((mk.winners >> b.outcome) & 1 ? won++ : lost++); }
        if (won + lost >= 2 && lost === 0) bits |= 4;
        if ((s.drops?.buys ?? []).some((b: any) => b.by === w) && s.final) bits |= 8;
        return bits;
      };
      const rowsA: string[] = [];
      for (const w of [...new Set([...humans.map((x) => rc.wallets[x]), ...bettors])]) {
        let bits = per(w);
        if (m.champion < 12 && rc.wallets[m.champion] === w && (rc.humanMask >> m.champion) & 1) {
          const pr = await api(`/season?id=${sid}&wallet=${w}`).catch(() => null);
          if (pr?.player && (pr.player.achievements & 1)) bits |= 1;
        }
        rowsA.push(`<div>${short(w)}: ${badges(bits) || '<span class="muted">—</span>'}</div>`);
      }
      $('#ach').insertAdjacentHTML('beforeend', rowsA.join(''));
    }
    // the Crown + capsule PDAs (["crown", match], ["capsule", match, slot]) as the keeper archived them
    const all = [...new Set([...(crown ? [crown] : []), ...((s?.assets ?? []) as string[])])];
    for (const a of all) {
      const ai = await account(BASE_HTTP, a).catch(() => null);
      if (!ai) continue;
      const at = coreAttrs(ai.data);
      $('#assets').insertAdjacentHTML('beforeend', `<div><a target="_blank" href="${explorer('address', a)}">${at.name ?? 'asset'}</a> ${at.rarity ? '· ' + at.rarity : ''} ${at.shell ? '· shell ' + at.shell : ''}</div>`);
    }
  })();
}
