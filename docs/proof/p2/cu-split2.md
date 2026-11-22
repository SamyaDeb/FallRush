scenario round, 30 Hz, up to 60 s, 12 bots, CU per tick = decode + step + encode (LiteSVM 0.9.1)

| map | ticks | CU p50 | CU p95 | CU max | max at tick | codec only | parity |
|---|---|---|---|---|---|---|---|
| CrumbleShelf | 1800 | 230257 | 403497 | 649687 | 241 | 21303 | OK |
| StarScramble | 1800 | 433364 | 713006 | 1082648 | 658 | 22929 | OK |
| GooTower | 1608 | 322339 | 474523 | 884490 | 893 | 25969 | OK |
| BlockPartyDash | 1800 | 571284 | 866072 | 1491032 | 1851 | 29307 | OK |
| Lobby | 1800 | 110401 | 110401 | 161944 | 2 | 21699 | OK |

max over maps: 1491032
