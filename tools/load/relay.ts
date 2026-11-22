// P0.4 fanout relay (untrusted helper): subscribes to World on the rollup and re-broadcasts the raw
// account bytes to every connected client over WebSocket. tsx relay.ts <port>
import wsPkg from "ws";
const WebSocketServer = (wsPkg as any).Server ?? (wsPkg as any).WebSocketServer;
import { er, pda } from "./lib.ts";
const port = Number(process.argv[2] ?? 8787);
const wss = new WebSocketServer({ port });
let n = 0;
er.onAccountChange(pda.world(0), (acc) => {
  n++;
  const b = acc.data as Buffer;
  for (const c of wss.clients) if (c.readyState === 1) c.send(b);
}, { commitment: "processed" });
setInterval(() => console.log(`relay: ${n} updates, ${wss.clients.size} clients`), 30000);
console.log(`relay listening on ${port}`);
