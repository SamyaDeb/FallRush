scenario round, 30 Hz, up to 90 s, 12 bots, CU per tick = decode + step + encode (LiteSVM 0.9.1)

| map | ticks | CU p50 | CU p95 | CU max | max at tick | codec only | parity |
|---|---|---|---|---|---|---|---|
| StarScramble | 1800 | 172742 | 264784 | 424681 | 1504 | 22929 | OK |
| GooTower | 1827 | 160322 | 256339 | 369407 | 654 | 25969 | OK |
| CrumbleShelf | 2700 | 155806 | 242146 | 368745 | 241 | 21303 | OK |
| BlockPartyDash | 2700 | 309554 | 477062 | 759771 | 912 | 29307 | OK |
| Lobby | 2700 | 68905 | 68905 | 92518 | 2 | 21699 | OK |

max over maps: 759771
