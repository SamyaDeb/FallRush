import { base, er, pda } from "../keeper/lib.ts";
import * as E from "../keeper/econ.ts";
const A = Number(process.argv[2] ?? 3);
for (let k = 0; k < 5; k++) { const i = await base.getAccountInfo(E.epda.market(A, k)); const j = await er.getAccountInfo(E.epda.market(A, k));
  console.log(k, "base owner", i?.owner.toBase58().slice(0, 6), i ? E.decodeMarket(i.data as Buffer).match.toBase58().slice(0, 8) : null, i ? E.decodeMarket(i.data as Buffer).status : null, "| er", j ? E.decodeMarket(j.data as Buffer).match.toBase58().slice(0, 8) : null, j ? E.decodeMarket(j.data as Buffer).status : null); }
