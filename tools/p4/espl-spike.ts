// P4-1 spike (time-box ½ day): MagicBlock Ephemeral SPL Token (SPLxh1LVZzEkX99H6rqYizhytLWPZVV296zyYDPagv2, v0.1.0)
// on devnet + devnet-as, through the ER TS SDK's own helpers (@magicblock-labs/ephemeral-rollups-sdk, already a dep).
//   1. a test mint (6 decimals; the spike's mint authority is the admin wallet: the real RUSH mint uses a program PDA)
//   2. deposit into the rollup (delegateSpl: eATA + global vault + deposit + delegate)
//   3. transfer inside the rollup: (a) signed by the owner, (b) by a session key the owner `approve`d as delegate
//   4. a PDA-signed payout needs our program to CPI the SPL Token program inside the rollup (not tested here)
//   5. withdraw back to Solana (withdrawSpl)
// ROUNDS round trips; every signature + timing into docs/proof/p4/espl-spike.json.
//   node --experimental-strip-types tools/p4/espl-spike.ts
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
const loadKp = (p: string) => Keypair.fromSecretKey(Uint8Array.from(JSON.parse(readFileSync(p, "utf8"))));
const admin = loadKp(join(homedir(), ".config/solana/id.json"));
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
const out: any = { started: new Date().toISOString(), rounds: [], notes: [] };
const OUT = join(ROOT, "docs/proof/p4/espl-spike.json");
mkdirSync(join(ROOT, "docs/proof/p4"), { recursive: true });
const save = () => writeFileSync(OUT, JSON.stringify(out, null, 1));

const ata = (mint: PublicKey, owner: PublicKey) => PublicKey.findProgramAddressSync([owner.toBuffer(), TOKEN.toBuffer(), mint.toBuffer()], ASSOC)[0];
const createAtaIx = (payer: PublicKey, owner: PublicKey, mint: PublicKey) => new TransactionInstruction({
  programId: ASSOC, data: Buffer.from([1]), // CreateIdempotent
  keys: [
    { pubkey: payer, isSigner: true, isWritable: true }, { pubkey: ata(mint, owner), isSigner: false, isWritable: true },
    { pubkey: owner, isSigner: false, isWritable: false }, { pubkey: mint, isSigner: false, isWritable: false },
    { pubkey: SystemProgram.programId, isSigner: false, isWritable: false }, { pubkey: TOKEN, isSigner: false, isWritable: false },
  ],
});
const u64 = (n: bigint) => { const b = Buffer.alloc(8); b.writeBigUInt64LE(n); return b; };
const tokIx = (data: Buffer, keys: [PublicKey, boolean, boolean][]) => new TransactionInstruction({ programId: TOKEN, data, keys: keys.map(([pubkey, isSigner, isWritable]) => ({ pubkey, isSigner, isWritable })) });

async function send(conn: Connection, ixs: TransactionInstruction[], signers: Keypair[], what: string, skipPreflight = false) {
  const t0 = Date.now();
  let last = "";
  for (let i = 0; i < 6; i++) {
    try {
      const tx = new Transaction().add(...ixs);
      tx.feePayer = signers[0].publicKey;
      tx.recentBlockhash = (await conn.getLatestBlockhash()).blockhash;
      tx.sign(...signers);
      const sig = await conn.sendRawTransaction(tx.serialize(), { skipPreflight });
      for (let k = 0; k < 120; k++) {
        const st = (await conn.getSignatureStatuses([sig])).value[0];
        if (st?.err) return { what, sig, ms: Date.now() - t0, err: JSON.stringify(st.err) };
        if (st && (st.confirmationStatus === "confirmed" || st.confirmationStatus === "finalized" || conn === er)) return { what, sig, ms: Date.now() - t0 };
        await sleep(400);
      }
      last = "timeout";
    } catch (e: any) { last = String(e?.message ?? e).slice(0, 400); if (!/429|blockhash|fetch|timed out/i.test(last)) break; }
    await sleep(1500 * (i + 1));
  }
  return { what, sig: "", ms: Date.now() - t0, err: last };
}
const amountOf = async (conn: Connection, acc: PublicKey) => { const i = await conn.getAccountInfo(acc).catch(() => null); return i && i.data.length >= 72 ? i.data.readBigUInt64LE(64) : null; };

// ---- 1. test mint + 2 users
const mint = Keypair.generate();
const owner = Keypair.generate(), other = Keypair.generate(), session = Keypair.generate();
out.mint = mint.publicKey.toBase58(); out.owner = owner.publicKey.toBase58(); out.other = other.publicKey.toBase58(); out.session = session.publicKey.toBase58();
const rent = await base.getMinimumBalanceForRentExemption(82);
let r: any = await send(base, [
  SystemProgram.createAccount({ fromPubkey: admin.publicKey, newAccountPubkey: mint.publicKey, lamports: rent, space: 82, programId: TOKEN }),
  tokIx(Buffer.concat([Buffer.from([20, 6]), admin.publicKey.toBuffer(), Buffer.from([0])]), [[mint.publicKey, false, true]]), // InitializeMint2
  SystemProgram.transfer({ fromPubkey: admin.publicKey, toPubkey: owner.publicKey, lamports: 30_000_000 }),
  SystemProgram.transfer({ fromPubkey: admin.publicKey, toPubkey: other.publicKey, lamports: 10_000_000 }),
  createAtaIx(admin.publicKey, owner.publicKey, mint.publicKey), createAtaIx(admin.publicKey, other.publicKey, mint.publicKey),
  tokIx(Buffer.concat([Buffer.from([7]), u64(1_000_000_000n)]), [[mint.publicKey, false, true], [ata(mint.publicKey, owner.publicKey), false, true], [admin.publicKey, true, false]]), // MintTo
], [admin, mint], "mint+fund");
out.setup = r; console.log("setup", r); save();
if (r.err) process.exit(1);

for (let k = 1; k <= ROUNDS; k++) {
  const rd: any = { k, steps: [] };
  out.rounds.push(rd);
  const step = (x: any) => { rd.steps.push(x); console.log(k, x.what, x.err ?? "ok", x.ms, "ms", x.sig?.slice(0, 16)); save(); return x; };
  try {
    // 2. deposit 100 RUSH into the rollup (both users get an eATA the first time)
    const dep = await sdk.delegateSpl(owner.publicKey, mint.publicKey, 100_000_000n, { validator: VALIDATOR, initIfMissing: true, initVaultIfMissing: k === 1, initAtasIfMissing: false } as any);
    if (step(await send(base, dep, [owner], "deposit+delegate (base)")).err) continue;
    if (k === 1) {
      const dep2 = await sdk.delegateSpl(other.publicKey, mint.publicKey, 0n, { validator: VALIDATOR, initIfMissing: true, payer: owner.publicKey } as any);
      step(await send(base, dep2, [owner, other], "other eATA delegate (base)"));
    }
    // wait until the rollup sees the owner's balance
    const oAta = ata(mint.publicKey, owner.publicKey), xAta = ata(mint.publicKey, other.publicKey);
    const tw = Date.now(); let seen: bigint | null = null;
    for (; Date.now() - tw < 30_000; ) { seen = await amountOf(er, oAta); if (seen != null && seen > 0n) break; await sleep(500); }
    rd.rollupSeenMs = Date.now() - tw; rd.rollupOwnerAmount = String(seen);
    // 3a. transfer inside the rollup, owner-signed (plain SPL Token transfer on the rollup's view of the ATAs)
    const tr = await sdk.transferSpl(owner.publicKey, other.publicKey, mint.publicKey, 10_000_000n, { visibility: "public", fromBalance: "ephemeral", toBalance: "ephemeral" } as any);
    step(await send(er, tr, [owner], "transfer in rollup (owner)", true));
    // 3b. session key as SPL delegate: approve(owner → session, 20) then transferChecked signed by the session key
    step(await send(er, [tokIx(Buffer.concat([Buffer.from([4]), u64(20_000_000n)]), [[oAta, false, true], [session.publicKey, false, false], [owner.publicKey, true, false]])], [owner], "approve session (rollup)", true));
    step(await send(er, [tokIx(Buffer.concat([Buffer.from([3]), u64(5_000_000n)]), [[oAta, false, true], [xAta, false, true], [session.publicKey, true, false]])], [owner, session], "transfer in rollup (session delegate)", true));
    rd.afterTransfers = { owner: String(await amountOf(er, oAta)), other: String(await amountOf(er, xAta)) };
    // 5. withdraw back to Solana
    const wd = await sdk.withdrawSpl(owner.publicKey, mint.publicKey, 50_000_000n, { validator: VALIDATOR } as any);
    const w = step(await send(er, wd, [owner], "withdraw (rollup → Solana)", true));
    if (w.err) { const wb = step(await send(base, wd, [owner], "withdraw (base)")); void wb; }
    const tb = Date.now(); let onBase: bigint | null = null;
    for (; Date.now() - tb < 45_000; ) { onBase = await amountOf(base, oAta); if (onBase != null && onBase >= 900_000_000n) break; await sleep(1500); }
    rd.baseOwnerAmount = String(onBase); rd.baseSeenMs = Date.now() - tb;
    rd.ok = rd.steps.every((s: any) => !s.err) && onBase != null && onBase >= 900_000_000n;
  } catch (e: any) { rd.error = String(e?.message ?? e).slice(0, 500); console.log("round error", rd.error); }
  save();
}
out.pass = out.rounds.filter((x: any) => x.ok).length;
out.finished = new Date().toISOString();
save();
console.log(`espl spike: ${out.pass}/${ROUNDS} round trips ok → ${OUT}`);
process.exit(0);
