// Shared state behind both transports: World queue (newest N), event log, stats, gap detection.
import type { Clock } from './clock.ts';
import type { WorldLayout } from './config.ts';
import { parseWorldAccount } from './wire.ts';
import { writeDrainEntry } from './wire.ts';
import { bytesToBase64 } from './b58.ts';

export interface WorldEntry { seq: number; tick: number; recvMs: number; bytes: Uint8Array }
export type ChainEvent = { type: string; [k: string]: unknown };

export class EventLog {
  private items: ChainEvent[] = [];
  dropped = 0;
  private max: number;
  private clock: Clock;
  constructor(max: number, clock: Clock) { this.max = max; this.clock = clock; }
  push(e: ChainEvent): void {
    (e as ChainEvent).at = Math.round(this.clock.now());
    this.items.push(e);
    if (this.items.length > this.max) { this.items.shift(); this.dropped++; }
  }
  drain(): ChainEvent[] { const a = this.items; this.items = []; return a; }
  peek(): readonly ChainEvent[] { return this.items; }
}

/** Sliding-window rate (events in the last `windowMs`). */
export class Rate {
  private t: number[] = [];
  private windowMs: number;
  constructor(windowMs = 2000) { this.windowMs = windowMs; }
  hit(now: number): void { this.t.push(now); this.trim(now); }
  perSec(now: number): number { this.trim(now); return (this.t.length * 1000) / this.windowMs; }
  private trim(now: number) { while (this.t.length && this.t[0] <= now - this.windowMs) this.t.shift(); }
}

export function percentile(sorted: number[], p: number): number {
  if (!sorted.length) return 0;
  const i = Math.min(sorted.length - 1, Math.max(0, Math.ceil((p / 100) * sorted.length) - 1));
  return sorted[i];
}

export class Stats {
  updates = new Rate();
  inputs = new Rate();
  confirms: number[] = []; // last 256 confirm latencies (ms)
  rttEwma = 0;
  gaps = 0;
  missingTicks = 0;
  polls = 0;
  pollFills = 0;
  reconnects = 0;
  lastTick = -1;
  queueDrops = 0;
  slicesDropped = 0;
  /** P3-K diagnostics: World notifications seen on the socket, and those dropped as duplicate / older ticks. */
  wsMsgs = 0;
  staleDrops = 0;
  confirmOk = 0;
  confirmBad = 0;
  confirmTimeouts = 0;
  sendErrors = 0;
  addConfirm(ms: number) { this.confirms.push(ms); if (this.confirms.length > 256) this.confirms.shift(); }
  addRtt(ms: number) { this.rttEwma = this.rttEwma === 0 ? ms : this.rttEwma * 0.8 + ms * 0.2; }
  snapshot(now: number) {
    const s = [...this.confirms].sort((a, b) => a - b);
    const r = (x: number) => Math.round(x * 10) / 10;
    return {
      updatesPerSec: r(this.updates.perSec(now)), inputBatchesPerSec: r(this.inputs.perSec(now)),
      confirmP50: r(percentile(s, 50)), confirmP95: r(percentile(s, 95)), confirmN: s.length,
      confirmOk: this.confirmOk, confirmBad: this.confirmBad, confirmTimeouts: this.confirmTimeouts, sendErrors: this.sendErrors,
      rttMs: r(this.rttEwma), gaps: this.gaps, missingTicks: this.missingTicks, polls: this.polls, pollFills: this.pollFills,
      reconnects: this.reconnects, lastTick: this.lastTick, queueDrops: this.queueDrops, slicesDropped: this.slicesDropped, wsMsgs: this.wsMsgs, staleDrops: this.staleDrops,
    };
  }
}

export class WorldIntake {
  private q: WorldEntry[] = [];
  private seq = 0;
  latest: WorldEntry | null = null;
  private arenaSeq: number | null = null;
  private epoch = 1;
  private layout: WorldLayout;
  private max: number;
  private clock: Clock;
  private events: EventLog;
  private stats: Stats;
  constructor(layout: WorldLayout, max: number, clock: Clock, events: EventLog, stats: Stats) {
    this.layout = layout; this.max = max; this.clock = clock; this.events = events; this.stats = stats;
  }

  /**
   * Ingest one World account image. source 'ws' = subscription notification, 'poll' = backup read.
   * Returns true when the tick was new (queued). Duplicates / older ticks are ignored.
   */
  ingest(acc: Uint8Array, source: 'ws' | 'poll', seq?: number): boolean {
    const now = this.clock.now();
    const p = parseWorldAccount(acc, this.layout);
    if (!p) { this.events.push({ type: 'error', where: 'world', why: 'unparseable World account', len: acc.length }); return false; }
    if (source === 'ws') this.stats.wsMsgs++;
    if (!p.complete) { this.stats.slicesDropped++; return false; } // P2 tick slice: only complete ticks go out
    const vdv = new DataView(p.view.buffer);
    if (this.layout.arenaSeqOffset == null) {
      // no restart counter in the account: a tick far behind the last one = a new match on the same arena
      // a new match restarts the World near tick 0; anything else far behind is a stale read (a slow poll reply)
      if (this.stats.lastTick >= 0 && p.tick + 30 < this.stats.lastTick) {
        if (p.tick >= 150) return false;
        this.epoch++;
      }
      vdv.setUint32(8, this.epoch, true);
    }
    const arenaSeq = vdv.getUint32(8, true);
    if (this.arenaSeq !== null && arenaSeq !== this.arenaSeq) {
      // the arena restarted (e.g. the Lid Flip Final): ticks start over
      this.events.push({ type: 'arenaChanged', from: this.arenaSeq, to: arenaSeq, tick: p.tick });
      this.stats.lastTick = -1;
    }
    this.arenaSeq = arenaSeq;
    const last = this.stats.lastTick;
    if (last >= 0 && p.tick <= last) {
      this.stats.staleDrops++;
      return false; // duplicate (poll of a tick the WS already delivered) or reordered
    } else if (last >= 0 && p.tick > last + 1) {
      const missing = p.tick - last - 1;
      this.stats.gaps++;
      this.stats.missingTicks += missing;
      this.events.push({ type: 'gap', from: last, to: p.tick, missing, source });
    }
    if (source === 'poll' && last >= 0 && p.tick > last) {
      this.stats.pollFills++;
      this.events.push({ type: 'pollFilled', tick: p.tick, prev: last });
    }
    this.stats.lastTick = p.tick;
    this.stats.updates.hit(now);
    const e: WorldEntry = { seq: seq ?? ++this.seq, tick: p.tick, recvMs: now, bytes: p.view };
    if (seq !== undefined) this.seq = seq;
    this.latest = e;
    this.q.push(e);
    if (this.q.length > this.max) { this.q.shift(); this.stats.queueDrops++; }
    return true;
  }

  get length() { return this.q.length; }

  /** Copies up to maxItems queued entries (oldest first) into `into`; returns the byte count written. */
  drain(into: Uint8Array, maxItems: number): number {
    let at = 0, n = 0;
    while (n < maxItems && this.q.length) {
      const e = this.q[0];
      const next = writeDrainEntry(into, at, e.recvMs, e.seq, e.bytes);
      if (next < 0) break;
      at = next; n++;
      this.q.shift();
    }
    return at;
  }

  latestBase64(): string { return this.latest ? bytesToBase64(this.latest.bytes) : ''; }

  reset(): void { this.q = []; this.latest = null; this.arenaSeq = null; this.stats.lastTick = -1; }
}
