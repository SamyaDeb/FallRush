// P2 program: config values, World parsing, input conversion, synthetic finalists, the join flow (mock rollup).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { Keypair, PublicKey, Transaction } from '@solana/web3.js';
import { createChain } from '../src/api.ts';
import { parseWorldAccount } from '../src/wire.ts';
import { syntheticFinalists, toP2Batch, pickSlot, customCode } from '../src/p2.ts';
import { encodeInputBatch } from '../src/wire.ts';
import { bytesToHex } from '../src/b58.ts';
import { sessionTokenV2Pda, SESSION_KEYS_PROGRAM_ID } from '../src/session.ts';
import { FakeClock, MockConn, memKV, flush } from './helpers/fake.ts';
import { EventLog, Stats, WorldIntake } from '../src/intake.ts';
import devnet from '../config/devnet.json' with { type: 'json' };
import local from '../config/local.json' with { type: 'json' };

const PROGRAM = new PublicKey('4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4');
const pda = (seeds: (Buffer | Uint8Array)[]) => PublicKey.findProgramAddressSync(seeds, PROGRAM)[0];
const sha8 = (s: string) => createHash('sha256').update(s).digest().subarray(0, 8).toString('hex');

test('config: discriminators = Anchor sha256 preimages; devnet arena 2 PDA; local endpoints', () => {
  assert.equal(devnet.rollup.submitInput.discriminator, sha8('global:submit_input'));
  assert.equal(devnet.rollup.p2.joinLobby.discriminator, sha8('global:join_lobby'));
  assert.equal(devnet.world.magicHex, sha8('account:WorldAcc'));
  assert.equal(pda([Buffer.from('arena'), Buffer.from([2])]).toBase58(), 'HbinPthhN3g5BRQRL4aKQ75HyQXMofhsUbg2c28bJXHt');
  assert.equal(devnet.addresses.arena, 'HbinPthhN3g5BRQRL4aKQ75HyQXMofhsUbg2c28bJXHt');
  assert.deepEqual([local.rollup.baseHttp, local.rollup.erHttp, local.rollup.erWs], ['http://localhost:8899', 'http://localhost:7799', 'ws://localhost:7800']);
  assert.deepEqual([devnet.rollup.baseHttp, devnet.rollup.erHttp, devnet.rollup.erWs], ['https://api.devnet.solana.com', 'https://devnet-as.magicblock.app', 'wss://devnet-as.magicblock.app']);
});

// ---- account builders (sim.rs / IDL layouts, with the 8-byte discriminator) -------------------------------------
function worldAccP2(tick: number, o: { part?: number; phase?: number; len?: number; applied?: number[] } = {}) {
  const b = new Uint8Array(2256);
  const dv = new DataView(b.buffer);
  b.set(Buffer.from(sha8('account:WorldAcc'), 'hex'), 0);
  (o.applied ?? []).forEach((t, i) => dv.setUint32(80 + 4 * i, t, true));
  dv.setUint32(128, tick, true);
  const len = o.len ?? 2037;
  dv.setUint32(132, len, true);
  b[136] = o.part ?? 0;
  b[138] = o.phase ?? 5;
  for (let i = 0; i < len; i++) b[208 + i] = (i + tick) & 255;
  return b;
}
function arenaAcc(o: { match?: PublicKey; status: number; humanMask?: number; wallets?: (PublicKey | null)[] }) {
  const b = new Uint8Array(1432);
  if (o.match) b.set(o.match.toBytes(), 40);
  (o.wallets ?? []).forEach((w, i) => { if (w) b.set(w.toBytes(), 616 + 32 * i); });
  b[1409] = o.status;
  new DataView(b.buffer).setUint16(1412, o.humanMask ?? 0, true);
  return b;
}
function matchAcc(id: Uint8Array, o: { players?: number; targets?: number[]; alive?: number[]; round?: number; status?: number } = {}) {
  const b = new Uint8Array(400);
  b.set(id, 10);
  b[26] = o.status ?? 2;
  b[128] = o.players ?? 12;
  b[129] = o.round ?? 0;
  b.set(o.targets ?? [8, 5, 3, 1], 130);
  for (const s of o.alive ?? []) b[214 + 4 * s] = 1;
  return b;
}

test('WorldAcc (P2): only complete ticks; u32 len at 132; World at 208; applied ticks; flags from phase; restart epoch', () => {
  const L = devnet.world as never;
  const p = parseWorldAccount(worldAccP2(77, { applied: [70, 0, 75], phase: 11 }), L)!;
  assert.equal(p.complete, true);
  const dv = new DataView(p.view.buffer);
  assert.deepEqual([dv.getUint32(0, true), dv.getUint32(4, true), dv.getUint16(12, true), dv.getUint16(14, true), dv.getUint32(16, true), dv.getUint32(24, true)], [77, 0, 3, 2037, 70, 75]);
  assert.equal(p.view[64], 77 & 255);
  assert.equal(parseWorldAccount(worldAccP2(78, { part: 3 }), L)!.complete, false);
  assert.equal(new DataView(parseWorldAccount(worldAccP2(78, { phase: 4 }), L)!.view.buffer).getUint16(12, true), 1);
});

test('input conversion: wire-v0 5-B commands → submit_input batch[12] (cam_yaw >> 8); short batch repeats the last', () => {
  const b = encodeInputBatch(4, 1000, [{ moveX: 10, moveY: -3, buttons: 3, camYaw: 0x1234 }, { moveX: -1, moveY: 0, buttons: 0, camYaw: 0xff00 }, { moveX: 0, moveY: 127, buttons: 8, camYaw: 0x00ff }]);
  const p = toP2Batch(b);
  assert.deepEqual([p.slot, p.start], [4, 1000]);
  assert.equal(bytesToHex(p.batch12), '0afd0312' + 'ff0000ff' + '007f0800');
  assert.equal(bytesToHex(toP2Batch(encodeInputBatch(1, 5, [{ moveX: 1, moveY: 2, buttons: 1, camYaw: 0x0500 }])).batch12), '01020105'.repeat(3));
  assert.equal(customCode({ InstructionError: [0, { Custom: 6023 }] }), 6023);
  assert.equal(customCode('Program failed: custom program error: 0x1787'), 6023);
});

test('synthetic finalists = tools/keeper/run.ts recordRounds13', async () => {
  // independent re-implementation, as the keeper: sha256(id ‖ [r, s]).readUInt32LE(0) ascending, keep targets[r-1]
  const keeper = (id: Buffer, players: number, targets: number[]) => {
    const sha = (r: number, s: number) => createHash('sha256').update(Buffer.concat([id, Buffer.from([r, s])])).digest().readUInt32LE(0);
    let alive = Array.from({ length: players }, (_, i) => i);
    for (let r = 1; r <= 3; r++) alive = [...alive].sort((a, b) => sha(r, a) - sha(r, b)).slice(0, targets[r - 1]);
    return alive.sort((a, b) => a - b);
  };
  for (let k = 0; k < 20; k++) {
    const id = createHash('sha256').update('m' + k).digest().subarray(0, 16);
    const want = keeper(Buffer.from(id), 12, [8, 5, 3, 1]);
    assert.deepEqual(await syntheticFinalists(new Uint8Array(id), 12, [8, 5, 3, 1]), want);
    assert.equal(want.length, 3);
  }
  assert.equal(pickSlot([2, 7, 9], 0b100), 7);
  assert.equal(pickSlot([2, 7, 9], 0b1010000100), null);
});

// ---- the join flow against a scripted rollup ------------------------------------------------------------------
interface Sim { arena: Uint8Array; match: Uint8Array | null; tokenOnBase: boolean; joins: Transaction[]; inputs: Transaction[]; base: Transaction[] }

async function setup(patch: (c: typeof devnet) => void = () => {}) {
  const clock = new FakeClock();
  const cfg = structuredClone(devnet);
  patch(cfg);
  const arenaPk = new PublicKey(cfg.addresses.arena);
  const sim: Sim = { arena: arenaAcc({ status: 0 }), match: null, tokenOnBase: false, joins: [], inputs: [], base: [] };
  let matchPk: PublicKey | null = null;
  const conns: MockConn[] = [];
  const chain = createChain({ clock, kv: memKV(), connFactory: (http) => {
    const m = new MockConn(http); m.clock = clock; conns.push(m);
    const isEr = http.includes('magicblock');
    const orig = m.sendRawTransaction.bind(m);
    m.sendRawTransaction = async (raw: Uint8Array) => {
      const sig = await orig(raw);
      const tx = Transaction.from(raw);
      const ix = tx.instructions[tx.instructions.length - 1];
      const disc = bytesToHex(ix.data.subarray(0, 8));
      if (!isEr) { sim.base.push(tx); if (ix.programId.toBase58() === SESSION_KEYS_PROGRAM_ID) sim.tokenOnBase = true; }
      else if (disc === cfg.rollup.p2.joinLobby.discriminator) {
        sim.joins.push(tx);
        const slot = ix.data[8];
        const dv = new DataView(sim.arena.buffer);
        const mask = dv.getUint16(1412, true);
        if ((mask >> slot) & 1) m.statuses.set(sig, { err: { InstructionError: [0, { Custom: 6023 }] } });
        else { dv.setUint16(1412, mask | (1 << slot), true); sim.arena.set(ix.data.subarray(10, 42), 616 + 32 * slot); }
      } else if (disc === cfg.rollup.submitInput.discriminator) sim.inputs.push(tx);
      return sig;
    };
    m.getAccountInfo = async (pk: PublicKey) => {
      if (pk.equals(arenaPk)) return { data: sim.arena };
      if (matchPk && pk.equals(matchPk) && sim.match) return { data: sim.match };
      if (!isEr && sim.tokenOnBase && pk.toBase58() !== arenaPk.toBase58()) return null;
      return null;
    };
    return m;
  } });
  const init = JSON.parse(await chain.init(JSON.stringify(cfg)));
  assert.equal(init.ok, true, JSON.stringify(init));
  return { clock, chain, sim, cfg, init, arenaPk, setMatch: (pk: PublicKey) => { matchPk = pk; } };
}

async function until<T>(clock: FakeClock, p: Promise<T>, stepMs = 100, maxMs = 120000): Promise<T> {
  let done = false, v: T;
  p.then((x) => { done = true; v = x; });
  for (let t = 0; !done && t < maxMs; t += stepMs) await clock.advance(stepMs);
  assert.ok(done, 'promise did not settle');
  return v!;
}
const evs = (chain: ReturnType<typeof createChain>) => JSON.parse(chain.drainEvents()) as Record<string, unknown>[];

test('join (no slot given): session token → wait lobby → lowest free synthetic finalist → lobbyJoined → started; submit_input', async () => {
  const { clock, chain, sim, init, arenaPk, setMatch } = await setup();
  const id = createHash('sha256').update('match-A').digest().subarray(0, 16);
  const matchPk = pda([Buffer.from('match'), id]);
  setMatch(matchPk);
  const fin = await syntheticFinalists(new Uint8Array(id), 12, [8, 5, 3, 1]);
  const jp = chain.join(JSON.stringify({ name: 'sam' }));
  await clock.advance(3000); // session create on base (throttled) + idle arena
  assert.equal(sim.base.length, 1, 'create_session_v2 sent first');
  // open_lobby: another human already holds the lowest finalist
  sim.match = matchAcc(new Uint8Array(id));
  sim.arena = arenaAcc({ match: matchPk, status: 1, humanMask: 1 << fin[0], wallets: Object.assign(new Array(12).fill(null), { [fin[0]]: Keypair.generate().publicKey }) });
  await clock.advance(2000);
  assert.equal(sim.joins.length, 1);
  const jtx = sim.joins[0];
  const jix = jtx.instructions[0];
  const sess = new PublicKey(JSON.parse(chain.wallet()).sessionPubkey);
  const token = sessionTokenV2Pda(new PublicKey(SESSION_KEYS_PROGRAM_ID), PROGRAM, sess, new PublicKey(init.burner));
  assert.equal(jtx.feePayer!.toBase58(), sess.toBase58());
  assert.deepEqual(jix.keys.map((k) => [k.pubkey.toBase58(), k.isSigner, k.isWritable]), [
    [sess.toBase58(), true, true], [arenaPk.toBase58(), false, true], [pda([Buffer.from('in'), arenaPk.toBuffer(), Buffer.from([fin[1]])]).toBase58(), false, true], [token.toBase58(), false, false],
  ]);
  assert.equal(bytesToHex(jix.data), '7f6677bed7dfd49f' + fin[1].toString(16).padStart(2, '0') + '00' + bytesToHex(new PublicKey(init.burner).toBytes()));
  let e = evs(chain).filter((x) => x.type === 'join').map((x) => x.stage);
  assert.deepEqual(e, ['session', 'waitingLobby', 'lobbyOpen', 'finalists', 'lobbyJoined']);
  // start_final: Match slots alive = the finalists, status RUNNING
  sim.match = matchAcc(new Uint8Array(id), { alive: fin, round: 3, status: 4 });
  new Uint8Array(sim.arena.buffer)[1409] = 2;
  const r = JSON.parse(await until(clock, jp));
  assert.equal(r.ok, true, JSON.stringify(r));
  assert.equal(r.slot, fin[1]);
  assert.equal(r.match, matchPk.toBase58());
  assert.deepEqual(r.roster.map((x: { slot: number; kind: string }) => [x.slot, x.kind]), [[fin[0], 'human'], [fin[1], 'human'], [fin[2], 'bot']]);
  assert.equal(r.coreVersion, 0);
  assert.deepEqual(evs(chain).filter((x) => x.type === 'join').map((x) => x.stage), ['started']);
  // inputs: session signer + its token
  chain.sendInputs(encodeInputBatch(r.slot, 500, [0, 1, 2].map(() => ({ moveX: 5, moveY: 6, buttons: 2, camYaw: 0x4000 }))));
  await flush();
  const itx = sim.inputs[0];
  const iix = itx.instructions[0];
  assert.deepEqual(iix.keys.map((k) => [k.pubkey.toBase58(), k.isSigner, k.isWritable]), [
    [sess.toBase58(), true, true], [pda([Buffer.from('in'), arenaPk.toBuffer(), Buffer.from([r.slot])]).toBase58(), false, true],
    [pda([Buffer.from('world'), arenaPk.toBuffer()]).toBase58(), false, false], [token.toBase58(), false, false],
  ]);
  assert.equal(bytesToHex(iix.data), '38b89a5bad3f158a' + r.slot.toString(16).padStart(2, '0') + 'f4010000' + '05060240'.repeat(3));
  const st = JSON.parse(chain.stats());
  assert.equal(st.receipt.match, matchPk.toBase58());
  assert.ok(st.receipt.joinSig && st.receipt.lastInputSig && st.receipt.sessionSig);
  // a second join returns the same result without new transactions
  assert.equal(JSON.parse(await chain.join('{}')).slot, r.slot);
  assert.equal(sim.joins.length, 1);
});

test('hybrid join (match + slot): waits for that match, joins the Classic slot, spectates if not a finalist', async () => {
  const { clock, chain, sim, arenaPk, setMatch } = await setup((c) => { c.rollup.p2.useSession = false; });
  const id = Buffer.from('00112233445566778899aabbccddeeff', 'hex');
  const matchPk = pda([Buffer.from('match'), id]);
  setMatch(matchPk);
  const other = pda([Buffer.from('match'), Buffer.alloc(16, 9)]);
  sim.arena = arenaAcc({ match: other, status: 1 }); // a lobby, but of another match
  const jp = chain.join(JSON.stringify({ name: 'sam', match: id.toString('hex'), slot: 5 }));
  await clock.advance(5000);
  assert.equal(sim.joins.length, 0, 'not the match we were told');
  sim.arena = arenaAcc({ match: matchPk, status: 1 });
  sim.match = matchAcc(new Uint8Array(id));
  await clock.advance(1000);
  assert.equal(sim.joins.length, 1);
  const jix = sim.joins[0].instructions[0];
  const burner = new PublicKey(JSON.parse(chain.wallet()).burner);
  assert.equal(jix.data[8], 5);
  assert.ok(jix.keys[0].pubkey.equals(burner), 'burner signs without a session');
  assert.ok(jix.keys[3].pubkey.equals(PROGRAM), 'no session token: Anchor None = program id');
  assert.ok(evs(chain).some((x) => x.stage === 'lobbyJoined' && x.slot === 5));
  sim.match = matchAcc(new Uint8Array(id), { alive: [1, 2, 9], round: 3, status: 4 });
  new Uint8Array(sim.arena.buffer)[1409] = 2;
  const r = JSON.parse(await until(clock, jp));
  assert.equal(r.ok, true);
  assert.equal(r.slot, 255, 'slot 5 is not a finalist → spectate');
  assert.deepEqual(r.roster.map((x: { slot: number }) => x.slot), [1, 2, 9]);
  assert.ok(evs(chain).some((x) => x.stage === 'notFinalist'));
  void arenaPk;
});

test('World intake with the P2 layout: slices dropped, complete ticks queued, new match = new epoch', () => {
  const clock = new FakeClock();
  const events = new EventLog(64, clock);
  const stats = new Stats();
  const intake = new WorldIntake(devnet.world as never, 64, clock, events, stats);
  assert.equal(intake.ingest(worldAccP2(100), 'ws'), true);
  assert.equal(intake.ingest(worldAccP2(101, { part: 2 }), 'ws'), false);
  assert.equal(intake.ingest(worldAccP2(101), 'ws'), true);
  assert.equal(stats.slicesDropped, 1);
  assert.equal(stats.gaps, 0);
  assert.equal(intake.ingest(worldAccP2(3), 'ws'), true, 'next match restarts the tick');
  assert.ok(events.peek().some((e) => e.type === 'arenaChanged'));
  assert.equal(new DataView(intake.latest!.bytes.buffer).getUint32(8, true), 2);
});
