scenario round, 30 Hz, up to 60 s, 12 bots, CU per tick = decode + step + encode (LiteSVM 0.9.1)

| map | ticks | CU p50 | CU p95 | CU max | max at tick | codec only | parity |
|---|---|---|---|---|---|---|---|
| CrumbleShelf | 1800 | 285028 | 439316 | 563717 | 241 | 21302 | OK | 2.01 |
| StarScramble | 1800 | 239135 | 307507 | 360553 | 215 | 22928 | OK | 2.66 |
| GooTower | 1667 | 261029 | 346984 | 513081 | 1065 | 25968 | OK | 2.62 |
| BlockPartyDash | 1800 | 353560 | 498202 | 1102640 | 1352 | 29306 | OK | 4.05 |
| Lobby | 1800 | 150300 | 150300 | 199579 | 2 | 21698 | OK | 2.00 |

max over maps: 1102640
