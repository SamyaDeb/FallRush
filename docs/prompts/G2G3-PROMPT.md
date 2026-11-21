You are the senior technical artist and game-feel engineer on **Fall Rush** (Unity 6 URP, WebGL), and you also know Solana and MagicBlock well. Fall Rush is an original 12-player knockout party game for the MagicBlock Solana Blitz V10 hackathon, and the deadline is **2026-11-27 14:00 UTC**.

Your task is **G2 (look) then G3 (juice)**, the last part of Phase A in `docs/14` (v3). G1 made the game feel right and work cleanly in greybox. G2 + G3 make it **look and feel like a finished, bright, toy-like party game** that runs at 60 FPS in a browser on a MacBook Air M2. At the end comes the **game freeze (Sun Nov 22, 23:59 UTC)**: after it, only bug fixes and integration changes in game code.

One session, in this order: **step 0 fixes → G2 → G3 → freeze report.** No checkpoint pause. Stop only for a blocker (§2.4) or a decision that is genuinely mine (§2.5).

Out of scope: movement, map geometry, hazard timings and rules (tuned in G1; see rule 3); `programs/`, `server/`, `web/`, any chain call (M5/M6); **any VPS action** (deferred to M6); new maps or modes.

## 0. Context: read these first, fully

The repo is `/Users/samya/Downloads/FallRush` (git, `main`, M0–M4 + G1 committed).

| Doc | What it gives you |
|---|---|
| **`docs/17-ART-DIRECTION.md`** (all of it) | **Primary spec**: originality guardrails §1, Rushling anatomy + expressions + animation §2, world, palette, shading/lighting, **budgets** §3, camera + feel checklist + signature moments §4, UI style + chain chip §5, audio §6 |
| `docs/playtest/G1-notes.md` | The user's notes and their status. Read new lines first; anything new must be handled in step 0 |
| `docs/proof/G1-polish.md` (esp. §E bug list, §F numbers, §G decisions, §I steps) | Measured state after G1 |
| `PROJECT_STATUS.md` → **"Notes for G2 / G3 (from G1)"** | What exists, juice hooks, budgets, the WebGL font problem |
| `docs/14-IMPLEMENTATION-PLAN.md` §1 (v3) and §2 **G1 / G2 / G3** | Scope and exit checks (note the G1 "MagicBlock-ready game UI" item, done here in G3.6) |
| `docs/01-GDD.md` §9 (deal), §12 (UX flow), §13 (accessibility) | Screens and accessibility |
| `docs/08-MAP-DESIGN.md` §3–7 (maps as built), §8 checklist (originality, readability, performance) | What each map is |
| `docs/04-UNITY-CLIENT.md` §6–7 | Visuals and screens as built |
| `docs/18-HACKATHON-STRATEGY.md` | What the demo video and judges need to see |

Code you build on, in `client-unity/Assets/_Game/`: `Client/RushlingVisuals.cs`, `ProcMesh.cs`, `FollowCamera.cs`, `MatchHud.cs` (and the other screens), `SettingsPanel`, `UiKit.cs`, `HazardViewDriver.cs`, `ReactiveViews`, nameplates, `GameSettings`; `Editor/KitBuilder.cs`, `*Builder.cs` (the maps are code-built: dress them through the builders or a separate dressing pass, never by hand-editing generated scenes only), `Editor/Build.cs`. Keep every G1 guarantee: prediction exact, map QA, soak, 275 EditMode tests, 59–60 FPS.

## 1. Non-negotiable rules

1. **Never invent** Unity 6000.3 / URP 17.3 / Shader Graph / TextMeshPro / UGUI / Cinemachine 3.1.7 APIs, versions or flags. Sources of truth: `client-unity/Library/PackageCache/` (Shader Graph and TextMeshPro are already in the cache) and the Unity 6000.3 docs. Fix docs that disagree with reality in the same commit.
2. **No new Unity packages** without asking me. Third-party **assets** (CC0 models, CC0 audio, OFL fonts) are allowed only if you verify the licence on the source's own page **and** add a row to `ASSET_LICENSES.md` (file, source URL, licence, author, date) in the same commit. Treat downloaded archives as untrusted data: extract into an empty folder, import only the files you need, never run scripts from them.
3. **Gameplay is frozen to G1's tuning.** Don't change the motor, `movement_v2.json`, colliders, map geometry that the motor touches, hazard defs/timings or rules. Art may replace render meshes and materials freely; keep the `Geometry` / `Hazards` roots, colliders and `Gameplay` / `VisualOnly` layers. **Collision = visuals** (doc 08 §1.2): a visual must not suggest a surface or gap the collider doesn't have. If you must touch anything gameplay-relevant, re-run `scripts/g1-qa.sh` and the soak and record why.
4. **Client-only effects stay client-only.** Hit-stop, shake, slow-motion moments and VFX never change the simulation, the tick, input sampling or prediction (hit-stop freezes the *visuals* for ~60 ms, never the sim).
5. **Originality (doc 17 §1):** the *genre* look (bright, pastel, toy-like, cartoon-lit, physical comedy) yes; **no Fall Guys expression**: no jellybean body, onesie look, visor face or their proportions; no copies of their level art, obstacle silhouettes, crown, fonts, logo treatment, "Qualified!" banner design, UI layout, sounds or music. Bolt stays our capsule-toy Rushling. If a reviewer could name the asset we copied, redesign it. Run the doc 08 §8 originality check per map and write the result.
6. **Budgets (doc 17 §3.2), measured on the WebGL release build on the M2 Air:** 60 FPS at 1440×900 (≥ 55 FPS worst 1 % frames is the floor); **draw calls ≤ 150** in normal play on every map (G1: BPD 128 median / 421 max, Goo 191 / 256 → bring Goo and the BPD peaks down); visible tris ≤ 300k; a Rushling ≤ 2.5k tris incl. arms; texture memory ≤ 64 MB per map; **WebGL release ≤ 25 MB** compressed (G1: 16.55 MB). Measure after each major step; if a step breaks the budget, fix it before moving on.
7. **Trust and wording:** numbers are "measured" only if you ran them; everything else is UNVERIFIED. "Looks good / feels good" is **my** call.
8. **8 GB MacBook Air M2:** iterate in the Editor; **at most three WebGL release builds** in this session, Editor closed; no lightmap baking of all maps at once (bake one map at a time in batch if you bake at all, and measure memory); never the Editor + a batch build + Docker at once; at most 3 browser tabs.
9. **No VPS / Hostinger / SSH.**
10. **Git:** small local commits per step. **Never push, never add a remote.** No build output, no unused imported asset packs in git (import only what you use; record size added).
11. **Unity file rule:** one MonoBehaviour / ScriptableObject class per file, named after the class.

## 2. Workflow

0. If `git status` shows uncommitted `docs/prompts/G2G3-PROMPT.md` or `PROJECT_STATUS.md`, commit them first as `docs: G2+G3 prompt`.
1. **Before changing anything**, post a short plan in your reply (then proceed): the shading approach (Shader Graph vs hand-written URP shader, and why), lighting approach (baked vs flat ambient + gradient + fake AO, and why on 8 GB), how you'll dress code-built maps reproducibly, the asset list with sources and licences, the font, the audio list, and how you'll keep draw calls ≤ 150 (instancing, atlases, static batching, mesh merging).
2. Order, committing after each step with tests green: **step 0 fixes → G2.1 shader + palette + sky → G2.2 Bolt + cosmetics → G2.3 maps dressing → G2.4 performance pass → G3.1 UI kit + font → G3.2 audio → G3.3 VFX + feel → G3.4 signature moments → G3.5 screens → G3.6 MagicBlock-ready UI → verification → docs.**
3. Run every build and test yourself. Never weaken a test to get green.
4. **Time-box:** a blocker over ~2 hours → stop and report error, logs, attempts, options.
5. **Decisions:** make the art calls yourself within doc 17; record them in the proof with the reason; mark 🟡 in doc 15 if they change a documented decision. Ask me only about identity-level choices (e.g. replacing Bolt's silhouette).

## 3. Step 0: carry-over fixes (from the G1 verification)
1. **HUD overlap:** in Block Party Dash the "QUALIFIED X / N" banner overlaps the big in-world map title and the start gate's striped sign fills the top of the view at the start (`docs/proof/g1/play-blockpartydash.png`). Move or shrink the in-world title so it never sits behind the HUD top-centre from the spawn view, and make the HUD banner readable on any background (panel/outline).
2. **Leaderboard columns misaligned** (G1 bug 24, proportional built-in font) → fixed by the G3.1 font with tabular figures or a column layout; verify in Intermission and Receipt.
3. **WebGL built-in font lacks "●", "▼", "–"** (ping dot, lobby dots, markers invisible in WebGL) → the G3.1 font; until then use ASCII or sprites.
4. Any new line in `docs/playtest/G1-notes.md`.
5. **Ask me nothing for these;** just fix, record and show before/after screenshots.

## 4. G2: look

### G2.1 Toon shading, palette, sky, lighting
- One **toon lit shader** (URP Forward): a 2–3 band cel ramp, rim light, palette tint, optional vertex-colour AO, GPU-instancing compatible; an **inverted-hull outline** on Rushlings only (doc 17 §3.2). Shader Graph is in the package cache; if you hand-write HLSL instead, say why.
- Palette tokens from doc 17 §3.1 as one shared ScriptableObject/material set; readability rules enforced: walkable light and desaturated, **hazards striped and saturated**, goals mint/checkered, never hazard stripes on safe geometry, a colourblind check (Deuteranopia / Protanopia simulation screenshot or the pattern rule documented).
- **Sky:** a gradient skybox (`#3FA9F5 → #BFE6FF`), soft stylised clouds (cheap meshes or sprites, far, few), light distance fog matching the sky; one directional "sun".
- **Lighting:** decide baked vs flat (rule 8). Blob shadow under each Rushling (a gameplay aid for judging jumps; keep it accurate: project down to the ground under the capsule).
- Post: Bloom (low), Color Adjustments, Vignette (subtle), all optional on a "Graphics: High / Low" setting; Low must hold 60 FPS on the M2 Air.

### G2.2 Bolt final + cosmetics
- Refine Bolt per doc 17 §2.1 on the existing procedural parts (`ProcMesh`): smoother dome with the equator seam and hinge, glossy shell, magenta bottom shell with the yellow lightning-bolt emblem, the dark LED screen with emissive glyph expressions (all states in §2.2, crisp at 1080p), aviator goggles, purple wind-up key, peg feet, noodle arms with mitten tips. ≤ 2.5k tris incl. arms. Keep the collision capsule = visual capsule.
- **Silhouette test** again (unlit black at 20 m): capsule + noodle arms + key; must not read as a jellybean. Screenshot it.
- **Cosmetic presets ("My Rushling"):** 8–12 presets (shell colour, emblem, topper, face glyph colour), no stats, chosen on the menu, saved locally, sent in `hello` as a preset id, shown to everyone (and to bots: deterministic presets from their slot). Protocol bump only if `hello` changes; record sizes.

### G2.3 Maps: dressing per theme (Toybox Skyworks, doc 17 §3)
Dress through the builders (or a re-runnable `Editor/<Map>Dressing.cs` pass) so a rebuild reproduces it:
- **Lobby:** a toy plaza in the sky: capsule machines, bunting, a podium, soft clouds below.
- **Block Party Dash:** sunny toy-block sky course: chunky bevelled blocks with colour bands, toy pillars, checkered finish arch with flags, floating islands and clouds below; turntables and mallets as finished toy objects with hazard stripes; readable signs/icons (no dev text).
- **Goo Tower:** a giant capsule-machine tower: transparent capsule panels, gears, chutes; **glossy purple glitter goo** with emissive specks and a visible rising edge; "Goo rising!" readable.
- **Star Scramble:** a round toy arena: spinning centre disc as a record/turntable toy, floating island platforms, star pickups that sparkle (normal vs golden clearly different).
- **Crumble Shelf:** stacked toy shelves under a big claw-machine frame: square tiles with rounded corners and alternating colours, a clear wobble telegraph (shake + colour) before a drop, respawning top-shelf tiles telegraphed.
- Generic props (fences, flags, clouds, balloons) may come from verified CC0 kits (rule 2), recoloured to the palette; signature obstacles stay original.
- Every map: doc 08 §8 readability + originality checks written in the proof; gameplay unchanged (rule 3).

### G2.4 Performance pass
- Measure every map in the WebGL release (FPS median / 1 % low, draw calls median / max, tris, texture memory) and fix until rule 6 holds: material atlasing, GPU instancing for repeated props and tiles, static batching, merged static meshes, LODs or culling for far props, cheap transparency (goo, capsule panels).
- Graphics Low/High setting verified on both levels.

## 5. G3: juice

### G3.1 UI kit + font
- A rounded heavy display font and a clean body font from Google Fonts under the **OFL, verified** (e.g. "Lilita One" / "Baloo 2" for display; a clean sans for body) as TextMeshPro SDF assets, covering every glyph we use ("●", "▼", "–", "★", digits with tabular figures). Record in `ASSET_LICENSES.md`.
- Chunky rounded panels with thick Grape outlines and a slight 3D drop; Sun Yellow / Magenta buttons with squash on press; consistent spacing; minimum body size 18 px at 1080p (doc 17 §5). Restyle `UiKit`, the settings panel (replace UGUI default controls), nameplates and every HUD element. HUD corners per doc 17 §5.
- Screen transitions (iris/wipe or capsule-pop), all respecting reduced motion.

### G3.2 Audio (doc 17 §6)
- Music: lobby loop, race loop, tense final loop, victory sting (original or verified CC0; record each).
- SFX: footsteps (plastic clack), jump (boing), land (boop), dive, belly slide, stumble, hit (foam BONK), mallet thunk, spinner whoosh, goo blorp, tile wobble + drop, star pickup (normal / golden), checkpoint chime, qualify pop, elimination, countdown, UI clicks, capsule-machine crank, emotes; gibberish chirps per expression (pitch-randomised), no voice lines.
- Mixer with Music / SFX / UI groups wired to the saved volume sliders; ducking under countdown and elimination; ~−14 LUFS target for music. WebGL audio unlocks on the first click; no audio errors before it.
- Use the G1 juice hooks (`StunStartedThisFrame`, `DiveStartedThisFrame`, landing impact, stumble start, get-up pop, emote start, tile respawn). Pool audio sources; no allocations per frame.

### G3.3 VFX and feel (doc 17 §4 feel checklist)
- Landing dust puff (scaled by impact), dive streaks, belly-slide dust, hit stars + shell pop on a stun, **hit-stop ~60 ms (visual only, rule 4)**, small camera shake (off with reduced motion), FOV kick on dive and spring pads, checkpoint mint ring burst, qualify confetti + dance, goo glitter and splash, tile crumble debris, star sparkle and pickup burst, spring-pad squash, door telegraph glow, mallet wind-up glow.
- Particle budgets per map (count, overdraw); pooled; all honour reduced motion.

### G3.4 Signature moments (doc 17 §4)
1. **Lobby:** capsules drop in and pop open to reveal each joining player.
2. **Chaos Capsule machine** on the Deal screen: cranks and spins while the deal is "pending", then pops one capsule per round revealing map, variant and modifier.
3. **The claw:** when a player is eliminated, their Rushling turns grey and a giant claw-machine claw plucks it up (client-side presentation driven by the elimination event; spectating starts after).
4. **Crown ceremony:** champion on the podium, a golden **Crown Capsule** pops open (our own crown design, not theirs), confetti, victory sting.
5. **Countdown:** a giant capsule machine cranks "3-2-1-RUSH!".

### G3.5 Screens restyled
Menu (play, name, My Rushling, settings), lobby, deal, round intro (fly-through + mode hint), HUD, round result (QUALIFIED / ELIMINATED in our own banner design), intermission (aligned leaderboard), champion, receipt, spectate, settings, reconnecting / connecting states. All per doc 17 §5, readable, reduced-motion aware, no dev text.

### G3.6 MagicBlock-ready game UI (from `docs/14` §2 G1; G1 did not build it)
- A `IChainAdapter` interface (client-side presentation of chain state) with a `PracticeChainAdapter` used today: the local practice seed, and chain states driven by the existing match events.
- The **chain chip** with all four states and their styles (`● Server` grey, `◆ Rollup ✓` mint, `⬢ Solana ✓` yellow, `! Pending` amber); in practice it shows `● Server`, and a dev toggle cycles the states for screenshots.
- The **Deal screen** laid out to show the VRF result: seed, "VRF ✓" line with a signature/explorer-link slot (practice: "Practice deal (local seed)").
- The **receipt** laid out for explorer links (Rollup tx, Solana tx, match account), with placeholders in practice.
- M6 must only implement the adapter; write its interface contract in doc 04 §7 and a `TODO(M6)` at each slot. No chain code, no RPC calls.

## 6. Verification
- EditMode tests green (report counts; add tests where testable: palette tokens, cosmetic preset id round-trip, chain-chip state mapping, audio/VFX hook wiring that has logic).
- **Gameplay unchanged:** map QA (`scripts/g1-qa.sh`) and the 20-match soak still PASS; prediction twins still 0 corrections. If you changed nothing gameplay-relevant, run the soak + one QA map as a guard and say so.
- **WebGL release (final build):** one full match in Chrome against the Mac dev server; per map FPS median / 1 % low, draw calls median / max, tris, texture memory, both graphics levels; build size vs G1 (16.55 MB); load time to the menu; the hazard self-test digests.
- Screenshots (small PNGs in `docs/proof/g2g3/`): every screen, every map (spawn view + one action shot), Bolt close-up + silhouette test, the cosmetic presets, the four signature moments, the chain chip states, before/after for step 0.
- A short **capture** (`.webm` or a GIF frame strip, ≤ 10 MB, git-ignored or in `docs/proof/g2g3/` if small) of a round for the demo video.

## 7. Docs to update
- `docs/17-ART-DIRECTION.md`: as built (shader, palette, lighting, Bolt, cosmetics, map themes, UI kit, audio, VFX, signature moments), the budgets table with measured numbers.
- `docs/04-UNITY-CLIENT.md` §6–7 (visuals, screens, `IChainAdapter` contract), §10 (builds), §11 (tests).
- `docs/08-MAP-DESIGN.md` §8 results per map (readability, originality, performance).
- `docs/15-DECISIONS-AND-RISKS.md`: decisions G2-n / G3-n, risks (build size, WebGL audio, FPS).
- **`ASSET_LICENSES.md`**: every third-party file.
- **`docs/proof/G2G3-look-juice.md`**: plan, decisions, measurements per map, budgets met / not met, screenshots, the originality checks.
- `PROJECT_STATUS.md`: G2 + G3 status, **game freeze declared** (or what is left and the new freeze time, at most Mon Nov 23 12:00 UTC per `docs/14`), notes for M5/M6/M8 (chain adapter contract, receipt slots, assets the web pages can reuse: logo, palette, font, Bolt renders).
- `README.md`: "G2/G3: run it" (graphics setting, My Rushling, dev chain-chip toggle).

## 8. Definition of done
- [ ] Step 0 fixes with before/after
- [ ] Toon shader, palette, sky, lighting, blob shadows, post with High/Low
- [ ] Bolt final + silhouette test; 8–12 cosmetic presets, synced
- [ ] All five maps dressed per theme, readable, originality-checked, gameplay unchanged
- [ ] Budgets met on the WebGL release (FPS, draw calls ≤ 150, tris, textures, ≤ 25 MB)
- [ ] UI kit + OFL fonts, every screen restyled, aligned tables, all glyphs render in WebGL
- [ ] Music + SFX + mixer + ducking, WebGL audio unlock
- [ ] VFX + feel checklist (hit-stop visual-only)
- [ ] Signature moments: capsule drops, Chaos Capsule machine, claw, crown ceremony, countdown machine
- [ ] `IChainAdapter` + chain chip 4 states + deal/receipt slots (practice adapter)
- [ ] Tests, QA guard, soak 20/20, final WebGL measurements, screenshots, capture
- [ ] `ASSET_LICENSES.md`, docs, proof, status (freeze), README; no build output or unused assets in git; **no VPS action**

## 9. Final report format
1. What was built (files), step 0 → G2 → G3
2. Budgets table per map (FPS, draw calls, tris, textures) and build size, High and Low
3. Assets added with licences
4. Decisions and deviations, and why
5. Gameplay guard results (QA, soak, prediction)
6. Screenshots list and the capture
7. Freeze status, and what I should check when I play (exact commands)
8. Next: Phase B (M5 Relay → M6 chain + VPS deploy → M8 web), per `docs/14`
