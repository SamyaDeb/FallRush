# P1a: `rush-core`, the shared integer game core (new session, alongside G4.13, PB0 and P0)

You are the gameplay-simulation engineer on **Fall Rush**, a Unity 6.3 WebGL 12-player knockout party game for the MagicBlock Solana Blitz V10 hackathon. The game is moving fully on-chain onto a MagicBlock Ephemeral Rollup.

You build **`rush-core`**: one `no_std` Rust crate with **integer maths only**, holding the whole game simulation (motor, collisions, hazards, round rules, deal, bots). It will be compiled to:
- **sBPF**, inside the `fall_rush` program, where it is the authority;
- **WASM**, in the browser, for client prediction.

Same source plus integer maths means client and chain agree **bit for bit, by construction**.

**Read first, in full:** `/Users/samya/Downloads/FallRush/docs/19-ONCHAIN-PLAN.md`, especially §2 repo layout, §3 conventions, §5 P1, §6 tests and §10 "what stays". It is uncommitted on `main`, so read it from that path.

**This session is P1a only:** pure Rust. **No Unity, no validator, no Docker, no devnet, no browser.** P1b (map exporter, C# oracle traces, real-map QA, feel A/B) comes later in another session. §5 previews it so you design for it; don't start it.

---

## 0. State at hand-off (2026-11-21, checked)

**Sessions running right now** (don't disturb them):

| Session | Where | Doing | Owns |
|---|---|---|---|
| G4.13 | main checkout `/Users/samya/Downloads/FallRush` (`main` @ `2c89cc0`) | Unity fixes, WebGL build #5, browser measurements, soak | everything in the main checkout, `client-unity/**` |
| PB0 | `../FallRush-pb0`, branch `pb0` (@ `8a87005` "ER SDK 0.18 API notes") | ER SDK 0.16.2 → 0.18.0 upgrade of `programs/fall_rush` | `programs/**`, `docs/07-*`, `docs/proof/PB0.md` |
| P0 | `../FallRush-p0`, branch `p0` | feasibility spikes: its own throw-away sim at `spikes/p0/sim` + spike program + load tools | `spikes/**`, `tools/load/**`, `docs/proof/P0*` |

**Facts you need:**
- **`crates/` doesn't exist anywhere yet. You create it.** PB0's task E (the skeleton) was moved to you; PB0's proof confirms it skipped it.
- P0's `spikes/p0/sim` is a **throw-away** cost-measurement sim. You may *read* it (and later P0's results in `../FallRush-p0/docs/proof/P0-feasibility.md`), but **never copy-depend on it or edit it**. `rush-core` is the real implementation.
- PB0 findings that matter to you (`../FallRush-pb0/docs/proof/PB0.md` §1):
  - SDK **0.18.0**, `anchor-lang =1.0.2`, which pulls `solana-program` 3.0.0 (+ 2.3.0 via the SDK);
  - task scheduling ("crank") is behind a `crank` feature.

  Your sBPF check crate must use the **same Solana crate versions as `programs/Cargo.lock` on branch `pb0`**. Read it with `git -C ../FallRush-pb0 show pb0:programs/Cargo.lock`, or the worktree file.
- **Machine:** 8 GB M2 Air; the load is very high while the other sessions build (load average 40–55 seen at hand-off).
- **Toolchain:** Rust stable (aarch64-apple-darwin), targets `wasm32-unknown-unknown` + `wasm32v1-none` installed, `cargo build-sbf` from the Solana CLI, Node 24.
- **No dotnet/mono** on this Mac, so C# reference traces need Unity (that's P1b).
- **Units in the live game** (`packages/shared/config/movement_v2.json`, 30 Hz): mm, mm/s, mm/s², ms, deg, permille.
  - capsule r 310, h 1,000, skin 10;
  - run 5,000 mm/s, accel 28,000, decel 30,000, air control 300 ‰;
  - gravity 20,000, fall multiplier 1,350 ‰, max fall 30,000;
  - jump apex 1,200 mm, coyote 100 ms, buffer 120 ms;
  - dive 7,000 / 2,500 mm/s, cooldown 1,000, land stun 450, cancel window 150, slide decel 12,000;
  - stumble from 3,000 mm for 300 ms at 550 ‰;
  - max slope 45°, step 250, snap 250, 3 slide iterations, ledge tolerance 50;
  - push max 2,500;
  - respawn after 1,000 ms below kill-Y −20,000.

**The C# sources to port** (read-only, `client-unity/Assets/_Game/Scripts/`, readable from the main checkout or your worktree):

| Area | Files (lines) |
|---|---|
| Motor | `Shared/KinematicMotor.cs` (468), `MotorState.cs`, `MovementConfig.cs`, `MovementConfigData.g.cs`, `MovementTuning.cs`, `InputCmd.cs`, `InputSanitizer.cs`, `Vec3.cs`, `Yaw256.cs`, `IMotorWorld.cs` (5 queries) |
| Per-tick loop | `Shared/SimCore.cs` (sanitise → motor → fall/respawn → soft separation) |
| Hazards | `Shared/Hazards/HazardMath.cs` (340): 14 types, `pose = f(def, variant, hazardSeed, t)`, integer phase + float polynomials, collision profiles (knockback override, stun, 0.5 s immunity) |
| World queries | `Net/PhysxMotorWorld.cs` (235): platform carry, doors, crumble tiles, falling blocks, lids, surface ids |
| Rules | `Shared/RaceRules.cs` (491), `SurvivalRules.cs`, `SkillRules.cs`, `FinalRules.cs` (**includes the G4.13 lid tiebreak**, commit `8fcd5ca`), `TimedRoundRules.cs`, `RoundTargets.cs`, `RoundParams.cs`, `ModifierEffects.cs` + `ModifiersData.g.cs`, `MatchDirector.cs` (856), `MatchTypes.cs` (transcript tags 1–7), `Deal.cs` |
| Bots | `Shared/BotBrain.cs` (1,114), `WaypointBot.cs` |
| Tests to mirror | `client-unity/Assets/_Game/Tests/EditMode/*` (e.g. `M4RulesTests.cs`) |
| TS twins | `packages/shared/src/deal.ts`, `targets.ts`, `transcript.ts` (+ `test-vectors/`) |

**Collider kinds the maps actually use** (from `Editor/KitBuilder.cs` and the map builders):
- `BoxCollider` → **OBB**;
- convex `MeshCollider` prisms (turn tables, bumpers) → **convex prism**;
- **sphere** (rolling balls);
- **capsule** (players).

Nothing else.

---

## 1. Rules

### 1.1 Parallel-session rules (mandatory)
- **Worktree:** `git worktree add ../FallRush-p1 -b p1 main`. Work only there. Never edit, stage or commit in the main checkout or in another session's worktree.
- **You own:** `crates/**` (`rush-core`, `rush-core-wasm`, `rush-core-sbf-check`, `rush-sim`), `tools/gen-trig/**`, `tools/wasm-run.mjs`, `packages/shared/config/movement_v4.json` (a new file), `docs/proof/P1-rush-core.md`, `scripts/p1-*.sh`.
- **Never touch:** `programs/**`, `docs/07-*`, `spikes/**`, `tools/load/**`, `client-unity/**`, `PROJECT_STATUS.md`, `docs/14-*`, `docs/15-*`, `docs/19-*`, existing config JSONs, or `packages/shared/src/**`. Status or decision updates go in "For main (merge later)" at the end of your proof.
- **Heavy jobs** go through the shared lock (it also waits for Unity / QA / soak / browser jobs):
  `bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p1-<what> -- <command>`
  - Heavy for you: `cargo build-sbf`, SVM-harness runs, `--release` benchmarks, `rush-sim` runs longer than about 1 minute, Node WASM runs of more than 10k vectors.
  - Never bypass or delete the lock, and never kill another session's process.
- **Be a good neighbour on unlocked work:** run everyday `cargo test` / `clippy` with `CARGO_BUILD_JOBS=2 nice -n 10`. If the 1-minute load (`sysctl -n vm.loadavg`) is above 20, prefer writing code and tests over compiling; batch your compiles.
- **No VPS / Hostinger / SSH. Never push, never add a remote.**
- Commit on `p1` with your own paths only: `git commit -- <paths>`. End every commit message with the attribution lines from the system reminder.

### 1.2 Engineering rules
- **Integer maths only** in `rush-core`:
  - `#![no_std]`, `#![forbid(unsafe_code)]`, `#![deny(clippy::float_arithmetic)]`;
  - a test that fails if `f32` or `f64` appears anywhere in `crates/rush-core/src`;
  - floats are allowed only in `tools/gen-trig` (offline table generation) and in test oracles.
- **Zero runtime dependencies** in `rush-core`.
  - Hashing (sha256 for the deal) comes in through a `Hasher` trait that the caller implements (the program with `solana-sha256-hasher`; tests with a pinned `sha2` dev-dependency).
  - Dev-deps must be pinned (`=x.y.z`) and justified in the proof.
  - For the sBPF harness, use **LiteSVM or Mollusk** as a dev-dependency of `rush-core-sbf-check` only, at a version that builds against the Solana crates in `pb0`'s `programs/Cargo.lock`. Write down the versions.
- **Determinism:**
  - `i64` intermediates;
  - **one** rounding helper, `div_round(a, b)` = round half away from zero, used for every division; shifts documented;
  - fixed iteration order (slot ascending, collider index ascending);
  - no `HashMap`, no unbounded recursion;
  - every loop has a compile-time maximum;
  - no allocation in `step()`.
- **Tick rate is a parameter.** P0 + the user decide 20 / 15 / 30 Hz. Everything takes `tick_rate_hz` / `dt_ms` from the config; no hard-coded 50 ms.
- **Cheap by design.** Broadphase first, bounded narrowphase. A `counters` cargo feature counts narrowphase tests, sweep sub-steps, slide iterations, TOI bisections and bot raycasts per tick, so P0 / P2 can turn counts into compute units.
- **Port faithfully.** Every function's doc comment names the C# file and method it ports, and **lists any intentional behaviour difference**. Keep `MotorState` field meanings and units the same; write down any unit change.
- One concept per module. Small files. Tests next to the code, plus golden vector files under `tests/vectors/`.
- A number counts as measured only if you ran it. Anything else is **UNVERIFIED**.

---

## 2. Tasks, in order (each letter ends green and committed before the next)

### A. Skeleton + checks
1. `crates/Cargo.toml` as its own workspace (members: `rush-core`, `rush-core-wasm`, `rush-core-sbf-check`, `rush-sim`). It must **not** join `programs/`; P2 will add `rush-core` as a path dependency.
2. `rush-core` modules (stubs with doc comments): `fx`, `vec`, `motor`, `collide/{obb,prism,sphere,capsule,sweep,grid}`, `hazards`, `rules`, `deal`, `bots`, `world`, `mapblob`, `config`.
3. `rush-core-wasm`: a plain C-ABI `cdylib` (no wasm-bindgen) exporting `rc_version`, `rc_world_size`, `rc_init(map_ptr, len)`, `rc_step(world_ptr, inputs_ptr)`, `rc_predict_player(...)`, `rc_hash(world_ptr)`. The hash is FNV-1a 64 over the canonical World bytes, for parity only; the chain uses sha256.
4. `rush-core-sbf-check`: a minimal Solana program (Solana crate versions as in `pb0`). Its instruction data is `{kind, seed, n}`; it runs n steps (or n vector cases) and returns the hash via return data. A harness test runs it in LiteSVM/Mollusk and compares with native.
5. `tools/wasm-run.mjs`: a ~20-line Node loader that runs a vector file through the WASM exports and prints hashes.
6. `scripts/p1-check.sh`:
   - unlocked: `cargo test`, `cargo clippy --all-targets -- -D warnings`, the float-grep test, `cargo build --release --target wasm32-unknown-unknown -p rush-core-wasm`, the Node WASM vector check;
   - under the lock: `cargo build-sbf` of `rush-core-sbf-check` + the harness check.
7. Commit `feat(p1-a): rush-core workspace + parity harness`.

### B. `fx` + `vec`
1. `isqrt_u64` (exact floor), `div_round`, saturating `mul_div`, Q15 / Q16 helpers, `normalize_q15(v) -> (unit_q15, len_mm)`.
2. Trig:
   - `tools/gen-trig` writes a 1,024-entry Q15 sin table to `src/fx/trig_table.rs`, with its sha256 asserted in a test;
   - `sin_q15` / `cos_q15` take a Q16 turn angle, with linear interpolation;
   - `atan2_yaw256(dx, dz)` matches `Yaw256` semantics.
3. `IVec3` (mm, i32), `Vel3` (mm/s, i32), dot / cross in `i64`, and a `Yaw256` port.
4. `tests/vectors/fx_v1.bin` (≥ 10k cases): native = WASM = sBPF.
5. Commit `feat(p1-b): fixed-point maths`.

### C. Collision kit
1. **Shapes (map blob form):**
   - OBB: centre mm, half extents mm, **three Q15 unit axes precomputed** (no runtime trig);
   - convex prism: n-gon (n ≤ 16), top/bottom y, precomputed Q15 face normals + plane offsets;
   - sphere;
   - capsule: feet point + r + h, as a segment + radius.

   Each has a `surface_id` and a `collider_idx`.
2. **Queries,** with the same meanings as the 5 `IMotorWorld` methods:
   - `capsule_sweep(feet, r, h, dir_q15, max_dist) -> Option<Hit { dist_mm, normal_q15, surface_id, collider_idx }>`;
   - `raycast(origin, dir_q15, max) -> Option<Hit>`;
   - `penetration(feet, r, h) -> Option<IVec3>`;
   - `begin_tick(tick)` (poses moving geometry via `hazards`);
   - `surface_delta(surface_id, feet) -> Option<IVec3>` (linear platform carry, point-aware turn-table rotation, conveyor push).
3. **Sweep algorithm** (recommended; any deviation needs a written reason):
   - split motion into **sub-steps of ≤ 200 mm** (no tunnelling through thin lids at 30 m/s fall);
   - per sub-step, run the swept-AABB broadphase on the grid, then **closest-point overlap tests** (capsule segment vs each shape);
   - on overlap, **bisect the time of impact for 8 iterations**;
   - the normal comes from the closest feature at the time of impact;
   - every loop is bounded.
4. **Broadphase:**
   - a uniform grid, cell size a parameter (start 2,000 mm), each cell's collider list ascending, built offline into the map blob;
   - moving hazards live in a small dynamic list that's always tested.
5. **Tests:**
   - analytic cases per shape pair: face, edge, corner, grazing, start-inside, parallel, zero-length move;
   - property tests (a pinned `proptest` dev-dep is fine): after sweep + slide the capsule never overlaps by more than the skin (10 mm); penetration always resolves; no overflow at ±1,000 m;
   - `collide_v1.bin`, with native = WASM = sBPF parity.
6. Commit `feat(p1-c): collision kit + grid broadphase`.

### D. Motor (port of `KinematicMotor.Step` + the motor part of `SimCore`)
1. **The same order of operations** as the C# header:
   1. depenetrate;
   2. input / timers / stun / dive-landing;
   3. facing;
   4. jump (buffer + coyote);
   5. dive;
   6. horizontal accel / decel / air control;
   7. gravity (exact per-step integration);
   8. collide-and-slide with step-up (≤ 3 iterations);
   9. ground probe + snap;
   10. landing transitions (belly slide, stumble);
   11. quantise.

   Also: the stun / knockback override, 0.5 s immunity, platform carry and fall → respawn.
2. **`movement_v4.json`** (generated by `rush-sim movement gen` from v2):
   - the same keys, plus tick counts for 15 / 20 / 30 Hz;
   - jump apex, run speed, dive distance and the coyote/buffer **ms** stay identical; tick counts are rounded with the error stated;
   - commit it with `tickRateHz: 20`, and put the 15/30 tables in the proof.
3. **Analytic checks, at each tick rate,** on synthetic flat ground / ramps / steps / ledges:
   - jump apex 1,200 mm ± 2 %;
   - time to max run speed;
   - dive distance;
   - 45° walkable but 46° not;
   - a 250 mm step climbs but 260 mm doesn't;
   - ledge tolerance;
   - coyote and buffer windows;
   - belly slide and stumble timings.

   Table each against the value implied by `movement_v2.json`.
4. `motor_v1.bin`: native = WASM = sBPF.
5. Commit `feat(p1-d): integer motor + movement_v4`.

### E. Hazards (port of `HazardMath`)
1. All 14 types:
   - SpinnerBar, MovingPlatform, Conveyor, SpringPad, TurnTable, GateDoor, MalletSwing, Bumper;
   - GooPlane, CrumbleTile, StarPickup, FallingBlock, RollingBall, LidFlip.

   Poses use the integer table; periods are in ticks at the configured rate (ms converted like the motor). Collision profiles are as in C#.
2. Lid Flip and crumble behave exactly like the C#. Write down any state that isn't a pure function of seed + tick (+ contact history).
3. The old `hazard_vectors_v1` (float, 30 Hz) **can't be bit-identical**. Generate `hazard_v2.bin` and a table of the pose error vs v1 at matching times (max mm, max deg) per type.
4. Commit `feat(p1-e): hazards`.

### F. World + separation + `step()`
1. `World` gets a fixed canonical little-endian layout with a version byte, `to_bytes` / `from_bytes`, and a size assert (target ≤ 2 KB; doc 19 §5 P2 table). It holds:
   - tick, phase, round, map key, hazard seed;
   - 12 players (motor state, flags, round fields: checkpoint, score, lid shelf/falls/drop tick);
   - goo, star bits, lid/crumble state;
   - a drop queue of 8 (P4, empty for now);
   - event seq.
2. Separation is a port of the soft push from `SimCore`: 66 pairs, fixed order. It's now predictable (fixes R14).
3. `step(world, inputs[12], map, hasher) -> StepEvents` (transcript tags 1–7 from `MatchTypes.cs`; tags 8–10 reserved). Include a port of `InputSanitizer`.
4. Commit `feat(p1-f): world + step`.

### G. Rules, director, deal
1. Port:
   - Race: checkpoints, distance **along the course polyline**, finish order, the timer ranking key;
   - Survival (goo);
   - Skill (stars, golden, scores);
   - Final: lid and crumble maps, **with the G4.13 lid tiebreak**: shelf last stood on, then fewer falls, then later drop, then slot;
   - `TimedRoundRules`, `RoundTargets(P)`;
   - minimum-advance (M4-8), elimination reasons (M4-9), modifiers.
2. Director phases with tick counts from `MatchDirector.cs`, converted to the configured rate.
3. **Deal:** a port of `Deal.cs` (map plan, variants, modifiers, hazard seed `u64 LE of sha256("fallrush-hazard-v1" ‖ seed ‖ round)[0..8]`, bot tiers 30/50/20). It must match `packages/shared/test-vectors/` **exactly** (byte-equal deals for every vector).
4. Re-create the C# EditMode rule scenarios as Rust table tests, citing the C# test name on each case.
5. Commit `feat(p1-g): round rules + director + deal`.

### H. Bots (time-box: 1 day; then write down what's left)
1. Port `BotBrain` + `WaypointBot`: tiers, per-mode behaviour, only player-visible information. Raycasts go through `collide` and are counted.
2. One function serves both P2 options: `bot_input(slot, world, map) -> InputCmd`, callable inside `step()` (option A) or from an external runner (option B).
3. Commit `feat(p1-h): bots`.

### I. `rush-sim` (native CLI)
- `run --map <blob> --seed <hex> --bots 12 [--rounds all] [--json]`: a headless match; prints the transcript, per-round duration / outs / falls / stuck-time.
- `mapblob build|inspect|hash`, `movement gen`, `vectors gen|check`.
- **Synthetic maps** (real blobs come in P1b): flat arena, ramp + step course, lid grid (9×9 over 5×5), goo tower, turntable + star field.
- **QA:** 100 seeded 12-bot rounds per synthetic map, under the lock. No panics, no player stuck > 5 s, **no tunnelling** (assert no capsule centre inside a solid after any tick), rounds end with the right number qualified.
- Commit `feat(p1-i): rush-sim + synthetic QA`.

### J. Proof
`docs/proof/P1-rush-core.md`:
1. Module map (C# file/method ↔ Rust fn), rounding rules, dependency list with versions.
2. Vector files and counts; native / WASM / sBPF parity results.
3. The motor analytic table at 15 / 20 / 30 Hz.
4. Hazard v1-vs-v2 error table.
5. `counters` per tick on each synthetic map (p50 / p95 / max), as input for P0 / P2 compute-unit pricing. If P0's proof exists by then, add a column converting counts to compute units with P0's numbers.
6. Intentional behaviour differences vs C#.
7. The P1b checklist (§5). UNVERIFIED items.
8. "For main (merge later)": the `PROJECT_STATUS.md` lines, and anything P0 / P2 need.

Commit `docs(p1): proof`. **Never push.**

---

## 3. When to stop and ask the user
- A C# behaviour that can't be reproduced exactly in integer maths and changes how the game feels (not just rounding). Describe it, propose an option, and wait.
- A dependency outside the rules in §1.2.
- Anything that would need Unity, a validator, devnet SOL or another session's files.

## 4. Report to the user (short, plain words)
- What's ported and tested; vector counts; parity status (native / WASM / sBPF).
- The motor table: does it still jump, run and dive the same at 20 Hz?
- Counters per tick (narrowphase tests, sub-steps, slides, bot rays) for compute-unit pricing.
- What's left for P1b, and anything UNVERIFIED.
- Ask before merging branch `p1` into `main`, which must wait until G4.13 has closed out, and before removing the worktree.

## 5. P1b preview (a later session; don't start it)
Starts after **G4.13 closes out** and the **user approves P0-1** (tick rate, bots on- or off-chain):
1. **Unity map-blob exporter** (Editor menu): `Gameplay` layer → OBB / prism / sphere + surface ids, hazard defs, checkpoints + polyline, spawns, kill-Y, goo, stars, lid grid → `maps/<key>.mapblob` + hash for Lobby + 4 maps. Geometry and the existing map hashes are unchanged.
2. **C# oracle traces:** an EditMode test runs scripted inputs through the current `KinematicMotor` + `PhysxMotorWorld` and dumps trajectories. `rush-sim` replays them on the blobs. Report drift; tune `movement_v4`.
3. **Real-map bot QA** with `rush-sim` against the G4 targets (route ratios 1.0–1.25, round medians, survival margins, Final 60–120 s); soak 20 matches.
4. **Feel A/B:** a Mac player build with a Classic ↔ `rush-core` toggle (native `.bundle`). The user plays both and signs off (OC-3).
