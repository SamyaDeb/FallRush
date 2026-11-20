# 02 — Product Requirements (Hackathon MVP)

**Status:** v2 (2026-11-18). Replaces `fall-rush-docs/02-PRD.md`. Scope fits the Blitz V10 window (doc 14).

## 1. Objective

Ship, by **Nov 27 14:00 UTC**, a browser-playable, original, 12-player, 4-round knockout party game that is:
- fun without a wallet;
- **verifiably fair and recorded through MagicBlock VRF + Ephemeral Rollups + Session Keys**, with receipts on Solana devnet.

## 2. Personas

- **Casual player:** clicks a link and plays in 10 s, no wallet.
- **Crypto-native player:** links a wallet, makes gasless Fan Picks, owns a Crown Capsule.
- **Judge:** wants to play, see the on-chain proof quickly, and understand the trust model.

## 3. Functional requirements (P0 = must ship, P1 = should ship, P2 = stretch)

| ID | Requirement | Priority |
|---|---|---|
| FR-01 | Guest identity (no signup); generated display name; cosmetic preset | P0 |
| FR-02 | Quick Play, plus private lobby with a code; min-player and countdown rules; bots fill for practice | P0 |
| FR-03 | Roster lock → on-chain match creation → delegation → **VRF deal inside the ER** → Deal screen showing map, variants, modifiers and a link | P0 |
| FR-04 | Server-authoritative movement with client prediction; jump, dive, stun, respawn; 4 maps; deterministic hazards | P0 |
| FR-05 | 4-round flow 12→8→5→3→1 with race, survival, skill and final rules (GDD §5); thresholds from the ruleset | P0 |
| FR-06 | Every qualification and elimination recorded on the ER in order; round commit to Solana; final commit + undelegate | P0 |
| FR-07 | HUD chain chip and receipt screen; **never** show "Solana ✓" before the commit is confirmed | P0 |
| FR-08 | Web: landing, `/play`, `/live/:id` (live ER feed), `/receipt/:id` with explorer links | P0 |
| FR-09 | Wallet link (Unity SDK or web) via signed nonce | P1 |
| FR-10 | Fan Picks: wallet users via session keys on the ER; guests via the DB; Oracle leaderboard | P1 |
| FR-11 | Crown Capsule (Metaplex Core) minted to a ranked champion's linked wallet | P1 |
| FR-12 | Chaos Capsule modifiers (6) | P1 (at least None + 2 must ship) |
| FR-13 | Bonk Factory as a second Round 1 map | P2 |
| FR-14 | Desktop builds | P2 |

## 4. Non-functional requirements

- **Players:** 12 per match (bots included); 2 concurrent matches on the deployment.
- **Timing:** 30 Hz server tick; 15 Hz snapshots; playable at 150 ms RTT.
- **Performance:** WebGL 60 FPS on a MacBook Air M2 (measured).
- **Chain timing:** the deal is ready in under 25 s, or the match falls back to Unranked. Chain I/O never blocks the simulation.
- **Reconnect:** a 20 s window.
- **Keys:** no secrets in client or web builds.

## 5. Acceptance criteria (demo-ready)

1. At least 8 human clients join one match from the public URL, in a repeatable test.
2. Ten consecutive bot-filled matches reach a champion with no manual intervention.
3. The server rejects a checkpoint-skip, speed hacks (fuzzed input) and a forged join ticket.
4. Every client sees the same deal and the same round outcomes.
5. The deal is written by the VRF callback; a direct call to the callback fails (test).
6. The match account is shown as delegated, updated on the ER per event, committed each round, then undelegated, with links in `PROOF.md`.
7. A failed chain operation shows `Pending` or `Unranked`, never a false "verified".
8. The README discloses bots, the server-trust model and that everything runs on devnet.
9. A 3-minute video and a public repo are submitted before Nov 27 12:00 UTC.
