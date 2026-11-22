// P2 program specifics (docs/proof/P2-onchain-sim.md §2, §10; programs/fall_rush/src/sim.rs, lib.rs; tools/keeper/run.ts).
// Offsets come from config (P2Config); only the algorithms live here.
import { PublicKey } from '@solana/web3.js';
import type { P2Config } from './config.ts';
import { INPUT_BATCH_HEADER, INPUT_CMD_BYTES, validateInputBatch } from './wire.ts';

export const N_SLOTS = 12;
export const P2_BATCH = 3;

/**
 * wire-v0 InputBatch (u8 slot, u32 first_tick, u8 count, count × {i8 mx, i8 my, u8 buttons, u16 yaw}) →
 * submit_input(slot u8, start u32, batch [u8; 12]) with 3 × {mx, my, buttons, cam_yaw >> 8} (sim.rs submit_input).
 * A batch with fewer than 3 commands repeats its last command (the program always writes 3 ticks).
 */
export function toP2Batch(b: Uint8Array): { slot: number; start: number; batch12: Uint8Array } {
  const err = validateInputBatch(b);
  if (err) throw new Error(err);
  const dv = new DataView(b.buffer, b.byteOffset, b.byteLength);
  const count = b[5];
  const out = new Uint8Array(4 * P2_BATCH);
  for (let k = 0; k < P2_BATCH; k++) {
    const at = INPUT_BATCH_HEADER + Math.min(k, count - 1) * INPUT_CMD_BYTES;
    out[k * 4] = b[at];
    out[k * 4 + 1] = b[at + 1];
    out[k * 4 + 2] = b[at + 2];
    out[k * 4 + 3] = dv.getUint16(at + 3, true) >> 8;
  }
  return { slot: b[0], start: dv.getUint32(1, true), batch12: out };
}

export interface ArenaInfo { matchAccount: PublicKey; status: number; humanMask: number; owners: PublicKey[]; wallets: PublicKey[] }

export function decodeArena(d: Uint8Array, c: P2Config): ArenaInfo | null {
  const L = c.arenaLayout;
  if (d.length < L.minLen) return null;
  const pk = (o: number) => new PublicKey(d.subarray(o, o + 32));
  const keys = (o: number) => Array.from({ length: N_SLOTS }, (_, i) => pk(o + 32 * i));
  return {
    matchAccount: pk(L.matchOffset),
    status: d[L.statusOffset],
    humanMask: d[L.humanMaskOffset] | (d[L.humanMaskOffset + 1] << 8),
    owners: keys(L.ownersOffset),
    wallets: keys(L.walletsOffset),
  };
}

export interface MatchInfo { matchId: Uint8Array; status: number; playerCount: number; currentRound: number; roundTargets: number[]; slotStates: number[] }

export function decodeMatch(d: Uint8Array, c: P2Config): MatchInfo | null {
  const L = c.matchLayout;
  if (d.length < L.minLen) return null;
  const pc = d[L.playerCountOffset];
  return {
    matchId: d.slice(L.matchIdOffset, L.matchIdOffset + 16),
    status: d[L.statusOffset],
    playerCount: pc,
    currentRound: d[L.currentRoundOffset],
    roundTargets: Array.from(d.subarray(L.roundTargetsOffset, L.roundTargetsOffset + 4)),
    slotStates: Array.from({ length: Math.min(pc, 20) }, (_, i) => d[L.slotsOffset + i * L.slotStride]),
  };
}

async function sha256(b: Uint8Array): Promise<Uint8Array> {
  return new Uint8Array(await globalThis.crypto.subtle.digest('SHA-256', b as unknown as ArrayBuffer));
}

/**
 * The keeper's synthetic finalists (tools/keeper/run.ts recordRounds13): alive = 0..playerCount-1; for rounds 1..3
 * order alive by sha256(matchId ‖ [r, s]) as LE u32 ascending (stable), keep the first roundTargets[r-1]. Sorted.
 */
export async function syntheticFinalists(matchId: Uint8Array, playerCount: number, roundTargets: number[]): Promise<number[]> {
  let alive = Array.from({ length: playerCount }, (_, i) => i);
  for (let r = 1; r <= 3; r++) {
    const keyed = await Promise.all(alive.map(async (s) => {
      const pre = new Uint8Array(matchId.length + 2);
      pre.set(matchId, 0); pre[matchId.length] = r; pre[matchId.length + 1] = s;
      const h = await sha256(pre);
      return { s, k: new DataView(h.buffer).getUint32(0, true) };
    }));
    keyed.sort((a, b) => a.k - b.k);
    alive = keyed.slice(0, roundTargets[r - 1]).map((x) => x.s);
  }
  return alive.sort((a, b) => a - b);
}

export function pickSlot(finalists: number[], humanMask: number): number | null {
  for (const s of [...finalists].sort((a, b) => a - b)) if (((humanMask >> s) & 1) === 0) return s;
  return null;
}

/** Custom program error code in a transaction error ({InstructionError:[i,{Custom:n}]}) or a message, else null. */
export function customCode(err: unknown): number | null {
  try {
    const s = typeof err === 'string' ? err : JSON.stringify(err);
    const m = s.match(/"Custom":(\d+)/) || s.match(/custom program error: 0x([0-9a-f]+)/i);
    if (!m) return null;
    return m[0].includes('0x') ? parseInt(m[1], 16) : Number(m[1]);
  } catch { return null; }
}
