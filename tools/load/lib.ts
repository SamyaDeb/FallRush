// P0 spike client library (throw-away). Plain @solana/web3.js, manual Anchor encoding.
// Confirms by polling getSignatureStatuses (the rollup sometimes drops WS signature notifications).
import {
  Connection, Keypair, PublicKey, SystemProgram, Transaction, TransactionInstruction,
  ComputeBudgetProgram, sendAndConfirmTransaction,
} from "@solana/web3.js";
import { createHash } from "node:crypto";
import { readFileSync, existsSync, writeFileSync, mkdirSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";
import { execSync } from "node:child_process";

export const PROGRAM_ID = new PublicKey("8z4ofj7GeGLArXaYjbV2QXrdUqDdbutavqQSWXqpQBxy");
export const DELEGATION_PROGRAM = new PublicKey("DELeGGvXpWV2fqJUhqcF5ZSYMS4JTLjteaAMARRSaeSh");
export const MAGIC_PROGRAM = new PublicKey("Magic11111111111111111111111111111111111111");
export const MAGIC_CONTEXT = new PublicKey("MagicContext1111111111111111111111111111111");
export const N = 12;

export type Net = "local" | "devnet";
export function endpoints(net: Net) {
  if (net === "local") return { base: "http://localhost:8899", er: "http://localhost:7799", erWs: "ws://localhost:7800", validator: new PublicKey("mAGicPQYBMvcYveUZA5F5UNNwyHvfYh5xkLS2Fr1mev") };
  return { base: "https://api.devnet.solana.com", er: "https://devnet-as.magicblock.app", erWs: "wss://devnet-as.magicblock.app", validator: new PublicKey("MAS1Dt9qreoRMQ14YQuhg8UTZMMzDdKhmkZMECCzk57") };
}
export const NET: Net = (process.env.P0_NET as Net) || "local";
export const EP = endpoints(NET);
export const base = new Connection(EP.base, { commitment: "confirmed" });
export const er = new Connection(EP.er, { commitment: "processed", wsEndpoint: EP.erWs });

export const ROOT = new URL("../../spikes/p0/", import.meta.url).pathname;
export const LOGS = join(ROOT, ".p0-logs");
export const KEYS = join(ROOT, "keys");
mkdirSync(LOGS, { recursive: true });
mkdirSync(KEYS, { recursive: true });

export const wallet = Keypair.fromSecretKey(Uint8Array.from(JSON.parse(readFileSync(join(homedir(), ".config/solana/id.json"), "utf8"))));

/** Persistent spike keypair (gitignored keys/). Secret keys are never printed. */
export function key(name: string): Keypair {
  const p = join(KEYS, `${NET}-${name}.json`);
  if (existsSync(p)) return Keypair.fromSecretKey(Uint8Array.from(JSON.parse(readFileSync(p, "utf8"))));
  const k = Keypair.generate();
  writeFileSync(p, JSON.stringify(Array.from(k.secretKey)), { mode: 0o600 });
  return k;
}

export function disc(name: string): Buffer {
  return createHash("sha256").update(`global:${name}`).digest().subarray(0, 8);
}
const u8 = (v: number) => Buffer.from([v & 255]);
const u32 = (v: number) => { const b = Buffer.alloc(4); b.writeUInt32LE(v >>> 0); return b; };
const u64 = (v: bigint | number) => { const b = Buffer.alloc(8); b.writeBigUInt64LE(BigInt(v)); return b; };
const i64 = (v: bigint | number) => { const b = Buffer.alloc(8); b.writeBigInt64LE(BigInt(v)); return b; };

export const pda = {
  world: (wid: number) => PublicKey.findProgramAddressSync([Buffer.from("world"), Buffer.from([wid])], PROGRAM_ID)[0],
  log: (wid: number) => PublicKey.findProgramAddressSync([Buffer.from("log"), Buffer.from([wid])], PROGRAM_ID)[0],
  input: (wid: number, slot: number) => PublicKey.findProgramAddressSync([Buffer.from("input"), Buffer.from([wid]), Buffer.from([slot])], PROGRAM_ID)[0],
  bet: (owner: PublicKey) => PublicKey.findProgramAddressSync([Buffer.from("bet"), owner.toBuffer()], PROGRAM_ID)[0],
};
const w = (pubkey: PublicKey, isSigner = false) => ({ pubkey, isSigner, isWritable: true });
const r = (pubkey: PublicKey, isSigner = false) => ({ pubkey, isSigner, isWritable: false });
const ix = (keys: any[], data: Buffer) => new TransactionInstruction({ programId: PROGRAM_ID, keys, data });

export const IX = {
  initWorld: (payer: PublicKey, wid: number, flags: number, gap: number) =>
    ix([w(payer, true), w(pda.world(wid)), w(pda.log(wid)), r(SystemProgram.programId)], Buffer.concat([disc("init_world"), u8(wid), u32(flags), u32(gap)])),
  initInput: (payer: PublicKey, wid: number, slot: number, owner: PublicKey) =>
    ix([w(payer, true), w(pda.world(wid)), w(pda.input(wid, slot)), r(SystemProgram.programId)], Buffer.concat([disc("init_input"), u8(wid), u8(slot), owner.toBuffer()])),
  initBet: (payer: PublicKey, owner: PublicKey) =>
    ix([w(payer, true), w(pda.bet(owner)), r(SystemProgram.programId)], Buffer.concat([disc("init_bet"), owner.toBuffer()])),
  uploadMap: (auth: PublicKey, map: PublicKey, offset: number, total: number, data: Buffer) =>
    ix([r(auth, true), w(map)], Buffer.concat([disc("upload_map_chunk"), u32(offset), u32(total), u32(data.length), data])),
  delegate: (payer: PublicKey, target: PublicKey, kind: number, wid: number, slot: number, owner: PublicKey, validator: PublicKey) => {
    const seed = (tag: string, k: PublicKey, prog: PublicKey) => PublicKey.findProgramAddressSync([Buffer.from(tag), k.toBuffer()], prog)[0];
    return ix([
      w(payer, true),
      w(seed("buffer", target, PROGRAM_ID)),
      w(seed("delegation", target, DELEGATION_PROGRAM)),
      w(seed("delegation-metadata", target, DELEGATION_PROGRAM)),
      w(target),
      r(PROGRAM_ID), r(DELEGATION_PROGRAM), r(SystemProgram.programId),
      r(validator),
    ], Buffer.concat([disc("delegate_pda"), u8(kind), u8(wid), u8(slot), owner.toBuffer()]));
  },
  undelegate: (payer: PublicKey, accs: PublicKey[]) =>
    ix([w(payer, true), r(MAGIC_PROGRAM), w(MAGIC_CONTEXT), ...accs.map((a) => w(a))], disc("undelegate")),
  closeAny: (auth: PublicKey, target: PublicKey) => ix([w(auth, true), w(target)], disc("close_any")),
  resetWorld: (auth: PublicKey, wid: number, map: PublicKey, seed: bigint | number, flags: number, gap: number) =>
    ix([r(auth, true), w(pda.world(wid)), r(map), ...(wid === 0 ? Array.from({ length: N }, (_, s) => w(pda.input(0, s))) : [])], Buffer.concat([disc("reset_world"), u64(seed), u32(flags), u32(gap)])),
  submitInput: (owner: PublicKey, wid: number, slot: number, startTick: number, inputs: Buffer) =>
    ix([r(owner, true), w(pda.input(wid, slot))], Buffer.concat([disc("submit_input"), u32(startTick), inputs])),
  placeBet: (owner: PublicKey, payload: Buffer) => ix([r(owner, true), w(pda.bet(owner))], Buffer.concat([disc("place_bet"), payload])),
  tick: (wid: number, map: PublicKey) =>
    ix([w(pda.world(wid)), w(pda.log(wid)), r(map), ...Array.from({ length: N }, (_, s) => r(pda.input(wid, s)))], disc("tick")),
  tickPart: (wid: number, map: PublicKey, part: number) =>
    ix([w(pda.world(wid)), w(pda.log(wid)), r(map), ...Array.from({ length: N }, (_, s) => r(pda.input(wid, s)))], Buffer.concat([disc("tick_part"), u8(part)])),
  runSteps: (wid: number, map: PublicKey, n: number, seed: bigint | number, reset: boolean) =>
    ix([w(pda.world(wid)), r(map)], Buffer.concat([disc("run_steps"), u32(n), u64(seed), u8(reset ? 1 : 0)])),
  scheduleTicks: (payer: PublicKey, wid: number, map: PublicKey, intervalMs: number, iterations: number, nonce: bigint | number, split = false) =>
    ix([w(payer, true), w(pda.world(wid)), w(pda.log(wid)), r(map), r(MAGIC_PROGRAM), ...Array.from({ length: N }, (_, s) => r(pda.input(wid, s)))],
      Buffer.concat([disc("schedule_ticks"), i64(intervalMs), i64(iterations), u64(nonce), u8(split ? 1 : 0)])),
  cancelTicks: (payer: PublicKey, taskId: bigint, taskContext: PublicKey) =>
    ix([w(payer, true), r(MAGIC_PROGRAM), w(taskContext)], Buffer.concat([disc("cancel_ticks"), i64(taskId)])),
};

export const cuLimit = (units: number) => ComputeBudgetProgram.setComputeUnitLimit({ units });

// ------------------------------------------------------------------ decoding (offsets incl. 8-byte discriminator)
export interface WorldView {
  authority: PublicKey; map: PublicKey; tick: number; seed: bigint; players: { pos: number[]; vel: number[]; grounded: number }[];
  applied: number[]; hash: bigint; lastSlot: bigint; ticksRun: bigint; taskId: bigint; flags: number; gap: number;
  cuInputs: number; cuBots: number; cuStep: number; cuTotal: number; narrow: number;
}
export function decodeWorld(d: Buffer): WorldView {
  const o = 8;
  const players = [];
  for (let s = 0; s < N; s++) {
    const p = o + 80 + s * 32;
    players.push({ pos: [d.readInt32LE(p), d.readInt32LE(p + 4), d.readInt32LE(p + 8)], vel: [d.readInt32LE(p + 12), d.readInt32LE(p + 16), d.readInt32LE(p + 20)], grounded: d[p + 25] });
  }
  return {
    authority: new PublicKey(d.subarray(o, o + 32)), map: new PublicKey(d.subarray(o + 32, o + 64)),
    tick: d.readUInt32LE(o + 64), seed: d.readBigUInt64LE(o + 72), players,
    applied: Array.from({ length: N }, (_, s) => d.readUInt32LE(o + 848 + s * 4)),
    hash: d.readBigUInt64LE(o + 896), lastSlot: d.readBigUInt64LE(o + 904), ticksRun: d.readBigUInt64LE(o + 912), taskId: d.readBigInt64LE(o + 920),
    flags: d.readUInt32LE(o + 928), gap: d.readUInt32LE(o + 932), cuInputs: d.readUInt32LE(o + 936), cuBots: d.readUInt32LE(o + 940),
    cuStep: d.readUInt32LE(o + 944), cuTotal: d.readUInt32LE(o + 948), narrow: d.readUInt32LE(o + 952),
  };
}
export const LOG_N = 400;
export function decodeLog(d: Buffer) {
  const o = 8;
  const head = d.readUInt32LE(o + 32);
  const e = [];
  for (let i = 0; i < LOG_N; i++) {
    const p = o + 40 + i * 24;
    e.push({ tick: d.readUInt32LE(p), cuStep: d.readUInt32LE(p + 4), slot: d.readBigUInt64LE(p + 8), unix: d.readBigInt64LE(p + 16) });
  }
  return { head, e };
}

// ------------------------------------------------------------------ sending
export const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

let bhCache: { conn: Connection; bh: string; at: number }[] = [];
export async function blockhash(conn: Connection, maxAgeMs = 2000): Promise<string> {
  const c = bhCache.find((x) => x.conn === conn);
  if (c && Date.now() - c.at < maxAgeMs) return c.bh;
  const bh = (await conn.getLatestBlockhash("processed")).blockhash;
  bhCache = bhCache.filter((x) => x.conn !== conn).concat([{ conn, bh, at: Date.now() }]);
  return bh;
}

export interface SendResult { sig: string; sentAt: number; confirmedAt?: number; err?: string }

/** Send raw (skipPreflight) and poll getSignatureStatuses until processed/confirmed or timeout. */
export async function send(conn: Connection, ixs: TransactionInstruction[], signers: Keypair[], opts: { pollMs?: number; timeoutMs?: number; confirm?: boolean } = {}): Promise<SendResult> {
  const tx = new Transaction();
  tx.add(...ixs);
  tx.feePayer = signers[0].publicKey;
  try { tx.recentBlockhash = await blockhash(conn); } catch (e: any) { return { sig: "", sentAt: Date.now(), err: `blockhash:${classify(e)}` }; }
  tx.sign(...signers);
  const sentAt = Date.now();
  let sig: string;
  try {
    sig = await conn.sendRawTransaction(tx.serialize(), { skipPreflight: true, maxRetries: 0 });
  } catch (e: any) {
    return { sig: "", sentAt, err: `send:${classify(e)}` };
  }
  if (opts.confirm === false) return { sig, sentAt };
  const res = await waitSig(conn, sig, opts.pollMs ?? 15, opts.timeoutMs ?? 15000);
  return { sig, sentAt, ...res };
}

export async function waitSig(conn: Connection, sig: string, pollMs: number, timeoutMs: number): Promise<{ confirmedAt?: number; err?: string }> {
  const t0 = Date.now();
  while (Date.now() - t0 < timeoutMs) {
    try {
      const st = (await conn.getSignatureStatuses([sig])).value[0];
      if (st) {
        if (st.err) return { confirmedAt: Date.now(), err: `tx:${JSON.stringify(st.err)}` };
        return { confirmedAt: Date.now() };
      }
    } catch (e: any) {
      const c = classify(e);
      if (c === "429") await sleep(200);
    }
    await sleep(pollMs);
  }
  return { err: "timeout" };
}

export function classify(e: any): string {
  const m = String(e?.message ?? e);
  if (/429|Too Many Requests|rate/i.test(m)) return "429";
  if (/blockhash/i.test(m)) return "blockhash";
  if (/ECONNRESET|socket|fetch failed|ETIMEDOUT/i.test(m)) return "network";
  return m.slice(0, 200).replace(/\s+/g, " ");
}

/** Base-layer send with confirmation via the web3 helper (confirmed commitment). */
export async function sendBase(ixs: TransactionInstruction[], signers: Keypair[]): Promise<string> {
  for (let i = 0; ; i++) {
    try {
      await sleep(NET === "devnet" ? 600 : 0); // public devnet RPC rate limit (base layer)
      const tx = new Transaction().add(...ixs);
      return await sendAndConfirmTransaction(base, tx, signers, { commitment: "confirmed", skipPreflight: false });
    } catch (e: any) {
      if (i < 8 && /429|Too Many|fetch failed|blockhash|timed out|not confirmed/i.test(String(e?.message ?? e))) { await sleep(3000 * (i + 1)); continue; }
      throw e;
    }
  }
}

export async function logsOf(conn: Connection, sig: string): Promise<{ logs: string[]; cu?: number; err: any }> {
  for (let i = 0; i < 40; i++) {
    const t = await conn.getTransaction(sig, { commitment: "confirmed", maxSupportedTransactionVersion: 0 }).catch(() => null);
    if (t) return { logs: t.meta?.logMessages ?? [], cu: t.meta?.computeUnitsConsumed ?? undefined, err: t.meta?.err };
    await sleep(250);
  }
  return { logs: [], err: "not found" };
}

export function pct(xs: number[], p: number): number {
  if (!xs.length) return NaN;
  const s = [...xs].sort((a, b) => a - b);
  return s[Math.min(s.length - 1, Math.floor((s.length - 1) * p / 100))];
}
export function summary(xs: number[]) {
  return { n: xs.length, p50: pct(xs, 50), p95: pct(xs, 95), p99: pct(xs, 99), max: xs.length ? Math.max(...xs) : NaN, min: xs.length ? Math.min(...xs) : NaN };
}
export function loadavg(): string {
  try { return execSync("sysctl -n vm.loadavg").toString().trim(); } catch { return "?"; }
}

/** Inputs for one slot at a tick (TS twin of a simple scripted walker; content does not matter for load). */
export function inputBytes(slot: number, tick: number): Buffer {
  const b = Buffer.alloc(8);
  const a = ((slot * 97 + Math.floor(tick / 24) * 31) % 1024) * Math.PI / 512;
  for (let k = 0; k < 2; k++) {
    b.writeInt8(Math.round(Math.cos(a) * 127), k * 4);
    b.writeInt8(Math.round(Math.sin(a) * 127), k * 4 + 1);
    b[k * 4 + 2] = (tick + k) % 30 === 0 ? 1 : 0;
    b[k * 4 + 3] = 0;
  }
  return b;
}

/** Retry a send while the rollup is still cloning accounts ("pending request" / AccountNotFound). */
export async function sendRetry(conn: Connection, ixs: TransactionInstruction[], signers: Keypair[], tries = 30): Promise<SendResult> {
  let r: SendResult = { sig: "", sentAt: 0, err: "none" };
  for (let i = 0; i < tries; i++) {
    r = await send(conn, ixs, signers);
    if (!r.err) return r;
    console.log(`  retry ${i}: ${r.err}`);
    await sleep(1000);
  }
  return r;
}
export const IX2 = {
  benchPrims: (map: PublicKey, n: number) => ix([r(map)], Buffer.concat([disc("bench_prims"), u32(n)])),
};

/** Batched confirmation: one getSignatureStatuses call per `pollMs` for every pending signature. */
export class SigPoller {
  pending = new Map<string, { sentAt: number; cb: (r: { confirmedAt?: number; err?: string }) => void }>();
  timer: any;
  errors: Record<string, number> = {};
  constructor(public conn: Connection, public pollMs = 20, public timeoutMs = 15000) {
    this.timer = setInterval(() => this.tick(), pollMs);
  }
  add(sig: string, sentAt: number): Promise<{ confirmedAt?: number; err?: string }> {
    return new Promise((cb) => this.pending.set(sig, { sentAt, cb }));
  }
  busy = false;
  async tick() {
    if (this.busy || !this.pending.size) return;
    this.busy = true;
    const all = [...this.pending.keys()];
    const chunks: string[][] = [];
    for (let i = 0; i < all.length; i += 200) chunks.push(all.slice(i, i + 200));
    await Promise.all(chunks.map(async (sigs) => {
      try {
        const st = (await this.conn.getSignatureStatuses(sigs)).value;
        const now = Date.now();
        sigs.forEach((s, i) => {
          const p = this.pending.get(s);
          if (!p) return;
          if (st[i]) { this.pending.delete(s); p.cb(st[i]!.err ? { confirmedAt: now, err: `tx:${JSON.stringify(st[i]!.err)}` } : { confirmedAt: now }); }
          else if (now - p.sentAt > this.timeoutMs) { this.pending.delete(s); p.cb({ err: "timeout" }); }
        });
      } catch (e: any) { const c = "poll:" + classify(e); this.errors[c] = (this.errors[c] ?? 0) + 1; }
    }));
    this.busy = false;
  }
  stop() { clearInterval(this.timer); }
}

/** Sign + send without waiting; confirmation through the poller. */
export async function sendVia(poller: SigPoller, ixs: TransactionInstruction[], signers: Keypair[]): Promise<SendResult> {
  const tx = new Transaction().add(...ixs);
  tx.feePayer = signers[0].publicKey;
  try { tx.recentBlockhash = await blockhash(poller.conn, 1000); } catch (e: any) { return { sig: "", sentAt: Date.now(), err: `blockhash:${classify(e)}` }; }
  tx.sign(...signers);
  const sentAt = Date.now();
  let sig: string;
  try { sig = await poller.conn.sendRawTransaction(tx.serialize(), { skipPreflight: true, maxRetries: 0 }); }
  catch (e: any) { return { sig: "", sentAt, err: `send:${classify(e)}` }; }
  const r = await poller.add(sig, sentAt);
  return { sig, sentAt, ...r };
}

export async function rttBaseline(conn: Connection, n = 30): Promise<ReturnType<typeof summary>> {
  const xs: number[] = [];
  for (let i = 0; i < n; i++) { const t = Date.now(); await conn.getSlot("processed").catch(() => 0); xs.push(Date.now() - t); await sleep(50); }
  return summary(xs);
}

/** task_id exactly as the program derives it: FNV-1a64(program id | world | nonce LE) & i64::MAX */
export function taskId(world: PublicKey, nonce: bigint): bigint {
  let h = 0xcbf29ce484222325n;
  const eat = (b: Uint8Array) => { for (const x of b) { h ^= BigInt(x); h = (h * 0x100000001b3n) & 0xffffffffffffffffn; } };
  eat(PROGRAM_ID.toBytes()); eat(world.toBytes());
  const nb = Buffer.alloc(8); nb.writeBigUInt64LE(nonce); eat(nb);
  return h & 0x7fffffffffffffffn;
}

export async function cancelTask(id: bigint): Promise<string | null> {
  const r = await send(er, [IX.cancelTicks(wallet.publicKey, id, MAGIC_CONTEXT)], [wallet]);
  return r.err ?? null;
}

/** Schedules a task and keeps exactly one alive: if no new tick is seen for `stallMs`, cancels the old
 *  task and schedules a new one. `stop()` cancels the live task. `onTick` must be fed from a subscription. */
export function keeper(map: PublicKey, intervalMs: number, iterations: number, split: boolean, stallMs = 3000) {
  const st = { restarts: 0, running: true, live: 0n, lastTick: -1, lastChange: Date.now(), cancelErrors: 0,
    onTick(t: number) { if (t > st.lastTick) { st.lastTick = t; st.lastChange = Date.now(); } },
    async start() { const n = BigInt(Date.now()); st.live = taskId(pda.world(0), n); await send(er, [cuLimit(400_000 - st.restarts), IX.scheduleTicks(wallet.publicKey, 0, map, intervalMs, iterations, n, split)], [wallet]); st.lastChange = Date.now(); },
    async stop() { st.running = false; if (st.live) { if (await cancelTask(st.live)) st.cancelErrors++; } } };
  (async () => {
    while (st.running) {
      await sleep(250);
      if (st.running && Date.now() - st.lastChange > stallMs) {
        st.restarts++;
        if (st.live && (await cancelTask(st.live))) st.cancelErrors++;
        await st.start();
      }
    }
  })();
  return st;
}
