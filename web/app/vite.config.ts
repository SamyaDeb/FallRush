import { defineConfig } from 'vite';
// Static multi-route app: nginx serves index.html for every route (try_files), /api → the indexer.
export default defineConfig({
  build: { target: 'es2020', outDir: 'dist', assetsInlineLimit: 0, chunkSizeWarningLimit: 600 },
  server: { proxy: { '/api': process.env.API ?? 'http://127.0.0.1:3112' } },
});
