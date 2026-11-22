// ws.mjs — a minimal RFC 6455 WebSocket server over node:http 'upgrade'. No dependencies.
// Supports text / binary / ping / pong / close, masked client frames (required), payloads up to 64 KiB.
// Fragmented messages (FIN = 0 or continuation frames) are rejected with close 1003; no extensions.
import { createHash } from 'node:crypto';
import { EventEmitter } from 'node:events';

const GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';
export const MAX_PAYLOAD = 64 * 1024;
export const OP = { CONT: 0, TEXT: 1, BINARY: 2, CLOSE: 8, PING: 9, PONG: 10 };

export function acceptKey(key) {
  return createHash('sha1').update(key + GUID).digest('base64');
}

/** Encodes one unmasked (server → client) frame, or a masked one when `mask` (4 bytes) is given (tests/clients). */
export function encodeFrame(opcode, payload, mask = null) {
  const data = typeof payload === 'string' ? Buffer.from(payload) : Buffer.from(payload.buffer, payload.byteOffset, payload.byteLength);
  const n = data.length;
  const ext = n < 126 ? 0 : n < 65536 ? 2 : 8;
  const head = Buffer.alloc(2 + ext + (mask ? 4 : 0));
  head[0] = 0x80 | opcode;
  head[1] = (mask ? 0x80 : 0) | (ext === 0 ? n : ext === 2 ? 126 : 127);
  if (ext === 2) head.writeUInt16BE(n, 2);
  else if (ext === 8) head.writeBigUInt64BE(BigInt(n), 2);
  if (!mask) return Buffer.concat([head, data]);
  mask.copy ? mask.copy(head, 2 + ext) : head.set(mask, 2 + ext);
  const body = Buffer.alloc(n);
  for (let i = 0; i < n; i++) body[i] = data[i] ^ mask[i & 3];
  return Buffer.concat([head, body]);
}

/** One server-side connection. Events: 'text' (string), 'binary' (Buffer), 'close' (code, reason). */
export class WsConn extends EventEmitter {
  constructor(socket, req) {
    super();
    this.socket = socket;
    this.req = req;
    this.buf = Buffer.alloc(0);
    this.open = true;
    socket.setNoDelay(true);
    socket.on('data', (d) => this._data(d));
    socket.on('close', () => this._closed(1006, ''));
    socket.on('error', () => this._closed(1006, 'socket error'));
  }

  sendText(s) { this._write(OP.TEXT, s); }
  sendBinary(b) { this._write(OP.BINARY, b); }
  send(x) { typeof x === 'string' ? this.sendText(x) : this.sendBinary(x); }

  close(code = 1000, reason = '') {
    if (!this.open) return;
    const p = Buffer.alloc(2 + Buffer.byteLength(reason));
    p.writeUInt16BE(code, 0);
    p.write(reason, 2);
    this._write(OP.CLOSE, p);
    this.socket.end();
    this._closed(code, reason);
  }

  _write(op, payload) {
    if (!this.open || this.socket.destroyed) return;
    this.socket.write(encodeFrame(op, payload));
  }

  _closed(code, reason) {
    if (!this.open) return;
    this.open = false;
    this.emit('close', code, reason);
  }

  _data(d) {
    this.buf = this.buf.length ? Buffer.concat([this.buf, d]) : d;
    while (this.open) {
      const b = this.buf;
      if (b.length < 2) return;
      const fin = (b[0] & 0x80) !== 0;
      const op = b[0] & 0x0f;
      const masked = (b[1] & 0x80) !== 0;
      let n = b[1] & 0x7f;
      let o = 2;
      if (n === 126) { if (b.length < 4) return; n = b.readUInt16BE(2); o = 4; }
      else if (n === 127) {
        if (b.length < 10) return;
        const big = b.readBigUInt64BE(2);
        if (big > BigInt(MAX_PAYLOAD)) return this.close(1009, 'too big');
        n = Number(big); o = 10;
      }
      if (b[0] & 0x70) return this.close(1002, 'rsv bits');
      if (!masked) return this.close(1002, 'unmasked client frame');
      if (n > MAX_PAYLOAD) return this.close(1009, 'too big');
      if (b.length < o + 4 + n) return;
      const mask = b.subarray(o, o + 4);
      const payload = Buffer.alloc(n);
      for (let i = 0; i < n; i++) payload[i] = b[o + 4 + i] ^ mask[i & 3];
      this.buf = b.subarray(o + 4 + n);
      if (op >= 8) {
        if (!fin || n > 125) return this.close(1002, 'bad control frame');
        if (op === OP.PING) this._write(OP.PONG, payload);
        else if (op === OP.CLOSE) {
          const code = n >= 2 ? payload.readUInt16BE(0) : 1005;
          this.close(code === 1005 ? 1000 : code, '');
        } else if (op !== OP.PONG) return this.close(1002, 'bad opcode');
        continue;
      }
      if (!fin || op === OP.CONT) return this.close(1003, 'fragmentation not supported');
      if (op === OP.TEXT) this.emit('text', payload.toString('utf8'));
      else if (op === OP.BINARY) this.emit('binary', payload);
      else return this.close(1002, 'bad opcode');
    }
  }
}

/**
 * Handles an http 'upgrade' event. Returns the WsConn, or null (and writes a 400) for a bad handshake.
 */
export function upgrade(req, socket, head) {
  const key = req.headers['sec-websocket-key'];
  const okUpgrade = String(req.headers.upgrade || '').toLowerCase() === 'websocket';
  const okVersion = String(req.headers['sec-websocket-version'] || '') === '13';
  if (req.method !== 'GET' || !okUpgrade || !okVersion || !key || Buffer.from(key, 'base64').length !== 16) {
    socket.end('HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n');
    return null;
  }
  socket.write(
    'HTTP/1.1 101 Switching Protocols\r\n' +
    'Upgrade: websocket\r\n' +
    'Connection: Upgrade\r\n' +
    `Sec-WebSocket-Accept: ${acceptKey(key)}\r\n\r\n`,
  );
  const c = new WsConn(socket, req);
  if (head && head.length) c._data(head);
  return c;
}
