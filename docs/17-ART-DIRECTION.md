# 17 — Art, Audio & Feel Direction

**Status:** v2, approved direction (2026-11-18)
**Canonical reference:** `Fall Rush  Logo.png` in the repo root (the hero character "Bolt" and the palette).
**Style target:** chunky, bouncy, comedic knockout-party *feel*, in the spirit of the genre (Fall Guys-style energy), executed with **original** characters, shapes, world, UI and audio.

---

## 1. Originality guardrails (read first)

We match the genre's *feel*: pastel chaos, wobbly physics comedy, big readable obstacles. We do **not** match any specific game's *expression*.

| Allowed (genre conventions) | Not allowed (protected expression) |
|---|---|
| Rounded, toy-like characters | Jellybean body, full-body onesie costume look, visor-hole face, Fall Guys' proportions |
| Pastel colors, floating arenas, cartoon obstacles | Tracing or reproducing any Fall Guys level layout, obstacle silhouette, crown design or UI layout |
| Knockout format, qualify / eliminated screens | Their fonts, logo treatment, sound effects, music, announcer lines, "Qualified!" banner design |
| Disappearing-tile final, rising hazard | Named copies of their rounds (e.g. Hex-A-Gone, Slime Climb, Door Dash) |

**Rule:** if a reviewer could name the Fall Guys asset we copied, we redesign it. Every map gets a short originality check in its QA list (doc 08).

---

## 2. The Rushlings (characters)

**Concept:** Rushlings are wind-up toys that pop out of **gacha capsules** in the Toybox Skyworks. Every one of them *is* a capsule.

### 2.1 Anatomy (hero "Bolt", per the logo)

| Part | Design | Gameplay / tech note |
|---|---|---|
| Top shell | Glossy white dome with a visible **equator seam** and a small hinge on one side | Shell skins swap this material |
| Screen face | Dark rounded-rect **LED screen** on the upper front. Eyes and mouth are emissive glyphs (`^ ^`, `> <`, `O O`, `x x`). | Expressions are a texture atlas plus UV offset, so there are no blendshapes (cheap) |
| Bottom shell | Saturated colored half (Bolt = magenta), with an emblem decal (Bolt = yellow lightning bolt) | Emblem is a cosmetic slot |
| Goggles / topper | Optional head item (Bolt = aviator goggles) | Topper cosmetic slot |
| Noodle arms | Two long, bendy, tube-shaped arms with rounded mitten tips | Procedural: bezier/spline tube driven by a spring chain (§2.3). **Visual only, no collision.** |
| Feet | Two short peg feet or little springs under the shell | Procedural stepping (§2.3) |
| Wind-up key | Purple key / knob on the back | Spins at a speed proportional to player speed; spins wildly when diving |

**Proportions:**
- Body height 1.0 unit, width 0.62.
- **The collision capsule matches the visual capsule.** This is critical for fairness and readability: what you see is what gets hit.
- Arms reach about 0.9 units when flung out.

**Silhouette test:**
- At 20 m distance, in pure black, a Rushling must read as *capsule + noodle arms + key*.
- It must not read as a jellybean.
- Run the test in Unity with an unlit black override material.

### 2.2 Expressions (screen face atlas, 4×4)

Each expression and its trigger:
- idle `^ ^`
- running `• •`
- jumping `O O`
- diving `> <`
- stunned `@ @` (spiral)
- falling `O ◡ O` (panic)
- qualified `★ ★`
- eliminated `x x` (plus a "crying" pixel animation)
- champion `♛`
- emotes: blink, wink, angry, love, sleepy, smug, and two spares

### 2.3 Procedural animation (no humanoid rig)

| System | Technique |
|---|---|
| Squash & stretch | Scale the body on vertical-velocity spikes: landing squash (1.15, 0.8) → spring back over 0.18 s; jump stretch (0.9, 1.12) |
| Body lean | Tilt toward acceleration (max 18°), with a critically damped spring |
| Wobble | Second-order spring on local rotation, excited by impacts and hazard hits |
| Noodle arms | Each arm is a 4–6 point verlet / spring chain anchored at a shoulder socket. Rendered as a tube mesh along a Catmull-Rom spline (≤ 8 segments × 6 sides). The arms trail motion, flail when airborne and reach forward on dive. |
| Feet | Two-point procedural stepping: foot targets snap forward when > stride distance, with a little arc |
| Stun "pop" | The top shell hinges open 25°, stars orbit, then the shell snaps shut (0.6 s) |
| Dive | Body pitches 80° forward, arms extend ahead, key spins ×3 |
| Ragdoll-like knockback | **Visual only.** The server owns the stun state and knockback velocity, and the client plays tumble rotation and wobble on top. There is no client-side physics ragdoll that could desync. |

**All of this runs on the client.** The dedicated server never runs animation (`-nographics`, components stripped).

#### 2.3.1 As built (G1, `Client/RushlingVisuals.cs`, allocation-free per frame)

Our own procedural animation on Bolt, driven only by the motor state the client already has (position, velocity, the motor flags `Grounded / Diving / DiveLanding / Stumbling`, stun ticks, facing). Remote players play the same states from interpolated snapshots: nothing new on the wire except the `Stumbling` flag bit (snap still 294 B for 12).

| State | What you see |
|---|---|
| Facing | Visual turn rate ≤ 720°/s (×2.5 while diving), eased; the body leans into turns (≤ 12°) |
| Run | A gait phase advances at `3.2 + 0.95 × speed` steps/s (8 steps/s at 5 m/s ≈ 0.6 m per step). Feet step in time with it: the stance foot stays planted in the world (no sliding), the swing foot lands half a step ahead with an arc; a waddle roll (±7°), a yaw wiggle (±5°) and a body bob (4 cm) per step; arms swing opposite the legs; a forward lean with speed plus the acceleration lean |
| Idle | Breathing squash (±1.4 %), blinks, a slow look-around (±25°) after 1.5 s, a fidget bounce with a key spin every 5–9 s |
| Jump / fall | Take-off: a quick squash that springs into a stretch; rising: lean back, arms up; falling fast (> 5 m/s): arms straight up and flailing faster, feet kicking, panic face; landing squash scaled by the impact |
| Stumble | (fall > 3 m) a forward pitch of 24° with a decaying roll shake and windmilling arms for 0.6 s; "falling" face |
| Dive | Belly-flop pitch 82°, arms reaching ahead (superhero), key ×3; the landing slaps (a little bounce) into the belly slide at 88°; when the slide ends a scramble (feet paddling, a body shake) and an underdamped spring pops past upright |
| Hit / tumble | On a stun: flop over in the knock direction (0 → 90° in 0.12 s), a full roll on stuns ≥ 0.45 s, a bounce, lying on the side with ragdoll arms, then the get-up pop (back to upright with a 12° overshoot, a hop, stars, lid pop). Reduced motion: flop only, no roll |
| Crowd | Rushlings closer than 0.7 m wobble away from each other (visual; pushes stay server-only) |
| Celebrations | Round result: qualified = jumping for joy with arms up and a happy face; eliminated = slumped (pitch 22°, squashed, arms hanging, sad face). Champion podium: arms-up crown pose (the podium drives the hop and the crown) |
| Emotes (1–4) | 1 wave, 2 dance (side-to-side, a full spin), 3 cheer (hops, arms up), 4 flop (belly-flop on the spot, pop up). Lobby, results, intermission, champion, receipt; relayed by the server (`ctl` Emote → `emo`, 1 per second per player) |
| Faces | Two new glyphs in the 4×4 atlas: Happy (7), Sad (8) |

**For G2 / G3 to build on (don't change the motor):** every pose is a function of the motor flags + velocity + timers in `RushlingVisuals`; restyle the meshes / materials freely, keep the node hierarchy (`Tilt` → `Squash` → parts; feet under the root; arms as world-space tubes). Natural hooks for juice: `StunStartedThisFrame`, `DiveStartedThisFrame`, the landing event (impact 0–1), stumble start, get-up pop, emote start — each wants a puff / stars / SFX in G3.

### 2.4 Customisation slots (cosmetics, no stats)

| Slot | Variety |
|---|---|
| Shell top color/pattern | Solid, two-tone, metallic, holographic |
| Shell bottom color/pattern | Same set as the top |
| Emblem decal | Lightning bolt, star, heart, skull, flame, … |
| Topper | Goggles, propeller cap, chef hat, crown (champions only) |
| Key style | Classic, star, gear, gem |
| Screen face theme | Glyph color: green, cyan, amber, pink |
| Victory pose | Plays on the champion screen |

**MVP content:**
- 8 color presets
- 6 emblems
- 4 toppers
- 3 keys
- 4 face themes

That is about 2,300 combinations from tiny assets. Each player's preset is assigned at random by the server and shown in the lobby.

---

## 3. World: Toybox Skyworks

A floating toy factory above the clouds:
- building-block platforms
- capsule-machine towers
- conveyor belts
- springboards
- spinning tops
- foam mallets
- marble-run tubes

### 3.0 The look (G4.8 soft toy look = the art direction, 2026-11-20)

The user played the G4.6 candy build and called it "too much colourful and unnatural"; G2/G3 before it was washed out (near-white). The direction is between the two, and the difference is **materiality, not hue**:

- **Big walkable surfaces are calm:** warm pastel tops (HSV S 0.12–0.28, V 0.93–0.98; never white, never grey) with a tone-on-tone pattern per section (±5 % value).
- **Strong colour lives on edges and objects:** padded rims, trim, props, hazards and Rushlings.
- **Chunky soft shapes:** rounded edges (radius 0.15–0.25 m), platforms ≥ 0.6 m thick, padded bumper rims.
- **Soft real light:** sun shadows, corner AO, a foam / vinyl sheen with broad highlights, a natural grade.
- **A soft bright sky:** hazy horizon, big cloud banks, a far stadium silhouette.

The **candy** look (G4.6 colours, light and grade) stays behind a dev switch: `?look=candy` (WebGL), `-fallrushLook candy` (desktop), **F7** in game. Both looks share one geometry set (decision G4.8-1). Proof: `docs/proof/G4.8-soft-look.md`.

**Soft look tokens** (`PaletteTokens`; on-screen colours, baked through the soft grade like G4.6-3):

| Hue | Top (pastel) | Side (mid tone, S 0.45 V 0.86) | Rim / trim (the saturated hue) | Top pattern |
|---|---|---|---|---|
| Bubblegum | `#F9D1E2` blush | `#DB79A4` | `#FF6FAE` | chevrons |
| Lemon drop | `#F9EDC2` vanilla | `#DBC679` | `#FFD43B` | stripes |
| Soda blue | `#D6EDF9` baby blue | `#79BADB` | `#4CC2FF` | wide soft stripes |
| Sour apple | `#E0F9D1` pistachio | `#9EDB79` | `#86E04E` | checks |
| Grape fizz | `#DED1F9` lavender | `#9879DB` | `#A77DFF` | polka dots |
| Tangerine | `#F9E4D1` peach cream | `#DBA879` | `#FF9A3D` | polka dots |

- **Hazards** keep hit pink + the stripe, saturated, with a gloss highlight: the loudest thing on screen. Hit pink stands ΔE ≥ 70 and Δluma ≥ 0.46 off every pastel floor. The stripe's yellow band is close to the floors in luma (≤ 0.12), so the pink band carries the read.
- **Goals, springs and spawns** stay mint + checker; **goo** stays purple + glitter.
- **Sky:** soft `#66B5F2` → `#9CD2F5` with a `#DDEFFB` haze band on the horizon. The fog is the horizon colour.
- **Light and grade:**
  - sun `#FFF3E0`, cool sky ambient `#C6DCFA`;
  - grade saturation +6, contrast +8, exposure 0, no tonemapper (decision G4.8-2);
  - bloom threshold 1.25, so only gloss highlights bloom.
- **Lid Flip:**
  - lids take the ring hues as pastels;
  - lit = a deeper amber `#FF8A00` (≥ 0.15 luma from every pastel lid, also for deuteranopia and protanopia);
  - the lower shelf's edge is its mid tone deepened 30 % toward ink.
- **Saturation budget:** pixels with S > 0.6 stay at or below 30 % of every spawn and action shot (`scripts/g48-satbudget.py`).

### 3.1 Palette (G4.6 candy palette, 2026-11-20 — now the **candy** alternative behind the look switch; tokens in `Scripts/Net/PaletteTokens.cs`, tested by `LookTests` / `PaletteTests`)

Colours below are **on-screen** colours: the map bake divides out the light and the grade (decision G4.6-3), so a lit top shows its token after the grade (measured on Editor shots, proof `docs/proof/G4.6-candy-palette.md` §3).

**Surface hues — every walkable top is coloured, one hue per section**

| Hue | Plan hue | Top token | Side token |
|---|---|---|---|
| Bubblegum | `#FF6FAE` | `#EB66A0` | `#D8327F` |
| Lemon drop | `#FFD43B` | `#EBC53D` | `#F09A12` |
| Soda blue | `#4CC2FF` | `#46B2EB` | `#1F82E0` |
| Sour apple | `#86E04E` | `#86E04E` | `#3FA82F` |
| Grape fizz | `#A77DFF` | `#9A73EB` | `#6D3FE0` |
| Tangerine | `#FF9A3D` | `#EB903D` | `#E05A1F` |

Tops sit at S 0.45–0.75, V 0.85–0.95 (the plan hues brought to V 0.92 and S ≤ 0.74, decision G4.6-1), never white; sides are the same hue (< 18°), darker in luma and at least as saturated; the band at the top of a side is the top lightened 30 %. Rails / walls take the section hue (sides a shade deeper toward ink); belts take the hue at S 0.5 / V 0.78 under the light chevrons; turntable stripes and moving-platform rims the hue darkened 35 % toward ink.

**Meaning colours — reserved, never on a walkable top (ΔE76 ≥ 15, tested on the committed scenes)**

| Meaning | Token | Notes |
|---|---|---|
| Hit (hits you) | hot pink `#FF2D8A` | always with the stripe (light band `#FFD43B`; luma contrast 0.45) |
| About to move | lemon `#FFD43B` | pulsing; the lit lids, gate / belt / chute pulses, the crown glow |
| Goal / spring / spawn | mint `#2FE0A8` | + checker; finish deck, summit, checkpoint posts, arch, spawn pads, springs |
| Goo | `#9B3CFF` | + glitter |
| Sky | `#2E9BFF` → `#A6E3FF` | skybox, fog |

**Brand / UI tokens (unchanged):** Magenta `#FF3EA5`, Sun Yellow `#FFD21F` (UI, collectibles, crowns, the Star Fountain rim lip), Grape `#7B2FF7` (UI frames; in the maps machine parts are Grape deepened toward ink `#5F25BD`), Cloud White `#FFFFFF`, ink `#2A1250`.

**Readability rules (G4.6):**
- Every walkable top carries a surface hue; one hue per section, so players know where they are.
- Meaning colours never on a walkable top. Lemon drop on at most one section per map, never beside a yellow telegraph, never on Lid Flip (today: unused). Grape fizz never on Goo Tower. Mint only on goal / spring / spawn.
- Bubblegum vs hit pink: ΔE ≥ 15 and the stripe's light band stands off a Bubblegum floor in luma (> 0.15); hazards keep the stripe.
- Hazards always striped and high-saturation; stripe luma contrast > 0.3 (colour-blind rule); on Lid Flip the lit lid differs from every solid lid by ≥ 0.15 luma, also under Deuteranopia / Protanopia (measured from shot pixels).
- Section table: `PaletteTokens.Sections` (doc 08 lists it per map).

**Light and grade:** warm sun `#FFF1DC` (1.2), cool sky ambient `#BFD9FF`, warm violet ground ambient `#C2A6D6`. Grade on both graphics levels: Neutral tonemapping, saturation +22, contrast +12, post-exposure −0.15, a ×1.6 pre-tonemap gain (colour filter); High adds bloom (threshold 1.15) and the vignette.

### 3.2 Shading & lighting (MacBook Air M2 budget)

- **URP Forward**, one directional light, **baked indirect**, light probes for characters.
- No realtime shadows except a **blob shadow decal** under each Rushling. This is a gameplay aid for judging jumps.
  - **G4.8 soft look:** URP main-light shadows: one cascade, 60 m, soft, 2048 (High) / 1024 (Low), strength 0.6.
  - Casters: map chunks, hazards, props, Rushlings, lids. Never: clouds, glass, backdrop.
  - The blob shadow stays, because it reads the landing spot.
  - The candy look casts none.
- Toon shader: a single **Shader Graph** cel ramp (2–3 bands) plus rim light plus an optional inverted-hull outline on characters only.
- Environment: vertex colors or a 256×256 gradient atlas. One material per map where possible. GPU instancing on repeated props.
- Post: Bloom (low), Color Adjustments, Vignette (subtle). No SSAO, SSR or volumetrics.

**Budgets** (initial targets, to be measured):

| Item | Budget |
|---|---|
| Visible triangles | ≤ 300k |
| Draw calls | ≤ 150 |
| Rushling | ≤ 2.5k tris (including arms) |
| Texture memory per map | ≤ 64 MB |
| Frame rate | 60 FPS at 1440×900 on M2 |

**Measured (G2/G3 final WebGL release, 1440 × 900, M2 Air, Chrome; proof §D):** FPS median 59 on every map at High and Low; 1 % low 53 (browser frame pacing, ~18.9 ms p99 frames, under the 55 floor: open item); draw calls max 122 (High) / 69 (Low), median ≤ 69; visible tris max 121k; Rushling 2,330 tris; texture memory ≈ 20.7 MB (High) / 13.7 MB (Low), estimated; build 19.03 MB.

### 3.3 Asset sourcing

| Asset | Source | License rule |
|---|---|---|
| Hero character & all Rushling parts | **Original**, modelled by us (Blender or ProBuilder) | Ours |
| Signature obstacles (mallets, spinners, capsule machine, goo) | **Original** (ProBuilder/Blender) | Ours |
| Generic props (blocks, fences, flags, clouds) | CC0 kits (e.g. Kenney, Quaternius), recolored to our palette | Verify CC0 on the kit's page and record it in `ASSET_LICENSES.md` |
| Fonts | Google Fonts under the OFL (display: a rounded heavy face such as "Lilita One" or "Baloo 2", **verified OFL**) | Record in `ASSET_LICENSES.md` |
| SFX / music | CC0 libraries (e.g. Kenney audio) plus original jingles | Same |

Every third-party file gets a row in `ASSET_LICENSES.md`: file, source URL, license, author, date. No asset enters the repo without a row.

---

## 4. Camera & game feel

**Camera:** third-person follow (Cinemachine).
- Distance 7 m, height 3 m, mouse orbit.
- Soft collision with geometry.
- Look-ahead along velocity.
- Gentle FOV kick (+6°) on dive and boost pads.

**Feel checklist** ("juice" — the impact effects):
- Coyote time 0.1 s and jump buffer 0.12 s.
- Landing: dust puff, squash, soft "boop".
- Hazard hit: hit-stop 60 ms (client-only visual), camera shake (small), shell pop, "BONK".
- Checkpoint: mint ring burst, chime, HUD tick.
- Qualify: confetti burst plus a capsule "pop" sound, then the character dances.
- Eliminated: the character turns to a grey capsule and is sucked up by a giant claw-machine claw (signature Fall Rush moment).
- Countdown: a giant capsule machine cranks "3-2-1-RUSH!"

**Signature moments to show in the trailer:**
1. Lobby capsules drop and pop open to reveal players.
2. The VRF **Chaos Capsule** machine spins and deals the round modifier (doc 01 §9).
3. The claw machine plucks eliminated players.
4. The champion is crowned and a golden **Crown Capsule** trophy pops out.

---

## 5. UI style

- Chunky rounded panels with thick Grape outlines, a slight 3D drop, and Sun Yellow / Magenta buttons with squash on press.
- HUD corners:
  - top-left: round name and modifier chip
  - top-right: timer
  - top-centre: qualified X / N
  - bottom-left: ping / connection
  - bottom-right: chain status chip
- **Chain status chip states:**
  - `● Server` (grey)
  - `◆ Rollup ✓` (mint)
  - `⬢ Solana ✓` (yellow)
  - `! Pending` (amber)

  Each state links to the explorer on the results screen.
- Fonts: rounded display font for headings, clean sans for body. Minimum body size 18 px at 1080p.

---

## 6. Audio direction

| Category | Direction |
|---|---|
| Music | Upbeat bouncy toy-pop: marimba, ukulele, claps, kazoo-like lead. One lobby loop, one race loop, one tense final loop, one victory sting. Original or CC0 only. |
| Character SFX | Plastic "clack", springy "boing", wind-up "zzzt", tiny squeaks |
| Hazards | Foam "BONK", wooden thunk, goo "blorp" |
| UI | Capsule-machine crank, coin clink, pop |
| Voice | No voice lines. Gibberish chirps per expression (pitch-randomised). |

Mix: music −14 LUFS integrated. SFX are ducked under important cues (countdown, elimination).

---

## 7. Production order (art)

1. **Greybox** all maps with ProBuilder plus the hazard-stripe material (Milestones M3–M4).
2. Bolt prototype: capsule mesh plus screen face plus noodle arms (procedural). This lands during M2, so movement feel is judged with the real silhouette.
3. Palette pass plus toon shader plus props per map (M7).
4. VFX and audio pass (M7).
5. Trailer moments (M9).

---

## 8. As built (G2 look + G3 juice, 2026-11-19)

Proof, decisions and measurements: [`docs/proof/G2G3-look-juice.md`](proof/G2G3-look-juice.md). Everything below is ours (code-generated meshes, hand-written shaders, synthesised audio); the only third-party files are three OFL fonts (`ASSET_LICENSES.md`).

### 8.1 Shading, palette, sky, lighting (G2.1)
- **`FallRush/Toon`** (`Assets/_Game/Shaders/Toon.shader`): a hand-written URP Forward HLSL shader, not Shader Graph (we build headless in batch mode; a text shader is reviewable, has only the variants we need: instancing × fog, and carries the outline as a second pass, which Shader Graph cannot do in URP without a renderer feature). 3-band cel ramp on half-Lambert from the one sun, a shade tint, rim light on the lit side, palette tint `_BaseColor × vertex colour`, fake AO baked into vertex colours, optional gloss highlight, hazard stripes (world / object space), a telegraph flash mask (vertex alpha), the belts' travelling chevron light (uv2.x), and Rushling flags in uv2.y (LED screen atlas, key spin, lid hinge, no-hull parts). SRP Batcher compatible (one `UnityPerMaterial` buffer), GPU-instancing compatible.
- **Outline:** inverted hull in the `SRPDefaultUnlit` pass, enabled only on the Rushling material (`SetShaderPassEnabled`), width 1.6 cm growing gently with distance; small parts (seam, emblem, goggles, face) and the thin noodle arms (0.6 cm) get no fat hull.
- **Palette:** `PaletteTokens` ScriptableObject (`Resources/FallRush/Palette.asset`, regenerated from the code defaults by every map build) = doc 17 §3.1 hex values (tested) + our derived tokens (ink, amber, walkable top `#F2F0FF`, pastel block sides). Rules enforced by the art pass and tested: walkable tops light and desaturated (s < 0.12, v > 0.9), hazards saturated and always striped, goals mint / checkered, no hazard stripes on safe geometry (the Goo Tower core bands were hazard yellow: now Grape), stripe luminance contrast > 0.3 (readable without hue: the colour-blind rule).
- **G4.6 candy palette (2026-11-20, proof `docs/proof/G4.6-candy-palette.md`):** the G2 rules above are replaced by §3.1: every walkable top carries its section's surface hue (S 0.45–0.75, V 0.85–0.95 measured on screen), sides the same hue darker, meaning colours reserved. Vertex colours are baked through the inverse of the grade and the map light (`MapArt.Bake`, `GraphicsQuality.AlbedoFor`): the project renders in linear space and vertex colours are not converted, which is why the G2 tops read near-white (measured `#FEFEFD`).
- **Sky:** `FallRush/SkyGradient` skybox `#3FA9F5 → #BFE6FF` with a soft warm sun glow; linear distance fog in the horizon colour (start 70–90 m); clouds are merged flattened puffs (one static mesh per map, far, below and around).
- **Lighting:** flat, **no baking** (8 GB machine, code-rebuilt maps, a toon look does not need GI): one directional sun (no realtime shadows), trilight ambient (palette sky / ground, set as shader globals by the client), the cel ramp and the vertex AO.
- **Blob shadows:** one soft disc per Rushling, raycast straight down from the capsule to the first gameplay surface (exact: where it would land), shrinking / fading with height, drawn in **one** instanced call.
- **Post (Graphics: High):** low Bloom (threshold 1.15, intensity 0.35), Colour Adjustments (+10 saturation, +8 contrast), a subtle Vignette (0.16), full render scale. **Low:** no post, the WebGL quality level's 0.8 render scale. Settings toggle, `?gfx=low|high` / `-fallrushGfx` for measurements.
- **G4.6 grade and rim:** both levels keep the grade (Neutral tonemapping — URP HDR grading mode, switched at runtime —, saturation +22, contrast +12, post-exposure −0.15, ×1.6 pre-tonemap gain); High adds bloom (threshold 1.15) + vignette, Low drops only those (Editor proxy: Low +0.14 ms over no post, WebGL UNVERIFIED until G4.7 build #1). Warm sun `#FFF1DC`, cool sky ambient, warm violet ground. Toon shader: a view rim independent of the sun on Rushlings (`_RimAll`), stripe posts and merged hit hazards (uv2.y = 4), never on up-facing faces (the old sun rim is now also off on tops, which had washed far decks white). No new keywords; SRP Batcher layout kept (two floats in `UnityPerMaterial`). Outline thickness tiers (thick on hazards, thin on props) are not done.

### 8.2 Bolt final + cosmetics (G2.2)
- One renderer per Rushling body (shell, seam, hinge, emblem, key, and the lid: dome, LED screen, topper) + one dynamic limbs mesh (noodle arms, mittens, peg feet) + the stun stars (only while stunned). The shader spins the key (`_KeyAngle`) and hinges the lid (`_LidAngle`), and draws the face from a 16-glyph, 64 px-per-cell anti-aliased atlas (idle, run, jump, dive, stunned, falling, blink, happy, sad = x x with tears, qualified ★★, champion crown, wink, angry, love ♥♥, sleepy, smug), tinted per face theme.
- **≤ 2,330 triangles** (Bolt; every preset ≤ 2.5k, tested), down from ~20 renderers to 2 (+ outline passes).
- **12 presets** (`Shared/CosmeticPresets.cs`): shell colour, emblem (bolt, star, heart, flame, diamond, moon), topper (aviator goggles, propeller cap, chef hat, headphones; the crown stays champion-only), LED colour (mint, cyan, amber, pink), key (classic, star, gear). Chosen on the menu ("My Rushling", live 3D preview), saved locally, sent in `hello` (the byte already existed), stored by the server, relayed in `match` slot rows (format v2, **protocol v6**), shown to everyone; bots get `CosmeticPresets.ForBot(slot)`. No stats.
- Silhouette test (unlit black, 20 m): capsule + noodle arms + key (+ topper) — `docs/proof/g2g3/bolt-silhouette.png`.

### 8.3 Maps (G2.3), Toybox Skyworks
All through `Editor/MapArt.cs` + `Editor/MapDressing.cs`, run by every builder before it saves (a rebuild reproduces the look; map hashes unchanged). Blocks: every render cube becomes a chamfered toy block of its own size (light top, white band, pastel side, AO toward the bottom) on one shared vertex-colour material, so static batching merges a map into a few draws; colliders untouched. Hazards: every rigid hazard group is merged into one vertex-coloured mesh (1 draw); bumpers fold their body in and are static. Signs are TextMeshPro in Lilita One with a thick Grape outline. Dressing is layer 0, no colliders, placed out of reach (islands below the kill plane, props ≥ 24 m away in the lobby, banners above the camera).
- **Lobby:** a capsule machine on the plinth (with a matching collider), four more machines and a podium on floating islands, bunting at 6.6 m, balloons, clouds below.
- **Block Party Dash:** pastel toy blocks, mint checkpoint banners at 5 m on post extensions, pennants on the finish arch, toy pillars under the decks, floating islands and balloons beside the course.
- **Goo Tower:** capsule windows and gears as flat decals on the core, a glass capsule globe and a spiral chute above the summit; glossy purple glitter goo (gloss, rim, emissive glitter in one mesh, a bright foam lip on the core walls).
- **Star Scramble:** the centre turn table as a toy record (groove rings, yellow label; light because it is walkable), islands with rounded undersides, a drum base, balloons; stars are chunky extruded stars drawn instanced: normal = sun yellow 0.65 m, golden = bigger, deeper gold, emissive, with a spinning halo.
- **Crumble Shelf:** the shelves inside a giant claw machine: glass panes only above the top shelf's jump reach, a magenta cabinet below the kill plane, a lit header; tiles keep the G1 telegraphs (shake + yellow, respawn blink).

### 8.4 UI kit, audio, VFX, signature moments (G3)
- **UI kit** (`UiKit`): chunky rounded panels (thick Grape border, a 3D drop), Sun Yellow / Magenta buttons with a lip that squash on press (`UiPress`), pills behind every HUD text (readable on any background), TextMeshPro everywhere: Lilita One (display), Varela Round (body, tabular figures), Noto Sans Symbols 2 (fallback). Settings use our own sliders and pill switches. Screen transitions: a capsule pop (white top, magenta bottom, Grape seam) on every scene load; reduced motion: a fade.
- **Audio** (`GameAudio` / `AudioDirector`): original toy-pop music synthesised by `tools/audio/synth.py` (marimba, Karplus-Strong ukulele, claps, shakers, a kazoo-like lead): lobby loop (112 bpm, C major), race loop (128 bpm, F major), tense final loop (142 bpm, A minor), victory sting; all measured at −14 LUFS (sting −13). 24 SFX + 6 UI sounds + 4 gibberish chirp moods (pitch-randomised, no words). Three buses in code (Music / SFX / UI) on pooled AudioSources wired to the saved sliders, ducking under the countdown and eliminations; nothing plays before the first click / key on WebGL.
- **VFX** (`Fx` / `FxDirector`): landing dust scaled by impact, dive streaks, belly-slide dust, hit stars + lid pop, a **visual-only 60 ms hit-stop** (the Rushling's pose holds; the sim never pauses), camera shake (off with reduced motion), FOV kick on dives and spring pads, checkpoint mint ring burst, qualify confetti, goo splash / poof on a knock-out, star sparkle, spring-pad squash, door and mallet telegraph glow (flash mask). Pooled particle systems on one material; reduced motion cuts counts and turns streaks off.
- **Signature moments:** (1) lobby capsules in the player's colours drop, bounce and pop open on their Rushling; (2) the Chaos Capsule machine (UI) cranks while the deal is pending, then pops one capsule per round; (3) the claw: eliminated Rushlings turn grey and a claw-machine claw (our design: grape hub, three magenta prongs with mint tips) plucks them out at the round result; (4) the crown ceremony: a golden Crown Capsule drops onto the champion, pops open, and our crown (a gold band with five rounded capsule prongs and a tiny wind-up key) settles on the dome, with confetti and the victory sting; (5) the countdown capsule machine cranks out 3-2-1-RUSH!.

## 9. As built (G4-B art, 2026-11-21; proof `docs/proof/G4-B-art.md`)
- **Look:** the G4.8 soft toy look is the default (§3.0); candy stays behind `?look=candy` / F7 until the look is picked.
- **Hit feedback:** a hit shows **dizzy swirls** (three ink / lavender spirals round the head) and a white bonk puff with 3–5
  white sparks; no stars and no gold on a hit (stars mean score: Star Fountain pickups / drops, the golden star, the podium).
  The §8.4 "hit stars" line is replaced by this.
  **Superseded 2026-11-24 (P7, OC-51, user pick):** a hit shows **three classic-yellow cartoon stars** (amber rim) circling the
  head through the stun and ~1.2 s after the get-up (sleepy eyes, slow woozy sway), a small chest-height puff, and only your
  own hits make a sound (boing + cartoon birds; a slide whistle when you fall off). Falling off the course no longer vanishes
  in a poof: the Rushling tumbles down with its stars until the respawn.
  **OC-52:** no white bonk burst (it hid the flopped Rushling under bloom); 4 small unlit lemon stars with a shine; tired ≈ 4 s
  (lid hangs open, key stops, slumped sway), look only.
- **Lid Flip (soft):** calm lids keep 85 % of the sun in a cast shadow, so a shaded calm lid never comes near the lit amber
  for deutan / protan eyes (≥ 0.12 luma, measured on shots).
- **Prop kit (G4.9):** 22 original capsule-toy props from one deterministic Blender script (`tools/blender/props.py`): foam
  mallet head, quilted pillow bumper, gumball with a lemon band and a shine band, foam spinner caps + hub, inflatable finish
  arch pieces, spring pad (mint + checker + coil), inflatable start arch and towers, confetti cannon, pennant, bunting,
  ribbon banner, coin slot, capsule-machine cabinet, prize star (score map only), claw, wind-up key, goo blob, bleacher
  block. ≤ 1.5k tris, vertex colours via palette slots in both looks, baked AO. A prop that replaces a hazard / platform look
  stays within 0.15 m of its collider and keeps the lemon hit stripe. Bumpers squash 12 % and wobble on a nearby hit (4 %, no
  wobble, with reduced motion).
- **Backdrop (G4.10, soft):** a hopping, swaying crowd of capsule spectators on the stadium tiers and waving banners / flags
  (one mesh, animated in its shader), inflatable towers, a cloud sea far below the course.
- **Camera (§4, G4.10):** presets in `CameraRig`: `close` (default: 6.5 m, 15°, look-at 0.75 m over the 0.8 m target, FOV 55°;
  a Rushling ≈ 14 % of the screen height) and `classic` (the G1 camera: 6.6 m, 22.5°, 0.4 m); `?cam=classic|close`.
- **Bolt:** unchanged procedural Bolt (§2.3.1). The rigged Bolt (G4.11) was cut (scope / risk).
- **Grade in builds (G4.12):** the URP assets must save HDR colour grading; with LDR on disk a player build strips the HDR
  grade variants and every build since G4.6 showed blown, oversaturated colours that the Editor shots never showed. Judge looks
  in a build (Mac player `scripts/g2-shots.sh` or WebGL), not only in Editor shots.
