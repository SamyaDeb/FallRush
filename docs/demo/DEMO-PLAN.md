# Fall Rush: the demo video (final plan, v4, 4:00)

> Locked 2026-11-26 ~14:30 UTC after two rounds of multi-agent roasts. Round 1 used a MagicBlock judge, a pitch coach, a video producer and a fact-checker. Round 2 used a MagicBlock judge, a motion designer and a script doctor. Superseded drafts: `DEMO-PLAN-v1-draft.md`, `DEMO-PLAN-v3-draft.md`.
> **Length 4:00**, with no limit in the rules (the build.magicblock.app form has a single "Pitch & Demo" field).
> - The round-2 judge argued that 3:00–3:15 beats 4:00 *if* the extra minute is repetition. This plan uses the minute only for **new** proof and the beta players, never to restate.
> - We also export a **3:15 cut** from the same edit (§9), and you choose which to submit.
>
> **1920×1080, 60 fps, captions burned in.** Your part: the voice-over (VO) plus 30–40 s of beta gameplay. Claude does everything else.
> **Recording sheet for the VO:** [`VO-RECORDING-SHEET.md`](VO-RECORDING-SHEET.md).

---

## 1. The idea

**Thesis:** *"The game isn't next to the chain. The game **is** the program."*

**Judging:** creativity, technical depth, and how compellingly the video shows what's possible on Solana. The track is Entertainment Finance.

| Act | Time | What the judge feels |
|---|---|---|
| Hook | 0:00–0:12 | "Wait, that's on-chain?" |
| Problem → proof | 0:12–1:20 | "They really did put the physics in a program." This is the moment people will talk about: press Space, and the rollup's account changes, **by 0:40**. |
| Finance + people | 1:20–2:48 | "It's fun, people bet and sabotage, and real players love it." |
| Depth + trust | 2:48–3:45 | "Things only MagicBlock makes possible, and I can verify it." |
| CTA | 3:45–4:00 | "I'm going to play it." |

---

## 2. Visual system: clean, not slop

### Type: Poppins only (SIL OFL, Google Fonts)
The logo is the only non-Poppins element.

| Role | Weight / size | Line-height | Tracking |
|---|---|---|---|
| Statement card | 600 / 104 px (support line 500 / 40, ink 55%) | 1.04 | −0.035em |
| Big number | 600 / 180 px (unit 500 / 32, ink 55%) | 1.0 | −0.04em |
| Label pill | 500 / 30 px, height 60, padding-x 24 | 1.0 | −0.005em |
| Caption | 500 / 36 px, ≤ 42 characters per line, ≤ 2 lines | 1.3 | 0 |
| Code / tx | 500 / 26 px, `tick()` and `crank()` in the accent colour, signatures truncated `4iZa…j6YQ` at ink 70% | 1.0 | +0.02em |
| Lower-third name | 600 / 34 px (handle 400 / 24, 60%) | 1.2 | −0.01em |
| Small print | 400 / 22 px | 1.2 | +0.01em |

- Text is **sentence case everywhere**. No backticks or mono boxes.
- **Counters:**
  - Poppins has no tabular figures, so every digit sits in its own `inline-block` span, with a width equal to the measured widest digit, centred.
  - The group is right-aligned and the count-up runs for ≤ 0.8 s on `power2.out`.
  - Claude measures `"0000000000"` against `"1111111111"` before the build.

### Colour (sampled from the game, not generic crypto)
| Token | Value | Use |
|---|---|---|
| Canvas | `#F5F6FA` (cool off-white) | Full-frame cards, split-screen background, the diagram |
| Ink | `#17142A` (violet-tinted) | Text, and **our pills over gameplay at 92%** |
| Accent | HUD violet ≈ `#5B34E0` (Claude samples the exact value from a frame) | `tick()`, one highlight per frame, travelling dots |
| Check | Game mint `#12B886` with ink text | ✓, byte-exact, "settled" |
| Hairline | `#E4E5EC` | Card borders |

### Layout rules
- **The game's HUD owns all four corners.** Our text goes bottom-centre (labels at y ≈ 880, captions above them, never both at once) or in a mid-left band.
- **Over gameplay**, text always sits on an **ink pill** with a 14 px radius and white text (≥ 7:1 contrast). Never white pills (they look like the game's own UI) and never bare text.
- **Don't repeat the ◆ Rollup ✓ badge in a pill.** Ring the game's own HUD badge once with a 2 px accent outline.
- **Grid:** 8 pt grid, 96 px margins, left-aligned, ≤ 6 words per card.
- **Split screens** sit on the canvas, with a 20 px radius on every frame:
  - Game + readout: game 1152×648, gutter 24, readout 552×648.
  - Game + phone: game 1240×698, gutter 40, phone 350×760 (radius 48, 10 px ink bezel, no notch).
- **The "rollup readout" card** replaces a raw terminal: no traffic-light dots, no green-on-black.
  - A white card with a hairline border and shadow `0 12px 32px rgba(23,20,42,.08)`.
  - Header: 400 / 20 px at 50%, reading "World account · devnet-as.magicblock.app", with a static mint dot.
  - The tick count as a big number, then key/value rows (label left at 55%, value right at 600), and the player's height as a 2 px accent sparkline.
  - A row flashes at 10% accent for 150 ms when its value changes.
  - It's built in HTML from real logged data. `tools/demo/tick-watch.mjs` logs the World account (tick, ticks/s, x/y/z) from the rollup RPC with timestamps, and the card replays that log in sync with the gameplay capture. Every number on screen is real.
- **Diagram (beat 9)**, in the Stripe style rather than a flowchart:
  - Two tinted zones: the rollup at violet 6% and Solana at ink 4%, with small zone labels.
  - White node cards (500 / 28 px), no icons, ≤ 6 nodes. VRF and the Private ER are smaller, on dashed links.
  - 2 px orthogonal connectors at ink 20%, with 24 px rounded corners and one arrowhead (on "commit").
  - It builds in order: a line draws (0.5 s), then its node rises 8 px and fades in.
  - Accent dots travel the paths: inputs in, commits out. The camera drifts 1.00 → 1.04 across the beat.

### Motion grammar: three styles, nothing else
1. **Text:** a mask-reveal up (0.45 s `expo.out`), then hold. Exit is a 0.2 s fade.
2. **Hard cuts** on impacts and SFX between gameplay shots.
3. **One signature move:** the full-frame game shrinks into its split-screen slot (FLIP, 0.7 s `power3.inOut`). It's used in beats 5, 6 and 11 only.

Proof shots get **one** slow push-in (1.00 → 1.12 over 1.2 s) or a hard cut to a crop.

**Never:**
- back or elastic eases, per-letter staggers, typewriter text, glitch, neon glow, or shake;
- whip or zoom-blur transitions, Ken Burns on cards, or gradient text;
- 3D blockchain cubes, particles, or emoji;
- two elements moving in opposite directions, or anything animating under a stressed VO word.

### Sound
- Our own music bed (ASSET_LICENSES), ducked 8–10 dB under the VO.
- **Beat 7 (beta):** the music bed is cut, so the players' laughter and the game audio are the soundtrack.
- Real game SFX on knockouts and the drop; a soft tick on each counter.
- Master to −14 LUFS integrated, −1 dBTP.

---

## 3. The script

VO lines are written out in full, with pause marks, in [`VO-RECORDING-SHEET.md`](VO-RECORDING-SHEET.md). On-screen text is in Poppins.

| # | Time | Picture | On screen | VO line(s) |
|---|---|---|---|---|
| 1 · Hook | 0:00–0:08 | **Beta clip:** the best laugh at a knockout. Freeze on the hit, and the rollup tx pill rises in. Then a 1 s flash of the readout card ticking. | This hit is an on-chain transaction. | L1 |
| 2 · Title | 0:08–0:12 | Logo sting over the real game audio, with the tagline well below it. The logo and tagline never animate together. | Race. Bet. Sabotage. Win the Crown. | L2 |
| 3 · Problem | 0:12–0:22 | 1 s of each map, then a canvas card with two big numbers | Game: a new world every 0.033 s → Solana L1: a block every ~0.4 s | L3 |
| 4 · Join | 0:22–0:34 | Incognito → `fallrush.fun/play?judge=1` → lobby, with a real elapsed-time chip | No wallet to install. No popups. → A session key signs each move. | L4 |
| 5 · **Hero proof** | 0:34–1:20 | The FLIP into the split screen: gameplay + rollup readout. **At 0:38 you press Space and the height row jumps.** Then a push-in on an explorer `tick()` tx, then a comparison bar. Bots race past; one bot gets a label. | 30 world steps a second → (bar) One tick: up to 1.58M CU / one tx: 1.4M max → Sliced physics. Anyone can crank. → Integer Rust → sBPF + WASM · same bytes everywhere → Bots fill empty seats. Their brains run on-chain. | L5a–L5e |
| 6 · Spectators | 1:20–1:48 | Game + phone frame (`/live`): tap a player → bet → the odds move → **Chaos Drop** (Bumper Rain) → the warning → it lands, players fly. The sealed Champion pick, with its attestation ✓ row. | Bets settled by the program. → Chaos Drops land on-chain. → Picks sealed until close · Private ER | L6 |
| 7 · **Beta players** | 1:48–2:26 | **Your 30–40 s clip**, unnarrated:<br>• the canvas title card for 2 s;<br>• your clip, cut every 2–3 s, opening on a laugh and ending on the biggest reaction;<br>• lower-third name pills (with permission);<br>• then a hard cut to the stat card with four separate counters. | Real players. This week. → [N] players · [M] on-chain matches · [B] bets · 0 wallet popups | L7-in (over the title card only), silence, L7-out (over the stat card) |
| 8 · Final + Crown | 2:26–2:48 | Lid Flip: 3 left → 1 → a **human** champion → the Crown ceremony → explorer: the undelegate tx with the Magic Action → the Crown asset in the winner's wallet | Undelegate → Crown minted · Magic Action | L8 |
| 9 · The loop | 2:48–3:00 | The diagram builds: Player → session key → Ephemeral Rollup (30 steps/s · markets · drops) → commit → Solana (Match · receipt · Crown). VRF and the Private ER sit on the side. A small ink chip reads "Server: seats players". | node labels only | L9 |
| 10 · Only on MagicBlock | 3:00–3:18 | Three fast proof cards, 6 s each:<br>① big numbers: "1 match ≈ [T] rollup transactions · fees: 0" vs "on L1: [T] × 5,000 lamports ≈ [X] SOL";<br>② an explorer push-in on the VRF callback tx;<br>③ "10-commit limit → program-paid fee vault → Seasons keep recording" | as written | L10 |
| 11 · Don't trust us | 3:18–3:45 | Receipt page: Rollup ✓ → Solana ✓ → the replay check (sped up) → it holds on **N / N byte-exact** (mint). Then the FLIP to a readout card where a laptop sends `crank()` from a fresh keypair and the signature lands. | Replay matches the chain ✓ → N / N snapshots, byte-exact → Anyone can `crank()`. | L11 |
| 12 · CTA | 3:45–4:00 | End card on the canvas: the thesis line, `fallrush.fun/play?judge=1`, a small QR on the right, MagicBlock + Solana logos in ink at 48 px. Small print. Then 2 s of silence. | The game is the program. · small print: Devnet · test chips · ER · VRF · Session Keys · Private ER · Magic Actions · the server can stall or abort (refunds), it can't pick a winner | L12 |

**3:15 cut** (only if you choose it). Built from the same edit:
- Beat 3 drops to 6 s and beat 7 to 25 s.
- Beat 9 is removed (its diagram becomes a static end-card thumbnail).
- Beat 10 keeps cards ① and ③ only.

### Numbers Claude fills from the filmed matches (never invented)
- `N / N`: the snapshot count from the hero match's receipt (the old 21,540 is from before two program upgrades).
- `[T]`: the rollup tx count for the hero match, from the indexer archive (`txs.json` + tick/crank counts). Then `[X] = T × 5,000 lamports`.
- `[N] [M] [B]`: beta players, **on-chain** matches (receipt says all rounds ran on the program) and bets. Claude cross-checks your numbers against the indexer.

---

## 4. Honesty rules (from both fact-checks; one caught overclaim costs us the rest)

| Never say / show | Instead |
|---|---|
| "No wallet" | "No wallet **to install**." A guest wallet is created. |
| "1.4M-CU slices" | "One tick: up to 1.58M CU; one tx: 1.4M max". The measured slice peak is ~940k. |
| "tick() 30 times a second" | "30 world steps a second". One 12-player step takes several slices. |
| "No floats anywhere" | "No floats **in the physics**". Unity renders with floats. |
| "Not a server. The program." on its own | Follow it with "Our keeper and MagicBlock's scheduler only deliver the transactions, and anyone can send one". The keeper sends ~50 crank tx/s. |
| "Our server can't pick a winner" | "It can stall or abort, which refunds everyone, but the program decides who wins". **See §8: verify this first.** |
| "Every match leaves a receipt" | "Every **on-chain** match". Private rooms run on the Classic server. |
| "Crown minted" with a bot champion | Only over a filmed **human** win. Otherwise use the fallback (§6). |
| Chaos Drop / sealed pick in full mode | Only if the rehearsal shows it working. Otherwise use the fallback. Never splice in hybrid-era footage. |
| Beta clips showing ● Server | Every clip under an on-chain claim shows ◆ Rollup ✓ |

---

## 5. Your beta clip (30–40 s): what to capture today

**Where to play** (this decides whether it's on-chain):
- Everyone uses the public **Play / Quick Match**, at about the same time. You join first and they follow within the lobby countdown.
- The matchmaker groups humans into one lobby on the on-chain arenas.
- **Not Create Room** (it goes to the off-chain Classic server first). **Not judge mode** (it starts as soon as the first player is in).

**Check:** ◆ Rollup ✓ in the corner. If you see ● Server, that match can't be used for on-chain lines.

**Capture:**
- A screen recording of the game, 1080p+, with game audio.
- **Plus the players' voices:** a call recording, or a phone filming the room. The laughter is the soundtrack of beat 7.
- Phone video of faces or reactions is gold, with their permission.

**Moments to hunt:**
1. The biggest laugh or scream at a knockout. This is also the **cold open (beat 1)**.
2. A Chaos Drop hitting players while the spectator cheers.
3. A tight Final.
4. **A human winning the Crown.** If a beta player or you win, that's beat 8.
5. Someone reacting to "no wallet".

**After each match, send me:**
- the match id or receipt link;
- the raw clips;
- the 3–5 best timestamps;
- names or handles they're OK showing;
- the real player, match and bet counts.

If you already edited a 30–40 s clip, send the raw files too: Claude needs the clean audio for the cold open and the timing for the cuts.

---

## 6. Preflight: rehearsal match (Claude + you, before any hero capture)

Three things haven't been exercised in a **full 4-round on-chain** match: a Chaos Drop, a sealed Champion bet + reveal, and a human-won Crown via the Magic Action. A judge in judge mode will try the first two.

**Rehearsal (`/play?judge=1`; you play, Claude drives `/live` in the browser pane):**
1. A bet in rounds 1–3; check that the odds move.
2. A sealed pick; check the attestation ✓.
3. A Chaos Drop in the Final; check that it lands.
4. You try to win the Final; check the Crown mint tx.
5. The receipt: all rounds on-chain, and the replay passes (note N).

Anything that fails is a production fix, made under the VPS rules and only with your OK. If it isn't fixed by 19:00 UTC, use the fallback line:
- **Beat 6, no drop:** "Your friends watch from their phones. They bet, and the program pays the winners. Champion picks stay sealed until betting closes." (Drop the drop shot. If the sealed pick also fails, drop that sentence too.)
- **Beat 8, no human win:** "When the match commits back to Solana, a Magic Action mints the rewards: a capsule for every player, and the Crown if you win." Show the capsule mint, which is proven.

---

## 7. Capture and build (Claude)

**Tools (read-only, ~1 h):**
- `tools/demo/tick-watch.mjs`: logs the World account from the rollup RPC (tick, ticks/s, your Rushling's x/y/z) with timestamps.
- `tools/demo/laptop-crank.mjs`: one `crank()` from a throwaway keypair during our own match; prints the signature.
- `tools/p7/replay-check.mjs <match> --sealed`: confirms N.

**Capture:**
- Chrome window 960×540 pt (records at 1080p on Retina), Cap at 60 fps (30 if the game drops below ~45 fps).
- `/live` runs in the browser pane with the phone preset, in the same recording, so both views stay in sync.

| Session | Content | Beats |
|---|---|---|
| R | Rehearsal (§6), recorded as spare footage | — |
| A (hero) | You play `?judge=1`; tick-watch logs the whole match; Claude bets, picks and drops | 4, 5, 6, 8 |
| B | Re-takes: the Space-jump sync shot, the drop, a human win | 5, 6, 8 |
| Post | Explorer: tick, VRF callback, undelegate + Magic Action, the Crown asset (click Connect off camera, screenshot each as insurance). Receipt + replay (2 takes), laptop crank, cold join from incognito (3 takes), 1 s per map | 3, 4, 8, 10, 11 |

**Disk** (~5.5 GB free):
- Keep ≥ 1.5 GB free and record in segments.
- Cut the selects, then offload the raw files to your drive or cloud before the next match.
- Convert beta iPhone clips to constant-frame-rate H.264 with ffmpeg first.

**Build:**
- One HyperFrames project, with one sub-composition per beat.
- Captions on one track; Poppins loaded from Google Fonts.
- Render per beat if memory is tight.
- Claude frame-checks every beat: text in safe zones, no HUD collisions, pill contrast, no dead air (no spinners, no Connect prompt).

---

## 8. Two repo issues the roasts found (decide before submitting)

1. **README overclaims** (README.md around lines 108–119):
   - "a human champion taking the Crown";
   - drops and sealed bets "inside full 4-round on-chain matches";
   - a "15-match load test" (it was hybrid-era, 8 completed).

   Reword them, unless the rehearsal makes them true.
2. **Possible trust hole (unverified):**
   - The legacy server-key instructions (`record_qualified`, `record_eliminated`, `complete_round`, `complete_match`, lib.rs:244–393) check only the server key, the status and the sequence number. Nothing in them is gated on full on-chain mode.
   - If the Match passes those status checks during an on-chain round, the server key could record eliminations or a champion, and settlement would pay that slot.
   - That would contradict "the server can't pick a winner". Claude must test this on a local validator, and if it's real, gate those instructions in full mode (a program upgrade, your call).
   - Until it's resolved, the video uses only the softer line in §4 and the small print.

---

## 9. Timeline (UTC)

| By | Milestone |
|---|---|
| 26th 15:30 | Tools built; trust-hole test on a local validator started |
| 26th 16:00 | **Rehearsal match R** |
| 26th 17:30 | Hero matches A + B captured |
| 26th 18:00 | Your beta clips, match ids, numbers and names in (record the beta session before this) |
| 26th 19:00 | Post shots done; fixes deployed or fallback lines chosen |
| 26th 19:30 | **Script locked** with real numbers; the recording sheet updated and sent to you |
| 26th 21:00 | Your VO in |
| 26th 23:30 | Rough cut (30 fps) to you |
| 27th 00:00–06:00 | Sleep |
| 27th 08:00 | Picture lock with your notes; 4:00 and 3:15 exports |
| 27th 09:30 | Final 60 fps export + QA (frames, captions, loudness) |
| 27th 10:00 | **You** upload to YouTube |
| 27th 11:00 | Check in a private window; README `{{DEMO_VIDEO_URL}}` + the form |
| 27th 12:00 | **Submit** |

**If time runs short, cut in this order:**
1. the 3:15 export;
2. beat 10 card ②;
3. session B;
4. beat 9's diagram (becomes a static frame).

Never cut sleep or the rehearsal.

**If the rollup is down during capture:** use the archived match via `/receipt` and `/replay`, the explorer screenshots and the existing proof images. The VO is unchanged.
