### G4.7 QA summary (scripts/g47-qa-all.sh reports; `docs/proof/g4`)

| Map | Target | Measured | Result | Note |
|---|---|---|---|---|
| Capsule Run | map hash = the G4 hash (map_hashes_v1) | 50125d89 · 100 runs per cell | **PASS** | 10 cells, mode Race; expected 50125d89 |
| Capsule Run | valid result every run | 10/10 cells 100 % | **PASS** |  |
| Capsule Run | 0 soft-locks | 0 over 10 cells | **PASS** |  |
| Capsule Run | deterministic (report self-check) | yes | **PASS** |  |
| Capsule Run | deterministic (two runs, same seeds) | g1-qa vs m4-qa: identical | **PASS** | wall-time line and the combined file's title ignored |
| Capsule Run | harness verdict | QA PASS | **PASS** |  |
| Capsule Run | 0 free routes | 0 (of 46 route × variant rows) | **PASS** |  |
| Capsule Run | route ratio ≤ 1.25 × the section's fastest | max 1.19; over 1.25: 0 | **PASS** |  |
| Capsule Run | normal median 85–110 s | 87.0 s | **PASS** |  |
| Capsule Run | per-cell normal medians | 78.3–107.7 s | ACCEPTED | 4 cell(s) under 85 s: accepted G4.2 §3 (A None / ButterFloors / TinyTilt 77–80 s) |
| Capsule Run | 8 of 12 qualify (every cell) | 8.00 mean | **PASS** |  |
| Capsule Run | timer expiries (normal, 150 s cap) | 2 over 10 cells | INFO | G4.2 accepted 2 / 100 in B TurboToys |
| Capsule Run | probe localhost: clients connected | 3/3 | **PASS** |  |
| Capsule Run | probe localhost: non-push corrections in a round ≤ 0.4 / 5 s | 0.00 / 0.00 / 0.00 (max corr 0 cm) | **PASS** |  |
| Capsule Run | probe localhost: 0 hazard mismatches | whole run 0 / 0 / 0; in a round 0 / 0 / 0 | **PASS** |  |
| Capsule Run | probe 150 ms ± 15 + 2 % loss: clients connected | 3/3 | **PASS** |  |
| Capsule Run | probe 150 ms ± 15 + 2 % loss: non-push corrections in a round ≤ 0.4 / 5 s | 1.42 / 0.25 / 0.10 (max corr 19–54 cm) | **FAIL** | C4: G4.2/G4.3 client 2 at 0.42 |
| Capsule Run | probe 150 ms ± 15 + 2 % loss: 0 hazard mismatches | whole run 0 / 0 / 0; in a round 0 / 0 / 0 | **PASS** |  |
| Goo Tower | map hash = the G4 hash (map_hashes_v1) | 5d5a00be · 100 runs per cell | **PASS** | 4 cells, mode Survival; expected 5d5a00be |
| Goo Tower | valid result every run | 4/4 cells 100 % | **PASS** |  |
| Goo Tower | 0 soft-locks | 0 over 4 cells | **PASS** |  |
| Goo Tower | deterministic (report self-check) | yes | **PASS** |  |
| Goo Tower | deterministic (two runs, same seeds) | g1-qa vs m4-qa: identical | **PASS** | wall-time line and the combined file's title ignored |
| Goo Tower | harness verdict | QA PASS | **PASS** |  |
| Goo Tower | 0 free routes | 0 (of 34 route × variant rows) | **PASS** |  |
| Goo Tower | route ratio ≤ 1.25 × the section's fastest | max 1.24; over 1.25: 0 | **PASS** |  |
| Goo Tower | normal median 50–65 s (G4.4-0) | 56.8 s | **PASS** | the harness line still prints the old 90–110 s GDD target |
| Goo Tower | 5 of 8 escape | 4.85–4.99 mean per cell | **PASS** | G4.3 §4 accepted ≈ 5 (5 in nearly every round; band ≥ 4.80 = no regression) |
| Goo Tower | every route index escapes alone, goo margin 8–10 s | 8.6–9.5 s over 6 indices; escaped all | **PASS** |  |
| Goo Tower | probe localhost: clients connected | 3/3 | **PASS** |  |
| Goo Tower | probe localhost: non-push corrections in a round ≤ 0.4 / 5 s | 0.00 / 0.00 / 0.00 (max corr 0 cm) | **PASS** |  |
| Goo Tower | probe localhost: 0 hazard mismatches | whole run 0 / 0 / 0; in a round 0 / 0 / 0 | **PASS** |  |
| Goo Tower | probe 150 ms ± 15 + 2 % loss: clients connected | 3/3 | **PASS** |  |
| Goo Tower | probe 150 ms ± 15 + 2 % loss: non-push corrections in a round ≤ 0.4 / 5 s | 0.10 / 0.25 / 0.12 (max corr 8–32 cm) | **PASS** |  |
| Goo Tower | probe 150 ms ± 15 + 2 % loss: 0 hazard mismatches | whole run 0 / 0 / 0; in a round 0 / 0 / 0 | **PASS** |  |
| Star Fountain | map hash = the G4 hash (map_hashes_v1) | 0bde7d75 · 100 runs per cell | **PASS** | 16 cells, mode Skill; expected 0bde7d75 |
| Star Fountain | valid result every run | 16/16 cells 100 % | **PASS** |  |
| Star Fountain | 0 soft-locks | 0 over 16 cells | **PASS** |  |
| Star Fountain | deterministic (report self-check) | yes | **PASS** |  |
| Star Fountain | deterministic (two runs, same seeds) | g1-qa vs m4-qa: identical | **PASS** | wall-time line and the combined file's title ignored |
| Star Fountain | harness verdict | QA PASS | **PASS** |  |
| Star Fountain | the 60 s timer every run (no early end) | timer 1600/1600 runs; median 60.0 s | **PASS** |  |
| Star Fountain | scores = pickups − drops | 16/16 cells OK | **PASS** |  |
| Star Fountain | exactly 3 qualify (every run) | 16/16 cells | **PASS** |  |
| Star Fountain | ≥ 60 % of pickups on the turntable | 63–67 % | **PASS** |  |
| Star Fountain | drops per bot: normal 1–4 | 1.20–1.78 | **PASS** |  |
| Star Fountain | drops per bot: reference ≤ 2 | 0.89–1.31 | **PASS** |  |
| Star Fountain | golden ≥ 50 % collected | 71–100 % | **PASS** |  |
| Star Fountain | reference spring → island ≥ 70 % | 85–100 % | **PASS** |  |
| Star Fountain | 3rd and 4th within 2 pts in ≥ 40 % of runs | 56–75 % | **PASS** |  |
| Star Fountain | reference ≤ 2 falls per round (mean per bot) | 0.00–0.32 | **PASS** |  |
| Star Fountain | camper probe (every spot below 3rd) | 14/14 spot × variant below; camper medians 0–6 | **PASS** |  |
| Star Fountain | probe localhost: clients connected | 3/3 | **PASS** |  |
| Star Fountain | probe localhost: non-push corrections in a round ≤ 0.4 / 5 s | 0.00 / 0.00 / 0.00 (max corr 0–10 cm) | **PASS** |  |
| Star Fountain | probe localhost: 0 hazard mismatches | whole run 0 / 0 / 0; in a round 0 / 0 / 0 | **PASS** |  |
| Star Fountain | probe 150 ms ± 15 + 2 % loss: clients connected | 3/3 | **PASS** |  |
| Star Fountain | probe 150 ms ± 15 + 2 % loss: non-push corrections in a round ≤ 0.4 / 5 s | 0.33 / 0.25 / 0.17 (max corr 34–152 cm) | **PASS** |  |
| Star Fountain | probe 150 ms ± 15 + 2 % loss: 0 hazard mismatches | whole run 0 / 0 / 0; in a round 0 / 0 / 0 | **PASS** |  |
| Lid Flip | map hash = the G4 hash (map_hashes_v1) | b0684a98 · 100 runs per cell | **PASS** | 12 cells, mode Final; expected b0684a98 |
| Lid Flip | valid result every run | 12/12 cells 100 % | **PASS** |  |
| Lid Flip | 0 soft-locks | 0 over 12 cells | **PASS** |  |
| Lid Flip | deterministic (report self-check) | yes | **PASS** |  |
| Lid Flip | deterministic (two runs, same seeds) | g1-qa vs m4-qa: identical | **PASS** | wall-time line and the combined file's title ignored |
| Lid Flip | harness verdict | QA PASS | **PASS** |  |
| Lid Flip | one champion every run | 12/12 cells | **PASS** |  |
| Lid Flip | normal median 60–100 s | 79.5 s | **PASS** |  |
| Lid Flip | normal per-cell median 60–100 s | 76.5–83.4 s | **PASS** |  |
| Lid Flip | normal p90 < 115 s | 94.5–105.3 s | **PASS** |  |
| Lid Flip | normal timer ≤ 5 % | 0–3 % | **PASS** |  |
| Lid Flip | last pair out in the same tick ≤ 2 % (≥ 98 % apart) | 0–2 % | **PASS** |  |
| Lid Flip | every lid fall fair (≥ 1 s yellow / magenta), every tier | 1909/1909 | **PASS** |  |
| Lid Flip | normal second chance ≥ 70 % | 73–85 % | **PASS** |  |
| Lid Flip | normal lower-shelf stay ≥ 8 s | 22.2–25.3 s | **PASS** |  |
| Lid Flip | inner floor ≥ 5 solid at every tick, every tier | violations 0, min solid 5 | **PASS** |  |
| Lid Flip | reference (flawless) bots reaching the timer | 29–57 % | INFO | G4.5-8 tiebreak (C1) |
| Lid Flip | probe localhost: clients connected | 3/3 | **PASS** |  |
| Lid Flip | probe localhost: non-push corrections in a round ≤ 0.4 / 5 s | 0.00 / 0.05 / 0.05 (max corr 0–11 cm) | **PASS** |  |
| Lid Flip | probe localhost: 0 hazard mismatches | whole run 0 / 0 / 0; in a round 0 / 0 / 0 | **PASS** |  |
| Lid Flip | probe 150 ms ± 15 + 2 % loss: clients connected | 3/3 | **PASS** |  |
| Lid Flip | probe 150 ms ± 15 + 2 % loss: non-push corrections in a round ≤ 0.4 / 5 s | 0.05 / 0.09 / 0.09 (max corr 8–29 cm) | **PASS** |  |
| Lid Flip | probe 150 ms ± 15 + 2 % loss: 0 hazard mismatches | whole run 0 / 0 / 0; in a round 0 / 0 / 0 | **PASS** |  |
| all maps | soak: every match complete | 20/20 complete, 0 aborted | **PASS** |  |
| all maps | soak: every event log valid (m4-verify-log) | 20/20 | **PASS** |  |
| all maps | soak: max RSS | 136 MB | INFO |  |
| all maps | C6 match length (soak median game time) | 410.0 s = 6.8 min (range 337–450 s, 20 matches) | INFO | GDD 7–9 min: ≈ +0.7 min lobby → receipt (G4.4-0); user's call, no change |

ACCEPTED 1 · FAIL 1 · INFO 4 · PASS 79

**1 target(s) FAIL or MISSING** (ACCEPTED = missed, accepted by a G4.x proof; INFO = no target)
