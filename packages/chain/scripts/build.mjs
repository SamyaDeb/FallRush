// Bundle src/index.ts → client-unity/Assets/StreamingAssets/fallrush-chain.js (one minified IIFE).
import { build } from 'esbuild';
import { gzipSync } from 'node:zlib';
import { readFileSync, mkdirSync, readdirSync, copyFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const pkg = resolve(here, '..');
const out = resolve(pkg, '../../client-unity/Assets/StreamingAssets/fallrush-chain.js');
mkdirSync(dirname(out), { recursive: true });

const r = await build({
  entryPoints: [resolve(pkg, 'src/index.ts')],
  outfile: out,
  bundle: true,
  format: 'iife',
  platform: 'browser',
  target: ['es2020'],
  minify: true,
  legalComments: 'none',
  inject: [resolve(pkg, 'src/buffer-shim.ts')],
  define: { 'process.env.NODE_ENV': '"production"', global: 'globalThis' },
  metafile: true,
  logLevel: 'warning',
});

// P6: the TEE attestation module (on demand; @phala/dcap-qvl from tools/p6/node_modules), next to the bundle
const teeOut = resolve(dirname(out), 'fallrush-tee.js');
const p6mods = resolve(pkg, '../../tools/p6/node_modules');
const empty = { name: 'empty', setup(b) { b.onResolve({ filter: /^(crypto|node-fetch)$/ }, () => ({ path: 'empty', namespace: 'empty' })); b.onLoad({ filter: /.*/, namespace: 'empty' }, () => ({ contents: 'module.exports = {}' })); } };
await build({
  entryPoints: [resolve(pkg, 'src/tee-attest.ts')], outfile: teeOut, bundle: true, format: 'iife', platform: 'browser', target: ['es2020'], minify: true,
  legalComments: 'none', nodePaths: [p6mods], plugins: [empty], inject: [resolve(pkg, 'src/buffer-shim.ts')], define: { 'process.env.NODE_ENV': '"production"', global: 'globalThis' }, logLevel: 'warning',
});
console.log(`fallrush-tee.js: ${readFileSync(teeOut).length} B raw, ${gzipSync(readFileSync(teeOut), { level: 9 }).length} B gzip`);

// configs next to the bundle: StreamingAssets/chain/<name>.json
const cfgOut = resolve(dirname(out), 'chain');
mkdirSync(cfgOut, { recursive: true });
for (const f of readdirSync(resolve(pkg, 'config'))) if (f.endsWith('.json')) copyFileSync(resolve(pkg, 'config', f), resolve(cfgOut, f));

const js = readFileSync(out);
const gz = gzipSync(js, { level: 9 });
console.log(`fallrush-chain.js: ${js.length} B raw, ${gz.length} B gzip → ${out}`);
if (process.argv.includes('--meta')) {
  const ins = Object.entries(r.metafile.outputs)[0][1].inputs;
  const byPkg = {};
  for (const [f, v] of Object.entries(ins)) {
    const m = f.match(/node_modules\/(?:\.pnpm\/[^/]+\/node_modules\/)?((?:@[^/]+\/)?[^/]+)/);
    const k = m ? m[1] : 'src';
    byPkg[k] = (byPkg[k] || 0) + v.bytesInOutput;
  }
  console.log(Object.entries(byPkg).sort((a, b) => b[1] - a[1]).map(([k, v]) => `${k} ${v}`).join('\n'));
}
