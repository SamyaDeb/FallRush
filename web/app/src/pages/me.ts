// /me — the guest wallet (burner) or Phantom: chips, "cash out to real RUSH" (rollup cashout → Solana mint_cashout),
// owned capsules + Crown (Metaplex Core, getProgramAccounts with an owner memcmp), equip a capsule shell for the next
// match (cosmetic only; the game server checks ownership), claim to Phantom, move RUSH to Phantom, back up the key.
import { rpc, api, BASE_HTTP, CORE, ASSETS_AUTH, explorer, short, rush, b64 } from '../lib/net';
import { coreAttrs, badges, seasonName } from '../lib/decode';
import { chainFor, ensureGuest, J, esc } from '../lib/chain';
import { icon, capsuleArt, RARITY_COLOUR } from '../lib/icons';
import { countUp } from '../lib/motion';

export function me(root: HTMLElement) {
  root.innerHTML = `<div class="card hue-grape enter"><div class="ribbon">${icon('capsule')} ME <span class="sub">your guest wallet, chips, capsules and Crown</span></div><div id="w" class="muted">opening your guest wallet…</div>
      <div class="stats" style="margin-top:12px">
        <div class="stat hue-lemon enter" style="--i:1"><div class="v" id="s-chips">…</div><div class="k">RUSH chips on the rollup</div></div>
        <div class="stat hue-apple enter" style="--i:2"><div class="v" id="s-rush">…</div><div class="k">real RUSH (SPL) on Solana</div></div>
        <div class="stat hue-bubble enter" style="--i:3"><div class="v" id="s-crowns">…</div><div class="k">crowns this season</div></div>
        <div class="stat hue-soda enter" style="--i:4"><div class="v" id="s-matches">…</div><div class="k">matches this season</div></div>
      </div></div>
    <div class="card hue-lemon"><div class="ribbon">${icon('crown')} CAPSULES &amp; CROWN</div><div id="caps" class="grid"><span class="muted">reading your assets…</span></div><p class="muted" style="margin-bottom:0">Shells are cosmetic only (never stats). The game server checks you own the capsule before you wear it.</p></div>
    <div class="card hue-soda"><div class="ribbon">${icon('trophy')} THIS SEASON</div><div id="season" class="muted">…</div></div>
    <div class="two">
      <div class="card hue-apple"><div class="ribbon">${icon('chip')} CASH OUT</div><p class="muted" style="margin-top:0">Turn rollup chips into real RUSH (SPL) on Solana: the rollup burns the chips, commits, and the program mints the tokens to your wallet.</p><div id="chips" class="muted">…</div>
        <div class="row" style="margin-top:8px"><input type="number" id="amt" min="1" value="100"> <button class="btn small mint" id="cash">Cash out to RUSH</button></div><div id="cashst" class="muted" style="margin-top:6px"></div></div>
      <div class="card hue-grape"><div class="ribbon">${icon('link')} PHANTOM</div><div class="row"><button class="btn small grape" id="ph">Connect Phantom</button><span id="phst" class="muted"></span></div>
        <div class="row" style="margin-top:8px"><input type="number" id="ramt" min="1" value="50"> <button class="btn small" id="mv" disabled>Move RUSH to Phantom</button></div><div id="mvst" class="muted"></div></div>
    </div>
    <div class="card warn hue-bubble" id="backup-card"><div class="ribbon">${icon('lock')} BACK UP YOUR KEY</div><p>This guest wallet lives only in this browser's storage. Clearing site data or switching device loses it, along with any Crown, capsules or RUSH it holds. Save the key, or claim your assets to Phantom.</p>
      <button class="btn small pink" id="reveal">Show my secret key</button><div id="keybox"></div>
      <details class="drawer"><summary>Restore a backed-up key</summary><div><textarea id="imp" rows="2" placeholder="base58 secret key"></textarea><div class="row" style="margin-top:6px"><button class="btn small" id="doimp">Restore</button> <span id="impst" class="muted"></span></div></div></details></div>`;
  const $ = (q: string) => root.querySelector(q) as HTMLElement;
  let phantom: string | null = null;
  (async () => {
    const c: any = await chainFor();
    const g = await ensureGuest((m) => { $('#w').textContent = m; });
    const w = JSON.parse(c.wallet());
    const refresh = async () => {
      const [st, rs, bal] = await Promise.all([J(c.econ('state', '{}')), J(c.econ('rush', '{}')), J(c.devBalances()).catch(() => ({}))]);
      $('#w').innerHTML = `Guest wallet <a target="_blank" class="mono" href="${explorer('address', w.burner)}">${w.burner}</a><br><span class="muted">${bal.ok ? (bal.burner / 1e9).toFixed(4) + ' SOL (devnet) · ' : ''}session key ${w.session?.token ? 'active until ' + new Date(w.session.validUntil * 1000).toLocaleString() : 'none'}${g.error ? ' · ' + esc(g.error) : ''}</span>`;
      $('#chips').innerHTML = st.chips ? `${rush(st.chips.balance)} RUSH chips available · won ${rush(st.chips.won ?? st.chips.wonTotal ?? 0)} · cashed out ${rush(st.chips.cashedOut)} · <a target="_blank" href="${explorer('address', rs.ata)}">your RUSH token account</a>` : 'no chips yet';
      countUp($('#s-chips'), st.chips ? Number(BigInt(st.chips.balance) / 1000000n) : 0);
      countUp($('#s-rush'), Number(BigInt(rs.amount ?? 0) / 1000000n));
      return { st, rs };
    };
    await refresh();
    api(`/season?wallet=${w.burner}`).then((r) => {
      const p = r?.player;
      countUp($('#s-crowns'), p?.crowns ?? 0); countUp($('#s-matches'), p?.matches ?? 0);
      $('#season').innerHTML = `<b>${seasonName(r.id)}</b>: ${p ? `${p.finals} Final(s) · markets ${Number(p.net) >= 0 ? '+' : ''}${rush(p.net)} RUSH (${p.betsWon}/${p.betsWon + p.betsLost} won) · ${p.drops} drop(s)<div style="margin-top:8px">${badges(p.achievements) || '<span class="muted">no achievements yet</span>'}</div>` : 'nothing recorded yet'}<div style="margin-top:8px"><a class="btn small" href="/season" data-link>${icon('trophy')} Season board</a>${p ? ` <a class="btn small ghost" target="_blank" href="${explorer('address', r.playerAccount)}">${icon('link')} on-chain record</a>` : ''}</div>`;
    }).catch(() => { $('#season').textContent = 'season unavailable'; countUp($('#s-crowns'), 0); countUp($('#s-matches'), 0); });
    $('#cash').onclick = async () => {
      const amount = Number(($('#amt') as HTMLInputElement).value);
      ($('#cash') as HTMLButtonElement).disabled = true;
      $('#cashst').textContent = 'cashing out: rollup → commit → mint on Solana…';
      const r = await J(c.econ('cashout', JSON.stringify({ amount })));
      $('#cashst').innerHTML = r.ok ? `<span class="ok">✓ ${amount} RUSH minted</span> · <a target="_blank" href="${explorer('tx', r.cashoutSig, true)}">cashout (rollup)</a> · <a target="_blank" href="${explorer('tx', r.mintSig)}">mint_cashout (Solana)</a> · commit ${r.commitMs} ms` : `<span class="bad">${esc(r.stage ?? '')} ${esc(String(r.error).slice(0, 160))}</span>`;
      ($('#cash') as HTMLButtonElement).disabled = false;
      refresh();
    };
    // capsules: Core assets owned by this wallet and minted by Fall Rush (update authority = the program's assets PDA)
    const loadCaps = async () => {
      const owner = w.burner;
      const res: any[] = await rpc(BASE_HTTP, 'getProgramAccounts', [CORE, { encoding: 'base64', filters: [{ memcmp: { offset: 1, bytes: owner } }, { memcmp: { offset: 34, bytes: ASSETS_AUTH } }] }]).catch(() => []);
      const eq = JSON.parse(localStorage.getItem('fallrush.equip') ?? 'null');
      $('#caps').innerHTML = res.length ? res.map((a, i) => {
        const at = coreAttrs(b64(a.account.data[0])); const isCrown = !at.shell;
        const col = isCrown ? '#ffd21f' : RARITY_COLOUR[Math.max(0, ['Common', 'Rare', 'Epic', 'Legendary'].indexOf(at.rarity ?? 'Common'))];
        return `<div class="tile asset enter ${isCrown ? 'crown' : ''}" style="--i:${i};${isCrown ? '' : `--rim:${col};--pastel:#fff`}">${capsuleArt(col, isCrown)}<div style="min-width:0"><a class="nm" style="color:inherit;text-decoration:none" target="_blank" href="${explorer('address', a.pubkey)}">${esc(at.name ?? 'asset')}</a>
          <div class="muted">${isCrown ? 'Crown Capsule' : at.rarity ?? ''}${at.shell ? ' · shell ' + at.shell : ''}</div>
          <div class="row" style="margin-top:6px">${at.shell ? `<button class="btn small mint" data-equip="${a.pubkey}" data-shell="${at.shell}">${eq?.asset === a.pubkey ? '✓ equipped' : 'Equip'}</button>` : ''}<button class="btn small grape" data-claim="${a.pubkey}" ${phantom ? '' : 'disabled'} title="${phantom ? '' : 'connect Phantom first'}">Claim to Phantom</button></div></div></div>`; }).join('') : '<div class="muted">no capsules yet: finish a match as a human to get one (and win the Final for the Crown).</div>';
    };
    await loadCaps();
    $('#caps').onclick = async (e) => {
      const t = e.target as HTMLElement;
      const eqb = t.closest('button[data-equip]') as HTMLButtonElement | null;
      if (eqb) { localStorage.setItem('fallrush.equip', JSON.stringify({ asset: eqb.dataset.equip, wallet: w.burner, shell: eqb.dataset.shell })); loadCaps(); return; }
      const cl = t.closest('button[data-claim]') as HTMLButtonElement | null;
      if (cl && phantom) {
        cl.disabled = true;
        const r = await J(c.econ('claim', JSON.stringify({ asset: cl.dataset.claim, to: phantom })));
        cl.outerHTML = r.ok ? `<span class="ok">✓ claimed</span> <a target="_blank" href="${explorer('tx', r.sig)}">tx</a>` : `<span class="bad">${esc(String(r.error).slice(0, 120))}</span>`;
      }
    };
    $('#ph').onclick = async () => {
      const r = await J(c.phantomConnect()).catch((e: any) => ({ ok: false, error: String(e) }));
      phantom = r.ok ? (r.pubkey ?? r.publicKey) : null;
      $('#phst').textContent = phantom ? `connected ${short(phantom)}` : `Phantom: ${r.error ?? 'not available'} (install the Phantom extension)`;
      ($('#mv') as HTMLButtonElement).disabled = !phantom;
      loadCaps();
    };
    $('#mv').onclick = async () => {
      if (!phantom) return;
      const r = await J(c.econ('sendRush', JSON.stringify({ to: phantom, amount: Number(($('#ramt') as HTMLInputElement).value) })));
      $('#mvst').innerHTML = r.ok ? `<span class="ok">✓ sent</span> <a target="_blank" href="${explorer('tx', r.sig)}">tx</a>` : `<span class="bad">${esc(String(r.error).slice(0, 120))}</span>`;
      refresh();
    };
    $('#reveal').onclick = () => {
      if (!confirm('Your secret key gives full control of this guest wallet. Anyone who sees it can take your Crown, capsules and RUSH. Show it?')) return;
      // P6: the sealed-bet salts ride along (a pick can be revealed with them if the private book's reveal fails)
      const salts = localStorage.getItem('fr.sealed.' + w.burner);
      const k = c.exportBurner() + (salts && salts !== '[]' ? '\nsealed:' + btoa(salts) : '');
      $('#keybox').innerHTML = `<textarea rows="3" readonly>${k}</textarea><div class="row"><button class="btn small" id="copy">Copy</button><span class="muted">Store it somewhere private (a password manager). Never paste it into a website you don't trust.</span></div>`;
      $('#copy').onclick = () => navigator.clipboard.writeText(k).then(() => ($('#copy').textContent = 'copied ✓'));
    };
    $('#doimp').onclick = () => {
      if (!confirm('Replace the guest wallet in this browser with the restored key? Back up the current one first if it holds anything.')) return;
      const [key, ...rest] = ($('#imp') as HTMLTextAreaElement).value.trim().split(/\s+/);
      const r = JSON.parse(c.importBurner(key));
      const sl = rest.find((x) => x.startsWith('sealed:'));
      if (r.ok && sl) { try { const nw = JSON.parse(c.wallet()).burner; localStorage.setItem('fr.sealed.' + nw, atob(sl.slice(7))); } catch { /* ignore */ } }
      $('#impst').textContent = r.ok ? 'restored: reloading…' : r.error;
      if (r.ok) setTimeout(() => location.reload(), 800);
    };
    if (new URLSearchParams(location.search).get('backup') === '1') $('#backup-card').scrollIntoView({ behavior: 'smooth' });
  })();
}
