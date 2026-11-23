// P7 A3.2 chaos: revoke a session key mid-match. A bettor (own burner) bets with its session key, revokes the session
// on Solana, bets again, then creates a new session and bets once more. Records which signer each bet used and the
// confirm times. Waits for an open market on ARENA.
//   cd tools/keeper && P2_NET=devnet ARENA=2 npx tsx ../p7/session-revoke.ts <out.json>
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { createChain } from "../../packages/chain/src/api.ts";
import { memoryKV } from "../../packages/chain/src/storage.ts";
const ROOT = new URL("../../", import.meta.url).pathname;
const ARENA = Number(process.env.ARENA ?? 2);
const KEYS: Record<number, string> = { 2: "HbinPthhN3g5BRQRL4aKQ75HyQXMofhsUbg2c28bJXHt", 3: "E9XE7vtQgrWnXLcGMn5FYXAFLb6GmBjrKcZq5wgPcEbG" };
const J = async (p: Promise<string>) => JSON.parse(await p);
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
// the program's betting windows (mirrors ChainEconomy.MarketOpen)
const open = (st: any, k: number) => {
  const m = st.match, w = st.world, mk = st.markets?.[k];
  if (!m || !mk || mk.status !== 1 || mk.match !== m.pda || m.status >= 5) return false;
  const final = (m.flags & 0x40) !== 0, live = final && w && w.round === 4 && w.roundPhase < 3;
  if (k === 1) return !!live;
  const r = k - 1;
  return r === 1 ? m.round === 0 && m.status <= 2 : m.round === r - 1 && m.status === 4;
};
const saved: string[] = JSON.parse(readFileSync(join(ROOT, "tools/keeper/.keys/devnet-p7-bettors.json"), "utf8"));
const chain: any = createChain({ kv: memoryKV() });
await chain.init(readFileSync(join(ROOT, `packages/chain/config/devnet-${KEYS[ARENA]}.json`), "utf8"));
chain.importBurner(saved[ARENA === 2 ? 0 : 1]);
const R: any = { arena: ARENA, at: new Date().toISOString(), steps: [] };
const step = async (name: string, f: () => Promise<any>) => { const t0 = Date.now(); const r = await f().catch((e) => ({ ok: false, error: String(e) })); R.steps.push({ name, ok: r.ok, session: r.session, ms: Date.now() - t0, sig: r.sig, error: r.error ? String(r.error).slice(0, 160) : undefined }); console.log(name, JSON.stringify(R.steps.at(-1))); return r; };
await step("session create", () => J(chain.session("create", "{}")));
// wait for an open Qualify / Final-winner market
let k = -1, outcome = 0;
for (let i = 0; i < 400 && k < 0; i++) {
  const st = await J(chain.econ("state", "{}"));
  if (st.ok && st.match) for (const kk of [2, 3, 4, 1]) if (open(st, kk)) { k = kk; outcome = st.match.slots.findIndex((x: number) => x === 1); break; }
  if (k < 0) await sleep(1000);
}
R.market = k;
const bet = (n: string) => step(n, () => J(chain.econ("bet", JSON.stringify({ k, outcome, amount: 5 }))));
await bet("bet 1 (session key)");
await step("session revoke (Solana)", () => J(chain.session("revoke", "{}")));
await bet("bet 2 (after the revoke)");
await step("session create again", () => J(chain.session("create", "{}")));
await bet("bet 3 (new session)");
writeFileSync(process.argv[2] ?? join(ROOT, "docs/proof/p7/chaos/session-revoke.json"), JSON.stringify(R, null, 1));
process.exit(0);
