scenario round, 30 Hz, up to 60 s, 12 bots, CU per tick = decode + step + encode (LiteSVM 0.9.1)

| map | ticks | CU p50 | CU p95 | CU max | max at tick | codec only | parity |
|---|---|---|---|---|---|---|---|
| CrumbleShelf | 1800 | 190732 | 355232 | 457954 | 241 | 21303 | OK |
| StarScramble | 1800 | 252377 | 350913 | 574352 | 680 | 22929 | OK |
| GooTower | 1557 | 219357 | 316118 | 713249 | 1008 | 25969 | OK |
| BlockPartyDash | 1800 | 395982 | 569186 | 1131969 | 930 | 29307 | OK |
| Lobby | 1800 | 78334 | 78334 | 107435 | 2 | 21699 | OK |

max over maps: 1131969
