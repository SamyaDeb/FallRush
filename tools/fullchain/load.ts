// fullchain multi-human load test (plan item #10). N headless "humans" in one Node process, each the real browser
// chain bundle (packages/chain createChain + RollupTransport: burner, SessionTokenV2, join_lobby, raw accountSubscribe
// sockets, submit_input, confirm polling), join dev arena ARENA's lobby for a full start_sim match and send
// submit_input at 10 Hz (3 ticks per tx, random human-ish moves) for the whole match. An observer polls the WorldAcc
// and records per-round real time, World executions/s (timed tasks + crank, ran vs NOT_DUE), World lag; the clients
// record input confirm latency (p50/p95), the applied ratio and the joined count. Pass criteria (plan #10): every round
// ≥ 0.99× real time, input p95 < 400 ms, joined == N + BROWSERS. Replay byte-exact is the keeper archive's
// tools/p7/replay-check.mjs (scripts/fc-load.sh runs it).
//
// Never against the live arenas 2/3. The keeper daemon + Classic server for ARENA must already run (scripts/fc-load.sh).
//   node --experimental-strip-types tools/fullchain/load.ts --dry-run             (no network: config, encoding, metrics)
//   P2_NET=devnet ARENA=7 N=11 BROWSERS=1 LABEL=gate0 node --experimental-strip-types tools/fullchain/load.ts
//   node --experimental-strip-types tools/fullchain/load.ts compare <gate0.json> <gate1.json>
// Env: ARENA (7) · N headless clients (11) · BROWSERS extra humans expected in the roster (0) · MATCHES (1) · LABEL
//      LEAD ticks ahead of the estimated World tick now (5: the browser's inputLead 3 + extraLead 2) · RAW_SOCKETS per client (2, as the browser) · SUBSCRIBE (1)
//      FUND_FROM keypair (~/.config/solana/id.json, the admin) · FUND_LAMPORTS (10_000_000) · FUND_MIN (6_000_000)
//      RESERVE_SLOTS "0,3" (browser humans' slots) · MATCH_LOG_DIR (the Classic server's match-*.jsonl: its human
//      slots are reserved automatically) · OBS_MS World poll (100) · OUT (tools/fullchain/.load/<label>-<ts>.json)
//      ER_HTTP / ER_WS / SOLANA_RPC_URL endpoint overrides · LOBBY_TIMEOUT_S (900) · MATCH_TIMEOUT_S (900)
import { Connection, Keypair, PublicKey } from "@solana/web3.js";
import { readFileSync, writeFileSync, existsSync, mkdirSync, readdirSync, statSync } from "node:fs";
import { homedir } from "node:os";
import { join, dirname } from "node:path";
import { monitorEventLoopDelay } from "node:perf_hooks";
import { createChain } from "../../packages/chain/src/api.ts";
import type { KV } from "../../packages/chain/src/storage.ts";
import { b58encode, bytesToHex } from "../../packages/chain/src/b58.ts";
import { buildInstruction } from "../../packages/chain/src/ix.ts";
import { decodeArena, decodeMatch, toP2Batch } from "../../packages/chain/src/p2.ts";
import * as L from "./load-lib.ts";

const env = process.env;
const num = (k: string, d: number) => (env[k] !== undefined && env[k] !== "" ? Number(env[k]) : d);
const ARENA = num("ARENA", 7), N = num("N", 11), BROWSERS = num("BROWSERS", 0), MATCHES = num("MATCHES", 1);
const LEAD = num("LEAD", 5), RAW_SOCKETS = num("RAW_SOCKETS", 2), SUBSCRIBE = env.SUBSCRIBE !== "0", OBS_MS = num("OBS_MS", 100);
const FUND_LAMPORTS = num("FUND_LAMPORTS", 10_000_000), FUND_MIN = num("FUND_MIN", 6_000_000);
const LOBBY_TIMEOUT_S = num("LOBBY_TIMEOUT_S", 900), MATCH_TIMEOUT_S = num("MATCH_TIMEOUT_S", 900);
const LABEL = env.LABEL || `crankgate${env.CRANK_GATE ?? "?"}`;
const RESERVE = (env.RESERVE_SLOTS ?? "").split(",").filter((x) => x.trim() !== "").map(Number);
const KEYS = join(L.ROOT, "tools/fullchain/.keys"); // gitignored (.keys/)
const OUTDIR = join(L.ROOT, "tools/fullchain/.load"); // gitignored (tools/fullchain/.gitignore)
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
const log = (o: object) => console.log(JSON.stringify({ t: new Date().toISOString(), ...o }));

const cfgOpts: L.LoadOpts = { arena: ARENA, rawSockets: RAW_SOCKETS, erHttp: env.ER_HTTP, erWs: env.ER_WS, baseHttp: env.SOLANA_RPC_URL };

// ------------------------------------------------------------------ guards + subcommands

const argv = process.argv.slice(2);
if (argv[0] === "compare") {
  if (argv.length < 3) { console.error("usage: load.ts compare <runA.json> <runB.json> [match index]"); process.exit(2); }
  const k = Number(argv[3] ?? 0);
  const a = JSON.parse(readFileSync(argv[1], "utf8")), b = JSON.parse(readFileSync(argv[2], "utf8"));
  if (!a.matches?.[k] || !b.matches?.[k]) { console.error(`match ${k} missing in ${!a.matches?.[k] ? argv[1] : argv[2]}`); process.exit(2); }
  const c = L.compareRuns({ rounds: a.matches[k].rounds, label: a.label, crankGate: a.crankGate }, { rounds: b.matches[k].rounds, label: b.label, crankGate: b.crankGate });
  console.log(JSON.stringify(c, null, 1));
  if (!c.order) console.error("warning: run A must be CRANK_GATE=0 and run B CRANK_GATE=1");
  if (!c.gateEffective) console.error("warning: the gated run saved < 10 World executions/s in R2–R4: is CRANK_GATE implemented in the keeper (check keeper-status.json crankGated)?");
  process.exit(c.ship ? 0 : 1);
}
if (argv.includes("--dry-run") || env.DRY === "1") { dryRun(); process.exit(0); }
if (ARENA === 2 || ARENA === 3) { console.error("refusing: arenas 2 and 3 are the live fallrush.fun arenas (use dev arena 7)"); process.exit(2); }
if (env.P2_NET !== "devnet") { console.error("set P2_NET=devnet (the load test runs against devnet + devnet-as only)"); process.exit(2); }
if (N < 1 || N + BROWSERS > L.N_SLOTS) { console.error(`N + BROWSERS must be 1..${L.N_SLOTS}`); process.exit(2); }

/** --dry-run: no network, no keys. Checks the client config, the submit_input encoding through the bundle's own
 * instruction builder, the slot plan and the metrics on synthetic samples. */
function dryRun() {
  const cfg = L.clientConfig(cfgOpts);
  const program = new PublicKey(cfg.addresses.program);
  const arena = PublicKey.findProgramAddressSync([Buffer.from("arena"), Buffer.from([ARENA])], program)[0];
  const world = PublicKey.findProgramAddressSync([Buffer.from("world"), arena.toBuffer()], program)[0];
  const m = L.mover(1);
  const batch = L.encodeBatch(11, 1234, [m(), m(), m()]);
  const p = toP2Batch(batch);
  const fake = Keypair.generate().publicKey;
  const ix = buildInstruction(cfg.rollup.submitInput, { ...cfg.addresses, arena, world, player: fake, sessionToken: fake, slot: p.slot, start: p.start, batch12: p.batch12 });
  const samples: L.WorldSample[] = [];
  for (let i = 0; i <= 400; i++) samples.push({ t: i * 100, tick: i * 3, round: 1 + Math.floor(i / 101), rphase: L.RUSH, phase: 3, exec: i * 15, notDue: i * 9, ticksRun: i * 3, lastSlot: 1000 + i * 2, startSlot: 1000, applied: Array(12).fill(i * 3) });
  const rounds = L.roundStats(samples, [11]);
  console.log(JSON.stringify({
    dryRun: true, arena: ARENA, arenaPda: arena.toBase58(), worldPda: world.toBase58(), n: N, browsers: BROWSERS, lead: LEAD, rawSockets: RAW_SOCKETS,
    slotPlan: L.chooseSlots(N, RESERVE), reserve: RESERVE,
    submitInput: { programId: ix.programId.toBase58(), keys: ix.keys.length, dataLen: ix.data.length, dataHex: Buffer.from(ix.data).toString("hex") },
    ratePerClient: `${1000 / 100} tx/s (${L.BATCH} ticks/tx at ${L.HZ} Hz)`, expectedInputTxPerS: N * 10,
    syntheticRounds: rounds, syntheticVerdict: L.verdict(rounds, 200, N + BROWSERS, { ...L.DEFAULT_CRITERIA, expectedHumans: N + BROWSERS }),
  }, null, 1));
}

// ------------------------------------------------------------------ headless client

/** localStorage stand-in persisted per client in tools/fullchain/.keys (mode 600, gitignored, never printed), so a
 * re-run reuses the funded burner and its still-valid session token. */
function fileKV(path: string): KV {
  let m: Record<string, string> = existsSync(path) ? JSON.parse(readFileSync(path, "utf8")) : {};
  const save = () => { mkdirSync(dirname(path), { recursive: true }); writeFileSync(path, JSON.stringify(m), { mode: 0o600 }); };
  return { getItem: (k) => m[k] ?? null, setItem: (k, v) => { m[k] = v; save(); }, removeItem: (k) => { delete m[k]; save(); } };
}

interface Client {
  i: number; chain: ReturnType<typeof createChain>; burner: string;
  slot: number; joined: boolean; joinMs: number | null; joinError?: string;
  cursor: number; sent: number; held: number; next: () => L.Cmd; timer?: ReturnType<typeof setInterval>; clock: L.TickClock;
  confirmMs: number[]; ok: number; err: number; timeout: number; window: number; sendErr: number; errs: Record<string, number>;
}

async function makeClient(i: number): Promise<Client> {
  const chain = createChain({ kv: fileKV(join(KEYS, `load-devnet-a${ARENA}-${i}.json`)) });
  const r = JSON.parse(await chain.init(JSON.stringify(L.clientConfig(cfgOpts))));
  if (!r.ok) throw new Error(`client ${i} init: ${r.error}`);
  return { i, chain, burner: r.burner, slot: 255, joined: false, joinMs: null, cursor: 0, sent: 0, held: 0, next: L.mover(0x5eed + i), clock: new L.TickClock(), confirmMs: [], ok: 0, err: 0, timeout: 0, window: 0, sendErr: 0, errs: {} };
}

function drain(c: Client) {
  let evs: any[] = [];
  try { evs = JSON.parse(c.chain.drainEvents()); } catch { return; }
  for (const e of evs) {
    // a send that never reached the rollup (build / cloner-retry / "not joined"): no confirm event follows
    if (e.type === "error" && e.where === "sendInputs") { c.sendErr++; const k = `send:${String(e.why ?? "").slice(0, 40)}`; c.errs[k] = (c.errs[k] ?? 0) + 1; continue; }
    if (e.type !== "confirm" || e.kind !== "input") continue;
    if (e.status === "ok") { c.ok++; if (typeof e.ms === "number") c.confirmMs.push(e.ms); }
    else if (e.status === "timeout") c.timeout++;
    else {
      c.err++;
      const code = String(e.err ?? "").match(/"Custom":(\d+)/)?.[1] ?? "other";
      if (code === "6020") c.window++;
      c.errs[code] = (c.errs[code] ?? 0) + 1;
    }
  }
}

// ------------------------------------------------------------------ main

const cfg0 = L.clientConfig(cfgOpts);
const PROGRAM = new PublicKey(cfg0.addresses.program);
const ARENA_PDA = PublicKey.findProgramAddressSync([Buffer.from("arena"), Buffer.from([ARENA])], PROGRAM)[0];
const WORLD_PDA = PublicKey.findProgramAddressSync([Buffer.from("world"), ARENA_PDA.toBuffer()], PROGRAM)[0];
const er = new Connection(cfg0.rollup.erHttp, { commitment: "processed" });
const base = new Connection(cfg0.rollup.baseHttp, { commitment: "confirmed" });
const p2 = cfg0.rollup.p2;
const readArena = async () => { const i = await er.getAccountInfo(ARENA_PDA, "processed").catch(() => null); return i ? decodeArena(new Uint8Array(i.data), p2) : null; };

const stamp = new Date().toISOString().replace(/[:.]/g, "-");
const OUT = env.OUT || join(OUTDIR, `${LABEL}-${stamp}.json`);
mkdirSync(dirname(OUT), { recursive: true });
const report: any = { label: LABEL, crankGate: env.CRANK_GATE ?? null, arena: ARENA, arenaPda: ARENA_PDA.toBase58(), world: WORLD_PDA.toBase58(), n: N, browsers: BROWSERS, lead: LEAD, rawSockets: RAW_SOCKETS, subscribe: SUBSCRIBE, startedAt: new Date().toISOString(), clients: [] as any[], matches: [] as any[] };
const save = () => writeFileSync(OUT, JSON.stringify(report, null, 1));

log({ ev: "start", arena: ARENA, arenaPda: ARENA_PDA.toBase58(), n: N, browsers: BROWSERS, out: OUT });

// 1. clients: init (sequential: each one opens its sockets + pollers), fund the burners from the admin wallet, then
//    the SessionTokenV2 on the base layer (the bundle's token bucket ≤ 1.5 tx/s each; done before the lobby so
//    join_lobby is one rollup tx, as for a returning browser player)
const fundFrom = env.FUND_FROM || join(homedir(), ".config/solana/id.json");
const funder = existsSync(fundFrom) ? b58encode(Keypair.fromSecretKey(Uint8Array.from(JSON.parse(readFileSync(fundFrom, "utf8")))).secretKey) : null;
const clients: Client[] = [];
for (let i = 0; i < N; i++) {
  const c = await makeClient(i);
  clients.push(c);
  let bal = -1;
  for (let k = 0; k < 3 && bal < 0; k++) { bal = await base.getBalance(new PublicKey(c.burner)).catch(() => -1); if (bal < 0) await sleep(1000); }
  if (bal < 0) throw new Error(`burner ${c.burner}: balance unreadable on ${cfg0.rollup.baseHttp}`);
  if (bal >= 0 && bal < FUND_MIN) {
    if (!funder) throw new Error(`burner ${c.burner} has ${bal} lamports and FUND_FROM ${fundFrom} is missing`);
    const f = JSON.parse(await c.chain.devFund(funder, FUND_LAMPORTS));
    log({ ev: "fund", i, burner: c.burner, before: bal, ok: f.ok, sig: f.sig, error: f.error });
    if (!f.ok) throw new Error(`fund ${i}: ${f.error}`);
  }
  const s = JSON.parse(await c.chain.session("create", "{}"));
  log({ ev: "session", i, burner: c.burner, ok: s.ok, note: s.note, error: s.error });
  report.clients.push({ i, burner: c.burner, funded: bal < FUND_MIN, session: !!s.ok });
}
save();

// 2. observer: World every OBS_MS, Arena every second; event-loop delay of this process (a saturated load tool must
//    not be mistaken for a slow rollup)
let samples: L.WorldSample[] = [];
let obsTick = -1, obsPhase = 0, obsLead = L.INPUT_WINDOW;
let observing = true;
const loop = monitorEventLoopDelay({ resolution: 10 });
loop.enable();
const observer = (async () => {
  while (observing) {
    const t0 = Date.now();
    try {
      const i = await er.getAccountInfo(WORLD_PDA, "processed");
      if (i) { const s = L.decodeWorldSample(new Uint8Array(i.data), Date.now()); samples.push(s); obsTick = s.tick; obsPhase = s.phase; obsLead = s.inputLead ?? L.INPUT_WINDOW; }
    } catch { /* next poll */ }
    await sleep(Math.max(0, OBS_MS - (Date.now() - t0)));
  }
})();

let lastMatch = (await readArena())?.matchAccount.toBase58() ?? "";
for (let mi = 0; mi < MATCHES; mi++) {
  // 3. the next match's lobby (a new Arena.match_account in status lobby)
  log({ ev: "waitLobby", match: mi + 1, after: lastMatch });
  let a: Awaited<ReturnType<typeof readArena>> = null;
  for (const t0 = Date.now(); ;) {
    a = await readArena();
    if (a && a.status === p2.arenaStatus.lobby && !a.matchAccount.equals(PublicKey.default) && a.matchAccount.toBase58() !== lastMatch) break;
    if (Date.now() - t0 > LOBBY_TIMEOUT_S * 1000) { log({ ev: "lobbyTimeout" }); break; }
    await sleep(200);
  }
  if (!a || a.status !== p2.arenaStatus.lobby) break;
  const matchPk = a.matchAccount;
  lastMatch = matchPk.toBase58();
  const md = await er.getAccountInfo(matchPk, "processed").catch(() => null);
  const mInfo = md ? decodeMatch(new Uint8Array(md.data), p2) : null;
  const matchHex = mInfo ? bytesToHex(mInfo.matchId) : undefined;
  // browser humans' Classic slots: RESERVE_SLOTS plus the server's jsonl header for this match id
  const reserved = [...RESERVE];
  if (env.MATCH_LOG_DIR && matchHex) {
    const f = join(env.MATCH_LOG_DIR, `match-${matchHex}.jsonl`);
    const files = existsSync(f) ? [f] : readdirSync(env.MATCH_LOG_DIR).filter((x) => x.endsWith(".jsonl")).map((x) => join(env.MATCH_LOG_DIR!, x)).sort((p, q) => statSync(q).mtimeMs - statSync(p).mtimeMs).slice(0, 4);
    for (const p of files) { const h = L.rosterHumans(readFileSync(p, "utf8"), matchHex); if (h) { reserved.push(...h); break; } }
  }
  const slots = L.chooseSlots(N, reserved, a.humanMask);
  log({ ev: "lobby", match: lastMatch, matchId: matchHex, humanMask: a.humanMask, reserved, slots });
  samples = [];
  const tJoin = Date.now();

  // 4. every client joins (join_lobby with its slot); the bundle's join resolves once start_sim ran (arena running)
  const joinOne = async (c: Client, slot: number) => {
    c.joined = false; c.slot = 255; c.joinError = undefined; c.cursor = 0; c.sent = 0; c.held = 0; c.clock.reset();
    c.confirmMs = []; c.ok = 0; c.err = 0; c.timeout = 0; c.window = 0; c.sendErr = 0; c.errs = {};
    for (let attempt = 0; attempt < 4; attempt++) {
      const r = JSON.parse(await c.chain.join(JSON.stringify({ full: true, slot, match: matchHex, name: `load-${c.i}` })));
      if (r.ok && r.slot !== 255) { c.slot = r.slot; c.joined = true; c.joinMs = Date.now() - tJoin; return; }
      if (r.ok) { c.joinError = "missedLobby (spectating): start_sim ran before this join"; return; }
      c.joinError = r.error;
      if (!/6023|SlotTaken/i.test(String(r.error))) return;
      const fresh = await readArena();
      const free = L.chooseSlots(L.N_SLOTS, reserved, fresh?.humanMask ?? 0).filter((s) => !clients.some((o) => o !== c && o.slot === s));
      if (!free.length) return;
      slot = free[free.length - 1 - (c.i % free.length)];
    }
  };
  await Promise.all(clients.map((c, k) => (k < slots.length ? sleep(k * 30).then(() => joinOne(c, slots[k])) : Promise.resolve())));
  const arun = await readArena();
  const joinedHeadless = clients.filter((c) => c.joined);
  log({ ev: "started", humanMask: arun?.humanMask, humans: popc(arun?.humanMask ?? 0), headless: joinedHeadless.length, errors: clients.filter((c) => !c.joined).map((c) => ({ i: c.i, e: c.joinError })) });

  // the observer may still hold the previous match's finished World: wait for start_sim's fresh one (phase < done)
  for (const t0 = Date.now(); obsPhase >= L.DONE_PHASE && Date.now() - t0 < 15_000;) await sleep(50);
  samples = samples.filter((s) => s.phase < L.DONE_PHASE);

  // 5. inputs: 10 Hz per client, staggered, until the World's phase is done (the event-loop delay counts from here)
  loop.reset();
  for (const c of joinedHeadless) {
    if (SUBSCRIBE) c.chain.subscribe("$world");
    await sleep(9);
    c.timer = setInterval(() => {
      if (obsPhase >= L.DONE_PHASE) return;
      const now = Date.now();
      c.clock.observe(SUBSCRIBE ? Math.max(c.chain.latestTick(), obsTick) : obsTick, now);
      const s = L.nextStart(c.clock.est(now), c.cursor, LEAD, obsLead, c.clock.seen);
      if (s === null) { c.held++; return; }
      c.chain.sendInputs(L.encodeBatch(c.slot, s, [c.next(), c.next(), c.next()]));
      c.cursor = s + L.BATCH;
      c.sent++;
    }, 100);
  }
  const drainer = setInterval(() => clients.forEach(drain), 200);
  const tStart = Date.now();
  let lastLog = 0;
  while (Date.now() - tStart < MATCH_TIMEOUT_S * 1000) {
    if (obsPhase >= L.DONE_PHASE && samples.length && samples[samples.length - 1].phase >= L.DONE_PHASE) break;
    const ar = await readArena();
    if (ar && ar.matchAccount.toBase58() !== lastMatch) break; // the arena moved on
    if (Date.now() - lastLog > 15_000 && samples.length) {
      lastLog = Date.now();
      const s = samples[samples.length - 1];
      log({ ev: "progress", tick: s.tick, round: s.round, rphase: s.rphase, phase: s.phase, sent: joinedHeadless.reduce((p, c) => p + c.sent, 0), loopP99Ms: Math.round(loop.percentile(99) / 1e6) });
    }
    await sleep(1000);
  }
  for (const c of clients) { if (c.timer) clearInterval(c.timer); c.timer = undefined; }
  await sleep(6000); // the last confirms (confirmTimeoutMs 5000)
  clearInterval(drainer);
  clients.forEach(drain);
  for (const c of joinedHeadless) c.chain.unsubscribe("$world");

  // 6. metrics
  const ms = joinedHeadless.flatMap((c) => c.confirmMs);
  const counts = { sent: 0, ok: 0, err: 0, timeout: 0, window: 0, sendErr: 0 };
  for (const c of joinedHeadless) { counts.sent += c.sent; counts.ok += c.ok; counts.err += c.err; counts.timeout += c.timeout; counts.window += c.window; counts.sendErr += c.sendErr; }
  const confirm = L.confirmSummary(ms, counts);
  const rounds = L.roundStats(samples, joinedHeadless.map((c) => c.slot));
  const humans = popc(arun?.humanMask ?? 0);
  const loopP99 = loop.percentile(99) / 1e6;
  const v = L.verdict(rounds, confirm.p95 ?? NaN, humans, { ...L.DEFAULT_CRITERIA, expectedHumans: N + BROWSERS }, loopP99 > 50);
  const wallS = samples.length > 1 ? (samples[samples.length - 1].t - samples[0].t) / 1000 : 0;
  const rec = {
    match: lastMatch, matchId: matchHex, reserved,
    joined: { humanMask: arun?.humanMask ?? null, humans, expected: N + BROWSERS, headless: clients.map((c) => ({ i: c.i, slot: c.slot, joined: c.joined, joinMs: c.joinMs, error: c.joinError })) },
    rounds, confirm,
    inputTxPerS: wallS ? Math.round((counts.sent / wallS) * 10) / 10 : null,
    worldExecPerS: wallS ? Math.round(((samples[samples.length - 1].exec - samples[0].exec) / wallS) * 10) / 10 : null,
    slotMs: L.slotMs(samples),
    perClient: joinedHeadless.map((c) => ({ i: c.i, slot: c.slot, sent: c.sent, held: c.held, ok: c.ok, err: c.err, timeout: c.timeout, sendErr: c.sendErr, p50: L.pct(c.confirmMs, 50), p95: L.pct(c.confirmMs, 95), errs: c.errs, stats: JSON.parse(c.chain.stats()) })),
    toolEventLoopMs: { p50: loop.percentile(50) / 1e6, p99: loopP99, max: loop.max / 1e6, saturated: loopP99 > 50 },
    verdict: v,
    samples: samples.map((s) => [s.t, s.tick, s.round, s.rphase, s.phase, s.exec, s.notDue, s.ticksRun, s.lastSlot]),
  };
  loop.reset();
  report.matches.push(rec);
  save();
  log({ ev: "match", match: lastMatch, rounds: rounds.map((r) => ({ r: r.round, rt: r.realTime, exec: r.execPerS, lag: r.lagMaxTicks, applied: r.appliedRatio })), confirm: { p50: confirm.p50, p95: confirm.p95, okRatio: confirm.okRatio }, humans, verdict: v.pass, inconclusive: v.inconclusive });
}

observing = false;
await observer;
for (const c of clients) c.chain.stop();
report.doneAt = new Date().toISOString();
report.pass = report.matches.length === MATCHES && report.matches.every((m: any) => m.verdict.pass);
save();
log({ ev: "done", out: OUT, pass: report.pass });
process.exit(report.pass ? 0 : 1);

function popc(m: number) { return L.popcount(m); }
