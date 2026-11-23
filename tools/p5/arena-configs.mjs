// P5 arena pool: writes packages/chain/config/devnet-<arenaKey>.json for each pool arena (the devnet config with
// rollup.p2.arenaId + addresses.arena/world of that arena). The bundle build copies them to StreamingAssets/chain.
//   node tools/p5/arena-configs.mjs 2 3
import { readFileSync, writeFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { PublicKey } from '@solana/web3.js';
const ROOT = join(dirname(fileURLToPath(import.meta.url)), '../..');
const base = JSON.parse(readFileSync(join(ROOT, 'packages/chain/config/devnet.json'), 'utf8'));
const prog = new PublicKey(base.addresses.program);
for (const id of process.argv.slice(2).map(Number)) {
  const arena = PublicKey.findProgramAddressSync([Buffer.from('arena'), Buffer.from([id])], prog)[0];
  const world = PublicKey.findProgramAddressSync([Buffer.from('world'), arena.toBuffer()], prog)[0];
  const c = structuredClone(base);
  c.name = `devnet (P5 pool, arena ${id})`;
  c.addresses = { ...c.addresses, arena: arena.toBase58(), world: world.toBase58() };
  c.rollup.p2 = { ...(c.rollup.p2 ?? {}), arenaId: id };
  writeFileSync(join(ROOT, `packages/chain/config/devnet-${arena.toBase58()}.json`), JSON.stringify(c, null, 2) + '\n');
  console.log(id, arena.toBase58(), world.toBase58());
}
