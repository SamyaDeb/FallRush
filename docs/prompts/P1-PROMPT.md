# P1 (full): `rush-core` + real maps + feel A/B (new session, alongside PB0 and P0)

You are the gameplay-simulation engineer on **Fall Rush**, a Unity 6.3 WebGL 12-player knockout party game for the MagicBlock Solana Blitz V10 hackathon. The game is moving fully on-chain onto a MagicBlock Ephemeral Rollup.

You build **`rush-core`**: one `no_std` Rust crate with **integer maths only**, holding the whole game simulation (motor, collisions, hazards, round rules, deal, bots). It will be compiled to:
- **sBPF**, inside the `fall_rush` program, where it is the authority;
- **WASM**, in the browser, for prediction.

Same source plus integer maths means client and chain agree bit for bit, by construction. Then you prove it plays the **real maps** like today's game, and give the user a **feel A/B** to sign off.

**Read first, in full:** `docs/19-ONCHAIN-PLAN.md` (committed on `main` at `4ea97b3`), especially §2, §3, §5 P1, §6 and §10.

**This session runs P1 end to end, in two parts:**
- **Part 1 (A–J):** pure Rust: core, vectors, `rush-sim`. No Unity.
- **Part 2 (K–O):** Unity Editor work in your own worktree: map-blob exporter, C# reference traces, real-map bot QA, a Feel Lab in a Mac player build for the user's A/B, and the proof.

You may start Part 2's Unity setup (K.1) while Part 1 is in progress, but **K.2 onward needs C, D and F green** (collisions, motor, world).

---

## 0. State at hand-off (2026-11-21, checked)

| Session | Where | State | Owns |
|---|---|---|---|
| G4.13 | main checkout | **Done**: proof committed (`4417c64`); deferred polish is now doc 19 **P8** (last) | — |
| PB0 | `../FallRush-pb0`, branch `pb0` (@ `d047291` "docs(pb0): proof") | ER SDK 0.18.0 upgrade done; may still be finishing devnet / merge notes | `programs/**`, `docs/07-*`, `docs/proof/PB0.md` |
| P0 | `../FallRush-p0`, branch `p0` | Running feasibility spikes (the local MagicBlock stack was up at hand-off; load average ~100) | `spikes/**`, `tools/load/**`, `docs/proof/P0*` |

**Facts:**
- **`main` @ `4ea97b3` is clean.** It holds doc 19 (with P8), docs 14 / 15 / 18, `PROJECT_STATUS.md`, the prompts and `scripts/heavy-lock.sh`.
- **`crates/` doesn't exist yet. You create it** (PB0's task E moved to you).
- **P0's `spikes/p0/sim`** is a throw-away cost sim. You may *read* it and P0's results (`../FallRush-p0/docs/proof/P0-feasibility.md` when it appears); never depend on it or edit it.
- **P0-1 (tick rate 20 / 15 / 30 Hz; bots on- or off-chain) is not decided yet.** It's the user's decision after P0. So:
  - everything is **tick-rate parametric**;
  - QA runs at **all three rates**;
  - the Feel Lab switches rate live;
  - `movement_v4.json` ships with `tickRateHz: 20` as the default.

  Bots are one function used either way.
- **Solana crate versions** for your sBPF check crate: match PB0's `programs/Cargo.lock` (`git -C /Users/samya/Downloads/FallRush-pb0 show pb0:programs/Cargo.lock`). That's SDK 0.18.0, `anchor-lang =1.0.2`, `solana-program` 3.0.0.
- **Tests on `main`:** EditMode **396/396** (G4.13 final).
  - Map content hashes (`MapSceneTests`, `client-unity/Assets/_Game/Tests/Vectors/map_hashes_v1.txt`) must not change:
    - Capsule Run `50125d89…`
    - Goo Tower `5d5a00be…`
    - Star Fountain `0bde7d75…`
    - Lid Flip `b0684a98…`
    - Lobby `77dcefe7…`
- **Scene names → maps:**
  - `Map_BlockPartyDash` = Capsule Run (Race);
  - `Map_GooTower` = Goo Tower (Survival);
  - `Map_StarScramble` = Star Fountain (Skill);
  - `Map_CrumbleShelf` = Lid Flip (Final);
  - `Map_Lobby`;
  - `Map_Sandbox` (motor test course: ramps 15/30/44/50°, steps 0.2/0.3, gaps, walls, beam).
- **Machine:** 8 GB M2 Air, 32 GB disk free, `client-unity/Library` = 5.0 GB. No dotnet/mono (C# runs only inside Unity). Rust targets `wasm32-unknown-unknown` + `wasm32v1-none`, `cargo build-sbf`, Node 24.
- **Unity tooling:**
  - `scripts/m1-unity.sh <label> [--tests] <args>` runs batch Unity on **that checkout's** `client-unity`. It refuses while any Unity runs, and logs to `.m1-logs/`.
  - `Editor/Build.cs` has `WebGL`, `Server` and `MacDevServer`, but **no Mac client build** (you add one, N.3).
  - Existing C# QA: `Net/RouteQa.cs`, `MapQa.cs`, `ModeQa.cs` (G4.7 targets, see L/M).
- **F-keys already used:** F1, F3 (HUD), F6, F7 (look), F9 (chain chip). Free: **F2, F4, F5, F8, F10**.
- **Units in the live game** (`packages/shared/config/movement_v2.json`, 30 Hz): mm, mm/s, mm/s², ms, deg, permille.
  - capsule r 310, h 1,000, skin 10;
  - run 5,000, accel 28,000, decel 30,000, air control 300 ‰;
  - gravity 20,000, fall multiplier 1,350 ‰, max fall 30,000;
  - jump apex 1,200, coyote 100 ms, buffer 120 ms;
  - dive 7,000 / 2,500, cooldown 1,000, land stun 450, cancel window 150, slide decel 12,000;
  - stumble from 3,000 mm, 300 ms at 550 ‰;
  - slope 45°, step 250, snap 250, 3 slides, ledge tolerance 50;
  - push 2,500;
  - respawn 1,000 ms below kill-Y −20,000.

**C# sources to port** (`client-unity/Assets/_Game/Scripts/`):

| Area | Files (lines) |
|---|---|
| Motor | `Shared/KinematicMotor.cs` (468), `MotorState.cs`, `MovementConfig.cs`, `MovementConfigData.g.cs`, `MovementTuning.cs`, `InputCmd.cs`, `InputSanitizer.cs`, `Vec3.cs`, `Yaw256.cs`, `IMotorWorld.cs` (5 queries) |
| Tick loop | `Shared/SimCore.cs` |
| Hazards | `Shared/Hazards/HazardMath.cs` (340): 14 types, `pose = f(def, variant, seed, t)`, collision profiles |
| World queries | `Net/PhysxMotorWorld.cs` (235), `Tests/EditMode/AnalyticWorld.cs` (an analytic `IMotorWorld`, useful as a reference) |
| Rules | `Shared/RaceRules.cs` (491), `SurvivalRules.cs`, `SkillRules.cs`, `FinalRules.cs` (incl. the G4.13 lid tiebreak `8fcd5ca`), `TimedRoundRules.cs`, `RoundTargets.cs`, `RoundParams.cs`, `ModifierEffects.cs` + `ModifiersData.g.cs`, `MatchDirector.cs` (856), `MatchTypes.cs`, `Deal.cs` |
| Bots | `Shared/BotBrain.cs` (1,114), `WaypointBot.cs` |
| Map authoring | `Net/MapDefinition.cs`, `MapInfo.cs`, `Hazard*.cs` authoring components, `RaceCheckpoint.cs`; `Editor/KitBuilder.cs` (`Prism(...)` = convex MeshCollider), the map builders |
| Tests to mirror | `Tests/EditMode/*` (`MotorTests`, `M4RulesTests`, `RaceRulesTests`, `LidFlipTests`, `StarFountainTests`, `GooTowerTests`, `RollingBallTests`, `HazardVectorsV3Tests`, …) |
| TS twins | `packages/shared/src/deal.ts`, `targets.ts`, `transcript.ts` + `test-vectors/` |

**Collider kinds in the maps:**
- `BoxCollider` → **OBB**;
- convex `MeshCollider` prism → **convex prism**;
- **sphere** (rolling balls);
- **capsule** (players).

---

## 1. Rules

### 1.1 Parallel-session rules (mandatory)
- **Worktree:** `git worktree add ../FallRush-p1 -b p1 main`. Work only there. Never edit, stage or commit in the main checkout or in another session's worktree.
- **You own (on branch `p1`):**
  - Rust and tooling: `crates/**`, `tools/gen-trig/**`, `tools/wasm-run.mjs`, `maps/**` (exported blobs), `packages/shared/config/movement_v4.json` (new), `docs/proof/P1-rush-core.md`, `docs/proof/p1/**`, `scripts/p1-*.sh`;
  - **Unity, new files only:**
    - `client-unity/Assets/_Game/Editor/MapBlobExport.cs`;
    - `Editor/P1Traces.cs`;
    - `Scripts/FeelLab/**` (its own asmdef, see N);
    - `Plugins/macOS/rush_core.bundle`;
    - `Tests/EditMode/P1*Tests.cs`;
    - a Mac client entry in `Editor/Build.cs` (additive only);
    - a Feel Lab menu button (the only allowed edit to an existing runtime file, guarded so Classic behaviour is unchanged).
- **Never touch:**
  - `programs/**`, `docs/07-*` (PB0); `spikes/**`, `tools/load/**` (P0);
  - `PROJECT_STATUS.md`, `docs/14-*`, `docs/15-*`, `docs/19-*`;
  - existing config JSONs, `packages/shared/src/**`;
  - any existing gameplay / net / rules / map-builder code (read-only).

  Status or decision updates go in "For main (merge later)" at the end of your proof.
- **Heavy jobs** use the shared lock (it also waits while another session's Unity / QA / soak / browser / local-stack job runs):
  `bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p1-<what> -- <command>`
  - Heavy for you: **every Unity batch run** (import, EditMode tests, exporter, traces, Mac build), `cargo build-sbf`, SVM-harness runs, `--release` benchmarks, `rush-sim` runs longer than about 1 minute, Node WASM runs of more than 10k vectors, launching the Mac player.
  - Never bypass or delete the lock, and never kill another session's process.
- **Good neighbour on unlocked work:** `CARGO_BUILD_JOBS=2 nice -n 10` for everyday `cargo test` / `clippy`. If the 1-minute load is above 20, write code and tests rather than compiling, and batch compiles.
- **Never open the Unity Editor UI** while another session's job runs; use batch mode via `m1-unity.sh` under the lock. The user opens the Mac player only for the A/B.
- **No VPS / Hostinger / SSH. Never push, never add a remote.** No new Unity packages. No validator, Docker or devnet.
- Commit on `p1` with your own paths only: `git commit -- <paths>`. End every commit message with the attribution lines from the system reminder.

### 1.2 Engineering rules
- **Integer maths only** in `rush-core`:
  - `#![no_std]`, `#![forbid(unsafe_code)]`, `#![deny(clippy::float_arithmetic)]`;
  - a test that fails if `f32` / `f64` appears in `crates/rush-core/src`;
  - floats are allowed only in `tools/gen-trig`, test oracles, the Unity side and `rush-sim`'s reporting.
- **Zero runtime dependencies** in `rush-core`.
  - sha256 for the deal comes in through a `Hasher` trait (the program uses `solana-sha256-hasher`; tests use a pinned `sha2` dev-dep).
  - Dev-deps are pinned (`=x.y.z`) and justified in the proof.
  - sBPF execution uses **LiteSVM or Mollusk** as a dev-dep of `rush-core-sbf-check` only, compatible with `pb0`'s Solana crates.
- **Determinism:**
  - `i64` intermediates;
  - **one** rounding helper `div_round` (round half away from zero) for every division; shifts documented;
  - fixed iteration order (slot ascending, collider ascending);
  - no `HashMap`, no unbounded recursion;
  - every loop has a compile-time maximum;
  - no allocation in `step()`.
- **Tick rate is a parameter** (`tick_rate_hz` / `dt_ms` from the config, never hard-coded).
- **Cheap by design:** broadphase first, bounded narrowphase. A `counters` feature counts narrowphase tests, sweep sub-steps, slides, TOI bisections and bot raycasts per tick (for compute-unit pricing in P0 / P2).
- **Port faithfully:** doc comments name the C# file and method each function ports, and list any intentional difference. `MotorState` meanings and units stay the same; write down any unit change.
- **Unity side:**
  - one MonoBehaviour per file;
  - **map hashes unchanged**;
  - **EditMode 396 + your new tests, all green**;
  - Classic mode behaviour byte-identical: the Feel Lab is reached only through its own button / arg.
- A number counts as measured only if you ran it. Anything else is **UNVERIFIED**.

---

## 2. Part 1: Rust core (A–J). Each letter ends green and committed before the next.

### A. Workspace + parity harness
1. `crates/Cargo.toml`, its own workspace (members `rush-core`, `rush-core-wasm`, `rush-core-ffi`, `rush-core-sbf-check`, `rush-sim`). It must not join `programs/`.
2. `rush-core` modules: `fx`, `vec`, `config`, `motor`, `collide/{obb,prism,sphere,capsule,sweep,grid}`, `hazards`, `rules`, `deal`, `bots`, `world`, `mapblob`.
3. `rush-core-wasm`: a C-ABI `cdylib` (no wasm-bindgen) exporting `rc_version`, `rc_world_size`, `rc_init(map_ptr, len, cfg_ptr)`, `rc_step(world_ptr, inputs_ptr)`, `rc_predict_player(...)`, `rc_hash(world_ptr)` (FNV-1a 64 over canonical bytes, parity only).
4. `rush-core-ffi`: the **same C ABI** as a native `cdylib` / `staticlib` for macOS (`rush_core.bundle`, universal arm64 + x86_64 if the Mac build target is OSXUniversal; check `Build.cs` and the player settings). Used by the Unity Editor tests and the Feel Lab.
5. `rush-core-sbf-check`: a minimal Solana program (instruction `{kind, seed, n}`) that runs vector cases / n steps and returns the hash via return data. A harness test in LiteSVM / Mollusk compares it with native.
6. `tools/wasm-run.mjs` (a Node loader); `scripts/p1-check.sh`:
   - unlocked: test, clippy `-D warnings`, the float-grep, the wasm build, the Node WASM check;
   - locked: build-sbf + harness.
7. Commit `feat(p1-a): rush-core workspace + parity harness`.

### B. `fx` + `vec`
1. `isqrt_u64`, `div_round`, saturating `mul_div`, Q15 / Q16 helpers, `normalize_q15`.
2. `tools/gen-trig` → a 1,024-entry Q15 sin table in `src/fx/trig_table.rs` (sha256 asserted). `sin_q15` / `cos_q15` (Q16 turns, interpolated). `atan2_yaw256` matching `Yaw256`.
3. `IVec3` (mm, i32), `Vel3` (mm/s, i32), dot / cross in `i64`, a `Yaw256` port.
4. `fx_v1.bin` (≥ 10k cases), native = WASM = sBPF.
5. Commit `feat(p1-b): fixed-point maths`.

### C. Collision kit
1. **Shapes (map-blob form):**
   - OBB: centre, half extents, **three precomputed Q15 axes**;
   - convex prism: n ≤ 16, top/bottom y, precomputed Q15 face normals + offsets;
   - sphere;
   - capsule: segment + radius.

   Each carries `surface_id` and `collider_idx`.
2. **Queries,** with the meanings of the 5 `IMotorWorld` methods:
   - `capsule_sweep`;
   - `raycast`;
   - `penetration`;
   - `begin_tick(tick)`;
   - `surface_delta(surface_id, feet)` (linear carry, point-aware turn-table rotation, conveyor).
3. **Sweep** (recommended; deviation needs a reason):
   - **sub-steps of ≤ 200 mm**;
   - swept-AABB grid broadphase;
   - closest-point overlap tests;
   - **8-iteration bisection of the time of impact**;
   - normal from the closest feature at that point;
   - all loops bounded.
4. **Grid:** uniform cells (parameter, start 2,000 mm), ascending lists, built offline into the blob; moving hazards in a small always-tested dynamic list.
5. **Tests:**
   - analytic per shape pair: face, edge, corner, grazing, start-inside, parallel, zero move;
   - property tests (pinned `proptest` dev-dep): never more than 10 mm overlap after sweep + slide; penetration resolves; no overflow at ±1,000 m.
   - `collide_v1.bin`, with parity on all three targets.
6. Commit `feat(p1-c): collision kit + grid broadphase`.

### D. Motor
1. Port `KinematicMotor.Step` **in the same order**:
   1. depenetrate;
   2. input / timers / stun / dive-landing;
   3. facing;
   4. jump (buffer + coyote);
   5. dive;
   6. accel / decel / air control;
   7. gravity (exact integration);
   8. collide-and-slide + step-up (≤ 3);
   9. ground probe + snap;
   10. landing transitions (belly slide, stumble);
   11. quantise.

   Plus stun / knockback override, 0.5 s immunity, carry, and fall → respawn.
2. **`movement_v4.json`** via `rush-sim movement gen` from v2:
   - the same keys, plus tick counts for 15 / 20 / 30 Hz;
   - apex, run speed, dive distance and the coyote / buffer ms stay identical; rounding errors are written down;
   - default `tickRateHz: 20`.
3. **Analytic table at each rate** (synthetic course):
   - apex 1,200 mm ± 2 %;
   - time to max speed;
   - dive distance;
   - 45° walkable / 46° not;
   - 250 mm step climbs / 260 mm not;
   - ledge tolerance;
   - coyote and buffer windows;
   - belly slide and stumble.
4. `motor_v1.bin`, parity on all three targets.
5. Commit `feat(p1-d): integer motor + movement_v4`.

### E. Hazards
1. All 14:
   - SpinnerBar, MovingPlatform, Conveyor, SpringPad, TurnTable, GateDoor, MalletSwing, Bumper;
   - GooPlane, CrumbleTile, StarPickup, FallingBlock, RollingBall, LidFlip.

   Poses use the integer table; periods in ticks at the configured rate; collision profiles as in C#.
2. Lid Flip and crumble behave exactly as in C#. List any state that isn't pure.
3. `hazard_v2.bin`, plus a v1-vs-v2 pose error table (max mm / deg per type, at matching times).
4. Commit `feat(p1-e): hazards`.

### F. World + separation + `step()`
1. Canonical little-endian `World` with a version byte and a size assert (≤ 2 KB). It holds:
   - tick, phase, round, map key, hazard seed;
   - 12 players (motor state, flags, checkpoint, score, lid shelf / falls / drop tick);
   - goo, stars, lid / crumble state;
   - a drop queue of 8 (empty);
   - event seq.
2. Separation: a port of `SimCore`'s soft push; 66 pairs, fixed order.
3. `step(world, inputs[12], map, hasher) -> StepEvents` (transcript tags 1–7; 8–10 reserved), plus a port of `InputSanitizer`.
4. Commit `feat(p1-f): world + step`.

### G. Rules, director, deal
1. Port:
   - Race: checkpoints, polyline distance, finish order, timer ranking key;
   - Survival;
   - Skill: stars, golden, scores;
   - Final: lid + crumble, **with the G4.13 lid tiebreak**: shelf, then falls, then later drop, then slot;
   - `TimedRoundRules`, `RoundTargets(P)`;
   - minimum-advance M4-8, elimination reasons M4-9, modifiers.
2. Director phases (tick counts from `MatchDirector.cs`, converted).
3. **Deal:** a port of `Deal.cs`. **Byte-equal** with every vector in `packages/shared/test-vectors/`.
4. Re-create the C# EditMode rule scenarios as Rust table tests, citing each C# test name.
5. Commit `feat(p1-g): round rules + director + deal`.

### H. Bots (time-box: 1 day; then write down what's left)
1. Port `BotBrain` + `WaypointBot`: tiers 30/50/20, per mode, player-visible information only; raycasts counted.
2. One function, `bot_input(slot, world, map) -> InputCmd`, serves both on-chain (inside `step()`) and off-chain runners.
3. Commit `feat(p1-h): bots`.

### I. `rush-sim` CLI
1. Commands:
   - `run --map <blob> --seed <hex> --rate <15|20|30> --bots 12 [--rounds all] [--json]`;
   - `mapblob build|inspect|hash`;
   - `movement gen`;
   - `vectors gen|check`;
   - `trace replay <trace.json>` (for L);
   - `qa <map> --runs N --rate R` (for M).
2. Synthetic maps: flat arena, ramp + step course, lid grid 9×9 over 5×5, goo tower, turntable + star field.
3. Synthetic QA (locked): 100 seeded 12-bot rounds per map. No panics, no player stuck > 5 s, **no tunnelling**, correct qualify counts.
4. Commit `feat(p1-i): rush-sim + synthetic QA`.

### J. Interim proof (Part 1)
Write `docs/proof/P1-rush-core.md` §1–7:
- module map C# ↔ Rust;
- rounding rules;
- dependencies;
- vectors + parity;
- the motor table at 3 rates;
- the hazard error table;
- `counters` per tick (p50 / p95 / max), with a compute-unit column if P0's numbers exist;
- intentional differences.

Commit `docs(p1): proof part 1`.

---

## 3. Part 2: real maps, reference, QA, Feel Lab (K–O)

### K. Unity in the worktree + map-blob exporter
1. **Set up Unity in `../FallRush-p1/client-unity`** without a 5 GB re-import:
   - clone the main checkout's Library with APFS copy-on-write: `cp -cR /Users/samya/Downloads/FallRush/client-unity/Library ../FallRush-p1/client-unity/Library` (main is clean at `4ea97b3`, so the Library matches);
   - then, under the lock, run `scripts/m1-unity.sh p1-import --tests -runTests -testPlatform EditMode -testResults "$PWD/.m1-logs/p1-base.xml"` in the worktree. Expect **396/396**; record the import time.
   - If the cloned Library fails, delete it and do a full import under the lock (write down the time).
   - Never touch the main checkout's Library.
2. **`Editor/MapBlobExport.cs`** (menu + batch method `MapBlobExport.ExportAll`). For each map scene (Lobby + 4 + Sandbox) it walks the `Gameplay` layer:
   - `BoxCollider` → OBB (world centre / half extents / axes, mm and Q15, using the same rounding as `rush-core`);
   - convex `MeshCollider` → prism (vertices → n-gon + y range; **assert** it's a vertical prism, otherwise fail loudly);
   - spheres;
   - the hazard authoring components → hazard defs with the same params as `HazardSet`;
   - surface ids exactly as `PhysxMotorWorld` assigns them;
   - checkpoints + course polyline, spawns, kill-Y, goo, star field, lid grid / crumble grid.

   Output: `maps/<key>.mapjson` (human-readable). Then `rush-sim mapblob build` → `maps/<key>.mapblob` + sha256 in `maps/map_blob_hashes_v1.txt`.
3. **Tests:**
   - `P1MapBlobTests`: the exporter is deterministic (run twice, same bytes); collider counts per map match the scene; **existing map content hashes are unchanged** (`MapSceneTests` still green).
   - In Rust: `rush-sim mapblob inspect` round-trips every blob.
   - Record the collider count per map (P0 / P2 compute-unit input; P0 guessed Capsule Run ≈ 300, so report the real number).
4. Commit `feat(p1-k): map-blob exporter + 6 map blobs`.

### L. C# reference traces vs `rush-core`
1. **`Editor/P1Traces.cs`** (batch method). On `Map_Sandbox` and each round map, it runs **scripted input sequences** through today's `KinematicMotor` + `PhysxMotorWorld` (+ hazards, `SimCore` separation with 2–3 players where relevant) at 30 Hz, and dumps per-tick `MotorState` + events to `docs/proof/p1/traces/<map>-<script>.json`. Scripts:
   - run straight;
   - run + jump;
   - jump-buffer edge;
   - coyote off a ledge;
   - dive + belly slide + jump-cancel;
   - slopes 15/30/44/50°;
   - steps 0.2/0.3;
   - a gap jump;
   - a wall slide;
   - a hard landing → stumble;
   - platform ride;
   - turn table;
   - conveyor;
   - spring pad;
   - sweeper hit → knockback + stun + immunity;
   - mallet;
   - bumper;
   - rolling ball;
   - lid fall;
   - goo death;
   - respawn;
   - a 3-player push.
2. **`rush-sim trace replay`** feeds the same inputs (re-timed to the target rate) into `rush-core` on the exported blob.
3. **Comparison** (they can't be bit-identical: floats vs integers, 30 Hz vs 20 Hz). Compare at matching *times*. Report per script:
   - max and final position drift (mm);
   - apex height;
   - landing time;
   - **outcome agreement**: reached / missed the platform, fell / didn't, stunned / not, step climbed / not.

   **Pass bar:**
   - outcomes identical on every script;
   - final drift ≤ 150 mm on 2 s scripts and ≤ 300 mm on 5 s scripts;
   - apex within ± 2 %.

   Failures are tuned (motor constants in `movement_v4`, never the maps) or written up as an intentional difference **with the user's OK** (§4).
4. Write the table to `docs/proof/p1/trace-compare.md`. Commit `test(p1-l): C# reference traces + comparison`.

### M. Real-map bot QA (`rush-sim`, all three tick rates)
1. `rush-sim qa` on each real blob, **100 seeded runs per map per rate** (locked; run one map × rate at a time). The **G4.7 targets** (`docs/proof/G4.7-qa-freeze.md`; per-map details in the G4.2–G4.5 proofs):

   | Map | Targets |
   |---|---|
   | Capsule Run | normal median 85–110 s, 8 qualify, **0 free routes, route ratio ≤ 1.25** |
   | Goo Tower | goo margin 8–10 s on every route index, median 50–65 s, ≈ 5 of 8 escape (band ≥ 4.80) |
   | Star Fountain | the G4.4 §3 Skill targets incl. the camper probe, 60 s timer |
   | Lid Flip | the G4.5 §2 Final targets (median in 60–120 s, every lid fall after ≥ 1 s of yellow, timer endings 0–3 %) |
   | Every map | valid 100 %, 0 soft-locks, deterministic (same seed twice → same transcript), no tunnelling |

   Route cost: port the needed part of `RouteQa` into `rush-sim` (per-section route cost ratios), or write down why not.
2. **A 20-match soak** with `rush-sim run --rounds all` at the default rate: 20/20 complete, median match length vs Classic (C6 ≈ 400 s).
3. Table each target: Classic (from G4.7) vs `rush-core` at 15 / 20 / 30 Hz. A miss is tuned (motor or bot constants only) or reported as open; **never change map geometry or hazard timing in ms**.
4. Commit `test(p1-m): real-map QA + soak`.

### N. Feel Lab (Mac player build for the user's A/B)
1. **`Scripts/FeelLab/`**, its own asmdef (references Shared + Client as needed, never referenced by them). A dev-only offline mode, reached by a "Feel Lab" button on the menu (shown only in development builds or with `-feellab`), that:
   - loads any round map (Sandbox, Capsule Run, Goo Tower, Star Fountain, Lid Flip) with **local hazards running**;
   - spawns the player with the **same visuals, camera and audio** (Bolt, `RushlingVisuals`, Cinemachine rig), plus optional `rush-core` bots (0 / 5 / 11);
   - **F5** toggles the engine live:
     - **Classic**: today's `KinematicMotor` + `PhysxMotorWorld` at 30 Hz;
     - **rush-core**: `rush_core.bundle` via P/Invoke on the exported blob, at the chosen rate, with interpolation for rendering;
   - **F8** cycles the `rush-core` tick rate 15 / 20 / 30 Hz;
   - **F10** toggles a small overlay: engine, rate, speed, grounded, apex of the last jump, frame time;
   - **F4** respawns at the map start.

   No networking. Classic mode elsewhere is untouched.
2. **`P1FeelLabTests`** (EditMode):
   - the bundle loads in the Editor;
   - `rc_version` matches;
   - stepping a blob in C# via P/Invoke gives the **same World hash** as `rush-sim` for 3 seeds (native parity through the FFI).
3. **Mac client build:** add `Build.MacClient` (additive in `Editor/Build.cs`; development build; includes the Feel Lab) and `pnpm`-free script `scripts/p1-build-mac.sh` (locked). Output `client-unity/Builds/Mac-p1-feellab/FallRush.app`. Record build time and size.
4. Smoke test (locked): launch the app with `-feellab -map Sandbox -autotest` (an auto-input script that exercises jump / dive / slope under both engines, logs the overlay values and quits). Capture **1 screenshot per engine per map** into `docs/proof/p1/feellab/`.
5. Commit `feat(p1-n): Feel Lab + Mac client build`.

### O. Final proof + hand-off to the user
1. Finish `docs/proof/P1-rush-core.md`:
   - §8 map blobs (hashes, collider counts);
   - §9 trace comparison;
   - §10 real-map QA (Classic vs `rush-core` at 3 rates) and the soak;
   - §11 Feel Lab (how to run it, controls, screenshots);
   - §12 compute-unit outlook: `counters` per tick on the **real** maps, p50 / p95 / max per rate, converted with P0's per-primitive numbers if available;
   - §13 UNVERIFIED;
   - "For main (merge later)": the `PROJECT_STATUS.md` lines and a doc 15 note — OC-3 waits for the user's feel sign-off, P0-1 is still open unless the user decided it.
2. Commit `docs(p1): proof`. **Never push.**
3. **Hand the A/B to the user:** tell them exactly how to open the app (`open client-unity/Builds/Mac-p1-feellab/FallRush.app --args -feellab` in the `../FallRush-p1` worktree) and the controls (F5 engine, F8 rate, F4 respawn, F10 overlay). Ask them to try each map with both engines and the three rates, and to answer:
   1. Is `rush-core` at 20 Hz as good as Classic? If not, what feels off (jump, run, dive, slopes, hazards)?
   2. Which rates feel acceptable?

   Record their answer in the proof (OC-3 sign-off). If they ask for tuning, change only `movement_v4` / the motor constants, re-run D.3 + L + M for that change, rebuild the Mac app, and ask again.

---

## 4. When to stop and ask the user
- A C# behaviour that can't be copied in integer maths **and changes how the game feels** (not just rounding). Describe it, propose an option, and wait.
- A trace or QA miss that can't be fixed by motor / bot tuning without touching geometry or hazard ms timing.
- A dependency outside §1.2, a new Unity package, or anything needing a validator, devnet SOL, the VPS or another session's files.
- If the APFS Library clone fails **and** a full import would take more than 60 minutes, ask first.

## 5. Report to the user (short, plain words)
- **Core:** what's ported, vector counts, parity (native / WASM / sBPF / the Unity FFI).
- **Real maps:** collider counts, the trace comparison (outcomes agree? drift), the QA table (Classic vs `rush-core` at 15 / 20 / 30 Hz), the soak.
- **Compute-unit outlook** from counters on real maps (with P0's numbers if they exist).
- **Feel Lab:** how to open it and the controls, and the two questions above.
- UNVERIFIED items.
- Ask before merging branch `p1` into `main` and before removing the worktree.
