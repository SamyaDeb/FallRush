// P5 0.3: fund the Magic Action escrow (delegation program TopUpEphemeralBalance, Solana): escrow =
// DLP PDA ["balance", escrow_auth, [255]], escrow_auth = fall_rush PDA ["escrow_auth"]. Idempotent: tops up to TARGET.
//   P2_NET=devnet node --experimental-strip-types tools/p5/escrow.ts [TARGET_LAMPORTS=20000000]
import { PublicKey, SystemProgram, TransactionInstruction } from "@solana/web3.js";
import { base, wallet, PROGRAM_ID, sendBase } from "../keeper/lib.ts";
const DLP = new PublicKey("DELeGGvXpWV2fqJUhqcF5ZSYMS4JTLjteaAMARRSaeSh");
const TARGET = Number(process.env.TARGET_LAMPORTS ?? 20_000_000);
if (TARGET > 50_000_000) throw new Error("escrow cap is 0.05 SOL (P5 prompt)");
export const escrowAuth = PublicKey.findProgramAddressSync([Buffer.from("escrow_auth")], PROGRAM_ID)[0];
export const escrow = PublicKey.findProgramAddressSync([Buffer.from("balance"), escrowAuth.toBuffer(), Buffer.from([255])], DLP)[0];
const have = await base.getBalance(escrow);
console.log(`escrow_auth ${escrowAuth.toBase58()} escrow ${escrow.toBase58()} balance ${have}`);
if (have < TARGET) {
  const d = Buffer.alloc(8 + 8 + 1);
  d.writeBigUInt64LE(9n, 0); d.writeBigUInt64LE(BigInt(TARGET - have), 8); d[16] = 255;
  const ix = new TransactionInstruction({ programId: DLP, keys: [
    { pubkey: wallet.publicKey, isSigner: true, isWritable: true }, { pubkey: escrowAuth, isSigner: false, isWritable: false },
    { pubkey: escrow, isSigner: false, isWritable: true }, { pubkey: SystemProgram.programId, isSigner: false, isWritable: false }], data: d });
  const sig = await sendBase([ix], [wallet], "top_up_escrow");
  console.log(JSON.stringify({ topUp: TARGET - have, sig, after: await base.getBalance(escrow) }));
}
process.exit(0);
