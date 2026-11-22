// Shaping: delay ± jitter, loss on droppable frames only, in-order delivery; the drift-free ticker.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { Link, FakeClock, rng32 } from '../src/shaping.mjs';
import { Ticker } from '../src/arena.mjs';

test('loss rate within tolerance over many frames; only droppable frames are lost; order preserved', () => {
  const clock = new FakeClock();
  const got = [];
  const at = [];
  const L = new Link({ delayMs: 45, jitterMs: 10, loss: 0.02 }, clock, rng32(7), (x) => { got.push(x); at.push(clock.now()); });
  const N = 100000;
  for (let i = 0; i < N; i++) {
    L.send({ i, drop: i % 10 !== 0 }, i % 10 !== 0); // every 10th frame is a confirm (never dropped)
    clock.advance(1000 / 30 / 10);
  }
  clock.advance(1000);
  const lost = N - got.length;
  const droppable = N * 0.9;
  const rate = lost / droppable;
  console.log(`# loss: ${lost} of ${droppable} droppable frames = ${(rate * 100).toFixed(3)} % (configured 2 %)`);
  assert.ok(Math.abs(rate - 0.02) < 0.002, `loss rate ${rate}`);
  assert.equal(got.filter((x) => !x.drop).length, N / 10, 'no confirm lost');
  for (let k = 1; k < got.length; k++) assert.ok(got[k].i > got[k - 1].i, 'in order');
  for (let k = 1; k < at.length; k++) assert.ok(at[k] >= at[k - 1], 'delivery times monotonic');
});

test('delay is delay ± jitter, and a frame never overtakes an earlier one', () => {
  const clock = new FakeClock();
  const lat = [];
  const sentAt = new Map();
  const L = new Link({ delayMs: 45, jitterMs: 10 }, clock, rng32(3), (i) => lat.push(clock.now() - sentAt.get(i)));
  for (let i = 0; i < 5000; i++) { sentAt.set(i, clock.now()); L.send(i); clock.advance(1); } // 1 ms apart < jitter
  clock.advance(200);
  assert.equal(lat.length, 5000);
  const min = Math.min(...lat), max = Math.max(...lat), avg = lat.reduce((a, b) => a + b, 0) / lat.length;
  console.log(`# one-way latency min ${min.toFixed(1)} avg ${avg.toFixed(1)} max ${max.toFixed(1)} ms (45 ± 10, in-order hold-back raises the average)`);
  assert.ok(min >= 35 - 1e-9 && max <= 55 + 1.5, `${min}..${max}`);
  assert.ok(avg > 45 && avg < 55);
});

test('zero shaping delivers synchronously', () => {
  const got = [];
  const L = new Link({}, new FakeClock(), Math.random, (x) => got.push(x));
  L.send(1); L.send(2);
  assert.deepEqual(got, [1, 2]);
});

test('ticker: drift-free 30 Hz, catches up at most 5 ticks then re-bases', () => {
  const clock = new FakeClock();
  let n = 0;
  const tk = new Ticker(() => n++, { clock });
  tk.start();
  clock.advance(10000);
  assert.equal(n, 300, '10 s = 300 ticks exactly');
  // A 1 s stall (the event loop was blocked: time passes, no timer fires): the wake-up runs 5 ticks, skips the rest.
  const before = n;
  clock.t += 1000;
  clock.advance(1);
  assert.equal(n - before, 5, 'catch up at most 5');
  assert.ok(tk.skipped >= 24 && tk.skipped <= 26, `skipped ${tk.skipped}`);
  clock.advance(1000);
  assert.equal(n - before, 5 + 30, 'back on a 30 Hz grid after the stall');
  tk.stop();
});
