// node --test --experimental-strip-types tools/fullchain/load.test.ts — the load test's pure parts (no network).
import { test } from "node:test";
import assert from "node:assert/strict";
import { PublicKey, Keypair } from "@solana/web3.js";
import { toP2Batch } from "../../packages/chain/src/p2.ts";
import { buildInstruction } from "../../packages/chain/src/ix.ts";
import { parseConfig } from "../../packages/chain/src/config.ts";
import * as L from "./load-lib.ts";

test("clientConfig points the browser config at the dev arena and parses", () => {
  const c = L.clientConfig({ arena: 7, rawSockets: 1, erHttp: "http://er.test" });
  assert.equal(c.rollup.p2.arenaId, 7);
  assert.equal(c.addresses.arena, undefined, "arena PDA derived from arenaId, not the live arena 2 address");
  assert.equal(c.rollup.rawSockets, 1);
  assert.equal(c.rollup.erHttp, "http://er.test");
  assert.doesNotThrow(() => parseConfig(JSON.stringify(c)));
});

test("encodeBatch → toP2Batch → submit_input data (slot, start, 3 × {mx, my, buttons, yaw>>8})", () => {
  const cmds = [{ mx: 10, my: -20, buttons: 3, yaw: 0x1234 }, { mx: 300, my: -300, buttons: 1, yaw: 0xabff }, { mx: 0, my: 127, buttons: 0, yaw: 0xff00 }];
  const b = L.encodeBatch(9, 777, cmds);
  assert.equal(b.length, 6 + 15);
  const p = toP2Batch(b);
  assert.equal(p.slot, 9);
  assert.equal(p.start, 777);
  assert.deepEqual([...p.batch12], [10, 236, 3, 0x12, 127, 129, 1, 0xab, 0, 127, 0, 0xff]);
  const c = L.clientConfig({ arena: 7, rawSockets: 2 });
  const k = Keypair.generate().publicKey;
  const ix = buildInstruction(c.rollup.submitInput, { ...c.addresses, arena: k, world: k, player: k, sessionToken: k, slot: p.slot, start: p.start, batch12: p.batch12 });
  assert.equal(ix.data.length, 8 + 1 + 4 + 12);
  assert.equal(ix.data[8], 9);
  assert.equal(Buffer.from(ix.data).readUInt32LE(9), 777);
  assert.throws(() => L.encodeBatch(0, 0, []));
});

test("mover is deterministic per seed and stays in range", () => {
  const a = L.mover(5), b = L.mover(5), c = L.mover(6);
  const xa = Array.from({ length: 300 }, a), xb = Array.from({ length: 300 }, b), xc = Array.from({ length: 300 }, c);
  assert.deepEqual(xa, xb);
  assert.notDeepEqual(xa, xc);
  for (const m of xa) { assert.ok(Math.abs(m.mx) <= 127 && Math.abs(m.my) <= 127); assert.ok(m.yaw >= 0 && m.yaw <= 0xffff); }
  assert.ok(xa.some((m) => m.buttons & L.JUMP_PRESSED), "jumps sometimes");
});

test("nextStart: lead ahead, never re-sends a tick, holds outside the program window", () => {
  assert.equal(L.nextStart(-1, 0, 4), null);
  assert.equal(L.nextStart(100, 0, 4), 104);
  assert.equal(L.nextStart(100, 107, 4), 107); // the cursor wins
  assert.equal(L.nextStart(100, 108, 4), 108); // = tick + window: still accepted
  assert.equal(L.nextStart(100, 109, 4), null); // the World is slow: hold
  // a 10 Hz loop at 30 Hz never leaves gaps: 3 ticks per step
  let cur = 0, sent: number[] = [];
  for (let step = 0; step < 50; step++) { const t = step * 3; const s = L.nextStart(t, cur, 4); if (s !== null) { for (let k = 0; k < 3; k++) sent.push(s + k); cur = s + 3; } }
  for (let i = 1; i < sent.length; i++) assert.equal(sent[i], sent[i - 1] + 1);
});

test("nextStart + TickClock: aims at the estimated tick now, checks the window against the newest tick seen", () => {
  // the estimate may run ahead, the program window is relative to the World at landing (≥ the seen tick)
  assert.equal(L.nextStart(103, 0, 5, 8, 100), 108);
  assert.equal(L.nextStart(104, 0, 5, 8, 100), null, "an estimate ahead of a slow World must not leave the window");
  assert.equal(L.nextStart(103, 0, 5, 8, -1), null);
  const k = new L.TickClock(3);
  assert.equal(k.est(0), -1);
  k.observe(100, 1000);
  assert.equal(k.est(1000), 100);
  assert.equal(k.est(1050), 101); // 30 Hz: 1.5 ticks → 1
  assert.equal(k.est(1100), 103);
  assert.equal(k.est(5000), 103, "capped: a stalled World does not run the estimate away");
  k.observe(99, 1200); // an older notification: ignored
  assert.equal(k.seen, 100);
  k.observe(106, 1200);
  assert.equal(k.est(1200), 106);
  k.observe(4, 1300); // the next match's fresh World: restart, not stuck at the old tick
  assert.equal(k.seen, 4);
  // a stale observer (seen 1 tick behind each step) still leaves no gaps and never re-sends a tick
  const c = new L.TickClock(3);
  let cur = 0; const sent: number[] = [];
  for (let step = 0; step < 60; step++) {
    const now = step * 100;
    c.observe(Math.max(0, step * 3 - 2), now);
    const s = L.nextStart(c.est(now), cur, 5, 8, c.seen);
    if (s !== null) { for (let q = 0; q < 3; q++) sent.push(s + q); cur = s + 3; }
  }
  assert.ok(sent.length > 150);
  for (let i = 1; i < sent.length; i++) assert.equal(sent[i], sent[i - 1] + 1);
});

test("chooseSlots: top down, skips reserved and taken; rosterHumans reads the server header", () => {
  assert.deepEqual(L.chooseSlots(11, []), [11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1]);
  assert.deepEqual(L.chooseSlots(3, [11, 9], 1 << 10), [8, 7, 6]);
  assert.deepEqual(L.chooseSlots(12, [0]).length, 11);
  const jsonl = `{"type":"match","matchId":"AB12","roster":[{"slot":0,"bot":false},{"slot":1,"bot":true},{"slot":4,"bot":false}]}\n{"type":"event","seq":1}\n`;
  assert.deepEqual(L.rosterHumans(jsonl), [0, 4]);
  assert.deepEqual(L.rosterHumans(jsonl, "ab12"), [0, 4]);
  assert.equal(L.rosterHumans(jsonl, "ffff"), null);
  assert.equal(L.popcount(0b1000_0000_0101), 3);
});

test("decodeWorldSample reads the WorldAcc header offsets", () => {
  const d = Buffer.alloc(208 + 16);
  d.writeBigUInt64LE(5000n, 40); d.writeBigUInt64LE(321n, 48); d.writeBigUInt64LE(9999n, 56); d.writeBigUInt64LE(6000n, 64);
  for (let s = 0; s < 12; s++) d.writeUInt32LE(100 + s, 80 + 4 * s);
  d.writeUInt32LE(321, 128); d[138] = 4; d[139] = 2; d[198] = 11; d[199] = 3; d.writeBigUInt64LE(4000n, 200);
  const w = L.decodeWorldSample(new Uint8Array(d), 42);
  assert.deepEqual({ t: w.t, lastSlot: w.lastSlot, ticksRun: w.ticksRun, exec: w.exec, notDue: w.notDue, tick: w.tick, phase: w.phase, round: w.round, rphase: w.rphase, startSlot: w.startSlot }, { t: 42, lastSlot: 5000, ticksRun: 321, exec: 9999, notDue: 6000, tick: 321, phase: 4, round: 2, rphase: 3, startSlot: 4000 });
  assert.equal(w.applied[11], 111);
  assert.equal(w.inputLead, 11);
  d[198] = 0; assert.equal(L.decodeWorldSample(new Uint8Array(d), 0).inputLead, L.INPUT_WINDOW, "0 = the default window");
});

function synth(rate: (r: number) => number, roundS = 20): L.WorldSample[] {
  // 100 ms samples; the ER slot clock 50 ms; World ticks at `rate(round)` × 30 Hz
  const out: L.WorldSample[] = [];
  let tick = 0, exec = 0, nd = 0;
  for (let r = 1; r <= 4; r++) for (let i = 0; i < roundS * 10; i++) {
    const t = out.length * 100;
    tick += 3 * rate(r); exec += r === 1 ? 15 : 10; nd += r === 1 ? 5 : 7;
    out.push({ t, tick: Math.round(tick), round: r, rphase: i < 30 ? 2 : L.RUSH, phase: 3, exec, notDue: nd, ticksRun: Math.round(tick), lastSlot: 1000 + out.length * 2, startSlot: 1000, applied: Array(12).fill(i % 10 === 0 ? Math.round(tick) - 2 : Math.round(tick)) });
  }
  out.push({ ...out[out.length - 1], t: out.length * 100, phase: L.DONE_PHASE });
  return out;
}

test("roundStats: real time, executions/s, lag and applied ratio per round", () => {
  const rs = L.roundStats(synth((r) => (r === 1 ? 0.9 : 1)), [10, 11]);
  assert.equal(rs.length, 4);
  assert.ok(Math.abs(rs[0].realTime! - 0.9) < 0.01, `R1 ${rs[0].realTime}`);
  for (const r of rs.slice(1)) assert.ok(Math.abs(r.realTime! - 1) < 0.01);
  assert.ok(Math.abs(rs[0].execPerS! - 150) < 2);
  assert.ok(Math.abs(rs[1].execPerS! - 100) < 2);
  assert.ok(Math.abs(rs[1].notDueShare! - 0.7) < 0.01);
  assert.ok(rs[0].lagMaxTicks! > 50, "R1 at 0.9x falls behind the slot clock"); // 20 s × 3 ticks/s behind
  assert.ok(Math.abs(rs[3].lagMaxTicks! - rs[0].lagMaxTicks!) < 5, "R2–R4 at 1.0x keep the R1 deficit, no new lag");
  assert.ok(rs[1].appliedRatio! > 0.85 && rs[1].appliedRatio! < 0.95); // 1 of 10 samples late
});

test("verdict: plan #10 pass criteria", () => {
  const ok = L.roundStats(synth(() => 1));
  assert.equal(L.verdict(ok, 250, 12, L.DEFAULT_CRITERIA).pass, true);
  assert.equal(L.verdict(ok, 450, 12, L.DEFAULT_CRITERIA).pass, false);
  assert.equal(L.verdict(ok, 250, 11, L.DEFAULT_CRITERIA).joined, false);
  const slow = L.roundStats(synth((r) => (r === 3 ? 0.98 : 1)));
  assert.equal(L.verdict(slow, 250, 12, L.DEFAULT_CRITERIA).realTime, false);
  assert.equal(L.verdict(ok.slice(0, 3), 250, 12, L.DEFAULT_CRITERIA).realTime, false, "a missing round fails");
  assert.equal(L.verdict(slow, 250, 12, L.DEFAULT_CRITERIA, true).inconclusive, true, "a FAIL with a saturated tool is inconclusive");
  assert.equal(L.verdict(ok, 250, 12, L.DEFAULT_CRITERIA, true).inconclusive, false);
});

test("compareRuns: CRANK_GATE=1 must stay ≥ 0.99x per round; reports executions saved in R2–R4", () => {
  const a = { label: "gate0", rounds: L.roundStats(synth(() => 1)) };
  const gated = synth(() => 1).map((s) => ({ ...s, exec: s.round === 1 ? s.exec : Math.round(s.exec * 0.55) }));
  const b = { label: "gate1", rounds: L.roundStats(gated) };
  const c = L.compareRuns(a, b);
  assert.equal(c.gateOk, true);
  assert.ok(c.meanSavedR2R4! > 30, `saved ${c.meanSavedR2R4}`);
  assert.equal(c.gateEffective, true);
  assert.equal(c.ship, true);
  const bad = { label: "gate1", rounds: L.roundStats(synth((r) => (r === 2 ? 0.95 : 1))) };
  assert.equal(L.compareRuns(a, bad).gateOk, false);
  assert.equal(L.compareRuns(a, bad).ship, false);
  // a keeper that ignores CRANK_GATE: B = A, real time fine, nothing saved → not a ship signal
  const same = L.compareRuns({ ...a, crankGate: "0" }, { label: "gate1", crankGate: "1", rounds: L.roundStats(synth(() => 1)) });
  assert.equal(same.gateOk, true);
  assert.equal(same.gateEffective, false);
  assert.equal(same.ship, false);
  // swapped arguments
  assert.equal(L.compareRuns({ ...b, crankGate: "1" }, { ...a, crankGate: "0" }).order, false);
});

test("confirmSummary + pct", () => {
  assert.equal(L.pct([5, 1, 3, 2, 4], 50), 3);
  assert.equal(L.pct([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 95), 10);
  assert.ok(Number.isNaN(L.pct([], 50)));
  const s = L.confirmSummary([100, 200, 300, 400], { sent: 5, ok: 4, err: 1, timeout: 0, window: 1 });
  assert.equal(s.p50, 200);
  assert.equal(s.okRatio, 0.8);
  void PublicKey;
});
