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

