// Minimal headless Chrome over CDP (Node built-in WebSocket), same approach as scripts/g47-webgl-measure.mjs.
import { spawn } from "node:child_process";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
const CHROME = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

export async function withChrome(url: string, onConsole: (text: string, t: number) => void, untilMs: number, done?: () => boolean) {
  const port = 9300 + Math.floor(Math.random() * 300);
  const prof = mkdtempSync(join(tmpdir(), "p0-chrome-"));
  const chrome = spawn(CHROME, ["--headless=new", `--remote-debugging-port=${port}`, `--user-data-dir=${prof}`, "--no-first-run", "--no-default-browser-check", "about:blank"], { stdio: "ignore" });
  try {
    let target: any;
    for (let i = 0; i < 60 && !target; i++) {
      await sleep(500);
      try { target = ((await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()) as any[]).find((t) => t.type === "page"); } catch {}
    }
    if (!target) throw new Error("chrome: no page");
    const ws = new WebSocket(target.webSocketDebuggerUrl);
    await new Promise((r) => (ws.onopen = r));
    let id = 0; const pending = new Map<number, (v: any) => void>();
    const send = (method: string, params: any = {}) => new Promise((res) => { const i = ++id; pending.set(i, res); ws.send(JSON.stringify({ id: i, method, params })); });
    ws.onmessage = (ev: any) => {
      const m = JSON.parse(ev.data);
      if (m.id && pending.has(m.id)) { pending.get(m.id)!(m.result ?? m.error); pending.delete(m.id); return; }
      if (m.method === "Runtime.consoleAPICalled") onConsole(m.params.args.map((a: any) => a.value ?? a.description ?? "").join(" "), Date.now());
      if (m.method === "Runtime.exceptionThrown") onConsole("EXC " + m.params.exceptionDetails.text + " " + (m.params.exceptionDetails.exception?.description ?? ""), Date.now());
    };
    await send("Runtime.enable");
    await send("Page.navigate", { url });
    const t0 = Date.now();
    while (Date.now() - t0 < untilMs && !(done && done())) await sleep(200);
    ws.close();
  } finally {
    chrome.kill();
    await sleep(500);
  }
}
