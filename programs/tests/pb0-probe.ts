/** PB0 debug probe: create + delegate a match on the local stack, then simulate ER instructions
 *  and print their program logs (the lifecycle test swallows them). Run via scripts/pb0-probe.sh. */
import { AnchorProvider, Program, Wallet } from "@coral-xyz/anchor";
import { Connection, Keypair, LAMPORTS_PER_SOL, PublicKey } from "@solana/web3.js";
import { randomBytes } from "node:crypto";
import idlJson from "../target/idl/fall_rush.json";
import type { FallRush } from "../target/types/fall_rush";
import { mapPoolV1Hash, rulesetV1Hash } from "../../packages/shared/src";

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
const VALIDATOR = new PublicKey("mAGicPQYBMvcYveUZA5F5UNNwyHvfYh5xkLS2Fr1mev");
const VRF_QUEUE = new PublicKey("Sc9MJUngNbQXSXGP3F67KvKwVnhaYn6kcioxXNVowYT");

async function main() {
  const admin = Wallet.local();
  const ga = Keypair.generate();
  const baseConn = new Connection("http://localhost:8899", "confirmed");
  const erConn = new Connection("http://localhost:7799", "confirmed");
  const adminP = new Program<FallRush>(idlJson as any, new AnchorProvider(baseConn, admin, {}));
  const base = new Program<FallRush>(idlJson as any, new AnchorProvider(baseConn, new Wallet(ga), {}));
  const er = new Program<FallRush>(idlJson as any, new AnchorProvider(erConn, new Wallet(ga), {}));
  const pid = base.programId;
  await baseConn.confirmTransaction(await baseConn.requestAirdrop(ga.publicKey, 2 * LAMPORTS_PER_SOL), "confirmed");
  const programData = PublicKey.findProgramAddressSync([pid.toBuffer()], new PublicKey("BPFLoaderUpgradeab1e11111111111111111111111"))[0];
  await adminP.methods.initializeConfig(ga.publicKey, 1, [...rulesetV1Hash()], 1, [...mapPoolV1Hash()]).accountsPartial({ admin: admin.publicKey, programData }).rpc();
  const matchId = randomBytes(16);
  const [matchPda] = PublicKey.findProgramAddressSync([Buffer.from("match"), matchId], pid);
  await base.methods.createMatch([...matchId], [...randomBytes(32)], 12).accountsPartial({ authority: ga.publicKey }).rpc();
  await base.methods.delegateMatch([...matchId]).accountsPartial({ authority: ga.publicKey, matchAccount: matchPda })
    .remainingAccounts([{ pubkey: VALIDATOR, isSigner: false, isWritable: false }]).rpc();
  for (let i = 0; i < 40 && !(await erConn.getAccountInfo(matchPda)); i++) await sleep(500);
  console.log("match on ER:", !!(await erConn.getAccountInfo(matchPda)));
  const progOnEr = await erConn.getAccountInfo(pid);
  console.log("program on ER: owner", progOnEr?.owner.toBase58(), "executable", progOnEr?.executable, "len", progOnEr?.data.length);
  const sim = async (label: string, txp: Promise<any>) => {
    const tx = await txp;
    tx.feePayer = ga.publicKey;
    tx.recentBlockhash = (await erConn.getLatestBlockhash()).blockhash;
    tx.sign(ga);
    const r = await erConn.simulateTransaction(tx);
    console.log(`--- ${label}: err=${JSON.stringify(r.value.err)} units=${r.value.unitsConsumed}`);
    for (const l of r.value.logs ?? []) console.log("   ", l);
  };
  const atk = Keypair.generate();
  {
    const tx = await er.methods.dealCallback([...randomBytes(32)]).accountsPartial({ vrfProgramIdentity: atk.publicKey, matchAccount: matchPda }).transaction();
    tx.feePayer = ga.publicKey; tx.recentBlockhash = (await erConn.getLatestBlockhash()).blockhash; tx.sign(ga, atk);
    const r = await erConn.simulateTransaction(tx);
    console.log(`--- deal_callback(attacker): err=${JSON.stringify(r.value.err)} units=${r.value.unitsConsumed}`);
    for (const l of r.value.logs ?? []) console.log("   ", l);
  }
  await sim("start_round(1,0)", er.methods.startRound(1, 0).accountsPartial({ authority: ga.publicKey, matchAccount: matchPda }).transaction());
  await sim("request_deal", er.methods.requestDeal().accountsPartial({ authority: ga.publicKey, matchAccount: matchPda, oracleQueue: VRF_QUEUE }).transaction());
}
main().then(() => process.exit(0), (e) => { console.error(e); process.exit(1); });
