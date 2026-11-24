# 05 — Multiplayer Backend Specification

## 1. Responsibilities

Backend handles identity, lobbies, matchmaking, server allocation, match orchestration, chain transactions, indexing, profiles, and telemetry. Dedicated game server owns live physics and gameplay adjudication.

## 2. Services

- **API Gateway:** REST endpoints, auth, rate limits.
- **Identity:** guest accounts, wallet challenge/nonce verification.
- **Lobby/Matchmaker:** queue, lobby membership, roster lock.
- **Match Orchestrator:** lifecycle, server allocation, map config, round transitions.
- **Game Server:** fixed-step authoritative simulation.
- **Chain Worker:** VRF, delegation, ER instructions, commit/finalize.
- **Indexer:** consumes confirmed transactions/events and updates read models.
- **Admin Ops:** abort, maintenance, bans, incident controls with audit logs.

## 3. Game server model

One match per server process/container for MVP. This isolates failures and simplifies state. Production may host multiple low-population matches per process after isolation and resource tests.

Simulation loop:
1. Read queued inputs.
2. Validate sequence/rate/range.
3. Apply movement and physics at fixed timestep.
4. Advance hazards using deterministic server clock.
5. Resolve checkpoint/fall/elimination events.
6. Emit snapshots.
7. Persist important events to append-only transcript/outbox.

Do not block simulation on RPC calls. Chain worker processes state events asynchronously.

## 4. Movement validation

Validate maximum speed, acceleration, jump impulse, air control, dive cooldown, grounded transitions, map bounds, checkpoint order, respawn rules, and impossible displacement. Use tolerances for latency and physics jitter. Flag suspicious events; do not automatically ban based on one noisy sample.

## 5. Lobby/match lifecycle

`OPEN → COUNTDOWN → LOCKED → INITIALIZING → READY → ACTIVE → RESOLVING → COMPLETED` or `ABORTED`.

Once roster locked, no late joiners. Reconnect uses same player ID and one-time session token. Player leaving during a round is marked disconnected; after grace period they are eliminated or converted to spectator according to published rules.

## 6. Server authority and chain authority

Server authority: continuous movement, collision, hazards, checkpoint truth in MVP.  
On-chain program authority: permitted state transitions, roster hash, sequence number, qualification status, final receipt, asset claim eligibility if enabled.  
Do not claim that on-chain state proves physics unless a separate proof/verification mechanism is implemented.

## 7. Event outbox

Each important server event is persisted before asynchronous chain submission:

```json
{
  "eventId": "uuid",
  "matchId": "base58-or-internal-id",
  "sequence": 17,
  "type": "PLAYER_QUALIFIED",
  "playerId": "opaque-id",
  "round": 2,
  "payloadHash": "sha256-hex",
  "occurredAt": "ISO-8601",
  "serverSignature": "optional-ed25519-signature"
}
```

Use a unique DB constraint on `(match_id, sequence)` and `(match_id, idempotency_key)`. Retry transient RPC errors with exponential backoff and jitter. Permanent program errors go to dead-letter queue and alert operator.

## 8. Wallet linking

Issue random nonce with short expiry. Client signs a domain-separated message containing origin, nonce, expiry, and requested action. Backend verifies signature against wallet public key, consumes nonce once, links wallet to user ID. Never ask for private key. Support unlink/relink policy and account takeover recovery.

## 9. Deployment

Dockerize API, worker, and dedicated server. Use environment config per cluster. Secrets manager for key material. Dedicated servers should not expose admin ports publicly. Health checks: liveness, readiness, simulation tick health, DB, Redis, RPC health. Graceful shutdown: stop new matches, complete/abort active match according to policy, flush outbox.

## 10. Operational metrics

Queue wait p50/p95, lobby fill rate, match start failures, simulation tick duration p50/p95/p99, CPU/memory, RTT, packet loss, snapshot age, corrections per minute, disconnects, chain queue depth, ER tx latency/success, VRF fulfillment time, commit failure count, RPC error rate.
