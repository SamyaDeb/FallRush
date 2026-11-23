// P6 task 2.3: close a season after ends_at (permissionless) and mint a Season Trophy (Metaplex Core) to each top 1.
//   cd tools/keeper && P2_NET=devnet npx tsx ../p6/close-season.ts <id>
import { closeSeasonAndMint, season, p6pda } from "../keeper/p6.ts";
const id = Number(process.argv[2]);
console.log("before", JSON.stringify(await season(id), (k, v) => (typeof v === "bigint" ? v.toString() : v?.toBase58 ? v.toBase58() : v)));
const r = await closeSeasonAndMint(id);
console.log("sigs", JSON.stringify(r));
const s = p6pda.season(id);
console.log("trophies", [0, 1, 2].map((k) => p6pda.trophy(s, k).toBase58()));
console.log("after", JSON.stringify(await season(id), (k, v) => (typeof v === "bigint" ? v.toString() : v?.toBase58 ? v.toBase58() : v)));
