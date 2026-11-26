#!/usr/bin/env node
// Demo readout: polls an arena's World account straight from MagicBlock's rollup RPC and prints the on-chain tick,
// world steps per second and every human player's position, live. With --log it also writes one JSON line per sample
// (wall-clock ms + the decoded values), which the demo video's "rollup readout" card replays in sync with the capture.
// Read-only: it sends no transactions. Node 20+, no dependencies.
//   node tools/demo/tick-watch.mjs [--arena 2|3] [--world <pubkey>] [--rpc https://devnet-as.magicblock.app] [--hz 10] [--log out.jsonl]
// Layout (programs/fall_rush/src/sim.rs WorldAcc, 8 B Anchor discriminator first): ticks_run @48, executions @56,
// tick @128, phase @138, round @139, World bytes @208. World (rush_core World::to_bytes, web/app/src/lib/core.ts):
// header 22 B, then 12 players × 115 B: x @+0, y @+4 (height), z @+8 (i32 mm), flags @+33 (1 active, 2 bot).
import { appendFileSync } from 'node:fs';
const opt = (k, d) => { const i = process.argv.indexOf('--' + k); return i > 0 ? process.argv[i + 1] : d; };
const WORLDS = { 2: 'HYJRSXpSGZ3HdjmZr4oMy3x6VgTWLcarkgHav7L4hSTQ', 3: '9Q5KBadvfx3Yoen1uqLUgbU1x6tXn4GmkEkjGqfPuRWn' };
const arena = Number(opt('arena', 2));
const WORLD = opt('world', WORLDS[arena]);
const RPC = opt('rpc', 'https://devnet-as.magicblock.app');
const HZ = Number(opt('hz', 10));
const LOG = opt('log', null);
const ROUNDS = ['Lobby', 'Capsule Run', 'Goo Tower', 'Star Fountain', 'Lid Flip', 'Done'];
let id = 0;
async function read() {
  const r = await fetch(RPC, { method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ jsonrpc: '2.0', id: ++id, method: 'getAccountInfo', params: [WORLD, { encoding: 'base64', commitment: 'processed' }] }) });
  const j = await r.json();
  const v = j?.result?.value;
  if (!v) throw new Error(j?.error?.message ?? 'no account');
  return { slot: j.result.context.slot, d: Buffer.from(v.data[0], 'base64') };
}
function decode(d) {
  const W = 208;
  const players = [];
  for (let s = 0; s < 12; s++) {
    const o = W + 22 + s * 115;
    if (o + 115 > d.length) break;
    const f = d[o + 33];
    if (!(f & 1)) continue;
    players.push({ slot: s, bot: (f & 2) !== 0, x: d.readInt32LE(o), y: d.readInt32LE(o + 4), z: d.readInt32LE(o + 8) });
  }
  return { tick: d.readUInt32LE(128), ticksRun: Number(d.readBigUInt64LE(48)), exec: Number(d.readBigUInt64LE(56)), phase: d[138], round: d[139], worldTick: d.readUInt32LE(W + 1), players };
}
const hist = [];
const m = (mm) => (mm / 1000).toFixed(2).padStart(7) + ' m';
console.log(`World ${WORLD} · ${RPC}`);
for (;;) {
  const t0 = Date.now();
  try {
    const { slot, d } = await read();
    const s = decode(d), t = Date.now();
    hist.push({ t, tick: s.tick }); while (hist.length && t - hist[0].t > 3000) hist.shift();
    const a = hist[0], rate = hist.length > 1 && t > a.t ? ((s.tick - a.tick) * 1000) / (t - a.t) : 0;
    if (LOG) appendFileSync(LOG, JSON.stringify({ t, slot, ...s, stepsPerS: +rate.toFixed(1) }) + '\n');
    const humans = s.players.filter((p) => !p.bot);
    process.stdout.write('\x1b[2J\x1b[H');
    console.log(`  on-chain tick   ${String(s.tick).padStart(8)}`);
    console.log(`  world steps/s   ${rate.toFixed(1).padStart(8)}`);
    console.log(`  round           ${ROUNDS[s.round] ?? s.round}   (rollup slot ${slot})`);
    console.log(`  players         ${s.players.length} (${humans.length} human)`);
    for (const p of humans) console.log(`  slot ${String(p.slot + 1).padStart(2)}  height ${m(p.y)}   x ${m(p.x)}   z ${m(p.z)}`);
  } catch (e) { console.error('read failed:', String(e?.message ?? e)); }
  const wait = 1000 / HZ - (Date.now() - t0);
  if (wait > 0) await new Promise((r) => setTimeout(r, wait));
}
