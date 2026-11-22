// P0.2 fallback: permissionless tick bot. World gap = 1 slot; a client sends tick() every `interval` ms.
// P0_NET=.. tsx p02-fallback.ts <intervalMs> <seconds> [map]
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { PublicKey } from "@solana/web3.js";
import { er, IX, LOGS, NET, wallet, pda, send, sendRetry, decodeWorld, summary, loadavg, sleep, cuLimit, key, SigPoller, sendVia } from "./lib.ts";

const interval = Number(process.argv[2] ?? 50);
const seconds = Number(process.argv[3] ?? 30);
const st = JSON.parse(readFileSync(join(LOGS, `state-${NET}.json`), "utf8"));
const map = new PublicKey(st.maps[process.argv[4] ?? "lid"]);
const ticker = key("bet-0"); // any funded keypair: tick() needs no authority

async function main() {
  await sendRetry(er, [IX.resetWorld(wallet.publicKey, 0, map, 13, 1, 1)], [wallet]);
  const w0 = decodeWorld((await er.getAccountInfo(pda.world(0)))!.data as Buffer);
  const errs: Record<string, number> = {};
  let ok = 0, n = 0;
  const t0 = Date.now();
  const pend: Promise<void>[] = [];
  const poller = new SigPoller(er, 25, 10000);
  while (Date.now() - t0 < seconds * 1000) {
    const i = n++;
    pend.push(sendVia(poller, [cuLimit(1_000_000 - i), IX.tickPart(0, map, 0), IX.tickPart(0, map, 1)], [ticker]).then((r) => {
      if (r.err) { const k = r.err.includes("0x1773") || r.err.includes("6003") ? "NotDue" : r.err.slice(0, 60); errs[k] = (errs[k] ?? 0) + 1; } else ok++;
    }));
    await sleep(interval);
  }
  await Promise.all(pend);
  poller.stop();
  const w1 = decodeWorld((await er.getAccountInfo(pda.world(0)))!.data as Buffer);
  const secs = (Date.now() - t0) / 1000;
  const out = { net: NET, note: "tick bot sends tick_part 0+1 every interval ms; World.min_slot_gap = 1", interval, seconds, sent: n, ok, errs, ticks: w1.tick - w0.tick, rate_hz: (w1.tick - w0.tick) / secs, load: loadavg() };
  console.log(JSON.stringify(out));
  writeFileSync(join(LOGS, `p02-${NET}-fallback-${interval}.json`), JSON.stringify(out, null, 2));
  await sendRetry(er, [IX.resetWorld(wallet.publicKey, 0, map, 13, 1, 0)], [wallet]);
}
main().catch((e) => { console.error(e); process.exit(1); });
