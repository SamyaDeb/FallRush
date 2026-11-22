scenario round, 30 Hz, up to 60 s, 12 bots, CU per tick = decode + step + encode (LiteSVM 0.9.1)

| map | ticks | CU p50 | CU p95 | CU max | max at tick | codec only | parity |
|---|---|---|---|---|---|---|---|
| CrumbleShelf | 1800 | 424558 | 787882 | 2031009 | 241 | 21279 | OK |
| StarScramble | 1800 | 1289237 | 1874521 | 2930108 | 658 | 22905 | OK |
| GooTower | 1670 | 1894958 | 4158509 | 6244012 | 1375 | 25945 | OK |
| BlockPartyDash | 1800 | 1591284 | 3965401 | 6210538 | 1346 | 29283 | OK |
| Lobby | 1800 | 316191 | 316191 | 457042 | 2 | 21675 | OK |

max over maps: 6244012
