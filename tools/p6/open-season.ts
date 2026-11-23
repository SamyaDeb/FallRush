// P6: open a test season (id ≥ 1,000,000, game authority) of LEN seconds, or the current weekly one (no args).
//   cd tools/keeper && P2_NET=devnet npx tsx ../p6/open-season.ts [id] [lenS]
import { ensureSeason, season, isoWeek } from "../keeper/p6.ts";
const id = Number(process.argv[2] ?? isoWeek(Date.now() / 1000)), len = Number(process.argv[3] ?? 0);
console.log("open", id, await ensureSeason(id, len));
console.log(JSON.stringify(await season(id), (k, v) => (typeof v === "bigint" ? v.toString() : v?.toBase58 ? v.toBase58() : v)));
