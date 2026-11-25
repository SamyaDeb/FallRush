import { defineConfig } from 'vite';
import { createHash } from 'node:crypto';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

// harden #8: a content hash of public/chain (the chain bundle, its configs, rush_core.wasm; scripts/assets.mjs copies
// them before `vite build`) versions every /chain/ URL (?v=…), so a bundle / wasm / config fix reaches returning
// players at once instead of after the CDN's cache (measured 4 h). Unity's jslib reuses the bundle /play preloads.
function chainVersion(): string {
  const dir = join(__dirname, 'public/chain');
  if (!existsSync(dir)) return 'dev';
  const h = createHash('sha256');
  for (const f of readdirSync(dir).filter((f) => /\.(js|json|wasm)$/.test(f)).sort()) { h.update(f); h.update(readFileSync(join(dir, f))); }
  return h.digest('hex').slice(0, 12);
}

// Static multi-route app: nginx serves index.html for every route (try_files), /api → the indexer.
export default defineConfig({
  build: { target: 'es2020', outDir: 'dist', assetsInlineLimit: 0, chunkSizeWarningLimit: 600 },
  define: { __CHAIN_V__: JSON.stringify(chainVersion()) },
  server: { proxy: { '/api': process.env.API ?? 'http://127.0.0.1:3112' } },
});
