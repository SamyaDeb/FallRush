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
- the server's 30 Hz tick event (`NetworkConfig.TickRate = 30`, `NetworkTickSystem.Tick`); the tick *number* is our own (`SimCore.Tick`), and clients use our own clock (`TickSync`, §4.3), not NGO's `ServerTime`
- **custom messages** (`CustomMessagingManager.SendNamedMessage`)
- scene management stays **off** in M2: both sides load `Map_Sandbox` locally (server on start, client from the Lobby). Round maps via `NetworkSceneManager` are an M4 item.

**Player movement is not** a `NetworkTransform`. We run our own input/snapshot protocol over named messages.

**Frame order (NGO 2.14 source, `NetworkManager.NetworkUpdate`):** incoming messages are processed in **EarlyUpdate**, the tick event fires in **PreUpdate**, and outgoing messages are flushed in **PostLateUpdate**. So a server frame is: receive inputs → simulate tick(s) → send snapshots.

**Server frame cap: 60 fps** (`NetBootstrap.DefaultServerFps`, override `-fallrushServerFps`), tick stays 30 Hz. Measured in M2a (Mac dev server, 12 occupants): localhost RTT p50 **33 → 17 ms**, CPU 1.0 → 1.7 % of one core, frame work p95 ≤ 0.7 ms (`docs/proof/M2-netcode.md` §2).

### 4.1 Messages (binary, little-endian; codecs in `Shared/NetMessages.cs`, strict decode)

| Name | Direction | Delivery | Payload | Size |
|---|---|---|---|---|
| `in` | C→S, every client tick (30 Hz) | Unreliable | `count u8 (1–3)` + count × `InputCmd`, newest first. `InputCmd` = `seq u32, tick u32, moveX i8, moveY i8, buttons u8, camYaw u16` (13 B) | **40 B** |
| `snap` | S→C, every 2nd tick (15 Hz; `-fallrushSnapHz 30` for every tick) | UnreliableSequenced | header (16 B): `serverTick u32, ackSeq u32, inputLead i8, ownSlot u8, own timers 5×u8 (coyote, jumpBuffer, diveCooldown, landTicks, immunity), count u8` + count × `PlayerState` (22 B): `slot u8, pos 3×i32 mm, vel 3×i16 cm/s, yaw u8, flags u8, stunTicks u8` | **280 B** for 12 |
| `evt` | S→C | ReliableSequenced | Match / round / checkpoint / qualify / eliminate / tile-drop / star / chain-status events with a `seq` (M4) | — |
| `hello` | C→S (approval payload) | Reliable | protocolVersion, buildVersion, cosmeticPreset | 4–36 B |

- **Protocol version 2** (M2). A v1 client is rejected with the readable M1 message.
- **`moveX/moveY`** are the stick after the client rotated it by `camYaw`, i.e. world X/Z in ±127 with length ≤ 127. The server does no trigonometry on input. `camYaw` only sets the dive direction when there is no move input.
- **`buttons`:** bit0 jump held, bit1 jump pressed (edge), bit2 dive held, bit3 dive pressed (edge). The client accumulates edges between ticks, so a tap is never lost.
- **Changes vs the v2 text:**
  - `in` starts with a `count` byte instead of `lastSeq`, since the newest command already carries it.
  - The `snap` header adds `inputLead` (clock-sync feedback, §4.3) and the owner's 5 motor timers. Without the timers a client rewind could not restore the complete `MotorState`.
- **`flags`:** grounded 1, diving 2, dive-landing 4, respawning 8, BOT 128.
- **Measured (M2a, one client, 12 occupants):**

  | Rate | Snapshot payload | Server socket out per client | Inputs in (payload / socket) |
  |---|---|---|---|
  | 15 Hz | 4.2 KB/s | 5.2 KB/s | 1.2 / 2.9 KB/s |
  | 30 Hz | 8.4 KB/s | 10.0–10.2 KB/s | same |

  The socket figures come from `nettop` on the server process and include NGO framing (named-message hash u64 + headers) and WebSocket framing.
  - Per message that is about +45–60 B on `in` and about +60 B on `snap` beyond the payload.
- **Delivery over WebSockets:** every pipeline rides one ordered TCP stream. `UnreliableSequenced` (UTP fragmentation + sequenced stage) never sees reordering on TCP, so its drop-old logic is inert. The client also drops any snapshot whose `serverTick` is not newer. Loss shows up as a stall (head-of-line blocking), never as a missing packet.

**Position encoding (corrected 2026-11-18):** v2 had `3×i16 at cm/4`, which covers only ±82 m. A 75–110 s race at up to 7 m/s needs a course of up to ~500 m, so positions are `i32` millimetres (±2,147 km).
- **The authoritative state *is* the wire precision.** `MotorState` stores pos in mm and vel in cm/s as integers, and `KinematicMotor.Step` quantises at the end of every step (round half away from zero). The state the client receives is exactly the state the server keeps simulating, so reconciliation never chases rounding error.

### 4.2 Server tick (30 Hz, `ServerSim` → `SimCore`)

`Net/ServerSim.cs` is the NGO adapter. `Shared/SimCore.cs` is the engine-free simulation, so EditMode tests can drive it.

1. **Inputs (EarlyUpdate):** each `in` goes to that player's `InputSanitizer`, a fixed-size buffer keyed by tick, so a flood cannot grow memory.
   - **Freshness:** a command must have `seq` > the last accepted one. Older ones are the normal redundant copies and are dropped.
   - **Axes:** clamped to ±127, length ≤ 127; unknown button bits are masked.
   - **Rate:** at most **3 new commands per player per tick**. Extra on-time ones wait for their redundant copy in the next message. Over 32 messages per tick, the extra are dropped undecoded and count as violations.
   - **Window:** `tick` must be within `[next, next+30]`. Further ahead is a violation. A late command (tick already simulated) is dropped, but its jump/dive press edges are applied on the next tick.
   - **Kick:** 50 violations in a minute (doc 11). **Measured:** a 200-message junk burst was kicked within its first tick, and server RSS rose 0.3 MB.
2. **Step (PreUpdate tick):** for each occupied slot, take the command for exactly this tick, then run `KinematicMotor.Step(ref state, cmd, 1/30, PhysxMotorWorld)`.
   - **Missing command:** repeat the last one, with edges cleared, for up to 3 ticks; then neutral.
   - **Bots:** the same motor, fed by `TestBotInput`.
3. *(M2b/M3)* Hazards via `HazardMath.Pose(def, seed, tick)` → knockback/stun.
4. **Fall volume:** `y < killY` (−20 m) → `respawning` for 30 ticks (1.0 s) → spawn point. Then player–player **soft separation**: each overlapping pair is pushed apart at ≤ 2.5 m/s, with no stun. It is server-only, not predicted, and goes through the motor's collide-and-slide (`Displace`).
   - *(M3/M4)* Checkpoints, finish, goo, tiles and stars come later → `RoundRules`.
5. **Snapshot** every 2nd tick (15 Hz) to each human: one shared body plus a per-recipient header. 30 Hz is a flag; the M2b gate data decides.
6. *(M4/M6)* Domain events → `evt` + `RelayClient.Enqueue`.

**Slots:** humans take the lowest free slot on connect. `-fallrushBots N` (env `GAMESERVER_BOTS`) fills up to N occupants with labelled TEST bots, and a joining human replaces the highest-numbered bot.

**Logs every 10 s** (the M1 `ServerStats` pattern):
- `[gs] stats`: tick rate, frame interval, frame work p50/p95/max.
- `[gs] sim`: humans/bots, sim step p50/p95, snapshots and payload B/s per client, input messages/payload, accepted/late/repeated/neutral/burst, violations (far/flood/malformed), kicks.

### 4.3 Client tick clock and prediction (local player only)

**Tick agreement (M2a, `Shared/TickSync.cs`):** a command stamped `tick = T` is applied by the server **at step T**, so the client must run ahead of the server.
- **Server feedback:** on arrival the server records `lead = cmd.tick − nextServerTick` and puts the window minimum in each snapshot (`inputLead`).
- **Dilation:** the client runs its 30 Hz clock at `rate = 1 − 0.02·(lead − target)`, clamped to ±6 %, so `lead` settles at `target`. When `lead` is more than 8 ticks off it re-syncs in one jump: forward, or a pause when ahead. Ticks never go backwards. Reports in flight are ignored for 0.5 s after a jump.
- **Adaptive target:** starts at 2 ticks. +1 (max 6) after a late command; −1 (min 1) after 10 s in which the lead never dipped below the target.
- **Start:** `predTick = serverTick + ceil(RTT/dt) + target`. The clock keeps running before input starts (lobby, map load), so the first commands are on time.
- **Steady state:** `predTick ≈ serverTickNow + (RTT/2 + send/receive quantisation)/dt + target`. That is the "RTT/2 + one tick" of the spec, with the quantisation measured rather than guessed.
- **Measured (M2a, localhost):** lead holds at target 2 (67 ms), 0 late commands, 0 re-syncs over 60 s windows. The closed-loop EditMode test at 20/75/150 ms one-way latency with jitter converges to target 2 with 0 late commands after 10 s.

**Prediction (M2b, `Net/ClientPrediction.cs`; history keyed by tick):**

```
each client tick T: cmd = Input.Sample(); send cmd(tick=T); state = Motor.Step(state, cmd, 1/30, LocalWorld(T)); history[T] = (cmd, state)
on snap(S):          auth = snap[mySlot] + own timers; drop history < S;
                     if |history[S].pos − auth.pos| > 0.05 m or flags/stun differ:
                         state = auth; replay cmds S+1 … predTick through Motor.Step   (reconcile)
                     ackSeq ≠ seq of history[S].cmd → the server used a repeated input at S (expect a correction)
                     visual offset smoothing: render pos blends the error to 0 over 0.1–0.15 s; snap only above ~2 m
```

**`LocalWorld(tick)`** evaluates hazards at the predicted tick with the same `HazardMath`. Deterministic hazards are what make prediction against moving obstacles accurate.

**Not predicted:**
- **Knockback from hazards is predicted.** It is deterministic.
- **Pushes from other players are not predicted.** The server corrects them, and the smoothing hides it.

**M2a rendering (no prediction):** every player, including the local one, is drawn from snapshots 100 ms behind the newest (`ClientSim.TrySample`, clamped, no extrapolation). Local control therefore feels the full RTT + 100 ms until M2b.

### 4.4 Remote players

Snapshot interpolation at `renderTime = serverTimeEstimate − interpDelay` (100 ms, adaptive). No extrapolation beyond 150 ms. After that, freeze and show a "lagging" icon.

### 4.5 Go / no-go gate (end of day Sun Nov 22)

- **Test:** 3 clients at 150 ms RTT and 2% loss. **Correction (M2):** Unity's network simulator is a UTP pipeline stage and has no effect on WebSocket/TCP, where loss appears as stalls. Latency/jitter is injected at the TCP level instead (a pinned `toxiproxy` image or `tc netem`, M2b), and what "2 % loss" means there is recorded in `docs/proof/M2-netcode.md`.
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

## 5. Kinematic motor (`Shared/KinematicMotor.cs`) — as built in M2a

`KinematicMotor.Step(ref MotorState, in InputCmd, float dt, IMotorWorld)` is a pure function of its inputs:
- no `Time`, `Random` or trigonometry; facing uses a literal 256-entry integer sine table, `Shared/Yaw256.cs`;
- it runs identically on server and client;
- `FallRush.Shared` has no UnityEngine reference (its own `Vec3`).

**Constants:** `packages/shared/config/movement_v1.json`. It is canonical JSON with integers only (mm, mm/s, ms, permille) and has its own sha256 (`pnpm hash`).
- `pnpm gen:movement` generates `Shared/MovementConfigData.g.cs`.
- An EditMode test hashes both the JSON file on disk and the C# constants and compares them.
- It is **not** in `ruleset_v1.json`; the on-chain hash is untouched. It merges into a ruleset v2 in M6.
- Times become ticks with round-half-up: coyote 3, jump buffer 4, dive cooldown 30, dive-land stun 11, jump-cancel window 5, respawn 30.

**Capsule:** upright, radius 0.31 m, height 1.0 m (= the visual capsule), skin 1 cm. `pos` is the feet point.

**Step order:**
1. Depenetrate.
2. Input, timers, stun, dive-landing.
3. Facing (from the move vector; `camYaw` only when diving without input).
4. Jump (buffer + coyote, or a jump-cancel out of a dive landing).
5. Dive.
6. Horizontal: ground accel 60 / decel 45 m/s², air control 35 %, no steering while diving.
7. Gravity, integrated exactly per step (`dy = v·dt − ½g·dt²`, fall multiplier 1.35 when `vy ≤ 0`, max fall 30 m/s). This is why the 1.6 m apex holds at 30 Hz (measured 1.6 m ± 2 cm in tests).
8. Collide-and-slide.
9. Ground probe + snap.
10. Landing transitions.
11. Quantise.

**Collision (`IMotorWorld`):**
- **Queries:** `CapsuleSweep`, `Raycast`, `ComputePenetration`.
- **In play:** `Net/PhysxMotorWorld.cs`, using `PhysicsScene.CapsuleCast/Raycast/OverlapCapsule` plus `Physics.ComputePenetration`, on the **`Gameplay` layer (8)** only, triggers ignored. `Physics.simulationMode = Script` and nothing is ever simulated.
- **In tests:** an analytic world (exact distances for planes and boxes).
- **Slide:** up to 3 iterations, projecting the remainder on the hit plane. A surface steeper than 45° is a wall: only horizontal sliding while grounded or rising, so it can't be climbed.
- **Step-up** (grounded, wall hit): up by 0.25 m, forward by the blocked remainder, then down. It is accepted only if the face it lands on is walkable and ≤ 0.25 m above the old feet.
- **Ground:** a downward sweep, plus a snap of up to 0.25 m while walking, so ramps and small drops don't launch you. Walkable means `n.y ≥ cos 45°`.
  - **Edge contacts** report the impact normal (verified on PhysX in EditMode). For those, a short ray just past the edge reads the face normal.
  - An edge counts as ground only if that face is ≤ 0.25 m above the feet while walking, or **≤ 5 cm (`ledgeTolerance`) when landing from the air**. The new constant came from a PhysX test: without it, a 1.6 m jump could hang on the corner of a 1.7 m wall and step up.

**Jump / coyote / buffer:**
- One jump, `v0 = √(2·g·1.6)`; no double jump.
- **Coyote:** a jump is allowed on the 3 ticks after the last grounded tick.
- **Buffer:** a press counts for 4 ticks after it is made. In practice, presses up to 3 ticks before the landing tick fire on the first grounded tick.

**Dive:**
- **Trigger and impulse:** the press edge, when not stunned, diving or in a dive landing, and cooldown is 0. Horizontal speed becomes `max(9, current speed along the dive dir)`, vertical 3 m/s; cooldown 30 ticks.
- **Landing:** 11-tick ground stun with no input. A fresh jump press within the first 5 ticks after touchdown cancels it and jumps. Buffered presses from before landing don't count.

**Moving platforms (M2b):** carry comes from `HazardMath` at this tick through `IMotorWorld` (`MotorHit.SurfaceId`), never from a Unity physics callback.

**State struct:** `MotorState` holds `pos i32×3 mm, vel i16×3 cm/s, yaw u8, flags u8, stunTicks u8, coyote, jumpBuffer, diveCooldown, landTicks, immunity` (all u8). It is blittable and compared bit-for-bit in the determinism tests. `lastCheckpoint` lives in round rules (M3), not in the motor.

**Chaos modifiers (M5):** applied as multipliers on a `MovementConfig` instance.

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
- **M2 stand-in:** `Shared/TestBotInput.cs`, a tiny deterministic wanderer (hops, rare dives) used only to fill 12 slots for load and feel tests (`-fallrushBots 12`). It goes through the same sanitiser and motor and is flagged BOT in snapshots and the HUD. It is not BotBrain.

## 10. Builds

- **Server:**

  ```
  Unity -batchmode -nographics -quit -projectPath client-unity -buildTarget Linux64 -standaloneBuildSubtarget Server -executeMethod Build.Server
  ```

  Mono backend, then a Docker image `fallrush-gameserver` (ubuntu base). `-standaloneBuildSubtarget Server` is verified in the 6000.3 command-line docs.
- **WebGL:** `Build.WebGL` (Brotli compression, no exceptions in release / "Explicitly Thrown" in dev, code stripping Medium, memory settings default).
  - Build on the M2 with the Editor closed (memory).
  - Host the build behind the Next.js site at `/play`.
- **M2 commands:**
  - `pnpm gen:movement`: regenerate `MovementConfigData.g.cs` after editing `movement_v1.json`.
  - `bash scripts/m1-unity.sh sandbox -executeMethod SandboxBuilder.Run`: rebuild `Map_Sandbox`.
  - `scripts/m1-macserver.sh start -fallrushBots 12 [-fallrushSnapHz 30] [-fallrushServerFps 30]`.
  - `scripts/m2-probe.sh <ws-url> <secs> run|idle|flood`: headless scripted client.
  - `scripts/m2-measure.sh <snapHz> <serverFps> <secs> <label>`: 12-occupant measurement.
  - Both server builds now include `Map_Sandbox`.
- **Real commands (M1):** `pnpm m1:build:webgl` (dev), `pnpm m1:build:webgl:release`, `pnpm m1:build:server`, `pnpm m1:test:unity`, plus `pnpm m1:build:macserver` (Apple Silicon local test server). All go through `scripts/m1-unity.sh`: refuses to run while a Unity Editor is open, logs to `.m1-logs/`, wraps Unity in `/usr/bin/time -l`.
- **Measured on the M2 Air (details: `docs/proof/M1-transport.md`):** WebGL release 523 s first / 294 s incremental; dev 714 s first / 238 s warm; Linux server 158 s first / 47–62 s incremental; peak RSS 1.1–1.5 GB; swap is used while other apps are open.
- **Brotli serving decision:** Decompression Fallback **off**; nginx adds `Content-Encoding: br` + the right `Content-Type` per file (verified). Unity 6000.3 **never compresses development builds** (manual: "Development builds aren't compressed"), so dev builds serve raw `.wasm`/`.data`/`.js`. `chmod -R a+rX` the build output (Unity writes `.br` files as 0600).
- **Flakiness seen:** twice the Unity licensing client timed out (`Timed-out after 30.00s, waiting for EntitlementDetailsResponse`) while the Mac was swapping heavily; Package Manager then loaded no packages and compilation failed. A plain re-run passed.

## 11. Tests

**EditMode (130 green at the M2a checkpoint; `pnpm m1:test:unity`):**
- **M1:** protocol hello round-trip and mismatch; `NetTargets`; `RollingSamples`.
- **`MovementConfigTests`:**
  - the C# constants hash equals the generated sha256;
  - `movement_v1.json` on disk hashes to the same;
  - tick conversions.
- **`MotorTests`** (analytic world):
  - max speed reached in ≤ 5 ticks and never exceeded, incl. diagonal; deceleration;
  - jump apex 1.6 m ± 2 cm; no double jump;
  - coyote (ticks 1–3 allowed, 4 not); jump buffer (0–3 ticks before landing fire, 4 do not);
  - dive impulse and 30-tick cooldown; dive with camera yaw;
  - jump-cancel window (ticks 1–5 cancel, 6 not); 11-tick dive-landing stun;
  - slopes (15/30/44° climb, 50/60° block); steps (0.2 m climbs, 0.3 m blocks); wall slide;
  - **determinism:** a 1,800-tick random input log run twice gives bit-identical states; replay from any recorded state reproduces the original run;
  - quantisation round-trip (10k random); `Yaw256`.
- **`PhysxMotorTests`** (real BoxColliders + PhysX queries): flat run, apex, slopes, steps, jump onto a 1.5 m wall but not 1.7 m, determinism.
- **`NetMessageTests`:**
  - sizes (13/40/16/22/280 B);
  - `in` and `snap` round-trips with extreme values;
  - rejection of every truncated prefix, trailing bytes, bad count and bad slot;
  - 20k random-buffer fuzz with no exceptions.
- **`SanitizerTests`:** on-time apply + ack; stale/duplicate seq; far-future violation; late edges carried; repeat ×3 then neutral; flood caps; clamping; 50k random-command fuzz.
- **`TickSyncTests`:**
  - init, dilation, re-sync, burst cap;
  - stale start without a target ratchet; target lowering;
  - closed loop at 20/75/150 ms one-way latency with jitter.
- **`SimCoreTests`:** 12 bots for 60 s, deterministic across two instances; humans replace bots; stacked players separate at ≤ 2.5 m/s; fall → respawn after exactly 30 ticks; snapshot header carries the owner's timers and ack.

**Not EditMode-testable (verified by running):**
- the server loop with 12 occupants (Mac dev server + headless probe, `scripts/m2-measure.sh`);
- flood kick (`scripts/m2-probe.sh … flood`);
- the client view (`-fallrushProbeView` screenshot).

**PlayMode (later):**
- checkpoint order
- fall → respawn
- round flow with bots
- reconnect within 20 s
