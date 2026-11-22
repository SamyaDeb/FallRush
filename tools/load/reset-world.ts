import { readFileSync } from "node:fs";
import { join } from "node:path";
import { PublicKey } from "@solana/web3.js";
import { er, IX, LOGS, NET, wallet, sendRetry } from "./lib.ts";
const st = JSON.parse(readFileSync(join(LOGS, `state-${NET}.json`), "utf8"));
const r = await sendRetry(er, [IX.resetWorld(wallet.publicKey, 0, new PublicKey(st.maps[process.argv[2] ?? "lid"]), Number(process.argv[3] ?? 3), 1, 0)], [wallet]);
console.log("reset", r.err ?? "ok");
process.exit(0);
