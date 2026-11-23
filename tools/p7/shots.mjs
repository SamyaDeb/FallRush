#!/usr/bin/env node
// P7 screenshot driver (copy of the tools/p6/smoke.mjs CDP core: Chrome over the DevTools protocol, clean profile).
//   pages   every web page at 390×844 and 1440×900 → <out>/<page>-<w>.png (+ console errors per page in errors.json)
//   player  /play?bot=match (the match probe plays rounds 1–3, the on-chain Final auto-plays): a PNG every --every s
//           until the keeper archives a new match (or --max s) → <out>/t<sec>.png
//   node tools/p7/shots.mjs --flow pages --base https://play.… --out docs/proof/p7/polish/before/web --match <pda>
import { spawn } from 'node:child_process';
import { mkdirSync, writeFileSync, appendFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';

const opt = (k, d) => { const i = process.argv.indexOf('--' + k); return i > 0 ? process.argv[i + 1] : d; };
const FLOW = opt('flow', 'pages'), BASE = opt('base', 'http://localhost:5173'), OUT = opt('out', 'shots-out');
const PORT = Number(opt('port', '9371')), MAX = Number(opt('max', '1300')), MATCH = opt('match', ''), EVERY = Number(opt('every', '4'));
mkdirSync(OUT, { recursive: true });
const LOG = join(OUT, 'console.log'); writeFileSync(LOG, '');
const profile = join(tmpdir(), `fr-p7-${FLOW}-${process.pid}`);
rmSync(profile, { recursive: true, force: true });
const CHROME = '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';
const chrome = spawn(CHROME, [`--remote-debugging-port=${PORT}`, `--user-data-dir=${profile}`, '--no-first-run', '--no-default-browser-check',
  '--window-size=1440,1000', '--autoplay-policy=no-user-gesture-required', '--disable-features=Translate', ...(process.argv.includes('--headless') ? ['--headless=new'] : []), 'about:blank'], { stdio: 'ignore' });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let target;
for (let i = 0; i < 60 && !target; i++) { await sleep(500); try { target = (await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json()).find((t) => t.type === 'page'); } catch {} }
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((r) => (ws.onopen = r));
let id = 0; const pending = new Map();
const send = (method, params = {}) => new Promise((res) => { const i = ++id; pending.set(i, res); ws.send(JSON.stringify({ id: i, method, params })); });
const t0 = Date.now(); const T = () => (Date.now() - t0) / 1000;
let errors = [];
ws.onmessage = (ev) => {
  const m = JSON.parse(ev.data);
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m.result ?? m.error); pending.delete(m.id); return; }
  if (m.method === 'Runtime.consoleAPICalled') { const t = m.params.args.map((a) => a.value ?? a.description ?? '').join(' '); if (m.params.type === 'error') errors.push(t.slice(0, 300)); appendFileSync(LOG, `${T().toFixed(1)}\t${m.params.type}\t${t}\n`); }
  else if (m.method === 'Runtime.exceptionThrown') { const t = `${m.params.exceptionDetails.text} ${m.params.exceptionDetails.exception?.description ?? ''}`; errors.push(t.slice(0, 300)); appendFileSync(LOG, `${T().toFixed(1)}\texception\t${t}\n`); }
  else if (m.method === 'Log.entryAdded' && m.params.entry.level === 'error') { errors.push(`[${m.params.entry.source}] ${m.params.entry.text} ${m.params.entry.url ?? ''}`.slice(0, 300)); appendFileSync(LOG, `${T().toFixed(1)}\tlog-error\t${m.params.entry.text} ${m.params.entry.url ?? ''}\n`); }
  else if (m.method === 'Page.javascriptDialogOpening') send('Page.handleJavaScriptDialog', { accept: true });
};
await send('Runtime.enable'); await send('Page.enable'); await send('Log.enable');
const ev = async (expr) => { const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true }); return r?.result?.value; };
const shot = async (name, full = false) => { const r = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: full }); if (r?.data) writeFileSync(join(OUT, `${name}.png`), Buffer.from(r.data, 'base64')); };
const size = (w, h, phone) => Promise.all([send('Emulation.setDeviceMetricsOverride', { width: w, height: h, deviceScaleFactor: phone ? 2 : 1, mobile: phone }),
  send('Emulation.setUserAgentOverride', { userAgent: phone ? 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1' : 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36' })]);
const done = async (code, extra = {}) => { writeFileSync(join(OUT, 'result.json'), JSON.stringify({ flow: FLOW, at: new Date().toISOString(), ...extra }, null, 1)); ws.close(); chrome.kill(); await sleep(500); rmSync(profile, { recursive: true, force: true }); process.exit(code); };

try {
  if (FLOW === 'pages') {
    const pages = (opt('pages', `arenas:/arenas,live:/live/2,receipt:/receipt/${MATCH},replay:/replay/${MATCH},me:/me,season:/season,play:/play`)).split(',').map((p) => p.split(/:(.*)/s));
    const perPage = {};
    for (const [w, h, phone] of [[390, 844, true], [1440, 900, false]]) {
      await size(w, h, phone);
      for (const [name, path] of pages) {
        errors = [];
        await send('Page.navigate', { url: BASE + path });
        await sleep(Number(opt('wait', '9')) * 1000);
        await shot(`${name}-${w}`, true);
        perPage[`${name}-${w}`] = errors;
        console.log(name, w, 'errors', errors.length);
      }
    }
    writeFileSync(join(OUT, 'errors.json'), JSON.stringify(perPage, null, 1));
    await done(0, { errors: Object.fromEntries(Object.entries(perPage).map(([k, v]) => [k, v.length])) });
  }
  if (FLOW === 'player') {
    await size(Number(opt('w', '1440')), Number(opt('h', '900')), false);
    const before = await (await fetch(`${BASE}/api/matches?n=1`)).json().catch(() => ({ matches: [] }));
    const last = before.matches?.[0]?.match;
    const sv = opt('server', '');
    await send('Page.navigate', { url: `${BASE}/play?bot=match&onchainauto=900${opt('q', '')}${sv ? '&server=' + encodeURIComponent(sv) : ''}` });
    let got = null; let k = 0;
    for (const end = Date.now() + MAX * 1000; Date.now() < end && !got;) {
      await shot(`t${String(Math.round(T())).padStart(4, '0')}`);
      if (++k % 3 === 0) {
        const m = await (await fetch(`${BASE}/api/matches?n=1`)).json().catch(() => null);
        got = (m?.matches ?? []).find((x) => x.match !== last && x.doneAt > Date.now() - MAX * 1000) ?? null;
      }
      await sleep(EVERY * 1000);
    }
    // keep shooting the receipt for a bit after the archive
    for (let i = 0; i < 6; i++) { await sleep(EVERY * 1000); await shot(`t${String(Math.round(T())).padStart(4, '0')}`); }
    await done(got ? 0 : 1, { match: got });
  }
  throw new Error('unknown flow ' + FLOW);
} catch (e) { console.error(e); await done(2, { error: String(e?.stack ?? e) }); }
