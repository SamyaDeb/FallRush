// P5 indexer / archive + small web API (container fallrush-indexer; stateless helper, anyone can run it).
//   GET  /api/status              every game server + its arena keeper (players, phase, arena, match PDA), cached 1 s
//   GET  /api/matches             the archived matches, newest first (summary fields)
//   GET  /api/match/:id           one match: the keeper summary (signatures, markets, drops, receipt) + tx lists
//   GET  /api/replay/:id/:file    world2.bin | ticks.json | classic.jsonl | keeper.jsonl (the replay's inputs)
//   GET  /api/season?id=&wallet=  P6: the on-chain Season board (+ a PlayerSeason) and the same boards rebuilt from the archive
//   GET  /api/seasons             P6: the seasons the archived matches were recorded into
//   POST /api/gas  {burner}       guest gas: a small devnet SOL top-up for a new burner (the only signer here: FUNDER)
//   POST /api/faucet-gate {burner} the per-IP / per-session soft limit for the chips faucet (signs nothing; on-chain
//                                 the faucet is limited per wallet only)
//   GET  /api/features            P7-UX: {rooms, mm} kill switches read by the game's play hub
//   POST /api/room {botMix}       P7-UX: a private room: reserves an idle server (an arena while another arena stays
//                                 public, else a Classic-only server) and writes ROOMS/<gs>.json for it → {code, url}
//   GET  /api/room/:code          P7-UX: the room's server URL + state (invite links)
//   POST /api/mm {tier}           P7-UX: skill-tier matchmaking: the public lobby to join → {url, gs, reason, humans, size}
// The archive lives in ARCHIVE/<match PDA>/ (written by the keepers); this service adds txs.json (bet / drop / market
// signatures read from the rollup) and prunes to the newest KEEP matches (its own directory only).
//   ARCHIVE=/archive KEEPERS="gs1=http://fallrush-keeper-1:3111,gs2=…" GS_PUBLIC="gs1=wss://gs1…,gs2=…" \
//   FUNDER_KEYPAIR=/run/secrets/funder.json node --experimental-strip-types tools/indexer/server.ts
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import { readFileSync, existsSync, readdirSync, statSync, writeFileSync, rmSync, createReadStream, renameSync, mkdirSync } from "node:fs";
import { randomBytes } from "node:crypto";
import { join } from "node:path";
import { Connection, Keypair, PublicKey, SystemProgram, Transaction } from "@solana/web3.js";

const PORT = Number(process.env.PORT ?? 3112);
const ARCHIVE = process.env.ARCHIVE ?? "/archive";
const KEEP = Number(process.env.KEEP ?? 200);
const parseMap = (s: string | undefined) => Object.fromEntries((s ?? "").split(",").filter(Boolean).map((x) => x.split("=").map((y) => y.trim())));
const KEEPERS: Record<string, string> = parseMap(process.env.KEEPERS);
// P7-UX: servers without an arena keeper (Classic-only room servers) publish status.json through a shared volume
const STATUS_FILES: Record<string, string> = parseMap(process.env.STATUS_FILES);
const ROOMS = process.env.ROOMS ?? "";
const ROOMS_ON = process.env.ROOMS_ON !== "0" && !!ROOMS;
const MM_ON = process.env.MM_ON !== "0";
const GS_PUBLIC: Record<string, string> = parseMap(process.env.GS_PUBLIC);
const ER = new Connection(process.env.ER_RPC_URL ?? "https://devnet-as.magicblock.app", "confirmed");
const BASE = new Connection(process.env.SOLANA_RPC_URL ?? "https://api.devnet.solana.com", "confirmed");
const PROGRAM = new PublicKey(process.env.FALL_RUSH_PROGRAM_ID ?? "4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4");
const GAS_LAMPORTS = Number(process.env.GAS_LAMPORTS ?? 12_000_000);
const GAS_PER_IP_DAY = Number(process.env.GAS_PER_IP_DAY ?? 20);
const GAS_PER_HOUR = Number(process.env.GAS_PER_HOUR ?? 60);
const FAUCET_PER_IP_DAY = Number(process.env.FAUCET_PER_IP_DAY ?? 20);
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
        keeper: { stage: k.stage, since: k.since, alive: Date.now() - (k.heartbeat ?? 0) < 30_000, match: k.match, dealSig: k.dealSig ?? null, worldTick: k.worldTick, worldPhase: k.worldPhase, worldRound: k.worldRound, last: k.last },
        phase: sv?.phase ?? "offline", inMatch: sv?.inMatch ?? false, round: sv?.round ?? 0, humans: sv?.humans ?? 0, size: sv?.size ?? 12, matchHex: sv?.match ?? "", served: sv?.completed ?? 0,
        room: !!sv?.room, lobbyTier: sv?.lobbyTier ?? null,
      };
    } catch (e: any) { return { id, url: GS_PUBLIC[id] ?? null, up: false, phase: "offline", error: String(e?.message ?? e).slice(0, 120) }; }
  }));
  for (const [id, file] of Object.entries(STATUS_FILES)) {
    const sv0 = readJson(file);
    const sv = sv0 && Date.now() - sv0.t < 10_000 ? sv0 : null;
    servers.push({ id, url: GS_PUBLIC[id] ?? null, up: !!sv, arena: null, classic: true, phase: sv?.phase ?? "offline", inMatch: sv?.inMatch ?? false, round: sv?.round ?? 0, humans: sv?.humans ?? 0, size: sv?.size ?? 12, matchHex: sv?.match ?? "", served: sv?.completed ?? 0, room: !!sv?.room, lobbyTier: sv?.lobbyTier ?? null } as any);
  }
  // P7-UX: a private room's server is never offered to the public (its code is never exposed)
  for (const sv of servers as any[]) { const r = roomOf(sv.id); sv.room = !!r || !!sv.room; }
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
    // P6: aborted (status 6) / no human and no on-chain Final (hidden from the default lists), sealed bets
    status: rc.status, aborted: rc.status === 6, hidden: !rc.humanMask && !s.final, sealed: s.sealed ? { n: s.sealed.n ?? 0, revealed: s.sealed.revealed ?? 0, forfeits: s.sealed.forfeits ?? 0 } : null,
  };
}

// ------------------------------------------------------------------ P6 seasons: the on-chain board + a rebuild from the archive
const u32le = (n: number) => { const b = Buffer.alloc(4); b.writeUInt32LE(n); return b; };
const seasonPda = (id: number) => PublicKey.findProgramAddressSync([Buffer.from("season"), u32le(id)], PROGRAM)[0];
const pseasonPda = (id: number, w: PublicKey) => PublicKey.findProgramAddressSync([Buffer.from("pseason"), u32le(id), w.toBuffer()], PROGRAM)[0];
function isoWeek(ts: number) {
  const days = Math.floor(ts / 86_400), wd = ((days + 3) % 7 + 7) % 7, th = days - wd + 3, y = new Date(th * 86_400_000).getUTCFullYear();
  return y * 100 + Math.floor((th - Math.floor(Date.UTC(y, 0, 1) / 86_400_000)) / 7) + 1;
}
const board = (d: Buffer, o: number) => Array.from({ length: 10 }, (_, i) => ({ wallet: new PublicKey(d.subarray(o + 40 * i, o + 40 * i + 32)).toBase58(), score: d.readBigInt64LE(o + 40 * i + 32).toString() })).filter((e) => e.wallet !== PublicKey.default.toBase58());
function decodeSeason(d: Buffer) {
  const b = 8;
  return { id: d.readUInt32LE(b), status: d[b + 4], trophies: d[b + 5], test: d[b + 7], startsAt: Number(d.readBigInt64LE(b + 8)), endsAt: Number(d.readBigInt64LE(b + 16)), nMatches: d.readUInt32LE(b + 24), nPlayers: d.readUInt32LE(b + 28), champions: board(d, b + 32), predictors: board(d, b + 432), dropMvps: board(d, b + 832) };
}
function decodePSeason(d: Buffer) {
  const b = 8, u16 = (o: number) => d.readUInt16LE(b + o);
  return { wallet: new PublicKey(d.subarray(b, b + 32)).toBase58(), season: d.readUInt32LE(b + 32), achievements: d.readUInt32LE(b + 36), net: d.readBigInt64LE(b + 40).toString(), crowns: u16(48), finals: u16(50), roundsSurvived: u16(52), matches: u16(54), betsWon: u16(56), betsLost: u16(58), drops: u16(60) };
}
const seasonCache = new Map<number, { at: number; body: any }>();
async function season(id: number) {
  const c = seasonCache.get(id);
  if (c && Date.now() - c.at < 10_000) return c.body;
  const k = seasonPda(id);
  const i = await BASE.getAccountInfo(k).catch(() => null);
  const onChain = i && i.owner.equals(PROGRAM) ? decodeSeason(i.data as Buffer) : null;
  const rb = rebuild(id);
  // P7: the same entries with the same scores; the order of equal scores is a tie-break detail (the program keeps
  // insertion order, the rebuild sorts), so compare as sets of (wallet, score)
  const key = (xs: any[]) => xs.map((x) => `${x.wallet}:${x.score}`).sort().join(",");
  const same = (a: any[], b: any[]) => a.length === b.length && key(a) === key(b);
  const check = onChain ? { champions: same(onChain.champions, rb.champions), predictors: same(onChain.predictors, rb.predictors), dropMvps: same(onChain.dropMvps, rb.dropMvps) } : null;
  const body = { ok: true, id, account: k.toBase58(), onChain, rebuild: rb, matches: check ? check.champions && check.predictors && check.dropMvps : null, check };
  seasonCache.set(id, { at: Date.now(), body });
  return body;
}
/** The same boards rebuilt from the archived keeper summaries of matches recorded into `id` (markets, drops, receipt). */
function rebuild(id: number) {
  const ps = new Map<string, { crowns: number; finals: number; net: bigint; drops: number }>();
  const get = (w: string) => { let x = ps.get(w); if (!x) ps.set(w, (x = { crowns: 0, finals: 0, net: 0n, drops: 0 })); return x; };
  let n = 0;
  for (const d of matchDirs()) {
    const s = readJson(join(ARCHIVE, d.id, "summary.json"));
    const rec = s?.seasons?.[id];
    if (!rec || !rec.sigs?.length) continue;
    n++;
    const rc = s.receipt ?? {};
    const recorded = new Set<string>();
    for (let k = 0; k < 5; k++) for (const b of s[`market${k}`]?.bets ?? []) recorded.add(b.wallet);
    for (let sl = 0; sl < 12; sl++) if ((rc.humanMask >> sl) & 1) recorded.add(rc.wallets[sl]);
    for (const b of s.drops?.buys ?? []) recorded.add(b.by);
    for (const w of rec.walletList ?? recorded) {
      const x = get(w);
      for (let sl = 0; sl < 12; sl++) if ((rc.humanMask >> sl) & 1 && rc.wallets[sl] === w) {
        const st = s.chainWorker?.finalists ?? [];
        if (rc.champion === sl && rc.status === 5) x.crowns++;
        if (st.includes(sl) || rc.champion === sl) x.finals++;
      }
      for (let k = 0; k < 5; k++) {
        const m = s[`market${k}`];
        if (!m || m.status !== 3 || m.refund === 1) continue;
        for (const b of m.bets) if (b.wallet === w) {
          const won = (m.winners >> b.outcome) & 1 && BigInt(m.winningStake) > 0n ? (BigInt(b.amount) * BigInt(m.pool)) / BigInt(m.winningStake) : 0n;
          x.net += won - BigInt(b.amount);
        }
      }
      if (s.final) for (const b of s.drops?.buys ?? []) if (b.by === w) x.drops++;
    }
  }
  const top = (f: (x: any) => bigint) => [...ps.entries()].map(([wallet, x]) => ({ wallet, score: f(x) })).filter((e) => e.score > 0n).sort((a, b) => (b.score > a.score ? 1 : b.score < a.score ? -1 : 0)).slice(0, 10).map((e) => ({ wallet: e.wallet, score: e.score.toString() }));
  return { matches: n, champions: top((x) => BigInt(x.crowns * 1000 + x.finals)), predictors: top((x) => x.net), dropMvps: top((x) => BigInt(x.drops)) };
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

// ------------------------------------------------------------------ P7-UX rooms + matchmaking
// A room reserves one idle game server for a code. The server reads ROOMS/<gs>.json every second: while it is there the
// server admits only hellos carrying the code, starts on the host's Start (no auto countdown) and fills to 12 with bots
// of the room's mix. Rule: a room may take an arena only if another arena stays public; else a Classic-only server.
type Room = { code: string; gs: string; createdAt: number; expiresAt: number; botMix: number; seen: boolean };
const ROOM_IDLE_MS = 10 * 60_000, ROOM_LIVE_MS = 5 * 60_000, ROOMS_PER_IP_HOUR = Number(process.env.ROOMS_PER_IP_HOUR ?? 6);
const rooms = new Map<string, Room>(); // by code
const roomIp: Record<string, number[]> = {};
const ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
const roomOf = (gs: string) => { for (const r of rooms.values()) if (r.gs === gs) return r; return null; };
const PUBLIC_PHASES = new Set(["LobbyOpen", "Countdown"]);
function writeRoom(r: Room | null, gs: string) {
  if (!ROOMS) return;
  const f = join(ROOMS, `${gs}.json`);
  try {
    if (!r) { rmSync(f, { force: true }); return; }
    writeFileSync(f + ".tmp", JSON.stringify({ code: r.code, expiresAt: r.expiresAt, botMix: r.botMix }));
    renameSync(f + ".tmp", f);
  } catch (e: any) { console.warn("[rooms] write", gs, e?.message ?? e); }
}
function newCode() {
  for (;;) {
    let c = "";
    const b = randomBytes(4);
    for (let i = 0; i < 4; i++) c += ALPHABET[b[i] % ALPHABET.length];
    if (!rooms.has(c)) return c;
  }
}
/** Extends live rooms, ends idle ones (called every 2 s and before every room / mm decision). */
async function sweepRooms() {
  if (!rooms.size) return;
  const st = await status();
  for (const r of [...rooms.values()]) {
    const sv: any = st.servers.find((x: any) => x.id === r.gs);
    const live = sv?.up && (sv.humans > 0 || sv.inMatch);
    if (live) { r.seen = true; r.expiresAt = Date.now() + ROOM_LIVE_MS; writeRoom(r, r.gs); }
    if (Date.now() > r.expiresAt || (sv && !sv.up && Date.now() - r.createdAt > 60_000)) { rooms.delete(r.code); writeRoom(null, r.gs); console.log(`[rooms] ${r.code} on ${r.gs} ended`); }
  }
}
setInterval(() => { sweepRooms().catch(() => {}); }, 2_000);
// rooms live in memory: a restarted indexer opens every server to the public again (stale room files removed)
if (ROOMS) {
  try { mkdirSync(ROOMS, { recursive: true }); for (const f of readdirSync(ROOMS)) if (f.endsWith(".json")) rmSync(join(ROOMS, f), { force: true }); } catch (e: any) { console.warn("[rooms] init", e?.message ?? e); }
  console.log(`[rooms] dir ${ROOMS} ${ROOMS_ON ? "on" : "off"}; classic servers ${Object.keys(STATUS_FILES).join(",") || "-"}`);
}
const idle = (sv: any) => sv.up && sv.url && !sv.room && !sv.inMatch && PUBLIC_PHASES.has(sv.phase) && (sv.humans ?? 0) === 0;
async function createRoom(ip: string, botMix: number) {
  if (!ROOMS_ON) return { code: 503, body: { ok: false, error: "rooms are off right now" } };
  roomIp[ip] = recent(roomIp[ip], 3_600_000);
  if (roomIp[ip].length >= ROOMS_PER_IP_HOUR) return { code: 429, body: { ok: false, error: "too many rooms from this network: try again later" } };
  await sweepRooms();
  statusCache.at = 0;
  const st = await status();
  const arenas = st.servers.filter((x: any) => x.arena != null && !x.classic);
  const publicArenas = (except: string) => arenas.filter((x: any) => x.id !== except && x.up && !x.room).length;
  // 1. an idle arena (the Final runs on-chain) while another arena stays public; 2. an idle Classic-only server
  const pick = arenas.find((x: any) => idle(x) && publicArenas(x.id) >= 1) ?? st.servers.find((x: any) => x.classic && idle(x));
  if (!pick) return { code: 409, body: { ok: false, error: "every room is busy right now: try Quick Match, or again in a few minutes" } };
  const r: Room = { code: newCode(), gs: pick.id, createdAt: Date.now(), expiresAt: Date.now() + ROOM_IDLE_MS, botMix: Math.max(0, Math.min(3, botMix | 0)), seen: false };
  rooms.set(r.code, r);
  writeRoom(r, r.gs);
  roomIp[ip].push(Date.now());
  statusCache.at = 0;
  console.log(`[rooms] ${r.code} on ${r.gs} (${pick.classic ? "classic" : "arena " + pick.arena}) mix ${r.botMix}`);
  return { code: 200, body: { ok: true, code: r.code, url: pick.url, gs: pick.id, onchainFinal: !pick.classic, expiresAt: r.expiresAt } };
}
async function roomInfo(code: string) {
  const r = rooms.get(code.toUpperCase());
  if (!r) return { code: 404, body: { ok: false, error: "no room with that code (it may have ended)" } };
  const st = await status();
  const sv: any = st.servers.find((x: any) => x.id === r.gs);
  return { code: 200, body: { ok: true, code: r.code, url: sv?.url ?? null, gs: r.gs, onchainFinal: !sv?.classic, phase: sv?.phase ?? "offline", humans: sv?.humans ?? 0, inMatch: !!sv?.inMatch, expiresAt: r.expiresAt } };
}
const TIERS = ["Rookie", "Contender", "Champion"];
/** Skill-tier matchmaking over the public servers: same-tier lobby → empty server → nearest-tier lobby → watch the emptiest. */
async function matchmake(tier: string) {
  const t = Math.max(0, TIERS.indexOf(tier));
  await sweepRooms();
  const st = await status();
  const pub = st.servers.filter((x: any) => x.up && x.url && !x.room && !x.classic);
  const open = pub.filter((x: any) => !x.inMatch && PUBLIC_PHASES.has(x.phase) && x.humans < x.size);
  const out = (x: any, reason: string) => ({ ok: true, url: x.url, gs: x.id, reason, humans: x.humans, size: x.size, phase: x.phase, lobbyTier: x.lobbyTier ?? null });
  const same = open.filter((x: any) => x.humans > 0 && x.lobbyTier === TIERS[t]).sort((a: any, b: any) => b.humans - a.humans)[0];
  if (same) return out(same, "same-tier lobby");
  const empty = open.filter((x: any) => x.humans === 0)[0];
  if (empty) return out(empty, "new lobby");
  const near = open.filter((x: any) => x.humans > 0).sort((a: any, b: any) => Math.abs(TIERS.indexOf(a.lobbyTier) - t) - Math.abs(TIERS.indexOf(b.lobbyTier) - t) || b.humans - a.humans)[0];
  if (near) return out(near, "nearest-tier lobby");
  const watch = pub.sort((a: any, b: any) => a.humans - b.humans)[0];
  if (watch) return out(watch, "all lobbies busy: watch, then join the next one");
  return { ok: false, error: "no game server is up" };
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
    if (p === "/matches") {
      // P6: matches with no human and no on-chain Final (aborted bot-only lobbies) only with ?all=1
      const all = u.searchParams.get("all") === "1";
      const xs = matchDirs().map((d) => brief(d.id)).filter((x): x is NonNullable<ReturnType<typeof brief>> => !!x && (all || !x.hidden));
      return send(res, 200, { ok: true, matches: xs.slice(0, Number(u.searchParams.get("n") ?? 50)) });
    }
    if (p === "/season") {
      const id = Number(u.searchParams.get("id") ?? isoWeek(Date.now() / 1000));
      const body = await season(id);
      const w = u.searchParams.get("wallet");
      if (w && SAFE.test(w)) {
        const i = await BASE.getAccountInfo(pseasonPda(id, new PublicKey(w))).catch(() => null);
        return send(res, 200, { ...body, player: i && i.owner.equals(PROGRAM) ? decodePSeason(i.data as Buffer) : null, playerAccount: pseasonPda(id, new PublicKey(w)).toBase58() });
      }
      return send(res, 200, body);
    }
    if (p === "/seasons") {
      const ids = new Set<number>([isoWeek(Date.now() / 1000)]);
      for (const d of matchDirs().slice(0, 200)) for (const k of Object.keys(readJson(join(ARCHIVE, d.id, "summary.json"))?.seasons ?? {})) ids.add(Number(k));
      return send(res, 200, { ok: true, current: isoWeek(Date.now() / 1000), seasons: [...ids].sort((a, b) => b - a).map((id) => ({ id, account: seasonPda(id).toBase58(), test: id >= 1_000_000 })) });
    }
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
    if (p === "/features") return send(res, 200, { ok: true, rooms: ROOMS_ON, mm: MM_ON });
    if (p === "/room" && req.method === "POST") { const b = await bodyOf(req); const r = await createRoom(ipOf(req), Number(b.botMix ?? 1)); return send(res, r.code, r.body); }
    m = p.match(/^\/room\/([A-Za-z0-9]{4,8})$/);
    if (m) { const r = await roomInfo(m[1]); return send(res, r.code, r.body); }
    if (p === "/mm" && req.method === "POST") { const b = await bodyOf(req); return send(res, 200, await matchmake(String(b.tier ?? "Contender"))); }
    if (p === "/gas" && req.method === "POST") { const b = await bodyOf(req); return send(res, 200, await gas(ipOf(req), String(b.burner ?? ""))); }
    if (p === "/faucet-gate" && req.method === "POST") { const b = await bodyOf(req); return send(res, 200, faucetGate(ipOf(req), String(b.burner ?? ""))); }
    return send(res, 404, { ok: false, error: "not found" });
  } catch (e: any) { return send(res, 500, { ok: false, error: String(e?.message ?? e).slice(0, 200) }); }
}).listen(PORT, "0.0.0.0", () => console.log(`[indexer] :${PORT} archive ${ARCHIVE} keepers ${Object.keys(KEEPERS).join(",")} funder ${funder?.publicKey.toBase58() ?? "-"}`));
