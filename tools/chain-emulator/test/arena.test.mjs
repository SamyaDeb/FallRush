// Arena: determinism vs P2's client core, input rules, a full 3-bot Final faster than real time.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { join } from 'node:path';
import { Arena, defaultRoster, RATE } from '../src/arena.mjs';
import { Core, BIN, loadMap, PHASE } from '../src/core.mjs';
import { Emulator } from '../src/server.mjs';
import { FakeClock, rng32 } from '../src/shaping.mjs';
import { cmd, clientSubmit } from './helpers.mjs';

const SEED = 0x0123456789abcdefn;

/** Runs a 1-human Final with the seeded client script; returns per-tick hashes, accepted inputs, begin-round world. */
function scriptedRun(ticks) {
  let joined = false;
  const a = new Arena({ roster: defaultRoster(3, 1), seed: SEED, autostartTicks: 1e9, isJoined: () => joined });
  const rand = rng32(42);
  const sent = new Map();
  const hashes = [];
  let begin = null;
  a.on('log', (e) => { if (e.ev === 'round_begin') begin = { tick: a.core.clock().tick, world: a.core.worldBytes() }; });
  for (let i = 0; i < ticks; i++) {
    if (i === 30) joined = true; // free play for 30 ticks, then the human "joins" → begin_round
    clientSubmit(a, 0, rand, sent);
    a.step();
    hashes.push(a.core.hash());
  }
  const r = { hashes, sent, begin, rushTick: a.rushTick, phase: a.phase };
  a.close();
  return r;
}

test('determinism: emulator stepping = P2 client core (bin/rush_core.wasm) for the same seed + inputs', () => {
  const N = 900;
  const A = scriptedRun(N);
  const B = scriptedRun(N);
  assert.deepEqual(A.hashes, B.hashes, 'two emulator runs agree');
  assert.ok(A.begin, 'round began');
  assert.equal(A.phase, PHASE.RUSH, 'reached RUSH');
  assert.ok(A.sent.size > 200 && A.sent.size < N, `some inputs missing (${A.sent.size}/${N} stamped)`);

  const ref = new Core(join(BIN, 'rush_core.wasm'));
  assert.ok(!ref.emu, 'reference is the client core (no rcx_*)');
  ref.init(loadMap('CrumbleShelf'), { rate: RATE, active: 7, bots: 6, seed: SEED });
  let checked = 0;
  for (let t = 1; t <= N; t++) {
    if (t === A.begin.tick + 1) {
      // The program's start_final: the client core cannot begin a round, so it takes the World as the chain has it.
      assert.equal(ref.clock().tick, A.begin.tick);
      ref.setWorld(A.begin.world);
    }
    const buf = new Uint8Array(72);
    const c = A.sent.get(t);
    if (c) { buf[0] = 1; buf.set(c, 1); }
    ref.step(buf);
    assert.equal(ref.hash(), A.hashes[t - 1], `hash at tick ${t}`);
    checked++;
  }
  assert.equal(checked, N);
  ref.free();
});

test('input rules: late / window / bad slot, exact-tick use, applied_tick', () => {
  const a = new Arena({ roster: defaultRoster(3, 1), seed: SEED, autostartTicks: 1e9, isJoined: () => false });
  for (let i = 0; i < 10; i++) a.step();
  assert.equal(a.tick, 10);
  assert.equal(a.submit(0, 10, [cmd(1, 0)]).status, 1, 'tick already stepped → late');
  assert.equal(a.submit(0, 19, [cmd(1, 0)]).status, 2, 'tick + 9 → out of window');
  assert.equal(a.submit(1, 12, [cmd(1, 0)]).status, 3, 'bot slot → bad slot');
  const mixed = a.submit(0, 9, [cmd(1, 0), cmd(2, 0), cmd(3, 0)]); // 9, 10 late; 11 ok
  assert.deepEqual([mixed.status, mixed.accepted, mixed.late], [0, 1, 2]);
  assert.equal(a.submit(0, 18, [cmd(4, 0)]).status, 0, 'tick + 8 is in the window');
  assert.deepEqual([a.stats.late, a.stats.window, a.stats.badSlot, a.stats.accepted], [3, 1, 1, 2]);

  a.submit(0, 13, [cmd(50, 0, 0, 7)]);
  a.step(); // 11: stamped
  assert.equal(a.lastInputs[0], 1);
  assert.equal(a.lastInputs[1], 3, "the command stamped 11 (move_x 3)");
  assert.equal(a.applied[0], 11);
  a.step(); // 12: nothing stamped → missing
  assert.equal(a.lastInputs[0], 0);
  assert.equal(a.applied[0], 11);
  a.step(); // 13: exactly the command stamped 13
  assert.deepEqual([...a.lastInputs.subarray(0, 6)], [1, 50, 0, 0, 7, 0]);
  assert.equal(a.applied[0], 13);
  assert.equal(a.lastAcc.length, 72 + a.world.length);
  a.close();
});

test('missing input → rush-core repeats the last command (edges cleared) for 3 ticks, then neutral', () => {
  // Free play (accepts input). Emulator: stamp ticks 1..5 only. Reference: explicit commands every tick.
  const a = new Arena({ roster: defaultRoster(1, 1), seed: SEED, autostartTicks: 1e9, isJoined: () => false });
  const ref = new Core(join(BIN, 'rush_core.wasm'));
  ref.init(loadMap('CrumbleShelf'), { rate: RATE, active: 1, bots: 0, seed: SEED });
  const go = cmd(120, 0, 2 | 1, 0); // jump pressed + held
  a.submit(0, 1, [go, go, go]);
  a.submit(0, 4, [go, go]);
  const pv = (w) => { const p = a.layout.playersOffset; return Buffer.from(w.subarray(p, p + 24)).toString('hex'); };
  for (let t = 1; t <= 14; t++) {
    a.step();
    const exp = t <= 5 ? go : t <= 8 ? cmd(120, 0, 1, 0) : cmd(0, 0, 0, 0); // repeat w/o press edge, then neutral
    const buf = new Uint8Array(72); buf[0] = 1; buf.set(exp, 1);
    ref.step(buf);
    assert.equal(pv(a.world), pv(ref.worldBytes()), `slot 0 pos/vel at tick ${t}`);
    if (t > 5) assert.equal(a.view.players[0].missing, t - 5, 'InputTrack.missing counts up');
  }
  assert.equal(a.applied[0], 5);
  a.close(); ref.free();
});

test('full Final with 3 bots reaches ENDED with a champion (injectable clock, faster than real time)', () => {
  const clock = new FakeClock();
  const emu = new Emulator({ humans: 0, players: 3, shaping: false, once: true, quiet: true, clock, seed: 'a11ce5eed0000003' });
  let done = null;
  emu.onDone = () => { done = emu.arena.result; };
  emu.ticker.start();
  const wall = performance.now();
  for (let s = 0; s < 300 && !done; s++) clock.advance(1000);
  const ms = performance.now() - wall;
  assert.ok(done, 'round over');
  assert.equal(emu.arena.state, 'stopped');
  assert.ok(done.champion >= 0 && done.champion < 3, `champion ${done.champion}`);
  assert.equal(done.placements.length, 3);
  assert.equal(done.placements[0].slot, done.champion);
  const st = emu.arena.status().stepMs;
  console.log(`# 3-bot Final: champion slot ${done.champion}, ENDED at tick ${done.tick} (${done.ticks} RUSH ticks after rush tick ${done.rushTick}, ` +
    `${(done.tick / RATE).toFixed(1)} s sim${done.timerExpired ? ', timer' : ''}); ${ms.toFixed(0)} ms wall; step ${st.avg} ms avg / ${st.max} ms max over ${st.ticks} ticks`);
  emu.ticker.stop();
  emu.arena.close();
});

test('12 players (1 human + 11 bots) step budget', () => {
  const a = new Arena({ roster: defaultRoster(12, 1), seed: SEED, autostartTicks: 0 });
  for (let i = 0; i < 900; i++) a.step();
  const st = a.status().stepMs;
  console.log(`# 12-player Final: ${st.ticks} ticks, step ${st.avg} ms avg / ${st.max} ms max, phase ${a.status().phaseName}`);
  assert.ok(st.avg < 5, 'well under the 33 ms tick');
  a.close();
});
