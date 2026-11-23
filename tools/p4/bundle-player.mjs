// P4 task 7: a "browser burner" in Node — the real browser bundle (client-unity/Assets/StreamingAssets/fallrush-chain.js,
// packages/chain) with its own burner + session key (localStorage in a key file), driving the economy exactly as the
// WebGL client does (FallRushChain.econ / session / join). Roles:
//   bettor     bets on every market when its window opens (program state), session-key signed when possible
//   spectator  buys Chaos Drops during the on-chain Final's RUSH (DROPS=1,3 by default), 5 s apart
//   stand      joins the arena lobby as a human finalist and never sends an input (a guaranteed-human Final), bets too
// Writes docs/proof/p4/runs/<RUN>/player-<name>.jsonl and exits once the match's markets are paid (or on timeout).
//   node tools/p4/bundle-player.mjs --net devnet --name A --role bettor --run <RUN> [--fund-from ~/.config/solana/id.json]
import { readFileSync, mkdirSync, appendFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { homedir } from 'node:os';
import { loadBundle, fileStorage } from '../p3/chain-relay.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = join(HERE, '../..');
const arg = (k, d) => { const i = process.argv.indexOf('--' + k); return i > 0 ? process.argv[i + 1] : d; };
const NET = arg('net', 'devnet'), NAME = arg('name', 'A'), ROLE = arg('role', 'bettor'), RUN = arg('run', `run-${Date.now()}`);
const FUND = (arg('fund-from', '~/.config/solana/id.json') || '').replace(/^~(?=\/)/, homedir());
const DROPS = (arg('drops', '1,3')).split(',').map(Number);
const TIMEOUT_MS = Number(arg('timeout-s', '2700')) * 1000;
const OUT = join(ROOT, `docs/proof/p4/runs/${RUN}`);
mkdirSync(OUT, { recursive: true });
const LOG = join(OUT, `player-${NAME}.jsonl`);
const log = (o) => { appendFileSync(LOG, JSON.stringify({ t: Date.now(), name: NAME, role: ROLE, ...o }) + '\n'); };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const keys = join(HERE, '.keys', `${NET}-player-${NAME}.json`);
mkdirSync(dirname(keys), { recursive: true });
globalThis.localStorage = fileStorage(keys);
const chain = loadBundle();
const cfg = JSON.parse(readFileSync(join(ROOT, `packages/chain/config/${NET}.json`), 'utf8'));
const init = JSON.parse(await chain.init(JSON.stringify(cfg)));
if (!init.ok) { console.error('init failed', init.error); process.exit(1); }
log({ act: 'init', burner: init.burner, session: init.sessionPubkey });
console.log(`[${NAME}] burner ${init.burner}`);
// SOL for the base-layer setup (chips init + delegate, session token): ~0.01 SOL, from the admin wallet
const b = JSON.parse(await chain.devBalances());
if (b.ok && b.burner < 15_000_000 && FUND) {
  const kp = Uint8Array.from(JSON.parse(readFileSync(FUND, 'utf8')));
  const B58 = '123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz';
  let x = 0n; for (const v of kp) x = (x << 8n) | BigInt(v); let s = ''; while (x > 0n) { s = B58[Number(x % 58n)] + s; x /= 58n; } for (const v of kp) { if (v !== 0) break; s = '1' + s; }
  const r = JSON.parse(await chain.devFund(s, 20_000_000 - b.burner));
  kp.fill(0);
  log({ act: 'fund', ok: r.ok, sig: r.sig, lamports: r.lamports });
  await sleep(2500);
}
// devnet's public RPC 429s above ~1.5 tx/s: setup calls retried with backoff (players also start staggered)
const retry = async (what, f, n = 10) => { let r; for (let i = 0; i < n; i++) { r = JSON.parse(await f()); if (r.ok) return r; log({ act: what + '_retry', i, error: String(r.error).slice(0, 160) }); await sleep(2500 * (i + 1)); } return r; };
const sess = await retry('session', () => chain.session('create', JSON.stringify({})));
log({ act: 'session', ok: sess.ok, sig: sess.sig, token: sess.token, error: sess.error });
const chips = await retry('chips', () => chain.econ('chips', '{}'));
log({ act: 'chips', ok: chips.ok, created: chips.created, faucet: chips.faucet, balance: chips.chips?.balance, error: chips.error });
console.log(`[${NAME}] chips ${chips.ok ? chips.chips.balance : chips.error}`);

const st = async () => JSON.parse(await chain.econ('state', '{}'));
const bet = async (k, outcome, amount) => {
  const r = JSON.parse(await chain.econ('bet', JSON.stringify({ k, outcome, amount })));
  log({ act: 'bet', k, outcome, amount, ok: r.ok, sig: r.sig, session: r.session, ms: r.ms, error: r.error });
  console.log(`[${NAME}] bet k${k} slot ${outcome} ${amount}: ${r.ok ? 'ok' : r.error} ${r.session ? '(session key)' : ''}`);
  return r.ok;
};
const open = (s, k) => {
  const m = s.match, mk = s.markets?.[k], w = s.world;
  if (!m || !mk || mk.status !== 1 || mk.match !== m.pda) return false;
  const final = (m.flags & 0x40) !== 0, live = final && w && w.round === 4 && w.roundPhase < 3;
  if (m.status >= 5) return false;
  if (k === 0) return !(m.round >= 4 || final) || live;
  if (k === 1) return live;
  const r = k - 1;
  return r === 1 ? m.round === 0 && m.status <= 2 : m.round === r - 1 && m.status === 4;
};

let match = null, joined = null, joinP = null, lastDrop = -1000, drops = 0;
const did = new Set();
const t0 = Date.now();
for (;;) {
  if (Date.now() - t0 > TIMEOUT_MS) { log({ act: 'timeout' }); break; }
  await sleep(450);
  let s;
  try { s = await st(); } catch (e) { continue; }
  if (!s.ok || !s.match) continue;
  if (!match) {
    if (s.markets?.[0]?.status !== 1 || s.markets[0].match !== s.match.pda || s.match.status >= 5) continue;
    match = s.match.pda; log({ act: 'match', match }); console.log(`[${NAME}] match ${match}`);
  }
  if (s.match.pda !== match) break; // the next match
  const alive = s.match.slots.map((x, i) => (x === 1 || x === 2 ? i : -1)).filter((i) => i >= 0 && i < s.match.players);
  const salt = NAME.charCodeAt(0);
  // ---- stand: a human finalist that never moves (join once the lobby opens)
  if (ROLE === 'stand' && !joinP) {
    joinP = chain.join(JSON.stringify({ name: 'P4-' + NAME })).then((r) => { joined = JSON.parse(r); log({ act: 'join', ...joined }); console.log(`[${NAME}] joined slot ${joined.slot}`); });
  }
  // ---- bets: every market once, when its window opens
  if (ROLE !== 'spectator' && alive.length) {
    for (const k of [0, 2, 3, 4, 1]) {
      const tag = `${k}:${s.match.round}`;
      if (did.has(`k${k}`) || !open(s, k)) continue;
      did.add(`k${k}`);
      const own = joined && joined.slot < 12 && alive.includes(joined.slot) ? joined.slot : null;
      const pick = own ?? alive[(salt * 7 + k * 3) % alive.length];
      await bet(k, pick, [15, 12, 8, 6, 5][k] + (salt % 5));
      void tag;
    }
  }
  // ---- spectator: Chaos Drops during RUSH
  if (ROLE === 'spectator' && drops < DROPS.length && (s.match.flags & 0x40) && s.world?.round === 4 && s.world.roundPhase === 3 && s.world.tick >= lastDrop + 165) {
    const kind = DROPS[drops];
    const sweeperReady = s.world.tick - s.world.roundStart >= 600;
    if (kind === 4 && !sweeperReady) continue;
    const r = JSON.parse(await chain.econ('drop', JSON.stringify({ kind })));
    log({ act: 'drop', kind, tick: s.world.tick, ok: r.ok, sig: r.sig, session: r.session, ms: r.ms, error: r.error });
    console.log(`[${NAME}] drop ${kind} at tick ${s.world.tick}: ${r.ok ? 'ok' : r.error}`);
    if (r.ok) drops++;
    lastDrop = s.world.tick;
  }
  if (s.markets.every((m) => m && m.match === match && m.status === 3)) { log({ act: 'markets_paid', balance: s.chips?.balance }); break; }
}
const end = await st();
log({ act: 'end', balance: end.chips?.balance, won: end.chips?.won, spent: end.chips?.spent });
console.log(`[${NAME}] end balance ${end.chips?.balance}`);
chain.stop();
process.exit(0);
