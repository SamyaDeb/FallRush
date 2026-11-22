// arena.mjs — the fake ER arena: one rush-core World stepped at 30 Hz, the input buffers the program's
// `submit_input` would write, the round lifecycle the program's `start_final` / director would run, and the WorldAcc
// bytes every account notification carries. Transport-free: the server (server.mjs) or a test drives it.
//
// Lifecycle: rc_init (every roster slot active, bots flagged) → WAIT (free play) until every human slot has joined
// or `autostartTicks` passed → rcx_begin_round with the director's Final parameters → step until the round phase is
// ENDED → `restartTicks` later a fresh arena (seq + 1), unless `once`.
import { EventEmitter } from 'node:events';
import { Core, loadMap, MAX_PLAYERS, MODE, PHASE, PHASE_NAME } from './core.mjs';
import { decodeWorld, encodeWorldAcc, ACC_FLAG_LIVE, ACC_FLAG_OVER } from './world-view.mjs';

export const RATE = 30;
export const INPUT_WINDOW = 8;
export const CMD_BYTES = 5;

/** Map modes by key (the blob's META mode; checked against the maps in bin/maps). */
export const MAP_MODE = { CrumbleShelf: MODE.FINAL, Lobby: MODE.LOBBY, Sandbox: MODE.SANDBOX, BlockPartyDash: MODE.RACE, GooTower: MODE.SURVIVAL, StarScramble: MODE.SKILL };

export function splitmix64(x) {
  let z = BigInt.asUintN(64, BigInt(x) + 0x9e3779b97f4a7c15n);
  z = BigInt.asUintN(64, (z ^ (z >> 30n)) * 0xbf58476d1ce4e5b9n);
  z = BigInt.asUintN(64, (z ^ (z >> 27n)) * 0x94d049bb133111ebn);
  return z ^ (z >> 31n);
}

export function parseSeed(s) {
  if (typeof s === 'bigint') return BigInt.asUintN(64, s);
  if (!/^[0-9a-fA-F]{1,16}$/.test(String(s))) throw new Error(`seed must be 1..16 hex digits, got ${s}`);
  return BigInt('0x' + s);
}
export const seedHex = (s) => BigInt.asUintN(64, s).toString(16).padStart(16, '0');

/** Default roster: `players` slots, the lowest `humans` human, the rest bots. */
export function defaultRoster(players, humans) {
  const r = [];
  for (let s = 0; s < players; s++) r.push({ slot: s, kind: s < humans ? 'human' : 'bot' });
  return r;
}

/**
 * Director's Final parameters (rules/director.rs `begin_round`): target = round_targets(P)[3] = 1, clamped to
 * [1, players]; participants = every alive roster slot; intro / countdown = ROUND_INTRO_30 / ROUND_COUNTDOWN_30.
 * Other modes (not a Final, emulator convenience): target 0 = the mode default.
 */
export function roundTarget(mode, players) {
  return mode === MODE.FINAL ? Math.max(1, Math.min(1, players)) : 0;
}

export class Arena extends EventEmitter {
  /**
   * opts: map ('CrumbleShelf'), roster ([{slot, kind, name?}]), seed (BigInt), autostartTicks (600), restartTicks
   * (240), once (false), wasmPath (bin/rush_emu_core.wasm), isJoined (slot → bool: a human holds this slot).
   */
  constructor(opts = {}) {
    super();
    this.opts = { map: 'CrumbleShelf', roster: defaultRoster(3, 1), seed: 1n, autostartTicks: 20 * RATE, restartTicks: 8 * RATE, once: false, ...opts };
    this.core = new Core(this.opts.wasmPath);
    this.layout = this.core.layout();
    this.timing = this.core.roundTiming();
    this.isJoined = this.opts.isJoined || (() => false);
    this.seq = 0;
    this.notifSeq = 0;
    this.stats = { accepted: 0, late: 0, window: 0, badSlot: 0, ticks: 0, stepMsTotal: 0, stepMsMax: 0, batches: 0 };
    this.results = [];
    this.lastAcc = null;
    this.start(this.opts);
  }

  /** (Re)starts the arena: a new World, seq + 1. spec: {map, roster, seed, final?, match?}. */
  start(spec = {}) {
    const s = { ...this.opts, ...spec };
    this.opts = s;
    this.mapKey = s.map;
    this.mode = MAP_MODE[s.map] ?? MODE.FINAL;
    this.roster = s.roster.map((r) => ({ slot: r.slot, kind: r.kind === 'human' ? 'human' : 'bot', name: r.name }));
    this.active = 0;
    this.bots = 0;
    for (const r of this.roster) {
      if (r.slot < 0 || r.slot >= MAX_PLAYERS) throw new Error(`bad roster slot ${r.slot}`);
      this.active |= 1 << r.slot;
      if (r.kind === 'bot') this.bots |= 1 << r.slot;
    }
    this.humanSlots = this.roster.filter((r) => r.kind === 'human').map((r) => r.slot);
    this.seed = BigInt.asUintN(64, s.seed);
    this.match = s.match ?? null;
    this.map = loadMap(this.mapKey);
    this.core.init(this.map, { rate: RATE, active: this.active, bots: this.bots, seed: this.seed });
    this.seq++;
    this.state = 'waiting';       // waiting → round → ended → (restart | stopped)
    this.stateTicks = 0;
    this.rushTick = 0;
    this.result = null;
    this.inputs = Array.from({ length: MAX_PLAYERS }, () => new Map());
    this.applied = new Array(MAX_PLAYERS).fill(0);
    this.tick = 0;
    this.world = this.core.worldBytes();
    this.view = decodeWorld(new DataView(this.world.buffer), 0, this.layout);
    this.emit('arena', this.info());
    this._log('arena_start', { arena: this.arenaId, map: this.mapKey, seed: seedHex(this.seed), roster: this.roster, match: this.match });
    this.publish();
  }

  get arenaId() { return `emu:${this.seq}`; }
  get phase() { return this.view.round.phase; }
  get roundsCapable() { return this.mode !== MODE.LOBBY && this.mode !== MODE.SANDBOX; }

  info() {
    return { arena: this.arenaId, seq: this.seq, map: this.mapKey, mode: this.mode, rate: RATE, coreVersion: this.core.version, seed: seedHex(this.seed), match: this.match, roster: this.roster };
  }

  _log(ev, data) { this.emit('log', { ev, tick: this.tick, ...data }); }

  /**
   * `submit_input`: stores the commands of a batch whose ticks are > World.tick and ≤ World.tick + 8.
   * cmds: array of 5-byte Uint8Arrays (InputCmd::to_bytes). Returns {status, chainTick, accepted, late, window}.
   * status: 0 ok (≥ 1 command stored), 1 late (every command was for a stepped tick), 2 out of window, 3 bad slot.
   */
  submit(slot, firstTick, cmds) {
    this.stats.batches++;
    const chainTick = this.tick;
    if (!this.humanSlots.includes(slot)) { this.stats.badSlot++; return { status: 3, chainTick, accepted: 0, late: 0, window: 0 }; }
    let accepted = 0, late = 0, window = 0;
    for (let k = 0; k < cmds.length; k++) {
      const t = (firstTick + k) >>> 0;
      if (t <= this.tick) { late++; continue; }
      if (t > this.tick + INPUT_WINDOW) { window++; continue; }
      this.inputs[slot].set(t, cmds[k]);
      accepted++;
    }
    this.stats.accepted += accepted; this.stats.late += late; this.stats.window += window;
    const status = accepted > 0 ? 0 : late > 0 ? 1 : 2;
    return { status, chainTick, accepted, late, window };
  }

  /** begin_round with the director's parameters (the program's `start_final`). */
  beginRound() {
    const players = this.roster.length;
    const target = roundTarget(this.mode, players);
    this.rushTick = this.core.beginRound(target, this.active, this.timing.intro30, this.timing.countdown30);
    this.state = 'round';
    this.stateTicks = 0;
    this._log('round_begin', { arena: this.arenaId, target, participants: this.active, rushTick: this.rushTick, intro30: this.timing.intro30, countdown30: this.timing.countdown30 });
  }

  /** One 30 Hz tick: lifecycle, inputs, step, publish. Returns the stepped tick. */
  step() {
    // Lifecycle before the step (the program's start_final runs between ticks).
    if (this.state === 'waiting' && this.roundsCapable) {
      const allIn = this.humanSlots.every((s) => this.isJoined(s));
      if (allIn || this.stateTicks >= this.opts.autostartTicks) this.beginRound();
    } else if (this.state === 'ended' && this.stateTicks >= this.opts.restartTicks) {
      if (this.opts.once) { this.state = 'stopped'; this.emit('done', this.result); return this.tick; }
      this.start({ seed: splitmix64(this.seed), match: null });
      return this.tick;
    }
    if (this.state === 'stopped') return this.tick;

    const t = this.tick + 1;
    const buf = new Uint8Array(MAX_PLAYERS * 6);
    for (const s of this.humanSlots) {
      const m = this.inputs[s];
      const cmd = m.get(t);
      if (cmd) {
        buf[s * 6] = 1;
        buf.set(cmd.subarray(0, CMD_BYTES), s * 6 + 1);
        this.applied[s] = t;
      }
      for (const k of m.keys()) if (k <= t) m.delete(k);
    }
    this.lastInputs = buf;
    const t0 = performance.now();
    this.core.step(buf);
    this.world = this.core.worldBytes();
    const ms = performance.now() - t0;
    this.stats.ticks++; this.stats.stepMsTotal += ms; if (ms > this.stats.stepMsMax) this.stats.stepMsMax = ms;
    this.view = decodeWorld(new DataView(this.world.buffer), 0, this.layout);
    this.tick = this.view.tick;
    this.stateTicks++;

    if (this.state === 'round' && this.view.round.phase === PHASE.ENDED) {
      const r = this.view.round.result;
      const order = r.order.slice(0, r.n);
      this.result = {
        arena: this.arenaId, match: this.match, seed: seedHex(this.seed), tick: this.tick,
        rushTick: this.view.round.rushTick, ticks: this.tick - this.view.round.rushTick, timerExpired: r.timerExpired,
        champion: r.n > 0 ? order[0] : 255,
        placements: order.map((slot, k) => ({ place: k + 1, slot, kind: this.roster.find((x) => x.slot === slot)?.kind, qualified: ((r.qualified >> k) & 1) === 1, reason: r.reasons[k], value: r.values[k] })),
      };
      this.results.push(this.result);
      if (this.results.length > 20) this.results.shift();
      this.state = 'ended';
      this.stateTicks = 0;
      this._log('round_end', this.result);
      this.emit('result', this.result);
    }
    this.publish();
    return this.tick;
  }

  flags() {
    return (this.state === 'round' || this.state === 'ended' || this.state === 'stopped' ? ACC_FLAG_LIVE : 0) |
      (this.state === 'ended' || this.state === 'stopped' ? ACC_FLAG_OVER : 0);
  }

  /** Builds the WorldAcc for the current World and emits 'publish' (notif_seq, bytes). */
  publish() {
    this.notifSeq = (this.notifSeq + 1) >>> 0;
    this.lastAcc = encodeWorldAcc({ coreVersion: this.core.version, tick: this.tick, applied: this.applied, arenaSeq: this.seq, flags: this.flags() }, this.world);
    this.emit('publish', this.notifSeq, this.lastAcc);
  }

  status() {
    const v = this.view;
    return {
      ...this.info(),
      state: this.state, phase: v.round.phase, phaseName: PHASE_NAME[v.round.phase], tick: this.tick,
      rushTick: v.round.rushTick, rushEnd: v.round.rushEnd, notifSeq: this.notifSeq,
      players: this.roster.map((r) => {
        const p = v.players[r.slot];
        return { slot: r.slot, kind: r.kind, name: r.name, joined: r.kind === 'human' ? this.isJoined(r.slot) : undefined, applied: this.applied[r.slot], pos: p.pos, rules: p.rules.flags, out: (p.rules.flags & 2) !== 0 };
      }),
      inputs: { batches: this.stats.batches, accepted: this.stats.accepted, late: this.stats.late, window: this.stats.window, badSlot: this.stats.badSlot },
      stepMs: { avg: this.stats.ticks ? +(this.stats.stepMsTotal / this.stats.ticks).toFixed(4) : 0, max: +this.stats.stepMsMax.toFixed(3), ticks: this.stats.ticks },
      result: this.result,
    };
  }

  close() { this.core.free(); this.state = 'stopped'; }
}

/**
 * Drift-free real-time driver: tick k is due at t0 + k × 1000/30 ms; at most `maxCatchUp` ticks per wake-up, after
 * that the schedule is re-based (ticks are skipped, not bunched).
 */
export class Ticker {
  constructor(onTick, { rate = RATE, maxCatchUp = 5, clock } = {}) {
    this.onTick = onTick;
    this.period = 1000 / rate;
    this.maxCatchUp = maxCatchUp;
    this.clock = clock;
    this.timer = null;
    this.skipped = 0;
  }
  start() {
    this.stopped = false;
    this.t0 = this.clock.now();
    this.k = 0;
    this._arm();
  }
  _arm() {
    const due = this.t0 + (this.k + 1) * this.period;
    this.timer = this.clock.setTimeout(() => this._wake(), Math.max(0, due - this.clock.now()));
  }
  _wake() {
    this.timer = null;
    const now = this.clock.now();
    let n = 0;
    while (this.t0 + (this.k + 1) * this.period <= now + 0.25 && n < this.maxCatchUp) {
      this.k++; n++;
      this.onTick();
      if (this.stopped) return;
    }
    const behind = Math.floor((now - this.t0) / this.period) - this.k;
    if (behind > 0) { this.skipped += behind; this.k += behind; }
    this._arm();
  }
  stop() { this.stopped = true; if (this.timer) this.clock.clearTimeout(this.timer); this.timer = null; }
}
