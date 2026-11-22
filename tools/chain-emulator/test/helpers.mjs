// Shared test helpers: a deterministic input script and the fixture generator.
import { Arena, defaultRoster } from '../src/arena.mjs';
import { rng32 } from '../src/shaping.mjs';
import { PHASE } from '../src/core.mjs';

/** 5-byte InputCmd. */
export function cmd(mx, my, buttons = 0, yaw = 0) {
  return new Uint8Array([mx & 0xff, my & 0xff, buttons, yaw & 0xff, (yaw >> 8) & 0xff]);
}

/** A pseudo-random stick / button command for tick t (seeded). */
export function scriptCmd(rand, t) {
  const a = (t / 40) * Math.PI * 2;
  const b = rand() < 0.05 ? 2 | 1 : rand() < 0.03 ? 8 | 4 : 0;
  return cmd(Math.round(Math.cos(a) * 100), Math.round(Math.sin(a) * 100), b, (t * 37) & 0xffff);
}

/**
 * Drives `arena` like a client: every 3 ticks a batch for T+3..T+5 (T = arena.tick), ~10 % of batches "lost" (never
 * submitted → those ticks are missing). Returns the map tick → cmd of what the arena accepted.
 */
export function clientSubmit(arena, slot, rand, sent) {
  const T = arena.tick;
  if (T % 3 !== 0) return;
  if (rand() < 0.1) return;
  const cmds = [3, 4, 5].map((k) => scriptCmd(rand, T + k));
  const r = arena.submit(slot, T + 3, cmds);
  if (r.status === 0) cmds.forEach((c, k) => sent.set(T + 3 + k, c));
}

export const FIXTURE_SEED = 0x5eed0f1a1f1a1f00n;

/**
 * The fixtures: a seeded 3-player Final (slot 0 human with scripted inputs, slots 1–2 bots). Returns
 * {waiting, rush, ended} WorldAcc byte arrays and the arena (for its layout).
 */
export function makeFixtures() {
  const arena = new Arena({ roster: defaultRoster(3, 1), seed: FIXTURE_SEED, autostartTicks: 12, restartTicks: 1e9, isJoined: () => false });
  const rand = rng32(0xf1);
  const sent = new Map();
  const out = {};
  for (let i = 0; i < 20000 && !out.ended; i++) {
    clientSubmit(arena, 0, rand, sent);
    arena.step();
    if (arena.tick === 6) out.waiting = arena.lastAcc;
    if (!out.rush && arena.view.round.phase === PHASE.RUSH && arena.tick === arena.view.round.rushTick + 300) out.rush = arena.lastAcc;
    if (arena.view.round.phase === PHASE.ENDED) out.ended = arena.lastAcc;
  }
  return { ...out, arena };
}

/** JSON.stringify with BigInt as 0x hex. */
export const toJson = (x) => JSON.stringify(x, (k, v) => (typeof v === 'bigint' ? '0x' + v.toString(16).padStart(16, '0') : v), 2);
