// P5 keeper daemon (one per arena; hybrid mode): runs forever beside one Classic game server.
//   - tails the server's match logs (shared volume, LOG_DIR/match-<id>.jsonl) and drives each match on-chain:
//     create + delegate the Match → open_lobby → open_markets (+ the labelled market maker) → VRF deal → rounds 1–3 from
//     the jsonl (chain worker) → start_final → timed task + stall restarts → settle + pay → receipt → capsule VRF →
//     end_sim → finalize (receipt commit + undelegate; the Magic Action mints when ACTION=1) → mint_rewards retry;
//   - every stage checks on-chain state first (idempotent), so after a crash / restart it RESUMES FROM ON-CHAIN STATE:
//     the arena's current Match is finished (settle → receipt → finalize → mint) before a new one starts (P5 0.5);
//   - archives each match (ARCHIVE/<match>/: world.bin, the jsonl, keeper.jsonl, summary.json, econ.json) for the indexer;
//   - serves GET /status (arena + keeper stage + the server's status.json) and GET /health on STATUS_PORT.
//   harden (2026-11-25): wallet balances + lowBalance and the rollup's MagicBlock version in /status; treasury top-up
//   before a minting finalize; ensureFinal (no non-final Match left with bets; MarketBusy recovery); runFinal cleans up
//   in a finally; orphaned full matches abort; seasons pause at the commit cap. Decisions: policy.ts (+ policy.test.ts).
//   P2_NET=devnet ARENA=2 LOG_DIR=/logs/gs1 ARCHIVE=/archive STATUS_PORT=3111 node --experimental-strip-types tools/keeper/daemon.ts
import { PublicKey, Transaction, SystemProgram } from "@solana/web3.js";
import { createServer } from "node:http";
import { mkdirSync, appendFileSync, writeFileSync, readFileSync, existsSync, readdirSync, statSync, copyFileSync, renameSync } from "node:fs";
import { join } from "node:path";
import { header, bridgeRounds13, readLines } from "../chain-worker/worker.ts";
import { NET, EP, base, er, wallet, pb, pe, pda, key, sendBase, sendEr, PROGRAM_ID, MAGIC_CONTEXT, N, sleep, decodeWorld, tickAccounts, log as logTo, cu, pct, BN } from "./lib.ts";
import * as E from "./econ.ts";
import * as S from "./p6.ts";
import { sampleCu } from "./cusample.ts";
import { startCrank } from "./crank.ts";
import * as P from "./policy.ts";

const ARENA = Number(process.env.ARENA ?? 2);
const LOG_DIR = process.env.LOG_DIR ?? "/logs";
const ARCHIVE = process.env.ARCHIVE ?? "/archive";
const STATUS_PORT = Number(process.env.STATUS_PORT ?? 3111);
const INTERVAL_MS = Number(process.env.INTERVAL_MS ?? 11);
const STALL_MS = Number(process.env.STALL_MS ?? 500);
const POLL_MS = Number(process.env.POLL_MS ?? 200);
const DRIVE_MS = Number(process.env.DRIVE_MS ?? 6000);
const WAIT_HUMANS_S = Number(process.env.WAIT_HUMANS_S ?? 4);
const MM = process.env.MM !== "0";
const ACTION = process.env.ACTION === "1"; // P5 0.3: finalize with the post-undelegate Magic Action (mint_rewards)
const ACTION_WAIT_S = Number(process.env.ACTION_WAIT_S ?? 60);
const SEALED = process.env.SEALED !== "0"; // P6: sealed Champion bets (escrow + private book on the TEE)
const SEASONS = process.env.SEASONS !== "0"; // P6: record finalized matches into the seasons
// harden #2: the 60 s wait for the committed market copy on Solana (only when commit_econ landed)
const SEASON_COPY_WAIT_S = Number(process.env.SEASON_COPY_WAIT_S ?? 60);
// harden #1: treasury top-up before a finalize that mints (target), low-balance thresholds (lamports), the guest funder
// to watch (pubkey only; its key stays in the indexer), balance poll period
// review: the default top-up target sits above the 0.1 SOL low threshold (60M left the flag on after every top-up)
const TREASURY_TOPUP_LAMPORTS = Number(process.env.TREASURY_TOPUP_LAMPORTS ?? P.TREASURY_TOPUP_DEFAULT);
const LOW = {
  authority: Number(process.env.LOW_AUTHORITY_LAMPORTS ?? P.LOW_DEFAULTS.authority),
  treasury: Number(process.env.LOW_TREASURY_LAMPORTS ?? P.LOW_DEFAULTS.treasury),
  funder: Number(process.env.LOW_FUNDER_LAMPORTS ?? P.LOW_DEFAULTS.funder),
};
const FUNDER_PUBKEY = (() => { try { return process.env.FUNDER_PUBKEY ? new PublicKey(process.env.FUNDER_PUBKEY) : null; } catch { return null; } })();
const BALANCE_POLL_MS = Number(process.env.BALANCE_POLL_MS ?? 60_000);
// harden #5: in full mode the stall path only re-schedules the tasks (the crank drives the World); 1 = the old tick() flood
const STALL_FLOOD_FULL = process.env.STALL_FLOOD_FULL === "1";
// harden #6: the Classic server no longer plays this full on-chain match for this long → abort ours (reason 8)
const ORPHAN_MS = Number(process.env.ORPHAN_MS ?? 10_000);
const KILL_AT = process.env.KILL_AT ?? ""; // test hook (P5 0.5): exit(9) right after this stage
// fullchain (docs/proof/fullchain/CONTRACT.md): ONCHAIN_ALL=1 + the server's header `onchainAll` → the whole match on
// the rollup (start_sim: the program computes all 4 rounds); off (default) = the hybrid flow, unchanged
const ONCHAIN_ALL = process.env.ONCHAIN_ALL === "1";
const FULL_TASKS = Number(process.env.FULL_TASKS ?? 2);
const CRANK_RATE = Number(process.env.CRANK_RATE ?? 50);
const ROTATE_MS = Number(process.env.ROTATE_MS ?? 8000);
const FULL_WAIT_HUMANS_S = Number(process.env.FULL_WAIT_HUMANS_S ?? 20); // a first join (session + gas) measured 8.5 s on production
const DONE = 11;
const BOOT = Date.now();
const STATE_FILE = join(ARCHIVE, `keeper-${NET}-arena${ARENA}.json`);
mkdirSync(ARCHIVE, { recursive: true });
const LOG = join(ARCHIVE, `keeper-${NET}-arena${ARENA}.jsonl`);
const log = (o: object) => { try { logTo(LOG, { arena: ARENA, ...o }); } catch {} };

const arena = pda.arena(ARENA);
const world = pda.world(arena);
const st: any = existsSync(STATE_FILE) ? JSON.parse(readFileSync(STATE_FILE, "utf8")) : { done: [] as string[] };
const saveState = () => { try { writeFileSync(STATE_FILE + ".tmp", JSON.stringify(st, null, 1)); renameSync(STATE_FILE + ".tmp", STATE_FILE); } catch (e) { log({ ev: "state_save_failed", err: String(e).slice(0, 200) }); } };
/** harden #7: a match is done for the main loop once finalized (or given up); `active` is cleared with it */
const markDone = (hex: string) => { if (!st.done.includes(hex)) { st.done.push(hex); st.done = st.done.slice(-500); } if (st.active?.matchHex === hex) delete st.active; saveState(); };
const status: any = { arena: ARENA, arenaKey: arena.toBase58(), world: world.toBase58(), net: NET, stage: "boot", since: Date.now(), boot: BOOT, last: [] as any[],
  authority: wallet.publicKey.toBase58(), treasury: E.epda.treasury().toBase58(), funder: FUNDER_PUBKEY?.toBase58() ?? null, seasons: SEASONS, seasonsPaused: !SEASONS };
const stage = (s: string, extra: object = {}) => {
  Object.assign(status, { stage: s, since: Date.now(), ...extra });
  log({ stage: s, ...extra });
  console.log(`[keeper a${ARENA}] ${new Date().toISOString()} ${s} ${JSON.stringify(extra).slice(0, 200)}`);
  if (KILL_AT && s === KILL_AT) { log({ act: "kill_hook", at: s }); console.log(`KILL_AT ${s}: exiting`); process.exit(9); }
};

// ------------------------------------------------------------------ status / health
const STAGE_REST = new Set(["idle", "boot", "match_failed", "resume_failed"]);
const serverStatus = () => { try { return JSON.parse(readFileSync(join(LOG_DIR, "status.json"), "utf8")); } catch { return null; } };
createServer((req, res) => {
  const body = req.url?.startsWith("/health")
    // beta audit: the heartbeat is a timer, so it ticks while the main loop is stuck on a hung RPC: a match stage
    // (a whole match is ~8 min) that has not moved for 25 min is unhealthy too (the watchdog then restarts the keeper,
    // which resumes or aborts + refunds the match)
    ? { ok: Date.now() - (status.heartbeat ?? BOOT) < 30_000 && (STAGE_REST.has(status.stage) || Date.now() - status.since < 25 * 60_000), stage: status.stage, since: status.since, heartbeat: status.heartbeat }
    : { ...status, server: serverStatus(), now: Date.now() };
  res.writeHead(200, { "content-type": "application/json", "cache-control": "no-store" });
  res.end(JSON.stringify(body));
}).listen(STATUS_PORT, "0.0.0.0");
setInterval(() => { status.heartbeat = Date.now(); }, 2000);

// harden #1: the wallets the game spends from (authority: create/delegate per match; treasury: Core asset rent; the guest
// funder when FUNDER_PUBKEY is set) in /status, with lowBalance flags + a log line when one turns low (no transfers here)
async function pollBalances() {
  const get = (k: PublicKey | null) => (k ? base.getBalance(k, "confirmed").catch(() => -1) : Promise.resolve(null));
  const [authorityLamports, treasuryLamports, funderLamports] = [await get(wallet.publicKey), await get(E.epda.treasury()), await get(FUNDER_PUBKEY)];
  const b = { authorityLamports, treasuryLamports, funderLamports };
  const low = P.lowBalance(b, LOW);
  const was = status.lowBalance ?? {};
  for (const w of ["authority", "treasury", "funder"] as const) if (low[w] && !was[w]) { log({ ev: "low_balance", wallet: w, lamports: (b as any)[`${w}Lamports`], below: LOW[w] }); console.log(`[keeper a${ARENA}] LOW BALANCE ${w}: ${(b as any)[`${w}Lamports`]} lamports`); }
  Object.assign(status, b, { lowBalance: low, balancesAt: Date.now() });
}
pollBalances().catch(() => {});
setInterval(() => { pollBalances().catch(() => {}); }, BALANCE_POLL_MS);

// harden #11: the rollup's version (MagicBlock core) in /status; a log alert when it changes after boot
async function erVersion(): Promise<any> {
  const r = await fetch(EP.er, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ jsonrpc: "2.0", id: 1, method: "getVersion" }), signal: AbortSignal.timeout(8000) });
  return ((await r.json()) as any).result ?? null;
}
async function pollVersion() {
  const v = await erVersion().catch(() => null);
  if (!v) return;
  const core = P.mbCore(v);
  status.erVersion = v; status.magicblockCore = core;
  if (status.magicblockCoreBoot === undefined) { status.magicblockCoreBoot = core; log({ ev: "er_version", core, raw: v }); }
  else if (core !== status.magicblockCoreBoot && !status.erVersionChanged) { status.erVersionChanged = true; log({ ev: "er_version_changed", boot: status.magicblockCoreBoot, now: core, raw: v }); console.log(`[keeper a${ARENA}] ALERT: rollup version changed ${status.magicblockCoreBoot} -> ${core}`); }
}
pollVersion().catch(() => {});
setInterval(() => { pollVersion().catch(() => {}); }, 10 * 60_000);

// ------------------------------------------------------------------ helpers
const act = async (what: string, f: () => Promise<any>, logFile?: string) => {
  const t0 = Date.now();
  try {
    const r = await f();
    const o = { act: what, ms: Date.now() - t0, ok: !r?.err, sig: typeof r === "string" ? r : r?.sig, err: r?.err };
    log(o); if (logFile) logTo(logFile, o);
    if (r?.err) throw new Error(`${what}: ${r.err}`);
    return r;
  } catch (e: any) {
    const o = { act: what, ms: Date.now() - t0, ok: false, err: String(e?.message ?? e).slice(0, 400) };
    log(o); if (logFile) logTo(logFile, o);
    throw e;
  }
};
const statusName = (m: any) => Object.keys(m.status)[0];
async function erMatch(matchPda: PublicKey) { return pe.account.match.fetch(matchPda).catch(() => null) as Promise<any>; }
async function baseOwner(k: PublicKey) { for (let i = 0; i < 6; i++) { try { const i2 = await base.getAccountInfo(k, "confirmed"); return i2 ? i2.owner : null; } catch { await sleep(1500 * (i + 1)); } } return null; }
async function arenaAcc(): Promise<any> { for (;;) { try { return await pe.account.arena.fetch(arena); } catch (e) { log({ ev: "arena_read_failed", err: String(e).slice(0, 200) }); await sleep(2000); } } }
const ZERO = PublicKey.default.toBase58();

/** The market maker (labelled "mm"): seeds each market once its window opens. */
function mmWatcher(matchPda: PublicKey, tiers: (string | undefined)[], mlog: any[], judge = false, full = false, humanSlots: number[] | null = null) {
  const mm = key("mm");
  let stopped = false;
  const done = new Set<string>();
  const seed = async (tag: string, k: number, slots: number[]) => {
    if (done.has(tag)) return;
    done.add(tag);
    const r = await E.mmSeed(mm, ARENA, k, matchPda, slots, tiers, k === 0 ? 1n : 2n);
    mlog.push({ tag, k, sigs: r.sigs.length, errs: r.errs });
  };
  const loop = (async () => {
    while (!stopped) {
      try {
        const m: any = await erMatch(matchPda);
        if (m) {
          const s = statusName(m);
          const alive = m.slots.map((x: any, i: number) => (x.state === 1 ? i : -1)).filter((i: number) => i >= 0 && i < m.playerCount);
          // judge mode: rounds 1–3 are fast-forwarded (not played), so the qualify markets get no seeded pools.
          // harden #7 (policy.mmSeeds): Qualify(2/3) only with ≥ 2 alive and a human alive; FinalWinner in full mode too
          const finalMode = (m.flags & 0x40) !== 0 || (full && (m.flags & 0x20) !== 0);
          // review: the World is read only near the Final (full mode sets 0x20 for the whole match: no 2.5 req/s from R1)
          const wi = finalMode && !done.has("final") && ((m.flags & 0x40) !== 0 || m.currentRound >= 3) ? await er.getAccountInfo(world, "processed") : null;
          const plan = P.mmSeeds({ status: s, currentRound: m.currentRound, flags: m.flags, alive }, { judge, full, humanSlots, worldRound: wi?.data[139], worldPhase: wi?.data[199] });
          for (const x of plan) await seed(x.tag, x.k, alive);
        }
      } catch {}
      await sleep(400);
    }
  })();
  return { stop: async () => { stopped = true; await loop; } };
}
async function ensureMm() {
  const mm = key("mm");
  const bal = await base.getBalance(mm.publicKey).catch(() => 0);
  if (bal < 20_000_000) await sendBase([SystemProgram.transfer({ fromPubkey: wallet.publicKey, toPubkey: mm.publicKey, lamports: 30_000_000 - bal })], [wallet], "fund mm");
  const c = await E.chipsOf(mm.publicKey);
  if (!c || c.balance < 200n * E.ONE) await E.ensureChips(mm);
}

// ------------------------------------------------------------------ P6 sealed Champion bets
/** Close (program decides: the Champion window) → the book public (TEE) → reveal every commit from it → finish once
 * all are revealed. Idempotent; called by the watcher (fast path, at the Final's RUSH tick) and again before settle. */
async function sealedStep(matchPda: PublicKey, mlog: string, out: any, force = false): Promise<boolean> {
  const e = await S.escrow(ARENA);
  if (!e || !e.match.equals(matchPda)) return true;
  out.sealed ??= {};
  if (e.state === 1) {
    const r = await S.sealedClose(ARENA, matchPda);
    if (r.err) return false; // window still open
    out.sealed.closedAt = Date.now(); out.sigs.sealedClose = r.sig;
    logTo(mlog, { act: "sealed_close", sig: r.sig, n: e.n });
    stage("sealed_reveal", { n: e.n });
  }
  const e2 = (await S.escrow(ARENA))!;
  if (e2.state === 2) {
    if (!out.sealed.bookClosed) {
      const b = await S.bookClose(ARENA).catch((x) => ({ sig: "", ms: 0, err: String(x) }));
      logTo(mlog, { act: "book_close", sig: b.sig, ms: b.ms, err: b.err });
      if (!b.err) { out.sealed.bookClosed = Date.now(); out.sigs.bookClose = b.sig; }
    }
    if (e2.nRevealed < e2.n) {
      const rv = await S.revealFromBook(ARENA, matchPda, mlog).catch((x) => ({ revealed: 0, sigs: [], unmatched: -1, err: String(x) }));
      out.sealed.reveals = [...(out.sealed.reveals ?? []), ...rv.sigs];
      logTo(mlog, { act: "reveal", ...rv });
    }
    const e3 = (await S.escrow(ARENA))!;
    if (e3.nRevealed === e3.n && !out.sealed.revealedAt) out.sealed.revealedAt = Date.now();
    if (e3.nRevealed === e3.n || force) {
      const f = await S.sealedFinish(ARENA, matchPda);
      logTo(mlog, { act: "sealed_finish", sig: f.sig, err: f.err });
      if (!f.err) out.sigs.sealedFinish = f.sig;
      return !f.err;
    }
    return false;
  }
  return e2.state === 3;
}
function sealedWatcher(matchPda: PublicKey, mlog: string, out: any) {
  let stopped = false;
  const loop = (async () => {
    while (!stopped) {
      try {
        const m: any = await erMatch(matchPda);
        const wi = await er.getAccountInfo(world, "processed");
        const final = m && (m.flags & 0x40) !== 0 && wi && wi.data[139] === 4 && wi.data[199] >= 3;
        if (m && (final || m.currentRound >= 4 || ["completed", "aborted"].includes(statusName(m)))) {
          if (await sealedStep(matchPda, mlog, out)) break;
        }
      } catch {}
      await sleep(300);
    }
  })();
  return { stop: async () => { stopped = true; await loop; } };
}

// ------------------------------------------------------------------ the Final: timed task + watch (+ resume)
/** fullchain: the Classic server's last `{"type":"onchainAll","state":…}` line for this match (live / fallback / done /
 * aborted). `fallback` = the server gave up on the rollup and plays the match on Classic: the keeper aborts its side. */
function serverAllState(jsonl: string | null): string | null {
  if (!jsonl) return null;
  const xs = readLines(jsonl).filter((l) => l.type === "onchainAll");
  return xs.length ? String(xs[xs.length - 1].state) : null;
}

/** harden #4: never leave a non-final Match with bets (policy.ensureFinalCore): stop the arena when it runs this match
 * (only with `force`: a server fallback / orphan / deadline / a new match taking the arena), then abort_match(reason),
 * both retried with backoff and never swallowed. Afterwards finish() refunds (settle), writes the receipt, finalizes. */
async function ensureFinal(matchPda: PublicKey, reason: number, force: boolean, mlog?: string) {
  const r = await P.ensureFinalCore({
    arena: async () => {
      const a: any = await arenaAcc();
      const runsThis = a.matchAccount.toBase58() === matchPda.toBase58();
      const wi = a.status === 2 && runsThis ? await er.getAccountInfo(world, "processed").catch(() => null) : null;
      return { running: a.status === 2, runsThis, phase: wi ? decodeWorld(wi.data as Buffer).phase : 0 };
    },
    matchStatus: async () => { const m = await erMatch(matchPda); return m ? statusName(m) : null; },
    abortArena: async () => { const r = await sendEr([await pe.methods.abortArena().accountsPartial({ authority: wallet.publicKey, arena, world }).instruction()], [wallet], "abort_arena"); if (r.err) throw new Error(r.err); if (mlog) logTo(mlog, { act: "abort_arena", sig: r.sig }); return r; },
    abortMatch: async (why) => { const r = await sendEr([await pe.methods.abortMatch(why).accountsPartial({ authority: wallet.publicKey, matchAccount: matchPda }).instruction()], [wallet], "abort_match"); if (r.err) throw new Error(r.err); if (mlog) logTo(mlog, { act: "abort_match", reason: why, sig: r.sig }); return r; },
    sleep,
    log: (o) => { log({ match: matchPda.toBase58(), ...o }); if (mlog) logTo(mlog, o); },
  }, reason, force);
  log({ ev: "ensure_final", match: matchPda.toBase58(), reason, force, result: r });
  return r;
}

async function runFinal(matchPda: PublicKey, maps: PublicKey[], mdir: string, mlog: string, full = false, gaveUp: () => string | false = () => false, firstStartedAt: number | null = null) {
  const tickMetas = tickAccounts(arena, matchPda, maps);
  const schedule = async () => act("schedule_ticks", async () => sendEr([cu(400_000), await pe.methods.scheduleTicks(new BN(INTERVAL_MS), new BN(2_000_000)).accountsPartial({ payer: wallet.publicKey, arena }).remainingAccounts(tickMetas).instruction()], [wallet], "schedule_ticks"), mlog);
  const cancel = async (taskId: any) => act("cancel_ticks", async () => sendEr([await pe.methods.cancelTicks(taskId).accountsPartial({ payer: wallet.publicKey }).remainingAccounts([{ pubkey: MAGIC_CONTEXT, isSigner: false, isWritable: true }]).instruction()], [wallet], "cancel_ticks", 3), mlog);
  const scheduleOne = async () => { await schedule(); return (await arenaAcc()).taskId.toString() as string; };
  // harden #5: everything started here (tasks, the World subscription, the crank) is stopped in the finally below,
  // whatever throws: a retry never runs a second crank / appender next to a leaked one
  const tasks: string[] = [];
  let sub: number | null = null;
  let crank: ReturnType<typeof startCrank> | null = null;
  let crankStats: any = null;
  let w: any;
  let liveProg = "", serverFellBack = false, gaveUpWhy: string | false = false, timedOut = false;
  let restarts = 0;
  const cus: number[] = [];
  const tickAt: [number, number, number][] = [];
  try {
    // resume: a task left by an earlier keeper process may still run (or be dead): cancel it, then schedule ours
    const a0: any = await arenaAcc();
    if (a0.taskId && a0.taskId.toString() !== "0") await cancel(a0.taskId).catch(() => {});
    tasks.push(await scheduleOne());
    // fullchain: N parallel timed tasks + the keeper crank (crank(budget), 1.4M CU) + task rotation + per-round commits
    for (let k = 1; k < (full ? FULL_TASKS : 1); k++) tasks.push(await scheduleOne());
    let rotatedAt = Date.now(), lastGaveUpCheck = 0;
    const committed = new Set<number>();
    let lastSnapKey = "";
    const record = (buf: Buffer) => {
      const w = decodeWorld(buf);
      if (w.partPending === 0 && (tickAt.length === 0 || w.tick > tickAt[tickAt.length - 1][0])) tickAt.push([w.tick, Date.now(), Number(w.lastSlot)]);
      const k = `${w.tick}:${w.partPending}`;
      liveProg = k;
      if (k === lastSnapKey) return w;
      lastSnapKey = k;
      const h = Buffer.alloc(7); h.writeUInt32LE(w.tick, 0); h[4] = w.partPending; h.writeUInt16LE(w.len, 5);
      // world2.bin (P5 replay): [u32 tick][u8 part][u16 len][80 B WorldAcc header @128..208: tick, len, phase, cur[12]
      // (the inputs latched for this tick), cur_some, serial…][World bytes]
      appendFileSync(join(mdir, "world2.bin"), Buffer.concat([h, buf.subarray(128, 208), w.world]));
      return w;
    };
    sub = er.onAccountChange(world, (a) => { try { record(a.data as Buffer); } catch {} }, { commitment: "processed" } as any);
    w = decodeWorld((await er.getAccountInfo(world))!.data as Buffer);
    liveProg = `${w.tick}:${w.partPending}`;
    crank = full ? startCrank({ er, wallet, rate: CRANK_RATE, progress: () => liveProg, log: (o) => logTo(mlog, o),
      ix: (b) => pe.methods.crank(b).remainingAccounts(tickMetas).instruction() }) : null;
    let lastProg = "", lastExecAt = Date.now(), lastCu = 0;
    const t0 = Date.now();
    // harden #4: the deadline counts from the match's first start (persisted across keeper restarts), so it fires
    // before the 25-min /health limit and the abort path runs instead of an endless restart-resume loop
    const deadline = P.finalDeadline(t0, full, firstStartedAt);
    while (w.phase < DONE) {
      await sleep(POLL_MS);
      const now = Date.now();
      if (full && now - lastGaveUpCheck > 2000) {
        lastGaveUpCheck = now;
        const why = gaveUp();
        if (why) { gaveUpWhy = why; log({ ev: why === "fallback" ? "server_fallback" : "server_abandoned", tick: w.tick }); logTo(mlog, { ev: why === "fallback" ? "server_fallback" : "server_abandoned", tick: w.tick }); serverFellBack = true; break; }
      }
      if (now > deadline) { log({ ev: "final_timeout", firstStartedAt }); logTo(mlog, { ev: "final_timeout", firstStartedAt }); timedOut = true; break; }
      const info = await er.getAccountInfo(world, "processed").catch(() => null);
      if (!info) continue;
      w = record(info.data as Buffer);
      status.worldTick = w.tick; status.worldPhase = w.phase; status.worldRound = w.round;
      const prog = `${w.tick}:${w.partPending}`;
      if (prog !== lastProg) { lastProg = prog; lastExecAt = now; }
      if (full && now - rotatedAt > ROTATE_MS) {
        // a task that hit an over-budget execution is gone for good: keep FULL_TASKS fresh ones running
        rotatedAt = now;
        const old = tasks.splice(0);
        for (let k = 0; k < FULL_TASKS; k++) { try { tasks.push(await scheduleOne()); } catch {} }
        for (const t of old) await cancel(new BN(t)).catch(() => {});
      }
      if (full && w.round >= 1 && !committed.has(w.round) && (w.phase === 7 || w.phase === 8 || w.phase >= 9)) {
        // each resolved round's results to Solana (commit_round: once per round, any payer)
        committed.add(w.round);
        const r = w.round;
        act(`commit_round_${r}`, async () => sendEr([await pe.methods.commitRound(r).accountsPartial({ payer: wallet.publicKey, matchAccount: matchPda }).instruction()], [wallet], "commit_round", 10), mlog).catch(() => committed.delete(r));
      }
      if (now - lastExecAt > STALL_MS) {
        restarts++;
        log({ ev: "stall", ms: now - lastExecAt, tick: w.tick });
        // harden #5 (policy.stallRecover): never throws; full mode only re-schedules (the crank keeps driving)
        const ixTick = !full || STALL_FLOOD_FULL ? await pe.methods.tick().remainingAccounts(tickMetas).instruction().catch(() => null) : null;
        const rs = await P.stallRecover({
          cancel: (t) => cancel(new BN(t)),
          schedule: scheduleOne,
          blockhash: async () => (await er.getLatestBlockhash("processed")).blockhash,
          sendTick: (bh, nonce) => {
            const tx = new Transaction().add(cu(1_400_000 - (nonce % 64)), ixTick!);
            tx.feePayer = wallet.publicKey; tx.recentBlockhash = bh; tx.sign(wallet);
            er.sendRawTransaction(tx.serialize(), { skipPreflight: true, maxRetries: 0 }).catch(() => {});
          },
          gaveUp: () => (full ? !!gaveUp() : false),
          sleep,
          log: (o) => logTo(mlog, o),
        }, tasks, { nTasks: full ? FULL_TASKS : 1, flood: !!ixTick, driveMs: DRIVE_MS });
        if (rs.bhErrors || rs.scheduled < (full ? FULL_TASKS : 1)) logTo(mlog, { ev: "stall_recover", ...rs });
        lastExecAt = Date.now();
      }
      if (now - lastCu > 15_000) { lastCu = now; sampleCu(world).then((xs) => cus.push(...xs.map((x) => x.cu))).catch(() => {}); }
    }
  } finally {
    crankStats = await P.cleanupFinal({
      crank,
      unsubscribe: async () => { if (sub != null) await er.removeAccountChangeListener(sub); },
      tasks,
      cancel: (t) => cancel(new BN(t)),
      log: (o) => logTo(mlog, o),
    });
    if (crankStats) logTo(mlog, { ev: "crank_stats", ...crankStats });
  }
  const g: number[] = [];
  for (let i = 1; i < tickAt.length; i++) g.push((tickAt[i][1] - tickAt[i - 1][1]) / Math.max(1, tickAt[i][0] - tickAt[i - 1][0]));
  const span = tickAt.length > 1 ? { ticks: tickAt[tickAt.length - 1][0] - tickAt[0][0], wallMs: tickAt[tickAt.length - 1][1] - tickAt[0][1] } : { ticks: 0, wallMs: 1 };
  writeFileSync(join(mdir, "ticks.json"), JSON.stringify(tickAt));
  return { full, serverFellBack, gaveUp: gaveUpWhy, timedOut, crank: crankStats, complete: w.phase >= DONE, ticks: w.tick, restarts, realTime: span.ticks / 30 / (span.wallMs / 1000), gapP95: pct(g, 95), cu: { n: cus.length, p95: pct(cus, 95), max: cus.length ? Math.max(...cus) : 0 } };
}

// ------------------------------------------------------------------ after the Final: settle → receipt → finalize → mint
async function finish(matchPda: PublicKey, maps: PublicKey[], mdir: string, mlog: string, out: any) {
  let m: any = await erMatch(matchPda);
  if (m && !["completed", "aborted"].includes(statusName(m))) {
    // harden #4: a non-final Match the arena no longer runs (VRF timeout, a swallowed abort, a stranded earlier match)
    // is aborted here, so its markets refund and finalize can run; a running World (phase < DONE) is never touched
    stage("ensure_final", { match: matchPda.toBase58(), status: statusName(m) });
    const r = await ensureFinal(matchPda, 7, false, mlog);
    out.ensureFinal = r;
    m = await erMatch(matchPda);
  }
  if (m && ["completed", "aborted"].includes(statusName(m))) {
    if (SEALED) {
      // P6: close → reveal → finish (unrevealed commits wait the 20 s reveal window, then forfeit / refund)
      stage("sealed_finish");
      for (const ts = Date.now(); Date.now() - ts < (S.REVEAL_WINDOW_S + 40) * 1000;) {
        const e = await S.escrow(ARENA).catch(() => null);
        const late = e && e.closedAt > 0 && Date.now() / 1000 > e.closedAt + S.REVEAL_WINDOW_S + 1;
        if (await sealedStep(matchPda, mlog, out, !!late).catch(() => false)) break;
        await sleep(1000);
      }
      const e = await S.escrow(ARENA).catch(() => null);
      if (e && e.match.equals(matchPda)) out.sealed = { ...(out.sealed ?? {}), n: e.n, revealed: e.nRevealed, forfeits: e.nForfeit, state: e.state, pool: e.pool.toString(), forfeit: e.forfeit.toString(), entries: e.entries.map((x) => ({ wallet: x.wallet.toBase58(), amount: x.amount.toString(), hash: x.hash, outcome: x.outcome, state: x.state })) };
      const bk = await S.readBook(ARENA).catch(() => null);
      if (bk && bk.match.equals(matchPda) && bk.closed) out.sealed.book = bk.entries.map((x) => ({ wallet: x.wallet.toBase58(), amount: x.amount.toString(), outcome: x.outcome, salt: x.salt.toString("hex") }));
    }
    stage("settle", { match: matchPda.toBase58() });
    out.settle = {};
    for (let k = 0; k < E.MARKETS; k++) {
      try { out.settle[k] = (await act(`settle_${k}`, async () => E.settleAndPay(ARENA, k, matchPda), mlog)).length; } catch (e: any) { out.settle[k] = { err: String(e?.message ?? e).slice(0, 200) }; }
      const mk = await E.market(ARENA, k);
      if (mk && mk.match.equals(matchPda)) out[`market${k}`] = { pool: mk.pool.toString(), winners: mk.winners, winningStake: mk.winningStake.toString(), label: mk.label, status: mk.status, refund: mk.refund, paidTotal: mk.paidTotal.toString(), bets: mk.bets.map((b) => ({ wallet: b.wallet.toBase58(), outcome: b.outcome, amount: b.amount.toString(), paid: b.paid })) };
    }
    const dq = await er.getAccountInfo(E.epda.drops(arena)).catch(() => null);
    if (dq) { const d = E.decodeDrops(dq.data as Buffer); if (d.match.equals(matchPda)) out.drops = { count: d.count, pot: d.pot.toString(), buys: d.buys.map((b) => ({ by: b.by.toBase58(), tick: b.tick, kind: b.kind })) }; }
    let rc = await E.matchReceipt(er, matchPda);
    if (rc && !(rc.flags & 1)) {
      stage("receipt");
      // an aborted match finalizes without a receipt (lib.rs finalize: the receipt is required for Completed only)
      try { out.sigs.writeReceipt = (await act("write_receipt", async () => E.writeReceipt(ARENA, matchPda, maps), mlog)).sig; }
      catch (e: any) { if (statusName(m) !== "aborted") throw e; out.receiptError = String(e?.message ?? e).slice(0, 300); }
    }
    if (SEASONS && !status.seasonsPaused && rc && !(out.sigs.commitEcon)) {
      // P6: the 5 markets + drop queue to Solana (the copies record_season checks against the receipt's bets hash)
      const c: any = await S.commitEcon(ARENA).catch((x) => ({ sig: "", err: String(x) }));
      logTo(mlog, { act: "commit_econ", sig: c.sig, err: c.err, failSig: c.failSig }); if (!c.err) out.sigs.commitEcon = c.sig; else out.commitEconError = { err: String(c.err).slice(0, 200), failSig: c.failSig ?? null };
    }
    rc = await E.matchReceipt(er, matchPda);
    m = await erMatch(matchPda);
    if (rc && statusName(m) === "completed" && rc.humanMask && !(rc.flags & (2 | 32))) {
      stage("capsules");
      out.sigs.requestCapsules = (await act("request_capsules", async () => E.requestCapsules(matchPda), mlog).catch((e) => ({ sig: "", err: String(e) }))).sig;
    }
    for (const tq = Date.now(); Date.now() - tq < 30_000;) { const r2 = await E.matchReceipt(er, matchPda); if (!r2 || !(r2.flags & 32) || r2.flags & 2) break; await sleep(400); }
  }
  const a: any = await arenaAcc();
  // harden #4: only the arena's own match is ended here (a recovered older match must never stop the current one)
  if (a.status === 2 && a.matchAccount.toBase58() === matchPda.toBase58()) {
    const wd = decodeWorld((await er.getAccountInfo(world))!.data as Buffer);
    // review: a World still running this match while its Match is not final (e.g. a transient null erMatch made
    // driveMatch skip runFinal) is never stopped here: throw, the caller retries and resumes it (an abort that is
    // wanted went through ensureFinal(force) first, which stops the arena)
    if (wd.phase < DONE && !(m && ["completed", "aborted"].includes(statusName(m)))) throw new Error("finish: the World still runs this match (not final): retry");
    if (wd.phase >= DONE) out.sigs.endSim = (await act("end_sim", async () => sendEr([await pe.methods.endSim().accountsPartial({ authority: wallet.publicKey, arena, world }).instruction()], [wallet], "end_sim"), mlog)).sig;
    else out.sigs.abortArena = (await act("abort_arena", async () => sendEr([await pe.methods.abortArena().accountsPartial({ authority: wallet.publicKey, arena, world }).instruction()], [wallet], "abort_arena"), mlog)).sig;
  }
  // finalize (receipt commit + undelegate) unless the Match is back on Solana already
  if (!(await baseOwner(matchPda))?.equals(PROGRAM_ID)) {
    stage("finalize");
    // harden #1: the Magic Action pays Core asset rent from the global treasury PDA: top it up first when it cannot
    // cover what this match owes; if the top-up fails, finalize plainly (the keeper's mint_rewards is the retry)
    // review: also before a plain finalize (the keeper's mint_rewards pays the same rent from the same treasury)
    let useAction = ACTION;
    const owed = P.owedCount(await E.matchReceipt(er, matchPda));
    if (owed > 0) {
      const tb = await base.getBalance(E.epda.treasury(), "confirmed").catch(() => -1);
      const top = P.treasuryTopUp(tb, owed, TREASURY_TOPUP_LAMPORTS);
      out.treasury = { before: tb, owed, topUp: top };
      if (tb < 0) { useAction = false; out.treasury.err = "treasury balance unreadable"; }
      else if (top > 0) {
        try { out.sigs.treasuryTopUp = await sendBase([SystemProgram.transfer({ fromPubkey: wallet.publicKey, toPubkey: E.epda.treasury(), lamports: top })], [wallet], "fund treasury"); logTo(mlog, { act: "treasury_topup", lamports: top, before: tb, owed, sig: out.sigs.treasuryTopUp }); }
        catch (e: any) { useAction = false; out.treasury.err = String(e?.message ?? e).slice(0, 200); logTo(mlog, { act: "treasury_topup", ok: false, err: out.treasury.err }); }
      }
    }
    await sleep(1000);
    const ix = useAction
      ? await pe.methods.finalizeMatchAction().accountsPartial({ authority: wallet.publicKey, matchAccount: matchPda }).instruction()
      : await pe.methods.finalizeMatch().accountsPartial({ authority: wallet.publicKey, matchAccount: matchPda }).instruction();
    try {
      out.sigs.finalize = (await act(useAction ? "finalize_match_action" : "finalize_match", async () => sendEr([cu(400_000), ix], [wallet], "finalize_match"), mlog)).sig;
      out.finalizeMode = useAction ? "action" : ACTION ? "plain-treasury" : "plain";
    } catch (e: any) {
      if (!useAction) throw e;
      // D-ACTION fallback: the plain finalize; the keeper's mint_rewards mints
      out.actionError = String(e?.message ?? e).slice(0, 300);
      out.sigs.finalize = (await act("finalize_match", async () => sendEr([await pe.methods.finalizeMatch().accountsPartial({ authority: wallet.publicKey, matchAccount: matchPda }).instruction()], [wallet], "finalize_match"), mlog)).sig;
      out.finalizeMode = "plain-fallback";
    }
  }
  const tf = Date.now();
  let onBase = false;
  for (; Date.now() - tf < 120_000;) { if ((await baseOwner(matchPda))?.equals(PROGRAM_ID)) { onBase = true; break; } await sleep(1500); }
  out.finalizeToBaseMs = Date.now() - tf;
  if (!onBase) throw new Error("finalize: the Match did not reach Solana in 120 s");
  // harden #7: finalized = done for the main loop (a restart during rewards / seasons never re-drives it)
  markDone(out.matchHex);
  stage("rewards");
  let rc = await E.matchReceipt(base, matchPda);
  const owedCount = P.owedCount;
  if (out.finalizeMode === "action" && owedCount(rc) > 0) {
    // the Magic Action runs mint_rewards on Solana after the undelegation: wait for it before any keeper retry
    const ta = Date.now();
    while (Date.now() - ta < ACTION_WAIT_S * 1000 && owedCount(rc) > 0) { await sleep(2000); rc = await E.matchReceipt(base, matchPda); }
    out.action = { waitedMs: Date.now() - ta, owedAfter: owedCount(rc), mintedMask: rc?.mintedMask, flags: rc?.flags };
  }
  if (rc && rc.flags & 1) out.rewards = await E.mintRewards(matchPda, mlog); // only what is still owed (idempotent on-chain)
  {
    const r2 = await E.matchReceipt(base, matchPda);
    out.assets = r2 ? [...(r2.flags & 4 ? [E.epda.crown(matchPda).toBase58()] : []), ...[...Array(12).keys()].filter((s) => (r2.mintedMask >> s) & 1).map((s) => E.epda.capsule(matchPda, s).toBase58())] : [];
  }
  out.receipt = JSON.parse(JSON.stringify(await E.matchReceipt(base, matchPda), (_k, v) => (typeof v === "bigint" ? v.toString() : v instanceof PublicKey ? v.toBase58() : v)));
  if (SEASONS && !status.seasonsPaused && !out.judge) await seasons(matchPda, mlog, out).catch((e) => { out.seasonError = String(e?.message ?? e).slice(0, 300); log({ ev: "season_failed", err: out.seasonError }); });
}

/** P6: record a finalized, completed match into its seasons (weekly + the active test season), permissionless. */
async function seasons(matchPda: PublicKey, mlog: string, out: any) {
  const rc: any = await E.matchReceipt(base, matchPda);
  if (!rc || rc.status !== 5 || !(rc.flags & 1) || /^0+$/.test(rc.betsHash)) return;
  // harden #2: without a landed commit_econ the copy never reaches Solana (record_season fails BadCopies): skip at once
  if (!out.sigs.commitEcon) { out.seasonSkipped = "commit_econ did not land"; log({ ev: "season_skip", why: out.seasonSkipped }); return; }
  stage("season");
  // wait until the committed market copies on Solana show this match (commit_econ landed)
  const mk0 = E.epda.market(ARENA, 0);
  for (const t = Date.now(); Date.now() - t < SEASON_COPY_WAIT_S * 1000;) {
    const i = await base.getAccountInfo(mk0).catch(() => null);
    if (i && E.decodeMarket(i.data as Buffer).match.equals(matchPda) && E.decodeMarket(i.data as Buffer).status === 3) break;
    await sleep(2000);
  }
  const mmKey = MM ? key("mm").publicKey.toBase58() : "";
  const ws = new Set<string>();
  for (let s = 0; s < 12; s++) if ((rc.humanMask >> s) & 1) ws.add(rc.wallets[s].toBase58());
  for (let k = 0; k < E.MARKETS; k++) for (const b of out[`market${k}`]?.bets ?? []) ws.add(b.wallet);
  for (const b of out.drops?.buys ?? []) ws.add(b.by);
  ws.delete(mmKey); ws.delete(ZERO);
  const wallets = [...ws].map((x) => new PublicKey(x));
  const champion = rc.champion < 12 && (rc.humanMask >> rc.champion) & 1 ? rc.wallets[rc.champion] : null;
  out.seasons = {};
  for (const id of S.seasonsFor(rc.completedAt)) {
    if (id < S.TEST_SEASON_BASE) await S.ensureSeason(id).catch((e) => log({ ev: "season_open_failed", err: String(e).slice(0, 200) }));
    // a just-created Season may not be visible to the public RPC yet (simulation: AccountOwnedByWrongProgram): wait, retry
    for (let i = 0; i < 20 && !(await S.season(id)); i++) await sleep(1500);
    let sigs: string[] = [], err = "";
    for (let t = 0; t < 3 && !sigs.length; t++) {
      try { sigs = await S.recordSeason(ARENA, matchPda, id, wallets, champion, mlog); } catch (e: any) { err = String(e?.message ?? e).slice(0, 200); log({ ev: "record_retry", season: id, try: t, err }); await sleep(4000); }
    }
    out.seasons[id] = { wallets: wallets.length, walletList: wallets.map((x) => x.toBase58()), sigs, err: sigs.length ? undefined : err };
  }
}


// ------------------------------------------------------------------ one match (idempotent; resumable)
/** harden #4: open_markets failed MarketBusy: every market still holding an older match's unpaid bets → drive that
 * match to the end (abort → refund → receipt → finalize) so the markets reset for `cur`. */
async function recoverMarkets(cur: PublicKey) {
  const olds = new Map<string, PublicKey>();
  for (let k = 0; k < E.MARKETS; k++) {
    const mk = await E.market(ARENA, k);
    if (mk && !mk.match.equals(cur) && !mk.match.equals(PublicKey.default) && mk.status !== 3 && mk.nBets > 0) olds.set(mk.match.toBase58(), mk.match);
  }
  log({ ev: "market_busy", match: cur.toBase58(), stale: [...olds.keys()] });
  for (const old of olds.values()) {
    const em: any = await erMatch(old);
    const bm: any = em ? null : await pb.account.match.fetch(old).catch(() => null);
    const id = (em ?? bm)?.matchId;
    if (!id) { log({ ev: "recover_unknown_match", match: old.toBase58() }); continue; }
    try { await driveMatch(Buffer.from(id).toString("hex"), null, true, { recover: true }); }
    catch (e: any) { log({ ev: "recover_failed", match: old.toBase58(), err: String(e?.message ?? e).slice(0, 300) }); }
  }
  status.match = cur.toBase58();
}

/** harden #7: summary.json of a re-driven match merges into the earlier one (never wipes sigs / final / finalizeMode) */
function writeSummary(mdir: string, out: any) {
  const f = join(mdir, "summary.json");
  const old = existsSync(f) ? (() => { try { return JSON.parse(readFileSync(f, "utf8")); } catch { return null; } })() : null;
  writeFileSync(f + ".tmp", JSON.stringify(P.mergeSummary(old, out), null, 1));
  renameSync(f + ".tmp", f);
}

async function driveMatch(matchHex: string, jsonl: string | null, resumed: boolean, opts: { recover?: boolean } = {}) {
  const matchId = Buffer.from(matchHex, "hex");
  const matchPda = pda.match(matchId);
  const mdir = join(ARCHIVE, matchPda.toBase58());
  mkdirSync(mdir, { recursive: true });
  const mlog = join(mdir, "keeper.jsonl");
  const out: any = { match: matchPda.toBase58(), matchHex, arena: ARENA, arenaKey: arena.toBase58(), world: world.toBase58(), resumed, startedAt: Date.now(), sigs: {} as Record<string, string> };
  const a0: any = await arenaAcc();
  const maps: PublicKey[] = a0.maps;
  out.maps = maps.map((k) => k.toBase58()); // fullchain: the sealed map blobs this match ran on (replay --sealed)
  if (opts.recover) {
    // harden #4 (MarketBusy recovery): an older match whose markets still hold bets: abort it (the arena runs another
    // match, so the guard never applies) → refund → receipt → finalize. Never touches the arena or the current match.
    out.recovered = true;
    stage("recover_match", { match: matchPda.toBase58() });
    await finish(matchPda, maps, mdir, mlog, out);
    out.doneAt = Date.now();
    writeSummary(mdir, out);
    markDone(matchHex);
    log({ ev: "match_recovered", match: out.match });
    return;
  }
  status.match = matchPda.toBase58(); status.matchHex = matchHex; status.dealSig = null; // P7: the VRF deal's request tx (the Deal screen links it)
  const lines = jsonl ? readLines(jsonl) : [];
  const h = lines.find((l) => l.type === "match");
  // submission (judge mode): the Classic server fast-forwarded rounds 1–3 (labelled on the receipt; no Season record)
  const judge = h?.judge === true;
  if (judge) out.judge = true;
  const full = ONCHAIN_ALL && h?.onchainAll === true;
  const exists = !!(await erMatch(matchPda)) || !!(await baseOwner(matchPda));
  // harden #7: the server already played (or ended) this match on Classic and nothing exists on-chain: no create
  if (P.skipClassic(serverAllState(jsonl), full, exists)) {
    log({ ev: "skip_classic", matchHex, server: serverAllState(jsonl) });
    markDone(matchHex);
    return;
  }
  // harden #4: the first start of this match survives keeper restarts (runFinal's deadline counts from it)
  if (st.active?.matchHex !== matchHex) { st.active = { matchHex, firstStartedAt: Date.now() }; saveState(); }
  if (jsonl) try { copyFileSync(jsonl, join(mdir, "classic.jsonl")); } catch {}
  // 1. Solana: create + delegate
  if (!exists) {
    if (!h) throw new Error("no Match and no jsonl header: cannot create");
    stage("create_match");
    out.sigs.create = await act("create_match", async () => sendBase([await pb.methods.createMatch([...matchId], [...Buffer.from(h.rosterHash, "hex")], h.players).accountsPartial({ authority: wallet.publicKey }).instruction()], [wallet], "create_match"), mlog);
    for (let i = 0; i < 60; i++) { if ((await baseOwner(matchPda))?.equals(PROGRAM_ID)) break; await sleep(1000); }
  }
  // delegate unless already delegated: decided by the Solana owner (the rollup clones any account it is asked to
  // read, so "readable on the rollup" does not mean delegated)
  const bm: any = (await baseOwner(matchPda))?.equals(PROGRAM_ID) ? await pb.account.match.fetch(matchPda).catch(() => null) : null;
  if (bm && statusName(bm) === "created") {
    stage("delegate_match");
    out.sigs.delegate = await act("delegate_match", async () => sendBase([await pb.methods.delegateMatch([...matchId]).accountsPartial({ authority: wallet.publicKey, matchAccount: matchPda }).remainingAccounts([{ pubkey: EP.validator, isSigner: false, isWritable: false }]).instruction()], [wallet], "delegate_match"), mlog);
    for (let i = 0; i < 60 && !(await baseOwner(matchPda))?.equals(E.DELEGATION); i++) await sleep(500);
    await sleep(1500);
  }
  stage("chain_setup");
  // 2. ER: lobby (the arena points at this match), markets, deal
  let a: any = await arenaAcc();
  if (a.matchAccount.toBase58() !== matchPda.toBase58()) {
    if (a.status === 2) { // a RUNNING arena left by another match: stop it first
      // harden #4: and abort that match (its markets refund through the MarketBusy recovery below), retried
      const old: PublicKey = a.matchAccount;
      try { await ensureFinal(old, 7, true, mlog); }
      catch (e: any) { log({ ev: "ensure_final_failed", match: old.toBase58(), err: String(e?.message ?? e).slice(0, 200) }); }
      // still RUNNING (its World reached DONE without end_sim, or the abort did not land): stop it as before
      if ((await arenaAcc()).status === 2) {
        const r = await sendEr([await pe.methods.abortArena().accountsPartial({ authority: wallet.publicKey, arena, world }).instruction()], [wallet], "abort_arena");
        if (r.err) throw new Error(`abort_arena: ${r.err}`);
      }
    }
    const inputMetas = Array.from({ length: N }, (_, s) => ({ pubkey: pda.input(arena, s), isSigner: false, isWritable: true }));
    out.sigs.openLobby = (await act("open_lobby", async () => sendEr([await pe.methods.openLobby(matchPda).accountsPartial({ authority: wallet.publicKey, arena, world }).remainingAccounts(inputMetas).instruction()], [wallet], "open_lobby", 40), mlog)).sig;
  }
  let m: any = await erMatch(matchPda);
  if (!m) throw new Error("the Match is not on the rollup");
  if (!(m.flags & 0x80) && ["created", "dealing", "dealt"].includes(statusName(m)) && m.currentRound === 0) {
    // a just-delegated Match can be refused as writable for a moment (InvalidWritableAccount): retry with backoff
    for (let i = 0, recovered = 0; ; i++) {
      try { out.sigs.openMarkets = (await act("open_markets", async () => E.openMarkets(ARENA, matchPda), mlog)).sig; break; }
      catch (e: any) {
        // harden #4: a market still holds an older match's bets: finish that match first (abort → refund → finalize)
        if (P.isMarketBusy(e) && recovered < 2) { recovered++; await recoverMarkets(matchPda); continue; }
        if (i >= 12 || !/InvalidWritableAccount|AccountNotFound|cloner|timeout|Blockhash/i.test(String(e?.message ?? e))) throw e;
        await sleep(1000 + 500 * i);
      }
    }
  }
  let sw: { stop: () => Promise<void> } | null = null;
  if (SEALED && ["created", "dealing", "dealt", "roundActive", "roundResolved"].includes(statusName(m))) {
    // P6: the sealed escrow (devnet-as) + the private book (TEE) for this match
    const e = await S.escrow(ARENA).catch(() => null);
    if (e && !e.match.equals(matchPda) && m.currentRound === 0) {
      const r = await S.escrowOpen(ARENA, matchPda).catch((x) => ({ sig: "", err: String(x) }));
      logTo(mlog, { act: "escrow_open", sig: r.sig, err: r.err }); if (!r.err) out.sigs.escrowOpen = r.sig;
      const b = await S.bookOpen(ARENA, matchPda).catch((x) => ({ sig: "", ms: 0, err: String(x) }));
      logTo(mlog, { act: "book_open", sig: b.sig, ms: b.ms, err: b.err }); if (!b.err) out.sigs.bookOpen = b.sig;
    }
    sw = sealedWatcher(matchPda, mlog, out);
  }
  let mmw: { stop: () => Promise<void> } | null = null;
  const mmLog: any[] = [];
  // P6 (D-OTHER): the market maker seeds only when a human is seated (no seeded pools on bot-only matches)
  const humansInRoster = (h?.roster ?? []).filter((r: any) => r.bot === false).length;
  out.humansInRoster = humansInRoster;
  if (MM && !["completed", "aborted"].includes(statusName(m)) && (humansInRoster > 0 || !h)) {
    const tiers = (h?.roster ?? []).map((r: any) => (r.bot ? r.tier : undefined));
    const humanSlots = h?.roster ? h.roster.filter((r: any) => r.bot === false).map((r: any) => Number(r.slot)) : null;
    mmw = mmWatcher(matchPda, tiers, mmLog, judge, full, humanSlots);
  }
  let dealFailed = false;
  try {
    if (statusName(m) === "created") {
      stage("deal");
      out.sigs.requestDeal = (await act("request_deal", async () => sendEr([await pe.methods.requestDeal().accountsPartial({ authority: wallet.publicKey, matchAccount: matchPda, oracleQueue: EP.vrfQueue }).instruction()], [wallet], "request_deal"), mlog)).sig;
      status.dealSig = out.sigs.requestDeal ?? null;
    }
    for (const td = Date.now(); ;) {
      m = await erMatch(matchPda);
      if (m && statusName(m) !== "created" && statusName(m) !== "dealing") break;
      if (Date.now() - td > 60_000) {
        // harden #4: never leave the Match in Dealing with the market maker's bets: abort (finish() refunds)
        stage("vrf_timeout");
        out.vrfTimeout = true;
        await ensureFinal(matchPda, 7, false, mlog);
        dealFailed = true;
        break;
      }
      await sleep(300);
    }
    m = await erMatch(matchPda);
    if (!m) throw new Error("the Match is not on the rollup");
    // 3. rounds 1–3 from the Classic server (idempotent by event_seq)
    let finalists: number[] = [];
    if (full) out.onchainAll = true;
    if (!dealFailed) {
    if (full && !(m.flags & 0x20) && !["completed", "aborted"].includes(statusName(m))) {
      // fullchain: every human of the roster pre-joins on-chain at roster lock (the server's SendPrejoin); wait for
      // them (bots fill the rest inside start_sim), then the program runs lobby → R1 … R4
      const want = (h?.roster ?? []).filter((r: any) => r.bot === false).length;
      const t0 = Date.now();
      for (;;) {
        const ar: any = await arenaAcc();
        let n = 0; for (let s = 0; s < N; s++) if ((Number(ar.humanMask) >> s) & 1) n++;
        if (n >= want || Date.now() - t0 >= (judge ? Math.max(FULL_WAIT_HUMANS_S, 30) : FULL_WAIT_HUMANS_S) * 1000) { out.humans = { want, joined: n, ms: Date.now() - t0 }; break; }
        if (serverAllState(jsonl) === "fallback") break;
        await sleep(200);
      }
    }
    if (full && serverAllState(jsonl) === "fallback" && !(m.flags & 0x20) && !["completed", "aborted"].includes(statusName(m))) {
      // the server plays this match on Classic (it never saw the rollup go live): abort ours, markets refund
      stage("server_fallback");
      out.serverFallback = true;
      out.sigs.abort = (await act("abort_match", async () => sendEr([await pe.methods.abortMatch(6).accountsPartial({ authority: wallet.publicKey, matchAccount: matchPda }).instruction()], [wallet], "abort_match"), mlog)).sig;
    } else if (full && !(m.flags & 0x20) && !["completed", "aborted"].includes(statusName(m))) {
      stage("start_sim", { humans: out.humans });
      out.sigs.startSim = (await act("start_sim", async () => sendEr([cu(400_000), await pe.methods.startSim().accountsPartial({ authority: wallet.publicKey, arena, world, matchAccount: matchPda, lobbyMap: maps[4] }).instruction()], [wallet], "start_sim"), mlog)).sig;
    } else if (!(m.flags & 0x40) && !(m.flags & 0x20) && !["completed", "aborted"].includes(statusName(m))) {
      if (!jsonl) {
        // resumed without the server's log: the Classic rounds cannot be bridged; abort (markets refund)
        stage("abort_no_log");
        out.sigs.abort = (await act("abort_match", async () => sendEr([await pe.methods.abortMatch(5).accountsPartial({ authority: wallet.publicKey, matchAccount: matchPda }).instruction()], [wallet], "abort_match"), mlog)).sig;
      } else {
        stage("rounds_1_3");
        let br: any;
        // P7: the Classic server's status says whether it still plays this match (a mid-match restart loses it)
        const classicGone = () => { const sv = serverStatus(); return !!sv && Date.now() - (sv.t ?? 0) < 10_000 && (!sv.inMatch || (sv.match && sv.match !== matchHex)); };
        try { br = await bridgeRounds13(jsonl, matchPda, mlog, undefined, classicGone); }
        catch (e: any) {
          // the Classic server aborted the match (e.g. every human left): abort it on-chain too (markets refund)
          if (!/unexpected event ABORT/.test(String(e?.message ?? e))) throw e;
          stage("classic_abort");
          out.sigs.abort = (await act("abort_match", async () => sendEr([await pe.methods.abortMatch(4).accountsPartial({ authority: wallet.publicKey, matchAccount: matchPda }).instruction()], [wallet], "abort_match"), mlog)).sig;
          br = null;
        }
        if (br) {
        finalists = br.finalists;
        out.chainWorker = br;
        const want = finalists.filter((s) => h?.roster?.find((r: any) => r.slot === s && r.bot === false)).length;
        if (want > 0) {
          const t0 = Date.now();
          for (;;) {
            const ar: any = await arenaAcc();
            let n = 0; for (let s = 0; s < N; s++) if ((Number(ar.humanMask) >> s) & 1) n++;
            if (n >= want || Date.now() - t0 >= (judge ? Math.max(WAIT_HUMANS_S, 8) : WAIT_HUMANS_S) * 1000) { out.humans = { want, joined: n, ms: Date.now() - t0 }; break; }
            await sleep(200);
          }
        }
        stage("start_final", { finalists });
        out.sigs.startFinal = (await act("start_final", async () => sendEr([cu(400_000), await pe.methods.startFinal(finalists).accountsPartial({ authority: wallet.publicKey, arena, world, matchAccount: matchPda, lobbyMap: maps[4] }).instruction()], [wallet], "start_final"), mlog)).sig;
        }
      }
    }
    // 4. the Final on the rollup (also resumes a Final left running by a crashed keeper)
    m = await erMatch(matchPda);
    const wd = decodeWorld((await er.getAccountInfo(world))!.data as Buffer);
    a = await arenaAcc();
    if (m && m.flags & 0x20 && a.status === 2 && wd.phase < DONE && a.matchAccount.toBase58() === matchPda.toBase58()) {
      const fullRun = !(m.flags & 0x40);
      stage(fullRun ? "onchain_match" : "final", { finalists });
      // harden #6: a full match the Classic server no longer plays (no-humans abort, server restart) for ORPHAN_MS
      // counts like a fallback: nobody watches it, so abort + refund frees the arena for the next match
      const orphaned = P.orphanDetector(matchHex, ORPHAN_MS);
      const gaveUp = () => (serverAllState(jsonl) === "fallback" ? "fallback" : orphaned(serverStatus()) ? "orphan" : false);
      out.final = await runFinal(matchPda, maps, mdir, mlog, fullRun, gaveUp, st.active?.matchHex === matchHex ? st.active.firstStartedAt : null);
      if (out.final.serverFellBack || out.final.timedOut) {
        // mid-match fallback / orphan / deadline: stop the arena and abort the Match, retried and never swallowed
        // (markets refund; the receipt says aborted)
        const why = out.final.timedOut ? "final_timeout" : out.final.gaveUp === "orphan" ? "server_abandoned" : "server_fallback";
        stage(why);
        if (why === "server_fallback") out.serverFallback = true;
        if (why === "server_abandoned") out.serverAbandoned = true;
        if (why === "final_timeout") out.finalTimeout = true;
        out.ensureFinal = await ensureFinal(matchPda, why === "server_fallback" ? 6 : why === "server_abandoned" ? 8 : 7, true, mlog);
      }
    }
    }
  } finally {
    if (mmw) await mmw.stop();
    out.mm = mmLog;
    if (sw) await sw.stop();
  }
  // 5. settle → receipt → capsules → end_sim → finalize → rewards
  await finish(matchPda, maps, mdir, mlog, out);
  out.doneAt = Date.now();
  writeSummary(mdir, out);
  if (jsonl) try { copyFileSync(jsonl, join(mdir, "classic.jsonl")); } catch {}
  markDone(matchHex);
  status.last = [{ match: out.match, at: out.doneAt, champion: out.receipt?.champion, minted: out.receipt?.mintedMask }, ...status.last].slice(0, 10);
  log({ ev: "match_done", match: out.match, wallS: (out.doneAt - out.startedAt) / 1000 });
}

// ------------------------------------------------------------------ main loop
function newestJsonl(): string | null {
  if (!existsSync(LOG_DIR)) return null;
  const fs = readdirSync(LOG_DIR).filter((f) => /^match-.*\.jsonl$/.test(f)).map((f) => ({ f: join(LOG_DIR, f), t: statSync(join(LOG_DIR, f)).mtimeMs })).sort((x, y) => y.t - x.t);
  for (const x of fs.slice(0, 3)) {
    const h = readLines(x.f).find((l) => l.type === "match");
    if (h && !st.done.includes(h.matchId) && x.t > BOOT - 15 * 60_000) return x.f;
  }
  return null;
}
function jsonlFor(matchHex: string): string | null {
  if (!existsSync(LOG_DIR)) return null;
  for (const f of readdirSync(LOG_DIR).filter((f) => /^match-.*\.jsonl$/.test(f))) {
    const h = readLines(join(LOG_DIR, f)).find((l) => l.type === "match");
    if (h?.matchId === matchHex) return join(LOG_DIR, f);
  }
  return null;
}

// idempotent economy setup for this arena (markets, drop queue, treasury, RUSH mint): P4 setupEcon
for (let i = 0; ; i++) { try { const o = await E.setupEcon(ARENA); log({ act: "econ_setup", ...o }); break; } catch (e) { log({ ev: "econ_setup_failed", err: String(e).slice(0, 200) }); if (i >= 5) throw e; await sleep(5000); } }
if (MM) await ensureMm().catch((e) => log({ ev: "mm_setup_failed", err: String(e).slice(0, 200) }));
if (SEALED) await S.setupSealed(ARENA).then((o) => log({ act: "sealed_setup", ...o })).catch((e) => log({ ev: "sealed_setup_failed", err: String(e).slice(0, 200) }));
// harden #2: the 5 markets + drop queue commit to Solana for the seasons; a delegated account without a fee payer stops
// committing after 10 commits (delegation-metadata last_update_nonce): at the cap, seasons are paused (no commit_econ,
// no 60 s copy wait, no record_season retries). /status → seasonsPaused (the indexer labels the board "paused").
if (SEASONS) {
  try {
    const accs = [...Array.from({ length: E.MARKETS }, (_, k) => E.epda.market(ARENA, k)), E.epda.drops(arena)];
    const metas = accs.map((k) => PublicKey.findProgramAddressSync([Buffer.from("delegation-metadata"), k.toBuffer()], E.DELEGATION)[0]);
    const infos = await base.getMultipleAccountsInfo(metas, "confirmed");
    const nonces = infos.map((i) => P.delegationNonce(i?.data as Buffer | undefined));
    status.commitNonces = nonces;
    if (P.commitCapped(nonces)) { status.seasonsPaused = true; status.seasonsPausedReason = "commit cap"; log({ ev: "seasons_paused", why: "commit cap", nonces }); console.log(`[keeper a${ARENA}] seasons paused: commit cap (nonces ${nonces.join(",")})`); }
  } catch (e) { log({ ev: "commit_cap_check_failed", err: String(e).slice(0, 200) }); }
} else status.seasonsPausedReason = "SEASONS=0";
// resume from on-chain state: the arena's current Match, unless it is finished (on Solana, nothing owed)
{
  const a: any = await arenaAcc();
  const cur = a.matchAccount.toBase58();
  if (cur !== ZERO) {
    const owner = await baseOwner(a.matchAccount);
    const rc = owner?.equals(PROGRAM_ID) ? await E.matchReceipt(base, a.matchAccount) : null;
    const owed = rc ? ((rc.flags & (4 | 16)) === 0 && rc.champion < 12 && rc.flags & 1 ? 1 : 0) + (rc.flags & 2 ? [...Array(12).keys()].filter((s) => (rc.humanMask >> s) & 1 && !((rc.mintedMask >> s) & 1)).length : 0) : 1;
    const em = await erMatch(a.matchAccount);
    const matchHex = em ? Buffer.from(em.matchId).toString("hex") : rc ? null : null;
    if (owed > 0 && (em || rc)) {
      const hex = matchHex ?? Buffer.from((await pb.account.match.fetch(a.matchAccount) as any).matchId).toString("hex");
      if (!st.done.includes(hex) || owed > 0) {
        stage("resume", { match: cur });
        try { await driveMatch(hex, jsonlFor(hex), true); } catch (e: any) { stage("resume_failed", { err: String(e?.message ?? e).slice(0, 300) }); }
      }
    }
  }
}
for (;;) {
  stage("idle");
  let j: string | null = null;
  while (!(j = newestJsonl())) await sleep(1000);
  const h = await header(j);
  const hex = h.matchId.toString("hex");
  // a transient failure mid-match: drive the same match again (every stage is idempotent); harden #4/#5: with
  // backoff 2 / 10 / 30 / 60 / 120 s (an ER hiccup outlasts 3 × 2 s)
  let ok = false;
  for (let tries = 0; tries <= P.RETRY_BACKOFF_S.length && !ok; tries++) {
    try { await driveMatch(hex, j, tries > 0); ok = true; }
    catch (e: any) {
      const wait = P.RETRY_BACKOFF_S[tries];
      stage("match_retry", { tries: tries + 1, waitS: wait ?? 0, err: String(e?.message ?? e).slice(0, 300) });
      if (wait != null) await sleep(wait * 1000);
    }
  }
  if (!ok) {
    stage("match_failed", { match: hex });
    if (!st.done.includes(hex)) { st.done.push(hex); saveState(); } // never loop on a broken match (active kept: the cleanup keeps its deadline)
    // finish whatever is on-chain (refunds, receipt, finalize) so the arena is free for the next match
    try { await driveMatch(hex, null, true); } catch (e2: any) { log({ ev: "cleanup_failed", err: String(e2?.message ?? e2).slice(0, 300) }); }
  }
}
