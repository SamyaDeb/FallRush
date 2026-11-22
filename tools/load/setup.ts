// Creates + funds + delegates every spike account on the chosen net. Idempotent-ish (skips existing).
// P0_NET=local|devnet tsx setup.ts [nBets]
import { Keypair, PublicKey, SystemProgram, LAMPORTS_PER_SOL } from "@solana/web3.js";
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { base, er, EP, IX, N, NET, LOGS, PROGRAM_ID, wallet, key, pda, sendBase, sleep } from "./lib.ts";

const nBets = Number(process.argv[2] ?? 10);
const FUND = 1_000_000; // lamports per client fee-payer (rent-exempt minimum is 890,880)

async function exists(k: PublicKey) { for (let i = 0; ; i++) { try { await sleep(NET === "devnet" ? 250 : 0); return (await base.getAccountInfo(k)) !== null; } catch (e) { if (i > 8) throw e; await sleep(3000); } } }

async function main() {
  const t0 = Date.now();
  const bal0 = await base.getBalance(wallet.publicKey);
  console.log(`net=${NET} wallet balance ${bal0 / LAMPORTS_PER_SOL} SOL`);
  const owners = Array.from({ length: N }, (_, s) => key(`input-${s}`));
  const bettors = Array.from({ length: nBets }, (_, i) => key(`bet-${i}`));
  // 1. fund client fee payers
  const toFund: Keypair[] = [];
  for (const k of [...owners, ...bettors]) if (!(await exists(k.publicKey))) toFund.push(k);
  for (let i = 0; i < toFund.length; i += 8) {
    await sendBase(toFund.slice(i, i + 8).map((k) => SystemProgram.transfer({ fromPubkey: wallet.publicKey, toPubkey: k.publicKey, lamports: FUND })), [wallet]);
  }
  console.log(`funded ${toFund.length} client keypairs`);
  // 2. map blobs
  const maps: Record<string, string> = {};
  for (const name of ["lid", "run"]) {
    const blob = readFileSync(join(LOGS, `map_${name}.bin`));
    const mk = key(`map-${name}`);
    maps[name] = mk.publicKey.toBase58();
    if (!(await exists(mk.publicKey))) {
      const space = 48 + blob.length;
      const lamports = await base.getMinimumBalanceForRentExemption(space);
      await sendBase([SystemProgram.createAccount({ fromPubkey: wallet.publicKey, newAccountPubkey: mk.publicKey, lamports, space, programId: PROGRAM_ID })], [wallet, mk]);
    }
    {
      const CH = 900;
      const cur = Buffer.from((await base.getAccountInfo(mk.publicKey))!.data);
      let up = 0;
      for (let o = 0; o < blob.length; o += CH) {
        const want = blob.subarray(o, o + CH);
        if (cur[0] === 0x50 && Buffer.from(cur.subarray(48 + o, 48 + o + want.length)).equals(want) && cur.readUInt32LE(40) === blob.length) continue;
        await sendBase([IX.uploadMap(wallet.publicKey, mk.publicKey, o, blob.length, want)], [wallet]);
        up++;
      }
      console.log(`map ${name}: ${blob.length} B, ${up} chunks uploaded -> ${mk.publicKey.toBase58()}`);
    }
    const onchain = (await base.getAccountInfo(mk.publicKey))!.data.subarray(48, 48 + blob.length);
    if (!Buffer.from(onchain).equals(blob)) throw new Error(`map ${name} mismatch on chain`);
  }
  // 3. worlds (0 = live tick, 1 = run_steps) + inputs + bets
  for (const wid of [0, 1]) {
    if (!(await exists(pda.world(wid)))) await sendBase([IX.initWorld(wallet.publicKey, wid, 1, 0)], [wallet]);
  }
  for (let s = 0; s < N; s++) {
    if (!(await exists(pda.input(0, s)))) await sendBase([IX.initInput(wallet.publicKey, 0, s, owners[s].publicKey)], [wallet]);
  }
  for (const b of bettors) if (!(await exists(pda.bet(b.publicKey)))) await sendBase([IX.initBet(wallet.publicKey, b.publicKey)], [wallet]);
  console.log("accounts initialised");
  // 4. delegate
  const del: [PublicKey, number, number, number, PublicKey][] = [];
  for (const wid of [0, 1]) { del.push([pda.world(wid), 0, wid, 0, PublicKey.default]); del.push([pda.log(wid), 1, wid, 0, PublicKey.default]); }
  for (let s = 0; s < N; s++) del.push([pda.input(0, s), 2, 0, s, PublicKey.default]);
  for (const b of bettors) del.push([pda.bet(b.publicKey), 3, 0, 0, b.publicKey]);
  let nd = 0;
  for (const [k, kind, wid, slot, owner] of del) {
    await sleep(NET === "devnet" ? 250 : 0);
    const info = await base.getAccountInfo(k);
    if (info && info.owner.equals(PROGRAM_ID)) {
      await sendBase([IX.delegate(wallet.publicKey, k, kind, wid, slot, owner, EP.validator)], [wallet]);
      nd++;
    }
  }
  console.log(`delegated ${nd} accounts`);
  // wait until the ER sees world 0
  for (let i = 0; i < 60; i++) { if (await er.getAccountInfo(pda.world(0))) break; await sleep(500); }
  const bal1 = await base.getBalance(wallet.publicKey);
  const state = { net: NET, program: PROGRAM_ID.toBase58(), maps, world0: pda.world(0).toBase58(), world1: pda.world(1).toBase58(), log0: pda.log(0).toBase58(),
    owners: owners.map((k) => k.publicKey.toBase58()), bettors: bettors.map((k) => k.publicKey.toBase58()), spentLamports: bal0 - bal1, at: new Date().toISOString() };
  writeFileSync(join(LOGS, `state-${NET}.json`), JSON.stringify(state, null, 2));
  console.log(`setup done in ${((Date.now() - t0) / 1000).toFixed(1)} s, spent ${(bal0 - bal1) / LAMPORTS_PER_SOL} SOL`);
}
main().catch((e) => { console.error(e); process.exit(1); });
