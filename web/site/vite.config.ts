import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';
import type { Plugin } from 'vite';

/** Build only: <link rel="modulepreload"> for the hero scene chunk and its imports (three.js), so the 3D
 *  download starts with the HTML instead of after the main script runs. */
function preload3d(): Plugin {
  return {
    name: 'preload-3d',
    apply: 'build',
    transformIndexHtml: {
      order: 'post',
      handler(html, ctx) {
        const bundle = ctx.bundle;
        if (!bundle) return html;
        const hero = Object.values(bundle).find((c) => c.type === 'chunk' && c.facadeModuleId?.endsWith('/three/HeroScene.tsx'));
        if (!hero || hero.type !== 'chunk') return html;
        const files = [hero.fileName, ...hero.imports].filter((f) => !html.includes(f));
        const tags = files.map((f) => `<link rel="modulepreload" crossorigin href="/${f}">`).join('\n    ');
        return html.replace('</head>', `    ${tags}\n  </head>`);
      },
    },
  };
}

// In dev, /api, /r and /p go to the waitlist worker (`npm run worker`, wrangler dev on :8787),
// the same split Cloudflare does in production (worker routes on the site's domain).
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '');
  // index.html uses %VITE_SITE_URL% for absolute og: URLs
  process.env.VITE_SITE_URL = (env.VITE_SITE_URL || 'https://your-site.com').replace(/\/$/, '');
  const worker = env.WORKER_DEV_URL || 'http://localhost:8787';
  // The dev proxy forwards to wrangler dev on another port; drop Origin so the worker's same-origin check treats it as
  // same-site (in production the site and the worker share one hostname, so no proxy is involved).
  const proxy = Object.fromEntries(
    ['/api', '/r/', '/p/'].map((p) => [
      p,
      { target: worker, changeOrigin: false, configure: (px: { on: (ev: 'proxyReq', fn: (req: { removeHeader: (h: string) => void }) => void) => void }) => px.on('proxyReq', (req) => req.removeHeader('origin')) },
    ]),
  );
  return {
    plugins: [react(), preload3d()],
    server: { port: 5173, strictPort: true, proxy },
    preview: { port: 4173, proxy },
    build: { target: 'es2022', chunkSizeWarningLimit: 1200 },
  };
});