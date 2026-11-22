scenario round, 30 Hz, up to 60 s, 12 bots, CU per tick = decode + step + encode (LiteSVM 0.9.1)

| map | ticks | CU p50 | CU p95 | CU max | max at tick | codec only | parity |
|---|---|---|---|---|---|---|---|
| CrumbleShelf | 1800 | 445288 | 808428 | 2037766 | 241 | 21279 | OK |
| StarScramble | 1800 | 1336041 | 1950372 | 3004783 | 658 | 22905 | OK |
| GooTower | 1670 | 1951913 | 4205021 | 6280992 | 1375 | 25945 | OK |
| BlockPartyDash | 1800 | 1820289 | 4136744 | 9374655 | 1346 | 29283 | OK |
| Lobby | 1800 | 315835 | 315835 | 456734 | 2 | 21675 | OK |

max over maps: 9374655
