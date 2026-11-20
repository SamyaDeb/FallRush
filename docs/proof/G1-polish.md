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
