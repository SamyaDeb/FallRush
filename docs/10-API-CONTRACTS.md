# 10 — API & Realtime Contracts

**Status:** v2 (2026-11-18).

**Conventions:**
- Public REST is served under `/v1` as JSON over HTTPS.
- Internal endpoints are listed in doc 05 §3.
- Types live in `packages/shared/src/api.ts` and use zod schemas. Unity mirrors them in `Shared/Protocol.cs`.

**Error envelope:**

```json
{ "error": { "code": "LOBBY_FULL", "message": "This lobby is full.", "requestId": "uuid", "retryable": false } }
```

## 1. Public REST

| Method & path | Auth | Request | Response |
|---|---|---|---|
| `POST /v1/identity/guest` | — | `{displayName?}` | `{userId, token, expiresAt, displayName, cosmeticPreset}` |
| `GET /v1/me` | Bearer | — | `{userId, displayName, xp, level, wallet?, cosmeticPreset}` |
| `PATCH /v1/me` | Bearer | `{displayName?, cosmeticPreset?}` | same as above |
| `POST /v1/wallet/nonce` | Bearer | `{walletAddress}` | `{nonce, message, expiresAt}`. `message` = `"Fall Rush wants you to link wallet <addr>\nNonce: <n>\nExpires: <iso>\nDomain: <host>"` |
| `POST /v1/wallet/verify` | Bearer | `{walletAddress, signature(base58)}` | `{walletAddress, linked:true}` |
| `POST /v1/play/quick` | Bearer | `{region?}` | `{serverWsUrl, joinTicket}` (single-use, 60 s) |
| `POST /v1/play/private` | Bearer | — | `{code, serverWsUrl, joinTicket}` |
| `POST /v1/play/join` | Bearer | `{code}` | `{serverWsUrl, joinTicket}` |
| `GET /v1/matches?limit=20` | — | — | recent matches (for the web home page) |
| `GET /v1/matches/:id` | — | — | receipt: players, rounds, deal, chain txs, status, PDAs |
| `GET /v1/matches/:id/live` | — | — | `{matchPda, erRpc, erWs, status}`, which the web uses to subscribe to the ER directly |
| `POST /v1/matches/:id/picks` | Bearer | `{round, slot}` | guest Fan Pick (DB only) |
| `GET /v1/leaderboard?type=crowns\|oracle` | — | — | top 50 |
| `POST /v1/trophies/:matchId/claim` | Bearer + linked wallet | — | `{status, assetAddress?}` |

**Rate limits** (per IP and per user):
- guest creation: 5/min
- nonce: 10/min
- play endpoints: 30/min

## 2. Realtime (game server, NGO)

- **Handshake:** the NGO connection-approval payload is `{protocolVersion:1, buildVersion, joinTicket}`.
  - The server verifies the ticket with the Relay's HMAC secret: user id, expiry, single use.
  - A protocol version mismatch is rejected with code `PROTO_MISMATCH`.
- **Messages:** see doc 04 §4.1 (`in`, `snap`, `evt`, `hello`).
- **`evt` types** (with `seq`):

  ```
  LOBBY_STATE{members[], countdownEndsAt?}
  DEAL_PENDING{matchId, matchPda}
  DEAL_READY{maps[], variants[], modifiers[], vrfSeedHex, vrfSig?}
  DEAL_UNRANKED{reason}
  ROUND_INTRO{round, mapId, modifier, startsAtTick}
  ROUND_STARTED{round, endsAtTick}
  CHECKPOINT{slot, index}
  QUALIFIED{slot, placement}
  ELIMINATED{slot, reason}
  TILE_DROP{tileId, tick}
  STAR{starId, slot?, kind}
  SCORE{slot, score}
  ROUND_COMPLETED{round, qualified[]}
  CHAMPION{slot}
  CHAIN_STATUS{seq, state, sig?, layer}
  MATCH_DONE{receiptUrl}
  SERVER_WARNING{code, message}
  ```

**Security rules:**
- The server ignores any client-supplied position, score, placement, checkpoint, deal or chain status.
- Inputs are validated as in doc 04 §4.2.
- Payload size cap: 256 B per `in` message.
- Rate cap: 90 `in` messages per second per client.
