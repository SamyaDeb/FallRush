# 19 — Fully On-Chain Fall Rush: Phase-wise Implementation Plan

**Status:** v1 (2026-11-21). The user approved the direction (2026-11-21):
1. Target = the whole game runs on a MagicBlock Ephemeral Rollup. **Classic mode** (today's Unity server) stays as the fallback until on-chain mode matches it.
2. **RUSH chips** on devnet (an SPL token from a faucet, no real money) replace ADR-006.
3. The motor may be **retuned** in `rush-core` (integer maths). The user signs off on an A/B feel test.

**Supersedes:** M5 / M6 / M8 / P1 in doc 14 §2, and the pitch in doc 18 §3.
**Keeps:** the art, audio, HUD, camera, maps (geometry and hashes), round formats, VRF deal semantics, and the transcript.

**Sources** (the research of 2026-11-21, summarised in §11):
- docs.magicblock.gg;
- the ephemeral-rollups-sdk releases;
- the magicblock-examples repo;
- MagicBlock's Blitz results threads V0–V9.

Anything not measured by us is marked **UNVERIFIED**.

---

## 0. The one-paragraph version

A Rust crate, **`rush-core`**, holds the whole game simulation with integer maths only: the motor, collisions, hazards, round rules and bots. It is compiled **to sBPF** inside the `fall_rush` program, where it is the authority, and **to WASM** inside Unity WebGL, for client prediction. Same source and integer maths means the client and the chain agree **bit for bit, by construction**.

On the Ephemeral Rollup:
- a **timed task** (the rollup's built-in scheduler, "crank" in the SDK) calls `tick()` 20 times a second;
- players write **their own input accounts** with session keys;
- browsers **subscribe to the `World` account**;
- the program itself decides eliminations, settles **Rush Markets**, applies **Chaos Drops** and, at the end, commits a receipt to Solana. A follow-up call on Solana ("Magic Action" in the SDK) then mints the **Crown Capsule** and the **VRF capsule drops** and pays out.

The Unity server is not used in on-chain mode.

---

## 1. Architecture

```
Browser (Unity WebGL)                         MagicBlock Ephemeral Rollup (devnet-as, MAS1…)
├─ rush_core.wasm  (prediction, own player)   ├─ fall_rush program (Anchor 1.0.2, ER SDK 0.18.x)
├─ fallrush-chain.js (bridge file → JS)       │   ├─ Arena / Lobby / Matchmaker      (timed task)
│   ├─ burner keypair + SessionTokenV2        │   ├─ tick(): rush-core sBPF, 30 Hz   (timed task)
│   ├─ send InputBatch  ────────────────────► │   ├─ Round rules: Race / Survival / Skill / Final
│   └─ onAccountChange(World, Events)  ◄───── │   ├─ Bots (rush-core, VRF-seeded)
└─ renders (visuals unchanged)                │   ├─ Markets · Chaos Drops · Pots   (rollup token balances)
                                              │   └─ VRF: deal, hazards, stars, capsules, drop spots
Spectator / Replay web page ── subscribes ──► │
                                              └─ commit+undelegate Receipt ─► SOLANA (devnet)
Permissionless, untrusted helpers:                                           ├─ MatchReceipt account
├─ fanout relay (re-broadcasts World over WS; optional)                     ├─ follow-up call: payouts, Crown + Capsule (Metaplex Core)
├─ indexer / replay archive (stores input txs)                              └─ Season account
└─ bot runner (only if bots run off-chain, see P2.6)
```

**Trust model (printed in the README):**
- The rules are public program code, run by MagicBlock's rollup validator.
- Inputs are signed by each player's session key.
- Results and payouts are computed by the program and committed to Solana.
- Anyone can replay a match from the archived inputs with `rush-core`.
- **Still trusted:** the rollup operator, for transaction ordering and liveness (one validator per region). Private rollups (P6) hide sealed bets from everyone, including us.

---

## 2. Repo layout (new)

```
crates/
  rush-core/            # no_std, no floats, no alloc in the hot path
    src/fx.rs           # fixed-point types, trig tables, sqrt
    src/vec.rs          # IVec3 (mm), velocity (mm/s), yaw256
    src/motor.rs        # port of KinematicMotor semantics
    src/collide/        # obb.rs, prism.rs, sphere.rs, capsule.rs, sweep.rs, grid.rs
    src/hazards/        # port of HazardMath (14 types)
    src/rules/          # race.rs, survival.rs, skill.rs, final.rs, director.rs, targets.rs
    src/bots/           # port of BotBrain + WaypointBot
    src/world.rs        # World state, step(world, inputs) -> events
    src/mapblob.rs      # map blob format (read-only)
    tests/vectors/      # golden vectors shared with C# and the program
  rush-core-wasm/       # wasm-bindgen-free C ABI: step(), predict(), hashes
programs/fall_rush/     # existing program, grows the on-chain sim
  src/sim/              # tick, inputs, world (zero-copy)
  src/econ/             # chips, markets, drops, pots
  src/assets/           # receipt, payout follow-up call, Core mints
web/                    # Vite app: spectator, bets, drops, replay, receipt, claim
packages/chain/         # TS: fallrush-chain.js (bridge target), worker libs, IDL types
tools/mapblob-export/   # Unity Editor exporter → .mapblob + hash
tools/load/             # headless TS clients for load tests
services/               # fanout relay, indexer (both optional, stateless)
```

Unity side: `Scripts/Chain/` holds the bridge file `FallRushChain.jslib`, `OnChainSession.cs` and `OnChainPrediction.cs`. Inside the Unity project, `Plugins/WebGL/rush_core.wasm` sits next to it.

---

## 3. Core conventions (fix these before writing code)

| Topic | Rule |
|---|---|
| Units | Lengths in **mm, i32**. Speeds in **mm/s, i32**. Acceleration in mm/s². Time in **ticks** (u32). Angles as **yaw256** (u8, as today) for input; internally Q16 turns. These match `movement_v2.json`'s units and today's wire precision. |
| Maths | `i64` intermediates, never `f32`/`f64`, enforced by `#![deny(clippy::float_arithmetic)]`. `isqrt` by integer Newton iteration. sin/cos from a 1024-entry Q15 table (generated, hashed). |
| Tick | **30 Hz (33.3 ms)** on-chain. Decided 2026-11-21: P0 measured 15 / 20 / 30 Hz scheduling as precise (30 Hz: 3,637 / 3,637, gap p95 42 ms), and in the user's Feel Lab A/B (OC-3) 30 Hz feels like Classic while 15 Hz is laggy. Classic also simulates at 30 Hz. `movement_v4.json` re-expresses every ms constant in ticks; the rate is a `rush-core` parameter (15 / 20 / 30). |
| Determinism | No hash maps with random order, no allocation-order dependence, fixed iteration order (slot ascending). Every step returns the transcript events it produced. |
| Hashes | The map blob hash replaces the map content hash on-chain. `rush_core_version` and the `movement_v4` hash go into the ruleset. |
| Accounts | Hot accounts are **zero-copy** (`#[account(zero_copy)]`, `bytemuck`). Every delegated account is ≤ 10,240 B (the commit limit). |
| Compute budget | **P2 (OC-11): the Final (3 players) runs on-chain first: worst tick 262k CU on devnet-as, real time 5/5.** Original target: `tick()` ≤ **300k CU worst case** at 12 players (P0-1 condition: a timed-task execution has 400k CU in total, and one failed execution ends the task). P1 measured `rush-core` at ≈ 493k CU per 12-player tick (LiteSVM, `docs/proof/P1-rush-core.md` §12), so P1b must cut it by about 40 % or more before P2. The per-tick budget is the same at 30 Hz; the per-second load is 1.5× that of 20 Hz. Re-checked in CI on every map. |

---

## 4. Phases at a glance

| Phase | Name | Output | Gate to the next phase |
|---|---|---|---|
| **PB0** | Housekeeping | G4.13 A+B done; SDK 0.18 upgrade; M0 lifecycle green on 0.18 | Lifecycle test passes on the local stack and devnet-as |
| **P0** | Feasibility spikes | 6 measurements (compute, timed task, inputs, state delivery, latency, sBPF = WASM) | All PASS → full plan. Partial → reduced scope (§4.1) |
| **P1** | `rush-core` | Integer motor, collisions, hazards, map blobs, vectors, feel A/B | Feel sign-off; vectors identical across Rust native, sBPF and WASM |
| **P2** | On-chain simulation | Tick, inputs, World, 4 rounds, director, bots, security tests | A 12-bot match finishes on devnet; replay matches |
| **P3** | Unity on-chain client | WASM prediction, input transactions, World subscription, reconcile | A human plays a full match on devnet with a good feel |
| **P4** | Economy | Chips, faucet, markets, Chaos Drops, pots, receipt, payouts, Crown + capsules | Bets and drops settle on devnet; assets visible in a wallet |
| **P5** | Product layer | Arena pool, lobby browser, web app, burner wallet, indexer, replay, cosmetics from the wallet | A stranger plays from the public URL with zero setup |
| **P6** | Privacy + seasons | Private-rollup sealed bets, season leaderboard, trophies | Sealed bets are hidden until close and verified after |
| **P7** | Hardening + launch | Load tests, playtests, PROOF.md, video, submission | Submitted; the URL stays up through judging |
| **P8** | Deferred G4.13 polish (last) | Soft-look budget measured on an idle machine (+ levers if over), build #5 browser FPS table, optional extra Lid Flip colour | Numbers measured (not UNVERIFIED); soft within budget or levers applied |

### 4.1 If P0 only partly passes
- **Compute too high for 12 players:**
  - first drop to 15 Hz;
  - then move bots off-chain (P2.6 option B);
  - then go on-chain round by round, Final → Skill → Survival → Race, with Classic mode serving the rest.
- **Timed task unreliable:** a permissionless **tick bot** (anyone can call `tick()`; the program checks the tick time) replaces the timed task. This keeps the trust model, because tick content is still program-computed.
- **State delivery too slow:** the fanout relay becomes the default delivery. It's untrusted: clients spot-check World hashes against the rollup RPC.
- **Latency too high to feel good:** more client-side input delay and prediction. Judges don't see that hidden latency because their own player is predicted.

---

## 5. Phase details

Each phase lists **Build** (numbered tasks), **Verify** (the checks that close the phase), **Deliverables**, and **Risks / notes**. Write one prompt per phase, `docs/prompts/<phase>-PROMPT.md`, in the same style as the G4 prompts.

---

### PB0 — Housekeeping (before any new work)

**Build:**
1. **G4.13** is running in a separate session. As of 2026-11-21 these are committed: A `8fcd5ca` (tiebreak), B `5904331` (lid render glitch), C `2c89cc0` (saturated lids). Let it finish, or stop it after C. D (soft budget) and F (build #5) can move to P7.
2. Upgrade `ephemeral-rollups-sdk` (Rust + TS) **0.16.2 → 0.18.x**. Stay on Anchor 1.0.2 and devnet-as (validator 1.1.1). Do **not** take 0.20, which needs Anchor ≥ 1.2.2 and the v2 validator.
   - Replace `create_request_scoped_randomness_ix` (removed in 0.17) with `create_request_randomness_ix_const(..., program_identity_pubkey(&ID))`.
   - Move commits to `MagicIntentBundleBuilder` (`FoldableIntentBuilder` import).
3. Re-run `pnpm m0:test:local` and `pnpm m0:devnet` on 0.18. Update docs 07 §1 and §4 with the new API names.
4. Add the `crates/` workspace and the `rush-core` skeleton (`cargo test` green, the float lint on).
5. Note the changes in `docs/15` (§9) and `PROJECT_STATUS.md`.

**Verify:** EditMode tests green (393 expected after G4.13 A). M0 lifecycle 10/10 locally and on devnet-as with 0.18. `cargo test -p rush-core` green.

**Deliverables:** commit `chore(pb0): ER SDK 0.18`, and a `docs/proof/PB0.md` listing the devnet signatures.

**Risks:** the Session keys crate 3.1.1 vs SDK 0.18 dependency tree (check `cargo tree`). `SessionTokenV2` is only created by the Rust/TS SDK, not by `gum-react-sdk` 3.0.10 (V1 only), so create it from our own TS.

---

### P0 — Feasibility spikes (the decision gate)

Six throw-away prototypes. They live in `spikes/p0-*`, are never shipped, and are kept for the record. Each produces a number in `docs/proof/P0-feasibility.md`.

| # | Spike | How | PASS when |
|---|---|---|---|
| P0.1 | **Compute per tick** | Minimal `rush-core`: capsule vs OBB/prism, gravity, ground snap, 12 players walking randomly on a Lid Flip-sized collider set (~120 colliders + grid broadphase). Run it in `solana-program-test` / the local rollup and read the compute units used. Repeat at 300 colliders (Capsule Run scale). | ≤ 150k CU per tick at 12 players on 120 colliders; ≤ 200k at 300 colliders (or a plan to split the tick into 2 instructions) |
| P0.2 | **Timed task at 20 Hz** | `ScheduleTask { execution_interval_millis: 50, iterations: 12000 }` with a unique `task_id`. On 1.1.1, task ids are shared across programs, and a colliding id is accepted but never runs. Log the slot and time of every run on devnet-as for 10 minutes. | ≥ 95 % of ticks within ±1 slot of the schedule; no gap > 250 ms |
| P0.3 | **Input load** | `tools/load`: 12 headless clients, each sending an `InputBatch` transaction every 100 ms (2 ticks each), plus 10 spectator bet transactions per second. | 0 rejected for rate limits; p95 send → confirm ≤ 150 ms from India |
| P0.4 | **State delivery** | Browser + Node `onAccountChange(World, "processed")`. Count updates per second, and the gap between `tick` values. Same for the fanout relay. | ≥ 18 of 20 updates/s received; relay adds ≤ 20 ms |
| P0.5 | **End-to-end latency** | A client sends input stamped tick T and measures when a World update with `applied_tick ≥ T` arrives. | p50 ≤ 150 ms, p95 ≤ 250 ms |
| P0.6 | **sBPF = WASM** | Build the spike `rush-core` to sBPF and to `wasm32-unknown-unknown`, then load the WASM in a WebGL test scene. Feed both 10k random input vectors and compare World hashes. | 10k / 10k identical |

**Verify:** the table is filled with measured numbers and a **go / reduced / no-go** decision is written in doc 15 (P0-1).

**Risks:**
- Devnet rate limits are **UNVERIFIED** and could fail P0.3. If so, ask MagicBlock (Discord) for a dedicated devnet endpoint, and mention it in the submission.
- The validator line can change. Pin the validator identity in config.

---

### P1 — `rush-core`: the shared game core

**Goal:** every gameplay rule exists once, in integer Rust, and plays like today (after a feel sign-off).

**Build:**
1. **`fx` maths.** Q16.16 and mm/i32 helpers, `isqrt`, `normalize`, sin/cos table (Q15, 1024 entries), `atan2` approximation for yaw. Tests: error bounds vs `f64`, and identical results across native, sBPF and WASM.
2. **Collision kit** (`collide/`), matching what the maps actually use: **BoxCollider (OBB)**, **convex prism MeshCollider** (turn tables, bumpers), **sphere** (rolling balls) and **capsule**.
   - `capsule_sweep(feet, r, h, dir, dist) -> Hit {t, normal, surface_id}`
   - `raycast`
   - `penetration` (depenetration vector)

   These match the 5 methods of today's `IMotorWorld`, so the motor port stays 1:1. A uniform-grid broadphase is precomputed per map (cell 2 m) and stored in the map blob.
3. **Motor** (`motor.rs`): port `KinematicMotor.cs` (468 lines). Everything else stays the same in meaning:
   - run/accelerate/decelerate;
   - air control;
   - gravity + fall multiplier;
   - jump apex, coyote time, jump buffer;
   - dive + cooldown + land stun + jump-cancel window;
   - stumble;
   - slope limit 45°, step offset, ground snap, 3 slide iterations, ledge tolerance;
   - stun/knockback override and 0.5 s immunity;
   - respawn on fall.

   Constants come from the new **`movement_v4.json`** (the same keys, re-expressed in ticks at the chosen rate; on-chain 30 Hz); `pnpm gen:movement` emits Rust and C#.
4. **Hazards** (`hazards/`): port `HazardMath.cs` (340 lines, already pure `f(def, variant, seed, tick)`). All 14 types:
   - SpinnerBar, MovingPlatform, Conveyor, SpringPad, TurnTable, GateDoor, MalletSwing, Bumper;
   - GooPlane, CrumbleTile, StarPickup, FallingBlock, RollingBall, LidFlip.

   Replace the float polynomials with the integer table. Surface carry (linear + rotational) goes through `surface_delta`.
5. **Separation:** soft player-vs-player push (today server-only) becomes part of `step()`, so the client can now predict it. That fixes R14 (push corrections).
6. **Map blobs** (`tools/mapblob-export`, a Unity Editor menu):
   - walk the `Gameplay` layer of each map scene → list of OBB / prism / sphere colliders with surface ids;
   - hazard defs, checkpoints + course polyline, spawns, kill-Y, goo params, star field, lid grid;
   - write `maps/<key>.mapblob` (little-endian, versioned header, sha256).

   Lobby + 4 maps. The blob hash goes into `map_pool_v2.json`. Geometry is unchanged, so the old map content hashes still hold for Classic mode.
7. **Rules** (`rules/`): port `RaceRules` (491 lines), `SurvivalRules`, `SkillRules`, `FinalRules` (including the G4.13 lid tiebreak), `TimedRoundRules`, `RoundTargets`, minimum-advance (M4-8), elimination reasons (M4-9), the modifiers (`modifiers_v1`), and the director (`MatchDirector`'s phases).
   - Output: transcript events with the same tags 1–7 as today, plus new tags:
     - 8 `DROP_APPLIED`
     - 9 `MARKET_CLOSED`
     - 10 `MARKET_SETTLED`
   - The transcript hash function stays identical (`fallrush-transcript-v1`).
8. **Bots** (`bots/`): port `BotBrain` (1,114 lines) + `WaypointBot`, behaviour per mode. Tier from the seed (30/50/20). Bots only read what a player can see. They may use `rush-core`'s `raycast`; count the compute units.
9. **Golden vectors:** `tests/vectors/*.bin` covering motor steps, hazard poses, full rounds from a seed + an input script, and transcript hashes. Run in 3 places:
   - `cargo test` (native);
   - the program test harness (sBPF);
   - a Unity EditMode test calling the native/WASM library.
10. **Feel A/B (user sign-off):**
    - a Mac player build with a toggle between Classic (`movement_v2`, PhysX) and `rush-core` (`movement_v4`);
    - route-cost QA on each map with bots (the route ratios 1.0–1.25 and the medians from G4 must still hold);
    - the user plays both and signs off.

**Verify:**
- `cargo test` green, vectors identical across native, sBPF and WASM.
- G4 QA targets met with `rush-core` bots on all 4 maps (soak of 20 bot matches through a headless `rush-core` runner).
- Feel sign-off recorded.

**Deliverables:** `crates/rush-core` v0.1, `movement_v4.json`, 5 map blobs + hashes, `docs/proof/P1-rush-core.md` (CU per function, QA tables, A/B notes).

**Risks:**
- Feel drift. Mitigation: tune `movement_v4` on screen and keep the jump apex, run speed and dive distance identical in mm.
- Swept capsule vs prism is the hardest maths. Budget time for it and test it against PhysX as an oracle.
- Bot compute units (see P2.6).

---

### P2 — On-chain simulation (the program is the server)

> **Done 2026-11-22, Final-first (OC-11).** The Final (Lid Flip, 3 players, bots in `step()`) runs on the rollup in real
> time at 30 Hz; the other three rounds stay Classic and a chain worker bridges them into the `Match`
> (`start_final` handoff). The full 4-round on-chain mode exists as an experimental option (it runs, in slow motion on
> Capsule Run). Measured: devnet-as runs at most one task execution per 10 ms slot. See `docs/proof/P2-onchain-sim.md`.

**Accounts (new):**

| Account | Where | Size (target) | Fields |
|---|---|---|---|
| `Arena` (PDA `["arena", id]`) | Delegated, reused across matches | ~1.5 KB | status, current match id, ruleset, map pool hash, roster (12 × {kind, wallet / session pubkey, cosmetic, input_acc}), timed-task id, fee-payer link |
| `World` (PDA `["world", arena]`) | Delegated, zero-copy | ≤ 2 KB | tick, phase, round, map key, hazard seed, per player {pos mm i32×3, vel i32×3, yaw, flags, timers, checkpoint, score, lid shelf/falls}, goo height, star field bits, lid grid state, drop queue (8), transcript hash, event seq |
| `InputBuf` (PDA `["in", arena, slot]`) | Delegated, written only by that slot's session key | ~256 B | ring of 16 × {tick u32, buttons u8, move i8×2, yaw u8}, last_tick |
| `EventLog` (PDA `["ev", arena]`) | Delegated | ≤ 8 KB ring | last N transcript events, for spectators and indexers |
| `Match` (existing) | Created per match on Solana, delegated | 1.4 KB | as today (deal, slots, champion, transcript), receives the final state |
| `MapBlob` (PDA `["map", key]`) | **Not delegated** (cloned read-only into the rollup) | ≤ 64 KB | the map blob bytes (written by an admin in chunks) |

**Instructions:**

| Instruction | Signer | Notes |
|---|---|---|
| `init_arena`, `delegate_arena` | game authority | Once per arena (pool of N, see P5) |
| `upload_map_chunk`, `seal_map` | game authority | Writes the map blob and checks its sha256 against `map_pool_v2` |
| `join_lobby(slot?, cosmetic)` | player wallet **or** session key | Assigns a slot and a session key, and checks the `SessionTokenV2` |
| `leave_lobby` | player | |
| `lobby_tick` (timed task) | timed-task signer / anyone | Countdown, roster lock, bots fill free slots, then `create_match` on Solana |
| `request_deal` / `deal_callback` | (existing, VRF in the rollup) | The seed drives the map plan, modifiers, hazard seeds, star bursts and bot tiers |
| `submit_input(batch)` | session key of that slot | Writes `InputBuf` only. Sanitiser runs here (`InputSanitizer` rules). Rejects ticks older than `World.tick` − 8 or newer than + 8. At 30 Hz a client sends one batch every 100 ms carrying 3 ticks: 120 tx/s for 12 players, the rate P0.3 measured |
| **`tick()`** (timed task, 30 Hz) | timed-task signer / anyone (checks `now ≥ next_tick_time`) | Reads `World` + 12 `InputBuf`s + `MapBlob`, runs `rush_core::step`, writes `World`, appends events, applies due Chaos Drops, closes markets at phase edges |
| `complete_round` + commit | inside `tick()` at round end | Uses an intent bundle with a delegated fee payer, so the 10-commit limit doesn't apply |
| `finalize` | inside `tick()` at match end | Writes `MatchReceipt`, then commit + undelegate it, with the payout follow-up call (P4) |
| `abort_arena` | game authority | Emergency reset; the match is marked aborted and bets are refunded (P4) |

**Build:**
1. **P2.1 Zero-copy World + inputs.** Layout structs with `bytemuck::Pod`, size asserts, a version byte.
2. **P2.2 `tick()`.** Map the input accounts by `remaining_accounts` in slot order, then call `rush_core::step`. Missing or late input means the last input is repeated, as today's server does. Log a compute-unit meter in debug builds.
3. **P2.3 Timed tasks.** Schedule the `tick()` task at arena start and cancel it at the end. Unique `task_id = hash(program, arena, match)`. Write a watchdog: if no tick happens for 500 ms, anyone may call `tick()` (the permissionless fallback).
4. **P2.4 Director on-chain.** Lobby → Countdown → Dealing (VRF) → Dealt → per round: Intro / Countdown / Rush / Resolve / Intermission → Champion → Receipt. Same tick counts as `MatchDirector` (already 30 Hz).
5. **P2.5 Rounds.** Enable them in order:
   1. Lid Flip (the Final);
   2. Star Fountain (Skill);
   3. Goo Tower (Survival);
   4. Capsule Run (Race).

   One map per PR, each with vectors and a devnet run.
6. **P2.6 Bots.**
   - **Option A (preferred):** bots run inside `tick()`, sharing the compute budget.
   - **Option B:** an off-chain bot runner (TS + `rush_core.wasm`) owns bot slots' session keys and sends inputs like a player. It is flagged `kind = bot_runner` in the roster, and anyone can run it.

   Decide from the P0.1 and P1 compute numbers.
7. **P2.7 Replay.** `tools/replay`: fetch a match's input transactions (archive from P5, or the rollup RPC while still available; retention is **UNVERIFIED**), re-run `rush-core` natively, and compare every round's transcript hash and the final hash with the `MatchReceipt`.
8. **P2.8 Security tests** (program-test + local rollup):
   - an input signed by another slot's key is rejected;
   - an expired or revoked session token is rejected;
   - inputs outside the ±8-tick window are rejected;
   - NaN/overflow-like values are clamped by the sanitiser;
   - double join;
   - a bot slot can't be claimed;
   - `tick()` called too early is a no-op;
   - an arena re-entering an old match is rejected;
   - an oversized `remaining_accounts` list is rejected;
   - a VRF callback not from the VRF identity is rejected (existing);
   - the commit path works past 10 commits with the fee payer.

**Verify:**
- A 12-bot match runs start to finish on **devnet-as**, unattended, 5 times in a row.
- Replay matches 5/5.
- Compute units per tick recorded for every map (max and p95).
- The security suite is green.

**Deliverables:** program v2 deployed to devnet (the program ID can stay the same, as an upgrade), `docs/proof/P2-onchain-sim.md` with signatures, compute-unit table and replay hashes.

**Risks:**
- The 64 KB map blob as a read-only clone (**UNVERIFIED** size limits for clones). Fallback: split it into one account per map section.
- Write-lock throughput on `World` (one writer per tick, fine). `InputBuf` per slot avoids contention.

---

### P3 — Unity on-chain client

**Goal:** the same game, look and feel, now talking to the rollup instead of the server.

**Build:**
1. **P3.1 `rush_core.wasm` in WebGL.**
   - `Plugins/WebGL/` + a bridge file loader. Exports: `rc_init(mapblob)`, `rc_predict(state_ptr, input) -> state`, `rc_hash(world)`.
   - Native `.bundle`/`.so` for the Editor and the Mac player.
   - Batch calls (one call per frame) so bridge overhead stays small.
2. **P3.2 `fallrush-chain.js`** (`packages/chain`, bundled with esbuild):
   - holds the burner keypair (localStorage) and the `SessionTokenV2`;
   - ER connection (devnet-as), optional Magic Router for base calls;
   - `sendInput(batch)` (fire-and-forget, never awaits a confirm);
   - `subscribeWorld(cb)`, `subscribeEvents(cb)`, `joinLobby()`, `placeBet()`, `buyDrop()`.

   Exposed to Unity through `FallRushChain.jslib` + `SendMessage` callbacks. **Remove Solana.Unity-SDK** from the WebGL build (−4 MB; Phantom connect was never verified).
3. **P3.3 `OnChainSession` (a new `IChainAdapter` implementation + transport):**
   - lobby join → slot;
   - World updates → the same `Snapshot` structs the HUD and visuals already consume;
   - transcript events → the existing `mevt` handlers (deal, qualified, eliminated, round results, champion).

   **The HUD, Deal screen, receipt, chain chip, claw and crown code is reused unchanged.**
4. **P3.4 Prediction:** `OnChainPrediction` uses `rc_predict` with tick-keyed history, exactly the M2b pattern:
   - rewind + replay on World mismatch;
   - 120 ms smoothing;
   - snap if off by more than 2 m.

   Input delay is sized from P0.5. Remote players are interpolated (100–150 ms adaptive, as today).
5. **P3.5 Tick clock:** `TickSync` driven by World `tick` + slot time. Shows "! LAGGING" when there are no World updates for 300 ms.
6. **P3.6 Chain chip states:**
   - `◆ Rollup ✓` live;
   - `⬢ Solana ✓` after a round commit;
   - `! Pending` when updates stall;
   - a new **tick health** row in F3 (updates/s, input confirm p50/p95, prediction corrections/5 s).
7. **P3.7 Mode switch:** the menu gets **Play On-Chain** (default) and **Play Classic** (the server, fallback). Both share all visuals.

**Verify:**
- A human + 11 bots, WebGL → devnet-as, full match.
- Corrections < 1 per 5 s while running (the M2b bar).
- Feel sign-off by the user on build #N.
- WebGL build ≤ 25 MB.
- 60 FPS target as in G4.

**Deliverables:** WebGL build, `docs/proof/P3-client.md` (the tick-health table and correction counts), a short capture.

**Risks:**
- Browser timer throttling in hidden tabs (R12) now also stops input transactions. The program treats missing inputs as repeats, and disconnect after 20 s stays as today.
- Bridge-file memory copies: use one shared heap buffer.

---

### P4 — Economy: chips, markets, Chaos Drops, pots, assets

**Build:**
1. **P4.1 RUSH chips.**
   - A devnet SPL mint `RUSH` (6 decimals) with the mint authority on a program PDA.
   - Balances live in rollup token accounts (**Ephemeral SPL Token** program `SPLxh1LVZzEkX99H6rqYizhytLWPZVV296zyYDPagv2`), with program-held vaults per arena.
   - **Spike first (½ day):** deposit → transfer in the rollup → withdraw on Solana. v0.1.0 is immature.
   - **Fallback:** a program-internal `ChipBalance` PDA per wallet, delegated, minted to real SPL on undelegate / claim.
2. **P4.2 Faucet:** `claim_faucet()` gives 1,000 RUSH per wallet per 24 h (wallet or burner). It's a rate-limited PDA. Guests get chips on first launch, with no popup.
3. **P4.3 Rush Markets** (shared pool, everyone splits it). One `Market` account per (arena, match, kind):
   - **Kinds:**
     - `Champion` (12 outcomes), open in the lobby and between rounds;
     - `Qualify(round, slot)` (yes/no);
     - `Survive(round)` (pick a slot).
   - `place_bet(market, outcome, amount)` with a session key. It only works while `market.open_tick ≤ World.tick < market.close_tick`, and close = the `RUSH` tick of that round, **enforced by the program**.
   - **Settlement inside `tick()`** at the round result: pool × (1 − fee 0 %) split by stake on the winning outcome. Refund if nobody won or the match is aborted.
   - **Market maker:** a labelled bot bettor seeds each market with a small pool spread by bot tier, so a solo judge sees real odds.
   - Max 64 bets per market (an account-size cap); larger amounts go into aggregates per outcome.
4. **P4.4 Chaos Drops.**
   - `buy_drop(kind)` during `Rush`, priced in RUSH. Kinds:
     - Goo Surge (+0.6 m for 3 s);
     - Bumper Rain (3 falling bumpers);
     - Lid Frenzy (2 extra lid flips);
     - Star Shower (+4 stars);
     - Wind Gust (side push 2 s).
   - Each kind has a price, a per-round cap of 3, and a 5 s global cooldown.
   - The program queues the drop at `World.tick + 15` (a 0.75 s telegraph). VRF (or the hazard seed + drop index) picks the spot.
   - `rush-core` applies it inside `step()`. **Drops hit everyone equally**, and no drop can target a slot.
   - The client predicts it because the drop queue is in `World`. The HUD banner reads "🌀 GOO SURGE by <name> ◆".
   - The drop design goes through the G4 fairness QA (route cost, survival margins) before it ships.
5. **P4.5 Skill pots (optional lobbies).**
   - "Crown Pot" lobbies with an entry stake (e.g. 100 RUSH), **humans only**, minimum 2 humans.
   - Payout ladder by human rank: 60/25/15.
   - Bots fill the remaining slots but never take a share.
6. **P4.6 Receipt + payouts.**
   - At finalize: a `MatchReceipt` (match id, ruleset, map blob hashes, VRF seed, roster hash, final transcript hash, champion, placements, market results, drop log hash).
   - Commit + undelegate it with a **follow-up call on Solana** (`#[action]` handler). That handler:
     - **(a)** withdraws winnings from the arena vault to winners' accounts (or credits rollup balances);
     - **(b)** mints the **Crown Capsule** (Metaplex Core) to the champion's wallet, or to a claimable PDA for guests;
     - **(c)** mints **VRF Capsule Drops**: every human gets 1 capsule, rarity from VRF (Common 70 / Rare 22 / Epic 7 / Legendary 1), and a shell from `CosmeticPresets` + new variants.
   - The docs say these follow-up calls are **not atomic** with the commit. So the handler is idempotent (a `paid` flag per receipt) and a keeper retries until `paid`. The handler checks the injected `escrow` signer.
7. **P4.7 In-game UI** (Unity, reusing the G3 UI kit):
   - a bet panel for eliminated players and spectators;
   - a drop bar during Rush (when spectating);
   - pool and odds shown on the Deal screen;
   - capsule pop on the champion screen;
   - a "claim with Phantom" button on the receipt.

**Verify:**
- Devnet: 3 matches with bets from 3 wallets (≥ 1 session key), ≥ 2 drops each, a pot lobby with 2 humans.
- Every payout matches a hand calculation.
- Crown + capsules visible in a devnet wallet / Core explorer.
- Follow-up call idempotent: forced failure + retry.
- Fairness QA with drops enabled.

**Deliverables:** `docs/proof/P4-economy.md` (signatures, payout table, asset addresses), RUSH mint address.

**Risks:**
- Ephemeral SPL immaturity (the fallback is ready).
- The compute cost of settlement inside `tick()`: settle in a separate `settle_market` instruction called by the timed task one tick later, if needed.
- **Mainnet would need a legal review.** README: "devnet test chips only, no real-money value".

---

**P4 as built (2026-11-23, `docs/proof/P4-economy.md`):**
- **Chips:** a program `ChipBalance` PDA per wallet (OC-12; the Ephemeral SPL spike failed 0/5), faucet 1,000 RUSH /
  24 h with no popup; real SPL RUSH minted by the program on cash-out.
- **Markets:** Champion + Final winner (◆ program-settled from the on-chain Final) and Qualify R1–R3 ("pick who
  qualifies", ● server-recorded); windows by program state (Champion / Final winner close at the RUSH tick, Qualify at
  `start_round`), **settlement in a separate permissionless op** (not inside `tick()`), shared pool, refunds incl. the
  Classic fallback; the labelled market maker seeds every market.
- **Chaos Drops:** Lid Frenzy, Bumper Rain, Wind Gust, Sweeper Rush (Goo Surge / Star Shower are other maps' and stay
  for when those rounds go on-chain); telegraph **23 ticks** at 30 Hz (the plan's "+15" was 20 Hz); `buy_drop` writes a
  separate `DropQueue` that `tick()` reads; QA + CU in the proof §4.
- **Receipt:** in `Match` (replaces the unused Fan Picks, same size); a second VRF draw rolls the capsules; the
  follow-up call is the permissionless `mint_rewards` op on Solana (OC-13: program-minted Metaplex Core, not a Magic
  Action); pots cut.
- **UI:** chips pill, bet panel, drop bar, drop banner + in-world telegraphs, receipt card with Claim with Phantom, F3.

### P5 — Product layer

**Build:**
1. **P5.1 Arena pool.** N = 4 arenas, delegated once, reused. A delegated fee payer + `magic_fee_vault` topped up by a keeper (`lamportsDelegatedTransferIx`). A `Directory` account lists arena status for the lobby browser.
2. **P5.2 Quick Play matchmaking:** join the fullest open arena; the countdown starts at ≥ 1 human (bots fill at lock, as today).
3. **P5.3 Web app** (`web/`, Vite + TS, served as static files):
   - `/` landing: logo, Play, live arenas, recent matches;
   - `/play`: Unity WebGL;
   - `/live/:arena`: 2D top-down race view drawn straight from the World subscription, odds, bet, drop buttons, event feed;
   - `/replay/:match`: plays the archived inputs through `rush_core.wasm` in a light Three.js view, or opens in Unity;
   - `/receipt/:match`: Rollup ✓ → Solana ✓ timeline, explorer links, payouts, Crown + capsules;
   - `/me`: wallet, chips, capsules, Crown, season stats; "equip" a capsule shell.
4. **P5.4 Wallets:**
   - burner by default (localStorage, exportable);
   - "Upgrade to Phantom" moves chips and claims assets by signing once;
   - session keys always do the in-game signing.
5. **P5.5 Cosmetics from the wallet:** the client reads owned Core capsules (via DAS-style lookup or a `getProgramAccounts` filter) and offers those shells. They are passed in `join_lobby(cosmetic)`, and the program checks ownership via the asset account. Cosmetic only, never stats (GDD pillar 4).
6. **P5.6 Indexer** (`services/indexer`, optional and stateless; anyone can run it): stores every input/bet/drop transaction per match into files, serves `/replay`, and hashes them into the receipt check.
7. **P5.7 Fanout relay** (only if P0.4 required it): subscribes to `World` once and broadcasts over WS. Clients check a World hash against the rollup every 2 s.
8. **P5.8 Hosting:** the static web app + WebGL on the Hostinger VPS nginx (doc 13 rules: don't touch other sites), plus the indexer, relay and keeper in Docker Compose. The **game server isn't needed** in on-chain mode; Classic mode keeps its container as a fallback.

**Verify:**
- From a clean browser on another machine: open the URL → playing within 30 s, no popups.
- A spectator on a phone bets and drops chaos.
- The receipt page shows every link.
- Replay of a public match matches its receipt.

**Deliverables:** public URL, `docs/proof/P5-product.md`.

**As built (P5, 2026-11-23; `docs/proof/P5-product.md`, doc 15 OC-14…OC-20):**
- Public at `https://play.<VPS_IP-dashed>.sslip.io` on the shared VPS (compose stack `fallrush-*`, doc 13 §2a). The
  hybrid stays the shipped mode: rounds 1–3 on the Classic server (2 game servers), the Final on the rollup (2 arenas,
  one keeper daemon each, resuming from on-chain state). No `Directory` account: the directory is the servers'
  `status.json` via the keepers and `/api/status`.
- P5.1 pool = 2 arenas (not 4; D-HOST measured 2 fine on 2 vCPU). P5.4: burner + session keys + gas station; key
  backup / restore; Phantom connect + claim + move RUSH (real Phantom UNVERIFIED). P5.5: capsule shells checked by the
  Classic server over RPC (the on-chain `join_lobby` cosmetic stays a preset). P5.6: `tools/indexer` (archive + replay).
  P5.7 fanout relay: not needed (clients subscribe to devnet-as directly).
- The follow-up call is a real **Magic Action** now (`finalize_match_action`, validator-run `mint_rewards_action`);
  the keeper's `mint_rewards` is the retry. Ephemeral SPL: 5/5 round trips, chips still `ChipBalance` (OC-15).
- `/replay` re-simulates the archived Final with `rush_core.wasm`, byte-exact per tick (inputs latched by the program +
  Chaos Drops entering the World) — "replay = on-chain ✓" on all 6 public matches.

---

### P6 — Privacy + seasons

**Build:**
1. **P6.1 Sealed bets on a Private ER** (`devnet-tee.magicblock.app`).
   - A `SealedBook` per market on the private rollup with `EphemeralPermission { is_private: true }`; members are none until close.
   - At close, the program reveals the totals into the public `Market` (a cross-rollup handoff through a commit to Solana + delegation to devnet-as).
   - **Simpler variant if the cross-rollup path is hard:** commit-reveal on the normal rollup:
     - commit `sha256(outcome ‖ amount ‖ salt)` before close;
     - reveal after close;
     - unrevealed bets are forfeited to the pool.
   - Client: `verifyTeeRpcIntegrity` + `getAuthToken` (requires SDK 0.18 `access-control` and the TEE auth flow in the browser).
2. **P6.2 Seasons:** a `Season` account with weekly id, top champions, top predictors (net RUSH from markets) and drop MVPs. A season trophy Core asset is minted at season end.
3. **P6.3 Achievements on the receipt** (GDD): first crown, 3-round survivor, perfect predictor.

**Verify:** a bet is invisible (RPC returns no data to non-members) before close and correct after; the season board updates from receipts.

**Risks:** private-rollup auth inside WebGL; the cross-rollup handoff latency. Keep commit-reveal as the shipped default if the private rollup isn't stable.

---

### P7 — Hardening, playtests, launch

**Build / do:**
1. **P7.1 Load:** 4 arenas × 12 clients (headless) + 30 spectators betting for 30 minutes. Record compute units, missed ticks and the confirm-time distribution.
2. **P7.2 Chaos tests:**
   - kill the keeper;
   - kill the relay;
   - throttle a client;
   - revoke a session mid-match;
   - force a follow-up call to fail.

   Each has a documented recovery.
3. **P7.3 Playtest 1:** ≥ 8 humans + 3 spectators. Bug list, feel notes, market and drop fun check.
4. **P7.4 Fixes** from the playtest (the deferred G4.13 items are P8, last).
5. **P7.5 Playtest 2, recorded:** two clean full matches for the video.
6. **P7.6 PROOF.md:** one explorer link per system. That covers:
   - arena delegate;
   - VRF deal callback;
   - a `tick()` transaction (with its compute units);
   - an input transaction signed by a session key;
   - a bet;
   - a Chaos Drop;
   - a round commit;
   - finalize + follow-up call;
   - the Crown and a Capsule asset;
   - the RUSH mint;
   - a sealed bet (P6).
7. **P7.7 README:** the pitch, play link, video, architecture diagram (§1), trust model (§1), "how to replay a match yourself", the `rush-core` crate as a reusable "3D character controller for Ephemeral Rollups", and the roadmap (mainnet, permissionless arenas, community maps).
8. **P7.8 Video (3:00)**, see §7.
9. **P7.9 Submit:** repo public, pitch + demo, explorer link, program address, prize wallet.

**Verify:** submitted; the URL monitored until the results announcement.

### P8 — Deferred G4.13 polish (last; user, 2026-11-21)
Render only; no map, rules or hash change. Kept for last (user call). Nothing here blocks the submission.

**Do:**
1. **P8.1 Idle measurement (G4.13 D, G4.13-3):** with the machine idle (load < 4, no other project's jobs), run build #5
   (`client-unity/Builds/WebGL-g413-build5/`) in Chrome: soft High, soft Low, candy High, one full 12-player bot match each
   (`docs/proof/G4-B-art.md` §G4.12 §3 steps). Record per map: FPS, 1 % low, draw calls, tris. This also replaces build #5's
   UNVERIFIED browser table (G4.13-5).
2. **P8.2 Soft budget levers, only if soft is still over** (≤ 300k tris, ≤ 150 draw calls), in order, stop once within budget:
   shadow distance 60 → 40 m (`GraphicsQuality.ShadowDistance`); one crowd row per stadium tier; soft-Low shadows off
   (G4.8-8); no shadow casting on far crowd / backdrop props. Then one WebGL build and a re-measure.
3. **P8.3 Optional: more Lid Flip colour (G4.13-2).** Only on the user's call: a different lit telegraph colour, or brighter ring
   hues (lime / aqua); the lit-vs-calm luma tests stay.

**Verify:** measured numbers in `docs/proof/G4.13-post-freeze.md` §D / §F; EditMode green; map hashes unchanged.

---

## 6. Test strategy (all phases)

| Layer | Tool | Gate |
|---|---|---|
| `rush-core` maths, collisions, motor, hazards, rules | `cargo test`, proptest fuzz, golden vectors | 100 % vectors identical across native, sBPF and WASM |
| Feel / fairness | headless `rush-core` bot QA (route cost, medians, survival margins), the G4 targets | Every G4 target holds, and holds again with Chaos Drops enabled |
| Program | program-test + local rollup stack (`mb-stack`) + 2 VRF oracles | Security suite (P2.8) + economy suite (P4) green |
| Integration | devnet-as runs, `tools/replay` | Replay matches the receipt every time |
| Client | Unity EditMode (existing 393 + new bridge-file/WASM vectors), WebGL capture | Corrections < 1 per 5 s; FPS as in G4 |
| Load / chaos | `tools/load` | P7.1 / P7.2 tables |

---

## 7. Demo video (3:00, new story)

| Time | Shot | Line |
|---|---|---|
| 0:00–0:10 | The capsule machine, logo | "Fall Rush: the first 3D party game whose physics runs on Solana." |
| 0:10–0:25 | Click a URL → playing, no wallet popup | "No wallet, no popups. Session keys sign every move." |
| 0:25–0:40 | Deal screen + VRF transaction on the explorer | "MagicBlock VRF deals the match." |
| 0:40–1:15 | Round 1 gameplay + the F3 tick health + a `tick()` transaction with its compute units | "Every tick of this race runs in a MagicBlock Ephemeral Rollup, 20 times a second." |
| 1:15–1:40 | A phone spectator bets; odds move; buys **Goo Surge**; the banner in game | "Spectators bet and drop chaos in real time." |
| 1:40–2:10 | The Final; a knockout settles the market on screen | "The program decides who's out, and settles the market itself." |
| 2:10–2:35 | Crown Capsule + VRF capsule pop → wallet; receipt page Rollup ✓ → Solana ✓ | "The crown and your capsules are yours." |
| 2:35–3:00 | Replay tool matches the receipt hash; architecture; trust line; URL | "Don't trust us. Replay it." |

---

## 8. Decisions this plan adds (also in doc 15)

| ID | Decision |
|---|---|
| OC-1 | Target = fully on-chain sim on the rollup; Classic mode is the fallback until on-chain mode matches it (user, 2026-11-21). |
| OC-2 | RUSH devnet SPL chips replace ADR-006's "no tokens". No real money; mainnet needs a legal review (user, 2026-11-21). |
| OC-3 | Motor retune in `rush-core` / `movement_v4`; the user signs off on the A/B (user, 2026-11-21). **Signed off at 30 Hz** in the P1 Feel Lab (2026-11-21; 15 Hz laggy). |
| OC-4 | ER SDK 0.18.x on Anchor 1.0.2 and devnet-as; not 0.20 (needs Anchor ≥ 1.2.2 + validator v2). |
| OC-5 | One Rust core compiled to sBPF + WASM (bit-for-bit by construction). |
| OC-6 | Solana.Unity-SDK removed from the WebGL build; chain I/O through the bridge file → TS bundle. |
| OC-7 | BOLT not used (deprecated, Anchor 0.x only). |
| OC-8 | M5 Rush Relay (Postgres) and the M8 Next.js site are dropped; replaced by the program + optional stateless helpers and a Vite web app. |
| OC-9 | Chaos Drops are map-wide, telegraphed for 0.75 s, capped, and never target a slot. |
| OC-10 | Pots pay humans only; bots never take a share. Markets are seeded by a labelled market-maker bot. |

---

## 9. Risk register (new)

| ID | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| OR-1 | Devnet rate limits block 200+ transactions/s per arena | Med | High | P0.3; ask MagicBlock for an endpoint; reduce the input rate (3 ticks per transaction) |
| OR-2 | `tick()` over the compute budget on Capsule Run | Med | High | Grid broadphase, bots off-chain, split the tick into 2 instructions, 15 Hz |
| OR-3 | Timed-task jitter | Med | Med | Permissionless tick fallback; the client tolerates ±1 tick |
| OR-4 | Feel worse than Classic | Med | High | A/B, input delay tuning, keep Classic as the default until signed off |
| OR-5 | Ephemeral SPL v0.1 bugs | Med | Med | `ChipBalance` PDA fallback |
| OR-6 | Follow-up call not atomic | Known | Med | Idempotent handler + keeper retry |
| OR-7 | Private rollup in the browser too hard | Med | Low | Commit-reveal default |
| OR-8 | Rollup down during judging | Low | High | Classic mode button + recorded video + replay files |
| OR-9 | Rollup transaction history not retained for replay | Med | Med | Our indexer archives every match |
| OR-10 | 8 GB machine: Rust + Unity + load tests | High | Med | One heavy job at a time; offload load tests to the VPS |

---

## 10. What stays exactly as it is
- Maps (geometry, keys, modes), the art, audio, VFX, HUD, camera, Rushlings, claw, crown ceremony.
- Round formats, targets and the minimum-advance rules. The G4.13 lid tiebreak is ported as is.
- The transcript format and tags 1–7, the VRF caller seed, the roster hash.
- The honest-trust principle: we state exactly what is and isn't proven.

## 11. Research summary (2026-11-21)

- **SDK and runtime:**
  - SDK lines: 0.18.0 (2026-11-18) supports validators v1 + v2 on Anchor 1.0; 0.20 is v2-only and needs Anchor ≥ 1.2.2.
  - Rollup limits: 200k CU per instruction, 1.4M per transaction, 64 KB transactions, delegated accounts ≤ 10,240 B to commit.
  - Fees: rollup transactions are free; commits are limited to 10 per delegation unless a fee payer is set up.
- **Platform features:**
  - Timed tasks: `ScheduleTask` (ms interval, about 10 ms granularity, fee-free; task ids shared across programs on 1.1.1).
  - Follow-up calls on Solana: `MagicIntentBundleBuilder` + `#[action]`, not atomic with the commit per the docs.
  - Ephemeral SPL Token v0.1.0.
  - Private rollups: TDX on `devnet-tee`, Permission Program `ACLseo…`.
  - VRF in the rollup: free, < 50 ms.
  - `SessionTokenV2`.
  - BOLT: deprecated.
- **Winners:**
  - Physics in the rollup: Ultra Pong (V8, 20 ticks/s).
  - Simulation in a private rollup: Blackout (V9).
  - Sealed answers: Herd (V8, 1st).
  - Polished Unity game: Battle Cities (V9, 1st).
  - Every blurb credits a feature that carries real load.
- **V10:**
  - Theme "assets they can sell anytime";
  - named examples: skill games, gacha, packs;
  - eligibility: ER, Private ER or VRF;
  - devnet activity counts.
