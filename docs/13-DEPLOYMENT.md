# 13 — Deployment

**Status:** v2 (2026-11-18).

## 1. Environments

| Env | Chain | Hosting | Purpose |
|---|---|---|---|
| `local` | Local MagicBlock stack (`mb-stack`) | Docker Postgres; Relay on the host; Unity editor / server build | Development |
| `devnet` | Solana devnet + ER `devnet-as` | Railway | Hackathon demo and judging |
| `mainnet` | — | — | **Out of scope.** Gated by doc 11 §4 |

Each environment has its own keypairs, program ID, database, and Crown collection.

## 2. Hosting choice (D-5, revised)

**Recommendation: Railway.** It is already connected to your Claude tools. With a WebGL client we only need WebSocket over HTTPS, which Railway supports. Railway does **not** support inbound UDP, and we no longer need it.

**Railway services:**
- `postgres` (Railway Postgres plugin)
- `relay` (Dockerfile in `server/`)
- `web` (Next.js; serves the WebGL build under `/play/` as static files with Brotli headers)
- `gameserver-1`, `gameserver-2` (Dockerfile `infra/gameserver.Dockerfile`, Linux x86_64 Mono server build, `-batchmode -nographics`)

**Region:** pick the Railway region closest to most playtesters and judges. Prefer the region closest to the ER validator (devnet-as = Asia) for the Relay.

**Fallback:** one VPS with Docker Compose and Caddy for TLS.
- Hetzner CPX32 (€30.49/mo) or DigitalOcean 4 vCPU / 8 GB ($48/mo).
- Use it if Railway's CPU for the Unity server turns out jittery (measure tick p95 in M6).

**UNVERIFIED (checked in the M1 spike):**
- Railway's per-service CPU behaviour for a 30 Hz Unity server.
- WebSocket upgrade through Railway's edge to a UTP server.

## 3. Configuration (`.env.example`; placeholders only)

```env
APP_ENV=devnet
# chain
SOLANA_RPC_URL=https://api.devnet.solana.com          # replace with a private devnet RPC if rate-limited
ER_RPC_URL=https://devnet-as.magicblock.app
ER_WS_URL=wss://devnet-as.magicblock.app
ER_VALIDATOR=MAS1Dt9qreoRMQ14YQuhg8UTZMMzDdKhmkZMECCzk57
VRF_QUEUE=5hBR571xnXppuCPveTrctfTU7tJLSN94nq7kv7FRK5Tc
FALL_RUSH_PROGRAM_ID=
GAME_AUTHORITY_KEYPAIR=            # base58 secret, Railway variable only
MINT_AUTHORITY_KEYPAIR=
CROWN_COLLECTION_ADDRESS=
# relay
DATABASE_URL=
TOKEN_HMAC_SECRET=
INTERNAL_HMAC_SECRET=
PUBLIC_WEB_ORIGIN=
# game server
RELAY_INTERNAL_URL=
GAMESERVER_ID=gs-1
GAMESERVER_PUBLIC_WS_URL=
```

## 4. Devnet release steps

1. `anchor build`, then `anchor deploy --provider.cluster devnet`. Record the program ID in `Anchor.toml`, `.env` and `packages/shared`.
2. `pnpm --filter server run init-config`. This calls `initialize_config` with the game authority and the hashes.
3. Create the Crown collection: `pnpm --filter server run create-collection`.
4. Push the Railway services and set the variables.
5. Smoke test: one practice match with bots, then a ranked match with 4+ humans. Export `PROOF.md`.

## 5. Freeze

**Code freeze:** Nov 27, 08:00 UTC. After that, only demo-blocking fixes. The submission goes in by **12:00 UTC** (2 hours of buffer before the 14:00 UTC deadline).
