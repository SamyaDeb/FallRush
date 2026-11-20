# M1 proof: Unity WebGL ↔ game server over WebSockets (2026-11-18)

**Machine:** MacBook Air M2, 8 GB RAM, macOS (Darwin 25.4). Docker Desktop VM ≈ 3.8 GB. Unity **6000.3.28f1** (`8f082d2fe476`). Browser: Chrome 157 driven by Playwright (one browser, ≤ 2 game tabs).
**Raw logs:** `.m1-logs/` (git-ignored). **Screenshots:** `docs/proof/m1/`.

> Words used precisely: **measured** = a number from a run listed here. **UNVERIFIED** = not run.

## 0. Important deviation: the local game server

The Linux x86_64 **Mono** dedicated server (`pnpm m1:build:server`, image `fallrush-gameserver:m1`) **crashes at start-up in local Docker on Apple Silicon**, under both Rosetta and QEMU emulation:

```
* Assertion at ../../mono/arch/amd64/../x86/x86-codegen.h:410, condition `offset == (gint32)offset' not met
Caught fatal signal - signo:6
```

Mono's x86-64 JIT assumes code and trampolines are within ±2 GB; the emulators' address layout breaks that (also with `MONO_ENV_OPTIONS=--interpreter`). This is an emulation artefact, not a code bug.

**Update:** the same image then ran fine on the real x86_64 VPS (§2b), so the crash is emulation-only.

With your approval, every local browser test below ran against a **native macOS dev server**: the same scenes and code (`NetBootstrap`, UTP 2.7.5 WebSocket server, approval, ping), built with `pnpm m1:build:macserver` (macOS player, Mono, arm64) and started headless with `-batchmode -nographics -fallrushServer -fallrushListen 127.0.0.1`. nginx stayed in Docker and proxied to `host.docker.internal:7777` (`pnpm m1:up:mac`). CPU/RAM numbers are therefore **Mac-native**, not VPS numbers.

## 1. Tests (§4.5)

| # | Test | Result | Evidence |
|---|---|---|---|
| 1 | Plain `ws`: 2 tabs at `http://localhost:8080/?server=ws://localhost:7777` | **PASS** — both in Lobby (client ids 1, 2; "Players connected: 2 / 12"); server: `connect client=1 total=1`, `connect client=2 total=2`; RTT shown | `m1/ws-tab1-lobby.png`, `m1/ws-tab2-lobby.png` |
| 2 | `wss` via nginx TLS, same origin: 2 tabs at `https://localhost:8443/?server=wss://localhost:8443` | **PASS** — both in Lobby (ids 4, 5; 2/12); Playwright saw `wss://localhost:8443/` with frames both ways; nginx access log: `"GET / HTTP/1.1" 101 … Chrome/157` for both tabs. Also `curl` upgrades → `101 Switching Protocols` on `:7777` (direct), `:8443` (same origin) and `:8444` (WS-only block) | `m1/wss-tab1-lobby.png`, `m1/wss-tab2-lobby.png`, `.m1-logs/nginx-101.txt` |
| 3 | Protocol mismatch (dev build, `?proto=999` dev-only hook) | **PASS** — server `reject client=1 reason="Protocol mismatch: server v1, client v999. Reload the page to get the current build."`; the client shows that text | `m1/protocol-mismatch-rejected.png` |
| 4 | Server restart with 2 `wss` clients (stop/start of the dev server process — the equivalent of `docker restart` here) | **PASS** — both show "Disconnected" + NGO reason (`ClosedByRemote`); "Reconnect" brings both back (new ids 1, 2; 2/12) **without a page reload** (same URL, one navigation entry) | `m1/restart-disconnected.png`, `m1/restart-reconnected.png` |
| 5 | Idle soak, 2 `wss` clients, 10 min (09:46:39 → 09:56:40 UTC) | **PASS** — no disconnects; `clients=2` in all 61 ten-second stats windows | `.m1-logs/soak-ps.txt`, `.m1-logs/macserver-tests1-5.log` |
| — | Brotli serving | **PASS** — `curl`: `.wasm.br` → `Content-Type: application/wasm`, `.js.br` → `application/javascript`, `.data.br` → `application/octet-stream`, all with `Content-Encoding: br`; the release build loaded in Chrome in **1.1 s** (localhost) | §2 |
| — | Wallet button in WebGL | **PASS (adapter UI)** — the SDK's wallet picker (Phantom, Solflare) opens, no console errors, main loop keeps running (30 rAF frames in 3 s). **Real Phantom connect: UNVERIFIED** (no wallet extension in the test browser) | `m1/wallet-adapter-ui.png` |

**Unexplained event (not reproduced):** between tests 1 and 2 the server logged `disconnect client=2` followed by a new `connect client=3`. It happened while the harness was switching tabs; a deliberate 45 s background test afterwards did not disconnect. Hidden-tab behaviour (Unity WebGL stops its `requestAnimationFrame` loop in hidden tabs, which could trip UTP's 30 s disconnect timeout) is **UNVERIFIED** and is an M2 item.

## 2. Measurements

| Metric | Value | Method |
|---|---|---|
| WebGL **release** build, first (clean WebGL Bee cache, after switching from Linux) | **522.6 s** build / 591.6 s wall | `[build] result` + `/usr/bin/time -l` (`.m1-logs/webgl-release-20261118-133242.*`) |
| WebGL **release** build, incremental (1 script change) | **294.2 s** build / 370.1 s wall | `webgl-release-20261118-134915` |
| WebGL **release** build with SDK added (package + TMP import since last build) | 641.1 s build / 766.7 s wall | `webgl-release-20261118-141513` |
| WebGL **dev** build, first (incl. switch from Linux) | **714.3 s** build / 827.9 s wall | `webgl-dev-20261118-122231` |
| WebGL **dev** build, warm Library (1 script change since the last release build; SDK added since the last dev build) | 238.2 s build / 262.2 s wall | `webgl-dev-20261118-152731` |
| Release size **without SDK** | **11.83 MB** total: `WebGL.wasm.br` 7.71 MB, `WebGL.data.br` 4.00 MB, `framework.js.br` 76 KB, `loader.js` 27 KB | `ls -l`, `[build] outDirBytes` |
| Release size **with SDK** | **15.83 MB** total: `wasm.br` 10.00 MB, `data.br` 5.70 MB, `framework.js.br` 83 KB, `loader.js` 27 KB | same |
| **SDK size delta** (release, compressed) | **+4.00 MB** (+34 %): wasm +2.29 MB, data +1.70 MB | difference of the two rows |
| Dev build size (uncompressed by design) | 137.7 MB with SDK (`wasm` 120.8 MB), 107.0 MB without | Unity 6000.3 manual: "Development builds aren't compressed" |
| Peak memory during WebGL builds | **1.10–1.53 GB** max RSS (largest single process) | `time -l` "maximum resident set size" |
| Swap during builds | **Yes** — machine-wide swap grew 1.2–3 GB during WebGL builds (8.0 → 11.0 GB used during the first dev build) while other apps were open; after closing apps: 2.1 → 3.8 GB | `sysctl vm.swapusage` before/after |
| Linux server build | 157.9 s first (545 s wall incl. target switch), 47–62 s incremental | `server-*.time` |
| Server build output | 89.8 MB without SDK, 98.8 MB with SDK | `outDirBytes` |
| Server Docker image | **68 MB** compressed content (`docker image inspect`), **262 MB** unpacked (`docker images`) with SDK; 64 MB / 250 MB without | containerd image store reports both |
| Page load (release, localhost, Brotli) | 1.1 s to loading-bar hidden | Playwright timer |
| Browser memory in Lobby (dev build) | wasm heap **95.8 MB** (max configured 2048 MB); JS heap 17.5 MB used / 20.8 MB total | `WebAssembly.Memory` hook + CDP `Performance.getMetrics` |
| RTT `ws` (localhost) | p50 **32–33 ms**, p95 34–50 ms (n=16, 2 tabs) | in-game ping/pong, 500 ms interval |
| RTT `wss` via nginx (localhost) | p50 **33 ms**, p95 49–50 ms (n=28–60) | same |
| RTT VPS (Mumbai, `wss://gs1.<VPS_IP>.sslip.io`, Let's Encrypt via nginx 1.24) | p50 **66 ms**, p95 **83 ms** (n=60, both tabs); ICMP ping 37.9 ms avg | in-game ping/pong; `ping -c 5` |
| Server tick | **30.0 Hz** (all windows) | `NetworkTickSystem.Tick` count per 10 s |
| Server frame interval | p50 33.33 ms, worst p95 34.4 ms | `Time.unscaledDeltaTime`, `targetFrameRate = 30` |
| Server frame **work** time (0 / 2 clients) | p95 0.54–0.77 ms; worst p95 in the soak 2.27 ms; worst single frame 13.0 ms | PlayerLoop markers (Initialization start → PostLateUpdate end) |
| Server process, 2 clients idle (Mac native) | CPU avg **1.9 %** (max 2.3 %), RSS ≈ 35 MB | `ps -o %cpu,rss` every 30 s for 10 min |
| **VPS container** (`--cpus 0.75 --memory 1g`), 0 clients | CPU 1.5–1.7 %, memory **84 MB**; tick 30.0 Hz; work p95 **0.40 ms**, max 0.74 ms | `docker stats --no-stream` ×3, `[gs] stats` |
| **VPS container**, 2 clients | CPU 1.6 %, memory **86 MB**; tick 30.0 Hz; work p95 **0.55–0.60 ms**, max 2.05 ms; frame-interval p95 33.4 ms | same |
| Image transfer to VPS | 27 s (`docker save \| gzip -1 \| ssh docker load`) | wall clock |

**Note on the localhost RTT (≈ 33 ms):** that is one 30 Hz frame. Inference, not separately measured: sends are flushed on frame/tick boundaries and the server is capped at 30 fps, so ping RTT carries up to 1–2 frames of quantisation on top of network time.

## 2b. Hostinger VPS edge check (§4.7, approved 2026-11-18) — **PASS**

| Step | Result |
|---|---|
| Read-only check | Docker 29.9.3, certbot 2.9.0, nginx 1.24.0, ufw active (22/80/443), 93 GB free, 6.4 GB RAM available. One existing nginx site (`finity`, proxies to `127.0.0.1:8787` and `:3100`); Ovelo runs in Docker behind cloudflared (not in nginx). Baseline: existing site `https 200`, `http 301` |
| Snapshot | Created and confirmed (`state: success`). **Hostinger snapshots auto-expire after 24 h** (expires 2026-11-19 10:15 UTC) |
| Image | `fallrush-gameserver:m1` loaded and run: `--restart unless-stopped -p 127.0.0.1:7777:7777 --cpus 0.75 --memory 1g`. **The Linux x86_64 Mono server runs on real hardware** (container `healthy`, `[gs] listening`); no apt packages beyond `ca-certificates` needed |
| nginx | One new file `sites-available/fallrush-gs1` + symlink (WebSocket proxy to `127.0.0.1:7777`, unique `map` variable name), `nginx -t` OK, `reload` (never restart) |
| Let's Encrypt | `certbot --nginx -d gs1.<VPS_IP>.sslip.io` **succeeded** (sslip.io works for issuance); `nginx -t` + reload OK; `curl` upgrade over real TLS → `101 Switching Protocols` |
| Browser | 2 Chrome tabs at `http://localhost:8080/?server=wss://gs1.<VPS_IP>.sslip.io` → both Lobby, client ids 1/2, 2/12, RTT p50 66 / p95 83 ms (`m1/vps-wss-tab1-lobby.png`, `m1/vps-wss-tab2-lobby.png`, hostname redacted) |
| Existing site unchanged | `https 200` / `http 301` before, after reload and after removal; `finity` config md5 unchanged; Ovelo containers up throughout |
| Removal (your instruction) | `docker rm -f fallrush-gs1`, nginx file + symlink removed, `nginx -t`, reload. **Kept:** snapshot, the `gs1` certificate (renewal will fail without its server block — delete with `certbot delete --cert-name gs1.<VPS_IP>.sslip.io` when you say), the image on the VPS |
| Firewall | Not touched |

**VPS RTT vs ping:** 66 ms vs 38 ms ICMP — the ≈ 28 ms difference matches the ≈ 33 ms one-frame quantisation seen on localhost.

## 3. Solana.Unity-SDK (R5)

- **Tag v1.2.9 (`3d139411`) breaks the WebGL build** on 6000.3:
  `Fatal error in Unity CIL Linker — Mono.Cecil.AssemblyResolutionException: Failed to resolve assembly: 'BouncyCastle.Cryptography, Version=2.0.0.0'`.
  The SDK ships the assembly `BouncyCastle.Cryptography` 2.0.0.0 in a file named `BouncyCastle.Crypto.dll`; `Solana.Unity.Wallet/KeyStore/Dex` reference it by assembly name and the IL2CPP linker resolves by file name. Upstream issue **#257** (open). Read with `dnfile` from the DLL metadata.
- **Upstream `main` commit `1da8fd7f`** (2025-10-20, "FIX asmdef issues and bouncy castle dll errors"; v1.2.9 + 2 commits: DLL rename, three broken asmdefs removed, docs) **fixes it**: WebGL release ✅, Linux server ✅ (0 errors), EditMode 52/52 ✅. Pin **approved** (D-15 ✅, 2026-11-18).
- At runtime the WebGL adapter loads `https://cdn.jsdelivr.net/npm/@magicblock-labs/unity-wallet-adapter@1.2.1` (third-party script). Consider self-hosting it in M5.
- The SDK copies prefabs into `Assets/Resources/SolanaUnitySDK/` and a template into `Assets/WebGLTemplates/xNFT` (our template stays `Default`). `Resources/` ships in the server build too.

## 4. How to reproduce

```bash
pnpm m1:test:unity            # EditMode tests (Editor closed)
pnpm m1:build:webgl:release   # or m1:build:webgl for a dev build
pnpm m1:build:macserver       # Apple Silicon local server (see §0)
pnpm m1:up:mac                # Mac server on 127.0.0.1:7777 + nginx :8080/:8443/:8444
# http://localhost:8080/?server=ws://localhost:7777
# https://localhost:8443/?server=wss://localhost:8443   (self-signed: accept once)
pnpm m1:down
# On an x86_64 Linux host instead: pnpm m1:build:server && pnpm m1:up
```
