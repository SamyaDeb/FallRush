// SessionTokenV2 encoding vs the Rust source of session-keys 3.1.1 (src/lib.rs). Values derived by hand from the source.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { Keypair, PublicKey, SystemProgram } from '@solana/web3.js';
import {
  ACC_SESSION_TOKEN_V2, createSessionV2Ix, decodeSessionTokenV2, encodeCreateSessionV2Data, encodeSessionTokenV2, IX_CREATE_SESSION_V2,
  IX_REVOKE_SESSION_V2, isExpired, revokeSessionV2Ix, SESSION_KEYS_PROGRAM_ID, SESSION_TOKEN_V2_LEN, sessionTokenV2Pda,
} from '../src/session.ts';
import { bytesToHex } from '../src/b58.ts';

const sha8 = (s: string) => createHash('sha256').update(s).digest().subarray(0, 8).toString('hex');
const PID = new PublicKey(SESSION_KEYS_PROGRAM_ID);
const target = new PublicKey('4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4');
const kp = (n: number) => Keypair.fromSeed(new Uint8Array(32).fill(n));

test('discriminators = sha256 Anchor preimages', () => {
  assert.equal(bytesToHex(IX_CREATE_SESSION_V2), sha8('global:create_session_v2'));
  assert.equal(bytesToHex(IX_REVOKE_SESSION_V2), sha8('global:revoke_session_v2'));
  assert.equal(bytesToHex(ACC_SESSION_TOKEN_V2), sha8('account:SessionTokenV2'));
  assert.equal(bytesToHex(IX_CREATE_SESSION_V2), 'dfe96c0741c2eb26');
  assert.equal(bytesToHex(IX_REVOKE_SESSION_V2), 'd33b7dbc2b9b0866');
  assert.equal(bytesToHex(ACC_SESSION_TOKEN_V2), 'b20355fe0d748029');
});

test('create_session_v2 data: disc | Option<bool> | Option<i64> | Option<u64> (borsh)', () => {
  assert.equal(bytesToHex(encodeCreateSessionV2Data(true, 1_800_003_600, 10_000_000)),
    'dfe96c0741c2eb26' + '0101' + '01' + '10e0496b00000000' + '01' + '8096980000000000');
  assert.equal(bytesToHex(encodeCreateSessionV2Data(null, null, null)), 'dfe96c0741c2eb26' + '00' + '00' + '00');
  assert.equal(bytesToHex(encodeCreateSessionV2Data(false, -1, null)), 'dfe96c0741c2eb26' + '0100' + '01' + 'ffffffffffffffff' + '00');
});

test('SessionTokenV2 PDA seeds = ["session_token_v2", target, signer, authority]', () => {
  const s = kp(1).publicKey, a = kp(2).publicKey;
  const [want] = PublicKey.findProgramAddressSync([Buffer.from('session_token_v2'), target.toBuffer(), s.toBuffer(), a.toBuffer()], PID);
  assert.ok(sessionTokenV2Pda(PID, target, s, a).equals(want));
  // V1 prefix must differ
  const [v1] = PublicKey.findProgramAddressSync([Buffer.from('session_token'), target.toBuffer(), s.toBuffer(), a.toBuffer()], PID);
  assert.ok(!v1.equals(want));
});

test('create_session_v2 accounts (CreateSessionTokenV2 order + flags)', () => {
  const s = kp(1).publicKey, a = kp(2).publicKey;
  const { ix, token } = createSessionV2Ix({ targetProgram: target, sessionSigner: s, feePayer: a, authority: a, topUp: true, validUntil: 1_800_003_600, lamports: 10_000_000 });
  assert.ok(ix.programId.equals(PID));
  const metas = ix.keys.map((k) => [k.pubkey.toBase58(), k.isSigner, k.isWritable]);
  assert.deepEqual(metas, [
    [token.toBase58(), false, true],
    [s.toBase58(), true, true],
    [a.toBase58(), true, true],
    [a.toBase58(), true, false],
    [target.toBase58(), false, false],
    [SystemProgram.programId.toBase58(), false, false],
  ]);
  assert.equal(ix.data.length, 8 + 2 + 9 + 9);
});

test('revoke_session_v2 accounts + data', () => {
  const tok = kp(3).publicKey, fp = kp(4).publicKey, au = kp(5).publicKey;
  const ix = revokeSessionV2Ix(tok, fp, au, true);
  assert.equal(bytesToHex(ix.data), 'd33b7dbc2b9b0866');
  assert.deepEqual(ix.keys.map((k) => [k.pubkey.toBase58(), k.isSigner, k.isWritable]), [
    [tok.toBase58(), false, true], [fp.toBase58(), false, true], [au.toBase58(), true, false], [SystemProgram.programId.toBase58(), false, false],
  ]);
  assert.equal(revokeSessionV2Ix(tok, fp, au, false).keys[2].isSigner, false);
});

test('SessionTokenV2 account layout: 144 B, field offsets', () => {
  const t = { authority: kp(6).publicKey, targetProgram: target, sessionSigner: kp(7).publicKey, feePayer: kp(8).publicKey, validUntil: 1_800_003_600n };
  const b = encodeSessionTokenV2(t);
  assert.equal(b.length, SESSION_TOKEN_V2_LEN);
  assert.equal(bytesToHex(b.subarray(0, 8)), 'b20355fe0d748029');
  assert.deepEqual(b.subarray(8, 40), t.authority.toBytes());
  assert.deepEqual(b.subarray(40, 72), target.toBytes());
  assert.deepEqual(b.subarray(72, 104), t.sessionSigner.toBytes());
  assert.deepEqual(b.subarray(104, 136), t.feePayer.toBytes());
  assert.equal(bytesToHex(b.subarray(136, 144)), '10e0496b00000000');
  const d = decodeSessionTokenV2(b)!;
  assert.ok(d.authority.equals(t.authority) && d.feePayer.equals(t.feePayer) && d.sessionSigner.equals(t.sessionSigner));
  assert.equal(d.validUntil, 1_800_003_600n);
  assert.equal(isExpired(d, 1_800_003_599), false);
  assert.equal(isExpired(d, 1_800_003_600), true); // now >= valid_until (lib.rs:489-492)
  const bad = b.slice(); bad[0] ^= 1;
  assert.equal(decodeSessionTokenV2(bad), null);
  assert.equal(decodeSessionTokenV2(b.subarray(0, 143)), null);
});
