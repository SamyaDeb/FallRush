# 01 — Game Design Document (GDD)

**Status:** v2 (2026-11-18). Replaces `fall-rush-docs/01-GDD.md`.
**Related docs:**
- 17-ART-DIRECTION
- 08-MAP-DESIGN
- 02-PRD
- 15-DECISIONS-AND-RISKS

---

## 1. High concept

**Fall Rush** is a colorful, chaotic, multiplayer knockout party game.

- Up to 12 Rushlings (20 after load tests) are wind-up toys born from gacha capsules. They brawl through four rounds of obstacle races, survival and skill challenges in the Toybox Skyworks until one is crowned.
- **Unique positioning:** *"The provably fair knockout party game."*
  - Every round's map and Chaos Capsule modifier is dealt by **MagicBlock VRF**.
  - Every qualification and knockout is recorded live on a **MagicBlock Ephemeral Rollup**.
  - Every champion's **Crown Capsule** trophy points to a permanent on-chain match receipt on Solana.
- **Design promise:** you understand it in 10 seconds, laugh within 30, and still want "one more match" after 100.

## 2. Pillars

1. **Readable chaos.** Hazards are striped, telegraphed and fair. You laugh when you fail, because you saw it coming.
2. **Feel first.** Movement is responsive, bouncy and forgiving: coyote time, jump buffer and a dive recovery.
3. **Same game for everyone.** Same map, seed, modifier and hazard timeline for every player, and this is provable.
4. **Skill over spending.** Cosmetics never change the hitbox, speed, jump or odds.
5. **No wallet required.** Guests play everything. A wallet adds ownership, never permission.
6. **Spectacle.** Capsule-machine reveals, claw-machine eliminations and a crown ceremony.

## 3. Audience

Party-game players, streamers and their viewers, and crypto-curious gamers. No crypto knowledge is assumed. Crypto-native players get verifiable records and ownable trophies.

## 4. Match format

### 4.1 MVP (12 players, 4 rounds), approved as D-6

| Round | Mode | Map pool (VRF picks) | Start | Qualify | Out | Target length |
|---|---|---|---:|---:|---:|---|
| 1 | Race | Block Party Dash. Bonk Factory joins the VRF pool if it ships (stretch, D-14). | 12 | 8 | 4 | 75–110 s |
| 2 | Survival | Goo Tower | 8 | 5 | 3 | 90–120 s |
| 3 | Skill | Star Scramble | 5 | 3 | 2 | 60–75 s |
| 4 | Final | Crumble Shelf | 3 | 1 | 2 | 60–120 s |

A full match takes 7–9 minutes including intermissions.

**Thresholds** live in the versioned server ruleset (`ruleset_v1.json`) and are mirrored on-chain in `GameConfig`. They are **never hard-coded in the client**.

**Scaling rule for player count *P*:**
- Round 1 qualifies ceil(P × 0.66).
- Round 2 qualifies ceil(R1 × 0.62).
- Round 3 qualifies min(3, R2 − 1).
- Round 4 crowns 1.

The rule needs P ≥ 4. With fewer players, use a practice ruleset (no on-chain ranked record).

- **As built (M4):** qualify counts come from `RoundTargets(P)` (C# port of `targets.ts`, P = occupants at roster lock incl. bots). A practice match always has 12 (bots fill). Durations measured with bots: docs/proof/M4-match.md (a full 12-bot match ≈ 5–6.5 min from roster lock; humans UNVERIFIED).
- **Minimum-advance (M4-8, all rounds):** fewer survivors than N → only they advance; exactly one left after any round → that player is the champion (remaining rounds skipped, recorded as such: `COMPLETE_MATCH` after that round's `COMPLETE_ROUND`); zero → the highest placed of the round's ranking advances as the sole survivor (never a match without a champion).
- **Elimination reasons (M4-9):** 0 not qualified · 1 goo · 2 fell out · 3 lower score · 4 time ran out (ranked below the target) · 5 disconnected (> 20 s).

### 4.2 Production (20 players)

20 → 12 → 8 → 4 → 1, using the same four modes plus more maps per pool.

## 5. Round modes

### 5.1 Race

- Reach the finish by crossing ordered checkpoints.
- **The first *N* valid finishers qualify.** When *N* have finished, the round ends immediately.
- **Timer expiry** with fewer than *N* finishers: the remaining places go by ranking key (checkpoint index ↓, distance to next checkpoint ↑, time reached last checkpoint ↑, slot index ↑).
- **Falling:** respawn at the last checkpoint after 1.0 s.

- **As built (M3):** timer cap 110 s after RUSH. "Distance to the next checkpoint" is measured **along the course polyline** (the map's centre line through the checkpoints), not straight-line. Qualify count `ceil(P × 0.66)` (one place in code, `RoundParams.RaceQualifyCount`, TODO M4/M6: read the ruleset). Inputs are ignored during intro/countdown and after you finish; finished racers leave collisions and spectate (Tab).

### 5.2 Survival (Goo Tower)

- Glittery goo rises at a fixed server velocity. Touching goo eliminates you.
- **Two ways to qualify:**
  - the first *N* players to reach the summit pad qualify instantly ("escaped");
  - if the timer ends first, every survivor qualifies.
- **If too many survive:** they are ranked by height (highest first), then by slot index.
- **If fewer than *N* are left alive:** only the survivors qualify, and the round can't qualify more players than are alive. The ruleset defines a **minimum-advance policy**: the next round runs with whoever survived, as long as at least 2 remain. A single survivor wins the match outright.

- **As built (M4, `SurvivalRules`):** goo `y = y0 + v·max(0, t − 6 s)` (integer mm/ticks), feet below the surface = out (predicted, so you see your own goo at the right tick); falling out of the map = out too. The first N to the summit pad escape and leave the round; the round ends when N escaped or nobody is climbing. Timer 150 s: survivors qualify by height (then slot) up to N, the rest time out. Escaped ranked by escape time, survivors by height, the knocked-out by how long they lasted.

### 5.3 Skill (Star Scramble)

- Stars spawn on moving and rotating platforms in a fixed timeline, derived from the round seed. Touching a star scores it (+1; golden stars +3).
- A knockback costs **one** carried star, which pops off and can be grabbed back.
- **Ranking:** after 60 s, the top *N* by score qualify. Ties break by earliest time at final score, then slot index.
- The server validates every pickup (pickup radius plus the player's server position).

- **As built (M4, `SkillRules`):** waves of 8 stars every 8 s on distinct non-island anchors (each wave lasts 16 s), a golden star (+3) every 15 s on an island (lasts 10 s); anchors on the turn table and the islands move with them (pure). A pickup counts only for the server's own capsule (0.31 m + 0.4 m reach). A hazard knockback with stars costs exactly one: it pops 1.4 m away over 0.5 s, then anyone can grab it (10 s). Falls respawn after 1 s and keep your stars. "Earliest time at the final score" = the tick of the last score change.

### 5.4 Final (Crumble Shelf)

- A grid of toy-block tiles on stacked shelves (3 layers). A tile wobbles for **1.0 s** after first contact, then drops (G1; M4: 0.6 s). **Top-shelf tiles come back** 8 s after they drop (they blink for the last 1.5 s), so the top stays contested.
- **Win:** the last Rushling standing wins.
- **Timer (120 s) tiebreak:** lowest layer reached (higher is better), then tiles touched (fewer is better), then slot index.

- **As built (M4, `FinalRules`; G1 timing):** tile contact = the server's own ground contact; the drop (collider off) is at contact + 30 ticks on both sides (replicated, so prediction is exact); a top-shelf tile is solid again at contact + 30 + 240 ticks and a new contact starts a new cycle (`TileGridDef.RespawnTicks/RespawnShelves`, `ReactiveSet.TileContactAt`). Bot QA median **≈ 73 s** (M4: 27–32 s). Spinner bars (one low to jump, one high to dive) hang raised until 60 s. Below −6 m = out. Simultaneous last falls: the last to go ranks highest, then the tiebreak.

## 6. Controls (keyboard + mouse baseline; gamepad post-MVP)

| Action | Key | Notes |
|---|---|---|
| Move | WASD | Camera-relative |
| Camera | Mouse | Adjustable sensitivity and invert |
| Jump | Space | Coyote 0.10 s, buffer 0.12 s |
| Dive | Shift / LMB | Forward burst, cooldown 1.0 s; the landing is a **belly slide** (0.45 s, no control, slows at 12 m/s²) unless they jump-cancel within 0.15 s of landing (skill tech) |
| Grab | E / RMB | Post-MVP (needs server-side grab rules) |
| Emote | 1–4 | Lobby, results, intermission, champion, receipt (G1: wave, dance, cheer, flop; relayed to everyone) |
| Help | F1 | Shows / hides the controls bar (shown for 8 s after each map loads) |
| Menu | Esc | Leaving mid-match asks for confirmation |

## 7. Movement model (authoritative constants in `movement_v2.json` since G1)

The model is **kinematic**: a custom capsule motor runs the same C# code on the server and in client prediction. No rigidbody player physics.

**G1 movement v2** (`packages/shared/config/movement_v2.json`; v1 kept unchanged for history). Bolt is 1.0 m tall, so m/s = body heights per second. Feel targets and reasons: `docs/proof/G1-polish.md` §A–B.

| Constant | v1 (M2–M4) | **v2 (G1)** | Why |
|---|---|---|---|
| Capsule | radius 0.31, height 1.0 | same | |
| Max run speed | 7.0 m/s | **5.0 m/s** | a jog with weight (5 body heights/s), not a sprint |
| Ground acceleration | 60 m/s² | **28 m/s²** (0.18 s to full speed) | momentum you can feel |
| Ground deceleration | 45 m/s² | **30 m/s²** (≈ 0.4 m stop slide) | a little slide when you stop |
| Air control | 35 % | **30 %** | commit to jumps |
| Gravity; fall multiplier | 24 m/s²; 1.35 | **20 m/s²; 1.35** | slightly floatier |
| Jump apex | 1.6 m | **1.2 m** (airtime 0.64 s, ≈ 3.2 m running reach) | a hop, not a leap |
| Dive | 9 / 3 m/s, cooldown 1.0 s, 0.35 s land stun | **7 / 2.5 m/s**, cooldown 1.0 s, **0.45 s belly slide at 12 m/s²** (jump-cancel 0.15 s) | reach extender, comedic |
| Stumble (new) | – | landing after a fall **> 3 m**: **0.3 s** at **55 %** speed, input live, a jump ends it | weight on big drops |
| Turn | instant | motor facing instant (dive direction stays precise); **visual turn ≤ 720°/s**; reversing costs the accel | weight |
| Slope / step | 45°, 0.25 m | same | |
| Hazard knockback | per hazard profile, stun 0.3–0.8 s | same (shown as a tumble: flop, roll, get up) | |
| Player–player | soft push ≤ 2.5 m/s | same (shown as a bump wobble) | |

Modifiers still scale the base: MoonBounce gravity ×0.7 / apex ×1.25 (→ 14 m/s², 1.5 m), ButterFloors ground and slide decel ×0.4 (→ 12 / 4.8 m/s²).

**Where the values live (M2; v2 since G1):**
- Today they are in `packages/shared/config/movement_v1.json`: integers in mm, mm/s and ms, with their own sha256, loaded by Unity through generated constants and checked by a hash test. The table above is unchanged by M2a and M2b (sha256 `1d966e17…`; nothing was retuned, because the gate passed with the GDD values and the feel verdict is still open).
- **M2a added** four constants the table did not name:
  - max fall speed 30 m/s;
  - ground snap 0.25 m;
  - skin 1 cm;
  - ledge tolerance 5 cm (a landing on a ledge counts only if its top is ≤ 5 cm above the feet).
- **Timer rounding at 30 Hz:** coyote 3 ticks (100 ms), jump buffer 4 ticks (133 ms), dive land stun 11 ticks (367 ms), jump-cancel window 5 ticks (167 ms). v2: belly slide 14 ticks (450 ms → 13.5), stumble 9 ticks.
- **Target (M6):** merge them into a ruleset v2 that is hashed into `ruleset_hash` and committed on-chain at match creation. `ruleset_v1.json` (already compiled into the deployed program) is not changed.

## 8. Chaos Capsule modifiers (VRF-dealt)

- **What it is:** before each round, the **Chaos Capsule machine** spins and pops one modifier. The draw comes from the verified VRF output: `rng = H(vrf_seed ‖ round)`.
- **Fairness:**
  - every modifier applies to **all players equally**;
  - every modifier is pre-tested for that map;
  - "None" is always a possible outcome.

| Modifier | Effect | Allowed maps |
|---|---|---|
| None | Normal rules | All |
| Moon Bounce | Gravity ×0.7, jump apex ×1.25 | All except Goo Tower |
| Butter Floors | Ground decel ×0.4 (slippery) | Race, Final |
| Turbo Toys | Hazard speed ×1.2 and telegraph ×1.0 (telegraph kept the same) | Race, Skill |
| Big Bonk | Mallet / bumper knockback ×1.4 | Bonk Factory, Star Scramble |
| Tiny Tilt | Camera FOV −8°, fog in (visual only) | Race |

Modifier tables are versioned in the map pool, and the pool hash is committed on-chain before the VRF request.

**As built (M3):** values in `packages/shared/config/modifiers_v1.json` (sha256 `09eef853…`, not on-chain yet): MoonBounce gravity 24 → 16.8 m/s², apex 1.6 → 2.0 m (measured 2.000 m); ButterFloors ground decel 45 → 18 m/s² (stop from 7 m/s: 5 → 12 ticks); TurboToys hazard periods ÷ 1.2 rounded half-up to whole ticks (150 → 125), belts × 1.2, telegraphs unchanged; BigBonk mallet/bumper knockback × 1.4; TinyTilt FOV −8°, exponential fog 0.025 (client only).

## 9. Fairness & the VRF deal

- **Before the match:** the server freezes the **map pool v1**. That covers:
  - eligible maps per round
  - obstacle variants
  - the modifier tables
  - the ruleset

  `map_pool_hash` and `ruleset_hash` are committed on-chain at `create_match`.
- **The request:** VRF is requested **once per match**. In the callback, the **program itself** derives from the 32 random bytes:
  - the round 1 race map (uniform 1-of-2, rejection-sampled);
  - one obstacle variant index per round;
  - one modifier per round (weighted table, rejection-sampled);
  - `hazard_seed` (the phase offsets of all deterministic hazards).
- **No operator influence:** the operator never supplies the sequence and cannot reroll. If VRF times out, the match becomes **Unranked** (doc 07 §6).
- **Visibility:** all clients receive the deal from the server and show it on the "Deal" screen, which displays the VRF signature. Anyone can recompute the deal from the on-chain seed with the open-source `deal.ts` / `Deal.cs`. These two are the same algorithm with cross-tested vectors.

## 10. Eliminated players: **Fan Picks** (P1 stretch, the differentiator)

- **What it is:** eliminated players and spectators stay engaged. Between rounds they **pick which remaining Rushling they think will win**.
- **Rewards:** **Rush Points**, which are free, non-transferable and non-redeemable. They feed a seasonal "Oracle" leaderboard and give cosmetic unlocks.
- **No money involved:** there is no wagering, no deposit and no value transfer. This is a prediction game *for fun*, consistent with ADR-006.
- **Wallet players:** picks are signed by a **MagicBlock session key** and written into the delegated match account on the rollup, with no popup. They are locked at round start, visible on the receipt, and scored at match end.
- **Guest players:** the server records their picks in the database.

This puts the hackathon theme on screen (Entertainment Finance: *compete, predict, own*) without legal exposure.

## 11. Progression & collectibles

| Item | MVP? | On-chain? |
|---|---|---|
| XP (participation +10, qualify +20/round, champion +100) | Yes | No (database) |
| Levels 1–30, unlocking cosmetic presets | Yes | No |
| Achievements (First Qualify, First Final, Champion, Untouchable = no hazard hits in a round, Oracle = a correct Fan Pick) | Yes | Receipt only |
| **Crown Capsule trophy** (champion, wallet linked) | Yes (devnet) | **Metaplex Core** asset with attributes `match`, `matchPda`, `seasonId`, `rounds`, `players`. Minted by the backend after the receipt is final on Solana. |
| Shell skin collectibles | P1 | Metaplex Core collection, free claim |
| Marketplace / trading | No (post-MVP, legal review) | — |

**No pay-to-win, no paid loot boxes, no token emissions.**

## 12. UX flow

1. **Boot.** Title (capsule machine idle), then "Play as Guest" or "Connect Wallet" (optional).
2. **Main menu.** Play (Quick Play / Join with Code / Create Private Lobby), My Rushling, Collection, Records.
3. **Lobby.** Capsules drop in as players join, with ready toggles and a countdown at the min/max threshold.
4. **Deal screen** (about 6 s). The VRF request is pending while capsules spin. The deal is revealed: map, modifier, seed, with links.
5. **Round intro.** A 5 s flythrough plus "3-2-1-RUSH!"
6. **Gameplay.** HUD as in doc 17 §5.
7. **Round result.** Qualified confetti / eliminated claw. The chain chip updates as rollup transactions confirm.
8. **Intermission** (8 s). Leaderboard, next deal, Fan Picks for eliminated players.
9. **Final, then crown ceremony.** Champion pose, Crown Capsule pop.
10. **Match receipt.** Placements, per-round results, chain status (Rollup ✓ → Solana ✓), and a "View on-chain" button that opens the web receipt.
11. **Rewards.** XP, achievements, and "Claim Crown Capsule" for the champion if a wallet is linked.
12. **Back to lobby.**

**As built (M4, greybox, `Client/MatchHud.cs`):** Menu (Play = Quick Play, Connect Wallet smoke path) → Lobby room (`Map_Lobby`: player list with BOT tags and connection dots, 20 s countdown, host Start, "Bots will fill empty slots"; capsules drop in) → Deal (spinning, then the 4 rounds' maps / variants / modifiers / targets, "Practice deal (local seed)", the seed) → Round intro (map, mode, modifier chip, goal line, fly-through, 3-2-1-RUSH) → HUD (round + modifier top-left, timer top-right, the mode counter top-centre: qualified X / N, escaped / alive, stars, alive; ping / reconnecting bottom-left; chain chip `● Server` bottom-right) → Round result (QUALIFIED / ELIMINATED per player with the reason) → Intermission (8 s leaderboard with per-round places, next round, Fan Picks "coming soon") → Champion (podium + crown pop) → Receipt (placements, per-round places, match id, seed, transcript hash, "Chain: not recorded (practice)", Back to lobby) → Lobby. Eliminated players spectate (Tab cycles). Not in M4: XP / rewards (11), the capsule machine and claw (M7).

## 13. Accessibility

- Hazards use a pattern as well as color (stripes).
- Subtitles for cues ("Goo rising!").
- Camera sensitivity, invert-Y, FOV slider.
- Reduced-motion option (no shake, no FOV kick).
- Colorblind-safe player outlines.
- A latency / reconnect indicator.

**As built (M4):** subtitles for "Goo rising!", "Tiles crumbling!", "Spinners incoming!"; reduced motion also stops the tile shake, star spin, goo swirl and the podium hop; "Reconnecting… N s" bottom-left. Colourblind-safe player outlines: **not done** (per-slot colours only; M7).

## 14. Success metrics

**Hackathon** (measured in the demo build, see doc 12):
- full-match completion rate ≥ 90% across 20 test matches
- median RTT and correction rate on screen
- rollup record success 100% of events (or explicit pending)
- VRF fulfillment p95
- commit-to-Solana success
- demo clarity

**Post-hackathon:**
- D1/D7 retention
- matches per session
- queue time
- crash-free sessions
