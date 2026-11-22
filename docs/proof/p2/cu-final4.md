scenario round, 30 Hz, up to 90 s, 12 bots, CU per tick = decode + step + encode (LiteSVM 0.9.1)

| map | ticks | CU p50 | CU p95 | CU max | max at tick | codec only | parity |
|---|---|---|---|---|---|---|---|
| CrumbleShelf | 2700 | 155806 | 242146 | 368747 | 241 | 21303 | OK |
| StarScramble | 1800 | 173029 | 257696 | 357367 | 1198 | 22929 | OK |
| GooTower | 2700 | 205546 | 277313 | 426996 | 1062 | 25969 | OK |
| BlockPartyDash | 2649 | 314311 | 404733 | 1005176 | 1361 | 29307 | OK |
| Lobby | 2700 | 68905 | 68905 | 92518 | 2 | 21699 | OK |

max over maps: 1005176
