// P0.6 (sBPF): run_steps on the rollup (World 1), chunked under the 1.4M CU tx cap; compare with native.
// P0_NET=local tsx p06-sbpf.ts [seeds] [steps]
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { PublicKey } from "@solana/web3.js";
import { er, IX, LOGS, NET, wallet, pda, send, sendRetry, decodeWorld, cuLimit } from "./lib.ts";
const S = Number(process.argv[2] ?? 100), STEPS = Number(process.argv[3] ?? 200);
const st = JSON.parse(readFileSync(join(LOGS, `state-${NET}.json`), "utf8"));
const res: Record<string, string[]> = {};
let txs = 0, shrink = 0;
for (const name of ["lid", "run"]) {
  const map = new PublicKey(st.maps[name]);
  const nat = readFileSync(join(LOGS, `native_${name}.txt`), "utf8").split("\n").filter((l) => l && !l.startsWith("#"));
  res[name] = [];
  let same = 0;
  for (let seed = 1; seed <= S; seed++) {
    let done = 0;
    let chunk = 4;
    while (done < STEPS) {
      const n = Math.min(chunk, STEPS - done);
      const r = await (done === 0 && seed === 1 ? sendRetry : send)(er, [cuLimit(1_400_000 - (txs % 1000)), IX.runSteps(1, map, n, seed, done === 0)], [wallet]);
      txs++;
      if (r.err) { if (chunk > 1) { chunk = Math.max(1, chunk >> 1); shrink++; continue; } throw new Error(`run_steps ${name} ${seed} ${done}: ${r.err}`); }
      done += n;
    }
    const w = decodeWorld((await er.getAccountInfo(pda.world(1), "processed"))!.data as Buffer);
    const line = `${seed} ${w.hash.toString(16).padStart(16, "0")}`;
    res[name].push(line);
    if (line === nat[seed - 1]) same++;
    else console.log(`MISMATCH ${name} ${line} native ${nat[seed - 1]} tick ${w.tick}`);
  }
  console.log(`sbpf ${name}: ${same}/${S} identical to native`);
}
console.log(`txs ${txs}, chunk shrinks ${shrink}`);
writeFileSync(join(LOGS, `sbpf_${NET}.json`), JSON.stringify(res));
