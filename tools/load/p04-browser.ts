// P0.4 (browser): headless Chrome subscribes to World directly (raw accountSubscribe) and via the relay.
// Needs a running timed task + relay. P0_NET=.. tsx p04-browser.ts <seconds> <relayPort>
import { writeFileSync } from "node:fs";
import { join } from "node:path";
import { EP, LOGS, NET, pda, summary } from "./lib.ts";
import { serve } from "./serve.ts";
import { withChrome } from "./chrome.ts";
const seconds = Number(process.argv[2] ?? 60);
const relayPort = Number(process.argv[3] ?? 8787);
const port = await serve();
const res: any = { net: NET, seconds };
for (const [mode, ws] of [["sub", EP.erWs], ["relay", `ws://127.0.0.1:${relayPort}`]] as const) {
  const recv = new Map<number, number>();
  let n = 0;
  await withChrome(`http://127.0.0.1:${port}/page.html?mode=${mode}&ws=${encodeURIComponent(ws)}&acct=${pda.world(0).toBase58()}`, (t) => {
    const m = /^recv (\d+) (\d+)/.exec(t);
    if (m) { n++; if (!recv.has(+m[1])) recv.set(+m[1], +m[2]); } else if (t.startsWith("EXC")) console.log(t);
  }, seconds * 1000 + 3000);
  const ticks = [...recv.keys()].sort((a, b) => a - b).slice(2);
  const gaps: number[] = [];
  let missing = 0;
  for (let i = 1; i < ticks.length; i++) { gaps.push(recv.get(ticks[i])! - recv.get(ticks[i - 1])!); missing += ticks[i] - ticks[i - 1] - 1; }
  const span = ticks.length > 1 ? (recv.get(ticks[ticks.length - 1])! - recv.get(ticks[0])!) / 1000 : 1;
  res[mode] = { updates: n, unique_ticks: ticks.length, missing_ticks: missing, updates_per_s: (ticks.length - 1) / span, gap_ms: summary(gaps), gaps_over_250: gaps.filter((g) => g > 250).length };
  console.log(mode, JSON.stringify(res[mode]));
}
writeFileSync(join(LOGS, `p04-${NET}-browser.json`), JSON.stringify(res, null, 2));
process.exit(0);
