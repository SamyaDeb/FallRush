### Map QA: Map_GooTower (Survival, map hash 5d5a00be), 100 runs per variant × modifier × tier, 8 BotBrain bots (the players this round has in a 12-player match; target 5)

| Tier | Variant | Modifier | Runs | Valid result | Soft-locks | Qualified (mean) | Median / p90 duration (s) | Timer expiries | Escaped / goo / fell (mean) | Stuck re-acquires / run |
|---|---|---|---|---|---|---|---|---|---|---|
| Reference | A | None | 100 | 100/100 | 0 | 4.99 | 50.7 / 55.0 | 0 | 5.0 / 0.9 / 0.0 | 1.1 |
| Reference | B | None | 100 | 100/100 | 0 | 4.97 | 50.8 / 54.0 | 0 | 5.0 / 0.7 / 0.0 | 1.1 |
| Normal | A | None | 100 | 100/100 | 0 | 4.92 | 55.9 / 62.8 | 0 | 4.9 / 1.9 / 0.0 | 2.8 |
| Normal | B | None | 100 | 100/100 | 0 | 4.85 | 57.5 / 64.8 | 0 | 4.9 / 2.1 / 0.0 | 3.5 |

- Reference tier: overall median **50.8 s** (GDD §4.1 target 90–110 s): **outside the target**
- Normal tier: overall median **56.8 s** (GDD §4.1 target 90–110 s): **outside the target**

| Variant | Route index (every side; route 0 where a side has fewer) | Escaped alone | Min goo margin (s) | Climb (s) |
|---|---|---|---|---|
| A | 0 | yes | 9.3 | 52.7 |
| A | 1 | yes | 9.1 | 51.4 |
| A | 2 | yes | 8.6 | 48.7 |
| B | 0 | yes | 9.0 | 49.6 |
| B | 1 | yes | 8.6 | 44.6 |
| B | 2 | yes | 9.5 | 46.7 |

The goo constraint (doc 08 §5): a reference bot alone on each route index keeps a positive margin above the goo all the way to the summit.
- Deterministic for a run seed: **yes**
- Wall time: 71 s

**QA PASS** (valid result every run, no soft-lock, qualified ≤ target, goo never overtakes a route, determinism)
