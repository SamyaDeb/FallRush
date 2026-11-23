// P4: "Claim with Phantom" through the real bundle (the same econ('claim') the in-game button calls): the burner that
// owns a Core asset transfers it to a wallet (here a stand-in for the connected Phantom).
//   node tools/p4/claim.mjs --name S1 --asset <pubkey> --to <pubkey> [--net devnet]
import { readFileSync, appendFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { loadBundle, fileStorage } from '../p3/chain-relay.mjs';
const HERE = dirname(fileURLToPath(import.meta.url)), ROOT = join(HERE, '../..');
const arg = (k, d) => { const i = process.argv.indexOf('--' + k); return i > 0 ? process.argv[i + 1] : d; };
const NET = arg('net', 'devnet'), NAME = arg('name', 'S1');
globalThis.localStorage = fileStorage(join(HERE, '.keys', `${NET}-player-${NAME}.json`));
const chain = loadBundle();
const init = JSON.parse(await chain.init(readFileSync(join(ROOT, `packages/chain/config/${NET}.json`), 'utf8')));
const r = JSON.parse(await chain.econ('claim', JSON.stringify({ asset: arg('asset'), to: arg('to') })));
const out = { t: new Date().toISOString(), burner: init.burner, asset: arg('asset'), to: arg('to'), ...r };
console.log(JSON.stringify(out));
appendFileSync(join(ROOT, 'docs/proof/p4/claims.jsonl'), JSON.stringify(out) + '\n');
chain.stop(); process.exit(r.ok ? 0 : 1);
