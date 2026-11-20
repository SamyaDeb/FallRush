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

### 4.1 As built (M4, 2026-11-19): `Shared/MatchDirector.cs` + `Net/ServerMatch.cs`

One match per server process (M4-1). Ticks at 30 Hz; the director runs before every `SimCore.Step`.

| Phase | Duration (as built) | What happens |
|---|---|---|
| `LOBBY_OPEN` | until ≥ 1 human is connected (practice, M4-2) | `Map_Lobby` loaded; humans run around; bots leave; dropped humans' slots are freed at once |
| `COUNTDOWN` | 20 s, or the host's **Start** (the host = the lowest-slot connected human) | back to `LOBBY_OPEN` if every human leaves |
| `ROSTER_LOCKED` | 1 tick | bots fill every free slot up to 12 (slots 0…P−1, the program's layout); `P`, `RoundTargets(P)`, `match_id` (= first 16 B of `sha256("fallrush-match-id" ‖ seed)`), `roster_hash` (M4 proposal, doc 06 §9), transcript init, `Deal(seed)` |
| `DEALING` | 3 s | Deal screen spinning (M6: the VRF request is pending here) |
| `DEALT` | 3 s | **DEAL** event (seq 0, arg = the 32-byte seed); Deal screen reveal |
| `ROUND_INTRO(r)` | load ≤ 10 s, then 2 s intro + 3 s countdown | the server loads the round map (single scene; the sim skips those frames), sends `round`; clients load it and answer `ready`; the round begins when every connected alive human is ready or after 10 s (late loaders spectate the round; they advance if the round can still qualify ≥ 1 without them, else they rank last); **START_ROUND** at the begin |
| `ROUND_ACTIVE(r)` | until the round's rules end it | RUSH = hazard time 0 |
| `ROUND_RESULT(r)` | 6 s | **QUALIFIED** in placement order, **ELIMINATED** (reason), **COMPLETE_ROUND** (qualified count) — all in one tick |
| `INTERMISSION` | 8 s | leaderboard, next-round preview, Fan Picks hook (P1) |
| `CHAMPION` | 8 s | **COMPLETE_MATCH** (slot = champion) — after round 4, or as soon as only one player is left (minimum-advance, M4-8) |
| `FINALIZING` | 1 tick | no-op in M4 (TODO(M6): `finalize_match` = commit + undelegate, wait for Solana) |
| `DONE` | until every connected human pressed "Back to lobby", ≤ 20 s | receipt |
| `ABORTED` | 5 s, then `LOBBY_OPEN` | **ABORT** (reason 1 = no humans left after the grace; 2 = nobody left to advance) |

- **Disconnects (M4-6):** in a match the Rushling stays in the world on neutral input. A reconnect whose `hello` carries the **reconnect token** (16 random bytes the server issued in the `match` message; WebGL keeps it in `sessionStorage`, desktop in memory or `-fallrushTokenFile`) within 20 s takes the same slot back and gets a full resync (`match` + the event log + `round` + `rstate` + `rx`). After 20 s the slot is eliminated with `DISCONNECT` at the end of the current round (a late reconnect still gets the slot back, to spectate). In the lobby a drop frees the slot at once. **The token is the M4 stand-in for M5's guest token.**
- **No late joiners:** a connection during a match without a valid token watches as a viewer (snapshots + screens, no slot) and gets a slot in the next lobby.
- **Event log:** every transcript event is appended to `match-logs/match-<id>.jsonl` (git-ignored; header line with the seed, roster, roster hash, deal, targets; one line per event with the transcript hash after it; an end line). `scripts/m4-verify-log.py` recomputes everything from it independently. **This file is M5's outbox input.**

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
- `GAME_AUTHORITY_KEYPAIR` comes from the secret store (`/opt/fallrush/.env` on the VPS, chmod 600, base58).
- It pays base-layer fees for create/delegate (devnet SOL).
- It is **never** the program upgrade authority and never holds more than 5 devnet SOL.

## 6. Crown Capsule mint (P1)

- **What is minted:** a Metaplex Core asset in a "Fall Rush Crowns (devnet)" collection, using `create(umi, {asset, name: "Crown Capsule #<n>", uri, owner: championWallet, plugins:[{type:'Attributes', attributeList:[{key:'matchPda',value}, {key:'matchId',value}, {key:'players',value}, {key:'season',value:'blitz-v10'}]}]})`.
- **Metadata JSON** goes to devnet Irys via `umi-uploader-irys`, or is served from the Relay (`/v1/meta/crown/:matchId.json`) if Irys is a problem.
- **Idempotency:** a unique `trophies(match_id)` row. The mint is never retried without first checking for an existing asset with that `matchPda` attribute.
- **Update authority:** a separate `MINT_AUTHORITY_KEYPAIR`, not the game authority.

## 7. Deployment (doc 13)

**Hostinger VPS** (Docker Compose behind the host's nginx with Let's Encrypt; WebSocket and HTTPS only, no UDP needed with WebGL) runs:
- `relay`
- `postgres`
- `web`
- `gameserver-1`, `gameserver-2`

A health endpoint on every service.

## 8. Observability

- `pino` JSON logs with `matchId`, `seq`, `slot`, `sig` (no tokens or IPs).
- `/internal/metrics` (simple JSON): queue depth, ER tx p50/p95 latency, VRF fulfilment time, commit time, failures.
- The game server logs a tick time p95 and a corrections counter every 10 s.
