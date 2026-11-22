// Smoke test of the built IIFE (pnpm build) in a bare VM context with browser-like globals only.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { resolve, dirname } from 'node:path';
import vm from 'node:vm';
import { MockWS, memKV, worldAcc, worldFrame, flush } from './helpers/fake.ts';
import emuCfg from '../config/emulator.json' with { type: 'json' };

const out = resolve(dirname(fileURLToPath(import.meta.url)), '../../../client-unity/Assets/StreamingAssets/fallrush-chain.js');

test('bundle sets globalThis.FallRushChain and runs the emulator path', { skip: existsSync(out) ? false : 'not built' }, async (t) => {
  MockWS.reset();
  const kv = memKV();
  const sandbox: Record<string, unknown> = {
    TextEncoder, TextDecoder, crypto: globalThis.crypto, atob, btoa, performance, URL, console,
    setTimeout, clearTimeout, setInterval, clearInterval, queueMicrotask, localStorage: kv, WebSocket: MockWS,
  };
  sandbox.window = sandbox; sandbox.self = sandbox;
  const ctx = vm.createContext(sandbox);
  vm.runInContext(readFileSync(out, 'utf8'), ctx);
  const C = (ctx as { FallRushChain: Record<string, (...a: unknown[]) => unknown> }).FallRushChain;
  assert.ok(C, 'FallRushChain global');
  t.after(() => C.stop());
  for (const f of ['init', 'wallet', 'exportBurner', 'phantomConnect', 'phantomSignMessage', 'session', 'join', 'sendInputs', 'subscribe', 'unsubscribe', 'drainWorlds', 'latestWorldBase64', 'drainEvents', 'stats']) assert.equal(typeof C[f], 'function', f);
  const init = JSON.parse(await (C.init(JSON.stringify(emuCfg)) as Promise<string>));
  assert.equal(init.ok, true, JSON.stringify(init));
  assert.equal(kv.getItem('fallrush.burner.v1'), C.exportBurner());
  C.subscribe('$world');
  MockWS.last.bin(worldFrame(0x90, 1, worldAcc(7)));
  const buf = new Uint8Array(1024);
  assert.equal(C.drainWorlds(buf, 8), 16 + 64 + 40);
  assert.equal(C.sendInputs(new Uint8Array([0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0])), 1);
  assert.equal(MockWS.last.bins()[0][0], 0x02);
  await flush();
});
