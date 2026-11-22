import { test } from 'node:test';
import assert from 'node:assert/strict';
import { TokenBucket } from '../src/throttle.ts';
import { FakeClock } from './helpers/fake.ts';

test('token bucket: a burst of 12 sends stays ≤ 1.5 tx/s', async () => {
  const c = new FakeClock();
  const b = new TokenBucket(1.5, 1, c);
  const t0 = c.now();
  const times: number[] = [];
  const all = Array.from({ length: 12 }, () => b.schedule(async () => { times.push(c.now() - t0); }));
  await c.advance(10000);
  await Promise.all(all);
  assert.equal(times.length, 12);
  // any window [t, t+1000) holds at most capacity + 1.5 sends; overall rate ≤ 1.5/s
  for (const s of times) assert.ok(times.filter((x) => x >= s && x < s + 1000).length <= 2, 'window ' + s);
  for (let i = 1; i < times.length; i++) assert.ok(times[i] - times[i - 1] >= 666, `gap ${i}: ${times[i] - times[i - 1]}`);
  assert.ok((times.length - 1) / (times[11] / 1000) <= 1.5 + 1e-9);
  assert.equal(times[0], 0, 'first send immediate');
});

test('token bucket refills while idle, FIFO order, rejects propagate', async () => {
  const c = new FakeClock();
  const b = new TokenBucket(1.5, 1, c);
  await b.schedule(async () => 1);
  await c.advance(5000);
  const order: number[] = [];
  const p1 = b.schedule(async () => { order.push(1); });
  const p2 = b.schedule(async () => { order.push(2); throw new Error('boom'); });
  const p2r = assert.rejects(p2, /boom/);
  await c.advance(2000);
  await p1;
  await p2r;
  assert.deepEqual(order, [1, 2]);
});
