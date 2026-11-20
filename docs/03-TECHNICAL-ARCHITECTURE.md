# 03 — Technical Architecture

**Status:** v2 (2026-11-18). Replaces `fall-rush-docs/03-TECHNICAL-ARCHITECTURE.md`.

---

## 1. Principle

Separate the work into four layers:
- **rendering** (the browser, Unity WebGL);
- **simulation** (the authoritative Unity server);
- **orchestration and chain I/O** (Rush Relay);
- **competitive state** (a MagicBlock Ephemeral Rollup, committed to Solana).

The chain is never in the per-frame loop. The ER **is** the live system of record for match outcomes.

## 2. System diagram

```
 Browser                                                     MagicBlock (devnet-as)          Solana devnet
┌───────────────────────────┐   wss (NGO/UTP, 30Hz in/15Hz snap)  ┌───────────────────────┐
│ Unity WebGL client        │◄──────────────────────────────────►│ Game Server (Unity    │
│  prediction, interp, VFX  │                                    │ headless, Linux, C#)  │
│  Solana.Unity-SDK (opt.)  │──https /v1──┐                      │ sim, hazards, rules,  │
└───────────────────────────┘             │                      │ lobby, bots           │
┌───────────────────────────┐             ▼                      └──────┬────────────────┘
│ Next.js web               │   ┌───────────────────────┐   internal    │ events, deal poll
│  /play (hosts WebGL)      │──►│ Rush Relay (Node/TS)  │◄──────────────┘
│  /live/:match  ───────────┼─┐ │ identity, directory,  │
│  /receipt/:match          │ │ │ outbox → chain worker │──── ER txs ────►┌──────────────┐  commit / undelegate
│  Fan Picks (session keys) │─┼─┤ Crown Capsule mint    │                 │ Match PDA    │─────────────────────►┌───────────┐
└───────────────────────────┘ │ └──────────┬────────────┘──create/delegate──────────────────────────────────►│ fall_rush │
                              │            │ PostgreSQL                   │ VRF (ER queue)│                     │ receipts  │
                              └── onAccountChange / getAccountInfo ──────►└──────────────┘                     │ Core NFTs │
                                  (ER while live, Solana after)                                                └───────────┘
```

## 3. Source of truth

| Data | Authority | Copies |
|---|---|---|
| Position, velocity, collisions, stun | Game server | Client snapshots |
| Hazard poses | Deterministic function (`HazardMath`) of the VRF seed + tick | Computed by everyone |
| Map / variant / modifier deal | **VRF callback in `fall_rush`** (ER) | Server, clients, DB |
| Qualification / elimination decisions | Game server rules | **Recorded and validated by `fall_rush` on the ER**, then committed to Solana |
| Champion | `fall_rush` (only remaining qualified slot) | DB, UI |
| Fan Picks (wallet users) | `fall_rush` on the ER (session-key signed) | DB |
| Guest identity, XP, cosmetics presets | Relay DB | Client |
| Crown Capsule ownership | Metaplex Core on Solana | DB index |

## 4. Repository layout (monorepo, pnpm workspaces)

```text
fall-rush/
  client-unity/          Unity 6.3 project (client + server builds)
  programs/              Anchor workspace (programs/fall_rush, tests/)
  server/                Rush Relay (TS)
  web/                   Next.js (landing, /play, /live, /receipt, picks)
  packages/shared/       TS: protocol types, deal.ts, ruleset/map_pool JSON + hashes, IDL types
  infra/                 Dockerfiles, railway configs, local docker-compose (postgres)
  docs/                  this documentation (v2)
  fall-rush-docs/        original v1 spec (frozen, reference only)
```

## 5. Shared constants & hashing

- `packages/shared/config/ruleset_v1.json` and `map_pool_v1.json` are canonical JSON (sorted keys, no whitespace), hashed with sha256.
- The hashes go into `GameConfig`. Unity copies them via a generated `GeneratedConstants.cs` (CI script), and a test asserts that the hashes match.
- Deal vectors: `packages/shared/test-vectors/deal_vectors.json` is consumed by the Rust, TS and C# tests.

## 6. Performance targets (hypotheses; measured in M4/M9)

| Metric | Target |
|---|---|
| Server tick | 30 Hz, p95 < 8 ms with 12 players on 2 vCPU |
| Client | 60 FPS WebGL on M2 (Chrome) at 1440×900 |
| RTT budget | Playable to 150 ms |
| ER event latency (send → confirmed) | p95 < 1 s (measure; the "10 ms blocks" claim is not ours to promise) |
| VRF deal | p95 < 5 s, 25 s hard cap |
| Match length | 7–9 min |
