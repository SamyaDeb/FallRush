// P6 task 1, Path A spike (doc 19 P6.1, prompt §2.1): MagicBlock Private Ephemeral Rollup (TEE) on devnet.
// Uses MagicBlock's own deployed `private-counter` example program (devnet 2WJiwbdn…, IDL tools/p6/private_counter.idl.json)
// so the spike needs no Fall Rush upgrade. Steps (each timed, raw responses saved):
//   verifyTeeRpcIntegrity → getAuthToken (member A, outsider O) → base initialize + delegate to the TEE validator →
//   TEE init_permission + set_privacy(true) (members = [A]) → increments → reads as A / O / no token →
//   set_privacy(false) ("reveal") → reads again → commit + undelegate to Solana.
//   cd tools/keeper && npx tsx ../p6/tee-spike.ts   (admin wallet funds A with 0.03 SOL)
import { AnchorProvider, Program, Wallet } from "@coral-xyz/anchor";
import { Connection, Keypair, PublicKey, SystemProgram, Transaction, sendAndConfirmTransaction } from "@solana/web3.js";
import { verifyTeeRpcIntegrity, getAuthToken } from "@magicblock-labs/ephemeral-rollups-sdk";
import nacl from "tweetnacl";
import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

const ROOT = new URL("../../", import.meta.url).pathname;
const OUT = join(ROOT, "docs/proof/p6/tee-spike.json");
mkdirSync(join(ROOT, "docs/proof/p6"), { recursive: true });
const TEE = "https://devnet-tee.magicblock.app";
const TEE_VALIDATOR = new PublicKey("MTEWGuqxUpYZGFJQcp8tLN7x5v9BSeoFHYWQQ3n3xzo");
const base = new Connection("https://api.devnet.solana.com", "confirmed");
const admin = Keypair.fromSecretKey(Uint8Array.from(JSON.parse(readFileSync(join(homedir(), ".config/solana/id.json"), "utf8"))));
const idl = JSON.parse(readFileSync(new URL("./private_counter.idl.json", import.meta.url), "utf8"));
const PID = new PublicKey(idl.address);
const R: any = { at: new Date().toISOString(), program: PID.toBase58(), tee: TEE, validator: TEE_VALIDATOR.toBase58(), steps: [] };
const save = () => writeFileSync(OUT, JSON.stringify(R, null, 2));
const t0 = () => performance.now();
async function step<T>(name: string, f: () => Promise<T>): Promise<T | undefined> {
  const s = t0();
  try { const v = await f(); R.steps.push({ name, ok: true, ms: Math.round(t0() - s), v: typeof v === "string" ? v : v === undefined ? null : v }); save(); console.log("✓", name, Math.round(t0() - s), "ms"); return v; }
  catch (e: any) { R.steps.push({ name, ok: false, ms: Math.round(t0() - s), err: String(e?.message ?? e).slice(0, 600), logs: e?.logs?.slice?.(-12) }); save(); console.log("✗", name, String(e?.message ?? e).slice(0, 300)); return undefined; }
}
const sign = (kp: Keypair) => (m: Uint8Array) => Promise.resolve(nacl.sign.detached(m, kp.secretKey));
/** Raw getAccountInfo JSON-RPC (so the proof shows exactly what a reader gets back). */
async function rawRead(url: string, k: PublicKey) {
  const r = await fetch(url, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ jsonrpc: "2.0", id: 1, method: "getAccountInfo", params: [k.toBase58(), { encoding: "base64", commitment: "processed" }] }) });
  const txt = await r.text();
  return { http: r.status, body: txt.slice(0, 700) };
}

const A = Keypair.generate(), O = Keypair.generate();
R.member = A.publicKey.toBase58(); R.outsider = O.publicKey.toBase58();
const counter = PublicKey.findProgramAddressSync([Buffer.from("counter"), A.publicKey.toBuffer()], PID)[0];
R.counter = counter.toBase58();

await step("verifyTeeRpcIntegrity", async () => { await verifyTeeRpcIntegrity(TEE); return "attested"; });
const ta = await step("getAuthToken(member)", async () => { const t = await getAuthToken(TEE, A.publicKey, sign(A)); R.tokenExpiresAt = t.expiresAt; return t.token; });
const to = await step("getAuthToken(outsider)", async () => (await getAuthToken(TEE, O.publicKey, sign(O))).token);
if (!ta || !to) { console.log("no tokens, stop"); process.exit(1); }
const teeA = new Connection(`${TEE}?token=${ta}`, { commitment: "processed", wsEndpoint: `${TEE.replace("https", "wss")}?token=${ta}` });
const progBase = new Program(idl, new AnchorProvider(base, new Wallet(A), { commitment: "confirmed" }));
const progTee = new Program(idl, new AnchorProvider(teeA, new Wallet(A), { commitment: "processed", skipPreflight: true } as any));

await step("fund member 0.03 SOL", () => sendAndConfirmTransaction(base, new Transaction().add(SystemProgram.transfer({ fromPubkey: admin.publicKey, toPubkey: A.publicKey, lamports: 30_000_000 })), [admin]));
await step("base initialize", () => progBase.methods.initialize().accountsPartial({ authority: A.publicKey }).rpc());
await step("base delegate → TEE validator", () => progBase.methods.delegate().accountsPartial({ authority: A.publicKey, validator: TEE_VALIDATOR }).rpc());
async function teeSend(name: string, ixP: Promise<any>) {
  return step(name, async () => {
    const ix = await ixP;
    const tx = new Transaction().add(ix);
    tx.feePayer = A.publicKey;
    tx.recentBlockhash = (await teeA.getLatestBlockhash()).blockhash;
    tx.sign(A);
    const sig = await teeA.sendRawTransaction(tx.serialize(), { skipPreflight: true });
    for (let i = 0; i < 60; i++) {
      const s = (await teeA.getSignatureStatuses([sig])).value[0];
      if (s?.err) throw new Error(`${sig} ${JSON.stringify(s.err)}`);
      if (s?.confirmationStatus) return sig;
      await new Promise((r) => setTimeout(r, 250));
    }
    throw new Error(`${sig} unconfirmed`);
  });
}
// wait for the TEE to see the delegated counter
await step("TEE sees counter", async () => { for (let i = 0; i < 60; i++) { const x = await teeA.getAccountInfo(counter).catch(() => null); if (x) return `owner ${x.owner.toBase58()}`; await new Promise((r) => setTimeout(r, 500)); } throw new Error("not visible"); });
await teeSend("TEE init_permission", progTee.methods.initPermission().accountsPartial({ authority: A.publicKey, counter }).instruction());
await teeSend("TEE set_privacy(true) members=[A]", progTee.methods.setPrivacy(true).accountsPartial({ authority: A.publicKey, counter }).instruction());
for (let i = 1; i <= 3; i++) await teeSend(`TEE increment ${i}`, progTee.methods.increment().accountsPartial({ counter }).instruction());
R.privateReads = {
  member: await rawRead(`${TEE}?token=${ta}`, counter),
  outsider: await rawRead(`${TEE}?token=${to}`, counter),
  noToken: await rawRead(TEE, counter),
  baseLayer: await rawRead("https://api.devnet.solana.com", counter),
};
save(); console.log("private reads", JSON.stringify(R.privateReads).slice(0, 900));
const rv = t0();
await teeSend("TEE set_privacy(false) (reveal)", progTee.methods.setPrivacy(false).accountsPartial({ authority: A.publicKey, counter }).instruction());
R.revealMs = Math.round(t0() - rv);
R.publicReads = { outsider: await rawRead(`${TEE}?token=${to}`, counter), noToken: await rawRead(TEE, counter) };
save(); console.log("public reads", JSON.stringify(R.publicReads).slice(0, 700));
const cu = t0();
await teeSend("TEE close_permission", progTee.methods.closePermission().accountsPartial({ authority: A.publicKey, counter }).instruction());
await teeSend("TEE commit+undelegate", progTee.methods.undelegate().accountsPartial({ payer: A.publicKey, counter }).instruction());
await step("counter back on Solana", async () => { for (let i = 0; i < 90; i++) { const x = await base.getAccountInfo(counter); if (x && x.owner.equals(PID)) return `count=${x.data.readBigUInt64LE(8)}`; await new Promise((r) => setTimeout(r, 1000)); } throw new Error("still delegated after 90 s"); });
R.undelegateMs = Math.round(t0() - cu);
save();
console.log("done →", OUT);
