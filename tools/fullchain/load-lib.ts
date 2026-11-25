// fullchain multi-human load test (plan item #10): the pure part — client config, input batches, slot choice and the
// metrics (per-round real time, World executions/s, World lag, input confirm latency, applied ratio, pass criteria,
// CRANK_GATE comparison). No network and no keys here, so it is unit tested (load.test.ts) and used by --dry-run.
// The runner is load.ts.
import { readFileSync } from "node:fs";
import { join } from "node:path";

export const ROOT = new URL("../../", import.meta.url).pathname;
export const N_SLOTS = 12;
export const HZ = 30;
export const BATCH = 3; // ticks per submit_input (ChainConfig.batchTicks 3 at 30 Hz = 10 tx/s per human)
export const INPUT_WINDOW = 8; // sim.rs INPUT_WINDOW: start ≤ world_tick + lead (lead default 8) and start + 8 ≥ world_tick
export const DONE_PHASE = 11; // WorldAcc phase ≥ 11 = the match is over (devnet.json world.donePhase)
export const RUSH = 3; // rush-core rules::phase: … 2 COUNTDOWN, 3 RUSH (WorldAcc byte 199)
// rush-core input.rs buttons
export const JUMP_HELD = 1 << 0, JUMP_PRESSED = 1 << 1;

// ------------------------------------------------------------------ client config

export interface LoadOpts {
  arena: number;
  rawSockets: number;
  erHttp?: string;
  erWs?: string;
  baseHttp?: string;
  confirmTimeoutMs?: number;
}

/** The browser bundle's devnet config re-pointed at dev arena `arena` (PDAs derived by the transport from arenaId). */
export function clientConfig(o: LoadOpts, base = JSON.parse(readFileSync(join(ROOT, "packages/chain/config/devnet.json"), "utf8"))): any {
  const c = structuredClone(base);
  c.name = `fc-load-a${o.arena}`;
  c.addresses = { ...c.addresses };
  delete c.addresses.arena;
  delete c.addresses.world;
  c.rollup = { ...c.rollup, p2: { ...c.rollup.p2, arenaId: o.arena }, rawSockets: o.rawSockets };
  if (o.erHttp) c.rollup.erHttp = o.erHttp;
  if (o.erWs) c.rollup.erWs = o.erWs;
  if (o.baseHttp) c.rollup.baseHttp = o.baseHttp;
  if (o.confirmTimeoutMs) c.rollup.confirmTimeoutMs = o.confirmTimeoutMs;
  // a headless client drains its events every 100 ms; keep only a few Worlds (nobody renders them)
  c.queue = { maxWorlds: 8, maxEvents: 8192 };
  return c;
}

// ------------------------------------------------------------------ inputs

export interface Cmd { mx: number; my: number; buttons: number; yaw: number }

/** wire-v0 InputBatch: u8 slot | u32 first_tick | u8 count | count × {i8 mx, i8 my, u8 buttons, u16 yaw} (what
 * OnChainSession.FlushBatch hands FallRushChain.sendInputs). */
export function encodeBatch(slot: number, start: number, cmds: Cmd[]): Uint8Array {
  if (cmds.length < 1 || cmds.length > BATCH) throw new Error("1..3 commands");
  const b = new Uint8Array(6 + 5 * cmds.length);
  const dv = new DataView(b.buffer);
  b[0] = slot; dv.setUint32(1, start >>> 0, true); b[5] = cmds.length;
  cmds.forEach((c, k) => {
    const o = 6 + 5 * k;
    dv.setInt8(o, Math.max(-127, Math.min(127, c.mx | 0)));
    dv.setInt8(o + 1, Math.max(-127, Math.min(127, c.my | 0)));
    b[o + 2] = c.buttons & 0xff;
    dv.setUint16(o + 3, c.yaw & 0xff00, true); // the program keeps yaw >> 8 (OnChainSession masks the same way)
  });
  return b;
}

/** mulberry32: a small seeded PRNG so each headless client's moves are reproducible from its index. */
export function prng(seed: number) {
  let a = seed >>> 0;
  return () => { a = (a + 0x6d2b79f5) >>> 0; let t = a; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
}

/** A human-ish random mover: holds a direction for 0.5–2 s, mostly forward, jumps now and then (press + hold a few
 * ticks), turns the camera slowly. One command per tick. */
export function mover(seed: number) {
  const r = prng(seed);
  let mx = 0, my = 127, yaw = Math.floor(r() * 65536), holdTicks = 0, jump = 0;
  return (): Cmd => {
    if (holdTicks-- <= 0) {
      holdTicks = 15 + Math.floor(r() * 45);
      const fwd = r() < 0.75;
      my = fwd ? 127 : Math.round((r() * 2 - 1) * 127);
      mx = Math.round((r() * 2 - 1) * (fwd ? 60 : 127));
      if (r() < 0.1) { mx = 0; my = 0; } // stands still sometimes
    }
    let buttons = 0;
    if (jump > 0) { buttons = JUMP_HELD; jump--; }
    else if (r() < 0.03) { buttons = JUMP_HELD | JUMP_PRESSED; jump = 4 + Math.floor(r() * 6); }
    yaw = (yaw + Math.round((r() * 2 - 1) * 600)) & 0xffff;
    return { mx, my, buttons, yaw };
  };
}

/**
 * Where the next batch starts. The client predicts ahead of the World (OnChainSession.Target: chain now + inputLead +
 * extraLead + batch − 1), so aim `lead` ticks ahead of the newest World tick, never re-send a tick (the cursor), and
 * never past the program's window (start ≤ tick + INPUT_WINDOW; a batch further ahead is rejected, so hold it).
 * Returns null when this 100 ms step should send nothing (the World is behind our cursor by more than the window).
 */
export function nextStart(worldTick: number, cursor: number, lead: number, window = INPUT_WINDOW): number | null {
  if (worldTick < 0) return null;
  const s = Math.max(cursor, worldTick + lead);
  if (s > worldTick + window) return null;
  return s;
}

// ------------------------------------------------------------------ slots

/** The slots the headless clients take: free (not in humanMask), not reserved (a browser human's Classic slot),
 * from the top (11, 10, …) so the low slots stay for the browser. */
export function chooseSlots(n: number, reserved: number[], humanMask = 0): number[] {
  const out: number[] = [];
  for (let s = N_SLOTS - 1; s >= 0 && out.length < n; s--) if (!reserved.includes(s) && !((humanMask >> s) & 1)) out.push(s);
  return out;
}

export function popcount(m: number): number { let n = 0; for (let s = 0; s < N_SLOTS; s++) if ((m >> s) & 1) n++; return n; }

/** The Classic server's human slots from its match jsonl header (bot === false), for RESERVE. */
export function rosterHumans(jsonlText: string, matchIdHex?: string): number[] | null {
  for (const l of jsonlText.split("\n")) {
    if (!l.trim()) continue;
    let j: any; try { j = JSON.parse(l); } catch { continue; }
    if (j?.type !== "match") continue;
    if (matchIdHex && String(j.matchId).toLowerCase() !== matchIdHex.toLowerCase()) return null;
    return (j.roster ?? []).filter((r: any) => r.bot === false).map((r: any) => Number(r.slot));
  }
  return null;
}

// ------------------------------------------------------------------ World account

export interface WorldSample { t: number; tick: number; round: number; rphase: number; phase: number; exec: number; notDue: number; ticksRun: number; lastSlot: number; startSlot: number; applied: number[]; inputLead?: number }

/** WorldAcc header (sim.rs; tools/keeper/lib.ts decodeWorld): 8 disc | arena 32 | last_slot, ticks_run, executions,
 * not_due, budget_hit u64 | applied_tick[12] u32 @80 | tick u32 @128 | len @132 | part_pending, parts, phase @138,
 * round @139 … | serial @194 | input_lead @198 | round phase @199 | start_slot u64 @200 | world @208. */
export function decodeWorldSample(d: Uint8Array, t: number): WorldSample {
  const dv = new DataView(d.buffer, d.byteOffset, d.byteLength);
  const u64 = (o: number) => Number(dv.getBigUint64(o, true));
  const applied: number[] = [];
  for (let s = 0; s < N_SLOTS; s++) applied.push(dv.getUint32(80 + 4 * s, true));
  return { t, lastSlot: u64(40), ticksRun: u64(48), exec: u64(56), notDue: u64(64), applied, tick: dv.getUint32(128, true), phase: d[138], round: d[139], rphase: d[199], inputLead: d[198] || INPUT_WINDOW, startSlot: u64(200) };
}

// ------------------------------------------------------------------ metrics

export function pct(xs: number[], p: number): number {
  if (!xs.length) return NaN;
  const s = [...xs].sort((a, b) => a - b);
  return s[Math.min(s.length - 1, Math.max(0, Math.ceil((p / 100) * s.length) - 1))];
}

const r3 = (x: number) => (Number.isFinite(x) ? Math.round(x * 1000) / 1000 : null);

export interface RoundStat {
  round: number; ticks: number; wallS: number; realTime: number | null; rushRealTime: number | null;
  execPerS: number | null; notDuePerS: number | null; ranPerS: number | null; notDueShare: number | null;
  lagMaxTicks: number | null; lagP95Ticks: number | null; samples: number; appliedRatio: number | null;
}

/** Slot duration (ms) from the samples: Δlast_slot / Δwall over the whole run (the ER's slot clock). */
export function slotMs(samples: WorldSample[]): number | null {
  const a = samples.find((s) => s.lastSlot > 0), b = [...samples].reverse().find((s) => s.lastSlot > 0);
  if (!a || !b || b.lastSlot <= a.lastSlot) return null;
  return (b.t - a.t) / (b.lastSlot - a.lastSlot);
}

/** World lag in ticks (the CRANK_GATE signal): elapsed_slots × slot_ms × 30 / 1000 − ticks_run. */
export function lagTicks(s: WorldSample, slotDurMs: number): number | null {
  if (!s.startSlot || !s.lastSlot || s.lastSlot < s.startSlot) return null;
  return ((s.lastSlot - s.startSlot) * slotDurMs * HZ) / 1000 - s.ticksRun;
}

/** Fraction of RUSH samples where each headless slot's applied_tick is the World tick (its input made it in time). */
export function appliedRatio(samples: WorldSample[], slots: number[]): number | null {
  let n = 0, ok = 0;
  for (const s of samples) {
    if (s.rphase !== RUSH) continue;
    for (const k of slots) { n++; if (s.applied[k] >= s.tick) ok++; }
  }
  return n ? ok / n : null;
}

function span(xs: WorldSample[]) {
  if (xs.length < 2) return null;
  const a = xs[0], b = xs[xs.length - 1];
  return { ticks: b.tick - a.tick, wallS: (b.t - a.t) / 1000, exec: b.exec - a.exec, notDue: b.notDue - a.notDue };
}

/** Per round (1..4): real time = (Δtick / 30) / Δwall over the round's samples; RUSH-only real time; World
 * executions/s (timed tasks + crank, ran and NOT_DUE no-ops), no-ops/s, World lag. */
export function roundStats(samples: WorldSample[], slots: number[] = []): RoundStat[] {
  const sm = slotMs(samples);
  const out: RoundStat[] = [];
  for (let r = 1; r <= 4; r++) {
    const xs = samples.filter((s) => s.round === r && s.phase < DONE_PHASE);
    const sp = span(xs);
    if (!sp || sp.wallS <= 0) continue;
    const rush = span(xs.filter((s) => s.rphase === RUSH));
    const lags = sm ? xs.map((s) => lagTicks(s, sm)).filter((x): x is number => x !== null) : [];
    out.push({
      round: r, ticks: sp.ticks, wallS: r3(sp.wallS)!, samples: xs.length,
      realTime: r3(sp.ticks / HZ / sp.wallS),
      rushRealTime: rush && rush.wallS > 0 ? r3(rush.ticks / HZ / rush.wallS) : null,
      execPerS: r3(sp.exec / sp.wallS), notDuePerS: r3(sp.notDue / sp.wallS), ranPerS: r3((sp.exec - sp.notDue) / sp.wallS),
      notDueShare: sp.exec ? r3(sp.notDue / sp.exec) : null,
      lagMaxTicks: lags.length ? r3(Math.max(...lags)) : null, lagP95Ticks: lags.length ? r3(pct(lags, 95)) : null,
      appliedRatio: r3(appliedRatio(xs, slots) ?? NaN),
    });
  }
  return out;
}

export interface Criteria { minRealTime: number; maxInputP95Ms: number; expectedHumans: number }
export const DEFAULT_CRITERIA: Criteria = { minRealTime: 0.99, maxInputP95Ms: 400, expectedHumans: 12 };

/** Plan #10 pass criteria (replay byte-exact is checked separately with tools/p7/replay-check.mjs on the keeper archive). */
export function verdict(rounds: RoundStat[], inputP95: number, joined: number, c: Criteria) {
  const realTime = rounds.length === 4 && rounds.every((r) => (r.realTime ?? 0) >= c.minRealTime);
  const input = Number.isFinite(inputP95) && inputP95 < c.maxInputP95Ms;
  const humans = joined === c.expectedHumans;
  return { pass: realTime && input && humans, realTime, rounds: rounds.length, inputP95: input, joined: humans, criteria: c };
}

/** CRANK_GATE=0 run `a` vs CRANK_GATE=1 run `b`: per round real time (b must stay ≥ 0.99 per round) and World
 * executions/s saved (the plan expects ~45/s fewer in R2–R4). */
export function compareRuns(a: { rounds: RoundStat[]; label?: string }, b: { rounds: RoundStat[]; label?: string }, minRealTime = 0.99) {
  const rows = [1, 2, 3, 4].map((r) => {
    const x = a.rounds.find((q) => q.round === r), y = b.rounds.find((q) => q.round === r);
    return {
      round: r,
      realTimeA: x?.realTime ?? null, realTimeB: y?.realTime ?? null,
      execPerSA: x?.execPerS ?? null, execPerSB: y?.execPerS ?? null,
      execSavedPerS: x?.execPerS != null && y?.execPerS != null ? r3(x.execPerS - y.execPerS) : null,
      lagMaxB: y?.lagMaxTicks ?? null,
    };
  });
  const gateOk = rows.every((r) => r.realTimeB !== null && r.realTimeB >= minRealTime);
  const saved = rows.filter((r) => r.round >= 2 && r.execSavedPerS !== null).map((r) => r.execSavedPerS!);
  return { a: a.label ?? "A", b: b.label ?? "B", rows, gateOk, meanSavedR2R4: saved.length ? r3(saved.reduce((p, q) => p + q, 0) / saved.length) : null };
}

/** Input confirm summary from the bundle's confirm events (kind "input"). */
export function confirmSummary(ms: number[], counts: { ok: number; err: number; timeout: number; window: number; sent: number }) {
  return { n: ms.length, p50: r3(pct(ms, 50)), p95: r3(pct(ms, 95)), p99: r3(pct(ms, 99)), max: ms.length ? Math.max(...ms) : null, ...counts, okRatio: counts.sent ? r3(counts.ok / counts.sent) : null };
}
