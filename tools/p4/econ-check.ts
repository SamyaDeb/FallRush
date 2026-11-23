// P4 task 7.3: every payout = a hand calculation.
//   snapshot <run> <pubkey…>   ChipBalance.won_total of those wallets before the match → docs/proof/p4/runs/<run>/won-before.json
//   verify <run> <match>       for the match's 5 markets (rollup): per bet stake · pool / winning stake (rounded down) or the
//                              refund; checks market.paid_total = Σ, every bet paid, and each snapshotted wallet's won_total
//                              delta = Σ its payouts → docs/proof/p4/runs/<run>/payout-check.json; exit 1 on a mismatch.
//   P2_NET=devnet ARENA=2 node_modules/.bin/tsx tools/p4/econ-check.ts …
import { PublicKey } from "@solana/web3.js";
import { writeFileSync, readFileSync, mkdirSync, existsSync } from "node:fs";
import { join } from "node:path";
import { ROOT } from "../keeper/lib.ts";
import * as E from "../keeper/econ.ts";

const [cmd, run, ...rest] = process.argv.slice(2);
const ARENA = Number(process.env.ARENA ?? 2);
const dir = join(ROOT, `docs/proof/p4/runs/${run}`);
mkdirSync(dir, { recursive: true });
const big = (o: unknown) => JSON.stringify(o, (_k, v) => (typeof v === "bigint" ? v.toString() : v instanceof PublicKey ? v.toBase58() : v), 1);

if (cmd === "snapshot") {
  const out: Record<string, string> = {};
  for (const w of rest) { const c = await E.chipsOf(new PublicKey(w)); out[w] = (c?.wonTotal ?? 0n).toString(); }
  writeFileSync(join(dir, "won-before.json"), big(out));
  console.log(`snapshot ${Object.keys(out).length} wallets`);
  process.exit(0);
}
if (cmd !== "verify") { console.error("snapshot|verify"); process.exit(2); }
const match = new PublicKey(rest[0]);
const before: Record<string, string> = existsSync(join(dir, "won-before.json")) ? JSON.parse(readFileSync(join(dir, "won-before.json"), "utf8")) : {};
const owed: Record<string, bigint> = {};
const markets: any[] = [];
let ok = true;
for (let k = 0; k < E.MARKETS; k++) {
  const m = await E.market(ARENA, k);
  if (!m || !m.match.equals(match)) { markets.push({ k, missing: true }); continue; }
  let sum = 0n;
  const bets = m.bets.map((b) => {
    const win = (m.winners >> b.outcome) & 1;
    const pay = m.refund ? b.amount : win && m.winningStake > 0n ? (b.amount * m.pool) / m.winningStake : 0n;
    sum += pay;
    owed[b.wallet.toBase58()] = (owed[b.wallet.toBase58()] ?? 0n) + pay;
    return { wallet: b.wallet.toBase58(), outcome: b.outcome, stake: b.amount, win: !!win, pay, paid: b.paid };
  });
  const allPaid = bets.every((b) => b.paid === 1);
  const good = m.status === 3 && allPaid && m.paidTotal === sum && sum <= m.pool;
  ok &&= good;
  markets.push({ k, kind: m.kind, round: m.round, label: m.label, refund: m.refund, pool: m.pool, winners: m.winners, winningStake: m.winningStake, paidTotal: m.paidTotal, handSum: sum, dust: m.pool - sum, allPaid, ok: good, bets });
}
const wallets: any[] = [];
for (const [w, b0] of Object.entries(before)) {
  const c = await E.chipsOf(new PublicKey(w));
  const delta = (c?.wonTotal ?? 0n) - BigInt(b0);
  const want = owed[w] ?? 0n;
  const good = delta === want;
  ok &&= good;
  wallets.push({ wallet: w, wonDelta: delta, handCalc: want, ok: good });
}
writeFileSync(join(dir, "payout-check.json"), big({ match: match.toBase58(), ok, markets, wallets }));
console.log(`payout check ${match.toBase58()}: ${ok ? "OK" : "MISMATCH"} — markets ${markets.filter((m) => m.ok).length}/${markets.filter((m) => !m.missing).length}, wallets ${wallets.filter((w) => w.ok).length}/${wallets.length}`);
process.exit(ok ? 0 : 1);
