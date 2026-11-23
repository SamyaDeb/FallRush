// Real web3.js Connection factory (kept separate so tests never touch the network).
import { Connection } from '@solana/web3.js';
import type { ConnFactory, ConnLike } from './rollup.ts';

/**
 * P7 D-RPC: the public devnet RPC answers 429 when many clients share one IP (a playtest behind one router, the load
 * test's bettors). web3.js's own rate-limit retry stays off (it retries quickly and in lock-step); this fetch retries a
 * 429 itself with exponential backoff + jitter (≈ 0.4, 0.9, 1.8, 3.2 s, +0–40 %), honouring Retry-After, at most 4
 * times, so a burst spreads out instead of failing the guest setup or a bet.
 */
export async function fetchWithBackoff(input: any, init?: any, tries = 4, sleep = (ms: number) => new Promise((r) => setTimeout(r, ms))): Promise<Response> {
  for (let i = 0; ; i++) {
    const r = await fetch(input, init);
    if (r.status !== 429 || i >= tries) return r;
    const ra = Number(r.headers.get('retry-after'));
    const base = [400, 900, 1800, 3200][i] ?? 3200;
    const ms = Number.isFinite(ra) && ra > 0 ? Math.min(5000, ra * 1000) : base * (1 + Math.random() * 0.4);
    rpc429.count++;
    await sleep(ms);
  }
}

/** 429s seen by this page (the F3 / debug panels and the load test read it). */
export const rpc429 = { count: 0 };

export const web3ConnFactory: ConnFactory = (http, ws, commitment) =>
  new Connection(http, { commitment: commitment as 'processed', wsEndpoint: ws ?? undefined, disableRetryOnRateLimit: true, fetch: fetchWithBackoff as any }) as unknown as ConnLike;
