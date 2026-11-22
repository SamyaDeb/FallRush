// P0.6 (WASM in Chrome): same hashes from the browser.
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { LOGS } from "./lib.ts";
import { serve } from "./serve.ts";
import { withChrome } from "./chrome.ts";
const port = await serve();
const lines: string[] = [];
let done = false;
await withChrome(`http://127.0.0.1:${port}/page.html?mode=wasm&seeds=100&steps=200`, (t) => { if (t.startsWith("hash ")) lines.push(t); if (t === "wasm done") done = true; if (t.startsWith("EXC")) console.log(t); }, 300000, () => done);
writeFileSync(join(LOGS, "wasm_chrome.txt"), lines.join("\n"));
for (const name of ["lid", "run"]) {
  const nat = readFileSync(join(LOGS, `native_${name}.txt`), "utf8").split("\n").filter((l) => l && !l.startsWith("#"));
  const mine = lines.filter((l) => l.startsWith(`hash ${name} `)).map((l) => l.slice(`hash ${name} `.length));
  console.log(`wasm-chrome ${name}: ${mine.filter((l, i) => l === nat[i]).length}/${mine.length} identical to native`);
}
process.exit(0);
