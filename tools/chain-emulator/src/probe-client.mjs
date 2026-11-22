#!/usr/bin/env node
// probe-client.mjs — a headless test client: joins, sends one 3-tick input batch every 100 ms (lead 3) like the Unity
// client will, and prints what it sees (notification rate / loss, confirm statuses, RTT, accepted vs late).
//   node tools/chain-emulator/src/probe-client.mjs [--url ws://127.0.0.1:8787/] [--seconds 10] [--slot N] [--lead 3]
import { decodeWorldAcc } from './world-view.mjs';
import { Core } from './core.mjs';
import { inputFrame, FRAME } from './server.mjs';

const arg = (k, d) => { const i = process.argv.indexOf(`--${k}`); return i > 0 ? process.argv[i + 1] : d; };
const url = arg('url', 'ws://127.0.0.1:8787/');
const seconds = Number(arg('seconds', 10));
const lead = Number(arg('lead', 3));
const slotArg = arg('slot');
const layout = new Core().layout();

const ws = new WebSocket(url);
ws.binaryType = 'arraybuffer';
const s = { notif: 0, gaps: 0, lastSeq: 0, confirms: [0, 0, 0, 0], rtt: [], lastTick: 0, lastAt: 0, slot: null, phase: -1, arena: '' };
let txid = 0;
ws.onopen = () => ws.send(JSON.stringify({ t: 'join', name: 'probe', session: 'probe', authority: 'probe', ...(slotArg ? { slot: Number(slotArg) } : {}) }));
ws.onmessage = (m) => {
  if (typeof m.data === 'string') {
    const j = JSON.parse(m.data);
    if (j.t === 'joined') { s.slot = j.slot; s.arena = j.arena; console.log(`joined ${j.arena} slot ${j.slot} map ${j.map} roster ${j.roster.map((r) => r.slot + ':' + r.kind).join(' ')}`); }
    else if (j.t === 'pong') s.rtt.push(performance.now() - j.c);
    else console.log('text', j);
    return;
  }
  const b = new Uint8Array(m.data);
  const dv = new DataView(b.buffer);
  if (b[0] === FRAME.NOTIFY) {
    const seq = dv.getUint32(1, true);
    if (s.lastSeq && seq > s.lastSeq + 1) s.gaps += seq - s.lastSeq - 1;
    s.lastSeq = seq; s.notif++;
    const acc = decodeWorldAcc(b.subarray(5), layout);
    s.lastTick = acc.tick; s.lastAt = performance.now();
    if (acc.world.round.phase !== s.phase) { s.phase = acc.world.round.phase; console.log(`tick ${acc.tick} phase ${s.phase}${s.phase === 4 ? ' champion slot ' + acc.world.round.result.order[0] : ''}`); }
  } else if (b[0] === FRAME.CONFIRM) s.confirms[b[5]]++;
};
const rttEst = () => (s.rtt.length ? s.rtt.slice(-10).reduce((a, x) => a + x, 0) / Math.min(10, s.rtt.length) : 100);
const send = setInterval(() => {
  if (s.slot === null || s.slot === 255 || !s.lastAt) return;
  // Estimate of the chain's tick now: last seen + one-way delay + time since it arrived.
  const T = s.lastTick + Math.round((rttEst() / 2 + performance.now() - s.lastAt) / (1000 / 30));
  const t = performance.now() / 1000;
  const c = (k) => new Uint8Array([Math.round(Math.cos(t + k) * 90) & 0xff, Math.round(Math.sin(t + k) * 90) & 0xff, 0, 0, 0]);
  ws.send(inputFrame(++txid, s.slot, T + lead, [c(0), c(1), c(2)]));
}, 100);
const ping = setInterval(() => ws.readyState === 1 && ws.send(JSON.stringify({ t: 'ping', c: performance.now() })), 1000);
setTimeout(() => {
  clearInterval(send); clearInterval(ping);
  const r = s.rtt.slice().sort((a, b) => a - b);
  console.log(`after ${seconds}s: ${s.notif} notifications (${(s.notif / seconds).toFixed(1)}/s), ${s.gaps} lost; confirms ok ${s.confirms[0]} late ${s.confirms[1]} window ${s.confirms[2]} badslot ${s.confirms[3]} of ${txid} sent; RTT p50 ${r[r.length >> 1]?.toFixed(1)} ms`);
  ws.close();
  process.exit(0);
}, seconds * 1000);
