// Token bucket for base-layer sends (P0 quirk 9: public devnet RPC 429s above ~1.5 tx/s).
import type { Clock } from './clock.ts';

export class TokenBucket {
  private tokens: number;
  private last: number;
  private waiting: { run: () => void }[] = [];
  private timer: unknown = null;
  private ratePerSec: number;
  private capacity: number;
  private clock: Clock;
  constructor(ratePerSec: number, capacity: number, clock: Clock) {
    this.ratePerSec = ratePerSec; this.capacity = capacity; this.clock = clock;
    if (!(ratePerSec > 0)) throw new Error('rate must be > 0');
    this.tokens = capacity;
    this.last = clock.now();
  }

  private refill() {
    const now = this.clock.now();
    this.tokens = Math.min(this.capacity, this.tokens + ((now - this.last) * this.ratePerSec) / 1000);
    this.last = now;
  }

  /** Runs fn when a token is available (FIFO). Resolves/rejects with fn's result. */
  schedule<T>(fn: () => Promise<T>): Promise<T> {
    return new Promise<T>((resolve, reject) => {
      this.waiting.push({ run: () => { fn().then(resolve, reject); } });
      this.pump();
    });
  }

  get queued() { return this.waiting.length; }

  private pump() {
    if (this.timer !== null) return;
    this.refill();
    while (this.waiting.length && this.tokens >= 1) {
      this.tokens -= 1;
      this.waiting.shift()!.run();
    }
    if (this.waiting.length) {
      const waitMs = Math.max(1, Math.ceil(((1 - this.tokens) * 1000) / this.ratePerSec));
      this.timer = this.clock.setTimeout(() => { this.timer = null; this.pump(); }, waitMs);
    }
  }
}
