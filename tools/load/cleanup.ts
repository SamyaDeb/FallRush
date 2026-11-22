// Undelegate (ER) and close (Solana) every spike account, refund client fee payers. Program stays deployed.
// P0_NET=.. tsx cleanup.ts
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { PublicKey, SystemProgram, LAMPORTS_PER_SOL } from "@solana/web3.js";
import { base, er, IX, LOGS, NET, PROGRAM_ID, wallet, pda, key, send, sendBase, sleep, N } from "./lib.ts";
const st = JSON.parse(readFileSync(join(LOGS, `state-${NET}.json`), "utf8"));
const bal0 = await base.getBalance(wallet.publicKey);
const deleg: PublicKey[] = [pda.world(0), pda.log(0), pda.world(1), pda.log(1), ...Array.from({ length: N }, (_, s) => pda.input(0, s)), ...st.bettors.map((b: string) => pda.bet(new PublicKey(b)))];
for (let i = 0; i < deleg.length; i += 4) {
  const chunk = deleg.slice(i, i + 4);
  const r = await send(er, [IX.undelegate(wallet.publicKey, chunk)], [wallet]);
  console.log(`undelegate ${i}..${i + chunk.length}: ${r.err ?? "ok"}`);
}
// wait for ownership to return to the program on Solana
for (let t = 0; t < 120; t++) {
  let back = 0;
  for (const k of deleg) { const a = await base.getAccountInfo(k).catch(() => null); if (a && a.owner.equals(PROGRAM_ID)) back++; await sleep(150); }
  console.log(`  back on Solana: ${back}/${deleg.length}`);
  if (back === deleg.length) break;
  await sleep(5000);
}
let closed = 0;
for (const k of [...deleg, new PublicKey(st.maps.lid), new PublicKey(st.maps.run)]) {
  const a = await base.getAccountInfo(k).catch(() => null);
  if (!a || !a.owner.equals(PROGRAM_ID)) { console.log(`skip ${k.toBase58()} (owner ${a?.owner.toBase58() ?? "none"})`); continue; }
  try { await sendBase([IX.closeAny(wallet.publicKey, k)], [wallet]); closed++; } catch (e: any) { console.log(`close ${k.toBase58()} failed: ${String(e.message).slice(0, 120)}`); }
}
// refund client keypairs
const clients = [...Array.from({ length: N }, (_, s) => key(`input-${s}`)), ...st.bettors.map((_: string, i: number) => key(`bet-${i}`))];
let refunded = 0;
for (const c of clients) {
  const b = await base.getBalance(c.publicKey).catch(() => 0);
  if (b > 5000) { try { await sendBase([SystemProgram.transfer({ fromPubkey: c.publicKey, toPubkey: wallet.publicKey, lamports: b - 5000 })], [c]); refunded++; } catch (e: any) { console.log(`refund failed ${String(e.message).slice(0, 80)}`); } }
}
const bal1 = await base.getBalance(wallet.publicKey);
const out = { net: NET, closed, refunded_clients: refunded, refund_sol: (bal1 - bal0) / LAMPORTS_PER_SOL, balance_after: bal1 / LAMPORTS_PER_SOL, at: new Date().toISOString() };
console.log(JSON.stringify(out));
writeFileSync(join(LOGS, `cleanup-${NET}.json`), JSON.stringify(out, null, 2));
process.exit(0);
