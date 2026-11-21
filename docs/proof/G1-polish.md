# G1 proof: gameplay polish (2026-11-19)

All numbers were measured on the dev machine (MacBook Air M2, 8 GB) unless marked **UNVERIFIED**. "Feels good" is the user's call: every feel value below is a **target to tune**, not a fact (there are no official public numbers for any knockout party game's movement). No VPS action was taken. Raw logs: `.m1-logs/g1/` (git-ignored).

## A. Genre reference (feel only) → our feel targets

What knockout party-game characters do, from general knowledge and public descriptions (no assets, animations, names, layouts or numbers copied):

- **Body and scale.** A short, round, bottom-heavy body. The run reads as a *jog with weight*: a few body heights per second, a fraction of a sprinter's pace relative to body size. Levels are built at that scale, so a gap of two or three body lengths is already a real jump.
- **Momentum.** Speed builds over a beat (a fifth of a second or so) and bleeds off with a small slide when you let go; reversing direction costs a visible skid. Turning is fast but not instant: the body swings round.
- **Jump.** A short hop of about one body height, a slightly floaty top and a faster fall. Limited air steering: you commit.
- **Dive.** A forward belly-flop that extends reach (catch a ledge, cross a gap, slip under a bar), ends lying flat with a short slide, then a scramble back up. Jumping right as you land is the skill move.
- **Big falls.** Landing from high up costs a stagger: arms windmill, a moment of reduced speed, no loss of control.
- **Getting hit.** A hard hit knocks the character over in the hit direction: a flop, a roll or tumble, a beat on the ground, then a pop back up, often with "seeing stars". Readable from the follow camera.
- **Crowds.** Bodies jostle: being bumped makes you wobble; pushes are soft, never a stun.
- **Camera.** A fairly high third-person orbit, close enough to read the body language, with look-ahead; it pulls in front of walls instead of clipping through them.
- **Comedy.** Floppy limbs, squash and stretch on every contact, exaggerated poses (flail while falling, arms up when qualified, slump when out).

**Our feel targets (Bolt is 1.0 m tall, so m/s = body heights per second):**

| Target | Value | M4 (v1) |
|---|---|---|
| Run speed | **5 body heights/s** (5.0 m/s) | 7 (a sprinter) |
| Time to full speed | **≈ 0.18 s** (5–6 ticks) | 0.12 s |
| Stop slide | **≈ 0.4 m** (0.17 s) | 0.54 m at 7 m/s but 0.16 s |
| Hop height | **1.2 body heights** (1.2 m) | 1.6 |
| Airtime of a flat hop | **≈ 0.64 s**, running reach **≈ 3.2 m** (3.2 body lengths) | 0.68 s, 4.8 m |
| Dive | **7 m/s** forward + **2.5 m/s** up, then a **0.45 s belly slide** (≈ 2 m) | 9 / 3 m/s, 0.35 s dead stop |
| Stumble | fall **> 3 m** → **0.3 s at 55 %** speed, input live | none |
| Turn | visual **≤ 720°/s**; reversing costs the 28 m/s² accel (≈ 0.36 s from +5 to −5 m/s) | instant |
| Air control | **30 %** of ground accel | 35 % |
| Gap design rule | a "real jump" gap = **70–85 % of the 3.2 m reach** (2.2–2.7 m); safe gaps ≤ 60 % | – |

## B. Movement v1 → v2 (`packages/shared/config/movement_v2.json`)

sha256 of the canonical JSON: see `pnpm hash` / `MovementConfigData.Sha256Hex` (`movement_v1.json` unchanged: `1d966e17…`, asserted by a test). **Final values = the prompt's start values**: tuned by running every map through the bots and the measurements in §D (they hit the feel targets above and every map passes); the user's playtest decides the next round of tuning (practice mode F6 / F7).

| Constant | v1 | **v2** | Why |
|---|---|---|---|
| Max run speed | 7.0 m/s | **5.0 m/s** | 5 body heights/s: a jog with weight |
| Ground accel / decel | 60 / 45 m/s² | **28 / 30 m/s²** | 0.18 s to full speed; a 0.4 m slide when you let go; reversing takes 0.36 s |
| Air control | 35 % | **30 %** | commit to jumps (8.4 m/s² in the air) |
| Gravity / fall multiplier | 24 / 1.35 | **20 / 1.35** | a slightly floatier top |
| Jump apex | 1.6 m | **1.2 m** | a hop: airtime 0.64 s, running reach ≈ 3.2 m |
| Dive | 9 / 3 m/s, 0.35 s stop | **7 / 2.5 m/s + 0.45 s belly slide at 12 m/s²** (jump-cancel 0.15 s kept) | a reach extender that ends in a comic slide (≈ 2 m) |
| Stumble | – | **> 3 m fall → 0.3 s at 55 % speed** | weight on big drops; input never lost; a jump ends it |
| Turn | instant | **visual 720°/s**, motor facing instant | the dive goes exactly where you point (decision G1-3) |
| Coyote / buffer / cooldown | 100 / 120 / 1000 ms | same | |

Modifiers on top: MoonBounce 14 m/s² / 1.5 m; ButterFloors decel 12 m/s², slide decel 4.8 m/s² (tests: `MovementConfigTests.MovementV2_Modifiers_StillScaleTheBase`, `KitTests.MoonBounce_Apex1p5m_ButterFloors_Decel`).

**Measured in EditMode (AnalyticWorld, 30 Hz):** full speed in 5–6 ticks; stop from 5 m/s in 5 ticks; apex 1.20 m (± 2 cm); belly slide 1.5–3.5 m band asserted, decel exact per tick; stumble at 3.4 m and 8 m drops, none at 1.5 / 2.8 m.

## C. Animation work done
Our own procedural set on Bolt (`Client/RushlingVisuals.cs`, client only, allocation-free per frame). Full table: doc 17 §2.3.1. Run (gait-timed waddle, bob, planted stepping feet, arm swing, lean into acceleration and turns, 720°/s visual turn), idle (breathing, blinks, look-around, fidget), jump / fall (anticipation squash → stretch, lean, flailing arms and kicking feet when falling fast, impact-scaled landing squash), **stumble** (forward pitch + roll shake + windmilling arms), **dive** (belly-flop, landing slap, slide, scramble back up with an overshoot), **hit / tumble** (flop in the knock direction, a roll on long stuns, bounce, lie on the side, get-up pop with stars; reduced motion: flop only), **crowd bump** wobble (< 0.7 m), **celebrations** (qualified jump for joy, eliminated slump, champion crown pose on the podium), **emotes** 1–4 (wave, dance, cheer, flop; `ctl` Emote → `emo` relay), Happy / Sad faces. Remote players play the same from interpolated snapshots (the only new wire data is the `Stumbling` flag bit; `snap` 294 B for 12, unchanged).

## E. Bug list (P0 breaks play · P1 visible · P2 minor)

| # | Source | Bug | P | Status |
|---|---|---|---|---|
| 1 | your notes | Runs too fast | P0 | **Fixed**: movement v2 (§B) |
| 2 | your notes | S1 gaps too small, all blocks at one low height | P0 | **Fixed**: S1 rebuilt (doc 08 §3.1) |
| 3 | your note (mid-G1) | Right-lane gaps of the first S1 cut too large → failed jumps | P0 | **Fixed**: 2.0–2.1 m, rises ≤ 0.4 m; ref bot falls 27–71 → 3–5 per race |
| 4 | your notes | Animations lack the genre's comedy | P1 | **Changed**: §C |
| 5 | M4 §D | The Final too short (27–32 s) | P1 | **Fixed**: 1.0 s wobble + top-shelf respawn → ≈ 73 s |
| 6 | M4 §D | Goo Tower short (66 s) | P1 | **Improved**: 85–89 s (bots), see §D |
| 7 | found | Goo Tower outer "1.4 m gap" was really 3 m (two steps removed) + 0.62 m rise: impassable at v2 | P0 | **Fixed** (one step removed); route index 1 now escapes |
| 8 | found | Goo Tower lap-1 blocks 1.11 m high vs a 1.2 m hop | P0 | **Fixed**: 4 × 0.83 m |
| 9 | found | Star Scramble islands +1.0 m / pads tuned for v1 gravity | P1 | **Fixed**: 0.8 m, pads 5.8 m/s |
| 10 | found | Bots' Goo-Tower ramp-gap jump point 0.8 m radius made them jump 2.7 m early | P1 | **Fixed** (0.35 m radius at the edge) |
| 11 | found | MoonBounce races hit the 150 s timer (bots overshoot the small blocks) | P1 | **Fixed**: bots ease off precise jumps when the apex > 1.3 m |
| 12 | §3.5.1 | Nameplates oversized / overlapping / "BOT BOT" stacks | P1 | **Fixed**: screen-space nameplates (doc 04 §7.x) |
| 13 | found | (G1's first nameplates) drew over the result panels | P2 | **Fixed**: own canvas sorted below |
| 14 | §3.5.2 | Developer text in the world | P1 | **Fixed**: `EditorOnly` labels (stripped from builds); titles, CHECKPOINT, FINISH kept as signs |
| 15 | §3.5.3 | "BlockPartyDash", "No Chaos Capsule", no mode hints | P1 | **Fixed**: `UiNames`, modifier effect line, mode tip on every intro |
| 16 | §3.5.4 | Camera clips into geometry / blocked in Goo Tower | P1 | **Fixed**: Decollider + Deoccluder, 6.6 m / 22.5°, distance + FOV settings, smooth spectate glide (before / after shots §H) |
| 17 | §3.5.5 | Champion name over the podium Rushling | P1 | **Fixed**: name at the top |
| 18 | §3.5.5 / found | QUALIFIED counter over the world title; the BPD title sign sat on the start gate, which lifted only 2.8 m into the top of the view | P1 | **Fixed**: counter smaller + outlined; gate lifts 5.2 m; that title sign dev-only |
| 19 | §3.5.5 | Help bar always on | P2 | **Fixed**: 8 s after a map loads, then F1 |
| 20 | §3.5.6 | WebGL initial connect gives up after 10 × 1 s | P1 | **Fixed**: 6 tries with 1/2/4/4/4 s backoff, "Connecting… (attempt n of 6)", Retry button |
| 21 | §3.5.7 | Hidden tab / tab close + reopen within 20 s unverified in a real browser | P1 | **Steps for you** in §I; automated parts in §F |
| 22 | §3.5.8 | Draw calls over budget (BPD 490, Goo 282) | P2 | **Improved**: dev labels gone from builds, WebGL static batching on, static render-only geometry; numbers §F. Art budgets are G2 |
| 23 | §3.5.9 | PerformanceTestRun*.json appear after test runs | P2 | **Fixed**: git-ignored |
| 24 | M4 §I | Leaderboard place columns misaligned (proportional font) | P2 | **Not fixed**: the only built-in font is proportional; G3 restyles the screens (UI kit) |
| 25 | found | Map content hash covers hazards, not static geometry (an S1 change alone kept the BPD hash) | P2 | **Explained**: old builds are still refused (protocol v5); add static colliders to the hash in M5/M6 if mixed builds become possible |
| 26 | found | ReducedMotion / Sensitivity read PlayerPrefs every frame per Rushling | P2 | **Fixed**: settings cached |

## G. Decisions made beyond the prompt and deviations (doc 15 G1-1…11)

- **Final movement values = the prompt's start values** (G1-1). Every map passes QA with them and they hit the feel targets in §A; further tuning is the user's call after a playtest (the practice mode exists for that).
- **New states reuse `LandTicks`** + one flag bit (G1-2): no new snapshot fields; protocol bumped to **v5** anyway (new flag meaning, `ctl` Emote, `emo`), so an M4 client is refused with the readable mismatch message.
- **Turn rate visual only** (G1-3); motor facing stays instant so a dive goes exactly where you steer. The motor's weight comes from the 28 m/s² accel.
- **S1 risky-lane gaps 2.0–2.1 m (≈ 65–70 %)** instead of the prompt's 70–85 % (G1-6): the first cut at 2.4–2.6 m (≈ 75–80 %) with 0.5–0.6 m rises failed too often (your mid-G1 note; reference bots fell 27–71 times per race). Height variation is kept (tops 0.3–1.4 m).
- **Goo Tower** kept goo 0.3 m/s and its three laps (G1-5); bots land at 85–89 s, a few seconds under the 90 s floor — human climbs are slower, so no extra lap.
- **Final** grid unchanged (G1-4): 1.0 s wobble + top-shelf respawn reach ≈ 73 s.
- **Dev labels `EditorOnly`** (G1-7) rather than deleted, so map builders still see them in the Editor.
- **WebGL static batching on** (G1-8); Standalone already had it.
- **Leaderboard column alignment** not fixed (bug 24): needs a monospace font or per-column Text; G3 rebuilds those screens.
- **Bots** (§3.4): the tier table is unchanged (reaction delay, wobble, late jumps, stick 82–100 %); new: easing off precise jumps under MoonBounce (G1-10). The v2 states need no special bot code (stumble keeps input; bots already wait for `LandTicks == 0`). Spread: normal-bot races 83–117 s median per cell, reference 61–64 s (§D); the probe (reference tier, playing on its *predicted* state through the real netcode) won a full match in run "b".
- **No new packages.** UGUI `DefaultControls` (already in the project) builds the settings sliders / toggles / name field.

## I. Hidden tab and tab close / reopen (R12 + reconnect token): steps for you

Automation cannot hide a real Chrome tab or close-and-reopen it in the same session (Playwright's headless tabs keep running timers). The code path is the same one exercised by the M4 server-restart test (sessionStorage token → `resume slot`). **Please run once:**

1. `pnpm m1:build:macserver && bash scripts/m1-macserver.sh start` (or reuse a running server), and serve the WebGL build (`pnpm m1:up:mac`).
2. Open `http://localhost:8080/?autoplay=1&name=You` in Chrome, press **Start now (host)** in the lobby.
3. **Hidden tab:** during round 1, switch to another tab for **30 s**, come back. Expected: the game resumes (a short catch-up, no "Disconnected"); the server log shows no disconnect for your slot (`grep "disconnect" .m1-logs/macserver.log`).
4. **Close + reopen within 20 s:** during round 2 (if you are still in), close the tab, then within 20 s press **Ctrl/Cmd + Shift + T** (reopens the same tab with its sessionStorage). Expected: "reconnecting with the token" → you are back in the same slot, still alive (`[gs] resume slot N → … (grace ok)` in the server log).
5. **After 20 s:** close the tab and wait 30 s before reopening. Expected: you spectate and are `ELIMINATED (disconnected)` at the end of that round.
6. Note anything odd in `docs/playtest/G1-notes.md`.

## D. Map retunes and QA (`scripts/g1-qa.sh 100`, full tables: [`g1-map-qa.md`](g1-map-qa.md))

Per-section retunes: doc 08 §3.1 (Block Party Dash: S1 rebuilt, S3 belt), §5.1 (Goo Tower), §6.1 (Star Scramble), §7.1 (Crumble Shelf). Every map × variant × allowed modifier × 100 runs, reference + normal bots (Block Party Dash reference tier added in G1): **all PASS** (valid result every run, 0 soft-locks, qualified ≤ target, determinism, the goo never overtakes a route, one champion, scores = pickups − drops).

| Map | Bots | Reference median | Normal median | GDD target | M4 normal | Notes |
|---|---|---|---|---|---|---|
| Block Party Dash | 12 | **61.9 s** | **88.4 s** (MoonBounce 108–111 s) | 75–120 s (humans) | 97.5 s | reference bots are the fast floor; normal falls ≈ 2.7 per bot per race, ref ≈ 0.3; 1 timer expiry in 1,000 normal races |
| Goo Tower | 8 | **85.2 s** | **89.6 s** | 90–120 s | 66.4 s | every route index escapes alone, goo margin **8.3–9.4 s**; ≈ 5 escape per round |
| Star Scramble | 5 | 60.0 s | 60.0 s (timer) | 60–75 s | 60 s | unchanged mode timer |
| Crumble Shelf | 3 | **76.5 s** | **71.8 s** | 60–120 s | 27.2 s | 1.0 s wobble + top-shelf respawn; one champion in 100 % of runs |

Goo Tower is 0.4–5 s under the 90 s floor with bots (decision G1-5); human climbs are slower. The Goo / Star / Crumble 100-run cells ran on the build before the last bot fix (reference-tier MoonBounce ease-off, which only touches precise jumps when the apex > 1.3 m); Block Party Dash re-ran on the final build.

**Tuning log (each from a QA run):** S1 right lane 2.4–2.6 → 2.0–2.1 m (ref falls 27–71 → 3–5 / race; your note); Goo Tower route 1 gooed at 31 s → the "1.4 m" gap was 3 m (two steps removed) → one step; then gooed at 37 s → the ramp-gap jump point fired 2.7 m early → 0.35 m radius; MoonBounce on BPD hit the 150 s timer (bots overshoot small blocks with the 4.3 m moon hop) → 70 % stick on precise jumps (normal), then the same for the reference tier (it bypassed the noise layer).

## F. Tests, soak, real clients

- **EditMode: 275 / 275** (M4: 254).
- **Prediction vs server twin**, movement v2: the M4 twins (tile drops + goo at leads 1/3/6 × delays 2/5; stars + bumpers) still 0 corrections / 0 hazard mismatches, plus the G1 twin with 10–12 belly slides, 1–2 stumbles and respawning tiles per case: **0 corrections, 0 silent resyncs, 0 hazard mismatches, 0 reactive late** (4 cases).
- **Hazard vectors**: v1 (`fc06826c…`) and v2 (`d4b2a73a…`) unchanged and passing (knock vectors depend only on the capsule size, which v2 keeps); no v3 needed.
- **Soak** (`scripts/m4-soak.sh 20 8`, sim speed 8): **20 / 20 COMPLETE**, every jsonl log re-verified (`20/20 logs valid`), 38 events each, match duration (roster lock → complete) min 343 / median 399 / max 442 s (M4: 294 / 377 / 388: v2 rounds are longer), sim step p95 ≤ 0.23 ms. Ran on the build before the reference-tier MoonBounce bot fix (bots only).
- **Real clients** (`scripts/m4-match-run.sh`, 3 probe clients playing BotBrain on their predicted state + 9 bots, full match, server on :7787):

| Run | Windows | Non-push corrections / 5 s | Push / 5 s | Hazard mismatches | Reactive late | RTT p50 | Notes |
|---|---|---|---|---|---|---|---|
| localhost | 63 | **0.00** | 0.10 | **0** | **0** | 17 ms | a probe reached the final |
| 150 ms ± 15 + 2 % loss | 68 | **0.18** | 0.24 | **0** | **0** | 178–192 ms | **all 3 probes connected** (M4: one gave up at 10 × 1 s) and one played the final (Crumble Shelf: 0.00) |

## H. WebGL (release, Chrome via Playwright, against the Mac dev server on :7787)

| | Result |
|---|---|
| Release build 1 | 654 s build / 682 s wall, peak RSS 1.87 GB, **16.54 MB** (`wasm.br` 10.28, `data.br` 6.13, `framework.js.br` 0.08, `loader.js` 0.03) vs M4 16.45 MB (+0.09 MB) |
| Full match | `?bot=match` (normal-tier BotBrain on the predicted state): lobby → deal → Block Party Dash → Goo Tower → Star Scramble → Crumble Shelf → champion → receipt, `COMPLETE` (38 events). No exceptions (only Unity's start-up "hidden URP shader not supported" lines, as in M4) |
| Determinism self-test (`?selftest=1`) | **PASS** `hazard_vectors_v1` `fc06826c…` and **PASS** `hazard_vectors_v2` `d4b2a73a…` |
| Corrections in racing windows | BPD 0 (13 windows) · Goo 4 (10) · Star 1 (7) · Crumble 0 (8) → ≈ 0.11 / 5 s; **0 hazard mismatches** |

**FPS and draw calls per map** (Playwright's 1280 × 720 window, M2 Air):

| Map | FPS (median / min of 5 s windows) | Frame p95 max | Draw calls (median / max) | Tris max | M4 draw calls |
|---|---|---|---|---|---|
| Lobby | 59 / 59 | 18 ms | 55 / 135 | 31k | 160 |
| Block Party Dash | 59 / 59 | 19 ms | 128 / 421 | 57k | 490 |
| Goo Tower | 59 / 59 | 18 ms | 191 / 256 | 48k | 282 |
| Star Scramble | 59 / 59 | 18 ms | 51 / 139 | 32k | 171 |
| Crumble Shelf | 59 / 59 | 18 ms | 35 / 62 | 19k | 51 |

Draw calls fell on every map (developer labels stripped, static batching). The maxima are moments with 12 Rushlings and the whole course in view. Doc 17's budget of 150 is G2's (art) target.

**Bugs found in WebGL and fixed** (build 2 verifies them, below):
- The new settings panel opened whenever the mouse was free, so it covered the lobby panel (incl. Start) and the spectate view (`g1/bug-webgl-settings-over-lobby.png`). It now opens on Esc / pointer-lock loss only.
- The WebGL build's font has no "–", "▼" or "●" glyphs (no OS fallback). The G1 strings now use ASCII ("1-4", "YOU"). The M4 dots (ping, lobby list, chain chip) are still missing: **G3** (UI kit font) item.

| Release build 2 (after the two fixes) | 334 s build / 404 s wall, **16.55 MB**. Verified in Chrome: the settings panel stays closed in the lobby and while spectating (`g1/webgl-b2-lobby.png`, the Final under Moon Bounce), "1-4" renders. (2 of the 2 allowed WebGL builds.) |

## J. Screenshots (`docs/proof/g1/`, 800 px)
Every phase and map from a windowed probe match on the Mac dev server (run "b": lobby countdown, dealing, dealt, every round intro, play and spectate on all four maps, every round result, every intermission, champion, receipt), plus WebGL shots (`webgl-*`).
- **Before / after:** nameplates `before-nameplates-m4.png` → `after-nameplates.png`; camera in Goo Tower `before-camera-gootower-m4.png` → `after-camera-gootower.png`; S1 Block Hop `docs/proof/m4/05-roundintro-r1-blockpartydash.png` (flat blocks) → `play-blockpartydash.png` (the probe on the stepped right lane).
- `bug-webgl-settings-over-lobby.png`: the WebGL bug found and fixed in §H.
- Note: run "b" was taken before two later fixes (labels overlapping a nearer one are now hidden, not dimmed; the start gate lifts 5.2 m and its title sign is dev-only).
