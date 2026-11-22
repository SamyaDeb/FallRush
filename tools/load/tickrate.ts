// Ticks per second of World 0 over N seconds (authoritative account polling).
import { er, pda, sleep } from "./lib.ts";
const secs = Number(process.argv[2] ?? 10);
const get = async () => (await er.getAccountInfo(pda.world(0), "processed"))!.data.readUInt32LE(8 + 64);
const a = await get(); const t0 = Date.now(); await sleep(secs * 1000); const b = await get();
console.log(`world0 tick ${a} -> ${b}: ${((b - a) * 1000 / (Date.now() - t0)).toFixed(2)} Hz`);
process.exit(0);
