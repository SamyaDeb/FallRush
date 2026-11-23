# P2 — the on-chain simulation: proof

Branch `p2` (worktree `../FallRush-p2`), from `main` @ `2decc78`. Prompt `docs/prompts/P2-PROMPT.md`.
Everything not run by us is marked **UNVERIFIED**. CU numbers are sBPF (LiteSVM 0.9.1 on the real map blobs, or the
devnet-as rollup where stated).

## 1. Carry-over results (0.1–0.4)

### 0.1 30 Hz
`movement_v4.json` `tickRateHz: 30` (commit `6f24f92`); the 15 / 20 Hz tick tables stay. Input batching: 3 ticks per
`submit_input` (12 players → 120 tx/s), stamped ahead, `InputBuf` ring 16. Classic reads `movement_v3` (unchanged).

### 0.2 Compute units
The first real-map measurement was far above P1's estimate (P1 measured 493k/tick on the *vector* map only):

| map | baseline p50 / max (unsplit tick) | after the exact cuts (part 1–3) p50 / max |
|---|---|---|
| Lid Flip | 1.35M / 5.1M | 0.38M / 2.0M |
| Star Fountain | 3.4M / 9.5M | 0.76M / 2.0M |
| Goo Tower | 11.0M / 63M | 0.55M / 1.2M |
| Capsule Run | 12.4M / 62M | 1.0M / 1.9M |
| Lobby | 0.74M / 1.3M | 0.19M / 0.30M |

Found with an exact sBPF profiler (`scripts/p2-prof.sh` + `tools/p2/sbf-prof.py`: LiteSVM register tracing → PC
histogram → functions of the unstripped `.so`). The causes and fixes (all bit-identical unless noted):
- **Signed division is a ≈ 240 CU library call on sBPF v0** (`u64 / u64` is 12 CU; `x / 3` 348 CU). The programs
  override `__divdi3 / __moddi3 / __divsi3 / __modsi3` with the magnitude form (11 CU); `fx::div_round / idiv /
  irem / floor_div` divide unsigned, power-of-two divisors shift.
- One bounds check per collider / prism vertex / hazard record (24 checked word reads were 1,000 CU); hazard defs read
  at their own size (a 1.1 KB `MAX_DEF_WORDS` buffer was cleared and copied per decode); Newton `isqrt`.
- Per-tick memos in `Scene`: seeded hazard phases + lid grid defs (`PhaseTable`), lid states (4 tier lags, packed),
  broadphase box (inflated query, fine-filtered once), rolling-ball kinematics, bot route directory, prism inscribed
  circles; column-wise grid lookup; per-candidate AABB skip + decode memo in `capsule_sweep`.
- Tilted boxes: derivative bisection instead of a 40-step ternary search (≤ 2 sub-mm vs the old result, 120k → 9k CU).
- Bots: shared-position ball look-ahead for the 8 headings + exact lane reject; cheap rejects before `isqrt`.
- Prisms: one pass over facing edges.
- **TOI bisection 8 → 6** (behaviour change): the 30 C# reference traces still pass at 30 Hz and 20 Hz; 4 and 5 steps
  fail `Sandbox-mallet` (300 mm). Golden vectors regenerated; native = WASM (Node) = sBPF on all 5 files.
- Build profile (step 4): rush-core `opt-level 3`, no overflow checks; dependencies `z`; `fall_rush` `s` (its borsh
  `Match` code overflows a 4 KB frame at `z`). `.so` 1,036,296 B.
- `Scene` lives on the heap in the program (≈ 3.8 KB; sBPF frames are 4 KB).

**The 300k gate for 12 players was not met as a single tick** (table above). **User decision (P2-DECISION-1, doc 15
OC-11): the Final goes on-chain first.** The gate now applies to **Lid Flip at 3 players**, and it **passes with bots
inside `step()` (option A)**:

| Lid Flip, sBPF (LiteSVM), whole tick incl. World decode / encode | p50 | p95 | max |
|---|---|---|---|
| 3 bots, Final round, 120 s × 30 Hz | 144k | 180k | **248k** |
| 3 humans pile-up (all driven at one point, jumps), 120 s | 86k | 114k | **145k** |
| *12 players pile-up (Lobby / practice budget)* | *188k* | *266k* | *342k* |
| *Lobby, 12 players pile-up* | *355k* | *398k* | *464k* |
| **On the rollup (devnet-as, task executions sampled from the 5 Finals)** | 138–158k | 169–193k | **262k** |

The work for the 12-player maps (experimental, not blocking) is kept: the tick is split and bounded by a deterministic
work budget (step 5):
- `world::step_slice` (final form): each `tick()` execution steps players in slot order while the work meter stays under
  `SLICE_WORK`; a heavy tick takes more executions. `step_part(K)` (fixed parts) is kept for comparison.
- Work meter (`Scene.work`, units ≈ 100 CU): narrowphase by shape, broadphase results, rays, bot probes, ball
  look-ahead, star scoring, plus a per-player base charge. Caps: bot brain 1,200 (degrades, no revert), motor 1,500
  (past it the step is reverted and the player stops for that tick). Counted, never `sol_remaining_compute_units`.
- The meter is a predictor, not a measurement: per-execution CU ≈ 70k + 107·units with p99 residual 130–206k
  (calibration, `docs/proof/p2/cu-cal*.md`). So executions can still exceed the 400k task budget; **the keeper's
  fallback `tick()` at 1.4M CU completes such an execution** and re-schedules the task (§3).

### 0.3 Bot pacing (bot constants only)
Per P2-DECISION-1 only the Final blocks. **Lid Flip (Final, 3 bots, through the director's `start_final`, 30 Hz,
`rush-sim final-qa`, 100 runs): median 77.3 s, p90 95.4 s, timer endings 1 %, 100 / 100 deterministic, the champion is
always a finalist** (G4.5 §2: 60–120 s, 0–3 %). Kept for the record (`SLICE` mode, 100 runs, 30 Hz): Capsule Run median
87.3–87.6 s with `RACE_PACE_PERMILLE` 875 (was 68.7 s), Goo Tower escape 4.81, Star Fountain 3 / 60 s, 20-match soak
20 / 20 (median 387.9 s, before the work meter). Bot constants only: maps, hazard timing and the motor are unchanged.

### 0.4 Program hygiene
`finalize_match` only from Completed / Aborted; VRF identities are `const`; neither `.so` imports
`sol_remaining_compute_units` (checked on the binaries).

## 2. Accounts and instructions as built
`programs/fall_rush/src/sim.rs` (module doc) + `lib.rs`. All sim PDAs are zero-copy and delegated, reused across matches.

| Account | Seeds | Bytes (with disc.) | Content |
|---|---|---|---|
| `Arena` | `["arena", id u8]` | 1,432 (P4 fix: was written 1,904) | authority, match_account, maps[5] (deal ids 0..3, lobby), owners[12] (InputBuf writers), wallets[12] (roster identity), inputs[12], task_id, nonce, matches_run, id, status, parts, human_mask, cosmetic[12], slot_ms |
| `WorldAcc` | `["world", arena]` | 2,256 | see §10 |
| `InputBuf` | `["in", arena, slot u8]` | 248 | arena, owner, token (SessionTokenV2 or default), latest, count, slot, ring[16] × {tick u32, input 4 B} |
| `EventLog` | `["ev", arena]` | 6,448 | head, ring[400] × {tick, seq, arg, kind, tag, slot, round} |
| `Match` | `["match", match_id]` | (M0) | per match; receives the results; the only account committed |
| map blob | program-owned keypair account | 80 + blob | `FRMAPBLB`, authority, len, sealed, map id, sha256 (checked against `maps/map_blob_hashes_v1.txt` by `seal_map`) |

Instructions: `init_arena`, `init_input`, `set_arena(maps, parts, slot_ms)`, `upload_map_chunk` / `seal_map`,
`delegate_sim`, `open_lobby`, `join_lobby(slot, cosmetic, wallet)` (+ optional `SessionTokenV2`), `start_sim` (4-round
experimental mode), **`start_final(slots[3])`**, **`tick()`**, `submit_input(slot, start, batch[12])` (+ the session
token when joined with one), `schedule_ticks` / `cancel_ticks` (hand-built Magic program CPI, task id
FNV-1a64(program ‖ world ‖ nonce) & i64::MAX), `commit_round(round)` (any payer, once per round), `end_sim`,
`abort_arena`; existing: `create_match`, `delegate_match`, `request_deal` / `deal_callback`, the M0 gameplay events
(used by the chain worker), `finalize_match` (now only from Completed / Aborted).

`tick()` takes every account in `remaining_accounts` (arena, world, event log, match, 5 maps, 12 InputBufs) and checks
them by hand; any problem is a logged no-op returning `Ok` (codes: 1 NotDue, 2 not running, 3 bad accounts, 4 bad
state). It runs one `step_slice` of the current tick. **Real-time gate:** tick *n* may start once *n*/30 s of slot clock
have passed since the first tick (`Arena.slot_ms`: 10 ms devnet-as, 50 ms local); behind schedule it catches up.
On director phase changes it mirrors results into `Match`. Final mode keeps Classic's slots / event chain and closes it
with `TAG_COMPLETE_MATCH` whose argument is the on-chain director transcript.

Deviations from the prompt's table: map blobs are keypair accounts (a > 10 KB PDA cannot be created by CPI in one
instruction); no pre-made `Match` pool (creation + delegation measured at p50 0.9 s + 1.0 s); session keys as below.

**Session keys (SessionTokenV2, session-keys 3.1.1):** `join_lobby(slot, cosmetic, wallet)` accepts the wallet itself as
signer, or a session signer with its `SessionTokenV2` (authority = wallet, target = this program, session signer =
the signer, PDA seeds, not expired). The signer becomes the slot's InputBuf writer, the wallet its roster identity.
`submit_input` re-checks the token every time (present, same key, not expired): a revoked (closed) token or an expired
one is rejected — measured on the local stack, where the rollup saw the closed token on the next input.

## 3. Keeper (`tools/keeper/run.ts`) and chain worker (`tools/chain-worker/worker.ts`)
- Keeper: polls the World every 200 ms (+ `onAccountChange`, the recorder); a stall = no (tick, slice) progress for
  500 ms → cancel every task it scheduled, drive mode (streams `tick()` at 1.4M CU), re-schedule with a new nonce.
  Commits each resolved round once, `end_sim`, `finalize_match`. Confirms by polling `getSignatureStatuses`. Every action
  with its latency in `docs/proof/p2/keeper-<run>.jsonl`. In the 5 Finals: **0 stalls, 0 restarts**.
- Latencies over the 6 devnet Final runs (ms, p50 / max): create_match 919 / 1,255, delegate_match 980 / 1,074,
  open_lobby 334 / 458, request_deal 276 / 341, VRF callback seen +77 / +147, start_final 376 / 546, schedule_ticks
  317 / 357, commit_round 386 / 822, finalize_match 356 / 539, chain-worker event 262 / 1,097.
- Chain worker: tails the Classic jsonl and sends each event only when its seq equals `Match.event_seq` (DEAL = seq 0 on
  both sides), so a restart resumes exactly; stops after `COMPLETE_ROUND 3` and hands the 3 qualifiers to `start_final`.

## 4. Per-round enablement (P2-DECISION-1)
| round | where | P2 status |
|---|---|---|
| Final — Lid Flip | **on-chain** | gate met (§1, §5) |
| Skill — Star Fountain | Classic | next to move (motor cost); 12-player slices p95 235k measured |
| Survival — Goo Tower | Classic | then; p95 266k, max 449k |
| Race — Capsule Run | Classic | last; p95 382k, max 940k |
Experimental: a complete 4-round 12-bot match ran on devnet (old fixed-part program): 4 rounds, champion slot 11,
committed; slow motion on Capsule Run (1,091 s wall for 463 s of game, 70 keeper restarts); replay identical
(33,188 snapshots, transcript = `Match`).

## 5. Devnet runs (devnet-as, 30 Hz, arena 2 `HbinPthhN3g5BRQRL4aKQ75HyQXMofhsUbg2c28bJXHt`, task interval 11 ms)
Exit gate: 5 unattended on-chain Finals, 3 bots, real time (game time ≥ 95 % of wall clock, tick gap p95 ≤ 50 ms), CU
max ≤ 300k, replay identical 5 / 5.

| # | Match | game / wall | on-chain tick gap p50 / p95 / max | client-seen gap p95 | CU / execution p50 / p95 / max | restarts | champion | replay | receipt |
|---|---|---|---|---|---|---|---|---|---|
| 1 | `MQYRScj9…` | 0.998 | 30 / 40 / 50 ms | 135 ms | 146k / 190k / 234k | 0 | 8 | ✓ 2,278 | ✓ |
| 2 | (summary) | 1.000 | 30 / 40 / 50 | 123 | 154k / 193k / 262k | 0 | 10 | ✓ 3,071 | ✓ |
| 3 | | 1.001 | 30 / 40 / 50 | 120 | 138k / 169k / 181k | 0 | 3 | ✓ 3,545 | ✓ |
| 4 | | 1.001 | 30 / 40 / 50 | 145 | 138k / 180k / 215k | 0 | 1 | ✓ 1,458 | ✓ |
| 5 | | 1.000 | 30 / 40 / 50 | 133 | 158k / 190k / 230k | 0 | 10 | ✓ 2,044 | ✓ |

**Pass: 5 / 5.** "game / wall" = ticks / 30 over the websocket arrival times of the first and last tick (the slot clock
gives 0.9999–1.000). The on-chain gap is the slot each tick started in × 10 ms (authoritative); the client-seen gap is
websocket delivery (bunched: p50 17–18 ms). 1.02 executions per tick, 0 budget hits. Signatures per match in
`docs/proof/p2/runs/devnet-finals-5x-m*/summary.json` (create, delegate, open_lobby, request_deal, start_final,
schedule, commit, end_sim, finalize), Match receipts in `match.json`.
Local stack: 3 / 3 Finals complete, replay identical 3 / 3, receipts ✓ (game / wall 0.66: 50 ms local slots allow 20
executions / s).

**P2.10 hybrid (devnet):** a Classic server match (Mac dev server, headless, 12 bots, sim speed 8,
`docs/proof/p2/classic/match-cb12c864….jsonl`) played rounds 1–3; the chain worker sent its 32 events (idempotent by
event_seq); `start_final([1, 6, 8])`; the Final ran on-chain in real time (game / wall 1.000, tick gap p95 40 ms);
`finalize_match` committed it. `docs/proof/p2/hybrid-verify.json`: rounds 1–3 eliminations on Solana = the jsonl,
finalists = the jsonl's round-3 qualifiers, champion 6 (a finalist), status completed, event_seq 33; replay of the Final
identical (1,954 snapshots), receipt = Match ✓. The Classic server's own Final (which it also played) crowned slot 6 too
(coincidence; the on-chain one is authoritative in this mode).

## 6. Replay (`rush-sim replay --run <dir>`)
Native re-run from the match's VRF seed (`start_match`, or `start_final` with the Match's roster hash, finalists and
eliminations), slice by slice, compared byte for byte at every recorded World snapshot (stale out-of-order snapshots
from the merged websocket / polling recorder are skipped). The Final's receipt is checked as
sha256(prev ‖ 6 ‖ seq ‖ 4 ‖ champion ‖ director transcript) = the Match on Solana. Results: 5 / 5 devnet Finals, the
hybrid Final, 3 / 3 local Finals, the experimental 4-round match — all identical. Recordings (~4 MB per Final, ~115 MB
per 4-round match) stay local (`docs/proof/p2/runs/*/world.bin`, gitignored); summaries, match.json and replay results
are committed. Rollup transaction retention was not relied on.

## 7. Security (local stack, `tools/keeper/security.ts`): **35 / 35 pass** (`docs/proof/p2/security-local.json`)
Input signed by another key; out-of-window ticks (+50, −10); sanitiser clamps (−128, −128, 0xFF → stick inside the unit
circle, buttons 0x0F); double join; taken slot; another slot's InputBuf; joining after the roster lock (claiming a bot
slot); session token expired at join / without a token / of another wallet / valid; input with the token / without it /
after revocation / after expiry; early tick() NotDue; tick() with no accounts / 5 accounts / reordered maps / a wrong
match account / 8 foreign accounts inserted → no-op success; tick while not running; start_sim before the deal / while
running / re-entering an old match; deal_callback from a non-VRF signer; commit_round before a round; finalize before
Completed; open_lobby by a non-authority; unique task ids per schedule. Writable foreign accounts are rejected by the
rollup itself before the program runs (a task's account list is ours). 10+ commits: impossible by construction
(commit_round once per round, bits 1–4; with finalize ≤ 5; the hybrid uses 3 complete_round + 1 + finalize = 5).

## 8. Cost (devnet)
| item | SOL |
|---|---|
| program extend 1,000 B → 1,081,896 B (560,000 + 40,000 B) | 3.049 (permanent rent; the program account now holds 5.497) |
| 4 upgrades (buffer refunded) | ≈ 0.021 fees |
| map blobs (5, 141 KB), 2 arenas (+ World, EventLog, 24 InputBufs), delegation records | ≈ 0.95 (game authority) |
| per match: Match account rent (kept as the receipt) + 2 base fees | ≈ 0.017 |
| rollup transactions (ticks, inputs, commits) | 0 |
| **balances** | admin wallet 8.748 → **5.678**; game authority 4.978 → **3.877** |

## 9. UNVERIFIED
- Humans on-chain (P3): no client sent inputs yet; input → applied latency, prediction and the T+3 delay are P3.
- Capsule Run / Goo Tower / Star Fountain on-chain at real time (only the experimental slow-motion run).
- The rollup's task scheduler above 100 executions / s (measured: one per 10 ms slot).
- Mainnet / other ER regions; many concurrent arenas on one validator.
- The work meter is a predictor (calibration residual p99 130–206k CU on the 12-player maps); not needed for the Final
  (max 262k on the rollup), but any map moved on-chain must be re-measured on the rollup.
- WASM in a browser and the Unity FFI bundle for the P2 `rush-core` (golden vectors pass in Node; the FFI expected
  hashes in `docs/proof/p1/ffi_expected.txt` predate P2 and must be regenerated by P3).

## 10. Inputs to P3
- **`WorldAcc`** (`["world", arena]`, 2,256 B incl. the 8-byte discriminator): 0 disc · 8 arena · 40 last_slot u64 ·
  48 ticks_run u64 · 56 executions u64 · 64 not_due u64 · 72 budget_hit u64 · 80 applied_tick[12] u32 · 128 tick u32 ·
  132 len u32 · 136 part_pending u8 (0 = tick boundary, 1..12 = slice position) · 137 parts · 138 director phase ·
  139 round · 140 results_round · 141 last_code · 142 pad 2 · 144 cur[12][4] · 192 cur_some u16 · 194 pad 6 ·
  200 start_slot u64 · **208 `rush_core::World` canonical bytes (len B, ≤ 2,048)** — decode with `World::load_from`
  (rush-core WASM `rc_*` ABI). A snapshot with part_pending = 0 is a complete tick.
- **`applied_tick[slot]`**: the last input tick the program applied for that slot (reconciliation for prediction).
- **Inputs:** `submit_input(slot u8, start u32, batch [u8; 12])` = 3 ticks × (move_x i8, move_y i8, buttons u8,
  cam_yaw >> 8 u8) for ticks start, start+1, start+2; accepted for start in [World.tick − 8, World.tick + 8]. The tick
  latches the input stamped exactly T+1 from `ring[(T+1) % 16]`; a client stamps **T+3** (sends ticks now+3..now+5 every
  3 ticks, 120 tx/s for 12 players). Sanitised on write and again in `rush-core`.
- **Accounts (devnet, program `4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4`):** arena id 2 =
  `HbinPthhN3g5BRQRL4aKQ75HyQXMofhsUbg2c28bJXHt` (Final arena); World / EventLog / InputBuf PDAs from the seeds in §2;
  map blobs in `Arena.maps`; config `["config"]`; game authority `3EYRtprz…`.
- **Session-key flow:** the wallet creates a `SessionTokenV2` (session-keys program `KeyspM2ssCJbqUhQ4k7sveSiY4WjnYsrXkC8oDbwde5`,
  seeds `["session_token_v2", fall_rush id, session signer, wallet]`, ≤ 7 days) on Solana; in the lobby the session
  signer calls `join_lobby(slot, cosmetic, wallet)` on the rollup with `session_token` = that PDA; then signs every
  `submit_input` and passes the same token account. Without a session key the wallet joins and signs itself
  (`session_token` = none).
- **Finding the arena for a match:** `Arena.match_account` = the `Match` PDA (`["match", match_id]`). Arena ids are small;
  read `["arena", 0..15]` (getMultipleAccounts on the rollup) and pick the one whose `match_account` is the match
  (devnet Finals: arena 2). In hybrid mode the arena opens when the chain worker has recorded round 3.

## For main
- doc 15: **OC-11** (P2-DECISION-1); OC-3 note; P0-1 tick rate 30 Hz with the evidence; the work-budget row.
- doc 19: 20 Hz → 30 Hz in §3 + P2; the Final-first note.
- `PROJECT_STATUS.md`: the P2 line.
