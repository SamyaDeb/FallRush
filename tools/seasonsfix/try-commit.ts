// seasons fix: one fee-vault commit_econ on an arena (program op a = 1), with the delegation-metadata nonces before/after.
//   P2_NET=devnet node --experimental-strip-types --no-warnings tools/seasonsfix/try-commit.ts <arena>
import { PublicKey } from "@solana/web3.js";
import { base, pda } from "../keeper/lib.ts";
import { epda, MARKETS, DELEGATION } from "../keeper/econ.ts";
import { commitEcon, magicFeeVault, COMMIT_FEE_VAULT } from "../keeper/p6.ts";
const arenaId = Number(process.argv[2] ?? 7);
const arena = pda.arena(arenaId);
const accs = [...Array.from({ length: MARKETS }, (_, k) => epda.market(arenaId, k)), epda.drops(arena)];
const nonces = async () => (await base.getMultipleAccountsInfo(accs.map((k) => PublicKey.findProgramAddressSync([Buffer.from("delegation-metadata"), k.toBuffer()], DELEGATION)[0]), "confirmed")).map((i) => (i ? Number((i.data as Buffer).readBigUInt64LE(8)) : null));
console.log("feeVault", magicFeeVault().toBase58(), "COMMIT_FEE_VAULT", COMMIT_FEE_VAULT);
console.log("nonces before", await nonces());
const r = await commitEcon(arenaId);
console.log("commitEcon", JSON.stringify(r));
for (let i = 0; i < 12; i++) { await new Promise((s) => setTimeout(s, 5000)); console.log("nonces", await nonces()); }
process.exit(0);
