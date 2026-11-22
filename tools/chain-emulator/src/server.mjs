// server.mjs — the emulator's transport: HTTP control + optional static files + hand-written WebSocket (ws.mjs) on
// one port, per-connection shaping (shaping.mjs) in both directions, wire-v0 §4/§5 frames around an Arena.
import { createServer } from 'node:http';
import { appendFileSync, createReadStream, statSync } from 'node:fs';
import { extname, join, normalize, resolve, sep } from 'node:path';
import { Arena, Ticker, RATE, defaultRoster, parseSeed, seedHex, CMD_BYTES } from './arena.mjs';
import { upgrade } from './ws.mjs';
import { Link, realClock, rng32 } from './shaping.mjs';
import { MAX_PLAYERS } from './core.mjs';

export const FRAME = { INPUT: 0x02, CONFIRM: 0x83, NOTIFY: 0x90, POLL: 0x91 };
export const SPECTATOR = 255;

const MIME = {
  '.html': 'text/html; charset=utf-8', '.js': 'application/javascript', '.mjs': 'application/javascript',
  '.wasm': 'application/wasm', '.data': 'application/octet-stream', '.json': 'application/json',
  '.css': 'text/css', '.png': 'image/png', '.jpg': 'image/jpeg', '.svg': 'image/svg+xml', '.ico': 'image/x-icon',
  '.txt': 'text/plain; charset=utf-8', '.bytes': 'application/octet-stream', '.mapblob': 'application/octet-stream',
};

/** Parses a 0x02 frame: u8 0x02, u32 txid, InputBatch {u8 slot, u32 first_tick, u8 count, count × 5 B}. */
export function parseInputFrame(b) {
  if (b.length < 11 || b[0] !== FRAME.INPUT) return null;
  const dv = new DataView(b.buffer, b.byteOffset, b.byteLength);
  const txid = dv.getUint32(1, true), slot = b[5], firstTick = dv.getUint32(6, true), count = b[10];
  if (count < 1 || count > 3 || b.length !== 11 + count * CMD_BYTES) return { txid, bad: true };
  const cmds = [];
  for (let k = 0; k < count; k++) cmds.push(b.subarray(11 + k * CMD_BYTES, 11 + (k + 1) * CMD_BYTES));
  return { txid, slot, firstTick, cmds };
}

/** Builds a 0x02 frame (clients / tests). cmds: array of 5-byte arrays. */
export function inputFrame(txid, slot, firstTick, cmds) {
  const b = new Uint8Array(11 + cmds.length * CMD_BYTES);
  const dv = new DataView(b.buffer);
  b[0] = FRAME.INPUT; dv.setUint32(1, txid, true); b[5] = slot; dv.setUint32(6, firstTick, true); b[10] = cmds.length;
  cmds.forEach((c, k) => b.set(c, 11 + k * CMD_BYTES));
  return b;
}

export function confirmFrame(txid, status, chainTick) {
  const b = new Uint8Array(10);
  const dv = new DataView(b.buffer);
  b[0] = FRAME.CONFIRM; dv.setUint32(1, txid, true); b[5] = status; dv.setUint32(6, chainTick, true);
  return b;
}

export function accFrame(kind, seq, acc) {
  const b = new Uint8Array(5 + acc.length);
  b[0] = kind; new DataView(b.buffer).setUint32(1, seq, true); b.set(acc, 5);
  return b;
}

export class Emulator {
  /**
   * o: port (8797), host ('127.0.0.1'), map, players (3), humans (1), delay (45), jitter (10), loss (0.001),
   * confirm (120), seed (hex | BigInt), shaping (true), autostart (20 s), restart (8 s), once, static (dir), log
   * (file), clock (realClock), ticker (true: run the 30 Hz real-time loop), quiet.
   */
  constructor(o = {}) {
    this.o = { port: 8797, host: '127.0.0.1', map: 'CrumbleShelf', players: 3, humans: 1, delay: 45, jitter: 10, loss: 0.001, confirm: 120, shaping: true, autostart: 20, restart: 8, once: false, clock: realClock, ticker: true, quiet: false, ...o };
    if (!this.o.shaping) Object.assign(this.o, { delay: 0, jitter: 0, loss: 0 });
    this.clock = this.o.clock;
    this.seed = this.o.seed === undefined ? BigInt('0x' + [...crypto.getRandomValues(new Uint8Array(8))].map((x) => x.toString(16).padStart(2, '0')).join('')) : parseSeed(this.o.seed);
    this.rand = rng32(Number(this.seed & 0xffffffffn) ^ 0x5eed);
    this.conns = new Set();
    this.nextConnId = 1;
    this.arena = new Arena({
      map: this.o.map, roster: defaultRoster(Math.min(MAX_PLAYERS, this.o.players), Math.min(this.o.humans, this.o.players)), seed: this.seed,
      autostartTicks: Math.round(this.o.autostart * RATE), restartTicks: Math.round(this.o.restart * RATE), once: this.o.once,
      wasmPath: this.o.wasmPath, isJoined: (s) => [...this.conns].some((c) => c.slot === s),
    });
    this.defaults = { map: this.o.map, roster: this.arena.roster, seed: this.seed, match: null };
    this.arena.on('publish', (seq, acc) => {
      const f = accFrame(FRAME.NOTIFY, seq, acc);
      for (const c of this.conns) c.down.send(f, true);
    });
    this.arena.on('arena', () => this._onArena());
    this.arena.on('log', (e) => this.log(e));
    this.arena.on('done', () => { this.log({ ev: 'done' }); this.onDone?.(); });
    this.log({ ev: 'arena_start', ...this.arena.info() });
    this.http = createServer((req, res) => this._http(req, res));
    this.http.on('upgrade', (req, sock, head) => this._upgrade(req, sock, head));
    this.ticker = new Ticker(() => this.arena.step(), { clock: this.clock });
  }

  log(e) {
    const line = { t: new Date().toISOString(), ...e };
    if (this.o.log) appendFileSync(this.o.log, JSON.stringify(line, (k, v) => (typeof v === 'bigint' ? v.toString() : v)) + '\n');
    if (!this.o.quiet && ['arena_start', 'round_begin', 'round_end', 'join', 'leave', 'final', 'reset', 'done'].includes(e.ev)) {
      const { t, ev, ...rest } = line;
      if (ev === 'round_end') console.log(`[emu] ${ev} ${rest.arena} champion slot ${rest.champion} after ${rest.ticks} RUSH ticks (tick ${rest.tick}${rest.timerExpired ? ', timer' : ''}) order ${rest.placements.map((p) => p.slot).join(',')}`);
      else console.log(`[emu] ${ev}`, JSON.stringify(rest));
    }
  }

  listen() {
    return new Promise((ok, fail) => {
      this.http.once('error', fail);
      this.http.listen(this.o.port, this.o.host, () => {
        this.port = this.http.address().port;
        if (this.o.ticker) this.ticker.start();
        ok(this.port);
      });
    });
  }

  close() {
    this.ticker.stop();
    for (const c of this.conns) { c.up.close(); c.down.close(); c.ws.close(1001, 'shutdown'); }
    this.conns.clear();
    this.arena.close();
    return new Promise((ok) => { this.http.close(() => ok()); this.http.closeAllConnections?.(); });
  }

  // ------------------------------------------------------------------ WebSocket

  _upgrade(req, sock, head) {
    const ws = upgrade(req, sock, head);
    if (!ws) return;
    const c = { id: this.nextConnId++, ws, slot: null, joined: false, name: null };
    c.down = new Link({ delayMs: this.o.delay, jitterMs: this.o.jitter, loss: this.o.loss }, this.clock, this.rand, (f) => ws.send(f));
    c.up = new Link({ delayMs: this.o.delay, jitterMs: this.o.jitter }, this.clock, this.rand, (m) => this._frame(c, m));
    ws.on('text', (s) => c.up.send(s));
    ws.on('binary', (b) => c.up.send(b));
    ws.on('close', () => {
      c.up.close(); c.down.close();
      this.conns.delete(c);
      if (c.joined) this.log({ ev: 'leave', conn: c.id, slot: c.slot });
    });
    this.conns.add(c);
  }

  _text(c, obj) { c.down.send(JSON.stringify(obj)); }

  _joinedMsg(c) {
    const a = this.arena;
    return {
      t: 'joined', slot: c.slot ?? SPECTATOR, arena: a.arenaId, map: a.mapKey, rate: RATE, coreVersion: a.core.version,
      match: a.match, seed: seedHex(a.seed),
      roster: a.roster.map((r) => ({ slot: r.slot, kind: r.kind, ...(r.name ? { name: r.name } : {}) })),
    };
  }

  /** A new arena started: slot holders keep their slot if it is still a human slot; everyone gets a fresh `joined`. */
  _onArena() {
    if (!this.conns) return;
    for (const c of this.conns) {
      if (!c.joined) continue;
      if (c.slot !== null && !this.arena.humanSlots.includes(c.slot)) c.slot = null;
      if (c.slot !== null) this._setName(c.slot, c.name);
      this._text(c, this._joinedMsg(c));
    }
  }

  _setName(slot, name) {
    const r = this.arena.roster.find((x) => x.slot === slot);
    if (r && name) r.name = String(name).slice(0, 32);
  }

  _frame(c, m) {
    if (typeof m === 'string') {
      let j;
      try { j = JSON.parse(m); } catch { return this._text(c, { t: 'err', why: 'bad json' }); }
      if (j.t === 'join') return this._join(c, j);
      if (j.t === 'get') {
        if (this.arena.lastAcc) c.down.send(accFrame(FRAME.POLL, this.arena.notifSeq, this.arena.lastAcc), false);
        return;
      }
      if (j.t === 'ping') return this._text(c, { t: 'pong', c: j.c });
      return this._text(c, { t: 'err', why: `unknown t ${j.t}` });
    }
    const b = new Uint8Array(m.buffer, m.byteOffset, m.byteLength);
    if (b[0] !== FRAME.INPUT) return this._text(c, { t: 'err', why: `unknown frame 0x${(b[0] ?? 0).toString(16)}` });
    const f = parseInputFrame(b);
    if (!f || f.bad) {
      this.arena.stats.window++;
      if (f) this._confirm(c, f.txid, 2, this.arena.tick);
      return;
    }
    let r;
    if (c.slot === null || f.slot !== c.slot) { this.arena.stats.badSlot++; this.arena.stats.batches++; r = { status: 3, chainTick: this.arena.tick }; }
    else r = this.arena.submit(f.slot, f.firstTick, f.cmds);
    this._confirm(c, f.txid, r.status, r.chainTick);
  }

  _confirm(c, txid, status, chainTick) {
    const f = confirmFrame(txid, status, chainTick);
    if (this.o.confirm > 0) this.clock.setTimeout(() => c.down.send(f), this.o.confirm);
    else c.down.send(f);
  }

  _join(c, j) {
    const a = this.arena;
    if (j.spectate) {
      c.slot = null; c.joined = true; c.name = j.name ?? null;
    } else {
      const taken = (s) => [...this.conns].some((o) => o !== c && o.slot === s);
      let slot;
      if (j.slot !== undefined && j.slot !== null) {
        slot = Number(j.slot);
        if (!a.humanSlots.includes(slot)) return this._text(c, { t: 'err', why: `slot ${slot} is not a human slot` });
        if (taken(slot)) return this._text(c, { t: 'err', why: `slot ${slot} is taken` });
      } else if (c.slot !== null) slot = c.slot;
      else {
        slot = a.humanSlots.find((s) => !taken(s));
        if (slot === undefined) return this._text(c, { t: 'full' });
      }
      c.slot = slot; c.joined = true; c.name = j.name ?? null;
      c.session = j.session; c.authority = j.authority;
      this._setName(slot, c.name);
    }
    this.log({ ev: 'join', conn: c.id, slot: c.slot ?? SPECTATOR, name: c.name });
    this._text(c, this._joinedMsg(c));
  }

  // ------------------------------------------------------------------ HTTP

  _json(res, code, obj) {
    const body = JSON.stringify(obj, (k, v) => (typeof v === 'bigint' ? v.toString() : v));
    res.writeHead(code, { 'Content-Type': 'application/json', 'Access-Control-Allow-Origin': '*', 'Cache-Control': 'no-store' });
    res.end(body);
  }

  _body(req) {
    return new Promise((ok, fail) => {
      let s = '';
      req.on('data', (d) => { s += d; if (s.length > 65536) { fail(new Error('body too big')); req.destroy(); } });
      req.on('end', () => ok(s));
      req.on('error', fail);
    });
  }

  status() {
    const s = this.arena.status();
    s.connections = [...this.conns].map((c) => ({ id: c.id, slot: c.slot ?? (c.joined ? SPECTATOR : null), name: c.name, down: c.down.stats, up: c.up.stats }));
    s.shaping = { delay: this.o.delay, jitter: this.o.jitter, loss: this.o.loss, confirm: this.o.confirm };
    s.results = this.arena.results.map((r) => ({ arena: r.arena, champion: r.champion, ticks: r.ticks, match: r.match }));
    s.ticker = { skipped: this.ticker.skipped };
    return s;
  }

  /** POST /final: restart as the Lid Flip Final with exactly this roster + seed. */
  final(body) {
    const roster = body.roster;
    if (!Array.isArray(roster) || roster.length < 1 || roster.length > MAX_PLAYERS) throw new Error('roster: 1..12 entries');
    const seen = new Set();
    const r = roster.map((e) => {
      const slot = Number(e.slot);
      if (!Number.isInteger(slot) || slot < 0 || slot >= MAX_PLAYERS || seen.has(slot)) throw new Error(`roster: bad or duplicate slot ${e.slot}`);
      if (e.kind !== 'human' && e.kind !== 'bot') throw new Error(`roster: kind must be human|bot (slot ${slot})`);
      seen.add(slot);
      return { slot, kind: e.kind, ...(e.name ? { name: String(e.name).slice(0, 32) } : {}) };
    }).sort((a, b) => a.slot - b.slot);
    if (body.seed === undefined) throw new Error('seed: 16 hex digits required');
    const seed = parseSeed(body.seed);
    this.log({ ev: 'final', match: body.match ?? null, seed: seedHex(seed), roster: r });
    this.arena.start({ map: 'CrumbleShelf', roster: r, seed, match: body.match ?? null });
    const humans = r.some((x) => x.kind === 'human');
    return { ok: true, arena: this.arena.arenaId, startsInMs: humans ? Math.round(this.o.autostart * 1000) : Math.round(1000 / RATE) };
  }

  reset() {
    this.log({ ev: 'reset' });
    this.arena.start({ ...this.defaults, roster: this.defaults.roster.map(({ slot, kind }) => ({ slot, kind })) });
    return { ok: true, arena: this.arena.arenaId };
  }

  async _http(req, res) {
    const url = new URL(req.url, 'http://x');
    try {
      if (req.method === 'OPTIONS') {
        res.writeHead(204, { 'Access-Control-Allow-Origin': '*', 'Access-Control-Allow-Methods': 'GET, POST', 'Access-Control-Allow-Headers': 'Content-Type' });
        return res.end();
      }
      if (url.pathname === '/status' && req.method === 'GET') return this._json(res, 200, this.status());
      if (url.pathname === '/final' && req.method === 'POST') {
        let body;
        try { body = JSON.parse((await this._body(req)) || '{}'); } catch { return this._json(res, 400, { ok: false, err: 'bad json' }); }
        try { return this._json(res, 200, this.final(body)); } catch (e) { return this._json(res, 400, { ok: false, err: e.message }); }
      }
      if (url.pathname === '/reset' && req.method === 'POST') { await this._body(req); return this._json(res, 200, this.reset()); }
      if (this.o.static && (req.method === 'GET' || req.method === 'HEAD')) return this._static(url, req, res);
      return this._json(res, 404, { ok: false, err: 'not found' });
    } catch (e) {
      return this._json(res, 500, { ok: false, err: e.message });
    }
  }

  _static(url, req, res) {
    const root = resolve(this.o.static);
    let rel = decodeURIComponent(url.pathname);
    if (rel.endsWith('/')) rel += 'index.html';
    const file = normalize(join(root, rel));
    if (file !== root && !file.startsWith(root + sep)) return this._json(res, 403, { ok: false, err: 'forbidden' });
    let st;
    try { st = statSync(file); } catch { return this._json(res, 404, { ok: false, err: 'not found' }); }
    if (st.isDirectory()) return this._static(new URL(url.pathname.replace(/\/?$/, '/'), 'http://x'), req, res);
    let ext = extname(file).toLowerCase();
    const headers = { 'Content-Length': st.size, 'Cache-Control': 'no-cache' };
    if (ext === '.br' || ext === '.gz') {
      headers['Content-Encoding'] = ext === '.br' ? 'br' : 'gzip';
      ext = extname(file.slice(0, -ext.length)).toLowerCase();
    }
    headers['Content-Type'] = MIME[ext] || 'application/octet-stream';
    res.writeHead(200, headers);
    if (req.method === 'HEAD') return res.end();
    createReadStream(file).pipe(res);
  }
}
