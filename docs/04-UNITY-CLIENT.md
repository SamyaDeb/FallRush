# 04 — Unity Client & Game Server (shared C# project)

**Status:** v2 (2026-11-18). Replaces `fall-rush-docs/04-UNITY-CLIENT.md`.

**One Unity project, two builds:**
- the **WebGL client** (desktop builds optional);
- the **Linux x86_64 Dedicated Server**.

Both share the movement, hazard and deal code, which is what makes client prediction possible.

---

## 1. Versions (VERIFIED 2026-11-18; pin in `Packages/manifest.json` + commit `packages-lock.json`)

| Package | Version | Note |
|---|---|---|
| Unity Editor | **6000.3.28f1 LTS** (macOS ARM64) | Modules: **Web Build Support**, **Linux Dedicated Server Build Support**, Linux Build Support (Mono) |
| com.unity.netcode.gameobjects | **2.14.0** | Not 3.x (that needs 6000.7 beta) |
| com.unity.transport | 2.6.0+ (NGO dependency; latest 2.7.5, pin after the M1 spike) | WebSockets for WebGL |
| com.unity.dedicated-server | **2.0.2** | Multiplayer Roles, content stripping |
| com.unity.multiplayer.playmode | **2.0.2** | Max 4 players; on 8 GB use **at most 1 virtual player** |
| com.unity.cinemachine | **3.1.7** | |
| com.unity.inputsystem | the version bundled with 6000.3 (pin at install) | |
| com.unity.render-pipelines.universal | the version bundled with 6000.3 | |
| com.solana.unity_sdk | git URL `https://github.com/magicblock-labs/Solana.Unity-SDK.git#v1.2.9` | Unity 6 support UNVERIFIED (issue #233 Unity 6 WebGL freeze is closed). M1 smoke test. |
| com.unity.toolchain.macos-arm64-linux-x86_64 | 2.0.5 | Only if building the server with IL2CPP. **The MVP server uses Mono**, so it is not needed. |

**Disk:** about 10.5 GB for the editor and modules. Budget 15 GB with Library.

## 2. Transport: WebSockets everywhere (decision D-4 revised to WebGL)

- **Why:** WebGL clients cannot use UDP. Every client in a match must share one transport.
- **The server** listens with UTP WebSockets enabled (`UnityTransport.UseWebSockets = true`).
- **TLS** terminates at the VPS's nginx (doc 13 §2). The browser connects with `wss://`, and the server sees plain `ws`.
  - **UNVERIFIED:** UTP WebSocket behind a TLS-terminating proxy. **This is the M1 spike's first test.**
  - Fallback: UTP's built-in secure WebSocket with certificates.
- **Consequence:** WebSocket is TCP, so everything is reliable and ordered. Snapshots go out at 15 Hz (tunable to 30 Hz). Loss shows up as jitter, so the interpolation buffer is 100 ms, adaptive up to 150 ms.

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

`#if UNITY_SERVER` and Multiplayer Roles strip the client-only code and components (rendering, audio, UI, VFX) from the server build.

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

- Optional "Connect Wallet" in the menu. WebGL uses the SDK's web wallet adapter (Phantom / Solflare); verify in M1.
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

  Mono backend, then a Docker image `fallrush-gameserver` (ubuntu base).
- **WebGL:** `Build.WebGL` (Brotli compression, no exceptions in release, code stripping Medium).
  - Build on the M2 with the Editor closed (memory).
  - Expect 10–20 min; UNVERIFIED, measure in M1.
  - Host the build behind the Next.js site at `/play`.

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
