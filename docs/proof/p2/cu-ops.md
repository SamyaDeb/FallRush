
## GooTower (129 colliders)
| op | CU / call |
|---|---|
| div_round i64 | 46 |
| mul_div (i128) | 276 |
| isqrt_i64 | 276 |
| normalize_q15 | 651 |
| map.collider(i) decode | 330 |
| capsule_sweep down 100 mm at spawn | 22568 |
| raycast down 3 m at spawn | 13183 |
| penetration at spawn | 11081 |
| gather 1x2x1 m box at spawn | 11633 |
| capsule_sweep fwd 300 mm at spawn | 11154 |
| sin_q15 | 27 |
| i64 / i64 | 30 |
| u64 / u64 | 12 |
| i64 / 3 (const) | 24 |
| idiv(x, 3) | 11 |
| i32 / 10 (const) | 28 |
| x*7/1000 (const) | 27 |
| div_round(x, 1000) | 37 |
| near() by shape | count, mean, max |
| shape 1 | 122 colliders, mean 1826, max 8879 |
| shape 2 | 7 colliders, mean 5394, max 5444 |
| tilted (general) OBBs | 17 colliders, max 8879 |
