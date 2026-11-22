// WebSocket server (src/ws.mjs): handshake with Node's built-in WebSocket client, then raw-socket framing checks.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { connect } from 'node:net';
import { randomBytes } from 'node:crypto';
import { upgrade, acceptKey, encodeFrame, OP, MAX_PAYLOAD } from '../src/ws.mjs';

/** An echo server: text → text, binary → binary. */
async function echoServer() {
  const http = createServer((q, s) => s.end());
  http.on('upgrade', (req, sock, head) => {
    const c = upgrade(req, sock, head);
    if (!c) return;
    c.on('text', (t) => c.sendText(t));
    c.on('binary', (b) => c.sendBinary(b));
  });
  await new Promise((ok) => http.listen(0, '127.0.0.1', ok));
  return { http, port: http.address().port };
}

test('Sec-WebSocket-Accept matches RFC 6455 §1.3 example', () => {
  assert.equal(acceptKey('dGhlIHNhbXBsZSBub25jZQ=='), 's3pPLMBiTxaQ9kYGzzhZRbK+xOo=');
});

test('built-in WebSocket client: handshake + text + binary round trip (incl. 64 KiB)', async () => {
  const { http, port } = await echoServer();
  const ws = new WebSocket(`ws://127.0.0.1:${port}/`);
  ws.binaryType = 'arraybuffer';
  const got = [];
  await new Promise((ok, fail) => { ws.onopen = ok; ws.onerror = fail; });
  const done = new Promise((ok) => { ws.onmessage = (m) => { got.push(m.data); if (got.length === 4) ok(); }; });
  const small = new Uint8Array([0x90, 1, 2, 3]);
  const mid = randomBytes(300);
  const big = randomBytes(MAX_PAYLOAD);
  ws.send('héllo');
  ws.send(small);
  ws.send(mid);
  ws.send(big);
  await done;
  assert.equal(got[0], 'héllo');
  assert.deepEqual(new Uint8Array(got[1]), small);
  assert.deepEqual(Buffer.from(got[2]), mid);
  assert.deepEqual(Buffer.from(got[3]), big);
  const closed = new Promise((ok) => { ws.onclose = (e) => ok(e.code); });
  ws.close(1000);
  assert.equal(await closed, 1000);
  http.close();
});

/** Raw client: handshake, then returns {sock, frames()} where frames() parses server frames received so far. */
async function raw(port) {
  const sock = connect(port, '127.0.0.1');
  await new Promise((ok) => sock.once('connect', ok));
  const key = randomBytes(16).toString('base64');
  sock.write(`GET / HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: ${key}\r\nSec-WebSocket-Version: 13\r\n\r\n`);
  let buf = Buffer.alloc(0);
  sock.on('data', (d) => { buf = Buffer.concat([buf, d]); });
  const until = async (pred) => { for (let i = 0; i < 200 && !pred(); i++) await new Promise((r) => setTimeout(r, 5)); };
  await until(() => buf.indexOf('\r\n\r\n') >= 0);
  const end = buf.indexOf('\r\n\r\n');
  const head = buf.subarray(0, end).toString();
  buf = buf.subarray(end + 4);
  assert.match(head, /^HTTP\/1.1 101/);
  assert.ok(head.includes(`Sec-WebSocket-Accept: ${acceptKey(key)}`));
  const frames = () => {
    const out = [];
    let o = 0;
    while (buf.length - o >= 2) {
      let n = buf[o + 1] & 0x7f, h = 2;
      if (n === 126) { n = buf.readUInt16BE(o + 2); h = 4; }
      if (buf.length - o < h + n) break;
      out.push({ op: buf[o] & 15, fin: (buf[o] & 0x80) !== 0, masked: (buf[o + 1] & 0x80) !== 0, payload: buf.subarray(o + h, o + h + n) });
      o += h + n;
    }
    return out;
  };
  const closed = new Promise((ok) => sock.on('close', ok));
  return { sock, frames, until, closed };
}

test('raw framing: masked frames in pieces, ping → pong, server frames unmasked', async () => {
  const { http, port } = await echoServer();
  const r = await raw(port);
  const mask = randomBytes(4);
  const f = encodeFrame(OP.BINARY, new Uint8Array([7, 8, 9]), mask);
  r.sock.write(f.subarray(0, 3)); // split across TCP writes
  await new Promise((ok) => setTimeout(ok, 10));
  r.sock.write(Buffer.concat([f.subarray(3), encodeFrame(OP.PING, 'pp', randomBytes(4)), encodeFrame(OP.TEXT, 'x'.repeat(200), randomBytes(4))]));
  await r.until(() => r.frames().length >= 3);
  const fr = r.frames();
  assert.deepEqual([...fr[0].payload], [7, 8, 9]);
  assert.equal(fr[0].op, OP.BINARY);
  assert.equal(fr[0].masked, false);
  assert.equal(fr[1].op, OP.PONG);
  assert.equal(fr[1].payload.toString(), 'pp');
  assert.equal(fr[2].payload.toString(), 'x'.repeat(200));
  r.sock.write(encodeFrame(OP.CLOSE, Buffer.from([0x03, 0xe8]), randomBytes(4)));
  await r.until(() => r.frames().some((x) => x.op === OP.CLOSE));
  assert.equal(r.frames().at(-1).payload.readUInt16BE(0), 1000);
  await r.closed;
  http.close();
});

test('raw framing: unmasked client frame → close 1002; fragmentation → close 1003; oversize → 1009', async () => {
  const { http, port } = await echoServer();
  const cases = [
    [encodeFrame(OP.TEXT, 'hi'), 1002],
    [(() => { const f = encodeFrame(OP.TEXT, 'part', randomBytes(4)); f[0] &= 0x7f; return f; })(), 1003],
    [(() => { const h = Buffer.alloc(14); h[0] = 0x82; h[1] = 0x80 | 127; h.writeBigUInt64BE(BigInt(MAX_PAYLOAD + 1), 2); return h; })(), 1009],
  ];
  for (const [frame, code] of cases) {
    const r = await raw(port);
    r.sock.write(frame);
    await r.closed;
    const c = r.frames().find((x) => x.op === OP.CLOSE);
    assert.ok(c, 'close frame sent');
    assert.equal(c.payload.readUInt16BE(0), code);
  }
  http.close();
});

test('bad handshake → 400', async () => {
  const { http, port } = await echoServer();
  const sock = connect(port, '127.0.0.1');
  await new Promise((ok) => sock.once('connect', ok));
  sock.write('GET / HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 8\r\nSec-WebSocket-Key: AAAAAAAAAAAAAAAAAAAAAA==\r\n\r\n');
  const data = await new Promise((ok) => sock.once('data', ok));
  assert.match(data.toString(), /^HTTP\/1.1 400/);
  sock.destroy();
  http.close();
});
