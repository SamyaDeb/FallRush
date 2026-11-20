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
