# P0: feasibility spikes for the fully on-chain plan (one session, alone; runs in parallel with PB0)

You are the Solana/Rust performance engineer on **Fall Rush**, a Unity 6.3 WebGL 12-player knockout party game for the MagicBlock Solana Blitz V10 hackathon. On 2026-11-21 the user approved moving the whole game simulation onto a MagicBlock Ephemeral Rollup:
- a timed task (the rollup's scheduler, "crank" in the SDK) calls `tick()` 20 times a second;
- players write their own input accounts;
- browsers subscribe to a `World` account.

The plan is **`/Users/samya/Downloads/FallRush/docs/19-ONCHAIN-PLAN.md`**. Read it in full first, especially §1, §3, §4.1, §5 P0, §9 and §11.

**P0 is the go / reduced / no-go gate for the whole plan.** Your job is to **measure**, not to build the game. All code here is throw-away spike code, kept for the record.

**Two other sessions run at the same time:**
- **G4.13** (Unity fixes, on `main`);
- **PB0** (SDK upgrade of the real program + `rush-core` skeleton, in `../FallRush-pb0`).

The parallel rules in §1 are mandatory.

## 0. State at hand-off (2026-11-21)
- **Repo** `/Users/samya/Downloads/FallRush`, branch `main`.
  - G4.13 is still running there (WebGL build #5, measurements, soak).
  - `docs/19-ONCHAIN-PLAN.md`, this prompt and `scripts/heavy-lock.sh` are **uncommitted on main**. Read them from the main checkout path.
- **Toolchain on this Mac:**
  - Anchor 1.0.2 (via `[toolchain]`), Solana CLI 3.1.x, `mb-stack` + `vrf-oracle` (see `scripts/m0-test-local.sh`, `scripts/local-env.sh`);
  - Rust targets `wasm32-unknown-unknown` installed;
  - Node + pnpm. The browser automation already in the repo is `scripts/g47-webgl-measure.mjs`; reuse its library.
- **Devnet:** wallet `~/.config/solana/id.json`, balance **0.853 SOL** (2026-11-21).
  - A spike program costs about 7 SOL per MB of `.so` in rent. Keep it lean (no IDL build for the hot program if size matters; release profile `opt-level="z"` if needed).
  - **Before any devnet deploy, check `solana balance`. If too low, stop and ask the user to fund** (faucet.solana.com).
  - PB0 also needs about 3.4 SOL for its upgrade, so tell the user the total you need.
- **Devnet rollup:** `https://devnet-as.magicblock.app`, validator `MAS1Dt9qreoRMQ14YQuhg8UTZMMzDdKhmkZMECCzk57` (core 1.1.1). Delegation program `DELeGGvXpWV2fqJUhqcF5ZSYMS4JTLjteaAMARRSaeSh`.
- **Measured before (M0, from this Mac):**
  - rollup send → confirm p50 227 ms (≈ network RTT);
  - VRF in the rollup 426 ms;
  - the rollup sometimes drops websocket signature notifications, so **confirm by polling `getSignatureStatuses`**.
- **Research findings** (doc 19 §11; verify each against the source before relying on it):
  - SDK **0.18.x** works with Anchor 1.0.2 + validator 1.1.1. Don't use 0.20.
  - Timed tasks: CPI `MagicBlockInstruction::ScheduleTask(ScheduleTaskArgs { task_id, execution_interval_millis, iterations, instructions })` from `magicblock-magic-program-api`, fee-free, about 10 ms granularity.
    - **On 1.1.1, task ids are shared across all programs. A colliding id is accepted, then silently never runs**, so derive a unique id.
  - Rollup limits: 200k CU per instruction, 1.4M per transaction, 64 KB transactions, delegated accounts ≤ 10,240 B to commit back.
  - Devnet rate limits: **UNVERIFIED**.
  - Read MagicBlock's crank docs (`docs.magicblock.gg/pages/tools/crank/implementation.md`) and the crank example in `magicblock-labs/magicblock-examples` before writing P0.2.
- **What the real game needs** (to make the spike realistic):
  - 12 players;
  - capsule r 310 mm, h 1,000 mm, skin 10 (`packages/shared/config/movement_v2.json`);
  - collisions only against **OBB boxes, convex prisms (turn tables, bumpers), spheres (rolling balls) and capsules** (as built by `client-unity/Assets/_Game/Editor/KitBuilder.cs` and the map builders);
  - the motor does capsule sweeps + raycasts + depenetration, with ≤ 3 slide iterations (`client-unity/Assets/_Game/Scripts/Shared/KinematicMotor.cs`, `IMotorWorld.cs`);
  - Lid Flip has 9×9 + 5×5 lids + 1 sweeper (≈ 120 colliders); Capsule Run is the heaviest (≈ 300 colliders, **UNVERIFIED**; count them from the map builder).

## 1. Rules (parallel-session rules first)
- **Work in your own worktree:** `git worktree add ../FallRush-p0 -b p0 main`. Never edit, stage or commit anything in the main checkout; G4.13 owns it.
- **You own** `spikes/**`, `tools/load/**`, `docs/proof/P0-feasibility.md`, `scripts/p0-*.sh`.
  - **Never touch:** `programs/**`, `crates/**`, `docs/07-*` (PB0 owns them), `client-unity/**`, `PROJECT_STATUS.md`, `docs/15-*`.
  - Put status and decision updates in a section "For main (merge later)" at the end of your proof.
  - Your spike program lives in **`spikes/p0/`**: its own Anchor workspace, own `Cargo.toml`, own new program keypair (gitignored). It pins ER SDK **0.18.x** itself, so you don't wait for PB0.
- **8 GB M2 Air: one heavy job at a time, across all sessions.** Wrap every heavy command in the shared lock:
  `bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p0-<what> -- <command>`
  - Heavy means: `anchor build` / `cargo build-sbf`, the local MagicBlock stack (fixed ports 8899/7799/6699; PB0 uses the same ports, so **never** start it outside the lock), devnet deploys, load-test runs, any Chrome run, Docker.
  - The lock also waits while any Unity / QA / soak / browser-measure job runs.
  - Plain `cargo test` of the small no_std spike sim crate may run without it.
  - Never bypass or delete the lock, and never kill another session's process.
  - A long devnet run (P0.2, 10 minutes) holds the lock for its whole duration. That's correct, because machine load would skew the timing.
- **No VPS / Hostinger / SSH. Never push, never add a remote. No Unity in this phase.** P0.6 tests WASM in Node and in Chrome. The Unity-WebGL integration of WASM is P3 and is marked UNVERIFIED here.
- **Integer maths only** in the sim crate: `#![no_std]`, `#![deny(clippy::float_arithmetic)]`.
- Keypairs never go in git. Never print a secret key.
- A number counts as measured only if you ran it. Anything else is **UNVERIFIED**.
- Every number goes in `docs/proof/P0-feasibility.md` with the command, date, machine load (`sysctl -n vm.loadavg`) and sample size.
- Commit on branch `p0` with your own paths: `git commit -- <paths>`. End every commit message with the attribution lines from the system reminder. Never push.

## 2. Spike code to write (minimum needed to measure honestly)

### `spikes/p0/sim`: a `no_std` integer sim crate, about 1 day of work, not the real `rush-core`
**State:**
- `Player { pos: [i32;3] mm, vel: [i32;3] mm/s, yaw: u8, grounded: bool, timers }`;
- `World { tick: u32, players: [Player; 12], seed: u64 }`.

**Step per tick (20 Hz, dt = 50 ms):**
- input → horizontal accel (run 5,000 mm/s, accel 28,000 mm/s²), jump (apex 1.2 m), gravity 20,000 mm/s² with a 1.35× fall multiplier;
- **swept capsule vs OBB / prism / sphere with up to 3 slide iterations**, ground snap 250 mm, step 250 mm;
- player-vs-player soft push (66 pairs);
- one rotating sweeper (pose = f(seed, tick), integer sin table).

The point is the **compute cost shape** of the real motor, so the collision maths must be the real kind (swept tests + slides), not a cheap stand-in. Note the shortcuts you took.

**Map:** a deterministic generator for (a) a **Lid Flip-like** set of about 120 colliders and (b) a **Capsule Run-like** set of about 300 (OBBs + 20 prisms + 6 spheres), plus a **uniform-grid broadphase** (2 m cells) built offline into a byte blob.

**Bots:** a scripted input generator `input(slot, tick, seed)` (random-walk + periodic jumps), so the load is realistic: some players airborne, some colliding.

**Native micro-benchmark:** time per step, number of narrowphase tests per player per tick.

### `spikes/p0/programs/p0_tick`: the spike program (Anchor 1.0.2, ER SDK 0.18.x)
**Accounts:**
- `World` (zero-copy, ≤ 2 KB, delegated);
- 12 × `InputBuf` (PDA per slot, ~128 B, delegated, ring of 8 inputs keyed by tick);
- `MapBlob` (not delegated, cloned read-only into the rollup);
- `TickLog` (ring of the last 512 `{tick, slot, unix_ms}` entries, for P0.2).

**Instructions:**
- `init`, `delegate_*`, `upload_map_chunk`;
- `submit_input(slot, batch)`, which writes only that slot's `InputBuf`;
- `tick()`: read inputs, run `sim::step`, write `World`, append to `TickLog`, and log compute units with `sol_log_compute_units()` before and after the step;
- `run_steps(n, seed)`: runs n steps from a seed and writes the World hash, for P0.6;
- `schedule_ticks(interval_ms, iterations)`: the timed-task CPI with a unique `task_id = hash(program, world, nonce)`;
- `cancel_ticks`.

Build it with the localnet and devnet variants of the queue / validator constants, like `programs/fall_rush` does.

### `tools/load`: Node TS clients
- 12 input clients and N spectator "bet" clients (a dummy instruction that writes a 64 B account), each with its own keypair, funded from the wallet. Plain keypairs are fine here; session-key overhead is one extra read-only account, so note it.
- Confirm by polling `getSignatureStatuses` (never wait on websocket notifications).
- A subscriber: `onAccountChange(World, "processed")` logs `(world.tick, receive_ms)`.
- A tiny fanout relay (`ws` server that re-broadcasts World bytes), for P0.4.

## 3. The six measurements, in this order

Run each **first on the local stack** (to debug cheaply), **then on devnet-as** (which is what counts). Write each result into the proof right away.

| # | What | Method | PASS |
|---|---|---|---|
| **P0.1** | **Compute units per tick** | `tick()` at 12 players on the 120- and 300-collider maps, ≥ 200 ticks each, with bot inputs. Report CU p50 / p95 / max for the step only and for the whole instruction. Also the bot-brain headroom: CU left for bots running inside the tick (P2.6 option A). | ≤ 150k CU p95 on 120 colliders; ≤ 200k max on 300 colliders, **or** a measured 2-instruction split that fits |
| **P0.2** | **Timed task at 20 Hz** | `schedule_ticks(50, 12000)` on devnet-as for **10 minutes**. From `TickLog` + subscriber logs: the gap between ticks (p50 / p95 / p99 / max), missed ticks, drift. Also try 33 ms (30 Hz) and 66 ms (15 Hz) for 2 minutes each. Verify the unique-task-id behaviour. Test the fallback where anyone may call `tick()` once `now ≥ next_tick_time`. | ≥ 95 % of ticks within ±1 slot of the schedule; no gap > 250 ms |
| **P0.3** | **Input load** | 12 clients × `submit_input` every 100 ms (2 ticks per batch) + 10 bet transactions/s, for 5 minutes on devnet-as, while the timed task runs. Count rejects / errors by type (watch for 429 / rate limiting); send → confirm p50 / p95. Then double the rate once to find the headroom. | 0 rate-limit rejects at the base rate; confirm p95 ≤ 150 ms (state the network RTT baseline you measured) |
| **P0.4** | **State delivery** | Subscriber in Node **and** in headless Chrome (a static page using the TS SDK). Updates received per second vs ticks produced; World tick gaps. The same through the fanout relay (added latency). | ≥ 18 of 20 updates/s received; relay adds ≤ 20 ms |
| **P0.5** | **End-to-end latency** | A client sends input stamped `tick T` and measures the arrival of the first World update whose `InputBuf.applied_tick[slot] ≥ T` (add that field). 1,000 samples. | p50 ≤ 150 ms, p95 ≤ 250 ms |
| **P0.6** | **sBPF = native = WASM** | 100 seeds × 200 steps (both maps): World hash from `run_steps` on the local rollup (sBPF), from native `cargo test`, and from `wasm32-unknown-unknown` in Node **and** in Chrome. Compute-unit cap: split `run_steps` into chunks if needed. | 100 % identical hashes on all four |

**Time boxes:** P0.1 one day including the sim; P0.2–P0.5 half a day each; P0.6 half a day. If a spike blows its time box, write down what you have and move on; don't polish.

## 4. Decision write-up
In `docs/proof/P0-feasibility.md`:
1. The results table (measured numbers, PASS / FAIL, conditions).
2. A recommendation: **GO** (full plan), **REDUCED** (which §4.1 fallback, with numbers), or **NO-GO** (back to the verifiable-server plan). Include the tick rate to use (20 / 15 / 30 Hz) and **bots on-chain vs off-chain**, with the compute-unit numbers that justify it.
3. Inputs to P1 / P2:
   - the compute-unit cost per collision primitive;
   - broadphase cell size;
   - World / InputBuf layouts that worked;
   - the timed-task API exactly as used;
   - any rollup quirks found.
4. "For main (merge later)": the doc 15 row **P0-1** (open → the decision, **for the user to approve**), and the `PROJECT_STATUS.md` lines.
5. Devnet cost: the SOL spent, and the program / account addresses (close spike accounts at the end and record the refund; keep the program deployed until the user says otherwise).

Commits on `p0`:
- `feat(p0): spike sim + p0_tick program + load tools`
- `test(p0): measurements`
- `docs(p0): feasibility proof + recommendation`

Never push.

## 5. Report to the user (short, plain words)
- The 6 numbers, each with PASS / FAIL.
- Your recommendation (GO / REDUCED / NO-GO) and the tick rate, in one paragraph.
- SOL spent, and what's still deployed.
- UNVERIFIED items (for example Unity-WebGL WASM, mainnet behaviour).
- **The decision is the user's.** Ask them to approve P0-1 before P1 starts. Ask before merging branch `p0` or removing the worktree.
