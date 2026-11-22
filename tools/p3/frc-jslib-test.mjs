// tools/p3/frc-jslib-test.mjs — runs Plugins/WebGL/FallRushChain.jslib + StreamingAssets/fallrush-chain.js in Node
// with a fake Emscripten heap against a live chain emulator, calling the jslib exactly as BundleChainTransport.cs does:
// boot -> state -> join -> join_result -> subscribe -> drain_worlds (framing) -> send_inputs -> drain_events (confirms).
//   node tools/p3/frc-jslib-test.mjs      (starts its own emulator on a free port)
import { readFileSync } from 'node:fs';
import { spawn } from 'node:child_process';
import vm from 'node:vm';
const SA = 'client-unity/Assets/StreamingAssets';
const port = 18900 + (process.pid % 90);
const emu = spawn(process.execPath, ['tools/chain-emulator/src/main.mjs', '--port', String(port), '--no-shaping', '--confirm', '20'], { stdio: 'ignore' });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
await sleep(800);
const lib = {}; globalThis.LibraryManager = { library: lib }; globalThis.mergeInto = (a, b) => Object.assign(a, b);
const HEAP = new Uint8Array(4 << 20); globalThis.HEAPU8 = HEAP; let top = 64;
const alloc = (n) => { const p = top; top += (n + 15) & ~15; return p; };
const put = (b) => { const p = alloc(b.length); HEAP.set(b, p); return p; };
const str = (s) => put(new TextEncoder().encode(s + '\0'));
globalThis.UTF8ToString = (p) => { if (!p) return ''; let e = p; while (HEAP[e]) e++; return new TextDecoder().decode(HEAP.subarray(p, e)); };
globalThis.lengthBytesUTF8 = (s) => new TextEncoder().encode(s).length;
globalThis.stringToUTF8 = (s, p, n) => { const b = new TextEncoder().encode(s); HEAP.set(b.subarray(0, n - 1), p); HEAP[p + Math.min(b.length, n - 1)] = 0; };
globalThis._malloc = alloc;
globalThis.window = globalThis;
globalThis.localStorage = { m: new Map(), getItem(k) { return this.m.get(k) ?? null; }, setItem(k, v) { this.m.set(k, String(v)); }, removeItem(k) { this.m.delete(k); } };
globalThis.document = { head: { appendChild: (s) => { try { vm.runInThisContext(readFileSync(s.src, 'utf8'), { filename: s.src }); s.onload(); } catch (e) { s.onerror(e); } } }, createElement: () => ({}) };
const realFetch = globalThis.fetch;
globalThis.fetch = async (u) => (u.startsWith('http') ? realFetch(u) : { ok: true, json: async () => JSON.parse(readFileSync(u, 'utf8')) });
new Function(readFileSync('client-unity/Assets/_Game/Plugins/WebGL/FallRushChain.jslib', 'utf8'))();
globalThis.FRC = lib.$FRC;
const f = Object.fromEntries(Object.entries(lib).filter(([k]) => k.startsWith('fr_') && !k.includes('__')));
const cstr = (p) => (p ? UTF8ToString(p) : null);
let bad = 0; const check = (c, m) => { console.log(`${c ? 'PASS' : 'FAIL'} ${m}`); if (!c) bad++; };
f.fr_boot(str(`${SA}/fallrush-chain.js`), str(`${SA}/chain/emulator.json`), str(`ws://127.0.0.1:${port}/`));
for (let i = 0; i < 100 && f.fr_state() === 2; i++) await sleep(50);
check(f.fr_state() === 1, `boot (state ${f.fr_state()} ${cstr(f.fr_error())})`);
f.fr_join(str('{"name":"jslib","spectate":false}'));
let jr = null; for (let i = 0; i < 100 && !jr; i++) { await sleep(50); jr = cstr(f.fr_join_result()); }
check(jr && JSON.parse(jr).ok && JSON.parse(jr).slot === 0, `join ${jr?.slice(0, 80)}`);
f.fr_subscribe(str('$world'));
await sleep(1500);
const cap = 64 * 2200, buf = alloc(cap);
const n = f.fr_drain_worlds(buf, cap, 64);
const dv = new DataView(HEAP.buffer, buf, n); let o = 0, items = 0, lastTick = -1, wl = 0;
while (o + 16 <= n) { const len = dv.getInt32(o, true); const at = buf + o + 16; lastTick = new DataView(HEAP.buffer, at, 4).getUint32(0, true); wl = new DataView(HEAP.buffer, at + 14, 2).getUint16(0, true); o += 16 + len; items++; }
check(items >= 20 && wl === 2037, `drain_worlds ${items} worlds in 1.5 s, ${n} B, last tick ${lastTick}, world_len ${wl}`);
const batch = new Uint8Array(21); batch[0] = 0; new DataView(batch.buffer).setUint32(1, lastTick + 4, true); batch[5] = 3;
const id = f.fr_send_inputs(put(batch), 21);
await sleep(600);
const ev = JSON.parse(cstr(f.fr_drain_events()));
const conf = ev.find((e) => e.type === 'confirm' && e.txid === id);
check(conf && conf.status === 'ok', `send_inputs txid ${id} confirm ${JSON.stringify(conf)?.slice(0, 100)}`);
const st = JSON.parse(cstr(f.fr_stats()));
check(st.updatesPerSec > 20, `stats updates/s ${st.updatesPerSec}`);
f.fr_stop(); emu.kill();
console.log(bad ? `FAIL (${bad})` : 'ALL PASS');
process.exit(bad ? 1 : 0);
