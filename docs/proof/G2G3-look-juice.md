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

## D. Measurements
(see §D.1–D.3 below, filled in from the WebGL release runs)

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
(filled in: map hashes, QA, soak, prediction)

## G. Screenshots and capture
(listed in §G below)
