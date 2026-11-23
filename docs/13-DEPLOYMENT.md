# 13 — Deployment

**Status:** v3 (2026-11-23): §2a is the P5 as-built public deployment; §2 keeps the M1 plan and findings.

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

**M1 result (2026-11-18):**
- **Verified locally:** UTP WebSocket behind **nginx 1.24.0** TLS termination. Two Chrome clients reached the Lobby over `wss://localhost:8443` (same origin: static + upgrade on `/`) and the nginx access log shows `GET / HTTP/1.1" 101`. Config: `infra/nginx/fallrush-m1.conf` (the `server` block shape for `gs1.<domain>` is the `:8444` block, upstream `127.0.0.1:7777`).
- **Brotli:** nginx serves Unity's pre-compressed `.br` files with `Content-Encoding: br` and the right `Content-Type` (`application/wasm`, `application/javascript`, `application/octet-stream`) — no brotli module needed, **Decompression Fallback off**. Unity writes the `.br` files with mode 0600: `chmod -R a+rX` the build before nginx serves it.
- **Verified on the VPS (M1 §4.7):** `fallrush-gameserver` (Linux x86_64, Mono) runs in Docker on the KVM 2 (`--cpus 0.75 --memory 1g`, bound to `127.0.0.1:7777`); **Let's Encrypt issues certificates for sslip.io names** via `certbot --nginx`; 2 Chrome clients over `wss://gs1.<VPS_IP>.sslip.io` → RTT p50 66 / p95 83 ms from the dev machine (ICMP 38 ms); container 84–86 MB, 1.6 % CPU, tick work p95 0.6 ms with 2 clients. The existing site was unchanged. The test server was then removed (snapshot, cert and image kept).
- On the Apple Silicon dev machine the Mono Linux player **cannot** run in Docker (crashes under Rosetta and QEMU: `x86-codegen.h:410`); local tests use the native macOS dev server (`pnpm m1:up:mac`).
- **Hostinger snapshots expire automatically after 24 h**: take a fresh one right before each VPS change.
- **Port clash:** `127.0.0.1:8787` and `:3100` are already used on the VPS by the existing `finity` site. The relay port in the layout above must change (e.g. 8790) before M6.
- CPU headroom for two 30 Hz Unity servers on 2 shared vCPUs (measure tick p95 in M6; drop to one game server if needed).

**Fallback:** a second VPS (Hostinger KVM 4, or any 4 vCPU / 8 GB box) with the same compose file.

## 2a. As built (P5, 2026-11-23): the public URL

`play.<VPS_IP-dashed>.sslip.io` (landing, `/play`, `/live`, `/replay`, `/receipt`, `/me`, `/api`), `gs1.…` and `gs2.…`
(game-server WebSockets). Let's Encrypt via `certbot --nginx` per name. No Postgres, no relay, no Next.js (OC-8): a
static Vite app + small Node helpers; the Final runs on the MagicBlock rollup (devnet-as).

```text
Internet ──443──▶ host nginx (existing; finity untouched)
   sites-available/fallrush-p5.conf, enabled as sites-enabled/zz-fallrush-p5.conf (loads after finity: finity stays
   the default server for unmatched hosts)
   ├─ play.…  → 127.0.0.1:3110  fallrush-web      nginx:alpine: web/app dist + /unity (WebGL) + /api → indexer
   ├─ gs1.…   → 127.0.0.1:7777  fallrush-gs1      Classic game server, arena 2 (WebSocket, 3600 s timeouts)
   └─ gs2.…   → 127.0.0.1:7778  fallrush-gs2      Classic game server, arena 3 (compose profile gs2)
docker compose project `fallrush` (/opt/fallrush, infra/prod/docker-compose.yml), all ports on 127.0.0.1:
   fallrush-keeper-1 :3111 (arena 2) · fallrush-keeper-2 :3113 (arena 3) · fallrush-indexer :3112
   volumes: ./logs/gs1|gs2 (server match logs + status.json → keeper), ./archive (keeper → indexer), ./web,
   ./secrets/{game-authority,funder}.json (root, 600; each mounted only where needed), ./keys (market maker)
```

| Service | Image | CPU / RAM cap | Measured during 2 concurrent matches (P5 §7) |
|---|---|---|---|
| fallrush-gs1 / gs2 | `fallrush-gameserver:p5` (`Build.LinuxDevServer` on the M3 image) | 0.75 / 1 GB each | CPU p50 ≈ 5 %, max ≈ 9 %; ≤ 140 MiB; tick work p95 ≤ 3.5 ms, 30 Hz |
| fallrush-keeper-1 / 2 | `fallrush-node:p5` (Node 24, `tools/keeper/daemon.ts`) | 0.25 / 512 MB each | CPU max ≈ 16 %; ≤ 72 MiB |
| fallrush-indexer | `fallrush-node:p5` (`tools/indexer/server.ts`) | 0.25 / 512 MB | ≤ 84 MiB |
| fallrush-web | `nginx:1.27-alpine` | 0.25 / 256 MB | ≈ 3 MiB |

- **WebGL loading:** Brotli with **Decompression Fallback** (`.unityweb`, served plain): `/play` fetches the two big
  files in 5 parallel Range requests (one TCP stream from the VPS measured 0.1–0.5 MB/s, 4 streams 2.2 MB/s) and the
  loader decodes them. OC-14.
- **Game server build:** `Build.LinuxDevServer` (Player subtarget + `FALLRUSH_DEV_SERVER`, `-fallrushServer`): the
  Dedicated Server subtarget does not compile the G4 editor tools. Image `FROM fallrush-gameserver:m3` (no apt step).
- **Ops:** `restart: unless-stopped`, health checks on every service, Docker log rotation (10 MB × 3).
  `scripts/p5-deploy.sh [web|node|gs|all] [--gs2]` (build → copy → `docker compose up -d` → smoke),
  `scripts/p5-deploy-quick.sh` (small web changes), `scripts/p5-health.sh` (services + public smoke + neighbours),
  `scripts/p5-neighbours.sh` (finity + Ovelo + raw-IP check), `scripts/p5-kill-test.sh`.
- The real IP / hostnames live only in `~/.config/fall-rush/vps.env` (local) and `/opt/fallrush/.env` (VPS).

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
GAMESERVER_PORT=7777               # UTP WebSocket listen port in the container (server: -port arg > GAMESERVER_PORT > 7777); host-bound to 127.0.0.1
GAMESERVER_PUBLIC_WS_URL=
```

**Game server image (M1):** `infra/gameserver.Dockerfile` — `linux/amd64` `ubuntu:22.04` + `ca-certificates`, non-root user, `ENTRYPOINT ["./FallRushServer.x86_64","-batchmode","-nographics","-logFile","-"]` (the Dedicated Server player prints "Unknown arg" for `-batchmode`/`-nographics`; harmless), TCP `HEALTHCHECK`. Image 262 MB (68 MB content) with the SDK in the project. Whether more apt packages are needed is UNVERIFIED until it runs on real x86_64.

## 4. Devnet release steps

1. `anchor build`, then `anchor deploy --provider.cluster devnet`. Record the program ID in `Anchor.toml`, `.env` and `packages/shared`.
2. `pnpm --filter server run init-config`. This calls `initialize_config` with the game authority and the hashes.
3. Create the Crown collection: `pnpm --filter server run create-collection`.
4. Copy images to the VPS, write `/opt/fallrush/.env` (chmod 600), `docker compose up -d`, add the nginx server blocks + certbot certs, `nginx -t`, reload.
5. Smoke test: one practice match with bots, then a ranked match with 4+ humans. Export `PROOF.md`.

## 5. Freeze

**Code freeze:** Nov 27, 08:00 UTC. After that, only demo-blocking fixes. The submission goes in by **12:00 UTC** (2 hours of buffer before the 14:00 UTC deadline).
