// P4 task 7: the economy end to end beside the keeper (local stack or devnet). Three wallets:
//   A bets with its own signature; B bets through a session key (SessionTokenV2); C (a spectator) buys Chaos Drops.
// Follows the match by program state: Champion + Qualify(1) before round 1, Qualify(2) / (3) between rounds,
// FinalWinner before the Final's RUSH tick, then 2 drops during RUSH (5 s apart). When the markets are paid it checks
// every payout against a hand calculation (stake · pool / winning stake, rounded down; refunds) from the Market
// accounts, and writes docs/proof/p4/e2e-<run>.json.
//   P2_NET=local|devnet ARENA=2 RUN=<keeper run> node --experimental-strip-types tools/p4/econ-e2e.ts
import { Keypair, PublicKey, SystemProgram } from "@solana/web3.js";
import { writeFileSync, mkdirSync } from "node:fs";
import { join } from "node:path";
import { NET, base, er, wallet, pe, pda, PROGRAM_ID, sendBase, sendEr, key, sleep, ROOT } from "../keeper/lib.ts";
import * as E from "../keeper/econ.ts";
import { createSessionV2Ix } from "../../packages/chain/src/session.ts";

const ARENA = Number(process.env.ARENA ?? 2);
const RUN = process.env.RUN ?? `${NET}-${Date.now()}`;
const DROPS = (process.env.DROP_KINDS ?? "1,3").split(",").map(Number);
const arena = pda.arena(ARENA), world = pda.world(arena);
const out: any = { net: NET, run: RUN, arena: arena.toBase58(), sigs: {} as Record<string, any>, bets: [] as any[], drops: [] as any[] };
const OUT = join(ROOT, `docs/proof/p4/e2e-${RUN}.json`);
mkdirSync(join(ROOT, "docs/proof/p4"), { recursive: true });
const save = () => writeFileSync(OUT, JSON.stringify(out, (_k, v) => (typeof v === "bigint" ? v.toString() : v), 1));

// ---- wallets: chips for A, B, C; B's session key
const A = key(`e2e-A`), B = key(`e2e-B`), C = key(`e2e-C`), Bs = Keypair.generate();
for (const k of [A, B, C]) {
  const bal = await base.getBalance(k.publicKey);
  if (bal < 15_000_000) out.sigs[`fund_${k.publicKey.toBase58().slice(0, 6)}`] = await sendBase([SystemProgram.transfer({ fromPubkey: wallet.publicKey, toPubkey: k.publicKey, lamports: 20_000_000 - bal })], [wallet], "fund");
}
for (const [n, k] of [["A", A], ["B", B], ["C", C]] as const) out.sigs[`chips_${n}`] = await E.ensureChips(k);
const now = Math.floor(Date.now() / 1000);
const { ix: sessIx, token: Btok } = createSessionV2Ix({ targetProgram: PROGRAM_ID, sessionSigner: Bs.publicKey, feePayer: B.publicKey, authority: B.publicKey, topUp: true, validUntil: now + 6 * 3600, lamports: 5_000_000 });
out.sigs.sessionB = await sendBase([sessIx], [B, Bs], "create_session_v2");
out.sessionToken = Btok.toBase58();
for (let i = 0; i < 30 && !(await er.getAccountInfo(Btok).catch(() => null)); i++) await sleep(500);
const start: Record<string, bigint> = {};
for (const [n, k] of [["A", A], ["B", B], ["C", C]] as const) start[n] = (await E.chipsOf(k.publicKey))?.balance ?? 0n;
out.start = start;
save();
console.log(`e2e wallets ready: A ${start.A} B ${start.B} C ${start.C}`);

// ---- follow the match
const bet = async (who: Keypair, label: string, k: number, matchPda: PublicKey, outcome: number, amount: bigint, tok?: PublicKey) => {
  const t0 = Date.now();
  const r = tok ? await E.placeBet(Bs, ARENA, k, matchPda, outcome, amount, tok, who.publicKey) : await E.placeBet(who, ARENA, k, matchPda, outcome, amount);
  out.bets.push({ who: label, k, outcome, amount: amount.toString(), sig: r.sig, err: r.err, ms: Date.now() - t0, session: !!tok });
  console.log(`bet ${label} k${k} slot ${outcome} ${amount / E.ONE} RUSH: ${r.err ?? "ok"}`);
  save();
};
const done = new Set<string>();
let matchPda: PublicKey | null = null, lastDropTick = 0;
const t0 = Date.now();
for (;;) {
  if (Date.now() - t0 > 40 * 60_000) { out.timeout = true; break; }
  await sleep(300);
  const a: any = await pe.account.arena.fetch(arena).catch(() => null);
  if (!a) continue;
  const mk0 = await E.market(ARENA, 0);
  if (!mk0 || mk0.status === 0 || mk0.match.equals(PublicKey.default)) continue;
  if (!matchPda) {
    if (mk0.status !== 1) continue; // wait for a freshly opened match
    matchPda = mk0.match; out.match = matchPda.toBase58(); console.log(`match ${matchPda.toBase58()}`);
  }
  if (!mk0.match.equals(matchPda)) break; // the next match opened
  const m: any = await pe.account.match.fetch(matchPda).catch(() => null);
  if (!m) continue;
  const st = Object.keys(m.status)[0];
  const alive: number[] = m.slots.map((x: any, i: number) => (x.state === 1 ? i : -1)).filter((i: number) => i >= 0 && i < m.playerCount);
  const pick = (salt: number) => alive[(salt * 7 + 3) % alive.length];
  if (!done.has("pre") && m.currentRound === 0 && ["created", "dealing", "dealt"].includes(st)) {
    done.add("pre");
    await bet(A, "A", 0, matchPda, pick(1), 25n * E.ONE);
    await bet(B, "B(session)", 0, matchPda, pick(2), 15n * E.ONE, Btok);
    await bet(A, "A", 2, matchPda, pick(3), 10n * E.ONE);
    await bet(B, "B(session)", 2, matchPda, pick(4), 12n * E.ONE, Btok);
  }
  if (!done.has("q2") && st === "roundResolved" && m.currentRound === 1) { done.add("q2"); await bet(A, "A", 3, matchPda, pick(5), 8n * E.ONE); await bet(B, "B(session)", 3, matchPda, pick(6), 9n * E.ONE, Btok); }
  if (!done.has("q3") && st === "roundResolved" && m.currentRound === 2) { done.add("q3"); await bet(B, "B(session)", 4, matchPda, pick(7), 6n * E.ONE, Btok); await bet(A, "A", 4, matchPda, pick(8), 7n * E.ONE); }
  const wi = await er.getAccountInfo(world, "processed").catch(() => null);
  if (!wi) continue;
  const w = { tick: wi.data.readUInt32LE(128), round: wi.data[139], rphase: wi.data[199], phase: wi.data[138] };
  if ((m.flags & 0x40) && !done.has("final") && w.round === 4 && w.rphase < 3) { done.add("final"); await bet(A, "A", 1, matchPda, alive[0], 20n * E.ONE); await bet(B, "B(session)", 1, matchPda, alive[alive.length - 1], 10n * E.ONE, Btok); }
  // ---- Chaos Drops during RUSH (the spectator C), 5 s cooldown
  if ((m.flags & 0x40) && w.round === 4 && w.rphase === 3 && out.drops.length < DROPS.length && w.tick >= lastDropTick + 160) {
    const kind = DROPS[out.drops.length];
    const t1 = Date.now();
    const accs = [{ pubkey: E.epda.chips(C.publicKey), isSigner: false, isWritable: true }, { pubkey: E.epda.drops(arena), isSigner: false, isWritable: true }, { pubkey: arena, isSigner: false, isWritable: false }, { pubkey: world, isSigner: false, isWritable: false }];
    const r = await sendEr([await E.econIx(C.publicKey, E.OP.BUY_DROP, kind, 0, accs)], [C], "buy_drop", 4);
    out.drops.push({ kind, tick: w.tick, sig: r.sig, err: r.err, ms: Date.now() - t1 });
    console.log(`drop kind ${kind} at tick ${w.tick}: ${r.err ?? "ok"}`);
    lastDropTick = w.tick;
    save();
  }
  // ---- done: every market of this match paid
  const ms = await Promise.all([0, 1, 2, 3, 4].map((k) => E.market(ARENA, k)));
  if (ms.every((x) => x && x.match.equals(matchPda!) && x.status === 3)) { out.markets = ms; break; }
}

// ---- hand calculation of every payout from the Market accounts
if (out.markets) {
  const exp: Record<string, bigint> = {};
  const check: any[] = [];
  for (const mk of out.markets as E.MarketView[]) {
    for (const b of mk.bets) {
      const win = (mk.winners >> b.outcome) & 1;
      const pay = mk.refund ? b.amount : win && mk.winningStake > 0n ? (b.amount * mk.pool) / mk.winningStake : 0n;
      const w = b.wallet.toBase58();
      exp[w] = (exp[w] ?? 0n) + pay;
      check.push({ k: mk.kind * 10 + mk.round, wallet: w.slice(0, 8), outcome: b.outcome, amount: b.amount, pay, label: mk.label });
    }
  }
  out.payouts = check;
  out.verify = {};
  for (const [n, k] of [["A", A], ["B", B], ["C", C]] as const) {
    const c = await E.chipsOf(k.publicKey);
    const staked = out.bets.filter((x: any) => x.who.startsWith(n) && !x.err).reduce((s: bigint, x: any) => s + BigInt(x.amount), 0n);
    const dropped = out.drops.filter((x: any) => !x.err).reduce((s: bigint, x: any) => s + [0n, 50n, 40n, 30n, 40n][x.kind] * E.ONE, 0n);
    const want = start[n] - staked - (n === "C" ? dropped : 0n) + (exp[k.publicKey.toBase58()] ?? 0n);
    out.verify[n] = { start: start[n], staked, paidBack: exp[k.publicKey.toBase58()] ?? 0n, expected: want, actual: c?.balance, ok: c?.balance === want };
  }
  out.payoutsMatchHandCalc = Object.values(out.verify).every((v: any) => v.ok);
  console.log(`payouts = hand calculation: ${out.payoutsMatchHandCalc}`, JSON.stringify(out.verify, (_k, v) => (typeof v === "bigint" ? v.toString() : v)));
}
save();
process.exit(out.payoutsMatchHandCalc ? 0 : 1);
