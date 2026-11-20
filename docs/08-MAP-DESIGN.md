# 08 — Map Design & Obstacle Kit

**Status:** v2 (2026-11-18). Replaces `fall-rush-docs/08-MAP-DESIGN.md`.

**Related docs:**
- 01-GDD §4–9
- 17-ART-DIRECTION
- 04-UNITY-CLIENT §8

---

## 1. Principles

1. **Deterministic hazards.** Every moving obstacle's pose is a **pure function**:

   `pose = f(params, variant, hazard_seed, serverTick)`

   - The server and every client evaluate the same function, so **no hazard state is networked**.
   - Clients evaluate it at the interpolated server time.
   - Only *reactive* elements are replicated, with sequence numbers: crumbling tiles, collected stars and occupancy-triggered blocks.
2. **Collision equals visuals.** Server collision geometry is the same prefab collider set the client renders. Visual-only meshes carry no colliders.
3. **Telegraph everything.** Every hazard winds up visibly for at least its `telegraphMs` before it can hit.
4. **Two routes minimum** in every race section: a safe-slow route and a risky-fast route.
5. **Original.** Each map passes the originality check (§8). These maps do not trace any existing game's levels.

## 2. Obstacle kit (shared, data-driven)

Each obstacle is a prefab with a `HazardDefinition` ScriptableObject plus an optional variant list. The server validates parameters against `allowedRanges`.

| Type ID | Description | Pose function | Collision profile |
|---|---|---|---|
| `SPINNER_BAR` | Horizontal foam bar rotating around a post | `yaw = φ + ω·t` | `knock_soft` (stun 0.35 s, push 6 m/s tangential) |
| `MALLET_SWING` | Giant toy mallet on a pendulum | `angle = A·sin(2π·t/T + φ)` | `knock_hard` (stun 0.6 s, push 9 m/s) |
| `PISTON_PUSHER` | Block that slams out of a wall on a cycle | piecewise: rest / windup (telegraph) / extend / retract | `knock_hard` |
| `MOVING_PLATFORM` | Platform on a looping spline path | `pos = spline(frac((t+φ)/T))` | `carry` (the player inherits velocity) |
| `CONVEYOR` | Belt surface | static; surface velocity `v` | `surface_push` |
| `SPRING_PAD` | Launch pad | static; impulse `J` on enter | `launch` |
| `TURN_TABLE` | Spinning disc platform | `yaw = φ + ω·t` | `carry_rot` |
| `GATE_DOOR` | Door block cycling open / closed | piecewise cycle | `solid` |
| `GOO_PLANE` | Rising hazard plane | `y = y0 + v·max(0, t − delay)` | `eliminate` |
| `CRUMBLE_TILE` | Tile that drops after contact | **reactive, server-owned** | `solid` → removed |
| `STAR_PICKUP` | Collectible (Skill round) | spawn table from seed | `trigger` |
| `BUMPER` | Round toy bumper | static; radial push | `knock_soft` |

**Definition of `t`:**
- `t = (serverTick − roundStartTick) / TICK_RATE`.
- `φ` (phase offset) = `hash(hazard_seed, obstacleId)` mapped into a per-obstacle allowed range.

**Data model:**

```json
{
  "obstacleId": "bpd_spinner_02",
  "type": "SPINNER_BAR",
  "version": 1,
  "transform": {"position": [12, 3, 40], "rotationEuler": [0, 0, 0]},
  "params": {"angularSpeed": 1.1, "direction": 1, "radius": 4.5},
  "phaseRange": [0, 6.283],
  "allowedRanges": {"angularSpeed": [0.6, 1.6]},
  "collisionProfile": "knock_soft",
  "telegraphMs": 0,
  "variants": {"A": {"direction": 1}, "B": {"direction": -1, "angularSpeed": 1.3}}
}
```

**Knockback rules** (server):
- A stun cooldown of **0.5 s immunity after a stun ends** prevents stun-locks.
- Knockback is applied as a velocity override, not as accumulated forces.

### 2.1 As built (M3, 2026-11-19)

Code: `client-unity/Assets/_Game/Scripts/Shared/Hazards/`. Hazard time `t = serverTick − roundStartTick` in **ticks** (not seconds). Every pose is integer-first: a literal Q30 quarter-wave sine table (`HazardTrig.g.cs`) with integer interpolation, positions in mm, contacts in integer mm; floats only to normalise a push direction. Effects run inside `PlayerStep.Run` in this order: door push-out → motor (carried through `IMotorWorld.SurfaceDelta`) → knock hazards → spring pads → fall volume.

| Type | Def fields (units) | Pose | Effect as built | Telegraph (client) |
|---|---|---|---|---|
| `SPINNER_BAR` | pivot mm, arm mm, bar radius/height mm, period ticks (sign = direction), phase16, knock fields, `Tangential` | `angle = φ + t/T` (phase16) | knock_soft: push 6 m/s along the bar's travel (away from it if you ran into its back) + 3.5 m/s up, stun 11 ticks (0.35 s), immunity 26 | bar stripes; constant motion |
| `MOVING_PLATFORM` | centre/half mm, period, phase16, sine travel **or** 2–8 closed Catmull-Rom knots (mm) | integer Horner (Q16) | carry in exact mm; **jumping or diving off keeps its horizontal velocity** (walking off does not) | mint rim |
| `CONVEYOR` | box mm, pitch cdeg, belt mm/s (3D) | static | belt displacement per tick `round(v·t/30) − round(v·(t−1)/30)` mm (no drift) | chevrons light up in a travelling wave at belt speed |
| `SPRING_PAD` | top centre mm, half extents, launch cm/s | static | grounded on it → velocity override, once per landing | yellow arrows |
| `TURN_TABLE` | top centre mm, radius, thickness, period, phase16 | `angle = φ + t/T` | point-aware rotational carry; the rider's table-frame offset snaps to a 2 mm grid, so an idle rider returns to the same mm after a turn; facing is not rotated | spokes |
| `GATE_DOOR` | closed centre/half mm, lift mm, closed/opening/open/closing ticks, offset, telegraph, phase16, `Mode` (0 cycle, 1 start gate) | piecewise integer lift | posed collider (solid whenever in the way; passable once lifted above head height); a closing door pushes a player inside out along its normal to the side they are on | body flashes magenta for `TelegraphTicks` (15) before and while closing |
| `MALLET_SWING` | pivot mm, swing axis, arm, head radius/half-width, amplitude phase16, period, phase16, telegraph, knock fields | `angle = A·sin(2π·t/T + φ)` (integer) | knock_hard: push 9 m/s along the head's travel + 5 m/s up, stun 18 (0.6 s), immunity 33 | head glows during the 15 ticks before each pass through the bottom |
| `BUMPER` | base mm, radius, height, contact margin 40 mm, knock fields | static (solid collider) | radial knock_soft | stripe bands |

- **Immunity:** 0.5 s (15 ticks) after a stun ends; no knock while immune. Tested with a crossfire of two spinners and a mallet: never fewer than 15 control ticks between hits.
- **Dive under the high bar:** a diving / dive-landed Rushling presents a 0.5 m hit height to the analytic knock hazards (its 80° dive pose); the motor capsule stays upright.
- **Seeded phase:** `φ = PhaseFromSeed(FNV-1a32(seed_le8 ‖ obstacleId))` mapped into `[phaseMin16, phaseMax16]`. TODO(M6): `H(vrf_seed ‖ round ‖ obstacleId)`.
- **Allowlists** (`HazardAllowlist.cs`, checked on resolved defs incl. the modifier; out of range → round refused): spinner |T| 60–600, tip ≤ 12 m/s; platform ≤ 200 mm/tick; conveyor ≤ 5 m/s; spring vy 3–20 m/s, |vxz| ≤ 12; turn table rim ≤ 4 m/s; door cycle 60–900, open ≥ 15, telegraph 9–90, lift ≥ 1.3 m, closing ≤ 200 mm/tick; mallet amplitude 10–75°, |T| 60–600; knock ≤ 14 m/s, stun ≤ 24, immunity ≥ stun + 15.
- **Variants:** each component holds def A and an optional def B, resolved from the same scene. **Modifiers:** `modifiers_v1.json`; TurboToys periods `round_half_up(P·1000/1200)` whole ticks, belts ×1.2, telegraphs unchanged.

### 2.2 M4 kit types (as built, 2026-11-19; `Shared/Hazards/M4Defs.cs`, `Shared/Reactive.cs`)

| Type | Def fields | Pose / state | Effect | Telegraph (client) |
|---|---|---|---|---|
| `GOO_PLANE` | y0 mm, rise mm/s, delay ticks, max y, visual extent, warn ticks | pure `y = y0 + ⌊v·max(0, t − delay)/30⌋` | feet below the surface → out (`PlayerStep.Result.Gooed`, frozen like a fall; predicted) | purple glitter plane with specks; "Goo rising!" subtitle from 3 s before it starts |
| `CRUMBLE_TILE` (one grid component) | origin, tile 1.6 m, gap 4 cm, 9 × 9, shelf tops, thickness, wobble 18 ticks; spinner start per variant | **reactive**: contact tick per tile (server ground contact) | collider off at contact + 18 on both sides | the tile shakes and turns yellow for 0.6 s, then falls away; "Tiles crumbling!" |
| Falling block | box mm, delay 18, gone 120 | **reactive**: trigger ticks | collider off for `[trigger + 18, + 138)` | shake, drop, come back |
| `STAR_PICKUP` (one field component) | anchors (static / platform / turn table), golden anchors, wave every / size / life, golden every / life / value, pickup reach, drop pop / life / distance | **pure** timeline (seeded anchors per wave), **reactive** collected / dropped | server-validated pickup (server capsule + 0.4 m); a knockback drops one | spinning stars, gold = bigger; dropped stars hop |

Allowlists unchanged (the new kit has no tunable speed beyond the goo, checked by the QA). The map content hash covers the new defs in an `m4` block that is only written when a map has them, so the M3 maps' hashes did not change (Block Party Dash `ffc099ad…`, Sandbox `2ae7d388…`). New pure functions are in `hazard_vectors_v2.json` (v1 unchanged).

## 3. Map 1A — **Block Party Dash** (Race)

**Layout:** a sunny toy-block sky course.

| Section | Content | Variant axis (VRF) |
|---|---|---|
| Start deck | Wide 12-slot start plaza (staggered spawns), gate drops at "RUSH!" | — |
| S1 Block Hop | Three rows of floating blocks; the middle row bobs (`MOVING_PLATFORM`, small amplitude) | A: bob vertical / B: bob lateral |
| CP1 | — | — |
| S2 Spinner Alley | Wide bridge with 3 `SPINNER_BAR`s at staggered heights (one low to jump, one high to dive under) | Direction pattern A/B |
| S3 Conveyor Climb | Two lanes: a conveyor ramp against you (safe, slower) or `SPRING_PAD` hops (fast, risky) | — |
| CP2 | — | — |
| S4 Door Dash-free gate wall | Five `GATE_DOOR`s cycling open in a sweep pattern. Not "guess the fake door": all doors are real and timed. | Sweep order A/B |
| CP3 | — | — |
| S5 Turntable Islands | Three `TURN_TABLE` discs over the void linked by short jumps (risky, fast), or a narrow walkway on either side (safe) | Rotation directions A/B |
| CP4 | — | — |
| S6 Mallet Bridge | A railed bridge under four foam-roller `MALLET_SWING`s swinging along it: run between them; a hit bounces you back. Narrow edge beams outside their reach (safe, slow) | Swing period set A/B |
| CP5 | — | — |
| S7 Finish ramp | Broad ramp, `BUMPER`s scattered, checkered finish arch | — |

- **Length:** about 250 m (lengthened 2026-11-19 from ~160 m: the reference bots finished the short course in ~31 s). Reference-bot median ≈ 47 s, best ≈ 41 s; target for humans 75–110 s (UNVERIFIED until a playtest).
- **Fall volume:** below y = −10, respawn at the last checkpoint.

### 3.1 As built (M3, `Editor/BlockPartyDashBuilder.cs`)

Course line ≈ 255 m (checkpoints at 47.0 / 115.3 / 132.3 / 175.8 / 226.3 m, finish 255.4 m along it). Kill-Y −10 m.
- **Start deck** z −12…8, 12 staggered spawns (3 rows × 4, 2.5 m apart), start gate (`GATE_DOOR` mode 1) opens at RUSH over 10 ticks.
- **S1 Block Hop** z 8…37: left lane 6 static 3 m blocks (1.5 m gaps), middle lane 6 bobbing blocks (variant A vertical ±0.6 m, B lateral ±1.2 m, period 120, seeded phase), right lane 5 static 2.4 m blocks (3.6 m gaps). **CP1** z 39.
- **S2 Spinner Alley** z 45…85: 10 m bridge with 3 spinner bars (arm 5 m, period 171 = ω 1.1 rad/s; low 0.35 m jump, high 0.95 m dive under, mid 0.5 m jump; variant A directions + − +, B − + −) and two 1 m edge beams outside the bars' reach (safe, slow, narrow).
- **S3 Conveyor Climb** z 85…105, +3 m: left a 10.6° conveyor ramp running down at **3.5 m/s** (tuned from 2.5); right two spring pads over void (pad 1 (0, 11, 7.5) m/s onto a block at +1.5 m, pad 2 (0, 11, **9**) m/s onto the deck). **CP2** z 107.
- **S4 Gate wall** z 117: five 3 m doors between 2 m pillars; cycle **210** ticks (closed 141, opening 6, open 45, closing 18), offsets 42 apart (variant A left→right, B reversed) → always ≥ 1 open. **CP3** z 124.
- **S5 Turntable Islands** z 135…163 (y 3, added 2026-11-19): three turn tables (r 4 m, centres z 139 / 149 / 159, 2 m gaps; periods A 360 / −300 / 360 ticks, B reversed) or a 1.5 m walkway at |x| 9…10.5. **CP4** z 167.5.
- **S6 Mallet Bridge** z 175…216 (added 2026-11-19): a 6 m bridge with 1.2 m side rails under four mallets 10 m apart (z 181 / 191 / 201 / 211; pivot 8.5 m, arm 4.9 m, amplitude 70°, swinging **along** the bridge, roller head half-width 2.5 m; periods A 120 / 112 / 128 / 116, B permuted; knock_hard). Edge beams at |x| 5…6 are outside the head. Overhead gantries, posts at |x| 6.6. Tuned in QA: mallets swinging *across* the bridge threw ~20 racers per race off it (no rails) or pinned them against the rails (413 hits in one race). **CP5** z 218.
- **S7 Finish ramp** z 224…244 (+2 m), 7 bumpers (r 0.7 m), checkered arch, **finish** z 247.
- Two or more routes per section; bottlenecks ≥ 10 m wide except the optional beams. Greybox palette per doc 17 §3.1 (hazards striped yellow/magenta, goals mint/checkered). Map content hash `ffc099ad…` (was `6780ef6c…` before the 2026-11-19 lengthening).
- **Timer cap 150 s** (raised from 110 s with the longer course; reference bots finish in ~47 s, humans slower).
- **Not done:** formal colourblind simulation and a tris/draw-call count (doc 17 §3.2); the originality check needs two people (yours).

## 4. Map 1B — **Bonk Factory** (Race)

**Layout:** an indoor toy factory with assembly lines.

| Section | Content | Variant |
|---|---|---|
| S1 Assembly Line | Three parallel conveyors at different speeds; side `PISTON_PUSHER`s | Piston phase set A/B |
| CP1 | — | — |
| S2 Mallet Row | Four `MALLET_SWING`s across a narrow-ish bridge (4 m wide), with clear wind-up | Swing period set A/B |
| S3 Turntable Hub | Three `TURN_TABLE`s linked by short gaps | Rotation directions |
| CP2 | — | — |
| S4 Capsule Chute | A marble-run slide with bumpers (fun and fast; low skill floor) | — |
| CP3 | — | — |
| S5 Packing Dock | Final sprint across moving crates (`MOVING_PLATFORM`) to the finish | — |

## 5. Map 2 — **Goo Tower** (Survival / climb)

- **Layout:** a vertical capsule-machine tower, 40 m tall. Purple glitter goo rises from the base: `GOO_PLANE`, delay 6 s, speed tuned so an average climber has a 6–8 s margin.
- **Climb sections:**
  1. stair blocks
  2. moving lifts
  3. conveyor ramps spiralling up
  4. bumpers on narrow ledges
  5. the summit pad
- **Falling blocks (occupancy-triggered):** the server owns them and replicates them with a sequence number (§1).
- **Constraint:** the goo must never overtake the *slowest valid route* at the reference speed. Verify with automated bot runs (§9).
- **Variants:** lift phase set; one optional shortcut opens (A) or closes (B).

### 5.1 As built (M4, `Editor/GooTowerBuilder.cs`)

A square spiral ledge (6 m wide, two 3 m lanes) round a 24 × 24 m capsule-machine core: 3 laps × 4 sides, each side a 24 m climb of +3.33 m plus a 6 × 6 m corner landing (path ≈ 360 m, 40 m tall). Lap 1, sides 0–1 **stair blocks** (inner: 16 small steps; outer: three 8 m blocks, +1.11 m jumps; side 1's are **falling blocks**); sides 2–3 **moving lifts** (a 3.3 m wall mid-side: outer lane a 4.4 × 2.9 m lift, period A 150 / B 120 ticks; inner lane a 29° belt ramp at 1.5 m/s or steps). Lap 2 **conveyor ramps**: inner lane belts against you (3 m/s pitched belts on the Z sides, 2.5 m/s belted treads with 0.83 m risers on the X sides — a CONVEYOR only pitches about X), outer lane a faster static climb with a 1.4–1.6 m gap (a miss drops you a lap); railed. Lap 3 **bumpers on narrow ledges**: a 4.5 m stair ledge with three bumpers and an open edge behind a 0.7 m lip; side 10's 1.5 m safe lane is the **shortcut behind a gate** (variant A open from RUSH, B open 15 of every 639 ticks). **Summit pad** (5.6 m, mint + checker) on the last corner at 40 m. Ground ring at y 0 catches falls from lap 1; spawn deck west of the start corner. Goo: y0 −1.5 m, delay 6 s, **0.30 m/s** (tuned in QA), summit reached at ≈ 144 s; timer 150 s. Map hash and QA: §9.2.

## 6. Map 3 — **Star Scramble** (Skill)

- **Layout:** a circular arena (Ø 36 m) with:
  - a central `TURN_TABLE`
  - four outer `MOVING_PLATFORM` islands
  - `BUMPER`s
  - two `SPRING_PAD`s to the islands
- **Stars:** spawn from a seeded timeline (`hazard_seed`): normal stars in waves of 8, and a golden star every 15 s on an island.
- **Knockback:** being hit by a `BUMPER` sweep or a dive drops one star.
- **Duration:** 60 s.
- **Variants:** island path shapes A/B.

### 6.1 As built (M4, `Editor/StarScrambleBuilder.cs`)

Ø 36 m arena (floor top y 0), central turn table r 5 m (20 s per turn; B the other way), four 4 × 4 m islands at r 22 m (top 1.0 m, just beyond the rim; A slide ±3 m along the rim in 12 s, B ±1.5 m in / out in 10 s), two spring pads at r 15.5 m toward islands 1 and 3 (launch 6.5 m/s out, 10 m/s up), six bumpers (r 8 / 13 m). Stars: 24 floor anchors (rings r 7 / 12 / 16), 4 on the turn table, 4 on the islands (golden only); waves of 8 every 8 s (life 16 s), golden +3 every 15 s (life 10 s). Timer 60 s. Spawns: a ring r 10 m.

## 7. Map 4 — **Crumble Shelf** (Final)

- **Layout:** three stacked shelves (y = 12, 6, 0) of toy-block tiles, 1.6 m hexagonal-free *square* tiles in a 9×9 grid per shelf, with rounded corners and alternating colors.
- **Crumble rule:** first contact starts a 0.6 s wobble, then the tile drops. The **server** owns tile state and broadcasts `TILE_DROP {tileId, seq, tick}`.
- **Pressure:** from 60 s, **spinner bars** sweep the top shelf to break stalemates.
- **Elimination:** falling below the lowest shelf (y < −6).
- **Variant:** spinner timing.
- **Originality note:** square block shelves with spinners and the claw-machine overhead are distinct from hex-tile designs.

### 7.1 As built (M4, `Editor/CrumbleShelfBuilder.cs`)

Shelves at tile tops y 12 / 6 / 0, 9 × 9 tiles of 1.6 m (243 box colliders, 1.56 m wide with 4 cm gaps, 0.4 m thick; visuals: one chamfered-corner mesh drawn instanced, pink / mint / yellow alternating with white per shelf). Two foam spinner bars hang from the claw over the top shelf (arm 6.6 m; low 0.35 m to jump: A 330 / B 270 ticks per turn; high 0.95 m to dive under: A −400 / B −300), raised until 60 s. Kill-Y −6 m. Timer 120 s. Open claw-machine frame (visual only). 12 spawns on top-shelf tiles.

### 7.2 `Map_Lobby` (M4-11, `Editor/LobbyBuilder.cs`)

A 28 × 28 m toy plaza with 1.2 m walls, a capsule-machine plinth, four bumpers, a spring pad onto a lookout block, 12 spawns on a ring; no eliminations (a fall respawns after 1 s). Map hash `77dcefe7…`.

## 8. Map authoring checklist (every map)

- [ ] `MapDefinition` SO: `mapId`, `version`, `mode`, `minPlayers`, `maxPlayers`, `targetDuration`, `checkpointCount`, `variants`, `allowedModifiers`
- [ ] 12 (later 20) spawn points, non-overlapping, safe
- [ ] Ordered checkpoints (race), each with a respawn transform
- [ ] Fall volumes, map bounds, spectator camera rails
- [ ] Every hazard has a `HazardDefinition` and passes the parameter allowlist
- [ ] Only gameplay colliders on the `Gameplay` layer; visual meshes on `VisualOnly` (no colliders)
- [ ] Originality check: two people answer "does this look like a specific existing level?" No → pass
- [ ] Readability check: hazard stripes, telegraphs, colorblind sim
- [ ] Performance check: tris, draw calls and FPS within doc 17 §3.2 budgets

## 9. Automated map QA

- **Bot runner:** a headless input-bot follows waypoint paths at reference speed.
  - Run each map × each variant × each allowed modifier **100 times** on the server build.
  - Assert: no soft-lock, a finish is reachable, goo never overtakes the reference route, there is no tick where every route is blocked, and the median duration is within target.
- **Determinism test:** for 1,000 random `(hazard_seed, tick)` pairs, the C# hazard pose equals the precomputed vectors (EditMode test). This also guards against float drift between macOS and Linux builds.

### 9.1 As built (M3)

`FallRushDevServer -batchmode -nographics -fallrushServer -fallrushMap BlockPartyDash -fallrushQA 100 -fallrushQAOut map-qa.md` (`Net/MapQa.cs`): variants A/B × allowed modifiers × 100 runs, 12 waypoint bots at reference speed (no noise), random spawn order and route choice per run seed, real PhysX world, no networking. 1,000 races take ≈ 70 s on the M2 Air.
**Result (2026-11-19, `docs/proof/m3-map-qa.md`):** 0 soft-locks, every route reaches its next checkpoint, 0 ticks with every door closed, exactly N qualified 1000/1000, deterministic per run seed. **Median duration 31–34 s: the 75–95 s target is NOT MET** by noise-free bots; human times are UNVERIFIED (see the M3 proof for the tuning log).

### 9.2 As built (M4)

`Net/ModeQa.cs` (`scripts/m4-qa.sh 100`): every map × variant × allowed modifier × 100 runs with `BotBrain` bots (reference tier and normal tier; Survival / Skill / Final with the 8 / 5 / 3 players those rounds have), real PhysX world, no networking. **Result (2026-11-19, `docs/proof/m4-map-qa.md`): all PASS** — valid result every run, 0 soft-locks, qualified ≤ target, one champion in every final, skill scores = pickups − drops, deterministic. Goo constraint: a reference bot alone on each route index escapes with a goo margin of **9.1–9.9 s**. Medians (normal bots): Block Party Dash 97.5 s (target 75–110 ✓), Goo Tower 66 s (90–120 ✗), Star Scramble 60 s (60–75 ✓), Crumble Shelf 27 s (60–120 ✗); human times UNVERIFIED. Map hashes: Lobby `77dcefe7…`, Goo Tower `98a3350d…`, Star Scramble `7d6db43f…`, Crumble Shelf `cb6ad3bd…` (`Tests/Vectors/map_hashes_v1.txt`). Note: static geometry is not part of the content hash (hazards, spawns, checkpoints, kill-Y are), as in M3.

