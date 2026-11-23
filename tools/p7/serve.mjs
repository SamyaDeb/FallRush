#!/usr/bin/env node
// P7: serves a web/app dist locally the way the VPS nginx does (SPA fallback, Range requests, the Unity build's
// .unityweb files as plain bytes) and proxies /api/* to a live indexer, so a new WebGL + web build plays a real public
// hybrid match from this Mac before it is deployed (after-screenshots from a player build, never the Editor).
//   node tools/p7/serve.mjs --dist web/app/dist --api https://play.<VPS_IP-dashed>.sslip.io --port 5180
import { createServer } from 'node:http';
import { createReadStream, statSync, existsSync } from 'node:fs';
import { join, extname, normalize } from 'node:path';
const opt = (k, d) => { const i = process.argv.indexOf('--' + k); return i > 0 ? process.argv[i + 1] : d; };
const DIST = opt('dist', 'web/app/dist'), API = opt('api', ''), PORT = Number(opt('port', '5180'));
const TYPES = { '.html': 'text/html; charset=utf-8', '.js': 'application/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.wasm': 'application/wasm', '.svg': 'image/svg+xml', '.mapblob': 'application/octet-stream', '.unityweb': 'application/octet-stream' };
createServer(async (req, res) => {
  const url = new URL(req.url, 'http://x');
  if (url.pathname.startsWith('/api/')) {
    try {
      const body = req.method === 'POST' ? await new Promise((ok) => { const c = []; req.on('data', (d) => c.push(d)); req.on('end', () => ok(Buffer.concat(c))); }) : undefined;
      const r = await fetch(API + url.pathname + url.search, { method: req.method, headers: { 'content-type': req.headers['content-type'] ?? 'application/json' }, body });
      res.writeHead(r.status, { 'content-type': r.headers.get('content-type') ?? 'application/json', 'cache-control': 'no-store' });
      res.end(Buffer.from(await r.arrayBuffer()));
    } catch (e) { res.writeHead(502); res.end(String(e)); }
    return;
  }
  let p = normalize(join(DIST, decodeURIComponent(url.pathname)));
  if (!p.startsWith(normalize(DIST)) || !existsSync(p) || statSync(p).isDirectory()) p = join(DIST, 'index.html');
  const st = statSync(p), type = TYPES[extname(p)] ?? 'application/octet-stream';
  const range = req.headers.range?.match(/bytes=(\d+)-(\d*)/);
  const head = { 'content-type': type, 'accept-ranges': 'bytes', 'cache-control': p.includes('/unity/') ? 'public, max-age=3600' : 'no-cache' };
  if (range) {
    const a = Number(range[1]), b = range[2] ? Number(range[2]) : st.size - 1;
    res.writeHead(206, { ...head, 'content-range': `bytes ${a}-${b}/${st.size}`, 'content-length': b - a + 1 });
    if (req.method === 'HEAD') return res.end();
    createReadStream(p, { start: a, end: b }).pipe(res);
    return;
  }
  res.writeHead(200, { ...head, 'content-length': st.size });
  if (req.method === 'HEAD') return res.end();
  createReadStream(p).pipe(res);
}).listen(PORT, '127.0.0.1', () => console.log(`[serve] http://127.0.0.1:${PORT} dist ${DIST} api ${API ? '(proxied)' : '-'}`));
