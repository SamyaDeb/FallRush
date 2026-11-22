// P0.5: end-to-end latency. Send input stamped T = latest seen tick + 1 for slot 0, measure until the first
// World update with applied_tick[0] >= T. Needs the timed task running. P0_NET=.. tsx p05-latency.ts <samples>
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { PublicKey } from "@solana/web3.js";
import { er, IX, LOGS, NET, wallet, pda, send, sendRetry, summary, loadavg, sleep, cuLimit, key, inputBytes, rttBaseline } from "./lib.ts";
const samples = Number(process.argv[2] ?? 1000);
const st = JSON.parse(readFileSync(join(LOGS, `state-${NET}.json`), "utf8"));
const map = new PublicKey(st.maps.lid);
const owner = key("input-0");

async function main() {
  const out: any = { net: NET, samples, load_before: loadavg(), at: new Date().toISOString() };
  out.rtt_getSlot_ms = await rttBaseline(er);
  await sendRetry(er, [IX.resetWorld(wallet.publicKey, 0, map, 31, 1, 0)], [wallet]);
  let curTick = 0;
  let waiting: { T: number; resolve: (t: number) => void } | null = null;
  const sub = er.onAccountChange(pda.world(0), (acc) => {
    const t = Date.now();
    const d = acc.data as Buffer;
    curTick = Math.max(curTick, d.readUInt32LE(8 + 64));
    const applied0 = d.readUInt32LE(8 + 848);
    if (waiting && applied0 >= waiting.T) { const w = waiting; waiting = null; w.resolve(t); }
  }, { commitment: "processed" });
  await sleep(1500);
  const iters = Math.ceil(samples * 0.5 * 1000 / 50) + 400;
  const rs = await send(er, [cuLimit(400_000), IX.scheduleTicks(wallet.publicKey, 0, map, 50, iters, BigInt(Date.now()), true)], [wallet]);
  out.schedule_err = rs.err ?? null;
  await sleep(1500);
  const e2e: number[] = [], conf: number[] = [], lateBy: number[] = [];
  let timeouts = 0, errs = 0;
  for (let i = 0; i < samples; i++) {
    const T = curTick + 1;
    const b = inputBytes(0, T); b[3] = i & 255; b[7] = (i >> 8) & 255;
    const tSend = Date.now();
    const seen = new Promise<number>((resolve) => { waiting = { T, resolve }; });
    const timeout = sleep(3000).then(() => -1);
    const r = await send(er, [IX.submitInput(owner.publicKey, 0, 0, T, b)], [owner], { pollMs: 10 });
    if (r.err) { errs++; waiting = null; continue; }
    conf.push(r.confirmedAt! - tSend);
    const tSeen = await Promise.race([seen, timeout]);
    if (tSeen < 0) { timeouts++; waiting = null; continue; }
    e2e.push(tSeen - tSend);
    lateBy.push(curTick - T);
    await sleep(20 + (i % 3) * 17); // de-phase vs the 50 ms tick
  }
  await er.removeAccountChangeListener(sub);
  out.e2e_ms = summary(e2e);
  out.confirm_ms = summary(conf);
  out.ticks_after_T_when_seen = summary(lateBy);
  out.timeouts = timeouts;
  out.errors = errs;
  out.load_after = loadavg();
  console.log(JSON.stringify(out));
  writeFileSync(join(LOGS, `p05-${NET}.json`), JSON.stringify(out, null, 2));
  process.exit(0);
}
main().catch((e) => { console.error(e); process.exit(1); });
