// P6: commit an arena's markets + drop queue (if needed) and record a finalized match into a season by hand (the
// keeper's permissionless path). Wallets = humans + every bettor + drop buyers from the archived summary.
//   cd tools/keeper && P2_NET=devnet npx tsx ../p6/record.ts <arena> <match> <seasonId> <summary.json> [commit=1]
import { PublicKey } from "@solana/web3.js";
import { readFileSync } from "node:fs";
import { base, sleep } from "../keeper/lib.ts";
import * as E from "../keeper/econ.ts";
import * as S from "../keeper/p6.ts";
const [A, M, SID, SUM, C] = process.argv.slice(2);
const arenaId = Number(A), matchPda = new PublicKey(M), sid = Number(SID);
const sum = JSON.parse(readFileSync(SUM, "utf8"));
if (C !== "0") console.log("commit", JSON.stringify(await S.commitEcon(arenaId)));
for (let i = 0; i < 40; i++) { const x = await base.getAccountInfo(E.epda.market(arenaId, 0)); if (x && E.decodeMarket(x.data as Buffer).match.equals(matchPda)) { console.log("copy landed after", i * 2, "s"); break; } await sleep(2000); }
const rc: any = await E.matchReceipt(base, matchPda);
const ws = new Set<string>();
for (let s = 0; s < 12; s++) if ((rc.humanMask >> s) & 1) ws.add(rc.wallets[s].toBase58());
for (let k = 0; k < 5; k++) for (const b of sum[`market${k}`]?.bets ?? []) ws.add(b.wallet);
for (const b of sum.drops?.buys ?? []) ws.add(b.by);
ws.delete("H9va5AgT1ceQzGCMpvFumAghnA76uLW1Mcy2475ZX9h"); ws.delete(PublicKey.default.toBase58());
const champion = rc.champion < 12 && (rc.humanMask >> rc.champion) & 1 ? rc.wallets[rc.champion] : null;
console.log("record", sid, ws.size, "wallets", await S.recordSeason(arenaId, matchPda, sid, [...ws].map((x) => new PublicKey(x)), champion));
console.log(JSON.stringify(await S.season(sid), (k, v) => (typeof v === "bigint" ? v.toString() : v?.toBase58 ? v.toBase58() : v)).slice(0, 1500));
