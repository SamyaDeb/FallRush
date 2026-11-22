// core.mjs — thin wrapper over a rush-core C-ABI WASM module (rush_emu_core.wasm or P2's rush_core.wasm).
// No wasm-bindgen: instantiate with {} imports, copy bytes in/out of linear memory through rc_alloc buffers.
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

export const BIN = join(dirname(fileURLToPath(import.meta.url)), '..', 'bin');
export const MAX_PLAYERS = 12;
export const PHASE = { WAITING: 0, INTRO: 1, COUNTDOWN: 2, RUSH: 3, ENDED: 4, SANDBOX: 5 };
export const PHASE_NAME = ['WAITING', 'INTRO', 'COUNTDOWN', 'RUSH', 'ENDED', 'SANDBOX'];
export const MODE = { SANDBOX: 0, RACE: 1, SURVIVAL: 2, SKILL: 3, FINAL: 4, LOBBY: 5 };

const modCache = new Map();
function module(path) {
  if (!modCache.has(path)) modCache.set(path, new WebAssembly.Module(readFileSync(path)));
  return modCache.get(path);
}

export function loadMap(key) {
  return readFileSync(join(BIN, 'maps', `${key}.mapblob`));
}

/** One WASM instance (its own linear memory and handle table). */
export class Core {
  constructor(wasmPath = join(BIN, 'rush_emu_core.wasm')) {
    this.x = new WebAssembly.Instance(module(wasmPath), {}).exports;
    const x = this.x;
    this.emu = typeof x.rcx_step === 'function';
    this.worldCap = x.rc_world_size() + 512;
    this.pIn = x.rc_alloc(MAX_PLAYERS * 6);
    this.pWorld = x.rc_alloc(this.worldCap);
    this.pCfg = x.rc_alloc(24);
    this.pPlayer = x.rc_alloc(36);
    this.pEvents = x.rc_alloc(64 * 16);
    this.version = x.rc_version();
    this.h = -1;
  }
  get mem() { return new Uint8Array(this.x.memory.buffer); }

  /** rcx_layout → named numbers (emulator core only). */
  layout() {
    const p = this.x.rc_alloc(64);
    this.x.rcx_layout(p);
    const v = [...new Uint32Array(this.x.memory.buffer.slice(p, p + 64))];
    this.x.rc_dealloc(p, 64);
    return {
      headerLen: v[0], playerStride: v[1], playersOffset: v[2], reactiveLen: v[3], dropQueueLen: v[4],
      roundOffset: v[5], roundLen: v[6], directorOffset: v[7], directorLen: v[8], totalLen: v[9],
      slotRulesOffset: v[10], botOffset: v[11], roundResultOffset: v[12],
    };
  }

  roundTiming() {
    const t = this.x.rcx_round_timing();
    return { intro30: t & 0xffff, countdown30: t >>> 16 };
  }

  /** rc_init. cfg: {rate, active, bots, seed (BigInt)}. Frees a previous world of this instance. */
  init(mapBytes, { rate = 30, active, bots, seed = 0n }) {
    const x = this.x;
    if (this.h >= 0) this.free();
    const mp = x.rc_alloc(mapBytes.length);
    this.mem.set(mapBytes, mp);
    const dv = new DataView(x.memory.buffer, this.pCfg, 24);
    dv.setUint32(0, rate, true);
    dv.setUint32(4, active >>> 0, true);
    dv.setUint32(8, bots >>> 0, true);
    dv.setUint32(12, 0, true);
    dv.setBigUint64(16, BigInt.asUintN(64, BigInt(seed)), true);
    this.h = x.rc_init(mp, mapBytes.length, this.pCfg);
    x.rc_dealloc(mp, mapBytes.length);
    if (this.h < 0) throw new Error('rc_init failed');
    return this.h;
  }

  free() { if (this.h >= 0) this.x.rc_free(this.h); this.h = -1; }

  /** inputs: Uint8Array(72) = 12 × {present, move_x, move_y, buttons, cam_yaw LE}. Returns the event count. */
  step(inputs) {
    this.mem.set(inputs, this.pIn);
    return this.emu ? this.x.rcx_step(this.h, this.pIn) : this.x.rc_step(this.h, this.pIn);
  }

  beginRound(target, participants, intro30, countdown30) {
    return this.x.rcx_begin_round(this.h, target, participants, intro30, countdown30);
  }

  /** The last rcx_step / rcx_begin_round events. */
  events() {
    const n = this.x.rcx_events(this.pEvents, 64 * 16);
    const dv = new DataView(this.x.memory.buffer, this.pEvents, n * 16);
    const out = [];
    for (let i = 0; i < n; i++) {
      const o = i * 16;
      out.push({ kind: dv.getUint8(o), tag: dv.getUint8(o + 1), slot: dv.getUint8(o + 2), round: dv.getUint8(o + 3),
        arg: dv.getInt32(o + 4, true), tick: dv.getUint32(o + 8, true), seq: dv.getUint32(o + 12, true) });
    }
    return out;
  }

  hash() { return BigInt.asUintN(64, this.x.rc_hash(this.h)); }

  /** Canonical World bytes (a copy). */
  worldBytes() {
    const n = this.x.rc_world_bytes(this.h, this.pWorld, this.worldCap);
    return this.mem.slice(this.pWorld, this.pWorld + n);
  }

  setWorld(bytes) {
    this.mem.set(bytes, this.pWorld);
    if (this.x.rc_set_world(this.h, this.pWorld, bytes.length) !== 0) throw new Error('rc_set_world failed');
  }

  clock() { const c = BigInt.asUintN(64, this.x.rc_clock(this.h)); return { tick: Number(c & 0xffffffffn), rate: Number(c >> 32n) }; }
}
