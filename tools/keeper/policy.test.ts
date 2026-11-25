// harden: unit tests for the keeper's pure decisions (no RPC, no keys).
//   node --experimental-strip-types --no-warnings --test tools/keeper/policy.test.ts
import { test } from "node:test";
import assert from "node:assert/strict";
import * as P from "./policy.ts";

// ------------------------------------------------------------------ #1 balances + treasury
test("owedCount: crown + unminted seeded human capsules", () => {
  assert.equal(P.owedCount(null), 0);
  assert.equal(P.owedCount({ flags: 1, champion: 3, humanMask: 0b1001, mintedMask: 0 }), 1); // crown only (not seeded)
  assert.equal(P.owedCount({ flags: 1 | 2, champion: 3, humanMask: 0b1001, mintedMask: 0b0001 }), 2); // crown + slot 3
  assert.equal(P.owedCount({ flags: 1 | 2 | 4, champion: 3, humanMask: 0b1001, mintedMask: 0b1001 }), 0); // crown minted
  assert.equal(P.owedCount({ flags: 1 | 16, champion: 12, humanMask: 0, mintedMask: 0 }), 0); // no crown
});

test("treasuryTopUp: a low treasury before finalize gets topped up (mocked low treasury)", () => {
  // the production state of 2026-11-25 (36.8M) is enough for 1 owed asset (need 20M) …
  assert.equal(P.treasuryTopUp(36_806_480, 1), 0);
  // … but not for a 9-human judge match (9 capsules + crown = 10 × 4.2M = 42M)
  assert.equal(P.treasuryTopUp(36_806_480, 10), 60_000_000 - 36_806_480);
  // below the 20M floor: top up to the target
  assert.equal(P.treasuryTopUp(5_000_000, 0), 55_000_000);
  // a need above the target: top up to the need
  assert.equal(P.treasuryTopUp(10_000_000, 20, 60_000_000), 84_000_000 - 10_000_000);
  // unreadable balance: never guess
  assert.equal(P.treasuryTopUp(-1, 5), 0);
  assert.equal(P.treasuryTopUp(NaN, 5), 0);
});

test("lowBalance flags at 0.3 / 0.1 / 0.2 SOL; unknown (-1 / null) is never low", () => {
  const ok = P.lowBalance({ authorityLamports: 1_006_505_712, treasuryLamports: 1_000_000_000, funderLamports: 753_710_000 });
  assert.deepEqual(ok, { authority: false, treasury: false, funder: false, any: false });
  const low = P.lowBalance({ authorityLamports: 290_000_000, treasuryLamports: 36_806_480, funderLamports: 150_000_000 });
  assert.deepEqual(low, { authority: true, treasury: true, funder: true, any: true });
  assert.equal(P.lowBalance({ authorityLamports: -1, treasuryLamports: null, funderLamports: null }).any, false);
});

// ------------------------------------------------------------------ #2 commit cap
test("delegationNonce reads the u64 at offset 8; the cap is 10", () => {
  const d = Buffer.alloc(64); d.writeBigUInt64LE(10n, 8);
  assert.equal(P.delegationNonce(d), 10);
  assert.equal(P.delegationNonce(null), null);
  assert.equal(P.delegationNonce(Buffer.alloc(8)), null);
  assert.equal(P.commitCapped([0, 3, 9, null]), false);
  assert.equal(P.commitCapped([10, 10, 10, 10, 10, 10]), true);
});

// ------------------------------------------------------------------ #7 market maker
const view = (o: Partial<P.MmView>): P.MmView => ({ status: "roundResolved", currentRound: 2, flags: 0x20, alive: [3, 7], ...o });
test("mmSeeds: no q3 seed when only one slot is alive (crowned before round 3)", () => {
  assert.deepEqual(P.mmSeeds(view({ alive: [3] }), { judge: false, full: true, humanSlots: [3] }), []);
});
test("mmSeeds: q2 / q3 with ≥ 2 alive and a human alive; none on a bot-only remainder", () => {
  assert.deepEqual(P.mmSeeds(view({ currentRound: 1 }), { judge: false, full: true, humanSlots: [3] }), [{ tag: "q2", k: 3 }]);
  assert.deepEqual(P.mmSeeds(view({}), { judge: false, full: true, humanSlots: [3] }), [{ tag: "q3", k: 4 }]);
  assert.deepEqual(P.mmSeeds(view({}), { judge: false, full: true, humanSlots: [0] }), []); // human 0 eliminated
  assert.deepEqual(P.mmSeeds(view({}), { judge: false, full: true, humanSlots: null }), [{ tag: "q3", k: 4 }]); // unknown roster
  assert.deepEqual(P.mmSeeds(view({}), { judge: true, full: true, humanSlots: [3] }), []); // judge: no qualify pools
});
test("mmSeeds: champion + q1 at the start (judge: champion only)", () => {
  const s = view({ status: "dealt", currentRound: 0, alive: [0, 1, 2] });
  assert.deepEqual(P.mmSeeds(s, { judge: false, full: true, humanSlots: [0] }), [{ tag: "champion", k: 0 }, { tag: "q1", k: 2 }]);
  assert.deepEqual(P.mmSeeds(s, { judge: true, full: true, humanSlots: [0] }), [{ tag: "champion", k: 0 }]);
});
test("mmSeeds: FinalWinner in full mode (no 0x40 flag) at R4 before RUSH; hybrid keeps the 0x40 gate", () => {
  const r4 = view({ status: "roundActive", currentRound: 4, flags: 0x20, alive: [3, 7, 9] });
  assert.deepEqual(P.mmSeeds(r4, { judge: false, full: true, humanSlots: [3], worldRound: 4, worldPhase: 1 }), [{ tag: "final", k: 1 }]);
  assert.deepEqual(P.mmSeeds(r4, { judge: false, full: true, humanSlots: [3], worldRound: 4, worldPhase: 3 }), []); // RUSH started
  assert.deepEqual(P.mmSeeds(r4, { judge: false, full: false, humanSlots: [3], worldRound: 4, worldPhase: 1 }), []); // hybrid, no 0x40
  assert.deepEqual(P.mmSeeds({ ...r4, flags: 0x60 }, { judge: false, full: false, humanSlots: [3], worldRound: 4, worldPhase: 1 }), [{ tag: "final", k: 1 }]);
});

// ------------------------------------------------------------------ #6 orphan
test("orphanDetector fires only after 10 s of a fresh status that no longer shows this match", () => {
  const o = P.orphanDetector("aa", 10_000);
  const t = 1_000_000;
  assert.equal(o({ t, inMatch: true, match: "aa" }, t), false);
  assert.equal(o({ t, inMatch: false, match: "" }, t), false); // starts the clock
  assert.equal(o({ t: t + 9_000, inMatch: false }, t + 9_000), false);
  assert.equal(o({ t: t + 10_000, inMatch: false }, t + 10_000), true);
  // back in this match: resets
  assert.equal(o({ t: t + 11_000, inMatch: true, match: "aa" }, t + 11_000), false);
  assert.equal(o({ t: t + 12_000, inMatch: true, match: "bb" }, t + 12_000), false);
  assert.equal(o({ t: t + 22_000, inMatch: true, match: "bb" }, t + 22_000), true); // another match for 10 s
  // a stale status (server down) never counts
  const o2 = P.orphanDetector("aa", 10_000);
  assert.equal(o2({ t, inMatch: false }, t + 20_000), false);
  assert.equal(o2(null, t + 40_000), false);
});

// ------------------------------------------------------------------ #4 / #5 / #7 small decisions
test("finalDeadline: min(t0 + 35 min, first start + 20 min) in full mode", () => {
  const t0 = 10_000_000;
  assert.equal(P.finalDeadline(t0, true, null), t0 + 35 * 60_000);
  assert.equal(P.finalDeadline(t0, true, t0 - 10 * 60_000), t0 + 10 * 60_000);
  assert.equal(P.finalDeadline(t0, false, t0 - 10 * 60_000), t0 + 20 * 60_000); // hybrid unchanged
});
test("skipClassic: only for full matches the server ended, with no Match on-chain", () => {
  assert.equal(P.skipClassic("fallback", true, false), true);
  assert.equal(P.skipClassic("done", true, false), true);
  assert.equal(P.skipClassic("aborted", true, false), true);
  assert.equal(P.skipClassic("live", true, false), false);
  assert.equal(P.skipClassic(null, true, false), false);
  assert.equal(P.skipClassic("fallback", true, true), false); // a Match exists: finish it
  assert.equal(P.skipClassic("fallback", false, false), false); // hybrid: unchanged
});
test("isMarketBusy / isAlreadyFinal match the program errors", () => {
  assert.equal(P.isMarketBusy(new Error('open_markets: tx:{"InstructionError":[0,{"Custom":6033}]}')), true);
  assert.equal(P.isMarketBusy("custom program error: 0x1791"), true);
  assert.equal(P.isMarketBusy("InvalidWritableAccount"), false);
  assert.equal(P.isAlreadyFinal('tx:{"InstructionError":[0,{"Custom":6016}]}'), true);
  assert.equal(P.isAlreadyFinal('tx:{"InstructionError":[0,{"Custom":60160}]}'), false);
});
test("mergeSummary keeps sigs / final / finalizeMode of an earlier run", () => {
  const old = { startedAt: 1, doneAt: 5, sigs: { create: "a", finalize: "f", endSim: "e" }, final: { realTime: 1 }, finalizeMode: "action", receipt: { champion: 4 } };
  const cur = { startedAt: 9, doneAt: 12, resumed: true, sigs: {}, finalizeMode: undefined, receipt: { champion: 4, flags: 7 }, settle: {} };
  const m = P.mergeSummary(old, cur);
  assert.deepEqual(m.sigs, old.sigs);
  assert.deepEqual(m.final, old.final);
  assert.equal(m.finalizeMode, "action");
  assert.equal(m.startedAt, 1);
  assert.equal(m.redrivenAt, 9);
  assert.equal(m.doneAt, 12);
  assert.deepEqual(m.receipt, { champion: 4, flags: 7 });
  assert.deepEqual(P.mergeSummary(null, cur), cur);
});
test("mbCore reads the MagicBlock core version", () => {
  assert.equal(P.mbCore({ "magicblock-core": "2.0.4", "solana-core": "4.2.2" }), "2.0.4");
  assert.equal(P.mbCore({ core: "2.0.4" }), "2.0.4");
  assert.equal(P.mbCore(null), null);
});

// ------------------------------------------------------------------ #4 ensureFinal
function fakeChain(o: { running: boolean; runsThis: boolean; phase: number; status: string; abortArenaFails?: number; abortMatchFails?: number; alreadyFinal?: boolean }) {
  const calls: string[] = [];
  const st = { ...o };
  const d: P.EnsureDeps = {
    arena: async () => ({ running: st.running, runsThis: st.runsThis, phase: st.phase }),
    matchStatus: async () => st.status,
    abortArena: async () => { calls.push("abort_arena"); if ((st.abortArenaFails ?? 0) > 0) { st.abortArenaFails!--; throw new Error("blockhash not found"); } st.running = false; },
    abortMatch: async (r) => {
      calls.push(`abort_match(${r})`);
      if ((st.abortMatchFails ?? 0) > 0) { st.abortMatchFails!--; throw new Error("timeout"); }
      if (st.alreadyFinal) { st.status = "aborted"; throw new Error('tx:{"InstructionError":[0,{"Custom":6016}]}'); }
      st.status = "aborted";
    },
    sleep: async () => {},
    log: (x) => calls.push(`log:${(x as any).ev}`),
  };
  return { d, calls, st };
}
test("ensureFinal guard: never aborts while the arena runs this match with phase < DONE (no force)", async () => {
  const { d, calls, st } = fakeChain({ running: true, runsThis: true, phase: 5, status: "roundActive" });
  assert.equal(await P.ensureFinalCore(d, 7, false), "guarded");
  assert.deepEqual(calls.filter((c) => !c.startsWith("log:")), []);
  assert.equal(st.status, "roundActive");
});
test("ensureFinal: a fallback (force) retries abort_arena until the arena stops, then aborts the Match", async () => {
  const { d, calls, st } = fakeChain({ running: true, runsThis: true, phase: 5, status: "roundActive", abortArenaFails: 1 });
  assert.equal(await P.ensureFinalCore(d, 6, true), "aborted");
  assert.deepEqual(calls.filter((c) => !c.startsWith("log:")), ["abort_arena", "abort_arena", "abort_match(6)"]);
  assert.equal(st.status, "aborted");
});
test("ensureFinal: VRF timeout (arena not running this match) aborts with retries; AlreadyFinal is ok", async () => {
  const a = fakeChain({ running: false, runsThis: false, phase: 0, status: "dealing", abortMatchFails: 2 });
  assert.equal(await P.ensureFinalCore(a.d, 7, false), "aborted");
  assert.equal(a.calls.filter((c) => c === "abort_match(7)").length, 3);
  const b = fakeChain({ running: true, runsThis: false, phase: 3, status: "dealt", alreadyFinal: true });
  assert.equal(await P.ensureFinalCore(b.d, 7, false), "aborted");
  assert.ok(!b.calls.includes("abort_arena")); // another match runs: the arena is never touched
  const c = fakeChain({ running: false, runsThis: false, phase: 0, status: "completed" });
  assert.equal(await P.ensureFinalCore(c.d, 7, false), "final");
  assert.deepEqual(c.calls, []);
});
test("ensureFinal throws when the abort never lands (the caller retries; nothing is swallowed)", async () => {
  const { d } = fakeChain({ running: false, runsThis: false, phase: 0, status: "dealing", abortMatchFails: 99 });
  await assert.rejects(P.ensureFinalCore(d, 7, false, 3), /abort_match did not land/);
});

// ------------------------------------------------------------------ #5 runFinal cleanup + stall recovery
test("cleanupFinal stops the crank, unsubscribes and cancels every task even when steps throw", async () => {
  let stopped = 0, unsub = 0;
  const cancelled: string[] = [];
  const tasks = ["1", "2", "3"];
  const stats = await P.cleanupFinal({
    crank: { stop: async () => { stopped++; return { sent: 10 }; } },
    unsubscribe: async () => { unsub++; throw new Error("ws closed"); },
    tasks,
    cancel: async (t) => { cancelled.push(t); if (t === "2") throw new Error("cancel failed"); },
  });
  assert.equal(stopped, 1); assert.equal(unsub, 1);
  assert.deepEqual(cancelled, ["1", "2", "3"]);
  assert.deepEqual(tasks, []);
  assert.deepEqual(stats, { sent: 10 });
  // a crank whose stop throws still lets the rest run
  let un2 = 0;
  await P.cleanupFinal({ crank: { stop: async () => { throw new Error("x"); } }, unsubscribe: async () => { un2++; }, tasks: [], cancel: async () => {} });
  assert.equal(un2, 1);
});

test("stallRecover: a blockhash that throws once mid-stall never escapes; one task set is rescheduled", async () => {
  let now = 0, bhCalls = 0, sent = 0, sched = 0;
  const tasks = ["7", "8"];
  const cancelled: string[] = [];
  const r = await P.stallRecover({
    cancel: async (t) => { cancelled.push(t); },
    schedule: async () => String(100 + sched++),
    blockhash: async () => { bhCalls++; if (bhCalls === 2) throw new Error("fetch failed"); return `bh${bhCalls}`; },
    sendTick: () => { sent++; },
    gaveUp: () => false,
    sleep: async (ms) => { now += ms; },
    now: () => now,
  }, tasks, { nTasks: 2, flood: true, driveMs: 6000, gapMs: 8 });
  assert.deepEqual(cancelled, ["7", "8"]);
  assert.equal(r.bhErrors, 1);
  assert.ok(sent > 600, `sent ${sent}`);
  assert.deepEqual(tasks, ["100", "101"]); // exactly FULL_TASKS fresh ones, no duplicate set
  assert.equal(r.scheduled, 2);
});
test("stallRecover: full mode (no flood) only re-schedules; a failed schedule is logged, not thrown", async () => {
  let sends = 0, n = 0;
  const logs: any[] = [];
  const tasks = ["1"];
  const r = await P.stallRecover({
    cancel: async () => {},
    schedule: async () => { n++; if (n === 1) throw new Error("schedule_ticks: timeout"); return "9"; },
    blockhash: async () => { throw new Error("never called"); },
    sendTick: () => { sends++; },
    gaveUp: () => false,
    sleep: async () => {},
    log: (o) => logs.push(o),
  }, tasks, { nTasks: 2, flood: false, driveMs: 1000 });
  assert.equal(sends, 0);
  assert.deepEqual(tasks, ["9"]);
  assert.equal(r.scheduled, 1);
  assert.equal(logs[0].ev, "reschedule_failed");
});
test("stallRecover: the drive stops at once when the server gave up", async () => {
  let now = 0, sent = 0;
  const r = await P.stallRecover({
    cancel: async () => {}, schedule: async () => "1", blockhash: async () => "bh", sendTick: () => { sent++; },
    gaveUp: () => sent >= 3, sleep: async (ms) => { now += ms; }, now: () => now,
  }, [], { nTasks: 1, flood: true, driveMs: 6000 });
  assert.equal(r.gaveUp, true);
  assert.equal(sent, 3);
});

test("review: the default top-up target clears the treasury low flag (a topped-up treasury never reads low)", () => {
  assert.ok(P.TREASURY_TOPUP_DEFAULT > P.LOW_DEFAULTS.treasury);
  const top = P.treasuryTopUp(5_000_000, 3, P.TREASURY_TOPUP_DEFAULT);
  assert.equal(P.lowBalance({ authorityLamports: null, treasuryLamports: 5_000_000 + top, funderLamports: null }).treasury, false);
});

// ------------------------------------------------------------------ followups: CRANK_GATE
test("tickDebt follows the program's real-time rule (elapsed_ms × 30 ≥ ticks_run × 1000)", () => {
  // slot_ms 50: 20 slots = 1000 ms → ticks 0..30 may have started (31); 30 run → 1 due now
  assert.equal(P.tickDebt({ slotNow: 120, startSlot: 100, ticksRun: 30, slotMs: 50 }), 1);
  assert.equal(P.tickDebt({ slotNow: 120, startSlot: 100, ticksRun: 31, slotMs: 50 }), 0); // caught up (NOT_DUE)
  assert.equal(P.tickDebt({ slotNow: 120, startSlot: 100, ticksRun: 40, slotMs: 50 }), -9); // never "ahead" of the program
  assert.equal(P.tickDebt({ slotNow: 140, startSlot: 100, ticksRun: 30, slotMs: 50 }), 31); // 1 s behind
  assert.equal(P.tickDebt({ slotNow: 0, startSlot: 0, ticksRun: 0, slotMs: 50 }), 1); // the first tick is due at once
  // the boundary: tick n is due exactly when elapsed × 30 = n × 1000 (n = 3 at 100 ms)
  assert.equal(P.tickDebt({ slotNow: 2, startSlot: 0, ticksRun: 3, slotMs: 50 }), 1);
  assert.equal(P.tickDebt({ slotNow: 1.99, startSlot: 0, ticksRun: 3, slotMs: 50 }), 0);
});

test("slotWallMs / slotNow: measured slot rate, fallback to slot_ms, extrapolated by wall time", () => {
  assert.equal(P.slotWallMs([]), null);
  assert.equal(P.slotWallMs([[10, 0], [12, 500]]), null); // span < 1 s
  assert.equal(P.slotWallMs([[10, 0], [10, 2000]]), null); // no slot moved
  assert.equal(P.slotWallMs([[10, 0], [50, 2000]]), 50);
  assert.equal(P.slotNow({ lastSlot: 100, seenAt: 1000, now: 1100, wallMsPerSlot: 25, slotMs: 50 }), 104);
  assert.equal(P.slotNow({ lastSlot: 100, seenAt: 1000, now: 1100, wallMsPerSlot: null, slotMs: 50 }), 102);
  assert.equal(P.slotNow({ lastSlot: 100, seenAt: 1000, now: 900, wallMsPerSlot: null, slotMs: 50 }), 100); // clock skew: never back
});

test("gateStep: fail-open start, closes when caught up after the hold, reopens on lag / stall / unknown", () => {
  const cfg = P.GATE_DEFAULTS;
  let s = P.gateInit(0);
  assert.equal(s.open, true);
  // keeping up (debt 0..1, fresh progress) but inside the 500 ms hold: stays open
  let r = P.gateStep(s, { now: 100, known: true, debt: 0, progAgeMs: 10 }, cfg);
  assert.equal(r.changed, false); assert.equal(r.state.open, true);
  // after the hold: closes
  r = P.gateStep(s, { now: 600, known: true, debt: 1, progAgeMs: 10 }, cfg);
  assert.equal(r.changed, true); assert.equal(r.state.open, false); s = r.state;
  assert.deepEqual(P.gateTotals(s, 600), { open: false, opens: 0, closes: 1, openMs: 600, closedMs: 0 });
  // a debt of 2..3 does not reopen (hysteresis band)
  r = P.gateStep(s, { now: 700, known: true, debt: 3, progAgeMs: 10 }, cfg);
  assert.equal(r.state.open, false);
  // lag > 3 ticks: opens
  r = P.gateStep(s, { now: 800, known: true, debt: 4, progAgeMs: 10 }, cfg);
  assert.equal(r.changed, true); assert.equal(r.state.open, true); assert.equal(r.why, "lag"); s = r.state;
  // open with debt 2 after the hold: stays open until ≤ closeTicks
  r = P.gateStep(s, { now: 1400, known: true, debt: 2, progAgeMs: 10 }, cfg);
  assert.equal(r.state.open, true);
  r = P.gateStep(s, { now: 1400, known: true, debt: 1, progAgeMs: 10 }, cfg);
  assert.equal(r.state.open, false); s = r.state;
  assert.deepEqual(P.gateTotals(s, 2000), { open: false, opens: 1, closes: 2, openMs: 600 + 600, closedMs: 200 + 600 });
  // no progress for > stallMs (a dead timed task): opens even with no debt
  r = P.gateStep(s, { now: 2000, known: true, debt: 0, progAgeMs: 60 }, cfg);
  assert.equal(r.state.open, true); assert.equal(r.why, "stall");
  // the World unknown (no fresh sample): opens
  r = P.gateStep(s, { now: 2000, known: false, debt: Infinity, progAgeMs: 0 }, cfg);
  assert.equal(r.state.open, true); assert.equal(r.why, "unknown");
  // a stalled World never closes, whatever the hold
  let o = P.gateInit(0);
  for (let t = 0; t < 5000; t += 100) o = P.gateStep(o, { now: t, known: true, debt: 0, progAgeMs: 400 }, cfg).state;
  assert.equal(o.open, true);
});

test("gate replay: R2–R4 idle ticks close it; an R1 backlog opens it, then it closes again", () => {
  // a crude 30 Hz World: the timed tasks keep up (debt 0/1) except a 1 s R1 window where they fall 6 ticks behind
  let s = P.gateInit(0), sends = 0, closedAt = -1, openedOnLag = false;
  for (let t = 0; t < 10_000; t += 10) {
    const backlog = t >= 4000 && t < 5000;
    const debt = backlog ? 6 : t % 33 < 3 ? 1 : 0;
    const r = P.gateStep(s, { now: t, known: true, debt, progAgeMs: backlog ? 30 : 5 });
    s = r.state;
    if (r.changed && !s.open && closedAt < 0) closedAt = t;
    if (r.changed && s.open && r.why === "lag") openedOnLag = true;
    if (s.open) sends++;
  }
  assert.ok(closedAt >= 500 && closedAt < 600, `closed after the hold (${closedAt})`);
  assert.ok(openedOnLag, "opened for the R1 backlog");
  assert.equal(s.open, false, "closed again once caught up");
  assert.ok(sends < 250, `open ≈ 0.5 s + 1.5 s of 10 s (${sends} of 1000 polls)`);
});

// ------------------------------------------------------------------ followups: FULL_MIN_HUMANS
test("fullHumansTarget / humansWaitDone: FULL_MIN_HUMANS raises the target (capped at N), the wait still times out", () => {
  assert.equal(P.fullHumansTarget(0, 0), 0); // default: the Classic roster only (unchanged)
  assert.equal(P.fullHumansTarget(2, 0), 2);
  assert.equal(P.fullHumansTarget(0, 11), 11); // 12-bot soak + 11 headless clients
  assert.equal(P.fullHumansTarget(3, 2), 3); // never below the roster's humans
  assert.equal(P.fullHumansTarget(0, 40), 12); // never above N
  assert.equal(P.fullHumansTarget(1, NaN), 1);
  assert.equal(P.fullHumansTarget(1, -5), 1);
  const w = { want: 0, minHumans: 11, waitS: 20 };
  assert.equal(P.humansWaitDone({ ...w, joined: 0, elapsedMs: 0 }), false); // before: done at once with want 0
  assert.equal(P.humansWaitDone({ ...w, minHumans: 0, joined: 0, elapsedMs: 0 }), true);
  assert.equal(P.humansWaitDone({ ...w, joined: 10, elapsedMs: 5000 }), false);
  assert.equal(P.humansWaitDone({ ...w, joined: 11, elapsedMs: 5000 }), true);
  assert.equal(P.humansWaitDone({ ...w, joined: 3, elapsedMs: 20_000 }), true); // the wait ran out: start anyway
});

test("orphanDetector: 0 = off (a dropped server socket never aborts a live chain match)", () => {
  const d = P.orphanDetector("aa", 0);
  assert.equal(d({ t: 1000, inMatch: false, match: "" }, 1000), false);
  assert.equal(d({ t: 60_000, inMatch: false, match: "" }, 60_000), false);
});
