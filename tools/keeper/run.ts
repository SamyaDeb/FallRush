// P2 keeper + runner (permissionless keeper logic; here it also plays the game authority for match setup).
// Runs MATCHES full 12-bot matches unattended on arena ARENA:
//   create_match + delegate (base) → open_lobby → request_deal (VRF) → start_sim → schedule_ticks → watch → commits
//   → end_sim → finalize_match. Watches World by polling (every POLL_MS) + onAccountChange; restarts the task when no
//   execution for STALL_MS (cancel + re-schedule with a new nonce) and sends fallback tick()s meanwhile.
// Records every World update (docs/proof/p2/runs/<match>/world.bin: [u32 tick][u8 part][u16 len][bytes]) for replay
// and logs every action with its latency (docs/proof/p2/keeper-<run>.jsonl).
//   P2_NET=local|devnet ARENA=0 MATCHES=1 INTERVAL_MS=8 node --experimental-strip-types tools/keeper/run.ts
import { PublicKey, Keypair, Transaction } from "@solana/web3.js";
import { randomBytes } from "node:crypto";
import { mkdirSync, appendFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { sampleCu } from "./cusample.ts";
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
const summary: any[] = [];
for (let mi = 1; mi <= MATCHES; mi++) {
  const matchId = randomBytes(16);
  const matchPda = pda.match(matchId);
  const mdir = join(ROOT, `docs/proof/p2/runs/${RUN}-m${mi}`);
  mkdirSync(mdir, { recursive: true });
  const sigs: Record<string, string> = {};
  console.log(`== match ${mi}/${MATCHES} ${matchPda.toBase58()}`);
  // ---- Solana: create + delegate the match
  const rosterHash = randomBytes(32); // the director computes the authoritative roster hash; Match keeps the pool's
  sigs.create = (await act("create_match", async () => sendBase([await pb.methods.createMatch([...matchId], [...rosterHash], N).accountsPartial({ authority: wallet.publicKey }).instruction()], [wallet], "create_match")));
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
  sigs.startSim = (await act("start_sim", async () => sendEr([cu(400_000), await pe.methods.startSim().accountsPartial({ authority: wallet.publicKey, arena, world, matchAccount: matchPda, lobbyMap: maps[4] }).instruction()], [wallet], "start_sim"))).sig;
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
  const record = (buf: Buffer) => {
    const w = decodeWorld(buf);
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
