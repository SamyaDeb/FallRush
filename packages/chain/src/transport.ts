import type { Clock } from './clock.ts';
import type { ChainConfig } from './config.ts';
import type { EventLog, Stats, WorldIntake } from './intake.ts';
import type { Wallet } from './wallet.ts';

export interface JoinArgs { name?: string; spectate?: boolean; slot?: number; map?: string; /** hybrid: Classic match id, 32 hex */ match?: string; /** fullchain: a whole on-chain match (start_sim): any of 12 slots, the map follows the World */ full?: boolean }
export interface JoinResult { ok: boolean; error?: string; slot?: number; arena?: string; map?: string; rate?: number; coreVersion?: number; roster?: unknown[]; world?: string; /** harden #9: joined as a viewer although a seat was asked for (slot 255), and why */ spectating?: boolean; reason?: string }

export interface Ctx {
  cfg: ChainConfig;
  clock: Clock;
  events: EventLog;
  stats: Stats;
  intake: WorldIntake;
  wallet: Wallet;
}

export interface Transport {
  readonly kind: 'emulator' | 'rollup';
  start(): Promise<void>;
  join(args: JoinArgs): Promise<JoinResult>;
  /** fire-and-forget; returns a local txid (u32) */
  sendInputs(batch: Uint8Array): number;
  subscribe(addr: string): void;
  unsubscribe(addr: string): void;
  session(action: string, args: Record<string, unknown>): Promise<Record<string, unknown>>;
  /** P4 economy (rollup only): chips | state | bet | drop | receipt | claim */
  econ?(action: string, args: Record<string, unknown>): Promise<Record<string, unknown>>;
  status(): Record<string, unknown>;
  stop(): void;
}

/** Exponential reconnect backoff with jitter. */
export class Backoff {
  attempt = 0;
  minMs: number;
  maxMs: number;
  constructor(minMs: number, maxMs: number) { this.minMs = minMs; this.maxMs = maxMs; }
  next(): number {
    const base = Math.min(this.maxMs, this.minMs * 2 ** this.attempt++);
    return Math.round(base * (0.75 + Math.random() * 0.25));
  }
  reset(): void { this.attempt = 0; }
}

export function errMsg(e: unknown): string { return String((e as Error)?.message ?? e); }
