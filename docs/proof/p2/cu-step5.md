scenario round, 30 Hz, up to 60 s, 12 bots, CU per tick = decode + step + encode (LiteSVM 0.9.1)

| map | ticks | CU p50 | CU p95 | CU max | max at tick | codec only | parity |
|---|---|---|---|---|---|---|---|
| CrumbleShelf | 1800 | 428151 | 792335 | 2035845 | 241 | 21279 | OK |
| StarScramble | 1800 | 1108797 | 1633445 | 2645237 | 658 | 22905 | OK |
| GooTower | 1670 | 1444587 | 1797475 | 3257453 | 1014 | 25945 | OK |
| BlockPartyDash | 1800 | 1233448 | 1888454 | 6103012 | 1346 | 29283 | OK |
| Lobby | 1800 | 321159 | 321159 | 467218 | 2 | 21675 | OK |

max over maps: 6103012
