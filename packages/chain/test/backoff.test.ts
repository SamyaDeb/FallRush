// P7 D-RPC: the 429 backoff in front of the public devnet RPC.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fetchWithBackoff, rpc429 } from '../src/web3conn.ts';

test('a 429 is retried with backoff, then the answer passes through', async () => {
  const orig = globalThis.fetch;
  let n = 0;
  const waits: number[] = [];
  globalThis.fetch = (async () => (++n < 3 ? new Response('busy', { status: 429 }) : new Response('{"ok":1}', { status: 200 }))) as any;
  try {
    const before = rpc429.count;
    const r = await fetchWithBackoff('http://x', {}, 4, async (ms) => { waits.push(ms); });
    assert.equal(r.status, 200);
    assert.equal(n, 3);
    assert.equal(rpc429.count - before, 2);
    assert.ok(waits[0] >= 400 && waits[0] <= 560, `first wait ${waits[0]}`);
    assert.ok(waits[1] >= 900 && waits[1] <= 1260, `second wait ${waits[1]}`);
  } finally { globalThis.fetch = orig; }
});

test('gives up after 4 retries and returns the 429; honours Retry-After', async () => {
  const orig = globalThis.fetch;
  let n = 0;
  const waits: number[] = [];
  globalThis.fetch = (async () => { n++; return new Response('busy', { status: 429, headers: { 'retry-after': '2' } }); }) as any;
  try {
    const r = await fetchWithBackoff('http://x', {}, 4, async (ms) => { waits.push(ms); });
    assert.equal(r.status, 429);
    assert.equal(n, 5);
    assert.deepEqual(waits, [2000, 2000, 2000, 2000]);
  } finally { globalThis.fetch = orig; }
});
