// P5 task 0.1 — "assets you can sell anytime", proven on devnet with the real browser bundle (fallrush-chain.js):
// a fresh burner gets faucet chips (session key, no popup) → cashout on the rollup (commits its ChipBalance to Solana)
// → mint_cashout on Solana (the program mints exactly the committed amount of the real SPL RUSH mint) → an SPL
// transfer of part of it to a second wallet. Every signature lands in docs/proof/p5/cashout-<run>.json.
//   node tools/p5/cashout.mjs [--amount 250] [--send 100] [--name CASH1]
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { homedir } from 'node:os';
import { Keypair } from '@solana/web3.js';
import { loadBundle, fileStorage } from '../p3/chain-relay.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = join(HERE, '../..');
const arg = (k, d) => { const i = process.argv.indexOf('--' + k); return i > 0 ? process.argv[i + 1] : d; };
const NAME = arg('name', `cash-${Date.now()}`), AMOUNT = Number(arg('amount', '250')), SEND = Number(arg('send', '100'));
const RUN = `p5-cashout-${new Date().toISOString().replace(/[:.]/g, '-')}`;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const out = { run: RUN, steps: [] };
const step = (what, o) => { out.steps.push({ what, t: new Date().toISOString(), ...o }); console.log(what, JSON.stringify(o).slice(0, 300)); };

globalThis.localStorage = fileStorage(join(HERE, '.keys', `devnet-${NAME}.json`));
const chain = loadBundle(join(ROOT, 'client-unity/Assets/StreamingAssets/fallrush-chain.js'));
const cfg = JSON.parse(readFileSync(join(ROOT, 'packages/chain/config/devnet.json'), 'utf8'));
const init = JSON.parse(await chain.init(JSON.stringify(cfg)));
if (!init.ok) throw new Error(init.error);
out.burner = init.burner;
const b = JSON.parse(await chain.devBalances());
if (b.burner < 25_000_000) {
  const kp = Uint8Array.from(JSON.parse(readFileSync(join(homedir(), '.config/solana/id.json'), 'utf8')));
  const B58 = '123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz';
  let x = 0n; for (const v of kp) x = (x << 8n) | BigInt(v); let s = ''; while (x > 0n) { s = B58[Number(x % 58n)] + s; x /= 58n; } for (const v of kp) { if (v !== 0) break; s = '1' + s; }
  const r = JSON.parse(await chain.devFund(s, 30_000_000 - b.burner));
  kp.fill(0);
  step('fund burner (admin → burner, 0.03 SOL)', { ok: r.ok, sig: r.sig });
  await sleep(3000);
}
const retry = async (what, f, n = 8) => { let r; for (let i = 0; i < n; i++) { r = JSON.parse(await f()); if (r.ok) return r; console.log(what, 'retry', r.error); await sleep(2500 * (i + 1)); } return r; };
const sess = await retry('session', () => chain.session('create', '{}'));
step('session key (SessionTokenV2)', { ok: sess.ok, sig: sess.sig, error: sess.error });
const chips = await retry('chips', () => chain.econ('chips', '{}'));
step('chips: init + delegate ChipBalance, faucet (session key)', { ok: chips.ok, created: chips.created, faucet: chips.faucet, balance: chips.chips?.balance, error: chips.error });
const rush0 = JSON.parse(await chain.econ('rush', '{}'));
step('RUSH SPL before', { amount: rush0.amount, ata: rush0.ata });
const t0 = Date.now();
let co = JSON.parse(await chain.econ('cashout', JSON.stringify({ amount: AMOUNT })));
for (let i = 0; !co.ok && co.stage !== 'rollup' && i < 4; i++) { await sleep(4000); co = JSON.parse(await chain.econ('cashout', JSON.stringify({ mintOnly: true }))); }
step(`cashout ${AMOUNT} RUSH: rollup cashout + commit → mint_cashout on Solana`, { ...co, wallMs: Date.now() - t0 });
let rush1;
for (let i = 0; i < 20; i++) { rush1 = JSON.parse(await chain.econ('rush', '{}')); if (BigInt(rush1.amount) > BigInt(rush0.amount)) break; await sleep(1500); }
step('RUSH SPL after (base units, 6 decimals)', { amount: rush1.amount, ata: rush1.ata, delta: (BigInt(rush1.amount) - BigInt(rush0.amount)).toString() });
const st = JSON.parse(await chain.econ('state', '{}'));
step('chips on the rollup after', { balance: st.chips?.balance, cashedOut: st.chips?.cashedOut });
const second = Keypair.generate().publicKey.toBase58();
const sr = JSON.parse(await chain.econ('sendRush', JSON.stringify({ to: second, amount: SEND })));
step(`SPL transfer ${SEND} RUSH → a second wallet`, { ...sr, to: second });
let r2;
for (let i = 0; i < 20; i++) { r2 = JSON.parse(await chain.econ('rush', JSON.stringify({ owner: second }))); if (BigInt(r2.amount) > 0n) break; await sleep(1500); }
const r3 = JSON.parse(await chain.econ('rush', '{}'));
step('balances after the transfer', { second: r2.amount, burner: r3.amount });
out.pass = co.ok && BigInt(rush1.amount) - BigInt(rush0.amount) === BigInt(AMOUNT) * 1_000_000n && BigInt(r2.amount) === BigInt(SEND) * 1_000_000n;
mkdirSync(join(ROOT, 'docs/proof/p5'), { recursive: true });
writeFileSync(join(ROOT, `docs/proof/p5/${RUN}.json`), JSON.stringify(out, null, 1));
console.log('PASS', out.pass, `docs/proof/p5/${RUN}.json`);
chain.stop();
process.exit(out.pass ? 0 : 1);
