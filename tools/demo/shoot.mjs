#!/usr/bin/env node
// Demo capture without OS screen-recording permission: opens a real Chrome (GPU, headed) with Playwright, records the
// page through the DevTools screencast (JPEG frames + their timestamps), optionally presses keys on a schedule, and
// encodes a constant-frame-rate H.264 file with ffmpeg. Wall-clock start/end are written next to the video so the
// rollup log (tick-watch --log) can be synced to it.
//   node tools/demo/shoot.mjs --url <url> --out docs/demo/capture/<name> [--secs 420] [--until "<text in page>"]
//        [--w 1280 --h 720 --dpr 1.5] [--keys "12.0:Space,15.5:Space"] [--fps 60] [--mobile]
// Output: <out>.mp4, <out>.json ({startMs, endMs, frames, url}).
import { createRequire } from 'node:module';
import { mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { homedir } from 'node:os';
const require = createRequire(join(homedir(), '.claude/skills/cap-director/package.json'));
const { chromium } = require('playwright-core');
const opt = (k, d) => { const i = process.argv.indexOf('--' + k); return i > 0 ? process.argv[i + 1] : d; };
const URL_ = opt('url'), OUT = opt('out'), SECS = Number(opt('secs', 420)), UNTIL = opt('until', null);
const W = Number(opt('w', 1280)), H = Number(opt('h', 720)), DPR = Number(opt('dpr', 1.5)), FPS = Number(opt('fps', 60));
// --keys "t:Key[:holdMs],…" (seconds from the start); --keyloop "Key:holdMs:everyMs:fromS:toS" repeats a press
const KEYS = (opt('keys', '') || '').split(',').filter(Boolean).map((s) => { const [t, k, h] = s.split(':'); return { t: Number(t) * 1000, k, h: Number(h || 0) }; });
for (const l of (opt('keyloop', '') || '').split(',').filter(Boolean)) { const [k, h, ev, a, b] = l.split(':'); for (let t = Number(a) * 1000; t < Number(b) * 1000; t += Number(ev)) KEYS.push({ t, k, h: Number(h) }); }
const mobile = process.argv.includes('--mobile');
// --rules file.json: [{"when": "regex on page text", "js": "code run in the page", "delay": ms}] each fired once, in order of appearance
const RULES = opt('rules', null) ? JSON.parse((await import('node:fs')).readFileSync(opt('rules'), 'utf8')) : [];
const fired = [];
if (!URL_ || !OUT) { console.error('need --url and --out'); process.exit(2); }
const dir = OUT + '.frames'; rmSync(dir, { recursive: true, force: true }); mkdirSync(dir, { recursive: true });
const browser = await chromium.launch({ executablePath: '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome', headless: false,
  args: [`--window-size=${W + 20},${H + 140}`, '--autoplay-policy=no-user-gesture-required', '--disable-features=CalculateNativeWinOcclusion', '--disable-background-timer-throttling', '--disable-renderer-backgrounding', '--disable-backgrounding-occluded-windows'] });
const ctx = await browser.newContext({ viewport: { width: W, height: H }, deviceScaleFactor: DPR, isMobile: mobile, hasTouch: mobile });
const page = await ctx.newPage();
const cdp = await ctx.newCDPSession(page);
const frames = [];
cdp.on('Page.screencastFrame', async (f) => {
  const i = frames.length;
  frames.push(f.metadata.timestamp);
  writeFileSync(join(dir, String(i).padStart(6, '0') + '.jpg'), Buffer.from(f.data, 'base64'));
  cdp.send('Page.screencastFrameAck', { sessionId: f.sessionId }).catch(() => {});
});
await page.goto(URL_);
await cdp.send('Page.startScreencast', { format: 'jpeg', quality: 90, maxWidth: Math.round(W * DPR), maxHeight: Math.round(H * DPR), everyNthFrame: 1 });
const startMs = Date.now();
console.log(JSON.stringify({ ev: 'recording', startMs, url: page.url() }));
const keyTimers = KEYS.map((k) => setTimeout(() => (k.h ? page.keyboard.down(k.k).then(() => new Promise((r) => setTimeout(r, k.h))).then(() => page.keyboard.up(k.k)) : page.keyboard.press(k.k)).catch(() => {}), k.t));
for (;;) {
  const el = Date.now() - startMs;
  if (el > SECS * 1000) break;
  for (const [i, r] of RULES.entries()) {
    if (fired[i]) continue;
    const hit = await page.evaluate((w) => new RegExp(w).test(document.body.innerText), r.when).catch(() => false);
    if (hit) { fired[i] = true; setTimeout(() => page.evaluate(r.js).then((v) => console.log(JSON.stringify({ ev: 'rule', i, at: Date.now() - startMs, v: String(v).slice(0, 200) }))).catch((e) => console.log(JSON.stringify({ ev: 'rule_err', i, e: String(e).slice(0, 200) }))), r.delay ?? 0); }
  }
  if (UNTIL && (await page.evaluate((u) => document.body.innerText.includes(u), UNTIL).catch(() => false))) break;
  await new Promise((r) => setTimeout(r, 1000));
}
keyTimers.forEach(clearTimeout);
await cdp.send('Page.stopScreencast').catch(() => {});
const endMs = Date.now(), finalUrl = page.url();
await browser.close();
// ffmpeg concat with each frame's real duration → constant frame rate
const lines = [];
for (let i = 0; i < frames.length; i++) {
  const d = i + 1 < frames.length ? Math.max(0.001, frames[i + 1] - frames[i]) : 1 / FPS;
  lines.push(`file '${String(i).padStart(6, '0')}.jpg'`, `duration ${d.toFixed(4)}`);
}
lines.push(`file '${String(frames.length - 1).padStart(6, '0')}.jpg'`);
writeFileSync(join(dir, 'list.txt'), lines.join('\n'));
execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-f', 'concat', '-safe', '0', '-i', join(dir, 'list.txt'), '-vf', `fps=${FPS},scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p`, '-c:v', 'libx264', '-crf', '17', '-preset', 'medium', OUT + '.mp4'], { stdio: 'inherit' });
const meta = { url: URL_, finalUrl, startMs, endMs, firstFrameTs: frames[0], frames: frames.length, avgFps: frames.length / ((frames.at(-1) - frames[0]) || 1) };
writeFileSync(OUT + '.json', JSON.stringify(meta, null, 1));
rmSync(dir, { recursive: true, force: true });
console.log(JSON.stringify({ ev: 'done', ...meta, mp4: OUT + '.mp4' }));
