// seasons runway: send `lamports` to an arena's DropQueue on the rollup, then watch its rollup balance.
//   P2_NET=devnet node --experimental-strip-types --no-warnings tools/seasonsfix/try-topup.ts <arena> <lamports>
import { base, er, pda, wallet, sendBase } from "../keeper/lib.ts";
import { epda } from "../keeper/econ.ts";
import { lamportsDelegatedTransferIx } from "../keeper/topup.ts";
const arenaId = Number(process.argv[2] ?? 2), amount = BigInt(process.argv[3] ?? 20_000_000);
const dq = epda.drops(pda.arena(arenaId));
const erBal = async () => (await er.getAccountInfo(dq))?.lamports;
console.log("dropq", dq.toBase58(), "rollup lamports before", await erBal(), "base wallet", await base.getBalance(wallet.publicKey));
const r: any = await sendBase([lamportsDelegatedTransferIx(wallet.publicKey, dq, amount)], [wallet], "lamports_topup");
console.log("sent", JSON.stringify(r).slice(0, 300));
for (let i = 0; i < 12; i++) { await new Promise((s) => setTimeout(s, 5000)); console.log("rollup lamports", await erBal()); }
process.exit(0);
