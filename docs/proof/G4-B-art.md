# G4-B art: G4.8z → G4.12 (2026-11-21 →)

Render and client feel only. Colliders, hazard poses / timings, rules, spawns and map content are unchanged: **all six map
hashes reproduce on every rebuild** (`Map_BlockPartyDash 50125d89…`, `Map_CrumbleShelf b0684a98…`, `Map_GooTower
5d5a00be…`, `Map_Lobby 77dcefe7…`, `Map_Sandbox 0c29efad…`, `Map_StarScramble 0bde7d75…` = `Tests/Vectors/map_hashes_v1.txt`,
checked by `MapSceneTests`). No VPS, never pushed, no new packages. Prompt: `docs/prompts/G4.8-G4.12-PROMPT.md`.

## G4.8z — close G4.8

### Look pick
`docs/playtest/G4-notes.md` → "G4.8 soft look" has no pick yet → **soft stays the default, F7 stays live** (G4.8-10).

### Lid Flip in soft (colourblind)
The G4.8 proof flagged a **0.075** deutan pixel gap between a lit lid and a shaded Top2 (Soda blue) lid. Two findings:
1. **Measurement:** the old check read a 3 × 3 patch at each lid's centre, which sits on the thin dark capsule seam that crosses
   every lid. Re-read on the same G4.8 shot off the seam, calm tops are ≈ 0.86–0.91 luma vs the lit amber 0.60 (deutan ≥ 0.19).
   `PaletteShots.LidPixels` now samples each lid 0.4 m either side of its centre (the darker side counts; a saturated sample on a
   calm lid is a hazard in front of it and is skipped) and reports the median and the **10th-percentile** lid per batch (a lid
   in a cast shadow), the darkest single sample, and the old centre number.
2. **The real weak case: a calm lid in a cast shadow.** Calm tops are *lighter* than the lit amber, so a deeper shade would move
   them *toward* the amber, not away (the prompt's "slightly deeper" reads the wrong way round here). In the soft look a cast
   shadow dropped the sun term to 0.25: a pink lid in full shadow fell to within ≈ 0.01–0.05 deutan luma of the lit amber.
   **Fix (render only):** a per-material `_ShadowKeep` in the toon shader (default 0.25, unchanged elsewhere); Lid Flip's calm
   lid batches keep **0.85** of the sun in shadow in the soft look (`LidGridView.CalmShadowKeep`), so a calm lid in shadow is
   ≈ 6 % darker (still a visible lavender patch) and stays ≥ 0.12 above the amber. Lit and warning lids keep the normal shadow.

Shot pixels after the fix (`g48z-lid-flip-lit-measure-soft.png`, soft, spawn-height view inside the machine, first lit wave;
`g48z-report-soft.md`):

| calm batch | median Δluma vs lit (n / d / p) | 10th-percentile lid (n / d / p) | old centre-on-seam (n / d / p) |
|---|---|---|---|
| Top2 (Soda blue) | 0.289 / **0.220** / 0.329 | 0.280 / **0.211** / 0.320 | 0.188 / 0.119 / 0.223 |
| Top3 (Tangerine) | 0.288 / **0.231** / 0.308 | 0.253 / **0.196** / 0.273 | 0.235 / 0.178 / 0.253 |
| TopPink (Bubblegum) | 0.238 / **0.187** / 0.256 | 0.215 / **0.163** / 0.233 | 0.226 / 0.174 / 0.243 |
| LowEdge | 0.292 / **0.222** / 0.332 | 0.272 / **0.203** / 0.313 | 0.378 / 0.309 / 0.415 |
| Low | 0.279 / **0.210** / 0.320 | 0.279 / **0.210** / 0.320 | 0.363 / 0.294 / 0.399 |

**Worst calm-vs-lit deutan / protan gap (median and 10th-percentile lid): 0.163 → PASS (rule ≥ 0.12).** The single darkest
sample (TopPink, 0.048 deutan) lies on the claw cable's thin shadow line crossing a lid (looked at in the shot), not on a lid's
colour. Token rule (`LidFlip_LitVsSolid_LumaGap_NormalDeutanProtan_BothLooks`) unchanged and green in both looks; the shot check
needs the GPU (batch with graphics), so it lives in `PaletteShots` (PASS / FAIL line in the report), not in EditMode.

### Dizzy swirls instead of stun stars
The user asked "why one becomes stars when they got hit by those obstacles?" — on the race maps stars mean score (Star
Fountain pickups / drops, the golden star, the podium sparkle), so the gold stars read as "turned into stars".
- **Head:** `RushlingParts.StunSwirls()` — three small swirl spirals (ink, lavender, ink; a flat Archimedean spiral swept as a
  thin tube, 2 turns, ≈ 0.1 m) orbiting the head, same node, timing and spin as the stars (0.6 s on the hit, 0.5 s again on the
  get-up). No star shape, nothing gold (tested: `StunVisualTests`).
- **Impact:** `Fx.Bonk` (was `Fx.HitStars`) — a white "bonk" puff (4 soft dots that grow and fade in 0.3 s) + 5 tiny white
  sparks (stretched dots, 0.22 s). Reduced motion: 2 puffs, 3 sparks (was 3 stars; 7 at full motion). The bonk sound, the
  60 ms hit-stop and the camera shake are unchanged.
- Stars stay where they mean score only: the Star Fountain pickups / drops, the golden star, the podium sparkle (unchanged).

**Body visibility through knock → tumble → get-up** (`StunShots`, a real `RushlingVisuals` with limbs, face and lid; a
scripted knock with the hazard profile's push / lift / stun ticks — not a sim replay):
| knock | look | lowest point of the body above the floor, whole sequence | min scale | swirls on for every stunned tick |
|---|---|---|---|---|
| mallet (hard: 9 m/s push, 5 m/s lift, 18 ticks), knocked back down the Mallet Run | soft / candy | **−0.021 m** (rule > −0.06) | ≥ 0.88 | yes |
| sweeper (soft: 6 m/s, 3.5 m/s, 11 ticks), Sweep Pad | soft / candy | **−0.004 m** | ≥ 0.88 | yes |

Sheets (every second tick): `docs/proof/g4/stun-sequence-{mallet,spinner}-{soft,candy}.png`; per-tick table
`stun-sequence-report.md`. The body never hides, shrinks or sinks into the floor (the −0.02 m is the bottom shell touching
the floor as it lies on its side); the same rule is an EditMode test over both profiles × full / reduced motion
(`StunVisualTests`). Nothing to fix. (The bonk puff is a particle burst: not in the Editor sheets.)

### Browser measurements of builds #1 and #2
**Deferred again — UNVERIFIED.** The machine was not idle at any point of this session's G4.8z phase (load average 16–80 on
8 cores from other projects' test runs and another session's Unity batch), so FPS / 1 % low would measure that load. They move
to G4.12 (build #3), with the same steps (G4.8 proof §6).

### Worktree
`../FallRush-g48` and branch `g4.8-soft-look`: fully merged into main, only ignored / generated files in it → **removed** on the
user's OK ("if no issues and merged and not needed u can delete then").

## G4.9 — Blender prop kit
**Script:** `tools/blender/props.py` (Blender 5.3 headless, `blender -b --factory-startup -P tools/blender/props.py -- --out
client-unity/Assets/_Game/Art/Props`, ≈ 5 s). Everything is built from maths in the script (lathes, sphere-swept boxes, swept
tubes, extruded outlines); no imported mesh, texture or reference. Motif: capsule toys (capsule seams, coin slot, prize star,
wind-up key, claw).
- **Output:** 22 FBX (no materials / textures) + `props.json` (slots, per prop the tris, pivot, target box, bounds).
- **Colour:** per corner UV0 = (palette slot, soft role), UV1 = (shade: baked AO from 16 fixed hemisphere rays × a bottom-dark
  gradient 0.8 → 1). `Editor/PropKit.cs` resolves the slot to `PaletteTokens` in **both looks** and writes the G4.8-1 layout:
  COLOR = soft albedo × shade, uv3 = (candy albedo × shade, 2 + 8 × role); hazard parts flag uv2.y = 4 (view rim). The FBX
  vertex colour is only a soft preview. `PropImport` sets the importer (readable, no materials / animation / tangents, axis baked).
- **Deterministic:** the FBX writer's clock is pinned and its ids hashed with SHA-1 (not Python's per-run `hash()`): two runs
  → **byte-identical** files (`shasum` of all 22 FBX + props.json, both runs identical). Hashes below.
- **Winding:** every part is turned outward by its signed volume about its centroid (the first import showed lathe bands wound
  inward and culled behind the balls); all 22 props: 0 inward islands (checked by re-importing every FBX in Blender).

| prop | tris (≤ 1500, tested) | FBX sha1 (12) | used on |
|---|---|---|---|
| arch_post | 1420 | `164f97e86ee3` | Capsule Run finish arch posts (2) |
| arch_seg | 772 | `840cd66d28a6` | Capsule Run finish arch top (9) |
| bleacher_block | 1272 | `b5bba5a7a9d3` | not placed (the G4.8 stadium tiers carry the G4.10 crowd) |
| bunting | 168 | `d08e678efeaa` | lobby bunting (tiled) |
| capsule_machine | 1312 | `3f1417c7564e` | lobby capsule machines (lands with the G4.10 rebuild) |
| claw | 416 | `3509c0f1d2e4` | Lid Flip, hanging from the header (y 19) |
| coin_slot | 768 | `76acb772ba58` | Lid Flip cabinet front (lands with the G4.10 rebuild) |
| confetti_cannon | 924 | `2aa4092c676f` | Capsule Run finish-arch post tops (2) |
| goo_blob | 496 | `35cc974e3b65` | Goo Tower far islands (8) |
| gumball | 760 | `281feb5e93bf` | every rolling ball (Capsule Run 27, Goo Tower 4, sandbox 4) |
| inflatable_tower | 1104 | `97462ab8d43d` | Capsule Run, 24-30 m beside the course (6) |
| mallet_head | 1360 | `cd523de235f8` | all mallets (Capsule Run 6, Goo Tower 3, sandbox 1) |
| padded_beam | 1092 | `c206c0214836` | mallet frame beams (sandbox; the race maps have none) |
| pennant | 80 | `e2eb2c41b573` | lobby / checkpoint pennants |
| pillow_bumper | 1248 | `a360157eb38c` | all bumpers (Capsule Run belt 3, Goo Tower, lobby 4, sandbox 2) |
| prize_star | 80 | `77ae08f03626` | Star Fountain drum (12; score map only) |
| ribbon_banner | 620 | `9062fc00fb85` | Capsule Run checkpoint banners |
| spinner_cap | 308 | `ce74c4c21f75` | both ends of every spinner / sweeper bar |
| spinner_hub | 600 | `71c4d5ad0402` | every spinner / sweeper hub |
| spring_pad | 692 | `7119933a9d81` | every spring pad (render child of the collider box) |
| start_arch | 1134 | `9fabde35f8a4` | Capsule Run start gate (dressing) |
| windup_key | 624 | `eb55431735b3` | Goo Tower dome top |

**Wiring (render only, `Editor/PropSwap.cs`, before MapArt merges):** mallet heads, bumpers, spinner caps + hubs, gumballs,
spring pads, Capsule Run's finish arch (posts + top), mallet beams. A prop that replaces a collider box's look goes on a
render-only child with the box scale undone; the box keeps its collider and transform, its renderer goes. MapArt keeps a prop's
own colours (a part on the hazard pink / lemon material is no longer flattened to one stripe colour when it is a prop). Every
hazard keeps a lemon hit stripe (tested). Dressing props merge into the per-map `Props` mesh (no extra draw call).

**Collision = visuals (`PropTests`, EditMode, every built map):** bumpers and spring pads (render bounds vs collider bounds),
gumballs (prop size vs 2 r), spinners (reach vs arm), mallets (head bottom vs arm + head radius, half width vs 0.75 m),
finish-arch pieces (vs their boxes): all within **0.15 m**; every bumper / gumball / spinner / mallet mesh carries the lemon
stripe.

**Squash on hit (client-only):** a stun within a bumper's radius + 1.2 m squashes its pillow 12 % and wobbles it back over
0.4 s (`HazardViewDriver.BumperScale`; reduced motion: 4 %, no wobble; tested). Bumper visuals are their own renderers now
(+1 draw each when in view, inside the +10 budget). Mallet heads are merged with their handles and do **not** squash (not done).

**Draw calls, spawn view (Editor frustum count, G4.5 method; Rushlings and lid snapshots hidden):**

| map | G4.8 soft | G4.9 soft | G4.9 candy |
|---|---|---|---|
| Capsule Run | 90 | **78** | 74 |
| Goo Tower | 22 | **21** | 20 |
| Star Fountain | 35 | **35** | 34 |
| Lid Flip | 9 | **9** | 8 |
| Lobby | 11 | **14** | 10 |

Capsule Run drops 12: a gumball is one renderer now (the separate band object is gone). Lobby +3: its bumpers are their own
renderers (squash). All within +10.

**Size:** the six map scenes grew 4.2 MB of scene text (88.0 → 92.2 MB; G4.8 grew them 59.8 MB for +2.81 MB of build), so the
estimate is **≈ +0.2 MB** of build — UNVERIFIED until build #3. The FBX files (0.8 MB) are not in the build (only the resolved
meshes inside the scenes).

**Images:** after = `docs/proof/g4/props-<map>-{spawn,action,overhead}-{soft,candy}.png`; before = the G4.8 shots
`soft-<map>-<shot>-{soft,candy}.png`. Reports: `props-report-{soft,candy}.md`.

**Originality (doc 08 §8), per map:** Capsule Run, Goo Tower, Star Fountain, Lid Flip and the lobby get only props generated by
our script from our own shapes (foam mallet, quilted pillow, gumball, foam caps, capsule-toy arch, coin slot, prize star,
wind-up key, claw); no asset, mesh, texture or layout of another game was extracted, ripped or traced.


## G4.10 — backdrop + camera

### Backdrop (soft look only, on top of G4.8's stadium 260 m out)
- **Crowd:** ≈ 1150–1360 little two-tone capsule spectators (white dome over a pastel shell in the rim hues, 40 tris each) in
  rows on every bleacher tier, plus the hanging banners and tower flags, in **one merged mesh, one draw call** on the new
  `FallRush/Crowd` shader: spectators hop and sway, banners / flags wave, all from a per-vertex phase in uv1 — **0 CPU per
  frame** (`Editor/BackdropCrowd.cs`, `Shaders/Crowd.shader`). Deviation from the prompt's wording: one merged mesh instead of
  GPU instancing — same draw count and no per-frame work, and no instancing path to verify in WebGL. Crowd mesh 46–55k tris for
  the whole ring (only the arc in view is drawn on screen; visible-tris in WebGL: UNVERIFIED until G4.12).
- **Inflatable towers** (G4.8 backdrop + G4.9 kit towers beside Capsule Run), **floating islands** (G4.6 dressing, unchanged).
- **Cloud sea far below:** 70 flat cloud puffs on a disc 55 m under the lowest deck (≈ 45 m under any kill plane), in the
  backdrop mesh (no extra draw): never next to a kill volume, never at a height a player could read as ground.
- Also landed with this rebuild (G4.9 kit): the coin slot on Lid Flip's cabinet, the capsule-machine cabinets in the lobby.
- **Saturation budget (≤ 30 % of pixels with S > 0.6):** every soft spawn / action shot and every `close` camera shot is
  inside it (worst 19.3 %, Capsule Run spawn; `g410-satbudget.md`).

### Camera (`Client/CameraRig.cs`: all tunables in one place; client-only feel)
| preset | distance | elevation | look-at above the target | FOV |
|---|---|---|---|---|
| `classic` (G1 → G4.8) | 6.6 m | 22.5° | 0.4 m | 55° |
| **`close` (default)** | 6.5 m | 15° | 0.75 m | 55° |

Dev switch **`?cam=classic|close`** (WebGL) / `-fallrushCam` (desktop). Measured on Editor renders (`CameraShots`: a real
Rushling, the preset's orbit pose without damping / look-ahead; `cam-report.md`, shots `cam-<map>-{spawn,action}-{classic,close}-{soft,candy}.png`):

| check | classic | close |
|---|---|---|
| Rushling height (feet → dome top), every map, spawn + action | 13.1 % | **14.3 %** (rule 12–15 %) |
| Capsule Run finish arch top, spawn view (viewport y) | 0.93 (top edge) | **0.73** |
| Capsule Run first obstacle (start gate) | in view (y 0.70) | in view (y 0.59) |
| Star Fountain first obstacle (spring pad) | in view | in view |
| Goo Tower first obstacle | the spawn view faces the tower's stairs with the first mallet in view (shot); the automatic pick chose a bumper round the corner (out of view) in both | same |
| Goo Tower summit beacon from the spawn | ≈ 52° look-up | ≈ 42° look-up |
| Lid Flip top shelf from the spawn | far corners + the whole board ahead; the two near corners are beside / behind the spawn (2/4) | same |

Reading: the classic camera already met the 12–15 % rule (the prompt assumed it was farther), so `close` keeps the distance
and goes **lower and flatter**: the course ahead sits lower in the frame (finish arch in view) and the Rushling stays inside
the band. **Not met:** the Goo Tower summit is not reachable "by a slight look-up" from the spawn with either preset (the
summit is 22 m up, ≈ 25 m away: it needs ≈ 40°); the Lid Flip near corners are outside any 55° frame from a spawn on the edge
ring. Both are geometry, not camera tuning; left as they are (your call).
- **Nameplates:** already non-overlapping since G1 (`Nameplates`: fade with distance 10 → 32 m, a farther label overlapping a
  nearer one is hidden, own marker always on top); unchanged.
- **`docs/proof/g2g3/bolt-closeup.png`** re-shot (the old file showed a bumper): Bolt face on in the lobby (`CameraShots.BoltCloseup`).

### Draw calls, spawn view (Editor frustum count; `g410-report-{soft,candy}.md`)
| map | G4.8 soft | G4.9 soft | **G4.10 soft** | G4.10 candy |
|---|---|---|---|---|
| Capsule Run | 90 | 78 | **79** | 77 |
| Goo Tower | 22 | 21 | **22** | 20 |
| Star Fountain | 35 | 35 | **36** | 34 |
| Lid Flip | 9 | 9 | **10** | 8 |
| Lobby | 11 | 14 | **15** | 13 |
+1 per map in soft = the crowd mesh. Shadow-pass draws: not in the Editor count (WebGL, G4.12).

Tests: EditMode **391/391** (+ `CameraRigTests` ×3: presets, switch, orbit pose and the 12–15 % share for both presets);
map hashes unchanged on the rebuild; prediction twins at 0 corrections (the EditMode prediction tests, unchanged).

## G4.11 — rigged Bolt: **cut** (scope / risk, not time)
G4.8z–G4.10 were green well before the Mon 12:00 UTC gate, so time allowed it; it is cut on judgment (first in the cut order):
today's Bolt is already one smooth body mesh whose lid hinge and key spin run in the shader, with procedural noodle limbs. A
skinned copy would add CPU skinning for 12 Rushlings in WebGL and re-route the 12 cosmetic presets (toppers, emblems, keys),
right before the last build — for the jiggle and the ragdoll-ish flop. If you want those, they can be added later on the
procedural Bolt (client-only springs on the existing nodes) without a rig. `?bolt=` does not exist.

## G4.12 — art QA + art freeze

### 1. Guard (render-only proof), measured
| check | result |
|---|---|
| Mac dev server rebuilt from the final art code (`Build.MacDevServer`, 0 errors) | ✅ |
| `scripts/m4-qa.sh 20` on Goo Tower, Star Fountain, Lid Flip, Capsule Run vs the **same 20 runs on the G4.7-era server binary** (built 2026-11-20 18:16 UTC, before any G4-B change; run at the start of this session) | **identical cell for cell** on all four maps; the only differing line is "Wall time" (`.m1-logs/g4b/qa-baseline/` vs `qa-after/`); every cell QA PASS |
| `scripts/m4-soak.sh 20` | **20/20 complete, 20/20 logs valid**; match 5.6–7.6 min (median 6.3); server RSS max 200 MB |
| EditMode | **391/391** |
| prediction twins | 0 corrections (EditMode prediction tests) |
| map hashes | unchanged on every rebuild (`MapSceneTests`) |
| Lid Flip localhost prediction probe (`m3-gate.sh`, the `g45-probe.sh` localhost leg, 3 clients, 140 s) | **0 corrections, 0 hazard mismatches, 0 hazard corrections** on all 3 clients; pushes 0.25–0.41 / 5 s; fps min 55–57 (probe clients) |

Note: the prompt asked to compare with "G4.7's reports"; those are 100-run reports, so the like-for-like comparison is the
20-run baseline on the pre-G4-B binary (same seeds). The 150 ms netem leg was not run (localhost only, as the prompt asks).

### 2. WebGL release builds #3 and #4
- Built 2026-11-20 22:04–22:16 UTC on `3d8ee41` (`pnpm m1:build:webgl:release`, no Editor, no other Unity): **24.09 MB**
  (`reportedSize` 24 085 594 B; 0 errors, 15 warnings, 720 s). Budget ≤ 25 MB: **met, 0.91 MB headroom.**
- vs build #2 (21.79 MB): **+2.30 MB**. My G4.9 estimate (+0.2 MB, props alone) was far too low: most of the growth is G4.10's
  crowd mesh (46–55k tris per map) and the cloud sea, both baked into all six scenes. No rebuild for size was needed. If more
  content comes after the freeze: drop the crowd to one row per tier (≈ −50 % of its mesh) or pack uv3 / colour as
  Float16 / UNorm8.
- Saved copy: `client-unity/Builds/WebGL-g412-build3/` (gitignored). Builds #1 and #2 untouched.
- **Build #3 had a broken colour grade — and so did every player build since G4.6** (found by the first browser run of this
  phase): blown highlights and colours pushed to primaries (pink gumballs red, the sky cyan). Cause: both URP assets were saved
  with **LDR** grading while `GraphicsQuality` switches to **HDR** grading at runtime (Neutral tonemapping needs it); a player
  build strips the post-processing variants an asset does not use, so the grade ran on the wrong path. The Editor never strips,
  so every Editor proof shot (G4.6 → G4.10) looked right. Isolated on the Mac player (post on / off, High / Low, the runtime
  volume logged: exactly the soft grade), fixed by saving HDR grading in both assets (`0d020c2`), verified on the Mac player in
  soft and candy. Your G4.6 "too colourful and unnatural" verdict was very likely partly this bug.
- **WebGL release build #4** (one over the budget, **on your OK**): `0d020c2`, 2026-11-20 22:5x UTC, **24.09 MB**
  (`reportedSize` 24 094 898 B, 0 errors, 351 s), saved at `client-unity/Builds/WebGL-g412-build4/`. **This is the art-freeze
  build.** `client-unity/Builds/WebGL/` holds it too (what `pnpm m1:up:mac` serves).

### 3. Browser measurements
Measured on build #4 with `scripts/g47-webgl-measure.mjs` (it now clicks PLAY: the G3 menu blocked autoplay, so the G4.7 /
G4.8 steps would never have reached a match), one full 12-player bot match per run, Chrome, the page's 960 × 600 canvas (the
xNFT template; not the 1440 × 900 canvas of the method). **The machine was not idle** (load average 7–35 during the runs:
other projects), so every number here is **UNVERIFIED** against the budget; they are what this machine did under that load.

| run | map | FPS median (worst window) | 1 % low (median / worst window) | draw calls (median of windows / max) | tris max |
|---|---|---|---|---|---|
| soft High | Capsule Run | 59 | 53 / 53 | 114 / **216** | **455k** |
| | Goo Tower | 59 | 53 / 53 | 64 / 150 | **392k** |
| | Star Fountain | 59 | 53 / 53 | 76 / 102 | 246k |
| | Lid Flip | 59 | 53 / 53 | 60 / 73 | 201k |
| | lobby | 59 | 53 / 53 | 55 / 62 | 207k |
| soft Low | Capsule Run | 59 | 53 / 53 | 85 / **174** | **440k** |
| | Goo Tower | 59 | 53 / 50 | 58 / 131 | **397k** |
| | Star Fountain | 59 | 53 / 53 | 52 / 108 | 260k |
| | Lid Flip | 59 | 53 / 53 | 46 / 61 | 207k |
| candy High | Capsule Run | 59 | 53 / 53 | 92 / 153 | 214k |
| | Goo Tower | 59 | 53 / 53 | 54 / 94 | 151k |
| | Star Fountain | 59 | 53 / 53 | 49 / 74 | 78k |
| | Lid Flip | 59 | 53 / 53 | 41 / 52 | 52k |

Reading (with the load caveat): the frame rate holds 59 everywhere; the **1 % low sits at 53 in every look and level** (candy
too), so it does not single out soft and the G4.8-8 rule (soft-Low shadows off only if soft misses where candy passes) is
**not triggered**. **Budgets missed in soft:** tris on Capsule Run (440–455k) and Goo Tower (≈ 395k) vs ≤ 300k, and the draw
call peak on Capsule Run (216 High / 174 Low vs ≤ 150; the median windows are inside). Candy is inside both, so the cost is the
soft look's shadow pass (the counter includes it) plus the G4.10 crowd ring. **Not changed** (it would need a build #5):
the cheap levers, in order, are a 40 m shadow distance (from 60), one crowd row per tier, and soft shadows off on Low (G4.8-8);
your call, with a re-measure on an idle machine. The camera default (`close`) was used in every run. Raw: `.m1-logs/g4b/webgl4/`.

To re-measure on an idle machine (load < 4, nothing else running):
```bash
cd /Users/samya/Downloads/FallRush
rm -rf client-unity/Builds/WebGL && cp -R client-unity/Builds/WebGL-g412-build4 client-unity/Builds/WebGL
pnpm m1:up:mac
for r in "soft high" "soft low" "candy high"; do set -- $r
  node scripts/g47-webgl-measure.mjs "http://localhost:8080/?autoplay=1&name=QA&bot=match&gfx=$2&look=$1" /tmp/g412/$1-$2 600 5
done
pnpm m1:down
grep "budget map=" /tmp/g412/*/console.log
```

### 4. Screenshots
From build #4 in Chrome (soft, High, `close` camera), cropped to the canvas: `docs/proof/g4/final-<map>-{spawn,action}.png`
for capsule-run, goo-tower, star-fountain, lid-flip; side by side **G2/G3 → G4.6 candy → G4.8 soft (Editor) → G4.12 build #4
(browser)**: `final-compare-<map>-spawn.png`; colourblind sheets (Machado 2009, severity 1: normal | deuteranopia |
protanopia): `final-<map>-spawn-colourblind.png`. The browser frames now match the Editor proof shots' colours (they did not
before the grading fix: the build #3 frames are in `.m1-logs/g4b/webgl/` for reference).

### 5. Freeze
**⚑ Art freeze declared 2026-11-20 23:56 UTC** on build #4 (`0d020c2` + this docs commit). After this: no art / look / camera change
without your call; M5 / M6 continue.

### 6. Open items (your call)
- **Look pick** (soft default / candy) — now worth a fresh look, since every earlier build showed a broken grade.
- **Camera pick** (`close` default / `classic`).
- **Soft budget misses** (tris on Capsule Run / Goo Tower, Capsule Run draw-call peak): accept, or a build #5 with a shorter
  shadow distance / a lighter crowd; re-measure on an idle machine either way.
- G4.11 rigged Bolt was cut (scope / risk); say if you want jiggle / flop on the procedural Bolt.
- Still open from G4-A: the G4.5-8 Lid Flip timer tiebreak, the D2 bump stagger (off), match length (soak median 6.3 min vs
  the GDD's 7–9), the Goo Tower summit look-up (≈ 40° from the spawn).

### 7. Commits
`f547d8b` prompts · `580ec72` G4.8z · `8210ba6` G4.9a · `ea429f6` G4.9b · `a31d7fe` G4.9 tests · `3d8ee41` G4.10 · `0d020c2`
grading fix · this docs commit (G4.12).

