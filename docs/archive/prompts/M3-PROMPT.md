You are the senior gameplay / multiplayer engineer on **Fall Rush**, and you also know Solana and MagicBlock well. Fall Rush is an original 12-player knockout party game for the MagicBlock Solana Blitz V10 hackathon, and the deadline is **2026-11-27 14:00 UTC**.

Your task is **Milestone M3: obstacle kit + race round**. It has two halves and a **checkpoint** between them:
- **M3a:** the deterministic obstacle kit (8 hazard types), round-relative hazard time, map data with a content hash, the cross-platform determinism check, and VRF-ready round parameters (variant, hazard seed, modifier). Then **stop at the checkpoint** and report.
- **M3b:** `RoundRules.Race` (countdown, ordered checkpoints, respawn, finish, qualification, timer ranking), the **Block Party Dash** greybox, a waypoint bot and the automated map QA, a race HUD, and the netcode re-check on a crowded 12-player race.

M2 proved movement and prediction against two hazards. M3 must keep that quality while adding **eight hazard types, a crowded start and a real course**. Out of scope:
- `programs/`, `server/` (Relay), `web/`, and the TS deal logic in `packages/shared/src`;
- the match director, lobby flow, Survival / Skill / Final rules, Goo Tower, Star Scramble and Crumble Shelf (M4);
- `GOO_PLANE`, `CRUMBLE_TILE`, `STAR_PICKUP` (M4), `PISTON_PUSHER` and Bonk Factory (stretch, D-14);
- `BotBrain` noise and mistakes (M4; you build its waypoint-following base);
- final art, audio and VFX (M7). Greybox plus readable telegraphs only.

## 0. Context: read these first, fully

The repo is `/Users/samya/Downloads/FallRush` (git, `main`, M0 + M1 + M2 committed).

| Doc | What it gives you |
|---|---|
| `docs/00-README.md` | Index and the rules you cannot break |
| `docs/08-MAP-DESIGN.md` | **Primary spec.** §1 principles (pure pose functions, collision = visuals, telegraphs, two routes), §2 obstacle kit + data model + knockback rules, §3 **Block Party Dash**, §8 authoring checklist, §9 automated map QA |
| `docs/01-GDD.md` §4.1, §5.1, §7, §8 | Match format and race qualify counts, **race rules + ranking key**, movement constants, Chaos Capsule modifiers |
| `docs/04-UNITY-CLIENT.md` | §4 netcode as built in M2 (messages, `SimCore`, `PlayerStep`, hazards, prediction, interpolation), §5 motor, §9 bots, §11 tests |
| `docs/06-ANCHOR-PROGRAM.md` §4 + the notes near line 127/193 | The deal: `deal_variants`, `deal_modifiers`, `hazard_seed` (clients use `H(vrf_seed ‖ round ‖ obstacleId)`) |
| `packages/shared/config/map_pool_v1.json` | Map ids, 2 variants per map, modifier ids and weights |
| `docs/11-SECURITY-ANTI-CHEAT.md` | Checkpoint skipping (ordered checkpoints, server trigger, reachability check), input threats |
| `docs/17-ART-DIRECTION.md` §1, §3.1, §3.2, §4 | Originality guardrails, palette, the MacBook Air M2 performance budget, camera/feel |
| `docs/12-TESTING-PLAN.md` | PlayMode and map-QA expectations |
| `docs/15-DECISIONS-AND-RISKS.md` | D-1b, D-13, D-14, D-16; R2, R12, **R14 (push corrections)** |
| `docs/proof/M2-netcode.md` | Measured M2 baseline. Read **B5** (pushes, why the gate course used lanes), **B8/B9** (VPS steps, cross-platform determinism: **WebGL ↔ Linux bit-identical, the arm64 Mac build is not**), **B10** (TCP-stall fixes) |
| `PROJECT_STATUS.md` | Current status and the **"Notes for M3"** sections from M2a and M2b. You will update it |

M2 code you build on, in `client-unity/Assets/_Game/Scripts/`:
- `Shared/HazardMath.cs` (`SweeperDef`, `PlatformDef`, `HazardSet`, `PlayerStep.Run`, `Sin16`, `PhaseFromSeed`), `Shared/IMotorWorld.cs` (`SurfaceDelta`), `Shared/KinematicMotor.cs`, `Shared/SimCore.cs`, `Shared/NetMessages.cs`, `Shared/MovementConfig*.cs`, `Shared/InputSanitizer.cs`, `Shared/TickSync.cs`
- `Net/ServerSim.cs`, `Net/ClientPrediction.cs`, `Net/Interpolation.cs`, `Net/PhysxMotorWorld.cs`, `Net/MapInfo.cs`, `Net/HazardSweeper.cs`, `Net/HazardPlatform.cs`, `Net/CourseBot.cs`, `Net/SimProbe.cs`, `Net/GateLogger.cs`
- `Client/SandboxView.cs`, `Client/RushlingVisuals.cs`, `Client/FollowCamera.cs`
- `Editor/SandboxBuilder.cs` (the code-built greybox pattern), `Editor/Build.cs`
- `scripts/m2-*.sh`, `scripts/m2-summarize.py`

Read them before changing anything. Keep every M2 behaviour working: prediction, interpolation, R12, the TCP-stall fixes, `Map_Sandbox`, and the **153 EditMode tests**.

## 1. Non-negotiable rules

1. **Never invent** Unity, NGO 2.14.0, UTP 2.7.5, Input System 1.20.1, Cinemachine 3.1.7, URP 17.3 or Unity Test Framework APIs, package versions, CLI flags or env vars.
   - Sources of truth: `client-unity/Library/PackageCache/` and the Unity 6000.3 docs.
   - If reality differs from a doc, follow reality and **fix the doc in the same commit**.
2. **No new packages** without asking me. ProBuilder is not needed: greybox from code (the `SandboxBuilder` pattern).
3. **Authority and determinism (ADR-002, D-1, doc 08 §1):**
   - The server is authoritative for positions, stun, knockback, checkpoints, finish and qualification. Clients send inputs only.
   - **Every hazard pose is a pure function** `f(def, variant, hazardSeed, roundTick)`: integer parameters (mm, ticks, phase16), integer phase, float polynomials only. No `Math.Sin/Cos`, no `Time.*`, no `Random`, no Unity transforms as inputs, no state carried between ticks. **No hazard state is networked.**
   - **Every hazard effect** (knockback, launch, conveyor push, carry, door solidity) runs inside `PlayerStep.Run`, the one function the server and client prediction share. Prediction must stay exact through every hazard.
   - Collision = visuals (doc 08 §1.2): motor-relevant colliders on `Gameplay`; visual-only meshes have no colliders.
   - Don't implement fallback F1.
4. **Hazard time is round-relative** (doc 08 §2): `t = (serverTick − roundStartTick)`. Today poses use the absolute tick; change that, and keep `Map_Sandbox` working (its round starts at tick 0).
5. **Config files and the chain:**
   - **Do not modify `ruleset_v1.json`, `map_pool_v1.json` or anything on-chain.** Their hashes may be committed on-chain; changes are an M6 task.
   - Modifier effect values go in a **new** `packages/shared/config/modifiers_v1.json` (canonical JSON + sha256 via `pnpm hash`, generated C# constants, a hash-match EditMode test: the `movement_v1.json` pattern). Ids must equal `map_pool_v1.json`.
   - Map content (resolved hazard defs, checkpoints, spawns, kill-Y) gets a **map content hash** (§3.3).
   - `movement_v1.json` changes only if tuning demands it; record why.
6. **Protocol:** bump `Protocol.Version` to **3**. Old clients must be rejected with the readable M1 message. Keep `snap` small: record its size for 12 players.
7. **Trust and wording:** never claim "cheat-proof"; numbers are "measured" only if you ran them; everything else is UNVERIFIED. "Fun", "fair" and "readable" are **my** calls after I play it; you report measurements and your honest read.
8. **8 GB MacBook Air M2:**
   - Iterate in the **Editor** and with the native **Mac dev server** (D-16). Multiplayer Play Mode with at most 1 virtual player.
   - **WebGL builds at most once per half-milestone**, with the Editor closed.
   - Never run the Editor, a batch build and Docker at the same time. At most 3 browser tabs.
   - **The Mac dev server is not bit-identical with WebGL and Linux** (proof B9; suspected FMA on arm64). Never judge cross-platform determinism or correction counts for the production pairing from Mac-only runs. Use WebGL ↔ Linux for that (§3.4).
9. **VPS** (Hostinger KVM 2, Mumbai; it hosts other projects, the nginx site `finity` and Ovelo, which you must never touch):
   - Use the M2 proof B8 pattern: snapshot first; the container bound to `127.0.0.1` with `--cpus 0.75 --memory 1g`; one nginx file from `infra/nginx/fallrush-gs1-vps.conf`; check `nginx -t`'s **own exit status** before a reload; reload, never restart.
   - **Ask me once, with the exact command list, before any VPS write in M3.** That includes a one-off `docker run --rm`.
   - **No host-wide commands** (`docker image prune`, `docker system prune`, `docker rm $(…)`, apt upgrades). Only touch `fallrush-*` names. M2 ran an unapproved prune once; don't repeat it.
   - SSH uses `~/.ssh/ovelo_vps_ed25519`. Docker Manager does not work on this VM. The `fallrush-gameserver:m2` image and the `gs1` certificate are on the VPS; the `gs1` nginx file was removed after M2.
   - Never commit the IP, VM ID or hostnames; use `<VPS_IP>` / `gs1.<IP-dashed>.sslip.io`.
10. **Git:** small local commits at logical checkpoints. **Never push, and never add a remote.** No build output in git.
11. **Originality** (doc 17 §1, doc 08 §1.5 and §8): every obstacle and the course are our own toy-block design. No Fall Guys level layouts, obstacle shapes, UI or sounds, or close copies. Doc 08 §3 is already an original layout; don't "improve" it towards a known level.
12. **Unity file rule (M2 lesson):** one MonoBehaviour / ScriptableObject class per file, with the file named after the class. A two-class file broke the M2 player build.

## 2. Workflow

0. If `git status` shows uncommitted changes to `docs/prompts/M3-PROMPT.md` or `PROJECT_STATUS.md`, commit them first as `docs: M3 prompt`.
1. **Before coding**, give me a short plan with:
   - files to create or change;
   - the hazard def structs (fields and units) and each pose/effect function;
   - how the motor handles conveyor push, launch, carry (linear and rotational) and doors;
   - the new message(s) and their byte sizes, and the new `snap` size for 12;
   - the race state machine and which events are reliable;
   - any doc discrepancy you found.

   Then proceed. M3a is approved.
2. **M3a → checkpoint → M3b.** At the checkpoint, **stop and wait for my "continue"**. M3b is approved only after that.
3. Run every build and test yourself. Fix errors; never weaken a test to get green.
4. **Time-box:** if one blocker eats more than ~2 hours, stop and report the error, logs, what you tried, and the options.

## 3. Engineering requirements (both halves)

### 3.1 The obstacle kit (8 types, `Shared/HazardMath.cs` or split into `Shared/Hazards/*.cs`)
Each type = an integer def struct + a pure pose function + an effect hooked into `PlayerStep.Run`, plus one scene authoring component per type (`Net/Hazard<Type>.cs`, resolving to the def, like `HazardSweeper`) and client-only visuals with telegraphs.

| Type | Pose | Effect (doc 08 §2) | Notes |
|---|---|---|---|
| `SPINNER_BAR` | `yaw = φ + ω·t` | `knock_soft`: stun 0.35 s, push 6 m/s tangential | Extend the M2 sweeper: height, radius, direction, variant |
| `MOVING_PLATFORM` | Looping path: generalise the M2 sine to a **closed polyline / Catmull-Rom loop** sampled by phase (integer mm knots) | `carry` | Decide and document jump-off velocity inheritance (M2 note: riders don't inherit today; doc 08 says they do). Inherit if prediction stays exact |
| `CONVEYOR` | static | `surface_push`: surface velocity `v` added while grounded on it | Through `IMotorWorld` (surface id), predicted |
| `SPRING_PAD` | static (optional visual squash) | `launch`: impulse `J` on enter, once per contact, short re-trigger lockout | Must clear the authored gap at reference speed |
| `TURN_TABLE` | `yaw = φ + ω·t` | `carry_rot`: **angular carry** (`SurfaceDelta` with a contact point: rotate the rider's feet about the axis and add yaw to the facing only if the design wants it) | M2 note: needs a point-aware `SurfaceDelta` |
| `GATE_DOOR` | piecewise cycle: closed / opening / open / closing (ticks) | `solid` while not open; its collider is posed or disabled per tick | All doors are real and timed (doc 08 §3, S4). **Never "guess the fake door".** |
| `MALLET_SWING` | `angle = A·sin(2π·t/T + φ)` | `knock_hard`: stun 0.6 s, push 9 m/s | Clear wind-up telegraph; analytic contact like the sweeper |
| `BUMPER` | static | radial push, `knock_soft` | Radius-based, analytic |

- **Knockback rules (doc 08 §2):** velocity override, not accumulated force. **0.5 s immunity after a stun ends.** No stun-lock between two hazards: test it.
- **Telegraphs (doc 08 §1.3):** every hazard that can hit winds up visibly for at least `telegraphMs` (stripes, a colour pulse or a pre-move), client-only, driven by the same pose function.
- **Parameters:** each def has an allowlist (`allowedRanges`) that the server validates when the map loads. Out-of-range = refuse to start the round, with a readable log.
- **Per-obstacle phase:** `φ = PhaseFromSeed(hash(hazardSeed, obstacleId))` mapped into the obstacle's `phaseRange` (doc 08 §2). `obstacleId` is a stable string id authored on the component; hash it with a fixed integer hash (document which). The TS side (`H(vrf_seed ‖ round ‖ obstacleId)`, doc 06) is M6: use a C# helper with a clear `TODO(M6)` to align with the on-chain derivation.

### 3.2 Round parameters (VRF-ready, no chain yet)
- `RoundParams { mapId, variant, hazardSeed, modifier, roundStartTick, qualifyCount }`. The server owns it; clients get it in a reliable round message before the countdown.
- **Source in M3:** a dev seed (`-fallrushSeed`, `-fallrushVariant`, `-fallrushModifier`, and a WebGL query param for local tests). If unset, derive variant and modifier from the seed using `map_pool_v1.json`'s variant count and modifier weights. M6 replaces the source with the VRF deal; keep that a one-line swap.
- **Variants** (doc 08 §3): S1 bob vertical / lateral, S2 direction pattern, S4 sweep order. Variant A/B = a different def set resolved from the same scene.
- **Modifiers** for the race (GDD §8, allowed on Block Party Dash: None, MoonBounce, ButterFloors, TurboToys, TinyTilt):
  - MoonBounce (gravity ×0.7, apex ×1.25) and ButterFloors (ground decel ×0.4) change the **motor config used by both sides**;
  - TurboToys (hazard speed ×1.2, telegraph unchanged) changes hazard periods as **exact integer ticks** (document the rounding);
  - TinyTilt is client-visual only (FOV −8°, fog).
  - Values come from `modifiers_v1.json` (rule 5).

### 3.3 Map data and the content hash
- `MapDefinition` ScriptableObject per doc 08 §8 (`mapId`, `version`, `mode`, `minPlayers`, `maxPlayers`, `targetDuration`, `checkpointCount`, `variants`, `allowedModifiers`), replacing the M2 `MapInfo` stand-in, or wrapping it if that is less invasive.
- **Map content hash:** sha256 over a canonical byte encoding of the resolved hazard defs (both variants), checkpoints, respawn points, spawns and kill-Y.
  - The server sends it in the round message. A client whose own hash differs **refuses to play that round** with a readable error, so mismatched builds can't desync silently.
  - Log it in the server stats and the proof. Keep it stable across rebuilds (test).

### 3.4 Cross-platform determinism (from the M2 B9 finding)
- An EditMode **vector test** per doc 08 §9: 1,000 random `(type, def, seed, variant, modifier, tick)` cases → pose and effect outputs, quantised to the wire precision, written to `client-unity/Assets/_Game/Tests/Vectors/hazard_vectors_v1.json` (committed), and re-checked on every EditMode run.
- A **self-test entry point** that runs the same vectors in a player build and prints one pass/fail line plus a digest:
  - Linux server: a CLI flag (for example `-fallrushSelfTest`), run once on the VPS as `docker run --rm` (needs my yes; §1.9);
  - WebGL: a query param, run in headless Chrome (Playwright).
- Report a mismatch exactly: which case, which platform, the value difference. If the arm64 Mac differs, record it; it is not a blocker unless WebGL or Linux disagree with the vectors.
- If a hazard function is not bit-identical between WebGL and Linux, fix the maths (integer-first, avoid patterns the compiler can contract into FMA, and no transcendental library calls) before M3b.

### 3.5 Netcode under a real race
- **Snapshots:** if the race needs per-player round state (checkpoint index, finished/qualified flag), add it compactly and record the new size. Domain events (checkpoint reached, finished, qualified, round end) go in a **reliable** message with a sequence number; the HUD must show the right state after a reconnect.
- **Crowd pushes (R14):** player–player separation is server-only, so a 12-player start and bridge bottlenecks will cause push corrections. Measure them per 5 s on the full race. If the pass bar (< 1 correction per 5 s while running, pushes included) is missed in the crowd, try the M2 note: predict separation against interpolated remote positions. Measure before and after; keep it only if it helps.
- The M2 HUD (F3) stays and adds: round phase, round tick, current checkpoint, place, map hash short id, variant, modifier.

## 4. M3a deliverables (then checkpoint)

1. **The 8 hazard types** (§3.1): def + pose + effect + authoring component + telegraph visuals, and allowlist validation.
2. **Point-aware carry** in `IMotorWorld` (linear + rotational), conveyor surface velocity, launch, door solidity per tick. `PhysxMotorWorld` and the analytic test world both implement it.
3. **Round-relative hazard time** (rule 4) and `RoundParams` with the dev seed source, variants and the race modifiers (§3.2). `modifiers_v1.json` + hash + hash test.
4. **`MapDefinition`** + the map content hash + the client hash check (§3.3).
5. **`Map_Sandbox` extended** with one of each new hazard type in a "kit row" (spawn → each hazard → back), so I can try every obstacle alone. Keep the M2 sandbox features.
6. **EditMode tests:**
   - every pose function: periodicity, phase from seed, TurboToys period rounding, variant resolution;
   - every effect: a standing player is hit and a jumping/diving player clears where designed; knock_soft vs knock_hard values; immunity 0.5 s; **no stun-lock** between two adjacent hazards;
   - carry: a rider on a looping platform for 1 full loop ends where the platform does (mm-exact); a rider on a turn table for one turn returns to the same spot (within 1 mm) and is never flung off at authored speeds; jump-off inheritance as documented;
   - conveyor: steady-state speed with and against the belt; spring pad reaches its designed apex/landing ± 5 cm; door: blocked while closed, passable while open, a player inside a closing door is pushed out, never stuck;
   - **prediction vs a server twin, bit-identical through every hazard type** (extend the M2 test);
   - modifiers: MoonBounce apex 2.0 m ± 2 cm (1.6 × 1.25); ButterFloors decel; both sides use the same modified config;
   - hazard vectors (§3.4) and the map hash stability test;
   - allowlist: an out-of-range def is refused.
7. **Self-test entry points** (§3.4) built and run: EditMode + Mac + WebGL headless. The Linux run waits for the one VPS approval (it can go in M3b's VPS batch).
8. **Measure:** server sim step p95 with 12 bots in the kit sandbox (Mac dev server); `snap` bytes for 12.

**Checkpoint report (then stop):**
- what was built; test counts;
- each hazard's def and effect values as built, and the carry/jump-off decision;
- the determinism results per platform;
- the measured numbers; the protocol v3 sizes;
- your plan for M3b, including the race state machine and the waypoint bot;
- open questions;
- how I can try every obstacle, with exact commands (Editor and Mac dev server; WebGL only if it was built).

## 5. M3b deliverables (after my "continue")

### 5.1 `RoundRules.Race` (`Shared/`, engine-free, driven by `SimCore`)
- **Phases:** `Intro` (camera fly-through, optional, ≤ 4 s) → `Countdown` (3-2-1, inputs ignored except camera, start gate closed) → `Rush` (gate drops) → `Ended` → results.
- **Ordered checkpoints** (server triggers; doc 11): a checkpoint counts only if it is the **next** one and the player passed a reachability check (distance from the previous checkpoint and elapsed time consistent with max speed plus launch/carry). A skipped checkpoint never counts.
- **Fall volume:** below the map's kill-Y → respawn at the **last checkpoint** after 1.0 s, on a free respawn spot (no stacking), facing the course.
- **Finish:** the first **N** valid finishers qualify (`qualifyCount`, from `RoundParams`; in M3 default to `ceil(P × 0.66)` per the ruleset, computed in one place with a `TODO(M4/M6)` to read `ruleset_v1`). When N have finished, the round ends immediately.
- **Timer** (target 75–110 s; set the cap from the QA medians, record it): on expiry the rest are ranked by the GDD §5.1 key: checkpoint index ↓, distance to the next checkpoint ↑, time reached the last checkpoint ↑, slot index ↑. "Distance" is along the course (a polyline through checkpoints), not straight-line; document it.
- **Finished players:** removed from collisions, shown at the finish, with the camera switching to a simple spectate follow (Tab cycles players). Full spectating is M4.
- **Output:** a `RaceResult` (ordered slots, qualified flags, finish ticks) that M4's `MatchDirector` and M6's chain events will consume. Deterministic: the same inputs give the same result (test).

### 5.2 Block Party Dash greybox (`Editor/BlockPartyDashBuilder.cs` → `Scenes/Map_BlockPartyDash.unity`)
- Code-built and re-runnable, like `SandboxBuilder`. The doc 08 §3 layout, about 160 m:
  - Start deck: wide 12-slot staggered start plaza, gate drops at "RUSH!".
  - S1 Block Hop: three rows of floating blocks; the middle row bobs (variant A vertical / B lateral).
  - CP1.
  - S2 Spinner Alley: wide bridge, 3 spinner bars at staggered heights (one low to jump, one high to dive under; variant = direction pattern).
  - S3 Conveyor Climb: two lanes, a conveyor ramp against you (safe, slower) or spring-pad hops (fast, risky).
  - CP2.
  - S4 gate wall: five gate doors cycling open in a sweep (variant = sweep order). All doors are real and timed.
  - CP3.
  - S5 Finish ramp: broad ramp, scattered bumpers, a checkered finish arch.
- **Two routes minimum** per section (doc 08 §1.4). Bottlenecks wide enough for 12 (measure the crowd, §3.5).
- Doc 08 §8 checklist items that apply: spawns, ordered checkpoints with respawn transforms, fall volume and bounds, `Gameplay` vs `VisualOnly` layers, allowlist pass, readability (hazard stripes, telegraphs, a colourblind-safe check of hazard vs floor colours using doc 17 §3.1), and the performance budget (doc 17 §3.2).
- Greybox look: doc 17 palette, flat colours, blob shadows if cheap. No final art.

### 5.3 Waypoint bot (`Shared/WaypointBot.cs`; the base of M4's `BotBrain`)
- Produces `InputCmd`s from an authored waypoint graph per map (both routes), through **the same sanitiser and motor** as humans. Labelled BOT.
- Knows only what a player can know: its own state, the hazard pose functions, the next waypoints. It times jumps at gaps, dives under the high bar, waits for doors, and recovers after a knockback or a fall.
- A "reference speed" mode (no noise) for QA; M4 adds noise and mistakes.

### 5.4 Automated map QA (doc 08 §9)
- A headless runner on the **server build** (Mac dev server is fine for this): Block Party Dash × variant A/B × each allowed modifier × **100 runs** with 12 waypoint bots (randomised starting slots and route choices per run from a run seed).
- **Assert:** no soft-lock (every bot finishes or is timed out with a logged reason); a finish is reachable on both routes; no tick where every route through a section is blocked; the median duration is within the target (best line ≈ 45 s, average 75–95 s); qualification gives exactly N; the result is deterministic for a given run seed.
- Output a summary table (per variant × modifier: finish rate, median/p90 duration, falls, hazard hits, soft-locks) into the proof. Use the numbers to tune the course and record each tuning change and why.

### 5.5 Race HUD and screens (client, greybox UI via `UiKit`)
- Countdown "3-2-1-RUSH!", a round timer, place / qualified count (`QUALIFIED 3/8`), checkpoint ticks, a "QUALIFIED!" / "ELIMINATED" banner, a simple end-of-round results list (place, name/slot, BOT tag, qualified).
- The variant and modifier shown on round start (text is fine; the Chaos Capsule machine is M7).
- Reduced motion respected. No final art.

### 5.6 PlayMode test (doc 12)
- A server-mode PlayMode test: load `Map_BlockPartyDash`, 12 waypoint bots, run a full race to `Ended`; assert N qualified, ordered results, no exceptions. Plus a **checkpoint-skip test**: a player that goes around a checkpoint, or whose state the test harness moves straight to a later checkpoint, never gets it counted, and a scripted fall respawns at the last checkpoint after exactly 30 ticks.
- If PlayMode is too heavy on 8 GB in batch mode, say so and run it as an EditMode `SimCore` + `PhysxMotorWorld` test instead, with the reason.

### 5.7 Measurements (the M3 gate)
- **Conditions:** (a) localhost with 3 clients + 9 bots on the full race; (b) **the real VPS** (needs my yes; one approval for the whole M3 VPS batch: the Linux self-test + the race run), WebGL client(s) ↔ Linux server; (c) 150 ms + jitter + 2 % loss via `scripts/m2-netem.sh`.
- **Per condition:** corrections per 5 s while racing (split into hazard / push / other), mean/max correction distance, hazard-contact mismatches (must be 0), replay length, remote stutter, client FPS on the WebGL release build with 12 Rushlings in the heaviest section, server sim p95 with 12 occupants, `snap` bytes/s.
- **Pass bar:** < 1 correction per 5 s while racing; 0 hazard mismatches; no visible rubber-banding on any hazard; QA all green. If the crowd is the only cause of misses, report the push-prediction experiment (§3.5).
- Give me exact steps to play each condition myself.

### 5.8 Builds
- One WebGL release build at the end, with the Editor closed. Record build time and size against M2 (16.03 MB).
- Server: the Linux x86_64 image `fallrush-gameserver:m3` for the VPS, and the Mac dev server for local work.
- After the VPS run, leave the VPS as I instruct and verify `finity` (https 200, config md5) and the Ovelo containers are unchanged.

## 6. Docs to update in the same change
- **doc 08:** §2 the kit as built (def fields, units, effect values, telegraphs, allowlists), §3 Block Party Dash as built (real lengths, tuned values, routes), §9 the QA runner and its results.
- **doc 04:** §4 protocol v3 messages and sizes, round-relative hazard time, `RoundParams`, the map hash check, the push-prediction result; §9 the waypoint bot; §11 the test list.
- **doc 01:** §5.1 anything that changed in the race rules (timer cap, distance definition), §8 modifier values as built.
- **doc 11:** the checkpoint reachability check as built.
- **doc 15:** R14 status with data, any new risk; any decision change goes in as 🟡 for me.
- **`docs/proof/M3-race.md`:** every measurement with its method per condition, the determinism table per platform, the QA table; small PNG screenshots in `docs/proof/m3/` (the kit row, each section of Block Party Dash, the race HUD, the results screen, the HUD under 150 ms).
- **`PROJECT_STATUS.md`:** M3 status, the gate data, notes for M4 (MatchDirector inputs: `RaceResult`, `RoundParams`) and M6 (the hazard-seed derivation, `modifiers_v1` and map hashes into a ruleset/map-pool v2).
- **`README.md`:** an "M3: run it" section.

## 7. Definition of done (report each item with evidence)

**M3a:**
- [ ] 8 hazard types with pure pose functions, effects in `PlayerStep.Run`, telegraphs, allowlists
- [ ] Point-aware carry (linear + rotational), conveyor, launch, doors; round-relative time; `RoundParams`; variants; race modifiers; `modifiers_v1.json` + hash test
- [ ] `MapDefinition` + map content hash + client refusal on mismatch
- [ ] Kit row in `Map_Sandbox`
- [ ] EditMode tests green (counts), including prediction-vs-server through every hazard and the hazard vectors
- [ ] Self-test run on Mac + WebGL (Linux pending the VPS approval)
- [ ] Checkpoint report sent; **waited for "continue"**

**M3b:**
- [ ] `RoundRules.Race`: countdown, ordered checkpoints with reachability, respawn at last checkpoint, finish → N qualify, timer ranking, `RaceResult`
- [ ] `Map_BlockPartyDash` greybox per doc 08 §3, two routes per section, checklist items done
- [ ] Waypoint bot; map QA 2 variants × modifiers × 100 runs, all green, table in the proof
- [ ] Race HUD + results screen
- [ ] PlayMode (or justified EditMode) bot race + checkpoint-skip test
- [ ] Gate data for localhost / VPS / 150 ms, crowd pushes measured; Linux self-test on the VPS
- [ ] WebGL release build time, size and FPS recorded
- [ ] Docs 01 / 04 / 08 / 11 / 15, `PROJECT_STATUS.md` and `README.md` updated
- [ ] No secrets, VPS identifiers or build output in git
- [ ] Local stack stopped; VPS left exactly as I instruct; other projects verified unchanged; no host-wide commands run

## 8. Final report format

1. What was built (files), M3a and M3b
2. Test results (counts), the determinism table per platform, the QA table
3. Gate data table per condition (hazard / push / other corrections), with your honest read and recommendation
4. Hazard values and course tuning as built, and why
5. Deviations from the docs, and why
6. Risks and unknowns for M4 (match flow, other maps, BotBrain) and M6 (seed derivation, hashes on-chain)
7. How I can play it myself: exact commands for Editor, local WebGL and VPS
8. Next milestone: M4 (match director + 3 more maps + bots), per `docs/14`
