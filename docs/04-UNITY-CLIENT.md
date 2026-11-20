# 04 — Unity Client & Game Server (shared C# project)

**Status:** v2 (2026-11-18). Replaces `fall-rush-docs/04-UNITY-CLIENT.md`.

**One Unity project, two builds:**
- the **WebGL client** (desktop builds optional);
- the **Linux x86_64 Dedicated Server**.

Both share the movement, hazard and deal code, which is what makes client prediction possible.

---

## 1. Versions (resolved and pinned in M1, 2026-11-18; `Packages/manifest.json` + `packages-lock.json` committed)

| Package | Version | Note |
|---|---|---|
| Unity Editor | **6000.3.28f1 LTS** (`8f082d2fe476`, macOS ARM64) | Modules: **Web Build Support**, **Linux Dedicated Server Build Support**, Linux Build Support (Mono). Installed with the standalone `unity` CLI (`~/.unity/bin/unity install-modules`) |
| com.unity.netcode.gameobjects | **2.14.0** | Not 3.x (that needs 6000.7 beta) |
| com.unity.transport | **2.7.5** (pinned explicitly; NGO's floor is 2.6.0) | WebSockets for WebGL. 2.7.5 worked for every M1 test, so it stays pinned |
| com.unity.dedicated-server | **2.0.2** | Multiplayer Roles strip **GameObjects/components** only; assemblies are excluded with asmdef `defineConstraints` (`UNITY_SERVER`) — correction to the v2 text |
| com.unity.multiplayer.playmode | **2.0.2** | Max 4 players; on 8 GB use **at most 1 virtual player** |
| com.unity.cinemachine | **3.1.7** | pulls com.unity.splines 2.9.1 |
| com.unity.inputsystem | **1.20.1** | bundled with 6000.3 |
| com.unity.render-pipelines.universal | **17.3.0** | bundled with 6000.3 |
| com.unity.burst / collections / mathematics | 1.8.31 / 2.7.1 / 1.3.3 | resolved via UTP |
| com.unity.nuget.newtonsoft-json | 3.2.2 | resolved (MPPM + SDK) |
| com.unity.textmeshpro | 5.0.0 (Unity 6 shim to ugui 2.0.0) | pulled by the SDK; TMP Essential Resources imported for the SDK's wallet prefab |
| com.unity.toolchain.macos-arm64-linux + sdk.linux-x86_64/arm64 + sysroot.base | **1.1.1** | **Added automatically by Unity** on every Linux build (`UnityEditor.LinuxStandalone.Extensions` → `InstallToolchainIfNeeded`); removing them is undone on the next Linux build, so they are pinned and committed. This is *not* the optional IL2CPP package `com.unity.toolchain.macos-arm64-linux-x86_64` 2.0.5 named in v2 — that one is still not installed |
| com.solana.unity_sdk | git `https://github.com/magicblock-labs/Solana.Unity-SDK.git#1da8fd7fe9e035ad0f6fb627997f3f2a6f134475` | **✅ approved (D-15).** Tag v1.2.9 (`3d139411`) **breaks the WebGL build** (§8). `1da8fd7f` = v1.2.9 + upstream fix (DLL renamed to `BouncyCastle.Cryptography.dll`, three broken asmdefs removed); no newer tag exists |

**Removed from the template:** collab-proxy, visualscripting, timeline, multiplayer.center, ai.navigation.

**Disk (measured 2026-11-18):** editor + 3 modules = 15 GB; `client-unity/Library/` = 4.7 GB after WebGL + Linux + macOS targets.

## 2. Transport: WebSockets everywhere (decision D-4 revised to WebGL)

- **Why:** WebGL clients cannot use UDP. Every client in a match must share one transport.
- **The server** listens with UTP WebSockets, plain `ws` (`UseWebSockets = true`, `UseEncryption = false`), `SetConnectionData(true, "0.0.0.0", port, "0.0.0.0")`. The first argument `true` stops UTP's own `-port`/`-ip` parsing; our order is `-port` → `GAMESERVER_PORT` → 7777.
- **TLS** terminates at the VPS's nginx (doc 13 §2). The browser connects with `wss://`, and the server sees plain `ws`. **VERIFIED in M1**: locally (nginx 1.24.0 in Docker, self-signed cert, Chrome) and on the VPS (nginx 1.24 + Let's Encrypt on an sslip.io name, Linux x86_64 image; RTT p50 66 / p95 83 ms). `docs/proof/M1-transport.md`.
- **Exact WebGL client API for `wss` (from the UTP 2.7.5 / NGO 2.14.0 source):**
  - `UseWebSockets = true; UseEncryption = true; SetConnectionData(true, host, port); ConnectionData.WebSocketPath = path; SetClientSecrets(host)`.
  - UTP's WebGL `WebSocketNetworkInterface` builds `"wss://"` when a secure hostname is set (`SecureNetworkProtocolParameter.Hostname`, filled by `SetClientSecrets`) and `"ws://"` otherwise, then `new WebSocket(url)` in JS — **the browser does TLS**. Hostnames are allowed on 6000.1+ (`HOSTNAME_RESOLUTION_AVAILABLE`).
  - `SetConnectionData` **replaces `ConnectionData` and drops `WebSocketPath`** — set the path afterwards.
- **URL path:** supported (`ConnectionData.WebSocketPath`, default `/`). The UTP server compares the request path **exactly** (404 otherwise), so no query strings; it also requires an `HTTP/1.1` request line and `Connection: upgrade` (case-insensitive) → nginx needs `proxy_http_version 1.1` + `Upgrade`/`Connection` headers.
- **Same-origin routing works:** `GET /` with `Upgrade: websocket` → game server, everything else → static WebGL (`infra/nginx/fallrush-m1.conf`, documented-safe `if` + `return 418` + named location). A WS-only server block (production `gs1.<domain>` shape) also works.
- The fallback (UTP built-in secure WebSocket with certificates) was **not needed**.
- **Consequence:** WebSocket is TCP, so everything is reliable and ordered. Snapshots go out at 15 Hz (tunable to 30 Hz). Loss shows up as jitter, so the interpolation buffer is 100 ms, adaptive up to 150 ms.
- **Measured (M1, localhost):** RTT p50 ≈ 33 ms for both `ws` and `wss` on loopback ≈ one 30 Hz frame. Inference (not measured separately): sends are flushed on frame/tick boundaries and the server is capped at 30 fps, so the ping RTT includes up to ~1–2 frames of quantisation. Budget for this in M2's input/snapshot timing.

## 3. Folder layout

```text
client-unity/Assets/_Game/
  Art/ Audio/ Materials/ Shaders/ VFX/
  Prefabs/{Rushling,Obstacles,Maps,UI}
  Scenes/{Boot,Menu,Lobby,Map_BlockPartyDash,Map_GooTower,Map_StarScramble,Map_CrumbleShelf,(Map_BonkFactory)}
  Scripts/
    Shared/        # pure C#, no UnityEngine.Object deps where possible (testable): MovementConfig, KinematicMotor, HazardMath, Deal, Ruleset, Protocol
    Net/           # NetBootstrap, Messages (custom messaging), ServerSim, ClientPrediction, Interpolation
    Server/        # [UNITY_SERVER] MatchDirector, RoundRules, RelayClient (HTTP to Rush Relay), Anticheat, BotBrain
    Client/        # Input, Camera, HUD, Screens, RushlingVisuals (procedural anim), Audio, Wallet
    Maps/          # MapDefinition, HazardDefinition, HazardBehaviour, Checkpoint, CrumbleTile, StarSpawner
  ScriptableObjects/{Maps,Hazards,Rulesets,Cosmetics}
  Tests/{EditMode,PlayMode}
```

**Assemblies (M1):** `FallRush.Shared` (no engine refs) · `FallRush.Net` · `FallRush.Client` (`!UNITY_SERVER`) · `FallRush.Server` (`UNITY_SERVER || UNITY_EDITOR || FALLRUSH_DEV_SERVER`) · `FallRush.Client.Wallet` (Editor + WebGL only, `!UNITY_SERVER`; the only one that references Solana.Unity-SDK) · `FallRush.Editor` · `FallRush.Tests.EditMode`.

The `UNITY_SERVER` asmdef constraints strip client-only **code**; Multiplayer Roles strip client-only **components** (rendering, audio, UI, VFX) from the server build. Note: anything under `Resources/` ships in every build — the SDK copies its wallet prefabs to `Assets/Resources/SolanaUnitySDK/`, so they are in the server build too (strip in M5).

## 4. Netcode architecture (decision D-1: NGO + custom prediction)

NGO is used for:
- connection approval (guest token)
- scene management (round maps via `NetworkSceneManager.LoadScene`)
- tick time (`NetworkManager.ServerTime`, `NetworkConfig.TickRate = 30`)
- **custom messages** (`CustomMessagingManager.SendNamedMessage`)

**Player movement is not** a `NetworkTransform`. We run our own input/snapshot protocol over named messages.

### 4.1 Messages (binary, `FastBufferWriter`)

| Name | Direction | Delivery | Payload |
|---|---|---|---|
| `in` | C→S | Unreliable (WS: effectively reliable) | `lastSeq u32`, 3 redundant `InputCmd` (seq, tick, moveX i8, moveY i8, buttons u8, camYaw u16) |
| `snap` | S→C | UnreliableSequenced | `serverTick u32`, `ackSeq u32` (per recipient), N × `PlayerState` (slot u8, pos 3×i16 at cm/4 precision relative to map origin, vel 3×i16, yaw u8, flags u8, stunTicks u8) |
| `evt` | S→C | ReliableSequenced | Match / round / checkpoint / qualify / eliminate / tile-drop / star / chain-status events with a `seq` |
| `hello` | C→S | Reliable | protocolVersion, buildVersion, cosmeticPreset |

**Snapshot size:** about 12 × 20 B = 240 B, at 15 Hz ≈ 3.6 KB/s per client.

### 4.2 Server tick (30 Hz, `ServerSim`)

1. Drain input queues. Validate them: finite values, ranges, seq freshness, ≤ 3 new commands per tick per player, clamp the axes.
2. For each player, run `KinematicMotor.Step(state, input, dt, world)`. Missing input → repeat the last input for up to 3 ticks, then neutral.
3. Evaluate hazards via `HazardMath.Pose(def, seed, tick)` and resolve hazard contacts → knockback/stun.
4. Checkpoints, finish triggers, fall volumes, goo, tiles, stars → `RoundRules`.
5. Send a snapshot every 2nd tick (15 Hz). Raise it to every tick (30 Hz) if measured bandwidth and CPU allow.
6. Domain events → `evt` to clients + `RelayClient.Enqueue(event)` (non-blocking HTTP to Rush Relay).

### 4.3 Client prediction (local player only)

```
on Update: accumulate dt; while acc >= 1/30: cmd = Input.Sample(); send cmd; 
           state = Motor.Step(state, cmd, 1/30, LocalWorld(predTick)); history[cmd.seq] = (cmd, state); predTick++
on snap:  auth = snap[mySlot]; drop history ≤ ackSeq;
          if |history[ackSeq].pos - auth.pos| > 0.05 m or stun mismatch:
              state = auth; replay all cmds > ackSeq through Motor.Step  (reconcile)
          visual offset smoothing: render pos lerps error to 0 over 0.1 s (no snapping)
```

**`LocalWorld(tick)`** evaluates hazards at the predicted tick with the same `HazardMath`. Deterministic hazards are what make prediction against moving obstacles accurate.

**Not predicted:**
- **Knockback from hazards is predicted.** It is deterministic.
- **Pushes from other players are not predicted.** The server corrects them, and the smoothing hides it.

### 4.4 Remote players

Snapshot interpolation at `renderTime = serverTimeEstimate − interpDelay` (100 ms, adaptive). No extrapolation beyond 150 ms. After that, freeze and show a "lagging" icon.

### 4.5 Go / no-go gate (end of day Sun Nov 22)

- **Test:** 3 clients at 150 ms RTT and 2% loss (Unity's network simulator plus `tc` on the server box).
- **Pass:**
  - the local player feels responsive;
  - corrections are < 1 per 5 s while running the course;
  - no visible rubber-banding on hazards.
- **Fail → fix window first (D-1b, approved):** until **Mon Nov 23 12:00 UTC**, find and fix the cause. Work through this in order, measuring each:
  1. **Determinism:** client and server motor produce identical results for the same inputs (EditMode replay test of a recorded input log). Any float or order difference is the usual cause of constant corrections.
  2. **Tick alignment:** the client predicts the same tick number the server simulates; check the input-delay buffer and clock-sync estimate.
  3. **Input delivery:** 3 redundant inputs per packet; the server never stalls on a missing input (it repeats the last one).
  4. **Moving-platform and hazard state:** hazards come from `pose = f(seed, tick)`, so the client must use the same tick; platform carry must be in the motor, not in a Unity physics callback.
  5. **Correction smoothing:** blend small errors over 100–150 ms instead of snapping; snap only above a threshold.
  6. **Server tick jitter:** p95 frame time under the tick budget on the VPS.
- **Still failing after the fix window → fallback F1:** **owner-predicted movement with server validation**.
  - The client is authoritative for its own position.
  - The server runs the same motor as a validator: speed, acceleration, jump and teleport bounds; hazard and stun authority stays on the server, and the server snaps a client back on violation.
  - This is weaker anti-cheat, disclosed in the demo.
  - **FishNet** stays the post-hackathon option.

## 5. Kinematic motor (`Shared/KinematicMotor.cs`)

- **Collision:** capsule sweep against the `Gameplay` layer with `Physics.CapsuleCast`, up to 3 slide iterations; ground probe; slope limit 45°; step offset 0.25.
  - On the server, PhysX scene queries are deterministic enough within a single build. The client uses the same scene geometry.
- **Moving platforms:** if the ground collider has a `HazardBehaviour` with `carry`, add the platform's velocity from `HazardMath` at this tick.
- **State struct:** `pos, vel, grounded, coyoteTicks, jumpBufferTicks, diveCooldownTicks, stunTicks, immunityTicks, lastCheckpoint`. It is a blittable struct, so history snapshots are cheap.
- **Constants:** `MovementConfig` (GDD §7), with Chaos modifiers applied as multipliers.

## 6. Rushling visuals (client only)

`RushlingVisuals` reads the interpolated or predicted state and drives:
- squash/stretch springs
- lean
- noodle-arm verlet chains (tube mesh)
- foot stepping
- key spin
- the screen-face atlas
- stun pop

See doc 17 §2.3. Per-player cosmetics come from `CosmeticPreset` (index into the SO tables), assigned by the server.

**Performance budget:** 12 Rushlings × (2 arms × 6 points verlet + tube mesh 8×6) is CPU-light. Arm meshes are updated with `Mesh.SetVertices` on a reused buffer.

## 7. Screens & HUD (doc 17 §5)

- **Screen flow:** Boot → Menu → Lobby → Deal → RoundIntro → HUD → RoundResult → Intermission (+ Fan Picks prompt) → … → Champion → Receipt → Rewards.
- **Chain chip:** driven by `evt.chainStatus` from the server, which the Relay forwards. The client never reads the chain directly; the web page does.

## 8. Wallet (decision D-8: Solana.Unity-SDK)

- Optional "Connect Wallet" in the menu. WebGL uses the SDK's web wallet adapter (Phantom / Solflare): `Web3.Instance.LoginWalletAdapter()` on a `Web3` component with `rpcCluster = DevNet` (the SDK sample's own path). Code: `Scripts/Client/Wallet/WalletSmokeTest.cs`, the only assembly referencing the SDK (Editor + WebGL only).
- **M1 result (2026-11-18):**
  - **Tag v1.2.9 breaks the WebGL build** on 6000.3: UnityLinker cannot resolve `BouncyCastle.Cryptography` 2.0.0.0 because the SDK ships it as `BouncyCastle.Crypto.dll` (upstream issue #257). **Pinned to upstream `main` `1da8fd7f`** (the fix, v1.2.9 + 2 commits) — approved (D-15).
  - With `1da8fd7f`: WebGL release ✅, Linux server build ✅ (SDK compiles into the server project but not into our server game code), EditMode ✅. Release size **+4.0 MB** compressed (11.83 → 15.83 MB).
  - In Chrome the wallet picker (Phantom/Solflare) opens without errors or freezes. A real Phantom connect is **UNVERIFIED**.
  - The adapter loads `@magicblock-labs/unity-wallet-adapter@1.2.1` from cdn.jsdelivr.net at runtime; self-host it in M5. The SDK copies prefabs into `Assets/Resources/SolanaUnitySDK/` (shipped in every build) and needs TMP Essential Resources.
- **Purpose in Unity:** link a wallet for the Crown Capsule mint (signed nonce → Relay `/v1/wallet/verify`).
- **Fan Picks** are done on the **web live page** with gum-sdk session keys (doc 07 §4.6).
  - Why there: the TS session-key path is proven in the official examples.
  - Optional in-Unity Fan Picks via the SDK's `SessionWallet` (`CreateSessionIX`, verified in the SDK source) is a stretch goal.
- **If the SDK fails on Unity 6 WebGL:** fall back to "Link wallet" opening the web page with a one-time code.

## 9. Bots (labelled)

- **What they are:** `BotBrain` produces `InputCmd`s from waypoint graphs per map, with noise and mistakes. Bots run **inside the server process** as virtual players: no network, a "BOT" tag in the HUD, `bot=true` in the roster hash.
- **Cost:** this gets us 12 occupants for testing on cheap hardware.
- **Ranked rule:** ranked on-chain matches require ≥ 4 humans; bots are allowed and disclosed.
- **Also used for** map QA (doc 08 §9).

## 10. Builds

- **Server:**

  ```
  Unity -batchmode -nographics -quit -projectPath client-unity -buildTarget Linux64 -standaloneBuildSubtarget Server -executeMethod Build.Server
  ```

  Mono backend, then a Docker image `fallrush-gameserver` (ubuntu base). `-standaloneBuildSubtarget Server` is verified in the 6000.3 command-line docs.
- **WebGL:** `Build.WebGL` (Brotli compression, no exceptions in release / "Explicitly Thrown" in dev, code stripping Medium, memory settings default).
  - Build on the M2 with the Editor closed (memory).
  - Host the build behind the Next.js site at `/play`.
- **Real commands (M1):** `pnpm m1:build:webgl` (dev), `pnpm m1:build:webgl:release`, `pnpm m1:build:server`, `pnpm m1:test:unity`, plus `pnpm m1:build:macserver` (Apple Silicon local test server). All go through `scripts/m1-unity.sh`: refuses to run while a Unity Editor is open, logs to `.m1-logs/`, wraps Unity in `/usr/bin/time -l`.
- **Measured on the M2 Air (details: `docs/proof/M1-transport.md`):** WebGL release 523 s first / 294 s incremental; dev 714 s first / 238 s warm; Linux server 158 s first / 47–62 s incremental; peak RSS 1.1–1.5 GB; swap is used while other apps are open.
- **Brotli serving decision:** Decompression Fallback **off**; nginx adds `Content-Encoding: br` + the right `Content-Type` per file (verified). Unity 6000.3 **never compresses development builds** (manual: "Development builds aren't compressed"), so dev builds serve raw `.wasm`/`.data`/`.js`. `chmod -R a+rX` the build output (Unity writes `.br` files as 0600).
- **Flakiness seen:** twice the Unity licensing client timed out (`Timed-out after 30.00s, waiting for EntitlementDetailsResponse`) while the Mac was swapping heavily; Package Manager then loaded no packages and compilation failed. A plain re-run passed.

## 11. Tests

**EditMode:**
- `KinematicMotor` (max speed, jump apex, dive cooldown, slope)
- `HazardMath` vectors
- `Deal.cs` vs `deal_vectors.json`
- `Ruleset` targets
- protocol encode/decode round-trip
- input validation (NaN, out of range, flood)

**PlayMode:**
- checkpoint order
- fall → respawn
- round flow with bots
- reconnect within 20 s
