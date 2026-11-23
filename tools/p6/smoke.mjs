#!/usr/bin/env node
// P6 copy of tools/p5/smoke.mjs + flow `sealed` (phone /live: a sealed Champion bet → close → reveal). P5 smoke / verification driver: Chrome over the DevTools protocol (Node's WebSocket, no packages), a CLEAN profile
// per run (empty storage), console + screenshots into <out>/. Flows:
//   lobby   landing → click "Play now" → time until the game server counts us (/api/status humans ≥ base + 1)
//   player  /play?bot=match&onchainauto=… (the match probe plays rounds 1–3, the on-chain Final auto-plays) until the
//           keeper archives a new match (or MAX)
//   live    phone viewport /live/<arena>: waits for an open market → bets 10 RUSH; during the Final's RUSH → a drop
//   replay  /replay/<match>: waits for the verdict (window.__replay)
//   receipt /receipt/<match>: Rollup ✓ / Solana ✓ text
//   me      /me: cash out 10 RUSH; (optional --phantom: a wallet-standard stand-in injected as window.solana)
//   node tools/p5/smoke.mjs --flow lobby --base https://play.… --out docs/proof/p5/smoke/lobby-1 [--port 9341] [--max 120]
import { spawn } from 'node:child_process';
import { mkdirSync, writeFileSync, appendFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';

const opt = (k, d) => { const i = process.argv.indexOf('--' + k); return i > 0 ? process.argv[i + 1] : d; };
const FLOW = opt('flow', 'lobby'), BASE = opt('base', 'http://localhost:5173'), OUT = opt('out', 'smoke-out');
const PORT = Number(opt('port', '9341')), MAX = Number(opt('max', '120')), ARENA = opt('arena', '2'), MATCH = opt('match', '');
const PHONE = FLOW === 'live' || FLOW === 'sealed' || process.argv.includes('--phone');
mkdirSync(join(OUT, 'shots'), { recursive: true });
const LOG = join(OUT, 'console.log'); writeFileSync(LOG, '');
const result = { flow: FLOW, started: new Date().toISOString(), marks: {} };
const profile = join(tmpdir(), `fr-p6-${FLOW}-${process.pid}`);
rmSync(profile, { recursive: true, force: true });
const CHROME = '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';
const chrome = spawn(CHROME, [`--remote-debugging-port=${PORT}`, `--user-data-dir=${profile}`, '--no-first-run', '--no-default-browser-check',
  '--window-size=1440,1000', '--autoplay-policy=no-user-gesture-required', '--disable-features=Translate', 'about:blank'], { stdio: 'ignore' });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let target;
for (let i = 0; i < 60 && !target; i++) { await sleep(500); try { target = (await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json()).find((t) => t.type === 'page'); } catch {} }
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((r) => (ws.onopen = r));
let id = 0; const pending = new Map();
const send = (method, params = {}) => new Promise((res) => { const i = ++id; pending.set(i, res); ws.send(JSON.stringify({ id: i, method, params })); });
const t0 = Date.now(); const T = () => (Date.now() - t0) / 1000;
const lines = [];
ws.onmessage = (ev) => {
  const m = JSON.parse(ev.data);
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m.result ?? m.error); pending.delete(m.id); return; }
  if (m.method === 'Runtime.consoleAPICalled') { const t = m.params.args.map((a) => a.value ?? a.description ?? '').join(' '); lines.push(t); appendFileSync(LOG, `${T().toFixed(1)}\t${m.params.type}\t${t}\n`); }
  else if (m.method === 'Runtime.exceptionThrown') appendFileSync(LOG, `${T().toFixed(1)}\texception\t${m.params.exceptionDetails.text} ${m.params.exceptionDetails.exception?.description ?? ''}\n`);
  else if (m.method === 'Page.javascriptDialogOpening') { appendFileSync(LOG, `${T().toFixed(1)}\tdialog\t${m.params.message}\n`); result.popups = (result.popups ?? 0) + 1; send('Page.handleJavaScriptDialog', { accept: true }); }
};
await send('Runtime.enable'); await send('Page.enable');
await send('Emulation.setDeviceMetricsOverride', PHONE ? { width: 390, height: 844, deviceScaleFactor: 2, mobile: true } : { width: 1440, height: 900, deviceScaleFactor: 1, mobile: false });
if (PHONE) await send('Emulation.setUserAgentOverride', { userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1' });
const ev = async (expr) => { const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true }); return r?.result?.value; };
const shot = async (name) => { const r = await send('Page.captureScreenshot', { format: 'png' }); if (r?.data) writeFileSync(join(OUT, 'shots', `${name}.png`), Buffer.from(r.data, 'base64')); };
const mark = (k, extra = {}) => { result.marks[k] = { t: Number(T().toFixed(2)), ...extra }; console.log(FLOW, k, T().toFixed(1), JSON.stringify(extra).slice(0, 200)); };
const status = async () => { try { return await (await fetch(`${BASE}/api/status`)).json(); } catch { return null; } };
const humansNow = (s) => (s?.servers ?? []).reduce((a, x) => a + (x.humans ?? 0), 0);
const until = async (f, secs, every = 500) => { const e = Date.now() + secs * 1000; while (Date.now() < e) { const v = await f(); if (v) return v; await sleep(every); } return null; };
const clickSel = (sel) => ev(`(() => { const e = document.querySelector(${JSON.stringify(sel)}); if (!e || e.disabled) return false; e.click(); return true; })()`);
const done = async (code) => { result.finished = new Date().toISOString(); writeFileSync(join(OUT, 'result.json'), JSON.stringify(result, null, 1)); ws.close(); chrome.kill(); await sleep(500); rmSync(profile, { recursive: true, force: true }); process.exit(code); };

try {
  if (FLOW === 'lobby') {
    const h0 = humansNow(await status());
    await send('Page.navigate', { url: BASE + opt('start', '/arenas') });
    await until(() => ev(`!!document.querySelector('a[href="/play"].btn')`), 30, 100);
    mark('landing');
    const tc = Date.now();
    await clickSel('a[href="/play"].btn');
    mark('click');
    const ok = await until(async () => humansNow(await status()) > h0, MAX, 400);
    result.clickToLobbyS = ok ? (Date.now() - tc) / 1000 : null;
    result.pageMarks = await ev('JSON.stringify(window.__fr?.marks ?? {})');
    mark(ok ? 'in_lobby' : 'timeout', { clickToLobbyS: result.clickToLobbyS });
    await shot('lobby');
    result.pass = !!ok && result.clickToLobbyS <= 30 && !result.popups;
    await done(result.pass ? 0 : 1);
  }
  if (FLOW === 'player') {
    const before = await (await fetch(`${BASE}/api/matches?n=1`)).json().catch(() => ({ matches: [] }));
    const last = before.matches?.[0]?.match;
    const sv = opt('server', '');
    await send('Page.navigate', { url: `${BASE}/play?bot=match&onchainauto=900&hud=1${opt('autobet', '') ? '&autobet=' + opt('autobet', '') : ''}${sv ? '&server=' + encodeURIComponent(sv) : ''}` });
    let n = 0;
    const got = await until(async () => {
      if (++n % 60 === 0) await shot(`t${Math.round(T())}`);
      const m = await (await fetch(`${BASE}/api/matches?n=1`)).json().catch(() => null);
      const nw = (m?.matches ?? []).find((x) => x.match !== last && x.doneAt > Date.now() - MAX * 1000 && (!opt('arena-only', '') || String(x.arena) === opt('arena-only', '')));
      return nw ?? null;
    }, MAX, 1000);
    result.match = got; result.switched = lines.some((l) => /\[onchain\] (switch|pre-join)/.test(l));
    mark(got ? 'archived' : 'timeout', { match: got?.match, switched: result.switched });
    await shot('end');
    result.pass = !!got;
    await done(got ? 0 : 1);
  }
  if (FLOW === 'sealed') {
    await send('Page.navigate', { url: `${BASE}/live/${ARENA}` });
    await until(() => ev(`!!document.querySelector('#cv')`), 30);
    mark('loaded');
    const ready = await until(() => ev(`/you: .*RUSH chips/.test(document.querySelector('#me')?.textContent ?? '') ? document.querySelector('#me').textContent : null`), 240, 1000);
    mark('guest_ready', { ready });
    const bet = await until(() => ev(`(() => { if (!/sealed/.test(document.querySelector('#markets')?.textContent ?? '')) return null; const b = [...document.querySelectorAll('button[data-k="0"]')].find((x) => !x.disabled); if (!b) return null; b.click(); return b.textContent; })()`), MAX, 1000);
    mark(bet ? 'sealed_clicked' : 'no_sealed_market', { bet });
    const ok = bet ? await until(() => ev(`[...document.querySelectorAll('#feed div')].map((d) => d.textContent).find((t) => /you sealed|sealed bet refused/.test(t)) ?? null`), 45) : null;
    mark('sealed_result', { ok });
    await sleep(1500); await shot('sealed-bet');
    const att = await until(() => ev(`[...document.querySelectorAll('#feed div')].map((d) => d.textContent).find((t) => /TEE attest/.test(t)) ?? null`), 40);
    mark('attestation', { att });
    const closed = await until(() => ev(`[...document.querySelectorAll('#feed div')].map((d) => d.textContent).find((t) => /Champion market closed/.test(t)) ?? null`), MAX, 1000);
    mark('closed', { closed });
    const rev = await until(() => ev(`(() => { const t = document.querySelector('#markets')?.textContent ?? ''; return /✓revealed|✗forfeited/.test(t) ? t.match(/yours:[^·]*/)?.[0] ?? t.slice(0, 200) : null; })()`), 60, 1000);
    mark('revealed', { rev });
    await shot('sealed-revealed');
    result.pass = /you sealed/.test(ok ?? '') && /revealed/.test(rev ?? '');
    await done(result.pass ? 0 : 1);
  }
  if (FLOW === 'live') {
    await send('Page.navigate', { url: `${BASE}/live/${ARENA}` });
    await until(() => ev(`!!document.querySelector('#cv')`), 30);
    mark('loaded');
    await shot('live-0');
    const bet = await until(() => ev(`(() => { const b = [...document.querySelectorAll('button[data-k]')].find((x) => !x.disabled); if (!b) return null; b.click(); return b.textContent; })()`), MAX, 1000);
    mark(bet ? 'bet_clicked' : 'no_open_market', { bet });
    const betOk = bet ? await until(() => ev(`[...document.querySelectorAll('#feed div')].map((d) => d.textContent).find((t) => /you bet|bet refused/.test(t)) ?? null`), 30) : null;
    mark('bet_result', { betOk });
    await shot('live-bet');
    const drop = await until(() => ev(`(() => { const b = [...document.querySelectorAll('button[data-drop]')].find((x) => !x.disabled); if (!b) return null; b.click(); return b.textContent; })()`), MAX, 1000);
    mark(drop ? 'drop_clicked' : 'no_drop_window', { drop });
    const dropOk = drop ? await until(() => ev(`[...document.querySelectorAll('#feed div')].map((d) => d.textContent).find((t) => /you bought|drop refused/.test(t)) ?? null`), 30) : null;
    mark('drop_result', { dropOk });
    await sleep(4000); await shot('live-drop');
    result.oddsSamples = await ev(`[...document.querySelectorAll('button[data-k]')].slice(0, 6).map((b) => b.textContent)`);
    result.pass = /you bet/.test(betOk ?? '') && /you bought/.test(dropOk ?? '');
    await done(result.pass ? 0 : 1);
  }
  if (FLOW === 'replay') {
    await send('Page.navigate', { url: `${BASE}/replay/${MATCH}` });
    const r = await until(() => ev('window.__replay ? JSON.stringify(window.__replay) : null'), MAX, 1000);
    result.replay = r ? JSON.parse(r) : null; result.text = await ev(`document.querySelector('#st')?.textContent`);
    mark('verdict', { text: result.text });
    await sleep(3000); await shot('replay');
    result.pass = !!result.replay?.pass;
    await done(result.pass ? 0 : 1);
  }
  if (FLOW === 'receipt') {
    await send('Page.navigate', { url: `${BASE}/receipt/${MATCH}` });
    const t = await until(() => ev(`(() => { const t = document.querySelector('#r')?.innerText ?? ''; return /Solana ✓/.test(t) && /Rollup ✓/.test(t) ? t : null; })()`), MAX, 1000);
    result.text = (t ?? (await ev(`document.querySelector('#r')?.innerText`)) ?? '').slice(0, 3000);
    mark(t ? 'rollup_solana_ok' : 'timeout');
    await sleep(2500); await shot('receipt');
    result.pass = !!t;
    await done(result.pass ? 0 : 1);
  }
  if (FLOW === 'me') {
    if (process.argv.includes('--phantom')) {
      // D-PHANTOM: a stand-in wallet injected as window.solana (connect only); real Phantom is UNVERIFIED
      await send('Page.addScriptToEvaluateOnNewDocument', { source: `window.solana = { isPhantom: true, publicKey: null, async connect() { const k = '${opt('to', '58YqMtZDoDAZ3zToumjRJa8zJuyU5bhpiFJqkTeZwAVk')}'; this.publicKey = { toBase58: () => k, toString: () => k }; return { publicKey: this.publicKey }; } };` });
    }
    await send('Page.navigate', { url: `${BASE}/me` });
    await until(() => ev(`/Guest wallet/.test(document.querySelector('#w')?.innerText ?? '')`), MAX, 1000);
    mark('wallet');
    await until(() => ev(`/RUSH chips/.test(document.querySelector('#chips')?.innerText ?? '')`), 60, 1000);
    await ev(`document.querySelector('#amt').value = '10'`);
    await clickSel('#cash');
    const c = await until(() => ev(`(() => { const t = document.querySelector('#cashst')?.innerText ?? ''; return /minted|error|rollup|commit|mint /.test(t) && !/cashing out/.test(t) ? t : null; })()`), 120, 1000);
    result.cashout = c; mark('cashout', { c });
    if (process.argv.includes('--phantom')) { await clickSel('#ph'); await sleep(1500); result.phantom = await ev(`document.querySelector('#phst')?.innerText`); }
    await sleep(2000); await shot('me');
    result.pass = /minted/.test(c ?? '');
    await done(result.pass ? 0 : 1);
  }
  throw new Error('unknown flow ' + FLOW);
} catch (e) { result.error = String(e?.stack ?? e); await done(2); }
