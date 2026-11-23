// /me — the guest wallet (burner) or Phantom: chips, "cash out to real RUSH" (rollup cashout → Solana mint_cashout),
// owned capsules + Crown (Metaplex Core, getProgramAccounts with an owner memcmp), equip a capsule shell for the next
// match (cosmetic only; the game server checks ownership), claim to Phantom, move RUSH to Phantom, back up the key.
import { rpc, BASE_HTTP, CORE, ASSETS_AUTH, explorer, short, rush, b64 } from '../lib/net';
import { coreAttrs } from '../lib/decode';
import { chainFor, ensureGuest, J } from '../lib/chain';

export function me(root: HTMLElement) {
  root.innerHTML = `<div class="card"><h2>Me</h2><div id="w" class="muted">opening your guest wallet…</div></div>
    <div class="card" id="backup-card"><h3>Back up your key</h3><p class="muted">This guest wallet lives only in this browser's storage. Clearing site data or switching device loses it, along with any Crown, capsules or RUSH it holds. Save the key, or claim your assets to Phantom.</p>
      <button class="btn small pink" id="reveal">Show my secret key</button><div id="keybox"></div>
      <details style="margin-top:8px"><summary>Restore a backed-up key</summary><textarea id="imp" rows="2" placeholder="base58 secret key"></textarea><button class="btn small" id="doimp">Restore</button> <span id="impst" class="muted"></span></details></div>
    <div class="card"><h3>Chips → real RUSH</h3><div id="chips" class="muted">…</div>
      <div class="row" style="margin-top:6px"><input type="number" id="amt" min="1" value="100"> <button class="btn small mint" id="cash">Cash out to RUSH (SPL)</button></div><div id="cashst" class="muted"></div></div>
    <div class="card"><h3>Capsules &amp; Crown</h3><div id="caps" class="muted">reading your assets…</div></div>
    <div class="card"><h3>Phantom</h3><div class="row"><button class="btn small grape" id="ph">Connect Phantom</button><span id="phst" class="muted"></span></div>
      <div class="row" style="margin-top:6px"><input type="number" id="ramt" min="1" value="50"> <button class="btn small" id="mv" disabled>Move RUSH to Phantom</button><span id="mvst" class="muted"></span></div></div>`;
  const $ = (q: string) => root.querySelector(q) as HTMLElement;
  let phantom: string | null = null;
  (async () => {
    const c: any = await chainFor();
    const g = await ensureGuest((m) => { $('#w').textContent = m; });
    const w = JSON.parse(c.wallet());
    const refresh = async () => {
      const [st, rs, bal] = await Promise.all([J(c.econ('state', '{}')), J(c.econ('rush', '{}')), J(c.devBalances()).catch(() => ({}))]);
      $('#w').innerHTML = `Guest wallet <a target="_blank" class="mono" href="${explorer('address', w.burner)}">${w.burner}</a><br><span class="muted">${bal.ok ? (bal.burner / 1e9).toFixed(4) + ' SOL (devnet) · ' : ''}session key ${w.session?.token ? 'active until ' + new Date(w.session.validUntil * 1000).toLocaleString() : 'none'}${g.error ? ' · ' + g.error : ''}</span>`;
      $('#chips').innerHTML = st.chips ? `<b>${rush(st.chips.balance)}</b> RUSH chips on the rollup · won ${rush(st.chips.won ?? st.chips.wonTotal ?? 0)} · cashed out ${rush(st.chips.cashedOut)}<br><b>${rush(rs.amount ?? 0)}</b> real RUSH (SPL, Solana) in <a target="_blank" href="${explorer('address', rs.ata)}">your token account</a>` : 'no chips yet';
      return { st, rs };
    };
    await refresh();
    $('#cash').onclick = async () => {
      const amount = Number(($('#amt') as HTMLInputElement).value);
      ($('#cash') as HTMLButtonElement).disabled = true;
      $('#cashst').textContent = 'cashing out: rollup → commit → mint on Solana…';
      const r = await J(c.econ('cashout', JSON.stringify({ amount })));
      $('#cashst').innerHTML = r.ok ? `<span class="ok">✓ ${amount} RUSH minted</span> · <a target="_blank" href="${explorer('tx', r.cashoutSig, true)}">cashout (rollup)</a> · <a target="_blank" href="${explorer('tx', r.mintSig)}">mint_cashout (Solana)</a> · commit ${r.commitMs} ms` : `<span class="bad">${r.stage ?? ''} ${String(r.error).slice(0, 160)}</span>`;
      ($('#cash') as HTMLButtonElement).disabled = false;
      refresh();
    };
    // capsules: Core assets owned by this wallet and minted by Fall Rush (update authority = the program's assets PDA)
    const loadCaps = async () => {
      const owner = w.burner;
      const res: any[] = await rpc(BASE_HTTP, 'getProgramAccounts', [CORE, { encoding: 'base64', filters: [{ memcmp: { offset: 1, bytes: owner } }, { memcmp: { offset: 34, bytes: ASSETS_AUTH } }] }]).catch(() => []);
      const eq = JSON.parse(localStorage.getItem('fallrush.equip') ?? 'null');
      $('#caps').innerHTML = res.length ? res.map((a) => { const at = coreAttrs(b64(a.account.data[0])); const isCrown = !at.shell; return `<div class="row" style="margin:6px 0">${isCrown ? '👑' : '🥚'} <a target="_blank" href="${explorer('address', a.pubkey)}">${at.name ?? 'asset'}</a> ${at.rarity ? `<span class="pill">${at.rarity}</span>` : ''} ${at.shell ? `<span class="pill">shell ${at.shell}</span>` : ''}
        ${at.shell ? `<button class="btn small mint" data-equip="${a.pubkey}" data-shell="${at.shell}">${eq?.asset === a.pubkey ? '✓ equipped' : 'Equip for the next match'}</button>` : ''}
        <button class="btn small grape" data-claim="${a.pubkey}" ${phantom ? '' : 'disabled'}>Claim to Phantom</button></div>`; }).join('') + `<p class="muted">Shells are cosmetic only (never stats). The game server checks you own the capsule before you wear it.</p>` : 'no capsules yet: finish a match as a human to get one (and win the Final for the Crown).';
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
        cl.outerHTML = r.ok ? `<span class="ok">✓ claimed</span> <a target="_blank" href="${explorer('tx', r.sig)}">tx</a>` : `<span class="bad">${String(r.error).slice(0, 120)}</span>`;
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
      $('#mvst').innerHTML = r.ok ? `<span class="ok">✓ sent</span> <a target="_blank" href="${explorer('tx', r.sig)}">tx</a>` : `<span class="bad">${String(r.error).slice(0, 120)}</span>`;
      refresh();
    };
    $('#reveal').onclick = () => {
      if (!confirm('Your secret key gives full control of this guest wallet. Anyone who sees it can take your Crown, capsules and RUSH. Show it?')) return;
      const k = c.exportBurner();
      $('#keybox').innerHTML = `<textarea rows="3" readonly>${k}</textarea><div class="row"><button class="btn small" id="copy">Copy</button><span class="muted">Store it somewhere private (a password manager). Never paste it into a website you don't trust.</span></div>`;
      $('#copy').onclick = () => navigator.clipboard.writeText(k).then(() => ($('#copy').textContent = 'copied ✓'));
    };
    $('#doimp').onclick = () => {
      if (!confirm('Replace the guest wallet in this browser with the restored key? Back up the current one first if it holds anything.')) return;
      const r = JSON.parse(c.importBurner(($('#imp') as HTMLTextAreaElement).value.trim()));
      $('#impst').textContent = r.ok ? 'restored: reloading…' : r.error;
      if (r.ok) setTimeout(() => location.reload(), 800);
    };
    if (new URLSearchParams(location.search).get('backup') === '1') $('#backup-card').scrollIntoView({ behavior: 'smooth' });
  })();
}
