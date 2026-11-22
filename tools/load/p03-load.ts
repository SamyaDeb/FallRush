// P0.3 input load + P0.4 (Node) state delivery, with the timed task running.
// P0_NET=.. tsx p03-load.ts <seconds> <rateMul 1|2> [nBets] [relayPort]
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { PublicKey } from "@solana/web3.js";
import { er, IX, LOGS, NET, wallet, pda, send, sendRetry, summary, loadavg, sleep, cuLimit, key, SigPoller, sendVia, rttBaseline, inputBytes, N, keeper } from "./lib.ts";

const seconds = Number(process.argv[2] ?? 60);
const mul = Number(process.argv[3] ?? 1);
const nBets = Number(process.argv[4] ?? 10);
const inputPeriod = Number(process.env.P0_INPUT_MS ?? 100); // per client
const noInputs = process.env.P0_NO_INPUTS === '1';
const idle = process.env.P0_IDLE === '1'; // inputs with no movement / no jump (cheap sim, same tx load)
const relayPort = process.argv[5] ? Number(process.argv[5]) : 0;
const st = JSON.parse(readFileSync(join(LOGS, `state-${NET}.json`), "utf8"));
const map = new PublicKey(st.maps.lid);

async function main() {
  const out: any = { net: NET, seconds, mul, nBets, load_before: loadavg(), at: new Date().toISOString() };
  out.rtt_getSlot_ms = await rttBaseline(er);
  console.log("rtt", JSON.stringify(out.rtt_getSlot_ms));
  await sendRetry(er, [IX.resetWorld(wallet.publicKey, 0, map, 21, 1, 0)], [wallet]);
  // Node subscriber (P0.4) — direct, and optionally through the relay
  let curTick = 0;
  let kpRef: any = null;
  const direct = new Map<number, number>();
  const viaRelay = new Map<number, number>();
  let directUpdates = 0;
  const sub = er.onAccountChange(pda.world(0), (acc) => {
    const t = Date.now();
    const tick = (acc.data as Buffer).readUInt32LE(8 + 64);
    directUpdates++;
    if (!direct.has(tick)) direct.set(tick, t);
    curTick = Math.max(curTick, tick);
    kpRef?.onTick(tick);
  }, { commitment: "processed" });
  let rws: WebSocket | undefined;
  if (relayPort) {
    rws = new WebSocket(`ws://127.0.0.1:${relayPort}`);
    rws.binaryType = "arraybuffer";
    rws.onmessage = (ev: any) => { const t = Date.now(); const tick = new DataView(ev.data).getUint32(8 + 64, true); if (!viaRelay.has(tick)) viaRelay.set(tick, t); };
  }
  await sleep(1500);
  const iters = Math.ceil((seconds + 5) * 1000 / 50);
  const kp = keeper(map, 50, iters, true);
  kpRef = kp;
  await kp.start();
  await sleep(1000);
  const poller = new SigPoller(er, 20, 15000);
  const owners = Array.from({ length: N }, (_, s) => key(`input-${s}`));
  const bettors = Array.from({ length: nBets }, (_, i) => key(`bet-${i}`));
  const lat: Record<string, number[]> = { input: [], bet: [] };
  const errs: Record<string, Record<string, number>> = { input: {}, bet: {} };
  let sent = { input: 0, bet: 0 };
  const pend: Promise<void>[] = [];
  const track = (kind: "input" | "bet", p: Promise<any>) => pend.push(p.then((r) => {
    if (r.err) errs[kind][r.err.slice(0, 90)] = (errs[kind][r.err.slice(0, 90)] ?? 0) + 1;
    else lat[kind].push(r.confirmedAt - r.sentAt);
  }));
  const t0 = Date.now();
  const tickStart = curTick;
  const polled: { t: number; tick: number }[] = [];
  let polling = true;
  (async () => { while (polling) { try { const a = await er.getAccountInfo(pda.world(0), "processed"); if (a) polled.push({ t: Date.now(), tick: (a.data as Buffer).readUInt32LE(8 + 64) }); } catch {} await sleep(1000); } })();
  let nonce = 0;
  const timers: any[] = [];
  owners.forEach((o, s) => {
    const period = inputPeriod / mul;
    if (noInputs) return;
    setTimeout(() => timers.push(setInterval(() => {
      if (Date.now() - t0 > seconds * 1000) return;
      const b = inputBytes(s, curTick + 1); if (idle) { b.fill(0); } b[3] = nonce++ & 255; b[7] = (nonce >> 8) & 255;
      sent.input++;
      track("input", sendVia(poller, [IX.submitInput(o.publicKey, 0, s, curTick + 1, b)], [o]));
    }, period)), (s * 100 / N / mul));
  });
  bettors.forEach((bk, i) => {
    const period = 1000 / mul;
    setTimeout(() => timers.push(setInterval(() => {
      if (Date.now() - t0 > seconds * 1000) return;
      const p = Buffer.alloc(64); p.writeUInt32LE(nonce++); p.writeDoubleLE(Date.now(), 8);
      sent.bet++;
      track("bet", sendVia(poller, [IX.placeBet(bk.publicKey, p)], [bk]));
    }, period)), i * 1000 / nBets / mul);
  });
  while (Date.now() - t0 < seconds * 1000) { await sleep(5000); console.log(`  t+${((Date.now() - t0) / 1000).toFixed(0)}s tick=${curTick} sent=${JSON.stringify(sent)} pending=${poller.pending.size}`); }
  timers.forEach(clearInterval);
  polling = false;
  await kp.stop();
  await Promise.all(pend);
  poller.stop();
  const elapsed = (Date.now() - t0) / 1000;
  const tickEnd = curTick;
  await er.removeAccountChangeListener(sub);
  rws?.close();
  out.sent = sent;
  out.tx_per_s = (sent.input + sent.bet) / elapsed;
  out.input = { confirm_ms: summary(lat.input), errors: errs.input };
  out.bet = { confirm_ms: summary(lat.bet), errors: errs.bet };
  out.poll_errors = poller.errors;
  out.ticks = { produced_seen_by_sub: tickEnd - tickStart, expected: Math.round(elapsed * 20) };
  if (polled.length > 1) {
    const rates: number[] = [];
    for (let i = 1; i < polled.length; i++) rates.push((polled[i].tick - polled[i - 1].tick) * 1000 / (polled[i].t - polled[i - 1].t));
    out.ticks_polled = { first: polled[0].tick, last: polled[polled.length - 1].tick, rate_hz: (polled[polled.length - 1].tick - polled[0].tick) * 1000 / (polled[polled.length - 1].t - polled[0].t), per_s: { p5: rates.sort((a, b) => a - b)[Math.floor(rates.length * 0.05)], p50: rates[Math.floor(rates.length / 2)] }, stalls_s: rates.filter((r) => r < 1).length };
  }
  // P0.4 Node: updates per second + tick gaps
  const ticks = [...direct.keys()].filter((t) => t > tickStart && t <= tickEnd).sort((a, b) => a - b);
  const gaps: number[] = [];
  let missing = 0;
  for (let i = 1; i < ticks.length; i++) { gaps.push(direct.get(ticks[i])! - direct.get(ticks[i - 1])!); missing += ticks[i] - ticks[i - 1] - 1; }
  out.node_sub = { unique_ticks_seen: ticks.length, ticks_produced: tickEnd - tickStart, missing_ticks: missing, updates_per_s: ticks.length / elapsed, total_updates: directUpdates, gap_ms: summary(gaps), gaps_over_250: gaps.filter((g) => g > 250).length };
  if (relayPort) {
    const add: number[] = [];
    for (const t of ticks) if (viaRelay.has(t)) add.push(viaRelay.get(t)! - direct.get(t)!);
    out.relay = { ticks_seen: [...viaRelay.keys()].filter((t) => t > tickStart && t <= tickEnd).length, added_ms: summary(add) };
  }
  out.keeper_restarts = kp.restarts;
  out.keeper_cancel_errors = kp.cancelErrors;
  out.load_after = loadavg();
  console.log(JSON.stringify(out));
  writeFileSync(join(LOGS, `p03-${NET}-x${mul}-${seconds}s${noInputs ? '-noinputs' : ''}${idle ? '-idle' : ''}${process.env.P0_INPUT_MS ? '-in' + process.env.P0_INPUT_MS : ''}.json`), JSON.stringify(out, null, 2));
}
main().catch((e) => { console.error(e); process.exit(1); });
