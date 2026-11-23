import { PublicKey } from "@solana/web3.js";
import { base, er } from "../keeper/lib.ts";
import { matchReceipt, chipsOf, market } from "../keeper/econ.ts";
for (const m of process.argv.slice(2)) {
  const r: any = await matchReceipt(base, new PublicKey(m));
  console.log(m, "owner", r.owner.toBase58(), "status", r.status, "champ", r.champion, "flags", r.flags, "human", r.humanMask, "markets", JSON.stringify(r.markets, (k, v) => typeof v === "bigint" ? v.toString() : v));
}
const mm = new PublicKey("H9va5AgT1ceQzGCMpvFumAghnA76uLW1Mcy2475ZX9h");
console.log("mm chips", JSON.stringify(await chipsOf(mm), (k, v) => typeof v === "bigint" ? v.toString() : v));
for (const a of [2, 3]) { const x: any = await market(a, 0); console.log("arena", a, "champion market now", x.match.toBase58(), "status", x.status, "pool", x.pool.toString(), "refund", x.refund); }
