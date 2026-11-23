// P6 D-CROWN: was the Crown Core asset created by the Magic Action in the undelegation tx (fee payer = validator)?
import { PublicKey } from "@solana/web3.js";
import { base } from "../keeper/lib.ts";
import * as E from "../keeper/econ.ts";
const m = new PublicKey(process.argv[2]);
const crown = E.epda.crown(m);
const sigs = await base.getSignaturesForAddress(crown, { limit: 10 });
const out: any = { match: m.toBase58(), crown: crown.toBase58(), info: (await base.getAccountInfo(crown))?.owner.toBase58(), txs: [] };
for (const s of sigs.reverse()) {
  const r: any = await (await fetch("https://api.devnet.solana.com", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ jsonrpc: "2.0", id: 1, method: "getTransaction", params: [s.signature, { encoding: "jsonParsed", maxSupportedTransactionVersion: 1, commitment: "confirmed" }] }) })).json();
  const t = r.result;
  const keys = t.transaction.message.accountKeys.map((k: any) => k.pubkey ?? k);
  const logs: string[] = t.meta.logMessages ?? [];
  out.txs.push({ sig: s.signature, feePayer: keys[0], err: t.meta.err, undelegation: logs.some((l) => /Undelegat/i.test(l)), mintAction: logs.some((l) => /MintRewardsAction/.test(l)), coreCreate: logs.some((l) => /CoREENx/.test(l)), logs: logs.filter((l) => /invoke \[1\]|Instruction:/.test(l)).slice(0, 14) });
}
console.log(JSON.stringify(out, null, 1));
