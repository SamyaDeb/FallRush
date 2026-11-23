// Copies the shared chain bundle, its configs, rush_core.wasm and the map blobs into public/ (same files the Unity
// build ships in StreamingAssets), so the web pages decode and sign exactly like the game.
import { cpSync, mkdirSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
const app = join(dirname(fileURLToPath(import.meta.url)), '..');
const root = join(app, '../..');
const sa = join(root, 'client-unity/Assets/StreamingAssets');
mkdirSync(join(app, 'public/chain'), { recursive: true });
cpSync(join(sa, 'fallrush-chain.js'), join(app, 'public/chain/fallrush-chain.js'));
cpSync(join(sa, 'rush_core.wasm'), join(app, 'public/chain/rush_core.wasm'));
for (const f of readdirSync(join(sa, 'chain')).filter((f) => f.endsWith('.json'))) cpSync(join(sa, 'chain', f), join(app, 'public/chain', f));
mkdirSync(join(app, 'public/maps'), { recursive: true });
for (const m of ['CrumbleShelf', 'Lobby']) cpSync(join(root, `maps/${m}.mapblob`), join(app, `public/maps/${m}.mapblob`));
import('node:child_process').then(({ execFileSync }) => {
  try { execFileSync('sips', ['-Z', '256', join(root, 'Fall Rush  Logo.png'), '--out', join(app, 'public/logo.png')], { stdio: 'ignore' }); }
  catch { cpSync(join(root, 'Fall Rush  Logo.png'), join(app, 'public/logo.png')); }
});
console.log('assets copied');
