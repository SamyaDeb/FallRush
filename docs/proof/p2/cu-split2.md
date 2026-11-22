scenario round, 30 Hz, up to 60 s, 12 bots, CU per tick = decode + step + encode (LiteSVM 0.9.1)

| map | ticks | CU p50 | CU p95 | CU max | max at tick | codec only | parity |
|---|---|---|---|---|---|---|---|
| CrumbleShelf | 1800 | 228403 | 404338 | 650501 | 241 | 21303 | OK |
| StarScramble | 1800 | 338939 | 468933 | 625054 | 658 | 22929 | OK |
| GooTower | 1557 | 285356 | 402246 | 792906 | 1008 | 25969 | OK |
| BlockPartyDash | 1800 | 499640 | 780443 | 1238071 | 979 | 29307 | OK |
| Lobby | 1800 | 104555 | 104555 | 148178 | 2 | 21699 | OK |

max over maps: 1238071
