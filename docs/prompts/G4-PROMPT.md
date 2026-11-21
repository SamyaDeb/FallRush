You are the senior game designer, gameplay engineer and technical artist on **Fall Rush** (Unity 6 URP, WebGL), and you also know Solana and MagicBlock well. Fall Rush is an original 12-player knockout party game for the MagicBlock Solana Blitz V10 hackathon, and the deadline is **2026-11-27 14:00 UTC**.

Your task is **G4: map redesign + candy look**. The user played the G2/G3 build and found:
1. The maps are badly planned. Block Party Dash has hazard-free edge beams and walkways (S2, S5, S6) that are as fast as the main route, so everyone takes the easy path.
2. The course feels like separate stations, the lanes split the players up, and nobody fights over space.
3. The look is washed out (walkable tops are near-white) and too boxy. The target is the genre's saturated, cartoon, toy-like look.

The approved design is the plan page **https://claude.ai/artifact/HbkjmVdp4PBpv13GBVFrPw** (design rules, four map layouts, candy palette, shape language). It is the primary spec for this task. Where it and an older doc disagree, the plan page wins; update the older doc in the same commit.

G4 has two parts with separate freezes:
- **G4-A, gameplay (Nov 20–22):** the hazard additions, four map rebuilds, the candy palette and grading, then QA and the **gameplay freeze, Sun Nov 22 23:59 UTC** (unchanged from `docs/14`).
- **G4-B, art (Nov 23, overlapping M5):** bevelled candy geometry, a Blender prop kit, backdrop and camera, and optionally a rigged Bolt. **Art freeze: Mon Nov 23 23:59 UTC**, at the latest Tue Nov 24 12:00 UTC. M6 (chain + VPS) does not move.

Out of scope: `programs/`, `server/`, `web/`, any chain call; **any VPS action**; new modes; changing map ids, keys, modes or variant counts (they are in the on-chain map pool hash).

## 0. Context: read these first

| Source | What it gives you |
|---|---|
| **The plan page** (above; read it with the Artifact tool) | Design rules 1–7, the four layouts with metres, palette hex, shape language, build order |
| `docs/08-MAP-DESIGN.md` §1–9 | Hazard kit, pose-function rules, maps as built, QA harness |
| `docs/17-ART-DIRECTION.md` §1–3 | Originality guardrails, Bolt identity, budgets |
| `packages/shared/config/movement_v2.json` | 5 m/s, 1.2 m hop, 3.2 m running reach, dive 7 + 2.5 m/s; gap rule: a real jump = 2.2–2.7 m |
| `docs/proof/G1-polish.md`, `docs/proof/G2G3-look-juice.md` | Measured state, budgets, the QA guard |
| `PROJECT_STATUS.md`, `docs/14` §1 | Schedule; Phase B (M5 Nov 23, M6 Nov 23–24) |
| `docs/playtest/G1-notes.md` | Any new user notes: handle them first |

Code: `Shared/Hazards/` (pure pose functions, integer-first), `Net/Hazard*.cs`, `Editor/*Builder.cs` + `KitBuilder.cs` + `MapArt.cs` + `MapDressing.cs` (maps are code-built: change the builders, never hand-edit generated scenes), `Net/PaletteTokens.cs`, `Client/GraphicsQuality.cs`, `Client/ProcMesh.cs`, `Client/RushlingVisuals.cs`, `Shared/SkillRules.cs`, `Shared/FinalRules.cs`, `Net/MapQa.cs`, `Net/ModeQa.cs`, `scripts/g1-qa.sh`, `scripts/m4-qa.sh`.

## 1. Non-negotiable rules

1. **Originality.** Take reference from the genre by observation only: pacing, crowding, readability, saturation, soft shapes. **Never extract, decompile or rip any other game's files** (no REA or similar tools on Fall Guys or any other commercial game), and never trace a level layout, obstacle silhouette, character or UI from one. Doc 17 §1 still holds: Bolt stays our capsule-toy Rushling (no jellybean body, onesie or visor face). Run the doc 08 §8 originality check per map and write the result.
2. **Determinism.** Every new hazard pose is a pure function `f(params, variant, hazard_seed, tick)` in integer maths (the Q30 sine table, mm, ticks), evaluated the same on server and client. Add hazard vectors to `HazardVectorsTool` and EditMode tests. Prediction twins must stay at 0 corrections.
3. **Collision equals visuals.** A visual must never suggest a surface or gap the collider doesn't have. Bevelled render meshes keep box colliders only where the bevel is ≤ 0.15 m; otherwise match the collider to the shape.
4. **Chain compatibility.** Map ids, keys, modes, variant counts and modifier weights stay the same (`map_pool_v1.json` hash is compiled into the program). Display names may change (`MapInfo`/UI only). Map content hashes change: update `Tests/Vectors/map_hashes_v1.txt` and note in `PROJECT_STATUS.md` → "Notes for M6" that the new hashes go into the ruleset/map-pool v2.
5. **Movement.** `movement_v2.json` stays as it is, except the optional bump stagger (§3, G4.1c), which needs the user's yes and is recorded as a ruleset-v2 field for M6.
6. **Budgets** (doc 17 §3.2, measured on the WebGL release, M2 Air): 60 FPS at 1440×900, worst 1 % ≥ 55; draw calls ≤ 150 normal play; visible tris ≤ 300k; Rushling ≤ 2.5k tris; WebGL release ≤ 25 MB (G2/G3: 19.03 MB).
7. **Trust.** Numbers are "measured" only if you ran them; everything else is UNVERIFIED. "Feels good / looks good" is the user's call.
8. **8 GB M2 Air.** At most **three WebGL release builds** in G4 (one per freeze plus one spare), Editor closed; never the Editor + a batch build + Docker at once. Blender runs headless (`blender -b -P script.py`), one instance at a time.
9. **No VPS / Hostinger / SSH. Git:** small local commits per phase; **never push, never add a remote.**
10. **No new Unity packages** without asking. Third-party assets only CC0/OFL with the licence verified on the source page and a row in `ASSET_LICENSES.md`.
11. One MonoBehaviour / ScriptableObject class per file, named after the class.

## 2. Workflow

1. Commit `docs/prompts/G4-PROMPT.md` first as `docs: G4 prompt`.
2. Post a short plan in your reply (then proceed): the new hazard maths, how the builders will be restructured, and how you will measure "safer route costs ≥ 25 % more time".
3. Work phase by phase in the order below. Commit after each phase with EditMode tests green. Run every test and build yourself; never weaken a test.
4. **Time-box:** a blocker over ~2 h → stop and report error, logs, attempts, options. If G4-A is not green by **Sun Nov 22 18:00 UTC**, cut in this order: Star Fountain changes (keep today's Star Scramble with the candy palette), then Lid Flip (keep the Crumble Shelf grid, recoloured, with the shrinking rings only), then Goo Tower (keep 3 laps, remove the dead time only). Capsule Run is never cut.
5. Ask the user only about identity-level choices and the §3 open decisions.

## 3. Open decisions (defaults until the user answers)

| # | Decision | Default |
|---|---|---|
| D1 | Display names: Capsule Run / Goo Tower / Star Fountain / Lid Flip | Use them; renames are a one-line change |
| D2 | High-speed player bumps stagger the slower player | **Off**, built behind a flag (G4.1c) so it can be switched on after the playtest |
| D3 | Six candy surface hues (plan page) | Use them as given |
| D4 | Rigged Bolt (G4.9) | Stretch: only after G4.6–G4.8 are green |

## 4. G4-A: gameplay (ends with the gameplay freeze, Sun Nov 22 23:59 UTC)

### G4.0 Rules and docs (≈ 1 h)
- Replace doc 08 §1 rule 4 with plan-page rules 1–7. Add the "route cost" check to the §8 checklist.
- `docs/15`: decisions G4-1 (shared routes, no free path), G4-2 (Lid Flip replaces reactive crumble in the Final), G4-3 (display names), G4-4 (two-part freeze).
- Exit: docs committed.

### G4.1 Hazard kit additions (≈ 4 h)
a. **ROLLING_BALL**: a sphere (r 1.0 m) moving down a line segment at a constant speed, respawning at the top. Params: start/end mm, speed mm/s, period ticks, phase16, radius mm. Pose is a pure function of the tick. Contact = `knock_hard` along the roll direction (like the mallet). Telegraph: a chute light flashes 0.5 s before each release.
b. **LID_FLIP** (one grid component, replacing the reactive `CRUMBLE_TILE` grid in the Final): an N × N grid of lids. Each lid's state at tick t is a pure function of `(hazard_seed, lid index, t)`: idle → lit (30 ticks, yellow) → flipped (no collider, 45 ticks) → back. The flip rate rises over time (wave every 90 → 45 ticks). **Ring retire:** the outer ring is removed for good at fixed ticks (plan: every 25 s) down to the inner 3 × 3. Keep the old grid in the code as the fallback (§2.4).
c. **Bump stagger flag** (D2, default off): when two players meet with closing speed ≥ 4 m/s, the slower one gets a 0.25 s stagger at 60 % speed (no stun, input live). Server-authoritative and predicted against remotes like the existing push. Flag in the ruleset config, not hard-coded.
- Tests: pose vectors for both hazards (100 ticks each, both variants), lid state round-trip, ring-retire timing, the bump flag on/off.
- Exit: EditMode green; prediction twins 0 corrections on a sandbox with both hazards.

### G4.2 Capsule Run rebuild (key `BlockPartyDash`, ≈ 6 h)
Rebuild `BlockPartyDashBuilder.cs` to the plan page layout (z in metres along the course):

| Section | z | Build |
|---|---|---|
| Start funnel | 0–14 | 20 m deck → 12 m behind the start gate (GATE_DOOR mode 1) |
| S1 Gumball Slope | 14–50 | 12 m ramp at 8°, rails; 5 ROLLING_BALL lanes, staggered phases, variant A/B release pattern |
| Wave Gates (choke) | 50–62 | 10 m wall at z 56.6 with five 2 m GATE_DOORs lifting in a travelling wave (phase offsets), magenta telegraph |
| S2 Hop Field | 62–104 | over the void: centre column of 2.4 m bobbing blocks (MOVING_PLATFORM, gaps 1.6 m) + two outer columns of 2.0 m static blocks (gaps 2.4 m), tops 0.3–1.2 m |
| S3 Sweep Pad | 104–136 | 12 m radius disc; two SWEEPERs in opposite directions: low arm (jump, 0.35 m) and high arm (dive under, 0.95 m) |
| Mallet Run (choke) | 136–166 | 7 m bridge; 3 MALLET_SWINGs along the bridge, heads covering 6 of 7 m; **no edge beams**; low 0.4 m rails |
| S4 Belt Climb | 166–200 | CONVEYOR 10 m wide against you, +6 m; 3 BUMPERs on it; side ledges with SPRING_PADs to the finish deck |
| Finish | 200–212 | mint/checker deck, inflatable arch visible from the start |

- Remove every in-world route label; rename the visible title (D1).
- Bot routes: update `WaypointBot`/`BotBrain` routes for every path, including the springs and both Hop Field columns.
- Exit (QA, `scripts/g1-qa.sh 100 BlockPartyDash` + `m4-qa`): 0 soft-locks; every route reaches its next checkpoint; **for each section, the slowest valid route takes ≤ 1.25× and ≥ 1.0× the time of the fastest; no route is hazard-free**; normal-bot median **85–110 s**; exactly 8 qualify in every run.

### G4.3 Goo Tower rebuild (key `GooTower`, ≈ 4 h)
- Two laps round a 14 × 14 m core on a 5 m ledge, y 0 → 22 m (+2.75 m per side), about 160 m of path.
- Lap 1 sides: stairs + falling steps · lift pair (A/B periods) · belt ramp vs. spring hop · bumpers on the ledge. Lap 2: narrow steps with 2.4 m gaps · SWEEPER bar · belt against you · gate → summit pad. Lap 2 corner spring shortcut (a miss drops one lap).
- GOO_PLANE delay 6 s, start rate 0.235 m/s, tuned in QA.
- Exit: every route index escapes alone at reference speed with a **goo margin of 8–10 s**; the goo never overtakes the slowest valid route; normal-bot median **90–110 s**; 5 of 8 survive.

### G4.4 Star Fountain (key `StarScramble`, ≈ 3 h)
- Arena r 18 m; TURN_TABLE ring r 6–11 m; static centre r 6 m with the capsule fountain; three low SWEEPER arms on the outer ring; three islands at r 21.5 m with golden stars, reached by SPRING_PADs on the rim.
- `SkillRules`: a wave of 12 stars every 8 s, spawned from the fountain onto anchors on the turntable ring (anchors move with it, pure); golden star every 15 s on one island. Knockback still costs one star.
- Exit: `m4-qa` Skill PASS; scores = pickups − drops; median **60–75 s**.

### G4.5 Lid Flip (key `CrumbleShelf`, ≈ 4 h)
- 9 × 9 lids of 1.8 m on the top shelf; one low SWEEPER across the top; a lower 5 × 5 shelf 6 m below (one more chance), kill-Y under it.
- `FinalRules`: last one standing; tie-break unchanged. The flip pattern uses the round's hazard seed (in M6 this becomes the VRF seed: note it in "Notes for M6").
- Exit: `m4-qa` Final PASS (one champion every run); median **60–100 s**; bots don't all die in the same tick.

### G4.6 Candy palette + grading (≈ 2 h; ships even if G4-B slips)
- `PaletteTokens.cs`: walkable tops and sides from the plan page (Bubblegum, Lemon drop, Soda blue, Sour apple, Grape fizz, Tangerine), one hue per section; tops at 45–75 % saturation, 85–95 % value, never white. Meaning colours reserved: hot pink + ink stripe (hits you), pulsing lemon (about to move), mint + checker (goal/spring), goo purple, sky `#2E9BFF → #A6E3FF`. Update the palette EditMode tests (a walkable-top saturation floor, meaning colours never used on walkable tops).
- `GraphicsQuality.cs`: Neutral tonemapping, saturation +22, contrast +12, post-exposure −0.15; warm sun, cool sky ambient; a rim term in the toon shader. Low keeps the colour work (it is cheap) and drops only bloom/vignette.
- Colourblind check (Deuteranopia / Protanopia) screenshot per map: hazards must still read by the stripe pattern.

### G4.7 Gameplay QA + freeze (≈ 3 h)
- `scripts/g1-qa.sh` and `scripts/m4-qa.sh 100` on all maps; soak 20/20; prediction twins 0; EditMode green; new map hashes in the vectors.
- **WebGL release build 1**: one full match in Chrome against the Mac dev server; FPS / draw calls per map, both graphics levels.
- Screenshots (`docs/proof/g4/`): every map's spawn view and one action shot, before/after per map, the palette and colourblind sheets.
- Ask the user to play all four maps (`docs/playtest/G4-notes.md`, one line each). **Gameplay freeze Sun Nov 22 23:59 UTC.**

## 5. G4-B: art (Nov 23, alongside M5; art freeze Mon Nov 23 23:59 UTC)

Art only: render meshes and materials. No collider, hazard timing or rule changes (rule 3). After G4-B, re-run the QA guard (one map) and the soak.

### G4.8 Candy geometry (≈ 4 h)
- `ProcMesh`: a bevelled-box generator (radius ≥ 0.15 m, 2–3 segments), rounded cylinders with soft caps, a slight bulge option for posts. Top/side hue split, baked vertex-colour AO and a bottom-dark gradient.
- Swap every walkable block, deck, rail and post render mesh in the builders; platforms at least 0.6 m thick.
- Keep the G2.4 static-chunk merge so draw calls stay ≤ 150.

### G4.9 Blender prop kit (≈ 4 h)
- One headless Python script in `tools/blender/` generates about 20 original props to the plan page's shape language: foam mallet roller with seams, pillow bumper, padded beam with round caps, gumball with a shine, spinner arm with foam caps, inflatable start and finish arches, capsule-machine dome, coin slot, prize star, confetti cannon, flags, bunting, bleacher silhouettes, inflatable towers. Exported FBX, ≤ 1.5k tris each, palette via vertex colour (no textures).
- Commit the script and the FBX files (small); the script must regenerate them exactly.
- Padded props squash and wobble on hit (client-only, driven by the existing hit events).

### G4.10 Backdrop + camera (≈ 2 h)
- Each map gets a stadium-style toy backdrop: bleacher silhouettes with a bobbing crowd of simple capsule shapes, banners, flags, inflatable towers, distant floating islands. Our own capsule-toy floor or cloud sea far below, not a copy of any game's floor.
- `FollowCamera`: closer and lower, so a Rushling is 12–15 % of screen height in normal play; nameplates never overlap (stack offset or fade with distance). Fix the broken `bolt-closeup.png` proof shot.

### G4.11 Rigged Bolt (stretch, D4; ≈ 5 h, cut first)
- A single smooth skinned Bolt (Blender headless, same identity and silhouette as today: capsule body, noodle arms, wind-up key), bones driven by the existing procedural animation. Client-only spring bones for jiggle, and a client-only visual ragdoll during tumble that blends back on get-up (the server still owns position). ≤ 2.5k tris. Silhouette test again.

### G4.12 Performance + art freeze (≈ 2 h)
- **WebGL release build 2**; measure every map on both levels against rule 6; fix before the freeze.
- QA guard (one map) + soak 20/20.
- Screenshots and a short capture for the demo video. **Art freeze Mon Nov 23 23:59 UTC** (at the latest Tue Nov 24 12:00 UTC).

## 6. Docs to update
- `docs/08-MAP-DESIGN.md`: rules, the four maps as built, QA results, route-cost table, originality checks.
- `docs/17-ART-DIRECTION.md`: palette, grading, shape language, prop kit, backdrop, camera, budgets measured.
- `docs/04-UNITY-CLIENT.md` (hazards, visuals), `docs/15` (G4 decisions and risks), `docs/14` (the two-part freeze).
- `ASSET_LICENSES.md` (only if a third-party file was used).
- **`docs/proof/G4-maps-look.md`**: plan, decisions, QA numbers per map, budgets, screenshots.
- `PROJECT_STATUS.md`: G4 status, the freezes, **Notes for M6** (new map content hashes, LID_FLIP seed = VRF seed, the bump flag as a ruleset-v2 field).
- `README.md`: "G4: run it".

## 7. Definition of done
- [ ] Rules in doc 08 replaced; no hazard-free route on any map; route-cost check per section
- [ ] ROLLING_BALL + LID_FLIP (+ ring retire) pure, tested, predicted; bump flag built (off unless the user says yes)
- [ ] Capsule Run, Goo Tower, Star Fountain, Lid Flip rebuilt; QA PASS; medians in target
- [ ] Candy palette + grading live on High and Low; colourblind check
- [ ] Gameplay freeze Nov 22 with WebGL build 1 and the user's playtest notes
- [ ] Bevelled candy geometry, Blender prop kit (script committed), backdrop, camera
- [ ] Rigged Bolt, or a note that it was cut
- [ ] Budgets met on WebGL build 2; QA guard + soak after the art; art freeze
- [ ] Docs, proof, status, README; no VPS action; nothing pushed

## 8. Final report format
1. What was built, phase by phase (files)
2. QA per map: medians, route-cost table, goo margins, qualify counts
3. Budgets per map (FPS, draw calls, tris) and build size, High and Low
4. Decisions and deviations, and why; what was cut
5. Gameplay guard (QA, soak, prediction)
6. Screenshots and capture
7. What the user should check when playing (exact commands)
8. Next: M5 → M6 (with the new map hashes and the VRF lid seed) → M8
