// Writes test/fixtures/{waiting,rush,ended}.worldacc + expected.json (decoded fields; the C# decoder test reuses them).
//   node tools/chain-emulator/test/make-fixtures.mjs
import { writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { decodeWorldAcc } from '../src/world-view.mjs';
import { makeFixtures, toJson, FIXTURE_SEED } from './helpers.mjs';

const dir = join(dirname(fileURLToPath(import.meta.url)), 'fixtures');
mkdirSync(dir, { recursive: true });
const f = makeFixtures();
const layout = f.arena.layout;
const expected = {
  note: 'WorldAcc v0 samples of a seeded 3-player Lid Flip Final (slot 0 human with scripted inputs, 1-2 bots). ' +
    'Files are the WorldAcc bytes (wire-v0 §3) without the 5-byte 0x90 frame header. Regenerate: node tools/chain-emulator/test/make-fixtures.mjs',
  seed: '0x' + FIXTURE_SEED.toString(16), layout, samples: {},
};
for (const k of ['waiting', 'rush', 'ended']) {
  writeFileSync(join(dir, `${k}.worldacc`), f[k]);
  expected.samples[k] = { file: `${k}.worldacc`, bytes: f[k].length, decoded: decodeWorldAcc(f[k], layout) };
}
writeFileSync(join(dir, 'expected.json'), toJson(expected) + '\n');
const e = expected.samples.ended.decoded.world.round;
console.log(`fixtures: waiting tick ${expected.samples.waiting.decoded.tick}, rush tick ${expected.samples.rush.decoded.tick}, ended tick ${expected.samples.ended.decoded.tick} (champion ${e.result.order[0]})`);
f.arena.close();
