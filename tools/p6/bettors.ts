// P6 task 1 / 6.1: scripted sealed bettors for a keeper-driven match on ARENA (devnet). For each of N wallets:
// PICK (outcome + amount + salt) on the private book (TEE), then COMMIT (stake + sha256 commitment) on devnet-as, while
// the Champion window is open. Mid-market privacy evidence: the book read raw by an outsider (TEE), the escrow read
// raw on devnet-as (commitments only). After the match: reveals, forfeits, payouts vs a hand calculation.
//   cd tools/keeper && P2_NET=devnet ARENA=3 N=5 [NOREVEAL=1] npx tsx ../p6/bettors.ts <out.json>
import { Connection, Keypair, PublicKey, SystemProgram, Transaction } from "@solana/web3.js";
import nacl from "tweetnacl";
import bs58 from "bs58";
import { createHash } from "node:crypto";
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";
import { base, er, pda, pe, sleep, sendBase, sendEr, waitSig, ROOT } from "../keeper/lib.ts";
import * as E from "../keeper/econ.ts";
import { p6Ix, OP6, p6pda, escrow, TEE_URL, decodeBook } from "../keeper/p6.ts";

const ARENA = Number(process.env.ARENA ?? 3);
const NB = Number(process.env.N ?? 5);
const OUT = process.argv[2] ?? join(ROOT, `docs/proof/p6/bettors-${Date.now()}.json`);
const R: any = { at: new Date().toISOString(), arena: ARENA, bettors: [] };
const save = () => writeFileSync(OUT, JSON.stringify(R, (k, v) => (typeof v === "bigint" ? v.toString() : v instanceof PublicKey ? v.toBase58() : v), 2));
const admin = Keypair.fromSecretKey(Uint8Array.from(JSON.parse(readFileSync(join(homedir(), ".config/solana/id.json"), "utf8"))));
const arena = pda.arena(ARENA);
const w_ = (pubkey: PublicKey) => ({ pubkey, isSigner: false, isWritable: true });
const r_ = (pubkey: PublicKey) => ({ pubkey, isSigner: false, isWritable: false });
async function tokenFor(kp: Keypair) {
  const c = await (await fetch(`${TEE_URL}/auth/challenge?pubkey=${kp.publicKey.toBase58()}`)).json() as any;
  const sig = nacl.sign.detached(new Uint8Array(Buffer.from(c.challenge, "utf-8")), kp.secretKey);
  const j = await (await fetch(`${TEE_URL}/auth/login`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ pubkey: kp.publicKey.toBase58(), challenge: c.challenge, signature: bs58.encode(sig) }) })).json() as any;
  return j.token as string;
}
async function raw(url: string, k: PublicKey) {
  const r = await fetch(url, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ jsonrpc: "2.0", id: 1, method: "getAccountInfo", params: [k.toBase58(), { encoding: "base64", commitment: "processed" }] }) });
  return { http: r.status, body: (await r.text()).slice(0, 300) };
}

// wallets (persisted in the scratch keys dir so a re-run reuses them)
const KEYF = join(ROOT, "tools/keeper/.keys/devnet-p6-bettors.json");
const keys: Keypair[] = existsSync(KEYF) ? JSON.parse(readFileSync(KEYF, "utf8")).map((s: number[]) => Keypair.fromSecretKey(Uint8Array.from(s))) : [];
while (keys.length < NB) keys.push(Keypair.generate());
writeFileSync(KEYF, JSON.stringify(keys.map((k) => Array.from(k.secretKey))), { mode: 0o600 });
const bettors = keys.slice(0, NB);
for (const k of bettors) {
  const b = await base.getBalance(k.publicKey).catch(() => 0);
  if (b < 8_000_000) await sendBase([SystemProgram.transfer({ fromPubkey: admin.publicKey, toPubkey: k.publicKey, lamports: 12_000_000 - b })], [admin], "fund bettor");
}
for (const k of bettors) { const c = await E.chipsOf(k.publicKey); if (!c || c.balance < 100n * E.ONE) await E.ensureChips(k); }
console.log("bettors ready", bettors.map((k) => k.publicKey.toBase58().slice(0, 6)));
// wait for the next match's escrow to open (the keeper opens it right after open_markets)
const a0: any = await pe.account.arena.fetch(arena);
const prev = (await escrow(ARENA))?.match.toBase58();
let matchPda: PublicKey | null = null;
for (const t = Date.now(); Date.now() - t < 15 * 60_000 && !matchPda;) {
  const e = await escrow(ARENA).catch(() => null);
  if (e && e.state === 1 && e.match.toBase58() !== prev) matchPda = e.match;
  else if (e && e.state === 1 && process.env.ANY === "1") matchPda = e.match;
  await sleep(1000);
}
if (!matchPda) { console.log("no open escrow"); process.exit(1); }
R.match = matchPda.toBase58(); save();
console.log("match", R.match, "(arena was", a0.matchAccount.toBase58().slice(0, 6), ")");
const book = p6pda.book(arena);
for (let i = 0; i < bettors.length; i++) {
  const k = bettors[i];
  const outcome = [0, 3, 5, 7, 9, 11, 2][i % 7];
  const amount = BigInt(5 + 3 * i) * E.ONE;
  const salt = Buffer.from(nacl.randomBytes(16));
  const tok = await tokenFor(k);
  const tee = new Connection(`${TEE_URL}?token=${tok}`, "processed");
  const ix = await p6Ix(k.publicKey, OP6.PICK, outcome, amount, salt, [w_(book), r_(k.publicKey)]);
  const t0 = Date.now();
  let pick: any = null;
  for (let j = 0; j < 10 && !pick; j++) {
    try {
      const tx = new Transaction().add(ix); tx.feePayer = k.publicKey; tx.recentBlockhash = (await tee.getLatestBlockhash()).blockhash; tx.sign(k);
      const sig = await tee.sendRawTransaction(tx.serialize(), { skipPreflight: true });
      const st = await waitSig(tee, sig, 100, 15_000);
      pick = { sig, err: st.err, ms: Date.now() - t0 };
    } catch (e: any) { await sleep(800); }
  }
  const amt = Buffer.alloc(8); amt.writeBigUInt64LE(amount);
  const hash = createHash("sha256").update(Buffer.concat([Buffer.from([outcome]), amt, salt, k.publicKey.toBuffer()])).digest();
  const accs = [w_(E.epda.chips(k.publicKey)), w_(p6pda.escrow(arena)), w_(E.epda.market(ARENA, 0)), r_(matchPda), r_(arena), r_(pda.world(arena))];
  const c = await sendEr([await p6Ix(k.publicKey, OP6.COMMIT, amount, 0, hash, accs)], [k], "commit", 6);
  R.bettors.push({ wallet: k.publicKey, outcome, amount, salt: salt.toString("hex"), hash: hash.toString("hex"), pick, commit: c, commitMs: c.ms });
  save(); console.log("bettor", i, "pick", pick?.ms, pick?.err ?? "", "commit", c.ms, c.err ?? "");
}
// mid-market privacy evidence (window still open)
const outsider = Keypair.generate();
R.privacy = { at: Date.now(), bookOutsider: await raw(`${TEE_URL}?token=${await tokenFor(outsider)}`, book), bookNoToken: await raw(TEE_URL, book), bookSolana: await raw("https://api.devnet.solana.com", book) };
const es = await escrow(ARENA);
R.privacy.escrowPublic = es && { n: es.n, pool: es.pool, entries: es.entries.map((x) => ({ wallet: x.wallet, amount: x.amount, hash: x.hash, outcome: x.outcome })) };
save(); console.log("privacy", R.privacy.bookOutsider.body.slice(0, 120));
// wait for the reveal and the settlement
let closedAt = 0, revealedAt = 0;
for (const t = Date.now(); Date.now() - t < 20 * 60_000;) {
  const e = await escrow(ARENA).catch(() => null);
  if (e && e.match.equals(matchPda)) {
    if (e.state >= 2 && !closedAt) { closedAt = Date.now(); R.closeSeenAt = closedAt; }
    if (e.state >= 2 && e.nRevealed === e.n && !revealedAt) { revealedAt = Date.now(); R.revealedSeenAt = revealedAt; R.closeToRevealMs = revealedAt - closedAt; R.escrowAfter = e; save(); console.log("revealed", e.nRevealed, "/", e.n, R.closeToRevealMs, "ms after close seen"); }
    if (e.state === 3) { R.escrowDone = e; break; }
  }
  await sleep(250);
}
const bkInfo = await new Connection(`${TEE_URL}?token=${await tokenFor(outsider)}`, "processed").getAccountInfo(book).catch(() => null);
R.bookAfter = bkInfo ? decodeBook(bkInfo.data as Buffer) : null;
for (const t = Date.now(); Date.now() - t < 10 * 60_000;) { const m = await E.market(ARENA, 0); if (m && m.match.equals(matchPda) && m.status === 3) { R.market0 = m; break; } await sleep(2000); }
save();
console.log("done", OUT);
