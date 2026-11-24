// P5 keeper daemon (one per arena; hybrid mode): runs forever beside one Classic game server.
//   - tails the server's match logs (shared volume, LOG_DIR/match-<id>.jsonl) and drives each match on-chain:
//     create + delegate the Match → open_lobby → open_markets (+ the labelled market maker) → VRF deal → rounds 1–3 from
//     the jsonl (chain worker) → start_final → timed task + stall restarts → settle + pay → receipt → capsule VRF →
//     end_sim → finalize (receipt commit + undelegate; the Magic Action mints when ACTION=1) → mint_rewards retry;
//   - every stage checks on-chain state first (idempotent), so after a crash / restart it RESUMES FROM ON-CHAIN STATE:
//     the arena's current Match is finished (settle → receipt → finalize → mint) before a new one starts (P5 0.5);
//   - archives each match (ARCHIVE/<match>/: world.bin, the jsonl, keeper.jsonl, summary.json, econ.json) for the indexer;
//   - serves GET /status (arena + keeper stage + the server's status.json) and GET /health on STATUS_PORT.
//   P2_NET=devnet ARENA=2 LOG_DIR=/logs/gs1 ARCHIVE=/archive STATUS_PORT=3111 node --experimental-strip-types tools/keeper/daemon.ts
import { PublicKey, Transaction, SystemProgram } from "@solana/web3.js";
import { createServer } from "node:http";
import { mkdirSync, appendFileSync, writeFileSync, readFileSync, existsSync, readdirSync, statSync, copyFileSync } from "node:fs";
import { join } from "node:path";
import { header, bridgeRounds13, readLines } from "../chain-worker/worker.ts";
import { NET, EP, base, er, wallet, pb, pe, pda, key, sendBase, sendEr, PROGRAM_ID, MAGIC_CONTEXT, N, sleep, decodeWorld, tickAccounts, log as logTo, cu, pct, BN } from "./lib.ts";
import * as E from "./econ.ts";
import * as S from "./p6.ts";
import { sampleCu } from "./cusample.ts";

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
const KILL_AT = process.env.KILL_AT ?? ""; // test hook (P5 0.5): exit(9) right after this stage
const DONE = 11;
const BOOT = Date.now();
const STATE_FILE = join(ARCHIVE, `keeper-${NET}-arena${ARENA}.json`);
mkdirSync(ARCHIVE, { recursive: true });
const LOG = join(ARCHIVE, `keeper-${NET}-arena${ARENA}.jsonl`);
const log = (o: object) => { try { logTo(LOG, { arena: ARENA, ...o }); } catch {} };

const arena = pda.arena(ARENA);
const world = pda.world(arena);
const st: any = existsSync(STATE_FILE) ? JSON.parse(readFileSync(STATE_FILE, "utf8")) : { done: [] as string[] };
const saveState = () => writeFileSync(STATE_FILE, JSON.stringify(st, null, 1));
const status: any = { arena: ARENA, arenaKey: arena.toBase58(), world: world.toBase58(), net: NET, stage: "boot", since: Date.now(), boot: BOOT, last: [] as any[] };
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
function mmWatcher(matchPda: PublicKey, tiers: (string | undefined)[], mlog: any[]) {
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
          if (m.currentRound === 0 && ["created", "dealing", "dealt"].includes(s)) { await seed("champion", 0, alive); await seed("q1", 2, alive); }
          if (s === "roundResolved" && m.currentRound === 1) await seed("q2", 3, alive);
          if (s === "roundResolved" && m.currentRound === 2) await seed("q3", 4, alive);
          if ((m.flags & 0x40) !== 0 && !done.has("final")) {
            const wi = await er.getAccountInfo(world, "processed");
            if (wi && wi.data[139] === 4 && wi.data[199] < 3) await seed("final", 1, alive);
          }
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
async function runFinal(matchPda: PublicKey, maps: PublicKey[], mdir: string, mlog: string) {
  const tickMetas = tickAccounts(arena, matchPda, maps);
  const schedule = async () => act("schedule_ticks", async () => sendEr([cu(400_000), await pe.methods.scheduleTicks(new BN(INTERVAL_MS), new BN(2_000_000)).accountsPartial({ payer: wallet.publicKey, arena }).remainingAccounts(tickMetas).instruction()], [wallet], "schedule_ticks"), mlog);
  const cancel = async (taskId: any) => act("cancel_ticks", async () => sendEr([await pe.methods.cancelTicks(taskId).accountsPartial({ payer: wallet.publicKey }).remainingAccounts([{ pubkey: MAGIC_CONTEXT, isSigner: false, isWritable: true }]).instruction()], [wallet], "cancel_ticks", 3), mlog);
  // resume: a task left by an earlier keeper process may still run (or be dead): cancel it, then schedule ours
  const a0: any = await arenaAcc();
  if (a0.taskId && a0.taskId.toString() !== "0") await cancel(a0.taskId).catch(() => {});
  await schedule();
  const tasks: string[] = [(await arenaAcc()).taskId.toString()];
  let lastSnapKey = "";
  const tickAt: [number, number, number][] = [];
  const record = (buf: Buffer) => {
    const w = decodeWorld(buf);
    if (w.partPending === 0 && (tickAt.length === 0 || w.tick > tickAt[tickAt.length - 1][0])) tickAt.push([w.tick, Date.now(), Number(w.lastSlot)]);
    const k = `${w.tick}:${w.partPending}`;
    if (k === lastSnapKey) return w;
    lastSnapKey = k;
    const h = Buffer.alloc(7); h.writeUInt32LE(w.tick, 0); h[4] = w.partPending; h.writeUInt16LE(w.len, 5);
    // world2.bin (P5 replay): [u32 tick][u8 part][u16 len][80 B WorldAcc header @128..208: tick, len, phase, cur[12]
    // (the inputs latched for this tick), cur_some, serial…][World bytes]
    appendFileSync(join(mdir, "world2.bin"), Buffer.concat([h, buf.subarray(128, 208), w.world]));
    return w;
  };
  const sub = er.onAccountChange(world, (a) => { try { record(a.data as Buffer); } catch {} }, { commitment: "processed" } as any);
  let w = decodeWorld((await er.getAccountInfo(world))!.data as Buffer);
  let lastProg = "", lastExecAt = Date.now(), restarts = 0, lastCu = 0;
  const cus: number[] = [];
  const t0 = Date.now();
  while (w.phase < DONE) {
    await sleep(POLL_MS);
    const info = await er.getAccountInfo(world, "processed").catch(() => null);
    if (!info) continue;
    w = record(info.data as Buffer);
    status.worldTick = w.tick; status.worldPhase = w.phase; status.worldRound = w.round;
    const now = Date.now();
    const prog = `${w.tick}:${w.partPending}`;
    if (prog !== lastProg) { lastProg = prog; lastExecAt = now; }
    if (now - lastExecAt > STALL_MS) {
      restarts++;
      log({ ev: "stall", ms: now - lastExecAt, tick: w.tick });
      for (const t of tasks.splice(0)) await cancel(new BN(t)).catch(() => {});
      const d0 = Date.now();
      const ixTick = await pe.methods.tick().remainingAccounts(tickMetas).instruction();
      let bh = (await er.getLatestBlockhash("processed")).blockhash, bhAt = Date.now(), nonce = 0;
      while (Date.now() - d0 < DRIVE_MS) {
        if (Date.now() - bhAt > 1500) { bh = (await er.getLatestBlockhash("processed")).blockhash; bhAt = Date.now(); }
        const tx = new Transaction().add(cu(1_400_000 - (nonce++ % 64)), ixTick);
        tx.feePayer = wallet.publicKey; tx.recentBlockhash = bh; tx.sign(wallet);
        er.sendRawTransaction(tx.serialize(), { skipPreflight: true, maxRetries: 0 }).catch(() => {});
        await sleep(8);
      }
      await schedule();
      tasks.push((await arenaAcc()).taskId.toString());
      lastExecAt = Date.now();
    }
    if (now - lastCu > 15_000) { lastCu = now; sampleCu(world).then((xs) => cus.push(...xs.map((x) => x.cu))).catch(() => {}); }
    if (now - t0 > 20 * 60_000) { log({ ev: "final_timeout" }); break; }
  }
  await er.removeAccountChangeListener(sub);
  for (const t of tasks.splice(0)) await cancel(new BN(t)).catch(() => {});
  const g: number[] = [];
  for (let i = 1; i < tickAt.length; i++) g.push((tickAt[i][1] - tickAt[i - 1][1]) / Math.max(1, tickAt[i][0] - tickAt[i - 1][0]));
  const span = tickAt.length > 1 ? { ticks: tickAt[tickAt.length - 1][0] - tickAt[0][0], wallMs: tickAt[tickAt.length - 1][1] - tickAt[0][1] } : { ticks: 0, wallMs: 1 };
  writeFileSync(join(mdir, "ticks.json"), JSON.stringify(tickAt));
  return { complete: w.phase >= DONE, ticks: w.tick, restarts, realTime: span.ticks / 30 / (span.wallMs / 1000), gapP95: pct(g, 95), cu: { n: cus.length, p95: pct(cus, 95), max: cus.length ? Math.max(...cus) : 0 } };
}

// ------------------------------------------------------------------ after the Final: settle → receipt → finalize → mint
async function finish(matchPda: PublicKey, maps: PublicKey[], mdir: string, mlog: string, out: any) {
  let m: any = await erMatch(matchPda);
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
    if (rc && !(rc.flags & 1)) { stage("receipt"); out.sigs.writeReceipt = (await act("write_receipt", async () => E.writeReceipt(ARENA, matchPda, maps), mlog)).sig; }
    if (SEASONS && rc && !(out.sigs.commitEcon)) {
      // P6: the 5 markets + drop queue to Solana (the copies record_season checks against the receipt's bets hash)
      const c = await S.commitEcon(ARENA).catch((x) => ({ sig: "", err: String(x) }));
      logTo(mlog, { act: "commit_econ", sig: c.sig, err: c.err }); if (!c.err) out.sigs.commitEcon = c.sig;
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
  if (a.status === 2) {
    const wd = decodeWorld((await er.getAccountInfo(world))!.data as Buffer);
    if (wd.phase >= DONE) out.sigs.endSim = (await act("end_sim", async () => sendEr([await pe.methods.endSim().accountsPartial({ authority: wallet.publicKey, arena, world }).instruction()], [wallet], "end_sim"), mlog)).sig;
    else out.sigs.abortArena = (await act("abort_arena", async () => sendEr([await pe.methods.abortArena().accountsPartial({ authority: wallet.publicKey, arena, world }).instruction()], [wallet], "abort_arena"), mlog)).sig;
  }
  // finalize (receipt commit + undelegate) unless the Match is back on Solana already
  if (!(await baseOwner(matchPda))?.equals(PROGRAM_ID)) {
    stage("finalize");
    await sleep(1000);
    const ix = ACTION
      ? await pe.methods.finalizeMatchAction().accountsPartial({ authority: wallet.publicKey, matchAccount: matchPda }).instruction()
      : await pe.methods.finalizeMatch().accountsPartial({ authority: wallet.publicKey, matchAccount: matchPda }).instruction();
    try {
      out.sigs.finalize = (await act(ACTION ? "finalize_match_action" : "finalize_match", async () => sendEr([cu(400_000), ix], [wallet], "finalize_match"), mlog)).sig;
      out.finalizeMode = ACTION ? "action" : "plain";
    } catch (e: any) {
      if (!ACTION) throw e;
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
  stage("rewards");
  let rc = await E.matchReceipt(base, matchPda);
  const owedCount = (r: any) => (r ? ((r.flags & (4 | 16)) === 0 && r.champion < 12 ? 1 : 0) + (r.flags & 2 ? [...Array(12).keys()].filter((s) => (r.humanMask >> s) & 1 && !((r.mintedMask >> s) & 1)).length : 0) : 0);
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
  if (SEASONS) await seasons(matchPda, mlog, out).catch((e) => { out.seasonError = String(e?.message ?? e).slice(0, 300); log({ ev: "season_failed", err: out.seasonError }); });
}

/** P6: record a finalized, completed match into its seasons (weekly + the active test season), permissionless. */
async function seasons(matchPda: PublicKey, mlog: string, out: any) {
  const rc: any = await E.matchReceipt(base, matchPda);
  if (!rc || rc.status !== 5 || !(rc.flags & 1) || /^0+$/.test(rc.betsHash)) return;
  stage("season");
  // wait until the committed market copies on Solana show this match (commit_econ landed)
  const mk0 = E.epda.market(ARENA, 0);
  for (const t = Date.now(); Date.now() - t < 60_000;) {
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
      try { sigs = await S.recordSeason(ARENA, matchPda, id, wallets, champion, mlog); } catch (e: any) { err = String(e?.message ?? e).slice(0, 200); log({ ev: "record_retry", season: id, t, err }); await sleep(4000); }
    }
    out.seasons[id] = { wallets: wallets.length, walletList: wallets.map((x) => x.toBase58()), sigs, err: sigs.length ? undefined : err };
  }
}


// ------------------------------------------------------------------ one match (idempotent; resumable)
async function driveMatch(matchHex: string, jsonl: string | null, resumed: boolean) {
  const matchId = Buffer.from(matchHex, "hex");
  const matchPda = pda.match(matchId);
  const mdir = join(ARCHIVE, matchPda.toBase58());
  mkdirSync(mdir, { recursive: true });
  const mlog = join(mdir, "keeper.jsonl");
  const out: any = { match: matchPda.toBase58(), matchHex, arena: ARENA, arenaKey: arena.toBase58(), world: world.toBase58(), resumed, startedAt: Date.now(), sigs: {} as Record<string, string> };
  status.match = matchPda.toBase58(); status.matchHex = matchHex; status.dealSig = null; // P7: the VRF deal's request tx (the Deal screen links it)
  const a0: any = await arenaAcc();
  const maps: PublicKey[] = a0.maps;
  const lines = jsonl ? readLines(jsonl) : [];
  const h = lines.find((l) => l.type === "match");
  if (jsonl) try { copyFileSync(jsonl, join(mdir, "classic.jsonl")); } catch {}
  // 1. Solana: create + delegate
  if (!(await erMatch(matchPda)) && !(await baseOwner(matchPda))) {
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
      await sendEr([await pe.methods.abortArena().accountsPartial({ authority: wallet.publicKey, arena, world }).instruction()], [wallet], "abort_arena");
    }
    const inputMetas = Array.from({ length: N }, (_, s) => ({ pubkey: pda.input(arena, s), isSigner: false, isWritable: true }));
    out.sigs.openLobby = (await act("open_lobby", async () => sendEr([await pe.methods.openLobby(matchPda).accountsPartial({ authority: wallet.publicKey, arena, world }).remainingAccounts(inputMetas).instruction()], [wallet], "open_lobby", 40), mlog)).sig;
  }
  let m: any = await erMatch(matchPda);
  if (!m) throw new Error("the Match is not on the rollup");
  if (!(m.flags & 0x80) && ["created", "dealing", "dealt"].includes(statusName(m)) && m.currentRound === 0) {
    // a just-delegated Match can be refused as writable for a moment (InvalidWritableAccount): retry with backoff
    for (let i = 0; ; i++) {
      try { out.sigs.openMarkets = (await act("open_markets", async () => E.openMarkets(ARENA, matchPda), mlog)).sig; break; }
      catch (e: any) { if (i >= 12 || !/InvalidWritableAccount|AccountNotFound|cloner|timeout|Blockhash/i.test(String(e?.message ?? e))) throw e; await sleep(1000 + 500 * i); }
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
    mmw = mmWatcher(matchPda, tiers, mmLog);
  }
  try {
    if (statusName(m) === "created") {
      stage("deal");
      out.sigs.requestDeal = (await act("request_deal", async () => sendEr([await pe.methods.requestDeal().accountsPartial({ authority: wallet.publicKey, matchAccount: matchPda, oracleQueue: EP.vrfQueue }).instruction()], [wallet], "request_deal"), mlog)).sig;
      status.dealSig = out.sigs.requestDeal ?? null;
    }
    for (const td = Date.now(); ;) {
      m = await erMatch(matchPda);
      if (m && statusName(m) !== "created" && statusName(m) !== "dealing") break;
      if (Date.now() - td > 60_000) throw new Error("VRF callback timeout");
      await sleep(300);
    }
    // 3. rounds 1–3 from the Classic server (idempotent by event_seq)
    let finalists: number[] = [];
    if (!(m.flags & 0x40) && !["completed", "aborted"].includes(statusName(m))) {
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
            if (n >= want || Date.now() - t0 >= WAIT_HUMANS_S * 1000) { out.humans = { want, joined: n, ms: Date.now() - t0 }; break; }
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
    if (m && m.flags & 0x40 && a.status === 2 && wd.phase < DONE && a.matchAccount.toBase58() === matchPda.toBase58()) {
      stage("final", { finalists });
      out.final = await runFinal(matchPda, maps, mdir, mlog);
    }
  } finally {
    if (mmw) await mmw.stop();
    out.mm = mmLog;
    if (sw) await sw.stop();
  }
  // 5. settle → receipt → capsules → end_sim → finalize → rewards
  await finish(matchPda, maps, mdir, mlog, out);
  out.doneAt = Date.now();
  writeFileSync(join(mdir, "summary.json"), JSON.stringify(out, null, 1));
  if (jsonl) try { copyFileSync(jsonl, join(mdir, "classic.jsonl")); } catch {}
  st.done.push(matchHex); st.done = st.done.slice(-500); saveState();
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
  // a transient failure mid-match: drive the same match again (every stage is idempotent), up to 3 times
  let ok = false;
  for (let tries = 0; tries < 3 && !ok; tries++) {
    try { await driveMatch(hex, j, tries > 0); ok = true; }
    catch (e: any) { stage("match_retry", { tries: tries + 1, err: String(e?.message ?? e).slice(0, 300) }); await sleep(2000); }
  }
  if (!ok) {
    stage("match_failed", { match: hex });
    st.done.push(hex); saveState(); // never loop on a broken match
    // finish whatever is on-chain (refunds, receipt, finalize) so the arena is free for the next match
    try { await driveMatch(hex, null, true); } catch (e2: any) { log({ ev: "cleanup_failed", err: String(e2?.message ?? e2).slice(0, 300) }); }
  }
}
