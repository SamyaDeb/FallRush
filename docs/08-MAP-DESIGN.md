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
| S5 Finish ramp | Broad ramp, `BUMPER`s scattered, checkered finish arch | — |

- **Length:** about 160 m; best line ≈ 45 s, average 75–95 s.
- **Fall volume:** below y = −10, respawn at the last checkpoint.

### 3.1 As built (M3, `Editor/BlockPartyDashBuilder.cs`)

Course line ≈ 166 m (checkpoints at 47.0 / 115.3 / 132.3 m, finish 166.4 m along it). Kill-Y −10 m.
- **Start deck** z −12…8, 12 staggered spawns (3 rows × 4, 2.5 m apart), start gate (`GATE_DOOR` mode 1) opens at RUSH over 10 ticks.
- **S1 Block Hop** z 8…37: left lane 6 static 3 m blocks (1.5 m gaps), middle lane 6 bobbing blocks (variant A vertical ±0.6 m, B lateral ±1.2 m, period 120, seeded phase), right lane 5 static 2.4 m blocks (3.6 m gaps). **CP1** z 39.
- **S2 Spinner Alley** z 45…85: 10 m bridge with 3 spinner bars (arm 5 m, period 171 = ω 1.1 rad/s; low 0.35 m jump, high 0.95 m dive under, mid 0.5 m jump; variant A directions + − +, B − + −) and two 1 m edge beams outside the bars' reach (safe, slow, narrow).
- **S3 Conveyor Climb** z 85…105, +3 m: left a 10.6° conveyor ramp running down at **3.5 m/s** (tuned from 2.5); right two spring pads over void (pad 1 (0, 11, 7.5) m/s onto a block at +1.5 m, pad 2 (0, 11, **9**) m/s onto the deck). **CP2** z 107.
- **S4 Gate wall** z 117: five 3 m doors between 2 m pillars; cycle **210** ticks (closed 141, opening 6, open 45, closing 18), offsets 42 apart (variant A left→right, B reversed) → always ≥ 1 open. **CP3** z 124.
- **S5 Finish ramp** z 135…155 (+2 m), 7 bumpers (r 0.7 m), checkered arch, **finish** z 158.
- Two or more routes per section; bottlenecks ≥ 10 m wide except the optional beams. Greybox palette per doc 17 §3.1 (hazards striped yellow/magenta, goals mint/checkered). Map content hash `6780ef6c…`.
- **Timer cap 110 s** (the upper end of the target; QA medians are far below it, see §9).
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

## 7. Map 4 — **Crumble Shelf** (Final)

- **Layout:** three stacked shelves (y = 12, 6, 0) of toy-block tiles, 1.6 m hexagonal-free *square* tiles in a 9×9 grid per shelf, with rounded corners and alternating colors.
- **Crumble rule:** first contact starts a 0.6 s wobble, then the tile drops. The **server** owns tile state and broadcasts `TILE_DROP {tileId, seq, tick}`.
- **Pressure:** from 60 s, **spinner bars** sweep the top shelf to break stalemates.
- **Elimination:** falling below the lowest shelf (y < −6).
- **Variant:** spinner timing.
- **Originality note:** square block shelves with spinners and the claw-machine overhead are distinct from hex-tile designs.

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
