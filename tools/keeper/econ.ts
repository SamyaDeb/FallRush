// P4 keeper economy: setup (markets, drop queue, treasury, RUSH mint), chips (init + delegate + faucet), the
// labelled market maker ("mm"), settle + payout, receipt, VRF capsule seed, and the follow-up call on Solana
// (mint_rewards: Crown + capsules, retried until done; idempotent on-chain). Every chain call is a permissionless or
// game-authority instruction of the fall_rush program (`econ(op, a, b)`, programs/fall_rush/src/econ.rs).
import { PublicKey, Keypair, SystemProgram, TransactionInstruction } from "@solana/web3.js";
import type { AccountMeta } from "@solana/web3.js";
import { readFileSync } from "node:fs";
import { NET, EP, base, er, wallet, pb, pe, pda, PROGRAM_ID, sendBase, sendEr, sleep, cu, BN, log } from "./lib.ts";

export const OP = { INIT_CHIPS: 1, FAUCET: 2, CASHOUT: 3, MINT_CASHOUT: 4, INIT_ECON: 5, OPEN_MARKETS: 6, PLACE_BET: 7, SETTLE: 8, BUY_DROP: 9, WRITE_RECEIPT: 10, MINT_REWARDS: 11 };
export const MARKETS = 5;
export const ONE = 1_000_000n;
export const TOKEN = new PublicKey("TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA");
export const ATA = new PublicKey("ATokenGPvbdGVxr1b2hvZbsiqW5xWH25efTNsLJA8knL");
export const CORE = new PublicKey("CoREENxT6tW1HoK8ypY1SxRMZTcVPm7R94rH4PZNhX7d");
export const RUSH_MINT = new PublicKey("3s8KstqicXn1kgFyWPVq3AZXkMprDtEtT7BNTxtQccEw");
export const DELEGATION = new PublicKey("DELeGGvXpWV2fqJUhqcF5ZSYMS4JTLjteaAMARRSaeSh");
const P = (seeds: (Buffer | Uint8Array)[]) => PublicKey.findProgramAddressSync(seeds, PROGRAM_ID)[0];
export const epda = {
  chips: (w: PublicKey) => P([Buffer.from("chips"), w.toBuffer()]),
  cashout: (w: PublicKey) => P([Buffer.from("cashout"), w.toBuffer()]),
  market: (id: number, k: number) => P([Buffer.from("market"), Buffer.from([id]), Buffer.from([k])]),
  drops: (arena: PublicKey) => P([Buffer.from("drops"), arena.toBuffer()]),
  mint: () => RUSH_MINT,
  mintAuth: () => P([Buffer.from("rush_mint_auth")]),
  treasury: () => P([Buffer.from("treasury")]),
  assets: () => P([Buffer.from("assets")]),
  crown: (m: PublicKey) => P([Buffer.from("crown"), m.toBuffer()]),
  capsule: (m: PublicKey, s: number) => P([Buffer.from("capsule"), m.toBuffer(), Buffer.from([s])]),
};
export const ata = (w: PublicKey, mint: PublicKey) => PublicKey.findProgramAddressSync([w.toBuffer(), TOKEN.toBuffer(), mint.toBuffer()], ATA)[0];
const w_ = (pubkey: PublicKey): AccountMeta => ({ pubkey, isSigner: false, isWritable: true });
const r_ = (pubkey: PublicKey): AccountMeta => ({ pubkey, isSigner: false, isWritable: false });

export async function econIx(signer: PublicKey, op: number, a: number | bigint, b: number | bigint, accs: AccountMeta[], onEr = true): Promise<TransactionInstruction> {
  return (onEr ? pe : pb).methods.econ(op, new BN(a.toString()), new BN(b.toString())).accountsPartial({ signer }).remainingAccounts(accs).instruction();
}

// ------------------------------------------------------------------ decoders (zero-copy layouts, econ.rs)
export interface Bet { wallet: PublicKey; amount: bigint; outcome: number; paid: number }
export interface MarketView { match: PublicKey; pool: bigint; winningStake: bigint; paidTotal: bigint; vault: bigint; stake: bigint[]; winners: number; id: number; kind: number; round: number; status: number; label: number; nBets: number; nPaid: number; refund: number; bets: Bet[] }
export function decodeMarket(d: Buffer): MarketView {
  let o = 8;
  const match = new PublicKey(d.subarray(o, o + 32)); o += 64;
  const u64 = () => { const v = d.readBigUInt64LE(o); o += 8; return v; };
  const pool = u64(), winningStake = u64(), paidTotal = u64(), vault = u64();
  const stake = Array.from({ length: 12 }, () => u64());
  const winners = d.readUInt16LE(o); o += 2;
  const [id, kind, round, status, label, nBets, nPaid, refund] = [...d.subarray(o, o + 8)]; o += 8 + 6;
  const bets: Bet[] = [];
  for (let i = 0; i < nBets; i++) { const b = o + i * 48; bets.push({ wallet: new PublicKey(d.subarray(b, b + 32)), amount: d.readBigUInt64LE(b + 32), outcome: d[b + 40], paid: d[b + 41] }); }
  return { match, pool, winningStake, paidTotal, vault, stake, winners, id, kind, round, status, label, nBets, nPaid, refund, bets };
}
export function decodeChips(d: Buffer) {
  return { wallet: new PublicKey(d.subarray(8, 40)), balance: d.readBigUInt64LE(40), faucetTotal: d.readBigUInt64LE(48), cashedOut: d.readBigUInt64LE(56), wonTotal: d.readBigUInt64LE(64), spentTotal: d.readBigUInt64LE(72), faucetAt: Number(d.readBigInt64LE(80)) };
}
export function decodeDrops(d: Buffer) {
  const count = d[8 + 84];
  const buys = Array.from({ length: count }, (_, i) => { const b = 8 + 88 + i * 40; return { by: new PublicKey(d.subarray(b, b + 32)), tick: d.readUInt32LE(b + 32), kind: d[b + 36] }; });
  return { arena: new PublicKey(d.subarray(8, 40)), match: new PublicKey(d.subarray(40, 72)), pot: d.readBigUInt64LE(72), spent: d.readBigUInt64LE(80), lastTick: d.readUInt32LE(88), count, buys };
}
/** Match.receipt (borsh, offset 295 incl. the discriminator). */
export function decodeReceipt(d: Buffer) {
  const r = 295;
  const markets = Array.from({ length: 5 }, (_, k) => { const b = r + 617 + k * 20; return { pool: d.readBigUInt64LE(b), winningStake: d.readBigUInt64LE(b + 8), winners: d.readUInt16LE(b + 16), label: d[b + 18], status: d[b + 19] }; });
  return {
    flags: d[r], trust: d[r + 1], coreVersion: d.readUInt16LE(r + 2), humanMask: d.readUInt16LE(r + 4), mintedMask: d.readUInt16LE(r + 6), drops: d[r + 8], dropSpent: d.readBigUInt64LE(r + 9),
    mapHash: d.subarray(r + 17, r + 49).toString("hex"), preFinal: d.subarray(r + 49, r + 81).toString("hex"), finalTranscript: d.subarray(r + 81, r + 113).toString("hex"), dropLog: d.subarray(r + 113, r + 145).toString("hex"),
    capsuleSeed: d.subarray(r + 145, r + 177).toString("hex"), crown: new PublicKey(d.subarray(r + 177, r + 209)),
    wallets: Array.from({ length: 12 }, (_, s) => new PublicKey(d.subarray(r + 209 + 32 * s, r + 241 + 32 * s))),
    placements: [...d.subarray(r + 593, r + 605)], rarity: [...d.subarray(r + 605, r + 617)], markets,
  };
}

// ------------------------------------------------------------------ setup (idempotent)
export async function setupEcon(arenaId: number, out: Record<string, string> = {}) {
  const arena = pda.arena(arenaId);
  const cfg = pda.config();
  const val = [{ pubkey: EP.validator, isSigner: false, isWritable: false }];
  for (let k = 0; k <= MARKETS; k++) {
    const acc = k < MARKETS ? epda.market(arenaId, k) : epda.drops(arena);
    const onBase = await base.getAccountInfo(acc);
    if (!onBase) out[`init_econ_${k}`] = await sendBase([await econIx(wallet.publicKey, OP.INIT_ECON, arenaId, k, [r_(cfg), r_(arena), w_(acc), r_(SystemProgram.programId)], false)], [wallet], `init_econ ${k}`);
    // the public RPC can lag behind the confirmation: wait until the new account is visible before delegating it
    let now = await base.getAccountInfo(acc, "confirmed");
    for (let i = 0; i < 60 && !now; i++) { await sleep(1000); now = await base.getAccountInfo(acc, "confirmed"); }
    if (now && now.owner.equals(PROGRAM_ID)) {
      out[`delegate_econ_${k}`] = await sendBase([await pb.methods.delegateSim(k < MARKETS ? 4 : 5, arenaId, k < MARKETS ? k : 0).accountsPartial({ authority: wallet.publicKey, pda: acc }).remainingAccounts(val).instruction()], [wallet], `delegate econ ${k}`);
    }
  }
  // treasury PDA (pays Core asset rent in mint_rewards)
  const tre = epda.treasury();
  const tb = (await base.getBalance(tre)) ?? 0;
  const want = Number(process.env.TREASURY_LAMPORTS ?? (NET === "local" ? 2_000_000_000 : 60_000_000));
  if (tb < want / 2) out.treasury = await sendBase([SystemProgram.transfer({ fromPubkey: wallet.publicKey, toPubkey: tre, lamports: want - tb })], [wallet], "fund treasury");
  // RUSH mint: a plain SPL mint whose mint authority is the program PDA (no freeze authority)
  await ensureRushMint(out);
  return out;
}

export async function ensureRushMint(out: Record<string, string> = {}) {
  const mint = RUSH_MINT;
  if (await base.getAccountInfo(mint)) return mint;
  // a plain SPL mint (keypair account) whose mint authority is the program PDA; the keypair only signs the creation
  const kp = Keypair.fromSecretKey(Uint8Array.from(JSON.parse(readFileSync(new URL("./.keys/rush-mint.json", import.meta.url), "utf8"))));
  if (!kp.publicKey.equals(mint)) throw new Error("tools/keeper/.keys/rush-mint.json is not the pinned RUSH mint");
  const lamports = await base.getMinimumBalanceForRentExemption(82);
  const init = new TransactionInstruction({ programId: TOKEN, keys: [{ pubkey: mint, isSigner: false, isWritable: true }], data: Buffer.concat([Buffer.from([20, 6]), epda.mintAuth().toBuffer(), Buffer.from([0])]) });
  out.rushMint = await sendBase([SystemProgram.createAccount({ fromPubkey: wallet.publicKey, newAccountPubkey: mint, lamports, space: 82, programId: TOKEN }), init], [wallet, kp], "create RUSH mint");
  return mint;
}

// ------------------------------------------------------------------ chips
/** ChipBalance for `kp` (init + delegate on Solana, one transaction) and the day's faucet claim on the rollup. */
export async function ensureChips(kp: Keypair, faucet = true): Promise<{ init?: string; faucet?: string }> {
  const r: any = {};
  const c = epda.chips(kp.publicKey);
  // a freshly funded key: wait until the base layer shows the credit (preflight would fail first)
  for (let i = 0; i < 40 && (await base.getBalance(kp.publicKey, "confirmed").catch(() => 0)) < 5_000_000; i++) await sleep(500);
  const info = await base.getAccountInfo(c);
  if (!info) {
    r.init = await sendBase([
      await econIx(kp.publicKey, OP.INIT_CHIPS, 0, 0, [w_(c), r_(SystemProgram.programId)], false),
      await pb.methods.delegateSim(6, 0, 0).accountsPartial({ authority: kp.publicKey, pda: c }).remainingAccounts([{ pubkey: EP.validator, isSigner: false, isWritable: false }]).instruction(),
    ], [kp], "init+delegate chips");
  } else if (info.owner.equals(PROGRAM_ID)) {
    r.init = await sendBase([await pb.methods.delegateSim(6, 0, 0).accountsPartial({ authority: kp.publicKey, pda: c }).remainingAccounts([{ pubkey: EP.validator, isSigner: false, isWritable: false }]).instruction()], [kp], "delegate chips");
  }
  if (faucet) {
    for (let i = 0; i < 40; i++) { if (await er.getAccountInfo(c).catch(() => null)) break; await sleep(500); }
    const ch = await chipsOf(kp.publicKey);
    if (!ch || ch.faucetTotal === 0n || Date.now() / 1000 - ch.faucetAt >= 86_400) {
      const x = await sendEr([await econIx(kp.publicKey, OP.FAUCET, 0, 0, [w_(c)])], [kp], "faucet", 20);
      r.faucet = x.sig; if (x.err) r.faucetErr = x.err;
    }
  }
  return r;
}
export async function chipsOf(w: PublicKey) {
  const i = await er.getAccountInfo(epda.chips(w)).catch(() => null);
  return i ? decodeChips(i.data as Buffer) : null;
}

// ------------------------------------------------------------------ markets
export async function openMarkets(arenaId: number, matchPda: PublicKey) {
  const arena = pda.arena(arenaId);
  const accs = [r_(arena), w_(epda.drops(arena)), ...Array.from({ length: MARKETS }, (_, k) => w_(epda.market(arenaId, k))), r_(matchPda)];
  return sendEr([await econIx(wallet.publicKey, OP.OPEN_MARKETS, 0, 0, accs)], [wallet], "open_markets", 20);
}
export async function market(arenaId: number, k: number) {
  const i = await er.getAccountInfo(epda.market(arenaId, k)).catch(() => null);
  return i ? decodeMarket(i.data as Buffer) : null;
}
/** A bet signed by `who` (the wallet, or its session signer with `sessionToken` for `owner`). */
export async function placeBet(who: Keypair, arenaId: number, k: number, matchPda: PublicKey, outcome: number, amount: bigint, sessionToken?: PublicKey, owner?: PublicKey) {
  const arena = pda.arena(arenaId);
  const accs = [w_(epda.chips(owner ?? who.publicKey)), w_(epda.market(arenaId, k)), r_(matchPda), r_(arena), r_(pda.world(arena))];
  if (sessionToken) accs.push(r_(sessionToken));
  return sendEr([await econIx(who.publicKey, OP.PLACE_BET, outcome, amount, accs)], [who], "place_bet", 6);
}
/** Settles market k (once) and pays every bettor: the bettors' ChipBalances in batches of 12. */
export async function settleAndPay(arenaId: number, k: number, matchPda: PublicKey): Promise<string[]> {
  const sigs: string[] = [];
  for (let round = 0; round < 8; round++) {
    const m = await market(arenaId, k);
    if (!m || !m.match.equals(matchPda) || m.status === 3) break;
    const unpaid = [...new Set(m.bets.filter((b) => !b.paid).map((b) => b.wallet.toBase58()))].map((s) => new PublicKey(s));
    const batch = unpaid.slice(0, 12);
    const r = await sendEr([cu(400_000), await econIx(wallet.publicKey, OP.SETTLE, 0, 0, [w_(epda.market(arenaId, k)), r_(matchPda), ...batch.map((x) => w_(epda.chips(x)))])], [wallet], `settle ${k}`, 10);
    if (r.err) throw new Error(`settle ${k}: ${r.err}`);
    sigs.push(r.sig);
    if (batch.length === 0) break;
  }
  return sigs;
}

// ------------------------------------------------------------------ the market maker (labelled "mm" in the UI)
const TIER_W: Record<string, number> = { Easy: 2, Normal: 4, Good: 7, Expert: 10 };
/** Seeds market k with a small pool spread by bot tier (stake ∝ tier weight); funded only by its faucet claims. */
export async function mmSeed(mm: Keypair, arenaId: number, k: number, matchPda: PublicKey, slots: number[], tiers?: (string | undefined)[], scale = 2n): Promise<{ sigs: string[]; errs: string[] }> {
  const sigs: string[] = [], errs: string[] = [];
  for (const s of slots) {
    const wgt = BigInt(TIER_W[tiers?.[s] ?? "Normal"] ?? 4);
    const r = await placeBet(mm, arenaId, k, matchPda, s, wgt * scale * ONE);
    if (r.err) errs.push(`${k}/${s}: ${r.err.slice(0, 80)}`); else sigs.push(r.sig);
  }
  return { sigs, errs };
}

// ------------------------------------------------------------------ receipt, capsules, follow-up call
export async function writeReceipt(arenaId: number, matchPda: PublicKey, maps: PublicKey[]) {
  const arena = pda.arena(arenaId);
  const accs = [w_(matchPda), r_(arena), r_(pda.world(arena)), r_(epda.drops(arena)), ...Array.from({ length: MARKETS }, (_, k) => r_(epda.market(arenaId, k))), ...maps.map(r_)];
  return sendEr([cu(400_000), await econIx(wallet.publicKey, OP.WRITE_RECEIPT, 0, 0, accs)], [wallet], "write_receipt", 10);
}
export async function requestCapsules(matchPda: PublicKey) {
  return sendEr([await pe.methods.requestCapsules().accountsPartial({ authority: wallet.publicKey, matchAccount: matchPda, oracleQueue: EP.vrfQueue }).instruction()], [wallet], "request_capsules", 10);
}
export async function matchReceipt(conn: typeof base, matchPda: PublicKey) {
  const i = await conn.getAccountInfo(matchPda).catch(() => null);
  return i ? { owner: i.owner, ...decodeReceipt(i.data as Buffer), status: i.data[26], champion: i.data[294] } : null;
}
/** The follow-up call on Solana after the receipt commit: Crown (what 0) + one capsule per human (1 + slot).
 * Idempotent on-chain; retried until every owed asset exists. FAIL_FIRST=1 sends the first attempt with a wrong
 * owner (the forced failure of P4 task 7.3). */
export async function mintRewards(matchPda: PublicKey, logFile?: string): Promise<{ minted: Record<string, string>; attempts: number; failures: string[] }> {
  const minted: Record<string, string> = {}, failures: string[] = [];
  let attempts = 0, forced = process.env.FAIL_FIRST === "1";
  for (let round = 0; round < 6; round++) {
    const rc = await matchReceipt(base, matchPda);
    if (!rc || !rc.owner.equals(PROGRAM_ID)) { await sleep(2000); continue; }
    const owed: { what: number; asset: PublicKey; owner: PublicKey }[] = [];
    if ((rc.flags & (4 | 16)) === 0 && rc.champion < 12) owed.push({ what: 0, asset: epda.crown(matchPda), owner: rc.wallets[rc.champion] });
    if (rc.flags & 2) for (let s = 0; s < 12; s++) if ((rc.humanMask >> s) & 1 && !((rc.mintedMask >> s) & 1)) owed.push({ what: 1 + s, asset: epda.capsule(matchPda, s), owner: rc.wallets[s] });
    if (!owed.length) break;
    for (const o of owed) {
      attempts++;
      const owner = forced ? Keypair.generate().publicKey : o.owner;
      forced = false;
      const accs = [w_(matchPda), w_(o.asset), r_(owner), w_(epda.treasury()), r_(epda.assets()), r_(CORE), r_(SystemProgram.programId)];
      try {
        const sig = await sendBase([cu(300_000), await econIx(wallet.publicKey, OP.MINT_REWARDS, o.what, 0, accs, false)], [wallet], `mint_rewards ${o.what}`);
        minted[o.what === 0 ? "crown" : `capsule${o.what - 1}`] = sig;
        if (logFile) log(logFile, { act: "mint_rewards", what: o.what, asset: o.asset.toBase58(), owner: o.owner.toBase58(), sig });
      } catch (e: any) {
        failures.push(`${o.what}: ${String(e?.message ?? e).slice(0, 160)}`);
        if (logFile) log(logFile, { act: "mint_rewards_failed", what: o.what, err: String(e?.message ?? e).slice(0, 300) });
      }
    }
  }
  return { minted, attempts, failures };
}
