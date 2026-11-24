# 10 — API and Realtime Contracts

All endpoints are versioned under `/v1`. Use JSON, TLS, request IDs, rate limits, schema validation, and consistent error envelopes. These are proposed contracts; implement and publish OpenAPI before client integration.

## 1. REST endpoints

### Identity
- `POST /v1/identity/guest` → `{ userId, accessToken, expiresAt }`
- `POST /v1/wallet/nonce` → `{ nonce, message, expiresAt }`
- `POST /v1/wallet/verify` → `{ walletAddress, linked: true }`

### Lobbies
- `POST /v1/lobbies` body `{ capacity, region, visibility }`
- `POST /v1/lobbies/{id}/join`
- `POST /v1/lobbies/{id}/ready` body `{ ready: boolean }`
- `POST /v1/lobbies/{id}/leave`
- `GET /v1/lobbies/{id}`

### Matchmaking
- `POST /v1/matchmaking/tickets` body `{ region, mode, partyId? }`
- `DELETE /v1/matchmaking/tickets/{ticketId}`
- `GET /v1/matches/{matchId}`
- `GET /v1/matches/{matchId}/result`

### Profile
- `GET /v1/users/me`
- `GET /v1/users/{id}/stats`
- `GET /v1/users/me/inventory`

## 2. Error envelope

```json
{
  "error": {
    "code": "LOBBY_FULL",
    "message": "This lobby is full.",
    "requestId": "uuid",
    "retryable": false
  }
}
```

## 3. Realtime protocol

Transport: choose NGO/UTP direct dedicated server for game simulation. WebSocket may be used for lobby/control events, not necessarily high-frequency physics. If using WebSocket for game state, measure latency and bandwidth and define binary encoding before production.

Client → server messages:
- `INPUT_FRAME { seq, moveX, moveY, jump, dive, interact, ackServerTick }`
- `CLIENT_READY { buildVersion, contentVersion }`
- `PING { nonce, clientTime }`
- `RECONNECT { reconnectToken }`

Server → client messages:
- `LOBBY_STATE { lobbyId, members, countdownAt }`
- `MATCH_INIT { matchId, rulesetVersion, mapSequence, mapSequenceHash }`
- `SNAPSHOT { serverTick, entities:[...] }`
- `ROUND_STARTED { round, mapId, endsAt }`
- `CHECKPOINT_CONFIRMED { playerId, checkpointIndex }`
- `PLAYER_QUALIFIED { playerId, placement }`
- `PLAYER_ELIMINATED { playerId, reason }`
- `ROUND_COMPLETED { round, qualifiedPlayers }`
- `MATCH_COMPLETED { championId, resultStatus }`
- `CHAIN_STATUS { status, signature?, explorerUrl? }`
- `SERVER_WARNING { code, message }`

## 4. Security rules

- Every message is authenticated to a session and match.
- Server ignores client-supplied position, score, placement, checkpoint claims, champion, map sequence, or chain status.
- Validate message size, frequency, finite numeric inputs, enum values, and sequence numbers.
- Reconnect tokens are short-lived, single-use, bound to user and match.
- Never expose RPC API keys or backend signer credentials to client.

## 5. Versioning

Add `protocolVersion` and `buildVersion` to handshake. Reject incompatible client/server protocol. Support one prior client version only if tested. Keep JSON schema/Protobuf definitions in shared package and generate client types where possible.
