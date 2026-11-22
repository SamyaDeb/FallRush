scenario round, 30 Hz, up to 90 s, 12 bots, CU per tick = decode + step + encode (LiteSVM 0.9.1)

| map | ticks | CU p50 | CU p95 | CU max | max at tick | codec only | parity |
|---|---|---|---|---|---|---|---|
| CrumbleShelf | 2700 | 338176 | 542403 | 952829 | 241 | 21302 | OK | 1.01 |
| StarScramble | 1800 | 395979 | 509376 | 549343 | 1117 | 22928 | OK | 1.50 |
| GooTower | 1667 | 359882 | 590149 | 721564 | 340 | 25968 | OK | 1.65 |
| BlockPartyDash | 2700 | 514724 | 691621 | 1255571 | 1829 | 29306 | OK | 2.89 |
| Lobby | 2700 | 195714 | 195714 | 290166 | 2 | 21698 | OK | 1.00 |

max over maps: 1255571
