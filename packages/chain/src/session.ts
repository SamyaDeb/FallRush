// gum session-keys (GPL Session) SessionTokenV2 — own encoding, from the Rust source of session-keys 3.1.1
// (~/.cargo/registry/src/index.crates.io-*/session-keys-3.1.1/src/lib.rs). Citations in README.md.
import { PublicKey, SystemProgram, TransactionInstruction } from '@solana/web3.js';
import { hexToBytes } from './b58.ts';

/** lib.rs:12 declare_id! */
export const SESSION_KEYS_PROGRAM_ID = 'KeyspM2ssCJbqUhQ4k7sveSiY4WjnYsrXkC8oDbwde5';
/** lib.rs:484 SessionTokenV2::SEED_PREFIX */
export const SESSION_V2_SEED = 'session_token_v2';
/** sha256("global:create_session_v2")[0..8] (Anchor ix discriminator; fn at lib.rs:58) */
export const IX_CREATE_SESSION_V2 = hexToBytes('dfe96c0741c2eb26');
/** sha256("global:revoke_session_v2")[0..8] (lib.rs:68) */
export const IX_REVOKE_SESSION_V2 = hexToBytes('d33b7dbc2b9b0866');
/** sha256("account:SessionTokenV2")[0..8] (#[account] at lib.rs:439) */
export const ACC_SESSION_TOKEN_V2 = hexToBytes('b20355fe0d748029');
/** lib.rs:483: 8 + size_of::<SessionTokenV2>() = 8 + 4×32 + 8 */
export const SESSION_TOKEN_V2_LEN = 144;
/** lib.rs:131-134/324-327: valid_until ≤ now + 7 days */
export const MAX_VALIDITY_SEC = 60 * 60 * 24 * 7;

/** PDA seeds lib.rs:274-279: [SEED_PREFIX, target_program, session_signer, authority] */
export function sessionTokenV2Pda(programId: PublicKey, targetProgram: PublicKey, sessionSigner: PublicKey, authority: PublicKey): PublicKey {
  return PublicKey.findProgramAddressSync(
    [new TextEncoder().encode(SESSION_V2_SEED), targetProgram.toBytes(), sessionSigner.toBytes(), authority.toBytes()],
    programId,
  )[0];
}

function optBool(v: boolean | null | undefined): number[] { return v == null ? [0] : [1, v ? 1 : 0]; }
function optI64(v: bigint | number | null | undefined, signed: boolean): number[] {
  if (v == null) return [0];
  const b = new Uint8Array(8);
  const dv = new DataView(b.buffer);
  if (signed) dv.setBigInt64(0, BigInt(v), true); else dv.setBigUint64(0, BigInt(v), true);
  return [1, ...b];
}

/** Borsh args of create_session_v2(top_up: Option<bool>, valid_until: Option<i64>, lamports: Option<u64>) — lib.rs:58-63 */
export function encodeCreateSessionV2Data(topUp: boolean | null, validUntil: bigint | number | null, lamports: bigint | number | null): Uint8Array {
  return Uint8Array.from([...IX_CREATE_SESSION_V2, ...optBool(topUp), ...optI64(validUntil, true), ...optI64(lamports, false)]);
}

export interface CreateSessionV2Params {
  programId?: PublicKey;
  targetProgram: PublicKey;
  sessionSigner: PublicKey;
  feePayer: PublicKey;
  authority: PublicKey;
  topUp: boolean | null;
  validUntil: number | bigint | null;
  lamports: number | bigint | null;
}

/** Accounts lib.rs:271-297 (CreateSessionTokenV2), in declaration order. */
export function createSessionV2Ix(p: CreateSessionV2Params): { ix: TransactionInstruction; token: PublicKey } {
  const programId = p.programId ?? new PublicKey(SESSION_KEYS_PROGRAM_ID);
  const token = sessionTokenV2Pda(programId, p.targetProgram, p.sessionSigner, p.authority);
  const ix = new TransactionInstruction({
    programId,
    keys: [
      { pubkey: token, isSigner: false, isWritable: true }, // session_token (init, payer = fee_payer)
      { pubkey: p.sessionSigner, isSigner: true, isWritable: true }, // session_signer: Signer, mut
      { pubkey: p.feePayer, isSigner: true, isWritable: true }, // fee_payer: Signer, mut
      { pubkey: p.authority, isSigner: true, isWritable: false }, // authority: Signer
      { pubkey: p.targetProgram, isSigner: false, isWritable: false }, // target_program (executable)
      { pubkey: SystemProgram.programId, isSigner: false, isWritable: false }, // system_program
    ],
    data: Buffer.from(encodeCreateSessionV2Data(p.topUp, p.validUntil, p.lamports)),
  });
  return { ix, token };
}

/**
 * Accounts lib.rs:377-401 (RevokeSessionTokenV2). No args. `authority` must sign while the token is still
 * valid (lib.rs:404-413); pass authoritySigns=false only for an expired token.
 */
export function revokeSessionV2Ix(token: PublicKey, feePayer: PublicKey, authority: PublicKey, authoritySigns = true, programId = new PublicKey(SESSION_KEYS_PROGRAM_ID)): TransactionInstruction {
  return new TransactionInstruction({
    programId,
    keys: [
      { pubkey: token, isSigner: false, isWritable: true }, // session_token (close = fee_payer)
      { pubkey: feePayer, isSigner: false, isWritable: true }, // fee_payer: SystemAccount, mut
      { pubkey: authority, isSigner: authoritySigns, isWritable: false }, // authority: SystemAccount
      { pubkey: SystemProgram.programId, isSigner: false, isWritable: false },
    ],
    data: Buffer.from(IX_REVOKE_SESSION_V2),
  });
}

export interface SessionTokenV2 { authority: PublicKey; targetProgram: PublicKey; sessionSigner: PublicKey; feePayer: PublicKey; validUntil: bigint }

/** Account layout lib.rs:439-448: disc[8] | authority | target_program | session_signer | fee_payer | valid_until i64 */
export function decodeSessionTokenV2(data: Uint8Array): SessionTokenV2 | null {
  if (data.length < SESSION_TOKEN_V2_LEN) return null;
  for (let i = 0; i < 8; i++) if (data[i] !== ACC_SESSION_TOKEN_V2[i]) return null;
  const pk = (o: number) => new PublicKey(data.subarray(o, o + 32));
  return {
    authority: pk(8), targetProgram: pk(40), sessionSigner: pk(72), feePayer: pk(104),
    validUntil: new DataView(data.buffer, data.byteOffset, data.byteLength).getBigInt64(136, true),
  };
}

export function encodeSessionTokenV2(t: SessionTokenV2): Uint8Array {
  const out = new Uint8Array(SESSION_TOKEN_V2_LEN);
  out.set(ACC_SESSION_TOKEN_V2, 0);
  out.set(t.authority.toBytes(), 8);
  out.set(t.targetProgram.toBytes(), 40);
  out.set(t.sessionSigner.toBytes(), 72);
  out.set(t.feePayer.toBytes(), 104);
  new DataView(out.buffer).setBigInt64(136, t.validUntil, true);
  return out;
}

/** lib.rs:489-492: expired when now >= valid_until */
export function isExpired(t: SessionTokenV2, nowSec: number): boolean { return BigInt(Math.floor(nowSec)) >= t.validUntil; }
