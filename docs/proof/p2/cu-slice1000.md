scenario round, 30 Hz, up to 60 s, 12 bots, CU per tick = decode + step + encode (LiteSVM 0.9.1)

| map | ticks | CU p50 | CU p95 | CU max | max at tick | codec only | parity |
|---|---|---|---|---|---|---|---|
| CrumbleShelf | 1800 | 228842 | 325753 | 436082 | 250 | 21302 | OK | 3.01 |
| StarScramble | 1800 | 172501 | 234901 | 330886 | 656 | 22928 | OK | 3.82 |
| GooTower | 1667 | 200157 | 265723 | 449197 | 999 | 25968 | OK | 3.79 |
| BlockPartyDash | 1800 | 312971 | 382077 | 939616 | 1851 | 29306 | OK | 5.60 |
| Lobby | 1800 | 108240 | 108240 | 133860 | 2 | 21698 | OK | 3.00 |

max over maps: 939616
