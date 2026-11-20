# 14 — Implementation Plan (Blitz V10 build: Nov 18 → Nov 27)

**Status:** v2 (2026-11-18). This replaces the missing `14-CODEX-IMPLEMENTATION.md`.

**Owners:**
- **C** = Claude (code, integration, tests, docs)
- **U** = you (Unity editor work, art and greybox, playtest recruiting, accounts, video)

**Gate rule:** each milestone ends with its verification passing, and I report the result. I start the next milestone only after you OK it. Within a day, the milestones in that day's row are pre-approved once the plan is approved.

---

## 1. Calendar (all times UTC; deadline Fri Nov 27 14:00 UTC = 19:30 IST)

| Day | Date | Milestones | Exit check |
|---|---|---|---|
| D0 | Wed 18 | Docs v2 ✔ · **U:** install Unity 6000.3.28f1 + Web + Linux Dedicated Server modules; register on Luma; create a Railway project; fund a devnet keypair | You have approved the docs |
| D1 | Thu 19 | **M0** repo + chain spike (local stack) · **M1** Unity project, NGO/UTP WebSocket, WebGL ↔ Linux server connectivity spike (local Docker) | M0: lifecycle test green locally · M1: WebGL client in the browser connects to the server container |
| D2 | Fri 20 (kickoff 14:00) | M0 on devnet · **M2a** KinematicMotor + input + server sim + snapshots · **U:** Bolt model blockout (capsule shell, screen face) | Devnet signatures saved · 2 clients move around |
| D3 | Sat 21 | **M2b** prediction + reconciliation + interpolation + camera + Rushling procedural animation | Feel test at 150 ms |
| D4 | Sun 22 | **M3** obstacle kit (HazardMath + 8 types), checkpoints, respawn, **Block Party Dash** greybox, race rules · **⚑ Netcode go/no-go** (doc 04 §4.5) | Bot race completes; prediction holds against spinners |
| D5 | Mon 23 | **M4** match director: lobby, 4-round flow, Goo Tower, Star Scramble, Crumble Shelf greyboxes, bots, disconnects · **U:** greybox polish in ProBuilder | 12-bot match completes ×10 |
| D6 | Tue 24 | **M5** Rush Relay (identity, directory, outbox) + **M6** full `fall_rush` program + chain worker + VRF deal driving the game · deploy to Railway | Devnet ranked match: receipt on Solana matches the DB |
| D7 | Wed 25 | **M7** art & juice pass (toon shader, palette, props, UI, audio, VFX, claw, capsule machine) · **M8** web: home, /play, /live (ER feed), /receipt · **Playtest 1** (≥ 8 humans) | Playtest bug list |
| D8 | Thu 26 | Fix the playtest bugs · **P1:** Fan Picks (session keys) + Crown Capsule mint · **Playtest 2 (recorded)** | Two clean recorded matches |
| D9 | Fri 27 | 08:00 freeze · README, PROOF.md, video edit (U+C) · submit by **12:00** | Submitted |

**Cut order if behind** (cut from the top first):
1. Bonk Factory (already stretch)
2. Crown Capsule mint
3. Fan Picks on-chain (keep guest picks in the DB)
4. Per-round commits (keep the single final commit)
5. Star Scramble → reuse the Block Party Dash route as a "time trial" skill round
6. Desktop builds

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
  - the WebGL build opened in Chrome connects to the server running in local Docker, both through plain `ws` and through a local TLS proxy (Caddy) with `wss`;
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

### M5: Rush Relay

- **Build:** Fastify app, migrations, identity, join tickets, directory, internal events API, outbox; server `RelayClient`.
- **Verify:** vitest suite; kill-and-restart test of the Relay with no duplicates.

### M6: production chain integration

- **Build:**
  - full `fall_rush` per doc 06 (slots, targets, picks, transcript)
  - all security tests
  - `ChainAdapter` + worker
  - Deal integrated into the server (Deal screen)
  - Railway deploy
- **Verify:** ranked devnet match end-to-end; the receipt equals the DB; `PROOF.md` exported.

### M7: art & juice

Per doc 17: toon shader, palette, Bolt final, cosmetic presets, props, UI kit, audio, VFX, capsule machine, claw elimination, crown ceremony.

**Verify:** 60 FPS WebGL on M2 in the heaviest map; playtest 1.

### M8: web

- **Build:** Next.js landing (logo, play button, recent matches), `/play`, `/live/:id` (ER `onAccountChange` feed), `/receipt/:id` (Rollup ✓ → Solana ✓ with explorer links), wallet connect (wallet-adapter).
- **Verify:** the live page updates during a match; the receipt is correct after finalize.

### P1: Fan Picks + Crown Capsule

Session-key Fan Picks on the web live page; guest picks in the DB; Crown Capsule mint after final.

**Verify:** a pick transaction signed by the session key on the ER; the asset is visible with the `matchPda` attribute.

## 3. What you can do in parallel (highest value first)

1. **Today:** install Unity and modules; register on Luma; create a Railway account/project; create a devnet wallet and fund it (faucet).
2. **Recruit 8–12 playtesters** for Nov 25 and Nov 26 (friends, Discord). This is the single biggest demo-quality lever.
3. **Bolt model** in Blender or ProBuilder, following doc 17 §2.1. I'll write the procedural animation; you own the look.
4. **Greybox maps** in ProBuilder from doc 08, once M3 lands the obstacle prefabs.
5. **Sound picks** from Kenney CC0 packs per doc 17 §6, logged in `ASSET_LICENSES.md`.
6. **Video:** OBS capture setup; edit the 3:00 cut per doc 18 §5.
