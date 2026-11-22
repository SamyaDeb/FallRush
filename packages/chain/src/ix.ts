// Config-driven instruction builder: program id, 8-byte discriminator, account metas (fixed / "$ref" / PDA), args.
import { PublicKey, TransactionInstruction } from '@solana/web3.js';
import type { AccountSpec, ArgSpec, InstructionSpec, SeedSpec } from './config.ts';
import { hexToBytes } from './b58.ts';

/** Values a "$name" ref can resolve to: pubkeys, numbers, byte arrays, strings, booleans. */
export type RefValue = PublicKey | number | bigint | Uint8Array | string | boolean;
export type Refs = Record<string, RefValue | undefined>;

export function resolveRef(v: string, refs: Refs): RefValue {
  if (typeof v === 'string' && v.startsWith('$')) {
    const r = refs[v.slice(1)];
    if (r === undefined) throw new Error('unresolved ref ' + v);
    return r;
  }
  return v;
}

export function toPubkey(v: RefValue): PublicKey {
  if (v instanceof PublicKey) return v;
  if (typeof v === 'string') return new PublicKey(v);
  if (v instanceof Uint8Array && v.length === 32) return new PublicKey(v);
  throw new Error('not a pubkey: ' + String(v));
}

export function resolvePubkey(v: string, refs: Refs): PublicKey { return toPubkey(resolveRef(v, refs)); }

function num(v: number | string, refs: Refs): bigint {
  const r = typeof v === 'string' ? resolveRef(v, refs) : v;
  if (typeof r === 'number' || typeof r === 'bigint') return BigInt(r);
  if (typeof r === 'string' && /^-?\d+$/.test(r)) return BigInt(r);
  throw new Error('not a number: ' + String(v));
}

function le(n: bigint, bytes: number, signed = false): Uint8Array {
  const out = new Uint8Array(bytes);
  const dv = new DataView(out.buffer);
  if (bytes === 1) dv.setUint8(0, Number(BigInt.asUintN(8, n)));
  else if (bytes === 2) dv.setUint16(0, Number(BigInt.asUintN(16, n)), true);
  else if (bytes === 4) dv.setUint32(0, Number(BigInt.asUintN(32, n)), true);
  else if (signed) dv.setBigInt64(0, n, true);
  else dv.setBigUint64(0, n, true);
  return out;
}

export function seedBytes(s: SeedSpec, refs: Refs): Uint8Array {
  if ('utf8' in s) return new TextEncoder().encode(s.utf8);
  if ('hex' in s) return hexToBytes(s.hex);
  if ('pubkey' in s) return resolvePubkey(s.pubkey, refs).toBytes();
  if ('u8' in s) return le(num(s.u8, refs), 1);
  if ('u16le' in s) return le(num(s.u16le, refs), 2);
  if ('u32le' in s) return le(num(s.u32le, refs), 4);
  if ('u64le' in s) return le(num(s.u64le, refs), 8);
  throw new Error('bad seed spec ' + JSON.stringify(s));
}

export function resolveAccount(a: AccountSpec, programId: PublicKey, refs: Refs): PublicKey {
  if (a.address) return resolvePubkey(a.address, refs);
  if (a.pda) {
    const pid = a.pda.programId ? resolvePubkey(a.pda.programId, refs) : programId;
    return PublicKey.findProgramAddressSync(a.pda.seeds.map((s) => seedBytes(s, refs)), pid)[0];
  }
  throw new Error('account spec needs address or pda: ' + JSON.stringify(a));
}

function concat(parts: Uint8Array[]): Uint8Array {
  const n = parts.reduce((a, p) => a + p.length, 0);
  const out = new Uint8Array(n);
  let at = 0;
  for (const p of parts) { out.set(p, at); at += p.length; }
  return out;
}

export function encodeArgs(args: ArgSpec[], refs: Refs): Uint8Array {
  const parts: Uint8Array[] = [];
  for (const a of args) {
    const src = (a as { from?: string }).from;
    const get = () => (src !== undefined ? resolveRef(src, refs) : (a as { value?: RefValue }).value);
    switch (a.type) {
      case 'raw': case 'vec': {
        const b = get();
        if (!(b instanceof Uint8Array)) throw new Error('arg ' + a.type + ' needs bytes');
        if (a.type === 'vec') parts.push(le(BigInt(b.length), 4));
        parts.push(b);
        break;
      }
      case 'u8': parts.push(le(num(get() as number, refs), 1)); break;
      case 'u16': parts.push(le(num(get() as number, refs), 2)); break;
      case 'u32': parts.push(le(num(get() as number, refs), 4)); break;
      case 'u64': parts.push(le(num(get() as number, refs), 8)); break;
      case 'i64': parts.push(le(num(get() as number, refs), 8, true)); break;
      case 'bool': parts.push(new Uint8Array([get() ? 1 : 0])); break;
      case 'pubkey': parts.push(toPubkey(get() as RefValue).toBytes()); break;
      case 'string': {
        const s = new TextEncoder().encode(String(get()));
        parts.push(le(BigInt(s.length), 4), s);
        break;
      }
      default: throw new Error('bad arg type ' + (a as ArgSpec).type);
    }
  }
  return concat(parts);
}

export function buildInstruction(spec: InstructionSpec, refs: Refs): TransactionInstruction {
  const programId = resolvePubkey(spec.programId, refs);
  const disc = hexToBytes(spec.discriminator);
  if (disc.length !== 8) throw new Error('discriminator must be 8 bytes');
  const keys = spec.accounts.map((a) => ({ pubkey: resolveAccount(a, programId, refs), isSigner: !!a.signer, isWritable: !!a.writable }));
  const data = concat([disc, encodeArgs(spec.args || [], refs)]);
  return new TransactionInstruction({ programId, keys, data: Buffer.from(data) });
}
