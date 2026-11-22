// world-view.mjs — decodes a WorldAcc (wire-v0 §3) and the rush-core World bytes inside it.
//
// The parent ports this to C#, so it is deliberately plain: a DataView, little-endian reads, fixed offsets.
//   * Section offsets (players, round state, round result inside the round state, slot rules inside a player)
//     come from `layout` = Core.layout() (rcx_layout), so they follow rush-core if a section grows.
//   * Field offsets *inside* a section are fixed by the Rust `Codec::enc` order and are written out below with the
//     Rust source they mirror (world.rs `World/Player/MotorState/InputTrack`, rules/mod.rs `SlotRules/RoundState/
//     RoundResult`). If rush-core reorders a struct, those tables change (the fixture test catches it).
//
// layout (rush-core 0x10001): headerLen 22, playerStride 115, playersOffset 22, reactiveLen 318, dropQueueLen 48,
// roundOffset 1768, roundLen 142, directorOffset 1910, directorLen 123, totalLen 2037, slotRulesOffset 46,
// botOffset 89, roundResultOffset 52.

export const MAX_PLAYERS = 12;

// ---- WorldAcc envelope (wire-v0 §3)
export const ACC = {
  magic: 0,          // u8[8] "FRWORLD0"
  coreVersion: 8,    // u32
  tick: 12,          // u32
  applied: 16,       // u32 × 12
  arenaSeq: 64,      // u32
  worldLen: 68,      // u16
  flags: 70,         // u16  bit0 round begun, bit1 round over
  world: 72,         // World bytes
};
export const ACC_MAGIC = 'FRWORLD0';
export const ACC_FLAG_LIVE = 1;
export const ACC_FLAG_OVER = 2;

// ---- World header (World::enc): version u8, tick u32, rate u8, round_index u8, map_key u8, variant u8,
// modifier u8, hazard_seed u64, round_start u32  (= 22 B = layout.headerLen)
const W = { version: 0, tick: 1, rate: 5, roundIndex: 6, mapKey: 7, variant: 8, modifier: 9, hazardSeed: 10, roundStart: 18 };

// ---- Player (Player::enc), offsets inside one player record (stride = layout.playerStride)
//   MotorState: pos x,y,z i32 (mm), vel x,y,z i32, yaw, flags, stun, coyote, jump_buffer, dive_cooldown, land,
//   immunity, surface (u8 each) = 33 B; then player flags u8, respawn_ticks u8, last_step u8;
//   InputTrack: last cmd 5 B, missing u8; falls u16, hits u16; SlotRules (at layout.slotRulesOffset); BotState.
const P = {
  pos: 0, vel: 12, yaw: 24, motorFlags: 25, stun: 26, coyote: 27, jumpBuffer: 28, diveCooldown: 29, land: 30,
  immunity: 31, surface: 32, flags: 33, respawn: 34, lastStep: 35, lastCmd: 36, missing: 41, falls: 42, hits: 44,
};
export const PLAYER_FLAG = { ACTIVE: 1, BOT: 2, DETACHED: 4 };

// ---- SlotRules (rules::SlotRules::enc), offsets from the slot-rules start (43 B)
const SR = {
  flags: 0, checkpoint: 1, checkpointTick: 2, doneTick: 6, place: 10, outTick: 11, outReason: 15, score: 16,
  scoreTick: 20, height: 24, deepest: 28, tiles: 29, touched: 31, lidShelf: 35, lidFalls: 36, lidDropTick: 37,
  rejected: 41,
};
export const SLOT_FLAG = { IN: 1, OUT: 2, DONE: 4, QUALIFIED: 8, ELIMINATED: 16 };

// ---- RoundState (rules::RoundState::enc), offsets from layout.roundOffset (142 B)
const RS = {
  mode: 0, phase: 1, phaseStart: 2, rushTick: 6, rushEnd: 10, introTicks: 14, countdownTicks: 16, target: 18,
  qualifyCount: 19, qualifiedCount: 20, finishedCount: 21, timerExpired: 22, endPending: 23,
  respawnCursor: 24,  // u8 × 16
  finishOrder: 40,    // u8 × 12
  // result: RoundResult at layout.roundResultOffset (52), 85 B; then has_result bool, event_seq u32
};
// ---- RoundResult (rules::RoundResult::enc), offsets from the result start (85 B)
const RR = {
  n: 0, order: 1 /* u8 × 12 */, qualified: 13 /* u16 bit k = order[k] qualified */, reasons: 15 /* u8 × 12 */,
  values: 27 /* i32 × 12 */, target: 75, startTick: 76, endTick: 80, timerExpired: 84,
};
const RESULT_LEN = 85;

export const PHASE = { WAITING: 0, INTRO: 1, COUNTDOWN: 2, RUSH: 3, ENDED: 4, SANDBOX: 5 };

/** Decodes the World bytes (rush_core::World::to_bytes) at `off` in `dv`. */
export function decodeWorld(dv, off, layout) {
  const u8 = (o) => dv.getUint8(off + o);
  const u16 = (o) => dv.getUint16(off + o, true);
  const u32 = (o) => dv.getUint32(off + o, true);
  const i32 = (o) => dv.getInt32(off + o, true);
  const vec = (o) => ({ x: i32(o), y: i32(o + 4), z: i32(o + 8) });

  const players = [];
  for (let s = 0; s < MAX_PLAYERS; s++) {
    const p = layout.playersOffset + s * layout.playerStride;
    const r = p + layout.slotRulesOffset;
    const flags = u8(p + P.flags);
    players.push({
      slot: s,
      active: (flags & PLAYER_FLAG.ACTIVE) !== 0,
      bot: (flags & PLAYER_FLAG.BOT) !== 0,
      flags,
      pos: vec(p + P.pos),            // mm
      vel: vec(p + P.vel),            // mm/s
      yaw: u8(p + P.yaw),             // 256 steps per turn
      motorFlags: u8(p + P.motorFlags),
      stun: u8(p + P.stun),
      respawn: u8(p + P.respawn),     // respawn ticks left
      lastStep: u8(p + P.lastStep),
      missing: u8(p + P.missing),     // InputTrack: consecutive missing inputs
      falls: u16(p + P.falls),
      hits: u16(p + P.hits),
      rules: {
        flags: u8(r + SR.flags),      // SLOT_FLAG bits
        checkpoint: u8(r + SR.checkpoint),
        doneTick: u32(r + SR.doneTick),
        place: u8(r + SR.place),
        outTick: u32(r + SR.outTick),
        outReason: u8(r + SR.outReason),
        score: i32(r + SR.score),
        height: i32(r + SR.height),
        deepest: u8(r + SR.deepest),
        tiles: u16(r + SR.tiles),
        lidShelf: u8(r + SR.lidShelf),
        lidFalls: u8(r + SR.lidFalls),
        lidDropTick: u32(r + SR.lidDropTick),
      },
    });
  }

  const ro = layout.roundOffset;
  const rr = ro + layout.roundResultOffset;
  const n = u8(rr + RR.n);
  const result = {
    n,
    order: [], qualified: u16(rr + RR.qualified), reasons: [], values: [],
    target: u8(rr + RR.target), startTick: u32(rr + RR.startTick), endTick: u32(rr + RR.endTick),
    timerExpired: u8(rr + RR.timerExpired) !== 0,
  };
  for (let k = 0; k < MAX_PLAYERS; k++) {
    result.order.push(u8(rr + RR.order + k));
    result.reasons.push(u8(rr + RR.reasons + k));
    result.values.push(i32(rr + RR.values + 4 * k));
  }
  const round = {
    mode: u8(ro + RS.mode), phase: u8(ro + RS.phase), phaseStart: u32(ro + RS.phaseStart),
    rushTick: u32(ro + RS.rushTick), rushEnd: u32(ro + RS.rushEnd),
    introTicks: u16(ro + RS.introTicks), countdownTicks: u16(ro + RS.countdownTicks), target: u8(ro + RS.target),
    qualifyCount: u8(ro + RS.qualifyCount), qualifiedCount: u8(ro + RS.qualifiedCount),
    finishedCount: u8(ro + RS.finishedCount), timerExpired: u8(ro + RS.timerExpired) !== 0,
    hasResult: u8(rr + RESULT_LEN) !== 0,
    eventSeq: u32(rr + RESULT_LEN + 1),
    result,
  };

  return {
    version: u8(W.version), tick: u32(W.tick), rate: u8(W.rate), roundIndex: u8(W.roundIndex),
    mapKey: u8(W.mapKey), variant: u8(W.variant), modifier: u8(W.modifier),
    hazardSeed: dv.getBigUint64(off + W.hazardSeed, true), roundStart: u32(W.roundStart),
    players, round,
    directorPhase: u8(layout.directorOffset),
    eventSeq: u32(layout.totalLen - 4),
  };
}

/** Decodes a WorldAcc (wire-v0 §3). `bytes`: Uint8Array starting at the magic (strip the frame's 5-byte header). */
export function decodeWorldAcc(bytes, layout) {
  const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const magic = String.fromCharCode(...bytes.subarray(0, 8));
  if (magic !== ACC_MAGIC) throw new Error(`bad WorldAcc magic ${JSON.stringify(magic)}`);
  const applied = [];
  for (let s = 0; s < MAX_PLAYERS; s++) applied.push(dv.getUint32(ACC.applied + 4 * s, true));
  const worldLen = dv.getUint16(ACC.worldLen, true);
  if (ACC.world + worldLen > bytes.length) throw new Error('WorldAcc truncated');
  return {
    coreVersion: dv.getUint32(ACC.coreVersion, true),
    tick: dv.getUint32(ACC.tick, true),
    applied,
    arenaSeq: dv.getUint32(ACC.arenaSeq, true),
    worldLen,
    flags: dv.getUint16(ACC.flags, true),
    world: decodeWorld(dv, ACC.world, layout),
  };
}

/** Builds a WorldAcc (the emulator side). */
export function encodeWorldAcc({ coreVersion, tick, applied, arenaSeq, flags }, world) {
  const out = new Uint8Array(ACC.world + world.length);
  const dv = new DataView(out.buffer);
  for (let i = 0; i < 8; i++) out[i] = ACC_MAGIC.charCodeAt(i);
  dv.setUint32(ACC.coreVersion, coreVersion, true);
  dv.setUint32(ACC.tick, tick, true);
  for (let s = 0; s < MAX_PLAYERS; s++) dv.setUint32(ACC.applied + 4 * s, applied[s] >>> 0, true);
  dv.setUint32(ACC.arenaSeq, arenaSeq, true);
  dv.setUint16(ACC.worldLen, world.length, true);
  dv.setUint16(ACC.flags, flags, true);
  out.set(world, ACC.world);
  return out;
}
