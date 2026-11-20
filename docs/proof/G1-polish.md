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
