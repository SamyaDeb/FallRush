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

## M2: run it (networked movement)
Status: **M2a done** (shared motor, server sim, snapshots, sandbox map, no prediction yet). **M2b is next**: prediction, interpolation, camera and Bolt v1. Proof and numbers are in [`docs/proof/M2-netcode.md`](docs/proof/M2-netcode.md). Movement constants live in `packages/shared/config/movement_v1.json`; after editing it, run `pnpm gen:movement`.

```bash
# once (Editor closed): build the local server (includes Map_Sandbox)
pnpm m1:build:macserver
# server with 12 occupants (11 TEST bots + you), 15 Hz snapshots, 60 fps frame cap
bash scripts/m1-macserver.sh start -fallrushBots 12
```

To play, use either:
- **The Editor:** open `client-unity` in Unity 6000.3.28f1 → `Assets/_Game/Scenes/Boot.unity` → Play → **Play (connect)** → **Enter Sandbox (M2)**.
- **A browser:** a **fresh** WebGL build (`pnpm m1:build:webgl`, with the Editor closed), served by `pnpm m1:up:mac`, at `http://localhost:8080/?server=ws://localhost:7777&autoplay=1`. An old M1 build is rejected as protocol v1.

Controls: click to capture the mouse · WASD move · Space jump · Shift/LMB dive · Esc release · **F3** netcode HUD.

```bash
bash scripts/m1-macserver.sh stop          # stop the local server
# measurements (Editor closed):
bash scripts/m2-measure.sh 15 60 60 mylabel                 # 12 occupants, headless probe client, nettop bytes
bash scripts/m2-probe.sh ws://127.0.0.1:7777 20 flood flood # input-flood kick test
```

Server flags:

| Flag | Env var | Default | Meaning |
|---|---|---|---|
| `-fallrushBots N` | `GAMESERVER_BOTS` | 0 | fill to N occupants with test bots |
| `-fallrushSnapHz 15\|30` | `GAMESERVER_SNAP_HZ` | 15 | snapshot rate |
| `-fallrushServerFps N` | — | 60 | server frame cap |
