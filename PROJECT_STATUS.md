# Fall Rush — Project Status

_Last updated: 2026-11-18 (D0, M0 + M1 complete; M2a at checkpoint)_

**Hackathon:** MagicBlock Blitz V10, running Nov 20 to Nov 27, 2026.
- **Submission deadline:** Nov 27, 14:00 UTC (19:30 IST).
- **Our internal target:** submit by 12:00 UTC.

## Completed
- **Engineering audit** of the v1 docs. See `ENGINEERING_REPORT.md`; it is now superseded by `docs/`.
- **Research, verified against these sources:**
  - Blitz V10 rules and past winners
  - MagicBlock SDK 0.16.2 APIs and `magicblock-examples`
  - Unity 6.3 / NGO 2.14
  - hosting options
  - Metaplex Core
- **Docs v2**, written in `docs/` (00–18):
  - art direction
  - hackathon strategy
  - implementation plan

## Milestone M0 (repo + MagicBlock lifecycle spike) — ✅ complete
| Item | State |
|---|---|
| Monorepo scaffold, `.gitignore`, shared config + hashes | ✅ |
| `anchor build` on Anchor 1.0.2 + ER SDK 0.16.2 | ✅ |
| Deal parity Rust (5/5) + TS (6/6) on 100-seed vectors | ✅ |
| `pnpm m0:test:local` lifecycle on mb-stack 0.14.10 + 2 VRF oracles | ✅ 10/10 |
| Program deployed to devnet `4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4` (deploy tx `zQzTyAr95iipfmHHaBc51TogPtXxkDe85A8E3k3Z8jebsZshE6Z2AiYvnKTsiXsmxLtxxNAXR6Hq1g8XNsSXmzf`) | ✅ |
| Devnet lifecycle on devnet-as (`pnpm m0:devnet`) → `docs/proof/M0-devnet.md` | ✅ full lifecycle on devnet-as, final Solana state == ER state (2026-11-18) |

**Devnet measurements (devnet-as, from this machine, one run):** VRF request→callback 426 ms; ER send→confirm p50 227 ms, max 235 ms (≈ network RTT); commit visible on Solana 1.9 s (GetCommitmentSignature 1.9 s); commit+undelegate ownership back 1.5 s; delegate tx 17 s (public devnet RPC 429 rate-limiting). Run cost ≈ 0.011 SOL.

**Local measurements (M2 Air, local stack):** VRF request→callback 0.1–0.6 s; ER send→confirm p50 8–18 ms, p95 30–220 ms (polling); commit visible on Solana ~6.5 s; commit+undelegate ownership back ~0.4–0.5 s.

### Notes for M6 (from M0)
- **Typed-account delegation:** `#[delegate]` needs `UncheckedAccount`; we deserialize `Match` manually before the CPI — works (tested).
- **Public devnet RPC rate-limits (429)** — the delegate tx took 17 s. Use a dedicated RPC (Helius etc.) for the chain worker in M6.
- **Confirmations:** local ER intermittently drops websocket signature notifications → the chain worker must confirm by polling `getSignatureStatuses`; `GetCommitmentSignature` can take ~30 s even when the commit already landed — show "Solana ✓" from the base account state, fetch the commit sig asynchronously.
- **Config on the ER** is a cloned read-only copy; config changes on Solana may be stale on the ER (UNVERIFIED clone-refresh behaviour).
- **Ruleset hash** is compiled into the program; adding movement constants (M2) to the ruleset means a new ruleset version + program upgrade + config update (`update_config` not implemented yet).
- **`finalize_match` from `RoundResolved`** is an M0 shortcut — remove in M6.
- Missing security tests listed in docs/06 §9.

### Independent review of M0 (2026-11-18)
- Re-ran Rust deal parity (5/5) and TS tests (6/6) myself: green. Config hashes match the canonical JSON.
- Program reviewed against doc 06: signer checks, `event_seq`, target bounds and the scoped VRF callback are correct. Commits per match: 4 rounds + 1 finalize = 5 (within the limit).
- No keypairs in git history. Program is live on devnet (upgrade authority is your default wallet).
- ~~Still open: the devnet run.~~ Done: `docs/proof/M0-devnet.md`.

## Milestone M1 (Unity foundation + WebGL ↔ server WebSocket spike) — ✅ complete (local + VPS)
Proof: [`docs/proof/M1-transport.md`](docs/proof/M1-transport.md) · screenshots `docs/proof/m1/`.

| Item | State |
|---|---|
| Unity 6000.3.28f1 URP project, packages pinned (NGO 2.14.0, UTP 2.7.5, DS 2.0.2, MPPM 2.0.2, CM 3.1.7, Input 1.20.1, URP 17.3.0), manifest + lock committed | ✅ |
| Assemblies Shared/Net/Client/Server/Client.Wallet/Tests, Boot/Menu/Lobby, NetBootstrap + ping/pong, approval (protocol, full at 12) | ✅ |
| `pnpm m1:build:server` / `m1:build:webgl[:release]` batch builds (Editor closed, logs in `.m1-logs/`) | ✅ |
| `pnpm m1:test:unity` EditMode | ✅ 52/52 |
| Plain `ws`: 2 Chrome clients in Lobby | ✅ (native Mac dev server, see note) |
| `wss` via nginx 1.24 TLS (same origin): 2 clients, 101 in nginx log | ✅ |
| Protocol mismatch rejection with readable reason; Reconnect after server restart without reload | ✅ |
| 10-min idle soak, 2 clients, no disconnects | ✅ |
| Solana.Unity-SDK: WebGL + server build with it | ✅ with upstream fix `1da8fd7f` (D-15 ✅); ❌ with tag v1.2.9 |
| Linux x86_64 server image runs | ✅ on the VPS (Mono crashes only under Rosetta/QEMU on this Mac) |
| VPS edge check (§4.7): Let's Encrypt on sslip.io, 2 clients over `wss`, RTT p50 66 / p95 83 ms, container 86 MB / 1.6 % CPU | ✅ — then removed as instructed; existing site unchanged |

**Key measurements:** WebGL release 523 s clean / 294 s incremental, 11.83 MB (15.83 MB with SDK); dev 714 s / 238 s; peak build RSS 1.1–1.5 GB; RTT p50 ≈ 33 ms localhost, **66 ms to the VPS** (ping 38 ms); VPS server 30.0 Hz, work p95 0.6 ms with 2 clients, 86 MB; wasm heap 96 MB in Lobby.

### Open issues (M1)
- **VPS leftovers (kept on purpose):** Hostinger snapshot (auto-expires 2026-11-19 10:15 UTC), the `gs1` Let's Encrypt cert (its renewal will fail without the server block — say if I should `certbot delete` it), the `fallrush-gameserver:m1` image on the VPS.
- **VPS port clash:** `127.0.0.1:8787` and `:3100` are used by the existing site; change the relay port before M6 (R13).
- Real Phantom connect in Chrome: UNVERIFIED (steps in the M1 report).
- Hidden-tab freeze → possible 30 s UTP disconnect (R12), unverified.
- SDK loads its wallet-adapter JS from jsdelivr at runtime; SDK prefabs sit in `Resources/` (ship in the server build). Revisit in M5.
- Unity licensing time-outs under heavy swap made two batch runs fail; re-run passed.
- Docker Desktop: Rosetta is now **off** (you turned it off for M1); it can be turned back on, since local tests no longer need amd64 emulation. Your nitroverse/chainpe containers did not restart after the Docker restart.

### Notes for M2 (from M1)
- **Frame budget:** on the VPS the server uses 0.4 ms (idle) / 0.6 ms (2 clients) p95 per 33 ms frame at 0.75 CPU. Measure again with 12 bots + movement.
- **RTT quantisation:** localhost ping ≈ 33 ms = one 30 Hz frame (sends flushed per frame/tick). Prediction must account for 1–2 frames of send latency on top of the network.
- **WebSockets are TCP:** `Unreliable`/`UnreliableSequenced` still travel in order on one TCP stream, so a lost packet stalls everything behind it (head-of-line). Keep snapshots small (≈ 240 B) and use sequencing to drop stale ones client-side.
- **WebGL memory:** 96 MB wasm heap in Lobby, 2 GB max; plenty of headroom so far.
- **Scene management is off** (`EnableSceneManagement = false`); M2 must turn on NGO scene management (or our own) for round maps.
- `UNITY_SERVER` asmdef constraints strip client code; Multiplayer Roles must still be used for components on shared prefabs.
- Dedicated-server player ignores `-batchmode`/`-nographics` ("Unknown arg"); harmless.

## Milestone M2a (shared motor + input + server sim + snapshots) — ✅ at checkpoint, waiting for your "continue"
Proof: [`docs/proof/M2-netcode.md`](docs/proof/M2-netcode.md) Part A · screenshot `docs/proof/m2/m2a-sandbox-hud.png`.

| Item | State |
|---|---|
| `movement_v1.json` (canonical, sha256 `1d966e17…`), `pnpm gen:movement` → C# constants, hash-match tests (file on disk + typed constants) | ✅ |
| `KinematicMotor` (pure, integer mm / cm/s state = wire precision), `InputCmd`, `MotorState`, `MovementConfig`, `IMotorWorld` (PhysX + analytic) | ✅ |
| Protocol v2: `in` 40 B, `snap` 280 B for 12; `InputSanitizer`; `TickSync` clock; `SimCore` + `ServerSim` (12 bots, separation push, fall → respawn 1 s) | ✅ |
| Client: Input System sampler, pointer lock on click, all players from snapshots (no prediction), placeholder capsules + orbit camera, F3 HUD | ✅ (Mac build; WebGL UNVERIFIED) |
| `Map_Sandbox` greybox (ramps 15/30/44/50°, steps 0.2/0.3, gaps 1.0/1.4/1.8, walls 1.5/1.7, beam, fall-off edge, 12 spawns) | ✅ |
| EditMode tests | ✅ **130/130** (was 52) |
| Server with 12 occupants (Mac dev server) | frame work p95 **≤ 0.7 ms** at 60 fps cap, sim step p95 ≤ 0.25 ms, CPU 1.7 % |
| Snapshot bandwidth per client | 15 Hz: 4.2 KB/s payload / **5.2 KB/s socket**; 30 Hz: 8.4 / **10.1 KB/s** |
| Server frame cap 30 → 60 fps | localhost RTT p50 **33 → 17 ms**; kept as default |
| Flood (200 junk msgs/frame) | kicked in the first tick, RSS +0.3 MB |

### Notes for M2b / M3 / M4 / M6 (from M2a)
- **M2b:**
  - History is keyed by **tick**, not seq: the server applies the command with `tick == S`, so reconcile compares `history[S]`.
  - The snapshot carries the owner's timers, so a rewind is exact.
  - Linux server build and the VPS deploy with the M2 code are still to do.
  - One WebGL build at the end.
- **M3:** `IMotorWorld.MotorHit.SurfaceId` is reserved for moving platforms; hazards must be `pose = f(params, seed, tick)` only.
- **M4:**
  - NGO scene management is still off; both sides load `Map_Sandbox` locally.
  - `TestBotInput` is a load-test stand-in, not `BotBrain`.
  - `lastCheckpoint` belongs in round rules, not `MotorState`.
- **M6, ruleset v2 merge:** fold `movement_v1.json` (hash `1d966e17…`) into a new ruleset version committed on-chain. This needs:
  - a program upgrade;
  - a config update (`update_config` is not implemented yet).
  
  `ruleset_v1.json` and anything on-chain were **not touched** in M2.
- **M6:** per-IP connection/rate limiting at nginx (the in-game kick handles a single flooding client).

## Next milestones
- **M2b (after your "continue"):** client prediction + reconciliation, remote interpolation, Cinemachine camera, Bolt v1, hazards for the gate, R12 hidden-tab test, go/no-go data (localhost / VPS / 150 ms).

## Next: M2 (networked movement), prompt `docs/prompts/M2-PROMPT.md`
- Agreed 2026-11-18: VPS check first (✅ done in M1 §4.7), SDK pin (D-15 ✅), **one M2 prompt with an M2a → M2b checkpoint**, and **Bolt v1 built in Unity from shapes** (no Blender).
- Spec fix before M2: doc 04 §4.1 snapshot position is now **i32 mm** (the old i16 at 2.5 mm covered only ±82 m, too short for a ~500 m race). The server quantises its own state to wire precision every tick.
- Open question for you: the Blitz rules don't say whether code written before Nov 20 is allowed. Check the Luma / Discord rules; we disclose either way (doc 18).
- VPS cleanup pending your word: the `gs1` cert (renewal fails without its server block) and the M1 image. The snapshot expires by itself on 2026-11-19 10:15 UTC.

## Decisions (all approved 2026-11-18, incl. D-15 SDK pin and D-16 Mac dev server — see docs/15)
| ID | Decision |
|---|---|
| D-1b | **Fix first:** if the Nov 22 go/no-go fails, find and fix prediction until Nov 23 12:00 UTC (doc 04 §4.5 checklist); F1 only if it still fails |
| D-3 | VRF requested inside the ER |
| D-4 | WebGL client |
| D-5 | **Hostinger VPS** (your KVM 2, Mumbai): Docker Compose behind the VPS's existing nginx + Let's Encrypt (replaces Railway). The existing site on the VPS must stay untouched. |
| D-8 | Fan Picks on the web page (session keys) |
| D-11 | Single `Match` PDA |
| D-12 | Commit to Solana once per round |
| D-13 | Bots inside the server process, labelled |
| D-14 | Bonk Factory is a stretch goal |

## Your tasks for today
- [x] **Unity:** 6000.3.28f1 with Web, Linux Mono and Linux Dedicated Server modules; Personal licence active.
- [x] D-15 (SDK pin) approved; M1 §4.7 VPS plan approved and run.
- [ ] Try a real Phantom connect (steps in the M1 report) — optional.
- [x] Register for the event on Luma.
- [x] Domain: none for now, so use sslip.io hostnames (verified to resolve to the VPS).
- [x] Hostinger: the CLI is signed in; the `ovelo-vps-admin` key is attached and matches `~/.ssh/ovelo_vps_ed25519`.
- [x] Create a devnet wallet and fund it.
- [ ] Recruit 8–12 playtesters for Nov 25 and Nov 26.

## Blockers
- None. M2b is waiting for your "continue" at the M2a checkpoint.

## Known bugs
- None open in our code. Upstream: Solana.Unity-SDK v1.2.9 WebGL link failure (#257), worked around by D-15.

## Devnet proof signatures
- Program deploy: `zQzTyAr95iipfmHHaBc51TogPtXxkDe85A8E3k3Z8jebsZshE6Z2AiYvnKTsiXsmxLtxxNAXR6Hq1g8XNsSXmzf` (devnet).
- Lifecycle run: all 22 steps with signatures in `docs/proof/M0-devnet.md` (match `JCaMhk5jYaEoe1Su4nrNR9kP49g33BxKFcxC8FeFGBpy`; VRF callback `4iwqeoGX…`; round-1 commit on Solana `22iX4EiL…`; undelegate on Solana `2VLBsaMi…`).

## Toolchain
| Tool | Version | Note |
|---|---|---|
| node | 24.21 | |
| pnpm | 10.18.3 | |
| rustc | 1.96.1 | |
| solana-cli | 3.1.10 | |
| anchor-cli | 1.0.2 (project, via `[toolchain]`) / 1.2.1 (global default) | 1.0.2 built from source, see docs/07 §9 |
| ephemeral-validator | 0.14.10 | global npm (mb-stack, vrf-oracle) |
| docker | 29.2 | |
| Unity | 6000.3.28f1 (`8f082d2fe476`) | + WebGL, Linux Mono, Linux Server modules; `unity` CLI 1.0.0 at `~/.unity/bin/unity` |
| nginx (local edge) | 1.24.0 (Docker) | matches the VPS line |
