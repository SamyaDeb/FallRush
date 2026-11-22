import { test } from 'node:test';
import assert from 'node:assert/strict';
import { decodeInputBatch, encodeInputBatch, encodeSubmitFrame, parseWorldAccount, readDrainEntries, validateInputBatch, writeDrainEntry } from '../src/wire.ts';
import { b58decode, b58encode, bytesToHex } from '../src/b58.ts';
import { Keypair } from '@solana/web3.js';
import { worldAcc } from './helpers/fake.ts';
import cfg from '../config/emulator.json' with { type: 'json' };

test('InputBatch encoding (wire-v0 §2) byte-for-byte', () => {
  const b = encodeInputBatch(3, 123456, [
    { moveX: 127, moveY: -128, buttons: 1 | 2, camYaw: 300 },
    { moveX: 0, moveY: -1, buttons: 4, camYaw: 65535 },
    { moveX: -5, moveY: 5, buttons: 8, camYaw: 0 },
  ]);
  assert.equal(b.length, 21);
  assert.equal(bytesToHex(b), '03' + '40e20100' + '03' + '7f80' + '03' + '2c01' + '00ff' + '04' + 'ffff' + 'fb05' + '08' + '0000');
  const d = decodeInputBatch(b);
  assert.equal(d.slot, 3); assert.equal(d.firstTick, 123456); assert.equal(d.cmds[1].camYaw, 65535); assert.equal(d.cmds[0].moveY, -128);
  assert.equal(validateInputBatch(b.subarray(0, 20)), 'InputBatch length mismatch');
  assert.equal(validateInputBatch(new Uint8Array([0, 0, 0, 0, 0, 0])), 'InputBatch count must be 1..3');
  assert.equal(bytesToHex(encodeSubmitFrame(7, b).subarray(0, 5)), '0207000000');
});

test('WorldAcc → WorldView with config offsets', () => {
  const acc = worldAcc(500, { core: 65537, seq: 4, flags: 3, applied: [498, 0, 497] });
  const p = parseWorldAccount(acc, cfg.world)!;
  assert.equal(p.tick, 500);
  const dv = new DataView(p.view.buffer);
  assert.equal(dv.getUint32(0, true), 500);
  assert.equal(dv.getUint32(4, true), 65537);
  assert.equal(dv.getUint32(8, true), 4);
  assert.equal(dv.getUint16(12, true), 3);
  assert.equal(dv.getUint16(14, true), 40);
  assert.equal(dv.getUint32(16, true), 498);
  assert.equal(dv.getUint32(24, true), 497);
  assert.deepEqual(p.view.subarray(64), acc.subarray(72));
  const wrong = acc.slice(); wrong[0] = 0;
  assert.equal(parseWorldAccount(wrong, cfg.world), null); // magic
  assert.equal(parseWorldAccount(acc.subarray(0, 100), cfg.world), null); // truncated
});

test('drain framing round-trip and buffer-full stop', () => {
  const buf = new Uint8Array(64);
  let at = writeDrainEntry(buf, 0, 12.5, 9, new Uint8Array([1, 2, 3]));
  assert.equal(at, 19);
  at = writeDrainEntry(buf, at, 99.25, 10, new Uint8Array([4]));
  assert.equal(writeDrainEntry(buf, at, 1, 1, new Uint8Array(40)), -1);
  const e = readDrainEntries(buf, at);
  assert.deepEqual(e.map((x) => [x.recvMs, x.seq, [...x.bytes]]), [[12.5, 9, [1, 2, 3]], [99.25, 10, [4]]]);
});

test('base58 matches web3.js', () => {
  const k = Keypair.generate();
  assert.equal(b58encode(k.publicKey.toBytes()), k.publicKey.toBase58());
  assert.deepEqual(b58decode(b58encode(k.secretKey)), k.secretKey);
  assert.equal(b58encode(new Uint8Array([0, 0, 1])), '112');
});
