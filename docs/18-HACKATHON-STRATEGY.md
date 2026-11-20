# 18 — Hackathon Strategy: MagicBlock Solana Blitz V10

**Status:** v2 (2026-11-18). Research sources are listed in §7. Everything marked VERIFIED was checked on 2026-11-18.

---

## 1. The event (VERIFIED)

| Item | Value | Source |
|---|---|---|
| Theme | **Entertainment Finance**: "apps where you trade, collect, compete and win assets you actually own" | luma.com/hedv5sgh |
| Window | **2026-11-20 14:00 UTC → 2026-11-27 14:00 UTC** (7 days, online). The submission deadline is the end of the window. | build.magicblock.app/api/events (id 17), Luma |
| Deadline in IST | **Fri 27 Nov 2026, 19:30 IST** | derived |
| Eligibility | "Every submission must integrate MagicBlock's Ephemeral Rollup **or** VRF." We integrate **both**, plus Session Keys. | build.magicblock.app panels |
| Judging | "creativity, technical depth, and how compellingly they showcase what's possible on Solana". An earlier edition said "depth of integration, creativity, and polish". | panels; MagicBlock X results thread |
| Prizes | 500 / 250 / 150 USDC, plus Wizardio's Choice 100 | Luma |
| Required submission | **Public GitHub repo**, pitch + demo, **explorer link as integration proof**, project description, wallet for prizes | build.magicblock.app/?stage=blitz#submit |
| Optional submission | Program address (we will provide it), X handle | same |
| Team | Solo allowed. No stated maximum. | FAQ |
| Pre-existing code | Not stated. A past winner was "three years in the making". **We disclose that the planning docs predate Nov 20.** | inference |

**Action:** register on Luma before Nov 20, and join the kickoff livestream (Nov 20, 14:00 UTC).

## 2. What wins (evidence and inference)

**Evidence (VERIFIED from results threads):**
- **V9, 1st place:** *Battle Cities*, "real-time tank combat built in Unity", with VRF power-ups.
- **V9, 2nd place:** a 20-tick battle royale in a Private ER.
- **V8:** 3 of 4 winners were real-time multiplayer games, combining ER and VRF:
  - a party game with sealed answers (1st);
  - an 8-player survival Pong (2nd).
- **Early editions:** a winner used **Session keys for gasless gameplay + full ER delegation lifecycle**.
- **Every winner** had a public playable or demo URL.

**Inference:**
1. A **playable multiplayer game from a URL** is the strongest format. Judges must be able to click and play. → **WebGL build** (decision D-4 revised, doc 15).
2. **Depth of integration:** ER + VRF + Session Keys, with the full lifecycle visible on the explorer.
3. **The ER must visibly matter.** The research flagged that "server-authoritative Unity physics may look less onchain". Our answer:
   - The **match itself lives on the rollup**: deal, rounds, every knockout, fan picks and the champion.
   - A **Live Rollup Feed** on the web shows it happening in real time, directly from the ER RPC.
   - Each round's result is **committed to Solana between rounds**.
4. **The Entertainment Finance angle without gambling:**
   - **Compete:** a provably fair knockout.
   - **Predict:** free Fan Picks, signed by session keys on the ER.
   - **Own:** a Crown Capsule trophy as a Metaplex Core asset tied to the on-chain receipt.
   - No paid entry and no cash prizes (ADR-006).
5. **Polish matters.** Juice, characters, audio, and a 3-minute video that feels like a real game.

## 3. Our pitch

> **Fall Rush — the provably fair knockout party game.**
> 12 wind-up capsule toys. 4 chaotic rounds. One crown.
> Every map and Chaos Capsule modifier is **dealt by MagicBlock VRF**. Every knockout is **recorded live on a MagicBlock Ephemeral Rollup** and committed to Solana each round. Eliminated players keep playing with **gasless Fan Picks** via session keys. The champion owns a **Crown Capsule** that points to its on-chain match receipt.
> Play in your browser, no wallet needed.

**Honest-trust line** (said in the demo and printed in the README):
> "Physics runs on our authoritative game server; the chain proves the match setup was random, and that every result was recorded in order, by the authorised server, consistent with the rules. It doesn't prove the physics — that's our roadmap."

## 4. Integration proof checklist (submission)

- [ ] Program ID on devnet, plus the IDL
- [ ] `create_match` transaction (Solana) → explorer
- [ ] `delegate_match` transaction (Solana) → explorer: account owner = Delegation Program
- [ ] `request_deal` + VRF callback transactions (ER) → ER explorer link; the deal stored in the account
- [ ] ≥ 1 `record_qualified` / `record_eliminated` transaction (ER)
- [ ] A per-round commit transaction (Solana) showing the updated account
- [ ] `finalize_match` (commit + undelegate) → the final account on Solana, owned by our program
- [ ] A `submit_pick` transaction signed by a **session key** (ER)
- [ ] Crown Capsule asset address (Metaplex Core, devnet)
- [ ] All of the above linked from the **web receipt page**, and in `PROOF.md` in the repo

**Explorer link format:** the Solana Explorer custom-RPC URL pattern for ER transactions must be checked against the MagicBlock docs before use. If none is documented, show the transaction through our own receipt page plus the ER RPC `getTransaction`.

## 5. Demo video (3:00 script)

| Time | Shot | Voice-over / caption |
|---|---|---|
| 0:00–0:12 | Capsule machine cranks, logo, Rushlings pop out | "Fall Rush: the provably fair knockout party game." |
| 0:12–0:30 | Browser → Play as Guest → lobby fills (12 capsules drop) | "Click a link, no wallet, you're in." |
| 0:30–0:45 | **Deal screen:** VRF capsule spins → map + Chaos modifier; the web Live Feed shows the VRF callback transaction | "MagicBlock VRF deals the match, verified on-chain, same for everyone." |
| 0:45–1:25 | Round 1 race gameplay montage: bonks, dives, qualify confetti, claw elimination. Split screen with the Live Rollup Feed ticking knockouts. | "Every knockout lands on a MagicBlock Ephemeral Rollup in real time, at zero fees." |
| 1:25–1:50 | Goo Tower and Star Scramble highlights; an eliminated player makes a **Fan Pick** with no popup | "Knocked out? Back a finalist. Session keys sign it on the rollup, no popups." |
| 1:50–2:20 | Final, Crumble Shelf → champion crowned → Crown Capsule pops | "One crown." |
| 2:20–2:45 | Receipt page: Rollup ✓ → Solana ✓, explorer links, Crown Capsule asset with the `matchPda` attribute | "The receipt lives on Solana. The crown is yours." |
| 2:45–3:00 | Architecture slide + honest-trust line + "Play now: <URL>" | — |

Record **two full real matches** before Nov 26. Keep the best footage. Bots are labelled in the HUD and disclosed in the video.

## 6. Submission package

- `README.md`: pitch, play link, video link, architecture diagram, trust model, how to run locally, program ID, proof links
- `PROOF.md`: every signature from §4 with explorer links
- Public repo (MIT for code; assets per `ASSET_LICENSES.md`)
- 3-minute video (YouTube unlisted + X post)
- The deployed build stays up until at least the results announcement (results historically come 3–5 days after the deadline; inference)

## 7. Sources

- https://luma.com/hedv5sgh
- https://build.magicblock.app/?stage=blitz (and its `/api/events`, `/api/panels`)
- https://www.magicblock.xyz/blog/entertainment-finance-on-magicblock
- MagicBlock X results threads for V0, V8 and V9 (IDs recorded in the research scratch notes)
- https://docs.magicblock.gg/pages/get-started/use-cases/games
