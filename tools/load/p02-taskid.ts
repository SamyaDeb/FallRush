// P0.2 task-id behaviour + cancel. P0_NET=.. tsx p02-taskid.ts
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { PublicKey } from "@solana/web3.js";
import { er, IX, LOGS, NET, wallet, pda, send, sendRetry, decodeWorld, sleep, cuLimit, MAGIC_CONTEXT, logsOf } from "./lib.ts";
const st = JSON.parse(readFileSync(join(LOGS, `state-${NET}.json`), "utf8"));
const map = new PublicKey(st.maps.lid);
const tick = async (): Promise<any> => { for (let i = 0; ; i++) { try { return decodeWorld((await er.getAccountInfo(pda.world(0), "processed"))!.data as Buffer); } catch (e) { if (i > 10) throw e; await sleep(500); } } };
const sched = (iters: number, nonce: bigint, k: number) => send(er, [cuLimit(400_000 - k), IX.scheduleTicks(wallet.publicKey, 0, map, 50, iters, nonce, true)], [wallet]);
const out: any = { net: NET };
await sendRetry(er, [IX.resetWorld(wallet.publicKey, 0, map, 5, 1, 0)], [wallet]);
const base = BigInt(Date.now());
// A: 60 iterations
let t0 = (await tick()).tick;
let r = await sched(60, base, 1);
await sleep(5000);
out.A_single_60 = { err: r.err ?? null, ticks: (await tick()).tick - t0 };
// B: reuse the same id after A finished
t0 = (await tick()).tick;
r = await sched(60, base, 2);
await sleep(5000);
out.B_reuse_finished_id_60 = { err: r.err ?? null, ticks: (await tick()).tick - t0 };
// C: two schedules with the SAME id at once (100 iterations each)
t0 = (await tick()).tick;
const r1 = await sched(100, base + 1n, 3);
const r2 = await sched(100, base + 1n, 4);
await sleep(8000);
out.C_duplicate_id_concurrent_2x100 = { err1: r1.err ?? null, err2: r2.err ?? null, ticks: (await tick()).tick - t0, note: "100 = second silently dropped (or replaced); 200 = both ran" };
// D: two DIFFERENT ids at once (100 each) — shows two tasks can drive one world
t0 = (await tick()).tick;
await sched(100, base + 2n, 5);
await sched(100, base + 3n, 6);
await sleep(8000);
out.D_two_ids_concurrent_2x100 = { ticks: (await tick()).tick - t0 };
// E: cancel with the magic context as task-context account
t0 = (await tick()).tick;
await sched(400, base + 4n, 7);
await sleep(2000);
const w = await tick();
const rc = await send(er, [IX.cancelTicks(wallet.publicKey, w.taskId, MAGIC_CONTEXT)], [wallet]);
const lc = rc.sig ? await logsOf(er, rc.sig) : { logs: [] as string[] };
const tc = (await tick()).tick;
await sleep(3000);
out.E_cancel = { err: rc.err ?? null, ticks_before_cancel: tc - t0, ticks_in_3s_after: (await tick()).tick - tc, logs: lc.logs.slice(-4) };
console.log(JSON.stringify(out, null, 1));
writeFileSync(join(LOGS, `p02-${NET}-taskid.json`), JSON.stringify(out, null, 2));
process.exit(0);
