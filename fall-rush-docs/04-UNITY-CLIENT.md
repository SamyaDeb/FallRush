# 04 — Unity Client Specification

## 1. Unity setup

Use a current Unity LTS release verified against required multiplayer packages. Pin editor and package versions in `ProjectSettings/Packages` and `Packages/manifest.json`. Use URP for the target art style and modest hardware. Commit `Packages/packages-lock.json`.

Recommended packages (verify compatible versions before installation):
- Netcode for GameObjects (NGO) for MVP networking.
- Unity Transport (UTP) or selected transport compatible with server deployment.
- Input System.
- Cinemachine (optional).
- Addressables (later, for content delivery).

Do not assume Unity Relay is a dedicated authoritative server. Relay routes connections; competitive simulation must run on a server process.

## 2. Folder layout

```text
Assets/_Game/
  Art/{Characters,Environments,Obstacles,UI,VFX,Audio}
  Animations/
  Audio/
  Prefabs/{Player,Obstacles,Maps,UI}
  Scenes/{Bootstrap,MainMenu,Lobby,Map_SkyDash,Map_HammerHavoc,Map_SlimeEscape,Map_LastIsland}
  Scripts/
    Core/ Input/ Player/ Camera/ Animation/
    Networking/ Match/ Rounds/ Maps/ Obstacles/
    UI/ Audio/ Cosmetics/ Telemetry/
  ScriptableObjects/{Maps,Obstacles,Rulesets,Cosmetics}
  Tests/{EditMode,PlayMode}
```

## 3. Scene strategy

Bootstrap loads config and initializes services. MainMenu handles guest/wallet choice. Lobby loads player roster and countdown. Each map is a separate scene or additive scene loaded by stable `map_id`; map content is data-driven. Match controller persists across round scene changes. Do not let the client decide map order.

## 4. Player controller

Components:
- `PlayerInputReader`: emits normalized input frame.
- `PlayerMotor`: local prediction and visual movement; authoritative server implementation is mirrored in server code or shared deterministic movement library where feasible.
- `PlayerNetworkState`: replicated position/rotation/velocity/grounded/animation flags.
- `PlayerVisuals`: mesh, rig, emotes, cosmetics.
- `PlayerCamera`: follow camera, collision handling, spectator target.

Keep authoritative capsule dimensions and movement constants in a versioned `MovementConfig`. Server is final authority. The client must not send arbitrary transforms.

## 5. Input protocol

`InputCommand`: `sequence`, `move_x`, `move_y`, `jump_pressed`, `dive_pressed`, `interact_pressed`, `client_tick_hint`. Server validates finite values, range, sequence freshness, and input rate. Client timestamp is advisory only.

## 6. Network behavior

- Spawn local and remote player representations from server-approved roster.
- Send input frames; receive authoritative snapshots.
- Local client predicts, then reconciles when server tick differs.
- Remote players interpolate buffered snapshots.
- Handle packet loss, jitter, reconnect, server migration only when implemented.
- Round transition, elimination, match result, lobby membership use reliable messages.

## 7. Gameplay UI

HUD: countdown, round title, timer, qualified/remaining counter, checkpoint progress, latency indicator, connection state. Results: qualified/eliminated, placement, reason, next round. Final: champion, full placements, match ID, chain status (pending/ER recorded/Solana finalized).

Never show “on-chain verified” unless the corresponding proof/transaction has been checked. Show “pending” when transaction is submitted but not confirmed/finalized.

## 8. Map authoring

Each map prefab includes:
- Spawn points with stable IDs.
- Checkpoint triggers in ordered sequence.
- Finish trigger or survival bounds.
- Hazard controllers with server-authoritative parameters.
- Out-of-bounds / respawn volumes.
- Spectator cameras.
- Map metadata ScriptableObject with `map_id`, `version`, `mode`, `min_players`, `max_players`, `estimated_duration`, `checkpoint_count`, `allowed_variants`.

## 9. Performance targets

For MacBook Air M2 development: URP, baked lighting where possible, simple shaders, LODs, GPU instancing, limited transparent effects, object pooling, low-poly assets. Target stable 60 FPS locally at a reasonable resolution, but don't promise until measured. Dedicated server build should disable rendering/audio and run headless.

## 10. Tests

EditMode: movement config validation, map metadata validation, deterministic sequence mapping. PlayMode: controller, jump, checkpoint triggers, scene transitions, UI states, disconnect/reconnect, snapshot interpolation. Multiplayer: host/client simulation with at least 8 clients and packet impairment.
