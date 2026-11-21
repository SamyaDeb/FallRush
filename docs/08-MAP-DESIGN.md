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
4. **Route design (G4, 2026-11-19; replaces "two routes minimum: safe-slow and risky-fast"; source: the approved G4 plan page).** Rules 4.1–4.7 hold on every map:
   1. **No free route.** Every path has a hazard. A safer path costs time, checked per route with the QA bots (§8 route cost, §9 `-fallrushQaRoutes`).
   2. **Share the space.** One main route per section. A shortcut is short, visible and genuinely risky. No parallel lanes that split the pack.
   3. **Breathe, then squeeze.** Open area, chokepoint, open area. Chokepoints are 7–10 m wide, so the crowd piles up and bumps.
   4. **Hazards push you into people.** Knockbacks send you sideways into the crowd or back down a slope, not straight off the map.
   5. **Read it without words.** No in-world signs. Pink means it hits you, yellow means it moves soon, mint means safe or goal, blue means it moves you.
   6. **See the goal.** The finish arch or summit is visible from the start, and the course rises toward it.
   7. **Escalate.** Each section is harder than the last. The final third has the tightest timing.
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

### 2.3 G4 kit types (as built, 2026-11-19; `Shared/Hazards/RollingBallDef.cs`, `Shared/Hazards/LidGridDef.cs`)

| Type | Def fields (units) | Pose / state | Collision / effect | Telegraph (client) |
|---|---|---|---|---|
| `ROLLING_BALL` (type 13) | start / end centre mm, radius 600–1200 mm, speed 2–7 m/s, release period 30–300 ticks, phase16 (seeded), telegraph ticks (15), knock fields (`knock_hard`); `LenMm` derived at load (integer √) | ball k released at `t_k = k·P − ⌊phase16·P / 2¹⁶⌋`; position = integer lerp start → end by `(t − t_k)·speed / (30·len)`; despawns after `⌈len·30 / speed⌉` ticks; `⌈travel / P⌉` balls alive at most, only those evaluated | **no collider** (a knock volume, never a surface). Sphere vs the player capsule in integer mm, newest ball first; push = roll direction + ½ × the lateral offset (normalised, float only there): 9 m/s + 5 m/s up, stun 18, immunity 33 (the mallet's). In `ApplyKnocks` after spinners and mallets, before bumpers | chute light rises over the 15 ticks before each release (`RollingBallTelegraph1000`); balls spin by the distance rolled |
| `LID_FLIP` (type 14, one grid component) | grid centre x/z mm, top mm, N (odd 5–11), lid 1.2–2.2 m, gap, thickness, lit 30 / flipped 45 ticks, first wave tick, wave intervals per stage (90 / 75 / 60 / 45), waves per stage (8), flip share per stage (‰ of the alive lids: 200 / 250 / 300 / 350), ring retire ticks (750 / 1500 / 2250, outermost first), retire warn (90), min inner solid (5 of the inner 3 × 3), loop ticks (0; the sandbox loops); per-obstacle seed at resolve | **pure** `LidState(def, seed, lid, t)` → Idle / Lit / Flipped / Retired / RetireWarn. Wave starts are a closed-form sum over the stages; wave w lights the first ⌊alive·‰⌋ lids of a SplitMix64 partial Fisher–Yates of the lids alive for the whole wave window (so a lid never lights while its ring is warned or retired); **fairness** in the function: each wave keeps at most ⌊(9 − min) / ⌈flipped / min interval⌉⌋ inner lids (later inner picks dropped), so ≥ 5 inner lids are solid at every tick. All lids at a tick in O(waves · N²) with a scratch buffer of N² (no allocation) | each lid's box collider is on in Idle / Lit / RetireWarn and off in Flipped / Retired, **posed from the tick** in the motor world (server, prediction, rewind alike; no replication, no new messages) | lit = yellow pulse; flipped = 180° flip over 6 ticks (and back); retire warn = blinking; retired = drops away |

- **Seeds:** the ball's phase is the usual `ObstaclePhase.Phase16(seed, obstacleId)`; the lid grid takes `ObstaclePhase.Hash(seed, obstacleId)` (FNV-1a32) as its seed, mixed per wave with SplitMix64. In M6 that per-obstacle seed comes from `H(vrf_seed ‖ round ‖ obstacleId)`: the Final's floor pattern is then derived from MagicBlock VRF with no change in the grid code.
- **Allowlists:** ball radius 0.6–1.2 m, speed 2–7 m/s, period 30–300, telegraph 9–90, path 2–250 m, spacing (speed × period) ≥ one diameter, the path must not start inside a spawn volume (0.5 m margin), knock rules; lid grid N odd 5–11, lid 1.2–2.2 m, lit 15–90, flipped 15–150, intervals 30–300, ≤ 500 ‰ per wave, rings increasing and never the inner 3 × 3, min inner solid 5–9, loop ≥ last retire + 300.
- **Hash:** a `g4` block in the map content hash, written only when a map has G4 defs (so every existing map keeps its hash; tested). New pure functions are in `hazard_vectors_v3.json` (v1 / v2 unchanged).
- **Fallback:** `CRUMBLE_TILE` / `ReactiveSet` stay in the code untouched (G4 prompt §2.4 cut order).
- **Sandbox:** `Map_Sandbox` has a G4 section east of the fall-off edge (bridge at z −20): one ball lane and a 7 × 7 lid grid that loops every 90 s in free play.

## 3. Map 1A — **Capsule Run** (Race, key `BlockPartyDash`) — G4.2 rebuild, 2026-11-20

> **The Block Party Dash text below (§3 table, §3.1) is replaced by this section** (kept for history). Proof: `docs/proof/G4.2-capsule-run.md`. Builder: `Editor/BlockPartyDashBuilder.cs`. Map id 0, key, mode, 2 variants and modifiers 0/1/2/3/5 unchanged; content hash `50125d89…`.

| Beat | z (m) | Build | Variant A / B |
|---|---|---|---|
| Start funnel | 0–14 | 20 m deck, 12 spawns, start gate z 10, angled rails to 12 m | — |
| S1 Gumball Slope | 14–50 (+5 m, 7.9°) | 12 m ramp, 0.6 m rails, 5 ROLLING_BALL lanes (x 0, ±2.4, ±4.8; r 1 m) rolling down; release hatches light at the top | periods A 112/126/104/134/118, B 122/108/130/114/138 ticks; speeds 3.6–4.4 m/s |
| CP1 | 50–54 | 12 m | — |
| Wave Gates (choke 10 m) | 54–62 | five 1.8 m grille GATE_DOORs, cycle 240 / open 54 / offsets 48 (≥ 1 open always); cross ROLLING_BALL at z 55.5 | wave L→R / R→L; cross ball 4.0 @ 60 / 4.6 m/s @ 54 ticks |
| S2 Hop Field (void) | 62–120 | centre 15 bobbing blocks (3 × 2.2 m, gaps 1.56 m); outer 13 static blocks at x ±4.6 (gaps 2.2 m, rises ≤ 0.3 m); gantry z 91, one MALLET per column swinging across | bob vertical ±0.25 / lateral ±0.8 m; mallets A 122/120/124, B 128/122/124 |
| CP2 | 120–124 | 12 m | — |
| S3 Sweep Pad | 124–148 | disc r 9.3 m, hub r 0.6 m, two SWEEPERs (arm 9 m): low 0.35 m, high 0.95 m, opposite directions | A low +192 / high −216 ticks, B reversed |
| CP3 | 148–152 | 12 m | — |
| Mallet Run (choke 7 m) | 152–182 | 7 m bridge, 0.4 m rails, three MALLETs along it (z 158/167/176, head 5 m) | periods A 180/190/200, B 190/200/180 |
| CP4 | 182–186 | 15.6 m | — |
| S4 Belt Climb | 186–216 (+6 m) | 10 m CONVEYOR 0.7 m/s against you, 3 BUMPERs r 1.7 m (x −3.3/0/3.3); edge walls; side ledges x ±5…7.8 with a ball lane each and a SPRING_PAD to the finish | ledge ball 5.2 @ 232 / 5.6 m/s @ 222 ticks |
| Finish | 216–229 | mint deck, backboard, tall arch z 222 | — |

- **QA (measured, 100 runs/cell):** route cost PASS (36 free routes → **0**; every section ≤ 1.18×); race QA PASS (8 of 12 every run, 0 soft-locks, deterministic); normal-bot median 86.7 s overall, **per cell 77–109 s (three variant-A cells under 85 s; accepted, proof §4.2)**; reference bots ≤ 3.6 falls per race.
- **Tuning table:** proof §4. **Route-cost table:** `docs/proof/g4/route-cost-after.md`.
- **Readability:** no in-world text (tested); hazards striped magenta/yellow, finish mint/checker, belt chevrons and bobbing blocks move; the finish arch is at the top edge of the spawn framing.
- **Originality check (self):** genre vocabulary in our own order and dimensions, capsule theme, no traced layout (proof §11). Two-person check: yours.
- **Draw calls (Editor):** 91 at the spawn view without Rushlings (≈ 115 with 12) ≤ 150.

## 3.0 Map 1A — Block Party Dash (Race) — **replaced by Capsule Run (above)**

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

**G1 retune (movement v2: 5 m/s, 1.2 m hop, ≈ 3.2 m running reach):**
- **S1 Block Hop rebuilt** (user note: gaps too small, every block at the same low height). Pillar blocks from y −1.5 to their own top.
  - *Safe lane (left)*: six 3.0–3.2 m wide, 2.6–2.9 m deep blocks, gaps 1.6–1.9 m (≈ 50–60 % of reach), tops 0.3 / 0.6 / 0.3 / 0.6 / 0.9 / 0.45 m.
  - *Fast, risky lane (right)*: seven 1.8–2.2 m blocks, gaps 2.0–2.1 m (≈ 65–70 %), tops 0.3 / 0.7 / 1.1 / **1.4 (stair)** / 0.9 / 0.5 / 0.8 m. A first cut with 2.4–2.6 m gaps and 0.5–0.6 m rises made even reference bots fall 27–71 times per race (and the user found the jumps failing): narrowed.
  - *Middle*: six bobbing platforms 3 × 2.4 m, gaps ≈ 2.1 m at rest, variant A vertical ±0.4 m (was ±0.6), B lateral ±1.2 m, period 4 s.
- S3: conveyor ramp against you 3.5 → **2.0 m/s** (net 3 m/s up at v2 speed). Spring pads unchanged (the v2 gravity lands pad 1 at z ≈ 94.3 on the 91.5–96 block, pad 2 at z ≈ 103 on the deck).
- S2 bars (0.35 / 0.95 / 0.5 m), S4 doors (open 1.5 s, 3 m wide), S5 turn tables (2 m gaps ≈ 63 % of reach), S6 mallets, S7 bumpers: unchanged; QA below.
- Developer labels (section names, "MID: JUMP", "CONVEYOR RAMP (…)") are `EditorOnly` (stripped from builds); the title, CHECKPOINT n and FINISH stay as signs.


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

**G1 retune:** lap 1 big blocks 3 × 1.11 m → **4 × 0.83 m** (1.11 m was at the edge of the 1.2 m hop); belted treads 2.5 → 1.5 m/s, Z-side belt ramps 3 → 2 m/s, lift ramp unchanged; the X-side outer "steps with a gap" removed **two** steps (the gap code drops every step it overlaps; centred on a step boundary it made a 3 m gap + 0.62 m rise, beyond v2): now one 1.5 m step; the bot jump points moved to the edges. Goo 0.3 m/s unchanged: every route index escapes alone with **8.3–9.0 s** margin; bot rounds 85–89 s (M4 66 s).


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

**G1 retune:** island tops 1.0 → **0.8 m** (2 m gap up to +0.8 m at v2), spring pads 6.5 → **5.8 m/s** outward (lands ≈ 0.5 m onto the island under v2 gravity).


Ø 36 m arena (floor top y 0), central turn table r 5 m (20 s per turn; B the other way), four 4 × 4 m islands at r 22 m (top 1.0 m, just beyond the rim; A slide ±3 m along the rim in 12 s, B ±1.5 m in / out in 10 s), two spring pads at r 15.5 m toward islands 1 and 3 (launch 6.5 m/s out, 10 m/s up), six bumpers (r 8 / 13 m). Stars: 24 floor anchors (rings r 7 / 12 / 16), 4 on the turn table, 4 on the islands (golden only); waves of 8 every 8 s (life 16 s), golden +3 every 15 s (life 10 s). Timer 60 s. Spawns: a ring r 10 m.

## 7. Map 4 — **Crumble Shelf** (Final)

- **Layout:** three stacked shelves (y = 12, 6, 0) of toy-block tiles, 1.6 m hexagonal-free *square* tiles in a 9×9 grid per shelf, with rounded corners and alternating colors.
- **Crumble rule:** first contact starts a 0.6 s wobble, then the tile drops. The **server** owns tile state and broadcasts `TILE_DROP {tileId, seq, tick}`.
- **Pressure:** from 60 s, **spinner bars** sweep the top shelf to break stalemates.
- **Elimination:** falling below the lowest shelf (y < −6).
- **Variant:** spinner timing.
- **Originality note:** square block shelves with spinners and the claw-machine overhead are distinct from hex-tile designs.

### 7.1 As built (M4, `Editor/CrumbleShelfBuilder.cs`)

**G1 retune:** wobble 18 → **30 ticks (1.0 s)**; **top-shelf tiles return 240 ticks (8 s) after they drop** (`TileGridDef.RespawnTicks = 240, RespawnShelves = 1`), blinking for the last 1.5 s; a new contact starts a new cycle (`ReactiveSet.TileContactAt`). Bot QA median **≈ 73 s** (M4: 27–32 s), inside 60–120 s. Dropping a shelf (6 m) now stumbles you on landing.


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
- [ ] **Route cost (G4):** per section, the slowest valid route takes **1.0–1.25×** the fastest (reference bots, median section time); every route has **at least one hazard contact** in QA (normal-tier bots; a route with none is a FREE ROUTE). Measured with `-fallrushQaRoutes` (§9).
- [ ] Originality check: two people answer "does this look like a specific existing level?" No → pass
- [ ] Readability check: hazard stripes, telegraphs, colorblind sim
- [ ] Performance check: tris, draw calls and FPS within doc 17 §3.2 budgets

### 8.1 G2/G3 results per map (2026-11-19, proof `docs/proof/G2G3-look-juice.md` §D–E)
| Map | Readability | Originality (self-check; the two-person check is yours) | Performance (WebGL High, 1440 × 900) |
|---|---|---|---|
| Lobby | tops light, spawn pads mint, bumpers striped; props out of reach; the capsule machine now has a collider | gacha machines, bunting, podium on islands: ours | 59 FPS, 46 / 52 draw calls, 41k tris |
| Block Party Dash | toy-block tops light, hazards striped (spinner bars, mallets, doors, bumpers, posts), checkpoint banners mint at 5 m, checkered finish; the title sign only during the intro | course layout ours (doc 08 §3); no FG obstacle silhouettes | 59 FPS, 69 / 122, 90k |
| Goo Tower | ledges light, core decals flat, falling blocks striped yellow/magenta, purple goo with a bright lip; "Goo rising!" subtitle | capsule-machine tower with windows / gears / globe: ours | 59 FPS, 58 / 99, 121k |
| Star Scramble | arena light, record turn table light, normal vs golden stars differ in size, colour, glow and halo | round toy arena with a record centre: ours | 59 FPS, 54 / 68, 48k |
| Crumble Shelf | square pastel tiles, wobble = shake + yellow, respawn blink, spinner bars striped | square tiles in a claw machine (not hexagons) | 59 FPS, 41 / 72, 59k |
Colour-blind: the stripe pattern rule (every hazard striped; stripe colours differ in luminance by > 0.3, tested); a simulated screenshot is UNVERIFIED.

## 9. Automated map QA

- **Bot runner:** a headless input-bot follows waypoint paths at reference speed.
  - Run each map × each variant × each allowed modifier **100 times** on the server build.
  - Assert: no soft-lock, a finish is reachable, goo never overtakes the reference route, there is no tick where every route is blocked, and the median duration is within target.
- **Determinism test:** for 1,000 random `(hazard_seed, tick)` pairs, the C# hazard pose equals the precomputed vectors (EditMode test). This also guards against float drift between macOS and Linux builds.

### 9.1 As built (M3)

`FallRushDevServer -batchmode -nographics -fallrushServer -fallrushMap BlockPartyDash -fallrushQA 100 -fallrushQAOut map-qa.md` (`Net/MapQa.cs`): variants A/B × allowed modifiers × 100 runs, 12 waypoint bots at reference speed (no noise), random spawn order and route choice per run seed, real PhysX world, no networking. 1,000 races take ≈ 70 s on the M2 Air.
**Result (2026-11-19, `docs/proof/m3-map-qa.md`):** 0 soft-locks, every route reaches its next checkpoint, 0 ticks with every door closed, exactly N qualified 1000/1000, deterministic per run seed. **Median duration 31–34 s: the 75–95 s target is NOT MET** by noise-free bots; human times are UNVERIFIED (see the M3 proof for the tuning log).

### 9.3 G1 (movement v2)
`scripts/g1-qa.sh [runs] [maps…]` (same harness as M4; writes `docs/proof/g1-map-qa.md`). Block Party Dash was also run with reference bots (M4 ran it with normal bots only). Results and durations: `docs/proof/G1-polish.md` §D. `ModeQa` gained `-fallrushQaTrace` (per-tick state of the survival route probe on a section) and logs the last alive state of a failed route probe.

### 9.4 G4 route cost (`-fallrushQaRoutes`)
`scripts/g4-route-qa.sh [runs=100] [map=BlockPartyDash] [out]` → `FallRushDevServer -batchmode -nographics -fallrushQA <runs> -fallrushQaRoutes -fallrushMap <key> -fallrushQAOut <md>` (`Net/RouteQa.cs`, race maps). Per variant, modifier None: **timing** with 12 reference-tier bots (median ticks from section entry to exit for each route index, ratio to the section's fastest route; target 1.0–1.25×) and **contacts** with 12 normal-tier bots (hazard hits / door push-outs / falls per 100 traversals; a route with no hazard contact is flagged **FREE ROUTE**; falls alone do not count). Output: `map · variant · section · route · median s · ratio` markdown. Baseline on today's Block Party Dash: `docs/proof/g4/route-cost-before.md` (36 free routes incl. the S2 / S5 / S6 edge beams at 1.00–1.08×).
Bump stagger measurement flag (DEV): `-fallrushBump 1` (server / QA) and `?bump=1` (client) force the movement_v3 bump flag; both sides must agree.

### 9.2 As built (M4)

`Net/ModeQa.cs` (`scripts/m4-qa.sh 100`): every map × variant × allowed modifier × 100 runs with `BotBrain` bots (reference tier and normal tier; Survival / Skill / Final with the 8 / 5 / 3 players those rounds have), real PhysX world, no networking. **Result (2026-11-19, `docs/proof/m4-map-qa.md`): all PASS** — valid result every run, 0 soft-locks, qualified ≤ target, one champion in every final, skill scores = pickups − drops, deterministic. Goo constraint: a reference bot alone on each route index escapes with a goo margin of **9.1–9.9 s**. Medians (normal bots): Block Party Dash 97.5 s (target 75–110 ✓), Goo Tower 66 s (90–120 ✗), Star Scramble 60 s (60–75 ✓), Crumble Shelf 27 s (60–120 ✗); human times UNVERIFIED. Map hashes: Lobby `77dcefe7…`, Goo Tower `98a3350d…`, Star Scramble `7d6db43f…`, Crumble Shelf `cb6ad3bd…` (`Tests/Vectors/map_hashes_v1.txt`). Note: static geometry is not part of the content hash (hazards, spawns, checkpoints, kill-Y are), as in M3.

