# G2 + G3 proof: look and juice (2026-11-19)

Numbers are **measured** on the dev machine (MacBook Air M2, 8 GB) unless marked **UNVERIFIED**. "Looks good / feels good / sounds good" is the user's call. No VPS action. Raw logs: `.m1-logs/g2/` (git-ignored). Screenshots: `docs/proof/g2g3/`.

## A. Plan (as posted before the work)
- **Shading:** a hand-written URP Forward HLSL toon shader (`FallRush/Toon`), not Shader Graph: we build headless in batch mode, a text shader is reviewable, keeps variants minimal (instancing × fog) and carries the inverted-hull outline as a second pass (Shader Graph cannot in URP without a renderer feature).
- **Lighting:** flat, not baked (8 GB, code-rebuilt maps, no lightmaps to rebake or ship): one sun, trilight ambient, cel ramp, vertex AO, linear fog, gradient sky; exact blob shadows.
- **Reproducible dressing:** `Editor/MapArt.cs` + `Editor/MapDressing.cs`, run by every builder before it saves; colliders / hazard defs untouched, map hashes checked.
- **Assets:** meshes and audio all original (code / `tools/audio/synth.py`); fonts Lilita One, Varela Round, Noto Sans Symbols 2 (OFL, verified on the `google/fonts` repo pages) as static TMP SDF atlases.
- **Draw calls:** Rushlings merged to 2 renderers, hazard groups merged (1 draw each), static batching with one vertex-colour material, instanced tiles / stars / blob shadows, outline LOD.

## B. Step 0 (carry-over fixes)
| # | Issue | Fix | Before → after |
|---|---|---|---|
| 1 | BPD "QUALIFIED X / N" banner over the in-world title; striped gate sign filling the top | G1 had moved the BPD title off the gate; G3 puts every HUD counter in a white pill with a Grape border (readable on any background); every map's in-world **title sign shows only during the intro fly-through**; checkpoint banners raised to 5 m (they sat at camera height); signs hide within 8 m of the camera | `g2g3/before/before-play-BlockPartyDash.png` → `g2g3/play-blockpartydash.png` |
| 2 | Leaderboard columns misaligned (bug 24) | Tables are rows of fixed-width cells in Varela Round, whose digits are tabular (all 40 units, asserted by a test) | `g1/08-intermission-r1-blockpartydash.png` → `g2g3/intermission.png` |
| 3 | WebGL font lacked "●", "▼", "–" | TMP static atlases + Noto Sans Symbols 2 fallback: every glyph we use is checked at bake time and by `JuiceTests.Fonts_EveryUiGlyphRenders_WithFallback`; seen in WebGL (ping dot, ▼ YOU, ◀ ▶, ● Server, ♛, ★) | `g2g3/webgl-*.png` |
| 4 | New lines in `docs/playtest/G1-notes.md` | none since G1's status lines | – |

## C. Decisions and deviations
See doc 15 **G2-1 … G2-9, G3-1 … G3-6**. In short: hand-written shader (not Shader Graph), flat lighting (not baked), humans pick presets (doc 17 said server-random), protocol v6, a lobby capsule-machine collider (collision = visuals), no AudioMixer asset (code buses), audio synthesised by us, the claw only for Rushlings still visible at the result, the arm-tube winding fix.

## D. Measurements (WebGL release, Chrome via Playwright, canvas 1440 × 900 at DPR 1, M2 Air, against the Mac dev server; `?bot=match` plays the whole match)

Per-map numbers come from the `[gate] budget` line (`GateLogger`, every 10 s window): FPS median and **1 % low** (the 99th-percentile frame time as FPS), draw calls median / max over every frame (`ProfilerRecorder "Draw Calls Count"`), the max visible triangles, and texture memory **estimated** from the loaded textures' sizes and formats (not a profiler counter).

### D.1 Final build (WebGL release 3, **19.03 MB**), Graphics **High**
| Map | Windows | FPS median | 1 % low | Draw calls median | Draw calls max | Tris max | Textures (est.) |
|---|---|---|---|---|---|---|---|
| Lobby | 2 | 59 | 53 | 46 | 52 | 41k | 20.7 MB |
| Block Party Dash | 9 | 59 | 53 | **69** | **122** | 90k | 20.7 MB |
| Goo Tower | 10 | 59 | 53 | **58** | **99** | 121k | 20.7 MB |
| Star Scramble | 7 | 59 | 53 | 54 | 68 | 48k | 20.7 MB |
| Crumble Shelf | 10 | 59 | 53 | 41 | 72 | 59k | 20.7 MB |

### D.2 Final build, Graphics **Low**
| Map | Windows | FPS median | 1 % low | Draw calls median | Draw calls max | Tris max | Textures (est.) |
|---|---|---|---|---|---|---|---|
| Block Party Dash | 7 | 59 | 53 | 49 | 69 | 70k | 13.7 MB |
| Goo Tower | 10 | 59 | 53 | 39 | 65 | 107k | 13.7 MB |
| Star Scramble | 7 | 59 | 53 | 29 | 41 | 46k | 13.7 MB |
| Crumble Shelf | 13 | 59 | 53 | 30 | 39 | 39k | 13.7 MB |
(The lobby window closed before the first Low log line.)

### D.3 How we got there
| Build | Size | BPD draw calls median / max (High) | Goo median / max | 1 % low (High) |
|---|---|---|---|---|
| G1 (greybox) | 16.55 MB in the G1 proof (the same Unity `reportedSize` counter logged 16.45 MB for it) | 128 / 421 | 191 / 256 | – |
| G2/G3 build 1 | 18.96 MB | 141 / 175 | 78 / 181 | 53 |
| build 2 (outline LOD, HUD churn removed) | 18.95 MB | 87 / 148 | 80 / 162 | 56 |
| **build 3** (+ static chunk merge) | **19.03 MB** | **69 / 122** | **58 / 99** | 53 |
Mac Metal A/B of the static merge (`FALLRUSH_NO_MERGE=1`): Goo 90 / 256 → 63 / 156, BPD max 238 → 216.

### D.4 Budgets (doc 17 §3.2)
| Budget | Result |
|---|---|
| 60 FPS at 1440 × 900 | **59 median on every map, both levels** (Chrome's rAF cadence) — met |
| ≥ 55 FPS worst 1 % frames | **53** in builds 1 and 3, **56** in build 2, identical on every map and on both levels, even in the lobby at 41 draw calls: the 99th-percentile frame is ~18.9 ms, i.e. browser frame-pacing jitter (no 33 ms missed frames), not render cost. **Not met as written** (53 < 55); the cause is UNVERIFIED (Playwright-driven Chrome; please check in a normal Chrome window) |
| Draw calls ≤ 150 in normal play | **met on every map** (max 122 High, 69 Low) |
| Visible tris ≤ 300k | **met** (max 121k) |
| A Rushling ≤ 2.5k tris incl. arms | **met** (2,330 Bolt; every preset tested) |
| Texture memory ≤ 64 MB / map | **met** (20.7 MB estimate High, 13.7 MB Low) |
| WebGL release ≤ 25 MB | **met** (19.03 MB; +2.5 MB vs G1: fonts' SDF atlases, 38 audio clips, scenes' baked meshes) |
| Load time to the menu | ~1.2 s on localhost with a warm browser cache (cold, over a network: UNVERIFIED) |

**Self-test** (`?selftest=1`, final build): PASS `hazard_vectors_v1` `fc06826c…` and PASS `hazard_vectors_v2` `d4b2a73a…` (0 mismatches). **Full match** in Chrome on both levels: `COMPLETE`, 38 events; no exceptions (only Unity's startup `getInternalformatParameter` warnings and Chrome's "AudioContext was not allowed to start" warnings until the first click, which is the expected browser rule).

## E. Originality and readability checks (doc 08 §8; two-person originality check still needs you)
Asked per map: "could a reviewer name a Fall Guys asset we copied?"
- **Rushling:** our capsule-toy Bolt (two-tone capsule shell with equator seam and hinge, LED screen face, wind-up key, peg feet, noodle arms) — no jellybean body, no onesie, no visor face; silhouette test passes (capsule + arms + key). Pass (self-check).
- **Lobby:** gacha capsule machines, bunting, a podium on floating islands. No FG lobby elements. Pass.
- **Block Party Dash:** pastel chamfered toy blocks with a white top band, spinner bars, gate doors, record-less turn tables, foam mallets; checkpoint banners and pennants. The course layout is ours (doc 08 §3). Pass.
- **Goo Tower:** a square capsule-machine tower with flat capsule windows, gears and a glass capsule globe; purple glitter goo. A rising-hazard climb is a genre convention; no FG level is traced. Pass.
- **Star Scramble:** a round arena with a toy-record centre, drum base, chunky stars. Pass.
- **Crumble Shelf:** square tiles on three shelves inside a giant claw machine (not hexagons, not a single arena). Pass.
- **UI:** our own two-tone capsule result banner (not a "Qualified!" ribbon), Lilita One / Varela Round (not FG's fonts), capsule-pop transitions, chain chip; layout per doc 17 §5. **Crown:** our gold band with five rounded capsule prongs, tipped balls and a tiny wind-up key, delivered in a Crown Capsule (not FG's crown). Pass.
- **Audio:** synthesised by us, no samples. Pass.

**Readability:** walkable tops light / desaturated (tested), hazards striped and saturated (spinner bars, mallet heads with bands, bumpers with bands, door stripes, falling blocks, world-space stripes on static hazard posts), goals mint / checkered (spawn pads, checkpoint stripes and banners, summit pad, finish), no stripes on safe geometry (Goo Tower core bands fixed). **Colour-blind:** we documented the pattern rule instead of a simulation screenshot: every hazard carries the stripe **pattern** (geometry stripes or the shader's world-space stripes), and the two stripe colours differ in luminance by > 0.3 (tested), so they read in grey-scale, i.e. under deuteranopia / protanopia. A simulated screenshot is UNVERIFIED (not produced).

## F. Gameplay guard
Nothing in the motor, `movement_v2.json`, colliders, map geometry the motor touches, hazard defs or rules changed, **except one lobby collider** (G2-7: the capsule machine now has a box collider matching its visual; the lobby has no eliminations and is not hashed). Guard runs on the final code:
- **Map content hashes unchanged** for all six maps after every rebuild (`map_hashes_v1.txt` diff empty; the EditMode test asserts the committed scenes).
- **EditMode 287 / 287** (G1: 275; new: `LookTests` ×7, `JuiceTests` ×5), incl. the prediction twins (0 corrections) and hazard vectors v1 / v2.
- **Soak** `scripts/m4-soak.sh 20 8`: **20 / 20 COMPLETE**, `20/20 logs valid`, 38 events each, durations 418–441 s for the last ones (G1: 343–442 s).
- **Map QA guard** `scripts/g1-qa.sh 100 CrumbleShelf`: **QA PASS**, medians reference 76.4 s / normal 71.9 s (G1: 76.5 / 71.8 s) — `g2g3/qa-guard-crumbleshelf.md`.
- **Real client (WebGL):** final build, two full matches: prediction 1 correction in 52 Low windows (6 in 147 windows in total), 0 hazard mismatches.

## G. Screenshots and capture (`docs/proof/g2g3/`, ≤ 960 px)
- **Step 0 before / after:** `before/before-*.png` (G1 build) → `play-blockpartydash.png`, `intro-*.png`, `intermission.png` (aligned table), `webgl-*.png` (glyphs in WebGL).
- **Every screen:** `menu.png`, `settings.png`, `webgl-menu.png`, `lobby.png` (capsule drop + roster), `deal-pending.png` / `deal-dealt.png` (Chaos Capsule machine, VRF slot), `intro-*.png` (four maps), `play-*.png` / `spectate-*.png`, `result-claw.png` and `result-gootower.png` (the claw), `intermission.png`, `champion-crown.png` (Crown Capsule ceremony), `receipt.png` / `webgl-receipt.png` (explorer slots, chain chip).
- **Bolt:** `bolt-closeup.png`, `bolt-silhouette.png` (unlit black; taken in the lobby at ~10–25 m rather than the exact 20 m side shot), `presets.png` (the 12 presets).
- **Chain chip states:** the four styles are tested (`JuiceTests.ChainChip_FourStates_MapToDoc17Styles`); screenshots of the cycling chip (`?chain=cycle`) were **not** taken — UNVERIFIED visually.
- **Signature moments:** 1 `lobby.png`, 2 `deal-*.png`, 3 `result-claw.png` / `result-gootower.png`, 4 `champion-crown.png`, 5 the countdown machine (seen in captures, no still committed).
- **Capture:** `capture-gootower.webm` (0.9 MB, 11 s at 10 fps: a WebGL Goo Tower climb, spectating).
