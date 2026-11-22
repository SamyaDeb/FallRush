// Test doubles: fake clock, mock WebSocket, mock Connection, World account builder.
import type { Clock } from '../../src/clock.ts';
import type { ConnLike } from '../../src/rollup.ts';
import type { PublicKey } from '@solana/web3.js';

interface Timer { id: number; due: number; fn: () => void; every: number | null }

export class FakeClock implements Clock {
  t = 1000;
  wall = 1_800_000_000_000;
  private timers: Timer[] = [];
  private nextId = 1;
  now() { return this.t; }
  wallMs() { return this.wall + this.t; }
  setTimeout(fn: () => void, ms: number) { const id = this.nextId++; this.timers.push({ id, due: this.t + Math.max(0, ms), fn, every: null }); return id; }
  clearTimeout(h: unknown) { this.timers = this.timers.filter((x) => x.id !== h); }
  setInterval(fn: () => void, ms: number) { const id = this.nextId++; this.timers.push({ id, due: this.t + ms, fn, every: ms }); return id; }
  clearInterval(h: unknown) { this.clearTimeout(h); }
  /** Advance time, running due timers in order and letting promises settle between them. */
  async advance(ms: number) {
    const end = this.t + ms;
    for (;;) {
      await flush();
      this.timers.sort((a, b) => a.due - b.due || a.id - b.id);
      const next = this.timers[0];
      if (!next || next.due > end) break;
      this.t = next.due;
      if (next.every !== null) next.due += next.every; else this.timers.shift();
      next.fn();
    }
    this.t = end;
    await flush();
  }
}

export async function flush() { for (let i = 0; i < 5; i++) await new Promise((r) => setImmediate(r)); }

export function memKV() {
  const m = new Map<string, string>();
  return { m, getItem: (k: string) => (m.has(k) ? m.get(k)! : null), setItem: (k: string, v: string) => void m.set(k, v), removeItem: (k: string) => void m.delete(k) };
}

// ---- mock WebSocket -------------------------------------------------------------------------------------------------
export class MockWS {
  static instances: MockWS[] = [];
  static autoOpen = true;
  url: string;
  binaryType = 'blob';
  readyState = 0;
  sent: (string | Uint8Array)[] = [];
  onopen: ((ev: unknown) => void) | null = null;
  onclose: ((ev: unknown) => void) | null = null;
  onerror: ((ev: unknown) => void) | null = null;
  onmessage: ((ev: { data: unknown }) => void) | null = null;
  constructor(url: string) {
    this.url = url;
    MockWS.instances.push(this);
    if (MockWS.autoOpen) queueMicrotask(() => this.open());
  }
  open() { this.readyState = 1; this.onopen?.({}); }
  send(d: string | Uint8Array | ArrayBuffer) { this.sent.push(typeof d === 'string' ? d : new Uint8Array(d instanceof ArrayBuffer ? d : d.slice())); }
  close() { if (this.readyState === 3) return; this.readyState = 3; this.onclose?.({}); }
  /** server → client */
  text(o: unknown) { this.onmessage?.({ data: JSON.stringify(o) }); }
  bin(b: Uint8Array) { const ab = b.slice().buffer; this.onmessage?.({ data: ab }); }
  texts() { return this.sent.filter((x) => typeof x === 'string').map((x) => JSON.parse(x as string)); }
  bins() { return this.sent.filter((x) => typeof x !== 'string') as Uint8Array[]; }
  static reset() { MockWS.instances = []; MockWS.autoOpen = true; }
  static get last() { return MockWS.instances[MockWS.instances.length - 1]; }
}

// ---- World account (wire-v0 §3) ---------------------------------------------------------------------------------
export function worldAcc(tick: number, opts: { core?: number; seq?: number; flags?: number; applied?: number[]; world?: Uint8Array } = {}) {
  const world = opts.world ?? Uint8Array.from({ length: 40 }, (_, i) => (i * 7 + tick) & 0xff);
  const b = new Uint8Array(72 + world.length);
  const dv = new DataView(b.buffer);
  b.set(new TextEncoder().encode('FRWORLD0'), 0);
  dv.setUint32(8, opts.core ?? 65537, true);
  dv.setUint32(12, tick, true);
  (opts.applied ?? []).forEach((t, i) => dv.setUint32(16 + 4 * i, t, true));
  dv.setUint32(64, opts.seq ?? 1, true);
  dv.setUint16(68, world.length, true);
  dv.setUint16(70, opts.flags ?? 1, true);
  b.set(world, 72);
  return b;
}

export function worldFrame(op: number, seq: number, acc: Uint8Array) {
  const f = new Uint8Array(5 + acc.length);
  f[0] = op;
  new DataView(f.buffer).setUint32(1, seq, true);
  f.set(acc, 5);
  return f;
}

// ---- mock Connection ---------------------------------------------------------------------------------------------
export class MockConn implements ConnLike {
  http: string;
  sentRaw: Uint8Array[] = [];
  statusCalls: string[][] = [];
  statusInFlight = 0;
  maxStatusInFlight = 0;
  statuses = new Map<string, { err: unknown } | null>();
  /** sig → resolves as ok once asked this many times */
  autoConfirm = true;
  sendError: ((n: number) => unknown) | null = null;
  sendCount = 0;
  listeners = new Map<number, { pk: PublicKey; cb: (info: { data: Uint8Array }, ctx: { slot: number }) => void }>();
  removed: number[] = [];
  nextListener = 1;
  account: Uint8Array | null = null;
  accountReads = 0;
  slotCalls = 0;
  statusDelayMs = 0;
  clock: FakeClock | null = null;
  constructor(http: string) { this.http = http; }
  async getLatestBlockhash() { return { blockhash: 'GHtXQBsoZHVnNFa9YevAzFr17DJjgHXk3ycTKD5xD3Zi', lastValidBlockHeight: 100 }; }
  async sendRawTransaction(raw: Uint8Array) {
    const n = this.sendCount++;
    if (this.sendError) { const e = this.sendError(n); if (e) throw e; }
    this.sentRaw.push(raw);
    const { Transaction } = await import('@solana/web3.js');
    const tx = Transaction.from(raw);
    const bs58 = (await import('../../src/b58.ts')).b58encode;
    return bs58(tx.signature!);
  }
  async getSignatureStatuses(sigs: string[]) {
    this.statusCalls.push(sigs);
    this.statusInFlight++;
    this.maxStatusInFlight = Math.max(this.maxStatusInFlight, this.statusInFlight);
    if (this.statusDelayMs && this.clock) await new Promise((r) => this.clock!.setTimeout(r, this.statusDelayMs));
    this.statusInFlight--;
    return { value: sigs.map((s) => (this.statuses.has(s) ? this.statuses.get(s)! : this.autoConfirm ? { err: null, confirmationStatus: 'processed' } : null)) };
  }
  onAccountChange(pk: PublicKey, cb: (info: { data: Uint8Array }, ctx: { slot: number }) => void) { const id = this.nextListener++; this.listeners.set(id, { pk, cb }); return id; }
  async removeAccountChangeListener(id: number) { this.removed.push(id); this.listeners.delete(id); }
  async getAccountInfo() { this.accountReads++; return this.account ? { data: this.account } : null; }
  async getSlot() { this.slotCalls++; return 1; }
  async getBalance() { return 5; }
  notify(data: Uint8Array) { for (const l of this.listeners.values()) l.cb({ data }, { slot: 1 }); }
}
