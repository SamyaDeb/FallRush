// Server end to end over a real socket: join, 0x90 notifications, 0x02 inputs → 0x83 confirms, get/ping,
// POST /final roster + join by slot, POST /reset, /status, static files.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { gzipSync } from 'node:zlib';
import { Emulator, inputFrame, FRAME } from '../src/server.mjs';
import { decodeWorldAcc } from '../src/world-view.mjs';
import { cmd } from './helpers.mjs';

/** A client: collects text (parsed) and binary frames. */
async function client(port) {
  const ws = new WebSocket(`ws://127.0.0.1:${port}/`);
  ws.binaryType = 'arraybuffer';
  const c = { ws, texts: [], bins: [] };
  ws.onmessage = (m) => (typeof m.data === 'string' ? c.texts.push(JSON.parse(m.data)) : c.bins.push(new Uint8Array(m.data)));
  await new Promise((ok, fail) => { ws.onopen = ok; ws.onerror = fail; });
  c.wait = async (pred, ms = 3000) => {
    const end = Date.now() + ms;
    for (;;) {
      const x = pred(c);
      if (x) return x;
      if (Date.now() > end) throw new Error('timeout');
      await new Promise((r) => setTimeout(r, 5));
    }
  };
  c.text = (t) => c.texts.find((x) => x.t === t);
  c.lastWorld = () => { for (let i = c.bins.length - 1; i >= 0; i--) if (c.bins[i][0] === FRAME.NOTIFY) return c.bins[i]; return null; };
  return c;
}

const post = (port, path, body) => fetch(`http://127.0.0.1:${port}${path}`, { method: 'POST', body: body === undefined ? '' : JSON.stringify(body) }).then(async (r) => ({ code: r.status, j: await r.json() }));
const get = (port, path) => fetch(`http://127.0.0.1:${port}${path}`).then((r) => r.json());

test('join, notifications, inputs → confirms, get, ping, /status', async () => {
  const emu = new Emulator({ port: 0, shaping: false, confirm: 20, quiet: true, seed: '00000000000000aa', autostart: 60 });
  const port = await emu.listen();
  const c = await client(port);
  c.ws.send(JSON.stringify({ t: 'join', name: 'ana', session: 'S1', authority: 'A1' }));
  const j = await c.wait(() => c.text('joined'));
  assert.equal(j.slot, 0);
  assert.equal(j.map, 'CrumbleShelf');
  assert.equal(j.rate, 30);
  assert.equal(j.coreVersion, emu.arena.core.version);
  assert.deepEqual(j.roster.map((r) => r.kind), ['human', 'bot', 'bot']);
  assert.equal(j.roster[0].name, 'ana');

  // All human slots joined → the round begins at the next tick.
  const f = await c.wait(() => { const b = c.lastWorld(); return b && decodeWorldAcc(b.subarray(5), emu.arena.layout).world.round.phase === 1 ? b : null; });
  const acc = decodeWorldAcc(f.subarray(5), emu.arena.layout);
  assert.equal(acc.arenaSeq, 1);
  assert.equal(acc.flags & 1, 1, 'live');

  // A second human is refused (one human slot); a spectator is fine.
  const d = await client(port);
  d.ws.send(JSON.stringify({ t: 'join', name: 'bob' }));
  await d.wait(() => d.text('full'));
  d.ws.send(JSON.stringify({ t: 'join', spectate: true }));
  assert.equal((await d.wait(() => d.text('joined'))).slot, 255);

  // Inputs: one in-window, one late, one bad slot.
  const T = emu.arena.tick;
  c.ws.send(inputFrame(7, 0, T + 3, [cmd(1, 0), cmd(2, 0), cmd(3, 0)]));
  c.ws.send(inputFrame(8, 0, 1, [cmd(1, 0)]));
  c.ws.send(inputFrame(9, 1, T + 3, [cmd(1, 0)]));
  const confirms = await c.wait(() => { const x = c.bins.filter((b) => b[0] === FRAME.CONFIRM); return x.length >= 3 ? x : null; });
  const st = confirms.map((b) => { const dv = new DataView(b.buffer, b.byteOffset); return [dv.getUint32(1, true), b[5], dv.getUint32(6, true)]; });
  assert.deepEqual(st.map((x) => x.slice(0, 2)), [[7, 0], [8, 1], [9, 3]]);
  assert.ok(st[0][2] >= T, 'chain tick');
  await c.wait(() => emu.arena.applied[0] >= T + 5);
  const lw = c.lastWorld();
  await c.wait(() => decodeWorldAcc(c.lastWorld().subarray(5), emu.arena.layout).applied[0] === T + 5);

  // get → 0x91; ping → pong.
  c.ws.send(JSON.stringify({ t: 'get' }));
  const poll = await c.wait(() => c.bins.find((b) => b[0] === FRAME.POLL));
  assert.ok(decodeWorldAcc(poll.subarray(5), emu.arena.layout).tick > 0);
  c.ws.send(JSON.stringify({ t: 'ping', c: 1234 }));
  assert.equal((await c.wait(() => c.text('pong'))).c, 1234);

  const s = await get(port, '/status');
  assert.equal(s.arena, 'emu:1');
  assert.deepEqual([s.inputs.accepted, s.inputs.late, s.inputs.badSlot], [3, 1, 1]);
  assert.equal(s.players[0].joined, true);
  assert.equal(s.connections.length, 2);
  assert.ok(lw);
  c.ws.close(); d.ws.close();
  await emu.close();
});

test('POST /final: exact roster + seed, humans join their roster slot; /reset', async () => {
  const emu = new Emulator({ port: 0, shaping: false, confirm: 0, quiet: true, autostart: 60 });
  const port = await emu.listen();
  const bad = await post(port, '/final', { roster: [{ slot: 0, kind: 'human' }, { slot: 0, kind: 'bot' }], seed: '01' });
  assert.equal(bad.code, 400);
  const roster = [{ slot: 0, kind: 'bot' }, { slot: 3, kind: 'human', name: 'p3' }, { slot: 5, kind: 'human', name: 'p5' }, { slot: 7, kind: 'bot' }];
  const r = await post(port, '/final', { match: 'm-42', seed: 'feedfacecafebeef', roster });
  assert.equal(r.code, 200);
  assert.deepEqual([r.j.ok, r.j.arena], [true, 'emu:2']);
  assert.equal(r.j.startsInMs, 60000);
  let s = await get(port, '/status');
  assert.deepEqual(s.roster.map((x) => [x.slot, x.kind]), roster.map((x) => [x.slot, x.kind]));
  assert.equal(s.seed, 'feedfacecafebeef');
  assert.equal(s.match, 'm-42');
  assert.equal(s.state, 'waiting');

  const a = await client(port), b = await client(port);
  a.ws.send(JSON.stringify({ t: 'join', name: 'x', slot: 0 }));
  assert.match((await a.wait(() => a.text('err'))).why, /not a human slot/);
  a.ws.send(JSON.stringify({ t: 'join', name: 'p5', slot: 5 }));
  assert.equal((await a.wait(() => a.text('joined'))).slot, 5);
  b.ws.send(JSON.stringify({ t: 'join', name: 'dup', slot: 5 }));
  assert.match((await b.wait(() => b.text('err'))).why, /taken/);
  b.ws.send(JSON.stringify({ t: 'join', name: 'p3', slot: 3 }));
  assert.equal((await b.wait(() => b.text('joined'))).slot, 3);
  await a.wait(() => emu.arena.state === 'round');
  const acc = decodeWorldAcc(emu.arena.lastAcc, emu.arena.layout);
  assert.deepEqual(acc.world.players.map((p) => (p.active ? (p.bot ? 'b' : 'h') : '.')).join(''), 'b..h.h.b....');
  assert.equal(acc.world.hazardSeed, 0xfeedfacecafebeefn);
  assert.equal(acc.world.round.qualifyCount, 1);

  const z = await post(port, '/reset');
  assert.deepEqual([z.code, z.j.arena], [200, 'emu:3']);
  s = await get(port, '/status');
  assert.deepEqual(s.roster.map((x) => x.kind), ['human', 'bot', 'bot']);
  // Slot 5 / 3 are not human slots any more → those connections are re-announced as spectators.
  await a.wait(() => a.texts.filter((x) => x.t === 'joined').at(-1).arena === 'emu:3');
  assert.equal(a.texts.filter((x) => x.t === 'joined').at(-1).slot, 255);
  a.ws.close(); b.ws.close();
  await emu.close();
});

test('static files: MIME, .br/.gz Content-Encoding, traversal refused', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'emu-static-'));
  writeFileSync(join(dir, 'index.html'), '<p>hi</p>');
  writeFileSync(join(dir, 'Build.wasm.gz'), gzipSync(Buffer.from([0, 97, 115, 109])));
  writeFileSync(join(dir, 'Build.data.br'), Buffer.from([1, 2, 3]));
  const emu = new Emulator({ port: 0, quiet: true, static: dir, ticker: false });
  const port = await emu.listen();
  let r = await fetch(`http://127.0.0.1:${port}/`);
  assert.equal(r.headers.get('content-type'), 'text/html; charset=utf-8');
  assert.equal(await r.text(), '<p>hi</p>');
  r = await fetch(`http://127.0.0.1:${port}/Build.wasm.gz`);
  assert.equal(r.headers.get('content-type'), 'application/wasm');
  assert.equal(r.headers.get('content-encoding'), 'gzip');
  assert.deepEqual([...new Uint8Array(await r.arrayBuffer())], [0, 97, 115, 109], 'fetch decoded the gzip');
  const raw = await new Promise((ok) => import('node:http').then(({ get: g }) => g({ port, host: '127.0.0.1', path: '/Build.data.br' }, (res) => { res.resume(); ok(res.headers); })));
  assert.equal(raw['content-encoding'], 'br');
  assert.equal(raw['content-type'], 'application/octet-stream');
  const t = await new Promise((ok) => import('node:http').then(({ get: g }) => g({ port, host: '127.0.0.1', path: '/../../etc/passwd' }, (res) => { res.resume(); ok(res.statusCode); })));
  assert.ok(t === 403 || t === 404);
  assert.equal((await fetch(`http://127.0.0.1:${port}/nope.js`)).status, 404);
  await emu.close();
});
