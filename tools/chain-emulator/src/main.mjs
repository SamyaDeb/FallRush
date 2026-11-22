#!/usr/bin/env node
// Fall Rush chain emulator — a fake MagicBlock Ephemeral Rollup running rush-core at 30 Hz (see ../README.md).
//   node tools/chain-emulator/src/main.mjs [--port 8787] [--map CrumbleShelf] [--players 3] [--humans 1]
//     [--delay 45] [--jitter 10] [--loss 0.001] [--confirm 120] [--seed <16 hex>] [--no-shaping]
//     [--autostart <s>] [--restart <s>] [--once] [--static <dir>] [--log <file.jsonl>] [--host 127.0.0.1]
import { Emulator } from './server.mjs';
import { seedHex } from './arena.mjs';

const NUM = { port: 1, players: 1, humans: 1, delay: 1, jitter: 1, loss: 1, confirm: 1, autostart: 1, restart: 1 };
const STR = { map: 1, seed: 1, static: 1, log: 1, host: 1 };

function parse(argv) {
  const o = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--help' || a === '-h') { o.help = true; continue; }
    if (a === '--no-shaping') { o.shaping = false; continue; }
    if (a === '--once') { o.once = true; continue; }
    const k = a.replace(/^--/, '');
    if (!(k in NUM) && !(k in STR)) throw new Error(`unknown flag ${a}`);
    const v = argv[++i];
    if (v === undefined) throw new Error(`${a} needs a value`);
    if (k in NUM) {
      const n = Number(v);
      if (!Number.isFinite(n) || n < 0) throw new Error(`${a}: bad number ${v}`);
      o[k] = n;
    } else o[k] = v;
  }
  if (o.players !== undefined && (o.players < 1 || o.players > 12 || !Number.isInteger(o.players))) throw new Error('--players: 1..12');
  if (o.humans !== undefined && o.humans > (o.players ?? 3)) throw new Error('--humans must be ≤ --players');
  if (o.loss !== undefined && o.loss > 1) throw new Error('--loss: 0..1');
  return o;
}

let o;
try { o = parse(process.argv.slice(2)); } catch (e) { console.error(`chain-emulator: ${e.message} (--help)`); process.exit(2); }
if (o.help) {
  console.log(`usage: node tools/chain-emulator/src/main.mjs [flags]
  --port 8787        HTTP + WebSocket port (ws://127.0.0.1:<port>/)
  --host 127.0.0.1   bind address
  --map CrumbleShelf arena map key in bin/maps (CrumbleShelf = Lid Flip Final; Lobby = free play, no round)
  --players 3        roster size (1..12); --humans 1: humans in the lowest slots, the rest bots
  --delay 45 --jitter 10 --loss 0.001 --confirm 120   shaping (ms, ms, fraction of notifications, ms)
  --no-shaping       delay / jitter / loss 0 (confirm latency stays)
  --seed <hex>       hazard seed (16 hex; default random); restarts derive the next seed
  --autostart 20     seconds to wait for human slots before starting the round
  --restart 8        seconds after ENDED before a fresh arena; --once: exit after the first round instead
  --static <dir>     also serve files from <dir> (.br / .gz → Content-Encoding)
  --log <file>       append JSONL lifecycle events`);
  process.exit(0);
}

const emu = new Emulator(o);
const port = await emu.listen();
console.log(`[emu] listening ws://${emu.o.host}:${port}/  map ${emu.o.map}  roster ${emu.arena.roster.map((r) => r.slot + ':' + r.kind[0]).join(' ')}  seed ${seedHex(emu.seed)}  shaping ${emu.o.delay}±${emu.o.jitter} ms loss ${emu.o.loss} confirm ${emu.o.confirm} ms${o.static ? '  static ' + o.static : ''}`);
emu.onDone = () => { console.log('[emu] --once: round over, exiting'); emu.close().then(() => process.exit(0)); };
const stop = () => { emu.close().then(() => process.exit(0)); setTimeout(() => process.exit(0), 500).unref(); };
process.on('SIGINT', stop);
process.on('SIGTERM', stop);
