# 20 · Launch Trailer Plan

> Status: **plan only** (2026-11-22). Nothing has been captured or built yet. This is a separate piece from the 3:00 hackathon demo video in doc 19 §7. It is a ~75 s hype trailer that ends on **Join the Waitlist**. The two pieces can share footage.

---

## 1. Concept

**Working title:** *"Only One Gets the Crown."*

**The idea:** cut a silly toy game like a serious blockbuster trailer. The trailer uses slow, beautiful wide shots, heavy title cards, a single song that carries everything, and dramatic silences. All of it is spent on wind-up capsule toys with noodle arms bonking each other off shelves. The contrast between the serious edit and the silly game is the hook. The knockouts are the punchlines, and each one lands on a drum hit.

**Story:** we follow **Bolt**, the canonical Rushling. Bolt pops out of a capsule, survives all four rounds while everyone else gets knocked out around them, and wins the crown. Then Bolt immediately gets bonked (the post-logo gag). The countdown cards follow the real match flow: **12 → 8 → 5 → 3 → 1**.

**Originality guardrail:** we borrow trailer *grammar* (structure, pacing, cards, music-led cutting). We do **not** copy GTA VI's song, its sunset-pink palette, its typography or its shots. The palette stays our soft-toy look (pastel surfaces, coloured rims, no over-saturation; see the look feedback in doc 17).

---

## 2. What the big trailers do, and what we take from each

| Source | What it does | What we take |
|---|---|---|
| **GTA VI trailers (Rockstar)** | One song carries the whole trailer. The opening is slow and moody, with title cards on black, character-led framing and long beautiful shots. Trailer editor Derek Lieu said Trailer 2's emotional curve is *flat*: strong moments, but no rising peaks, and the music isn't "trailerized" to shift between sections. | Use one song, black-frame cards, a moody open and a hero character. **Fix the flaw:** build a rising curve of taller and taller spikes, and edit the music so every act changes energy. |
| **Fall Guys Ranked Knockout trailer** | Hard-hitting opening, then every jump and emote cut to land on the beat. | Beat-synced jumps, dives and knockouts in Act 2. |
| **Derek Lieu (trailer editor)** | Not every cut needs to hit the beat. Game trailers must still communicate the gameplay, so flashy cutting can get in the way. | Hold some shots long enough that a viewer *understands* the game: a race, then goo, then stars, then falling tiles. |
| **Steam / store best practice** (Steamworks, Presskit.gg, Unity) | Hook in 0–5 s with gameplay, not a logo. Genre and core loop clear within 15 s. Main trailer 60–90 s, plus a version under 30 s. Logo for 2–3 s at most. End on a call to action. | Gameplay inside the first 3 s, the loop is clear by 0:18, the logo comes late, and the CTA is the last 5 s. |
| **Common AAA / Nintendo convention** | A "button": one last gag *after* the logo. | Bolt gets bonked off the podium after the title card. |

---

## 3. Beat sheet (master cut, ~78 s at 120 BPM: 1 bar = 2 s, 1 beat = 0.5 s)

At 120 BPM every cut point falls on a round number, which makes the edit and the captures easy to plan.

| Time | Act | Picture | Sound | Card |
|---|---|---|---|---|
| 0:00–0:03 | **Cold open** | Black. Macro shot: the claw machine drops a capsule. It cracks open, and Bolt's eyes appear in the gap. | A wind-up key cranks: *creak… creak… creak.* Silence. | — |
| 0:03–0:10 | **World** | Slow drone wides over the Toybox Skyworks: Capsule Run, Goo Tower, Star Fountain, Lid Flip. Slow push-ins at golden-hour light. Letterboxed to 2.39:1. | A music-box melody, in the song's intro or a custom layer. | `12 ENTER.` |
| 0:10–0:18 | **Lineup** | 12 Rushlings on the start line in slow-mo, with Bolt in the centre. Close-ups of rivals squaring up. The countdown 3–2–1 lands on 3 beats. | A riser and a breath, then the **RUSH = beat drop**. The letterbox snaps open to full frame. | `RUSH` |
| 0:18–0:34 | **R1 Race** | Fast cuts on the beat (0.5–1 s): jumps, dives, belly slides. Sweeper bars and bumpers smack Rushlings off the course, **each knockout on a kick**. Speed ramps (real speed → slow-mo → real speed) on the best hits. | Full beat, with impacts and squeaks layered on the hits. | `8 SURVIVE.` |
| 0:34–0:44 | **R2 Goo Tower** | The tension dip. Glittery goo rises. Long low angles. A rival slips into the goo in slow-mo, and Bolt barely makes a jump. | The music goes low-pass (muffled), with a heartbeat and goo bubbling. | `5 CLIMB.` |
| 0:44–0:54 | **R3 Star Fountain** | The chaos peak. Whip pans, and stars popping off on knockbacks. **Freeze-frame + name tag + "OUT" stamp** on two comedic knockouts. | The music comes back in full, plus a star "ding" on each pickup. | `3 REMAIN.` |
| 0:54–1:02 | **R4 Lid Flip** | Lids light yellow and swing open. Cuts accelerate (1 s → 0.5 s → 0.25 s). Three left, then two. | A long riser. | — |
| 1:02–1:03 | **The Stop** | **Hard cut to black.** | **Total silence** (0.5–1 s). This is the trailer's biggest moment. | — |
| 1:03–1:07 | **The Win** | One slow-mo shot: Bolt dives across a lid as it swings open as the last rival drops past the camera. The crown lands. | The final hit and the song's climax. | — |
| 1:07–1:10 | **Title** | The FALL RUSH logo animates in (≤ 3 s). | A big sub hit, then the reverb tail. | `ONLY ONE GETS THE CROWN.` |
| 1:10–1:13 | **Button** | Bolt celebrates on the podium, and a sweeper bar bonks them off-screen. | One toy squeak, comedic timing. | — |
| 1:13–1:18 | **CTA** | End card on the soft-toy background: **JOIN THE WAITLIST**, the URL or QR, and a small "Built on Solana · MagicBlock". | The music button or ending. | `JOIN THE WAITLIST` |

**Energy curve:** low (open) → medium (world) → spike (drop) → high (race) → **dip** (goo) → higher (stars) → highest (final) → **zero** (silence) → max (win) → release (logo, gag). Each peak is taller than the last, which is the fix for the GTA VI Trailer 2 critique.

---

## 4. Edit toolbox (the "different types of edits")

- **Beat cuts**: on kicks and snares for action. Leave sections off the grid where the viewer needs to read the game.
- **Speed ramps**: real speed → 20–25 % → real speed on impacts. This needs 60 fps capture, and ideally 120 fps for clean slow-mo.
- **Freeze frame + tag**: freeze on a knockout, add a name tag ("RUSHLING #7"), slam on an "OUT" stamp, then unfreeze.
- **Match cuts**: capsule crack → goo bubble pop; spinning claw → spinning star; Bolt's eye → the crown.
- **Whip pans / smash cuts**: transitions between maps in the chaos sections.
- **Letterbox open**: 2.39:1 during the "cinematic" sections, snapping to full 16:9 on the drop. This signals "movie → game".
- **Hard silence + black**: used once, right before the win.
- **Low-pass dip**: muffles the music during Goo Tower for tension.
- **Punch-in zoom / camera shake** on the biggest hits.
- **Counter cards**: 12 → 8 → 5 → 3 → 1 as heavy cards on black. These tell the knockout format without any explanation.
- **J/L cuts**: the next scene's sound starts before its picture does (the wind-up crank under the first frame).

---

## 5. Shot list and how to capture it

| # | Shot | Map | Camera | Needs |
|---|---|---|---|---|
| 1 | Claw drops the capsule, capsule cracks, Bolt's eyes | Lobby / capsule machine | Macro, locked | Capsule-pop animation, free camera |
| 2–5 | Drone wide of each map | All 4 | Slow spline dolly | Cinematic camera, no HUD, golden light |
| 6 | 12 on the start line, Bolt centre | Capsule Run | Low tracking, slow-mo | Bots placed, Bolt skin pinned to a slot |
| 7 | Rival close-ups (×3) | Capsule Run | Tight | Emotes / idle |
| 8–15 | Jumps, dives, sweeper knockouts (×8+) | Capsule Run | Chase cam + side angles | Bots + hazards, 60/120 fps |
| 16–19 | Goo rising, slip into goo, Bolt's jump | Goo Tower | Low, looking up | Slow-mo |
| 20–25 | Star pickups, knockback star pops, 2 comedic knockouts | Star Fountain | Whip-friendly wide | Knockback reliability |
| 26–30 | Lids lighting and swinging open, 3 → 2 left | Lid Flip | Overhead + low | Bots |
| 31 | **Hero:** Bolt dives as the last rival falls past the lens | Lid Flip | Slow-mo, low | Scripted; many takes |
| 32 | Crown lands | Champion scene | Locked | Crown ceremony |
| 33 | Podium bonk gag | Champion / Sandbox | Locked wide | Scripted sweeper |

**Rule:** capture **3–5× more footage than you use**. AAA studios often spend about a month on capture alone (Derek Lieu). We have days, so capture smart: scripted bots plus repeatable takes.

---

## 6. What we have to build in the game first ("Trailer Mode")

The project has **Cinemachine 3.1.7** and a spectator camera (Tab), but no free camera, HUD toggle, slow-mo or recorder. To capture the shots we need:

1. **HUD-off toggle**: hides the HUD, nameplates and the F3 overlay.
2. **Free / dolly camera**: a Cinemachine spline dolly for the drone wides, plus a free-fly camera for the macro shots.
3. **Offline capture match**: a local practice match with bots (no server), so we can control time:
   - `Time.timeScale` slow-mo for capture;
   - frame-stepped rendering for a locked 60/120 fps.
4. **Scripted moments**: seed and bot overrides so a specific knockout happens on cue (the podium bonk, the hero dive).
5. **Bolt hero skin pinned** to one slot.
6. **Capture path**, one of two options:
   - (a) **Unity Recorder**: a new package, so it **needs your OK**.
   - (b) OBS or macOS screen recording from a **player build** at 1080p/60.
   - ⚠️ We have been bitten before: the Editor hid player-only render bugs (G4.6–G4.12). Whichever path we use, check that the captured look matches the WebGL build.
7. **Camera-shake / impact hooks** as an optional extra, so in-game hits feel bigger (screen shake can also be added in the edit).

Estimate: about 1 day of engine work and 1 day of capture. This is unverified and depends on how cleanly the offline sim takes `timeScale`.

---

## 7. Music and sound

- **No copyrighted songs** (no GTA tracks, no charting songs). They get Content-ID strikes on YouTube and X, and they're a legal risk.
- **The ideal track structure:** a quiet intro → a build → a **drop** → a breakdown → a riser → a **stop** → a final hit → an ending. If the track doesn't have these, we "trailerize" it: cut and rearrange its sections to fit the beat sheet (this is exactly what Lieu says GTA VI didn't do).
- **Sources, best first:**
  1. A licensed trailer-music library such as Artlist or Epidemic Sound (subscription; clear social licence);
  2. A commissioned track from a composer on Fiverr / SoundBetter;
  3. A generated track (check the generator's commercial terms).
- **Genre options:** playful hybrid (music box → trap/phonk drop), or orchestral-epic played completely straight. The second is funnier against toy footage.
- **SFX list:** wind-up key crank, capsule crack/pop, toy squeaks, rubbery bonks, whooshes for whip pans, sub drops, risers, heartbeat, goo bubbles, star dings, a crowd "ooooh", a crown chime.
- **Mix rule:** the music ducks under the big SFX hits. The final silence means *real* silence (no room tone).

---

## 8. Deliverables

| Asset | Format | Use |
|---|---|---|
| Master trailer | 16:9, 1080p (4K if capture allows), 60 fps, ~75 s | YouTube, landing page, pitch |
| Vertical cut | 9:16, 25–30 s (hook → race knockouts → final → CTA) | X, TikTok, Reels, Shorts |
| Bumper | 6 s (one knockout + logo + CTA) | Ads, replies, quote-tweets |
| Thumbnail / poster | 1280×720 + 1080×1350 | YouTube, X card |
| GIFs | 3–5 s knockout loops | Social replies, README |

---

## 9. Skills, tools and things still needed

**Skills (people or roles):**

| Role | Why |
|---|---|
| In-engine cinematographer | Camera paths, framing, lighting the wides |
| Trailer editor | Rhythm, speed ramps, the energy curve: the single most important skill |
| Sound designer / mixer | SFX layering, ducking, the silence moment |
| Motion designer | Counter cards, title animation, end card, "OUT" stamps |
| Copywriter | 6–8 words of cards in total; every word counts |
| Colourist (light) | One consistent soft-toy grade across all maps |

**Software (all free or already available):**

- **DaVinci Resolve (free)** for the main edit, speed ramps, colour and Fairlight audio. It runs on the M2 8 GB if we edit 1080p proxies (optimized media) and close Unity while editing.
- **CapCut** for the fast vertical cut and auto-captions.
- **OBS** if we don't add Unity Recorder.
- **Claude skills in this environment:**
  - `hyperframes` / `motion-graphics` for title cards, counter cards and the end card;
  - `music-to-video` + `hyperframes beats` for beat detection and beat-grid assembly;
  - `hyperframes-keyframes` for punch-ins;
  - `media-use` for SFX/BGM sourcing and generation.
  - `crypto-teaser` and `promo-video` exist too, but their house styles (noir black / SaaS) don't fit a soft-toy party game. Use them only for the end card's "on Solana" lockup.

**Things that don't exist yet:**

1. **A waitlist page + URL.** The M8 Next.js site was dropped (OC-8). We need a landing page with an email form (the Vite web app, or a simple hosted form) **before** the end card can be finalized.
2. **Trailer Mode** in the client (§6).
3. **A music track** (§7) and **an SFX pack**.
4. **A logo animation**: currently a static PNG (`Fall Rush  Logo.png`).
5. A **final decision on timing**: before the 2026-11-27 hackathon deadline, or after it as the public launch.

---

## 10. Proposed schedule (if done before the hackathon deadline)

| Day | Work |
|---|---|
| D1 | Trailer Mode in the client (§6), pick the music, waitlist page up |
| D2 | Capture all 33 shots (+ alternates); the demo video (doc 19 §7) reuses these |
| D3 | Assembly edit on the beat grid, cards, sound pass |
| D4 | Polish, vertical cut + bumper, thumbnail, export, test on a phone |

## 11. Open decisions (user)

1. Music: a library licence, a commission, or generated?
2. Waitlist: which platform / URL?
3. When: before the 2026-11-27 deadline, or as a post-hackathon launch?
4. Voice-over: none (recommended; cards only) or a narrator?
5. Who edits: you in Resolve with me building the cards/graphics, or me assembling the whole thing in HyperFrames from your captures?
6. Add the Unity Recorder package (new dependency), or capture with OBS?

---

### Sources
- Aftermath, Derek Lieu on the GTA VI trailers: https://aftermath.site/grand-theft-auto-vi-trailer-analysis-details-rockstar
- Nerdist, GTA VI trailer breakdown: https://nerdist.com/article/grand-theft-auto-vi-launch-trailer-breakdown/
- Derek Lieu, "More trailer editing is not better trailer editing": https://derek-lieu-yrd2.squarespace.com/blog/2023/4/23/more-trailer-editing-is-not-better-trailer-editing
- The Source, how the Fall Guys Knockout trailer was cut: https://thesource.com/?p=775547
- PC Gamer, the art of making effective game trailers: https://www.pcgamer.com/the-art-of-making-effective-game-trailers
- Steamworks trailer docs: https://partner.steamgames.com/doc/store/trailer
- Presskit.gg trailer guide: https://presskit.gg/field-guides/game-trailers-screenshots-guide
- Unity, release killer game trailers: https://unity3d.com/how-to/release-killer-game-trailers
- Game Developer, worst trailer mistakes: https://gamedeveloper.com/business/worst-trailer-mistakes
