// P6 task 1: devnet setup of the sealed accounts (escrow → devnet-as, book → the TEE validator) for arenas 2 + 3, and
// spike 2 on the Fall Rush program itself: BOOK_OPEN (EphemeralPermission created + private, no members) → a PICK
// signed by a fresh wallet on the TEE → raw reads (outsider token, no token, the game authority's own token) →
// BOOK_CLOSE (public) → reads again → BOOK_OPEN (private again).
//   cd tools/keeper && P2_NET=devnet npx tsx ../p6/setup-sealed.ts
import { Keypair, PublicKey, SystemProgram, Transaction, sendAndConfirmTransaction } from "@solana/web3.js";
import nacl from "tweetnacl";
import bs58 from "bs58";
import { readFileSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";
import { base, pda, wallet, ROOT } from "../keeper/lib.ts";
import { setupSealed, bookOpen, bookClose, p6pda, p6Ix, OP6, TEE_URL, teeToken, readBook, decodeBook } from "../keeper/p6.ts";

const OUT = join(ROOT, "docs/proof/p6/sealed-setup.json");
const R: any = { at: new Date().toISOString(), steps: {} };
const save = () => writeFileSync(OUT, JSON.stringify(R, (k, v) => typeof v === "bigint" ? v.toString() : v, 2));
const admin = Keypair.fromSecretKey(Uint8Array.from(JSON.parse(readFileSync(join(homedir(), ".config/solana/id.json"), "utf8"))));
async function tokenFor(kp: Keypair) {
  const c = await (await fetch(`${TEE_URL}/auth/challenge?pubkey=${kp.publicKey.toBase58()}`)).json() as any;
  const sig = nacl.sign.detached(new Uint8Array(Buffer.from(c.challenge, "utf-8")), kp.secretKey);
  const j = await (await fetch(`${TEE_URL}/auth/login`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ pubkey: kp.publicKey.toBase58(), challenge: c.challenge, signature: bs58.encode(sig) }) })).json() as any;
  return j.token as string;
}
async function raw(url: string, k: PublicKey) {
  const r = await fetch(url, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ jsonrpc: "2.0", id: 1, method: "getAccountInfo", params: [k.toBase58(), { encoding: "base64", commitment: "processed" }] }) });
  return { http: r.status, body: (await r.text()).slice(0, 400) };
}

for (const id of [2, 3]) { R.steps[`setup_${id}`] = await setupSealed(id); save(); console.log("setup", id, R.steps[`setup_${id}`]); }
const arena = pda.arena(2);
const book = p6pda.book(arena);
// wait until the TEE sees the delegated book
const { Connection } = await import("@solana/web3.js");
const teeAuth = new Connection(`${TEE_URL}?token=${await teeToken()}`, "processed");
for (let i = 0; i < 60; i++) { if (await teeAuth.getAccountInfo(book).catch(() => null)) break; await new Promise((r) => setTimeout(r, 1000)); }
const dummyMatch = Keypair.generate().publicKey;
const t0 = Date.now();
R.steps.bookOpen = await bookOpen(2, dummyMatch); R.steps.bookOpenMs = Date.now() - t0; save(); console.log("book_open", R.steps.bookOpen);
// a fresh bettor picks on the TEE (signs itself; in the product its session key signs)
const W = Keypair.generate();
await sendAndConfirmTransaction(base, new Transaction().add(SystemProgram.transfer({ fromPubkey: admin.publicKey, toPubkey: W.publicKey, lamports: 10_000_000 })), [admin]);
R.bettor = W.publicKey.toBase58();
const wTok = await tokenFor(W);
const wConn = new Connection(`${TEE_URL}?token=${wTok}`, "processed");
const salt = nacl.randomBytes(16);
const ix = await p6Ix(W.publicKey, OP6.PICK, 4, 5_000_000, salt, [{ pubkey: book, isSigner: false, isWritable: true }, { pubkey: W.publicKey, isSigner: false, isWritable: false }]);
for (let i = 0; i < 30 && !R.steps.pick; i++) {
  try {
    const tx = new Transaction().add(ix); tx.feePayer = W.publicKey; tx.recentBlockhash = (await wConn.getLatestBlockhash()).blockhash; tx.sign(W);
    const t1 = Date.now();
    const sig = await wConn.sendRawTransaction(tx.serialize(), { skipPreflight: true });
    for (let k = 0; k < 60; k++) { const s = (await wConn.getSignatureStatuses([sig])).value[0]; if (s) { R.steps.pick = { sig, err: s.err, ms: Date.now() - t1 }; break; } await new Promise((r) => setTimeout(r, 200)); }
  } catch (e: any) { R.steps.pickErr = String(e?.message ?? e).slice(0, 300); await new Promise((r) => setTimeout(r, 1000)); }
}
save(); console.log("pick", R.steps.pick, R.steps.pickErr ?? "");
const outsider = Keypair.generate();
const oTok = await tokenFor(outsider);
R.privateReads = { outsider: await raw(`${TEE_URL}?token=${oTok}`, book), bettorItself: await raw(`${TEE_URL}?token=${wTok}`, book), gameAuthority: await raw(`${TEE_URL}?token=${await teeToken()}`, book), noToken: await raw(TEE_URL, book), solanaBase: await raw("https://api.devnet.solana.com", book) };
save(); console.log("private reads", JSON.stringify(R.privateReads).slice(0, 600));
const t2 = Date.now();
R.steps.bookClose = await bookClose(2); R.steps.closeToPublicMs = Date.now() - t2;
const pub = await readBook(2, new Connection(`${TEE_URL}?token=${oTok}`, "processed"));
R.publicAfterClose = pub ? { n: pub.n, closed: pub.closed, entry0: pub.entries[0] && { wallet: pub.entries[0].wallet.toBase58(), outcome: pub.entries[0].outcome, amount: pub.entries[0].amount, saltOk: Buffer.from(salt).equals(pub.entries[0].salt) } } : null;
save(); console.log("after close (outsider reads)", R.publicAfterClose, R.steps.closeToPublicMs, "ms");
R.steps.reopen = await bookOpen(2, dummyMatch);
R.reopenedRead = await raw(`${TEE_URL}?token=${oTok}`, book);
save(); console.log("reopen", R.steps.reopen, R.reopenedRead.body.slice(0, 120));
const t3 = await bookOpen(3, dummyMatch); R.steps.bookOpen3 = t3; save(); console.log("arena 3 book", t3);
