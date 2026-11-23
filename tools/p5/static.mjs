// Local stand-in for fallrush-web (nginx-web.conf): the built web app (web/app/dist incl. /unity) with Unity's
// pre-compressed .br headers, SPA fallback to index.html, /api → the indexer.
//   node tools/p5/static.mjs [port=4173] [api=http://127.0.0.1:3912]
import { createServer, request } from 'node:http';
import { createReadStream, existsSync, statSync } from 'node:fs';
import { join, extname, normalize } from 'node:path';
const ROOT = new URL('../../web/app/dist/', import.meta.url).pathname;
const PORT = Number(process.argv[2] ?? 4173), API = new URL(process.argv[3] ?? 'http://127.0.0.1:3912');
const TYPES = { '.html': 'text/html', '.js': 'application/javascript', '.css': 'text/css', '.json': 'application/json', '.wasm': 'application/wasm', '.png': 'image/png', '.mapblob': 'application/octet-stream', '.data': 'application/octet-stream' };
createServer((req, res) => {
  const u = new URL(req.url, 'http://x');
  if (u.pathname.startsWith('/api/')) {
    const p = request({ host: API.hostname, port: API.port, path: req.url, method: req.method, headers: { ...req.headers, 'x-real-ip': req.socket.remoteAddress } }, (r) => { res.writeHead(r.statusCode, r.headers); r.pipe(res); });
    p.on('error', () => { res.writeHead(502); res.end(); });
    return void req.pipe(p);
  }
  let f = normalize(join(ROOT, decodeURIComponent(u.pathname)));
  if (!f.startsWith(ROOT) || !existsSync(f) || statSync(f).isDirectory()) f = join(ROOT, 'index.html');
  const h = {};
  if (f.endsWith('.br')) { h['content-encoding'] = 'br'; h['content-type'] = TYPES[extname(f.slice(0, -3))] ?? 'application/octet-stream'; }
  else h['content-type'] = TYPES[extname(f)] ?? 'application/octet-stream';
  res.writeHead(200, h); createReadStream(f).pipe(res);
}).listen(PORT, () => console.log(`static :${PORT} → ${ROOT}, /api → ${API.href}`));
