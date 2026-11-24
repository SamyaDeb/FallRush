# 03 — Technical Architecture

## 1. Architecture principle

Separate rendering, simulation, orchestration, delegated chain state, and durable ownership. Do not treat Solana or an Ephemeral Rollup as a real-time 3D physics engine.

## 2. Logical components

```text
Unity Client
  ├─ Input / camera / animation / UI
  ├─ Local prediction + snapshot interpolation
  └─ HTTPS + realtime transport
       │
API / Identity / Matchmaking (TypeScript)
  ├─ Guest identity / wallet nonce verification
  ├─ Lobby and queue service
  ├─ Match orchestration
  ├─ Result/indexing service
  └─ Chain adapter (Solana + MagicBlock)
       │
Authoritative Game Server (per match)
  ├─ Fixed-step simulation
  ├─ Movement / physics / hazards
  ├─ Checkpoint and qualification validation
  ├─ Anti-cheat rules
  └─ Signed event transcript
       │
MagicBlock ER + Anchor Program
  ├─ Delegated match state
  ├─ Round transitions / qualification records
  └─ Commit / finalize / undelegate
       │
Solana Mainnet (post-audit) / Devnet (hackathon)
  ├─ Persistent match receipt / player profile
  ├─ Cosmetic ownership (Metaplex Core)
  └─ Optional future escrow (not MVP)
```

## 3. Source of truth

| Data | Authority | Replication |
|---|---|---|
| Position/velocity/collision | Dedicated game server | Client snapshots |
| Inputs | Client proposal, server validation | Server event log |
| Checkpoint reached | Game server validates | ER event / match account |
| Qualification/elimination | Server rules + program transition | ER and final Solana record |
| Random map seed | Verified MagicBlock VRF output | Match config |
| Cosmetic ownership | Solana asset account | Backend index/cache |
| Display name/avatar | Profile service | Client cache |
| Match history | Finalized chain receipt + indexed DB | Client UI |

The server is trusted for physics in MVP. ER transactions attest that authorized state transitions were recorded, not that every physical event was independently proven. Production can strengthen this with signed deterministic transcripts, independent observers, replay validation, or proof systems if economically justified.

## 4. Simulation and networking

- Fixed server timestep: start at 30 ticks/second; profile before increasing.
- Client render: variable frame rate.
- Snapshot replication: start 10–20 updates/second.
- Client sends input sequence number, buttons/axes, client timestamp (untrusted), and last acknowledged server tick.
- Server validates input rate, clamps axes, simulates movement, and broadcasts authoritative snapshots.
- Client predicts its own movement and reconciles against server snapshots.
- Remote players are interpolated; do not extrapolate indefinitely.
- Use reliable ordered messages for lobby and state transitions; use suitable unreliable/sequenced transport for frequent movement snapshots.

Unity package selection must be pinned after a prototype. Netcode for GameObjects is the pragmatic MVP choice for a small GameObject-based project; Netcode for Entities is an option if the team already knows DOTS and needs higher scale. Do not mix frameworks in the first week.

## 5. Chain integration boundary

Define `ChainAdapter` interface in TypeScript:
- `initializeMatch(matchConfig)`
- `requestMapRandomness(matchId, requestSeed)`
- `getVerifiedMapSequence(matchId)`
- `delegateMatch(matchAccount)`
- `recordRoundTransition(event)`
- `recordQualification(event)`
- `recordFinalResult(result)`
- `commitAndFinalize(matchAccount)`

Each call returns typed result with `status`, `signature`, `cluster`, `slot`, `errorCode`, and retryability. Adapter implementation must follow the exact MagicBlock SDK version and account lifecycle. No hardcoded guessed program IDs or queues.

## 6. Event-driven orchestration

Game server emits domain events to a durable queue/outbox. Chain worker consumes events in sequence and submits chain updates. Use idempotency key `(match_id, event_seq, event_type)`. Store pending/sent/confirmed/finalized/failed state. Retry only transient failures. Never duplicate reward settlement. If chain state is behind, do not let the next chain-dependent phase falsely claim finality; game may continue only if policy allows and the result remains pending.

## 7. Match state model

```text
MatchAccount {
  match_id: [u8; 32]
  authority: Pubkey
  ruleset_version: u16
  status: MatchStatus
  roster_hash: [u8; 32]
  map_sequence_hash: [u8; 32]
  current_round: u8
  player_count: u8
  qualified_bitmap / bounded player records
  champion: Option<Pubkey or player identifier>
  event_sequence: u32
  transcript_hash: [u8; 32]
  created_at / completed_at
}
```

Do not store arbitrary unbounded vectors in a fixed account. For larger rosters, use per-player/per-round PDAs or bounded arrays and account compression only after profiling. Account layout must be finalized before program implementation and versioned for migrations.

## 8. Security boundaries

- Client never signs server-owned match state.
- Backend never stores user seed phrases.
- Server signing key is isolated and rotated; if used for event authorization, its authority is restricted by program rules and operational controls.
- Treasury/upgrade authority uses multisig for production.
- RPC credentials stay server-side; use private RPC with fallback for production.
- All chain input is validated against allowlisted program IDs, mint IDs, cluster, PDA derivations, and expected owners.

## 9. Deployment topology

MVP: one API process, one matchmaking service, one dedicated game server per active match, PostgreSQL, Redis, chain worker, and private test RPC if available. Production: separate regions, autoscaling game server fleet, queue, read replicas, metrics/log aggregation, rate limiting, WAF, backups, failover RPC, secrets manager, and incident paging.
