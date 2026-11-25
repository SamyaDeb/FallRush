// P5 indexer / archive + small web API (container fallrush-indexer; stateless helper, anyone can run it).
//   GET  /api/status              every game server + its arena keeper (players, phase, arena, match PDA), cached 1 s
//   GET  /api/matches             the archived matches, newest first (summary fields) + counts {total, finalOnChain, allOnChain}
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
import { serverIdle, keeperIdle, idle, lowFlags, seasonPause, PUBLIC_PHASES, LOW_DEFAULTS } from "./policy.ts";

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
// submission: judge mode (POST /api/judge): a private room on an on-chain arena whose rounds 1–3 are fast-forwarded
const JUDGE_ON = process.env.JUDGE_ON !== "0" && ROOMS_ON;
const JUDGE_PER_IP_HOUR = Number(process.env.JUDGE_PER_IP_HOUR ?? 4);
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
type Ledger = { burners: Record<string, number>; ip: Record<string, number[]>; faucet: Record<string, number[]>; sent: number[] };
// beta audit: a corrupt ledger (killed mid-write) must not crash-loop the whole /api: start empty, keep the bad file aside
const ledger: Ledger = (() => {
  const empty: Ledger = { burners: {}, ip: {}, faucet: {}, sent: [] };
  if (!existsSync(LEDGER)) return empty;
  try { return { ...empty, ...JSON.parse(readFileSync(LEDGER, "utf8")) }; }
  catch (e: any) { console.warn("[gas] ledger unreadable, starting empty:", e?.message ?? e); try { renameSync(LEDGER, LEDGER + ".bad-" + Date.now()); } catch {} return empty; }
})();
// atomic (tmp + rename), and pruned so it cannot grow forever (old burners, empty per-IP lists)
const saveLedger = () => {
  try {
    const now = Date.now();
    for (const [k, t] of Object.entries(ledger.burners)) if (now - t > 7 * 86_400_000) delete ledger.burners[k];
    for (const m of [ledger.ip, ledger.faucet]) for (const [k, ts] of Object.entries(m)) { const r = ts.filter((t) => now - t < 86_400_000); if (r.length) m[k] = r; else delete m[k]; }
    writeFileSync(LEDGER + ".tmp", JSON.stringify(ledger)); renameSync(LEDGER + ".tmp", LEDGER);
  } catch {}
};
const DAY = 86_400_000;

// beta audit: /health used to hit the public RPC on every request (shared rate limit with the keepers)
const funderCache = { at: 0, v: null as number | null };
async function funderLamports() {
  if (!funder) return null;
  if (Date.now() - funderCache.at < 30_000 && funderCache.v != null) return funderCache.v;
  funderCache.at = Date.now();
  funderCache.v = await BASE.getBalance(funder.publicKey).catch(() => funderCache.v ?? -1);
  return funderCache.v;
}
// harden #1: the other two wallets the game spends from: the game authority (create + delegate every match; its pubkey
// from AUTHORITY_PUBKEY or a keeper's /status) and the global treasury PDA (Core asset rent for Crowns + capsules).
// lowBalance below 0.3 / 0.1 / 0.2 SOL (LOW_*_LAMPORTS); the watchdog logs a FAIL line on it (no restart).
const TREASURY = PublicKey.findProgramAddressSync([Buffer.from("treasury")], PROGRAM)[0];
const LOW = { authority: Number(process.env.LOW_AUTHORITY_LAMPORTS ?? LOW_DEFAULTS.authority), treasury: Number(process.env.LOW_TREASURY_LAMPORTS ?? LOW_DEFAULTS.treasury), funder: Number(process.env.LOW_FUNDER_LAMPORTS ?? LOW_DEFAULTS.funder) };
const balCache = new Map<string, { at: number; v: number }>();
async function lamportsOf(k: PublicKey | null) {
  if (!k) return null;
  const id = k.toBase58(), c = balCache.get(id);
  if (c && Date.now() - c.at < 30_000) return c.v;
  const v = await BASE.getBalance(k).catch(() => c?.v ?? -1);
  balCache.set(id, { at: Date.now(), v });
  return v;
}
async function authorityKey(): Promise<PublicKey | null> {
  try { if (process.env.AUTHORITY_PUBKEY) return new PublicKey(process.env.AUTHORITY_PUBKEY); } catch {}
  const st = await status().catch(() => null);
  const a = st?.servers?.find((x: any) => x.keeper?.authority)?.keeper?.authority;
  try { return a ? new PublicKey(a) : null; } catch { return null; }
}
async function health() {
  const [funderL, authority] = [await funderLamports(), await authorityKey()];
  const b = { funderLamports: funderL, authorityLamports: await lamportsOf(authority), treasuryLamports: await lamportsOf(TREASURY) };
  const low = lowFlags(b, LOW);
  return { ok: true, funder: funder?.publicKey.toBase58() ?? null, authority: authority?.toBase58() ?? null, treasury: TREASURY.toBase58(), ...b, lowBalance: low.any, low, matches: matchDirs().length };
}
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
        keeper: { stage: k.stage, since: k.since, alive: Date.now() - (k.heartbeat ?? 0) < 30_000, match: k.match, dealSig: k.dealSig ?? null, worldTick: k.worldTick, worldPhase: k.worldPhase, worldRound: k.worldRound, last: k.last,
          // harden #1 / #2 / #11: the keeper's wallet balances + flags, the season pause, the rollup's MagicBlock version
          authority: k.authority ?? null, authorityLamports: k.authorityLamports ?? null, treasuryLamports: k.treasuryLamports ?? null, lowBalance: k.lowBalance ?? null,
          seasons: k.seasons ?? null, seasonsPaused: k.seasonsPaused ?? null, seasonsPausedReason: k.seasonsPausedReason ?? null, magicblockCore: k.magicblockCore ?? null, erVersionChanged: !!k.erVersionChanged },
        phase: sv?.phase ?? "offline", inMatch: sv?.inMatch ?? false, round: sv?.round ?? 0, humans: sv?.humans ?? 0, size: sv?.size ?? 12, matchHex: sv?.match ?? "", served: sv?.completed ?? 0, onchainAll: !!sv && "onchainAll" in sv,
        room: !!sv?.room, lobbyTier: sv?.lobbyTier ?? null, conns: sv?.conns ?? null,
      };
    } catch (e: any) { return { id, url: GS_PUBLIC[id] ?? null, up: false, phase: "offline", error: String(e?.message ?? e).slice(0, 120) }; }
  }));
  for (const [id, file] of Object.entries(STATUS_FILES)) {
    const sv0 = readJson(file);
    const sv = sv0 && Date.now() - sv0.t < 10_000 ? sv0 : null;
    servers.push({ id, url: GS_PUBLIC[id] ?? null, up: !!sv, arena: null, classic: true, phase: sv?.phase ?? "offline", inMatch: sv?.inMatch ?? false, round: sv?.round ?? 0, humans: sv?.humans ?? 0, size: sv?.size ?? 12, matchHex: sv?.match ?? "", served: sv?.completed ?? 0, room: !!sv?.room, lobbyTier: sv?.lobbyTier ?? null, conns: sv?.conns ?? null } as any);
  }
  // P7-UX: a private room's server is never offered to the public (its code is never exposed)
  for (const sv of servers as any[]) { const r = roomOf(sv.id); sv.room = !!r || !!sv.room; }
  statusCache = { at: Date.now(), body: { ok: true, t: Date.now(), servers, program: PROGRAM.toBase58(), network: "devnet", rollup: "devnet-as.magicblock.app" } };
  return statusCache.body;
}

// ------------------------------------------------------------------ archive
// beta audit: cached for 5 s (it reads every summary.json; /matches is polled by every landing + /live tab)
let dirsCache: { at: number; v: { id: string; at: number }[] } = { at: 0, v: [] };
function matchDirs(): { id: string; at: number }[] {
  if (Date.now() - dirsCache.at < 5000) return dirsCache.v;
  dirsCache = { at: Date.now(), v: matchDirs0() };
  return dirsCache.v;
}
function matchDirs0(): { id: string; at: number }[] {
  if (!existsSync(ARCHIVE)) return [];
  return readdirSync(ARCHIVE).filter((d) => existsSync(join(ARCHIVE, d, "summary.json")))
    .map((d) => ({ id: d, at: (() => { try { return JSON.parse(readFileSync(join(ARCHIVE, d, "summary.json"), "utf8")).doneAt ?? 0; } catch { return statSync(join(ARCHIVE, d)).mtimeMs; } })() }))
    .sort((a, b) => b.at - a.at);
}
const readJson = (p: string) => { try { return JSON.parse(readFileSync(p, "utf8")); } catch { return null; } };
// summary.json is immutable once archived: cache it by mtime (brief / rebuild / seasons re-read every file otherwise)
const sumCache = new Map<string, { m: number; v: any }>();
function summaryOf(id: string) {
  const f = join(ARCHIVE, id, "summary.json");
  let m = 0;
  try { m = statSync(f).mtimeMs; } catch { sumCache.delete(id); return null; }
  const c = sumCache.get(id);
  if (c && c.m === m) return c.v;
  const v = readJson(f);
  if (sumCache.size > 400) sumCache.clear();
  sumCache.set(id, { m, v });
  return v;
}
function brief(id: string) {
  const s = summaryOf(id);
  if (!s) return null;
  const rc = s.receipt ?? {};
  // fullchain: receipt trust bit 4 (econ.rs trust::ALL_PROGRAM) = every round computed by the program (its Final too)
  const allOnChain = ((Number(rc.trust) || 0) & 4) !== 0;
  return {
    match: id, arena: s.arena, doneAt: s.doneAt, startedAt: s.startedAt, champion: rc.champion, humanMask: rc.humanMask, flags: rc.flags, mintedMask: rc.mintedMask,
    finalOnChain: !!s.final || allOnChain, allOnChain, trust: rc.trust, realTime: s.final?.realTime, drops: s.drops?.count ?? 0, pools: [0, 1, 2, 3, 4].map((k) => s[`market${k}`]?.pool ?? "0"),
    finalizeMode: s.finalizeMode, replay: existsSync(join(ARCHIVE, id, "world2.bin")), judge: !!s.judge,
    // P6: aborted (status 6) / no human and no on-chain Final (hidden from the default lists), sealed bets
    status: rc.status, aborted: rc.status === 6, hidden: !rc.humanMask && !s.final && !allOnChain, sealed: s.sealed ? { n: s.sealed.n ?? 0, revealed: s.sealed.revealed ?? 0, forfeits: s.sealed.forfeits ?? 0 } : null,
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
const pseasonCache = new Map<string, { at: number; v: any }>();
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
  if (seasonCache.size > 64) seasonCache.clear();
  seasonCache.set(id, { at: Date.now(), body });
  return body;
}
/** The same boards rebuilt from the archived keeper summaries of matches recorded into `id` (markets, drops, receipt). */
function rebuild(id: number) {
  const ps = new Map<string, { crowns: number; finals: number; net: bigint; drops: number }>();
  const get = (w: string) => { let x = ps.get(w); if (!x) ps.set(w, (x = { crowns: 0, finals: 0, net: 0n, drops: 0 })); return x; };
  let n = 0;
  for (const d of matchDirs()) {
    const s = summaryOf(d.id);
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
const ipOf0 = (req: IncomingMessage) => String(req.headers["x-real-ip"] ?? req.headers["x-forwarded-for"] ?? req.socket.remoteAddress ?? "?").split(",")[0].trim();
// beta audit: an IPv6 client can rotate addresses inside its /64: limits are keyed by the /64
const ipOf = (req: IncomingMessage) => { const ip = ipOf0(req); return ip.includes(":") && !ip.startsWith("::ffff:") ? ip.split(":").slice(0, 4).join(":") + "::/64" : ip; };
const gasInFlight = new Set<string>();
const recent = (xs: number[] | undefined, ms: number) => (xs ?? []).filter((t) => Date.now() - t < ms);
async function gas(ip: string, burner: string) {
  let pk: PublicKey;
  try { pk = new PublicKey(burner); } catch { return { ok: false, error: "bad burner" }; }
  if (!funder) return { ok: false, error: "gas station not configured" };
  // beta audit: every check AND the reservation happen synchronously, before the first await: N parallel requests
  // used to all pass the checks (the ledger was written only after the send) and drain the funder in one burst
  // beta audit: a returning guest may be topped up again after 24 h (sessions spend ~0.004 SOL each: "once ever"
  // turned betting off for good on the 2nd–3rd visit)
  if ((ledger.burners[burner] && Date.now() - ledger.burners[burner] < DAY) || gasInFlight.has(burner)) {
    const bal = await BASE.getBalance(pk).catch(() => -1);
    return bal >= 5_000_000 ? { ok: true, funded: false, balance: bal } : { ok: false, error: "this guest wallet was already topped up", balance: bal };
  }
  ledger.ip[ip] = recent(ledger.ip[ip], DAY);
  if (ledger.ip[ip].length >= GAS_PER_IP_DAY) return { ok: false, error: "daily guest limit for this network reached" };
  ledger.sent = recent(ledger.sent, 3_600_000);
  if (ledger.sent.length >= GAS_PER_HOUR) return { ok: false, error: "busy: try again in a few minutes" };
  const now = Date.now();
  gasInFlight.add(burner); ledger.burners[burner] = now; ledger.ip[ip].push(now); ledger.sent.push(now);
  const undo = () => { delete ledger.burners[burner]; ledger.ip[ip] = (ledger.ip[ip] ?? []).filter((t) => t !== now); ledger.sent = ledger.sent.filter((t) => t !== now); };
  let sig: string;
  try {
    const bal = await BASE.getBalance(pk).catch(() => -1);
    if (bal >= 5_000_000) { undo(); return { ok: true, funded: false, balance: bal }; }
    const tx = new Transaction().add(SystemProgram.transfer({ fromPubkey: funder.publicKey, toPubkey: pk, lamports: GAS_LAMPORTS }));
    tx.feePayer = funder.publicKey;
    tx.recentBlockhash = (await BASE.getLatestBlockhash("confirmed")).blockhash;
    tx.sign(funder);
    sig = await BASE.sendRawTransaction(tx.serialize(), { maxRetries: 3 });
  } catch (e: any) { undo(); return { ok: false, error: "gas station busy: try again (" + String(e?.message ?? e).slice(0, 80) + ")" }; }
  finally { gasInFlight.delete(burner); }
  saveLedger();
  funderCache.at = 0;
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
type Room = { code: string; gs: string; createdAt: number; expiresAt: number; botMix: number; seen: boolean; ip: string; judge?: boolean };
// beta audit: a room nobody ever joined is released after 3 min (was 10: one network could hold servers for free)
const ROOM_UNJOINED_MS = 3 * 60_000;
const ROOM_IDLE_MS = 10 * 60_000, ROOM_LIVE_MS = 5 * 60_000, ROOMS_PER_IP_HOUR = Number(process.env.ROOMS_PER_IP_HOUR ?? 6);
const rooms = new Map<string, Room>(); // by code
const roomIp: Record<string, number[]> = {};
const ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
const roomOf = (gs: string) => { for (const r of rooms.values()) if (r.gs === gs) return r; return null; };
function writeRoom(r: Room | null, gs: string) {
  if (!ROOMS) return;
  const f = join(ROOMS, `${gs}.json`);
  try {
    if (!r) { rmSync(f, { force: true }); return; }
    writeFileSync(f + ".tmp", JSON.stringify({ code: r.code, expiresAt: r.expiresAt, botMix: r.botMix, ...(r.judge ? { judge: true } : {}) }));
    renameSync(f + ".tmp", f);
  } catch (e: any) { console.warn("[rooms] write", gs, e?.message ?? e); }
}
function newCode() {
  for (;;) {
    let c = "";
    // beta audit: 6 characters (32^6 ≈ 1e9; 4 was ≈ 1e6 and /room/:code had no limit). The game accepts 4–8.
    const b = randomBytes(6);
    for (let i = 0; i < 6; i++) c += ALPHABET[b[i] % ALPHABET.length];
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
    else if (!r.seen && Date.now() - r.createdAt > ROOM_UNJOINED_MS) r.expiresAt = 0;
    if (Date.now() > r.expiresAt || (sv && !sv.up && Date.now() - r.createdAt > 60_000)) { rooms.delete(r.code); writeRoom(null, r.gs); console.log(`[rooms] ${r.code} on ${r.gs} ended`); }
  }
}
setInterval(() => { sweepRooms().catch(() => {}); }, 2_000);
// rooms live in memory: a restarted indexer opens every server to the public again (stale room files removed)
if (ROOMS) {
  try { mkdirSync(ROOMS, { recursive: true }); for (const f of readdirSync(ROOMS)) if (f.endsWith(".json")) rmSync(join(ROOMS, f), { force: true }); } catch (e: any) { console.warn("[rooms] init", e?.message ?? e); }
  console.log(`[rooms] dir ${ROOMS} ${ROOMS_ON ? "on" : "off"}; classic servers ${Object.keys(STATUS_FILES).join(",") || "-"}`);
}
// harden #6 (policy.ts): an arena server is idle only when its keeper is idle too
// beta audit: one room decision at a time (two concurrent creates both picked the same idle server: the second
// overwrote the first's room file, so the first invite was dead and the server reopened to the public on its end)
let roomLock: Promise<unknown> = Promise.resolve();
function createRoom(ip: string, botMix: number) {
  const run = roomLock.then(() => createRoom0(ip, botMix));
  roomLock = run.catch(() => {});
  return run;
}
async function createRoom0(ip: string, botMix: number) {
  if (!ROOMS_ON) return { code: 503, body: { ok: false, error: "rooms are off right now" } };
  const mine = [...rooms.values()].find((r) => r.ip === ip && !r.seen);
  if (mine) return { code: 429, body: { ok: false, error: `your room ${mine.code} is still open: join it, or wait a few minutes` } };
  roomIp[ip] = recent(roomIp[ip], 3_600_000);
  if (roomIp[ip].length >= ROOMS_PER_IP_HOUR) return { code: 429, body: { ok: false, error: "too many rooms from this network: try again later" } };
  await sweepRooms();
  statusCache.at = 0;
  const st = await status();
  const arenas = st.servers.filter((x: any) => x.arena != null && !x.classic);
  const publicArenas = (except: string) => arenas.filter((x: any) => x.id !== except && x.up && !x.room).length;
  // beta audit: 1. an idle Classic-only server first (it exists for rooms; taking an arena first halved public capacity);
  // 2. an idle arena while another arena stays public
  const pick = st.servers.find((x: any) => x.classic && idle(x) && !roomOf(x.id)) ?? arenas.find((x: any) => idle(x) && !roomOf(x.id) && publicArenas(x.id) >= 1);
  if (!pick) return { code: 409, body: { ok: false, error: "every room is busy right now: try Quick Match, or again in a few minutes" } };
  const r: Room = { code: newCode(), gs: pick.id, createdAt: Date.now(), expiresAt: Date.now() + ROOM_IDLE_MS, botMix: Math.max(0, Math.min(3, botMix | 0)), seen: false, ip };
  rooms.set(r.code, r);
  writeRoom(r, r.gs);
  roomIp[ip].push(Date.now());
  statusCache.at = 0;
  console.log(`[rooms] ${r.code} on ${r.gs} (${pick.classic ? "classic" : "arena " + pick.arena}) mix ${r.botMix}`);
  return { code: 200, body: { ok: true, code: r.code, url: pick.url, gs: pick.id, onchainFinal: !pick.classic, expiresAt: r.expiresAt } };
}
/**
 * Submission (judge mode): one click → a private room on an idle ON-CHAIN arena (never a Classic-only server), flagged
 * `judge`: the game server starts as soon as the judge is in and fast-forwards rounds 1–3 (the receipt labels it), so the
 * judge plays the on-chain Final within about a minute. An arena is taken only if another arena or the Classic overflow
 * server stays open to the public.
 */
const judgeIp: Record<string, number[]> = {};
function createJudge(ip: string) {
  const run = roomLock.then(() => createJudge0(ip));
  roomLock = run.catch(() => {});
  return run;
}
async function createJudge0(ip: string) {
  if (!JUDGE_ON) return { code: 503, body: { ok: false, error: "judge mode is off right now: use Play" } };
  const mine = [...rooms.values()].find((r) => r.ip === ip && r.judge && !r.seen);
  if (mine) { const st0 = await status(); const sv0: any = st0.servers.find((x: any) => x.id === mine.gs); return { code: 200, body: { ok: true, code: mine.code, url: sv0?.url ?? null, gs: mine.gs, arena: sv0?.arena ?? null, onchainFinal: true, expiresAt: mine.expiresAt, reused: true } }; }
  judgeIp[ip] = recent(judgeIp[ip], 3_600_000);
  if (judgeIp[ip].length >= JUDGE_PER_IP_HOUR) return { code: 429, body: { ok: false, error: "too many judge matches from this network: try again later, or use Play" } };
  await sweepRooms();
  statusCache.at = 0;
  const st = await status();
  const arenas = st.servers.filter((x: any) => x.arena != null && !x.classic);
  const othersOpen = (except: string) => arenas.filter((x: any) => x.id !== except && x.up && !x.room).length + st.servers.filter((x: any) => x.classic && x.up && !x.room).length;
  const pick = arenas.find((x: any) => idle(x) && !roomOf(x.id) && othersOpen(x.id) >= 1);
  if (!pick) {
    // harden #6: an arena whose server is free but whose keeper still finishes the last match: retry shortly
    const finishing = arenas.find((x: any) => serverIdle(x) && !keeperIdle(x) && !roomOf(x.id) && othersOpen(x.id) >= 1);
    if (finishing) return { code: 409, body: { ok: false, busy: "keeper", error: "arena finishing the last match", stage: finishing.keeper?.stage ?? null, retryInS: 5 } };
    return { code: 409, body: { ok: false, busy: "arenas", error: "both on-chain arenas are busy: try again in a minute (a match takes about 6 minutes), or use Play", retryInS: 60 } };
  }
  const r: Room = { code: newCode(), gs: pick.id, createdAt: Date.now(), expiresAt: Date.now() + ROOM_IDLE_MS, botMix: 1, seen: false, ip, judge: true };
  rooms.set(r.code, r);
  writeRoom(r, r.gs);
  judgeIp[ip].push(Date.now());
  statusCache.at = 0;
  console.log(`[rooms] judge ${r.code} on ${r.gs} (arena ${pick.arena})`);
  return { code: 200, body: { ok: true, code: r.code, url: pick.url, gs: pick.id, arena: pick.arena, onchainFinal: true, expiresAt: r.expiresAt } };
}
const roomMiss: Record<string, number[]> = {};
async function roomInfo(code: string, ip: string) {
  // beta audit: misses are limited per network (code guessing)
  roomMiss[ip] = recent(roomMiss[ip], 600_000);
  if (roomMiss[ip].length >= 30) return { code: 429, body: { ok: false, error: "too many wrong codes: try again in a few minutes" } };
  const r = rooms.get(code.toUpperCase());
  if (!r) { roomMiss[ip].push(Date.now()); return { code: 404, body: { ok: false, error: "no room with that code (it may have ended)" } }; }
  const st = await status();
  const sv: any = st.servers.find((x: any) => x.id === r.gs);
  return { code: 200, body: { ok: true, code: r.code, url: sv?.url ?? null, gs: r.gs, onchainFinal: !sv?.classic, onchainAll: !!sv?.onchainAll, phase: sv?.phase ?? "offline", humans: sv?.humans ?? 0, inMatch: !!sv?.inMatch, expiresAt: r.expiresAt } };
}
const TIERS = ["Rookie", "Contender", "Champion"];
/** Skill-tier matchmaking over the public servers: same-tier lobby → empty server → nearest-tier lobby → watch the emptiest. */
async function matchmake(tier: string) {
  const t = Math.max(0, TIERS.indexOf(tier));
  await sweepRooms();
  const st = await status();
  const pub = st.servers.filter((x: any) => x.up && x.url && !x.room && !x.classic);
  // beta audit: a server takes at most 12 connections (players + watchers): `conns` (game servers from the beta audit
  // on) is what fills it; servers without the field fall back to humans
  const conns = (x: any) => x.conns ?? x.humans ?? 0;
  const open = pub.filter((x: any) => !x.inMatch && PUBLIC_PHASES.has(x.phase) && x.humans < x.size && conns(x) < 12);
  const out = (x: any, reason: string) => ({ ok: true, url: x.url, gs: x.id, reason, humans: x.humans, size: x.size, phase: x.phase, lobbyTier: x.lobbyTier ?? null });
  const same = open.filter((x: any) => x.humans > 0 && x.lobbyTier === TIERS[t]).sort((a: any, b: any) => b.humans - a.humans)[0];
  if (same) return out(same, "same-tier lobby");
  // beta audit: a lobby with people in it beats an empty server whatever its tier (at beta scale, "empty first"
  // split 20 people over two servers and filled both matches with bots); tiers only pick among the open lobbies
  const near = open.filter((x: any) => x.humans > 0).sort((a: any, b: any) => Math.abs(TIERS.indexOf(a.lobbyTier) - t) - Math.abs(TIERS.indexOf(b.lobbyTier) - t) || b.humans - a.humans)[0];
  if (near) return out(near, "nearest-tier lobby");
  const empty = open.filter((x: any) => x.humans === 0)[0];
  if (empty) return out(empty, "new lobby");
  // beta audit: both arenas are mid-match: an open Classic-only server (no on-chain Final) beats watching
  const classic = st.servers.filter((x: any) => x.up && x.url && x.classic && !x.room && !x.inMatch && PUBLIC_PHASES.has(x.phase) && x.humans < x.size && conns(x) < 12).sort((a: any, b: any) => b.humans - a.humans)[0];
  if (classic) return out(classic, "overflow lobby (Classic: the Final runs on the server)");
  const watch = pub.filter((x: any) => conns(x) < 12).sort((a: any, b: any) => conns(a) - conns(b))[0];
  if (watch) return out(watch, "all lobbies busy: watch, then join the next one");
  return { ok: false, error: pub.length ? "every server is full right now: try again in a minute" : "no game server is up" };
}

// ------------------------------------------------------------------ http
const send = (res: ServerResponse, code: number, body: any) => { res.writeHead(code, { "content-type": "application/json", "cache-control": "no-store" }); res.end(JSON.stringify(body)); };
const bodyOf = (req: IncomingMessage) => new Promise<any>((ok) => { let b = ""; req.on("data", (c) => { b += c; if (b.length > 4096) { req.destroy(); ok({}); } }); req.on("error", () => ok({})); req.on("end", () => { try { ok(JSON.parse(b || "{}")); } catch { ok({}); } }); });
const SAFE = /^[1-9A-HJ-NP-Za-km-z]{32,44}$/;
createServer(async (req, res) => {
  try {
    const u = new URL(req.url ?? "/", "http://x");
    const p = u.pathname.replace(/^\/api/, "");
    if (p === "/status") return send(res, 200, await status());
    if (p === "/health") return send(res, 200, await health());
    if (p === "/matches") {
      // P6: matches with no human and no on-chain Final (aborted bot-only lobbies) only with ?all=1
      const all = u.searchParams.get("all") === "1";
      const xs = matchDirs().map((d) => brief(d.id)).filter((x): x is NonNullable<ReturnType<typeof brief>> => !!x && (all || !x.hidden));
      const counts = { total: xs.length, finalOnChain: xs.filter((x) => x.finalOnChain).length, allOnChain: xs.filter((x) => x.allOnChain).length };
      return send(res, 200, { ok: true, counts, matches: xs.slice(0, Number(u.searchParams.get("n") ?? 50)) });
    }
    if (p === "/season") {
      const id = Number(u.searchParams.get("id") ?? isoWeek(Date.now() / 1000));
      // beta audit: only plausible ids (ISO weeks 2024–2099 or the 7-digit test seasons): an id sweep would otherwise
      // make one RPC call each from the IP the keepers share
      if (!Number.isInteger(id) || !((id >= 202401 && id <= 209953) || (id >= 1_000_000 && id <= 9_999_999))) return send(res, 400, { ok: false, error: "bad season id" });
      // harden #2: the keepers pause seasons at the MagicBlock commit cap (or SEASONS=0): the board says so
      const st0 = await status().catch(() => null);
      const pause = seasonPause((st0?.servers ?? []).map((x: any) => x.keeper));
      const body = { ...(await season(id)), paused: pause.paused, pausedReason: pause.reason };
      const w = u.searchParams.get("wallet");
      if (w && SAFE.test(w)) {
        let pk: PublicKey; try { pk = new PublicKey(w); } catch { return send(res, 400, { ok: false, error: "bad wallet" }); }
        const ck = `${id}:${w}`; const pc = pseasonCache.get(ck);
        const i = pc && Date.now() - pc.at < 10_000 ? pc.v : await BASE.getAccountInfo(pseasonPda(id, pk)).catch(() => null);
        if (!pc || pc.v !== i) { if (pseasonCache.size > 500) pseasonCache.clear(); pseasonCache.set(ck, { at: Date.now(), v: i }); }
        return send(res, 200, { ...body, player: i && i.owner.equals(PROGRAM) ? decodePSeason(i.data as Buffer) : null, playerAccount: pseasonPda(id, new PublicKey(w)).toBase58() });
      }
      return send(res, 200, body);
    }
    if (p === "/seasons") {
      const ids = new Set<number>([isoWeek(Date.now() / 1000)]);
      for (const d of matchDirs().slice(0, 200)) for (const k of Object.keys(summaryOf(d.id)?.seasons ?? {})) ids.add(Number(k));
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
      // beta audit: a file pruned mid-stream used to throw an uncaught error and crash the indexer (rooms lost)
      return void createReadStream(f).on("error", () => res.destroy()).pipe(res);
    }
    if (p === "/features") return send(res, 200, { ok: true, rooms: ROOMS_ON, mm: MM_ON, judge: JUDGE_ON });
    if (p === "/judge" && req.method === "POST") { const r = await createJudge(ipOf(req)); return send(res, r.code, r.body); }
    if (p === "/room" && req.method === "POST") { const b = await bodyOf(req); const r = await createRoom(ipOf(req), Number(b.botMix ?? 1)); return send(res, r.code, r.body); }
    m = p.match(/^\/room\/([A-Za-z0-9]{4,8})$/);
    if (m) { const r = await roomInfo(m[1], ipOf(req)); return send(res, r.code, r.body); }
    if (p === "/mm" && req.method === "POST") { const b = await bodyOf(req); return send(res, 200, await matchmake(String(b.tier ?? "Contender"))); }
    if (p === "/gas" && req.method === "POST") { const b = await bodyOf(req); return send(res, 200, await gas(ipOf(req), String(b.burner ?? ""))); }
    if (p === "/faucet-gate" && req.method === "POST") { const b = await bodyOf(req); return send(res, 200, faucetGate(ipOf(req), String(b.burner ?? ""))); }
    return send(res, 404, { ok: false, error: "not found" });
  } catch (e: any) { return send(res, 500, { ok: false, error: String(e?.message ?? e).slice(0, 200) }); }
}).listen(PORT, "0.0.0.0", () => console.log(`[indexer] :${PORT} archive ${ARCHIVE} keepers ${Object.keys(KEEPERS).join(",")} funder ${funder?.publicKey.toBase58() ?? "-"}`));
