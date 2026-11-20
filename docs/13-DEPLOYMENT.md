# 13 — Deployment

**Status:** v2 (2026-11-18).

## 1. Environments

| Env | Chain | Hosting | Purpose |
|---|---|---|---|
| `local` | Local MagicBlock stack (`mb-stack`) | Docker Postgres; Relay on the host; Unity editor / server build | Development |
| `devnet` | Solana devnet + ER `devnet-as` | Hostinger VPS (Mumbai), Docker Compose behind nginx | Hackathon demo and judging |
| `mainnet` | — | — | **Out of scope.** Gated by doc 11 §4 |

Each environment has its own keypairs, program ID, database, and Crown collection.

## 2. Hosting (D-5, approved 2026-11-18): your Hostinger VPS

**Box:** the existing Hostinger **KVM 2** VPS. Ubuntu 24.04, 2 vCPU, 8 GB RAM, 100 GB disk, data centre **Mumbai** (`mum2`).
- Ping from the dev machine is ≈ 45 ms (measured 2026-11-18). It is in Asia, like the ER validator `devnet-as`.
- **It already serves another site with nginx 1.24 on ports 80/443.** Fall Rush must never edit, restart or replace that site's config. It only adds new nginx `server` blocks for its own subdomains, and checks `nginx -t` before every reload.
- The real hostname and IP stay out of git. Use placeholders in docs (`<VPS_HOST>`).

**Layout:**

```text
Internet ──443──▶ nginx (existing, on the host) ── TLS (Let's Encrypt via certbot, per subdomain)
                    ├─ play.<domain>   → 127.0.0.1:3000  web (Next.js; WebGL build under /play/ with Brotli headers)
                    ├─ api.<domain>    → 127.0.0.1:8787  relay (HTTP + WebSocket)
                    ├─ gs1.<domain>    → 127.0.0.1:7777  gameserver-1 (UTP WebSocket, plain ws behind nginx)
                    └─ gs2.<domain>    → 127.0.0.1:7778  gameserver-2
docker compose (infra/docker-compose.prod.yml), every port bound to 127.0.0.1 only:
  web · relay · postgres (volume, nightly pg_dump) · gameserver-1 · gameserver-2
```

- **WebSocket proxying in nginx:** `proxy_http_version 1.1`, `proxy_set_header Upgrade $http_upgrade`, `proxy_set_header Connection "upgrade"`, and a long `proxy_read_timeout` (for example 3600 s), since idle match sockets must not be cut. These are standard nginx directives; the exact config is tested locally in M1 before it touches the VPS.
- **Domain:** you have **no domain** for now (2026-11-18), so use **sslip.io** hostnames that embed the IP: `play.<IP-dashed>.sslip.io`, `api.…`, `gs1.…`, `gs2.…`. DNS resolution was verified; Let's Encrypt issuance is UNVERIFIED until M1 §4.7. A real domain can replace them later by changing only nginx `server_name` and the web config.
- **Hostinger CLI** (`hostinger`, signed in through OAuth) handles VM info, snapshots (take one before every VPS change), SSH keys and the firewall. **Docker Manager is not supported on this VM's OS** (`VPS:2044`), so containers are deployed over SSH with the attached key. Don't create or activate a Hostinger firewall group without checking the existing Ovelo site's ports.
- **Firewall:** only 22, 80 and 443 inbound (Hostinger firewall or `ufw`). Game server ports are never exposed directly.
- **Resource caps:** Docker `cpus` / `mem_limit` per service so Fall Rush cannot starve the existing site. Starting budget: each game server 0.75 CPU / 1 GB, relay 0.25 / 512 MB, postgres 0.25 / 512 MB, web 0.25 / 512 MB. Tune with measurements.
- **Deploys:** build images locally (server image is linux/amd64, which matches the VPS), `docker save | ssh … docker load` or a private registry, then `docker compose up -d`. No CI in the hackathon.

**UNVERIFIED (checked in M1 and M6):**
- UTP WebSocket behind nginx TLS termination (M1 tests the same nginx config locally, then on the VPS).
- CPU headroom for two 30 Hz Unity servers on 2 shared vCPUs (measure tick p95 in M6; drop to one game server if needed).

**Fallback:** a second VPS (Hostinger KVM 4, or any 4 vCPU / 8 GB box) with the same compose file.

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
GAME_AUTHORITY_KEYPAIR=            # base58 secret; only in /opt/fallrush/.env on the VPS (chmod 600, root-owned), never in git
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
4. Copy images to the VPS, write `/opt/fallrush/.env` (chmod 600), `docker compose up -d`, add the nginx server blocks + certbot certs, `nginx -t`, reload.
5. Smoke test: one practice match with bots, then a ranked match with 4+ humans. Export `PROOF.md`.

## 5. Freeze

**Code freeze:** Nov 27, 08:00 UTC. After that, only demo-blocking fixes. The submission goes in by **12:00 UTC** (2 hours of buffer before the 14:00 UTC deadline).
