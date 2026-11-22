// Manually run tick_part 0+1 (and a full tick) on the current World and print errors / CU / logs.
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { PublicKey } from "@solana/web3.js";
import { er, IX, LOGS, NET, wallet, pda, send, decodeWorld, logsOf, cuLimit } from "./lib.ts";
const st = JSON.parse(readFileSync(join(LOGS, `state-${NET}.json`), "utf8"));
const map = new PublicKey(st.maps[process.argv[2] ?? "lid"]);
const w = decodeWorld((await er.getAccountInfo(pda.world(0), "processed"))!.data as Buffer);
console.log("world tick", w.tick, "map", w.map.toBase58(), "part_pending", (await er.getAccountInfo(pda.world(0)))!.data[8 + 957]);
for (const [label, ixs] of [["split default", [IX.tickPart(0, map, 0), IX.tickPart(0, map, 1)]], ["split 1.4M", [cuLimit(1_400_000), IX.tickPart(0, map, 0), IX.tickPart(0, map, 1)]]] as const) {
  const r = await send(er, ixs as any, [wallet], { pollMs: 20 });
  const l = r.sig ? await logsOf(er, r.sig) : { logs: [], cu: undefined, err: r.err };
  console.log(label, "err", r.err ?? null, "cu", l.cu);
  console.log(l.logs.filter((x) => !x.includes("consumption")).slice(-8).join("\n"));
}
process.exit(0);
