// node --test --test-force-exit tools/p3/chain-relay.test.mjs — the relay's wire-v0 side against a stub chain (no network).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, statSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { startRelay, viewToWorldAcc, fileStorage, confirmStatus, OP } from './chain-relay.mjs';

function view(tick, len = 40) {
  const v = Buffer.alloc(64 + len);
  v.writeUInt32LE(tick, 0); v.writeUInt32LE(0, 4); v.writeUInt32LE(3, 8); v.writeUInt16LE(1, 12); v.writeUInt16LE(len, 14);
  v.writeUInt32LE(tick - 2, 16 + 4 * 5);
  for (let i = 0; i < len; i++) v[64 + i] = (i + tick) & 255;
  return v;
}

function stubChain() {
  const s = { worlds: [], events: [], inputs: [], joins: [], subs: [], tick: 0, joinResolve: null };
  s.chain = {
    join: (a) => { s.joins.push(JSON.parse(a)); return new Promise((r) => { s.joinResolve = (o) => r(JSON.stringify(o)); }); },
    subscribe: (a) => s.subs.push(a),
    sendInputs: (b) => { s.inputs.push(Buffer.from(b)); return 100 + s.inputs.length; },
    drainWorlds: (into, max) => {
      let at = 0;
      while (s.worlds.length && max-- > 0) {
        const v = s.worlds.shift();
        const dv = new DataView(into.buffer);
        dv.setUint32(at, v.length, true); dv.setFloat64(at + 4, 1, true); dv.setUint32(at + 12, 1, true);
        into.set(v, at + 16); at += 16 + v.length;
      }
      return at;
    },
    drainEvents: () => { const e = s.events; s.events = []; return JSON.stringify(e); },
    latestWorldBase64: () => view(s.tick).toString('base64'),
    latestTick: () => s.tick,
    stats: () => JSON.stringify({ lastTick: s.tick }),
    wallet: () => JSON.stringify({ burner: 'B' }),
  };
  return s;
}

function client(port) {
  const ws = new WebSocket(`ws://127.0.0.1:${port}/`);
  ws.binaryType = 'arraybuffer';
  const texts = [], bins = [];
  ws.onmessage = (e) => (typeof e.data === 'string' ? texts.push(JSON.parse(e.data)) : bins.push(Buffer.from(e.data)));
  return { ws, texts, bins, open: new Promise((r) => (ws.onopen = r)) };
}
const until = async (f, ms = 3000) => { const t0 = Date.now(); while (!f()) { if (Date.now() - t0 > ms) throw new Error('timeout'); await new Promise((r) => setTimeout(r, 10)); } };

test('WorldView → wire-v0 §3 WorldAcc', () => {
  const a = viewToWorldAcc(view(77));
  assert.equal(a.subarray(0, 8).toString(), 'FRWORLD0');
  assert.deepEqual([a.readUInt32LE(8), a.readUInt32LE(12), a.readUInt32LE(16 + 20), a.readUInt32LE(64), a.readUInt16LE(68), a.readUInt16LE(70), a.length], [0, 77, 75, 3, 40, 1, 112]);
  assert.equal(a[72], 77);
  assert.equal(confirmStatus({ status: 'ok' }), 0);
  assert.equal(confirmStatus({ status: 'err', err: '{"InstructionError":[0,{"Custom":6020}]}' }), 2);
  assert.equal(confirmStatus({ status: 'err', err: '{"InstructionError":[0,{"Custom":6000}]}' }), 3);
  assert.equal(confirmStatus({ status: 'timeout' }), 1);
});

test('key file shim: persisted, mode 600, gitignored dir', () => {
  const dir = mkdtempSync(join(tmpdir(), 'relay-keys-'));
  const f = join(dir, 'k', 'local.json');
  const kv = fileStorage(f);
  kv.setItem('fallrush.burner.v1', 'secret');
  assert.equal(fileStorage(f).getItem('fallrush.burner.v1'), 'secret');
  assert.equal(statSync(f).mode & 0o777, 0o600);
  assert.ok(existsSync(join(dir, 'k', '.gitignore')));
  assert.equal(readFileSync(join(dir, 'k', '.gitignore'), 'utf8'), '*\n');
});

test('relay: join passthrough (match, slot), joined after the chain join, inputs/confirms, World frames, get, ping', { skip: typeof WebSocket === 'undefined' && 'no WebSocket' }, async (t) => {
  const s = stubChain();
  const logs = [];
  const relay = await startRelay({ chain: s.chain, port: 0, log: (x) => logs.push(x), tickMs: 5 });
  t.after(() => relay.close());
  const c = client(relay.port);
  await c.open;
  c.ws.send(JSON.stringify({ t: 'ping', c: 123 }));
  await until(() => c.texts.some((m) => m.t === 'pong'));
  assert.equal(c.texts.find((m) => m.t === 'pong').c, 123);
  c.ws.send(JSON.stringify({ t: 'join', name: 'sam', spectate: false, slot: 4, match: '00112233445566778899aabbccddeeff' }));
  await until(() => s.joins.length === 1);
  assert.deepEqual(s.joins[0], { name: 'sam', spectate: false, slot: 4, match: '00112233445566778899aabbccddeeff' });
  // progress events reach the client while the chain join is pending; World frames do not (not joined yet)
  s.events.push({ type: 'join', stage: 'lobbyJoined', slot: 4 });
  s.worlds.push(view(10));
  await until(() => c.texts.some((m) => m.t === 'chain'));
  assert.equal(c.bins.length, 0);
  s.joinResolve({ ok: true, slot: 4, arena: 'A', map: 'CrumbleShelf', rate: 30, coreVersion: 0, roster: [{ slot: 4, kind: 'human' }], match: 'M' });
  await until(() => c.texts.some((m) => m.t === 'joined'));
  const j = c.texts.find((m) => m.t === 'joined');
  assert.deepEqual([j.slot, j.arena, j.map, j.rate, j.match], [4, 'A', 'CrumbleShelf', 30, 'M']);
  assert.deepEqual(s.subs, ['$world']);
  // World → 0x90 with a relay seq
  s.worlds.push(view(11), view(12));
  await until(() => c.bins.filter((b) => b[0] === OP.NOTIFY).length === 2);
  const w = c.bins.filter((b) => b[0] === OP.NOTIFY);
  assert.deepEqual(w.map((b) => [b.readUInt32LE(1), b.readUInt32LE(5 + 12)]), [[1, 11], [2, 12]]);
  // input → sendInputs (batch bytes only) → confirm with the client's txid
  const batch = Buffer.from([4, 50, 0, 0, 0, 3, ...new Array(15).fill(1)]);
  const f = Buffer.concat([Buffer.from([OP.INPUT, 7, 0, 0, 0]), batch]);
  c.ws.send(f);
  await until(() => s.inputs.length === 1);
  assert.deepEqual(s.inputs[0], batch);
  s.tick = 52;
  s.events.push({ type: 'confirm', txid: 101, status: 'ok', ms: 80, kind: 'input' });
  await until(() => c.bins.some((b) => b[0] === OP.CONFIRM));
  const cf = c.bins.find((b) => b[0] === OP.CONFIRM);
  assert.deepEqual([cf.readUInt32LE(1), cf[5], cf.readUInt32LE(6)], [7, 0, 52]);
  // get → 0x91
  c.ws.send(JSON.stringify({ t: 'get' }));
  await until(() => c.bins.some((b) => b[0] === OP.POLL));
  // a second client (Unity reconnect) gets the cached joined at once, without a second chain join
  const c2 = client(relay.port);
  await c2.open;
  c2.ws.send(JSON.stringify({ t: 'join', name: 'sam', slot: 4 }));
  await until(() => c2.texts.some((m) => m.t === 'joined'));
  assert.equal(s.joins.length, 1);
  c.ws.close(); c2.ws.close();
});

test('relay: a failed join is reported as err and can be retried', { skip: typeof WebSocket === 'undefined' && 'no WebSocket' }, async (t) => {
  const s = stubChain();
  const relay = await startRelay({ chain: s.chain, port: 0, log: () => {}, tickMs: 5 });
  t.after(() => relay.close());
  const c = client(relay.port);
  await c.open;
  c.ws.send(JSON.stringify({ t: 'join', name: 'x' }));
  await until(() => s.joinResolve);
  s.joinResolve({ ok: false, error: 'full' });
  await until(() => c.texts.some((m) => m.t === 'err'));
  assert.equal(c.texts.find((m) => m.t === 'err').why, 'full');
  c.ws.send(JSON.stringify({ t: 'join', name: 'x' }));
  await until(() => s.joins.length === 2);
  c.ws.close();
});
