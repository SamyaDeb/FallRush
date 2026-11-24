// fullchain: the keeper's tick crank. Streams `crank(budget)` transactions (1.4M CU, permissionless, never fail on the
// program side) at a target rate next to the timed tasks. A heavy Capsule Run tick can need more than a task
// execution's 400k CU (one player's step up to ~866k, docs/proof/fullchain/RESEARCH.md §7); the crank runs it.
// The slice size never changes the simulation (RESEARCH §1), so the budget adapts freely: when the World makes no
// progress for STUCK_MS (a slice over 1.4M CU fails at the same point every time), the budget halves (floor 1,000 =
// the timed task's slice, one player always fits); after UP_MS of progress it doubles back (cap 9,000).
import { Transaction, Connection, Keypair, TransactionInstruction } from "@solana/web3.js";
import { cu, sleep } from "./lib.ts";

export const BUDGETS = [6000, 4500, 2250, 1000]; // LiteSVM Capsule Run 12p max per execution: 6000 → 1.43M (rare), 4500 → 1.38M

export type CrankStats = { sent: number; ok: number; err: number; down: number; up: number; msAt: Record<number, number> };

export function startCrank(o: {
  er: Connection;
  wallet: Keypair;
  /** the crank instruction for a budget */
  ix: (budget: number) => Promise<TransactionInstruction>;
  /** the World's current progress key (tick:part), updated by the caller's account subscription */
  progress: () => string;
  /** sends per second (the rollup lands ~108/s from one sender, RESEARCH §9) */
  rate?: number;
  stuckMs?: number;
  upMs?: number;
  log?: (o: object) => void;
}) {
  const rate = o.rate ?? 50, stuckMs = o.stuckMs ?? 250, upMs = o.upMs ?? 1000;
  const st: CrankStats = { sent: 0, ok: 0, err: 0, down: 0, up: 0, msAt: {} };
  let run = true;
  const done = (async () => {
    const ixs: Record<number, TransactionInstruction> = {};
    for (const b of BUDGETS) ixs[b] = await o.ix(b);
    let level = 0, levelAt = Date.now();
    let bh = (await o.er.getLatestBlockhash("processed")).blockhash, bhAt = Date.now(), nonce = 0;
    let lastProg = o.progress(), lastProgAt = Date.now(), t0 = Date.now(), sent0 = 0;
    while (run) {
      const now = Date.now();
      if (now - bhAt > 1500) { o.er.getLatestBlockhash("processed").then((x) => { bh = x.blockhash; }).catch(() => {}); bhAt = now; }
      const p = o.progress();
      if (p !== lastProg) { lastProg = p; lastProgAt = now; }
      if (now - lastProgAt > stuckMs && level < BUDGETS.length - 1) {
        st.msAt[BUDGETS[level]] = (st.msAt[BUDGETS[level]] ?? 0) + (now - levelAt);
        level++; levelAt = now; lastProgAt = now; st.down++;
        o.log?.({ ev: "crank_budget", budget: BUDGETS[level], why: "stuck", at: p });
      } else if (level > 0 && now - levelAt > upMs && now - lastProgAt < stuckMs) {
        st.msAt[BUDGETS[level]] = (st.msAt[BUDGETS[level]] ?? 0) + (now - levelAt);
        level--; levelAt = now; st.up++;
      }
      const due = Math.floor(((now - t0) / 1000) * rate);
      while (sent0 < due) {
        const tx = new Transaction().add(cu(1_400_000 - (nonce++ % 4096)), ixs[BUDGETS[level]]);
        tx.feePayer = o.wallet.publicKey; tx.recentBlockhash = bh; tx.sign(o.wallet);
        o.er.sendRawTransaction(tx.serialize(), { skipPreflight: true, maxRetries: 0 }).then(() => st.ok++).catch(() => st.err++);
        sent0++; st.sent++;
      }
      await sleep(3);
    }
    st.msAt[BUDGETS[level]] = (st.msAt[BUDGETS[level]] ?? 0) + (Date.now() - levelAt);
  })();
  return { stats: st, stop: async () => { run = false; await done; return st; } };
}
