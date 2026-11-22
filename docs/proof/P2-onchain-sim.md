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

**The 300k gate was not met as a single tick.** Decided path (user: "complete full p2" with my defaults): split the tick
and bound it with a deterministic work budget (step 5):
- `world::step_slice` (final form): each `tick()` execution steps players in slot order while the work meter stays under
  `SLICE_WORK`; a heavy tick takes more executions. `step_part(K)` (fixed parts) is kept for comparison.
- Work meter (`Scene.work`, units ≈ 100 CU): narrowphase by shape, broadphase results, rays, bot probes, ball
  look-ahead, star scoring, plus a per-player base charge. Caps: bot brain 1,200 (degrades, no revert), motor 1,500
  (past it the step is reverted and the player stops for that tick). Counted, never `sol_remaining_compute_units`.
- The meter is a predictor, not a measurement: per-execution CU ≈ 70k + 107·units with p99 residual 130–206k
  (calibration, `docs/proof/p2/cu-cal*.md`). So executions can still exceed the 400k task budget; **the keeper's
  fallback `tick()` at 1.4M CU completes such an execution** and re-schedules the task (§3).

### 0.3 Bot pacing (bot constants only)
`RACE_PACE_PERMILLE` (race-only stick scale). Final values in §9 / `docs/proof/p2/qa/`.

### 0.4 Program hygiene
`finalize_match` only from Completed / Aborted; VRF identities are `const`; neither `.so` imports
`sol_remaining_compute_units` (checked on the binaries).

## 2. Accounts and instructions as built
See `programs/fall_rush/src/sim.rs` (module doc) and `lib.rs`. Deviations from the prompt's table:
- Map blobs are program-owned **keypair** accounts (not `["map", key]` PDAs: a > 10 KB PDA cannot be created by CPI in
  one instruction); `Arena::maps` pins their keys; `seal_map` checks the sha256 and that the blob parses.
- `Match` is created per match (no pre-made pool yet: creation + delegation take ≈ 2.5 s on devnet, measured).
- Session keys (`SessionTokenV2`) are **not** integrated: `join_lobby` binds the signer itself as the slot owner.
  Expired / revoked session tokens are therefore untested (UNVERIFIED, P3).
- `tick()` takes every account in `remaining_accounts` and validates by hand, so a bad list is a no-op, never an error.

## 3. Keeper (`tools/keeper/run.ts`)
Polls the World every 200 ms (+ `onAccountChange` for the recorder). Stall = no (tick, slice) progress for 500 ms →
cancel every task it scheduled, **drive mode** (streams `tick()` at 1.4M CU for a few seconds), re-schedule with a new
nonce. Commits each resolved round once (`commit_round`), then `end_sim` + `finalize_match`. Confirms by polling
`getSignatureStatuses`. Log: `docs/proof/p2/keeper-<run>.jsonl`.

## 4. Per-round enablement
All four rounds run on-chain in the same arena (one match = Race → Survival → Skill → Final). See §5.

## 5. Devnet runs
TBD

## 6. Replay
TBD

## 7. Security (local stack, `tools/keeper/security.ts`)
26 / 26 pass (`docs/proof/p2/security-local.json`).

## 8. Cost
TBD

## 9. UNVERIFIED
TBD

## 10. Inputs to P3
TBD
