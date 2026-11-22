// P0.1: compute units per tick() at 12 bot players on the 120- and 300-collider maps.
// P0_NET=local|devnet tsx p01-cu.ts [ticks]
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { PublicKey } from "@solana/web3.js";
import { IX2 } from "./lib.ts";
import { er, IX, sendRetry, LOGS, NET, wallet, pda, send, decodeWorld, summary, logsOf, cuLimit, loadavg } from "./lib.ts";

const TICKS = Number(process.argv[2] ?? 200);
const st = JSON.parse(readFileSync(join(LOGS, `state-${NET}.json`), "utf8"));

async function world() { return decodeWorld((await er.getAccountInfo(pda.world(0), "processed"))!.data as Buffer); }

async function main() {
  const out: any = { net: NET, ticks: TICKS, load_before: loadavg(), at: new Date().toISOString(), maps: {} };
  for (const name of ["lid", "run"]) {
    const map = new PublicKey(st.maps[name]);
    const r0 = await sendRetry(er, [IX.resetWorld(wallet.publicKey, 0, map, 7, 1, 0)], [wallet]);
    if (r0.err) throw new Error(`reset ${name}: ${r0.err}`);
    const rows: any[] = [];
    const sigs: string[] = [];
    let fails = 0;
    for (let i = 0; i < TICKS; i++) {
      const r = await send(er, [cuLimit(1_400_000 - i), IX.tick(0, map)], [wallet], { pollMs: 10 });
      if (r.err) { fails++; console.log(`tick ${i} err ${r.err}`); continue; }
      sigs.push(r.sig);
    }
    const w = await world();
    const txcu: number[] = [];
    for (const sig of sigs) {
      const l = await logsOf(er, sig);
      const c = l.logs.filter((x) => x.includes("Program consumption:")).map((x) => Number(/consumption: (\d+)/.exec(x)![1]));
      if (c.length < 6) { console.log("missing cu logs", sig); continue; }
      rows.push({ inputs: c[1] - c[2], bots: c[2] - c[3], step: c[3] - c[4], handler: c[0] - c[5], tx: l.cu });
      if (l.cu) txcu.push(l.cu);
    }
    // default 200k limit: does a tick without a compute-budget ix succeed?
    const rd = await send(er, [IX.tick(0, map)], [wallet], { pollMs: 10 });
    const rdLogs = rd.sig ? await logsOf(er, rd.sig) : { cu: undefined, err: rd.err, logs: [] };
    // 2-instruction split: same tx, NO compute-budget ix (default limit = 200k x 2 ixs)
    await sendRetry(er, [IX.resetWorld(wallet.publicKey, 0, map, 7, 1, 0)], [wallet]);
    const split: any[] = [];
    let splitFails = 0;
    const ssigs: string[] = [];
    for (let i = 0; i < TICKS; i++) {
      const r = await send(er, [IX.tickPart(0, map, 0), IX.tickPart(0, map, 1), ...(i ? [cuLimit(400_000 - i)] : [])], [wallet], { pollMs: 10 });
      if (r.err) { splitFails++; if (splitFails < 5) console.log(`split ${i} err ${r.err}`); continue; }
      ssigs.push(r.sig);
    }
    const wSplit = await world();
    for (const sig of ssigs) {
      const l = await logsOf(er, sig);
      const c = l.logs.filter((x) => x.includes("Program consumption:")).map((x) => Number(/consumption: (\d+)/.exec(x)![1]));
      // part 0 logs 6 values (c0..c4 + none: returns early -> 5), part 1 logs c0,c1,c4,c5 (4)
      if (c.length < 9) continue;
      split.push({ p0_step: c[3] - c[4], p0_handler: c[0] - c[4], p1_step: c[6] - c[7], p1_handler: c[5] - c[8], tx: l.cu });
    }
    out.maps[name + "_split"] = { fails: splitFails, final_tick: wSplit.tick, same_hash_as_full: null,
      p0_step: summary(split.map((r) => r.p0_step)), p0_handler: summary(split.map((r) => r.p0_handler)),
      p1_step: summary(split.map((r) => r.p1_step)), p1_handler: summary(split.map((r) => r.p1_handler)), tx: summary(split.map((r) => r.tx)) };
    console.log(name + "_split", JSON.stringify(out.maps[name + "_split"]));
    out.maps[name] = {
      fails,
      step: summary(rows.map((r) => r.step)), bots: summary(rows.map((r) => r.bots)), inputs: summary(rows.map((r) => r.inputs)),
      handler: summary(rows.map((r) => r.handler)), tx: summary(txcu), final_tick: w.tick, airborne_end: w.players.filter((p) => !p.grounded).length,
      default_limit_tick: { err: rd.err ?? rdLogs.err ?? null, cu: rdLogs.cu ?? null },
    };
    console.log(name, JSON.stringify(out.maps[name]));
    writeFileSync(join(LOGS, `p01-${NET}-${name}-rows.json`), JSON.stringify(rows));
  }
  // CU per collision primitive (n = 50 probes each; probe+touch, probe+finish(sqrt+normal)), and isqrt
  const NP = 50;
  const rb = await send(er, [cuLimit(1_400_000), IX2.benchPrims(new PublicKey(st.maps.run), NP)], [wallet], { pollMs: 10 });
  const lb = await logsOf(er, rb.sig);
  const L = lb.logs.filter((x) => x.includes("Program consumption:")).map((x) => Number(/consumption: (\d+)/.exec(x)![1]));
  const kinds = ["box", "prism", "sphere", "capsule"];
  out.prims = { n: NP, note: "CU per call incl. loop + ~100 CU log overhead amortised over n", err: rb.err ?? lb.err ?? null } as any;
  if (L.length >= 10) {
    kinds.forEach((k, i) => { out.prims[k] = { probe_touch: (L[2 * i] - L[2 * i + 1]) / NP, probe_finish: (L[2 * i + 1] - L[2 * i + 2]) / NP }; });
    out.prims.isqrt = (L[8] - L[9]) / NP;
  }
  console.log("prims", JSON.stringify(out.prims));
  out.load_after = loadavg();
  writeFileSync(join(LOGS, `p01-${NET}.json`), JSON.stringify(out, null, 2));
}
main().catch((e) => { console.error(e); process.exit(1); });
