import { test } from 'node:test';
import assert from 'node:assert/strict';
import { Keypair } from '@solana/web3.js';
import { Wallet } from '../src/wallet.ts';
import { b58decode, b58encode } from '../src/b58.ts';
import { memKV } from './helpers/fake.ts';

test('burner: created on first run, persisted, exported', () => {
  const kv = memKV();
  const w1 = new Wallet(kv, 'fallrush.burner.v1', 'fallrush.session.v1', () => 1);
  assert.equal(w1.burnerCreated, true);
  assert.ok(kv.getItem('fallrush.burner.v1'));
  const w2 = new Wallet(kv, 'fallrush.burner.v1', 'fallrush.session.v1', () => 2);
  assert.equal(w2.burnerCreated, false);
  assert.ok(w2.burner.publicKey.equals(w1.burner.publicKey));
  assert.ok(w2.session.publicKey.equals(w1.session.publicKey), 'session key persisted too');
  const exp = w2.exportBurner();
  assert.ok(Keypair.fromSecretKey(b58decode(exp)).publicKey.equals(w1.burner.publicKey));
  // nothing stored in plaintext besides the two keys
  assert.deepEqual([...kv.m.keys()].sort(), ['fallrush.burner.v1', 'fallrush.session.v1']);
});

test('corrupt storage → fresh keys; rotate + import', () => {
  const kv = memKV();
  kv.setItem('fallrush.burner.v1', 'not base58 !!');
  kv.setItem('fallrush.session.v1', '{bad json');
  const w = new Wallet(kv, 'fallrush.burner.v1', 'fallrush.session.v1', () => 1);
  assert.equal(w.burnerCreated, true);
  const s0 = w.session.publicKey;
  w.saveSessionMeta({ token: 'x', validUntil: 5 });
  assert.equal(JSON.parse(kv.getItem('fallrush.session.v1')!).validUntil, 5);
  w.rotateSession(3);
  assert.ok(!w.session.publicKey.equals(s0));
  assert.equal(w.sessionMeta.token, null);
  const k = Keypair.generate();
  w.importBurner(b58encode(k.secretKey));
  assert.ok(w.burner.publicKey.equals(k.publicKey));
  const again = new Wallet(kv, 'fallrush.burner.v1', 'fallrush.session.v1', () => 1);
  assert.ok(again.burner.publicKey.equals(k.publicKey));
});
