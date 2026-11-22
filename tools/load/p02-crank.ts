// P0.2: timed task (ScheduleTask) driving tick() — gaps, missed ticks, drift; fallback permissionless tick.
// P0_NET=.. tsx p02-crank.ts <intervalMs> <seconds> <split 0|1> [map lid|run] [nonce] [label]
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { PublicKey } from "@solana/web3.js";
import { er, IX, LOGS, NET, wallet, pda, send, sendRetry, decodeWorld, decodeLog, summary, logsOf, loadavg, sleep, cuLimit, LOG_N } from "./lib.ts";

const interval = Number(process.argv[2] ?? 50);
const seconds = Number(process.argv[3] ?? 60);
const split = process.argv[4] === "1";
const mapName = process.argv[5] ?? "lid";
const nonce = BigInt(process.argv[6] ?? Date.now());
const label = process.argv[7] ?? `${interval}ms-${seconds}s-${split ? "split" : "single"}-${mapName}`;
const st = JSON.parse(readFileSync(join(LOGS, `state-${NET}.json`), "utf8"));
const map = new PublicKey(st.maps[mapName]);

async function getWorld() { return decodeWorld((await er.getAccountInfo(pda.world(0), "processed"))!.data as Buffer); }

async function main() {
  const out: any = { net: NET, label, interval, seconds, split, map: mapName, nonce: nonce.toString(), load_before: loadavg(), at: new Date().toISOString() };
  const r0 = await sendRetry(er, [IX.resetWorld(wallet.publicKey, 0, map, 11, 1, 0)], [wallet]);
  if (r0.err) throw new Error("reset " + r0.err);
  // subscriber
  const recv: { tick: number; t: number; slot: number }[] = [];
  const subId = er.onAccountChange(pda.world(0), (acc, ctx) => {
    const d = acc.data as Buffer;
    recv.push({ tick: d.readUInt32LE(8 + 64), t: Date.now(), slot: ctx.slot });
  }, { commitment: "processed" });
  await sleep(1500);
  // ER slot rate
  const s0 = await er.getSlot("processed"); const ts0 = Date.now();
  const iterations = Math.ceil((seconds * 1000) / interval);
  const rs = await send(er, [cuLimit(400_000), IX.scheduleTicks(wallet.publicKey, 0, map, interval, iterations, nonce, split)], [wallet]);
  const tSched = Date.now();
  const ls = rs.sig ? await logsOf(er, rs.sig) : { logs: [], err: rs.err };
  out.schedule = { err: rs.err ?? null, sig: rs.sig, taskLog: ls.logs.find((l) => l.includes("task_id")) ?? null };
  console.log("scheduled", JSON.stringify(out.schedule));
  // collect TickLog snapshots every 5 s (ring holds 400 entries = 20 s at 20 Hz)
  const logE = new Map<number, { slot: bigint; unix: bigint; cu: number }>();
  const end = tSched + seconds * 1000 + 3000;
  while (Date.now() < end) {
    await sleep(5000);
    const li = await er.getAccountInfo(pda.log(0), "processed");
    if (li) for (const e of decodeLog(li.data as Buffer).e) if (e.tick) logE.set(e.tick, { slot: e.slot, unix: e.unix, cu: e.cuStep });
    const w = await getWorld();
    process.stdout.write(`  t+${((Date.now() - tSched) / 1000).toFixed(0)}s tick=${w.tick} recv=${recv.length}\n`);
  }
  const s1 = await er.getSlot("processed"); const ts1 = Date.now();
  await er.removeAccountChangeListener(subId);
  const w = await getWorld();
  out.slot_ms = (ts1 - ts0) / (s1 - s0);
  out.expected_ticks = iterations;
  out.ticks_done = w.tick;
  out.world_ticks_run = Number(w.ticksRun);
  out.task_id = w.taskId.toString();
  // gaps from subscriber receive times (unique ticks, first arrival)
  const first = new Map<number, number>();
  for (const r of recv) if (!first.has(r.tick)) first.set(r.tick, r.t);
  const ticks = [...first.keys()].sort((a, b) => a - b);
  const gaps: number[] = [];
  for (let i = 1; i < ticks.length; i++) gaps.push((first.get(ticks[i])! - first.get(ticks[i - 1])!) / (ticks[i] - ticks[i - 1]));
  const rawGaps: number[] = [];
  for (let i = 1; i < ticks.length; i++) rawGaps.push(first.get(ticks[i])! - first.get(ticks[i - 1])!);
  out.recv_updates = recv.length;
  out.recv_unique_ticks = ticks.length;
  out.recv_gap_ms = summary(rawGaps);
  out.recv_gap_over_250 = rawGaps.filter((g) => g > 250).length;
  const tTotal = ticks.length > 1 ? first.get(ticks[ticks.length - 1])! - first.get(ticks[0])! : 0;
  out.recv_rate_hz = ticks.length > 1 ? ((ticks[ticks.length - 1] - ticks[0]) * 1000) / tTotal : 0;
  out.drift_ms = ticks.length > 1 ? tTotal - (ticks[ticks.length - 1] - ticks[0]) * interval : null;
  // slot adherence from TickLog
  const lt = [...logE.keys()].sort((a, b) => a - b);
  const slotGaps: number[] = [];
  for (let i = 1; i < lt.length; i++) if (lt[i] === lt[i - 1] + 1) slotGaps.push(Number(logE.get(lt[i])!.slot - logE.get(lt[i - 1])!.slot));
  const expSlots = interval / out.slot_ms;
  out.ticklog_entries = lt.length;
  out.ticklog_slot_gap = summary(slotGaps);
  out.ticklog_within_1_slot_pct = slotGaps.length ? (100 * slotGaps.filter((g) => Math.abs(g - expSlots) <= 1).length) / slotGaps.length : null;
  out.ticklog_cu_step = summary(lt.map((t) => logE.get(t)!.cu));
  out.load_after = loadavg();
  console.log(JSON.stringify(out));
  writeFileSync(join(LOGS, `p02-${NET}-${label}.json`), JSON.stringify(out, null, 2));
  writeFileSync(join(LOGS, `p02-${NET}-${label}-recv.json`), JSON.stringify(recv));
}
main().catch((e) => { console.error(e); process.exit(1); });
