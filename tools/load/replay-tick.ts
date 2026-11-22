// Fast-forward World 0 with run_steps (all bots, same as tick() with no human inputs) to tick N, then
// run one split tick with a 1.4M limit and print the CU of each part. P0_NET=.. tsx replay-tick.ts <map> <seed> <N>
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { PublicKey } from "@solana/web3.js";
import { er, IX, LOGS, NET, wallet, pda, send, sendRetry, decodeWorld, logsOf, cuLimit } from "./lib.ts";
const st = JSON.parse(readFileSync(join(LOGS, `state-${NET}.json`), "utf8"));
const map = new PublicKey(st.maps[process.argv[2]]);
const seed = Number(process.argv[3]), N = Number(process.argv[4]);
await sendRetry(er, [IX.resetWorld(wallet.publicKey, 0, map, seed, 1, 0)], [wallet]);
let done = 0, k = 0, chunk = 4;
while (done < N) {
  const n = Math.min(chunk, N - done);
  const r = await send(er, [cuLimit(1_400_000 - (k++ % 1000)), IX.runSteps(0, map, n, seed, false)], [wallet]);
  if (r.err) { if (chunk > 1) { chunk >>= 1; continue; } console.log("run_steps err", r.err); process.exit(1); }
  done += n;
  if (k % 50 === 0) chunk = 4;
}
const w = decodeWorld((await er.getAccountInfo(pda.world(0), "processed"))!.data as Buffer);
console.log("world tick", w.tick);
for (let i = 0; i < 4; i++) {
  const r = await send(er, [cuLimit(1_400_000 - i), IX.tickPart(0, map, 0), IX.tickPart(0, map, 1)], [wallet], { pollMs: 20 });
  const l = await logsOf(er, r.sig);
  const c = l.logs.filter((x) => / consumed \d+ of/.test(x)).map((x) => Number(/consumed (\d+)/.exec(x)![1]));
  console.log(`tick ${w.tick + i} -> ${w.tick + i + 1}: part0 ${c[0]} part1 ${c[1]} total ${c[0] + c[1]} err ${r.err ?? null}`);
}
process.exit(0);
