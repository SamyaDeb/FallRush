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
| `snap` | S→C, every 2nd tick (15 Hz; `-fallrushSnapHz 30` for every tick) | UnreliableSequenced | header (18 B): `serverTick u32, ackSeq u32, inputLead i8, ownSlot u8, own state 6×u8 (coyote, jumpBuffer, diveCooldown, landTicks, immunity, surface), ownEvents u8 (bit0 = pushed by another player since your last snapshot), count u8` + count × `PlayerState` (22 B): `slot u8, pos 3×i32 mm, vel 3×i16 cm/s, yaw u8, flags u8, stunTicks u8` | **282 B** for 12 |
| `evt` | S→C | ReliableSequenced | **M3:** `seq u32, type u8, tick u32, slot u8, arg i32` (checkpoint / finished / qualified / eliminated / respawned / round end / phase) | **14 B** |
| `round` | S→C, on connect + every new round | ReliableSequenced | **M3:** `RoundParams` (map, variant, modifier, round, hazard seed u64, round start tick, qualify, players, intro/countdown ticks, timer) + 32 B map content hash | **59 B** |
| `rstate` | S→C, on connect + after every phase change | ReliableSequenced | **M3:** last event seq, phase, phase start tick, RUSH end tick, qualify / qualified / finisher counts + finishers (slot, tick) | 16 + 5 per finisher |
| `hello` | C→S (approval payload) | Reliable | protocolVersion, buildVersion, cosmeticPreset | 4–36 B |

- **Protocol version 3** (M3; v2 in M2). An older client is rejected with the readable M1 message. **M3:** `PlayerState` gains a race byte (checkpoints reached in the low nibble, finished / qualified / eliminated bits): 23 B, **`snap` 294 B for 12**.
- **`moveX/moveY`** are the stick after the client rotated it by `camYaw`, i.e. world X/Z in ±127 with length ≤ 127. The server does no trigonometry on input. `camYaw` only sets the dive direction when there is no move input.
- **`buttons`:** bit0 jump held, bit1 jump pressed (edge), bit2 dive held, bit3 dive pressed (edge). The client accumulates edges between ticks, so a tap is never lost.
- **Changes vs the v2 text:**
  - `in` starts with a `count` byte instead of `lastSeq`, since the newest command already carries it.
  - The `snap` header adds `inputLead` (clock-sync feedback, §4.3) and the owner's 5 motor timers. Without the timers a client rewind could not restore the complete `MotorState`.
  - M2b adds `surface` (the moving platform the owner stands on, so a rewind restores platform carry) and `ownEvents` (server-only pushes, so the client can tell an expected correction from a prediction error). Still protocol v2: v2 was never deployed outside the dev machine.
- **Server snapshot pause (R12, M2b):** a client that had sent input but has sent nothing for 30 ticks (1 s; a hidden browser tab runs no frames) gets no snapshots until its input resumes. Otherwise it wakes up to hundreds of queued snapshots.
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

### 4.1.1 Rounds, hazard time and the map hash (M3, as built)

- **Hazard time is round-relative:** `t = serverTick − RoundParams.RoundStartTick` (`HazardSet.Time`). `Map_Sandbox` starts at tick 0, so its M2 behaviour is unchanged. On race maps the start is the **RUSH tick**, so hazards run at negative `t` during the intro and countdown. While the server waits for players, the start is `0x7FFF0000`, so the start gate stays closed.
- **`RoundParams`** (`Shared/RoundParams.cs`) are server-owned and sent in `round`. **Source in M3:** `-fallrushSeed` / `-fallrushVariant` / `-fallrushModifier` / `-fallrushMap` (env `GAMESERVER_SEED`, `_VARIANT`, `_MODIFIER`, `_MAP`). Unset variant/modifier are derived from the seed using the `map_pool_v1` tables (C# mirror `MapPoolV1`, checked against the JSON by a test). M6 swaps this for the VRF deal in `ServerSim.ChooseRoundParams`. Race rounds loop with seed + 1.
- **Map content hash** (`Shared/MapContent.cs`, own SHA-256 in `Shared/Sha256.cs`) covers both variants' resolved defs (phase excluded, phase ranges included), checkpoints + respawn spots, spawns, kill-Y and the course line. A client whose own hash differs disconnects with "Map data mismatch … Reload the page". Recorded per map in `Tests/Vectors/map_hashes_v1.txt` and checked by a test; stable across rebuilds. Current: Block Party Dash `6780ef6c…`, Sandbox `2ae7d388…`.
- **Allowlist:** the server validates the resolved defs (variant + modifier) when a round starts. Out of range → `[gs] ROUND REFUSED …` and nothing is simulated.
- **Countdown:** inputs for ticks before RUSH (intro/countdown) are neutral on both sides. A teleport during intro/countdown is adopted, not counted.
- **Finished racers** are out of the simulation and collisions. The client stops predicting itself and spectates.
- **Push prediction (R14, M3 measurement):** the client also applies the server's soft separation to itself, against remotes extrapolated from the newest snapshot (≤ 12 ticks). It is on by default (`ClientSim.DefaultPredictPushes`; `?pushpred=0` turns it off). Localhost crowd race: corrections + pushes 2.73 → 0.73 per 5 s (`docs/proof/M3-race.md`).

### 4.1.2 Protocol v4 (M4, as built)

`Protocol.Version = 4`; a v3 client gets the readable "Protocol mismatch … Reload the page" reason. Unchanged: `in` (40 B), `snap` (**294 B for 12**: players out of the round are left out of it; race-byte bit 7 is unused; the low nibble carries checkpoints / stars / the deepest shelf by mode), `round` (59 B), `rstate` (16 + 5 B per entry: race finishers, survival escapes, skill scores, final tiles), `evt` (14 B; new types Out / Scored / Dropped; one sequence per server process).

| Name | Direction | Delivery | Payload | Size |
|---|---|---|---|---|
| `hello` v4 | C→S approval | — | v3 fields + `u8 nameLen, name (≤ 16 B UTF-8), 16 B reconnect token` (optional; strict) | ≤ 69 B |
| `match` | S→C, per recipient, on join / reconnect / every phase or roster change | ReliableSequenced | phase, phase start / end ticks, round, load serial, host, own slot, champion, P, match id, seed, transcript hash, event count, deal (maps / variants / modifiers / targets), **your reconnect token**, match serial; per slot: flags (bot, alive, connected, grace expired, host, ready, rostered), tier, reason, eliminated round, places ×4, late mask, name | 140 + (10 + name) per slot; measured 310 B (lobby, 1 player), ≤ 452 B max |
| `mevt` | S→C | ReliableSequenced | one transcript event: seq u32, tag, round, slot, tick u32, arg (≤ 32 B) | 12 + arg: DEAL 44 B, QUALIFIED / ELIMINATED / COMPLETE_ROUND / ABORT 13 B, START_ROUND / COMPLETE_MATCH 12 B |
| `rx` | S→C | ReliableSequenced | `u8 kind (0 delta, 1 full), u8 epoch (load serial), u32 first seq, u16 n` + n × 14 B (type, id u16, slot, tick u32, x/y/z i16 cm) | 8 + 14 n; chunks of ≤ 80 entries (1,128 B) so every message stays under NGO's 1,296 B non-fragmented limit (a full Crumble Shelf state = 243 contacts = 4 chunks) |
| `ready` | C→S | ReliableSequenced | load serial + map-hash prefix | 5 B |
| `ctl` | C→S | ReliableSequenced | 1 host Start, 2 Back to lobby, 3 rx resend (+ u32) | 5 B |

**Join / reconnect:** the server sends `match` (with the token), every `mevt` of the match so far, `round`, `rstate`, `rx` full; a client never needs an earlier message to show the right screen.

### 4.1.3 Scene flow (M4-3, as built)

No NGO scene management. The director asks for a map (`ServerMatch.LoadMap`): the server loads it in single mode (the sim does `SkipTick` for those frames so the tick clock keeps running), rebuilds the world for the round params and sends `round` (map id + content hash; hazards are far in the past until the round begins). Clients with `ClientSim.FollowScenes` (menu Play, probes) load the named scene, check the hash, and send `ready` (load serial + hash prefix). The round begins when every connected alive human is ready or after 10 s. Late loaders spectate that round.

### 4.1.4 Reactive elements (M4 §3.4, as built: `Shared/Reactive.cs`)

- **State:** an append-only log of `RxEntry (seq, type, id, slot, tick, pos)` per round, changed only by the server's rules: `TileContact` (Crumble Shelf: the server's own ground contact on a tile), `BlockTrigger` (Goo Tower falling blocks), `StarCollect`, `StarDrop`. The state at any tick is a pure function of the log: a tile is solid while `tick < contact + 18`; a block is gone in `[trigger + 18, trigger + 138)`; a star is taken from its pickup tick.
- **Replication:** deltas after every server step, full state on join / reconnect / a new round; a delta whose first seq is not the next one → the client asks for the full state (`ctl` 3). Deltas and the full state are the same entries (EditMode equivalence test).
- **Prediction:** `PhysxMotorWorld.BeginTick(t)` (and the analytic test world) switches tile / block colliders from the same lookup on both sides; client replays across a drop are exact once the contact is known. A drop is 18 ticks (600 ms) after the contact, longer than RTT + lead, so the client normally knows it first. `ClientSim.RxLate` counts changes that arrived after their effective tick in the predicted timeline.
- **Stars** move with their anchors (pure); pickups are server-validated (no client claims exist: inputs only).

### 4.1.5 Reconnect (M4-6, as built)

The server issues a 16-byte token at join (`match`); WebGL keeps it in `sessionStorage` (`Plugins/WebGL/FallRushSession.jslib`), desktop in memory (`-fallrushTokenFile` for the probe). On an unexpected drop the client shows "Reconnecting… N s" and retries every 2 s for 20 s (`NetBootstrap.AutoReconnect`); `hello` carries the token; the server re-attaches the same slot (`SimCore.Attach`, fresh input buffer) and sends the full state. The Rushling stayed in the world on neutral input meanwhile. TODO(M5): the Relay's signed guest token replaces it.

### 4.2 Server tick (30 Hz, `ServerSim` → `SimCore`)

`Net/ServerSim.cs` is the NGO adapter. `Shared/SimCore.cs` is the engine-free simulation, so EditMode tests can drive it.

1. **Inputs (EarlyUpdate):** each `in` goes to that player's `InputSanitizer`, a fixed-size buffer keyed by tick, so a flood cannot grow memory.
   - **Freshness:** a command must have `seq` > the last accepted one. Older ones are the normal redundant copies and are dropped.
   - **Axes:** clamped to ±127, length ≤ 127; unknown button bits are masked.
   - **Rate:** at most **3 new commands per player per tick**. Extra on-time ones wait for their redundant copy in the next message. Over 32 messages per tick, the extra are dropped undecoded and count as violations.
   - **Window:** `tick` must be within `[next, next+30]`. Further ahead is a violation. A late command (tick already simulated) is dropped, but its jump/dive press edges are applied on the next tick.
   - **Kick:** 50 violations in a minute (doc 11). **Measured:** a 200-message junk burst was kicked within its first tick, and server RSS rose 0.3 MB.
   - **M2b, TCP-stall tolerance (proof B10):** more than 32 messages in one tick are dropped undecoded as `backlog`. Flood **violations** come from a token bucket (45 msg/s, burst 450), so a stall's burst is tolerated and a sustained flood is not. Commands 31–300 ticks ahead are dropped as `early` with no violation; beyond 300 they are violations. Server `MaxPacketQueueSize` is 1024.
2. **Step (PreUpdate tick):** for each occupied slot, take the command for exactly this tick, then run `KinematicMotor.Step(ref state, cmd, 1/30, PhysxMotorWorld)`.
   - **Missing command:** repeat the last one, with edges cleared, for up to 3 ticks; then neutral.
   - **Bots:** the same motor, fed by `TestBotInput`.
3. **Hazards (M2b, `Shared/HazardMath.cs`):** before the players of tick T are stepped, `world.BeginTick(T)` poses moving geometry (platform colliders) at `f(def, seed, T)`. The motor rides platforms through `IMotorWorld.SurfaceDelta`. After each motor step, `HazardSet.ApplySweepers` tests the capsule against each rotating bar analytically: knockback 9 m/s + 5 m/s up, stun 15 ticks (0.5 s), immunity 30 ticks. `Shared/PlayerStep.Run` (motor → sweepers → fall volume) is the one function both the server and client prediction call.
4. **Fall volume:** `y < killY` (−20 m) → `respawning` for 30 ticks (1.0 s) → spawn point. Then player–player **soft separation**: each overlapping pair is pushed apart at ≤ 2.5 m/s, with no stun. It is server-only, not predicted, and goes through the motor's collide-and-slide (`Displace`).
   - *(M3/M4)* Checkpoints, finish, goo, tiles and stars come later → `RoundRules`.
5. **Snapshot** every 2nd tick (15 Hz) to each human: one shared body plus a per-recipient header. 30 Hz is a flag; the M2b gate data decides.
6. *(M4/M6)* Domain events → `evt` + `RelayClient.Enqueue`.

**Slots:** humans take the lowest free slot on connect. `-fallrushBots N` (env `GAMESERVER_BOTS`) fills up to N occupants with labelled TEST bots, and a joining human replaces the highest-numbered bot.

**Logs every 10 s** (the M1 `ServerStats` pattern):
- `[gs] stats`: tick rate, frame interval, frame work p50/p95/max.
- `[gs] sim`: humans/bots, sim step p50/p95, snapshots and payload B/s per client, input messages/payload, accepted/late/repeated/neutral/burst, violations (far/flood/malformed), kicks, snapshots paused for idle clients, pushes, hazard hits.

### 4.3 Client tick clock and prediction (local player only)

**Tick agreement (M2a, `Shared/TickSync.cs`):** a command stamped `tick = T` is applied by the server **at step T**, so the client must run ahead of the server.
- **Server feedback:** on arrival the server records `lead = cmd.tick − nextServerTick` and puts the window minimum in each snapshot (`inputLead`).
- **Dilation:** the client runs its 30 Hz clock at `rate = 1 − 0.02·(lead − target)`, clamped to ±6 %, so `lead` settles at `target`. When `lead` is more than 8 ticks off it re-syncs in one jump: forward, or a pause when ahead. Ticks never go backwards. Reports in flight are ignored for 0.5 s after a jump.
- **Adaptive target:** starts at 2 ticks. A command late by k ticks → target += max(1, k) (max 6) and the clock jumps straight to the new target (M2b; with +1 steps, 150 ms + jitter + loss gave 10–13 start-up corrections per client, now 2–4). −1 (min 1) after 10 s in which the lead never dipped below the target.
- **Stalls vs lost time (M2b, proof B10):** the clock jumps forward only by ticks it actually lost (a capped `Advance`: a long frame or a hidden tab). A saturated −127 report jumps by all of them. Far-late reports without lost time are a transport stall's backlog: they are held, and re-sync only if they persist for 2 s.
- **Start:** `predTick = serverTick + ceil(RTT/dt) + target`. The clock keeps running before input starts (lobby, map load), so the first commands are on time.
- **Steady state:** `predTick ≈ serverTickNow + (RTT/2 + send/receive quantisation)/dt + target`. That is the "RTT/2 + one tick" of the spec, with the quantisation measured rather than guessed.
- **Measured (M2a, localhost):** lead holds at target 2 (67 ms), 0 late commands, 0 re-syncs over 60 s windows. The closed-loop EditMode test at 20/75/150 ms one-way latency with jitter converges to target 2 with 0 late commands after 10 s.

**Prediction (M2b, as built: `Net/ClientPrediction.cs`; history keyed by tick, 128 ticks):**

```
each client tick T: cmd = Input.Sample(); send cmd(tick=T); world.BeginTick(T); state = PlayerStep.Run(state, cmd, T); history[T] = (cmd, state)
on snap(S):          auth = snap[mySlot] + owner fields from the header
                     history[S] bit-identical to auth → nothing (the normal case: 99.7 % of snapshots in the localhost gate)
                     otherwise rewind: state = auth; replay T = S+1 … newest through PlayerStep.Run
                       (ticks with no command sent are replayed the way the server fills them: last command, edges cleared, ×3, then neutral)
                     count a correction if |Δpos| > 5 cm or flags/stun differ; smaller differences are fixed silently.
                     ownEvents.pushed → counted as a push correction (not predicted by design); respawns are teleports, not corrections.
visual smoothing:    the jump of the predicted present goes into RenderOffset, decaying exp(−3t/0.12 s) (5 % left after 120 ms); > 2 m snaps
render:              lerp(state[T−1], state[T], tick fraction) + RenderOffset  (one tick behind the newest prediction, always smooth)
long stall (> 64 ticks without sampling): drop the prediction and adopt the next snapshot (R12)
```

**`LocalWorld(tick)`** is the same PhysX scene with the platform colliders posed for the predicted tick (`PhysxMotorWorld.BeginTick` → `Physics.SyncTransforms`). The sweeper is analytic (`HazardMath.SweeperContact`). Hazard **visuals** are drawn on the local predicted timeline (`ClientSim.HazardRenderTick`), so what you collide with is where it is drawn. Remote players near a hazard are drawn ≈ RTT/2 + 100 ms in the past relative to it (standard trade-off, visible only at high RTT).

**Not predicted:** pushes from other players (server-only soft separation). **Predicted:** sweeper knockback + stun, platform carry, falling out (the respawn teleport arrives from the server).

**Measured (M2b gate, `docs/proof/M2-netcode.md` Part B):**
- **Bit-identical across 1,800-tick server twins** (EditMode, analytic world) at lead 1/3/6 and downlink delay 2/5 ticks, including sweeper hits and platform rides.
- **Same binary (Mac Mono client and server):** 0 corrections in 3 × 120 s.
- **WebGL (wasm) vs Mac (Mono) server:** mean position error on mismatching snapshots 3.7 mm (max 200 mm, once at start-up); most fixed silently; about 1 counted correction per 10 s with ~0 cm visible shift (flag-only).

### 4.4 Remote players

Snapshot interpolation at `renderTime = serverTimeEstimate − interpDelay` (`Net/Interpolation.cs`, M2b).
- **Server time estimate:** the smoothed (server tick time − local arrival time) of the freshest snapshots. Earlier-than-expected arrivals pull it forward fast (0.2), late ones drift it back slowly (0.01).
- **Adaptive delay:** `clamp(snapInterval + 1 tick + lateness_p95(last 64 snapshots), 100 ms, 150 ms)`, slewing at ≤ 5 % of real time. Measured: 105–125 ms on localhost, 141–150 ms at 150 ms RTT + jitter + loss.
- **Starvation:** extrapolate horizontally with the snapshot velocity for ≤ 150 ms; then freeze and show the "! LAGGING" icon.

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

**Moving platforms (M2b, as built):**
- `IMotorWorld.BeginTick(tick)` poses moving geometry.
- The ground probe records `MotorHit.SurfaceId` → `MotorState.Surface` (0 = static).
- On the next step, if the player was grounded on a surface, the motor first collide-and-slides by `IMotorWorld.SurfaceDelta` (the platform's mm-quantised displacement from tick − 1 to tick), then runs as usual.
- No Unity physics callback is involved, and the platform is a kinematic collider that is never simulated.
- Carried riders keep their offset to within 3 mm per tick (EditMode test).
- ~~Jumping off does not inherit the platform's velocity~~ **M3:** jumping or diving off any moving surface keeps its horizontal velocity (carry / dt); walking off does not.
- **M3 surfaces:** `IMotorWorld.SurfaceDelta(surfaceId, feet, out delta)` is point-aware. One id space covers platforms, turn tables, conveyors and spring pads. Turn tables rotate the rider about their axis (2 mm table-frame grid, exact return after a turn); conveyors add an exact mm belt delta per tick.
- **M3 air control:** above the run speed, air control only steers; it no longer brakes a spring launch back to 7 m/s when you hold forward (found by the map QA).

**State struct:** `MotorState` holds `pos i32×3 mm, vel i16×3 cm/s, yaw u8, flags u8, stunTicks u8, coyote, jumpBuffer, diveCooldown, landTicks, immunity, surface` (all u8). It is blittable and compared bit-for-bit in the determinism tests. `lastCheckpoint` lives in round rules (M3), not in the motor.

**Chaos modifiers (M5):** applied as multipliers on a `MovementConfig` instance.

### 5.x Movement v2 and new motor states (G1, as built)
- The game uses **`movement_v2.json`** (sha256 in `MovementConfigData.g.cs`, regenerated by `pnpm gen:movement`; hash test reads the file on disk; `movement_v1.json` unchanged, its hash is asserted too). Values: GDD §7. `MovementTuning` holds the tunable integers (dev practice mode only; `MovementConfig.FromTuning`).
- **Belly slide:** a dive landing sets `DiveLanding` + `LandTicks = 14`; while it runs: no input, horizontal decel `slideDecel` 12 m/s² (×0.4 on ButterFloors), jump-cancel in the first 5 ticks as before; sliding off an edge ends it (air control returns).
- **Stumble:** a landing faster than `√(2 · g · fallMult · 3 m)` (a fall of more than `stumble.minFallHeight`, under the modifier's gravity) sets the new flag **`MotorState.Stumbling` (bit 4)** + `LandTicks = 9`; input stays live, the ground speed target is capped at 55 % (incl. the last tick); a jump or a dive ends it; a dive landing takes priority. Knocks and spring launches clear it.
- **Tumble:** visual only, during the existing stun (immunity rules unchanged).
- Wire: unchanged layouts (`snap` 294 B for 12; the owner's `LandTicks` already travels as `OwnLand`). Protocol **v5** (the new flag bit, `ctl` Emote, `emo`).
- Tests: `MotorTests` (v2 values, slide distance/decel, slide off an edge, stumble threshold 1.5/2.8 m no, 3.4/8 m yes, cap, jump ends it, dive priority), `MovementConfigTests` (v2 is the game config, v1 hash kept, modifiers scale the v2 base, tuning round trip), `M4RulesTests.G1_Prediction_BitForBit_*` (prediction vs server twin through 10–12 belly slides, stumbles and respawning tiles: 0 corrections, 0 hazard mismatches).

## 6. Rushling visuals (client only)

**Bolt v1 as built (M2b):** `Client/RushlingVisuals.cs` + `Client/ProcMesh.cs`. Everything is generated in code at start-up, with no imported meshes or textures. Screenshots: `docs/proof/m2/m2b-bolt-closeup.png`, `m2b-bolt-silhouette-20m.png`.
- **Body inside the 0.31 × 1.0 collision capsule:**
  - white lathe dome on a back hinge, with the seam ring and the hinge block;
  - coloured lower shell (per-slot preset, 8 colours; slot 0 = Bolt magenta) with an extruded yellow lightning-bolt emblem (our own 8-point shape);
  - peg feet in the bottom 8 cm.
- **Screen face:** a rounded-square patch following the dome. It uses a 128×128 code-drawn 4×4 atlas with mint LED glyphs: idle `^ ^`, running `• •`, jumping `O O`, diving `> <`, stunned spirals, falling `O ◡ O`, and blink. The glyph is picked by UV offset (`_BaseMap_ST` through the face's only `MaterialPropertyBlock`).
- **Goggles:** strap ring plus two lens discs with rims, pushed up on the forehead.
- **Wind-up key:** purple shaft plus two loops on the back. It spins at 60 + 110 °/s per m/s, ×3 while diving.
- **Noodle arms:**
  - 6-point verlet chains anchored at the shoulders, with damping, gravity, a pose spring and 3 distance-constraint iterations;
  - the pose hangs out and down, flails when airborne, and reaches ahead on a dive;
  - rendered as a Catmull-Rom tube of 8 segments × 6 sides (radius 4.5 cm, parallel-transport frames), with mitten spheres at the tips;
  - visual only: no collision, clamped above the floor.
- **Motion:**
  - squash/stretch spring: landing (1.15, 0.8) scaled by impact, jump (0.9, 1.12); about 0.18 s, slightly underdamped;
  - lean toward smoothed acceleration, ≤ 18°, critically damped;
  - wobble spring kicked by landings and hazard hits;
  - stun pop: the lid hinges open 25° and 3 stars orbit for 0.6 s;
  - dive pitch 80° (60 ° of it held during the dive landing);
  - two-point foot stepping (stride 0.2 m, 0.09 s arc).
- **Rendering:**
  - URP Lit + URP Unlit (`Materials/Sandbox/CharacterUnlit.mat`), one shared material per colour, so the SRP Batcher stays on;
  - no shadows or probes;
  - about 1.6k triangles per Rushling (12 lathe/torus/extrude parts plus 2 × 96-triangle arms).
- **Stripped from the server:** `FallRush.Client` has `defineConstraints: !UNITY_SERVER`, and the Linux server build's `Managed/` contains no `FallRush.Client.dll` (checked).
- **No allocations in the animation loop:** springs on fields, arm vertex/normal arrays reused (`mesh.vertices = sameArray`), no LINQ, closures or strings per frame. This is UNVERIFIED with the profiler.
- **Not yet (M3+):** toon ramp shader, blob shadow, `CosmeticPreset` from the server (per-slot colour for now), hit-stop and BONK.

**Camera (`Client/FollowCamera.cs`, Cinemachine 3.1.7):**
- `CinemachineCamera` + `CinemachineOrbitalFollow`: Sphere orbit, radius 7.6 m at 23.2° elevation (≈ 7 m back / 3 m up), WorldSpace binding, so the horizontal axis is the world yaw the move input is rotated by.
- `CinemachineRotationComposer`: look-ahead 0.2 s, smoothing 8, ignore Y.
- `CinemachineDeoccluder`: Gameplay layer, pull forward, radius 0.3, damping 0.5 / 0.1.
- `CinemachineCameraOffset`: 0.25 s shake on stun.
- +6° FOV kick on dive.
- Mouse drives the axes directly while the pointer is locked.
- Options panel (Esc): sensitivity, invert Y, reduced motion (no shake, no FOV kick), stored in PlayerPrefs.

### 6.x Animation as built (G1)
See doc 17 §2.3.1 for the full table. `RushlingVisuals` API added: `SetPose(Pose)` (None / Qualified / Eliminated / Champion), `PlayEmote(1–4)`, `Bump(dir, strength)`. Glyphs Happy (7) and Sad (8) added to the face atlas.

## 7. Screens & HUD (doc 17 §5)

**As built (M4, greybox, `Client/MatchHud.cs` + `ReactiveViews.cs` + `ChampionPodium.cs`):** see doc 01 §12 "As built" for the flow. The match HUD is the default view; F3 still toggles the netcode HUD (`?hud=1` shows it at start). Spectating: eliminated / out / finished players and viewers follow alive players, Tab cycles. Crumble tiles are drawn with `Graphics.RenderMeshInstanced` (7 batches: 3 shelves × 2 colours + wobbling). Screenshots: `docs/proof/m4/`.


- **Screen flow:** Boot → Menu → Lobby → Deal → RoundIntro → HUD → RoundResult → Intermission (+ Fan Picks prompt) → … → Champion → Receipt → Rewards.
- **Chain chip:** driven by `evt.chainStatus` from the server, which the Relay forwards. The client never reads the chain directly; the web page does.

### 7.x G1 screens / UX (as built)
- **Nameplates** (`Client/Nameplates.cs`): screen-space, constant size, full to 10 m and faded out by 32 m, hidden when the head is behind level geometry (raycast, a third of the players per frame), a farther label overlapping a nearer one is dimmed, small BOT tag, own marker magenta "▼ YOU"; own canvas sorted under every panel; Settings → Show nameplates.
- **Settings** (`Client/SettingsPanel.cs`, menu + Esc in game): mouse sensitivity, invert Y, FOV offset (−10…+15°), camera distance (×0.8–1.4), reduced motion, master / music / SFX volume (music / SFX wired for G3), nameplates. Saved locally (`GameSettings`, cached).
- **Menu:** name entry (saved; sent in hello unless `?name=` / `-fallrushName`), Settings, a controls line, "Practice / tuning (dev)" in dev builds.
- **HUD:** friendly names (`UiNames`: "Block Party Dash", "No modifier", "Moon Bounce"…), the round intro shows the goal line + a mode tip + the modifier's effect, the champion name moved to the top (it covered the podium), HUD text outlined, the counter smaller; the controls bar shows 8 s after each map loads, then F1; the lobby panel carries a controls card.
- **Camera** (`FollowCamera`): 6.6 m / 22.5° (was 7.6 m / 23.2°) × the distance setting, `CinemachineDecollider` (radius 0.35) before the Deoccluder, FOV setting; spectate switches glide over 0.45 s.
- **Connect:** an initial connect that fails retries up to 6 times after 1, 2, 4, 4, 4 s ("Connecting… (attempt n of 6)"), the lobby button reads **Retry**.
- **Emotes:** keys 1–4 outside round play → `ctl` Emote → server relays `emo` (2 B) to everyone (1 per second per player; not during RoundIntro / RoundActive).
- **DEV practice / tuning mode** (`Client/PracticeMode.cs`): `-fallrushPractice <MapKey>` / `?practice=<MapKey>` / the menu button, Editor and development builds only (`NetBootstrap.PracticeMapArg` returns null in release). Runs `PlayerStep.Run` + the map's hazards locally; F6 panel edits the v2 values live (readouts: speed, last arc apex / airtime / distance, stumble / slide), F7 prints them as JSON (log + clipboard). Reactive elements stay idle.

### 7.y G2 / G3 screens and the MagicBlock-ready UI (as built 2026-11-19)
- **UI kit** (`Client/UiKit.cs`, doc 17 §8.4): TextMeshPro everywhere (static SDF atlases: `FR_Display` Lilita One, `FR_Body` Varela Round with tabular figures, `FR_Symbols` Noto Sans Symbols 2 fallback), chunky panels, squash buttons (`UiPress`), pills, our own sliders / switches, the capsule-pop transition (`ScreenFx`).
- **Screens:** menu (title, name, "My Rushling" picker + live preview `RushlingPreview`, Play, Settings, wallet smoke test, dev practice), connecting / reconnecting (`LobbyScreen`), lobby roster (preset swatch, BOT, ♛ host), Deal (Chaos Capsule machine `CapsuleMachineWidget`, round cards revealed one by one, the VRF slot), round intro (title, goal, tip, modifier hint, the countdown capsule machine 3-2-1-RUSH!), HUD (corner pills, timer badge with tabular figures, the counter pill), result banner (`ResultBanner`, our two-tone capsule), round result list, intermission leaderboard (fixed columns), champion (podium + Crown Capsule, `ChampionPodium`), receipt (table, ids, chain section with three explorer slots), spectate bar, settings. Nameplates use the body face with a white outline; "▼ YOU" now renders in WebGL.
- **Signature moments:** lobby capsules (`ReactiveViews`), the claw (`ClawPresenter`), crown ceremony (`ChampionPodium`), countdown and deal machines (`CapsuleMachineWidget`).
- **Audio / VFX:** `GameAudio` + `AudioDirector` (pooled sources, Music / SFX / UI buses, ducking, WebGL unlock on the first gesture), `Fx` + `FxDirector` (pooled particle systems), the juice hooks in `RushlingVisuals` (`LandedThisFrame` + `LandImpact`, `StepThisFrame`, `JumpedThisFrame`, `DiveStartedThisFrame`, `StunStartedThisFrame`, `StumbleStartedThisFrame`, `GetUpThisFrame`, `EmoteStartedThisFrame`, `Sliding`) consumed by `SandboxView.Juice`. Hit-stop: the stunned Rushling's pose holds 60 ms (`Drive` skipped); the simulation, tick, input sampling and prediction never see it.

#### `IChainAdapter` contract (G3.6; M6 implements it)
`Client/ChainAdapter.cs`. The game screens read chain state **only** through this interface; `PracticeChainAdapter` is used today. M6 adds a `RankedChainAdapter` and selects it when the match is ranked; no screen code changes.

| Member | Practice (today) | M6 (ranked) |
|---|---|---|
| `ChainState State` | `Server` (● grey). DEV: F9 (Editor / dev builds) or `?chain=cycle` / `-fallrushChain cycle` cycles the four states for screenshots | From the server's `evt.chainStatus` (Relay-forwarded): `Pending` (! amber) while an ER tx is in flight, `RollupConfirmed` (◆ mint) once the round's events are on the ER, `SolanaConfirmed` (⬢ yellow) after the per-round commit / final undelegate |
| `string Detail` | "Practice match: not recorded on chain" | e.g. "Round 2 committed to Solana" |
| `bool IsPractice` | true | false |
| `DealProof Deal` | `SeedHex` = the local seed, `Headline` "Practice deal (local seed)", `Verified` false, `VrfSignature` / `ExplorerUrl` null | the VRF seed from the callback, `Verified` true, `Headline` "MagicBlock VRF", the callback tx signature + explorer link (Deal screen shows "VRF ✓ …") |
| `ReceiptLinks Receipt` | all null (placeholders "— (practice: not recorded)") | `RollupTx`/`RollupUrl` (last ER tx), `SolanaTx`/`SolanaUrl` (commit / undelegate), `MatchAccount`/`AccountUrl` (the Match PDA) |
| `void OnMatchState(MatchStateMessage)` | keeps the seed | the same, plus whatever M6 needs from `match` |

Rules: no RPC, no wallet calls and no chain SDK in the adapter or the screens (the client never reads the chain directly; the web receipt page does). Every slot is marked `TODO(M6)` in `PracticeChainAdapter.cs` and `MatchHud.cs` (`ShowDeal`, `ShowReceipt`). Tests: `JuiceTests.ChainChip_FourStates_MapToDoc17Styles`, `PracticeAdapter_ServerState_LocalSeed_NoLinks`.

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

**M3 waypoint bot (`Shared/WaypointBot.cs`, the base of M4's BotBrain):**
- Follows authored routes (`MapInfo.botRoutes`, 2–5 per section, server-only, not hashed). Steering produces `InputCmd`s through the same sanitiser and motor as humans. Bots are labelled BOT.
- Actions: `Run`, `Jump` (gap edge; counts once passed), `Land` (block centre, reached only when grounded), `WaitClear`, `Door` (waits until the door stays open long enough), `JumpToPlatform` / `Platform` (moving blocks, read from the pose function), `Dive`.
- Reactive layer: jumps a low/mid spinner bar or dives under a high one, predicted 3–6 ticks ahead from the pose functions.
- Recovery: re-acquires the route after a knockback, respawn or 6 s without progress.
- Reference speed: no noise. The probe and WebGL `?bot=race` drive the same bot from the predicted state.

**M4 BotBrain (`Shared/BotBrain.cs`, as built):** tiers per bot from the match seed (30 % easy, 50 % normal, 20 % good; reference = no noise, QA only): steering reaction delay (8 / 5 / 2 ticks), a slow steering wobble (22° / 12° / 5°), late jump presses (≤ 3 / 2 / 1 ticks), stick 82 / 90 / 97 %, hesitation after waits at doors, bars and lifts (35 / 18 / 6 %). Noise is off during precise moves (gap jumps, landings, platforms, airborne). Race: the M3 routes; Survival: the spiral's routes by the height of the last ground contact; Skill: the best reachable star (value / distance, others closer count against it; good bots go for island stars via the spring pads), bumper detours, stay off the rim; Final: frugal tile stepping (stay on a tile until ~0.3 s before it drops, then a fresh neighbouring tile away from others), drop to the next shelf when the shelf is nearly gone, jump / dive the spinner bars. Bots see only what a player sees (own state, pure poses, replicated reactive state, the others from snapshots); the client probe drives the same brain (`MatchProbeBot`, `?bot=match`).

## 10. Builds

- **M4:** map builders `LobbyBuilder`, `GooTowerBuilder`, `StarScrambleBuilder`, `CrumbleShelfBuilder` (`bash scripts/m1-unity.sh <label> -executeMethod <Builder>.Run`); every map scene is in both builds (`Build.MapScenes`); scenes not built yet are skipped with a warning.
- **Server:**

  ```
  Unity -batchmode -nographics -quit -projectPath client-unity -buildTarget Linux64 -standaloneBuildSubtarget Server -executeMethod Build.Server
  ```

  Mono backend, then a Docker image `fallrush-gameserver` (ubuntu base). `-standaloneBuildSubtarget Server` is verified in the 6000.3 command-line docs.
- **WebGL:** `Build.WebGL` (Brotli compression, "Explicitly Thrown Exceptions Only" in release and dev — **M4: release was None until a release-only halt at the Crumble Shelf load**, docs/proof/M4-match.md §H — code stripping Medium, memory settings default). M4 release: 16.45 MB, 384–799 s; 59 FPS in every map in Chrome (Playwright window).
  - Build on the M2 with the Editor closed (memory).
  - Host the build behind the Next.js site at `/play`.
- **M2 commands:**
  - `pnpm gen:movement`: regenerate `MovementConfigData.g.cs` after editing `movement_v1.json`.
  - `bash scripts/m1-unity.sh sandbox -executeMethod SandboxBuilder.Run`: rebuild `Map_Sandbox`.
  - `scripts/m1-macserver.sh start -fallrushBots 12 [-fallrushSnapHz 30] [-fallrushServerFps 30]`.
  - `scripts/m2-probe.sh <ws-url> <secs> run|idle|flood`: headless scripted client.
  - `scripts/m2-measure.sh <snapHz> <serverFps> <secs> <label>`: 12-occupant measurement.
  - Both server builds now include `Map_Sandbox`.
  - **M2b:** `scripts/m2-probe.sh <url> <secs> course <label> [-fallrushProbeHide 20:60]` (course runner; hidden-tab simulation); `scripts/m2-netem.sh start 75 15 2` (tc-netem TCP relay on `ws://127.0.0.1:7778`); `python3 scripts/m2-summarize.py <labels…>`; WebGL `?autoplay=1&bot=course` (logs `[gate]` lines to the console); photo mode `-fallrushPhoto silhouette|closeup` with `-fallrushProbeView`.
  - **M2b WebGL release (measured):** 1,196.5 s build / 1,226.9 s wall (M1: 641 s with the SDK; this run swapped, 3.5 GB of swap in use), **16.03 MB** (`wasm.br` 10.10 MB, `data.br` 5.80 MB, `framework.js.br` 83 KB, `loader.js` 27 KB) vs 15.83 MB in M1 (+0.20 MB for prediction, hazards, Cinemachine and Bolt).
  - **Linux server (M2b):** 16.2 s build, 95 MB output, image `fallrush-gameserver:m2` (`docker build --platform linux/amd64 -f infra/gameserver.Dockerfile …`).
- **Real commands (M1):** `pnpm m1:build:webgl` (dev), `pnpm m1:build:webgl:release`, `pnpm m1:build:server`, `pnpm m1:test:unity`, plus `pnpm m1:build:macserver` (Apple Silicon local test server). All go through `scripts/m1-unity.sh`: refuses to run while a Unity Editor is open, logs to `.m1-logs/`, wraps Unity in `/usr/bin/time -l`.
- **Measured on the M2 Air (details: `docs/proof/M1-transport.md`):** WebGL release 523 s first / 294 s incremental; dev 714 s first / 238 s warm; Linux server 158 s first / 47–62 s incremental; peak RSS 1.1–1.5 GB; swap is used while other apps are open.
- **Brotli serving decision:** Decompression Fallback **off**; nginx adds `Content-Encoding: br` + the right `Content-Type` per file (verified). Unity 6000.3 **never compresses development builds** (manual: "Development builds aren't compressed"), so dev builds serve raw `.wasm`/`.data`/`.js`. `chmod -R a+rX` the build output (Unity writes `.br` files as 0600).
- **Flakiness seen:** twice the Unity licensing client timed out (`Timed-out after 30.00s, waiting for EntitlementDetailsResponse`) while the Mac was swapping heavily; Package Manager then loaded no packages and compilation failed. A plain re-run passed.

**G2/G3 builds (2026-11-19):** WebGL release 3 = **19.03 MB** (`reportedSize`; G1 16.45–16.55 MB), incremental rebuild 98 s, full 384–411 s, peak RSS 1.6 GB. The Mac dev build now includes the Menu and Lobby scenes (screens proof; the server ignores them). Map scenes carry baked toy-block / merged meshes (1–5 MB YAML each).


## 11. Tests

**G1:** EditMode **275/275** (M4: 254). New: `G1Tests` (respawning tiles incl. rewind history, full ≡ deltas, emote codec + phase rule, snapshot size with the stumble flag, friendly names), motor v2 tests, the G1 prediction twin. Test asmdef now references `FallRush.Client` (for `UiNames` / `GameSettings` tests).


**EditMode (149 green at the end of M2b; 130 at the M2a checkpoint; `pnpm m1:test:unity`):**
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
  - sizes (13/40/18/22/282 B);
  - `in` and `snap` round-trips with extreme values;
  - rejection of every truncated prefix, trailing bytes, bad count and bad slot;
  - 20k random-buffer fuzz with no exceptions.
- **`SanitizerTests`:** on-time apply + ack; stale/duplicate seq; far-future violation; late edges carried; repeat ×3 then neutral; flood caps; clamping; 50k random-command fuzz.
- **`TickSyncTests`:**
  - init, dilation, re-sync, burst cap;
  - stale start without a target ratchet; target lowering;
  - closed loop at 20/75/150 ms one-way latency with jitter.
- **`HazardAndPredictionTests` (M2b):**
  - `Sin16` within 4e-6 of `sin` over all 65,536 phases; phase periodicity;
  - platform deltas sum exactly to the displacement in mm;
  - sweeper contact for a standing player but not one jumping over it; knockback/stun/immunity;
  - a rider carried by the platform for 180 ticks;
  - **prediction vs a server twin**, bit-identical through sweeper hits (lead 1/3/6 × delay 2/5) and while riding;
  - a dropped command is corrected and every later snapshot matches; smoothing decays in 120 ms; server push reported once per snapshot;
  - interpolation: steady 15 Hz → 100 ms with no extrapolation; a stall extrapolates, then freezes with lagging; jitter raises the delay, capped at 150 ms.
- **`SimCoreTests`:** 12 bots for 60 s, deterministic across two instances; humans replace bots; stacked players separate at ≤ 2.5 m/s; fall → respawn after exactly 30 ticks; snapshot header carries the owner's timers and ack.

**Not EditMode-testable (verified by running):**
- the server loop with 12 occupants (Mac dev server + headless probe, `scripts/m2-measure.sh`);
- flood kick (`scripts/m2-probe.sh … flood`);
- the client view (`-fallrushProbeView` screenshot);
- the gate (`scripts/m2-probe.sh … course`, `scripts/m2-netem.sh`, `scripts/m2-summarize.py`; WebGL `?bot=course`);
- R12 (`-fallrushProbeHide 20:60`).

**M3 (207 green at the end of M3):** `KitTests`, `KitPredictionTests` (twin bit-identical through every hazard type × modifiers), `MapSceneTests` (committed scene hashes + allowlist), `RaceRulesTests` (qualify, timer ranking, checkpoint skip/teleport, respawn after 30 ticks, countdown lock, start gate), `BlockPartyDashTests` (a full 12-bot race on the real scene, PhysX, run in EditMode instead of PlayMode, reason in the M3 proof). Cross-platform `hazard_vectors_v1`: the EditMode test, plus `-fallrushSelfTest` (Mac, Linux) and `?selftest=1` (WebGL). All four agree on digest `fc06826c…`.

**M4 (EditMode, see docs/proof/M4-match.md §B for the count):** `ParityTests` (deal 100 + rnd 40, round targets P 4…20, roster hash, three transcripts against TS vectors), `MatchDirectorTests` (full walk + timings, event order and transcript = TS reference for scripted matches, minimum-advance 0 / 1 / fewer than N, roster lock + bot fill + no late joiners, lobby / soak, determinism, reconnect within / beyond the grace, abort, late loader), `M4RulesTests` (goo pose + kill tick on server and prediction, survival / skill / final ranking and tiebreaks, star timeline + turn-table anchors, pickup validation + exactly one drop + re-grab, tile drop at +18, spinners from 60 s, last standing, 120 s timer, reactive delta / full / gap, prediction twins bit-identical through tile drops + goo and star pickups + bumper drops, hazard vectors v2), `MatchMessageTests` (v4 codecs, sizes, fuzz). Hazard vectors v2: 1,400 cases (v1's 1,000 unchanged + 400 M4), digest `d4b2a73a…`.

**PlayMode (later):**
- checkpoint order
- fall → respawn
- round flow with bots
- reconnect within 20 s

**G2/G3 tests:** 287 EditMode (G1 275 + `LookTests` 7: palette tokens, readability, presets on the wire, hello preset, director clamp, Rushling ≤ 2.5k tris; `JuiceTests` 5: chain-chip states, practice adapter, every audio cue has a clip, every UI glyph renders with fallback + tabular body digits, juice hooks).

