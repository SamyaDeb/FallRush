# P3 (full): the Unity on-chain client (new session, in parallel with P2)

Supersedes `docs/prompts/P3a-PROMPT.md` (never started).

You are the Unity / web client engineer on **Fall Rush**, a Unity 6.3 WebGL 12-player knockout party game for the MagicBlock Solana Blitz V10 hackathon.
- The simulation runs on a MagicBlock Ephemeral Rollup through **`rush-core`**: our integer Rust core, which gives the same results natively, in WASM and on-chain.
- **User decision 2026-11-22** (`docs/prompts/P2-DECISION-1.md`): **the Final (Lid Flip) runs on-chain first.** Rounds 1–3 stay on the Classic server, so **one match switches from the Classic server to the rollup at the Final.**

P2 is still building the on-chain side in another session, so P3 has two parts:
- **Part 1 (A–H): needs nothing from P2.** Build everything against a **local chain emulator** that behaves like the rollup.
- **Part 2 (I–N): gated on P2 milestones** (§0.3). Swap the emulator for the real arena on devnet-as, do the Classic → on-chain switch at the Final, and run the devnet feel test.

If a gate isn't open yet when Part 1 is done, write down what you're waiting for, **check every 30 minutes** (§0.3), and do the gate-free polish in the meantime. Never work around a gate by changing P2's files.

**Read first, in full:**
1. `docs/19-ONCHAIN-PLAN.md`: §1, §3, §5 P3, §9.
2. `docs/prompts/P2-DECISION-1.md`.
3. `docs/proof/P0-feasibility.md`: §3 (P0.4 delivery, P0.5 latency) and §4 (quirks).
4. `docs/proof/P1-rush-core.md`: §4 parity incl. the Unity FFI, §11 Feel Lab, OC-3 (30 Hz).
5. **P2's branch, read-only:** `git -C /Users/samya/Downloads/FallRush-p2 log --oneline main..p2`, `../FallRush-p2/docs/proof/p2/*`, and `../FallRush-p2/docs/proof/P2-onchain-sim.md` once it exists (§10 = "Inputs to P3").
6. Code:
   - `crates/rush-core-wasm/src/lib.rs` (C-ABI exports), `crates/rush-core/src/world.rs`;
   - `client-unity/Assets/_Game/Scripts/FeelLab/*` (`RushCoreNative.cs` = P/Invoke, which shows the ABI in use);
   - `Scripts/Net/ClientPrediction.cs`, `Interpolation.cs`, `TickSync.cs`, `NetBootstrap.cs`;
   - `Scripts/Client/ChainAdapter.cs`, `PracticeChainAdapter.cs`, `MatchHud.cs`, `WalletBridge.cs`, `Client/Wallet/WalletSmokeTest.cs`;
   - `Plugins/WebGL/FallRushSession.jslib`;
   - `Scripts/Net/ServerMatch.cs` (the jsonl event log, round flow).

---

## 0. State at hand-off (2026-11-22 ~05:40 UTC, checked)

### 0.1 Sessions and branches
| Session | Where | State | Owns |
|---|---|---|---|
| P2 | `../FallRush-p2`, branch `p2` (@ `e9f56a7`) | 0.1 (30 Hz), 0.3 (bot pacing: all targets pass at 30 Hz), 0.4 (hygiene) done. 0.2 has a **split tick** (`world::step_part`, 2–6 parts, hash-identical), **TOI bisection 8 → 6** (golden vectors regenerated). 12-bot worst part of a 3-part split: Lid Flip 458k, Star 574k, Goo 713k, Run 1.13M CU. Next per the decision: Lid Flip at 3 players, then P2.1–P2.10 | `programs/**`, `crates/**`, `tools/keeper|replay|bot-runner|chain-worker/**`, `docs/07-*`, `docs/proof/P2*`, `docs/proof/p2/**`, `maps/**`, its final doc commit (`docs/15`, `docs/19`, `PROJECT_STATUS.md`) |

`main` @ `2decc78`. Old worktrees `../FallRush-p0` and `../FallRush-p1` exist; don't use or delete them.

### 0.2 Facts
- **The `rush-core` version must match the chain.**
  - P2 changed behaviour (TOI 8 → 6; vectors regenerated), so **`main`'s `crates/` is now stale** for the client.
  - **Build `rush_core.wasm` and the native library from P2's branch, read-only:** `git -C ../FallRush-p2 archive p2 crates maps packages/shared/config | tar -x -C <your worktree>/.p2-core/`, then build there with your own `CARGO_TARGET_DIR`.
  - Write down the P2 commit you built from in `client-unity/Assets/_Game/Plugins/rush_core_version.txt`. Run the vector parity test (A.4) on every rebuild.
  - Re-sync when P2 commits a change under `crates/` (check with the §0.3 check).
- **WASM exports** (P1): `rc_version`, `rc_world_size`, `rc_init`, `rc_step`, `rc_predict_player`, `rc_hash` (FNV-1a 64), `rc_set_rate`. P2 added `step_part`; the client only needs the full `step`.
- **Tick rate 30 Hz** (OC-3).
  - Latency to devnet-as from this Mac: RTT ~80–90 ms, confirm p95 270 ms, end-to-end p95 337 ms.
  - Design: **own-player prediction**, **inputs stamped T+3**, **3 ticks per input transaction**, remote players interpolated ~150 ms in the past.
  - A World notification arrives per tick (P0: 1 of 1,000 lost), so keep a 1 s backup poll.
- **Unity** 6000.3.28f1 URP WebGL. EditMode suite: run it to get the baseline (≥ 396). WebGL release ≈ 24 MB; **budget ≤ 25 MB**.
  - `Solana.Unity-SDK` (git `#1da8fd7f…` in `Packages/manifest.json`) is used only by `WalletBridge.cs` + `WalletSmokeTest.cs`. **OC-6:** remove it; chain I/O goes through a bridge file → a TS bundle.
- **Keys:**
  - used: F1, F3 (HUD), F6, F7 (look), F9 (chain chip), plus the Feel Lab's F2/F4/F5/F8/F10 (Mac: number keys 1–5);
  - new debug toggles go inside the F3 panel.
- **Devnet wallet: 3.75 SOL, shared with P2.**
  - Part 1 needs none.
  - Part 2 needs only small fees (burner top-ups ≤ 0.01 SOL each, session tokens).
  - **Never deploy or upgrade a program.** Before spending more than 0.2 SOL total, ask the user.

### 0.3 P2 gates for Part 2 (check with `git -C ../FallRush-p2 log --oneline main..p2` + P2's proof)
| Gate | Opens when | Unlocks |
|---|---|---|
| **G-layout** | P2.1 + P2.2 are committed (accounts + instructions) **and** P2's proof has the §10 inputs (`WorldAcc` layout + World offset, seeds, input batch format, `join_lobby` session flow, how to find an arena) | I, J (real accounts, local stack) |
| **G-devnet** | P2.9 has passed (5 unattended on-chain Finals on devnet-as) and P2's proof lists the devnet program / arena addresses and how to run its keeper + bot runner | K, L (devnet) |
| **G-bridge** | P2.10 has passed (Classic rounds 1–3 recorded by the chain worker, `start_final` handoff, one devnet run) | M (the Classic → on-chain switch) |

---

## 1. Rules
- **Worktree:** `git worktree add ../FallRush-p3 -b p3 main`. Work only there.
- **Unity without a 5 GB re-import:** `cp -cR /Users/samya/Downloads/FallRush/client-unity/Library ../FallRush-p3/client-unity/Library` (APFS copy-on-write). Run EditMode under the lock for the baseline.
- **You own (on `p3`):**
  - `client-unity/**`. Classic behaviour must stay byte-identical; on-chain mode is reached only through its own menu entry / URL arg;
  - `packages/chain/**`, `tools/chain-emulator/**`, `tools/p3/**`;
  - `docs/proof/P3-client.md`, `docs/proof/p3/**`, `scripts/p3-*.sh`.
  - **The Classic server's** on-chain handoff hook (task M) is in `client-unity/Assets/_Game/Scripts/Net/` too. Keep it **additive and flag-guarded** (`-fallrushOnchainFinal`); it's off by default.
- **Never edit** `crates/**`, `programs/**`, `maps/**`, P2's tools, `docs/07|15|19`, `PROJECT_STATUS.md`.
  - You **may run** P2's built tools (keeper, bot runner, chain worker) from `../FallRush-p2`, exactly as P2's proof says to, under the lock. Never modify them.
  - Changes you need from P2 go in your proof under **"Requests for P2"**. If one blocks you, stop and ask the user.
- **8 GB M2 Air, one heavy job at a time.** `bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p3-<what> -- <command>` for:
  - every Unity batch run, WebGL / Mac builds, Chrome measurement;
  - `cargo build` of the core (`CARGO_BUILD_JOBS=2`, own `CARGO_TARGET_DIR`);
  - `pnpm install`;
  - local-stack or devnet runs, emulator soaks longer than 2 minutes.

  P2 also uses the lock, so write code while you wait.
- **WebGL builds:** at most **two** in P3 (one after Part 1, one after Part 2), plus one more only if the user OKs it.
- **No VPS / Hostinger / SSH. Never push, never add a remote.**
  - No new Unity packages; removing `Solana.Unity-SDK` is the only package change.
  - npm packages pinned exactly: `@solana/web3.js` (the version in `programs/package.json`), `@magicblock-labs/ephemeral-rollups-sdk =0.18.0`, `esbuild` (dev), a test runner.
  - Session keys: our own TS for **`SessionTokenV2`** (`gum-react-sdk` only makes V1).
- **Verify in player builds** (Mac player and WebGL in Chrome), not only in the Editor (G4.12 lesson).
- Keypairs never in git; burner keys never logged. Throttle base-layer transactions to ≤ 1.5 tx/s.
- One MonoBehaviour per file. A number counts as measured only if you ran it. Anything else is **UNVERIFIED**.
- Commit on `p3` with your own paths: `git commit -- <paths>`. End every commit message with the attribution lines from the system reminder.

---

## 2. Part 1: build against the emulator (needs nothing from P2)

### A. `rush-core` in Unity (WASM in WebGL, native in the Editor / Mac)
1. Build `rush-core-wasm` (release) and `rush-core-ffi` (macOS bundle) **from P2's branch** (§0.2). Put them in `Plugins/WebGL/rush_core.wasm` (or `StreamingAssets`) and `Plugins/macOS/`. Write `rush_core_version.txt`.
2. **`RushCoreWasm.jslib`:**
   - instantiate once at boot (async);
   - one shared linear-memory region for World + inputs, so there's one copy per frame;
   - batch calls `rcw_step(n)` and `rcw_predict(from, to)`.
3. **`IRushCore`** in C#, with native (reuse the `FeelLab/RushCoreNative.cs` P/Invoke) and WASM implementations. Same calls, same results.
4. **Parity:**
   - EditMode: native vs P2's golden vectors;
   - WebGL: dev path `?rcselftest=1` runs 200 vector cases in the browser and prints PASS / FAIL + hashes;
   - at runtime: compare `rc_version` with the arena's core version once P2 exposes it (a request to P2: a core-version field in `WorldAcc`).
5. Commit `feat(p3-a): rush-core WASM + IRushCore`.

### B. `packages/chain`: `fallrush-chain.js`
1. TS → one esbuild file, loaded by `FallRushChain.jslib`. **Layout-agnostic:** addresses, seeds, IDL and byte offsets come from a JSON config, so Part 2 only fills the config.
2. **Wallet:**
   - **burner keypair** in `localStorage` (created on first run, exportable, never logged);
   - **`SessionTokenV2`** create / top-up / revoke for `fall_rush` (`KeyspM2ss…`, instruction `create_session_v2`; verify the encoding against the session-keys 3.1.1 source);
   - Phantom optional (`window.solana`: connect + sign a message only; claims are P5).
3. **Transports** behind one interface:
   - `emulator` (local WS);
   - `rollup`: base RPC throttled ≤ 1.5 tx/s; ER `https://devnet-as.magicblock.app` + WS; **confirm by polling `getSignatureStatuses`**; fire-and-forget `sendInputBatch`; `subscribe(account)` with a 1 s backup poll and gap detection; P0 quirk 7, retry "pending request owner … Cloner" for a few seconds after delegation.
4. **Bridge:** `fr_init(config)`, `fr_wallet()`, `fr_session(state)`, `fr_send_inputs(ptr, len)`, `fr_subscribe(addr)`, `fr_unsubscribe`. Callbacks go through `SendMessage` (base64) or a shared buffer for 30 Hz World updates; measure which is cheaper.
5. Node tests: burner wallet, session-token encoding, batching, reconnect, the throttle.
6. Commit `feat(p3-b): chain bundle + bridge`.

### C. `tools/chain-emulator`: a fake rollup
1. Node (`rush-core` WASM, built from P2's crates) at **30 Hz** in real time:
   - input batches over a local WS in the bundle's byte format;
   - applied by stamped tick (late → repeat the last input, like the program);
   - publishes World bytes every tick.
2. **Network shaping:** one-way delay 45 ± 10 ms, 0.1 % notification loss, confirm latency (configurable).
3. Default: the **Lid Flip Final with 3 players** (1 human + 2 bots via `bot_input`). Options: 12 players, other maps, the Lobby.
4. **Emulator mode for the Classic → on-chain switch:** it can also be told "start the Final with this roster + seed", to build task M before G-bridge opens.
5. Commit `feat(p3-c): chain emulator`.

### D. On-chain session (prediction, interpolation, HUD)
1. **`OnChainSession`:** a new `IChainAdapter` + transport, behind the menu entry **"Play On-Chain (preview)"** / `?onchain=emu`. It covers:
   - join, which gives a slot;
   - World → the **same snapshot structs** the HUD / visuals use;
   - `rush-core` events → the existing round / qualify / eliminate / champion handlers.

   **Reuse the HUD, Deal screen, claw, crown, receipt and chain chip unchanged.**
2. **`OnChainPrediction`** (the M2b pattern with `IRushCore`):
   - tick-keyed history;
   - inputs stamped **T+3** in batches of 3;
   - reconcile the own player at `applied_tick`, rewind + replay;
   - 120 ms smoothing, snap if off by more than 2 m.

   Remote players are interpolated (reuse `Interpolation`, 100–150 ms adaptive).
3. **`TickSync` from World ticks:** "! LAGGING" after 300 ms with no update; chip `◆ Rollup ✓` live / `! Pending` on a stall.
4. **Tick health** in F3: updates/s, input batches/s, corrections per 5 s, estimated RTT, World hash, core version.
5. **Feel targets** (emulator, RTT 90 ms, 0.1 % loss):
   - **< 1 correction per 5 s while running**;
   - no pops on lid falls;
   - the own player responds on the input frame.

   Measure in the **Mac player** first, then in WebGL (H).
6. Commit `feat(p3-d): on-chain session + prediction`.

### E. Remove Solana.Unity-SDK (OC-6)
1. Remove it from `manifest.json` + `packages-lock.json`.
2. Replace `WalletBridge` / `WalletSmokeTest` with a thin `ChainWallet` over the bundle.
3. EditMode green; WebGL and the Linux server build compile.
4. Commit `chore(p3-e): drop Solana.Unity-SDK (OC-6)`.

### F. Classic → on-chain switch, client side (built against the emulator)
1. **Flow:**
   1. the Classic server (rounds 1–3) sends a new reliable message `final_onchain { arena, match, slot_map }` at the end of round 3;
   2. during the intermission the client pre-joins / pre-subscribes (session token ready, World subscription live);
   3. at the Final's intro, transport switches from NGO to the chain session **without a scene reload if possible**; the camera / HUD continue; the chip changes to `◆ Rollup`;
   4. eliminated / spectating players watch the on-chain World.
2. **Failure path:** if the arena isn't live within N seconds, the Classic server runs the Final itself (Classic), and the chip shows `! Pending`, then `● Server`.
3. **Server side** (flag `-fallrushOnchainFinal`, off by default): in round 3's results the server emits `final_onchain` and **doesn't run the Final**. In Part 1 it calls the emulator's "start Final" API; in Part 2 it calls P2's `start_final` through P2's chain worker / keeper (task M).
4. EditMode tests for the message and both paths (the emulator stands in).
5. Commit `feat(p3-f): Classic → on-chain Final switch (emulator)`.

### G. Spectator view of the on-chain World
Eliminated players and late joiners can watch the on-chain Final from the World subscription (no input). Same camera tools as today's spectate (Tab). Commit `feat(p3-g): on-chain spectate`.

### H. WebGL build #1 + measurement
1. Under the lock: `pnpm m1:build:webgl:release` in your worktree; ≤ 25 MB, with the saving from E recorded.
2. Serve it locally with the emulator (and the Classic Mac dev server for F: `pnpm m1:up:mac` from your worktree, under the lock).
3. In Chrome (the `scripts/g47-webgl-measure.mjs` approach), measure:
   - **(a)** `?rcselftest=1` PASS;
   - **(b)** `?onchain=emu`: a full Lid Flip Final vs 2 bots: FPS / 1 % low, updates/s, corrections per 5 s, batches/s, wasm heap;
   - **(c)** a **full Classic match with the switch at the Final** (emulator);
   - **(d)** hidden tab for 10 / 30 s (R12) behaviour;
   - **(e)** plain Classic still plays a full match.
4. Screenshots to `docs/proof/p3/`. Commit `test(p3-h): WebGL build #1 measurements`.

---

## 3. Part 2: the real rollup (gated, §0.3)

### I. Real accounts on the local stack (gate **G-layout**)
1. Fill the bundle config from P2's proof §10: program ID, seeds, the `WorldAcc` layout + World offset, input batch format, `join_lobby` + session flow, arena discovery.
2. Re-sync the core build to P2's current commit; parity green.
3. Under the lock, run P2's local stack + program + keeper + bot runner **as P2's proof describes**. Point the Mac player at it (`?onchain=local`). A Lid Flip Final: you + 2 bots.
4. Fix client-side mismatches. Layout or semantics questions go in **"Requests for P2"**; stop and ask the user if one blocks you.
5. Commit `feat(p3-i): rollup transport on the local stack`.

### J. Session keys for real (G-layout)
Burner → `SessionTokenV2` → `join_lobby` → `submit_input` signed by the session key, on the local stack. Test revoke / expiry: inputs are rejected and the client shows "Session expired → rejoin". Commit `feat(p3-j): session keys end to end`.

### K. Devnet-as (gate **G-devnet**)
1. The config for P2's devnet arena (addresses from P2's proof). Only small fees: a burner top-up from the main wallet ≤ 0.01 SOL each, **≤ 0.2 SOL in total without asking**.
2. Mac player, then WebGL: **5 Lid Flip Finals on devnet-as** (you + 2 bots, via P2's keeper / bot runner).
3. Record per run: corrections per 5 s, updates/s, gap p95 / max, input confirm p50 / p95, RTT, missed / late inputs, the final placements vs the on-chain `Match`, and the explorer links.
4. **Pass:** 5/5 complete, **< 1 correction per 5 s**, nothing that feels laggy to the user (L).
5. Commit `test(p3-k): devnet Finals`.

### L. Devnet feel test (user)
Give the user a WebGL build (served locally) or the Mac player pointed at devnet. They play **3 on-chain Finals** and compare them with the Classic Final. Ask:
1. Does on-chain feel as good as Classic?
2. Anything laggy (jump, lid falls, sweeper hits)?

Record the answer. If tuning is needed, change only client-side input delay / smoothing / interpolation. **Never `rush-core`**; motor changes are requests for P2.

### M. The full match: Classic rounds 1–3 → on-chain Final (gate **G-bridge**)
1. Turn on `-fallrushOnchainFinal` on the Mac dev server (your worktree, under the lock), wired to P2's chain worker + `start_final` as P2's proof describes.
2. **3 full matches with bots:**
   - rounds 1–3 on Classic;
   - the switch;
   - the Final on devnet-as;
   - the champion on screen = the on-chain champion;
   - the receipt shows Rollup ✓ → Solana ✓ with explorer links.
3. Also test the failure path: the arena isn't ready, so the Final falls back to Classic.
4. Commit `feat(p3-m): full match with on-chain Final`.

### N. WebGL build #2 + final proof
1. WebGL release ≤ 25 MB, then Chrome:
   - **(a)** a full match with the switch on devnet;
   - **(b)** the Classic-only regression;
   - **(c)** hidden tab;
   - **(d)** FPS.
2. Write `docs/proof/P3-client.md`:
   - core build + parity (Editor / Mac / Chrome);
   - the bundle API;
   - the emulator;
   - prediction numbers (emulator, local stack, devnet);
   - SDK removal + sizes;
   - switch flow + failure path;
   - devnet runs table;
   - the user's feel answer;
   - UNVERIFIED items;
   - "Requests for P2";
   - "For P4" (where the bet / drop panels plug in);
   - "For main (merge later)" (`PROJECT_STATUS.md` lines, a doc 15 note on OC-6).
3. Commit `docs(p3): proof`. **Never push.**

---

## 4. When to stop and ask the user
- The emulator can't reach < 1 correction per 5 s at RTT 90 ms with T+3.
- Devnet feels worse than Classic and client-side tuning doesn't fix it.
- A P2 request is blocking.
- Spending more than 0.2 SOL.
- WebGL over 25 MB.
- Anything touching another session's files, the VPS, or a program deploy.

## 5. Report to the user (short, plain words)
- What works: WASM in WebGL, the emulator Final, the switch, the devnet Finals, the full match.
- Feel numbers (corrections per 5 s, latency), and the user's own verdict.
- Build sizes; Classic still fine.
- Which P2 gates were open or closed, and what's waiting on P2.
- Ask before merging `p3` into `main` (after or alongside P2's merge) and before removing the worktree.
