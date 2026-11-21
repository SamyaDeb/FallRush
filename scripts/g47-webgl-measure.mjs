#!/usr/bin/env node
// G4.7 LOCAL MEASUREMENT: drive Chrome over the DevTools protocol (no npm packages: Node's built-in WebSocket) against
// the local WebGL build (pnpm m1:up:mac). Forces a 1440 × 900 viewport at DPR 1 (the G2/G3 §D method), records every
// console line (the [probe] budget / frame / render lines, [gfx], errors) and takes a screenshot every SHOT_S seconds.
// Ends when the console says the match is over (champion → lobby) or after MAX_S seconds.
//
// usage: node scripts/g47-webgl-measure.mjs <url> <outDir> [maxSeconds=600] [shotEverySeconds=5] [--uncapped]
//   --uncapped: Chrome without vsync / the frame-rate limit (FPS = render cost; for A/B only, not the 60 FPS budget)
import { spawn } from "node:child_process";
import { mkdirSync, writeFileSync, appendFileSync } from "node:fs";
import { join } from "node:path";

const [url, outDir, maxS = "600", shotS = "5"] = process.argv.slice(2).filter((a) => !a.startsWith("--"));
const uncapped = process.argv.includes("--uncapped");
if (!url || !outDir) { console.error("usage: g47-webgl-measure.mjs <url> <outDir> [maxS] [shotS] [--uncapped]"); process.exit(2); }
mkdirSync(join(outDir, "shots"), { recursive: true });
const log = join(outDir, "console.log");
writeFileSync(log, "");
const CHROME = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
const port = 9333;
const args = [`--remote-debugging-port=${port}`, `--user-data-dir=${join(outDir, "profile")}`, "--no-first-run", "--no-default-browser-check",
  "--window-size=1440,1000", "--autoplay-policy=no-user-gesture-required", "about:blank"];
if (uncapped) args.unshift("--disable-gpu-vsync", "--disable-frame-rate-limit");
const chrome = spawn(CHROME, args, { stdio: "ignore" });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let target;
for (let i = 0; i < 60 && !target; i++) {
  await sleep(500);
  try { target = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find((t) => t.type === "page"); } catch {}
}
if (!target) { console.error("chrome did not expose a page"); chrome.kill(); process.exit(1); }
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((r) => (ws.onopen = r));
let id = 0; const pending = new Map();
const send = (method, params = {}) => new Promise((res) => { const i = ++id; pending.set(i, res); ws.send(JSON.stringify({ id: i, method, params })); });
let done = false;
const t0 = Date.now();
ws.onmessage = (ev) => {
  const m = JSON.parse(ev.data);
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m.result ?? m.error); pending.delete(m.id); return; }
  if (m.method === "Runtime.consoleAPICalled") {
    const text = m.params.args.map((a) => a.value ?? a.description ?? "").join(" ");
    appendFileSync(log, `${((Date.now() - t0) / 1000).toFixed(1)}\t${m.params.type}\t${text}\n`);
    if (/match=Done|\[match\] champion|Champion .* COMPLETE|soak match/.test(text)) done = done || text;
  } else if (m.method === "Runtime.exceptionThrown") {
    appendFileSync(log, `${((Date.now() - t0) / 1000).toFixed(1)}\texception\t${m.params.exceptionDetails.text} ${m.params.exceptionDetails.exception?.description ?? ""}\n`);
  }
};
await send("Runtime.enable");
await send("Page.enable");
await send("Emulation.setDeviceMetricsOverride", { width: 1440, height: 900, deviceScaleFactor: 1, mobile: false });
await send("Page.navigate", { url });
let n = 0;
while (!done && (Date.now() - t0) / 1000 < Number(maxS)) {
  await sleep(Number(shotS) * 1000);
  const r = await send("Page.captureScreenshot", { format: "jpeg", quality: 80 });
  if (r?.data) writeFileSync(join(outDir, "shots", `${String(++n).padStart(3, "0")}-${Math.round((Date.now() - t0) / 1000)}s.jpg`), Buffer.from(r.data, "base64"));
}
await sleep(12000); // the last budget window
console.log(`[g47-webgl] ${done ? "match over: " + String(done).slice(0, 120) : "time limit"} after ${Math.round((Date.now() - t0) / 1000)} s, ${n} shots → ${outDir}`);
ws.close(); chrome.kill(); await sleep(1000); process.exit(0);
