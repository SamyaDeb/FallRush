# P2: the on-chain simulation (the program becomes the game server)

You are the Solana / Rust engineer on **Fall Rush**, a Unity 6.3 WebGL 12-player knockout party game for the MagicBlock Solana Blitz V10 hackathon. The plan is to run the **whole game simulation on a MagicBlock Ephemeral Rollup**:
- a timed task calls `tick()`;
- each player writes their own input account;
- the program, using **`rush-core`** (our integer Rust core, already built and verified in P1), decides every movement, hazard, elimination and round result.

P2 makes that real on devnet-as with **12 bots and no humans**. P3 connects the Unity client and P4 adds the economy, so neither is in scope here.

**Read first, in full** (all on `main`):
1. `docs/19-ONCHAIN-PLAN.md`: §1, §3, §4.1, §5 P2, §6, §9.
2. `docs/proof/P0-feasibility.md`: §2 recommendation, §3, and **§4, which holds the hard limits and the exact task-scheduling API that worked**.
3. `docs/proof/P1-rush-core.md`: §4 parity, §10 QA, **§12 compute-unit outlook**, OC-3 sign-off, "For main".
4. `docs/proof/PB0.md`: "For main", especially the VRF callback compute-unit trap.
5. `docs/07-MAGICBLOCK-INTEGRATION.md`; `programs/fall_rush/src/{lib.rs,state.rs}`; `crates/rush-core/src/**`.

---

## 0. State at hand-off (2026-11-21, checked)

- **`main` @ `422e5e6`**: PB0, P0 and P1 are all merged. No other phase session is running.
  - Old worktrees `../FallRush-p0`, `../FallRush-p1`, `../FallRush-p1g`, `../FallRush-p1h` may still exist. **Don't use or delete them; ask the user.**
- **Program `programs/fall_rush`:**
  - Anchor `=1.0.2`, `ephemeral-rollups-sdk =0.18.0` (`anchor`, `vrf`);
  - program ID `4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4` on devnet (upgrade authority = `~/.config/solana/id.json`); `.so` 482 KB;
  - lifecycle 10/10 local, devnet-as green on 0.18.
  - The **VRF callback identity must be a `const` item**. A runtime PDA helper in a constraint costs more than 200k CU (PB0). The same applies to any `const fn` PDA helper.
- **`rush-core`** (`crates/`): integer `no_std`. Native = WASM = sBPF = Unity FFI on every golden vector. Map blobs for 6 maps in `maps/` (hashes in `maps/map_blob_hashes_v1.txt`):

  | Map | Blob size |
  |---|---|
  | Capsule Run (`BlockPartyDash`) | **53.7 KB** |
  | Goo Tower | 43.7 KB |
  | Lid Flip (`CrumbleShelf`) | 18.4 KB |
  | Star Fountain (`StarScramble`) | 17.8 KB |
  | Sandbox | 28.0 KB |
  | Lobby | 7.0 KB |

  - P0 proved a **28.8 KB** read-only clone into the rollup. **53.7 KB is UNVERIFIED.**
  - `rush-sim` CLI: run / soak / qa / trace / counters / mapblob / vectors.
- **Decisions in force:**
  - **P0-1 = REDUCED / conditional GO** (doc 15):
    - whole sim on the rollup, **bots on-chain**;
    - timed task + permissionless keeper + a `NotDue` tick fallback;
    - **tick ≤ 300k CU worst case**;
    - own-player prediction + 2–3 tick input delay;
    - a fallback **round by round** (Final → Skill → Survival → Race) if Capsule Run can't meet the budget.
  - **OC-3, the feel sign-off: the user chose 30 Hz** ("30 OK, 15 a bit laggy… okk then 30 is fine"). This **overrides P0-1's 20 Hz. The on-chain tick rate is 30 Hz.**
- **Hard limits P0 measured** (P0 §4):
  - **a timed-task execution has 400k CU total, shared by all its instructions**;
  - **one failed execution ends the task forever**;
  - a duplicate `task_id` is silently ignored;
  - the task's transaction runs our instruction as a CPI from `Crank1111…` with **no signers** (payer = the validator);
  - devnet-as slots are 10 ms;
  - the rollup can't clone a program that imports `sol_remaining_compute_units`;
  - the public devnet base RPC returns 429 errors above ~1.5 tx/s;
  - end-to-end p95 latency was 337 ms, confirm p95 270 ms;
  - one client sent 253 tx/s with 0 rejects.
- **Measured `rush-core` cost (P1 §12):** **≈ 493k CU per 12-player tick** (LiteSVM, random inputs), **≈ 27k CU per player-tick for the motor alone**. That is over both the 400k task budget and the 300k condition, so it has to come down before anything else (task 0.2).
- **Open QA misses at 30 Hz (P1 §10):**
  - Capsule Run Normal median **68.7 s** (target 85–110 s; the bots are too fast);
  - Goo Tower escape **4.79** (band ≥ 4.80).

  Lid Flip and Star Fountain pass.
- **Devnet wallet: 3.75 SOL.** The program will grow with `rush-core` linked in. An upgrade needs a temporary buffer of the full `.so` size (~7 SOL per MB) plus `solana program extend` for the extra bytes. **Estimate the need after the first build. If the balance is short, stop and ask the user to fund** (faucet.solana.com).

---

## 1. Rules
- **Worktree:** `git worktree add ../FallRush-p2 -b p2 main`. Work only there; never edit or commit in the main checkout.
- **You own (on `p2`):**
  - `programs/**`, `crates/**` (for the carry-over work in §2);
  - `tools/keeper/**`, `tools/replay/**`, `tools/p2/**`;
  - `packages/shared/config/movement_v4.json`;
  - `docs/07-*`, `docs/proof/P2-*.md`, `docs/proof/p2/**`, `scripts/p2-*.sh`;
  - **plus one final doc commit** to `docs/15`, `docs/19` and `PROJECT_STATUS.md` (§4).
  - **Don't touch** `client-unity/**` (P3) or the Classic server code.
- **8 GB M2 Air, one heavy job at a time.** Wrap heavy work in
  `bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p2-<what> -- <command>`.
  - Heavy means: `anchor build`, `cargo build-sbf`, LiteSVM runs, the local MagicBlock stack (fixed ports), devnet runs, `rush-sim` runs longer than about 1 minute, load clients.
  - Use `CARGO_BUILD_JOBS=2 nice -n 10` for everyday `cargo test`.
- **No VPS / Hostinger / SSH. Never push, never add a remote.** No Unity.
- **Throttle base-layer (Solana devnet) transactions to ≤ 1.5 tx/s** (429s). Rollup transactions are not throttled.
- **Never break determinism.** Every change to `rush-core` regenerates and re-checks every golden vector on **native, WASM and sBPF**, and keeps the FFI parity test. Classic mode's C# code is untouched.
- **The hot tick never fails:**
  - no `require!` / `?`-returned error on any path reachable during normal play;
  - bad or missing input → repeat the last input;
  - not due / wrong phase → `NotDue` no-op that returns `Ok`;
  - budget exhausted → the deterministic degradation from 0.2.
- Keypairs never go in git. Pin every new dependency exactly (`=x.y.z`), with a justification.
- A number counts as measured only if you ran it. Anything else is **UNVERIFIED**.
- Commit on `p2` with your own paths: `git commit -- <paths>`. End every commit message with the attribution lines from the system reminder.

---

## 2. Carry-over fixes from P0 / P1 (do these first, in order; each is a gate)

### 0.1 Tick rate = 30 Hz everywhere
1. `movement_v4.json`: `tickRateHz: 30` as the shipped default (the 15/20 tables stay as reference).
2. Every P2 number (director tick counts, input windows, task interval **33 ms**) uses `tick_rate_hz` from the config, never a literal.
3. **Inputs:** 12 × 30 = 360 inputs/s. Batch **3 ticks per input transaction** (120 tx/s total, under P0's 253 clean), each input stamped `T+3`. `InputBuf` ring ≥ 16 entries.
4. Commit `chore(p2-0.1): 30 Hz default (OC-3)`.

### 0.2 Compute-unit pass: a deterministic per-tick work budget (the critical gate)
**Goal:** a full 12-player `step()` costs **≤ 300k CU worst case** on every real map at 30 Hz, measured in sBPF. Apply in order, measuring after each (LiteSVM, the real map blobs, `counters` on):
1. **Cheap rejects first:**
   - a per-player swept-AABB reject before any narrowphase;
   - per-moving-hazard AABB reject;
   - bounding circles / spheres before prism SAT;
   - **no `isqrt` on a miss**;
   - normals only for the final hit (P0 §4.1).
2. **Fewer bisections:** time-of-impact search 8 → 4 iterations (or a closed-form time of impact for sphere/capsule vs plane faces). Re-run the trace comparison (P1 L) to check outcomes still agree.
3. **Touch test before closest-feature:** run the cheap overlap test first, and closest-feature only on overlap.
4. **Build profile:** `opt-level = 3` and **overflow-checks off** for `rush-core` only (per-package profile in `programs/Cargo.toml`), with `z` for the rest. Record the `.so` size.
5. **A deterministic work budget** (only if still over):
   - fixed caps on narrowphase tests per player per tick and on slide iterations per tick, counted (never `sol_remaining_compute_units`);
   - when a cap is hit, the player keeps the last safe position, the same on sBPF, WASM and native;
   - log a `budget_hit` counter in World.
6. **Stress vectors:**
   - `pileup_v1`: 12 players driven into one wall corner on each map, plus 12 players on a moving platform / turntable;
   - a native **100k-tick soak** per map with bot inputs;
   - CU max per map in LiteSVM.

**Gate:** p95 and **max ≤ 300k CU** on all 4 round maps + Lobby, golden vectors regenerated and parity green everywhere, trace outcomes unchanged (P1 L).

**If the gate can't be met on some maps**, measure and present both options to the user, and **stop and ask**:
- **(a) split tick:** one task at 2× rate alternating `tick_part(0)` / `tick_part(1)` (6 players each), driven by a `part_pending` state in World. The full step = part 0 ∘ part 1, and must give exactly the same results as the unsplit step on native / WASM (prove it with vectors). 60 executions/s scheduling is **UNVERIFIED**; P0 measured up to 30 Hz, so measure it on devnet;
- **(b) round by round:** those maps stay on Classic for now (P0-1).

Commit `perf(p2-0.2): rush-core work budget (<= 300k CU)`.

### 0.3 Bot pacing at 30 Hz (on-chain bots define round length)
1. Tune **bot constants only** in `rush-core` (never maps, hazard timing or the motor feel the user signed off):
   - Capsule Run Normal median into **85–110 s**;
   - Goo Tower escape **≥ 4.80** of 8.
2. Re-run `rush-sim qa` at 30 Hz on all 4 maps (100 runs each) and the 20-match soak. Every other G4.7 target must still hold.
3. Commit `fix(p2-0.3): bot pacing at 30 Hz`.

### 0.4 Program hygiene
1. Remove the M0 shortcut `finalize_match` from `RoundResolved`. The on-chain director replaces it.
2. Every VRF callback / PDA helper is a `const` item (PB0).
3. **Don't import `sol_remaining_compute_units` anywhere**, or the rollup can't clone the program (P0).
4. Commit `chore(p2-0.4): program hygiene`.

---

## 3. P2 build (doc 19 §5 P2, updated with P0 / P1 lessons)

### P2.1 Accounts (zero-copy, `#[repr(C)]`, each delegated account ≤ 10,240 B)

| Account | Seeds | Delegated? | Content |
|---|---|---|---|
| `Arena` | `["arena", id]` | yes, reused across matches | status, current match id, ruleset + map-pool hashes, roster 12 × {kind (human / bot), authority, session signer, cosmetic, input acc}, task ids, keeper nonce, counters |
| `WorldAcc` | `["world", arena]` | yes, reused | `rush_core::World` bytes (≤ 2 KB) + `applied_tick[12]` + last slot + ticks run + `budget_hit` + `part_pending` (if 0.2a) + world hash |
| `InputBuf` | `["in", arena, slot]` | yes | owner (session signer), ring ≥ 16 × {tick u32, input 4 B}, latest tick |
| `EventLog` | `["ev", arena]` | yes | ring of the last N transcript events (≤ 8 KB) for spectators |
| `Match` (existing, extended if needed) | `["match", id]` | **fresh per match**, created on Solana from a pre-made pool | deal, slots, champion, transcript; **receives the commits**: 4 round commits + 1 commit + undelegate = 5, under the 10-commit limit without a fee payer |
| `MapBlob` | `["map", key]` | **no** (read-only clone) | blob bytes, uploaded in ≤ 900 B chunks, `seal_map` checks sha256 against `maps/map_blob_hashes_v1.txt` |

**The `World` / `Arena` / `InputBuf` / `EventLog` accounts are never committed during play.** They live in the rollup, which avoids the commit limit for reused accounts. Only the per-match `Match` is committed.

**Capsule Run's 53.7 KB blob clone is UNVERIFIED; test it first in P2.1.** If it fails, split the blob into per-section accounts (`["map", key, part]`) and load only the parts the grid needs.

### P2.2 Instructions

| Instruction | Signer | Notes |
|---|---|---|
| `init_arena`, `delegate_arena` (+ World, InputBufs, EventLog) | game authority | once per arena |
| `upload_map_chunk`, `seal_map` | game authority | base layer, throttled |
| `create_match_from_pool`, `delegate_match` | game authority / keeper | from a pool of pre-created `Match` accounts (avoids the 17 s base delays at match start) |
| `join_lobby(cosmetic)` | player wallet, or a session key (`SessionTokenV2`, session-keys 3.1.1) | assigns a slot + InputBuf owner. For P2, tests use plain keypairs + session tokens |
| `submit_input(slot, batch[3])` | that slot's session signer | writes `InputBuf` only; sanitises (`rush-core` port); rejects ticks outside `[World.tick − 8, World.tick + 8]` (a real error is fine here: it's not the hot tick) |
| **`tick()`** | **none** (timed-task CPI from `Crank1111…`), or anyone in the fallback | reads `WorldAcc` + 12 `InputBuf`s + `MapBlob`(s) + `Match`, runs `rush_core::step` (bots inside, option A), writes World + `applied_tick` + `EventLog`, writes results into `Match` (qualified / eliminated / champion / transcript). **Never fails** (§1). `NotDue` when `now < next_tick_time` or the phase doesn't tick |
| `request_deal` / `deal_callback` | existing (VRF in the rollup) | the VRF seed → `rush_core::deal` → World (map plan, modifiers, hazard seeds, bot tiers). The callback identity is `const` |
| `schedule_ticks`, `cancel_ticks` | game authority or keeper | hand-built `MagicBlockInstruction::ScheduleTask` / `CancelTask` CPI **exactly as P0 §4.3** (`magicblock-magic-program-api =0.10.1` + `bincode =1.3.3`); unique `task_id = FNV-1a64(program ‖ world ‖ nonce) & i64::MAX`; interval 33 ms |
| `commit_round(round)` | keeper (any payer; checks `Match` state) | the intent-bundle commit of `Match` when World says round `r` is resolved. **Commits are never inside `tick()`**, because the task has no signer payer |
| `finalize_match` | keeper | commit + undelegate `Match` when World says Champion; frees the arena for the next match |
| `abort_arena` | game authority | cancels the task and marks the match aborted |

### P2.3 Keeper (`tools/keeper`, TS, permissionless; anyone could run it)
1. Watches each arena's World (`onAccountChange` + `getAccountInfo` polling every 1 s as a backup).
2. **If no tick for 500 ms:** cancel + re-schedule with a new nonce (≤ 1 s recovery), and meanwhile send fallback `tick()` transactions (they no-op as `NotDue` if the task is alive).
3. Sends `commit_round` / `finalize_match` at phase edges, `request_deal` at Dealing, and pulls the next `Match` from the pool. Idempotent on retries; confirms by polling `getSignatureStatuses` (never websocket only).
4. Writes `docs/proof/p2/keeper-<run>.jsonl` (every action and its latency).

### P2.4 The director on-chain
- Lobby countdown → roster lock (bots fill free slots) → Dealing (VRF) → Dealt → per round Intro / Countdown / Rush / Resolve / Intermission → Champion → Receipt → back to Lobby.
- All tick counts come from `rush-core`'s director at 30 Hz.
- Round results come from `rush-core` rules, so **the program decides eliminations**.
- Minimum-advance (M4-8) and the elimination reasons (M4-9) come from the core.

### P2.5 Rounds, enabled one at a time
Enable in this order: **Lid Flip → Star Fountain → Goo Tower → Capsule Run**. Each map is its own commit. For each:
- a local-stack run;
- a devnet-as run of that round;
- CU max per tick from the logs;
- a replay check (P2.7).

### P2.6 Bots
Option A: inside `step()`, via `rush-core` `bot_input`. Bots are flagged `kind = bot` in the roster and **in the roster hash**.

### P2.7 Replay + recorder (`tools/replay`)
1. **Recorder:** while a match runs, archive every `submit_input` transaction's data and every World update (rollup transaction retention is UNVERIFIED, so don't rely on it) into `docs/proof/p2/runs/<match>/`.
2. **Replay:** re-run `rush-core` natively (`rush-sim`) from the VRF seed + recorded inputs (applied by tick). Compare the World hash **every tick**, every round's transcript hash, and the final `Match` state on Solana.

### P2.8 Security suite (local stack + LiteSVM)
- An input signed by another slot's key is rejected.
- An expired or revoked session token is rejected.
- Input ticks out of window are rejected.
- Sanitiser clamps hostile values.
- Double join.
- Claiming a bot slot.
- An early `tick()` is a `NotDue` no-op.
- `tick()` with wrong / missing / reordered remaining accounts is a **no-op, not a failure**. Check how a wrong account set behaves when it comes from the task; it must never kill the task.
- An arena can't re-enter an old match.
- A VRF callback from a non-VRF signer is rejected.
- `commit_round` / `finalize_match` before the World phase allows it is rejected.
- A duplicate `task_id` is detected (the keeper reads the task state).
- 10+ commits are impossible per `Match`.
- Any hostile `remaining_accounts` length.

### P2.9 Verify (the exit gate of P2)
1. **Local stack:** a full 12-bot match (4 rounds) ×3, unattended.
2. **Devnet-as at 30 Hz:** a full 12-bot match **×5 in a row, unattended**, through the keeper.
   - Record per run:
     - tick gap p50 / p95 / max;
     - missed ticks;
     - task restarts by the keeper;
     - **CU per tick p50 / p95 / max per map**;
     - match length vs Classic (≈ 400 s);
     - every signature (create, delegate, VRF, commits, finalize).
   - **Pass:** 5/5 complete, CU max ≤ 300k (or the approved split), 0 World corruption, replay identical on 5/5.
3. **Security suite green.**
4. **Devnet cost table** (deploy, upgrade, extend, rent, per-match cost) and the final balance.

---

## 4. Proof + doc updates
1. `docs/proof/P2-onchain-sim.md`:
   - §1 the carry-over results (0.1–0.4, with before / after CU tables);
   - §2 accounts and instructions as built;
   - §3 keeper;
   - §4 the per-round enablement table;
   - §5 the devnet 5-run table;
   - §6 replay;
   - §7 security;
   - §8 cost;
   - §9 UNVERIFIED;
   - §10 "Inputs to P3": the World byte layout, `applied_tick` use, input delay T+3 and batching, account addresses, how the client finds the arena.
2. **One final doc commit** on `p2` (`docs(p2): proof + 30 Hz / P0-1 updates`):
   - `docs/19`: 20 Hz → 30 Hz in §3 + P2;
   - `docs/15`: OC-3 note "signed off at 30 Hz", P0-1 tick rate amended to 30 Hz with the evidence, a new row for the work-budget / split decision if one was taken;
   - `PROJECT_STATUS.md`: the P2 line.

   **Never push.**

## 5. When to stop and ask the user
- Task 0.2 can't reach 300k on some maps (present split-tick vs round-by-round with numbers).
- Bot tuning in 0.3 would need motor / feel changes.
- The devnet balance is short (say how much is needed and why).
- The 53.7 KB blob can't be cloned and splitting changes the plan.
- Anything that would touch Unity, the VPS or main.

## 6. Report to the user (short, plain words)
- Carry-over results: CU per tick before / after per map, the bot pacing fix.
- What runs on-chain now, and the 5 devnet matches: complete? CU max? tick gaps? keeper restarts?
- Replay result, security suite, SOL spent.
- UNVERIFIED items; what P3 needs.
- Ask before merging `p2` into `main` and before removing the worktree.
