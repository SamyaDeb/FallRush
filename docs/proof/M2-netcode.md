# M2 proof: networked movement

> **Measured** = a number from a run listed here. **UNVERIFIED** = not run. "Feels good" is the user's call after playing; this file holds numbers only.

**Machine:** MacBook Air M2 (Mac14,2), 8 GB, macOS 26.4. Unity 6000.3.28f1. Raw logs are in `.m1-logs/` (git-ignored). Screenshots are in `docs/proof/m2/`.

---

## Part A: M2a checkpoint (2026-11-18)

### A1. Setup used for every M2a measurement

**Server:** the native **Mac dev server** (`pnpm m1:build:macserver`, D-16), headless, `127.0.0.1:7777`.
- `-fallrushBots 12` → **12 occupants**: 11 TEST bots plus 1 client. The bots run `TestBotInput` through the same sanitiser and motor.
- Map: `Map_Sandbox`.

**Client:** the same Mac build run as a **headless probe** (`scripts/m2-probe.sh`, `-fallrushProbe`).
- It goes through the real client path: `ClientSim`, `TickSync`, `in`/`snap` messages over UTP WebSockets.
- Script `run`: forward/back along Z with a hop every 1.5 s. It runs at 60 fps, like a browser.

**Sources of the numbers:**

| Source | What it gives |
|---|---|
| `[gs] stats` / `[gs] sim` lines every 10 s | server-side numbers |
| `[probe]` lines every 10 s | client-side numbers |
| `nettop -P -x -L 1 -p <server pid>` | server socket bytes, two cumulative samples about 60 s apart |
| `ps -o %cpu,rss` | CPU and memory |

**Script:** `scripts/m2-measure.sh <snapHz> <serverFps> 60 <label>`. Each config ran for about 65 s; three 10 s server windows are reported.

### A2. Server cost with 12 occupants, snapshot rate, server frame cap

**Sim step** = time inside the 30 Hz tick (12 motor steps + separation + snapshot encode/send). **Frame work** = PlayerLoop Initialization start → PostLateUpdate end, i.e. the whole frame minus the frame-cap wait.

| Config | Sim step p50 / p95 | Frame work p95 (3 windows) | Frame work max | CPU (1 core) | RSS | Tick rate |
|---|---|---|---|---|---|---|
| 15 Hz snaps, 30 fps cap | 0.03–0.23 / 0.06–0.28 ms | 0.29 / 0.33 / 1.14 ms | 1.66 ms | 1.0 % | 75 MB | 30.0 Hz |
| 30 Hz snaps, 30 fps cap | 0.14 / 0.26 ms | 0.92 / 0.99 / 1.06 ms | 2.77 ms | 2.5 % | 166 MB* | 30.0 Hz |
| **15 Hz snaps, 60 fps cap (new default)** | 0.08–0.11 / 0.14–0.20 ms | **0.55 / 0.59 / 0.69 ms** | 1.22 ms | **1.7 %** | 83 MB | 30.0 Hz |
| 30 Hz snaps, 60 fps cap | 0.13–0.14 / 0.24–0.25 ms | 0.65 / 0.69 / 0.71 ms | 2.10 ms | 3.3 % | 84 MB | 30.0 Hz |

*RSS varied between runs of the same build (76–170 MB). This is macOS memory accounting, not growth: the flood test below shows +0.3 MB.

**Reading:**
- With 12 occupants the server uses well under 1 ms of a 33 ms tick (≤ 3 %).
- These are **Mac-native** numbers. The VPS numbers (Linux x86_64 Mono, `--cpus 0.75`) come in M2b.
- In M1 the VPS frame work was close to the Mac's (0.6 ms vs 0.5–0.8 ms with 2 clients), but this has to be measured again with movement.

### A3. Bandwidth (1 client; per-client numbers scale linearly)

| Rate | Snapshot payload | Server socket out | Input payload | Server socket in |
|---|---|---|---|---|
| 15 Hz | 4.19–4.22 KB/s (280 B × 15) | **5.19 KB/s** | 1.20 KB/s (40 B × 30) | 2.93 KB/s |
| 30 Hz | 8.40–8.41 KB/s | **10.05–10.17 KB/s** | 1.20 KB/s | 2.89–2.92 KB/s |

- **Overhead:** socket bytes include NGO framing (named-message `u64` hash, message and batch headers), WebSocket framing and the 2 Hz ping/pong. That is about +60 B per snapshot and +55 B per input message.
- **Projection (inferred, not measured):** 12 humans would need about 62 KB/s out of the server at 15 Hz and about 121 KB/s at 30 Hz.

### A4. RTT and the M1 one-frame quantisation (§3.1 of the prompt)

| Server frame cap | Probe RTT p50 / p95 (localhost) |
|---|---|
| 30 fps (M1 setting) | 33.3 / 50.0 ms (15 Hz) · 17.8 / 35.9 ms (30 Hz, bimodal) |
| **60 fps** | **16.7 / 17.0 ms** (15 Hz) · 16.7 / 33.2 ms (30 Hz) |

**Decision:** the server default frame cap is now **60 fps** (`NetBootstrap.DefaultServerFps`; `-fallrushServerFps` overrides it).
- It confirms the M1 inference that ping RTT included about one server frame.
- Cost: +0.7 pp CPU at 15 Hz, frame work p95 still under 0.7 ms.
- The same change on the VPS is expected to bring its 66 ms RTT closer to the 38 ms ICMP. That is **UNVERIFIED until M2b**.

### A5. Tick sync (`TickSync`) on localhost

**Setup:** 4 × 65 s runs, with the server reporting the minimum input lead in each snapshot.

**Result:**
- **Lead:** holds at **target 2 ticks (67 ms)**. One 10 s window read 1 under 30 Hz snapshots, with no late commands.
- **Server side:** `late=0 repeated=0 neutral=0` in every window with the probe connected.

**Bug found and fixed:**
- **What happened:** in the first run the client's target ratcheted to 6 ticks.
- **Why:** the clock did not run while the map was loading, so the first commands arrived stale and each late report raised the target.
- **Fix:**
  - the clock now runs from the first snapshot;
  - big lags re-sync without raising the target;
  - the target is lowered after 10 s without dipping below it.
- **Covered by:** 3 new EditMode tests.

### A6. Input validation under flood (doc 11)

**Test:** `scripts/m2-probe.sh ws://127.0.0.1:7777 20 flood`. The client sends 200 `in` messages per frame:
- random lengths and bytes;
- every 4th one a well-formed command with tick `0x7FFFFFFF`.

**Result:**
- **Server log:** `kick client=2 slot=11: 200 input violations in a minute (far=8 flood=168 malformed=24)` in the **first tick** of the flood.
- **Client:** shows `Disconnected: too many invalid input messages.`
- **Server process:** CPU 1.6 % → 2.4 % (ps decaying average), RSS 87,296 → 87,616 KB (**+0.3 MB**).
- The 11 bots kept simulating at 30.0 Hz.
- **Not tested:** a sustained flood from many IPs. Per-IP rate limiting belongs at nginx (M6 note).

### A7. EditMode tests: 130 / 130 green

`pnpm m1:test:unity` → `.m1-logs/editmode-results.xml`. There were 52 tests in M1 and 78 are new. Highlights:
- **Jump apex:** 1.6 m ± 2 cm, on both the analytic and the PhysX world.
- **Max speed:** 7.0 m/s reached in ≤ 5 ticks, never exceeded.
- **Windows:** coyote 3 ticks, buffer 4, dive cooldown 30, jump-cancel 5, land stun 11.
- **Slopes and steps:** 44° climbs, 50° blocks; 0.2 m steps up, 0.3 m blocks.
- **Wall:** the 1.5 m wall is reachable, the 1.7 m wall is not.
- **Determinism:**
  - 1,800 random ticks twice give bit-identical states on the analytic world, and 900 on PhysX;
  - replay from recorded states reproduces the run;
  - 12-bot `SimCore` twins stay identical for 60 s.
- **Fuzz:**
  - 20,000 random buffers through both decoders: no exception, 0 bogus `snap` accepted.
  - 50,000 random commands through the sanitiser: output always clamped, unknown bits masked.

**PhysX finding:** with real PhysX queries a 1.6 m jump reached the top of a **1.7 m** wall. The capsule counted as grounded while hanging on the edge, then stepped up.
- **Fix:** new `ground.ledgeTolerance` = 5 cm: landing from the air on an edge counts only if the face is ≤ 5 cm above the feet.
- `movement_v1.json` sha256 is now `1d966e17…`.

### A8. Client view (no WebGL build at the checkpoint)

`docs/proof/m2/m2a-sandbox-hud.png`: the Mac build in a 1280×720 window, the probe in view mode.
- **Map and players:** `Map_Sandbox`, 12 placeholder capsules with facing visors and BOT/YOU labels.
- **F3 HUD** at the time of the shot:
  - RTT 33 / 587 ms (the p95 includes the window's start-up hitch);
  - server tick 2600 vs predicted 2604;
  - input lead 2 / target 2 (67 ms), 1 re-sync at start-up;
  - snapshots 14.9 Hz, interpolation delay 99 ms;
  - payload in 4.08 KB/s / out 1.17 KB/s;
  - 60 FPS.

**UNVERIFIED at the checkpoint:**
- WebGL pointer lock and keyboard input in a browser (no WebGL build in M2a, per the build budget);
- the Linux x86_64 server build with the M2 code. Both come in M2b.

---

## Part B: M2b, prediction, hazards, Bolt v1, gate data (2026-11-18)

### B1. Setup for every M2b measurement

**Map:** `Map_Sandbox` plus the hazard course on the lane z = −28:
- **Sweeper:** pivot (−30, −31), arm 4.5 m, bar 0.3–0.6 m high, 5 s per turn. Contact: knockback 9 m/s + 5 m/s up, stun 15 ticks, immunity 30.
- **Moving platform:** past the −X edge, x = −46 ± 3.5 m, 6 s sine, shuttling to an island at x = −56.

**Server:** Mac dev server, `-fallrushBots 12` → 12 occupants (3 course clients + 9 TEST bots), 15 Hz snapshots, 60 fps cap.

**Clients: 3 × headless probe, script `course` (`Net/CourseBot.cs`).** Each client runs through the real client path (prediction, interpolation, the same messages as a browser) and steers from its *predicted* state and the hazard poses at its predicted tick. One lap:
- run west through the sweeper (jumping the bar; on odd laps it stands in the bar's path until hit);
- wait at the edge, jump onto the platform, ride it, jump to the island;
- ride back and run east;
- each client keeps its own lane (0.9 m apart), so pushes don't dominate (see B5).

**Numbers:** `[probe] pred` lines every 10 s (`Net/GateLogger.cs`), summarised with `python3 scripts/m2-summarize.py`. Logs are in `.m1-logs/probe-fa*.log`, `probe-fc*.log` (git-ignored).

**What each metric means:**
- **Correction:** the authoritative state at S differed from `history[S]` by > 5 cm, or flags/stun differed, with no server-only cause.
- **Push correction:** the server reported a player–player push since the last snapshot. Pushes are not predicted by design.
- **Silent:** differed by ≤ 5 cm; replayed without counting.
- **Bit-identical:** `history[S] == auth` exactly.
- **Remote stutter:** per frame, |rendered speed − interpolated server speed| of one running remote player (0 = perfectly smooth).

### B2. Gate data per condition

| | (a) localhost | (b) real VPS (Mumbai) | (c) 150 ms RTT + jitter + 2 % loss |
|---|---|---|---|
| Path | `ws://127.0.0.1:7777` | `wss://gs1.<VPS_IP>.sslip.io` → nginx → container | `ws://127.0.0.1:7778` → tc-netem relay → Mac server |
| Duration | 3 clients × 120 s (5 laps each) | **pending your approval (B8)** | 3 × 120 s (5 laps each) |
| RTT p50 / p95 (in-game ping) | 16.6–16.7 / 17–33 ms | | 183–185 / 252–284 ms |
| **Corrections per 5 s while running the course** | **0.00 / 0.00 / 0.00** | | **0.08 / 0.12 / 0.17** (2, 3, 4 in total; **all in the first 10 s**, 0 in every later window) |
| Push corrections (server-only, B5) | 2 / 0 / 0 | | 0 / 1 / 1 |
| Mean / max correction distance (jump of the predicted present) | — (none) | | 57–71 cm / 70–122 cm (start-up only) |
| Bit-identical snapshots | 1785/1791, 1784/1788, 1788/1791 (99.7 %) | | 1770/1775, 1773/1780, 1773/1781 (99.6 %) |
| Reconcile replay length | mean 0–0.1, max 1–3 ticks | | mean ≈ 0 (no mismatch), max 10–13 ticks |
| Input lead (client clock) | 1 tick (33 ms) | | 5–6 ticks |
| **Hazard-contact mismatches** | **0** (9 sweeper hits, all predicted) | | **0** (9 hits) |
| Interpolation delay | 105–125 ms (lateness p95 ≤ 14 ms) | | 141–150 ms (lateness p95 ≤ 297 ms) |
| Remote extrapolating / frozen frames | 0 % / 0 % | | 2.0–2.7 % / ≤ 2.9 % (worst window) |
| Remote stutter p95 | ≤ 1.10 m/s | | ≤ 1.30 m/s |
| Server sim step p95 (12 occupants) | 0.15–0.24 ms | | 0.16–0.24 ms |
| Server frame work p95 (12 occupants) | 0.45–0.47 ms (max 0.8–2.7 ms) | | same process |
| Server input counters | 0 late, 0 repeated | | per 10 s: late 0–17, repeated 0–17, burst 20–61 (TCP stall bursts deferred to the redundant copy) |
| Client FPS (probe, headless) | 57–59 | | 57–59 |

**WebGL client (release build, Chrome via Playwright, Mac server, localhost, 20 × 10 s, `?autoplay=1&bot=course`):**
- **FPS:** frame time p50 17.0 ms, p95 17–18 ms → **59–60 FPS** with 12 Bolts on screen, at Playwright's default window size.
- **Snapshots:** 2,914 / 3,004 bit-identical (97.0 %); 69 silent; 9 corrections in 200 s (≈ 0.23 per 5 s) + 1 push.
  - The counted corrections showed **≈ 0 cm** mean shift of the predicted present (the HUD's "last" stayed at the start-up value): they are flag-only differences between wasm and Mono, invisible.
- **Measured cross-platform drift (wasm client vs native Mono server, PhysX on both):** mean position error on mismatching snapshots **3.7 mm** (one 200 mm start-up mismatch dominates the early mean); 1 sub-millimetre mismatch. This is the §3.1 "measure the real drift" number for a Mac server; Linux x86 Mono vs wasm is in (b).
- **Raw log:** `.m1-logs/webgl-local-console.log`.

**Start-up corrections (both conditions):**
- **Cause:** one adoption/convergence step in the first seconds, while the clock settles and the first commands race the first snapshots.
- **At 150 ms with loss:** the clock target has to climb to 5–6. With the M2a +1-per-late rule this took ~10 s and produced **10–13** corrections per client (first run). With "raise by how late, and jump the clock" it is 2–4 (§B6).

### B3. What "150 ms + jitter + 2 % loss" means here

**Tool:** `scripts/m2-netem.sh start 75 15 2`.
- A Docker container (`alpine:3.20.3@sha256:1e42bb…`, `--cpus 0.5 --memory 128m`) runs Linux `tc qdisc … netem delay 75ms 15ms distribution normal loss 2%` on its egress and `socat` as a TCP relay: `127.0.0.1:7778` → `host.docker.internal:7777`.
- The relay forwards each direction once out of the container, so the delay applies once per direction: +150 ms RTT ± ~21 ms.
- Measured in-game RTT: p50 183–185 ms (the ~17 ms baseline plus the netem 150 ms plus relay hops) and p95 252–284 ms.

**What "loss" means here:**
- WebSocket is TCP, so a 2 % netem drop is a dropped TCP segment that TCP retransmits.
- The game never sees a lost message, only head-of-line **stalls**: an RTO or fast-retransmit delay, during which later snapshots and inputs queue up and then arrive in a burst.
- **Visible as:**
  - interpolation lateness p95 up to 297 ms;
  - "burst" deferrals and occasional late/repeated inputs on the server;
  - the client clock raising its input lead to 5–6 ticks so commands still arrive before their tick.
- UTP's own network simulator is a UDP pipeline stage and does nothing for WebSockets (M2a finding), hence the TCP-level relay.

### B4. R12: hidden tab

**How it was tested:**
- `-fallrushProbeHide 20:<secs>` blocks the client's main thread for 10/30/60 s.
- That is what a hidden tab does to Unity WebGL: no `requestAnimationFrame`, so no frames, no transport updates and no input. The server keeps running.
- Three course clients ran at the same time (`.m1-logs/probe-r12-{10,30,60}.log`).

| Step | 10 s | 30 s | 60 s |
|---|---|---|---|
| M2a code | **dropped** on wake-up: `Receive queue is full … (128)`, then `ProtocolError` | dropped | dropped (the server would also have timed it out at 30 s) |
| + client queue 1024, server disconnect timeout 90 s, server pauses snapshots after 1 s without input | survives | dropped (`queue is full (1024)`) | dropped |
| + `WebSocketDriverConstructor` (reliable resend 1–10 s) | survives | survives | dropped |
| **+ reliable resend 10–30 s (final)** | **survives** | **survives** | **survives** |

**After waking, in all three cases:**
- the clock re-syncs (3, 7 and 14 re-sync events);
- the prediction re-adopts the server state;
- every later 10 s window has **0 corrections**;
- the client runs to the end of its 135 s script.

**Root cause, measured:**
- With snapshots already paused, the server still sent **16.7 KB/s** to the stalled client (`nettop`).
- That traffic was NGO's ReliableSequenced pipeline: window 64, resend ≤ 500 ms (NGO 2.14.0 `UnityTransport.GetDefaultNetworkSettings`). It re-sent every unacknowledged reliable packet (e.g. NGO's 1 Hz time sync) twice a second, because the frozen client acknowledged nothing.
- On wake-up those thousands of duplicates overflowed UTP's receive queue. On desktop UTP's WebSocket layer then aborts the connection (`WebSocketLayer.cs`: "Insufficient space in receive queue").
- Over TCP those resends are redundant. `Net/WebSocketDriverConstructor.cs` builds NGO's driver exactly as NGO does (via the documented `UnityTransport.s_DriverConstructor` hook), only with resend 10–30 s.

**UNVERIFIED:**
- **A real hidden Chrome tab.** Playwright keeps automated pages `visible` (`document.visibilityState` stayed `visible` with another tab in front), so the browser path was not exercised.
- The browser's `WebSocketNetworkInterface` leaves overflow in the socket instead of aborting (UTP source), so it should be at least as tolerant as desktop.
- **Steps for you:** open the game, enter the sandbox, switch to another tab for 60 s, come back. Expected: the "! LAGGING" icon for a moment, then normal play; the HUD's `resyncs` goes up by one or more.

### B5. Pushes (server-only), and why the course uses lanes

- **First localhost run:** all three bots ran the same line at the same time. Result: 2–11 "corrections" per 5 s, mean ≈ 8 cm. The server logged ~500 player–player separation pushes per 10 s; that is the cause, not prediction.
- **After adding `ownEvents.pushed`:** those count as push corrections, and with 0.9 m lanes the counted corrections went to 0.
- **Real players will push each other**, and those corrections are real (smoothed over 120 ms). Risk R14 in doc 15.

### B6. Fix-window items 1–6 (doc 04 §4.5): status

The bar was met in (a) and (c) without entering the fix window. These items were still worked through while building, and each one was measured:
1. **Determinism:** EditMode server-twin tests are bit-identical (1,800 ticks, leads 1/3/6, delays 2/5, with hazards).
   - Fixed: replay now mirrors the server's missing-input rule (it was the only twin mismatch, at start-up).
2. **Tick alignment:** found and fixed the slow start-up convergence (late by k → target += k, clock jumps).
   - Measured at 150 ms: start-up corrections 1 / 10 / 13 → 2 / 3 / 4.
3. **Input delivery:** 3 redundant inputs; the server repeats for 3 ticks, then neutral. `burst` deferrals under loss work as designed.
4. **Hazards:** pose = f(def, seed, tick) with integer phase + polynomial sine; carry in the motor. 0 hazard mismatches in all runs.
5. **Smoothing:** exponential, 5 % left after 120 ms; > 2 m snaps.
6. **Server tick jitter:** Mac frame work p95 0.45–0.47 ms with 12 occupants. The VPS number is in (b).

### B7. Bolt v1 and screens

| File | What |
|---|---|
| `m2/m2b-bolt-closeup.png` | Bolt v1 (slot preset: mint shell, yellow bolt emblem): dome + seam, screen face `^ ^`, goggles, noodle arms with mittens, peg feet |
| `m2/m2b-bolt-silhouette-20m.png` | Silhouette test: unlit black override, white background, camera 20 m side-on, FOV 12°. Reads as capsule + noodle arms + key (the key's T-shape on the back, arms as lines with mitten dots, peg feet) — not a jellybean. **Your call** |
| `m2/m2b-course-localhost-hud.png` | In-game, localhost: HUD (F3), sweeper bar with hazard stripes |
| `m2/m2b-hud-150ms-jitter-loss.png` | HUD under condition (c): RTT 198/283, lead 6, interp 142 ms, 1 start-up correction, 0 hazard mismatches |

**Defects found and fixed while taking these:**
- **Noodle arms rendered twisted:** the tube frame flipped per ring; replaced with parallel transport.
- **Screen corners mismatched the lit dome:** they were unlit white. The face mesh is now a squircle and the atlas is fully dark.

### B8. VPS condition (b): pending your approval

UNVERIFIED until you approve the command list in the M2b report. `fallrush-gameserver:m2` is built (68 MB), the nginx file is `infra/nginx/fallrush-gs1-vps.conf`, and the existing `gs1` certificate is valid for 89 days.

### B9. Builds (M2b)

| Build | Time | Size | vs M1 |
|---|---|---|---|
| WebGL release (Editor closed; 3.5 GB swap in use) | 1,196.5 s build / 1,226.9 s wall, peak RSS 1.5 GB | **16.03 MB** (`wasm.br` 10.10, `data.br` 5.80, `framework.js.br` 0.08, `loader.js` 0.03) | 641 s / 15.83 MB → +0.20 MB |
| Linux x86_64 server (Mono, Dedicated Server) | 16.2 s | 95 MB output; image 68 MB | `FallRush.Client.dll` absent (visuals stripped) |
| Mac dev server | 13–18 s | 141 MB | — |
