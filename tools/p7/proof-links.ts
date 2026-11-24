// P7 C1: the PROOF.md link list for one archived match, every link re-fetched live (getTransaction / getAccountInfo)
// so the list only holds what the explorer can open today. Rollup links use the explorer's custom cluster (devnet-as).
//   cd tools/keeper && npx tsx ../p7/proof-links.ts <match> [--base https://fallrush.fun] > ../../docs/proof/p7/proof-links.json
import { Connection, PublicKey } from "@solana/web3.js";
const ER = "https://devnet-as.magicblock.app", BASE = "https://api.devnet.solana.com";
const er = new Connection(ER, "confirmed"), base = new Connection(BASE, "confirmed");
const match = process.argv[2];
const ib = process.argv.indexOf("--base"); const IDX = ib > 0 ? process.argv[ib + 1] : "https://fallrush.fun";
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
const ex = (kind: "tx" | "address", id: string, rollup: boolean) => `https://explorer.solana.com/${kind}/${id}?cluster=${rollup ? "custom&customUrl=" + encodeURIComponent(ER) : "devnet"}`;
// raw JSON-RPC: web3.js cannot parse version-1 transactions (the undelegation + Magic Action ones), P6 gotcha
async function tx(c: Connection, sig: string) {
  for (let i = 0; i < 4; i++) {
    try {
      const r = await fetch(c.rpcEndpoint, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ jsonrpc: "2.0", id: 1, method: "getTransaction", params: [sig, { encoding: "json", maxSupportedTransactionVersion: 1, commitment: "confirmed" }] }) });
      if (r.status === 429) throw new Error("429");
      const j: any = await r.json();
      const t = j.result;
      if (t?.transaction?.message?.accountKeys) t.transaction.message.accountKeys = t.transaction.message.accountKeys.map((k: string) => ({ toBase58: () => k, toString: () => k }));
      return t ?? null;
    } catch { await sleep(800 * (i + 1)); }
  }
  return null;
}
const out: any = { match, at: new Date().toISOString(), links: [] as any[] };
const add = async (system: string, proves: string, sig: string | null | undefined, rollup: boolean, extra: object = {}) => {
  if (!sig) { out.links.push({ system, proves, missing: true }); return null; }
  const t: any = await tx(rollup ? er : base, sig);
  out.links.push({ system, proves, sig, url: ex("tx", sig, rollup), rollup, live: !!t, err: t?.meta?.err ?? null, cu: t?.meta?.computeUnitsConsumed ?? null, slot: t?.slot ?? null, ...extra });
  await sleep(250);
  return t;
};
const logsHave = (t: any, re: RegExp) => (t?.meta?.logMessages ?? []).some((l: string) => re.test(l));
const PROGRAM = "4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4";
const B58 = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
const b58dec = (str: string) => { let n = 0n; for (const c of str) n = n * 58n + BigInt(B58.indexOf(c)); const out: number[] = []; while (n > 0n) { out.unshift(Number(n & 255n)); n >>= 8n; } for (const c of str) { if (c !== "1") break; out.unshift(0); } return out; };
/** submission: the econ op (econ.rs OP: PLACE_BET 7, BUY_DROP 9) of a tx's fall_rush instruction (byte 8 after the discriminator). */
const econOp = (t: any) => {
  const msg = t?.transaction?.message; const keys = (msg?.accountKeys ?? msg?.staticAccountKeys ?? []).map((k: any) => k.toBase58?.() ?? String(k));
  for (const ix of msg?.instructions ?? msg?.compiledInstructions ?? []) {
    if (keys[ix.programIdIndex] !== PROGRAM) continue;
    const d = typeof ix.data === "string" ? b58dec(ix.data) : Array.from(ix.data ?? []);
    if (d.length > 8) return d[8];
  }
  return -1;
};
/** The first (oldest-first scan) tx of a list whose econ op is `op`. */
async function firstOp(list: any[], op: number) {
  for (const x of [...(list ?? [])].reverse()) {
    if (x.err || !x.sig) continue;
    const t: any = await tx(er, x.sig);
    if (t && econOp(t) === op) return x.sig as string;
    await sleep(120);
  }
  return null;
}
/** The highest-CU tick() of the Final: the Match's signatures between start_final and write_receipt (every tick
 * touches the Match), sampled every 25th, paging backwards from the receipt. */
async function bestTick(world: string, limit = 300) {
  if (sig.writeReceipt && sig.startFinal) {
    let before = sig.writeReceipt, best: any = null, n = 0;
    for (let page = 0; page < 8; page++) {
      const ps = await er.getSignaturesForAddress(new PublicKey(match), { before, until: sig.startFinal, limit: 1000 }).catch(() => []);
      if (!ps.length) break;
      for (let i = 0; i < ps.length; i += 25) {
        const x = ps[i]; if (x.err) continue;
        const t: any = await tx(er, x.signature); n++;
        if (t && logsHave(t, /Instruction: Tick/i) && !logsHave(t, /no-op/i) && (!best || (t.meta?.computeUnitsConsumed ?? 0) > (best.t.meta?.computeUnitsConsumed ?? 0))) best = { sig: x.signature, t };
        await sleep(40);
      }
      before = ps[ps.length - 1].signature;
    }
    if (best) { best.sampled = n; return best; }
  }
  const sigs = await er.getSignaturesForAddress(new PublicKey(world), { limit }).catch(() => []);
  let best: any = null, seen = 0;
  for (const x of sigs) {
    if (x.err) continue;
    const t: any = await tx(er, x.signature);
    if (t && logsHave(t, /Instruction: Tick/i)) { seen++; if (!best || (t.meta?.computeUnitsConsumed ?? 0) > (best.t.meta?.computeUnitsConsumed ?? 0)) best = { sig: x.signature, t }; if (seen >= 40) break; }
    await sleep(60);
  }
  return best;
}
async function findIn(c: Connection, addr: string, re: RegExp, limit = 200, oldestFirst = false) {
  const sigs0 = await c.getSignaturesForAddress(new PublicKey(addr), { limit }).catch(() => []);
  const sigs = oldestFirst ? [...sigs0].reverse() : sigs0;
  for (const s of sigs) {
    if (s.err) continue;
    const t: any = await tx(c, s.signature);
    if (t && logsHave(t, re)) return { sig: s.signature, t };
    await sleep(120);
  }
  return null;
}

const j: any = await (await fetch(`${IDX}/api/match/${match}`)).json();
if (!j.ok) throw new Error("not archived: " + JSON.stringify(j).slice(0, 200));
const s = j.summary, sig = s.sigs ?? {};
out.judge = !!s.judge;
const st: any = await (await fetch(`${IDX}/api/status`)).json();
const sv = st.servers.find((x: any) => x.arena === s.arena);
out.arena = s.arena; out.arenaKey = sv?.arenaKey; out.world = sv?.world;
await add("Match create (Solana)", "the Match account is created on Solana by the arena keeper", sig.create, false);
await add("Delegate to the Ephemeral Rollup", "the Match is delegated to MagicBlock's rollup validator", sig.delegate, false);
await add("VRF deal request", "the program asks MagicBlock VRF for the match seed", sig.requestDeal, true);
// every tick() touches the Match: look just before start_final (the callback lands before rounds 1–3 are bridged)
let cb: any = null;
if (sig.startFinal) {
  const pre = await er.getSignaturesForAddress(new PublicKey(match), { before: sig.startFinal, limit: 300 }).catch(() => []);
  for (const x of [...pre].reverse()) { if (x.err) continue; const t: any = await tx(er, x.signature); if (t && logsHave(t, /DealCallback|deal_callback/i)) { cb = { sig: x.signature, t }; break; } await sleep(80); }
}
if (cb) out.links.push({ system: "VRF deal callback", proves: "the VRF oracle's callback writes the seed into the Match (it seeds the on-chain Final)", sig: cb.sig, url: ex("tx", cb.sig, true), rollup: true, live: true, cu: cb.t?.meta?.computeUnitsConsumed });
else out.links.push({ system: "VRF deal callback", missing: true });
await add("Start the on-chain Final", "the finalists are handed from the Classic rounds to the program", sig.startFinal, true);
// a tick() of the Final (timed task) with its compute units
if (sv?.world) {
  const tk = await bestTick(sv.world);
  if (tk) out.links.push({ system: "tick() (timed task, 30 Hz)", proves: "one physics step of the Final, run as sBPF by the program (the costliest of the ticks sampled)", sig: tk.sig, url: ex("tx", tk.sig, true), rollup: true, live: true, cu: tk.t?.meta?.computeUnitsConsumed, sampled: tk.sampled });
}
// market txs from the indexer's tx lists (bets, drops)
const txs = j.txs ?? {};
const firstOk = (arr: any[]) => (arr ?? []).find((x) => !x.err)?.sig;
await add("Bet (Rush Market)", "a bet on this match's market, settled by the program (signer shown on the explorer)", (await firstOp(txs.finalWinner, 7)) ?? (await firstOp(txs.champion, 7)) ?? (await firstOp(txs.qualify1, 7)), true);
await add("Sealed market close (TEE book)", "the sealed Champion book is closed on MagicBlock's Private ER", sig.bookClose, false, { note: "devnet-tee: the explorer cannot open the private rollup; the close is recorded here" });
await add("Sealed reveal", "a sealed pick revealed and checked against its commitment", (s.sealed?.reveals ?? [])[0], true);
await add("Sealed settle", "the program settles the sealed bets with the Champion market", sig.sealedFinish, true);
// a drop buyer's ChipBalance (seeds "chips", wallet) has few txs; the drop queue itself is touched by every tick
let dropSig: string | null = null;
for (const b of (s.drops?.buys ?? []).slice(0, 3)) {
  const chips = PublicKey.findProgramAddressSync([Buffer.from("chips"), new PublicKey(b.by).toBuffer()], new PublicKey(PROGRAM))[0];
  const list = (await er.getSignaturesForAddress(chips, { limit: 60 }).catch(() => [])).map((x) => ({ sig: x.signature, err: !!x.err }));
  dropSig = await firstOp(list, 9);
  if (dropSig) break;
}
await add("Chaos Drop", "a spectator's map-wide event bought on-chain with a session key, entering the Final's World", dropSig ?? (await firstOp(txs.drops, 9)), true);
await add("Receipt (rollup)", "the program writes the match receipt", sig.writeReceipt, true);
await add("Finalize: commit + undelegate + Magic Action", "the Match returns to Solana; a Magic Action mints the rewards", sig.finalize, true);
// the undelegation tx on Solana (fee payer = the validator) and the minted assets
const und = await findIn(base, match, /ProcessUndelegation|Undelegat/i, 60, true);
if (und) out.links.push({ system: "Undelegation on Solana (Magic Action inside)", proves: "commit + undelegate landed on Solana; the post-undelegate action created the Core assets", sig: und.sig, url: ex("tx", und.sig, false), rollup: false, live: true, feePayer: und.t?.transaction?.message?.accountKeys?.[0]?.toBase58?.() ?? null });
for (const a of [...new Set([...(s.receipt?.crown && s.receipt.crown !== "11111111111111111111111111111111" ? [s.receipt.crown] : []), ...(s.assets ?? [])])]) {
  const info = await base.getAccountInfo(new PublicKey(a)).catch(() => null);
  out.links.push({ system: a === s.receipt?.crown ? "Crown Capsule (Metaplex Core)" : "Capsule (Metaplex Core)", proves: "an asset the player owns, minted by the program", address: a, url: ex("address", a, false), live: !!info });
}
await add("Season record", "the match recorded into the on-chain weekly Season", Object.values(s.seasons ?? {}).flatMap((v: any) => v.sigs ?? [])[0] as string, false);
// one human input batch, signed by a session key (submit_input touches the arena's World)
if (sv?.world) {
  // the human finalists' input accounts (seeds "in", arena, slot) hold their session-key-signed batches
  const humanSlots = [...Array(12).keys()].filter((k) => ((s.receipt?.humanMask ?? 0) >> k) & 1);
  let ip: any = null;
  for (const k of humanSlots) {
    const inPda = PublicKey.findProgramAddressSync([Buffer.from("in"), new PublicKey(sv.arenaKey).toBuffer(), Buffer.from([k])], new PublicKey(PROGRAM))[0];
    ip = await findIn(er, inPda.toBase58(), /SubmitInput|submit_input/i, 60);
    if (ip) break;
  }
  if (!ip) ip = await findIn(er, sv.world, /SubmitInput|submit_input/i, 400);
  if (ip) out.links.push({ system: "Player input (session key)", proves: "a player's input batch for the Final, signed by a session key (no wallet popup)", sig: ip.sig, url: ex("tx", ip.sig, true), rollup: true, live: true, signer: ip.t?.transaction?.message?.accountKeys?.[0]?.toBase58?.() ?? null });
  else out.links.push({ system: "Player input (session key)", missing: true });
}
out.links.push({ system: "Match account", address: match, url: ex("address", match, false), live: !!(await base.getAccountInfo(new PublicKey(match)).catch(() => null)) });
console.log(JSON.stringify(out, null, 1));
