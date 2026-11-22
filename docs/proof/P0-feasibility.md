# P0 — Feasibility spikes for the fully on-chain plan (proof)

**Branch:** `p0` (worktree `../FallRush-p0`), never pushed. **Date:** 2026-11-21.
**Plan:** `docs/19-ONCHAIN-PLAN.md` §5 P0. **Prompt:** `docs/prompts/P0-PROMPT.md`.
**Machine:** MacBook Air M2 8 GB. The machine was shared with other sessions and unrelated projects all day: load average 12–77 (recorded per run below). Client-side numbers (send rates, confirm/receive times) carry that noise. On-chain numbers (CU, slots, tick counts) do not.
**Rollups:** devnet-as `https://devnet-as.magicblock.app` (`getVersion`: magicblock-core **1.1.1**, solana-core 4.0.0, git 8886db2) and the local stack (`mb-stack`, ephemeral-validator npm 0.14.10).
**Program:** `p0_tick` `8z4ofj7GeGLArXaYjbV2QXrdUqDdbutavqQSWXqpQBxy` (Anchor 1.0.2, ER SDK 0.18.0), spike code in `spikes/p0/`, clients in `tools/load/`.

Every number below was measured by a command in this file. Anything else is marked **UNVERIFIED**. Raw JSON for every run is in `spikes/p0/.p0-logs/` (gitignored; the important values are copied here).

---

## 1. Results

| # | What | Measured (devnet-as unless noted) | Target | Verdict |
|---|---|---|---|---|
| **P0.1** | CU per tick, 12 bot players | **Single instruction:** step p50 **142k** / p95 **218k** / max 246k (120 colliders); p50 155k / p95 214k / max 247k (300 colliders). **2-instruction split:** per instruction max **194k** (120) / **180k** (300), p95 141k / 136k. Bots +6–12k. 200 ticks each. **Long tail:** a whole tick reaches **402k** (seen at tick 7,967 of seed 11); about 0.2 % (120) / 1.8 % (300) of ticks are in that range (native proxy, 2×200k ticks) | ≤150k p95 (120); ≤200k max (300), or a split that fits | **PASS only with the split, per instruction.** The real limit turned out to be the timed task's **400k per execution**, and the tail exceeds it. See §3.1 |
| **P0.2** | Timed task at 20 Hz | Interval held to **20.00 Hz**; receive gap p50 50 / p95 59–62 / p99 71–87 / max 222–249 ms; **99.8 %** of ticks within ±1 slot (10 ms slots); drift −44…−53 ms over 398 s. **But** both 10-min runs **stopped at tick 7,967**: one over-budget tick (402k > 399.7k) **ends the task**. 30 Hz: 3,637/3,637, p95 42 ms. 15 Hz: 1,819/1,819, p95 76 ms | ≥95 % within ±1 slot; no gap >250 ms | **Timing PASS, robustness FAIL** (a task dies on its first failed execution; a keeper is needed) |
| **P0.3** | Input load | **0 rejects of any kind, 0 rate limits (no 429)** in 46,000+ rollup transactions. Best clean run: **253 tx/s** (12 inputs every 50 ms + 20 bets/s = 2× base) from one IP; confirm p50 **217** / p95 **270** / p99 304 ms; RTT baseline (getSlot) p50 89 ms. Base rate at load 19: 102 tx/s (client-limited), p50 244 / p95 828 ms. **With realistic inputs the tick goes over budget and the World gets stuck** (60 ticks in 300 s despite 62 task restarts); with idle inputs (same tx load) it ticks at 19–20 Hz | 0 rate-limit rejects; confirm p95 ≤150 ms | **Rate limits PASS; confirm p95 FAIL** (270 ms vs 150 ms) |
| **P0.4** | State delivery | **Chrome** (raw `accountSubscribe`): 19.94 updates/s, 0 missing ticks (1,244). **Node:** 7,968/7,968 ticks in the 10-min run (20.0/s). **Relay:** 19.93 updates/s in Chrome; added latency vs direct (Node, same machine) p50 **0** / p95 **13** ms (102 tx/s background), p95 27 ms (253 tx/s) | ≥18/20 updates/s; relay adds ≤20 ms | **PASS** (relay p95 27 ms under 2× load) |
| **P0.5** | End-to-end latency | 999 samples: input stamped T → first World update with `applied_tick ≥ T`: p50 **130** / p95 **337** / p99 506 / max 2,006 ms. Confirm p50 167 / p95 358 ms. RTT p50 80 ms. Seen at tick T+2 (p50) / T+6 (p95). 1 timeout, 1 dropped WS notification | p50 ≤150, p95 ≤250 ms | **p50 PASS, p95 FAIL** |
| **P0.6** | sBPF = native = WASM | **200/200 identical** World hashes (100 seeds × 2 maps × 200 steps): sBPF `run_steps` on the local rollup, native, `wasm32-unknown-unknown` in Node and in headless Chrome. P0.1 CU on devnet-as = local CU to the unit (deterministic) | 100 % | **PASS** |

## 2. Recommendation: **REDUCED** (a conditional GO, not NO-GO)

Keep the plan's shape:
- the whole simulation on the ER at **20 Hz**;
- the timed task;
- **bots on-chain** (P2.6 option A): the scripted bots cost ≤ 12k CU for 12 players, under 3 % of the budget.

Nothing measured argues for the verifiable-server fallback:
- determinism is exact (P0.6);
- the scheduler is precise at 15–30 Hz;
- the rollup absorbed 253 tx/s from one client with zero rejects;
- browsers receive every tick.

The reductions are about **two hard limits found here**:
1. **The tick must never exceed the task budget, and must never fail.**
   - A timed-task execution has **400k CU** (not 200k per instruction, and not 1.4M). The first failed execution **ends the task forever**.
   - Random bots produce rare 400k+ ticks (both 10-min runs died at tick 7,967). Realistic inputs pile players up and produce **sustained** over-budget ticks: the World is stuck and re-scheduling can't recover it.
   - So P1 / P2 must:
     - (a) give `rush-core` a **deterministic work budget**: a cap on narrowphase tests and slide iterations per player and per tick that degrades gracefully, the same on sBPF and WASM. Target **≤ 300k CU worst case** for the whole tick, proven by a 100k-tick native soak plus an sBPF pile-up stress vector. The spike already went from 717k to 155k p50 in three cheap steps, and the per-primitive costs in §3.1 show where the rest is (prisms: 557 CU per test);
     - (b) never `require!` inside the hot tick during normal play;
     - (c) run a **permissionless keeper** that re-schedules within ~1 s, plus the `NotDue`-guarded tick fallback (both measured working).
   - If (a) can't reach 300k on Capsule Run, use the doc 19 §4.1 fallback **round by round** (Final → Skill → Survival → Race) instead of dropping to 15 Hz. 15 Hz doesn't lower the per-tick cost; it only lowers the per-second cost.
2. **Latency:** end-to-end p95 is 337 ms and confirm p95 270 ms from this Mac (RTT about 80–90 ms to devnet-as).
   - Use the doc 19 §4.1 latency fallback by design: client-side **prediction for the own player** (rush-core WASM, P0.6 PASS) plus a fixed **input delay of 2–3 ticks**, with inputs stamped T+3.
   - Remote players are shown about 150 ms in the past (interpolation).
   - The ≤150 ms confirm target isn't reachable from India to devnet-as. That target should move to "≤ RTT + 150 ms".

- **Tick rate: 20 Hz.**
  - 30 Hz runs equally precisely (3,637/3,637), but it gives no more CU per tick and 1.5× the per-second load.
  - 15 Hz is precise too, but it doesn't fix the per-tick budget.
- **Bots: on-chain.**
- **Relay:** optional; it adds p50 0 / p95 13–27 ms, so it's fine as the spectator fanout.

**The decision is the user's (doc 15 P0-1).**

---

## 3. Measurements in detail

### 3.0 What was built (throw-away, kept for the record)

- **`spikes/p0/sim`** (`p0_sim`): `#![no_std]`, `#![deny(clippy::float_arithmetic)]`, no alloc in the hot path.
  - **State:** `Player { pos [i32;3] mm, vel [i32;3] mm/s, yaw u8, grounded, coyote, jbuf, … }` (32 B); `World { tick, seed, players[12] }` (400 B).
  - **Step (dt = 50 ms):**
    - run 5,000 mm/s, accel 28,000, decel 30,000, air 30 %;
    - jump v = 6,928 mm/s (apex 1.2 m), gravity 20,000 ×1.35 falling, coyote / buffer 2 ticks;
    - swept upright capsule (r 310 + skin 10, h 1,000) vs OBB / convex prism / sphere / horizontal capsule; time of impact by 6 bisections (1/64 of the motion) over exact segment–primitive distances; **≤ 3 slide iterations**;
    - step-up (up 250 → across → down), ground snap 250 mm as a short capsule cast down, 2 depenetration passes;
    - 66 player pairs (soft push ≤ 125 mm per tick); moving hazards (rotating sweeper capsules, rolling balls) as pose = f(seed, tick) with a 1,024-step Q15 sine table.
  - **Broadphase:** uniform grid, **2,048 mm cells**, built offline into the map blob (cells = `start<<8 | count`, u16 index list); per player one gather per tick with a 512-bit dedupe bitset and an AABB reject; per-player AABB reject for the moving hazards.
  - **Maps** (deterministic generator, `gen` feature):
    - **Lid Flip-like**: 120 colliders (9×9 + 5×5 lids, 6 octagon bumpers, posts, rails, 2 capsule rails) + 1 sweeper; blob 9,416 B.
    - **Capsule Run-like**: 300 (274 OBBs, 20 prisms, 6 spheres) + 2 sweepers + 6 rolling balls; blob 28,796 B.
    - For comparison, the real scenes (counted from `client-unity/Assets/_Game/Scenes/*.unity`): Capsule Run (`Map_BlockPartyDash`) = 122 BoxColliders + 5 MeshColliders (prisms); Lid Flip (`Map_CrumbleShelf`) = 106 BoxColliders, plus hazards built at runtime. So **300 is a conservative upper bound** (the prompt's "≈300" was UNVERIFIED).
  - **Bots:** `bot_input(slot, tick, seed, map, player)`: random heading per 24-tick epoch (course maps: forward ±90°; arena: steer back inside 7 m), 10 % idle, jump about 1 in 15 ticks. About 50 % of players are airborne at any time; about 1 respawn per player every 10 s on the arena map.
  - **Tests** (`cargo test -p p0_sim --features gen`, 6 green): stand still, max speed, wall blocks, 200 mm step-up, jump apex 1.0–1.4 m, and **split step = whole step** (bit-identical, 300 ticks × 2 maps).
  - **Shortcuts vs the Unity motor:**
    - OBBs, prisms and capsules are **yaw-only** (no tilted ramps);
    - time of impact by bisection instead of analytic capsule casts;
    - no dive / stumble / respawn timers;
    - hazards are simplified (one sweeper type, one ball type);
    - prisms are regular polygons;
    - no per-hazard-type logic (the 14 types in doc 19 are P1).
  - The collision maths is the real kind (swept tests + slides), so the **cost shape** holds. The **absolute** cost of rush-core will differ (§3.1 lists the optimisations measured so far).
- **`spikes/p0/programs/p0_tick`:**
  - instructions:
    - `init_world`, `init_input`, `init_bet`, `upload_map_chunk`, `delegate_pda`, `undelegate`, `close_any`;
    - `reset_world`, which also clears the 12 InputBufs (it's in the 2nd deploy);
    - `submit_input(start_tick, 2 inputs)`, `place_bet(64 B)`;
    - `tick`, `tick_part(0|1)`, `run_steps(n, seed, reset)`, `bench_prims(n)`;
    - `schedule_ticks(interval, iterations, nonce, split)`, `cancel_ticks`.
  - **Release profile:** `opt-level="z"` for the program and **`opt-level=3` + no overflow checks for `p0_sim` only**; `.so` 368,600 B.
- **`spikes/p0/sim-wasm`:** C ABI (`p0_alloc`, `p0_hash`), no wasm-bindgen, 29 KB.
- **`tools/load`:**
  - shared library: manual Anchor encoding; confirmation by **polling `getSignatureStatuses`** in batches;
  - `setup` / `cleanup`, one runner per measurement (`p01-cu`, `p02-crank`, `p02-fallback`, `p02-taskid`, `p03-load`, `p04-browser`, `p05-latency`, `p06-sbpf`, `wasm-node`, `wasm-chrome`);
  - `relay` (a `ws` fanout); a headless-Chrome page driven over CDP, the same way as `scripts/g47-webgl-measure.mjs`.
- **Scripts:** `scripts/p0-local.sh` (local stack + spike program, always inside `heavy-lock.sh`) and `scripts/p0-devnet-deploy.sh`.

### 3.1 P0.1 — compute units per tick

**Method:**
- Command: `P0_NET=devnet npx tsx tools/load/p01-cu.ts 200` (also `P0_NET=local`, inside `scripts/p0-local.sh`).
- Run: 2026-11-21 09:49 UTC; load 59.5 → 40.6.
- Inputs: 12 scripted bots inside `tick()` (`F_BOTS`), seed 7, 200 consecutive ticks per map and mode.
- CU is read from `sol_log_compute_units` lines between the phases (inputs / bots / step / tail) and from `meta.computeUnitsConsumed`.
- **The numbers are identical on devnet-as and the local stack, to the unit.**

| Map | Mode | step p50 | p95 | p99 | max | whole tx p50 / p95 / max |
|---|---|---|---|---|---|---|
| 120 colliders | 1 instruction | 141,878 | 218,036 | 237,620 | 246,307 | 159,998 / 235,573 / 262,355 |
| 300 colliders | 1 instruction | 154,557 | 213,605 | 245,955 | 246,734 | 169,894 / 228,738 / 262,301 |
| 120 | split, part 0 (players 0–5 + inputs + bots) | 65,583 | 117,127 | 140,606 | 158,203 | handler max 166,258 |
| 120 | split, part 1 (players 6–11 + 66 pairs + log) | 72,558 | 140,195 | 177,124 | 192,713 | handler max 193,615 |
| 300 | split, part 0 | 76,921 | 118,181 | 129,740 | 135,131 | handler max 142,626 |
| 300 | split, part 1 | 81,558 | 135,356 | 159,143 | 179,078 | handler max 179,980 |

- **Fixed costs:** reading 12 InputBufs 1,317 CU; bots 8.3k p50 / 12.1k max (arena) and 5.5k / 5.9k (course); tx overhead about 8k.
- **Bot-brain headroom (P2.6 option A):** the scripted bots cost **≤ 12k CU** for 12 players. Real bots (BotBrain + waypoints) will cost more (UNVERIFIED); at ×5 they would still be about 60k, which fits inside a split.

**Long tail (the important finding):**
- Both 10-minute timed-task runs (P0.2) died at tick 7,967.
- Replaying that state (`tools/load/replay-tick.ts lid 11 7966` on the local rollup) gives:

| tick | part 0 | part 1 | total |
|---|---|---|---|
| 7,966 → 7,967 | 193,865 | 79,752 | 273,617 |
| **7,967 → 7,968** | **299,847** | 102,120 | **401,967** |
| 7,968 → 7,969 | 151,840 | 108,505 | 260,345 |

- A native proxy over 20 seeds × 10,000 ticks per map (`cargo run --release -p p0_sim --features gen --example tail -- 20 10000`) counts narrowphase tests per tick:
  - 120 colliders: p50 261, p99 430, p99.9 499, max 657;
  - 300 colliders: p50 296, p99 500, p99.9 581, max 693.
- The tick that died had **479 tests**: 405 of 200,000 ticks (0.2 %) reached that count on the 120 map, and 3,566 (1.8 %) on the 300 map.
- The proxy isn't linear in CU (prisms cost more than boxes), so these rates are an **order of magnitude, not a measurement**. The fact itself is measured: random 12-player play produces ticks above 400k CU.

**CU per collision primitive** (`bench_prims(50)` on devnet-as, 300-collider blob; includes the loop):

| primitive | probe + touch test (no sqrt) | probe + normal (isqrt + 3 divisions) |
|---|---|---|
| sphere | 135 | 259 |
| OBB (yaw) | 221 | 329 |
| horizontal capsule | 221 | 289 |
| convex prism (8–12 edges) | 557 | 675 |
| `isqrt` (Newton, u64) | 93 | — |

**Optimisation history** (same sim behaviour; World hashes unchanged by the last two steps):

| version | lid step p50 / p95 | run step p50 / p95 / max |
|---|---|---|
| v1: probes return distance + normal, `opt-level=z`, overflow checks on | 370k / 675k | 717k / 988k / 1,254k |
| v2: sqrt-free touch tests, normal only on hit; sim `opt-level=3`, no overflow checks | 151k / 226k | 214k / 285k / 329k |
| v3: per-player AABB reject for moving hazards (identical hashes) | 142k / 218k | 155k / 214k / 247k |

**Rollup compute facts (measured):**
- A client transaction may set `SetComputeUnitLimit` up to **1.4M** and a single instruction may use more than 200k (`run_steps`, 4 steps ≈ 1M, ran 10,000 times).
- Without a compute-budget instruction the default is 200k per instruction: a full tick failed with `ProgramFailedToComplete` on the 300 map.
- **A timed-task execution gets 400,000 CU** (logs: `consumed X of 399700`; a 300-CU wrapper instruction comes first), shared by all instructions of the task. So the 2-instruction split does **not** buy more budget inside a task. It only helps client-sent ticks.

### 3.2 P0.2 — timed task (ScheduleTask) at 20 Hz

**Method:**
- Command: `P0_NET=devnet npx tsx tools/load/p02-crank.ts <ms> <s> <split> <map> <nonce> <label>`.
- `schedule_ticks` CPI (exact API in §4.4).
- Node `onAccountChange(World, "processed")` logs (tick, receive ms).
- TickLog (400-entry ring of {tick, slot, unix_s}) is polled every 5 s.
- devnet-as slot time measured at **10.0 ms** (getSlot over the run).

| run (devnet-as) | ticks / expected | receive gap p50 / p95 / p99 / max (ms) | gaps >250 ms | within ±1 slot | rate | drift | load |
|---|---|---|---|---|---|---|---|
| 50 ms × 600 s, split, 120 map (09:56 UTC) | **7,967 / 12,000 (task died)** | 50 / 62 / 87 / 222 | 0 | 99.85 % | 20.002 Hz | −44 ms / 398 s | 39 → 20 |
| same, run 2 (10:26 UTC) | **7,967 / 12,000 (task died, same tick)** | 50 / 59 / 71 / 249 | 0 | 99.77 % | 20.003 Hz | −53 ms | 33 → 32 |
| 33 ms × 120 s, split | 3,637 / 3,637 | 33 / 42 / 57 / 148 | 0 | 99.97 % | 30.31 Hz | −46 ms | 20 |
| 66 ms × 120 s, split | 1,819 / 1,819 | 66 / 76 / 88 / 217 | 0 | 100 % | 15.16 Hz | −72 ms | — |
| 50 ms × 120 s, **single** ix, 300 map | 2,400 / 2,400 | 50 / 60 / 73 / **1,079** | 1 | 99.33 % | 20.007 Hz | −42 ms | 12 |

- Local stack (load 34–45): 600/600 at 20 Hz, gap p95 56 ms, max 118 ms.

**Task-id behaviour** (`tools/load/p02-taskid.ts`, devnet-as, task_id = FNV-1a(program | world | nonce) & i64::MAX):

| case | result |
|---|---|
| A: one task, 60 iterations | 60 ticks |
| B: same id again after A finished | accepted, 60 ticks (an id is reusable once its task ended) |
| **C: two schedules with the same id at once, 100 each** | **both accepted without error; 105 ticks ⇒ the second was silently dropped** (confirms the 1.1.1 warning) |
| D: two different ids at once, 100 each | 200 ticks (two tasks can drive one World; P2 must forbid this) |
| E: `CancelTask` with task-context account = `MagicContext1111111111111111111111111111111` | works: "Successfully added cancel request", 0 ticks in the next 3 s |

**Fallback (anyone may tick when due):**
- Setup: `World.min_slot_gap = 1`; a tick bot sends `tick_part 0+1` every 40 ms (`p02-fallback.ts 40 60`).
- Result: 849 sent, **794 ticks (13.2 Hz)**, 55 rejected `NotDue`, 0 other errors.
- The guard works. The rate was limited by our client (one Node process, load 44), not by the rollup.
- 20 Hz from a tick bot is **UNVERIFIED** (it needs a send cadence below 50 ms and a better client).

**Verdict:**
- **Scheduling precision passes** at 15, 20 and 30 Hz.
- **Robustness fails:** the task stops forever on its first failed execution (an over-budget tick here; P0.3 shows contention also kills it). P2 needs:
  - (a) a tick that can never fail: deterministic work budget, no `require!` that can trip during normal play;
  - (b) a permissionless keeper that re-schedules within ~1 s;
  - (c) the `NotDue`-guarded tick fallback.

### 3.3 P0.3 — input load

**Method:**
- Command: `P0_NET=devnet npx tsx tools/load/p03-load.ts <s> <mul> 10 8787`.
- Load: 12 input clients (each its own funded keypair) send `submit_input` (2 ticks per batch) every 100 ms / mul; 10 bet clients send `place_bet` (64 B) every 1 s / mul.
- The timed task runs during the test, kept alive by the keeper.
- All confirmations go through one batched `getSignatureStatuses` poller (20 ms).
- Session-key overhead (not used here): one extra read-only account per input transaction, so no measurable CU (UNVERIFIED).

| run (devnet-as) | sent tx/s | errors / rejects | input confirm p50 / p95 / p99 (ms) | bet confirm p50 / p95 | ticks | load |
|---|---|---|---|---|---|---|
| ×2 for 60 s (11:17 UTC) | **253** | **0 / 0** | **217 / 270 / 304** | 218 / 269 | ran; keeper over-restarted (§3.2 note) | **14** |
| ×1 for 300 s (11:12 UTC) | 102 (client-limited) | 0 / 0 | 244 / 828 / 1,611 | 265 / 1,164 | 5.5 Hz: **tick over budget** | 19 |
| ×1 for 300 s, realistic inputs (12:18 UTC) | 68 | 0 / 0 | 571 / 3,216 / 6,069 | 832 / 3,938 | **0.2 Hz: World stuck over budget**, 62 restarts | 49 |
| ×1 for 120 s, idle inputs (same tx load) | 21 | 0 / 0 | 2,646 / 7,484 | — | **19.1 Hz**, 1 restart | **107** |
| ×2 for 60 s, idle inputs | 29 | 0 / 0 | 1,627 / 7,372 | — | **20.1 Hz**, 0 restarts | 47 |

**Reading it:**
- **The rollup never refused or rate-limited anything** from one IP: no 429, no errors across all runs, peak 253 tx/s = 2× the base rate.
- Client-side numbers degrade with **our Mac's** load (single Node process, 22 signers). The clean row is the load-14 one.
- The rollup itself confirms in about RTT + 130 ms. That's consistent with 10 ms slots plus the RPC path. The ≤150 ms target is below what the network allows from here.
- The three "idle inputs" rows show that the input **traffic** doesn't disturb the tick: 19–20 Hz.
- The realistic-input rows show that the input **content** (players running into each other and into rails) produces over-budget ticks, and the World can't get out of it.
- Devnet rate limits beyond 253 tx/s from one IP and from many IPs: **UNVERIFIED**.

### 3.4 P0.4 — state delivery

**Node** (`onAccountChange(World, "processed")`, `@solana/web3.js` 1.98.4):
- P0.2 10-min run: 7,968/7,968 ticks received, 20.0 updates/s, receive gap p95 59–62 ms.
- During P0.3 with idle inputs: 0 missing ticks (2,343 and 1,272).

**Headless Chrome** (`p04-browser.ts 60 8787`; `tools/load/page.html` speaks raw JSON-RPC `accountSubscribe` over WebSocket, the same messages the TS SDK sends, without bundling the SDK). Run on devnet-as at 12:2x UTC, all bots, one task:

| path | updates/s | ticks missing | gap p50 / p95 / p99 / max (ms) | gaps > 250 ms |
|---|---|---|---|---|
| direct to devnet-as | **19.94** | 0 / 1,244 | 50 / 110 / 361 / 825 | 24 |
| through the relay (`tools/load/relay.ts`, `ws` fanout of the raw bytes) | **19.93** | 0 / 1,254 | 49 / 87 / 391 / 867 | 20 |

- **Relay added latency** (Node, direct vs relay for the same tick, same machine):

| background load | p50 | p95 | p99 | max |
|---|---|---|---|---|
| 102 tx/s (load 19) | 0 | 13 | 69 | 357 ms |
| 253 tx/s (load 14) | 0 | 27 | 36 | 43 ms |

- The large negative minima in the noisy runs are relay messages that beat the direct socket.
- Local stack (load 34): Chrome direct 20.00/s, gap p95 52 ms.
- Delivery to many simultaneous browsers (fan-out limits of the rollup's WS): **UNVERIFIED**.

### 3.5 P0.5 — end-to-end latency

**Method:**
- Command: `P0_NET=devnet npx tsx tools/load/p05-latency.ts 1000` (12:30 UTC, load 15 → 23). A `applied_tick[12]` field was added to World.
- Each sample:
  - the client reads the latest tick L from its subscription;
  - it sends `submit_input` for slot 0 stamped T = L + 1;
  - it measures until the first World notification with `applied_tick[0] ≥ T`.
- `tick()` applies the newest ring entry with tick ≤ the current tick, so an input stamped T is used by the tick that steps from T.
- A parallel 20 ms account poll catches dropped WebSocket notifications.
- The other 11 slots are bots. Samples are sequential with 20–54 ms de-phasing.

| | p50 | p95 | p99 | max | n |
|---|---|---|---|---|---|
| end-to-end (WS) | **130** | **337** | 506 | 2,006 | 999 |
| end-to-end (poll path, when it saw it first) | 122 | 415 | 598 | 1,369 | 335 |
| send → confirm | 167 | 358 | 576 | 1,928 | 1,000 |
| ticks after T when seen | 2 | 6 | 13 | 39 | 999 |
| RTT baseline (getSlot) | 80 | 88 | 89 | 395 | 30 |

- 1 timeout (3 s); 1 notification never arrived on the WebSocket but was seen by polling.
- **p50 PASS, p95 FAIL.** The tail is the confirm path (p95 358 ms > the 2.5-tick budget), not the tick.

### 3.6 P0.6 — sBPF = native = WASM

**Method:**
- Seeds 1..100 × {120, 300 map} × 200 steps of scripted bots; FNV-1a 64 over the `World` bytes.
- **Native:** `cargo run --release -p p0_sim --features gen --example dump_maps -- .p0-logs 100 200`.
- **sBPF:** `run_steps` on the local rollup (World 1, 4 steps per transaction under a 1.4M limit, 10,000 transactions, 0 retries) via `tools/load/p06-sbpf.ts`.
- **WASM / Node:** `tools/load/wasm-node.ts`. **WASM / Chrome:** `tools/load/wasm-chrome.ts` (headless Chrome, page loads the `.wasm`).

| target | lid (120) | run (300) |
|---|---|---|
| sBPF (local rollup) | 100/100 | 100/100 |
| WASM in Node 24 | 100/100 | 100/100 |
| WASM in Chrome (headless) | 100/100 | 100/100 |

- Blob hashes: lid `30c6aa24172d0050`, run `5c619b2647cf1f2b`.
- Supporting evidence: devnet-as CU = local CU to the unit, and the split step = the whole step (unit test).
- **Unity-WebGL loading this `.wasm` is UNVERIFIED** (P3).

---

## 4. Inputs to P1 / P2

### 4.1 Compute
- CU per primitive: see §3.1. Touch test without sqrt: sphere 135, OBB 221, capsule 221, prism 557. With a normal: +70–125. `isqrt` 93.
- **Lessons:**
  - never take `sqrt` on a miss;
  - normals only on the final hit;
  - per-player AABB reject for moving hazards;
  - `opt-level=3` + no overflow checks for the sim crate (keep `z` for the rest; the `.so` is 368 KB ≈ 1.92 SOL rent);
  - precompute prism edge normals (done) and consider bounding circles first.
- **Budget:**
  - timed-task execution **≤ 399,700 CU** in total;
  - client transaction up to 1.4M with `SetComputeUnitLimit`;
  - no per-instruction 200k cap when a limit is set.
- **The work budget must be deterministic.** `sol_remaining_compute_units` can't be used (§4.5), so count tests.
- **Broadphase:** 2,048 mm cells (`1 << 11`) worked. 1.6 candidates per player per tick on both maps, with a 512-bit dedupe set (≤ 512 colliders per map), ≤ 96 candidates.

### 4.2 Layouts that worked (zero-copy, `#[repr(C)]`, all ≤ 10,240 B)
| account | bytes (with the 8 B discriminator) | content |
|---|---|---|
| `WorldAcc` | 1,016 | authority, map, `World` (400 B: tick, seed, 12 × 32 B players), 12 input keys, `applied_tick[12]`, hash, last_slot, ticks_run, task_id, flags, min_slot_gap, CU fields, narrow, wid, part_pending, `inputs_cur[12]` |
| `InputBuf` | 152 | authority, owner, latest, count, slot, ring[8] of {tick u32, Input 4 B} |
| `TickLog` | 9,648 | authority, head, 400 × {tick, cu_step, slot, unix_s} (20 s at 20 Hz; poll ≤ every 15 s) |
| `BetAcc` | 144 | authority, owner, n, payload[64] |
| map blob | 48 + 9,416 / 28,796 | not delegated; read-only clone into the rollup worked (uploaded in 900 B chunks) |

- `Input` = {mx i8, mz i8, btn u8, yaw u8} (4 B). The `submit_input` batch is 2 inputs (8 B arg).

### 4.3 Timed task: the API exactly as used (validator 1.1.1)
```rust
// accounts: payer (signer, mut), world, log, map, magic_program (= Magic1111…), + 12 InputBufs (remaining)
let ix = Instruction { program_id: crate::ID, accounts: vec![w(world), w(log), r(map), r(input_0..11)],  // no signers
                       data: crate::instruction::TickPart { part: 0 }.data() };  // + part 1, or Tick {}
let data = bincode::serialize(&MagicBlockInstruction::ScheduleTask(ScheduleTaskArgs {
    task_id,                       // FNV-1a64(program id | world | nonce LE) & i64::MAX — unique per program+world+nonce
    execution_interval_millis: 50, iterations, instructions: vec![ix0, ix1] })).unwrap();
invoke(&Instruction { program_id: MAGIC_PROGRAM_ID, accounts: [payer (signer, w)] + every account of the scheduled ixs, data },
       &[payer, world, log, map, inputs…])?;
// cancel: MagicBlockInstruction::CancelTask { task_id }, accounts [payer (signer, w), MagicContext1111…1111 (w)]
```
- Crates: `magicblock-magic-program-api = "=0.10.1"` + `bincode 1.3.3`.
- `ephemeral_rollups_sdk::crank::*` needs the SDK feature `crank` (it pulls `hydra-api`). Its `&'a AccountInfo<'a>` signature didn't fit Anchor 1.0 contexts, hence the hand-built CPI.
- The task transaction runs our instruction **as a CPI from `Crank11111111111111111111111111111111111111`** (invoke depth 2), payer = the validator `MAS1Dt…`.

### 4.4 Rollup quirks found (all measured)
1. The rollup **can't clone a program that imports `sol_remaining_compute_units`**: "Failed to clone program … InvalidAccountData". Use `sol_log_compute_units` and parse logs.
2. **Task budget 400k CU** per execution, shared by all its instructions, after a 300-CU wrapper.
3. **One failed execution ends the task.** We saw repeated failed executions at the same instant, then no more.
4. **A duplicate task_id is accepted silently and the second task never runs.** An id is reusable after its task ends. Two different ids can drive the same World.
5. `CancelTask` works with `MagicContext…` as the task-context account.
6. devnet-as **slots are 10 ms**; the local stack's are 50 ms.
7. The first transactions after delegation can fail with "pending request owner … Cloner" while the rollup clones; retry for a few seconds.
8. A full World notification arrives for every tick (Node and Chrome). 1 of 1,000 WS notifications never arrived (P0.5).
9. The **public devnet base RPC** (api.devnet.solana.com) returned 429 during setup (≈ 100 base transactions). Throttle to ~1.5 tx/s; the rollup RPC never did.
10. Account data clones of non-delegated accounts (the map blob, 28.8 KB) into the rollup worked, read-only.

### 4.5 What P1 must add because of P0
- A deterministic per-tick work budget in `rush-core`, with a pile-up stress vector (12 players into one wall corner) in `tests/vectors/` and a CI gate: max CU ≤ 300k on every map.
- An idempotent, never-failing tick. A permissionless keeper (cancel + re-schedule) and a `NotDue` tick fallback.
- Inputs stamped T+3 with client prediction. `applied_tick` per slot kept in World for reconciliation.

---

## 5. Devnet cost and addresses

| item | SOL |
|---|---|
| start balance (funded by the user; PB0 used 0.0024 earlier) | 5.850800385 |
| program deploy (rent 1.868 + fees) | −1.871025 |
| setup (map blobs 0.195, world/log/inputs/bets, delegation records, 22 client fee payers at 0.001) | −0.4385 (incl. retries) |
| upgrade (+10,240 B extend, 2nd deploy) | −0.053970 |
| test transactions (base-layer fees; rollup transactions are free) | ≈ −0.0 |
| cleanup refund (28 accounts closed, 22 clients drained) | **+0.360670** |
| **end balance** | **3.848006441** |
| **spent** | **2.002794** = program rent 1.920265 (still deployed, refundable by closing the program) + ≈ 0.0825 fees / non-refundable |

- **Still deployed:** program `8z4ofj7GeGLArXaYjbV2QXrdUqDdbutavqQSWXqpQBxy` (ProgramData `GyioBjMA1JGMXhSxGGfrofzUzE7ubxH9NBYo3kpzt6hq`, 377,832 B, upgrade authority = the wallet `71Wd3S…`). The program keypair is in `spikes/p0/keys/` (gitignored).
- `solana program close 8z4ofj7G… --bypass-warning` returns 1.92 SOL **when the user says so**.
- **Closed:**
  - World 0/1, TickLog 0/1, 12 InputBufs, 10 bet PDAs;
  - map blobs `5H1RQpGHWk72VyW5R36FTWfrLkyEfJoqDjc9RfjCgoTU` (lid) and `13wquzmUVBcBxNHKrA1NNj6mhwhAt5TTjjW29fpXdgGd` (run).
- Example signatures:
  - deploy `5bwdve8cdU198W9TfRqnibudWaXv475GHR814PcNmHTNmKRwd7oryN15dMj54KvNDypRVwuNptYxJAJRcuiKKE62`;
  - first 20 Hz `schedule_ticks` (rollup) `5GeoRUWW2NgEpCchmxSAbL8AuSK3VnA4xkxfuXiDgMy7toZ4d14fNsQAoQfFmrmZEj2rYwfj2J1TEcvt7pWiK9Cf` (task_id 990414777865594556).

## 6. UNVERIFIED
- Unity-WebGL loading `rush_core.wasm` (P3). Only Node 24 and headless Chrome were measured.
- Mainnet ER behaviour, other regions (devnet-eu / devnet-us = validator 2.0 with Hydra cranks, different rules).
- Rate limits above 253 tx/s, from many IPs, or for many WebSocket subscribers.
- 20 Hz from a permissionless tick bot (13.2 Hz measured, client-limited).
- Real BotBrain / WaypointBot CU (only scripted bots measured). Real hazards (14 types), dives and tilted geometry.
- Session-key CU overhead. Ephemeral SPL, VRF in the tick, commits/undelegation of a live match.
- The 0.2 % / 1.8 % over-budget tick rates (a native proxy, not CU).

## 7. For main (merge later)

**doc 15 — new row (for the user to approve):**

| ID | Decision | Status |
|---|---|---|
| P0-1 | P0 result = **REDUCED (conditional GO)**: the whole sim on the ER at **20 Hz**, timed task + permissionless keeper/fallback, **bots on-chain**. Conditions: (1) rush-core deterministic work budget, tick ≤ 300k CU worst case (task budget 400k; one failure ends the task); (2) own-player prediction + 3-tick input delay (end-to-end p95 337 ms); (3) relay optional for spectators. If (1) fails on Capsule Run in P1, go on-chain round by round (Final → Skill → Survival → Race) with Classic serving the rest. Evidence: `docs/proof/P0-feasibility.md` | **proposed, awaiting user approval** |

**PROJECT_STATUS.md lines:**
- `P0 feasibility (2026-11-21): done on branch p0 — 6 measurements; P0.4 + P0.6 PASS, P0.1 PASS only as a split (task budget 400k found), P0.2 timing PASS / robustness FAIL, P0.3 rate limits PASS / confirm p95 270 ms FAIL, P0.5 p50 130 PASS / p95 337 ms FAIL. Recommendation REDUCED (conditional GO, 20 Hz, bots on-chain). P0-1 awaits user approval. Proof docs/proof/P0-feasibility.md.`
- `Devnet: p0_tick 8z4ofj7G… still deployed (1.92 SOL rent, refundable); spike accounts closed; wallet 3.848 SOL.`

**doc 19:** §3 "Compute budget" row → "≤ 300k CU per tick worst case (task budget 400k measured in P0)"; §4.1 "Timed task unreliable" → add "a failed execution ends the task: keeper re-schedules"; §11 add the rollup quirks of §4.4.
