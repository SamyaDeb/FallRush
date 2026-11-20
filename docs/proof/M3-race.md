# M3 proof: obstacle kit + race round (2026-11-19)

All numbers below were measured on the dev machine (MacBook Air M2, 8 GB) or the VPS unless marked **UNVERIFIED**. Raw logs live in `.m1-logs/` (git-ignored).

## A. What was built

| Area | Files |
|---|---|
| Obstacle kit (8 types: def + pure pose + effect) | `Scripts/Shared/Hazards/HazardMath.cs`, `KitDefs.cs`, `HazardSet.cs` (incl. `PlayerStep`), `HazardAllowlist.cs`, `HazardResolve.cs`, `HazardTrig.g.cs` (Q30 sine table), `HazardVectors.cs` + `HazardVectorsExpected.g.cs` |
| Modifiers | `packages/shared/config/modifiers_v1.json` (sha256 `09eef853…`), `scripts/gen-modifiers-cs.ts` → `Shared/ModifiersData.g.cs`, `Shared/ModifierEffects.cs` (`HazardTuning`), `MovementConfig.For(modifier)` |
| Round + map data | `Shared/RoundParams.cs` (dev seed, `MapPoolV1` mirror, `ObstaclePhase`), `Shared/MapContent.cs` (map content hash), `Shared/Sha256.cs`, `Net/MapDefinition.cs` (SO), `Net/MapInfo.cs`, `Net/RaceCheckpoint.cs` |
| Authoring components (one class per file) | `Net/HazardAuthoring.cs`, `HazardSweeper` (SPINNER_BAR), `HazardPlatform`, `HazardConveyor`, `HazardSpringPad`, `HazardTurnTable`, `HazardGateDoor`, `HazardMallet`, `HazardBumper` |
| Protocol v3 | `Shared/Protocol.cs`, `NetMessages.cs` (race byte), `RoundMessages.cs` (`round`, `rstate`, `evt`) |
| Race | `Shared/RaceRules.cs` (+ `RaceResult`), `Shared/WaypointBot.cs`, `Net/ServerRace.cs`, `Net/MapQa.cs`, `Net/RaceProbeBot.cs`, `Net/SelfTest.cs` |
| Client | `Client/HazardViewDriver.cs` (visuals + telegraphs), `Client/RaceHud.cs`, `SandboxView` (spectate, TinyTilt FOV/fog, HUD lines) |
| Maps | `Editor/KitBuilder.cs`, `Editor/SandboxBuilder.cs` (kit row), `Editor/BlockPartyDashBuilder.cs` → `Scenes/Map_BlockPartyDash.unity`, `Editor/HazardVectorsTool.cs`, `Editor/MapHashTool.cs`, `Editor/QaDebug.cs` |
| Tests | `KitTests`, `KitPredictionTests`, `MapSceneTests`, `RaceRulesTests`, `BlockPartyDashTests` (+ updated M2 protocol/size tests) |
| Scripts | `scripts/m3-gate.sh`, `scripts/m2-summarize.py` (racing windows, hazard corrections) |

## B. Tests

**EditMode 207 / 207** (M2: 153). New: kit poses/periodicity/round-relative time, TurboToys rounding, seeded phase, variant resolution, dev-seed deal, low bar jumped / high bar dived, knock_soft vs knock_hard, immunity + no stun-lock (crossfire of 2 spinners + mallet: 40 hits, ≥ 15 control ticks before every hit), loop platform mm-exact, turn table one turn 0.00 mm (periods 240 / −300 / 900), jump-off inheritance, conveyor 8990 / −4990 / 2000 mm/s (expect 9000 / −5000 / 2000), spring apex 3.000 m and landing 5.629 m (design 3.000 / 5.629), doors (blocked / open / closing push-out, never stuck), MoonBounce apex 2.000 m, ButterFloors stop 12 ticks (vs 5), allowlist refusal, modifiers_v1 + map-pool mirror hash tests, hazard vectors, map hash stability, round-message round trips, **prediction vs a server twin bit-identical through every hazard type** (leads 1/3/6 × delay 2/5 × None/MoonBounce/TurboToys: 0 corrections, 0 silent, 0 hazard mismatches; spinner/mallet/bumper hits, spring launches, door push-outs, conveyor/turn-table/platform carry all exercised), race rules (N qualify, round ends at the N-th finisher, deterministic, countdown input lock + start gate, checkpoint skip/teleport never counts, respawn after exactly 30 ticks on free spots, timer ranking key, finished racers out of sim/collisions, course distance along the polyline), and a full 12-bot race on the real `Map_BlockPartyDash` scene (PhysX) for variants A and B.

**PlayMode → EditMode (justified):** the full-race test runs `SimCore` + `PhysxMotorWorld` on the real scene in EditMode. A PlayMode batch run would also load NGO, the player loop and the client view on the 8 GB machine; the race logic is engine-free and the PhysX world is the same one the server uses.

## C. Cross-platform determinism (hazard_vectors_v1: 1,000 cases × 12 ints)

| Platform | How | Result |
|---|---|---|
| Unity Editor, macOS arm64 (Mono) | EditMode test (generated the vectors) | PASS, digest `fc06826c90569570…` |
| Mac player, arm64 (Mono) | `FallRushDevServer -fallrushSelfTest` | **PASS** (26 ms) |
| WebGL (wasm), Chrome headless (Playwright) | `?selftest=1` | **PASS** (3 ms) |
| Linux x86_64 server (Mono), VPS Docker | `docker run --rm fallrush-gameserver:m3 -fallrushSelfTest` | **PASS** (AMD EPYC 9354P, 12 ms) |

Why the arm64 Mac now agrees (it did not in M2 for the motor): every hazard pose and contact is integer-first (literal Q30 sine table with integer interpolation, mm/Q30 integer geometry; floats only for the final push-direction normalisation, with explicit casts), so there is nothing for FMA contraction or libm to change.

## D. Map QA (doc 08 §9)

Mac dev server build, `-fallrushQA 100`, 12 waypoint bots, random start slots + route choices per run seed: **1,000 races in 70 s**. Full table: [`m3-map-qa.md`](m3-map-qa.md).

| | Result |
|---|---|
| Soft-locks | **0** (all variants × modifiers) |
| Qualified = N (8) | **1000 / 1000** |
| Every route reaches its next checkpoint | yes (15 routes, all with finishers) |
| Ticks with every S4 door closed | **0** |
| Deterministic for a run seed | yes |
| Median / p90 round duration | **46–49 s / 48–51 s**; best line median 40–42 s (after the 2026-11-19 lengthening, §I; was 31–34 s) |
| Duration target (reference bots 40–60 s ≈ humans 75–110 s) | **met** (overall median 47.0 s). Human times UNVERIFIED until a playtest |

**Tuning changes (each one from a QA run):**
1. S1 lanes: bots overshot jump points, turned back, fell (≈ 100 falls/race) → jump points count once passed; block centres are `Land` targets (air control brakes over them).
2. S3 spring pad 2 launched short from its near edge → forward launch 7.5 → 9 m/s.
3. **Motor:** air control braked a 9 m/s launch back to 7 m/s when holding forward (shorter hops when you push forward). Now above run speed air control only steers. Prediction stays exact (shared code).
4. S4 door cycle 150 → 210 ticks (open 45, offsets 42 apart: always ≥ 1 open); S3 conveyor 2.5 → 3.5 m/s: hazards cost time.
5. Allowlist caught door closing speed under TurboToys (15 → 13 ticks = 223 mm/tick > 200): doors close over 18 ticks.

## E. Gate data

**Method:** `scripts/m3-gate.sh`: 3 headless race clients (`-fallrushProbeScript race`: the same waypoint bot, steering from the *predicted* state, through the real client path) + 9 server bots = 12 occupants on `Map_BlockPartyDash`; the server loops rounds (intro 3 s, countdown 3 s, race, results 10 s). "While racing" = 10 s windows in phase Rush. Push = server-reported player–player push (not predicted by M2).

| | (a) localhost | (c) 150 ms + jitter + 2 % loss | (b) real VPS (Mumbai) |
|---|---|---|---|
| Path | `ws://127.0.0.1:7777`, Mac dev server | `scripts/m2-netem.sh start 75 15 2` → Mac dev server | `wss://gs1.<IP-dashed>.sslip.io` → nginx → Linux container (`--cpus 0.75 --memory 1g`) |
| RTT p50 / p95 | 17 / 18–33 ms | 177–193 / 262–311 ms | **157–629 / 417–10,164 ms (degraded link, see below)** |
| Corrections / 5 s while racing (non-push) | **0.08 / 0.00 / 0.00** | **0.23 / 0.36 / 0.38** | 5.9–9.9 (late/repeated inputs from stalls) |
| Push corrections / 5 s while racing | 1.04 / 0.67 / 0.21 | 1.32 / 0.86 / 1.31 | 0.46–1.27 |
| Hazard corrections / hazard-contact mismatches | **0 / 0** | **0 / 0** | 2 / 2 (one client, during multi-second stalls) |
| Mean / max correction distance | 9 cm / 10 cm | 29–39 cm / 74–118 cm | 50–86 cm / up to 11.7 m |
| Replay length max | 3 ticks | 18–24 ticks | 94–481 ticks |
| Remote extrapolating / frozen frames | 0 % / 0 % | ≈ 2 % / ≤ 5 % | 14–16 % / ≤ 55 % |
| Server sim step p95 (12 occupants) | 0.28–0.32 ms (Mac) | 0.23–0.32 ms (Mac) | **0.20–0.25 ms (Linux, 0.75 vCPU)** |
| Server frame work p95 / CPU / RSS | 0.70–0.76 ms / 2.7 % / 67 MB | — | **0.72–0.84 ms / 3.2 % / 99 MB** |
| `snap` | 294 B (12 players); 4.4 KB/s payload per client at 15 Hz; server socket out 6.0 KB/s per client | same | 4.4–5.6 KB/s payload |

**Pass bar (< 1 correction per 5 s while racing, pushes included; 0 hazard mismatches):**
- Non-push corrections pass everywhere the link works (≤ 0.08 localhost, ≤ 0.38 at 150 ms). Hazard mismatches are 0 in (a) and (c).
- **Crowd pushes miss it:** localhost totals 1.12 / 0.67 / 0.21 (one client just over); 150 ms totals 1.55 / 1.22 / 1.69.

**R14 push-prediction experiment** (localhost, same seed, 3 clients, corrections + pushes per 5 s while racing): off 1.45 / 5.59 / 1.14 (mean 2.73) → **on 1.00 / 0.59 / 0.59 (mean 0.73)**. Kept on (`ClientSim.DefaultPredictPushes`; `?pushpred=0` / no flag to compare). The client applies the server's soft separation to itself against remotes extrapolated from the newest snapshot (≤ 12 ticks). A 24-tick cap at 150 ms gave no clear gain (1.70 / 1.25 / 0.75).

**(b) VPS, what happened:** the server ran fine (22 race rounds, 0 rejected checkpoints, numbers above). The **link** from this Mac was degraded during the run: ICMP to the VPS showed **85 % packet loss** (M2: 38 ms, 0 % loss) and uploads ran at ~17 KB/s. The probe numbers therefore measure the network outage, not the netcode. A WebGL release client → VPS run (`?bot=race`) connected, matched the map hash (`6780ef6c`) and ran a race at 59 FPS with 0 hazard mismatches before the link stalled completely (ping 21 s). **The production-pairing gate (WebGL ↔ Linux on a healthy link) is UNVERIFIED for M3** and should be re-run.

**VPS steps:** Hostinger snapshot **not** taken (CLI OAuth token expired; `hostinger` hung) → substituted a backup of `/etc/nginx` (`/root/fallrush-nginx-backup-20261119.tgz`). Image built on the VPS from the `m2` image's files + an rsync of the 15 changed files (1.7 MB; full upload would have taken ~1 h). Container `fallrush-gs1` bound to `127.0.0.1:7777`, one nginx file from `infra/nginx/fallrush-gs1-vps.conf`, `nginx -t` exit 0 → reload. **Afterwards:** container and nginx file removed, `nginx -t` exit 0 → reload, `/root/fallrush-build-m3` removed; images `fallrush-gameserver:m2` and `:m3` kept; `finity` config md5 `c6a6cca8` (unchanged), https 200, Ovelo containers up throughout. No host-wide commands.

## F. Client FPS and builds

| | Result |
|---|---|
| WebGL release, Chrome (Playwright), 12 Rushlings, racing Block Party Dash | frame p50 17.0 ms, p95 18–19 ms → **59 FPS** (Playwright's default window) |
| WebGL release M3a | 959.8 s build / 1037.6 s wall, **16.17 MB** |
| WebGL release M3b (final) | 410.8 s build / 460.9 s wall, **16.22 MB** (M2: 16.03 MB → +0.19 MB) |
| Linux server | 15.2 s build, 99 MB, image `fallrush-gameserver:m3` 68.3 MB |
| Mac dev server | 16–40 s, 141 MB |

## G. Screenshots (`m3/`)

`race-hud-intro.png` (GET READY + variant / Chaos Capsule banner), `bpd-s2-spinner-alley.png`, `bpd-s2-high-bar-hud.png` (race HUD: QUALIFIED x/8, checkpoint ticks, timer, place), `bpd-s3-conveyor-ramp.png`, `bpd-s4-gate-wall.png`, `bpd-s5-finish-ramp.png` (bumpers, checkered arch), `race-results.png` (12 rows, BOT tags, qualified/eliminated).
**Not taken:** the kit row in `Map_Sandbox` (the probe camera follows a scripted player that does not walk the kit row) and the HUD under 150 ms (the netem relay could not reach the Mac server at screenshot time). The S3 floor label is too large for its distance (cosmetic).

## H. Bugs found and fixed while measuring

- **`rstate` overtook `evt`:** on a phase change the server sent the round state (whose sequence already covered the new events) before the events, so clients dropped them as stale; the results list showed 7 of 12. State is now sent after the tick's events.
- **Null vs empty arrays in the map hash:** Unity serialises null arrays as empty; the hash changed after a save. Null is now encoded as empty.
- **Finished racers kept their velocity** (frozen but "running"): zeroed at the finish.
- **Gate counters went negative** when the prediction was rebuilt for a new round: reset per prediction instance.

## I. Course lengthened (2026-11-19, your decision 1a) and push result accepted (1b)

**Why:** reference bots finished the ~166 m course in ~31 s, far below a 75–110 s human round.

**What changed** (`Editor/BlockPartyDashBuilder.cs`, doc 08 §3.1): two new sections between CP3 and the finish, **S5 Turntable Islands** and **S6 Mallet Bridge**, plus CP4 and CP5; the finish ramp moved to z 224…247. Course line 166 → 255 m, 6 checkpoints, timer cap 110 → 150 s, map hash `ffc099ad…`. The QA duration target is now stated for reference bots (40–60 s), since they run ~1.5–2× faster than humans.

**Tuning (each from a traced QA run):**
1. Mallets swinging across a 6 m bridge with no rails: ~20 falls per race (knock_hard throws you off).
2. Added 1.2 m rails: falls gone, but racers were pinned between mallet and rail (one race: 413 hits, 5 soft-locks, timer expiry).
3. Mallets now swing **along** the bridge with a 2.5 m half-width roller head (allowlist max), 10 m apart, amplitude 70°, ~4 s period: a hit bounces you back down the bridge. Bot wait points moved to the safe pockets between mallets.

**Map QA (100 runs × A/B × 5 modifiers, Mac dev server, 105 s wall):** PASS. 0 soft-locks, N = 8 in 1000/1000, 0 ticks with every door closed, deterministic, every route reaches its next checkpoint, median 46.2–48.9 s, falls 0.2–2.1 per race. Table: `m3-map-qa.md`.

**Localhost gate on the long course** (`scripts/m3-gate.sh`, 3 probe clients + 9 bots, 150 s): hazard mismatches **0**; corrections from the new sections **0**. Client 1: 0.00 non-push / 0.77 push per 5 s while racing. Clients 2 and 3: 1.00–1.05 non-push and 1.9 push per 5 s, **all inside one 10 s window** at the S4 gate wall (x 10, z 115.6) while bots crowded a door: the known crowd-push effect (R14), not hazard prediction.

**Pushes (1b):** accepted as is for M3: push prediction stays on, push strength unchanged (2.5 m/s). Re-measure on the real VPS and in the M4 playtest; softening pushes stays an option.

**Not redone after the change (UNVERIFIED for the long course):** the WebGL release build and its FPS (§F was measured on the short course), the 150 ms run and the VPS run. The VPS work is deferred by your decision ("plan the VPS for later": M6 deploy).
