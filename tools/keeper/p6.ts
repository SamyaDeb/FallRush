// P6 keeper side (programs/fall_rush/src/p6.rs): sealed Champion bets (escrow on devnet-as, private book on the
// MagicBlock TEE rollup), seasons + achievements (Solana), the TEE auth token. Every call is a `p6(op, a, b, data)`
// instruction of fall_rush; the keeper is only the trigger (close / reveal / finish / record are permissionless).
import { Connection, PublicKey, SystemProgram } from "@solana/web3.js";
import type { AccountMeta, Keypair, TransactionInstruction } from "@solana/web3.js";
import nacl from "tweetnacl";
import bs58 from "bs58";
import { NET, base, er, wallet, pb, pe, pda, PROGRAM_ID, MAGIC_PROGRAM, MAGIC_CONTEXT, sendBase, sendEr, sleep, cu, BN, log, waitSig } from "./lib.ts";
import { epda, MARKETS, CORE } from "./econ.ts";
import { Transaction } from "@solana/web3.js";

export const OP6 = { INIT: 1, ESCROW_OPEN: 2, COMMIT: 3, BOOK_OPEN: 4, PICK: 5, BOOK_CLOSE: 6, CLOSE: 7, REVEAL: 8, FINISH: 9, OPEN_SEASON: 10, RECORD: 11, INIT_PSEASON: 12, INIT_PROFILE: 13, CLOSE_SEASON: 14, MINT_TROPHY: 15, COMMIT_ECON: 16 };
export const TEE_URL = process.env.TEE_URL ?? "https://devnet-tee.magicblock.app";
export const TEE_VALIDATOR = new PublicKey(process.env.TEE_VALIDATOR ?? "MTEWGuqxUpYZGFJQcp8tLN7x5v9BSeoFHYWQQ3n3xzo");
export const PERMISSION_PROGRAM = new PublicKey("ACLseoPoyC3cBqoUtkbjZ4aDrkurZW86v19pXz2XQnp1");
export const EPHEMERAL_VAULT = new PublicKey("MagicVau1t999999999999999999999999999999999");
export const REVEAL_WINDOW_S = 20;
export const TEST_SEASON_BASE = 1_000_000;
const P = (seeds: (Buffer | Uint8Array)[]) => PublicKey.findProgramAddressSync(seeds, PROGRAM_ID)[0];
const u32le = (n: number) => { const b = Buffer.alloc(4); b.writeUInt32LE(n); return b; };
export const p6pda = {
  escrow: (arena: PublicKey) => P([Buffer.from("sealed"), arena.toBuffer()]),
  book: (arena: PublicKey) => P([Buffer.from("sbook"), arena.toBuffer()]),
  permission: (acc: PublicKey) => PublicKey.findProgramAddressSync([Buffer.from("permission:"), acc.toBuffer()], PERMISSION_PROGRAM)[0],
  season: (id: number) => P([Buffer.from("season"), u32le(id)]),
  pseason: (id: number, w: PublicKey) => P([Buffer.from("pseason"), u32le(id), w.toBuffer()]),
  profile: (w: PublicKey) => P([Buffer.from("profile"), w.toBuffer()]),
  trophy: (season: PublicKey, k: number) => P([Buffer.from("trophy"), season.toBuffer(), Buffer.from([k])]),
};
const w_ = (pubkey: PublicKey): AccountMeta => ({ pubkey, isSigner: false, isWritable: true });
const r_ = (pubkey: PublicKey): AccountMeta => ({ pubkey, isSigner: false, isWritable: false });

export async function p6Ix(signer: PublicKey, op: number, a: number | bigint, b: number | bigint, data: Uint8Array | number[], accs: AccountMeta[], onEr = true): Promise<TransactionInstruction> {
  const d = Array.from({ length: 32 }, (_, i) => data[i] ?? 0);
  return (onEr ? pe : pb).methods.p6(op, new BN(a.toString()), new BN(b.toString()), d).accountsPartial({ signer }).remainingAccounts(accs).instruction();
}

// ------------------------------------------------------------------ TEE (private rollup) connection
let teeTok: { token: string; exp: number } | null = null;
/** TEE auth token for the game authority (challenge signed with its key; ~30 days). */
export async function teeToken(kp: Keypair = wallet): Promise<string> {
  if (teeTok && teeTok.exp > Date.now() + 60_000) return teeTok.token;
  const c = await (await fetch(`${TEE_URL}/auth/challenge?pubkey=${kp.publicKey.toBase58()}`, { signal: AbortSignal.timeout(15_000) })).json() as any;
  if (!c.challenge) throw new Error(`tee challenge: ${JSON.stringify(c).slice(0, 200)}`);
  const sig = nacl.sign.detached(new Uint8Array(Buffer.from(c.challenge, "utf-8")), kp.secretKey);
  const r = await fetch(`${TEE_URL}/auth/login`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ pubkey: kp.publicKey.toBase58(), challenge: c.challenge, signature: bs58.encode(sig) }), signal: AbortSignal.timeout(15_000) });
  const j = await r.json() as any;
  if (r.status !== 200 || !j.token) throw new Error(`tee login: ${JSON.stringify(j).slice(0, 200)}`);
  teeTok = { token: j.token, exp: j.expiresAt ?? Date.now() + 86_400_000 };
  return j.token;
}
let teeConn: Connection | null = null;
export async function tee(): Promise<Connection> {
  const t = await teeToken();
  if (!teeConn || !(teeConn as any).rpcEndpoint.includes(t)) teeConn = new Connection(`${TEE_URL}?token=${t}`, { commitment: "processed" });
  return teeConn;
}
export async function sendTee(ixs: TransactionInstruction[], what = "", tries = 10): Promise<{ sig: string; ms: number; err?: string }> {
  let last = "";
  for (let i = 0; i < tries; i++) {
    const t0 = Date.now();
    try {
      const c = await tee();
      const tx = new Transaction().add(...ixs);
      tx.feePayer = wallet.publicKey;
      tx.recentBlockhash = (await c.getLatestBlockhash("processed")).blockhash;
      tx.sign(wallet);
      const sig = await c.sendRawTransaction(tx.serialize(), { skipPreflight: true, maxRetries: 0 });
      const r = await waitSig(c, sig, 100, 15_000);
      if (!r.err) return { sig, ms: Date.now() - t0 };
      last = r.err;
      if (!/pending|AccountNotFound|cloner|timeout|Blockhash/i.test(last)) return { sig, ms: Date.now() - t0, err: last };
    } catch (e: any) { last = String(e?.message ?? e).slice(0, 300); }
    await sleep(700);
  }
  return { sig: "", ms: 0, err: `${what}: ${last}` };
}

// ------------------------------------------------------------------ decoders
export interface SealedEntry { wallet: PublicKey; amount: bigint; hash: string; outcome: number; state: number }
export function decodeEscrow(d: Buffer) {
  const n = d[8 + 88];
  const entries: SealedEntry[] = Array.from({ length: n }, (_, i) => { const o = 8 + 96 + 80 * i; return { wallet: new PublicKey(d.subarray(o, o + 32)), amount: d.readBigUInt64LE(o + 32), hash: d.subarray(o + 40, o + 72).toString("hex"), outcome: d[o + 72], state: d[o + 73] }; });
  return { arena: new PublicKey(d.subarray(8, 40)), match: new PublicKey(d.subarray(40, 72)), closedAt: Number(d.readBigInt64LE(8 + 64)), pool: d.readBigUInt64LE(8 + 72), forfeit: d.readBigUInt64LE(8 + 80), n, nRevealed: d[8 + 89], state: d[8 + 90], nForfeit: d[8 + 91], entries };
}
export interface BookEntry { wallet: PublicKey; amount: bigint; salt: Buffer; outcome: number }
export function decodeBook(d: Buffer) {
  const n = d[8 + 64];
  const entries: BookEntry[] = Array.from({ length: n }, (_, i) => { const o = 8 + 72 + 64 * i; return { wallet: new PublicKey(d.subarray(o, o + 32)), amount: d.readBigUInt64LE(o + 32), salt: Buffer.from(d.subarray(o + 40, o + 56)), outcome: d[o + 56] }; });
  return { arena: new PublicKey(d.subarray(8, 40)), match: new PublicKey(d.subarray(40, 72)), n, closed: d[8 + 65], entries };
}
const board = (d: Buffer, o: number) => Array.from({ length: 10 }, (_, i) => ({ wallet: new PublicKey(d.subarray(o + 40 * i, o + 40 * i + 32)), score: d.readBigInt64LE(o + 40 * i + 32) })).filter((e) => !e.wallet.equals(PublicKey.default));
export function decodeSeason(d: Buffer) {
  const b = 8;
  return { id: d.readUInt32LE(b), status: d[b + 4], trophies: d[b + 5], test: d[b + 7], startsAt: Number(d.readBigInt64LE(b + 8)), endsAt: Number(d.readBigInt64LE(b + 16)), nMatches: d.readUInt32LE(b + 24), nPlayers: d.readUInt32LE(b + 28), champions: board(d, b + 32), predictors: board(d, b + 432), dropMvps: board(d, b + 832) };
}
export function decodePSeason(d: Buffer) {
  const b = 8, u16 = (o: number) => d.readUInt16LE(b + o);
  return { wallet: new PublicKey(d.subarray(b, b + 32)), season: d.readUInt32LE(b + 32), achievements: d.readUInt32LE(b + 36), net: d.readBigInt64LE(b + 40), crowns: u16(48), finals: u16(50), roundsSurvived: u16(52), matches: u16(54), betsWon: u16(56), betsLost: u16(58), drops: u16(60) };
}

// ------------------------------------------------------------------ setup (Solana: init + delegate; TEE: permission)
async function baseInfo(k: PublicKey) {
  for (let i = 0; ; i++) { try { return await base.getAccountInfo(k, "confirmed"); } catch (e) { if (i >= 8) throw e; await sleep(1500 * (i + 1)); } }
}
export async function setupSealed(arenaId: number, out: Record<string, string> = {}) {
  const arena = pda.arena(arenaId);
  const cfg = pda.config();
  for (const [b, acc, kind, validator] of [[0, p6pda.escrow(arena), 7, null], [1, p6pda.book(arena), 8, TEE_VALIDATOR]] as const) {
    if (!(await baseInfo(acc))) out[`init_${b}`] = await sendBase([await p6Ix(wallet.publicKey, OP6.INIT, arenaId, b, [], [r_(cfg), r_(arena), w_(acc), r_(SystemProgram.programId)], false)], [wallet], `p6 init ${b}`);
    let now = await baseInfo(acc);
    for (let i = 0; i < 60 && !now; i++) { await sleep(1000); now = await baseInfo(acc); }
    if (now && now.owner.equals(PROGRAM_ID)) {
      // the escrow goes to the arena's rollup (devnet-as, the default validator); the book to the TEE validator
      const v = validator ?? (await import("./lib.ts")).EP.validator;
      out[`delegate_${b}`] = await sendBase([await pb.methods.delegateSim(kind, arenaId, 0).accountsPartial({ authority: wallet.publicKey, pda: acc }).remainingAccounts([r_(v)]).instruction()], [wallet], `delegate p6 ${b}`);
    }
  }
  return out;
}

function bookAccs(arena: PublicKey) {
  const book = p6pda.book(arena);
  return [r_(pda.config()), w_(book), w_(p6pda.permission(book)), w_(EPHEMERAL_VAULT), r_(MAGIC_PROGRAM), r_(PERMISSION_PROGRAM)];
}
/** TEE: (re)open the arena's private book for `match` (creates the EphemeralPermission the first time). */
export async function bookOpen(arenaId: number, matchPda: PublicKey) {
  return sendTee([cu(300_000), await p6Ix(wallet.publicKey, OP6.BOOK_OPEN, 0, 0, matchPda.toBytes(), bookAccs(pda.arena(arenaId)))], "book_open");
}
/** TEE: close the book and make it public (the reveal). */
export async function bookClose(arenaId: number) {
  return sendTee([cu(300_000), await p6Ix(wallet.publicKey, OP6.BOOK_CLOSE, 0, 0, [], bookAccs(pda.arena(arenaId)))], "book_close");
}
export async function readBook(arenaId: number, conn?: Connection) {
  const c = conn ?? await tee();
  const i = await c.getAccountInfo(p6pda.book(pda.arena(arenaId))).catch(() => null);
  return i ? decodeBook(i.data as Buffer) : null;
}
export async function escrow(arenaId: number) {
  const i = await er.getAccountInfo(p6pda.escrow(pda.arena(arenaId))).catch(() => null);
  return i ? decodeEscrow(i.data as Buffer) : null;
}
export async function escrowOpen(arenaId: number, matchPda: PublicKey) {
  const arena = pda.arena(arenaId);
  return sendEr([await p6Ix(wallet.publicKey, OP6.ESCROW_OPEN, 0, 0, [], [r_(arena), w_(p6pda.escrow(arena)), r_(epda.market(arenaId, 0)), r_(matchPda)])], [wallet], "escrow_open", 10);
}
export async function sealedClose(arenaId: number, matchPda: PublicKey) {
  const arena = pda.arena(arenaId);
  return sendEr([await p6Ix(wallet.publicKey, OP6.CLOSE, 0, 0, [], [w_(p6pda.escrow(arena)), r_(epda.market(arenaId, 0)), r_(matchPda), r_(arena), r_(pda.world(arena))])], [wallet], "sealed_close", 6);
}
export async function sealedFinish(arenaId: number, matchPda: PublicKey) {
  const arena = pda.arena(arenaId);
  return sendEr([await p6Ix(wallet.publicKey, OP6.FINISH, 0, 0, [], [w_(p6pda.escrow(arena)), w_(epda.market(arenaId, 0)), r_(matchPda)])], [wallet], "sealed_finish", 6);
}
/** Reveals every committed entry whose preimage is in the (now public) book: pairs by the program's commitment. */
export async function revealFromBook(arenaId: number, matchPda: PublicKey, logFile?: string): Promise<{ revealed: number; sigs: string[]; unmatched: number }> {
  const { createHash } = await import("node:crypto");
  const arena = pda.arena(arenaId);
  const e = await escrow(arenaId);
  if (!e || !e.match.equals(matchPda)) return { revealed: 0, sigs: [], unmatched: 0 };
  const bk = await readBook(arenaId);
  const pre = new Map<string, BookEntry>();
  for (const b of bk && bk.match.equals(matchPda) ? bk.entries : []) {
    const amt = Buffer.alloc(8); amt.writeBigUInt64LE(b.amount);
    pre.set(createHash("sha256").update(Buffer.concat([Buffer.from([b.outcome]), amt, b.salt, b.wallet.toBuffer()])).digest("hex"), b);
  }
  const sigs: string[] = [];
  let revealed = 0, unmatched = 0;
  const ixs: TransactionInstruction[] = [];
  for (let i = 0; i < e.entries.length; i++) {
    const x = e.entries[i];
    if (x.state !== 0) continue;
    const b = pre.get(x.hash);
    if (!b) { unmatched++; continue; }
    ixs.push(await p6Ix(wallet.publicKey, OP6.REVEAL, i, b.outcome, b.salt, [w_(p6pda.escrow(arena)), w_(epda.market(arenaId, 0)), r_(matchPda)]));
  }
  for (let i = 0; i < ixs.length; i += 6) {
    const r = await sendEr([cu(200_000), ...ixs.slice(i, i + 6)], [wallet], "reveal", 6);
    if (r.err) { if (logFile) log(logFile, { act: "reveal_failed", err: r.err.slice(0, 200) }); continue; }
    sigs.push(r.sig); revealed += Math.min(6, ixs.length - i);
  }
  return { revealed, sigs, unmatched };
}
/** Commits the arena's 5 markets + drop queue to Solana (the copies `record_season` reads), PER intents of `per`
 * accounts (the validator refuses an intent whose base transaction would not fit). */
export async function commitEcon(arenaId: number, per = Number(process.env.COMMIT_PER ?? 1)) {
  const arena = pda.arena(arenaId);
  const all = [...Array.from({ length: MARKETS }, (_, k) => epda.market(arenaId, k)), epda.drops(arena)];
  const sigs: string[] = [];
  for (let i = 0; i < all.length; i += per) {
    const accs = [...all.slice(i, i + per).map(w_), w_(MAGIC_CONTEXT), r_(MAGIC_PROGRAM)];
    const r = await sendEr([cu(200_000), await p6Ix(wallet.publicKey, OP6.COMMIT_ECON, 0, 0, [], accs)], [wallet], "commit_econ", 10);
    // harden #2: the failing tx's own signature (its ER logs say why), and which accounts it carried
    if (r.err) return { sig: sigs.join(","), err: r.err, failSig: r.sig, failAt: i };
    sigs.push(r.sig);
  }
  return { sig: sigs.join(","), sigs };
}

// ------------------------------------------------------------------ seasons (Solana)
export function isoWeek(ts: number): number {
  const days = Math.floor(ts / 86_400);
  const wd = ((days + 3) % 7 + 7) % 7;
  const thursday = days - wd + 3;
  const y = new Date(thursday * 86_400_000).getUTCFullYear();
  const jan1 = Math.floor(Date.UTC(y, 0, 1) / 86_400_000);
  return y * 100 + Math.floor((thursday - jan1) / 7) + 1;
}
export async function season(id: number) {
  const i = await base.getAccountInfo(p6pda.season(id)).catch(() => null);
  return i && i.owner.equals(PROGRAM_ID) ? decodeSeason(i.data as Buffer) : null;
}
export async function ensureSeason(id: number, lengthS = 0) {
  if (await season(id)) return null;
  return sendBase([await p6Ix(wallet.publicKey, OP6.OPEN_SEASON, id, lengthS, [], [r_(pda.config()), w_(p6pda.season(id)), r_(SystemProgram.programId)], false)], [wallet], `open_season ${id}`);
}
/** Seasons a match completed at `ts` belongs to: the ISO week + the active test season (ACTIVE_TEST_SEASON env or file). */
export function seasonsFor(ts: number): number[] {
  const out = process.env.WEEKLY === "0" ? [] : [isoWeek(ts)];
  const t = Number(process.env.TEST_SEASON ?? 0);
  if (t >= TEST_SEASON_BASE) out.push(t);
  return out;
}
/** Records a finalized match (back on Solana) into `seasonId` for `wallets` (humans + bettors + drop buyers);
 * creates the missing PlayerSeason / champion profile accounts first (the authority pays their rent). */
export async function recordSeason(arenaId: number, matchPda: PublicKey, seasonId: number, wallets: PublicKey[], champion: PublicKey | null, logFile?: string) {
  const arena = pda.arena(arenaId);
  const sigs: string[] = [];
  const missing: PublicKey[] = [];
  const infos = await base.getMultipleAccountsInfo(wallets.map((x) => p6pda.pseason(seasonId, x))).catch(() => wallets.map(() => null));
  wallets.forEach((x, i) => { if (!infos[i]) missing.push(x); });
  for (let i = 0; i < missing.length; i += 4) {
    const ixs = await Promise.all(missing.slice(i, i + 4).map((x) => p6Ix(wallet.publicKey, OP6.INIT_PSEASON, seasonId, 0, [], [w_(p6pda.pseason(seasonId, x)), r_(x), r_(SystemProgram.programId)], false)));
    sigs.push(await sendBase(ixs, [wallet], "init_pseason"));
  }
  const prof = champion ? p6pda.profile(champion) : SystemProgram.programId;
  if (champion && !(await base.getAccountInfo(prof).catch(() => null))) sigs.push(await sendBase([await p6Ix(wallet.publicKey, OP6.INIT_PROFILE, 0, 0, [], [w_(prof), r_(champion), r_(SystemProgram.programId)], false)], [wallet], "init_profile"));
  const head = [r_(matchPda), w_(p6pda.season(seasonId)), r_(epda.drops(arena)), ...Array.from({ length: MARKETS }, (_, k) => r_(epda.market(arenaId, k))), champion ? w_(prof) : r_(prof)];
  for (let i = 0; i < Math.max(1, wallets.length); i += 12) {
    const part = wallets.slice(i, i + 12).map((x) => w_(p6pda.pseason(seasonId, x)));
    const sig = await sendBase([cu(600_000), await p6Ix(wallet.publicKey, OP6.RECORD, 0, 0, [], [...head, ...part], false)], [wallet], `record_season ${seasonId}`);
    sigs.push(sig);
    if (logFile) log(logFile, { act: "record_season", season: seasonId, wallets: part.length, sig });
  }
  return sigs;
}
export async function closeSeasonAndMint(seasonId: number) {
  const s = p6pda.season(seasonId);
  const out: Record<string, string> = {};
  const cur = await season(seasonId);
  if (!cur) throw new Error("no season");
  if (cur.status === 0) out.close = await sendBase([await p6Ix(wallet.publicKey, OP6.CLOSE_SEASON, 0, 0, [], [w_(s)], false)], [wallet], "close_season");
  const after = (await season(seasonId))!;
  const tops = [after.champions[0], after.predictors[0], after.dropMvps[0]];
  for (let k = 0; k < 3; k++) {
    if (!tops[k] || tops[k].score <= 0n || (after.trophies >> k) & 1) continue;
    const accs = [w_(s), w_(p6pda.trophy(s, k)), r_(tops[k].wallet), w_(epda.treasury()), r_(epda.assets()), r_(CORE), r_(SystemProgram.programId)];
    out[`trophy${k}`] = await sendBase([cu(300_000), await p6Ix(wallet.publicKey, OP6.MINT_TROPHY, k, 0, [], accs, false)], [wallet], `mint_trophy ${k}`);
  }
  return out;
}
export { NET };
