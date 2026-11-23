// P5 indexer / archive + small web API (container fallrush-indexer; stateless helper, anyone can run it).
//   GET  /api/status              every game server + its arena keeper (players, phase, arena, match PDA), cached 1 s
//   GET  /api/matches             the archived matches, newest first (summary fields)
//   GET  /api/match/:id           one match: the keeper summary (signatures, markets, drops, receipt) + tx lists
//   GET  /api/replay/:id/:file    world2.bin | ticks.json | classic.jsonl | keeper.jsonl (the replay's inputs)
//   POST /api/gas  {burner}       guest gas: a small devnet SOL top-up for a new burner (the only signer here: FUNDER)
//   POST /api/faucet-gate {burner} the per-IP / per-session soft limit for the chips faucet (signs nothing; on-chain
//                                 the faucet is limited per wallet only)
// The archive lives in ARCHIVE/<match PDA>/ (written by the keepers); this service adds txs.json (bet / drop / market
// signatures read from the rollup) and prunes to the newest KEEP matches (its own directory only).
//   ARCHIVE=/archive KEEPERS="gs1=http://fallrush-keeper-1:3111,gs2=…" GS_PUBLIC="gs1=wss://gs1…,gs2=…" \
//   FUNDER_KEYPAIR=/run/secrets/funder.json node --experimental-strip-types tools/indexer/server.ts
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import { readFileSync, existsSync, readdirSync, statSync, writeFileSync, rmSync, createReadStream } from "node:fs";
import { join } from "node:path";
import { Connection, Keypair, PublicKey, SystemProgram, Transaction } from "@solana/web3.js";

const PORT = Number(process.env.PORT ?? 3112);
const ARCHIVE = process.env.ARCHIVE ?? "/archive";
const KEEP = Number(process.env.KEEP ?? 200);
const parseMap = (s: string | undefined) => Object.fromEntries((s ?? "").split(",").filter(Boolean).map((x) => x.split("=").map((y) => y.trim())));
const KEEPERS: Record<string, string> = parseMap(process.env.KEEPERS);
const GS_PUBLIC: Record<string, string> = parseMap(process.env.GS_PUBLIC);
const ER = new Connection(process.env.ER_RPC_URL ?? "https://devnet-as.magicblock.app", "confirmed");
const BASE = new Connection(process.env.SOLANA_RPC_URL ?? "https://api.devnet.solana.com", "confirmed");
const PROGRAM = new PublicKey(process.env.FALL_RUSH_PROGRAM_ID ?? "4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4");
const GAS_LAMPORTS = Number(process.env.GAS_LAMPORTS ?? 12_000_000);
const GAS_PER_IP_DAY = Number(process.env.GAS_PER_IP_DAY ?? 4);
const GAS_PER_HOUR = Number(process.env.GAS_PER_HOUR ?? 60);
const FAUCET_PER_IP_DAY = Number(process.env.FAUCET_PER_IP_DAY ?? 6);
const funder: Keypair | null = (() => {
  const p = process.env.FUNDER_KEYPAIR;
  if (!p) return null;
  try { return Keypair.fromSecretKey(Uint8Array.from(JSON.parse(p.trim().startsWith("[") ? p : readFileSync(p, "utf8")))); } catch { return null; }
})();
const LEDGER = join(ARCHIVE, "gas-ledger.json");
const ledger: { burners: Record<string, number>; ip: Record<string, number[]>; faucet: Record<string, number[]>; sent: number[] } =
  existsSync(LEDGER) ? JSON.parse(readFileSync(LEDGER, "utf8")) : { burners: {}, ip: {}, faucet: {}, sent: [] };
const saveLedger = () => { try { writeFileSync(LEDGER, JSON.stringify(ledger)); } catch {} };
const DAY = 86_400_000;

// ------------------------------------------------------------------ status (cached)
let statusCache: { at: number; body: any } = { at: 0, body: null };
async function status() {
  if (Date.now() - statusCache.at < 1000 && statusCache.body) return statusCache.body;
  const servers = await Promise.all(Object.entries(KEEPERS).map(async ([id, url]) => {
    try {
      const r = await fetch(`${url}/status`, { signal: AbortSignal.timeout(1500) });
      const k: any = await r.json();
      const sv = k.server && Date.now() - k.server.t < 10_000 ? k.server : null;
      return {
        id, url: GS_PUBLIC[id] ?? null, up: !!sv, arena: k.arena, arenaKey: k.arenaKey, world: k.world,
        keeper: { stage: k.stage, since: k.since, alive: Date.now() - (k.heartbeat ?? 0) < 30_000, match: k.match, worldTick: k.worldTick, worldPhase: k.worldPhase, worldRound: k.worldRound, last: k.last },
        phase: sv?.phase ?? "offline", inMatch: sv?.inMatch ?? false, round: sv?.round ?? 0, humans: sv?.humans ?? 0, size: sv?.size ?? 12, matchHex: sv?.match ?? "", served: sv?.completed ?? 0,
      };
    } catch (e: any) { return { id, url: GS_PUBLIC[id] ?? null, up: false, phase: "offline", error: String(e?.message ?? e).slice(0, 120) }; }
  }));
  statusCache = { at: Date.now(), body: { ok: true, t: Date.now(), servers, program: PROGRAM.toBase58(), network: "devnet", rollup: "devnet-as.magicblock.app" } };
  return statusCache.body;
}

// ------------------------------------------------------------------ archive
function matchDirs(): { id: string; at: number }[] {
  if (!existsSync(ARCHIVE)) return [];
  return readdirSync(ARCHIVE).filter((d) => existsSync(join(ARCHIVE, d, "summary.json")))
    .map((d) => ({ id: d, at: (() => { try { return JSON.parse(readFileSync(join(ARCHIVE, d, "summary.json"), "utf8")).doneAt ?? 0; } catch { return statSync(join(ARCHIVE, d)).mtimeMs; } })() }))
    .sort((a, b) => b.at - a.at);
}
const readJson = (p: string) => { try { return JSON.parse(readFileSync(p, "utf8")); } catch { return null; } };
function brief(id: string) {
  const s = readJson(join(ARCHIVE, id, "summary.json"));
  if (!s) return null;
  const rc = s.receipt ?? {};
  return {
    match: id, arena: s.arena, doneAt: s.doneAt, startedAt: s.startedAt, champion: rc.champion, humanMask: rc.humanMask, flags: rc.flags, mintedMask: rc.mintedMask,
    finalOnChain: !!s.final, realTime: s.final?.realTime, drops: s.drops?.count ?? 0, pools: [0, 1, 2, 3, 4].map((k) => s[`market${k}`]?.pool ?? "0"),
    finalizeMode: s.finalizeMode, replay: existsSync(join(ARCHIVE, id, "world2.bin")),
  };
}
const ECON_SEEDS = (arenaId: number, arena: PublicKey) => [
  ...[0, 1, 2, 3, 4].map((k) => PublicKey.findProgramAddressSync([Buffer.from("market"), Buffer.from([arenaId]), Buffer.from([k])], PROGRAM)[0]),
  PublicKey.findProgramAddressSync([Buffer.from("drops"), arena.toBuffer()], PROGRAM)[0],
];
/** Bet / drop / settle signatures of a finished match from the rollup (once; OR-9: the rollup may not keep history). */
async function enrich(id: string) {
  const dir = join(ARCHIVE, id);
  if (existsSync(join(dir, "txs.json"))) return;
  const s = readJson(join(dir, "summary.json"));
  if (!s?.arenaKey) return;
  const [m0, m1, m2, m3, m4, dq] = ECON_SEEDS(s.arena, new PublicKey(s.arenaKey));
  const out: Record<string, any[]> = {};
  for (const [name, pk] of Object.entries({ champion: m0, finalWinner: m1, qualify1: m2, qualify2: m3, qualify3: m4, drops: dq })) {
    try {
      const sigs = await ER.getSignaturesForAddress(pk, { limit: 400 });
      out[name] = sigs.filter((x) => x.blockTime == null || (x.blockTime * 1000 >= s.startedAt - 60_000 && x.blockTime * 1000 <= s.doneAt + 60_000)).map((x) => ({ sig: x.signature, t: x.blockTime, err: !!x.err }));
    } catch (e: any) { out[name] = [{ error: String(e?.message ?? e).slice(0, 120) }]; }
    await new Promise((r) => setTimeout(r, 400)); // D-RPC: gentle on the public endpoints
  }
  writeFileSync(join(dir, "txs.json"), JSON.stringify({ at: Date.now(), ...out }));
}
async function housekeeping() {
  const ds = matchDirs();
  for (const d of ds.slice(0, 20)) await enrich(d.id).catch(() => {});
  for (const d of ds.slice(KEEP)) { try { rmSync(join(ARCHIVE, d.id), { recursive: true, force: true }); } catch {} }
}
setInterval(() => { housekeeping().catch(() => {}); }, 30_000);

// ------------------------------------------------------------------ gas + faucet gate
const ipOf = (req: IncomingMessage) => String(req.headers["x-real-ip"] ?? req.headers["x-forwarded-for"] ?? req.socket.remoteAddress ?? "?").split(",")[0].trim();
const recent = (xs: number[] | undefined, ms: number) => (xs ?? []).filter((t) => Date.now() - t < ms);
async function gas(ip: string, burner: string) {
  let pk: PublicKey;
  try { pk = new PublicKey(burner); } catch { return { ok: false, error: "bad burner" }; }
  if (!funder) return { ok: false, error: "gas station not configured" };
  const bal = await BASE.getBalance(pk).catch(() => -1);
  if (bal >= 5_000_000) return { ok: true, funded: false, balance: bal };
  if (ledger.burners[burner]) return { ok: false, error: "this guest wallet was already topped up", balance: bal };
  ledger.ip[ip] = recent(ledger.ip[ip], DAY);
  if (ledger.ip[ip].length >= GAS_PER_IP_DAY) return { ok: false, error: "daily guest limit for this network reached" };
  ledger.sent = recent(ledger.sent, 3_600_000);
  if (ledger.sent.length >= GAS_PER_HOUR) return { ok: false, error: "busy: try again in a few minutes" };
  const tx = new Transaction().add(SystemProgram.transfer({ fromPubkey: funder.publicKey, toPubkey: pk, lamports: GAS_LAMPORTS }));
  tx.feePayer = funder.publicKey;
  tx.recentBlockhash = (await BASE.getLatestBlockhash("confirmed")).blockhash;
  tx.sign(funder);
  const sig = await BASE.sendRawTransaction(tx.serialize(), { maxRetries: 3 });
  ledger.burners[burner] = Date.now(); ledger.ip[ip].push(Date.now()); ledger.sent.push(Date.now()); saveLedger();
  for (let i = 0; i < 40; i++) { const st = (await BASE.getSignatureStatuses([sig])).value[0]; if (st?.confirmationStatus === "confirmed" || st?.confirmationStatus === "finalized") break; await new Promise((r) => setTimeout(r, 500)); }
  return { ok: true, funded: true, sig, lamports: GAS_LAMPORTS };
}
function faucetGate(ip: string, burner: string) {
  ledger.faucet[ip] = recent(ledger.faucet[ip], DAY);
  const seen = ledger.faucet[ip].length;
  if (seen >= FAUCET_PER_IP_DAY) return { ok: false, error: "faucet: daily limit for this network (devnet test chips)", remaining: 0 };
  ledger.faucet[ip].push(Date.now()); saveLedger();
  return { ok: true, remaining: FAUCET_PER_IP_DAY - seen - 1, note: "soft limit in the web layer; on-chain the faucet is limited per wallet (1,000 RUSH / 24 h)", burner };
}

// ------------------------------------------------------------------ http
const send = (res: ServerResponse, code: number, body: any) => { res.writeHead(code, { "content-type": "application/json", "cache-control": "no-store" }); res.end(JSON.stringify(body)); };
const bodyOf = (req: IncomingMessage) => new Promise<any>((ok) => { let b = ""; req.on("data", (c) => { b += c; if (b.length > 4096) req.destroy(); }); req.on("end", () => { try { ok(JSON.parse(b || "{}")); } catch { ok({}); } }); });
const SAFE = /^[1-9A-HJ-NP-Za-km-z]{32,44}$/;
createServer(async (req, res) => {
  try {
    const u = new URL(req.url ?? "/", "http://x");
    const p = u.pathname.replace(/^\/api/, "");
    if (p === "/status") return send(res, 200, await status());
    if (p === "/health") return send(res, 200, { ok: true, funder: funder?.publicKey.toBase58() ?? null, funderLamports: funder ? await BASE.getBalance(funder.publicKey).catch(() => -1) : null, matches: matchDirs().length });
    if (p === "/matches") return send(res, 200, { ok: true, matches: matchDirs().slice(0, Number(u.searchParams.get("n") ?? 50)).map((d) => brief(d.id)).filter(Boolean) });
    let m = p.match(/^\/match\/([1-9A-HJ-NP-Za-km-z]{32,44})$/);
    if (m) {
      const dir = join(ARCHIVE, m[1]);
      const s = readJson(join(dir, "summary.json"));
      if (!s) return send(res, 404, { ok: false, error: "unknown match (not archived here)" });
      return send(res, 200, { ok: true, summary: s, txs: readJson(join(dir, "txs.json")), files: readdirSync(dir) });
    }
    m = p.match(/^\/replay\/([1-9A-HJ-NP-Za-km-z]{32,44})\/(world2\.bin|ticks\.json|classic\.jsonl|keeper\.jsonl)$/);
    if (m && SAFE.test(m[1])) {
      const f = join(ARCHIVE, m[1], m[2]);
      if (!existsSync(f)) return send(res, 404, { ok: false, error: "no such file" });
      res.writeHead(200, { "content-type": m[2].endsWith(".bin") ? "application/octet-stream" : "text/plain", "cache-control": "public, max-age=3600" });
      return void createReadStream(f).pipe(res);
    }
    if (p === "/gas" && req.method === "POST") { const b = await bodyOf(req); return send(res, 200, await gas(ipOf(req), String(b.burner ?? ""))); }
    if (p === "/faucet-gate" && req.method === "POST") { const b = await bodyOf(req); return send(res, 200, faucetGate(ipOf(req), String(b.burner ?? ""))); }
    return send(res, 404, { ok: false, error: "not found" });
  } catch (e: any) { return send(res, 500, { ok: false, error: String(e?.message ?? e).slice(0, 200) }); }
}).listen(PORT, "0.0.0.0", () => console.log(`[indexer] :${PORT} archive ${ARCHIVE} keepers ${Object.keys(KEEPERS).join(",")} funder ${funder?.publicKey.toBase58() ?? "-"}`));
