#!/usr/bin/env node
// P3-H/N (locked): drive Chrome over the DevTools protocol (Node's built-in WebSocket, no packages) on a local WebGL
// build: records every console line, a screenshot every SHOT seconds, JS heap (Performance.getMetrics) every 5 s, and
// optionally hides the tab (Page.setWebLifecycleState frozen = what a background tab gets) for HIDE seconds at AT s.
// Ends on DONE_RE in the console or after MAX seconds.
//   node scripts/p3-webgl-measure.mjs <url> <outDir> [maxS=300] [--shot=15] [--done=<regex>] [--hide=AT:SECS]
import { spawn } from "node:child_process";
import { mkdirSync, writeFileSync, appendFileSync } from "node:fs";
import { join } from "node:path";

const pos = process.argv.slice(2).filter((a) => !a.startsWith("--"));
const opt = (k, d) => (process.argv.find((a) => a.startsWith(`--${k}=`)) ?? `--${k}=${d}`).split("=").slice(1).join("=");
const [url, outDir, maxS = "300"] = pos;
const shotS = Number(opt("shot", "15"));
const doneRe = new RegExp(opt("done", "AUTO DONE|\\[rcselftest\\] (PASS|FAIL) "));
const hide = opt("hide", "");
// --profile=<dir>: a persistent Chrome profile (keeps the page's localStorage burner between devnet runs)
const profile = opt("profile", "") || join(outDir, "profile");
const keepProfile = opt("profile", "") !== "";
// --play=x,y: click PLAY once the menu is up (CSS px of the 1440 × 900 viewport; G4.7's 585,492)
const play = opt("play", "");
// --until=<file>:<text>: also end when <file> contains <text> (e.g. the game server's COMPLETE_MATCH)
const until = opt("until", "");
mkdirSync(join(outDir, "shots"), { recursive: true });
const log = join(outDir, "console.log");
writeFileSync(log, "");
const CHROME = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
const port = 9334;
const chrome = spawn(CHROME, [`--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, "--no-first-run",
  "--no-default-browser-check", "--window-size=1440,1000", "--autoplay-policy=no-user-gesture-required", "about:blank"], { stdio: "ignore" });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let target;
for (let i = 0; i < 60 && !target; i++) { await sleep(500); try { target = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find((t) => t.type === "page"); } catch {} }
if (!target) { console.error("chrome did not expose a page"); chrome.kill(); process.exit(1); }
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((r) => (ws.onopen = r));
let id = 0; const pending = new Map();
const send = (method, params = {}) => new Promise((res) => { const i = ++id; pending.set(i, res); ws.send(JSON.stringify({ id: i, method, params })); });
let done = false;
const t0 = Date.now();
const T = () => ((Date.now() - t0) / 1000).toFixed(1);
ws.onmessage = (ev) => {
  const m = JSON.parse(ev.data);
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m.result ?? m.error); pending.delete(m.id); return; }
  if (m.method === "Runtime.consoleAPICalled") {
    const text = m.params.args.map((a) => a.value ?? a.description ?? "").join(" ");
    appendFileSync(log, `${T()}\t${m.params.type}\t${text}\n`);
    if (doneRe.test(text)) done = done || text;
  } else if (m.method === "Runtime.exceptionThrown") {
    appendFileSync(log, `${T()}\texception\t${m.params.exceptionDetails.text} ${m.params.exceptionDetails.exception?.description ?? ""}\n`);
  }
};
await send("Runtime.enable"); await send("Page.enable"); await send("Performance.enable");
await send("Emulation.setDeviceMetricsOverride", { width: 1440, height: 900, deviceScaleFactor: 1, mobile: false });
await send("Page.navigate", { url });
let n = 0, lastShot = 0, lastHeap = 0, hidden = false, played = 0;
const [hideAt, hideFor] = hide ? hide.split(":").map(Number) : [0, 0];
while (!done && (Date.now() - t0) / 1000 < Number(maxS)) {
  await sleep(1000);
  const s = (Date.now() - t0) / 1000;
  if (play && played < 3 && s > [14, 32, 52][played]) {
    played++; // P4: up to 3 clicks (a menu not interactive yet at the first one)
    const [x, y] = play.split(",").map(Number);
    for (const type of ["mouseMoved", "mousePressed", "mouseReleased"]) await send("Input.dispatchMouseEvent", { type, x, y, button: "left", clickCount: 1 });
    appendFileSync(log, `${T()}\tclick\tPLAY at ${x},${y}\n`);
  }
  if (until) { const [f, ...t] = until.split(":"); try { if ((await import("node:fs")).readFileSync(f, "utf8").includes(t.join(":"))) { done = `until ${t.join(":")}`; await sleep(15000); } } catch {} }
  if (hide && !hidden && s >= hideAt) { hidden = true; appendFileSync(log, `${T()}\thide\tfrozen for ${hideFor} s\n`); await send("Page.setWebLifecycleState", { state: "frozen" }); await sleep(hideFor * 1000); await send("Page.setWebLifecycleState", { state: "active" }); await send("Page.bringToFront"); await send("Emulation.setFocusEmulationEnabled", { enabled: true }); appendFileSync(log, `${T()}\thide\tactive again\n`); }
  if (s - lastHeap >= 5) { lastHeap = s; const m = await send("Performance.getMetrics"); const h = (m?.metrics ?? []).find((x) => x.name === "JSHeapUsedSize"); appendFileSync(log, `${T()}\theap\tJSHeapUsedSize ${h ? (h.value / 1048576).toFixed(1) : "?"} MB\n`); }
  if (s - lastShot >= shotS) { lastShot = s; const r = await send("Page.captureScreenshot", { format: "jpeg", quality: 75 }); if (r?.data) writeFileSync(join(outDir, "shots", `${String(++n).padStart(3, "0")}-${Math.round(s)}s.jpg`), Buffer.from(r.data, "base64")); }
}
await sleep(3000);
console.log(`[p3-webgl] ${done ? "done: " + String(done).slice(0, 160) : "time limit"} after ${Math.round((Date.now() - t0) / 1000)} s, ${n} shots → ${outDir}`);
ws.close(); chrome.kill(); await sleep(1500); if (!keepProfile) { try { (await import("node:fs")).rmSync(profile, { recursive: true, force: true }); } catch {} } process.exit(0);
