// Saves each run's final Match (from Solana) as match.json next to its recording, for rush-sim replay:
//   P2_NET=devnet tsx tools/keeper/matchdump.ts docs/proof/p2/runs/<run>-m1 ...
import { PublicKey } from "@solana/web3.js";
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { pb } from "./lib.ts";
for (const dir of process.argv.slice(2)) {
  const sum = JSON.parse(readFileSync(join(dir, "summary.json"), "utf8"));
  const m: any = await pb.account.match.fetch(new PublicKey(sum.matchPda));
  const out = {
    match: sum.matchPda, status: Object.keys(m.status)[0], playerCount: m.playerCount, currentRound: m.currentRound, champion: m.championSlot,
    rosterHash: Buffer.from(m.rosterHash).toString("hex"), vrfSeed: Buffer.from(m.vrfSeed).toString("hex"), transcript: Buffer.from(m.transcriptHash).toString("hex"), eventSeq: m.eventSeq, flags: m.flags,
    slots: m.slots.slice(0, m.playerCount).map((s: any) => ({ state: s.state, round: s.round, placement: s.placement, reason: s.reason })),
  };
  writeFileSync(join(dir, "match.json"), JSON.stringify(out, null, 1));
  console.log(dir, out.status, "champion", out.champion);
}
process.exit(0);
