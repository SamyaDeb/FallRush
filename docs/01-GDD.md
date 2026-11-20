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

### 4.2 Production (20 players)

20 → 12 → 8 → 4 → 1, using the same four modes plus more maps per pool.

## 5. Round modes

### 5.1 Race

- Reach the finish by crossing ordered checkpoints.
- **The first *N* valid finishers qualify.** When *N* have finished, the round ends immediately.
- **Timer expiry** with fewer than *N* finishers: the remaining places go by ranking key (checkpoint index ↓, distance to next checkpoint ↑, time reached last checkpoint ↑, slot index ↑).
- **Falling:** respawn at the last checkpoint after 1.0 s.

### 5.2 Survival (Goo Tower)

- Glittery goo rises at a fixed server velocity. Touching goo eliminates you.
- **Two ways to qualify:**
  - the first *N* players to reach the summit pad qualify instantly ("escaped");
  - if the timer ends first, every survivor qualifies.
- **If too many survive:** they are ranked by height (highest first), then by slot index.
- **If fewer than *N* are left alive:** only the survivors qualify, and the round can't qualify more players than are alive. The ruleset defines a **minimum-advance policy**: the next round runs with whoever survived, as long as at least 2 remain. A single survivor wins the match outright.

### 5.3 Skill (Star Scramble)

- Stars spawn on moving and rotating platforms in a fixed timeline, derived from the round seed. Touching a star scores it (+1; golden stars +3).
- A knockback costs **one** carried star, which pops off and can be grabbed back.
- **Ranking:** after 60 s, the top *N* by score qualify. Ties break by earliest time at final score, then slot index.
- The server validates every pickup (pickup radius plus the player's server position).

### 5.4 Final (Crumble Shelf)

- A grid of toy-block tiles on stacked shelves (3 layers). A tile wobbles for 0.6 s after first contact, then drops.
- **Win:** the last Rushling standing wins.
- **Timer (120 s) tiebreak:** lowest layer reached (higher is better), then tiles touched (fewer is better), then slot index.

## 6. Controls (keyboard + mouse baseline; gamepad post-MVP)

| Action | Key | Notes |
|---|---|---|
| Move | WASD | Camera-relative |
| Camera | Mouse | Adjustable sensitivity and invert |
| Jump | Space | Coyote 0.10 s, buffer 0.12 s |
| Dive | Shift / LMB | Forward burst, cooldown 1.0 s; the player is grounded-stunned 0.35 s after landing unless they jump-cancel within 0.15 s of landing (skill tech) |
| Grab | E / RMB | Post-MVP (needs server-side grab rules) |
| Emote | 1–4 | Lobby, intermission and spectating only |
| Menu | Esc | Leaving mid-match asks for confirmation |

## 7. Movement model (authoritative constants in `MovementConfig v1`)

The model is **kinematic**: a custom capsule motor runs the same C# code on the server and in client prediction. No rigidbody player physics.

| Constant | Initial value (tune in M2) |
|---|---|
| Capsule | radius 0.31, height 1.0 |
| Max run speed | 7.0 m/s |
| Ground acceleration | 60 m/s² |
| Ground deceleration | 45 m/s² |
| Air control | 35% of ground acceleration |
| Gravity | 24 m/s²; fall multiplier 1.35 |
| Jump | apex 1.6 m (one jump; no double jump) |
| Dive | horizontal impulse 9 m/s, vertical 3 m/s, cooldown 1.0 s |
| Slope | max 45°; step offset 0.25 m |
| Hazard knockback | defined per hazard profile; sets stun 0.3–0.8 s (no input) |
| Player–player | soft separation push (max 2.5 m/s), no stun; prevents stacking exploits |

All values live in the ruleset, are hashed into `ruleset_hash`, and that hash is committed on-chain at match creation.

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

## 13. Accessibility

- Hazards use a pattern as well as color (stripes).
- Subtitles for cues ("Goo rising!").
- Camera sensitivity, invert-Y, FOV slider.
- Reduced-motion option (no shake, no FOV kick).
- Colorblind-safe player outlines.
- A latency / reconnect indicator.

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
