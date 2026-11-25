# Full game on-chain: research (2026-11-24/25)

Goal: rounds 1–3 (Capsule Run, Goo Tower, Star Fountain) computed on the ER like the Final (today OC-11: Final only).
Worktree `../FallRush-fullchain`, branch `fullchain`. No live change was made. Dev arena **7**
(`3uJwGdZSpdXN49uBPVKRgHfDLhZgc8Kv4bBUUiRnCin3`) set up on devnet, reusing arena 2's sealed maps (`setup.ts FROM_ARENA=2`).

## 1. Facts that shape the plan
- Round order is fixed (deal.rs): R1 Capsule Run (12 players) → R2 Goo Tower (8) → R3 Star Fountain (5) → Final (3).
  So "Star Fountain first" = the chain plays rounds 3–4; each later step starts one round earlier.
- Cost is compute, not SOL: rollup transactions are free. Limits: 400k CU per timed-task execution (one failure ends the
  task), ~1 execution per 10 ms slot per task (~100/s), 1.4M CU per keeper-sent transaction.
- **Slice size never changes the simulation**: `rush-sim run` transcripts are identical at SLICE 1000 / 4000 / 100000
  (seed 2). So a transaction can run a bigger slice when it has more CU, with no replay or trust impact.
- On-chain Goo Tower blob is stale (sealed hash ≠ local `maps/GooTower.mapblob`, changed 2026-11-24, e3af378); the
  deployed program predates e3af378 too. Going live needs a re-upload + program upgrade anyway.

## 2. CU per execution, LiteSVM, real player counts (scratch matrix, 90 s RUSH)
| round (players) | sliced 1000: p95 / max | executions per tick | whole tick max |
|---|---|---|---|
| Star Fountain (5) | 212k / 290k | 1.96 | 626k |
| Goo Tower (8) | 246k / 440k | 2.60 | 760k |
| Capsule Run (12) | 407k / 940k | 4.93 | 1.58M (s10000: 1.68M, 1.23/tick) |
20 Hz does not make a tick cheaper (only fewer per second).

## 3. Devnet run, current program, `start_sim` 4 rounds, 12 bots, 1 task (fc-devnet-1)
Match `ANs5ns2Hj58bhh3WgGqJ6RqjGZWLpUNBrdHbcpfPXHeu`: complete, champion slot 3, committed. 471 s game / 658 s wall
(0.72×), 47,822 executions, 35 restarts, 12,838 fallback sends.
| round (RUSH) | game / wall | stalls | work-cap hits |
|---|---|---|---|
| R1 Capsule Run | 0.30× | 32 | 1 |
| R2 Goo Tower | 0.94× | 3 | 3,228 |
| R3 Star Fountain | 0.89× | 0 | 4 |
| R4 Final | 1.60× (catch-up) | 0 | 0 |
Healthy task throughput ≈ 98 executions/s (R2, R3). Drive mode: ~61 sends/s but only ~29 slices/s landed, ~0.19× on R1.

## 4. Eliminated / finished slots still cost work (found here)
`step_slice` charges `PLAYER_BASE_WORK` and runs the bot brain for every active slot, before `simulates()` returns for
OUT / DONE players (`world.rs` step_player). Native full matches (SLICE 1000, 30 Hz, 3 seeds), slices per RUSH tick:
| round | today | skip non-simulating slots |
|---|---|---|
| R1 | 5.0–5.5 | 4.9–5.4 |
| R2 | 3.3–3.5 | 2.3 |
| R3 | 3.67 (= devnet 3.67) | 1.9–2.0 |
| R4 | 2.1 | 1.0 |
Changes World bytes (bot state of idle slots) → must be version-gated so archived matches still replay.
Halving bot thinking (upper bound) barely moves R1 (4.7–5.2): Capsule Run's cost is motor/collision, not bots.

## 5. Integration inventory (code audit, CU excluded)
Program: no start-at-round-N (`start_from(round, alive_mask)` needed); `mirror_final` is Final-only and the non-final
mirror overwrites the Classic transcript → hybrid mirror; no free `Match.flags` bit; markets / Chaos Drops / sealed
bets gate on round 4. Keeper `daemon.ts` Final-only (start_final, raw-byte round checks, 20 min timeout, no commit_round
for chain rounds). Classic server hands off only at the last round (`ServerMatch.cs:375`); Classic seed ≠ chain VRF seed.
Unity: one map per on-chain session, `OnChainMatchFeed` hard-codes the Final (L), join assumes 3 finalists, 12-player
browser prediction cost UNVERIFIED. Web `/live` `/receipt`, `replay-check.mjs` (wasm cannot cross map loads), indexer:
Final-only labels / logic. Any program upgrade hits live arenas 2 and 3 (same program ID).

## 6. Parallel timed tasks (fc-devnet-t2, current program, TASKS=2 in run.ts)
Match `8YjxVYQCJgvatbVEDkaQ51x2kTjdXt2nYmaGi4Shq6g5`: complete; 382.5 s game / 412.8 s wall (**0.93×**, was 0.72×),
40,641 executions, 31 restarts. Each `schedule_ticks` makes a new task id, so N tasks need no program change.
| round (RUSH) | game / wall | stalls |
|---|---|---|
| R1 Capsule Run | 0.36× | 28 |
| R2 Goo Tower | 1.08× | 3 |
| R3 Star Fountain | 1.80× (catch-up) | 0 |
| R4 Final | 3.18× (catch-up) | 0 |

## 7. Capsule Run cannot fit the 400k task
LiteSVM, BlockPartyDash, 12 players: one player per execution (SLICE 1) p95 300k, **max 866k**; SLICE 500 max 882k.
A single player's step can exceed 400k, so no slice size avoids task deaths. Options: keeper-sent `tick()` at 1.4M CU
with a bigger slice (needs a budget argument; slice size never changes results, §1), or motor/collision CU work on
the race map (uncertain).

## 8. Motor-cap freezes on the current maps
`ev.capped` (motor work cap → the player is held still that tick), native full matches, seeds 1–4: R1 0–3, R2 0–2,
R3 3–27 per match. The 3,228 on devnet (fc-devnet-1, R2) came from the stale Goo blob + older program: re-check on
devnet after the upgrade.

## 9. Keeper crank throughput (fc-devnet-crank1, Capsule Run RUSH, tasks cancelled, one sender, 1.4M CU tx)
| send rate /s | sent | RPC errors | landed executions /s | ticks /s (SLICE 1000) |
|---|---|---|---|---|
| 40 | 599 | 0 | 36.1 | 5.1 |
| 80 | 1,199 | 0 | 72.3 | 9.5 |
| 120 | 1,799 | 0 | 107.7 | 14.4 |
| 160 | 2,318 | 0 | 107.8 (ceiling) | 14.8 |
One sender lands ~108 tx/s with no errors. With today's 1000-unit slices Capsule Run needs ~7 per tick (too many);
with a slice budget sized for 1.4M CU (~1.2–1.5 per tick, LiteSVM s10000) 30 Hz needs ~40–45 tx/s, under the ceiling.
UNVERIFIED until the upgrade: that the rollup sustains that rate when each tx uses ~1M CU.

## 10. GATE 1 — PASSED (2026-11-25, program 411b61fa, World v2, 2 tasks + crank, arena 7, current Goo blob)
| match | R1 Capsule Run | R2 Goo Tower | R3 Star Fountain | R4 Final | whole | restarts |
|---|---|---|---|---|---|---|
| 7w8Am8A4…VRfj | 1.00× | 1.00× | 0.99× | 1.00× | 1.0003× | 0 |
| 86pL554E…w4zD | 1.00× | 1.00× | 1.00× | 1.00× | 1.0001× | 0 |
| 2cDu1tuj…1cJD | 1.00× (3 stalls) | 1.00× | 1.00× | 1.00× | 0.9997× | 3 |
Crank: ~19–23k sends per match, 0–12 send errors; budget mostly 6000. Motor-cap freezes 5–10 per match.
Replay of match 1 (`replay-check.mjs …/world.bin --sealed`): replay = on-chain ✓.
Hybrid regression on the upgraded program: 1.000×, 0 restarts, 5 markets settled (fc-hybrid-regress).
