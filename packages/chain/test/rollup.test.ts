import { test } from 'node:test';
import assert from 'node:assert/strict';
import { Keypair, PublicKey, SystemProgram, Transaction } from '@solana/web3.js';
import { createChain } from '../src/api.ts';
import { readDrainEntries } from '../src/wire.ts';
import { bytesToHex } from '../src/b58.ts';
import { sessionTokenV2Pda, SESSION_KEYS_PROGRAM_ID, encodeSessionTokenV2 } from '../src/session.ts';
import { isClonerError } from '../src/rollup.ts';
import { FakeClock, MockConn, memKV, worldAcc, flush } from './helpers/fake.ts';
import devnet from '../config/devnet.json' with { type: 'json' };

const PROGRAM = Keypair.fromSeed(new Uint8Array(32).fill(11)).publicKey;
const ARENA = Keypair.fromSeed(new Uint8Array(32).fill(12)).publicKey;
const WORLD = Keypair.fromSeed(new Uint8Array(32).fill(13)).publicKey;

function cfg(patch: (c: typeof devnet) => void = () => {}) {
  const c = structuredClone(devnet);
  c.addresses = { program: PROGRAM.toBase58(), arena: ARENA.toBase58(), world: WORLD.toBase58() };
  c.world.magicHex = '4652574f524c4430' as never;
  patch(c);
  return JSON.stringify(c);
}

async function setup(cfgJson = cfg()) {
  const clock = new FakeClock();
  const conns: MockConn[] = [];
  const chain = createChain({ clock, kv: memKV(), connFactory: (http) => { const m = new MockConn(http); m.clock = clock; conns.push(m); return m; } });
  const init = JSON.parse(await chain.init(cfgJson));
  assert.equal(init.ok, true, JSON.stringify(init));
  const er = () => conns.filter((c) => c.http.includes('magicblock')).pop()!;
  const base = conns.find((c) => c.http.includes('api.devnet'))!;
  return { clock, chain, conns, er, base, init };
}

const batch = (slot: number, tick: number) => new Uint8Array([slot, tick & 255, (tick >> 8) & 255, 0, 0, 3, ...new Array(15).fill(0)]);
const events = (chain: ReturnType<typeof createChain>) => JSON.parse(chain.drainEvents()) as Record<string, unknown>[];

test('submit_input built from config: program, disc, PDA metas, Vec<u8> arg, session signer; fire-and-forget', async () => {
  const { chain, er, init } = await setup();
  const b = batch(5, 300);
  const id = chain.sendInputs(b);
  assert.equal(id, 1);
  await flush();
  assert.equal(er().sentRaw.length, 1);
  const tx = Transaction.from(er().sentRaw[0]);
  assert.equal(tx.feePayer!.toBase58(), init.sessionPubkey);
  assert.ok(tx.verifySignatures());
  const ix = tx.instructions[0];
  assert.ok(ix.programId.equals(PROGRAM));
  assert.equal(bytesToHex(ix.data.subarray(0, 8)), '38b89a5bad3f158a');
  assert.equal(ix.data.readUInt32LE(8), 21);
  assert.deepEqual(new Uint8Array(ix.data.subarray(12)), b);
  const [inBuf] = PublicKey.findProgramAddressSync([Buffer.from('in'), ARENA.toBuffer(), Buffer.from([5])], PROGRAM);
  const token = sessionTokenV2Pda(new PublicKey(SESSION_KEYS_PROGRAM_ID), PROGRAM, new PublicKey(init.sessionPubkey), new PublicKey(init.burner));
  assert.deepEqual(ix.keys.map((k) => [k.pubkey.toBase58(), k.isSigner, k.isWritable]), [
    [init.sessionPubkey, true, true], [inBuf.toBase58(), false, true], [token.toBase58(), false, false],
  ]);
});

test('confirm poller: one poller, ≤ 256 sigs per call, one call in flight, latency + timeouts', async () => {
  const { chain, er, clock } = await setup();
  er().autoConfirm = false;
  er().statusDelayMs = 120; // a slow RPC: the 50 ms poller must not stack calls
  for (let i = 0; i < 300; i++) chain.sendInputs(batch(1, 1000 + i));
  await flush();
  assert.equal(er().sentRaw.length, 300);
  await clock.advance(500);
  assert.ok(er().statusCalls.length >= 2);
  assert.equal(er().maxStatusInFlight, 1);
  assert.ok(er().statusCalls.every((c) => c.length <= 256));
  assert.equal(er().statusCalls[0].length, 256);
  // the rest rotate in: all 300 get asked
  assert.equal(new Set(er().statusCalls.flat()).size, 300);
  er().autoConfirm = true;
  await clock.advance(400);
  const ev = events(chain).filter((e) => e.type === 'confirm');
  assert.equal(ev.length, 300);
  assert.ok(ev.every((e) => e.status === 'ok' && (e.ms as number) > 0));
  const st = JSON.parse(chain.stats());
  assert.equal(st.confirmOk, 300);
  assert.ok(st.confirmP95 >= st.confirmP50 && st.confirmP50 > 0);
  // a never-seen signature times out after confirmTimeoutMs
  er().autoConfirm = false;
  chain.sendInputs(batch(1, 5000));
  await clock.advance(5200);
  assert.ok(events(chain).some((e) => e.type === 'confirm' && e.status === 'timeout'));
});

test('failed transaction status → confirm err', async () => {
  const { chain, er, clock } = await setup();
  const origSend = er().sendRawTransaction.bind(er());
  er().sendRawTransaction = async (raw: Uint8Array) => { const s = await origSend(raw); er().statuses.set(s, { err: { InstructionError: [0, { Custom: 6001 }] } }); return s; };
  chain.sendInputs(batch(1, 10));
  await clock.advance(100);
  const c = events(chain).find((e) => e.type === 'confirm')!;
  assert.equal(c.status, 'err');
  assert.match(String(c.err), /6001/);
});

test('Cloner quirk: send retried until it goes through', async () => {
  assert.ok(isClonerError(new Error('Transaction failed: pending request owner 4ye… Cloner')));
  assert.ok(!isClonerError(new Error('blockhash not found')));
  const { chain, er, clock } = await setup(cfg((c) => { c.rollup.inputClonerRetryMs = 1000; }));
  er().sendError = (n) => (n < 2 ? new Error('pending request owner xyz Cloner') : null);
  chain.sendInputs(batch(1, 10));
  await clock.advance(1000);
  assert.equal(er().sendCount, 3);
  assert.equal(er().sentRaw.length, 1);
  const ev = events(chain);
  assert.ok(ev.some((e) => e.type === 'clonerRetry'));
  assert.ok(ev.some((e) => e.type === 'confirm' && e.status === 'ok'));
  // a non-cloner error is not retried
  er().sendError = () => new Error('boom');
  const before = er().sendCount;
  chain.sendInputs(batch(1, 11));
  await clock.advance(1000);
  assert.equal(er().sendCount - before, 1);
  assert.ok(events(chain).some((e) => e.type === 'error' && e.why === 'boom'));
});

test('subscribe: onAccountChange + 1 s backup poll, gap detection, poll fills, stall → resubscribe → new connection', async () => {
  const { chain, er, clock, conns } = await setup();
  er().account = worldAcc(100);
  chain.subscribe('$world');
  await flush();
  const ws = er();
  assert.equal(ws.listeners.size, 1);
  assert.ok([...ws.listeners.values()][0].pk.equals(WORLD));
  assert.equal(ws.accountReads, 1, 'immediate backup read');
  for (let t = 101; t <= 103; t++) ws.notify(worldAcc(t));
  ws.notify(worldAcc(107)); // 104..106 lost
  const ev = events(chain);
  const gap = ev.find((e) => e.type === 'gap')!;
  assert.deepEqual([gap.from, gap.to, gap.missing], [103, 107, 3]);
  // the WS goes silent while the chain advances: the 1 s poll fills in
  ws.account = worldAcc(140);
  await clock.advance(1000);
  assert.ok(events(chain).some((e) => e.type === 'pollFilled' && e.tick === 140));
  const buf = new Uint8Array(8192);
  const items = readDrainEntries(buf, chain.drainWorlds(buf, 64));
  assert.deepEqual(items.map((i) => new DataView(i.bytes.buffer).getUint32(0, true)), [100, 101, 102, 103, 107, 140]);
  // still silent past stallMs (2 s): resubscribe on the same connection
  await clock.advance(2100);
  let e2 = events(chain);
  assert.ok(e2.some((e) => e.type === 'wsReconnect' && e.how === 'resubscribe'), JSON.stringify(e2));
  assert.equal(ws.removed.length, 1);
  assert.equal(ws.listeners.size, 1);
  // keeps failing → after 3 stalls a brand-new ER Connection (fresh socket), subscription restored there
  await clock.advance(120000);
  e2 = events(chain);
  assert.ok(e2.some((e) => e.type === 'wsReconnect' && e.how === 'newConnection'));
  const fresh = er();
  assert.notEqual(fresh, ws);
  assert.equal(fresh.listeners.size, 1);
  assert.ok(conns.length >= 3);
  fresh.notify(worldAcc(5000));
  assert.equal(JSON.parse(chain.stats()).lastTick, 5000);
  // notifications from the old connection are ignored
  ws.notify(worldAcc(6000));
  assert.equal(JSON.parse(chain.stats()).lastTick, 5000);
  const st = JSON.parse(chain.stats());
  assert.ok(st.reconnects >= 3 && st.polls > 100 && st.gaps >= 1 && st.pollFills >= 1);
  chain.unsubscribe('$world');
  assert.equal(fresh.listeners.size, 0);
});

test('base-layer sends throttled ≤ 1.5 tx/s; session create/topup/revoke/status encodings', async () => {
  const { chain, base, clock, init } = await setup();
  const ps = [chain.session('create', '{}'), chain.session('topup', '{"lamports":5000}'), chain.session('topup', '{"lamports":6000}')];
  await clock.advance(50);
  assert.equal(base.sentRaw.length, 1, 'one token at t=0');
  await clock.advance(700);
  assert.equal(base.sentRaw.length, 2);
  await clock.advance(700);
  assert.equal(base.sentRaw.length, 3);
  await clock.advance(1500);
  const [c, t1] = (await Promise.all(ps)).map((s) => JSON.parse(s));
  assert.equal(c.ok, true, JSON.stringify(c));
  assert.equal(t1.ok, true);
  // create tx: create_session_v2 by the burner for the session key
  const tx = Transaction.from(base.sentRaw[0]);
  assert.ok(tx.verifySignatures());
  assert.equal(tx.feePayer!.toBase58(), init.burner);
  const ix = tx.instructions[0];
  assert.equal(ix.programId.toBase58(), SESSION_KEYS_PROGRAM_ID);
  assert.equal(bytesToHex(ix.data.subarray(0, 11)), 'dfe96c0741c2eb26' + '0101' + '01');
  assert.equal(bytesToHex(ix.data.subarray(19)), '01' + '8096980000000000');
  const validUntil = Number(ix.data.readBigInt64LE(11));
  assert.equal(validUntil, Math.floor((clock.wall + 1000) / 1000) + 3600);
  assert.equal(ix.keys[1].pubkey.toBase58(), c.sessionPubkey);
  assert.ok(ix.keys[4].pubkey.equals(PROGRAM));
  assert.equal(c.token, ix.keys[0].pubkey.toBase58());
  // topup = system transfer burner → session
  const tt = Transaction.from(base.sentRaw[1]).instructions[0];
  assert.ok(tt.programId.equals(SystemProgram.programId));
  assert.equal(tt.data.readBigUInt64LE(4), 5000n);
  // status decodes the on-chain SessionTokenV2
  base.account = encodeSessionTokenV2({ authority: new PublicKey(init.burner), targetProgram: PROGRAM, sessionSigner: new PublicKey(c.sessionPubkey), feePayer: new PublicKey(init.burner), validUntil: BigInt(validUntil) });
  const s = JSON.parse(await chain.session('status'));
  assert.equal(s.state, 'active');
  assert.equal(s.onChain.exists, true);
  assert.equal(s.onChain.validUntil, validUntil);
  assert.equal(s.onChain.sessionSigner, c.sessionPubkey);
  // revoke: authority signs while active; then a fresh session key
  const rp = chain.session('revoke');
  await clock.advance(3000);
  const r = JSON.parse(await rp);
  assert.equal(r.ok, true);
  const rt = Transaction.from(base.sentRaw[3]);
  assert.equal(bytesToHex(rt.instructions[0].data), 'd33b7dbc2b9b0866');
  assert.equal(rt.instructions[0].keys[2].isSigner, true);
  assert.notEqual(JSON.parse(chain.wallet()).sessionPubkey, c.sessionPubkey);
  const sev = events(chain).filter((e) => e.type === 'session').map((e) => e.state);
  assert.deepEqual(sev, ['creating', 'active', 'toppedUp', 'toppedUp', 'revoked']);
});

test('join: config-driven slot table + World core version', async () => {
  const slotAcc = Keypair.fromSeed(new Uint8Array(32).fill(14)).publicKey;
  const { chain, er, init } = await setup(cfg((c) => {
    (c.addresses as Record<string, string>).lobby = slotAcc.toBase58();
    c.rollup.join = { ix: null, layer: 'er', slotTable: { account: '$lobby', offset: 8, stride: 40, count: 12, keyOffset: 0, key: 'authority' } as never, map: 'GooTower', rate: 30, confirmTimeoutMs: 15000 };
  }));
  const table = new Uint8Array(8 + 12 * 40);
  table.set(new PublicKey(init.burner).toBytes(), 8 + 7 * 40);
  table.set(Keypair.generate().publicKey.toBytes(), 8 + 2 * 40);
  er().getAccountInfo = async (pk: PublicKey) => (pk.equals(slotAcc) ? { data: table } : { data: worldAcc(3, { core: 77 }) });
  const j = JSON.parse(await chain.join('{"name":"sam"}'));
  assert.deepEqual([j.ok, j.slot, j.map, j.rate, j.coreVersion, j.world], [true, 7, 'GooTower', 30, 77, WORLD.toBase58()]);
  assert.equal(j.roster.filter((r: { key: string | null }) => r.key).length, 2);
});

test('RTT probe + stats shape', async () => {
  const { chain, clock } = await setup();
  await clock.advance(4000);
  const st = JSON.parse(chain.stats());
  for (const k of ['updatesPerSec', 'inputBatchesPerSec', 'confirmP50', 'confirmP95', 'rttMs', 'gaps', 'polls', 'reconnects', 'lastTick']) assert.ok(k in st, k);
  assert.equal(st.transport, 'rollup');
});
