# Fall Rush — Engineering Understanding Report (v1)

> **SUPERSEDED (2026-11-18):** decisions and plan now live in `docs/` (v2). Kept for history.

**Date:** 2026-11-18
**Scope:** Review of all 17 files in `fall-rush-docs/`, checked against current official SDKs, registries and docs.
**Status:** For discussion. No code has been written and no decision has been made.

**How to read the labels**
- **[VERIFIED]** — confirmed today from an official repo, registry or doc.
- **[UNVERIFIED]** — could not be confirmed.
- **[ASSUMPTION]** — something I assumed and you should check.
- **[DECISION]** — needs your approval.

---

## A. Product understanding

Fall Rush is an original 3D knockout party game. Each match puts 10–12 players (20 later) through a series of short rounds. Every round is a different obstacle course or survival arena, and the field shrinks until one champion is left.

**The game must be fun with no wallet.** The blockchain does three jobs, and none of them interrupts play:

1. **Fair setup.** MagicBlock VRF picks the map sequence and hazard variants. The pick is provably random, and every player gets the same one.
2. **Live competitive record.** The match account is delegated to an Ephemeral Rollup. Round start, each qualification or elimination, round completion and the champion are written there with low latency and no fee per transaction.
3. **Durable receipt.** At the end, the account is committed and undelegated back to Solana devnet. It becomes a permanent match receipt that anyone can inspect. Optional later step: cosmetic or trophy ownership via Metaplex Core.

**Honest trust model.** The dedicated game server decides everything physical. The chain proves only these things:
- that an authorised server recorded a well-formed, ordered, roster-consistent sequence of outcomes;
- that the map setup came from verified randomness.

The chain does not prove that the physics was fair. The demo has to say this.

**Hackathon fit (Entertainment Finance / "compete, win, own").** The fit is verifiable competitive records, VRF fairness and owned trophies. No paid entry, no prizes, no token.

---

## B. Architecture understanding

### B.1 Components and how they talk

```
                ┌─────────── HTTPS (REST /v1) ───────────┐
 Unity Client ──┤                                         ├── Backend API (TS/Node)
 (C#, URP)      └── UDP (Unity Transport / NGO) ──┐       │     • guest identity, lobbies
                                                  │       │     • match orchestration
                                                  ▼       │     • outbox + chain worker
                           Dedicated Game Server (Unity   │     • read API for web
                           headless Linux build, C#) ─────┘ internal HTTP: events, results
                           • 30 Hz fixed-step sim                │
                           • movement, hazards, checkpoints      ▼
                           • qualification decisions      PostgreSQL (read model + outbox)
                                                                 │
                                         chain worker (TS, @magicblock-labs SDK)
                                                                 │
                    ┌──── Magic Router / ER endpoint ────────────┤
                    ▼                                            ▼
          MagicBlock ER (delegated Match PDA)          Solana devnet (init, delegate,
          • VRF callback, round events                 undelegated final receipt)
                    └──── commit_and_undelegate ───────────────►┘
 Next.js web ── reads DB + on-chain account → match receipt page, explorer links, wallet link
```

### B.2 Key point the docs blur

The **game server is a Unity headless build written in C#**, not a TypeScript service.

`00-README` lists `server/` as "TypeScript lobby/match orchestration + game server adapter". That is fine for the backend, but the physics simulation has to be Unity. Only then can the client and server run the **same movement code**, which client prediction needs.

The C# game server never touches Solana. It posts domain events to the backend over an authenticated internal HTTP endpoint. The backend writes them to a Postgres outbox, and the TS chain worker submits them.

### B.3 Data authority

The data-authority table in doc 03 §3 is correct and I keep it as written.

---

## C. Documentation audit

Severity scale: **Critical** blocks the MVP or makes a claim false. **High** means large rework if ignored. **Medium** and **Low** as usual.

### C.1 Missing requirements

| # | Issue | Why it matters | Sev | Recommendation |
|---|---|---|---|---|
| M1 | `14-CODEX-IMPLEMENTATION.md` is referenced by the README but not in the bundle. | The build prompts and any constraints in it are unknown to me. | Medium | Send it if it exists. Otherwise this report and `PROJECT_STATUS.md` replace it. |
| M2 | No hackathon deadline or submission date anywhere. | It decides scope: 3 or 4 rounds, how many maps, and whether there is a web dashboard. | **Critical** | Tell me the deadline. I will size the milestones to it. |
| M3 | No team size or roles. Who does art, animation and audio? | Original characters and animations are the largest polish cost. | High | Confirm whether you are solo. See D-7. |
| M4 | No target platform: macOS/Windows desktop, WebGL, or both. | It drives the transport (UDP vs WebSocket), the build pipeline, and whether judges can play. | High | See D-4. |
| M5 | No dedicated-server hosting choice. **Unity Multiplay Game Server Hosting was deprecated on 2026-04-01** [VERIFIED]. | The game server needs a host that supports UDP. | High | A plain Linux VPS running Docker. See D-5. |
| M6 | No plan for getting 8–12 clients into a test match. An 8 GB machine cannot run that many Unity clients. | PRD acceptance criterion #1 is "≥8 clients join". | High | Headless "input-bot" client builds run on the remote Linux box, plus a few real humans. Bots are labelled. |
| M7 | No definition of the roster identity for **guest players who have no wallet**. Anchor instructions take `player: Pubkey`. | You cannot put a guest into a Pubkey-keyed roster. | **Critical** | On-chain roster uses slot indexes. See C.2 #C5. |
| M8 | No ruleset values: tick rate, qualify counts, timers, disconnect grace. | The program must enforce counts it knows about. | Medium | A versioned `ruleset.json` shared by the server, the backend and the on-chain config. |
| M9 | No character or art direction beyond "toy-like". | The IP risk of a bean-shaped silhouette, and the scope of the asset pipeline. | Medium | See D-7. |

### C.2 Contradictions across documents

| # | Contradiction | Sev | Resolution |
|---|---|---|---|
| C1 | **Three different state machines.**<br>• PRD FR-05: WAITING→…→INTERMISSION→FINAL_ACTIVE→COMPLETED<br>• Backend 05 §5: OPEN→COUNTDOWN→…→RESOLVING→COMPLETED<br>• Anchor 06 §7: Created→Locked→RandomnessPending→Ready→InProgress⇄RoundResolving→Completed | High | Define one canonical **Match** state machine (on-chain enum = server enum). Keep **Lobby** state as its own small machine (OPEN/COUNTDOWN/LOCKED/CLOSED). Write one mapping table in the docs. |
| C2 | **Round count.**<br>• 00-README: 3 rounds minimum (race, survival, final), skill round is stretch<br>• GDD and your brief: 4 rounds, 12→8→5→3→1 | Medium | MVP has 3 rounds: 12→7→4→1, or 12→8→4→1. The skill round is stretch. Thresholds are config, not code. See D-6. |
| C3 | **"Two polished maps minimum"** cannot fill three distinct round modes (race, survival, final). | High | Minimum of 3 maps, built from one shared obstacle kit. See D-6. |
| C4 | `initialize_match(... map_sequence_hash)` takes the map sequence **before** VRF has run. `set_randomness_result(request_id, randomness, map_sequence_hash)` lets the caller supply the hash. | **Critical** | At init, commit only to `map_pool_hash` and the ruleset. The **VRF callback derives the sequence on-chain** from the randomness. Neither the operator nor the server ever supplies it, which keeps "no reroll / no operator choice" true. |
| C5 | Roster checks are keyed by `Pubkey` (06 §6), but players are guests (ADR-005). | **Critical** | Store `roster_hash = H(slot_i ‖ H(user_id_i) ‖ wallet_i or 0)`. Instructions take a `slot: u8`. Per-slot state lives in a fixed `[u8; 20]` array. A wallet is optional metadata. |
| C6 | Two accounts, `MatchRegistry` and `MatchState`, hold overlapping fields: status, round, sequence, transcript. Doc 03 has one `MatchAccount`. | Medium | **One `Match` PDA** that is delegated during play and becomes the receipt after undelegation, plus one `GameConfig` PDA (authority, map pool, rulesets). Two accounts double the delegation and commit surface for no benefit. |
| C7 | 06 §5 has per-player `record_player_qualified` events. 07 §6 says "keep a small number of transitions". | Low | Keep per-player events. ER transactions are free [VERIFIED, fee doc], and live per-player writes are the demo's ER showcase. **Commit to Solana only once, at the end.** |
| C8 | 00-README repo layout implies a TS game-server adapter. The architecture docs imply a Unity dedicated server. | Medium | As B.2: the game server is Unity C#, and the backend is TS. |
| C9 | Realtime protocol (10 §3) defines custom messages like `INPUT_FRAME`/`SNAPSHOT`, while the stack is NGO. | Low | Implement them as NGO custom messages and RPCs with the same fields. Keep the field definitions as the contract. |
| C10 | 05 §7 implies a separate queue. 03 §9 lists Redis. | Low | Postgres outbox with `SELECT … FOR UPDATE SKIP LOCKED` covers the MVP. **Drop Redis** until it is measured as necessary. |

### C.3 Technically questionable or outdated against today's reality

| # | Finding | Sev | Recommendation |
|---|---|---|---|
| T1 | **NGO has no built-in client-side prediction or reconciliation** [VERIFIED, NGO manual: "doesn't support full client-side prediction and reconciliation", it only offers "client anticipation"].<br>Unity's competitive quickstart does no prediction: the client never moves its own object [VERIFIED].<br>The docs assume prediction is part of the stack. | **Critical** | We must build prediction or choose another stack. See **D-1**. This is the most important gameplay decision. |
| T2 | **The MagicBlock SDK split into two release lines on 2026-11-16** [VERIFIED]:<br>• `0.17.x` targets Validator v1<br>• `0.20.x` targets Validator v2 and requires **Anchor ≥ 1.2.2**, with all older-Anchor compatibility features removed<br>Official examples still pin SDK 0.16.2 and Anchor 1.0.2.<br>On devnet, the `as`/`eu` ER endpoints report core 1.1.1 and `us` reports 2.0.0 [observed today; v1/v2 mapping UNVERIFIED]. | **Critical** | Run a day-one spike to pick **one tuple** that works end to end on devnet. See D-2. The docs' warning about this was right; the situation is now worse. |
| T3 | Docs and examples use `create_request_scoped_randomness_ix`, which **does not exist in SDK 0.17.3 or 0.20.0** [VERIFIED].<br>In 0.20 you use `#[vrf]` with `create_request_randomness_ix_const`, and the callback uses `#[vrf_callback]`. The callback signer is a **per-program scoped identity PDA**. The global `VRF_PROGRAM_IDENTITY` check is deprecated. | High | Follow the 0.20 source, not the docs. Doc 06 §9 ("validate VRF program identity signer") still holds, but the mechanism changed. |
| T4 | Local anchor-cli is **1.2.1**. SDK 0.20 needs anchor-lang **1.2.2** [VERIFIED]. | Medium | `avm install 1.2.2` (pending D-2). |
| T5 | `mpl-core` Rust crate supports **Anchor 0.31/0.32 only**, not 1.x [VERIFIED]. This conflicts with an on-chain `cosmetic_claim` CPI. | Medium | **Never CPI Metaplex from our program.** The backend mints with `@metaplex-foundation/mpl-core` 1.10 + Umi [VERIFIED] after checking eligibility in the DB and on-chain receipt. Post-MVP, P1. |
| T6 | ER commits cost **0.001 SOL each after the first**, and **without a delegated fee payer only 10 commits are allowed; the 11th fails** with `0xA0000000` [VERIFIED; checked on v1, UNVERIFIED for v2]. | High | **Do not commit per event.** Use one `commit_and_undelegate` at match end, plus an optional single mid-match commit. Our ER transactions themselves are free. |
| T7 | Delegated accounts are **locked on the base layer** until undelegation completes [VERIFIED]. | Medium | A delegated match can only be read from the ER (via the router) until the end. The web receipt page must read through the router while the match is live. |
| T8 | Session keys (07 §7) solve a problem we do not have. Players never sign match state, and the server signs everything. | Low | **Cut from MVP.** |
| T9 | "Deterministic replay tests" (12 §4) assume determinism. PhysX is not deterministic across machines. | Medium | A custom kinematic motor at a fixed step gives *near-determinism on the same build*. Treat replay as a regression aid, not as proof. |
| T10 | JWT refresh rotation, MFA admin, WAF and similar (11 §6). | Low | Production items. For the MVP, use short-lived guest tokens and a single admin key held only on the server. |
| T11 | Solana.Unity-SDK: latest release v1.2.9 (2025-09); **Unity 6 compatibility is UNVERIFIED**. | Medium | **Keep wallets out of Unity for the MVP.** Link wallets on the Next.js site with a signed nonce. Unity shows a "link at fallrush.xyz/link?code=…" code. This removes a risky dependency from the game client. |
| T12 | The docs fear "physics parity" between server and client (15 risk #4). | — | This is mostly solved structurally:<br>• **Hazards are pure functions of (server tick, VRF seed, params)**, so clients compute them locally with zero hazard bandwidth and stay perfectly in sync.<br>• Player movement uses one shared C# kinematic motor.<br>• Physics-driven "ragdoll" is **visual only**: animation, procedural wobble and a server-owned stun state. |

### C.4 Unnecessarily complicated for the hackathon

**Cut from the MVP:**
- Redis
- Matchmaking tickets (use lobby codes and a "quick play" that fills one lobby)
- Session keys
- On-chain `player_profile` / `achievement` PDAs
- Metaplex CPI
- Wallet in Unity
- Server ed25519 signature per event (a rolling transcript hash on-chain is enough)
- Multiple regions
- Autoscaling
- Read replicas
- Refresh tokens
- Team round
- Spectator mode beyond "follow a remaining player"
- Controller support
- OpenAPI codegen (a hand-written shared types package is enough)

**Keep, because they are cheap and high-value:**
- Idempotent outbox
- Monotonic event sequence on-chain
- Rolling `transcript_hash`
- On-chain invariants:
  - champion must be the only surviving slot;
  - counts must match the ruleset;
  - no skipped rounds.
- Explicit UI chain states: pending → ER recorded → Solana finalized.

### C.5 Architectural risks (ranked)

1. **ER/VRF version churn (T2, T3).** Mitigation: spike first, pin everything, adapter boundary.
2. **Netcode feel (T1).** Bad prediction makes the game feel bad no matter what else is good. Mitigation: build the networked motor first and test it at 150 ms RTT before writing any maps.
3. **Schedule vs art (M2, M3).** Mitigation: CC0 low-poly kits with our own palette and character; polish budget gated by milestones.
4. **8 GB RAM.** Unity Editor + Rider/VS Code + Docker + local validators all compete for memory. Mitigation: run the local ER stack only during chain work, offload multi-client tests to a VPS, and use Multiplayer Play Mode with at most one virtual player.
5. **Disk.** About 31 GB free. Unity 6.3 + Linux server modules ≈ 10.5 GB [VERIFIED], plus Library cache. This is tight but fits. Clear space if you can.
6. **Server signer trust** is a real limitation (documented): the server key can fabricate results. Mitigation: isolate the key, restrict the program to match transitions, and disclose it.
7. **Devnet ER/VRF oracle availability on demo day.** Mitigation: record proof runs early, and use a fail-closed "unranked" fallback (FR-06).

---

## D. Hackathon MVP plan (the minimum winning build)

**Definition of done:**
- A recorded, repeatable match: 8–12 clients (real players plus labelled input-bots) go through 3 rounds to a champion.
- Every client sees the same VRF-selected sequence and hazard variants.
- Every qualification and elimination is visible live as an ER transaction.
- The final receipt is on Solana devnet and viewable on a web page with explorer links.

### D.1 Scope

| Area | MVP | Stretch |
|---|---|---|
| Rounds | Race → Survival → Final (3) | Skill round (4) |
| Maps | Sky Dash (race), Slime Escape-lite (survival: rising plane + lifts), Last Island (final: disappearing tiles) | Hammer Havoc as a 2nd race map in the VRF pool |
| VRF use | Selects 1 of K pre-validated sequences + hazard variant/phase seed | Weighted pools |
| Players | 12 slots | 20 after load test |
| Chain | GameConfig + Match PDA; init/lock (base) → delegate → VRF in ER → round & per-player events in ER → complete → commit_and_undelegate | Mid-match commit |
| Backend | Guest identity, lobbies, orchestrator, outbox, chain worker, results API | Leaderboard |
| Web | Match receipt page (state from chain + DB), wallet link | Profile, cosmetic claim (Metaplex Core via Umi) |
| Client | Character, camera, HUD, results, champion screen, chain-status badges | Emotes, cosmetics |

### D.2 Proposed on-chain design [ASSUMPTION, to validate in spike]

**`GameConfig` PDA** `[b"config"]`
- `admin`
- `game_authority` (the server signer, rotatable)
- `map_pool_hash`
- `ruleset_versions`

**`Match` PDA** `[b"match", match_id(32)]`
- `version`, `match_id`, `status`, `ruleset_version`
- `roster_hash`, `player_count`
- `slot_state: [u8; 20]`, a packed status per slot: alive / qualified(round) / eliminated(round, reason)
- `placements`, `current_round`, `event_seq: u32`
- `transcript_hash` (rolling sha256)
- `vrf_seed: [u8; 32]`, `sequence_index: u8`
- `champion_slot`
- `timestamps`

This is a few hundred bytes, fixed size, with no Vec.

**Instructions**
- **Base layer:** `init_config`, `create_match`, `lock_match`, `delegate_match`
- **ER:**
  - `request_map_randomness`
  - `vrf_callback`, which derives the sequence on-chain
  - `start_round`
  - `record_qualified`
  - `record_eliminated`
  - `complete_round`, which enforces counts against the ruleset
  - `complete_match`, which enforces that the champion is the only alive slot
  - `finalize_and_undelegate`
- **Either layer:** `abort_match`

**Open question:** whether the VRF request runs on the base layer before delegation or inside the ER. The ER is faster and keeps everything in one place, but adds an ER-oracle dependency. The spike decides, and the base layer is the fallback.

---

## E. End-to-end roadmap (milestones and dependencies)

I propose two changes to your phase order:

1. **Chain spike moves to Phase 0.** It is the highest-uncertainty item and a requirement for Blitz eligibility. It needs no Unity, so it can run while Unity installs.
2. **Phase 1 is built networked from day one.** Retrofitting prediction onto a single-player controller is a rewrite.

| M | Milestone | Depends on | Verify by |
|---|---|---|---|
| **M0** | Repo foundation + **MagicBlock lifecycle spike**: Anchor workspace, minimal Match PDA, delegate → ER write → VRF callback → commit_and_undelegate, TS script | D-2 | `anchor test` against the local ER stack. Same script on devnet prints signatures; the account state is read back from Solana |
| M1 | Unity project foundation (6000.3 LTS, URP, NGO, Dedicated Server), bootstrap scene, headless server build runs on macOS + Linux Docker | Unity install, D-1, D-4 | Server container starts; editor client connects locally |
| M2 | **Networked character motor**: shared kinematic motor, input commands, server sim at 30 Hz, client prediction + reconciliation, remote interpolation, camera, jump/dive | M1 | 2–3 clients; latency sim 150 ms / 2% loss; correction-rate metric shown on screen; speed/teleport input fuzz rejected |
| M3 | Obstacle kit (deterministic hazards: beam, pendulum, mover, rising plane, vanishing tile) + checkpoints/respawn + race qualification on test map | M2 | EditMode tests for hazard determinism; PlayMode checkpoint-order test |
| M4 | Match lifecycle on game server: roster, 3-round flow, scene transitions, elimination, disconnect grace, champion; input-bot client | M3 | 8+ connections (bots on VPS) complete a full match unattended ×10 |
| M5 | Backend: guest identity, lobbies, orchestrator, Postgres outbox, game-server → backend events | M4 (contract can start in parallel) | Integration tests; kill/restart worker → no duplicate events |
| M6 | Production Anchor program (from M0 spike) + chain worker wired to outbox; VRF drives map sequence in-game | M0, M5 | Full match → ER txs per qualification → final receipt on devnet matches DB |
| M7 | Maps to polish: Sky Dash, Slime-lite, Last Island; characters, animation, UI, audio, VFX | M3–M4 | Playtests; 60 FPS on M2 at target resolution (measured) |
| M8 | Web receipt page + wallet link | M6 | Receipt matches on-chain account; explorer links |
| M9 | Hardening + deploy (VPS game server + API + DB), demo recording | all | Release checklist (12 §5); 3-minute demo |

**Parallel tracks:**
- M0 runs alongside M1.
- The backend contract in M5 can start after M2.
- Art in M7 can start any time once D-7 is decided.

---

## F. Critical decisions (need your approval)

| ID | Decision | Options | My recommendation |
|---|---|---|---|
| **D-1** | Netcode framework & prediction | (a) **NGO 2.14** + hand-built prediction/reconciliation over a shared kinematic motor (b) **FishNet 4.7** (free; built-in prediction/reconcile) (c) Netcode for Entities (DOTS; built-in prediction) | **(a)**. Our motor is custom and kinematic anyway, and prediction for one local character is a bounded, well-known task (about 3–5 days). NGO is the official, documented path and the Dedicated Server tooling matches it. (b) is the fallback if the M2 prediction spike fails its feel test. (c) is too heavy a learning curve for the timeline. |
| **D-2** | MagicBlock SDK line | Official-examples tuple vs `0.20.x` (Validator v2, Anchor ≥1.2.2) | **Revised (rev 2):** start with the tuple MagicBlock's own current examples pin, which is **`anchor-lang 1.0.2` + `ephemeral-rollups-sdk 0.16.2` with features `["anchor","vrf"]`**. Verified in `magicblock-labs/magicblock-examples` (`counter`, `roll-dice-delegated`), updated 2026-11-13. Evaluate the 0.20 upgrade only after the lifecycle works. |
| **D-3** | VRF placement | Base layer before delegation vs inside ER | Inside ER if the spike passes; otherwise base. |
| **D-4** | Platform | Desktop (macOS + Windows) vs WebGL | **Desktop for MVP**: UDP, best feel, simplest. Keep transport WebSocket-capable so WebGL stays possible later. |
| **D-5** | Hosting | VPS (Docker, UDP) vs managed | **One Linux VPS** (4 vCPU / 8 GB) for game servers, input-bots, API and Postgres during the hackathon. Provider is your choice (it must allow UDP). |
| **D-6** | Match format & maps | 3 or 4 rounds; 3 or 4 maps | **3 rounds, 3 maps** (12→7→4→1, thresholds in config); Hammer Havoc + skill round are stretch. |
| **D-7** | Art & character | Hand-made in Blender vs CC0 low-poly kits (e.g. Kenney, Quaternius) recolored + one custom hero character | **CC0 kits for environment, original character design** with a distinct silhouette (not a jellybean). Verify licenses per asset. |
| **D-8** | Wallet location | Unity SDK vs web link | **Revised (rev 2):** use MagicBlock's **Solana.Unity-SDK** in the client for the optional wallet path, with **MagicBlock Session Keys** so a wallet player approves once and then sees no popups. Unity 6 compatibility gets checked in M1; the web link is the fallback. |
| **D-9** | Drop Redis & matchmaking tickets | — | **Drop for MVP** (C10). |
| **D-10** | Server/chain authority | Single `game_authority` key in GameConfig vs per-match keys | **Single rotatable key per environment**, never the upgrade authority or a treasury, held only by the chain worker. Disclosed in the demo. |

---

### F.1 MagicBlock stack adoption (rev 2, after "we must use their stack")

**Use:**
- Ephemeral Rollups (delegation program, commit/undelegate)
- Magic Router (TypeScript client routing)
- VRF, run inside the rollup via the `vrf` feature of `ephemeral-rollups-sdk`
- Solana.Unity-SDK (Unity wallet)
- Session Keys (`session-keys` 3.1.1, program `KeyspM2ssCJbqUhQ4k7sveSiY4WjnYsrXkC8oDbwde5`)

**Reference examples:** `magicblock-examples/rewards-delegated-vrf`, `gasless-starter`, `session-keys`, `roll-dice`.

**Do not use:**
- **BOLT.** Last release v0.2.6 was on 2025-09-24. Its Cargo.toml pins `anchor-lang ^0` and `solana-program ^2`, which clashes with the Anchor 1.x line the current ER SDK and examples use. It also targets fully on-chain ECS simulation, and our physics runs off-chain.
- **SOAR.** The repo was last pushed in April 2024.

**Not covered by MagicBlock at all:** MagicBlock has no real-time netcode or physics product. Unity, NGO and the authoritative server stay as planned.

## G. First implementation milestone — M0

**What:** monorepo skeleton plus a **MagicBlock lifecycle spike**:
- Anchor program `fall_rush` with `GameConfig` and a minimal `Match` PDA (status, round, event_seq, transcript_hash, vrf_seed);
- instructions: create → delegate → VRF request + callback (in the ER, or base as fallback) → `record_event` ×N in the ER → `finalize_and_undelegate`;
- a TS script that runs the lifecycle and prints every signature and account state at each stage.

**Why first:**
- It is the hackathon eligibility gate.
- It is the place where the docs are now provably out of date (T2, T3).
- It needs no Unity, so it can run while Unity installs.
- It fixes the version tuple for everything downstream.

**Files (expected):**
- Root config: `package.json` (pnpm workspace), `.gitignore`, `.env.example`, `.nvmrc`, `rust-toolchain.toml`
- `programs/` Anchor workspace: `Anchor.toml`, `programs/fall_rush/src/lib.rs` plus modules, `tests/lifecycle.ts`
- `scripts/devnet-lifecycle.ts`
- `docs/` (copy of the spec docs), `PROJECT_STATUS.md`, `docs/adr/ADR-007-sdk-tuple.md`
- `git init` (no remote, no push)

**Verification:**
1. `anchor build` with the pinned tuple.
2. `anchor test` against the local stack (`mb-test-validator` + `ephemeral-validator` + VRF oracle, per the MagicBlock local-dev docs). Pass criteria:
   - the account owner is the delegation program while delegated;
   - the ER write succeeds;
   - the VRF callback is accepted only from the scoped identity;
   - a forged callback is rejected;
   - a duplicate `event_seq` is rejected;
   - after undelegation the account is owned by our program and readable on the base layer with the final values.
3. The same flow on devnet, with Explorer links recorded in `PROJECT_STATUS.md`.

**What you need to do in parallel:**
- Install Unity Hub + **Unity 6000.3.28f1 LTS** [VERIFIED latest 6.3 LTS], with the Linux Dedicated Server and Linux Build Support (Mono) modules.
- Fund a devnet keypair (`solana airdrop` or a faucet). I will never generate or commit a funded key into the repo.

**Time-box:** if the 0.20 + v2 devnet path does not work within the spike, I stop, report what failed with logs, and we decide on 0.17.3.
