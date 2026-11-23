// P2 keeper + runner (permissionless keeper logic; here it also plays the game authority for match setup).
// Runs MATCHES full 12-bot matches unattended on arena ARENA:
//   create_match + delegate (base) → open_lobby → request_deal (VRF) → start_sim → schedule_ticks → watch → commits
//   → end_sim → finalize_match. Watches World by polling (every POLL_MS) + onAccountChange; restarts the task when no
//   execution for STALL_MS (cancel + re-schedule with a new nonce) and sends fallback tick()s meanwhile.
// Records every World update (docs/proof/p2/runs/<match>/world.bin: [u32 tick][u8 part][u16 len][bytes]) for replay
// and logs every action with its latency (docs/proof/p2/keeper-<run>.jsonl).
//   P2_NET=local|devnet ARENA=0 MATCHES=1 INTERVAL_MS=8 node --experimental-strip-types tools/keeper/run.ts
import { PublicKey, Keypair, Transaction } from "@solana/web3.js";
import { randomBytes, createHash } from "node:crypto";
import { mkdirSync, appendFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { sampleCu } from "./cusample.ts";
import { header, bridgeRounds13, readLines } from "../chain-worker/worker.ts";
import { NET, EP, base, er, wallet, pb, pe, pda, key, sendBase, sendEr, ROOT, PROGRAM_ID, MAGIC_PROGRAM, MAGIC_CONTEXT, N, sleep, decodeWorld, tickAccounts, log, cu, pct, BN } from "./lib.ts";

const ARENA = Number(process.env.ARENA ?? 0);
const MATCHES = Number(process.env.MATCHES ?? 1);
const INTERVAL_MS = Number(process.env.INTERVAL_MS ?? (NET === "local" ? 50 : 8));
const STALL_MS = Number(process.env.STALL_MS ?? (NET === "local" ? 3000 : 500));
const POLL_MS = Number(process.env.POLL_MS ?? 200);
const DRIVE_MS = Number(process.env.DRIVE_MS ?? 6000);
const DRIVE_GAP_MS = Number(process.env.DRIVE_GAP_MS ?? (NET === "local" ? 40 : 8));
const RUN = process.env.RUN ?? `${NET}-${new Date().toISOString().replace(/[:.]/g, "-")}`;
const LOG = join(ROOT, `docs/proof/p2/keeper-${RUN}.jsonl`);
const DONE = 11;
const HYBRID = (process.env.MODE ?? "slice") === "hybrid"; // P2.10: Classic jsonl (CLASSIC_JSONL) rounds 1–3 → on-chain Final
const FINAL = (process.env.MODE ?? "slice") === "final" || HYBRID;
// P4 0.3 "wait for humans": before start_final, wait until HUMANS humans have joined the lobby or WAIT_HUMANS_S
// seconds have passed. Hybrid: HUMANS defaults to the human finalists of the Classic handoff (jsonl roster).
const WAIT_HUMANS_S = Number(process.env.WAIT_HUMANS_S ?? 8);
const HUMANS_ENV = process.env.HUMANS != null ? Number(process.env.HUMANS) : null;
// P4 0.4: the arena's forward input window (set_input_lead; 0 = program default 8); unset = leave as is
const LEAD = process.env.LEAD != null ? Number(process.env.LEAD) : null;

/** Rounds 1–3 recorded the way the Classic server's chain worker does (start_round / record_qualified /
 * record_eliminated / complete_round, game authority, idempotent by event_seq). Qualifiers: a deterministic pick from
 * the match id (bots). Returns the 3 finalists. */
async function recordRounds13(matchPda: PublicKey, matchId: Buffer): Promise<number[]> {
  const m0: any = await pe.account.match.fetch(matchPda);
  let seq: number = m0.eventSeq;
  const targets: number[] = m0.roundTargets;
  let alive = Array.from({ length: m0.playerCount }, (_, i) => i);
  const gp = { authority: wallet.publicKey, matchAccount: matchPda };
  const ev = async (what: string, ix: Promise<any>) => { const r = await sendEr([await ix], [wallet], what, 10); if (r.err) throw new Error(`${what}: ${r.err}`); seq++; return r; };
  for (let r = 1; r <= 3; r++) {
    await ev(`start_round ${r}`, pe.methods.startRound(r, seq).accountsPartial(gp).instruction());
    const order = [...alive].sort((a, b) => sha(matchId, r, a) - sha(matchId, r, b));
    const q = order.slice(0, targets[r - 1]), out = order.slice(targets[r - 1]);
    for (let i = 0; i < q.length; i++) await ev(`q ${r}/${q[i]}`, pe.methods.recordQualified(r, q[i], i + 1, seq).accountsPartial(gp).instruction());
    for (const sl of out) await ev(`e ${r}/${sl}`, pe.methods.recordEliminated(r, sl, 1, seq).accountsPartial(gp).instruction());
    await ev(`complete_round ${r}`, pe.methods.completeRound(r, seq).accountsPartial(gp).instruction());
    alive = q;
  }
  log(LOG, { act: "rounds_1_3", finalists: alive, seq });
  return alive.sort((a, b) => a - b);
}
function sha(id: Buffer, r: number, s: number) { return createHash("sha256").update(Buffer.concat([id, Buffer.from([r, s])])).digest().readUInt32LE(0); }
mkdirSync(join(ROOT, "docs/proof/p2/runs"), { recursive: true });

const arena = pda.arena(ARENA);
const world = pda.world(arena);
const arenaAcc: any = await pe.account.arena.fetch(arena);
const maps: PublicKey[] = arenaAcc.maps;
const parts: number = arenaAcc.parts;
const inputs = Array.from({ length: N }, (_, s) => pda.input(arena, s));
const act = async (what: string, f: () => Promise<any>) => { const t0 = Date.now(); try { const r = await f(); log(LOG, { act: what, ms: Date.now() - t0, ok: true, sig: typeof r === "string" ? r : r?.sig, err: r?.err }); if (r?.err) throw new Error(`${what}: ${r.err}`); return r; } catch (e: any) { log(LOG, { act: what, ms: Date.now() - t0, ok: false, err: String(e?.message ?? e).slice(0, 400) }); throw e; } };

// an arena left RUNNING by an interrupted run: stop it first (open_lobby needs it stopped)
if (arenaAcc.status === 2) {
  await sendEr([await pe.methods.cancelTicks(arenaAcc.taskId).accountsPartial({ payer: wallet.publicKey }).remainingAccounts([{ pubkey: MAGIC_CONTEXT, isSigner: false, isWritable: true }]).instruction()], [wallet], "cancel_ticks", 2).catch(() => {});
  await sendEr([await pe.methods.abortArena().accountsPartial({ authority: wallet.publicKey, arena, world }).instruction()], [wallet], "abort_arena");
  console.log("aborted a running arena left by an earlier run");
}
if (LEAD != null) {
  const r = await sendEr([await pe.methods.setInputLead(LEAD).accountsPartial({ authority: wallet.publicKey, arena, world }).instruction()], [wallet], "set_input_lead");
  log(LOG, { act: "set_input_lead", lead: LEAD, sig: r.sig, err: r.err });
  console.log(`input lead ${LEAD}: ${r.err ?? "ok"}`);
}
/** Waits until `want` humans have joined the arena (popcount of human_mask) or `secs` passed; returns the count. */
async function waitHumans(want: number, secs: number): Promise<{ joined: number; ms: number }> {
  const t0 = Date.now();
  for (;;) {
    const a: any = await pe.account.arena.fetch(arena).catch(() => null);
    const mask = a ? Number(a.humanMask) : 0;
    let n = 0; for (let s = 0; s < N; s++) if ((mask >> s) & 1) n++;
    if (n >= want || Date.now() - t0 >= secs * 1000) return { joined: n, ms: Date.now() - t0 };
    await sleep(250);
  }
}
const summary: any[] = [];
for (let mi = 1; mi <= MATCHES; mi++) {
  const classic = HYBRID ? await header(process.env.CLASSIC_JSONL!) : null;
  const matchId = classic ? classic.matchId : randomBytes(16);
  const matchPda = pda.match(matchId);
  const mdir = join(ROOT, `docs/proof/p2/runs/${RUN}-m${mi}`);
  mkdirSync(mdir, { recursive: true });
  const sigs: Record<string, string> = {};
  console.log(`== match ${mi}/${MATCHES} ${matchPda.toBase58()}`);
  // ---- Solana: create + delegate the match
  const rosterHash = classic ? classic.rosterHash : randomBytes(32); // hybrid: the Classic roster hash
  sigs.create = (await act("create_match", async () => sendBase([await pb.methods.createMatch([...matchId], [...rosterHash], classic ? classic.players : N).accountsPartial({ authority: wallet.publicKey }).instruction()], [wallet], "create_match")));
  // the public RPC can lag behind the confirmation: wait until the new Match is visible before delegating it
  for (let i = 0; i < 60; i++) { const ai = await base.getAccountInfo(matchPda, "confirmed").catch(() => null); if (ai && ai.owner.equals(PROGRAM_ID)) break; await sleep(1000); }
  sigs.delegate = (await act("delegate_match", async () => sendBase([await pb.methods.delegateMatch([...matchId]).accountsPartial({ authority: wallet.publicKey, matchAccount: matchPda }).remainingAccounts([{ pubkey: EP.validator, isSigner: false, isWritable: false }]).instruction()], [wallet], "delegate_match")));
  // ---- ER: lobby → VRF → start
  const inputMetas = inputs.map((pubkey) => ({ pubkey, isSigner: false, isWritable: true }));
  sigs.openLobby = (await act("open_lobby", async () => sendEr([await pe.methods.openLobby(matchPda).accountsPartial({ authority: wallet.publicKey, arena, world }).remainingAccounts(inputMetas).instruction()], [wallet], "open_lobby", 40))).sig;
  sigs.requestDeal = (await act("request_deal", async () => sendEr([await pe.methods.requestDeal().accountsPartial({ authority: wallet.publicKey, matchAccount: matchPda, oracleQueue: EP.vrfQueue }).instruction()], [wallet], "request_deal"))).sig;
  const tDeal = Date.now();
  for (;;) {
    const m: any = await pe.account.match.fetch(matchPda).catch(() => null);
    if (m && Object.keys(m.status)[0] === "dealt") break;
    if (Date.now() - tDeal > 60_000) throw new Error("VRF callback timeout");
    await sleep(300);
  }
  log(LOG, { act: "vrf_callback_seen", ms: Date.now() - tDeal });
  let finalists: number[] = [];
  let preFinal: any = null;
  if (FINAL) {
    // Classic rounds 1–3 → Match (here: synthetic bot results; P2.10's chain worker sends the Classic server's), then
    // the on-chain Final from the 3 survivors
    finalists = HYBRID ? (await bridgeRounds13(process.env.CLASSIC_JSONL!, matchPda, LOG)).finalists : await recordRounds13(matchPda, matchId);
    const mp: any = await pe.account.match.fetch(matchPda);
    preFinal = { transcript: Buffer.from(mp.transcriptHash).toString("hex"), eventSeq: mp.eventSeq };
    // wait for humans (deterministic human Finals; bets and drops from real people need them in)
    let want = HUMANS_ENV ?? 0;
    if (HYBRID && HUMANS_ENV == null) {
      const h = readLines(process.env.CLASSIC_JSONL!).find((l) => l.type === "match");
      want = finalists.filter((s) => h?.roster?.find((r: any) => r.slot === s && r.bot === false)).length;
    }
    if (want > 0) {
      const wh = await waitHumans(want, WAIT_HUMANS_S);
      log(LOG, { act: "wait_humans", want, ...wh });
      console.log(`  humans ${wh.joined}/${want} after ${wh.ms} ms`);
      preFinal.humans = { want, ...wh };
    }
    sigs.startFinal = (await act("start_final", async () => sendEr([cu(400_000), await pe.methods.startFinal(finalists).accountsPartial({ authority: wallet.publicKey, arena, world, matchAccount: matchPda, lobbyMap: maps[4] }).instruction()], [wallet], "start_final"))).sig;
  } else {
    sigs.startSim = (await act("start_sim", async () => sendEr([cu(400_000), await pe.methods.startSim().accountsPartial({ authority: wallet.publicKey, arena, world, matchAccount: matchPda, lobbyMap: maps[4] }).instruction()], [wallet], "start_sim"))).sig;
  }
  // ---- the timed task
  const tickMetas = tickAccounts(arena, matchPda, maps);
  const iterations = 2_000_000;
  const schedule = async () => act("schedule_ticks", async () => sendEr([cu(400_000), await pe.methods.scheduleTicks(new BN(INTERVAL_MS), new BN(iterations)).accountsPartial({ payer: wallet.publicKey, arena }).remainingAccounts(tickMetas).instruction()], [wallet], "schedule_ticks"));
  const cancel = async (taskId: any) => act("cancel_ticks", async () => sendEr([await pe.methods.cancelTicks(taskId).accountsPartial({ payer: wallet.publicKey }).remainingAccounts([{ pubkey: MAGIC_CONTEXT, isSigner: false, isWritable: true }]).instruction()], [wallet], "cancel_ticks", 3));
  const tasks: string[] = [];
  const noteTask = async () => { const a: any = await pe.account.arena.fetch(arena); tasks.push(a.taskId.toString()); };
  sigs.schedule = (await schedule()).sig;
  await noteTask();
  // ---- watch: recorder + stall restart + commits
  const t0 = Date.now();
  let lastProg = "", lastExecAt = Date.now(), restarts = 0, fallback = 0;
  let lastTick = -1, lastTickAt = Date.now();
  const gaps: number[] = [];
  const committed = new Set<number>();
  let lastSnapKey = "";
  const tickAt: [number, number, number][] = [];
  const record = (buf: Buffer) => {
    const w = decodeWorld(buf);
    if (w.partPending === 0 && (tickAt.length === 0 || w.tick > tickAt[tickAt.length - 1][0])) tickAt.push([w.tick, Date.now(), Number(w.lastSlot)]);
    const k = `${w.tick}:${w.partPending}`;
    if (k === lastSnapKey) return w;
    lastSnapKey = k;
    const h = Buffer.alloc(7); h.writeUInt32LE(w.tick, 0); h[4] = w.partPending; h.writeUInt16LE(w.len, 5);
    appendFileSync(join(mdir, "world.bin"), Buffer.concat([h, w.world]));
    return w;
  };
  const sub = er.onAccountChange(world, (a) => { try { record(a.data as Buffer); } catch {} }, { commitment: "processed" } as any);
  let w = decodeWorld((await er.getAccountInfo(world))!.data as Buffer);
  const base0 = { executions: w.executions, notDue: w.notDue, budgetHit: w.budgetHit };
  let phaseRound = "";
  const cuByRound: Record<string, number[]> = {};
  let lastCu = 0;
  while (w.phase < DONE) {
    await sleep(POLL_MS);
    const info = await er.getAccountInfo(world, "processed").catch(() => null);
    if (!info) continue;
    w = record(info.data as Buffer);
    const now = Date.now();
    // progress = the (tick, part) the World is at; executions alone also count other tasks' no-ops
    const prog = `${w.tick}:${w.partPending}`;
    if (prog !== lastProg) { lastProg = prog; lastExecAt = now; }
    if (w.tick !== lastTick) { if (lastTick >= 0) gaps.push((now - lastTickAt) / Math.max(1, w.tick - lastTick)); lastTick = w.tick; lastTickAt = now; }
    if (`${w.phase}/${w.round}` !== phaseRound) { phaseRound = `${w.phase}/${w.round}`; log(LOG, { ev: "phase", phase: w.phase, round: w.round, tick: w.tick, ticksRun: Number(w.ticksRun), budgetHit: Number(w.budgetHit) }); console.log(`  tick ${w.tick} phase ${w.phase} round ${w.round} execs ${w.executions} budget_hit ${w.budgetHit}`); }
    // stall: no execution for STALL_MS → fallback ticks + cancel / re-schedule
    if (now - lastExecAt > STALL_MS) {
      restarts++;
      log(LOG, { ev: "stall", ms: now - lastExecAt, tick: w.tick, lastCode: w.lastCode });
      for (const t of tasks.splice(0)) await cancel(new BN(t)).catch(() => {});
      // drive mode: the keeper streams tick()s at the client limit (1.4M CU) for DRIVE_MS — a part over the task's
      // 400k budget completes here, and the next parts follow without waiting for confirmations (each call runs the
      // part pending when it lands; early part-0 calls are NotDue no-ops). Then the task takes over again.
      const d0 = Date.now(), tick0 = w.tick;
      const ixTick = await pe.methods.tick().remainingAccounts(tickMetas).instruction();
      let bh = (await er.getLatestBlockhash("processed")).blockhash, bhAt = Date.now(), nonce = 0;
      const sent: string[] = [];
      while (Date.now() - d0 < DRIVE_MS) {
        if (Date.now() - bhAt > 1500) { bh = (await er.getLatestBlockhash("processed")).blockhash; bhAt = Date.now(); }
        const tx = new Transaction().add(cu(1_400_000 - (nonce++ % 64)), ixTick); // unique per send (CU limit varies)
        tx.feePayer = wallet.publicKey; tx.recentBlockhash = bh; tx.sign(wallet);
        er.sendRawTransaction(tx.serialize(), { skipPreflight: true, maxRetries: 0 }).then((sig) => sent.push(sig)).catch(() => {});
        fallback++;
        await sleep(DRIVE_GAP_MS);
      }
      const wd = decodeWorld((await er.getAccountInfo(world))!.data as Buffer);
      log(LOG, { act: "drive", ms: Date.now() - d0, sends: sent.length, ticks: wd.tick - tick0, sample: sent.slice(-3) });
      await schedule();
      await noteTask();
      lastExecAt = Date.now();
    }
    // commits at phase edges: once per resolved round (Match says RoundResolved / Completed for that round)
    if (w.round >= 1 && !committed.has(w.round) && (w.phase === 7 || w.phase === 8 || w.phase >= 9)) {
      committed.add(w.round);
      const r = w.round;
      act(`commit_round_${r}`, async () => sendEr([await pe.methods.commitRound(r).accountsPartial({ payer: wallet.publicKey, matchAccount: matchPda }).instruction()], [wallet], "commit_round", 10)).then((x) => { sigs[`commit${r}`] = x.sig; }).catch(() => committed.delete(r));
    }
    if (now - lastCu > 15_000) {
      lastCu = now;
      sampleCu(world).then((xs) => { const k = `r${w.round}`; (cuByRound[k] ??= []).push(...xs.map((x) => x.cu)); }).catch(() => {});
    }
    if (now - t0 > 60 * 60_000) { log(LOG, { ev: "timeout" }); break; }
  }
  await er.removeAccountChangeListener(sub);
  // real time: game time vs wall clock over the round's ticks; per-tick gaps from the websocket arrivals
  const g: number[] = [];
  for (let i = 1; i < tickAt.length; i++) g.push((tickAt[i][1] - tickAt[i - 1][1]) / Math.max(1, tickAt[i][0] - tickAt[i - 1][0]));
  const span = tickAt.length > 1 ? { ticks: tickAt[tickAt.length - 1][0] - tickAt[0][0], wallMs: tickAt[tickAt.length - 1][1] - tickAt[0][1] } : { ticks: 0, wallMs: 1 };
  // on-chain tick spacing: the slot each tick started in (rollup slots: SLOT_MS each), consecutive ticks only
  const SLOT_MS = NET === "local" ? 50 : 10;
  const gs: number[] = [];
  for (let i = 1; i < tickAt.length; i++) if (tickAt[i][0] === tickAt[i - 1][0] + 1) gs.push((tickAt[i][2] - tickAt[i - 1][2]) * SLOT_MS);
  const slotSpan = tickAt.length > 1 ? (tickAt[tickAt.length - 1][2] - tickAt[0][2]) * SLOT_MS / 1000 : 1;
  const realTime = { gameS: span.ticks / 30, wallS: span.wallMs / 1000, ratio: span.ticks / 30 / (span.wallMs / 1000), slotWallS: slotSpan, slotRatio: span.ticks / 30 / slotSpan,
    tickGapOnChainMs: { p50: pct(gs, 50), p95: pct(gs, 95), p99: pct(gs, 99), max: Math.max(...gs), n: gs.length },
    tickGapClientMs: { p50: pct(g, 50), p95: pct(g, 95), p99: pct(g, 99), max: Math.max(...g), n: g.length } };
  writeFileSync(join(mdir, "ticks.json"), JSON.stringify(tickAt));
  const dur = (Date.now() - t0) / 1000;
  for (const t of tasks.splice(0)) await cancel(new BN(t)).catch(() => {});
  sigs.endSim = (await act("end_sim", async () => sendEr([await pe.methods.endSim().accountsPartial({ authority: wallet.publicKey, arena, world }).instruction()], [wallet], "end_sim"))).sig;
  await sleep(1500);
  sigs.finalize = (await act("finalize_match", async () => sendEr([await pe.methods.finalizeMatch().accountsPartial({ authority: wallet.publicKey, matchAccount: matchPda }).instruction()], [wallet], "finalize_match"))).sig;
  // Match back on Solana
  const tf = Date.now();
  let mb: any = null;
  for (; Date.now() - tf < 90_000;) { const i = await base.getAccountInfo(matchPda); if (i && i.owner.equals(PROGRAM_ID)) { mb = await pb.account.match.fetch(matchPda); break; } await sleep(1000); }
  const res = {
    realTime, finalists, preFinal,
    match: mi, matchPda: matchPda.toBase58(), complete: w.phase >= DONE, wallS: dur, gameS: w.tick / 30, ticks: w.tick, executions: Number(w.executions - base0.executions), notDue: Number(w.notDue - base0.notDue),
    execPerTick: Number(w.executions - base0.executions) / Math.max(1, w.tick), budgetHit: Number(w.budgetHit - base0.budgetHit), restarts, fallback,
    cuPerPart: Object.fromEntries(Object.entries(cuByRound).map(([k, v]) => [k, { n: v.length, p50: pct(v, 50), p95: pct(v, 95), max: Math.max(...v) }])), gapMs: { p50: pct(gaps, 50), p95: pct(gaps, 95), max: Math.max(...gaps) }, onSolana: mb ? { status: Object.keys(mb.status)[0], champion: mb.championSlot, round: mb.currentRound, transcript: Buffer.from(mb.transcriptHash).toString("hex"), vrfSeed: Buffer.from(mb.vrfSeed).toString("hex") } : null, sigs,
  };
  writeFileSync(join(mdir, "summary.json"), JSON.stringify({ ...res, mode: process.env.MODE ?? "slice", parts, interval: INTERVAL_MS, arena: arena.toBase58(), maps: maps.map((m) => m.toBase58()) }, null, 1));
  log(LOG, { ev: "match_done", ...res });
  console.log(JSON.stringify(res));
  summary.push(res);
}
console.log(`run ${RUN}: ${summary.filter((s) => s.complete).length}/${summary.length} complete`);
process.exit(0);
