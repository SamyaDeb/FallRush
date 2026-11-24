# Submission pack: MagicBlock Solana Blitz V10 (build.magicblock.app)

Copy each field as it is. **Leave blank for the owner: the prize wallet and the video URL.** Nothing here has been submitted or posted by the agent.

---

**Project name**
Fall Rush

**One-liner (≤ 100 chars)**
A 3D knockout party game whose Final runs on a MagicBlock Ephemeral Rollup, with live spectator markets.

**Track / theme**
Entertainment Finance

**Short description (≤ 280 chars)**
12 wind-up capsule toys race through 4 rounds; the Final runs inside a MagicBlock Ephemeral Rollup at 30 Hz. Spectators bet on live markets (with a sealed Champion market on a Private ER), buy Chaos Drops that hit the Final, and winners keep the Crown as an on-chain asset.

**Long description**
Fall Rush is an original browser party game: 12 Rushlings (wind-up capsule toys with noodle arms) play four rounds of chaos, and the last one standing wins the Crown. You click Play and you're in: no wallet, no popups. A guest wallet and a MagicBlock session key sign your Final moves, bets and Chaos Drops for you.

The Final, *Lid Flip*, is not simulated by our server. It runs inside a **MagicBlock Ephemeral Rollup**: a timed task calls our program's `tick()` 30 times a second, and `tick()` runs `rush-core`, our integer-only 3D character controller, compiled to sBPF. The program decides who falls, who wins, and settles every bet itself.

The **Entertainment Finance** layer is built around watching:
- **Rush Markets** with live odds (Qualify for rounds 1–3, Final winner, Champion), pari-mutuel, settled by the program.
- A **sealed Champion market** on a **MagicBlock Private Ephemeral Rollup (TEE)**: nobody, including us, can see a pick until betting closes; reveals are verified against commitments, with a commit-reveal fallback.
- **Chaos Drops**: spectators pay chips to drop telegraphed map-wide events (Lid Frenzy, Bumper Rain, Wind Gust, Sweeper Rush) into the live Final; they enter the same World the program steps.
- **Assets you own**: when the Match is committed back to Solana, a **Magic Action** in the undelegation transaction mints the champion's Crown Capsule and a VRF-rolled capsule for every human (Metaplex Core). Weekly Seasons and trophies live on Solana.

Every match leaves a **receipt** on Solana (Rollup ✓ → Solana ✓), and anyone can **replay** the Final from the archived inputs with the same `rush-core` code, in the browser or with one Node command, and get the same bytes.

Rounds 1–3 run on our server and are recorded on-chain. The Final's physics runs on Solana, in a MagicBlock Ephemeral Rollup, 30 times a second. Replay any match and compare it with the on-chain receipt. Devnet test chips; real money only after a legal review.

**What MagicBlock features do you use?**
Ephemeral Rollups (the Final's physics + markets + drops on devnet-as), session keys (every input, bet and drop), VRF (the Final's seed, capsule rarity), Private Ephemeral Rollup / TEE (sealed bets), Magic Actions (minting the Crown + capsules on undelegation), commit + undelegate (receipts, markets and the Match back to Solana).

**Live demo URL**
https://fallrush.fun (judges: https://fallrush.fun/play?judge=1 reaches the on-chain Final in about 30 s, then scan the QR code to bet and drop chaos from a phone)

**Demo video URL**
*(blank: the owner fills it in)*

**GitHub repository**
*(the owner's public repo URL after the push)*

**Explorer link to lead with**
One `tick()` of the on-chain Final (243,232 CU, Success; Explorer asks once to "Connect" to the rollup's RPC):
https://explorer.solana.com/tx/KT8U57NoxmHsiKYCPrQi6ihG2RoAF5Cx9cWVNW1rUFU6SFiBgTsNMNPh5KaBA8zP9iLudHxyAv1QsgBRZu8YLGq?cluster=custom&customUrl=https%3A%2F%2Fdevnet-as.magicblock.app
All other links: `PROOF.md`. The match's receipt: https://fallrush.fun/receipt/3JnhbrWGH2VV4c9Z9hwEgDwYg8QFQ2EeovFPA1vd3Uv9

**Program address (devnet)**
`4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4`

**Prize wallet (Solana address)**
*(blank: the owner fills it in)*

**Team**
*(the owner's names / handles)*

---

## X post draft (≤ 280 chars)
We built a 3D party game where the Final runs *on-chain*. 🎮
12 capsule toys, 4 rounds, 1 crown. The Final's physics runs in a @magicblock Ephemeral Rollup at 30 Hz, spectators bet + drop chaos live, and the winner keeps the Crown.
Play: https://fallrush.fun

## Thread (4)
1/ No wallet, no popups: click Play and a guest wallet + MagicBlock session key sign your Final moves, bets and Chaos Drops for you.
2/ The Final (Lid Flip) isn't on our server. A timed task calls our program's tick() 30×/s inside an Ephemeral Rollup; it runs rush-core, our integer 3D controller compiled to sBPF.
3/ Spectators bet on live markets, and the Champion market is sealed in a Private ER (TEE): nobody sees picks until close. Chaos Drops land in the same World the program steps.
4/ Every match ends with a receipt on Solana, a Crown minted by a Magic Action, and a replay anyone can run: same inputs, same bytes. Don't trust us, replay it. 🔁 <VIDEO_URL>
