#!/usr/bin/env node
// chain-relay.mjs — the Mac player's way onto the real rollup (P3). Runs the browser chain bundle
// (client-unity/Assets/StreamingAssets/fallrush-chain.js) in Node and serves the emulator WebSocket protocol
// (docs/proof/p3/wire-v0.md §4) on ws://127.0.0.1:8788/, so the Unity WsChainTransport needs no change:
//   join/joined · 0x02 inputs → FallRushChain.sendInputs · 0x83 confirms · 0x90/0x91 World frames (the bundle's
//   WorldView re-wrapped into the wire-v0 §3 WorldAcc) · ping/pong · {"t":"get"}.
// Keys: a localStorage shim persisted to tools/p3/.relay-keys/<net>.json (mode 600, gitignored, never printed).
//
//   node tools/p3/chain-relay.mjs --net local
//   node tools/p3/chain-relay.mjs --net devnet --fund-from ~/.config/solana/id.json [--fund-lamports 10000000]
// Flags: --net local|devnet|emulator · --config <json> · --port 8788 · --host 127.0.0.1 · --bundle <js> · --keys <file>
//        --arena <id> (P2 arena id; derives the PDAs) · --no-session (burner signs) · --fund-from <keypair.json>
//        --fund-lamports N (default 10_000_000 = 0.01 SOL; sent only when the burner holds less than needed)
import { createServer } from 'node:http';
import { readFileSync, writeFileSync, existsSync, mkdirSync, chmodSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { homedir } from 'node:os';
import vm from 'node:vm';
import { upgrade } from '../chain-emulator/src/ws.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(HERE, '../..');
export const DEFAULT_BUNDLE = join(ROOT, 'client-unity/Assets/StreamingAssets/fallrush-chain.js');
export const DEFAULT_PORT = 8788;
const LAMPORTS = 1e9;

// ---- wire-v0 frames ---------------------------------------------------------------------------------------------
export const OP = { INPUT: 0x02, CONFIRM: 0x83, NOTIFY: 0x90, POLL: 0x91 };
const MAGIC = Buffer.from('FRWORLD0');

/** Bundle WorldView (tick 0 · core 4 · seq 8 · flags 12 · len 14 · applied 16..64 · World 64) → wire-v0 §3 WorldAcc. */
export function viewToWorldAcc(view) {
  const v = Buffer.from(view.buffer, view.byteOffset, view.byteLength);
  const len = v.readUInt16LE(14);
  const out = Buffer.alloc(72 + len);
  MAGIC.copy(out, 0);
  out.writeUInt32LE(v.readUInt32LE(4), 8); // core_version (P2: 0)
  out.writeUInt32LE(v.readUInt32LE(0), 12); // tick
  v.copy(out, 16, 16, 64); // applied_tick[12]
  out.writeUInt32LE(v.readUInt32LE(8), 64); // arena_seq
  out.writeUInt16LE(len, 68);
  out.writeUInt16LE(v.readUInt16LE(12), 70); // flags
  v.copy(out, 72, 64, 64 + len);
  return out;
}

export function accFrame(op, seq, acc) {
  const f = Buffer.alloc(5 + acc.length);
  f[0] = op;
  f.writeUInt32LE(seq >>> 0, 1);
  acc.copy(f, 5);
  return f;
}

export function confirmFrame(txid, status, chainTick) {
  const f = Buffer.alloc(10);
  f[0] = OP.CONFIRM;
  f.writeUInt32LE(txid >>> 0, 1);
  f[5] = status;
  f.writeUInt32LE(Math.max(0, chainTick) >>> 0, 6);
  return f;
}

/** Bundle confirm event → wire status (0 ok, 1 late, 2 out of window, 3 bad slot). */
export function confirmStatus(ev, errors = {}) {
  const s = ev.status;
  if (s === 'ok') return 0;
  if (s === 'late') return 1;
  if (s === 'window') return 2;
  if (s === 'badSlot') return 3;
  const m = String(ev.err ?? '').match(/"Custom":(\d+)/);
  const code = m ? Number(m[1]) : null;
  if (code === (errors.inputOutOfWindow ?? 6020)) return 2;
  if (code === 6000 || code === 6005) return 3; // Unauthorized / InvalidSlot
  return 1;
}

// ---- persisted localStorage shim --------------------------------------------------------------------------------
export function fileStorage(file) {
  mkdirSync(dirname(file), { recursive: true });
  const gi = join(dirname(file), '.gitignore');
  if (!existsSync(gi)) writeFileSync(gi, '*\n');
  let m = {};
  if (existsSync(file)) { try { m = JSON.parse(readFileSync(file, 'utf8')); } catch { m = {}; } }
  const save = () => { writeFileSync(file, JSON.stringify(m), { mode: 0o600 }); chmodSync(file, 0o600); };
  return {
    getItem: (k) => (Object.prototype.hasOwnProperty.call(m, k) ? m[k] : null),
    setItem: (k, v) => { m[k] = String(v); save(); },
    removeItem: (k) => { delete m[k]; save(); },
  };
}

// ---- base58 (Solana CLI keypair → secret) -----------------------------------------------------------------------
const B58 = '123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz';
function b58encode(bytes) {
  let zeros = 0;
  while (zeros < bytes.length && bytes[zeros] === 0) zeros++;
  const d = [];
  for (let i = zeros; i < bytes.length; i++) {
    let c = bytes[i];
    for (let j = 0; j < d.length; j++) { c += d[j] << 8; d[j] = c % 58; c = (c / 58) | 0; }
    while (c) { d.push(c % 58); c = (c / 58) | 0; }
  }
  return '1'.repeat(zeros) + d.reverse().map((x) => B58[x]).join('');
}

/** Loads the bundle into this realm (sets globalThis.FallRushChain). */
export function loadBundle(path = DEFAULT_BUNDLE) {
  vm.runInThisContext(readFileSync(path, 'utf8'), { filename: path });
  if (!globalThis.FallRushChain) throw new Error('bundle did not set globalThis.FallRushChain');
  return globalThis.FallRushChain;
}

const sol = (l) => (l / LAMPORTS).toFixed(6) + ' SOL';

/**
 * Starts the relay around an initialised `chain` (the bundle API, or a stub in tests).
 * Returns { port, close, conns }.
 */
export async function startRelay({ chain, port = DEFAULT_PORT, host = '127.0.0.1', log = console.log, tickMs = 10, errors = {} }) {
  const conns = new Set();
  const sent = new Map(); // bundle txid → { c, txid }
  let joinState = null; // { promise, result }
  let seq = 0;
  let subscribed = false;
  const buf = new Uint8Array(1 << 18);

  const sendText = (c, o) => c.ws.sendText(JSON.stringify(o));
  const broadcastJoined = (bin) => { for (const c of conns) if (c.joined) c.ws.sendBinary(bin); };

  async function doJoin(c, m) {
    if (joinState?.result?.ok) { c.joined = true; sendText(c, { t: 'joined', ...joinState.result }); return; }
    if (!joinState) {
      const args = { name: m.name, spectate: !!m.spectate };
      if (Number.isInteger(m.slot)) args.slot = m.slot;
      if (typeof m.match === 'string' && m.match) args.match = m.match;
      log(`[relay] join ${JSON.stringify(args)} (waits for the arena lobby and start_final)`);
      joinState = { promise: chain.join(JSON.stringify(args)).then((s) => JSON.parse(s)), result: null, waiters: new Set() };
      joinState.promise.then((r) => {
        const js = joinState;
        if (!r.ok) {
          log(`[relay] join failed: ${r.error}`);
          for (const w of js.waiters) if (conns.has(w)) sendText(w, { t: 'err', why: r.error });
          joinState = null;
          return;
        }
        js.result = r;
        log(`[relay] joined: slot ${r.slot} arena ${r.arena} match ${r.match ?? '-'} roster ${JSON.stringify(r.roster)}`);
        if (!subscribed) { chain.subscribe('$world'); subscribed = true; }
        for (const w of js.waiters) if (conns.has(w)) { w.joined = true; sendText(w, { t: 'joined', ...r }); }
      });
    }
    joinState.waiters.add(c);
  }

  function onText(c, s) {
    let m;
    try { m = JSON.parse(s); } catch { return; }
    if (m.t === 'ping') return sendText(c, { t: 'pong', c: m.c });
    if (m.t === 'join') return void doJoin(c, m);
    if (m.t === 'get') {
      const b64 = chain.latestWorldBase64();
      if (b64 && c.joined) c.ws.sendBinary(accFrame(OP.POLL, seq, viewToWorldAcc(Buffer.from(b64, 'base64'))));
    }
  }

  function onBinary(c, b) {
    if (b.length < 5 || b[0] !== OP.INPUT) return;
    const txid = b.readUInt32LE(1);
    const batch = new Uint8Array(b.subarray(5));
    const id = chain.sendInputs(batch);
    if (id) sent.set(id, { c, txid });
  }

  // pump: Worlds → 0x90 to joined clients; events → confirms, logs, join progress
  const pump = setInterval(() => {
    const n = chain.drainWorlds(buf, 64);
    let at = 0;
    const dv = new DataView(buf.buffer);
    while (at < n) {
      const len = dv.getUint32(at, true);
      const view = buf.subarray(at + 16, at + 16 + len);
      at += 16 + len;
      if ([...conns].some((c) => c.joined)) broadcastJoined(accFrame(OP.NOTIFY, ++seq, viewToWorldAcc(view))); // seq counts only sent frames (Unity counts gaps by it)
    }
    let evs;
    try { evs = JSON.parse(chain.drainEvents()); } catch { evs = []; }
    for (const e of evs) {
      if (e.type === 'confirm' && (e.kind === undefined || e.kind === 'input')) {
        const s = sent.get(e.txid);
        if (!s) continue;
        sent.delete(e.txid);
        if (conns.has(s.c)) s.c.ws.sendBinary(confirmFrame(s.txid, confirmStatus(e, errors), chain.latestTick()));
      } else if (e.type === 'join') {
        log(`[relay] join: ${e.stage} ${JSON.stringify({ ...e, type: undefined, stage: undefined, at: undefined })}`);
        for (const c of conns) sendText(c, { t: 'chain', ev: e });
      } else if (e.type === 'session') {
        if (e.state === 'active') log(`[relay] SOL: burner → session token rent (~${sol((144 + 128) * 3480 * 2)}) + session top-up (create_session_v2)`);
        else if (e.state === 'toppedUp') log(`[relay] SOL: burner → session ${sol(Number(e.lamports ?? 0))} (top-up)`);
        else if (e.state === 'revoked') log('[relay] SOL: session token closed, rent back to the burner (revoke_session_v2)');
        else log(`[relay] session: ${e.state}${e.why ? ' ' + e.why : ''}`);
      } else if (e.type === 'error' || e.type === 'wsReconnect' || e.type === 'gap' || e.type === 'arenaChanged' || e.type === 'clonerRetry') {
        if (e.type !== 'gap') log(`[relay] ${e.type} ${JSON.stringify({ ...e, type: undefined, at: undefined })}`);
      }
    }
  }, tickMs);

  const statusTimer = setInterval(() => {
    try {
      const s = JSON.parse(chain.stats());
      log(`[relay] tick ${s.lastTick} upd/s ${s.updatesPerSec} in/s ${s.inputBatchesPerSec} confirm p50/p95 ${s.confirmP50}/${s.confirmP95} ms rtt ${s.rttMs} gaps ${s.gaps} slices ${s.slicesDropped ?? 0} clients ${conns.size}`);
    } catch { /* not initialised */ }
  }, 5000);

  const http = createServer((req, res) => {
    if (req.method === 'GET' && req.url === '/status') {
      res.writeHead(200, { 'content-type': 'application/json' });
      let stats = {};
      try { stats = JSON.parse(chain.stats()); } catch { /* ignore */ }
      res.end(JSON.stringify({ wallet: JSON.parse(chain.wallet()), stats, joined: joinState?.result ?? null, clients: conns.size }));
      return;
    }
    res.writeHead(404); res.end();
  });
  http.on('upgrade', (req, sock, head) => {
    const ws = upgrade(req, sock, head);
    if (!ws) return;
    const c = { ws, joined: false };
    conns.add(c);
    ws.on('text', (s) => onText(c, s));
    ws.on('binary', (b) => onBinary(c, b));
    ws.on('close', () => { conns.delete(c); joinState?.waiters?.delete(c); });
  });
  await new Promise((r) => http.listen(port, host, r));
  const real = http.address().port;
  return {
    port: real,
    conns,
    close: () => new Promise((r) => {
      clearInterval(pump); clearInterval(statusTimer);
      for (const c of conns) c.ws.close();
      http.close(() => r());
    }),
  };
}

// ---- CLI --------------------------------------------------------------------------------------------------------
function parseArgs(argv) {
  const o = { net: 'local', port: DEFAULT_PORT, host: '127.0.0.1', fundLamports: 10_000_000, session: true };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const v = () => { const x = argv[++i]; if (x === undefined) throw new Error(a + ' needs a value'); return x; };
    if (a === '--net') o.net = v();
    else if (a === '--config') o.config = v();
    else if (a === '--port') o.port = Number(v());
    else if (a === '--host') o.host = v();
    else if (a === '--bundle') o.bundle = v();
    else if (a === '--keys') o.keys = v();
    else if (a === '--arena') o.arena = Number(v());
    else if (a === '--no-session') o.session = false;
    else if (a === '--fund-from') o.fundFrom = v().replace(/^~(?=\/)/, homedir());
    else if (a === '--fund-lamports') o.fundLamports = Number(v());
    else if (a === '--help' || a === '-h') o.help = true;
    else throw new Error('unknown flag ' + a);
  }
  if (!['local', 'devnet', 'emulator'].includes(o.net) && !o.config) throw new Error('--net local|devnet|emulator');
  if (!(o.fundLamports > 0)) throw new Error('--fund-lamports must be > 0');
  return o;
}

async function main() {
  let o;
  try { o = parseArgs(process.argv.slice(2)); } catch (e) { console.error(`chain-relay: ${e.message} (--help)`); process.exit(2); }
  if (o.help) { console.log(readFileSync(fileURLToPath(import.meta.url), 'utf8').split('\n').slice(1, 14).join('\n')); return; }
  const cfgPath = o.config ?? join(ROOT, `packages/chain/config/${o.net}.json`);
  const cfg = JSON.parse(readFileSync(cfgPath, 'utf8'));
  if (cfg.rollup?.p2) {
    if (o.arena !== undefined) { cfg.rollup.p2.arenaId = o.arena; delete cfg.addresses.arena; delete cfg.addresses.world; }
    if (!o.session) cfg.rollup.p2.useSession = false;
  }
  const keysFile = o.keys ?? join(HERE, '.relay-keys', `${o.net}.json`);
  globalThis.localStorage = fileStorage(keysFile);
  const chain = loadBundle(o.bundle ?? DEFAULT_BUNDLE);
  const init = JSON.parse(await chain.init(JSON.stringify(cfg)));
  if (!init.ok) { console.error(`chain-relay: init failed: ${init.error}`); process.exit(1); }
  console.log(`[relay] ${cfg.name}: burner ${init.burner} session ${init.sessionPubkey} (keys ${keysFile})`);
  if (cfg.transport === 'rollup') {
    const b = JSON.parse(await chain.devBalances());
    if (b.ok) {
      console.log(`[relay] balance: burner ${sol(b.burner)}, session ${sol(b.session)}; a session costs ~${sol(b.needed)}`);
      if (b.burner < b.needed) {
        if (o.fundFrom) {
          const kp = Uint8Array.from(JSON.parse(readFileSync(o.fundFrom, 'utf8')));
          const lamports = Math.max(o.fundLamports, b.needed - b.burner);
          console.log(`[relay] SOL: funding burner ${b.burnerPubkey} with ${sol(lamports)} from ${o.fundFrom} …`);
          const r = JSON.parse(await chain.devFund(b58encode(kp), lamports));
          kp.fill(0);
          console.log(r.ok ? `[relay] SOL: ${r.from} → burner ${r.to} ${sol(r.lamports)} (sig ${r.sig})` : `[relay] funding failed: ${r.error}`);
        } else {
          console.log(`[relay] WARNING: burner ${b.burnerPubkey} has ${sol(b.burner)} < ${sol(b.needed)}; pass --fund-from <keypair.json> or fund it by hand (the session falls back to the burner signing)`);
        }
      }
    } else console.log(`[relay] balance check failed: ${b.error}`);
  }
  const relay = await startRelay({ chain, port: o.port, host: o.host, errors: cfg.rollup?.p2?.errors });
  console.log(`[relay] listening ws://${o.host}:${relay.port}/ (wire-v0 §4) → ${cfg.transport} ${cfg.rollup?.erHttp ?? cfg.emulator?.url}`);
  const stop = () => {
    try { const s = JSON.parse(chain.stats()); if (s.receipt) console.log(`[relay] receipt ${JSON.stringify(s.receipt)}`); } catch { /* ignore */ }
    relay.close().then(() => { chain.stop(); process.exit(0); });
    setTimeout(() => process.exit(0), 1000).unref();
  };
  process.on('SIGINT', stop);
  process.on('SIGTERM', stop);
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) main();
