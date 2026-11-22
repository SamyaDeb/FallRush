// Tiny static server for page.html + wasm + map blobs. Returns the port.
import { createServer } from "node:http";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { LOGS, ROOT } from "./lib.ts";
export function serve(): Promise<number> {
  const here = new URL(".", import.meta.url).pathname;
  const srv = createServer((req, res) => {
    const u = new URL(req.url!, "http://x");
    try {
      if (u.pathname === "/" || u.pathname === "/page.html") { res.setHeader("content-type", "text/html"); res.end(readFileSync(join(here, "page.html"))); }
      else if (u.pathname === "/wasm") { res.setHeader("content-type", "application/wasm"); res.end(readFileSync(join(ROOT, "target/wasm32-unknown-unknown/release/p0_sim_wasm.wasm"))); }
      else if (u.pathname.startsWith("/map_")) res.end(readFileSync(join(LOGS, `${u.pathname.slice(1)}.bin`)));
      else { res.statusCode = 404; res.end(); }
    } catch { res.statusCode = 500; res.end(); }
  });
  return new Promise((r) => srv.listen(0, "127.0.0.1", () => r((srv.address() as any).port)));
}
