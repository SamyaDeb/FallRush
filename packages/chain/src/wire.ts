// Byte formats (docs/proof/p3/wire-v0.md). All little-endian.
import type { WorldLayout } from './config.ts';
import { hexToBytes } from './b58.ts';

export interface InputCmd { moveX: number; moveY: number; buttons: number; camYaw: number }
export const INPUT_CMD_BYTES = 5;
export const INPUT_BATCH_HEADER = 6;

/** wire-v0 §1: move_x i8, move_y i8, buttons u8, cam_yaw u16 */
export function encodeInputCmd(c: InputCmd, out: Uint8Array, at: number): void {
  const dv = new DataView(out.buffer, out.byteOffset, out.byteLength);
  dv.setInt8(at, c.moveX);
  dv.setInt8(at + 1, c.moveY);
  dv.setUint8(at + 2, c.buttons);
  dv.setUint16(at + 3, c.camYaw, true);
}

/** wire-v0 §2: slot u8, first_tick u32, count u8, count × InputCmd */
export function encodeInputBatch(slot: number, firstTick: number, cmds: InputCmd[]): Uint8Array {
  if (cmds.length < 1 || cmds.length > 3) throw new Error('InputBatch count must be 1..3');
  const out = new Uint8Array(INPUT_BATCH_HEADER + cmds.length * INPUT_CMD_BYTES);
  const dv = new DataView(out.buffer);
  dv.setUint8(0, slot);
  dv.setUint32(1, firstTick >>> 0, true);
  dv.setUint8(5, cmds.length);
  cmds.forEach((c, k) => encodeInputCmd(c, out, INPUT_BATCH_HEADER + k * INPUT_CMD_BYTES));
  return out;
}

export function decodeInputBatch(b: Uint8Array): { slot: number; firstTick: number; cmds: InputCmd[] } {
  const err = validateInputBatch(b);
  if (err) throw new Error(err);
  const dv = new DataView(b.buffer, b.byteOffset, b.byteLength);
  const count = b[5];
  const cmds: InputCmd[] = [];
  for (let k = 0; k < count; k++) {
    const at = INPUT_BATCH_HEADER + k * INPUT_CMD_BYTES;
    cmds.push({ moveX: dv.getInt8(at), moveY: dv.getInt8(at + 1), buttons: b[at + 2], camYaw: dv.getUint16(at + 3, true) });
  }
  return { slot: b[0], firstTick: dv.getUint32(1, true), cmds };
}

export function validateInputBatch(b: Uint8Array): string | null {
  if (!(b instanceof Uint8Array)) return 'InputBatch must be a Uint8Array';
  if (b.length < INPUT_BATCH_HEADER) return 'InputBatch too short';
  const count = b[5];
  if (count < 1 || count > 3) return 'InputBatch count must be 1..3';
  if (b.length !== INPUT_BATCH_HEADER + count * INPUT_CMD_BYTES) return 'InputBatch length mismatch';
  return null;
}

// ---- World account → normalized WorldView --------------------------------------------------------------------
// The bundle hands Unity a layout-independent view so C# never needs P2's offsets:
//   0  u32 tick | 4 u32 core_version | 8 u32 arena_seq | 12 u16 flags | 14 u16 world_len
//   16 u32×12 applied_tick | 64 World bytes (world_len)
// Missing fields (offset null in config) read as 0.
export const VIEW_HEADER = 64;
export const VIEW_SLOTS = 12;

export interface ParsedWorld { tick: number; view: Uint8Array; complete: boolean }

export function parseWorldAccount(acc: Uint8Array, L: WorldLayout): ParsedWorld | null {
  const dv = new DataView(acc.buffer, acc.byteOffset, acc.byteLength);
  if (L.magicHex) {
    const m = hexToBytes(L.magicHex);
    if (acc.length < m.length) return null;
    for (let i = 0; i < m.length; i++) if (acc[i] !== m[i]) return null;
  }
  const u32 = (off: number | null | undefined) => (off == null || off + 4 > acc.length ? 0 : dv.getUint32(off, true));
  const u16 = (off: number | null | undefined) => (off == null || off + 2 > acc.length ? 0 : dv.getUint16(off, true));
  if (L.tickOffset + 4 > acc.length || L.offset > acc.length) return null;
  const worldLen = L.lenOffset == null ? acc.length - L.offset : L.lenBytes === 4 ? u32(L.lenOffset) : u16(L.lenOffset);
  if (worldLen > 0xffff) return null;
  if (L.offset + worldLen > acc.length) return null;
  const view = new Uint8Array(VIEW_HEADER + worldLen);
  const vd = new DataView(view.buffer);
  const tick = u32(L.tickOffset);
  vd.setUint32(0, tick, true);
  vd.setUint32(4, u32(L.coreVersionOffset), true);
  vd.setUint32(8, u32(L.arenaSeqOffset), true);
  let flags = u16(L.flagsOffset);
  if (L.flagsOffset == null && L.phaseOffset != null && L.phaseOffset < acc.length) {
    flags = 1 | (L.donePhase != null && acc[L.phaseOffset] >= L.donePhase ? 2 : 0);
  }
  vd.setUint16(12, flags, true);
  vd.setUint16(14, worldLen, true);
  const n = Math.min(L.appliedTickCount ?? VIEW_SLOTS, VIEW_SLOTS);
  if (L.appliedTickOffset != null) for (let i = 0; i < n; i++) vd.setUint32(16 + 4 * i, u32(L.appliedTickOffset + 4 * i), true);
  view.set(acc.subarray(L.offset, L.offset + worldLen), VIEW_HEADER);
  const complete = L.completeOffset == null || (L.completeOffset < acc.length && acc[L.completeOffset] === 0);
  return { tick, view, complete };
}

// ---- drainWorlds framing ---------------------------------------------------------------------------------------
// per entry: u32 len (of bytes) | f64 recvMs | u32 seq | bytes (WorldView)
export const DRAIN_ENTRY_HEADER = 16;

export function writeDrainEntry(into: Uint8Array, at: number, recvMs: number, seq: number, bytes: Uint8Array): number {
  const need = DRAIN_ENTRY_HEADER + bytes.length;
  if (at + need > into.length) return -1;
  const dv = new DataView(into.buffer, into.byteOffset, into.byteLength);
  dv.setUint32(at, bytes.length, true);
  dv.setFloat64(at + 4, recvMs, true);
  dv.setUint32(at + 12, seq >>> 0, true);
  into.set(bytes, at + DRAIN_ENTRY_HEADER);
  return at + need;
}

export function readDrainEntries(buf: Uint8Array, len: number): { recvMs: number; seq: number; bytes: Uint8Array }[] {
  const dv = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
  const out = [];
  let at = 0;
  while (at < len) {
    const n = dv.getUint32(at, true);
    out.push({ recvMs: dv.getFloat64(at + 4, true), seq: dv.getUint32(at + 12, true), bytes: buf.slice(at + 16, at + 16 + n) });
    at += 16 + n;
  }
  return out;
}

// ---- emulator binary frames (wire-v0 §4) ---------------------------------------------------------------------
export const OP_SUBMIT = 0x02;
export const OP_CONFIRM = 0x83;
export const OP_WORLD = 0x90;
export const OP_WORLD_POLL = 0x91;
export const CONFIRM_STATUS = ['ok', 'late', 'window', 'badSlot'] as const;

export function encodeSubmitFrame(txid: number, batch: Uint8Array): Uint8Array {
  const out = new Uint8Array(5 + batch.length);
  out[0] = OP_SUBMIT;
  new DataView(out.buffer).setUint32(1, txid >>> 0, true);
  out.set(batch, 5);
  return out;
}

export function decodeConfirmFrame(b: Uint8Array): { txid: number; status: number; chainTick: number } | null {
  if (b.length < 10 || b[0] !== OP_CONFIRM) return null;
  const dv = new DataView(b.buffer, b.byteOffset, b.byteLength);
  return { txid: dv.getUint32(1, true), status: b[5], chainTick: dv.getUint32(6, true) };
}
