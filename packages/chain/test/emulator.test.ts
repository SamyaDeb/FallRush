import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { createChain } from '../src/api.ts';
import { readDrainEntries } from '../src/wire.ts';
import { FakeClock, MockWS, memKV, worldAcc, worldFrame, flush } from './helpers/fake.ts';
import emuCfg from '../config/emulator.json' with { type: 'json' };

const cfgJson = JSON.stringify(emuCfg);

async function setup() {
  MockWS.reset();
  const clock = new FakeClock();
  const kv = memKV();
  const chain = createChain({ clock, kv, WebSocket: MockWS as never });
  const initP = chain.init(cfgJson);
  await flush();
  const init = JSON.parse(await initP);
  return { clock, kv, chain, init, ws: () => MockWS.last };
}

beforeEach(() => MockWS.reset());

test('init + join over the emulator protocol', async () => {
  const { chain, init, ws } = await setup();
  assert.equal(init.ok, true, JSON.stringify(init));
  assert.equal(init.transport, 'emulator');
  assert.equal(ws().url, 'ws://127.0.0.1:8787/');
  assert.equal(ws().binaryType, 'arraybuffer');
  const jp = chain.join(JSON.stringify({ name: 'sam' }));
  await flush();
  const j = ws().texts().find((m) => m.t === 'join');
  assert.equal(j.name, 'sam');
  assert.equal(j.authority, init.burner);
  assert.equal(j.session, init.sessionPubkey);
  assert.equal(j.spectate, false);
  ws().text({ t: 'joined', slot: 4, arena: 'emu:1', map: 'CrumbleShelf', rate: 30, coreVersion: 65537, roster: [{ slot: 4, name: 'sam', kind: 'human' }] });
  const r = JSON.parse(await jp);
  assert.deepEqual([r.ok, r.slot, r.arena, r.map, r.rate, r.coreVersion, r.roster.length], [true, 4, 'emu:1', 'CrumbleShelf', 30, 65537, 1]);
});

test('join refused (full) and timeout', async () => {
  const { chain, clock, ws } = await setup();
  const p = chain.join('{}');
  await flush();
  ws().text({ t: 'full' });
  assert.equal(JSON.parse(await p).error, 'full');
  const p2 = chain.join('{}');
  await clock.advance(5001);
  assert.equal(JSON.parse(await p2).error, 'join timeout');
});

test('inputs: 0x02 frame, 0x83 confirm → event with latency + stats', async () => {
  const { chain, clock, ws } = await setup();
  const batch = new Uint8Array([4, 10, 0, 0, 0, 3, 1, 2, 3, 4, 0, 1, 2, 3, 4, 0, 1, 2, 3, 4, 0]);
  const id = chain.sendInputs(batch);
  assert.equal(id, 1);
  const f = ws().bins()[0];
  assert.equal(f[0], 0x02);
  assert.equal(new DataView(f.buffer).getUint32(1, true), 1);
  assert.deepEqual(f.subarray(5), batch);
  await clock.advance(120);
  const c = new Uint8Array(10); c[0] = 0x83; new DataView(c.buffer).setUint32(1, 1, true); c[5] = 1; new DataView(c.buffer).setUint32(6, 99, true);
  ws().bin(c);
  const ev = JSON.parse(chain.drainEvents()).filter((e: { type: string }) => e.type === 'confirm');
  assert.deepEqual([ev[0].txid, ev[0].status, ev[0].chainTick, ev[0].ms], [1, 'late', 99, 120]);
  const st = JSON.parse(chain.stats());
  assert.equal(st.confirmP50, 120);
  assert.equal(st.confirmBad, 1);
  assert.ok(st.inputBatchesPerSec > 0);
  // a malformed batch is rejected locally
  chain.sendInputs(new Uint8Array(3));
  assert.equal(ws().bins().length, 1);
  assert.equal(JSON.parse(chain.drainEvents())[0].type, 'error');
});

test('World frames → queue (newest 64), drainWorlds framing, latestWorldBase64, gaps, backup poll', async () => {
  const { chain, clock, ws } = await setup();
  ws().bin(worldFrame(0x90, 1, worldAcc(10)));
  assert.equal(chain.pendingWorlds(), 0, 'not queued before subscribe');
  chain.subscribe('$world');
  assert.ok(ws().texts().some((m) => m.t === 'get'), 'subscribe polls immediately');
  ws().bin(worldFrame(0x90, 2, worldAcc(11)));
  ws().bin(worldFrame(0x90, 3, worldAcc(12)));
  ws().bin(worldFrame(0x90, 5, worldAcc(15))); // ticks 13, 14 lost
  ws().bin(worldFrame(0x91, 9, worldAcc(15))); // poll of the same tick: duplicate
  ws().bin(worldFrame(0x91, 10, worldAcc(16))); // poll beats WS
  assert.equal(chain.pendingWorlds(), 4);
  const buf = new Uint8Array(4096);
  const n = chain.drainWorlds(buf, 3);
  const items = readDrainEntries(buf, n);
  assert.deepEqual(items.map((x) => new DataView(x.bytes.buffer).getUint32(0, true)), [11, 12, 15]);
  assert.deepEqual(items.map((x) => x.seq), [2, 3, 5]);
  assert.equal(items[0].bytes.length, 64 + 40);
  assert.equal(chain.pendingWorlds(), 1);
  const ev = JSON.parse(chain.drainEvents());
  const gap = ev.find((e: { type: string }) => e.type === 'gap');
  assert.deepEqual([gap.from, gap.to, gap.missing], [12, 15, 2]);
  assert.ok(ev.some((e: { type: string; tick: number }) => e.type === 'pollFilled' && e.tick === 16));
  const latest = Buffer.from(chain.latestWorldBase64(), 'base64');
  assert.equal(latest.readUInt32LE(0), 16);
  // backup poll every 1 s
  const before = ws().texts().filter((m) => m.t === 'get').length;
  await clock.advance(3000);
  assert.equal(ws().texts().filter((m) => m.t === 'get').length - before, 3);
  // bounded queue
  for (let t = 17; t < 17 + 100; t++) ws().bin(worldFrame(0x90, t, worldAcc(t)));
  assert.equal(chain.pendingWorlds(), 64);
  const st = JSON.parse(chain.stats());
  assert.equal(st.lastTick, 116);
  assert.equal(st.gaps, 1);
  assert.ok(st.queueDrops > 0);
  // a buffer too small for one entry copies nothing and keeps the queue
  assert.equal(chain.drainWorlds(new Uint8Array(10), 64), 0);
  assert.equal(chain.pendingWorlds(), 64);
});

test('arena restart (arena_seq changes) resets the tick baseline', async () => {
  const { chain, ws } = await setup();
  chain.subscribe('$world');
  ws().bin(worldFrame(0x90, 1, worldAcc(900, { seq: 1 })));
  ws().bin(worldFrame(0x90, 2, worldAcc(0, { seq: 2 })));
  ws().bin(worldFrame(0x90, 3, worldAcc(1, { seq: 2 })));
  assert.equal(chain.pendingWorlds(), 3);
  const ev = JSON.parse(chain.drainEvents());
  assert.ok(ev.some((e: { type: string }) => e.type === 'arenaChanged'));
  assert.ok(!ev.some((e: { type: string }) => e.type === 'gap'));
});

test('ping/pong RTT EWMA', async () => {
  const { chain, clock, ws } = await setup();
  await clock.advance(1000);
  const ping = ws().texts().filter((m) => m.t === 'ping').pop();
  await clock.advance(90);
  ws().text({ t: 'pong', c: ping.c });
  assert.equal(JSON.parse(chain.stats()).rttMs, 90);
});

test('reconnect with backoff restores join + subscription', async () => {
  const { chain, clock, ws } = await setup();
  const jp = chain.join(JSON.stringify({ name: 'sam' }));
  await flush();
  ws().text({ t: 'joined', slot: 2, arena: 'emu:1', map: 'CrumbleShelf', rate: 30, coreVersion: 65537, roster: [] });
  await jp;
  chain.subscribe('$world');
  ws().bin(worldFrame(0x90, 1, worldAcc(50)));
  const first = ws();
  MockWS.autoOpen = false;
  first.close();
  assert.equal(chain.sendInputs(new Uint8Array([2, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0])) > 0, true);
  await clock.advance(400); // backoff ≤ 250 ms → new socket attempt
  const second = ws();
  assert.notEqual(second, first);
  second.close(); // fails too → backoff grows
  await clock.advance(200);
  assert.equal(MockWS.instances.length, 2, 'second backoff is longer than the first');
  await clock.advance(400);
  const third = ws();
  assert.equal(MockWS.instances.length, 3);
  third.open();
  await flush();
  const t = third.texts();
  assert.ok(t.some((m) => m.t === 'join' && m.name === 'sam'), 'rejoins');
  assert.ok(t.some((m) => m.t === 'get'), 'resyncs the subscription immediately');
  third.text({ t: 'joined', slot: 2, arena: 'emu:1', map: 'CrumbleShelf', rate: 30, coreVersion: 65537, roster: [] });
  third.bin(worldFrame(0x91, 7, worldAcc(80)));
  const ev = JSON.parse(chain.drainEvents()).map((e: { type: string }) => e.type);
  for (const k of ['wsClosed', 'wsReconnect', 'rejoined', 'gap', 'pollFilled']) assert.ok(ev.includes(k), k + ' in ' + ev.join(','));
  const st = JSON.parse(chain.stats());
  assert.equal(st.reconnects, 1);
  assert.equal(st.lastTick, 80);
  assert.equal(st.sendErrors, 1);
});

test('emulated session actions + wallet + exportBurner', async () => {
  const { chain, kv } = await setup();
  const c = JSON.parse(await chain.session('create', '{}'));
  assert.equal(c.ok, true); assert.equal(c.state, 'active'); assert.equal(c.emulated, true);
  const w = JSON.parse(chain.wallet());
  assert.equal(w.phantom.available, false);
  assert.ok(w.session.validUntil > 0);
  const secret = chain.exportBurner();
  assert.equal(kv.getItem('fallrush.burner.v1'), secret);
  const r = JSON.parse(await chain.session('revoke', '{}'));
  assert.equal(r.state, 'revoked');
  assert.notEqual(JSON.parse(chain.wallet()).sessionPubkey, w.sessionPubkey);
  assert.equal(JSON.parse(await chain.session('bogus')).ok, false);
});
