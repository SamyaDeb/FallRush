// Optional: against the real tools/chain-emulator (skipped when it is absent). Real sockets, real clock, localhost only.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { resolve, dirname } from 'node:path';
import { createChain } from '../src/api.ts';
import { encodeInputBatch, readDrainEntries } from '../src/wire.ts';
import { memKV } from './helpers/fake.ts';
import emuCfg from '../config/emulator.json' with { type: 'json' };

const main = resolve(dirname(fileURLToPath(import.meta.url)), '../../../tools/chain-emulator/src/main.mjs');
const skip = !existsSync(main) ? 'tools/chain-emulator not present' : typeof WebSocket === 'undefined' ? 'no global WebSocket' : false;

test('emulator integration: join, World stream, inputs confirmed, RTT', { skip, timeout: 30000 }, async (t) => {
  const proc = spawn(process.execPath, [main, '--port', '0', '--no-shaping', '--confirm', '20', '--players', '3', '--humans', '1', '--autostart', '1', '--map', 'Lobby'], { stdio: ['ignore', 'pipe', 'pipe'] });
  try {
    const port = await new Promise<number>((res, rej) => {
      let out = '';
      const t = setTimeout(() => rej(new Error('emulator did not start: ' + out)), 10000);
      proc.stdout.on('data', (d) => { out += d; const m = out.match(/ws:\/\/[\d.]+:(\d+)\//); if (m) { clearTimeout(t); res(Number(m[1])); } });
      proc.stderr.on('data', (d) => { out += d; });
      proc.on('exit', (c) => rej(new Error('emulator exited ' + c + ': ' + out)));
    });
    const chain = createChain({ kv: memKV() });
    t.after(() => chain.stop());
    const cfg = structuredClone(emuCfg);
    cfg.emulator.url = `ws://127.0.0.1:${port}/`;
    const init = JSON.parse(await chain.init(JSON.stringify(cfg)));
    assert.equal(init.ok, true, JSON.stringify(init));
    const j = JSON.parse(await chain.join(JSON.stringify({ name: 'it' })));
    assert.equal(j.ok, true, JSON.stringify(j));
    assert.ok(j.slot >= 0 && j.slot < 12);
    assert.equal(j.rate, 30);
    chain.subscribe('$world');
    await new Promise((r) => setTimeout(r, 1500));
    const buf = new Uint8Array(1 << 20);
    const items = readDrainEntries(buf, chain.drainWorlds(buf, 64));
    assert.ok(items.length >= 20, 'worlds received: ' + items.length);
    const tick = chain.latestTick();
    for (let k = 0; k < 5; k++) {
      const T = chain.latestTick();
      chain.sendInputs(encodeInputBatch(j.slot, T + 3, [0, 1, 2].map(() => ({ moveX: 50, moveY: 0, buttons: 0, camYaw: 0 }))));
      await new Promise((r) => setTimeout(r, 100));
    }
    await new Promise((r) => setTimeout(r, 1200));
    const ev = JSON.parse(chain.drainEvents());
    const confirms = ev.filter((e: { type: string }) => e.type === 'confirm');
    assert.equal(confirms.length, 5, JSON.stringify(ev.slice(-10)));
    assert.ok(confirms.filter((c: { status: string }) => c.status === 'ok').length >= 4, JSON.stringify(confirms));
    const st = JSON.parse(chain.stats());
    assert.ok(st.updatesPerSec > 20, 'updates/s ' + st.updatesPerSec);
    assert.ok(st.rttMs >= 0);
    assert.ok(st.lastTick > tick);
    console.log('[it] confirms', confirms.map((c: { status: string; ms: number }) => c.status + '/' + c.ms).join(' '), 'stats', JSON.stringify(st));
  } finally {
    proc.kill('SIGTERM');
  }
});
