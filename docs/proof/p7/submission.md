# Submission pack: MagicBlock Solana Blitz V10 (build.magicblock.app)

Copy each field as it is. **The owner fills in the `{{…}}` fields: prize wallet, video URL, team.** Nothing here has been submitted or posted by the agent. Updated 2026-11-26 for the full on-chain game (all 4 rounds on the rollup), in line with `README.md`.

---

**Project name**
Fall Rush

**One-liner (≤ 100 chars)**
A 12-player party game where every round runs on a MagicBlock Ephemeral Rollup, with live betting.

**Track / theme**
Entertainment Finance

**Short description (≤ 280 chars)**
12 wind-up capsule toys play 4 knockout rounds, and every round's physics runs in a Solana program on a MagicBlock Ephemeral Rollup at 30 Hz. Spectators bet from their phones, including a sealed market on a Private ER, drop chaos into the Final, and the champion keeps the Crown NFT.

**Long description**
Fall Rush is an original browser party game. Twelve Rushlings (wind-up capsule toys with noodle arms) play four rounds: a race, a climb away from rising goo, a star scramble, and a Final on flipping lids. The last one standing wins the Crown. You click Play and you're in, with no wallet and no popups: a guest wallet and a MagicBlock session key sign your moves, bets and Chaos Drops in the background.

Most on-chain games run on a server and write results to a chain afterwards. In Fall Rush the game itself runs on-chain. On our on-chain arenas, every round (movement, collisions, hazards, who qualifies, who falls) is computed by our program inside a **MagicBlock Ephemeral Rollup**. MagicBlock's task scheduler calls `tick()` 30 times a second, and a permissionless crank helps with the heaviest 12-player ticks. `tick()` runs `rush-core`, our integer-only 3D physics engine compiled to sBPF. Our server runs the lobby and hands the match over. After that it can stall or abort a match (an abort refunds every market), but it can't pick the winner.

The Entertainment Finance layer is built around watching:
- **Rush Markets**: 5 pool markets per match (qualify in rounds 1–3, Final winner, Champion), with live odds, settled by the program.
- A **sealed Champion market** on a **MagicBlock Private Ephemeral Rollup (TEE)**. Picks stay hidden until betting closes, the browser verifies the TEE's attestation, reveals are checked against on-chain commitments, and a commit-reveal fallback exists.
- **Chaos Drops**: spectators spend chips to drop telegraphed, map-wide events (Lid Frenzy, Bumper Rain, Wind Gust, Sweeper Rush) into the live Final. They enter the same world the program steps.
- **Assets you own**: when the Match is committed back to Solana, a **Magic Action** in the undelegation transaction mints the champion's Crown and a VRF-rolled capsule for each human (Metaplex Core). Chips cash out to the RUSH SPL token.

Every match leaves a receipt on Solana, and anyone can re-run a whole match from its archived inputs with the same `rush-core` code, in the browser or with one Node command, and get the same bytes the rollup produced. Devnet test chips only; real money only after a legal review.

**What MagicBlock features do you use?**
Ephemeral Rollups (all 4 rounds' physics at 30 Hz, markets and drops, on devnet-as; task scheduler + permissionless crank), session keys (every input, bet and drop), VRF (the match deal and capsule rarity, inside the rollup), Private Ephemeral Rollup / TEE (sealed Champion bets, attestation verified in the browser), Magic Actions (minting the Crown and capsules on undelegation), commit + undelegate (each round, then the receipt).

**Live demo URL**
https://fallrush.fun (judges: https://fallrush.fun/play?judge=1 gives you a private room and a full 4-round on-chain match, about 6–7 minutes; scan the QR code to bet and drop chaos from a phone)

**Demo video URL**
{{DEMO_VIDEO_URL}}

**GitHub repository**
https://github.com/SamyaDeb/FallRush

**Explorer link to lead with**
One `tick()` of a full on-chain match: a 12-player physics step, 207,225 CU, Success. Explorer asks once to "Connect" to the rollup's RPC.
https://explorer.solana.com/tx/4iZaSrLVAp6WT7WcXFEHXKHamQGJrJtNkQnfhA5nqHFLJVXJo1UH661y21K8xZfuVMBMSct3qojJFjuNLMw7j6YQ?cluster=custom&customUrl=https%3A%2F%2Fdevnet-as.magicblock.app
All other links: `PROOF.md`. The match's receipt: https://fallrush.fun/receipt/Hxf8ae7ymYhyNcjEhKuah8y1FHfV4fkVYkk4VqnqAYDn

**Program address (devnet)**
`4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4`

**Prize wallet (Solana address)**
{{PRIZE_WALLET}}

**Team**
{{TEAM}}

---

## X post draft (≤ 280 chars)
We built a 3D party game that runs on-chain. Every round. 🎮
12 capsule toys, 4 rounds, 1 crown. The physics runs in a @magicblock Ephemeral Rollup at 30 Hz, spectators bet and drop chaos from their phones, and the winner keeps the Crown.
Play: https://fallrush.fun

## Thread (4)
1/ No wallet, no popups: click Play, and a guest wallet plus a MagicBlock session key sign your moves, bets and Chaos Drops for you.
2/ None of the 4 rounds run on our server. Our program's tick() runs 30×/s inside an Ephemeral Rollup, executing rush-core, our integer 3D physics engine compiled to sBPF.
3/ Spectators bet on live markets. The Champion market is sealed in a Private ER (TEE), so nobody sees picks until close. Chaos Drops land in the same world the program steps.
4/ Every match ends with a receipt on Solana, a Crown minted by a Magic Action, and a replay anyone can run: same inputs, same bytes. Check it yourself. 🔁 {{DEMO_VIDEO_URL}}
