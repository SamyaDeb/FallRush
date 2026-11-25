// Input transactions of one player in one match, straight from the rollup (input run 2, 2026-11-25): accepted vs
// refused (6020 InputOutOfWindow), and how far each batch's stamped tick sat from the arena's real-time tick.
//   node tools/fullchain/input-tx-health.mjs <session key> <match start slot> <from ISO> <to ISO>
// The start slot is world2.bin's header @72 (WorldAcc.start_slot); devnet-as slots are 10 ms → 0.3 ticks per slot.
const [who, s0, from, to] = process.argv.slice(2);
if (!to) { console.log('usage: input-tx-health.mjs <session key> <start slot> <from ISO> <to ISO>'); process.exit(2); }
const ER = 'https://devnet-as.magicblock.app', S0 = Number(s0);
const rpc = async (method, params) => { for (let k = 0; k < 4; k++) { try { return await (await fetch(ER, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ jsonrpc: '2.0', id: 1, method, params }) })).json(); } catch { await new Promise((r) => setTimeout(r, 300)); } } return {}; };
const A = '123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz';
const unb58 = (s) => { let n = 0n; for (const c of s) n = n * 58n + BigInt(A.indexOf(c)); let h = n.toString(16); if (h.length % 2) h = '0' + h; const out = Buffer.from(h, 'hex'); let z = 0; for (const c of s) { if (c === '1') z++; else break; } return Buffer.concat([Buffer.alloc(z), out]); };
const t0 = Date.parse(from) / 1000, t1 = Date.parse(to) / 1000;
const all = []; let before;
for (let i = 0; i < 10; i++) { const r = await rpc('getSignaturesForAddress', [who, { limit: 1000, before }]); const a = r.result ?? []; if (!a.length) break; all.push(...a); before = a[a.length - 1].signature; if (a[a.length - 1].blockTime < t0) break; }
const m = all.filter((x) => x.blockTime >= t0 && x.blockTime <= t1);
const rows = [];
for (let i = 0; i < m.length; i += 25) await Promise.all(m.slice(i, i + 25).map(async (x) => {
  const tx = (await rpc('getTransaction', [x.signature, { encoding: 'json', maxSupportedTransactionVersion: 0 }])).result;
  const ix = tx?.transaction.message.instructions.find((q) => { const d = unb58(q.data); return d.length >= 13 && d[0] === 56 && d[1] === 184; });
  if (!ix) return;
  const start = unb58(ix.data).readUInt32LE(9);
  rows.push({ ok: !x.err, slot: tx.slot, start, d: start - (tx.slot - S0) * 0.3 });
}));
if (process.env.DUMP) (await import('node:fs')).writeFileSync(process.env.DUMP, JSON.stringify(rows.map((r) => ({ ...r }))));
const q = (a, p) => { const s = [...a].sort((x, y) => x - y); return s.length ? s[Math.floor(p * (s.length - 1))].toFixed(1) : '-'; };
const ok = rows.filter((r) => r.ok).map((r) => r.d), bad = rows.filter((r) => !r.ok).map((r) => r.d);
console.log(`input txs ${rows.length}: accepted ${ok.length}, refused ${bad.length} (${(100 * bad.length / Math.max(1, rows.length)).toFixed(1)}%: ahead ${bad.filter((x) => x > 0).length}, behind ${bad.filter((x) => x <= 0).length})`);
console.log(`stamp − arena real-time tick: accepted p5/50/95 ${q(ok, .05)} / ${q(ok, .5)} / ${q(ok, .95)}   refused p50 ${q(bad, .5)}`);
