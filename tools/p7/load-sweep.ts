// P7 D-SOL: returns the load-test burners' devnet SOL to the admin (all but the transfer fee; the chips accounts on the
// rollup are left as they are). Reads tools/keeper/.keys/devnet-p7-bettors.json (base58 secret keys).
//   cd tools/keeper && npx tsx ../p7/load-sweep.ts
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { homedir } from "node:os";
import { Keypair, SystemProgram, Transaction, Connection, sendAndConfirmTransaction } from "@solana/web3.js";
import { b58decode } from "../../packages/chain/src/b58.ts";
const ROOT = new URL("../../", import.meta.url).pathname;
const base = new Connection("https://api.devnet.solana.com", "confirmed");
const admin = Keypair.fromSecretKey(Uint8Array.from(JSON.parse(readFileSync(join(homedir(), ".config/solana/id.json"), "utf8"))));
const keys: string[] = JSON.parse(readFileSync(join(ROOT, "tools/keeper/.keys/devnet-p7-bettors.json"), "utf8"));
let back = 0;
for (const k of keys) {
  const kp = Keypair.fromSecretKey(b58decode(k));
  const bal = await base.getBalance(kp.publicKey).catch(() => 0);
  if (bal <= 10_000) continue;
  const amt = bal - 5_000;
  try { await sendAndConfirmTransaction(base, new Transaction().add(SystemProgram.transfer({ fromPubkey: kp.publicKey, toPubkey: admin.publicKey, lamports: amt })), [kp]); back += amt; }
  catch (e) { console.log("sweep", kp.publicKey.toBase58(), String(e).slice(0, 120)); }
  await new Promise((r) => setTimeout(r, 700));
}
console.log(`swept ${back / 1e9} SOL back to the admin`);
