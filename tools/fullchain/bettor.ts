// fullchain Gate 2: two bettors on dev arena ARENA (default 7) for MATCHES matches. Per match: Champion (market 0) and
// Qualify(1) (market 2) bets while the on-chain rounds 1–3 run, a FinalWinner (market 1) bet at the round-4 countdown,
// then after settlement each bet's payout is checked against the pari-mutuel rule by hand (econ.owed formula:
// winners share the pool pro rata to their stake; no winner → refund). Writes docs/proof/fullchain/gate2-bets.json.
//   P2_NET=devnet ARENA=7 MATCHES=5 node --experimental-strip-types tools/fullchain/bettor.ts
import { Keypair, PublicKey, SystemProgram } from "@solana/web3.js";
import { writeFileSync } from "node:fs";
import { join } from "node:path";
import { base, er, wallet, pe, pda, key, sendBase, sleep, decodeWorld, ROOT } from "../keeper/lib.ts";
import * as E from "../keeper/econ.ts";

const ARENA = Number(process.env.ARENA ?? 7), MATCHES = Number(process.env.MATCHES ?? 5);
const arena = pda.arena(ARENA), world = pda.world(arena);
const ONE = 1_000_000n; // chip decimals 6
const out: any[] = [];
const bettors = [key("fc-bettor-a"), key("fc-bettor-b")];
for (const b of bettors) {
  if ((await base.getBalance(b.publicKey)) < 15_000_000) {
    await sendBase([SystemProgram.transfer({ fromPubkey: wallet.publicKey, toPubkey: b.publicKey, lamports: 20_000_000 })], [wallet], "fund bettor");
  }
  console.log("bettor", b.publicKey.toBase58(), JSON.stringify(await E.ensureChips(b)));
}
const chips = async (k: Keypair) => (await E.chipsOf(k.publicKey))?.balance ?? 0n;
let last = (await pe.account.arena.fetch(arena)).matchAccount.toBase58();
for (let n = 0; n < MATCHES; n++) {
  // a new match on the arena
  let m: PublicKey;
  for (;;) { const a: any = await pe.account.arena.fetch(arena); if (a.matchAccount.toBase58() !== last) { m = a.matchAccount; last = m.toBase58(); break; } await sleep(1000); }
  const rec: any = { match: m.toBase58(), bets: [] as any[], before: {} as any };
  for (const b of bettors) rec.before[b.publicKey.toBase58()] = (await chips(b)).toString();
  const bet = async (b: Keypair, k: number, outcome: number, amt: bigint, when: string) => {
    for (let i = 0; i < 20; i++) {
      const wd = (await er.getAccountInfo(world))!.data as Buffer, wv = decodeWorld(wd);
      const at = { t: new Date().toISOString(), tick: wv.tick, round: wv.round, roundPhase: wd[199] };
      const r: any = await E.placeBet(b, ARENA, k, m, outcome, amt).catch((e) => ({ err: String(e?.message ?? e) }));
      Object.assign(r, { at });
      if (!r.err) { rec.bets.push({ wallet: b.publicKey.toBase58(), market: k, outcome, amount: amt.toString(), when, at: r.at, sig: r.sig }); return; }
      if (i === 19 || !/MarketClosed|AccountNotFound|InvalidWritable|Blockhash|timeout/i.test(r.err)) { rec.bets.push({ wallet: b.publicKey.toBase58(), market: k, outcome, err: r.err.slice(0, 160), when, at: r.at }); return; }
      await sleep(1500);
    }
  };
  // wait for the markets (open_markets) and the on-chain round 1
  for (;;) { const w = decodeWorld((await er.getAccountInfo(world))!.data as Buffer); const mk = await E.market(ARENA, 0); if (mk && mk.match.equals(m) && w.round >= 1) break; await sleep(1000); }
  await bet(bettors[0], 0, 0, 10n * ONE, "R1 champion");
  await bet(bettors[1], 0, 5, 20n * ONE, "R1 champion");
  await bet(bettors[0], 2, 1, 10n * ONE, "R1 qualify(1)");
  await bet(bettors[1], 2, 2, 10n * ONE, "R1 qualify(1)");
  // round 4 countdown: FinalWinner on two alive finalists
  // WorldAcc raw: round phase at byte 199 (rush-core rules::phase: … 2 COUNTDOWN, 3 RUSH)
  for (;;) { const d = (await er.getAccountInfo(world))!.data as Buffer; const w = decodeWorld(d); if (w.round === 4 && d[199] < 3) break; if (w.phase >= 11) break; await sleep(300); }
  const mm: any = await pe.account.match.fetch(m);
  const alive = mm.slots.map((s: any, i: number) => (s.state === 1 ? i : -1)).filter((i: number) => i >= 0);
  rec.finalists = alive;
  if (alive.length >= 2) { await bet(bettors[0], 1, alive[0], 15n * ONE, "R4 final winner"); await bet(bettors[1], 1, alive[1], 15n * ONE, "R4 final winner"); }
  // after settlement: every market of this match paid / refunded; hand check of each bet
  for (let i = 0; i < 600; i++) { const mk = await E.market(ARENA, 0); if (!mk || !mk.match.equals(m) || mk.status >= 3) break; await sleep(1000); }
  rec.markets = {};
  for (const k of [0, 1, 2]) {
    const mk = await E.market(ARENA, k);
    if (!mk || !mk.match.equals(m)) continue;
    const pool = BigInt(mk.pool.toString()), ws = BigInt(mk.winningStake.toString());
    const bets = mk.bets.map((b: any) => {
      const amt = BigInt(b.amount.toString()), win = (mk.winners >> b.outcome) & 1;
      const want = mk.refund ? amt : win && ws > 0n ? (amt * pool) / ws : 0n;
      return { wallet: b.wallet.toBase58(), outcome: b.outcome, amount: amt.toString(), wantPaid: want.toString(), paidFlag: b.paid };
    });
    rec.markets[k] = { status: mk.status, label: mk.label, winners: mk.winners, refund: mk.refund, pool: pool.toString(), winningStake: ws.toString(), paidTotal: mk.paidTotal.toString(), bets };
  }
  rec.after = {}; for (const b of bettors) rec.after[b.publicKey.toBase58()] = (await chips(b)).toString();
  // chips delta per bettor = Σ wantPaid − Σ staked over its bets in this match
  rec.check = {};
  for (const b of bettors) {
    const id = b.publicKey.toBase58();
    let want = 0n;
    for (const k of Object.keys(rec.markets)) for (const x of rec.markets[k].bets) if (x.wallet === id) want += BigInt(x.wantPaid) - BigInt(x.amount);
    const got = BigInt(rec.after[id]) - BigInt(rec.before[id]);
    rec.check[id] = { want: want.toString(), got: got.toString(), ok: want === got };
  }
  out.push(rec);
  console.log(JSON.stringify({ match: rec.match, finalists: rec.finalists, check: rec.check, labels: Object.fromEntries(Object.entries(rec.markets).map(([k, v]: any) => [k, v.label])) }));
  writeFileSync(join(ROOT, "docs/proof/fullchain/gate2-bets.json"), JSON.stringify(out, null, 1));
}
process.exit(0);
