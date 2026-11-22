// What compute budget does the rollup scheduler give a task transaction? Schedule 20 ticks, then read the
// task transactions' logs ("consumed X of Y compute units").
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { PublicKey } from "@solana/web3.js";
import { er, IX, LOGS, NET, wallet, pda, send, sendRetry, sleep, cuLimit } from "./lib.ts";
const st = JSON.parse(readFileSync(join(LOGS, `state-${NET}.json`), "utf8"));
const out: any = {};
for (const [name, split] of [["run", false], ["lid", true]] as const) {
  const map = new PublicKey(st.maps[name]);
  await sendRetry(er, [IX.resetWorld(wallet.publicKey, 0, map, 11, 1, 0)], [wallet]);
  const r = await send(er, [cuLimit(400_000), IX.scheduleTicks(wallet.publicKey, 0, map, 50, 20, BigInt(Date.now()), split)], [wallet]);
  await sleep(4000);
  const sigs = await er.getSignaturesForAddress(pda.world(0), { limit: 25 }, "confirmed");
  const budgets: string[] = [];
  for (const s of sigs.slice(0, 12)) {
    const t = await er.getTransaction(s.signature, { maxSupportedTransactionVersion: 0, commitment: "confirmed" }).catch(() => null);
    const lines = (t?.meta?.logMessages ?? []).filter((l) => / consumed \d+ of \d+ compute units/.test(l)).map((l) => /consumed (\d+) of (\d+)/.exec(l)!.slice(1).join("/"));
    const ixs = t?.transaction.message.compiledInstructions?.length ?? (t as any)?.transaction?.message?.instructions?.length;
    const signer = t?.transaction.message.staticAccountKeys?.[0]?.toBase58();
    budgets.push(`${s.signature.slice(0, 8)} ixs=${ixs} payer=${signer?.slice(0, 6)} err=${JSON.stringify(s.err)} ${lines.join(" ")}`);
  }
  out[name + (split ? "_split" : "_single")] = { schedule_err: r.err ?? null, budgets };
  console.log(name, split, "\n " + budgets.join("\n "));
}
writeFileSync(join(LOGS, `crank-budget-${NET}.json`), JSON.stringify(out, null, 2));
process.exit(0);
