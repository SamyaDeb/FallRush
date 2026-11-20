# 08 — Map Design Specifications

## 1. Map authoring rules

Every map must have a stable ID/version, mode, player count range, spawn points, ordered checkpoints, bounds, respawn points, spectator cameras, hazard configuration, accessibility/readability review, and server-side collision configuration. Client visuals may be richer but must match collision geometry.

All maps and obstacle combinations must be original. Do not reproduce Fall Guys map layouts, art, names, UI, or distinctive obstacle arrangements.

## 2. Map A — SKY_DASH_01

**Mode:** Race  
**Target duration:** 75–110 seconds  
**Qualification:** first 60% (configurable)

Flow:
1. Wide start deck.
2. Three staggered floating platforms.
3. Rotating horizontal beam with visible sweep direction.
4. Two moving gates with safe timing window.
5. Broad final ramp and finish trigger.

Requirements: 3 ordered checkpoints; at least two viable routes; fall respawn at last checkpoint; no single-frame collision traps. Hazard cycle is deterministic from match seed and server clock.

## 3. Map B — HAMMER_HAVOC_01

**Mode:** Race / skill  
**Target duration:** 70–100 seconds

Flow:
1. Alternating lanes.
2. Swinging padded pendulums with telegraphed arc.
3. Rotating sweep platform.
4. Timed gate sequence.
5. Finish platform.

Requirements: hazard speed limits, clear wind-up animation, collision cooldown to avoid repeated stun locks, checkpoint before difficult section.

## 4. Map C — SLIME_ESCAPE_01

**Mode:** Survival / climb  
**Target duration:** 90–130 seconds

Flow:
1. Vertical climb from lower safe zone.
2. Rising hazard plane with fixed server velocity.
3. Moving lift platforms.
4. Falling blocks triggered by occupancy.
5. Upper safe platform / survival timer.

Requirements: hazard never overtakes a valid route due to random timing; platform cycles deterministic; server validates survival zone. MVP can simplify to fixed rising hazard plus three obstacle types.

## 5. Map D — LAST_ISLAND_01

**Mode:** Final survival  
**Target duration:** 60–120 seconds

Flow:
- Modular platform grid.
- Tiles disappear after server-validated contact and configured delay.
- Arena boundary and fall elimination.
- Last player alive wins; timer tiebreak by survival score.

Requirements: no collision gaps caused by client-only tile state; server owns tile state and elimination. Broadcast tile state changes with sequence number.

## 6. Obstacle data model

```json
{
  "obstacleId": "sweep_beam_a",
  "type": "ROTATING_BEAM",
  "version": 1,
  "transform": {"position": [0, 2, 0], "rotation": [0, 0, 0]},
  "parameters": {"angularSpeed": 0.8, "direction": 1, "radius": 4.5},
  "phaseOffset": 0,
  "collisionProfile": "padded_sweep",
  "telegraphMs": 900
}
```

Validate all parameters against server allowlists. Never accept arbitrary client-supplied obstacle transforms or hazard speeds.

## 7. VRF map sequence

Before match start, freeze `map_pool_version`, eligible maps, weights, mode constraints, and player count. Request VRF with a match-specific seed. Derive sequence deterministically from verified bytes. Apply constraints: no duplicate final map, valid mode progression, player-count compatibility, and no map disabled for maintenance. Publish sequence commitment and map IDs to all players. For MVP, choose among a small number of fixed, tested sequences; later derive weighted sequences.

## 8. Map QA checklist

- Every spawn is safe and non-overlapping.
- Checkpoints are ordered and cannot be skipped by legitimate movement.
- Every route is physically reachable.
- Fall volumes and respawns are consistent.
- Hazard timing is fair and deterministic.
- Map remains playable at tested latency.
- Spectator camera cannot reveal hidden information in future modes.
- Client and server collision geometry match.
- 100 automated runs complete without softlock.
