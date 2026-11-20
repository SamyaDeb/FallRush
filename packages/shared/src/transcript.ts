/**
 * Rolling transcript (doc 06 §3). Mirrors `programs/fall_rush/src/state.rs::Match::append`.
 *   init  = sha256("fallrush-transcript-v1" ‖ match_id[16] ‖ roster_hash[32])
 *   next  = sha256(prev ‖ tag u8 ‖ seq u32-LE ‖ round u8 ‖ slot u8 ‖ arg bytes)
 */
import { createHash } from "node:crypto";

export const TAG = {
  DEAL: 1, // round 0, slot 255, arg = vrf randomness (32 B)
  START_ROUND: 2, // slot 255, arg = []
  QUALIFIED: 3, // arg = [placement]
  ELIMINATED: 4, // arg = [reason]
  COMPLETE_ROUND: 5, // slot 255, arg = [qualified_count]
  COMPLETE_MATCH: 6, // slot = champion, arg = []
  ABORT: 7, // slot 255, arg = [reason]
} as const;

export const NO_SLOT = 255;

export function transcriptInit(matchId: Uint8Array, rosterHash: Uint8Array): Buffer {
  return createHash("sha256").update(Buffer.from("fallrush-transcript-v1")).update(matchId).update(rosterHash).digest();
}

export function transcriptNext(prev: Uint8Array, tag: number, seq: number, round: number, slot: number, arg: Uint8Array = new Uint8Array()): Buffer {
  const seqLe = Buffer.alloc(4);
  seqLe.writeUInt32LE(seq);
  return createHash("sha256").update(prev).update(Buffer.from([tag])).update(seqLe).update(Buffer.from([round, slot])).update(arg).digest();
}

/** caller_seed = sha256("fallrush-deal" ‖ match_id ‖ ruleset_hash ‖ map_pool_hash) (doc 06 §3 #5). */
export function dealCallerSeed(matchId: Uint8Array, rulesetHash: Uint8Array, mapPoolHash: Uint8Array): Buffer {
  return createHash("sha256").update(Buffer.from("fallrush-deal")).update(matchId).update(rulesetHash).update(mapPoolHash).digest();
}
