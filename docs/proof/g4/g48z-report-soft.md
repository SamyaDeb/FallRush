# G4.8 look shots (soft)

## lid-flip

Pipeline: Mobile_RPAsset grading HighDynamicRange HDR True scale 0.80, quality level 0 (Mobile)

- **lid-flip spawn view (soft; Rushlings and lid snapshots hidden)**: 9 visible renderers, 9 renderer × material draws (MergedStatic 4, Hazards 1, Dressing 4; by root: Hazards 1; Dressing 4; MergedStatic 4;) — Editor frustum count, no Rushlings, no UI; not a GPU capture.
- instanced lid batches at t 0: 5
- action shot: t 3.60 s
- retire shot: t 24.73 s

| Lid Flip shot pixels: batch | lids | median top RGB | Δluma vs lit, median (normal / deutan / protan) | 10th-percentile lid RGB | Δluma vs lit, 10th pct (n / d / p) | darkest sample: RGB, Δluma (n / d / p) | old centre-on-seam Δluma (n / d / p) |
|---|---|---|---|---|---|---|---|
| Top2 | 14 | D7EBF1 | 0.289 / 0.220 / 0.329 | D4E9EE | 0.280 / 0.211 / 0.320 | D0E4EA 0.262 / 0.192 / 0.301 | 0.188 / 0.119 / 0.223 |
| Lit | 8 | FF9004 | 0.000 / 0.000 / 0.000 | D87403 | 0.111 / 0.116 / 0.106 | D87403 0.111 / 0.116 / 0.106 | 0.000 / 0.000 / 0.000 |
| Top3 | 11 | FAE4CC | 0.288 / 0.231 / 0.308 | F1DBC4 | 0.253 / 0.196 / 0.273 | F1DBC4 0.253 / 0.196 / 0.273 | 0.235 / 0.178 / 0.253 |
| TopPink | 24 | FBD0DD | 0.238 / 0.187 / 0.256 | F3CBD5 | 0.215 / 0.163 / 0.233 | D3ACD8 0.102 / 0.048 / 0.125 | 0.226 / 0.174 / 0.243 |
| LowEdge | 7 | D7ECF1 | 0.292 / 0.222 / 0.332 | D2E7ED | 0.272 / 0.203 / 0.313 | D2E7ED 0.272 / 0.203 / 0.313 | 0.378 / 0.309 / 0.415 |
| Low | 3 | D3E9EE | 0.279 / 0.210 / 0.320 | D3E9EE | 0.279 / 0.210 / 0.320 | D3E9EE 0.279 / 0.210 / 0.320 | 0.363 / 0.294 / 0.399 |

- worst calm-vs-lit deutan / protan gap (median and 10th-percentile lid): **0.163** → PASS (rule ≥ 0.12)

| section | sample (x, y0, z) | top hit | pixel RGB | H° | S | V |
|---|---|---|---|---|---|---|
| top ring 4 | (7.4, 0.0, -3.7) | LidCol_26 y 6.00 n.y 1.00 | FFD7E4 | 341 | 0.16 | 1.00 |
| top ring 3 | (-5.6, 0.0, 3.7) | LidCol_55 y 6.00 n.y 1.00 | FFEBD1 | 34 | 0.18 | 1.00 |
| top ring 2 | (-3.7, 0.0, -1.9) | LidCol_29 y 6.00 n.y 1.00 | DFF4FA | 193 | 0.11 | 0.98 |
| top ring 1 | (1.9, 0.0, -1.9) | LidCol_32 y 6.00 n.y 1.00 | FFD7E4 | 341 | 0.16 | 1.00 |
| top ring 0 | (0.6, 0.0, -0.6) | LidCol_40 y 6.00 n.y 1.00 | FAD0DD | 341 | 0.17 | 0.98 |


## Colourblind sheets (Machado 2009, severity 1)

- g48z-lid-flip-spawn-colourblind.png: normal | deuteranopia | protanopia
- g48z-lid-flip-action-colourblind.png: normal | deuteranopia | protanopia

| pair | normal Δluma | deutan Δluma | protan Δluma |
|---|---|---|---|
| stripe yellow vs hit pink | 0.446 | 0.254 | 0.354 |
| lid lit vs top ring 0 (tokens, soft) | 0.258 | 0.201 | 0.275 |
| lid lit vs top ring 1 (tokens, soft) | 0.258 | 0.201 | 0.275 |
| lid lit vs top ring 2 (tokens, soft) | 0.314 | 0.238 | 0.354 |
| lid lit vs top ring 3 (tokens, soft) | 0.307 | 0.244 | 0.325 |
| lid lit vs top ring 4 (tokens, soft) | 0.258 | 0.201 | 0.275 |
| lid lit vs lower shelf (tokens, soft) | 0.242 | 0.172 | 0.271 |
| lid lit vs lower edge (tokens, soft) | 0.198 | 0.270 | 0.171 |
