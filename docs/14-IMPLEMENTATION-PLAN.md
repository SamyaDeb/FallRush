# 14 — Implementation Plan (Blitz V10 build: Nov 18 → Nov 27)

**Status:** v3 (2026-11-19): re-planned game first (Phase A), then platform (Phase B). v2 (2026-11-18) replaced the missing `14-CODEX-IMPLEMENTATION.md`.

**Owners:**
- **C** = Claude (code, integration, tests, docs)
- **U** = you (Unity editor work, art and greybox, playtest recruiting, accounts, video)

**Gate rule:** each milestone ends with its verification passing, and I report the result. I start the next milestone only after you OK it. Within a day, the milestones in that day's row are pre-approved once the plan is approved.

---

## 1. Calendar (all times UTC; deadline Fri Nov 27 14:00 UTC = 19:30 IST)

**Re-planned 2026-11-19 (your decision): game first, platform second.** We were ~3 days ahead (M0–M3 done on D0–D1). Phase A finishes a **polished, complete game** locally, with the MagicBlock moments (VRF deal, chain chip, receipt) already designed into the game behind an adapter. Phase B then plugs in the backend, the chain and the VPS. Everything M5/M6 needs was built as a one-line swap (deal seed source, jsonl event log → outbox, chain chip states), so Phase A work is not thrown away.

### Phase A: the polished game (local, no backend, no VPS)

| Day | Date | Milestones | Exit check |
|---|---|---|---|
| D0–D1 | Wed 18 – Thu 19 | ✔ M0 chain spike (local + devnet) · ✔ M1 transport · ✔ M2 netcode · ✔ M3 kit + race · **M4** full match (in progress) | M4: 20 unattended matches, reconnect, WebGL full match |
| D1–D2 | Thu 19 – Fri 20 (kickoff 14:00) | **G1 Gameplay polish** (§2 G1) · **U:** play full matches, give feel notes | Matches feel good to you; Final lasts 60–120 s |
| D2–D4 | Fri 20 – Sun 22 | **G2 Look** (art) + **G3 Juice** (UI, audio, VFX, signature moments) (§2 G2, G3) · **U:** approve the look, pick music mood | 60 FPS WebGL on the M2 Air in the heaviest map; screenshots and a capture you're happy to show |
| **D4 end** | **Sun 22, 23:59** | **⚑ Game freeze**: after this, only bug fixes and integration changes in game code | The game is complete and polished locally |

### Phase B: platform (backend, MagicBlock chain, VPS, web)

| Day | Date | Milestones | Exit check |
|---|---|---|---|
| D5 | Mon 23 | **M5** Rush Relay (identity, join tickets, directory, outbox fed by the M4 jsonl events) | Relay restart with no duplicates |
| D5–D6 | Mon 23 – Tue 24 | **M6** full `fall_rush` + chain worker + **VRF deal driving the game** + ER events + **Hostinger VPS deploy** (compose + nginx + certbot) | Ranked devnet match on the public URL: receipt on Solana matches the DB |
| D7 | Wed 25 | **M8** web: home, /play, /live (ER feed), /receipt · **Playtest 1** (≥ 8 humans on the public URL) | Playtest bug list |
| D8 | Thu 26 | Fix the playtest bugs · **P1:** Fan Picks (session keys) + Crown Capsule mint, only if Phase B is green · **Playtest 2 (recorded)** | Two clean recorded matches |
| D9 | Fri 27 | 08:00 freeze · README, PROOF.md, video edit (U+C) · submit by **12:00** | Submitted |

**Why the chain can wait until D5:** M0 already proved the risky parts on devnet (delegate, VRF in the ER, per-round commit, commit + undelegate; 22/22 steps). M6 extends a working spike; it is not new research. **It cannot slip past D6**: the judges weigh the MagicBlock integration, and Playtest 1 needs the public URL.

**Cut order if behind** (cut from the top first):
1. Bonk Factory (already stretch)
2. Crown Capsule mint
3. Fan Picks on-chain (keep guest picks in the DB)
4. G3 extras (claw animation, crown ceremony detail) → simple versions
5. Per-round commits (keep the single final commit)
6. Star Scramble → reuse the Block Party Dash route as a "time trial" skill round
7. Desktop builds

**If Phase A runs late:** the game freeze moves at most to **Mon 23 12:00**, and the cut comes from G3 first, never from M5/M6.

**Never cut:**
- the ER lifecycle
- the VRF deal
- 4 rounds to a champion
- the WebGL URL
- the receipt page

## 2. Milestone specs

### M0: foundation + MagicBlock lifecycle spike

- **Build:**
  - `git init`, pnpm workspace, `.gitignore`, `.env.example`, `rust-toolchain.toml` (if needed)
  - `programs/fall_rush` with `GameConfig`, a minimal `Match`, `create_match`, `delegate_match`, `request_deal`, `deal_callback`, `record_event(seq)`, `complete_round` (+ commit), `finalize_match`
  - `programs/tests/lifecycle.ts`
  - `scripts/devnet-lifecycle.ts`
- **Docs:** 06, 07.
- **Verify:**
  1. `anchor build` (Anchor 1.0.2).
  2. `anchor test --skip-local-validator` against the `mb-stack` + 2 VRF oracles, with these asserts:
     - owner = Delegation Program after delegation;
     - the deal is set by the callback;
     - a direct callback call fails;
     - a duplicate seq fails;
     - after a commit, Solana shows the updated data;
     - after finalize, the owner is `fall_rush` with the final values.
  3. The same lifecycle on devnet-as; signatures go into PROJECT_STATUS.
- **Time-box:** 1 day. On failure: report the logs and decide together (validator choice or SDK line).

### M1: Unity foundation + transport spike

- **Build:**
  - Unity project with URP, NGO 2.14, Dedicated Server 2.0.2, Cinemachine 3.1.7, Input System
  - Boot, Menu and Lobby scenes (placeholder)
  - `NetBootstrap` with UTP WebSockets
  - `Build.cs` (WebGL + Linux server)
  - `infra/gameserver.Dockerfile`
  - Solana.Unity-SDK import smoke test
- **Verify:**
  - the WebGL build opened in Chrome connects to the server running in local Docker, both through plain `ws` and through a local TLS proxy (nginx 1.24 in Docker, the same server-block shape as the VPS) with `wss`;
  - Solana.Unity-SDK compiles under Unity 6.3 WebGL;
  - measured WebGL build time and memory recorded.

### M2: networked movement (a + b)

- **Build:** `KinematicMotor`, `MovementConfig`, `InputCmd`, `ServerSim`, `ClientPrediction`, `Interpolation`, follow camera, `RushlingVisuals` v1 (squash, lean, noodle arms, face).
- **Verify:**
  - EditMode motor tests;
  - 3 clients under the network simulator;
  - correction counter on the HUD;
  - flood and NaN input fuzz rejected.

### M3: obstacle kit + race

- **Build:** `HazardMath` (pure), `HazardBehaviour`, 8 hazard types, checkpoints, fall volume, `RoundRules.Race`, Block Party Dash greybox.
- **Verify:** hazard vector tests; a PlayMode bot race; the checkpoint-skip test is rejected.

### M4: full match

- **Build:**
  - `MatchDirector` state machine (doc 05 §4)
  - the lobby room
  - Survival, Skill and Final rules
  - 3 more greybox maps
  - `BotBrain`
  - disconnect grace
  - intermission, results and champion screens
- **Verify:** 10 unattended 12-bot matches complete; the reconnect test passes.

### G1: gameplay polish (Phase A, after M4)

- **Build:**
  - fixes from the M4 findings (Crumble Shelf too short: longer wobble and/or respawning top-shelf tiles; connection retries through lossy links);
  - tuning from your play sessions: movement constants, camera, bot difficulty, course and round lengths, hazard timings;
  - menu and lobby feel: name entry, settings (sensitivity, invert Y, FOV, reduced motion, volume), controls screen, pause/leave confirm;
  - first-time hints per round mode ("Reach the finish", "Climb! Goo rising");
  - **MagicBlock-ready game UI:** a `IChainAdapter` interface with a `PracticeChainAdapter` (today's local seed + "● Server" chip); the Deal screen designed to show a VRF signature and seed, the chain chip with all four states (`● Server`, `◆ Rollup ✓`, `⬢ Solana ✓`, `! Pending`) driven by fake states in practice, and the receipt screen laid out for explorer links. M6 implements the adapter; no game code changes then.
- **Verify:** soak 20/20 still green; your feel sign-off; the Final 60–120 s in bot QA.

### G2: look (Phase A, the art part of the old M7)

Per doc 17 §2–3: toon shader (cel ramp, rim, character outline), palette, sky and baked lighting, blob shadows, Bolt final + cosmetic presets ("My Rushling"), props and set dressing on all five maps (toy blocks, capsule-machine tower, factory/shelf details), readable hazard materials (stripes, telegraph glow).

**Verify:** 60 FPS WebGL on the M2 Air in the heaviest map (doc 17 §3.2 budgets: tris, draw calls, texture memory); WebGL release size recorded; the originality check (doc 08 §8).

### G3: juice (Phase A, the feel part of the old M7)

Per doc 17 §4–6: the UI kit (chunky panels, buttons with squash), HUD styling, transitions; audio (music per phase, SFX per action and hazard, cues with subtitles); VFX (dust, landing puffs, hit stars, confetti, goo glitter, tile crumble); signature moments: the **Chaos Capsule machine** on the Deal screen (spins while the deal is pending, pops the modifier; built to show the VRF result in M6), the **claw** that picks up eliminated Rushlings, the **crown ceremony**.

**Verify:** a full match captured in WebGL at 60 FPS; your sign-off; **game freeze**.

### M5: Rush Relay

- **Build:** Fastify app, migrations, identity, join tickets, directory, internal events API, outbox; server `RelayClient`.
- **Verify:** vitest suite; kill-and-restart test of the Relay with no duplicates.

### M6: production chain integration

- **Build:**
  - full `fall_rush` per doc 06 (slots, targets, picks, transcript)
  - all security tests
  - `ChainAdapter` + worker
  - Deal integrated into the server (Deal screen)
  - Hostinger VPS deploy (compose + nginx + certbot)
- Implements `IChainAdapter` from G1 (VRF deal → Deal screen, ER events → chain chip, receipt links); game code otherwise unchanged.
- **Verify:** ranked devnet match end-to-end; the receipt equals the DB; `PROOF.md` exported.

### M7: art & juice → moved into Phase A as **G2 (look)** and **G3 (juice)** (re-plan 2026-11-19)

Only the playtest-driven art fixes remain in Phase B (D8).

### M8: web

- **Build:** Next.js landing (logo, play button, recent matches), `/play`, `/live/:id` (ER `onAccountChange` feed), `/receipt/:id` (Rollup ✓ → Solana ✓ with explorer links), wallet connect (wallet-adapter).
- **Verify:** the live page updates during a match; the receipt is correct after finalize.

### P1: Fan Picks + Crown Capsule

Session-key Fan Picks on the web live page; guest picks in the DB; Crown Capsule mint after final.

**Verify:** a pick transaction signed by the session key on the ER; the asset is visible with the `matchPda` attribute.

## 3. What you can do in parallel (highest value first)

1. **Today:** install Unity and modules; register on Luma; choose the domain/subdomains for the Hostinger VPS; create a devnet wallet and fund it (faucet).
2. **Recruit 8–12 playtesters** for Nov 25 and Nov 26 (friends, Discord). This is the single biggest demo-quality lever.
3. **Bolt model** in Blender or ProBuilder, following doc 17 §2.1. I'll write the procedural animation; you own the look.
4. **Greybox maps** in ProBuilder from doc 08, once M3 lands the obstacle prefabs.
5. **Sound picks** from Kenney CC0 packs per doc 17 §6, logged in `ASSET_LICENSES.md`.
6. **Video:** OBS capture setup; edit the 3:00 cut per doc 18 §5.
