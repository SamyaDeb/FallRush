# Fall Rush

An original 12-player, 4-round knockout party game for the **MagicBlock Solana Blitz V10** hackathon. Match results are recorded on a MagicBlock Ephemeral Rollup and committed to Solana; the map/modifier deal comes from MagicBlock VRF. Specs live in [`docs/`](docs/00-README.md).

**Trust model:** the authoritative game server decides physics and who qualified. The `fall_rush` program accepts results only from the configured game authority, in strict sequence, for roster slots, with counts consistent with the committed ruleset; the deal is derived on-chain from VRF. It does **not** prove that gameplay was fair.

## Layout
| Path | What |
|---|---|
| `programs/` | Anchor 1.0.2 workspace: `fall_rush/` program, `tests/lifecycle.ts` |
| `packages/shared/` | canonical `ruleset_v1` / `map_pool_v1` JSON + hashes, `deal.ts`, test vectors |
| `client-unity/` | Unity 6000.3.28f1 project: WebGL client + Linux dedicated server (NGO 2.14, UTP 2.7.5 WebSockets) |
| `infra/` | game-server Dockerfile, nginx 1.24 edge config, local compose |
| `scripts/` | local test runner, devnet deploy + lifecycle, Unity batch wrapper |
| `docs/` | engineering docs v2 (`docs/proof/` = devnet proofs) |

## M0: run it
Prereqs: Node 24, pnpm 10, Rust, solana-cli 3.1.x, `avm` with Anchor **1.0.2** (see docs/07 §9 for the install workaround), and `npm i -g @magicblock-labs/ephemeral-validator@0.14.10`.

```bash
pnpm install
pnpm m0:test:local      # build (localnet feature) + Rust/TS deal parity + mb-stack + 2 vrf-oracles + lifecycle test; stops the stack
pnpm hash               # sha256 of the canonical config JSON
pnpm m0:devnet:deploy   # build (devnet queue) + deploy/upgrade on devnet with ~/.config/solana/id.json
pnpm m0:devnet          # full lifecycle on devnet + devnet-as ER; writes docs/proof/M0-devnet.md
```
The devnet game-authority key lives outside the repo at `~/.config/fall-rush/devnet-game-authority.json`.

## M1: run it
Prereqs: Unity **6000.3.28f1** with Web Build Support, Linux Build Support (Mono) and Linux Dedicated Server Build Support (e.g. `unity install-modules --editor-version 6000.3.28f1 -m webgl linux-mono linux-server`), an activated licence, Docker Desktop. **Close the Unity Editor** before any of these (8 GB rule).

```bash
pnpm m1:test:unity            # EditMode tests (results: .m1-logs/editmode-results.xml)
pnpm m1:build:webgl           # dev build  → client-unity/Builds/WebGL (uncompressed by Unity design)
pnpm m1:build:webgl:release   # release    → Brotli .br files
pnpm m1:build:server          # Linux x86_64 dedicated server (Mono) → client-unity/Builds/Server
```

Local stack (self-signed cert generated into `.m1-certs/`, never committed):

```bash
# Apple Silicon (the Mono Linux server cannot run under Docker emulation, see docs/proof/M1-transport.md §0):
pnpm m1:build:macserver && pnpm m1:up:mac
# x86_64 Linux host:
pnpm m1:up

# then open (one browser window, ≤ 2–3 tabs):
#   http://localhost:8080/?server=ws://localhost:7777
#   https://localhost:8443/?server=wss://localhost:8443   (accept the self-signed cert once)
pnpm m1:down
```

Server env: `GAMESERVER_PORT` (default 7777; `-port N` wins), `GAMESERVER_ID`. Proof and measurements: [`docs/proof/M1-transport.md`](docs/proof/M1-transport.md).
