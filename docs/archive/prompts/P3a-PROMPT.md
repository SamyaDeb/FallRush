# P3a: the Unity on-chain client, the parts that don't depend on P2 (new session, alongside P2)

You are the Unity / web client engineer on **Fall Rush**, a Unity 6.3 WebGL 12-player knockout party game for the MagicBlock Solana Blitz V10 hackathon.
- The game simulation is moving onto a MagicBlock Ephemeral Rollup, using **`rush-core`**: our integer Rust core, the same code natively, in WASM and on-chain.
- **User decision 2026-11-22** (`docs/prompts/P2-DECISION-1.md`): **the Final (Lid Flip) runs on-chain first.** Rounds 1–3 stay on the Classic server, so one match will **switch from the Classic server to the rollup at the Final**.

**P2 is still building the on-chain side** (accounts, `tick()`, keeper) in another session. Your job, **P3a**, builds every client piece that does **not** need P2's final account layout. You test against a **local chain emulator** that behaves like the rollup (same `rush-core` step, 30 Hz, a configurable delay). A later **P3b** swaps the emulator for the real rollup and handles the Classic → on-chain switch.

**Read first, in full** (on `main` unless noted):
1. `docs/19-ONCHAIN-PLAN.md`: §1, §3, §5 P3, §9.
2. `docs/prompts/P2-DECISION-1.md`: the Final-first decision.
3. `docs/proof/P0-feasibility.md`: §3 (P0.4 state delivery, P0.5 latency: end-to-end p95 337 ms, RTT ~80–90 ms) and §4.
4. `docs/proof/P1-rush-core.md`: §4 parity incl. the Unity FFI, §11 Feel Lab, OC-3 (30 Hz).
5. Code:
   - `crates/rush-core-wasm/src/lib.rs` (the C-ABI exports), `crates/rush-core/src/world.rs`;
   - `client-unity/Assets/_Game/Scripts/FeelLab/*` (`RushCoreNative.cs` = P/Invoke to the native library, which shows the ABI in use);
   - `Scripts/Net/ClientPrediction.cs`, `Interpolation.cs`, `TickSync.cs`;
   - `Scripts/Client/ChainAdapter.cs`, `PracticeChainAdapter.cs`, `MatchHud.cs`;
   - `Plugins/WebGL/FallRushSession.jslib`;
   - `Scripts/Client/WalletBridge.cs`, `Client/Wallet/WalletSmokeTest.cs`.

---

## 0. State at hand-off (2026-11-22)

**Sessions:**

| Session | Where | Doing | Owns |
|---|---|---|---|
| P2 | `../FallRush-p2`, branch `p2` (on top of `main` @ `2decc78`) | the on-chain Final: compute-cost cuts in `rush-core`, accounts, `tick()`, keeper, bot runner, devnet runs, then the Classic → on-chain bridge | `programs/**`, `crates/**`, `tools/keeper/**`, `tools/replay/**`, `tools/bot-runner/**`, `tools/chain-worker/**`, `docs/07-*`, `docs/proof/P2*`, `docs/proof/p2/**`, its final doc commit (`docs/15`, `docs/19`, `PROJECT_STATUS.md`) |

Old worktrees `../FallRush-p0` and `../FallRush-p1` exist. Don't use or delete them.

**Facts:**
- **`rush-core` on `main`** (from P1):
  - native = WASM = sBPF = Unity FFI on every golden vector;
  - WASM exports `rc_version`, `rc_world_size`, `rc_init`, `rc_step`, `rc_predict_player`, `rc_hash` (FNV-1a 64), `rc_set_rate`;
  - map blobs in `maps/` (Lid Flip = `CrumbleShelf.mapblob`, 18.4 KB);
  - `movement_v4.json`: P2 has already changed the default to 30 Hz on its branch; call `rc_set_rate(30)` anyway.
- **P2 is changing `rush-core` performance code on its branch.** The changes are behaviour-identical (golden vectors unchanged), so build `rush_core.wasm` from **`main`'s** `crates/`. When P2 merges, a rebuild must give identical vector hashes; the P3a parity test checks this.
- **Tick rate 30 Hz** (OC-3). **Latency to devnet-as from this Mac:** RTT ~80–90 ms, confirm p95 270 ms, end-to-end p95 337 ms.
  - Design: **own-player prediction** with `rush-core`;
  - **inputs stamped T+3** (2–3 tick input delay), **batched 3 ticks per transaction**;
  - remote players interpolated ~150 ms in the past;
  - every tick's World arrives as an account notification (P0: 1 of 1,000 lost).
- **Unity:** 6000.3.28f1 URP WebGL. EditMode **396 +** (P1 added tests; run the suite to get the current count). WebGL release was 24.09 MB (build #4) / build #5 in G4.13. **Budget ≤ 25 MB.**
  - `Solana.Unity-SDK` is in `Packages/manifest.json` (git `#1da8fd7f…`), used only by `Client/WalletBridge.cs` + `Client/Wallet/WalletSmokeTest.cs`. **OC-6:** remove it from the client; chain I/O goes through a bridge file → a TS bundle. Expect about −4 MB.
- **F-keys used:** F1, F3 (HUD), F6, F7 (look), F9 (chain chip); the Feel Lab uses F5, F8, F4, F10, F2 (number keys 1–5 mirror them on Mac). **Pick new debug keys only from the free ones, or behind F3.**
- **Devnet wallet:** 3.75 SOL. P3a needs **none**: no devnet transactions except optional read-only RPC calls.

---

## 1. Rules
- **Worktree:** `git worktree add ../FallRush-p3 -b p3 main`. Work only there.
- **Unity in the worktree without a 5 GB re-import:** `cp -cR /Users/samya/Downloads/FallRush/client-unity/Library ../FallRush-p3/client-unity/Library` (APFS copy-on-write, the same trick as P1). Then run EditMode under the lock to get the baseline count.
- **You own (on `p3`):**
  - `client-unity/**`, except don't change existing gameplay / rules / map code behaviour; Classic must stay byte-identical;
  - `packages/chain/**` (new);
  - `tools/chain-emulator/**` (new);
  - `docs/proof/P3a-client.md`, `docs/proof/p3/**`, `scripts/p3-*.sh`.
- **Never touch** `crates/**`, `programs/**`, `tools/keeper|replay|bot-runner|chain-worker/**`, `docs/07|15|19`, `PROJECT_STATUS.md`, `maps/**` (read-only).
  - If you need a `rush-core` or WASM export change, **write it down as a request** in your proof ("For P2"). Never edit `crates/` yourself.
- **8 GB M2 Air, one heavy job at a time.** `bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p3-<what> -- <command>` for:
  - every Unity batch run (tests, builds);
  - WebGL builds;
  - Chrome measurement runs;
  - `cargo build` of `rush-core-wasm` (use `CARGO_BUILD_JOBS=2`, a separate `CARGO_TARGET_DIR` inside your worktree);
  - `pnpm install` of new packages;
  - long emulator soaks.

  P2 will hold the lock for builds and devnet runs, so expect waits; write code while you wait. **At most one WebGL build in P3a** (plus one more only if the user OKs it).
- **No VPS / Hostinger / SSH. Never push, never add a remote.** No new Unity packages (removing `Solana.Unity-SDK` is the only package change). Pin new npm packages exactly.
  - Expected: `@solana/web3.js` (the version in `programs/package.json`), `@magicblock-labs/ephemeral-rollups-sdk` `=0.18.0`, session keys via our own TS (gum-react-sdk 3.0.10 only makes V1 tokens; we need **`SessionTokenV2`**), `esbuild` (dev).
- **Classic mode stays the default and byte-identical.** On-chain mode is reached only through its own menu entry / URL arg. Existing tests stay green.
- **Verify in a player build, not only the Editor** (G4.12 lesson: the Editor hid a broken WebGL grade). Anything WebGL-only, such as bridge-file loading, WASM memory and timers in hidden tabs, is verified in the WebGL build in Chrome.
- One MonoBehaviour per file. A number counts as measured only if you ran it. Anything else is **UNVERIFIED**.
- Commit on `p3` with your own paths: `git commit -- <paths>`. End every commit message with the attribution lines from the system reminder.

---

## 2. Tasks, in order (each ends green and committed)

### A. `rush_core.wasm` inside Unity WebGL
1. Build `crates/rush-core-wasm` (release, `wasm32-unknown-unknown`) from `main` into `client-unity/Assets/_Game/Plugins/WebGL/rush_core.wasm` (or `StreamingAssets`). Write down the size.
2. **`RushCoreWasm.jslib`:**
   - instantiate the module **once** (async at boot);
   - **one shared linear-memory region** for World bytes + inputs, so there's one copy per frame, not per call;
   - batch API: `rcw_step(n)`, `rcw_predict(fromTick, toTick)`.
3. **`IRushCore` (C#)** with two implementations:
   - **native** (Editor / Mac, reuse `FeelLab/RushCoreNative.cs`'s P/Invoke);
   - **WASM** (WebGL, through the bridge file).

   Same calls, same results.
4. **Parity test:**
   - EditMode: native vs the golden vectors;
   - WebGL: a dev-only `?rcselftest=1` boot path runs 200 vector cases through WASM in the browser and prints PASS / FAIL + hashes to the console.

   Verified in the WebGL build in task G.
5. Commit `feat(p3a-a): rush-core WASM in WebGL + IRushCore`.

### B. `packages/chain`: the JS chain bundle (`fallrush-chain.js`)
1. TS, bundled with esbuild into one file for WebGL (`Plugins/WebGL/` or `StreamingAssets`, loaded by the bridge file). **Generic about P2's layout:** account addresses, seeds and the IDL come in as config, so P3b only swaps the config.
2. **Wallet:**
   - **burner keypair** in `localStorage` (created on first run, exportable as a backup string, never logged);
   - **`SessionTokenV2`** creation / top-up / revoke for `fall_rush` (built from the session-keys program IDL / instruction layout, `KeyspM2ss…`; the V2 instruction is `create_session_v2`, so verify it against the 3.1.1 source);
   - Phantom optional, detected via `window.solana` (P5 uses it for claims; here, connect + sign a message only).
3. **Connections:**
   - devnet base RPC (throttled ≤ 1.5 tx/s);
   - rollup `https://devnet-as.magicblock.app` + its websocket;
   - **confirm by polling `getSignatureStatuses`** (never websocket only);
   - fire-and-forget `sendInputBatch(bytes)` that never waits for a confirm;
   - `subscribe(account, cb)` with a backup poll (1 s) and gap detection.
4. **Bridge to Unity:** `FallRushChain.jslib` exposes `fr_init(config)`, `fr_wallet()`, `fr_session(state)`, `fr_send_inputs(ptr, len)`, `fr_subscribe(addr)`, `fr_unsubscribe`. Callbacks come back through `SendMessage` with byte payloads as base64, or through a shared buffer for World updates at 30 Hz (measure which is cheaper).
5. **Tests:** Node unit tests (vitest or `node:test`) for the burner wallet, session-token instruction encoding (against the program's IDL), batching and reconnect logic.
6. Commit `feat(p3a-b): chain bundle + bridge`.

### C. `tools/chain-emulator`: a fake rollup for development
1. A Node process (or an in-page worker) that **behaves like P2's arena**:
   - runs `rush-core` (WASM in Node) at **30 Hz** with real time;
   - takes input batches over a local websocket using the same byte format as the chain bundle;
   - applies them by stamped tick (late → repeat the last input, like the program);
   - publishes the World bytes every tick.
2. **Network shaping:** configurable one-way delay (default 45 ms ± 10 jitter, so RTT ~90 ms), notification loss (default 0.1 %), and confirm latency.
3. Runs a Lid Flip Final with 3 players: 1 human slot + 2 bots (rush-core `bot_input`) by default; options for 12 players / other maps.
4. **The chain bundle has an `emulator` transport,** so Unity talks to the emulator through exactly the same API it will use for the rollup.
5. Commit `feat(p3a-c): chain emulator`.

### D. On-chain session in Unity (prediction, interpolation, HUD)
1. **`OnChainSession`:** a new `IChainAdapter` implementation plus a transport, behind a new menu entry **"Play On-Chain (preview)"** (and the URL arg `?onchain=emu`). It covers:
   - join, which gives a slot;
   - World updates → the **same snapshot structs** the HUD / visuals already use;
   - `rush-core` events → the existing round / qualify / eliminate / champion handlers.

   **Reuse the HUD, Deal screen, claw, crown, receipt and chain chip unchanged.**
2. **`OnChainPrediction`:** the M2b pattern with `IRushCore`:
   - tick-keyed input history;
   - inputs stamped **T+3** and sent in batches of 3;
   - on each World update, compare the own player against history at `applied_tick`, then rewind + replay;
   - 120 ms smoothing, snap if off by more than 2 m.

   Remote players are interpolated (100–150 ms adaptive, reuse `Interpolation`).
3. **`TickSync` from World ticks:** "! LAGGING" when there's no update for 300 ms; the chain chip shows `◆ Rollup ✓` live / `! Pending` on a stall.
4. **Tick health** (in F3): updates/s, input send rate, prediction corrections per 5 s, estimated RTT, the last World hash.
5. **Feel targets in the emulator** (RTT 90 ms, 0.1 % loss):
   - **corrections < 1 per 5 s while running** (the M2b bar);
   - no visible pops on lid falls;
   - the own player responds on the same frame as input (prediction).

   Measure in the **Mac player** first (native core), then in WebGL (task G).
6. Commit `feat(p3a-d): on-chain session + prediction against the emulator`.

### E. Remove Solana.Unity-SDK (OC-6)
1. Remove the package from `Packages/manifest.json` + `packages-lock.json`.
2. Delete or replace `WalletBridge.cs` / `WalletSmokeTest.cs` with a thin `ChainWallet` that calls the chain bundle (burner / Phantom).
3. Fix every reference. EditMode is green, the WebGL and server builds compile.
4. Record the size saving in G.
5. Commit `chore(p3a-e): drop Solana.Unity-SDK (OC-6)`.

### F. Design for the Classic → on-chain switch (P3b implements it)
In the proof, specify how one match switches transport at the Final:
- the Classic server announces "Final on-chain: arena X, match Y" after round 3;
- the client pre-joins / pre-subscribes during the intermission;
- the camera / HUD continue without a scene reload if possible;
- what happens if the arena isn't ready (fall back to a Classic Final, chip `! Pending`);
- reconnects.

Add any needed C# seams now as **no-op interfaces** (no behaviour change). Commit `docs(p3a-f): Classic→on-chain switch design`.

### G. One WebGL release build + measurement
1. Under the lock: `pnpm m1:build:webgl:release` in your worktree. Size must be ≤ 25 MB; record the saving from E.
2. Serve it locally, the same way as the M1 / G4 runs (`scripts/m1-certs.sh` + nginx compose, or a plain static server) **plus** the emulator.
3. In Chrome (`scripts/g47-webgl-measure.mjs` style, under the lock):
   - **(a)** `?rcselftest=1`: WASM parity PASS;
   - **(b)** `?onchain=emu`: a full Lid Flip Final vs 2 bots. Record FPS / 1 % low, World updates/s, corrections per 5 s, input batches/s, wasm heap;
   - **(c)** hidden tab for 10 / 30 s: what happens to inputs and updates (R12). Write down the behaviour;
   - **(d)** Classic mode still plays a full match (regression).
4. Screenshots to `docs/proof/p3/`.

### H. Proof + hand-off
`docs/proof/P3a-client.md`:
- the WASM size and parity (Editor native / Mac player / Chrome WASM);
- the chain bundle API;
- the emulator design and defaults;
- prediction numbers (Mac + WebGL);
- SDK removal and build size;
- the switch design;
- UNVERIFIED items (everything against the real rollup);
- **"For P2"** (requests: exports, layout fields, `applied_tick` semantics);
- **"For P3b"**: the checklist to swap the emulator for P2's arena (config fields to fill from P2 proof §10), real session tokens, the bridge to the Classic server, devnet feel test.

Commit `docs(p3a): proof`. **Never push.**

## 3. When to stop and ask the user
- Prediction can't reach < 1 correction per 5 s in the emulator at RTT 90 ms with the T+3 design.
- Removing the SDK breaks something that needs a decision.
- The WebGL size goes over 25 MB.
- You need a `rush-core` change (write the request for P2; ask if it's blocking).
- Anything needing devnet SOL, the VPS, or another session's files.

## 4. Report to the user (short, plain words)
- WASM in WebGL: works? Size? Parity?
- On-chain session against the emulator: does it feel right? Corrections per 5 s on the Mac and in WebGL.
- Build size before / after removing the SDK; Classic still fine.
- What P3b needs from P2.
- Ask before merging `p3` into `main` (after P2 merges, or coordinated) and before removing the worktree.
