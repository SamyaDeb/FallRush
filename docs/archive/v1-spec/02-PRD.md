# 02 — Product Requirements Document (PRD)

## 1. Objective

Ship a playable original multiplayer knockout game that demonstrates meaningful MagicBlock integration and a credible path to a production Solana game.

## 2. Personas

- **Casual competitor:** wants quick, fun matches without crypto onboarding.
- **Competitive player:** wants consistent rules, fair matchmaking, rankings, and reliable results.
- **Collector:** wants optional ownership of cosmetic items and achievements.
- **Operator/moderator:** needs match controls, abuse tools, telemetry, and incident recovery.

## 3. Functional requirements

### FR-01 Identity
- Support guest identity with server-issued opaque user ID.
- Optional wallet link using signed nonce; never request seed phrase/private key.
- Wallet ownership proof must be verified server-side; nonce expires and is single-use.

### FR-02 Lobby and matchmaking
- Create public/private lobby.
- Join/leave lobby; ready/unready.
- Server validates capacity and build compatibility.
- Start only after configured minimum players or explicit test-bot policy.
- Prevent duplicate joins and stale tickets.

### FR-03 Match initialization
- Freeze roster and ruleset version.
- Request VRF randomness once per match sequence.
- Store request ID, proof-verification status, seed hash/output reference, map pool version, selected map sequence.
- Never expose unverified random values as final.

### FR-04 Gameplay
- Clients send input commands, not authoritative positions or scores.
- Dedicated server simulates movement, hazards, checkpoints, collisions and elimination.
- Client renders interpolated snapshots and predicts local movement.
- Server validates movement bounds, input rate, map boundaries and checkpoint order.

### FR-05 Round lifecycle
- State machine: WAITING → LOCKED → RANDOMNESS_PENDING → READY → ROUND_ACTIVE → ROUND_RESOLVING → INTERMISSION → FINAL_ACTIVE → COMPLETED / ABORTED.
- Each transition has an authorized actor and preconditions.
- Qualification list is deterministic, deduplicated and immutable once finalized.
- A player cannot qualify twice or re-enter after elimination.

### FR-06 MagicBlock state
- Demonstrate delegation of match-scoped account(s) using the supported SDK flow.
- Submit only meaningful state transitions / checkpoints, not every render frame.
- Confirm delegated account owner/routing and ER transaction success.
- Commit final state and undelegate/finalize using the exact lifecycle supported by the selected SDK version.
- If ER unavailable, fail closed for prize/achievement finalization; allow an unranked practice fallback only.

### FR-07 Results and records
- Final result includes match ID, ruleset version, map sequence ID, roster hash, round results, champion, server transcript hash, timestamps, ER transaction references, and status.
- Result can be challenged only through a defined admin/dispute path in MVP; production requires independent evidence and clear appeal policy.
- UI distinguishes server-verified, ER-updated, and Solana-finalized states.

### FR-08 Collectibles
- MVP: one demo cosmetic collection and optional user-initiated mint/claim on devnet.
- Use Metaplex Core only after checking current SDK/program versions and metadata hosting.
- Cosmetic metadata contains stable asset ID, collection, name, image URI, attributes, creator/project provenance.
- Gameplay stats are not encoded as tradable advantages.

### FR-09 Observability
- Structured logs with correlation IDs: lobby_id, match_id, player_id, round_id, tx_signature.
- Metrics: queue time, match start success, tick duration, RTT, packet loss, server corrections, disconnects, ER transaction success/latency, VRF fulfillment latency, commit failures.
- Redact wallet signatures, access tokens, IP addresses, and personal data from routine logs.

## 4. Non-functional requirements

- MVP: 10–12 concurrent clients in a single match on a measured test environment.
- Server simulation target: 30 Hz initial; evaluate 20–30 Hz depending on CPU/network and game feel. Client render can run at display refresh rate.
- Snapshot replication: 10–20 Hz initial with interpolation; tune from measurement.
- Server process must not block simulation while waiting for Solana/ER transactions. Use asynchronous event queue and explicit state barriers between rounds.
- Reconnect window: 20 seconds MVP; reconnect to same player identity and authoritative state if session token valid.
- Match state transitions must be idempotent.
- No mainnet money movement in hackathon MVP.

## 5. Acceptance criteria

A build is hackathon-demo ready only if:
1. At least 8 clients can join one match in a repeatable test.
2. A complete match can progress through three rounds to a champion without manual database edits.
3. Server rejects invalid checkpoint order and teleport/speed exploit tests.
4. Every client sees the same map sequence and round outcome.
5. VRF proof is verified by the VRF program before its output affects map selection.
6. Match account is demonstrably delegated to an ER, updated there, and committed/finalized to Solana through the documented flow.
7. The submitted program and transactions are verifiable on devnet/explorer.
8. A failed chain operation does not falsely display a finalized reward.
9. Demo clearly discloses bots, server trust assumptions, and testnet status.

## 6. Prioritization

P0: character controller, two maps, dedicated server, match state machine, ER delegation/commit proof, VRF sequence, result screen.  
P1: third/fourth map, profile, leaderboard, cosmetic demo.  
P2: full marketplace, map editor, seasons, paid competitions, token design.

## 7. Risks and mitigations

- **ER integration consumes schedule:** create a minimal delegation spike on day one; keep a clean adapter boundary.
- **Physics desync:** authoritative server, fixed timestep, client interpolation, no client-authoritative scores.
- **VRF latency:** request before countdown; use a precommitted fallback policy for unranked mode only.
- **Mainnet safety:** devnet only during hackathon; audit and staged release before mainnet.
- **IP exposure:** original art and names; legal clearance before commercial release.
- **Regulatory exposure:** no paid-entry/cash-prize mode in India without legal advice.
