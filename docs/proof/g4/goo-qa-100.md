### Map QA: Map_GooTower (Survival, map hash fe3f1208), 100 runs per variant × modifier × tier, 8 BotBrain bots (the players this round has in a 12-player match; target 5)

| Tier | Variant | Modifier | Runs | Valid result | Soft-locks | Qualified (mean) | Median / p90 duration (s) | Timer expiries | Escaped / goo / fell (mean) | Stuck re-acquires / run |
|---|---|---|---|---|---|---|---|---|---|---|
| Reference | A | None | 100 | 100/100 | 0 | 4.88 | 51.2 / 56.6 | 0 | 4.9 / 1.6 / 0.0 | 1.5 |
| Reference | B | None | 100 | 100/100 | 0 | 5.00 | 52.7 / 57.1 | 0 | 5.0 / 1.5 / 0.0 | 1.9 |
| Normal | A | None | 100 | 100/100 | 0 | 4.27 | 58.8 / 68.8 | 0 | 4.3 / 3.6 / 0.0 | 3.9 |
| Normal | B | None | 100 | 100/100 | 0 | 4.14 | 59.0 / 65.9 | 0 | 4.1 / 3.7 / 0.0 | 3.3 |

- Reference tier: overall median **51.5 s** (GDD §4.1 target 90–110 s): **outside the target**
- Normal tier: overall median **58.9 s** (GDD §4.1 target 90–110 s): **outside the target**

| Variant | Route index (every side; route 0 where a side has fewer) | Escaped alone | Min goo margin (s) | Climb (s) |
|---|---|---|---|---|
| A | 0 | yes | 6.5 | 52.7 |
| A | 1 | yes | 9.1 | 51.9 |
| A | 2 | yes | 8.6 | 48.7 |
| B | 0 | yes | 9.0 | 48.9 |
| B | 1 | yes | 8.6 | 51.6 |
| B | 2 | yes | 9.5 | 46.7 |

The goo constraint (doc 08 §5): a reference bot alone on each route index keeps a positive margin above the goo all the way to the summit.
- Deterministic for a run seed: **yes**
- Wall time: 44 s

**QA PASS** (valid result every run, no soft-lock, qualified ≤ target, goo never overtakes a route, determinism)
