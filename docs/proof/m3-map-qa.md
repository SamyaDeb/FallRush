### Map QA: Map_BlockPartyDash (map hash ffc099ad), 100 runs per variant × modifier, 12 waypoint bots (reference speed)

| Variant | Modifier | Runs | Finish rate | Qualified = N | Median / p90 duration (s) | Best line median (s) | Timer expiries | Falls / run | Hits / run | Bot stuck re-acquires / run | Soft-locks |
|---|---|---|---|---|---|---|---|---|---|---|---|
| A | None | 100 | 66.7 % | 100/100 | 46.9 / 48.7 | 41.0 | 0 | 0.4 | 17.6 | 0.3 | 0 |
| A | MoonBounce | 100 | 66.8 % | 100/100 | 48.4 / 51.0 | 42.2 | 0 | 2.1 | 10.0 | 0.1 | 0 |
| A | ButterFloors | 100 | 66.8 % | 100/100 | 47.0 / 48.9 | 41.1 | 0 | 1.0 | 13.8 | 0.3 | 0 |
| A | TurboToys | 100 | 66.8 % | 100/100 | 47.6 / 49.3 | 40.2 | 0 | 0.3 | 17.2 | 0.0 | 0 |
| A | TinyTilt | 100 | 66.7 % | 100/100 | 46.7 / 48.9 | 41.1 | 0 | 0.5 | 16.5 | 0.2 | 0 |
| B | None | 100 | 66.7 % | 100/100 | 46.2 / 48.5 | 40.8 | 0 | 0.2 | 16.2 | 0.3 | 0 |
| B | MoonBounce | 100 | 66.8 % | 100/100 | 48.9 / 51.0 | 42.3 | 0 | 1.7 | 11.3 | 0.2 | 0 |
| B | ButterFloors | 100 | 66.8 % | 100/100 | 46.6 / 48.2 | 41.0 | 0 | 0.3 | 11.9 | 0.5 | 0 |
| B | TurboToys | 100 | 66.8 % | 100/100 | 47.1 / 49.2 | 40.2 | 0 | 0.6 | 15.5 | 0.0 | 0 |
| B | TinyTilt | 100 | 66.7 % | 100/100 | 46.5 / 48.8 | 41.0 | 0 | 0.2 | 16.3 | 0.2 | 0 |

| Section | Route | Bots that took it | Reached the next checkpoint on it |
|---|---|---|---|
| 0 | S1-left | 4036 | 4036 |
| 0 | S1-right | 3956 | 3945 |
| 0 | S1-bobbing | 4008 | 4005 |
| 1 | S2-centre+S3-conveyor | 3064 | 3064 |
| 1 | S2-centre+S3-springs | 3023 | 3023 |
| 1 | S2-beamL+S3-conveyor | 2935 | 2935 |
| 1 | S2-beamR+S3-springs | 2964 | 2964 |
| 2 | S4-door1 | 2364 | 2364 |
| 2 | S4-door2 | 2484 | 2484 |
| 2 | S4-door3 | 2339 | 2339 |
| 2 | S4-door4 | 2450 | 2450 |
| 2 | S4-door5 | 2349 | 2349 |
| 3 | S5-tables | 4018 | 4017 |
| 3 | S5-walkL | 4029 | 4027 |
| 3 | S5-walkR | 3939 | 3931 |

- Ticks with every S4 door closed (2,000 ticks × each variant × modifier): **0**
- Deterministic for a run seed (re-run of run 0, A and B): **yes**
- Overall median duration: **47.0 s** (reference-bot target 40–60 s ≈ humans 75–110 s): **within target** — reference bots have no noise or mistakes (M4 adds them); human times are UNVERIFIED until a playtest
- Wall time: 105 s

**QA PASS** (soft-locks, reachability per route, doors, N qualified, determinism)
