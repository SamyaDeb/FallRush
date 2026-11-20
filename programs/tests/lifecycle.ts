/**
 * M0 lifecycle test on the local MagicBlock stack (mb-stack + 2 vrf-oracles).
 * Structure follows magicblock-examples (counter/anchor, roll-dice-delegated): one base-layer
 * provider, one ER provider, env from scripts/local-env.sh. Run via `pnpm m0:test:local`.
 */
import * as anchor from "@coral-xyz/anchor";
import { AnchorError, AnchorProvider, Program, Wallet } from "@coral-xyz/anchor";
import { Connection, Keypair, LAMPORTS_PER_SOL, PublicKey } from "@solana/web3.js";
import { GetCommitmentSignature } from "@magicblock-labs/ephemeral-rollups-sdk";
import { expect } from "chai";
import { randomBytes } from "node:crypto";
import idlJson from "../target/idl/fall_rush.json";
import type { FallRush } from "../target/types/fall_rush";
import {
  DELEGATION_PROGRAM_ID,
  NO_SLOT,
  TAG,
  deriveDeal,
  mapPoolV1Hash,
  roundTargets,
  rulesetV1Hash,
  transcriptInit,
  transcriptNext,
} from "../../packages/shared/src";

const BASE_URL = process.env.PROVIDER_ENDPOINT || "http://localhost:8899";
const BASE_WS = process.env.WS_ENDPOINT || "ws://localhost:8900";
const ER_URL = process.env.EPHEMERAL_PROVIDER_ENDPOINT || "http://localhost:7799";
const ER_WS = process.env.EPHEMERAL_WS_ENDPOINT || "ws://localhost:7800";
const VALIDATOR = new PublicKey(process.env.VALIDATOR || "mAGicPQYBMvcYveUZA5F5UNNwyHvfYh5xkLS2Fr1mev");
const VRF_QUEUE = new PublicKey(process.env.VRF_EPHEMERAL_QUEUE || "Sc9MJUngNbQXSXGP3F67KvKwVnhaYn6kcioxXNVowYT");
const VRF_TIMEOUT_MS = 30_000;
const COMMIT_TIMEOUT_MS = 60_000;

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
const statusOf = (s: object) => Object.keys(s)[0];

/** Asserts the promise rejects with the named Anchor error code (custom or framework). */
async function expectError(p: Promise<unknown>, code: string) {
  let err: any;
  try {
    await p;
  } catch (e) {
    err = e;
  }
  if (!err) throw new Error(`expected ${code}, but the transaction succeeded`);
  const logs: string[] = err.logs ?? err.transactionLogs ?? err.simulationResponse?.logs ?? [];
  const parsed = err instanceof AnchorError ? err : logs.length ? AnchorError.parse(logs) : null;
  const got = parsed?.error.errorCode.code ?? err.error?.errorCode?.code;
  if (got === code) return;
  const text = `${err}\n${logs.join("\n")}`;
  if (text.includes(`Error Code: ${code}.`)) return;
  throw new Error(`expected ${code}, got ${got ?? "(unparsed)"}:\n${text.slice(0, 3000)}`);
}

async function pollUntil<T>(what: string, timeoutMs: number, fn: () => Promise<T | undefined>): Promise<T> {
  const start = Date.now();
  for (;;) {
    const v = await fn().catch(() => undefined);
    if (v !== undefined) return v;
    if (Date.now() - start > timeoutMs) throw new Error(`timeout after ${timeoutMs}ms waiting for ${what}`);
    await sleep(500);
  }
}

/** Normalises decoded accounts (BN, PublicKey, number[]) for deep equality. */
const plain = (x: unknown) => JSON.parse(JSON.stringify(x));

describe("fall_rush M0 lifecycle (local MagicBlock stack)", () => {
  const admin = Wallet.local(); // upgrade authority of the preloaded program
  const gameAuthority = Keypair.generate();
  const attacker = Keypair.generate();

  const baseConn = new Connection(BASE_URL, { wsEndpoint: BASE_WS, commitment: "confirmed" });
  const erConn = new Connection(ER_URL, { wsEndpoint: ER_WS, commitment: "confirmed" });

  const adminProgram = new Program<FallRush>(idlJson as any, new AnchorProvider(baseConn, admin, { commitment: "confirmed" }));
  const base = new Program<FallRush>(idlJson as any, new AnchorProvider(baseConn, new Wallet(gameAuthority), { commitment: "confirmed" }));
  const er = new Program<FallRush>(idlJson as any, new AnchorProvider(erConn, new Wallet(gameAuthority), { commitment: "confirmed" }));
  const attackerBase = new Program<FallRush>(idlJson as any, new AnchorProvider(baseConn, new Wallet(attacker), { commitment: "confirmed" }));
  const programId = base.programId;

  const [configPda] = PublicKey.findProgramAddressSync([Buffer.from("config")], programId);
  const matchId = randomBytes(16);
  const rosterHash = randomBytes(32);
  const [matchPda] = PublicKey.findProgramAddressSync([Buffer.from("match"), matchId], programId);
  const PLAYERS = 12;

  // Off-chain mirror of the rolling transcript (doc 06 §3).
  let transcript = transcriptInit(matchId, rosterHash);
  let seq = 0;
  const mirror = (tag: number, round: number, slot: number, arg: number[] | Buffer = []) => {
    transcript = transcriptNext(transcript, tag, seq, round, slot, Buffer.from(arg));
    seq += 1;
  };

  const timings: Record<string, number> = {};
  const gameplay = { authority: gameAuthority.publicKey, matchAccount: matchPda };

  before(async () => {
    console.log(`  base ${BASE_URL} | ER ${ER_URL} | validator ${VALIDATOR} | queue ${VRF_QUEUE}`);
    console.log(`  program ${programId} | match ${matchPda} | game authority ${gameAuthority.publicKey}`);
    for (const kp of [gameAuthority, attacker]) {
      const sig = await baseConn.requestAirdrop(kp.publicKey, 2 * LAMPORTS_PER_SOL);
      await baseConn.confirmTransaction(sig, "confirmed");
    }
  });

  after(() => console.log("  timings (ms):", timings));

  it("initialize_config rejects foreign hashes and non-upgrade-authority admins", async () => {
    const good = {
      ga: gameAuthority.publicKey,
      rv: 1,
      rh: [...rulesetV1Hash()],
      mv: 1,
      mh: [...mapPoolV1Hash()],
    };
    const programData = PublicKey.findProgramAddressSync([programId.toBuffer()], new PublicKey("BPFLoaderUpgradeab1e11111111111111111111111"))[0];
    await expectError(
      adminProgram.methods.initializeConfig(good.ga, good.rv, good.rh, good.mv, [...randomBytes(32)]).accountsPartial({ admin: admin.publicKey, programData }).rpc(),
      "InvalidMapPool",
    );
    await expectError(
      attackerBase.methods.initializeConfig(good.ga, good.rv, good.rh, good.mv, good.mh).accountsPartial({ admin: attacker.publicKey, programData }).rpc(),
      "Unauthorized",
    );
    await adminProgram.methods.initializeConfig(good.ga, good.rv, good.rh, good.mv, good.mh).accountsPartial({ admin: admin.publicKey, programData }).rpc();
    const cfg = await base.account.gameConfig.fetch(configPda);
    expect(cfg.gameAuthority.toBase58()).to.eq(gameAuthority.publicKey.toBase58());
    expect(Buffer.from(cfg.mapPoolHash).equals(mapPoolV1Hash())).to.eq(true);
    expect(Buffer.from(cfg.rulesetHash).equals(rulesetV1Hash())).to.eq(true);
  });

  it("create_match derives round targets on-chain and marks slots alive", async () => {
    await expectError(base.methods.createMatch([...matchId], [...rosterHash], 3).accountsPartial({ authority: gameAuthority.publicKey }).rpc(), "InvalidPlayerCount");
    await expectError(
      attackerBase.methods.createMatch([...matchId], [...rosterHash], PLAYERS).accountsPartial({ authority: attacker.publicKey }).rpc(),
      "Unauthorized",
    );
    await base.methods.createMatch([...matchId], [...rosterHash], PLAYERS).accountsPartial({ authority: gameAuthority.publicKey }).rpc();
    const m = await base.account.match.fetch(matchPda);
    expect(statusOf(m.status)).to.eq("created");
    expect(m.roundTargets).to.deep.eq(roundTargets(PLAYERS));
    expect(m.slots.map((s) => s.state)).to.deep.eq([...Array(20)].map((_, i) => (i < PLAYERS ? 1 : 0)));
    expect(m.eventSeq).to.eq(0);
    expect(Buffer.from(m.transcriptHash).equals(transcript)).to.eq(true);
    expect(m.championSlot).to.eq(NO_SLOT);
  });

  it("delegate_match: base owner becomes the Delegation Program", async () => {
    const t0 = Date.now();
    const sig = await base.methods
      .delegateMatch([...matchId])
      .accountsPartial({ authority: gameAuthority.publicKey, matchAccount: matchPda })
      .remainingAccounts([{ pubkey: VALIDATOR, isSigner: false, isWritable: false }])
      .rpc();
    timings.delegate = Date.now() - t0;
    console.log(`    [Solana] delegate_match ${sig}`);
    const info = await baseConn.getAccountInfo(matchPda, "confirmed");
    expect(info!.owner.toBase58()).to.eq(DELEGATION_PROGRAM_ID);
    // The ER clones the account on first use; wait until it serves it.
    await pollUntil("match on ER", 20_000, async () => ((await erConn.getAccountInfo(matchPda)) ? true : undefined));
  });

  it("deal_callback called directly by a normal signer fails (scoped VRF identity)", async () => {
    await expectError(
      er.methods
        .dealCallback([...randomBytes(32)])
        .accountsPartial({ vrfProgramIdentity: attacker.publicKey, matchAccount: matchPda })
        .signers([attacker])
        .rpc(),
      "ConstraintAddress",
    );
  });

  it("request_deal on the ER is fulfilled by the VRF callback within 30 s", async () => {
    let resolveDealt!: (v: any) => void;
    const dealt = new Promise<any>((r) => (resolveDealt = r));
    // Subscribe before sending (doc 07 §5).
    const sub = erConn.onAccountChange(
      matchPda,
      (acc) => {
        const m = er.coder.accounts.decode("match", acc.data);
        if (statusOf(m.status) === "dealt") resolveDealt(m);
      },
      { commitment: "processed" },
    );
    try {
      const t0 = Date.now();
      const sig = await er.methods.requestDeal().accountsPartial({ authority: gameAuthority.publicKey, matchAccount: matchPda, oracleQueue: VRF_QUEUE }).rpc();
      timings.requestDealConfirm = Date.now() - t0;
      console.log(`    [ER] request_deal ${sig}`);
      const m = await Promise.race([dealt, sleep(VRF_TIMEOUT_MS).then(() => null)]);
      timings.vrfCallback = Date.now() - t0;
      expect(m, "VRF callback not observed within 30 s").to.not.eq(null);

      const seed = Buffer.from(m.vrfSeed);
      expect(seed.equals(Buffer.alloc(32))).to.eq(false);
      const expected = deriveDeal(seed);
      expect(m.dealMaps).to.deep.eq(expected.maps);
      expect(m.dealVariants).to.deep.eq(expected.variants);
      expect(m.dealModifiers).to.deep.eq(expected.modifiers);
      m.dealMaps.forEach((x: number) => expect(x).to.be.within(0, 3));
      m.dealVariants.forEach((x: number) => expect(x).to.be.within(0, 1));
      m.dealModifiers.forEach((x: number) => expect(x).to.be.within(0, 5));
      mirror(TAG.DEAL, 0, NO_SLOT, seed);
      expect(m.eventSeq).to.eq(seq);
      expect(Buffer.from(m.transcriptHash).equals(transcript)).to.eq(true);
    } finally {
      await erConn.removeAccountChangeListener(sub);
    }
  });

  it("round 1: sequence, signer and count rules are enforced", async () => {
    await er.methods.startRound(1, seq).accountsPartial(gameplay).rpc();
    mirror(TAG.START_ROUND, 1, NO_SLOT);

    // duplicate and skipped seq
    await expectError(er.methods.startRound(1, seq - 1).accountsPartial(gameplay).rpc(), "InvalidSequence");
    await expectError(er.methods.recordQualified(1, 0, 1, seq + 1).accountsPartial(gameplay).rpc(), "InvalidSequence");
    // wrong signer on a gameplay instruction (fee payer is the game authority, attacker signs as authority)
    await expectError(
      er.methods.recordQualified(1, 0, 1, seq).accountsPartial({ authority: attacker.publicKey, matchAccount: matchPda }).signers([attacker]).rpc(),
      "Unauthorized",
    );
    // slot outside the roster
    await expectError(er.methods.recordQualified(1, 15, 1, seq).accountsPartial(gameplay).rpc(), "InvalidSlot");

    const target = roundTargets(PLAYERS)[0]; // 8
    for (let slot = 0; slot < target; slot++) {
      await er.methods.recordQualified(1, slot, slot + 1, seq).accountsPartial(gameplay).rpc();
      mirror(TAG.QUALIFIED, 1, slot, [slot + 1]);
    }
    await expectError(er.methods.recordQualified(1, target, target + 1, seq).accountsPartial(gameplay).rpc(), "TargetExceeded");
    await expectError(er.methods.completeRound(1, seq).accountsPartial({ authority: gameAuthority.publicKey, matchAccount: matchPda }).rpc(), "RoundNotResolved");
    for (let slot = target; slot < PLAYERS; slot++) {
      await er.methods.recordEliminated(1, slot, 1, seq).accountsPartial(gameplay).rpc();
      mirror(TAG.ELIMINATED, 1, slot, [1]);
    }
    await expectError(er.methods.recordQualified(1, target, 1, seq).accountsPartial(gameplay).rpc(), "SlotNotAlive");
    await expectError(er.methods.startRound(2, seq).accountsPartial(gameplay).rpc(), "InvalidStatus");
  });

  it("complete_round commits; Solana shows the new round state once the commit lands", async () => {
    const t0 = Date.now();
    const sig = await er.methods.completeRound(1, seq).accountsPartial({ authority: gameAuthority.publicKey, matchAccount: matchPda }).rpc();
    timings.completeRoundErConfirm = Date.now() - t0;
    mirror(TAG.COMPLETE_ROUND, 1, NO_SLOT, [roundTargets(PLAYERS)[0]]);
    const expectedSeq = seq;
    const commitSig = await GetCommitmentSignature(sig, erConn);
    console.log(`    [ER] complete_round(1) ${sig}\n    [Solana] commit ${commitSig}`);
    const m = await pollUntil("committed round 1 on Solana", COMMIT_TIMEOUT_MS, async () => {
      const acc = await base.account.match.fetch(matchPda, "confirmed");
      return acc.currentRound === 1 && acc.eventSeq === expectedSeq ? acc : undefined;
    });
    timings.commitLandedOnSolana = Date.now() - t0;
    expect(statusOf(m.status)).to.eq("roundResolved");
    expect(Buffer.from(m.transcriptHash).equals(transcript)).to.eq(true);
    const info = await baseConn.getAccountInfo(matchPda, "confirmed");
    expect(info!.owner.toBase58()).to.eq(DELEGATION_PROGRAM_ID); // still delegated
  });

  it("rounds 2–4 and complete_match crown exactly one champion", async () => {
    await expectError(er.methods.startRound(3, seq).accountsPartial(gameplay).rpc(), "InvalidRound");
    await expectError(er.methods.completeMatch(seq).accountsPartial(gameplay).rpc(), "InvalidChampion");
    const targets = roundTargets(PLAYERS);
    let alive = [...Array(targets[0]).keys()];
    for (let round = 2; round <= 4; round++) {
      await er.methods.startRound(round, seq).accountsPartial(gameplay).rpc();
      mirror(TAG.START_ROUND, round, NO_SLOT);
      const target = targets[round - 1];
      const [winners, losers] = [alive.slice(0, target), alive.slice(target)];
      for (const [i, slot] of winners.entries()) {
        await er.methods.recordQualified(round, slot, i + 1, seq).accountsPartial(gameplay).rpc();
        mirror(TAG.QUALIFIED, round, slot, [i + 1]);
      }
      for (const slot of losers) {
        await er.methods.recordEliminated(round, slot, 2, seq).accountsPartial(gameplay).rpc();
        mirror(TAG.ELIMINATED, round, slot, [2]);
      }
      const sig = await er.methods.completeRound(round, seq).accountsPartial({ authority: gameAuthority.publicKey, matchAccount: matchPda }).rpc();
      mirror(TAG.COMPLETE_ROUND, round, NO_SLOT, [winners.length]);
      console.log(`    [ER] complete_round(${round}) ${sig}\n    [Solana] commit ${await GetCommitmentSignature(sig, erConn)}`);
      alive = winners;
    }
    await er.methods.completeMatch(seq).accountsPartial(gameplay).rpc();
    mirror(TAG.COMPLETE_MATCH, 4, alive[0]);
    const m = await er.account.match.fetch(matchPda);
    expect(statusOf(m.status)).to.eq("completed");
    expect(m.championSlot).to.eq(alive[0]);
    expect(Buffer.from(m.transcriptHash).equals(transcript)).to.eq(true);
  });

  it("finalize_match: commit + undelegate returns ownership to fall_rush with final values", async () => {
    const erState = plain(await er.account.match.fetch(matchPda));
    const t0 = Date.now();
    const sig = await er.methods.finalizeMatch().accountsPartial({ authority: gameAuthority.publicKey, matchAccount: matchPda }).rpc();
    const commitSig = await GetCommitmentSignature(sig, erConn);
    console.log(`    [ER] finalize_match ${sig}\n    [Solana] commit+undelegate ${commitSig}`);
    await pollUntil("owner back to fall_rush", COMMIT_TIMEOUT_MS, async () => {
      const info = await baseConn.getAccountInfo(matchPda, "confirmed");
      return info && info.owner.equals(programId) ? true : undefined;
    });
    timings.undelegateLandedOnSolana = Date.now() - t0;
    const finalBase = plain(await base.account.match.fetch(matchPda, "confirmed"));
    expect(finalBase).to.deep.eq(erState);
    expect(statusOf(finalBase.status)).to.eq("completed");
  });

  it("abort_match: admin can abort on Solana; aborting twice fails; strangers cannot", async () => {
    const id2 = randomBytes(16);
    const [pda2] = PublicKey.findProgramAddressSync([Buffer.from("match"), id2], programId);
    await base.methods.createMatch([...id2], [...randomBytes(32)], 4).accountsPartial({ authority: gameAuthority.publicKey }).rpc();
    await expectError(attackerBase.methods.abortMatch(9).accountsPartial({ authority: attacker.publicKey, matchAccount: pda2 }).rpc(), "Unauthorized");
    await adminProgram.methods.abortMatch(9).accountsPartial({ authority: admin.publicKey, matchAccount: pda2 }).rpc();
    expect(statusOf((await base.account.match.fetch(pda2)).status)).to.eq("aborted");
    await expectError(adminProgram.methods.abortMatch(9).accountsPartial({ authority: admin.publicKey, matchAccount: pda2 }).rpc(), "AlreadyFinal");
  });
});
