# G1 map QA (scripts/g1-qa.sh 100, Mac dev server, 2026-11-19, 100 runs per cell; final build)

### Map QA: Map_GooTower (Survival, map hash fb5a25bb), 100 runs per variant × modifier × tier, 8 BotBrain bots (the players this round has in a 12-player match; target 5)

| Tier | Variant | Modifier | Runs | Valid result | Soft-locks | Qualified (mean) | Median / p90 duration (s) | Timer expiries | Escaped / goo / fell (mean) | Stuck re-acquires / run |
|---|---|---|---|---|---|---|---|---|---|---|
| Reference | A | None | 100 | 100/100 | 0 | 5.00 | 85.3 / 87.6 | 0 | 5.0 / 0.5 / 0.0 | 9.5 |
| Reference | B | None | 100 | 100/100 | 0 | 5.00 | 85.2 / 87.3 | 0 | 5.0 / 0.8 / 0.0 | 8.0 |
| Normal | A | None | 100 | 100/100 | 0 | 5.00 | 88.7 / 91.5 | 0 | 5.0 / 0.6 / 0.0 | 8.0 |
| Normal | B | None | 100 | 100/100 | 0 | 4.99 | 90.3 / 95.0 | 0 | 5.0 / 0.9 / 0.0 | 9.0 |

- Reference tier: overall median **85.2 s** (GDD §4.1 target 90–120 s): **outside the target**
- Normal tier: overall median **89.6 s** (GDD §4.1 target 90–120 s): **outside the target**

| Variant | Route index (every side) | Escaped alone | Min goo margin (s) | Climb (s) |
|---|---|---|---|---|
| A | 0 | yes | 8.3 | 87.8 |
| A | 1 | yes | 9.0 | 82.1 |
| B | 0 | yes | 8.7 | 84.6 |
| B | 1 | yes | 9.4 | 81.6 |

The goo constraint (doc 08 §5): a reference bot alone on each route index keeps a positive margin above the goo all the way to the summit.
- Deterministic for a run seed: **yes**
- Wall time: 105 s

**QA PASS** (valid result every run, no soft-lock, qualified ≤ target, goo never overtakes a route, determinism)

### Map QA: Map_StarScramble (Skill, map hash 4e004f12), 100 runs per variant × modifier × tier, 5 BotBrain bots (the players this round has in a 12-player match; target 3)

| Tier | Variant | Modifier | Runs | Valid result | Soft-locks | Qualified (mean) | Median / p90 duration (s) | Timer expiries | Pickups / drops (mean) · score check | Stuck re-acquires / run |
|---|---|---|---|---|---|---|---|---|---|---|
| Reference | A | None | 100 | 100/100 | 0 | 3.00 | 60.0 / 60.0 | 100 | 61.2 / 0.0 · OK | 0.0 |
| Reference | A | MoonBounce | 100 | 100/100 | 0 | 3.00 | 60.0 / 60.0 | 100 | 59.2 / 0.0 · OK | 0.0 |
| Reference | A | TurboToys | 100 | 100/100 | 0 | 3.00 | 60.0 / 60.0 | 100 | 61.1 / 0.0 · OK | 0.0 |
| Reference | A | BigBonk | 100 | 100/100 | 0 | 3.00 | 60.0 / 60.0 | 100 | 61.4 / 0.0 · OK | 0.0 |
| Reference | B | None | 100 | 100/100 | 0 | 3.00 | 60.0 / 60.0 | 100 | 60.7 / 0.0 · OK | 0.0 |
| Reference | B | MoonBounce | 100 | 100/100 | 0 | 3.00 | 60.0 / 60.0 | 100 | 59.2 / 0.0 · OK | 0.0 |
| Reference | B | TurboToys | 100 | 100/100 | 0 | 3.00 | 60.0 / 60.0 | 100 | 61.2 / 0.0 · OK | 0.0 |
| Reference | B | BigBonk | 100 | 100/100 | 0 | 3.00 | 60.0 / 60.0 | 100 | 61.3 / 0.0 · OK | 0.0 |
| Normal | A | None | 100 | 100/100 | 0 | 3.00 | 60.0 / 60.0 | 100 | 58.9 / 0.0 · OK | 0.0 |
| Normal | A | MoonBounce | 100 | 100/100 | 0 | 3.00 | 60.0 / 60.0 | 100 | 56.0 / 0.0 · OK | 0.0 |
| Normal | A | TurboToys | 100 | 100/100 | 0 | 3.00 | 60.0 / 60.0 | 100 | 59.0 / 0.0 · OK | 0.0 |
| Normal | A | BigBonk | 100 | 100/100 | 0 | 3.00 | 60.0 / 60.0 | 100 | 59.0 / 0.0 · OK | 0.0 |
| Normal | B | None | 100 | 100/100 | 0 | 3.00 | 60.0 / 60.0 | 100 | 58.3 / 0.0 · OK | 0.0 |
| Normal | B | MoonBounce | 100 | 100/100 | 0 | 3.00 | 60.0 / 60.0 | 100 | 55.6 / 0.0 · OK | 0.0 |
| Normal | B | TurboToys | 100 | 100/100 | 0 | 3.00 | 60.0 / 60.0 | 100 | 58.8 / 0.0 · OK | 0.0 |
| Normal | B | BigBonk | 100 | 100/100 | 0 | 3.00 | 60.0 / 60.0 | 100 | 59.0 / 0.0 · OK | 0.0 |

- Reference tier: overall median **60.0 s** (GDD §4.1 target 60–75 s): **within target**
- Normal tier: overall median **60.0 s** (GDD §4.1 target 60–75 s): **within target**
- Deterministic for a run seed: **yes**
- Wall time: 399 s

**QA PASS** (valid result every run, no soft-lock, qualified ≤ target, scores = pickups − drops, determinism)

### Map QA: Map_CrumbleShelf (Final, map hash 75438337), 100 runs per variant × modifier × tier, 3 BotBrain bots (the players this round has in a 12-player match; target 1)

| Tier | Variant | Modifier | Runs | Valid result | Soft-locks | Qualified (mean) | Median / p90 duration (s) | Timer expiries | One champion · fell (mean) | Stuck re-acquires / run |
|---|---|---|---|---|---|---|---|---|---|---|
| Reference | A | None | 100 | 100/100 | 0 | 1.00 | 77.1 / 96.2 | 0 | 100/100 · 2.0 | 0.0 |
| Reference | A | MoonBounce | 100 | 100/100 | 0 | 1.00 | 76.9 / 98.0 | 0 | 100/100 · 2.0 | 0.0 |
| Reference | A | ButterFloors | 100 | 100/100 | 0 | 0.99 | 74.3 / 93.5 | 0 | 100/100 · 2.0 | 0.0 |
| Reference | B | None | 100 | 100/100 | 0 | 1.00 | 77.1 / 98.2 | 0 | 100/100 · 2.0 | 0.0 |
| Reference | B | MoonBounce | 100 | 100/100 | 0 | 0.99 | 74.8 / 94.7 | 0 | 100/100 · 2.0 | 0.0 |
| Reference | B | ButterFloors | 100 | 100/100 | 0 | 1.00 | 75.8 / 92.9 | 0 | 100/100 · 2.0 | 0.0 |
| Normal | A | None | 100 | 100/100 | 0 | 1.00 | 74.8 / 94.2 | 0 | 100/100 · 2.0 | 0.0 |
| Normal | A | MoonBounce | 100 | 100/100 | 0 | 1.00 | 75.4 / 94.1 | 0 | 100/100 · 2.0 | 0.0 |
| Normal | A | ButterFloors | 100 | 100/100 | 0 | 1.00 | 68.7 / 91.5 | 0 | 100/100 · 2.0 | 0.0 |
| Normal | B | None | 100 | 100/100 | 0 | 1.00 | 68.8 / 89.3 | 0 | 100/100 · 2.0 | 0.0 |
| Normal | B | MoonBounce | 100 | 100/100 | 0 | 1.00 | 77.1 / 96.5 | 0 | 100/100 · 2.0 | 0.0 |
| Normal | B | ButterFloors | 100 | 100/100 | 0 | 1.00 | 71.5 / 92.6 | 0 | 100/100 · 2.0 | 0.0 |

- Reference tier: overall median **76.5 s** (GDD §4.1 target 60–120 s): **within target**
- Normal tier: overall median **71.8 s** (GDD §4.1 target 60–120 s): **within target**
- Deterministic for a run seed: **yes**
- Wall time: 35 s

**QA PASS** (valid result every run, no soft-lock, qualified ≤ target, one champion, determinism)

### Map QA: Map_BlockPartyDash (Race, map hash 965b8ccf), 100 runs per variant × modifier × tier, 12 BotBrain bots (the players this round has in a 12-player match; target 8)

| Tier | Variant | Modifier | Runs | Valid result | Soft-locks | Qualified (mean) | Median / p90 duration (s) | Timer expiries | Falls / hits (mean) | Stuck re-acquires / run |
|---|---|---|---|---|---|---|---|---|---|---|
| Normal | A | None | 100 | 100/100 | 0 | 8.00 | 86.4 / 96.5 | 0 | 34.5 / 7.2 | 13.3 |
| Normal | A | MoonBounce | 100 | 100/100 | 0 | 8.00 | 110.9 / 130.1 | 0 | 32.1 / 17.2 | 40.4 |
| Normal | A | ButterFloors | 100 | 100/100 | 0 | 8.00 | 86.0 / 96.8 | 0 | 34.1 / 9.0 | 13.3 |
| Normal | A | TurboToys | 100 | 100/100 | 0 | 8.00 | 85.8 / 95.1 | 0 | 33.9 / 8.2 | 13.4 |
| Normal | A | TinyTilt | 100 | 100/100 | 0 | 8.00 | 86.9 / 95.3 | 0 | 34.2 / 7.2 | 13.1 |
| Normal | B | None | 100 | 100/100 | 0 | 8.00 | 87.0 / 97.7 | 0 | 35.5 / 6.4 | 13.1 |
| Normal | B | MoonBounce | 100 | 100/100 | 0 | 8.00 | 107.9 / 123.7 | 1 | 32.9 / 15.3 | 38.2 |
| Normal | B | ButterFloors | 100 | 100/100 | 0 | 8.00 | 86.2 / 97.8 | 0 | 34.4 / 5.8 | 13.3 |
| Normal | B | TurboToys | 100 | 100/100 | 0 | 8.00 | 86.8 / 100.3 | 0 | 34.7 / 7.1 | 13.2 |
| Normal | B | TinyTilt | 100 | 100/100 | 0 | 8.00 | 85.5 / 96.2 | 0 | 34.1 / 6.4 | 13.2 |

- Normal tier: overall median **88.4 s** (GDD §4.1 target 75–120 s): **within target**
- Deterministic for a run seed: **yes**
- Wall time: 188 s

**QA PASS** (valid result every run, no soft-lock, qualified ≤ target, determinism)

#### Block Party Dash, reference tier (G1 addition)

### Map QA: Map_BlockPartyDash (Race, map hash 965b8ccf), 100 runs per variant × modifier × tier, 12 BotBrain bots (the players this round has in a 12-player match; target 8)

| Tier | Variant | Modifier | Runs | Valid result | Soft-locks | Qualified (mean) | Median / p90 duration (s) | Timer expiries | Falls / hits (mean) | Stuck re-acquires / run |
|---|---|---|---|---|---|---|---|---|---|---|
| Reference | A | None | 100 | 100/100 | 0 | 8.00 | 61.8 / 64.4 | 0 | 3.1 / 6.3 | 7.7 |
| Reference | A | MoonBounce | 100 | 100/100 | 0 | 8.00 | 74.9 / 83.2 | 0 | 1.1 / 15.2 | 24.6 |
| Reference | A | ButterFloors | 100 | 100/100 | 0 | 8.00 | 61.6 / 63.5 | 0 | 3.3 / 7.7 | 8.2 |
| Reference | A | TurboToys | 100 | 100/100 | 0 | 8.00 | 61.7 / 64.1 | 0 | 3.1 / 5.5 | 8.0 |
| Reference | A | TinyTilt | 100 | 100/100 | 0 | 8.00 | 61.5 / 63.8 | 0 | 3.2 / 6.1 | 8.1 |
| Reference | B | None | 100 | 100/100 | 0 | 8.00 | 61.0 / 63.2 | 0 | 3.7 / 5.0 | 7.5 |
| Reference | B | MoonBounce | 100 | 100/100 | 0 | 8.00 | 74.1 / 80.6 | 0 | 0.6 / 14.2 | 23.6 |
| Reference | B | ButterFloors | 100 | 100/100 | 0 | 8.00 | 61.6 / 63.4 | 0 | 3.7 / 6.1 | 7.7 |
| Reference | B | TurboToys | 100 | 100/100 | 0 | 8.00 | 61.8 / 64.7 | 0 | 5.3 / 4.5 | 7.6 |
| Reference | B | TinyTilt | 100 | 100/100 | 0 | 8.00 | 60.6 / 63.2 | 0 | 3.4 / 5.3 | 7.6 |

- Reference tier: overall median **61.9 s** (GDD §4.1 target 75–120 s): **outside the target**
- Deterministic for a run seed: **yes**
- Wall time: 142 s

**QA PASS** (valid result every run, no soft-lock, qualified ≤ target, determinism)
