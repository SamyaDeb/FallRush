# 05 — Backend: Rush Relay + Game Server Orchestration

**Status:** v2 (2026-11-18). Replaces `fall-rush-docs/05-MULTIPLAYER-BACKEND.md`.

**Shape:** one TypeScript service (**Rush Relay**), one PostgreSQL database, and N game-server processes.

**Dropped for the hackathon** (D-9): Redis, separate microservices, matchmaking tickets. The internal module boundaries are kept, so we can split services later.

---

## 1. Responsibilities

| Concern | Owner |
|---|---|
| Physics, hazards, checkpoints, qualification decisions, lobby room, roster lock | **Game server** (Unity headless, C#) |
| Guest identity, wallet linking, server directory and quick-play, event outbox, chain worker, receipts / profile API, Crown Capsule mint | **Rush Relay** (Node/TS) |
| Live on-chain view, receipts, Fan Picks with session keys | **Web** (Next.js), reading the ER/Solana directly plus the Relay API |

## 2. Rush Relay modules (`server/src/`)

```text
identity/     guest tokens (HMAC-SHA256 signed, 7-day expiry), wallet nonce + ed25519 verify (tweetnacl)
directory/    registered game servers {id, wsUrl, state, players}; POST /internal/servers/heartbeat
matches/      POST /internal/matches (server opens match: roster) ; POST /internal/matches/:id/events (batch)
outbox/       match_events table, ordered by (match_id, seq); idempotent inserts
chain/        ChainAdapter (Anchor TS + ER SDK), worker loop, commit tracking, proof export
mint/         Crown Capsule mint via Umi + mpl-core (after Solana-final receipt)
api/          public REST /v1 (doc 10)
```

- **Stack:** Node 24 LTS-line (local 24.21), TypeScript, **Fastify**, `pg` + plain SQL migrations, `zod` for validation, `pino` logs.
- **Versions** are pinned exactly in `package.json` at M5, and the lockfile is committed.

## 3. Game server ↔ Relay contract (internal; shared secret + HMAC header, private network only)

| Call | When | Body |
|---|---|---|
| `POST /internal/servers/heartbeat` | Every 5 s | `{serverId, wsUrl, state: OPEN\|IN_MATCH, humans, capacity}` |
| `POST /internal/matches` | Roster locked | `{matchId, ruleset, roster:[{slot, userId, wallet?, bot}], rosterHash}` → `202`. The Relay starts the chain flow. |
| `GET /internal/matches/:id/deal` | Server polls every 250 ms during the Deal screen (max 25 s) | `{status: PENDING\|READY\|UNRANKED, deal?, vrfSeed?, signatures}` |
| `POST /internal/matches/:id/events` | Each domain event (batched every 200 ms) | `[{seq, type, round, slot?, placement?, reason?, tick, serverTranscriptHash}]` |
| `GET /internal/matches/:id/chain-status` | Polled every 1 s | per-seq status → forwarded to clients as `evt.chainStatus` |

**The game server never waits on the chain except at one place: the Deal.** A match cannot start ranked play without the VRF deal (or the Unranked fallback after 25 s).

## 4. Canonical match state machine (shared: program, server, DB, UI)

| Server phase | On-chain `MatchStatus` | Client screen |
|---|---|---|
| LOBBY_OPEN / COUNTDOWN | — (no account yet) | Lobby |
| ROSTER_LOCKED | `Created` → delegated | Deal (spinning) |
| DEALING | `Dealing` | Deal (spinning) |
| DEALT | `Dealt` | Deal (reveal) |
| ROUND_INTRO(r) / ROUND_ACTIVE(r) | `RoundActive(r)` | RoundIntro / HUD |
| ROUND_RESULT(r) / INTERMISSION | `RoundResolved(r)` (+ commit) | Results / Intermission (Fan Picks open) |
| CHAMPION | `Completed` | Champion |
| FINALIZING → DONE | `Completed` + undelegated | Receipt |
| ABORTED | `Aborted` | Error / Receipt |

**Lobby rules:**
- min 4 humans **or** 1 human + bots in practice mode;
- countdown 20 s when ≥ 8 humans or the host presses start;
- the roster locks at countdown end; no late joiners.

**Disconnects:**
- 20 s grace; reconnect with the same guest token takes the same slot.
- After the grace period: eliminated with `reason=DISCONNECT`.

## 5. Chain worker

**Per match** it runs a single ordered consumer, so two workers never interleave on one match.

1. `create_match` (Solana) → `delegate_match` (Solana, validator = `ER_VALIDATOR`) → `request_deal` (ER) → wait for `Dealt` via `onAccountChange` → expose the deal.
2. **Consume events in `seq` order.** Map them to instructions (`start_round`, `record_qualified`, `record_eliminated`, `complete_round` [+commit], `complete_match`, `finalize_match`).
   - Record each signature **before** confirmation (`chain_txs.status=submitted`).
   - **Idempotency:**
     - Before sending, read `match.event_seq` from the ER.
     - If `event_seq > seq`, mark the event `confirmed` (already applied).
     - If `<`, wait for the earlier events.
3. **Retries:** transient RPC errors → backoff 0.5 / 1 / 2 / 4 / 8 s. Program errors other than `InvalidSequence` → dead-letter plus an alert log; the match is marked `chain_degraded`.
4. **After `finalize_match`:**
   - Poll Solana until the account owner == `fall_rush` and the status is `Completed`.
   - Then mark `final_signature` and, if the champion has a linked wallet and the match is ranked, enqueue the Crown Capsule mint.

**Signer:**
- `GAME_AUTHORITY_KEYPAIR` comes from the secret store (Railway variable, base58).
- It pays base-layer fees for create/delegate (devnet SOL).
- It is **never** the program upgrade authority and never holds more than 5 devnet SOL.

## 6. Crown Capsule mint (P1)

- **What is minted:** a Metaplex Core asset in a "Fall Rush Crowns (devnet)" collection, using `create(umi, {asset, name: "Crown Capsule #<n>", uri, owner: championWallet, plugins:[{type:'Attributes', attributeList:[{key:'matchPda',value}, {key:'matchId',value}, {key:'players',value}, {key:'season',value:'blitz-v10'}]}]})`.
- **Metadata JSON** goes to devnet Irys via `umi-uploader-irys`, or is served from the Relay (`/v1/meta/crown/:matchId.json`) if Irys is a problem.
- **Idempotency:** a unique `trophies(match_id)` row. The mint is never retried without first checking for an existing asset with that `matchPda` attribute.
- **Update authority:** a separate `MINT_AUTHORITY_KEYPAIR`, not the game authority.

## 7. Deployment (doc 13)

**Railway** (WebSocket and HTTPS via Railway domains; no UDP needed with WebGL) runs:
- `relay`
- `postgres`
- `web`
- `gameserver-1`, `gameserver-2`

A health endpoint on every service.

## 8. Observability

- `pino` JSON logs with `matchId`, `seq`, `slot`, `sig` (no tokens or IPs).
- `/internal/metrics` (simple JSON): queue depth, ER tx p50/p95 latency, VRF fulfilment time, commit time, failures.
- The game server logs a tick time p95 and a corrections counter every 10 s.
