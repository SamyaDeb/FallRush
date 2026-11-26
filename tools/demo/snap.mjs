#!/usr/bin/env node
// Screenshot a page in real Chrome (Playwright). Clicks a "Connect" button if Solana Explorer asks to connect to a
// custom cluster.  node tools/demo/snap.mjs <url> <out.png> [waitMs] [w] [h]
import { createRequire } from 'node:module';
import { join } from 'node:path';
import { homedir } from 'node:os';
const require = createRequire(join(homedir(), '.claude/skills/cap-director/package.json'));
const { chromium } = require('playwright-core');
const [url, out, wait = '9000', w = '1280', h = '800'] = process.argv.slice(2);
const b = await chromium.launch({ executablePath: '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome', headless: false });
const p = await (await b.newContext({ viewport: { width: +w, height: +h }, deviceScaleFactor: 2 })).newPage();
await p.goto(url);
await p.waitForTimeout(3000);
const c = p.getByRole('button', { name: /connect/i });
if (!url.includes('cluster=devnet') && await c.count().catch(() => 0)) { await c.first().click().catch(() => {}); }
await p.waitForTimeout(+wait);
await p.screenshot({ path: out });
console.log('saved', out);
await b.close();
