/**
 * pnpm m0:devnet — full M0 lifecycle on Solana devnet + MagicBlock ER devnet-as:
 * config (if missing) → create → delegate → request_deal (VRF) → round 1 events →
 * complete_round (commit) → finalize_match (commit + undelegate).
 * Prints every signature with its layer, account snapshots between steps, and writes
 * docs/proof/M0-devnet.md.
 *
 * Keys: admin/upgrade authority = ~/.config/solana/id.json;
 *       game authority = ~/.config/fall-rush/devnet-game-authority.json (override: GAME_AUTHORITY_KEYPAIR=path).
 */
import { AnchorProvider, Program, Wallet } from "@coral-xyz/anchor";
import { Connection, Keypair, PublicKey, Transaction } from "@solana/web3.js";
import { GetCommitmentSignature } from "@magicblock-labs/ephemeral-rollups-sdk";
import { randomBytes } from "node:crypto";
import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";
import type { FallRush } from "../programs/target/types/fall_rush";
import { DELEGATION_PROGRAM_ID, NETWORKS, deriveDeal, mapPoolV1Hash, roundTargets, rulesetV1Hash } from "../packages/shared/src";

const ROOT = join(__dirname, "..");
const idl = JSON.parse(readFileSync(join(ROOT, "programs/target/idl/fall_rush.json"), "utf8"));
const net = NETWORKS.devnet;
const SOLANA_RPC = process.env.SOLANA_RPC_URL || net.solanaRpc;
const ER_RPC = process.env.ER_RPC_URL || net.erRpc;
const ER_WS = process.env.ER_WS_URL || net.erWs;
const VALIDATOR = new PublicKey(process.env.ER_VALIDATOR || net.validator);
const VRF_QUEUE = new PublicKey(process.env.VRF_QUEUE || net.vrfQueue);
const VRF_TIMEOUT_MS = 60_000;
const COMMIT_TIMEOUT_MS = 120_000;
const PLAYERS = 12;

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
const statusOf = (s: object) => Object.keys(s)[0];
const loadKp = (p: string) => Keypair.fromSecretKey(Uint8Array.from(JSON.parse(readFileSync(p, "utf8"))));

async function pollUntil<T>(what: string, timeoutMs: number, fn: () => Promise<T | undefined>, everyMs = 500): Promise<T> {
  const start = Date.now();
  for (;;) {
    const v = await fn().catch(() => undefined);
    if (v !== undefined) return v;
    if (Date.now() - start > timeoutMs) throw new Error(`timeout after ${timeoutMs}ms waiting for ${what}`);
    await sleep(everyMs);
  }
}

/** Sign, send with preflight, confirm by polling statuses (see programs/tests/lifecycle.ts for why). */
async function sendAndPoll(conn: Connection, wallet: Wallet, tx: Transaction, signers: Keypair[] = []) {
  tx.feePayer = tx.feePayer ?? wallet.publicKey;
  tx.recentBlockhash = (await conn.getLatestBlockhash("confirmed")).blockhash;
  if (signers.length) tx.partialSign(...signers);
  const signed = await wallet.signTransaction(tx);
  const sig = await conn.sendRawTransaction(signed.serialize(), { preflightCommitment: "confirmed" });
  await pollUntil(`confirmation of ${sig}`, 60_000, async () => {
    const st = (await conn.getSignatureStatuses([sig])).value[0];
    if (!st || !(st.confirmationStatus === "confirmed" || st.confirmationStatus === "finalized")) return undefined;
    if (st.err) throw Object.assign(new Error(`tx ${sig} failed: ${JSON.stringify(st.err)}`), { fatal: true });
    return true;
  }, 150).catch(async (e) => {
    const t = await conn.getTransaction(sig, { commitment: "confirmed", maxSupportedTransactionVersion: 0 }).catch(() => null);
    throw Object.assign(new Error(`${e.message}\n${(t?.meta?.logMessages ?? []).join("\n")}`), { sig });
  });
  return sig;
}

function programFor(conn: Connection, kp: Keypair) {
  const wallet = new Wallet(kp);
  const provider = new AnchorProvider(conn, wallet, { commitment: "confirmed" });
  provider.sendAndConfirm = (tx: any, signers: any[] = []) => sendAndPoll(conn, wallet, tx, signers);
  return new Program<FallRush>(idl, provider);
}

type Row = { step: string; layer: "Solana" | "ER"; sig: string; ms: number; note?: string };
const rows: Row[] = [];
const timings: Record<string, number> = {};

async function main() {
  const admin = loadKp(join(homedir(), ".config/solana/id.json"));
  const ga = loadKp(process.env.GAME_AUTHORITY_KEYPAIR || join(homedir(), ".config/fall-rush/devnet-game-authority.json"));
  const baseConn = new Connection(SOLANA_RPC, { commitment: "confirmed" });
  const erConn = new Connection(ER_RPC, { wsEndpoint: ER_WS, commitment: "confirmed" });
  const adminBase = programFor(baseConn, admin);
  const base = programFor(baseConn, ga);
  const er = programFor(erConn, ga);
  const programId = base.programId;

  const gaBal = await baseConn.getBalance(ga.publicKey);
  console.log(`program ${programId}\nSolana ${SOLANA_RPC}\nER ${ER_RPC} (validator ${VALIDATOR})\nVRF queue ${VRF_QUEUE}`);
  console.log(`admin ${admin.publicKey} | game authority ${ga.publicKey} (${gaBal / 1e9} SOL)`);
  if (gaBal < 0.05e9) throw new Error(`game authority ${ga.publicKey} needs devnet SOL (has ${gaBal / 1e9})`);

  const record = async (step: string, layer: Row["layer"], fn: () => Promise<string>, note?: string) => {
    const t0 = Date.now();
    const sig = await fn();
    const ms = Date.now() - t0;
    rows.push({ step, layer, sig, ms, note });
    console.log(`  [${layer}] ${step.padEnd(34)} ${sig}  (${ms} ms)`);
    return sig;
  };
  const snapshot = async (label: string, conn: Connection, prog: Program<FallRush>, pda: PublicKey) => {
    const info = await conn.getAccountInfo(pda, "confirmed");
    if (!info) return console.log(`  ⟐ ${label}: (absent)`);
    const m: any = prog.coder.accounts.decode("match", info.data);
    console.log(`  ⟐ ${label}: owner=${info.owner.toBase58().slice(0, 8)}… status=${statusOf(m.status)} round=${m.currentRound} seq=${m.eventSeq} transcript=${Buffer.from(m.transcriptHash).toString("hex").slice(0, 16)}…`);
  };

  // 1. config
  const [configPda] = PublicKey.findProgramAddressSync([Buffer.from("config")], programId);
  const cfgInfo = await baseConn.getAccountInfo(configPda);
  if (!cfgInfo) {
    const programData = PublicKey.findProgramAddressSync([programId.toBuffer()], new PublicKey("BPFLoaderUpgradeab1e11111111111111111111111"))[0];
    await record("initialize_config", "Solana", () =>
      adminBase.methods.initializeConfig(ga.publicKey, 1, [...rulesetV1Hash()], 1, [...mapPoolV1Hash()]).accountsPartial({ admin: admin.publicKey, programData }).rpc(),
    );
  } else {
    const cfg: any = await base.account.gameConfig.fetch(configPda);
    if (!cfg.gameAuthority.equals(ga.publicKey)) throw new Error(`config game_authority ${cfg.gameAuthority} != ${ga.publicKey}`);
    console.log(`  config exists (match_count ${cfg.matchCount})`);
  }

  // 2. create + delegate
  const matchId = randomBytes(16);
  const [matchPda] = PublicKey.findProgramAddressSync([Buffer.from("match"), matchId], programId);
  console.log(`match ${matchPda} (id ${matchId.toString("hex")})`);
  await record("create_match (12 players)", "Solana", () =>
    base.methods.createMatch([...matchId], [...randomBytes(32)], PLAYERS).accountsPartial({ authority: ga.publicKey }).rpc(),
  );
  await snapshot("after create (Solana)", baseConn, base, matchPda);
  await record("delegate_match → devnet-as", "Solana", () =>
    base.methods
      .delegateMatch([...matchId])
      .accountsPartial({ authority: ga.publicKey, matchAccount: matchPda })
      .remainingAccounts([{ pubkey: VALIDATOR, isSigner: false, isWritable: false }])
      .rpc(),
  );
  const owner = (await baseConn.getAccountInfo(matchPda, "confirmed"))!.owner.toBase58();
  if (owner !== DELEGATION_PROGRAM_ID) throw new Error(`owner after delegation is ${owner}`);
  console.log(`  ✓ base owner = Delegation Program`);
  await pollUntil("match visible on ER", 30_000, async () => ((await erConn.getAccountInfo(matchPda)) ? true : undefined));
  await snapshot("after delegate (ER)", erConn, er, matchPda);

  // 3. VRF deal on the ER (subscribe before sending, poll as fallback)
  let dealtSig = "";
  const subId = erConn.onLogs(programId, (l) => { if (!l.err && l.logs.some((x) => x.includes("Instruction: DealCallback"))) dealtSig ||= l.signature; }, "confirmed");
  const tReq = Date.now();
  const reqSig = await record("request_deal (VRF, ER queue)", "ER", () =>
    er.methods.requestDeal().accountsPartial({ authority: ga.publicKey, matchAccount: matchPda, oracleQueue: VRF_QUEUE }).rpc(),
  );
  const dealt: any = await pollUntil("Dealt status", VRF_TIMEOUT_MS, async () => {
    const m: any = await er.account.match.fetch(matchPda, "confirmed");
    return statusOf(m.status) === "dealt" ? m : undefined;
  }, 200);
  timings.vrfCallbackMs = Date.now() - tReq;
  await sleep(500);
  await erConn.removeOnLogsListener(subId);
  if (!dealtSig) {
    const sigs = await erConn.getSignaturesForAddress(matchPda, { limit: 10 });
    for (const s of sigs) {
      const t = await erConn.getTransaction(s.signature, { maxSupportedTransactionVersion: 0 });
      if (t?.meta?.logMessages?.some((x) => x.includes("Instruction: DealCallback"))) { dealtSig = s.signature; break; }
    }
  }
  rows.push({ step: "deal_callback (VRF oracle, scoped identity)", layer: "ER", sig: dealtSig || "(not found)", ms: timings.vrfCallbackMs, note: "time from request_deal send" });
  const seed = Buffer.from(dealt.vrfSeed);
  const expected = deriveDeal(seed);
  const parity = JSON.stringify(expected) === JSON.stringify({ maps: dealt.dealMaps, variants: dealt.dealVariants, modifiers: dealt.dealModifiers });
  console.log(`  ✓ dealt in ${timings.vrfCallbackMs} ms; seed ${seed.toString("hex")}; maps ${dealt.dealMaps} variants ${dealt.dealVariants} modifiers ${dealt.dealModifiers}; TS deal parity ${parity}`);
  if (!parity) throw new Error("on-chain deal != deal.ts");
  void reqSig;

  // 4. round 1 events
  let seq = dealt.eventSeq as number;
  const gp = { authority: ga.publicKey, matchAccount: matchPda };
  const erTimes: number[] = [];
  const erStep = async (step: string, fn: () => Promise<string>) => {
    const t0 = Date.now();
    await record(step, "ER", fn);
    erTimes.push(Date.now() - t0);
    seq += 1;
  };
  await erStep(`start_round(1) seq ${seq}`, () => er.methods.startRound(1, seq).accountsPartial(gp).rpc());
  const target = roundTargets(PLAYERS)[0];
  for (let slot = 0; slot < PLAYERS; slot++) {
    if (slot < target) await erStep(`record_qualified slot ${slot} seq ${seq}`, () => er.methods.recordQualified(1, slot, slot + 1, seq).accountsPartial(gp).rpc());
    else await erStep(`record_eliminated slot ${slot} seq ${seq}`, () => er.methods.recordEliminated(1, slot, 1, seq).accountsPartial(gp).rpc());
  }
  await snapshot("after round-1 events (ER)", erConn, er, matchPda);

  // 5. complete_round + commit
  const tCommit = Date.now();
  const crSig = await record(`complete_round(1) + commit seq ${seq}`, "ER", () => er.methods.completeRound(1, seq).accountsPartial({ authority: ga.publicKey, matchAccount: matchPda }).rpc());
  seq += 1;
  const commitSigP = GetCommitmentSignature(crSig, erConn).then((s) => ((timings.getCommitmentSignatureMs = Date.now() - tCommit), s)).catch((e) => `UNRESOLVED (${e.message})`);
  await pollUntil("round-1 commit on Solana", COMMIT_TIMEOUT_MS, async () => {
    const m: any = await base.account.match.fetch(matchPda, "confirmed");
    return m.currentRound === 1 && m.eventSeq === seq && statusOf(m.status) === "roundResolved" ? true : undefined;
  });
  timings.commitLandedMs = Date.now() - tCommit;
  const commitSig = await Promise.race([commitSigP, sleep(90_000).then(() => "UNRESOLVED (timeout 90 s)")]);
  rows.push({ step: "commit (round 1) landed on Solana", layer: "Solana", sig: commitSig, ms: timings.commitLandedMs, note: "via GetCommitmentSignature; ms = until Solana account showed round 1" });
  console.log(`  [Solana] commit ${commitSig} (state visible on Solana after ${timings.commitLandedMs} ms)`);
  await snapshot("after commit (Solana, still delegated)", baseConn, base, matchPda);

  // 6. finalize: commit + undelegate (M0 shortcut from RoundResolved)
  const erFinal: any = await er.account.match.fetch(matchPda, "confirmed");
  const tFin = Date.now();
  const finSig = await record("finalize_match (commit+undelegate)", "ER", () => er.methods.finalizeMatch().accountsPartial({ authority: ga.publicKey, matchAccount: matchPda }).rpc());
  const finCommitP = GetCommitmentSignature(finSig, erConn).catch((e) => `UNRESOLVED (${e.message})`);
  await pollUntil("owner back to fall_rush", COMMIT_TIMEOUT_MS, async () => {
    const info = await baseConn.getAccountInfo(matchPda, "confirmed");
    return info && info.owner.equals(programId) ? true : undefined;
  });
  timings.undelegateLandedMs = Date.now() - tFin;
  const finCommit = await Promise.race([finCommitP, sleep(90_000).then(() => "UNRESOLVED (timeout 90 s)")]);
  rows.push({ step: "commit + undelegate landed on Solana", layer: "Solana", sig: finCommit, ms: timings.undelegateLandedMs, note: "ms = until base owner was fall_rush again" });
  const finalBase: any = await base.account.match.fetch(matchPda, "confirmed");
  const same = JSON.stringify(finalBase) === JSON.stringify(erFinal);
  console.log(`  ✓ owner = fall_rush after ${timings.undelegateLandedMs} ms; final Solana state equals ER state: ${same}`);
  await snapshot("final (Solana)", baseConn, base, matchPda);
  if (!same) throw new Error("final Solana state differs from ER state");

  const sorted = [...erTimes].sort((a, b) => a - b);
  timings.erConfirmP50Ms = sorted[Math.floor(sorted.length / 2)];
  timings.erConfirmMaxMs = sorted[sorted.length - 1];
  console.log("timings:", timings);
  writeProof({ programId, matchPda, matchId, seed, dealt, same });
}

function writeProof(x: { programId: PublicKey; matchPda: PublicKey; matchId: Buffer; seed: Buffer; dealt: any; same: boolean }) {
  const solLink = (s: string) => (/^[1-9A-HJ-NP-Za-km-z]{60,}$/.test(s) ? `[${s.slice(0, 10)}…](https://explorer.solana.com/tx/${s}?cluster=devnet)` : s);
  // MagicBlock docs (docs.magicblock.gg …/how-integrate-your-program/anchor) link ER txs as
  // solscan.io/tx/<sig>?cluster=custom&customUrl=<ER RPC>; shown there for devnet.magicblock.app.
  // Using it with devnet-as is UNVERIFIED, so the raw signature is listed too.
  const erLink = (s: string) => (/^[1-9A-HJ-NP-Za-km-z]{60,}$/.test(s) ? `\`${s}\` ([solscan, UNVERIFIED](https://solscan.io/tx/${s}?cluster=custom&customUrl=${encodeURIComponent(ER_RPC)}))` : s);
  const lines = [
    "# M0 devnet proof — fall_rush lifecycle",
    "",
    `Generated by \`pnpm m0:devnet\` on ${new Date().toISOString()}.`,
    "",
    "| | |",
    "|---|---|",
    `| Program | [\`${x.programId}\`](https://explorer.solana.com/address/${x.programId}?cluster=devnet) |`,
    `| Match PDA | [\`${x.matchPda}\`](https://explorer.solana.com/address/${x.matchPda}?cluster=devnet) (id \`${x.matchId.toString("hex")}\`) |`,
    `| Solana RPC | ${SOLANA_RPC} |`,
    `| ER RPC | ${ER_RPC} (validator \`${VALIDATOR}\`) |`,
    `| VRF queue | \`${VRF_QUEUE}\` |`,
    `| VRF seed | \`${x.seed.toString("hex")}\` |`,
    `| Deal | maps ${x.dealt.dealMaps.join(",")} · variants ${x.dealt.dealVariants.join(",")} · modifiers ${x.dealt.dealModifiers.join(",")} (matches \`deal.ts\`) |`,
    `| Final Solana state == ER state | ${x.same} |`,
    "",
    "**ER transactions:** the explorer pattern MagicBlock documents is Solscan with a custom RPC (`?cluster=custom&customUrl=<ER RPC>`), shown in their docs for `devnet.magicblock.app`. For `devnet-as` it is **UNVERIFIED**; the raw signature can always be checked with `getTransaction` against the ER RPC above.",
    "",
    "| # | Step | Layer | Signature | ms | Note |",
    "|---|---|---|---|---:|---|",
    ...rows.map((r, i) => `| ${i + 1} | ${r.step} | ${r.layer} | ${r.layer === "Solana" ? solLink(r.sig) : erLink(r.sig)} | ${r.ms} | ${r.note ?? ""} |`),
    "",
    "## Measured latencies",
    "",
    "| Metric | ms |",
    "|---|---:|",
    ...Object.entries(timings).map(([k, v]) => `| ${k} | ${v} |`),
    "",
    "_ER confirm = send → `getSignatureStatuses` reports `confirmed`, measured from this machine; includes network RTT._",
    "",
  ];
  mkdirSync(join(ROOT, "docs/proof"), { recursive: true });
  writeFileSync(join(ROOT, "docs/proof/M0-devnet.md"), lines.join("\n"));
  console.log("wrote docs/proof/M0-devnet.md");
}

main().catch((e) => {
  console.error("FAILED:", e);
  if (rows.length) console.error("signatures so far:", rows);
  process.exit(1);
});
