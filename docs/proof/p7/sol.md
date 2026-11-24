# P7 devnet SOL (budget 1.5 SOL across all wallets; admin stays ≥ 1.0)

| when (UTC) | what | admin | game authority | guest-gas funder |
|---|---|---|---|---|
| 2026-11-23T19:31:59Z | P7 start (after two public test matches: the before run + the first after run) | 3.441858678 | 2.220500752 | 0.545915 |
| 2026-11-23T20:05Z | 30 load bettors funded 0.02 each (0.60), + 4 fill wallets (0.08) | | | |
| 2026-11-23T21:50Z | after the load run (15 matches), the fill test (2), chaos (6), after-shot runs (3): sweep of the bettor wallets returned 0.349 | 3.044507918 | 1.742563792 | 0.473885 |
| 2026-11-23T21:51:23Z | funder top-up +0.20 (D-SOL: ≈ 56 guests at the measured 0.0111 SOL / guest; the full 100-guest top-up (+0.73) would exceed the 1.5 budget) | 2.844502918 | 1.742563792 | 0.673885 |

**P7 so far: 6.3009 (P6 end: admin 3.4419 + authority 2.2891 + funder 0.5699) → 5.261 = 1.04 SOL used** (keeper matches ≈ 0.016 SOL each on the authority × ~35 matches; bettor onboarding ≈ 0.0111 each × 34; guest gas). Admin ≥ 1.0 ✓.
| 2026-11-24T17:43:47Z | beta audit: funder top-up +0.50 for the 20-person private beta (≈ 80 guests at 0.0111) | 2.344 | 1.334 | 0.909775 |
| 2026-11-24T18:23:34Z | beta audit: load run1 (4 arena matches, 10 bettors swept back 0.166); authority top-up +0.80 for the beta (≈ 80 more matches) | 1.510475318 | 2.032968992 | 0.89777 |
