# M4 proof: the full match (2026-11-19)

All numbers were measured on the dev machine (MacBook Air M2, 8 GB) unless marked **UNVERIFIED**. No VPS action was taken (deferred to M6). Raw logs: `.m1-logs/m4/` (git-ignored).

## A. What was built

| Area | Files |
|---|---|
| Ports + parity | `Shared/Deal.cs` (deal.ts), `Shared/RoundTargets.cs` (targets.ts), `Shared/Transcript.cs` (transcript.ts + `RosterHash`, M4 proposal), `MapPoolV1.RoundMaps`; TS generator `packages/shared/scripts/gen-transcript-vectors.ts` → `test-vectors/transcript_vectors.json` |
| Match core | `Shared/MatchDirector.cs` (state machine, roster, deal, events, transcript, grace, minimum-advance), `Shared/MatchTypes.cs` (MatchPhase, ElimReason, MatchEvent, RosterSlot, RoundResult, `IRoundRules`), `Shared/LobbyRules.cs`, `RaceRules` adapted to `IRoundRules` |
| Round modes | `Shared/TimedRoundRules.cs`, `SurvivalRules.cs`, `SkillRules.cs`, `FinalRules.cs` |
| M4 kit + reactive | `Shared/Hazards/M4Defs.cs` (GOO_PLANE, crumble grid, falling block, star field; pure functions), `Shared/Reactive.cs` (`ReactiveSet`, `rx` codec), `HazardSet` / `PlayerStep` (goo effect, spinner start), `PhysxMotorWorld` (reactive colliders), map hash `m4` block; authoring `Net/HazardGoo`, `HazardCrumbleGrid`, `HazardFallingBlock`, `HazardStarField` |
| Protocol v4 | `Shared/Protocol.cs` (hello name + token), `Shared/MatchMessages.cs` (`match`, `mevt`, `ready`, `ctl`) |
| Server | `Net/ServerMatch.cs` (replaces `ServerRace`: scene flow, ready, viewers, reconnect, jsonl log, soak), `ServerSim` (map switching, `SkipTick`, rx send, viewers' snapshots, sim speed), `Net/RulesFactory.cs`, `Net/BotBrainSet.cs`, `NetBootstrap` (match mode, hello stash, auto-reconnect) |
| Bots | `Shared/BotBrain.cs` (tiers, per-mode), `WaypointBot` (precise-move flags), `Net/MatchProbeBot.cs` (client probe / `?bot=match`) |
| Client | `Client/MatchHud.cs` (every screen), `ReactiveViews.cs`, `ChampionPodium.cs`, `ClientSim` (match / mevt / rx, scene following, ready), `Net/SessionToken.cs` + `Plugins/WebGL/FallRushSession.jslib`; `RaceHud` removed |
| Maps | `Editor/LobbyBuilder.cs`, `GooTowerBuilder.cs`, `StarScrambleBuilder.cs`, `CrumbleShelfBuilder.cs` → `Map_Lobby`, `Map_GooTower`, `Map_StarScramble`, `Map_CrumbleShelf` |
| QA / measurement | `Net/ModeQa.cs`, `HazardVectorsV2` + `Tests/Vectors/hazard_vectors_v2.json`, `scripts/m4-qa.sh`, `m4-soak.sh`, `m4-verify-log.py`, `m4-match-run.sh`, `m4-reconnect-run.sh`, `m4-summarize.py` |
| Tests | `ParityTests`, `MatchDirectorTests`, `M4RulesTests`, `MatchMessageTests` (+ updated `ProtocolTests`) |

## B. Tests

**EditMode 254 / 254** (M3: 207; +47). Highlights:
- **Parity (6):** all 100 deal vectors + 40 rejection-sampling vectors; round targets P = 4…20; four roster hashes; three transcripts (every step) — all byte-identical to the TS reference.
- **MatchDirector (11):** full walk LOBBY_OPEN → … → DONE → LOBBY_OPEN with the timings (countdown 600, dealing 90, dealt 90, intro+countdown 150 + 2, results 180, intermission 240, champion 240, receipt 600 ticks); the events of two scripted matches equal the TS vectors (seq, tag, round, slot, arg, transcript after each); minimum-advance with 1 / 0 / fewer-than-N survivors; roster lock + bot fill + labelled + no late joiners; lobby needs a human unless soak; same seed → same events; reconnect after 10 s keeps the slot and the qualification; 25 s away → `ELIMINATED(DISCONNECT)`; every human gone → `ABORT(1)`; late loader protected, qualified ≤ target.
- **Rules + reactive (24):** goo pose integer-exact; goo kills at hazard tick 61 on the server **and in prediction**; survival first-N escape, timer ranking by height then slot capped at N, fewer-than-N; star timeline (waves of 8 distinct non-golden anchors, golden every 15 s, seeded), a star on a turn table returns after one turn; pickup only by the server capsule, a knockback drops exactly one star, re-grab; skill ranking ties (time at final score, slot); tile solid until contact + 17, gone at + 18, the player falls from that tick; spinners only from 60 s; last standing ends the final, tiebreak (layer, tiles, slot), 120 s timer; rx delta chunks ≡ full ≡ server state over 1,400 ticks, seq gap refused, malformed refused; **prediction vs a server twin bit-identical** (leads 1/3/6 × delays 2/5: 0 corrections, 0 silent, 0 hazard mismatches) through ~35 tile drops down all three shelves + goo, and (leads 1/6 × delays 2/5) through ~50 star pickups, ~32 drops and ~34 bumper knockbacks; hazard vectors v2.
- **Messages (5):** `match` for 12 = 334 B (header 140 B, max 452 B, < the 1,296 B non-fragmented limit), strict decode at every truncation; `mevt` 44 / 13 B; `ready`, `ctl`; `snap` still 294 B for 12; 20k-message fuzz with no exception.

## C. Determinism

- **Deal / targets / transcript:** C# = TS byte for byte (§B). The Python verifier (`scripts/m4-verify-log.py`, its own deal / roster-hash / transcript implementation) agrees with every server log produced in M4 (soak 20/20, the real-client runs, the dev matches).
- **Hazard vectors v2:** 1,400 cases (v1's 1,000 unchanged, digest `fc06826c…` still passes) + 400 M4 cases (goo, star timeline, dropped stars, tile lookup); digest **`d4b2a73a…`** in the Editor. WebGL self-test: §H.
- **Same seed → same events** (EditMode) and **map QA run 0 re-run identical** for every map.

## D. Map QA

Full tables: [`m4-map-qa.md`](m4-map-qa.md) (`scripts/m4-qa.sh 100`, Mac dev server, 100 runs per variant × allowed modifier × tier). **All four PASS**: valid result in every run, 0 soft-locks, qualified ≤ target, deterministic.

| Map | Bots | Reference median | Normal median | GDD §4.1 target | Notes |
|---|---|---|---|---|---|
| Block Party Dash | 12 | 47 s (M3) | **97.5 s** (p90 ≤ 145) | 75–110 s | normal bots fall ~8× per race each; 7 / 1,000 races hit the 150 s timer |
| Goo Tower | 8 | 62.7 s | **66.4 s** | 90–120 s | ~4.9 escape, ~1.7 gooed per round; **every route index escapes alone with a goo margin of 9.1–9.9 s** (the goo never overtakes a route) |
| Star Scramble | 5 | 60 s | **60 s** (timer) | 60–75 s | ~61 pickups per round; score = pickups − drops in every run |
| Crumble Shelf | 3 | 31.8 s | **27.2 s** | 60–120 s | one champion in 100 % of runs |

**Tuning log (each from a QA run):** Goo Tower: crawlspace under lap-1 steps (bots wandered under the outer blocks) → lap 1 solid to the ground; survival section switched in mid-jump → section by the last ground contact; spawn deck moved west so the start corner is the nearest waypoint; bumper knocks threw players past the lap below into the void → 0.7 m ledge lips; one 3 m lift jammed a 12-bot crowd → 4.4 m lifts; 18 m sides gave 53 s rounds → 24 m sides; goo 0.4 → 0.3 m/s (0.4 left 2–3 of 8 escaping). Bots: noise during precise moves made normal bots fall ~15× per race → no wobble / delay / slow stick during jumps, landings, platforms and while airborne; reaction delay made bots dance at doors → stops are immediate; hesitation fired on the Final's deliberate hold → only at door / bar / lift waits; Skill drops were 80 % of pickups → bumper detours; the Final lasted ~9 s → frugal tile stepping.

**Durations outside the GDD targets (Survival, Final) are bot numbers; human times are UNVERIFIED.** The Final is short by design arithmetic: a tile holds you for 0.6 s (GDD), so 3 players use up 243 tiles in under a minute even when careful. Options for you: a longer wobble (e.g. 1.0 s), a bigger grid, or tiles that respawn on the top shelf.

## E. Unattended match soak (`scripts/m4-soak.sh 20 8`)

20 complete 12-bot matches back to back, Mac dev server, headless, random seed per match, **sim speed 8** (8 server ticks per network tick while nobody is connected: the same code path, faster than real time; 1,011 s wall for ~2 h of match time). **Result: 20 / 20** (pass ≥ 19). Every match reached CHAMPION → DONE → LOBBY, exactly one champion, event seq contiguous, **all 20 transcripts recomputed from the jsonl logs equal the server's**, 0 exceptions, 0 soft-locks, RSS 176 MB at match 1 → 179 MB at match 20 (ps samples every 5 s: max 183 MB).

| Match | Result | Rounds | Champion slot | Duration (s, roster lock → COMPLETE_MATCH) | Events | Transcript (first 16) | Sim step p50 / p95 (ms, per tick) | RSS (MB) | Mono heap (MB) |
|---|---|---|---|---|---|---|---|---|---|
| 1 | COMPLETE | 4 | 10 | 340 | 38 | `ce053fee1582a334` | 0.122 / 0.266 | 176 | 1.7 |
| 2 | COMPLETE | 4 | 3 | 380 | 38 | `2b0b928309a213ec` | 0.106 / 0.231 | 177 | 1.7 |
| 3 | COMPLETE | 4 | 5 | 308 | 38 | `1f2cd266a2052371` | 0.098 / 0.226 | 177 | 1.7 |
| 4 | COMPLETE | 4 | 0 | 384 | 38 | `7bb4de0bf67f7427` | 0.104 / 0.237 | 177 | 1.7 |
| 5 | COMPLETE | 4 | 10 | 385 | 38 | `acc4803815afaba3` | 0.078 / 0.206 | 177 | 1.7 |
| 6 | COMPLETE | 4 | 5 | 378 | 38 | `e767c81fb151ac9e` | 0.106 / 0.236 | 177 | 1.7 |
| 7 | COMPLETE | 4 | 8 | 335 | 37 | `a60211f04da730e2` | 0.390 / 0.784 | 178 | 1.7 |
| 8 | COMPLETE | 4 | 3 | 331 | 38 | `931aa5b2900e2536` | 0.378 / 0.805 | 178 | 1.7 |
| 9 | COMPLETE | 4 | 1 | 377 | 38 | `79ef8824d81c2e11` | 0.105 / 0.248 | 178 | 1.7 |
| 10 | COMPLETE | 4 | 1 | 388 | 37 | `54f9147e93512e8e` | 0.097 / 0.225 | 178 | 1.7 |
| 11 | COMPLETE | 4 | 1 | 386 | 38 | `0d61dc7fc638c6b8` | 0.088 / 0.194 | 178 | 1.7 |
| 12 | COMPLETE | 4 | 4 | 318 | 38 | `fd3ce27c4b5b957a` | 0.323 / 0.563 | 178 | 1.7 |
| 13 | COMPLETE | 4 | 10 | 362 | 38 | `0b690cde86f434c4` | 0.077 / 0.203 | 179 | 1.8 |
| 14 | COMPLETE | 4 | 9 | 378 | 38 | `cc9ba8f9076d113d` | 0.094 / 0.221 | 179 | 1.7 |
| 15 | COMPLETE | 4 | 6 | 382 | 38 | `d89560f36abc67c4` | 0.107 / 0.261 | 179 | 1.7 |
| 16 | COMPLETE | 4 | 6 | 294 | 38 | `4186db0b89edc701` | 0.104 / 0.227 | 179 | 1.7 |
| 17 | COMPLETE | 4 | 0 | 340 | 38 | `cc2ad9f648a5919a` | 0.097 / 0.236 | 179 | 1.8 |
| 18 | COMPLETE | 4 | 2 | 379 | 38 | `47e44ddd0c22350f` | 0.078 / 0.150 | 179 | 1.8 |
| 19 | COMPLETE | 4 | 6 | 318 | 38 | `9771812ad6adbca5` | 0.089 / 0.219 | 179 | 1.8 |
| 20 | COMPLETE | 4 | 3 | 326 | 38 | `2730076949fc04de` | 0.105 / 0.231 | 179 | 1.8 |

20 matches: 20 complete; duration median 377 s (min 294, max 388); sim p95 median 0.231 ms, max 0.805 ms; RSS 176 → 179 MB.

Durations are game time from the roster lock (add the 20 s lobby countdown); 37 events = a round with fewer qualifiers than the target (minimum-advance). Sim step per simulated tick (12 occupants); matches 7, 8 and 12 overlapped with other work on the machine.

## F. Real-client runs (`scripts/m4-match-run.sh`, `scripts/m4-reconnect-run.sh`)

3 headless probe clients (the real client path; a normal-tier `BotBrain` steering from the **predicted** state, seeing only snapshots and replicated state) + 9 server bots, one full match each. "Racing" = 10 s windows in RoundActive / Rush while the probe is in the round. Push = the server reported a player–player push (R14, accepted in M3).

**(a) localhost** (`ws://127.0.0.1:7777`) — champion slot 8, 336 s, 38 events, transcript verified:

| Client | Round | Map | 10 s windows racing | Corrections / 5 s (non-push) | Push corr. / 5 s | Hazard corr. | Hazard mismatches | Reactive late | Max corr. (cm) | RTT p50 / p95 (ms) | Snapshot payload (B/s) |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 1 | BlockPartyDash | 8 | 0.00 | 0.00 | 0 | 0 | 0 | 0.0 | 17 / 34 | 4410 |
| 1 | 2 | GooTower | 7 | 0.00 | 0.29 | 0 | 0 | 0 | 0.0 | 17 / 33 | 3030 |
| 1 | 3 | StarScramble | 6 | 0.00 | 1.42 | 0 | 0 | 0 | 0.0 | 17 / 19 | 1995 |
| 1 | 4 | CrumbleShelf | 3 | 0.00 | 0.00 | 0 | 0 | 0 | 0.0 | 17 / 33 | 1305 |
| 2 | 1 | BlockPartyDash | 7 | 0.00 | 0.07 | 0 | 0 | 0 | 0.0 | 17 / 33 | 4410 |
| 2 | 2 | GooTower | 7 | 0.00 | 0.14 | 0 | 0 | 0 | 0.0 | 17 / 18 | 3030 |
| 2 | 3 | StarScramble | 6 | 0.00 | 1.08 | 0 | 0 | 0 | 0.0 | 17 / 19 | 1995 |
| 3 | 1 | BlockPartyDash | 11 | 0.00 | 0.09 | 0 | 0 | 0 | 0.0 | 17 / 18 | 4410 |

All: **0.00 non-push corrections / 5 s**, push 0.35 / 5 s (Star Scramble 1.1–1.4: five players chase the same stars), **0 hazard mismatches, 0 reactive changes late**. Server sim step p95 (10 s windows): median 0.54 ms, max 1.44 ms. Client 1 played all four rounds (into the final).

**(b) 150 ms RTT ± 15 ms jitter + 2 % TCP segment loss** (`scripts/m2-netem.sh start 75 15 2`) — champion slot 4, 323 s, transcript verified:

| Client | Round | Map | 10 s windows racing | Corrections / 5 s (non-push) | Push corr. / 5 s | Hazard corr. | Hazard mismatches | Reactive late | Max corr. (cm) | RTT p50 / p95 (ms) | Snapshot payload (B/s) |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 1 | BlockPartyDash | 7 | 0.64 | 0.00 | 0 | 0 | 0 | 322.5 | 185 / 314 | 4410 |
| 1 | 2 | GooTower | 7 | 0.07 | 0.50 | 0 | 0 | 0 | 1.2 | 187 / 259 | 3030 |
| 3 | 1 | BlockPartyDash | 7 | 0.00 | 0.29 | 0 | 0 | 0 | 0.0 | 190 / 270 | 4410 |
| 3 | 2 | GooTower | 7 | 0.14 | 0.86 | 0 | 0 | 0 | 22.5 | 189 / 280 | 3030 |
| 3 | 3 | StarScramble | 6 | 0.00 | 2.17 | 0 | 0 | 0 | 0.0 | 187 / 274 | 1995 |

All: **0.18 non-push corrections / 5 s** (worst round 0.64: client 1's race, incl. one 3.2 m snap during start-up clock convergence), push 0.72 / 5 s, **0 hazard mismatches, 0 reactive late** (neither probe reached the final in this run, so Crumble Shelf's tiles were not exercised at 150 ms by a probe; the EditMode twin covers it at delay 5 + lead 6). Server sim p95 median 0.56 ms. **Finding:** client 2 never connected — UTP gave up after `MaxConnectionAttempts` (10 × 1 s) through the lossy relay at start-up. The run had 2 probes + 10 bots. A human would press Reconnect; raising the attempts for WebGL is an M5/M6 item.

**Pass bar** (< 1 non-push correction per 5 s, 0 hazard mismatches): **met** on localhost and at 150 ms.

**(c) Reconnect** (`scripts/m4-reconnect-run.sh`, reference-tier probes so they survive round 1): all three probes alive in round 2; 15 s into round 2 Probe1 (slot 0) and Probe2 (slot 1) were killed (`kill -9`).
- Probe1 restarted after **10 s** with its token file → `[gs] resume slot 0 → client=4 (grace ok)`; it reloaded Goo Tower, kept climbing from 13 m (its Rushling had stood on neutral input), reached the summit and **QUALIFIED in round 2 (5th)**.
- Probe2 restarted after **30 s** → `resume slot 1 … grace EXPIRED: spectates` → **`ELIMINATED r2 slot=1 arg=5` (DISCONNECT)** at the end of round 2.
- The match completed (champion slot 8, 38 events) and its log verified.

**Bandwidth (payload, per client):** snapshots 4.41 KB/s with 12 players (race), 3.03 (8), 2.0 (5), 1.3 (3); `rx` in Crumble Shelf ≈ 140–150 B/s (≈ 7 tile contacts/s, peak 68 entries in 10 s); `match` ≈ 33–100 B/s around phase changes, 0 otherwise. The heaviest round is the race (12 snapshots × 23 B); Crumble Shelf adds < 0.2 KB/s of reactive state.

## G. Message sizes (protocol v4)

| Message | Size |
|---|---|
| `snap` (12 players) | **294 B** (unchanged; players out of the round are omitted, so later rounds are smaller: 8 → 202 B, 5 → 133 B, 3 → 87 B) |
| `match` | 140 B + (10 + name) per slot: **334 B** for 12 (EditMode), 310 B lobby with 1; max 452 B |
| `mevt` | DEAL 44 B; QUALIFIED / ELIMINATED / COMPLETE_ROUND / ABORT 13 B; START_ROUND / COMPLETE_MATCH 12 B; a 12-player match = 38 events ≈ 0.5 KB |
| `rx` | 8 + 14 B per entry, ≤ 80 entries per message |
| `ready`, `ctl` | 5 B each |
| `hello` v4 | ≤ 69 B |
| `round` / `rstate` / `evt` | 59 B / 16 + 5 n / 14 B (unchanged layouts) |

## H. WebGL (release, Chrome via Playwright, against the Mac dev server)

| | Result |
|---|---|
| Release build 1 (exception support None) | 383.7 s build / 409 s wall, **16.45 MB** (`wasm.br` 10.25, `data.br` 6.07, `framework.js.br` 0.08, `loader.js` 0.03) vs M3 16.22 MB (+0.23 MB for four maps + M4 code). **A full match halted** ("Uncaught undefined", `abort`) the moment the client — eliminated and spectating — built its Crumble Shelf prediction for round 4. Rounds 1–3 ran at 59 FPS. |
| Diagnosis | a WebGL **development** build (exceptions supported) ran the same scenarios (Crumble Shelf playing, falling, spectating, viewer join, a full `?bot=match` match into round 4) **without any exception or error**, and no Mac (Mono) run logged an exception. With exception support None any thrown exception aborts, even one caught internally. **Root cause not identified** (no managed exception surfaced in any build that reports them). |
| Fix | `Build.WebGL` release now uses **"Explicitly Thrown Exceptions Only"** (Unity's WebGL default; any exception is reported and caught ones no longer abort). Release build 2: 799.1 s build / 819 s wall (machine swapping), **16.45 MB**. |
| Full match, release build 2 | lobby → deal → Block Party Dash → Goo Tower → Star Scramble → **Crumble Shelf** → champion → receipt → **back to the lobby**, `?bot=match` (normal-tier BotBrain on the predicted state), no error. Corrections in racing windows: Block Party Dash 0 (6 windows), Goo Tower 2 (7), Star Scramble 1 (6) → ≈ 0.1 / 5 s; **0 hazard mismatches** (WebGL wasm client ↔ Mac arm64 server: not the production pairing, see M2 B9). |
| FPS (Playwright's default window, 12 / 8 / 5 / 3 Rushlings) | Lobby, Block Party Dash, Goo Tower, Star Scramble, Crumble Shelf: frame p50 **17.0 ms**, p95 ≤ 18 ms → **59 FPS in every map** |
| Draw calls (Render counters, release player) | Lobby 160 · **Block Party Dash 490** · Goo Tower 282 · Star Scramble 171 · **Crumble Shelf 51** (243 tiles in 7 instanced batches; 11k tris). Block Party Dash / Goo Tower exceed the doc 17 §3.2 budget of 150 (greybox boxes, labels, not static-batched) — **M7 item**; FPS is fine on the M2. |
| Determinism self-test (`?selftest=1`) | **PASS** `hazard_vectors_v1` digest `fc06826c…` and **PASS** `hazard_vectors_v2` digest **`d4b2a73a…`** (1,400 cases), both release builds |
| Auto-reconnect in WebGL | seen in the dev build: the server was restarted under a running page → "reconnecting with the token" → `resume slot 0` |

## I. Screenshots (`m4/`, 960 px, from a windowed probe running a full match)

`01-countdown-r0-lobby.png` (lobby room, player list, countdown, host Start), `04-dealt-r0-lobby.png` (the deal), `05-roundintro-r1-blockpartydash.png` (intro + goal line), `play-blockpartydash.png`, `play-gootower.png` (escaped / alive counter), `05-roundintro-r3-starscramble.png`, `spectate-starscramble.png`, `spectate-crumbleshelf.png` (instanced tiles, wobbling = yellow, "Tiles crumbling!"), `07-roundresult-r2-gootower.png`, `08-intermission-r2-gootower.png` (leaderboard, next round, Fan Picks hook), `09-champion-r4-crumbleshelf.png` (podium + crown), `11-done-r4-crumbleshelf.png` (receipt). Cosmetic: the leaderboard's place columns use a proportional font and do not line up perfectly.

## J. Decisions made beyond §3.0 (also in doc 15 as M4-12…14, 🟡)

1. **Late loaders** (10 s timeout) advance if the round can still qualify ≥ 1 without them (the rules' target drops by their number), else they rank last (e.g. the final).
2. **A disconnected top finisher's place is not handed down**: grace expiry turns a qualifier into `DISCONNECT` and the round may qualify fewer than the target (always ≥ 1, minimum-advance).
3. **Shortened dev plans** (`-fallrushMap X`, `-fallrushRounds N`): the last planned round crowns 1, so every dev match still ends with `COMPLETE_MATCH`.
4. **Abort:** practice match with every human gone beyond the grace → `ABORT(1)`; soak / QA matches never abort for lack of humans.
5. **Per-round hazard seed** `sha256("fallrush-hazard-v1" ‖ seed ‖ r)[0..8]`; **match id** `sha256("fallrush-match-id" ‖ seed)[0..16]` (deterministic for tests; M5 assigns real ids).
6. **Survival sections for bots** from the height of the last ground contact; **Skill tie rule** "earliest time at final score" = the tick of the last score change; dropped stars always worth 1; falls keep stars.
7. **Map QA bot counts** 12 / 8 / 5 / 3 by mode (what those rounds really have); **Goo Tower** sides 24 m, goo 0.3 m/s, ledge lips; **bot noise** off during precise moves.
8. **Reactive chunks** ≤ 80 entries (NGO non-fragmented limit 1,296 B) rather than `ReliableFragmentedSequenced`.
9. **WebGL release exceptions**: None → Explicitly Thrown Only (§H).
10. **Name** in `hello` (desktop `-fallrushName`, WebGL `?name=`), "Player N" otherwise; bots get toy names, always shown with BOT.

## K. Deviations from the docs / prompt

- `MatchDirector`, `BotBrain` live in `Shared/` (engine-free, testable) as the prompt says; doc 04 §3 placed them in `Server/` (doc fixed).
- Lobby rule: practice countdown starts with ≥ 1 human (doc 05 said "≥ 8 humans or host"); ranked rules are M6.
- Doc 08 §7 `TILE_DROP {tileId, seq, tick}` → a contact entry; the drop is contact + 18 on both sides.
- The intro is 5 s in total (2 s intro phase with the fly-through + 3 s 3-2-1), not M3's 3 s + 3 s.
- Map QA ran Survival / Skill / Final with 8 / 5 / 3 bots, not 12 (decision 7); a 12-bot run of every map passed in the smoke tests.
- The soak ran at sim speed 8 (§E).
- GDD duration targets: met for the Race (normal bots) and Skill; **not met by bots for Survival (≈ 66 s) and the Final (≈ 27–32 s)** (§D).
- Colourblind-safe outlines: not done (prompt: "if cheap").

## L. Bugs found and fixed while building / measuring

- Soak never quit (the lobby re-armed before the quit check) → check before the director's step.
- Goo Tower: crawlspace under lap-1 steps; mid-jump section switch; spawn deck behind a wall; bumper knocks into the void; one lift jamming a crowd (all §D).
- Bots dancing around door waits (delayed "stop"); hesitation on the Final's hold; noise during jumps.
- A full `rx` state can exceed NGO's 1,296 B non-fragmented limit → chunks.
- WebGL release halt at the Crumble Shelf load (§H).

## M. Not done / UNVERIFIED

- **Human feel, fun, fairness, readability and human round durations: yours** after a playtest.
- The production pairing (WebGL ↔ Linux x86_64 server on the VPS): **not run in M4** (VPS deferred to M6).
- 150 ms run: one of three probes could not connect through the lossy relay (UTP `MaxConnectionAttempts` 10 × 1 s); neither remaining probe reached the final, so Crumble Shelf reactive timing at 150 ms was covered only by the EditMode twin.
- Draw calls over budget on Block Party Dash and Goo Tower (§H).
- A real hidden Chrome tab (R12) and a real browser tab close + reopen within 20 s: the token path is in place (sessionStorage) and was exercised by a server restart under a page, not by closing the tab.
