You are the senior Unity multiplayer engineer on **Fall Rush**. You also know Solana and MagicBlock well. Fall Rush is an original 12-player knockout party game for the MagicBlock Solana Blitz V10 hackathon, and the deadline is **2026-11-27 14:00 UTC**.

Your task is **Milestone M1: Unity foundation + WebGL ↔ Linux server WebSocket spike**. Do M1 only.
- Do not touch `programs/` or `packages/shared/` logic. M0 is done and verified.
- Do not start the backend service (`server/`) or the web app (`web/`).
- Do not write movement, prediction or hazards. That is M2/M3.

M1 exists to answer three risk questions on day 1, before we build gameplay on top:
1. Does a Unity 6.3 **WebGL** client in Chrome connect over **WebSockets** to a **Linux x86_64 dedicated server** running in Docker? We test plain `ws`, and `wss` through a TLS-terminating proxy (risk R3).
2. Does **Solana.Unity-SDK v1.2.9** compile and work in a Unity 6.3 WebGL build without breaking the server build (risk R5)?
3. How long does a WebGL build take on an 8 GB MacBook Air M2, how big is it, and how much memory does it use (risk R6)?

## 0. Context: read these first, fully

The repo is at `/Users/samya/Downloads/FallRush`. It is a git repo on `main` with M0 commits.

| Doc | What it gives you |
|---|---|
| `docs/00-README.md` | Index and the rules you cannot break |
| `docs/04-UNITY-CLIENT.md` | **Primary spec.** Versions, transport, folder layout, messages, builds, wallet, tests |
| `docs/03-TECHNICAL-ARCHITECTURE.md` §4 | Monorepo layout (`client-unity/`, `infra/`) |
| `docs/13-DEPLOYMENT.md` | Hosting plan (Hostinger VPS, Docker Compose behind the VPS's existing nginx), env vars |
| `docs/14-IMPLEMENTATION-PLAN.md` §2 "M1" | Scope and exit criteria |
| `docs/15-DECISIONS-AND-RISKS.md` | Decisions D-1, D-4, D-5, D-8; risks R3, R5, R6 |
| `docs/17-ART-DIRECTION.md` §5 | Only for the palette in placeholder UI. No art work in M1 |
| `docs/11-SECURITY-ANTI-CHEAT.md` | Keys and trust wording |
| `PROJECT_STATUS.md` | Current status. You will update it |

`fall-rush-docs/` is the frozen v1 spec. Where it conflicts with `docs/`, ignore it.

## 1. Preconditions: check these first, and stop if any fails

1. Unity **6000.3.28f1** is installed with **Web Build Support**, **Linux Dedicated Server Build Support** and **Linux Build Support (Mono)**.
   - Check that `/Applications/Unity/Hub/Editor/6000.3.28f1/` exists and that its `PlaybackEngines/` contains `WebGLSupport` and `LinuxStandaloneSupport`.
   - Check that a Unity license (Personal is fine) is activated, because batch-mode builds need it.
   - The editor's changeset is **`8f082d2fe476`** (Unity release API, LTS, released 2026-11-03).
   - Unity Hub 4.0.1 is installed. Its old `--headless` CLI is gone; Unity's replacement is the standalone `unity` CLI. Use whichever is installed. Never run an install script without me.
   - If either is missing, stop. Tell me exactly what to install, and do nothing else.
2. Docker Desktop is running.
   - `docker run --rm --platform linux/amd64 ubuntu:22.04 uname -m` must print `x86_64`. This was verified on this machine on 2026-11-18.
   - The Docker VM has about 3.8 GB of memory.
3. `git status` shows no unexpected changes. Untracked logs, `docs/prompts/` and `PROJECT_STATUS.md` edits are fine; commit those first as `docs: M1 prompt`.

## 2. Non-negotiable rules

1. **Never invent** Unity, NGO, UTP or Solana.Unity-SDK APIs, package versions, Unity CLI flags, or env vars.
   - Sources of truth:
     - the **package source** in `client-unity/Library/PackageCache/` after install;
     - Unity's official docs for 6000.3;
     - the Solana.Unity-SDK repository at tag `v1.2.9` (clone it into the scratchpad, not the repo).
   - Specifically, read the `UnityTransport` source for:
     - `UseWebSockets`
     - `UseEncryption`
     - `SetConnectionData`
     - `SetClientSecureParameters`
     - WebSocket path support

     Don't rely on memory for any of these.
   - If reality differs from doc 04 or doc 13, **follow reality and fix the doc in the same commit**, noting the correction.
2. **Pinned versions (approved, doc 04 §1):**

   | Package | Version |
   |---|---|
   | Unity Editor | 6000.3.28f1 |
   | com.unity.netcode.gameobjects | **2.14.0** (not 3.x) |
   | com.unity.transport | resolved by NGO, ≥ 2.6.0. After the spike, **pin it explicitly** (prefer 2.7.5 if it works) and record why |
   | com.unity.dedicated-server | **2.0.2** |
   | com.unity.multiplayer.playmode | **2.0.2** (on 8 GB, at most 1 virtual player) |
   | com.unity.cinemachine | **3.1.7** |
   | com.unity.inputsystem, com.unity.render-pipelines.universal | the versions bundled with 6000.3; pin what resolves |
   | com.solana.unity_sdk | `https://github.com/magicblock-labs/Solana.Unity-SDK.git#v1.2.9` (tag verified to exist, commit `3d139411`) |

   - Commit `Packages/manifest.json` **and** `Packages/packages-lock.json`.
   - Use no floating versions and no preview packages, except where doc 04 already names one.
   - **The server uses the Mono backend.** Don't install the IL2CPP Linux cross-toolchain.
3. **Env vars and protocol.** Only these are new in M1. Add them to `.env.example` and to doc 13 §3:
   - `GAMESERVER_PORT` (default 7777)
   - `GAMESERVER_ID`, already in doc 13.

   Protocol version starts at `1` and lives in `Scripts/Shared/Protocol.cs`.
4. **Trust and wording:** don't claim something works until a run proves it. Label anything unproven as UNVERIFIED. Write "measured" only for numbers you measured.
5. **8 GB machine:**
   - Never run the Unity Editor, a batch-mode build and the Docker stack at the same time.
   - Close the Editor before batch-mode builds, and use **one** browser window with at most 2–3 tabs for tests.
   - Don't run the MagicBlock local stack in M1.
6. **Git:**
   - Before the first Unity commit, write a Unity `.gitignore` for `client-unity/`. It must cover `Library/`, `Temp/`, `Obj/`, `Logs/`, `UserSettings/`, `Builds/`, `*.csproj`, `*.sln` and `.vs/`.
   - Use **Force Text** serialization and **Visible Meta Files**.
   - Never commit build output.
   - Make small local commits at logical checkpoints. **Never push, and never add a remote.**
7. **Outward-facing actions:** ask me before every one, and wait for an explicit yes. This covers:
   - any global install (for example `mkcert` or `nginx` on the Mac; run nginx in Docker instead);
   - anything that changes the Hostinger VPS: SSH writes, Docker, nginx, certbot, Hostinger CLI write commands (see §4.7). Read-only Hostinger CLI commands (`list`, `get`) are fine at any time.
   - deploying anything public.
8. **Originality:** placeholder UI uses our palette and plain shapes only. Don't use any Fall Guys assets, fonts or UI copies.

## 3. Workflow

1. **Before coding**, give me a short plan with:
   - the files you will create;
   - the package versions that actually resolve;
   - what the UTP source says about WebSocket + TLS on server and client, including whether a URL path is supported;
   - any discrepancy you found with doc 04.

   Then proceed without waiting for approval. M1 is approved, except §4.7 (Hostinger VPS), which needs my yes.
2. Implement in this order:
   1. project + packages
   2. assemblies + scenes
   3. `NetBootstrap` + ping
   4. `Build.cs`
   5. Docker + nginx
   6. browser tests
   7. Solana.Unity-SDK smoke test
   8. measurements
   9. docs
3. Run every build and test yourself. **Fix the errors; don't hide them.** Never weaken a check to get green.
4. **Time-box:** if one blocker eats more than ~2 hours, stop and report:
   - the exact error and logs;
   - what you tried;
   - the options.

   Typical blockers: `wss` through the proxy never upgrades; the WebGL build OOMs; the SDK breaks the build. The fallbacks are in doc 04 §2 (UTP built-in secure WebSocket with certificates) and §8 (web wallet-link page). Let me decide.

## 4. Deliverables

### 4.1 Unity project `client-unity/`

- Create a **URP** project for Unity 6000.3.28f1 at `client-unity/`. Use the 3D URP template, or an empty project plus URP set up by hand.
- Use the folder layout from doc 04 §3 under `Assets/_Game/`. Create only the folders M1 needs, plus `.gitkeep` where useful.
- **Assembly definitions** (keep them small and explicit):

  | Assembly | Contents and settings |
  |---|---|
  | `FallRush.Shared` | `Scripts/Shared/`, **No Engine References** on. M1 puts only `Protocol.cs` here: version constant, message names, and `hello` encode/decode on plain byte spans/arrays |
  | `FallRush.Net` | `Scripts/Net/`. References NGO, UTP and Shared |
  | `FallRush.Client` | `Scripts/Client/`. Excluded from the server build: use the `UNITY_SERVER` define and/or Multiplayer Roles, whichever the dedicated-server 2.0.2 docs say works |
  | `FallRush.Server` | `Scripts/Server/`. Server-only |
  | `FallRush.Client.Wallet` | `Scripts/Client/Wallet/`. **The only** assembly that references Solana.Unity-SDK. Limit it to WebGL + Editor platforms so the SDK code never compiles into the server's game code |
  | `FallRush.Tests.EditMode` | EditMode tests |

- **Scenes** (placeholder, flat colours from our palette):
  - `Boot`: picks the role. A dedicated-server build goes straight to listen. A client goes to Menu.
  - `Menu`: "Play (connect)" plus a "Connect Wallet (smoke test)" button.
  - `Lobby`: shows connection state, my client id, connected player count, and live RTT p50/p95.
- **Player settings:**
  - Company and product name: Fall Rush.
  - WebGL: Brotli compression; **Decompression Fallback off** if nginx serves the correct `Content-Encoding` headers (preferred), otherwise on, and document which.
  - WebGL: Exceptions "Explicitly Thrown" for dev builds, "None" for release; code stripping Medium; memory settings left at defaults unless measured otherwise.
  - Run In Background on.
  - NGO: `NetworkConfig.TickRate = 30`.

### 4.2 `NetBootstrap` + ping protocol (`Scripts/Net/`)

- **One `NetworkManager` + `UnityTransport` prefab** in Boot, with `UseWebSockets = true` on both server and client builds.
- **Server role:**
  - Listen on `0.0.0.0`, on `GAMESERVER_PORT`, else 7777.
  - Plain `ws` only. TLS terminates at the proxy (doc 04 §2).
  - Log one line per connect/disconnect with the client id and the total count.
  - Log tick rate and frame-time p50/p95 once every 10 s, with no allocations in the hot path.
- **Client role.** Connection target, resolved in this order:
  1. query string on `Application.absoluteURL`: `?server=ws://host:port` or `?server=wss://host[:port]`;
  2. a default in a `ScriptableObject` config;
  3. the Editor/desktop dev default `ws://127.0.0.1:7777`.

  `wss` sets the client to encrypted WebSocket. Use whatever API the UTP source says is correct for a WebGL client, where the browser does TLS.
- **Connection approval:**
  - The `hello` payload is `protocolVersion u16`, `buildVersion string`, `cosmeticPreset u8`.
  - Reject on a protocol mismatch, with a readable reason shown on the client.
  - Reject when full; the cap is 12 for now.
- **Ping:** a named custom message pair `ping`/`pong` (client timestamp echoed by the server) every 500 ms. The Lobby shows RTT p50/p95 over the last 60 samples.
  - This is the only custom message in M1.
  - Use `CustomMessagingManager.SendNamedMessage` with `FastBufferWriter`, as in doc 04 §4.
- **Disconnect / reconnect:** when the server goes away, the client shows "Disconnected" plus a "Reconnect" button that works without reloading the page.

### 4.3 Builds: `Assets/_Game/Editor/Build.cs`

- `Build.WebGL` (development and release variants, chosen by a `-fallrushRelease` arg or similar) writes to `client-unity/Builds/WebGL/`.
- `Build.Server` writes to `client-unity/Builds/Server/` as **Linux64 + Dedicated Server subtarget, Mono**.
- Both build only the scenes in a fixed list, and exit non-zero on failure.
- Add root scripts that run Unity in batch mode with the Editor closed: `pnpm m1:build:webgl` and `pnpm m1:build:server`.
  - Base them on the command in doc 04 §10.
  - Check `-standaloneBuildSubtarget Server` against Unity 6000.3 docs.
  - Each run writes a log to `.m1-logs/` (gitignored).
  - Wrap each build in `/usr/bin/time -l` so peak memory is captured.

### 4.4 Local infra: `infra/`

- **`infra/gameserver.Dockerfile`:**
  - `--platform=linux/amd64`, `ubuntu:22.04` base (or the slimmest base the Unity Linux server runtime needs; list the apt deps you found necessary).
  - Copies `client-unity/Builds/Server/`, runs as a non-root user and exposes 7777.
  - Starts with `-batchmode -nographics -logFile -`.
  - Has a `HEALTHCHECK` if cheap; otherwise none.
- **`infra/nginx/fallrush-m1.conf`.** Production TLS runs on the VPS's **existing nginx** (doc 13 §2), so the local proxy is **nginx too**, in Docker (official `nginx` image, tag pinned to the 1.24.x line to match the VPS where available; otherwise the nearest stable tag, recorded). The same `server`-block shape is reused on the VPS in §4.7. It serves two origins:
  - **`http://localhost:8080`**: static WebGL build with correct Brotli headers (`Content-Encoding: br`, plus `Content-Type: application/wasm` for `.wasm.br`, `application/javascript` for `.js.br`). The page connects with `?server=ws://localhost:7777` straight to the container.
  - **`https://localhost:8443`** with a self-signed cert generated by `openssl` in the scratchpad (never committed; no host trust-store install). The same static build is served. **WebSocket upgrade requests are proxied** to `gameserver:7777`. Use `proxy_http_version 1.1`, `Upgrade`/`Connection "upgrade"` headers and a long `proxy_read_timeout`, from nginx's own WebSocket proxying docs. So the page connects with `?server=wss://localhost:8443` to the **same origin**: one cert prompt in Chrome, and no URL path needed. If routing by the upgrade header on `/` is awkward in nginx, use a separate server block on `https://localhost:8444` for the game server and record that choice. That is how production works anyway, with one subdomain per game server.
  - If UTP cannot connect this way, record exactly why and try the doc 04 §2 fallback.
- **`infra/docker-compose.m1.yml`:** `gameserver` + `nginx`, with the WebGL build mounted read-only. Add `pnpm m1:up` / `pnpm m1:down`.

### 4.5 Browser tests (do them yourself; use Playwright/Chrome if available, otherwise give me exact steps and I'll click)

1. **Plain `ws`:** 2 tabs at `http://localhost:8080/?server=ws://localhost:7777` both reach Lobby, the server logs 2 connections, and RTT is shown.
2. **`wss` via proxy:** 2 tabs at `https://localhost:8443/?server=wss://localhost:8443`, with the same checks. Capture the Chrome DevTools WS frame view or nginx access logs that show the upgrade (101).
3. **Protocol mismatch:** a dev build with protocol version 999 is rejected, and the reason is visible.
4. **Server restart:** `docker restart` the gameserver. Clients show Disconnected, then Reconnect works.
5. **Idle soak:** 2 clients connected for 10 minutes with no disconnects. Record the server container's CPU % and RSS (`docker stats`) and the tick frame-time p95.

Save screenshots to `docs/proof/m1/` (small PNGs only).

### 4.6 Solana.Unity-SDK smoke test (`FallRush.Client.Wallet`)

- Read the SDK's own WebGL sample and docs at tag v1.2.9 first. Use only the APIs they show: the `Web3` component and its login method for the WebGL web wallet adapter.
- **"Connect Wallet (smoke test)"** button: on WebGL it opens Phantom/Solflare through the SDK adapter and, on success, shows the public key. Set the network to **devnet**.
  - Don't send any transaction, sign anything, or request airdrops.
  - Signing a nonce is M5.
- **Pass criteria:**
  1. the WebGL build compiles with the SDK;
  2. the **server build still compiles and runs** with the SDK in the project;
  3. the WebGL build size delta with and without the SDK is recorded;
  4. **UNVERIFIED unless done:** a real Phantom connect in Chrome. I'll do this if you can't; give me the steps.
- **If the SDK breaks the build or freezes WebGL:** isolate it (remove the package), record the error, and recommend the doc 04 §8 fallback: a "Link wallet" button that opens the web page with a one-time code. Don't spend more than the time-box on it.

### 4.7 Hostinger VPS edge check (**needs my explicit yes first**)

**What is already verified (2026-11-18):**
- **The VPS:** my existing Hostinger **KVM 2** (2 vCPU, 8 GB, 100 GB, Ubuntu 24.04, data centre Mumbai). Get its ID, IP and hostname with `hostinger vps virtual-machines list --format json`. **Never write the IP, VM ID or any key into a committed file.** This repo becomes public; use placeholders like `<VPS_IP>` in docs.
- **It already runs another project (Ovelo) behind nginx 1.24 on 80/443.** That project is not ours: never edit, restart, stop or remove anything of it.
- **Hostinger CLI:** use `hostinger` (`~/.local/bin/hostinger`, already signed in through OAuth; check with `hostinger auth status`) for everything the API offers:
  - VM info: `vps virtual-machines`
  - backups: `vps snapshots` (the VM has **no** snapshot today)
  - SSH keys: `vps public-keys`
  - firewall: `vps firewall` (**no** Hostinger firewall group exists today)
  - Run `--help` on each command before using it. Use `--dry-run` to preview write requests.
- **Hostinger Docker Manager (`vps docker …`) does not work on this VM.** The API returns `[VPS:2044] Currently installed operating system does not support Docker Manager`. So containers are deployed over **SSH**.
- **SSH:** the Hostinger key `ovelo-vps-admin` is attached to the VM and matches the local key `~/.ssh/ovelo_vps_ed25519`. Use `ssh -i ~/.ssh/ovelo_vps_ed25519 root@<VPS_IP>`, unless `~/.ssh/config` already has a host entry for it. Don't create or attach new keys without asking.
- **Domain:** I have **no domain**. Use **sslip.io** hostnames that embed the IP, e.g. `gs1.<VPS-IP-with-dashes>.sslip.io`. DNS was verified to resolve to the VPS. Let's Encrypt issuance for it is UNVERIFIED until certbot succeeds.
- Ping from this Mac to the VPS is about 45 ms.

**Only after §4.5 passes locally, ask me.** One message with the exact plan (every command that writes) and an approval request. If I say yes:
1. **Read first, change nothing:**
   - `ssh … 'nginx -T; docker --version; docker ps; ufw status; df -h; free -m'`
   - the Ovelo site's HTTP status from here (`curl -sI https://<vps-hostname>`).

   Report all of it. **If Docker isn't installed, stop and ask** before installing it (official Docker apt repo for Ubuntu 24.04).
2. **Safety net:** `hostinger vps snapshots create <vm-id>` (preview it with `--dry-run` first). Wait until it is done, and confirm with `snapshots get`.
3. **Ship the image:** `docker save fallrush-gameserver:m1 | ssh … docker load`, then run it:

   ```
   docker run -d --name fallrush-gs1 --restart unless-stopped -p 127.0.0.1:7777:7777 --cpus 0.75 --memory 1g fallrush-gameserver:m1
   ```

   It binds to **localhost only**.
4. **nginx:** add **one new** file, `/etc/nginx/sites-available/fallrush-gs1` (plus its symlink in `sites-enabled`):
   - `server_name gs1.<VPS-IP-with-dashes>.sslip.io;`
   - WebSocket proxying to `127.0.0.1:7777`, the same block as tested locally in §4.4.

   Then:
   - `nginx -t`. If it fails, remove your file and stop.
   - `systemctl reload nginx`, **never restart**.
   - `certbot --nginx -d gs1.<…>.sslip.io` for that one name only. If certbot isn't installed, ask first.
   - `nginx -t` and reload again.
5. **Don't touch firewalls.** Creating or activating a Hostinger firewall group could block the Ovelo site. Port 7777 is not exposed because it binds to localhost.
6. **Test:** the WebGL page served locally, `http://localhost:8080/?server=wss://gs1.<…>.sslip.io`, with 2 tabs.
7. **Record:**
   - whether the WebSocket upgrade works through nginx TLS to UTP;
   - RTT p50/p95 from here;
   - container CPU/RSS idle and with 2 clients (`docker stats --no-stream`);
   - server tick frame-time p95;
   - the Ovelo site returning the same status as before.

Then leave it running or remove it, as I instruct. Removal means:
- `docker rm -f fallrush-gs1`
- remove your nginx file + symlink, run `nginx -t`, reload
- `certbot delete --cert-name …` if I ask

Keep the snapshot until I say to delete it. If I say no to §4.7, mark the VPS half of R3 as UNVERIFIED in docs 13 and 15.

### 4.8 Tests

EditMode (run in batch mode with `-runTests -testPlatform EditMode`; check the flags against Unity docs; add `pnpm m1:test:unity`):
- `Protocol` `hello` encode/decode round-trip, including max-length and truncated-buffer rejection.
- Server-URL resolution (query string → config → default) as a pure function in Shared or Net.
- Port resolution order (`-port` arg → `GAMESERVER_PORT` → 7777), pure function.

### 4.9 Measurements → `docs/proof/M1-transport.md`

Write a table of **measured** values, each with the method used:
- WebGL build time: clean (first) and incremental (one script change), dev and release.
- WebGL build size: compressed total, plus the largest files.
- Peak memory during the WebGL build (`time -l` max RSS) and whether swap was used.
- Server build time and image size.
- RTT p50/p95 for `ws` vs `wss` locally (and the VPS, if §4.7 ran).
- Server container idle CPU % and RSS with 0 and 2 clients; tick frame-time p95.
- SDK size delta.

Also list each test in §4.5 with pass/fail and evidence links.

## 5. Docs to update in the same change

- **doc 04:**
  - §1: the versions that actually resolved, with UTP pinned.
  - §2: what was verified about TLS/proxy, and the exact client API used for `wss`.
  - §8: SDK result.
  - §10: real build commands, times and the Brotli serving decision.
- **doc 13:** §2 (VPS result or UNVERIFIED) and §3 (new env vars).
- **doc 15:** update R3, R5 and R6 with measured status, and note any decision change as 🟡 for me to approve.
- **`PROJECT_STATUS.md`:** M1 done or blocked, the proof link, open issues, and notes for M2 (for example the frame-time budget, WebGL memory headroom, and anything about `UnreliableSequenced` over WS).
- **`README.md`:** an "M1: run it" section with exact commands.

## 6. Definition of done (report each item with evidence)

- [ ] Unity project opens; packages pinned; `manifest.json` + `packages-lock.json` committed
- [ ] `pnpm m1:build:server` and `pnpm m1:build:webgl` succeed from a closed-Editor batch run (logs in `.m1-logs/`)
- [ ] `pnpm m1:test:unity` EditMode tests green (counts)
- [ ] Plain `ws`: 2 browser clients in Lobby on the Docker server
- [ ] `wss` through nginx TLS (local Docker): 2 browser clients in Lobby
- [ ] Protocol-mismatch rejection and reconnect after a server restart both work
- [ ] 10-minute idle soak: no disconnects; CPU/RSS recorded
- [ ] Solana.Unity-SDK: WebGL + server builds both compile with it (or the fallback is recommended with evidence)
- [ ] `docs/proof/M1-transport.md` written with measured numbers
- [ ] Docs 04 / 13 / 15, `PROJECT_STATUS.md` and `README.md` updated
- [ ] No secrets or build output in git (`git status`, plus `git ls-files client-unity | grep -E 'Library/|Builds/'` returns nothing)
- [ ] Docker stack stopped (`pnpm m1:down`); VPS container and nginx file left or removed as I instructed, and the existing site verified unchanged

## 7. Final report format

1. What was built (files)
2. Package versions that actually resolved and were pinned
3. Transport findings: `ws`, `wss` via nginx locally, Hostinger VPS (or UNVERIFIED), and the exact UTP settings used
4. Measurements table (from `docs/proof/M1-transport.md`)
5. Solana.Unity-SDK result
6. Deviations from the docs, and why
7. Risks and unknowns for M2 (prediction over TCP/WS, WebGL frame budget, memory)
8. How I can run it myself: exact commands
9. Next milestone: M2a (KinematicMotor + input + server sim + snapshots), per `docs/14`
