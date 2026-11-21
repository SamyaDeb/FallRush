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
Status: **M2b done locally**: client prediction + reconciliation, remote interpolation, the Cinemachine camera, Bolt v1, a sweeper bar and a moving platform. Gate data for localhost, the real VPS and 150 ms is in [`docs/proof/M2-netcode.md`](docs/proof/M2-netcode.md). Proof and numbers are in [`docs/proof/M2-netcode.md`](docs/proof/M2-netcode.md). Movement constants live in `packages/shared/config/movement_v1.json`; after editing it, run `pnpm gen:movement`.

```bash
# once (Editor closed): build the local server (includes Map_Sandbox)
pnpm m1:build:macserver
# server with 12 occupants (11 TEST bots + you), 15 Hz snapshots, 60 fps frame cap
# (M4: without -fallrushMap the server runs matches; free play needs -fallrushMap Sandbox)
bash scripts/m1-macserver.sh start -fallrushMap Sandbox -fallrushBots 12
```

To play, use either:
- **The Editor:** open `client-unity` in Unity 6000.3.28f1 → `Assets/_Game/Scenes/Boot.unity` → Play → **Play (connect)** → **Enter Sandbox (M2)**.
- **A browser:** a **fresh** WebGL build (`pnpm m1:build:webgl:release`, with the Editor closed), served by `pnpm m1:up:mac`, at `http://localhost:8080/?autoplay=1` → **Play (connect)**. An old M1 build is rejected as protocol v1. Add `&bot=course` to watch the scripted course runner (it logs `[gate]` lines to the console).
- **At 150 ms RTT + jitter + 2 % loss:** `bash scripts/m2-netem.sh start 75 15 2`, then open `http://localhost:8080/?server=ws://localhost:7778&autoplay=1` (or point the Editor's server field at `ws://127.0.0.1:7778`). Stop it with `bash scripts/m2-netem.sh stop`.

Controls: click to capture the mouse · WASD move · Space jump · Shift/LMB dive · Esc releases the mouse and shows Options (sensitivity, invert Y, reduced motion) · **F3** netcode HUD. The hazard course is west of the spawn (run left): the striped sweeper bar, then the moving platform to the island.

```bash
bash scripts/m1-macserver.sh stop          # stop the local server
# measurements (Editor closed):
bash scripts/m2-measure.sh 15 60 60 mylabel                 # 12 occupants, headless probe client, nettop bytes
bash scripts/m2-probe.sh ws://127.0.0.1:7777 20 flood flood # input-flood kick test
bash scripts/m2-probe.sh ws://127.0.0.1:7777 120 course a1   # gate: scripted course runner (run 3 in parallel)
bash scripts/m2-probe.sh ws://127.0.0.1:7777 135 course r12 -fallrushProbeHide 20:60   # R12 hidden-tab simulation
python3 scripts/m2-summarize.py a1                         # one gate line per client
```

Server flags:

| Flag | Env var | Default | Meaning |
|---|---|---|---|
| `-fallrushBots N` | `GAMESERVER_BOTS` | 0 | fill to N occupants with test bots |
| `-fallrushSnapHz 15\|30` | `GAMESERVER_SNAP_HZ` | 15 | snapshot rate |
| `-fallrushServerFps N` | — | 60 | server frame cap |

## M3: run it (obstacle kit + Block Party Dash race)

```bash
pnpm m1:build:macserver                       # Mac dev server (also the headless probe/QA binary)
bash scripts/m1-unity.sh bpd -executeMethod BlockPartyDashBuilder.Run   # rebuild the race map (Editor closed)
bash scripts/m1-unity.sh sandbox -executeMethod SandboxBuilder.Run      # rebuild Map_Sandbox + kit row

# Play the kit row: server on the sandbox, then Editor Play (Boot) or the WebGL build → Lobby → "Enter Sandbox + obstacle kit"; walk north to z ≈ 54
bash scripts/m1-macserver.sh start -fallrushMap Sandbox -fallrushBots 4 [-fallrushModifier TurboToys]
# Race (M4: a one-round dev match: lobby → deal → the race, which crowns its winner → lobby); Tab spectates
bash scripts/m1-macserver.sh start -fallrushMap BlockPartyDash [-fallrushSeed 7 -fallrushVariant B -fallrushModifier MoonBounce]
pnpm m1:up:mac                                 # local nginx: http://localhost:8080 (WebGL) → Play → Enter Block Party Dash

# Measurements
FallRushDevServer -batchmode -nographics -fallrushMap BlockPartyDash -fallrushQA 100 -fallrushQATier m3 -fallrushQAOut map-qa.md   # M3 map QA (waypoint bots)
bash scripts/m3-gate.sh ws://127.0.0.1:7777 200 m3-local                                       # 3 race probes
FallRushDevServer -batchmode -nographics -fallrushSelfTest     # determinism self-test (WebGL: ?selftest=1; Linux: docker run --rm fallrush-gameserver:m3 -fallrushSelfTest)
pnpm gen:modifiers                                             # after editing modifiers_v1.json
```
Proof: `docs/proof/M3-race.md`.

## G1: run it (gameplay polish: movement v2, animation, map retunes, UX)
Status: **G1 built locally** (no VPS). Proof: [`docs/proof/G1-polish.md`](docs/proof/G1-polish.md), map QA [`docs/proof/g1-map-qa.md`](docs/proof/g1-map-qa.md), your play notes [`docs/playtest/G1-notes.md`](docs/playtest/G1-notes.md).

**Play a match** (as in M4): `pnpm m1:build:macserver` → `bash scripts/m1-macserver.sh start` → Editor `Boot.unity` → Play → type your name → **Play (Quick Play)**. Esc = settings (sensitivity, invert Y, FOV, camera distance, reduced motion, volumes, nameplates); 1–4 = emotes in the lobby / between rounds; F1 = controls bar; F3 = netcode HUD.

**Practice / tuning mode (Editor and development builds only):** the menu's **Practice / tuning (dev)** button, or `-fallrushPractice BlockPartyDash` (desktop dev build) / `?practice=GooTower` (WebGL development build). No server: the shared motor + the map's hazards run locally. **F6** tuning panel (PgUp/PgDn select, +/− change; readouts: speed, last jump apex / airtime / distance, stumble / slide), **F7** prints the values as JSON (console + clipboard) → paste into `packages/shared/config/movement_v2.json` → `pnpm gen:movement` → `pnpm hash` → rebuild. **R** respawn. Release builds cannot enter it.

```bash
pnpm gen:movement && pnpm hash                    # after editing movement_v2.json (C# constants + sha256)
bash scripts/m1-unity.sh maps -executeMethod AllMapsBuilder.Run   # rebuild every map scene (Editor closed)
bash scripts/g1-qa.sh 100                         # map QA → docs/proof/g1-map-qa.md
```

## G2/G3: run it (look + juice)
Same commands as M4 / G1 (`pnpm m1:build:macserver`, `pnpm m1:build:webgl:release`, `pnpm m1:up:mac`, open `http://localhost:8080/`).
- **Graphics: High / Low** in Settings (Esc in game, or the menu). High = post-processing (bloom, colour adjustments, vignette) at full render scale; Low = no post at 0.8 render scale. URL override for measurements: `?gfx=low` / `?gfx=high` (desktop: `-fallrushGfx low|high`).
- **My Rushling**: ◀ / ▶ on the menu picks one of 12 cosmetic presets (live preview, saved locally, sent in `hello`, shown to everyone). URL / CLI override: `?preset=N` / `-fallrushPreset N`.
- **Chain chip (dev)**: F9 in the Editor or a development build cycles `● Server` → `◆ Rollup ✓` → `⬢ Solana ✓` → `! Pending`; `?chain=cycle` (`-fallrushChain cycle`) cycles them every 2.5 s in any build (display only).
- **Audio** starts on your first click / key press (browser rule). Music / SFX volume sliders in Settings.
- **Proof tools**: `scripts/g2-shots.sh <dir> [secs] [w] [h] [args]` (a windowed probe saves a PNG of every screen and map; add `-fallrushPhoto closeup|silhouette|presets` for the Bolt photos); menu shot: `FallRushDevServer -fallrushMenuShot <png> [-fallrushMenuSettings]`.
- Regenerate the audio: `python3 tools/audio/synth.py <dir>` (needs numpy + ffmpeg); re-bake the fonts: `Unity -batchmode -executeMethod FontBaker.Run -quit` (not `-nographics`).

## M4: run it (a full match: lobby → deal → 4 rounds → champion → receipt → lobby)
Status: **M4 built locally** (no VPS: deferred to M6). Proof: [`docs/proof/M4-match.md`](docs/proof/M4-match.md), map QA [`docs/proof/m4-map-qa.md`](docs/proof/m4-map-qa.md).

```bash
pnpm m1:build:macserver                                   # Mac dev server (Editor closed); also the probe / QA binary
bash scripts/m1-macserver.sh start                        # a match server: Map_Lobby first; bots fill to 12 when the countdown ends
```

**Play a full match yourself**
- **Editor:** open `client-unity` in Unity 6000.3.28f1 → `Assets/_Game/Scenes/Boot.unity` → Play → **Play (Quick Play)**. You land in the lobby room; press **Start now (host)** or wait 20 s. Everything after that is automatic; Tab cycles the camera while you spectate; Esc = options; F3 = netcode HUD.
- **Browser:** `pnpm m1:build:webgl:release` (Editor closed, ~7–20 min) → `pnpm m1:up:mac` → `http://localhost:8080/?autoplay=1` (add `&name=You`; `&bot=match` lets a bot play for you; `&hud=1` shows the netcode HUD; `&selftest=1` prints the determinism self-test).
- **At 150 ms + jitter + 2 % loss:** `bash scripts/m2-netem.sh start 75 15 2`, then `?server=ws://localhost:7778&autoplay=1`.
- **Reconnect:** close the tab / kill the client mid-round and come back within 20 s in the same tab (sessionStorage token): same slot, still alive. After 20 s you are eliminated (disconnected) at the end of the round.

**Server flags (M4)**

| Flag | Env var | Default | Meaning |
|---|---|---|---|
| `-fallrushMap X` | `GAMESERVER_MAP` | match | `Sandbox` = M2 free play; a pool map (`BlockPartyDash`, `GooTower`, `StarScramble`, `CrumbleShelf`) = a one-round dev match on it |
| `-fallrushRounds N` | `GAMESERVER_ROUNDS` | 4 | play only the deal's first N rounds (the last one crowns 1) |
| `-fallrushSeed hex64\|text` | `GAMESERVER_SEED` | random per match | the practice match seed (match k uses sha256^(k−1)) |
| `-fallrushVariant A\|B`, `-fallrushModifier Name` | `GAMESERVER_VARIANT` / `_MODIFIER` | from the deal | dev overrides (where allowed) |
| `-fallrushMatches N` | `GAMESERVER_MATCHES` | 0 | soak: start without humans, quit after N matches |
| `-fallrushSimSpeed N` | `GAMESERVER_SIM_SPEED` | 1 | soak: ticks per network tick while nobody is connected |
| `-fallrushMatchSize N` | `GAMESERVER_MATCH_SIZE` | 12 | bots fill to N (4–12) |
| `-fallrushMatchLogDir dir` | `GAMESERVER_MATCH_LOG_DIR` | `./match-logs` | the jsonl event logs (M5 outbox input) |

**Measurements and QA**

```bash
bash scripts/m4-qa.sh 100                       # map QA: every new map × variant × modifier × 100 runs (reference + normal bots), BPD normal → docs/proof/m4-map-qa.md
bash scripts/m4-soak.sh 20 8                    # 20 unattended 12-bot matches back to back (sim speed 8), RSS samples, every jsonl log re-verified
python3 scripts/m4-verify-log.py match-logs/    # independent check of event logs: roster hash, deal, transcript, program order
bash scripts/m4-match-run.sh local              # 3 probe clients (BotBrain on their predicted state) + 9 bots, one full match, per-round corrections
bash scripts/m2-netem.sh start 75 15 2 && bash scripts/m4-match-run.sh netem ws://127.0.0.1:7778
bash scripts/m4-reconnect-run.sh                # kill a probe mid-round 2: back in 10 s (same slot) / 30 s (DISCONNECT)
pnpm --filter @fall-rush/shared gen-transcript-vectors   # regenerate the TS transcript vectors (C# parity tests read them)
bash scripts/m1-unity.sh goo -executeMethod GooTowerBuilder.Run        # rebuild a map (also LobbyBuilder, StarScrambleBuilder, CrumbleShelfBuilder)
```

