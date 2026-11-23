// P5 task 0.2 — Ephemeral SPL Token retry (time-boxed), the documented order this time, with a FRESH mint and FRESH
// wallets every round (P4's spike reused one eATA, which a failed base withdraw left unusable):
//   base: mint + ATA + 100 tokens → delegateSpl (eATA + vault + deposit + delegate, base transaction)
//   ER:   the owner's ATA on the rollup shows 100 → a transfer of 10 inside the rollup to the owner's 2nd ATA? (no: a
//         plain self-check of the balance) → undelegateIx (ER: commit + undelegate the eATA FIRST)
//   base: wait until the eATA is owned by the e-token program again → withdrawSplIx (vault → the owner's ATA)
//   check: the owner's base ATA holds 100 again = one round trip.
// P4's failure was sending withdrawSpl's base transaction to the rollup (Custom 27 = the delegation program's
// DelegationRecordInvalidAccountOwner, research brief) and a base withdraw before the eATA settled.
//   node --experimental-strip-types tools/p5/espl-retry.ts   (ROUNDS=5)
import { Connection, Keypair, PublicKey, SystemProgram, Transaction, TransactionInstruction } from "@solana/web3.js";
import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";
import * as sdk from "@magicblock-labs/ephemeral-rollups-sdk";

const ROOT = new URL("../../", import.meta.url).pathname;
const ROUNDS = Number(process.env.ROUNDS ?? 5);
const base = new Connection("https://api.devnet.solana.com", "confirmed");
const er = new Connection("https://devnet-as.magicblock.app", "processed");
const VALIDATOR = new PublicKey("MAS1Dt9qreoRMQ14YQuhg8UTZMMzDdKhmkZMECCzk57");
const TOKEN = new PublicKey("TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA");
const ASSOC = new PublicKey("ATokenGPvbdGVxr1b2hvZbsiqW5xWH25efTNsLJA8knL");
const ESPL = new PublicKey("SPLxh1LVZzEkX99H6rqYizhytLWPZVV296zyYDPagv2");
const admin = Keypair.fromSecretKey(Uint8Array.from(JSON.parse(readFileSync(join(homedir(), ".config/solana/id.json"), "utf8"))));
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
const OUT = join(ROOT, "docs/proof/p5/espl-retry.json");
mkdirSync(join(ROOT, "docs/proof/p5"), { recursive: true });
const out: any = { started: new Date().toISOString(), sdk: "0.16.2 (installed)", rounds: [] };
const save = () => writeFileSync(OUT, JSON.stringify(out, null, 1));
const ata = (mint: PublicKey, owner: PublicKey) => PublicKey.findProgramAddressSync([owner.toBuffer(), TOKEN.toBuffer(), mint.toBuffer()], ASSOC)[0];
const u64 = (n: bigint) => { const b = Buffer.alloc(8); b.writeBigUInt64LE(n); return b; };

async function send(conn: Connection, ixs: TransactionInstruction[], signers: Keypair[], what: string) {
  const t0 = Date.now();
  let last = "";
  for (let i = 0; i < 6; i++) {
    try {
      const tx = new Transaction().add(...ixs);
      tx.feePayer = signers[0].publicKey;
      tx.recentBlockhash = (await conn.getLatestBlockhash()).blockhash;
      tx.sign(...signers);
      const sig = await conn.sendRawTransaction(tx.serialize(), { skipPreflight: true });
      for (let k = 0; k < 120; k++) {
        const st = (await conn.getSignatureStatuses([sig])).value[0];
        if (st?.err) {
          const logs = (await conn.getTransaction(sig, { maxSupportedTransactionVersion: 0, commitment: "confirmed" }).catch(() => null))?.meta?.logMessages?.slice(-12);
          return { what, sig, ms: Date.now() - t0, err: JSON.stringify(st.err), logs };
        }
        if (st && (st.confirmationStatus === "confirmed" || st.confirmationStatus === "finalized" || conn === er)) return { what, sig, ms: Date.now() - t0 };
        await sleep(400);
      }
      last = "timeout";
    } catch (e: any) { last = String(e?.message ?? e).slice(0, 300); if (!/429|blockhash|fetch|timed out/i.test(last)) break; }
    await sleep(1500 * (i + 1));
  }
  return { what, sig: "", ms: Date.now() - t0, err: last };
}
async function tokenAmount(conn: Connection, k: PublicKey): Promise<bigint | null> {
  const i = await conn.getAccountInfo(k).catch(() => null);
  return i && i.data.length >= 72 ? i.data.readBigUInt64LE(64) : null;
}

for (let r = 1; r <= ROUNDS; r++) {
  const round: any = { round: r, steps: [] };
  out.rounds.push(round);
  const step = (x: any) => { round.steps.push(x); console.log(`r${r} ${x.what}: ${x.err ? "ERR " + x.err : "ok"} ${x.ms ?? ""}ms`); save(); return !x.err; };
  const owner = Keypair.generate(), mint = Keypair.generate();
  round.owner = owner.publicKey.toBase58(); round.mint = mint.publicKey.toBase58();
  const AMT = 100_000_000n;
  // 1. base: fund, mint, ATA, tokens
  const rentMint = await base.getMinimumBalanceForRentExemption(82);
  const init = new TransactionInstruction({ programId: TOKEN, keys: [{ pubkey: mint.publicKey, isSigner: false, isWritable: true }], data: Buffer.concat([Buffer.from([20, 6]), admin.publicKey.toBuffer(), Buffer.from([0])]) });
  const mkAta = new TransactionInstruction({ programId: ASSOC, data: Buffer.from([1]), keys: [{ pubkey: admin.publicKey, isSigner: true, isWritable: true }, { pubkey: ata(mint.publicKey, owner.publicKey), isSigner: false, isWritable: true }, { pubkey: owner.publicKey, isSigner: false, isWritable: false }, { pubkey: mint.publicKey, isSigner: false, isWritable: false }, { pubkey: SystemProgram.programId, isSigner: false, isWritable: false }, { pubkey: TOKEN, isSigner: false, isWritable: false }] });
  const mintTo = new TransactionInstruction({ programId: TOKEN, data: Buffer.concat([Buffer.from([7]), u64(AMT)]), keys: [{ pubkey: mint.publicKey, isSigner: false, isWritable: true }, { pubkey: ata(mint.publicKey, owner.publicKey), isSigner: false, isWritable: true }, { pubkey: admin.publicKey, isSigner: true, isWritable: false }] });
  if (!step(await send(base, [SystemProgram.transfer({ fromPubkey: admin.publicKey, toPubkey: owner.publicKey, lamports: 20_000_000 }), SystemProgram.createAccount({ fromPubkey: admin.publicKey, newAccountPubkey: mint.publicKey, lamports: rentMint, space: 82, programId: TOKEN }), init, mkAta, mintTo], [admin, mint], "fund + mint + ATA + 100 tokens"))) continue;
  // 2. base: deposit into the rollup
  const t0 = Date.now();
  const dix = await sdk.delegateSpl(owner.publicKey, mint.publicKey, AMT, { validator: VALIDATOR, initIfMissing: true, initVaultIfMissing: true, initAtasIfMissing: true, payer: owner.publicKey } as any);
  if (!step(await send(base, dix, [owner], `delegateSpl (${dix.length} ix)`))) continue;
  let seen: bigint | null = null;
  for (let i = 0; i < 60 && !(seen = await tokenAmount(er, ata(mint.publicKey, owner.publicKey))); i++) await sleep(500);
  step({ what: "rollup sees the balance", ms: Date.now() - t0, amount: String(seen), err: seen === AMT ? undefined : `ER balance ${seen}` });
  // 3. ER: undelegate the eATA first (commit + undelegate)
  const t1 = Date.now();
  if (!step(await send(er, [sdk.undelegateIx(owner.publicKey, mint.publicKey)], [owner], "undelegateIx (ER)"))) continue;
  const eata = (sdk as any).deriveEphemeralAta(owner.publicKey, mint.publicKey);
  const eataKey: PublicKey = Array.isArray(eata) ? eata[0] : eata;
  let settled = false;
  for (let i = 0; i < 120; i++) { const ai = await base.getAccountInfo(eataKey).catch(() => null); if (ai && ai.owner.equals(ESPL)) { settled = true; break; } await sleep(1000); }
  step({ what: "eATA back on Solana (owner = e-token program)", ms: Date.now() - t1, err: settled ? undefined : "not settled in 120 s" });
  if (!settled) continue;
  // 4. base: withdraw vault → ATA
  if (!step(await send(base, [sdk.withdrawSplIx(owner.publicKey, mint.publicKey, AMT)], [owner], "withdrawSplIx (Solana)"))) continue;
  const back = await tokenAmount(base, ata(mint.publicKey, owner.publicKey));
  round.roundTrip = back === AMT;
  step({ what: "base ATA after withdraw", amount: String(back), err: back === AMT ? undefined : `expected ${AMT}` });
  await sleep(1500);
}
out.roundTrips = out.rounds.filter((x: any) => x.roundTrip).length;
out.finished = new Date().toISOString();
save();
console.log(`round trips ${out.roundTrips}/${ROUNDS}`);
process.exit(0);
