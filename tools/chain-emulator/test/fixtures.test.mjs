// The WorldAcc fixtures (the C# decoder test reuses them): regenerating gives the same bytes, and decoding the
// committed bytes gives the committed expected.json. Also checks layout = rcx_layout.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { decodeWorldAcc } from '../src/world-view.mjs';
import { Core } from '../src/core.mjs';
import { makeFixtures, toJson } from './helpers.mjs';

const dir = join(dirname(fileURLToPath(import.meta.url)), 'fixtures');
const expected = JSON.parse(readFileSync(join(dir, 'expected.json'), 'utf8'));

test('layout in expected.json = rcx_layout', () => {
  const c = new Core();
  assert.deepEqual(c.layout(), expected.layout);
});

test('decoding the fixture files gives expected.json', () => {
  for (const [k, s] of Object.entries(expected.samples)) {
    const b = new Uint8Array(readFileSync(join(dir, s.file)));
    assert.equal(b.length, s.bytes);
    assert.deepEqual(JSON.parse(toJson(decodeWorldAcc(b, expected.layout))), s.decoded, k);
  }
  const w = expected.samples.waiting.decoded, r = expected.samples.rush.decoded, e = expected.samples.ended.decoded;
  assert.equal(w.world.round.phase, 5, 'waiting = free play (SANDBOX phase)');
  assert.equal(r.world.round.phase, 3, 'RUSH');
  assert.equal(e.world.round.phase, 4, 'ENDED');
  assert.equal(e.flags, 3);
  assert.equal(e.world.round.result.n, 3);
});

test('regenerating the fixtures is deterministic', () => {
  const f = makeFixtures();
  for (const k of ['waiting', 'rush', 'ended']) assert.deepEqual(Buffer.from(f[k]), readFileSync(join(dir, `${k}.worldacc`)), k);
  f.arena.close();
});
