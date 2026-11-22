// P2 keeper / runner library: connections, PDAs, the fall_rush IDL client, throttled base-layer sends, polling
// confirmation (never websocket-only), map upload. P2_NET=local|devnet.
import { AnchorProvider, Program, Wallet, BN } from "@coral-xyz/anchor";
import { Connection, Keypair, PublicKey, SystemProgram, Transaction, ComputeBudgetProgram } from "@solana/web3.js";
import type { TransactionInstruction, AccountMeta } from "@solana/web3.js";
import { createHash } from "node:crypto";
import { readFileSync, writeFileSync, existsSync, mkdirSync, appendFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

export const ROOT = new URL("../../", import.meta.url).pathname;
export const NET = (process.env.P2_NET as "local" | "devnet") || "local";
export const EP = NET === "local"
  ? { base: "http://localhost:8899", er: "http://localhost:7799", erWs: "ws://localhost:7800", validator: new PublicKey("mAGicPQYBMvcYveUZA5F5UNNwyHvfYh5xkLS2Fr1mev"), vrfQueue: new PublicKey("Sc9MJUngNbQXSXGP3F67KvKwVnhaYn6kcioxXNVowYT") }
  : { base: "https://api.devnet.solana.com", er: "https://devnet-as.magicblock.app", erWs: "wss://devnet-as.magicblock.app", validator: new PublicKey("MAS1Dt9qreoRMQ14YQuhg8UTZMMzDdKhmkZMECCzk57"), vrfQueue: new PublicKey("5hBR571xnXppuCPveTrctfTU7tJLSN94nq7kv7FRK5Tc") };
export const base = new Connection(EP.base, { commitment: "confirmed" });
export const er = new Connection(EP.er, { commitment: "processed", wsEndpoint: EP.erWs });
export const wallet = Keypair.fromSecretKey(Uint8Array.from(JSON.parse(readFileSync(join(homedir(), ".config/solana/id.json"), "utf8"))));
export const PROGRAM_ID = new PublicKey("4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4");
export const MAGIC_PROGRAM = new PublicKey("Magic11111111111111111111111111111111111111");
export const MAGIC_CONTEXT = new PublicKey("MagicContext1111111111111111111111111111111");
export const N = 12;
export const MAP_STEMS = ["BlockPartyDash", "GooTower", "StarScramble", "CrumbleShelf", "Lobby"]; // Arena::maps order (deal ids 0..3, lobby)

const idl = JSON.parse(readFileSync(join(ROOT, "programs/target/idl/fall_rush.json"), "utf8"));
const mk = (c: Connection) => new Program(idl, new AnchorProvider(c, new Wallet(wallet), { commitment: "confirmed", skipPreflight: true } as any));
export const pb = mk(base);
export const pe = mk(er);

export const KEYS = join(ROOT, "tools/keeper/.keys");
mkdirSync(KEYS, { recursive: true });
/** Persistent keypair in tools/keeper/.keys (gitignored). Secret keys are never printed. */
export function key(name: string): Keypair {
  const p = join(KEYS, `${NET}-${name}.json`);
  if (existsSync(p)) return Keypair.fromSecretKey(Uint8Array.from(JSON.parse(readFileSync(p, "utf8"))));
  const k = Keypair.generate();
  writeFileSync(p, JSON.stringify(Array.from(k.secretKey)), { mode: 0o600 });
  return k;
}

const pdaOf = (seeds: (Buffer | Uint8Array)[]) => PublicKey.findProgramAddressSync(seeds, PROGRAM_ID)[0];
export const pda = {
  config: () => pdaOf([Buffer.from("config")]),
  arena: (id: number) => pdaOf([Buffer.from("arena"), Buffer.from([id])]),
  world: (arena: PublicKey) => pdaOf([Buffer.from("world"), arena.toBuffer()]),
  evlog: (arena: PublicKey) => pdaOf([Buffer.from("ev"), arena.toBuffer()]),
  input: (arena: PublicKey, slot: number) => pdaOf([Buffer.from("in"), arena.toBuffer(), Buffer.from([slot])]),
  match: (id: Uint8Array) => pdaOf([Buffer.from("match"), Buffer.from(id)]),
};

export const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
export const sha256 = (b: Uint8Array) => createHash("sha256").update(b).digest();

// ------------------------------------------------------------------ sending + confirmation

let lastBase = 0;
/** Base layer: throttled to ≤ 1.5 tx/s on devnet (public RPC 429s, P0 §4.4), retried, confirmed by polling. */
export async function sendBase(ixs: TransactionInstruction[], signers: Keypair[] = [wallet], what = ""): Promise<string> {
  for (let i = 0; ; i++) {
    const gap = NET === "devnet" ? 700 : 0;
    const wait = lastBase + gap - Date.now();
    if (wait > 0) await sleep(wait);
    lastBase = Date.now();
    try {
      const tx = new Transaction().add(...ixs);
      tx.feePayer = signers[0].publicKey;
      tx.recentBlockhash = (await base.getLatestBlockhash("confirmed")).blockhash;
      tx.sign(...signers);
      const sig = await base.sendRawTransaction(tx.serialize(), { skipPreflight: false, maxRetries: 3 });
      const r = await waitSig(base, sig, 500, 60_000);
      if (r.err) throw new Error(`${what} ${r.err}`);
      return sig;
    } catch (e: any) {
      const m = String(e?.message ?? e);
      if (i < 8 && /429|Too Many|fetch failed|blockhash|timed out|timeout|not confirmed|ECONNRESET/i.test(m)) { await sleep(2000 * (i + 1)); continue; }
      throw e;
    }
  }
}

/** Rollup: unthrottled; retried while the rollup is still cloning (P0 §4.4 quirk 7). */
export async function sendEr(ixs: TransactionInstruction[], signers: Keypair[] = [wallet], what = "", tries = 20): Promise<{ sig: string; ms: number; err?: string }> {
  let last = "";
  for (let i = 0; i < tries; i++) {
    const t0 = Date.now();
    try {
      const tx = new Transaction().add(...ixs);
      tx.feePayer = signers[0].publicKey;
      tx.recentBlockhash = (await er.getLatestBlockhash("processed")).blockhash;
      tx.sign(...signers);
      const sig = await er.sendRawTransaction(tx.serialize(), { skipPreflight: true, maxRetries: 0 });
      const r = await waitSig(er, sig, 20, 15_000);
      if (!r.err) return { sig, ms: Date.now() - t0 };
      last = r.err;
      if (!/pending|AccountNotFound|cloner|timeout|Blockhash/i.test(last)) return { sig, ms: Date.now() - t0, err: last };
    } catch (e: any) { last = String(e?.message ?? e).slice(0, 300); }
    await sleep(600);
  }
  return { sig: "", ms: 0, err: `${what}: ${last}` };
}

export async function waitSig(conn: Connection, sig: string, pollMs: number, timeoutMs: number): Promise<{ err?: string }> {
  const t0 = Date.now();
  while (Date.now() - t0 < timeoutMs) {
    try {
      const st = (await conn.getSignatureStatuses([sig])).value[0];
      if (st) return st.err ? { err: `tx:${JSON.stringify(st.err)}` } : {};
    } catch { await sleep(200); }
    await sleep(pollMs);
  }
  return { err: "timeout" };
}

export const cu = (units: number) => ComputeBudgetProgram.setComputeUnitLimit({ units });

// ------------------------------------------------------------------ accounts

export interface WorldView { tick: number; len: number; partPending: number; parts: number; phase: number; round: number; ticksRun: bigint; executions: bigint; notDue: bigint; budgetHit: bigint; lastSlot: bigint; lastCode: number; world: Buffer; appliedTick: number[] }
/** WorldAcc layout (sim.rs): 8 disc | arena 32 | last_slot, ticks_run, executions, not_due, budget_hit u64 | applied_tick[12] u32 | tick u32 | len u32 | part_pending, parts, phase, round, results_round, last_code u8 | pad 2 | cur 48 | cur_some u16 | pad 6 | world 2048 */
export function decodeWorld(d: Buffer): WorldView {
  let o = 8 + 32;
  const u64 = () => { const v = d.readBigUInt64LE(o); o += 8; return v; };
  const lastSlot = u64(), ticksRun = u64(), executions = u64(), notDue = u64(), budgetHit = u64();
  const appliedTick: number[] = [];
  for (let i = 0; i < N; i++) { appliedTick.push(d.readUInt32LE(o)); o += 4; }
  const tick = d.readUInt32LE(o); o += 4;
  const len = d.readUInt32LE(o); o += 4;
  const partPending = d[o], parts = d[o + 1], phase = d[o + 2], round = d[o + 3], lastCode = d[o + 5]; o += 8;
  o += 48 + 2 + 6;
  return { tick, len, partPending, parts, phase, round, ticksRun, executions, notDue, budgetHit, lastSlot, lastCode, world: d.subarray(o, o + len), appliedTick };
}
export const WORLD_OFFSET = 8 + 32 + 40 + 48 + 8 + 8 + 48 + 8;

/** The tick() account list: arena, world, event log, match, 5 maps, 12 InputBufs. */
export function tickAccounts(arena: PublicKey, match: PublicKey, maps: PublicKey[]): AccountMeta[] {
  const w = (pubkey: PublicKey) => ({ pubkey, isSigner: false, isWritable: true });
  const r = (pubkey: PublicKey) => ({ pubkey, isSigner: false, isWritable: false });
  return [r(arena), w(pda.world(arena)), w(pda.evlog(arena)), w(match), ...maps.map(r), ...Array.from({ length: N }, (_, s) => r(pda.input(arena, s)))];
}

export function log(file: string, o: object) {
  appendFileSync(file, JSON.stringify({ t: Date.now(), ...o }) + "\n");
}

export function pct(xs: number[], p: number) { if (!xs.length) return NaN; const s = [...xs].sort((a, b) => a - b); return s[Math.min(s.length - 1, Math.floor((s.length - 1) * p / 100))]; }
export { BN };
