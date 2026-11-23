// Zero-copy layouts (programs/fall_rush/src/{sim,econ,state}.rs; the same offsets the bundle + Unity use).
import { b58 } from './net';
const dv = (d: Uint8Array) => new DataView(d.buffer, d.byteOffset, d.byteLength);
const u64 = (d: Uint8Array, o: number) => dv(d).getBigUint64(o, true);
const u32 = (d: Uint8Array, o: number) => dv(d).getUint32(o, true);
const u16 = (d: Uint8Array, o: number) => d[o] | (d[o + 1] << 8);
const pk = (d: Uint8Array, o: number) => b58(d.subarray(o, o + 32));
const hex = (d: Uint8Array, a: number, b: number) => Array.from(d.subarray(a, b), (x) => x.toString(16).padStart(2, '0')).join('');

export interface WorldAccView { tick: number; len: number; part: number; phase: number; round: number; coreVersion: number; serial: number; roundPhase: number; cur: Uint8Array; curSome: number; world: Uint8Array }
export function decodeWorldAcc(d: Uint8Array): WorldAccView {
  const len = u32(d, 132);
  return { tick: u32(d, 128), len, part: d[136], phase: d[138], round: d[139], coreVersion: u16(d, 142), cur: d.subarray(144, 192), curSome: u16(d, 192), serial: u32(d, 194), roundPhase: d[199], world: d.subarray(208, 208 + len) };
}
/** Arena (1,432 B): match @40, wallets @(40+32+160+384), id @1408, status @1409, human_mask @1412. */
export function decodeArena(d: Uint8Array) {
  return { match: pk(d, 40), id: d[1408], status: d[1409], humanMask: u16(d, 1412), wallets: Array.from({ length: 12 }, (_, s) => pk(d, 8 + 32 + 32 + 160 + 384 + 32 * s)) };
}
export const MARKET_NAMES = ['Champion', 'Final winner', 'Qualify R1', 'Qualify R2', 'Qualify R3'];
export const LABELS = ['', '◆ program-settled', '● server-recorded', '● Classic fallback (refund)', 'aborted (refund)', 'no winner (refund)'];
export function decodeMarket(d: Uint8Array) {
  const stake = Array.from({ length: 12 }, (_, i) => u64(d, 104 + 8 * i));
  const n = d[207];
  const bets = Array.from({ length: n }, (_, i) => { const b = 222 + 48 * i; return { wallet: pk(d, b), amount: u64(d, b + 32), outcome: d[b + 40], paid: d[b + 41] }; });
  return { match: pk(d, 8), pool: u64(d, 72), winningStake: u64(d, 80), paidTotal: u64(d, 88), stake, winners: u16(d, 200), kind: d[203], round: d[204], status: d[205], label: d[206], nBets: n, refund: d[209], bets };
}
export function decodeDrops(d: Uint8Array) {
  const count = d[92];
  return { match: pk(d, 40), pot: u64(d, 72), lastTick: u32(d, 88), count, buys: Array.from({ length: count }, (_, i) => { const b = 96 + 40 * i; return { by: pk(d, b), tick: u32(d, b + 32), kind: d[b + 36] }; }) };
}
export function decodeChips(d: Uint8Array) {
  return { wallet: pk(d, 8), balance: u64(d, 40), faucetTotal: u64(d, 48), cashedOut: u64(d, 56), won: u64(d, 64), spent: u64(d, 72) };
}
/** Match (borsh; status @26, flags @27, players @128, round @129, slots @214 (4 B each), champion @294, receipt @295). */
export function decodeMatch(d: Uint8Array) {
  const r = 295;
  const markets = Array.from({ length: 5 }, (_, k) => { const b = r + 617 + 20 * k; return { pool: u64(d, b), winningStake: u64(d, b + 8), winners: u16(d, b + 16), label: d[b + 18], status: d[b + 19] }; });
  return {
    matchId: hex(d, 8, 24), status: d[26], flags: d[27], players: d[128], round: d[129], champion: d[294],
    slots: Array.from({ length: 12 }, (_, i) => d[214 + 4 * i]),
    receipt: {
      flags: d[r], trust: d[r + 1], coreVersion: u16(d, r + 2), humanMask: u16(d, r + 4), mintedMask: u16(d, r + 6), drops: d[r + 8], dropSpent: u64(d, r + 9),
      mapHash: hex(d, r + 17, r + 49), preFinal: hex(d, r + 49, r + 81), finalTranscript: hex(d, r + 81, r + 113), dropLog: hex(d, r + 113, r + 145), capsuleSeed: hex(d, r + 145, r + 177),
      crown: pk(d, r + 177), wallets: Array.from({ length: 12 }, (_, s) => pk(d, r + 209 + 32 * s)), placements: Array.from(d.subarray(r + 593, r + 605)), rarity: Array.from(d.subarray(r + 605, r + 617)), markets,
    },
  };
}
export const MATCH_STATUS = ['Created', 'Dealing', 'Dealt', 'Round active', 'Round resolved', 'Completed', 'Aborted'];
export const RARITY = ['Common', 'Rare', 'Epic', 'Legendary'];
export const DROPS = ['', 'Lid Frenzy', 'Bumper Rain', 'Wind Gust', 'Sweeper Rush'];
export const DROP_PRICE = [0, 50, 40, 30, 40];

/** Odds of each outcome of a market (shared pool, fee 0 %): pool / stake on that outcome. */
export function odds(m: ReturnType<typeof decodeMarket>, outcome: number): number | null {
  const s = m.stake[outcome];
  return s > 0n ? Number(m.pool) / Number(s) : null;
}
/** Core AssetV1 attributes ("shell", "rarity", …): borsh strings searched after the name/uri. */
export function coreAttrs(d: Uint8Array): Record<string, string> {
  const out: Record<string, string> = {};
  const td = new TextDecoder();
  const str = (o: number): [string, number] | null => { if (o + 4 > d.length) return null; const n = u32(d, o); if (n === 0 || n > 64 || o + 4 + n > d.length) return null; return [td.decode(d.subarray(o + 4, o + 4 + n)), o + 4 + n]; };
  for (const key of ['match', 'matchPda', 'slot', 'rarity', 'shell', 'season', 'rounds', 'players', 'finalOnChain']) {
    const kb = new TextEncoder().encode(key);
    for (let o = 66; o + 4 + kb.length < d.length; o++) {
      if (u32(d, o) !== kb.length) continue;
      let ok = true; for (let i = 0; i < kb.length && ok; i++) ok = d[o + 4 + i] === kb[i];
      if (!ok) continue;
      const v = str(o + 4 + kb.length);
      if (v) { out[key] = v[0]; break; }
    }
  }
  const nameLen = u32(d, 66);
  if (nameLen > 0 && nameLen < 80) out.name = td.decode(d.subarray(70, 70 + nameLen));
  return out;
}
