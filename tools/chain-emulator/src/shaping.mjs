// shaping.mjs — network shaping per connection and direction: one-way delay ± uniform jitter, loss for droppable
// frames (account notifications only), in-order delivery (a frame never overtakes an earlier one, like a WS).
// Time comes from an injectable clock so tests can run faster than real time.

export const realClock = {
  now: () => performance.now(),
  setTimeout: (f, ms) => setTimeout(f, ms),
  clearTimeout: (t) => clearTimeout(t),
};

/** A manual clock: timers run in due order when `advance(ms)` is called. */
export class FakeClock {
  constructor() { this.t = 0; this.timers = []; this.seq = 0; }
  now() { return this.t; }
  setTimeout(f, ms) {
    const id = ++this.seq;
    this.timers.push({ id, at: this.t + Math.max(0, ms), f });
    return id;
  }
  clearTimeout(id) { this.timers = this.timers.filter((x) => x.id !== id); }
  advance(ms) {
    const end = this.t + ms;
    for (;;) {
      let best = null;
      for (const x of this.timers) if (x.at <= end && (!best || x.at < best.at || (x.at === best.at && x.id < best.id))) best = x;
      if (!best) break;
      this.timers.splice(this.timers.indexOf(best), 1);
      this.t = Math.max(this.t, best.at);
      best.f();
    }
    this.t = end;
  }
}

/** mulberry32: small seeded PRNG in [0, 1). */
export function rng32(seed) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** One direction of one connection. `deliver(item)` is called in send order, after the shaped delay. */
export class Link {
  constructor({ delayMs = 0, jitterMs = 0, loss = 0 } = {}, clock = realClock, rand = Math.random, deliver = () => {}) {
    Object.assign(this, { delayMs, jitterMs, loss, clock, rand, deliver });
    this.q = [];
    this.lastAt = -Infinity;
    this.timer = null;
    this.stats = { sent: 0, dropped: 0, delivered: 0 };
    this.closed = false;
  }

  send(item, droppable = false) {
    if (this.closed) return;
    this.stats.sent++;
    if (droppable && this.loss > 0 && this.rand() < this.loss) { this.stats.dropped++; return; }
    const now = this.clock.now();
    const d = this.delayMs + (this.jitterMs > 0 ? (this.rand() * 2 - 1) * this.jitterMs : 0);
    const at = Math.max(now + Math.max(0, d), this.lastAt);
    this.lastAt = at;
    if (this.q.length === 0 && at <= now) { this.stats.delivered++; this.deliver(item); return; }
    this.q.push({ at, item });
    this._arm();
  }

  _arm() {
    if (this.timer !== null || this.q.length === 0) return;
    const wait = this.q[0].at - this.clock.now();
    this.timer = this.clock.setTimeout(() => this._fire(), Math.max(0, wait));
  }

  _fire() {
    this.timer = null;
    const now = this.clock.now();
    while (this.q.length && this.q[0].at <= now + 0.5 && !this.closed) {
      const { item } = this.q.shift();
      this.stats.delivered++;
      this.deliver(item);
    }
    this._arm();
  }

  close() {
    this.closed = true;
    if (this.timer !== null) this.clock.clearTimeout(this.timer);
    this.q = [];
  }
}
