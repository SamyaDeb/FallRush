// P7 A3.1 (D-LOAD): headless spectator bettors for the load test. Each bettor is the game's own chain bundle
// (packages/chain, the code the WebGL client and /live run) with its own burner + session key and RUSH chips. They
// follow the public arenas for DURATION seconds: open bets on the Qualify / Final-winner markets, sealed Champion bets
// (MagicBlock Private ER, TEE) while the sealed market is open, and Chaos Drops during the on-chain Final's RUSH.
// Every action is timed (call → confirmed result); 429s are counted by the bundle's backoff (rpc429).
//   cd tools/keeper && P2_NET=devnet N=30 DURATION=1800 ARENAS=2,3 npx tsx ../p7/load-bettors.ts <out dir>
//   (FILL=1: every bettor sealed-bets on the first open sealed market of ARENAS[0] — the TEE book capacity test;
//   tools/p7/load-sweep.ts returns the burners' SOL to the admin afterwards)
import { readFileSync, writeFileSync, existsSync, appendFileSync, mkdirSync } from "node:fs";
import { join } from "node:path";
import { homedir } from "node:os";
import { Keypair, PublicKey, SystemProgram, Transaction, Connection, sendAndConfirmTransaction } from "@solana/web3.js";
import { createChain } from "../../packages/chain/src/api.ts";
import { memoryKV } from "../../packages/chain/src/storage.ts";
import { rpc429 } from "../../packages/chain/src/web3conn.ts";

const ROOT = new URL("../../", import.meta.url).pathname;
const OUT = process.argv[2] ?? join(ROOT, `docs/proof/p7/load/bettors-${Date.now()}`);
mkdirSync(OUT, { recursive: true });
const N = Number(process.env.N ?? 30), DURATION = Number(process.env.DURATION ?? 1800) * 1000;
const ARENAS = (process.env.ARENAS ?? "2,3").split(",").map(Number);
const FILL = process.env.FILL === "1";
const KEYS: Record<number, string> = { 2: "HbinPthhN3g5BRQRL4aKQ75HyQXMofhsUbg2c28bJXHt", 3: "E9XE7vtQgrWnXLcGMn5FYXAFLb6GmBjrKcZq5wgPcEbG" };
const base = new Connection("https://api.devnet.solana.com", "confirmed");
const admin = Keypair.fromSecretKey(Uint8Array.from(JSON.parse(readFileSync(join(homedir(), ".config/solana/id.json"), "utf8"))));
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
const EV = join(OUT, "events.jsonl");
const ev = (o: object) => appendFileSync(EV, JSON.stringify({ t: Date.now(), ...o }) + "\n");
const J = async (p: Promise<string>) => JSON.parse(await p);
const pick = <T>(a: T[]) => a[Math.floor(Math.random() * a.length)];

// persistent burners (gitignored keys dir)
const KEYF = join(ROOT, "tools/keeper/.keys/devnet-p7-bettors.json");
const saved: string[] = existsSync(KEYF) ? JSON.parse(readFileSync(KEYF, "utf8")) : [];
const cfgFor = (a: number) => JSON.parse(readFileSync(join(ROOT, `packages/chain/config/devnet-${KEYS[a]}.json`), "utf8"));

interface Bettor { i: number; arena: number; chain: any; burner: PublicKey; done: Set<string>; acts: number; }
const bettors: Bettor[] = [];
for (let i = 0; i < N; i++) {
  const arena = FILL ? ARENAS[0] : ARENAS[i % ARENAS.length];
  const chain: any = createChain({ kv: memoryKV() });
  const r = JSON.parse(await chain.init(JSON.stringify(cfgFor(arena))));
  if (!r.ok) throw new Error("init " + r.error);
  if (saved[i]) JSON.parse(chain.importBurner(saved[i]));
  saved[i] = chain.exportBurner();
  bettors.push({ i, arena, chain, burner: new PublicKey(JSON.parse(chain.wallet()).burner), done: new Set(), acts: 0 });
}
writeFileSync(KEYF, JSON.stringify(saved), { mode: 0o600 });
const solStart = await base.getBalance(admin.publicKey);
ev({ ev: "start", n: N, arenas: ARENAS, fill: FILL, adminLamports: solStart });
console.log(`[load] ${N} bettors on arenas ${ARENAS} for ${DURATION / 1000} s; admin ${solStart / 1e9} SOL`);

// setup: top up to 0.02 SOL (fees for the chips account + delegation + session), session key, chips (+ faucet)
let funded = 0;
for (const b of bettors) {
  const bal = await base.getBalance(b.burner).catch(() => 0);
  if (bal < 12_000_000) {
    await sendAndConfirmTransaction(base, new Transaction().add(SystemProgram.transfer({ fromPubkey: admin.publicKey, toPubkey: b.burner, lamports: 20_000_000 - bal })), [admin]);
    funded += 20_000_000 - bal;
    await sleep(700); // ≤ 1.5 tx/s on the public RPC
  }
}
// D-LOAD: onboarding is sequential with retries. From ONE IP the public devnet RPC rate-limits connections: 30
// concurrent onboardings gave 370 × 429 and 3/30 ready (run1 first attempt); the bettors then play concurrently
// (their steady state reads the rollup, not the base RPC)
const setup: boolean[] = [];
for (const b of bettors) {
  const t0 = Date.now();
  let s: any = { ok: false }, c: any = { ok: false };
  for (let tryN = 0; tryN < 3 && !(s.ok && c.ok); tryN++) {
    if (!s.ok) s = await J(b.chain.session("create", "{}")).catch((e) => ({ ok: false, error: String(e) }));
    if (s.ok) c = await J(b.chain.econ("chips", "{}")).catch((e) => ({ ok: false, error: String(e) }));
    if (!(s.ok && c.ok)) await sleep(2500 * (tryN + 1));
  }
  ev({ ev: "setup", i: b.i, arena: b.arena, session: s.ok, chips: c.ok, err: s.ok && c.ok ? undefined : String(s.error ?? c.error).slice(0, 160), ms: Date.now() - t0 });
  setup.push(s.ok && c.ok);
  if (!(s.ok && c.ok)) (b as any).dead = true;
}
console.log(`[load] setup ok ${setup.filter(Boolean).length}/${N}; funded ${funded / 1e9} SOL; 429s so far ${rpc429.count}`);

// the loop: each bettor polls its arena's state every ~6 s (jittered) and acts on what is open
const t0 = Date.now();
const stats: Record<string, { ok: number; fail: number; ms: number[]; errs: Record<string, number> }> = {};
const note = (what: string, ok: boolean, ms: number, err?: string) => {
  const s = (stats[what] ??= { ok: 0, fail: 0, ms: [], errs: {} });
  if (ok) { s.ok++; s.ms.push(ms); } else { s.fail++; const k = String(err ?? "?").slice(0, 60); s.errs[k] = (s.errs[k] ?? 0) + 1; }
};
const marketOpen = (st: any, k: number) => {
  const m = st.match, w = st.world, mk = st.markets?.[k];
  if (!m || !mk || mk.status !== 1 || mk.match !== m.pda) return false;
  const final = (m.flags & 0x40) !== 0, live = final && w && w.round === 4 && w.roundPhase < 3;
  if (m.status >= 5) return false;
  if (k === 0) return !(m.round >= 4 || final) || !!live;
  if (k === 1) return !!live;
  const r = k - 1;
  return r === 1 ? m.round === 0 && m.status <= 2 : m.round === r - 1 && m.status === 4;
};
async function run(b: Bettor) {
  if ((b as any).dead) return;
  await sleep(Math.random() * 6000);
  while (Date.now() - t0 < DURATION) {
    try {
      const st = await J(b.chain.econ("state", "{}"));
      if (st.ok && st.match) {
        const m = st.match;
        const alive = (m.slots as number[]).map((x, i) => (x === 1 || x === 2) && i < m.players ? i : -1).filter((i) => i >= 0);
        // sealed Champion (P6): every bettor once per match while the sealed market is open
        const sealed = await J(b.chain.econ("sealedState", "{}")).catch(() => null);
        if (sealed?.open && !b.done.has("s" + m.pda) && alive.length && (FILL || Math.random() < 0.6)) {
          b.done.add("s" + m.pda);
          const a = Date.now();
          const r = await J(b.chain.econ("sealedBet", JSON.stringify({ outcome: pick(alive), amount: 5 + Math.floor(Math.random() * 20) })));
          note("sealedBet", r.ok, Date.now() - a, r.error); ev({ ev: "sealedBet", i: b.i, arena: b.arena, match: m.pda, ok: r.ok, ms: Date.now() - a, pickOk: r.pick?.ok, err: r.ok ? undefined : String(r.error).slice(0, 200) });
        }
        // open markets: Qualify R1–R3 and the Final winner, one bet each per match
        for (const k of [2, 3, 4, 1]) {
          if (FILL || !marketOpen(st, k) || b.done.has(k + m.pda) || !alive.length || Math.random() > 0.5) continue;
          b.done.add(k + m.pda);
          const a = Date.now();
          const r = await J(b.chain.econ("bet", JSON.stringify({ k, outcome: pick(alive), amount: 5 + Math.floor(Math.random() * 30) })));
          note("bet", r.ok, Date.now() - a, r.error); ev({ ev: "bet", i: b.i, arena: b.arena, k, match: m.pda, ok: r.ok, ms: Date.now() - a, err: r.ok ? undefined : String(r.error).slice(0, 200) });
        }
        // Chaos Drops during the on-chain Final's RUSH (the program caps 3 per Final + a 5 s cooldown; refusals are counted)
        const w = st.world;
        if (!FILL && w && (m.flags & 0x40) && w.round === 4 && w.roundPhase === 3 && (st.drops?.count ?? 0) < 3 && Math.random() < 0.12) {
          const a = Date.now();
          const kind = 1 + Math.floor(Math.random() * 3);
          const r = await J(b.chain.econ("drop", JSON.stringify({ kind })));
          note("drop", r.ok, Date.now() - a, r.error); ev({ ev: "drop", i: b.i, arena: b.arena, kind, match: m.pda, ok: r.ok, ms: Date.now() - a, err: r.ok ? undefined : String(r.error).slice(0, 200) });
        }
      }
      note("state", !!st.ok, 0, st.error);
    } catch (e) { note("loop", false, 0, String(e)); }
    await sleep(5000 + Math.random() * 2500);
  }
}
const timer = setInterval(() => { ev({ ev: "tick", s: Math.round((Date.now() - t0) / 1000), rpc429: rpc429.count, stats: Object.fromEntries(Object.entries(stats).map(([k, v]) => [k, { ok: v.ok, fail: v.fail }])) }); }, 30_000);
await Promise.all(bettors.map(run));
clearInterval(timer);

const pct = (a: number[], p: number) => { if (!a.length) return null; const s = [...a].sort((x, y) => x - y); return s[Math.min(s.length - 1, Math.floor((p / 100) * s.length))]; };
const summary: any = { n: N, arenas: ARENAS, fill: FILL, seconds: Math.round((Date.now() - t0) / 1000), rpc429: rpc429.count, fundedSol: funded / 1e9, setupOk: setup.filter(Boolean).length,
  actions: Object.fromEntries(Object.entries(stats).map(([k, v]) => [k, { ok: v.ok, fail: v.fail, p50: pct(v.ms, 50), p95: pct(v.ms, 95), max: v.ms.length ? Math.max(...v.ms) : null, errs: v.errs }])) };
summary.adminLamportsEnd = await base.getBalance(admin.publicKey);
writeFileSync(join(OUT, "summary.json"), JSON.stringify(summary, null, 1));
console.log(JSON.stringify(summary, null, 1));
process.exit(0);
