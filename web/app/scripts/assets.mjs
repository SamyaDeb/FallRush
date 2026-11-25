// Copies the shared chain bundle, its configs, rush_core.wasm and the map blobs into public/ (same files the Unity
// build ships in StreamingAssets), so the web pages decode and sign exactly like the game.
import { cpSync, mkdirSync, readdirSync, writeFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
const app = join(dirname(fileURLToPath(import.meta.url)), '..');
const root = join(app, '../..');
const sa = join(root, 'client-unity/Assets/StreamingAssets');
mkdirSync(join(app, 'public/chain'), { recursive: true });
cpSync(join(sa, 'fallrush-chain.js'), join(app, 'public/chain/fallrush-chain.js'));
// fullchain: RUSH_CORE_WASM=<path> overrides the Unity copy (e.g. crates/target/wasm32-unknown-unknown/release/rush_core_wasm.wasm)
cpSync(process.env.RUSH_CORE_WASM || join(sa, 'rush_core.wasm'), join(app, 'public/chain/rush_core.wasm'));
cpSync(join(sa, 'fallrush-tee.js'), join(app, 'public/chain/fallrush-tee.js')); // P6: TEE attestation (on demand)
for (const f of readdirSync(join(sa, 'chain')).filter((f) => f.endsWith('.json'))) cpSync(join(sa, 'chain', f), join(app, 'public/chain', f));
mkdirSync(join(app, 'public/maps'), { recursive: true });
// fullchain: every round map (the replay / live view of a full on-chain match follows the World's map)
for (const m of ['BlockPartyDash', 'GooTower', 'StarScramble', 'CrumbleShelf', 'Lobby']) cpSync(join(root, `maps/${m}.mapblob`), join(app, `public/maps/${m}.mapblob`));
// live board: the static map art of rounds 1–3 (scripts/bake-boards.mjs)
await import('./bake-boards.mjs').then(({ bake }) => { for (const m of ['BlockPartyDash', 'GooTower', 'StarScramble']) writeFileSync(join(app, `public/maps/${m}.board.json`), JSON.stringify(bake(m))); });
import('node:child_process').then(({ execFileSync }) => {
  try { execFileSync('sips', ['-Z', '256', join(root, 'docs/media/logo.png'), '--out', join(app, 'public/logo.png')], { stdio: 'ignore' }); }
  catch { cpSync(join(root, 'docs/media/logo.png'), join(app, 'public/logo.png')); }
});
console.log('assets copied');
