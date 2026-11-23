// P4: where the economy accounts of arena ARENA live (base owner / rollup), for setup checks.
import { base, er, pda } from "../keeper/lib.ts";
import * as E from "../keeper/econ.ts";
const arena = pda.arena(Number(process.env.ARENA ?? 2));
const names = ["market0", "market1", "market2", "market3", "market4", "drops", "rushMint", "treasury"];
const ks = [0, 1, 2, 3, 4].map((k) => E.epda.market(Number(process.env.ARENA ?? 2), k)).concat([E.epda.drops(arena), E.RUSH_MINT, E.epda.treasury()]);
for (let i = 0; i < ks.length; i++) {
  const b = await base.getAccountInfo(ks[i]).catch(() => null);
  const r = await er.getAccountInfo(ks[i]).catch(() => null);
  console.log(names[i].padEnd(9), ks[i].toBase58(), "base", b ? `${b.owner.toBase58().slice(0, 6)} ${b.data.length} B ${b.lamports}` : "-", "| er", r ? `${r.owner.toBase58().slice(0, 6)} ${r.data.length} B` : "-");
}
process.exit(0);
