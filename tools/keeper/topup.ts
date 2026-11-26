// Seasons runway: tops up a DELEGATED account's lamports on the rollup from a Solana wallet, through MagicBlock's
// Ephemeral SPL Token program (ix 20 "lamports delegated transfer"; hand-encoded copy of ephemeral-rollups-sdk 0.20.0
// `lamportsDelegatedTransferIx`, so no new package). Used for the arena's DropQueue, which pays the fee-vault commits
// (program op commit_econ a = 1): from the 26th commit of an account MagicBlock charges 1,000,000 lamports per
// committed account per commit.
import { PublicKey, SystemProgram, TransactionInstruction } from "@solana/web3.js";
import { randomBytes } from "node:crypto";
import { DELEGATION } from "./econ.ts";

export const EPHEMERAL_SPL_TOKEN = new PublicKey("SPLxh1LVZzEkX99H6rqYizhytLWPZVV296zyYDPagv2");
const pdaOf = (seeds: Buffer[], prog: PublicKey) => PublicKey.findProgramAddressSync(seeds, prog)[0];

export function lamportsDelegatedTransferIx(payer: PublicKey, destination: PublicKey, amount: bigint, salt: Uint8Array = randomBytes(32)): TransactionInstruction {
  if (amount < 0n) throw new Error("amount must be non-negative");
  if (salt.length !== 32) throw new Error("salt must be exactly 32 bytes");
  const rent = pdaOf([Buffer.from("rent")], EPHEMERAL_SPL_TOKEN);
  const lamports = pdaOf([Buffer.from("lamports"), payer.toBuffer(), destination.toBuffer(), Buffer.from(salt)], EPHEMERAL_SPL_TOKEN);
  const data = Buffer.alloc(41);
  data[0] = 20;
  data.writeBigUInt64LE(amount, 1);
  Buffer.from(salt).copy(data, 9);
  return new TransactionInstruction({
    programId: EPHEMERAL_SPL_TOKEN,
    keys: [
      { pubkey: payer, isSigner: true, isWritable: true },
      { pubkey: rent, isSigner: false, isWritable: true },
      { pubkey: lamports, isSigner: false, isWritable: true },
      { pubkey: EPHEMERAL_SPL_TOKEN, isSigner: false, isWritable: false },
      { pubkey: pdaOf([Buffer.from("buffer"), lamports.toBuffer()], EPHEMERAL_SPL_TOKEN), isSigner: false, isWritable: true },
      { pubkey: pdaOf([Buffer.from("delegation"), lamports.toBuffer()], DELEGATION), isSigner: false, isWritable: true },
      { pubkey: pdaOf([Buffer.from("delegation-metadata"), lamports.toBuffer()], DELEGATION), isSigner: false, isWritable: true },
      { pubkey: DELEGATION, isSigner: false, isWritable: false },
      { pubkey: SystemProgram.programId, isSigner: false, isWritable: false },
      { pubkey: destination, isSigner: false, isWritable: true },
      { pubkey: pdaOf([Buffer.from("delegation"), destination.toBuffer()], DELEGATION), isSigner: false, isWritable: false },
    ],
    data,
  });
}
