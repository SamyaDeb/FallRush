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
