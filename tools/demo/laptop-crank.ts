// Demo: "anyone can send the next slice". Sends ONE permissionless `crank(budget)` to an arena's World on MagicBlock's
// rollup, signed by a brand-new throwaway keypair made on this laptop (no keeper key, no server key), and prints the
// signature + an Explorer link. crank() never fails on the program side: when no tick is due it records NOT_DUE and
// changes nothing; during a match it steps one slice, and slice size never changes the simulation.
//   node --experimental-strip-types tools/demo/laptop-crank.ts [--arena 2] [--budget 1000] [--rpc https://devnet-as.magicblock.app]
import { Connection, Keypair, PublicKey, Transaction, TransactionInstruction, ComputeBudgetProgram } from "@solana/web3.js";
import { BorshAccountsCoder } from "@coral-xyz/anchor";
import { readFileSync } from "node:fs";
const opt = (k: string, d: string) => { const i = process.argv.indexOf("--" + k); return i > 0 ? process.argv[i + 1] : d; };
const PROGRAM = new PublicKey("4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4");
const RPC = opt("rpc", "https://devnet-as.magicblock.app");
const ID = Number(opt("arena", "2")), BUDGET = Number(opt("budget", "1000"));
const idl = JSON.parse(readFileSync(new URL("../../programs/target/idl/fall_rush.json", import.meta.url), "utf8"));
const er = new Connection(RPC, "processed");
const p = (seeds: Buffer[]) => PublicKey.findProgramAddressSync(seeds, PROGRAM)[0];
const arena = p([Buffer.from("arena"), Buffer.from([ID])]);
const info = await er.getAccountInfo(arena, "processed");
if (!info) throw new Error("arena not found on the rollup");
const a: any = new BorshAccountsCoder(idl).decode("Arena", info.data);
const match: PublicKey = a.match_account, maps: PublicKey[] = a.maps;
const w = (pubkey: PublicKey) => ({ pubkey, isSigner: false, isWritable: true }), r = (pubkey: PublicKey) => ({ pubkey, isSigner: false, isWritable: false });
// the same account list as the keeper's tickAccounts (tools/keeper/lib.ts)
const keys = [r(arena), w(p([Buffer.from("world"), arena.toBuffer()])), w(p([Buffer.from("ev"), arena.toBuffer()])), w(match), ...maps.map(r),
  ...Array.from({ length: 12 }, (_, s) => r(p([Buffer.from("in"), arena.toBuffer(), Buffer.from([s])]))), r(p([Buffer.from("drops"), arena.toBuffer()]))];
const disc = Buffer.from(idl.instructions.find((i: any) => i.name === "crank").discriminator);
const arg = Buffer.alloc(4); arg.writeUInt32LE(BUDGET);
const me = Keypair.generate();
const tx = new Transaction().add(ComputeBudgetProgram.setComputeUnitLimit({ units: 1_400_000 }), new TransactionInstruction({ programId: PROGRAM, keys, data: Buffer.concat([disc, arg]) }));
tx.feePayer = me.publicKey;
tx.recentBlockhash = (await er.getLatestBlockhash("processed")).blockhash;
tx.sign(me);
console.log(`fresh laptop keypair  ${me.publicKey.toBase58()}`);
console.log(`arena ${ID} · match ${match.toBase58()}`);
const sig = await er.sendRawTransaction(tx.serialize(), { skipPreflight: true });
console.log(`crank() sent          ${sig}`);
for (let i = 0; i < 30; i++) {
  const st = (await er.getSignatureStatuses([sig])).value[0];
  if (st?.confirmationStatus) { console.log(st.err ? `landed with error ${JSON.stringify(st.err)}` : `landed ✓ (${st.confirmationStatus})`); break; }
  await new Promise((res) => setTimeout(res, 300));
}
console.log(`https://explorer.solana.com/tx/${sig}?cluster=custom&customUrl=${encodeURIComponent(RPC)}`);
