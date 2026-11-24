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
async function tx(c: Connection, sig: string) {
  for (let i = 0; i < 4; i++) {
    try { return await c.getTransaction(sig, { maxSupportedTransactionVersion: 1, commitment: "confirmed" } as any); } catch { await sleep(800 * (i + 1)); }
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
async function findIn(c: Connection, addr: string, re: RegExp, limit = 200) {
  const sigs = await c.getSignaturesForAddress(new PublicKey(addr), { limit }).catch(() => []);
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
const st: any = await (await fetch(`${IDX}/api/status`)).json();
const sv = st.servers.find((x: any) => x.arena === s.arena);
out.arena = s.arena; out.arenaKey = sv?.arenaKey; out.world = sv?.world;
await add("Match create (Solana)", "the Match account is created on Solana by the arena keeper", sig.create, false);
await add("Delegate to the Ephemeral Rollup", "the Match is delegated to MagicBlock's rollup validator", sig.delegate, false);
await add("VRF deal request", "the program asks MagicBlock VRF for the match seed", sig.requestDeal, true);
const cb = await findIn(er, match, /DealCallback|deal_callback/i, 300);
if (cb) out.links.push({ system: "VRF deal callback", proves: "the VRF oracle's callback writes the seed into the Match (it seeds the on-chain Final)", sig: cb.sig, url: ex("tx", cb.sig, true), rollup: true, live: true, cu: cb.t?.meta?.computeUnitsConsumed });
else out.links.push({ system: "VRF deal callback", missing: true });
await add("Start the on-chain Final", "the finalists are handed from the Classic rounds to the program", sig.startFinal, true);
// a tick() of the Final (timed task) with its compute units
if (sv?.world) {
  const tk = await findIn(er, sv.world, /Instruction: Tick/i, 60);
  if (tk) out.links.push({ system: "tick() (timed task, 30 Hz)", proves: "one physics step of the Final, run as sBPF by the program", sig: tk.sig, url: ex("tx", tk.sig, true), rollup: true, live: true, cu: tk.t?.meta?.computeUnitsConsumed, note: "a recent tick of this arena's World" });
}
// market txs from the indexer's tx lists (bets, drops)
const txs = j.txs ?? {};
const firstOk = (arr: any[]) => (arr ?? []).find((x) => !x.err)?.sig;
await add("Open bet (Rush Market)", "a spectator's bet, signed by a session key, settled by the program", firstOk(txs.qualify1) ?? firstOk(txs.qualify2) ?? firstOk(txs.finalWinner), true);
await add("Sealed market close (TEE book)", "the sealed Champion book is closed on MagicBlock's Private ER", sig.bookClose, false, { note: "devnet-tee: the explorer cannot open the private rollup; the close is recorded here" });
await add("Sealed reveal", "a sealed pick revealed and checked against its commitment", (s.sealed?.reveals ?? [])[0], true);
await add("Sealed settle", "the program settles the sealed bets with the Champion market", sig.sealedFinish, true);
await add("Chaos Drop", "a spectator's map-wide event bought on-chain, entering the Final's World", firstOk(txs.drops), true);
await add("Receipt (rollup)", "the program writes the match receipt", sig.writeReceipt, true);
await add("Finalize: commit + undelegate + Magic Action", "the Match returns to Solana; a Magic Action mints the rewards", sig.finalize, true);
// the undelegation tx on Solana (fee payer = the validator) and the minted assets
const und = await findIn(base, match, /ProcessUndelegation|Undelegat/i, 20);
if (und) out.links.push({ system: "Undelegation on Solana (Magic Action inside)", proves: "commit + undelegate landed on Solana; the post-undelegate action created the Core assets", sig: und.sig, url: ex("tx", und.sig, false), rollup: false, live: true, feePayer: und.t?.transaction?.message?.accountKeys?.[0]?.toBase58?.() ?? null });
for (const a of [...new Set([...(s.receipt?.crown && s.receipt.crown !== "11111111111111111111111111111111" ? [s.receipt.crown] : []), ...(s.assets ?? [])])]) {
  const info = await base.getAccountInfo(new PublicKey(a)).catch(() => null);
  out.links.push({ system: a === s.receipt?.crown ? "Crown Capsule (Metaplex Core)" : "Capsule (Metaplex Core)", proves: "an asset the player owns, minted by the program", address: a, url: ex("address", a, false), live: !!info });
}
await add("Season record", "the match recorded into the on-chain weekly Season", Object.values(s.seasons ?? {}).flatMap((v: any) => v.sigs ?? [])[0] as string, false);
// one human input batch, signed by a session key (submit_input touches the arena's World)
if (sv?.world) {
  const ip = await findIn(er, sv.world, /SubmitInput|submit_input/i, 400);
  if (ip) out.links.push({ system: "Player input (session key)", proves: "a player's input batch for the Final, signed by a session key (no wallet popup)", sig: ip.sig, url: ex("tx", ip.sig, true), rollup: true, live: true, signer: ip.t?.transaction?.message?.accountKeys?.[0]?.toBase58?.() ?? null });
  else out.links.push({ system: "Player input (session key)", missing: true });
}
out.links.push({ system: "Match account", address: match, url: ex("address", match, false), live: !!(await base.getAccountInfo(new PublicKey(match)).catch(() => null)) });
console.log(JSON.stringify(out, null, 1));
