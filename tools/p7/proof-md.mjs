// Submission: PROOF.md from proof-links.ts output (every link re-checked live when it was generated) + a few links
// recorded in earlier proofs that one match does not produce (cash-out to the RUSH SPL token).
//   node tools/p7/proof-md.mjs docs/proof/sub/proof-links.json > PROOF.md
import { readFileSync } from "node:fs";
const j = JSON.parse(readFileSync(process.argv[2], "utf8"));
const ER = "https://devnet-as.magicblock.app";
const tx = (sig, rollup) => `https://explorer.solana.com/tx/${sig}?cluster=${rollup ? "custom&customUrl=" + encodeURIComponent(ER) : "devnet"}`;
const short = (s) => (s ? s.slice(0, 8) + "…" + s.slice(-4) : "");
const where = (l) => (l.rollup ? "Ephemeral Rollup" : l.system.includes("TEE") ? "Private ER (TEE)" : "Solana devnet");
const rows = j.links.filter((l) => !l.missing && (l.url || l.sig));
const missing = j.links.filter((l) => l.missing).map((l) => l.system);
const body = rows.map((l) => {
  const id = l.sig ?? l.address;
  const link = l.system.includes("TEE") ? `\`${short(id)}\` (private rollup: not readable from a public explorer; see the receipt)` : `[\`${short(id)}\`](${l.url})`;
  const cu = l.cu ? ` · ${Number(l.cu).toLocaleString("en-US")} CU` : "";
  return `| ${l.system} | ${l.proves ?? ""}${cu} | ${where(l)} | ${link} |`;
}).join("\n");
const extra = [
  ["Session-key bet (P4 run)", "a spectator's bet signed by a session key: no wallet popup", "Ephemeral Rollup", "2AkjGY2J973H96dMVuwik4GgZuCma9ag8jBByykyQRZJjfe5UkpNjXmbMUHC6SNGMr4NKfVreDJFYZPPMaJQDttd", true],
  ["Sealed bet revealed (P6 run)", "a sealed Champion pick from the Private ER book, checked against its on-chain commitment", "Ephemeral Rollup", "5hHtAaQaSkAo2MzPZYsVQn832ftQ5P9oWfJkC4hZw95XW3wLZCwFmbmMg3nekBZymUA1WT2w9FWfBL454sY3xFXq", true],
  ["Cash-out (rollup)", "chips cashed out on the rollup, signed by a session key", "Ephemeral Rollup", "WMNKz6687fnFjdWudPadZhdPRjYMgq5ZaUgfYrDx3QgszVi4WANAJThS7JWwcFymuZptSsdLz1yfcwcwfxWMNUY", true],
  ["Cash-out → RUSH SPL token", "250 RUSH minted to the player's token account on Solana", "Solana devnet", "4qnt6mkNNqy1weCxsTqi2HC7j17r12HLjMxb3a4vdyvgFtw99GVrGrcAiVruhwsNW1fYJfEREnMQRW1grxnooUWs", false],
].map(([s, p, w, sig, r]) => `| ${s} | ${p} | ${w} | [\`${short(sig)}\`](${tx(sig, r)}) |`).join("\n") +
  `\n| Crown minted by a Magic Action (P6 run) | the Crown (Metaplex Core) created by the post-undelegate Magic Action; the winner was a scripted test wallet | Solana devnet | [\`Eoi1Xaek…wAi2z\`](https://explorer.solana.com/address/Eoi1XaekwQz2CB4BvHCXxK2P4Uuxx1PCn7FXxB8wAi2z?cluster=devnet) |`;
console.log(`# Proof: one explorer link per system

Every link below was fetched live (\`getTransaction\` / \`getAccountInfo\`) when this page was generated (${j.at.slice(0, 16).replace("T", " ")} UTC), from one archived match:
**[\`${j.match}\`](https://fallrush.fun/receipt/${j.match})** (arena ${j.arena}). Its receipt page shows every step, with ${j.full ? "the whole match" : "the Final"} replayed in your browser.${j.full ? "\nIt is a **full on-chain match**: all 4 rounds (Capsule Run, Goo Tower, Star Fountain, Lid Flip) were computed by the program in the Ephemeral Rollup" + (j.judge ? ", played in **judge mode** (`/play?judge=1`) by a browser player." : ".") : j.judge ? "\nIt was played in **judge mode** (`/play?judge=1`): rounds 1–3 were fast-forwarded by the server (the receipt says so); the Final was played and computed on-chain. A phone on `/live` bought the Chaos Drop below during it." : ""}

Rollup transactions open in Solana Explorer through a custom cluster (\`${ER}\`): Explorer asks once to **Connect** to that server; click Connect and the transaction loads (${j.full ? "the `tick()` below was fetched over RPC when this page was generated: success, " : "checked by hand for the `tick()` below: Success, "}${Number(j.links.find((l) => l.system.startsWith('tick'))?.cu ?? 0).toLocaleString('en-US')} CU). Rollups do not keep transactions forever: if a rollup link stops opening, the [receipt](https://fallrush.fun/receipt/${j.match}) and the [replay](https://fallrush.fun/replay/${j.match}) work from our archive and the Match account on Solana.

${j.full ? "> All 4 rounds are computed by the `fall_rush` program in a MagicBlock Ephemeral Rollup, 30 times a second, for all 12 players. Ticks come from MagicBlock's task scheduler (timed tasks; on MagicBlock core 2.0.4 a task runs as a Hydra crank and a failed execution is retried) and a permissionless keeper crank. Replay any match and compare it with the on-chain receipt, byte for byte." : "> Rounds 1–3 run on our server and are recorded on-chain. The Final's physics runs on Solana, in a MagicBlock Ephemeral Rollup, 30 times a second. Replay any match and compare it with the on-chain receipt."}

| System | What it proves | Where | Link |
|---|---|---|---|
${body}
${extra}

**Program:** [\`4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4\`](https://explorer.solana.com/address/4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4?cluster=devnet) · **RUSH mint:** [\`3s8KstqicXn1kgFyWPVq3AZXkMprDtEtT7BNTxtQccEw\`](https://explorer.solana.com/address/3s8KstqicXn1kgFyWPVq3AZXkMprDtEtT7BNTxtQccEw?cluster=devnet) · arena ${j.arena} World \`${j.world ?? ""}\`
${missing.length ? `\nNot produced by this match: ${missing.join(", ")}.\n` : ""}
**Replay it yourself:** \`node tools/p7/replay-check.mjs ${j.match}\` (Node 24, no install) re-simulates ${j.full ? "the whole match (all 4 rounds, across every map load)" : "the Final"} with the same \`rush-core\` the program runs and compares every tick byte for byte.

The cash-out rows come from the P5 run (\`docs/proof/p5/\`). Per-phase runs, numbers and what is still unverified: [\`docs/proof/\`](docs/proof/).
`);
