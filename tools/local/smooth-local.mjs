// Local check of a WebGL build against the live game: serves /unity/* from a local build and proxies everything else
// (the /play page, /chain bundle, /api: judge, matchmaking, status) to https://fallrush.fun. The page then joins the
// production game servers and devnet arenas with the local client. No dependencies.
//   node tools/local/smooth-local.mjs [port] [buildDir]
import http from 'node:http';
import https from 'node:https';
import { createReadStream, existsSync, statSync, readdirSync, writeFileSync } from 'node:fs';
import { join, dirname, normalize } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';

const root = join(dirname(fileURLToPath(import.meta.url)), '../..');
const port = Number(process.argv[2] ?? process.env.PORT ?? 5190);
const build = process.argv[3] ?? join(root, 'client-unity/Builds/WebGL');
const UP = 'fallrush.fun';

// build.json like tools/p5/build-json.py (names + parallel + version)
const bd = join(build, 'Build');
const fs = readdirSync(bd).filter((f) => f !== 'build.json');
const pick = (s) => fs.find((f) => f.endsWith(s));
const h = createHash('sha1');
for (const f of [...fs].sort()) { const st = statSync(join(bd, f)); h.update(`${f}:${st.size}:${Math.floor(st.mtimeMs / 1000)}`); }
const bj = { loader: pick('.loader.js'), data: pick('.data.unityweb'), framework: pick('.framework.js.unityweb'), code: pick('.wasm.unityweb'), parallel: true, v: 'local-' + h.digest('hex').slice(0, 8) };
writeFileSync(join(bd, 'build.json'), JSON.stringify(bj));

const types = { '.js': 'application/javascript', '.json': 'application/json', '.wasm': 'application/wasm', '.unityweb': 'application/octet-stream', '.bytes': 'application/octet-stream' };

http.createServer((req, res) => {
  const url = new URL(req.url, 'http://x');
  if (url.pathname.startsWith('/unity/')) {
    const p = normalize(join(build, url.pathname.slice('/unity/'.length)));
    if (!p.startsWith(build) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
    const ext = p.slice(p.lastIndexOf('.'));
    res.writeHead(200, { 'Content-Type': types[ext] ?? 'application/octet-stream', 'Cache-Control': 'no-store' });
    createReadStream(p).pipe(res);
    return;
  }
  const headers = { ...req.headers, host: UP };
  delete headers['accept-encoding']; // plain bytes back (no recompression here)
  delete headers.origin;
  delete headers.referer;
  const up = https.request({ host: UP, port: 443, method: req.method, path: req.url, headers }, (r) => {
    const out = { ...r.headers };
    delete out['content-security-policy'];
    delete out['strict-transport-security'];
    res.writeHead(r.statusCode ?? 502, out);
    r.pipe(res);
  });
  up.on('error', (e) => { res.writeHead(502); res.end(String(e)); });
  req.pipe(up);
}).listen(port, '127.0.0.1', () => console.log(`smooth-local: http://localhost:${port}/play  (unity ${bj.v} from ${build}; rest from https://${UP})`));
