You are the senior gameplay / multiplayer engineer on **Fall Rush**, and you also know Solana and MagicBlock well. Fall Rush is an original 12-player knockout party game for the MagicBlock Solana Blitz V10 hackathon, and the deadline is **2026-11-27 14:00 UTC**.

Your task is **Milestone M4: the full match**. When it is done, one server runs a complete match end to end, locally: a lobby room → a deal → **four rounds** (Race → Survival → Skill → Final) with real eliminations → a champion → a receipt → back to the lobby, with labelled bots filling empty slots, disconnect grace and reconnect, and every screen in greybox.

It has two halves. **Don't stop between them** (the user prefers one run); stop only for a blocker (§2.4) or a decision that is genuinely mine (§2.5).
- **M4a, the match core:** C# ports of the deal, round targets and transcript (with parity tests against the TS vectors), `MatchDirector`, the lobby rules, Survival / Skill / Final rules, the reactive-element system, disconnect grace + reconnect, protocol v4.
- **M4b, content and polish:** the three new maps (Goo Tower, Star Scramble, Crumble Shelf) and a lobby room, `BotBrain`, every match screen, the map QA and the **unattended match soak**.

Out of scope:
- `programs/`, `server/` (Relay, M5), `web/` (M8), any chain call or VRF request (M6). M4 runs **practice matches** from a local seed through the **same deal algorithm** that M6 will feed with VRF;
- **VPS work: none in M4.** The user deferred all VPS steps to M6. No SSH, no Hostinger CLI writes;
- Fan Picks, XP/achievements, the Crown Capsule mint (P1/M8), Bonk Factory (stretch, D-14);
- final art, audio, VFX, the capsule machine, the claw (M7). Greybox, readable, with simple placeholder animation.

## 0. Context: read these first, fully

The repo is `/Users/samya/Downloads/FallRush` (git, `main`, M0–M3 committed).

| Doc | What it gives you |
|---|---|
| `docs/00-README.md` | Index and the rules you cannot break |
| `docs/01-GDD.md` §4 (match format, scaling rule), **§5.2–5.4** (Survival, Skill, Final rules and tiebreaks), §8 (modifiers), §9 (deal), §12 (UX flow), §13 (accessibility) | **Primary game spec** |
| `docs/05-BACKEND-RUSH-RELAY.md` **§4** | The canonical match state machine, lobby rules, disconnect rules |
| `docs/08-MAP-DESIGN.md` §1, §2, **§5 Goo Tower, §6 Star Scramble, §7 Crumble Shelf**, §8 checklist, §9 QA | Map and kit spec; §3.1 shows how Block Party Dash was built |
| `docs/06-ANCHOR-PROGRAM.md` §3–4 and the notes near lines 104–192 | The on-chain event model your director must match (seq, tags, reasons, roster hash, transcript) |
| `docs/04-UNITY-CLIENT.md` §4 (netcode as built: protocol v3, `SimCore`, `PlayerStep`, `RoundParams`, map hash, push prediction), §7 (screens), §9 (bots), §11 (tests) | Engine architecture |
| `docs/17-ART-DIRECTION.md` §1 (originality), §3.1 (palette), §3.2 (perf budget), §5 (UI style, HUD corners, chain chip) | Look and UI |
| `docs/11-SECURITY-ANTI-CHEAT.md` | Server validation of pickups, tiles, eliminations |
| `docs/12-TESTING-PLAN.md` | PlayMode / soak expectations |
| `docs/15-DECISIONS-AND-RISKS.md` | D-1b, D-6, D-11, D-12, D-13, D-14, D-16; R14 |
| `docs/proof/M3-race.md` (incl. **§I**, the lengthened course) and `docs/proof/M2-netcode.md` (B9: the Mac arm64 build is **not** bit-identical with WebGL/Linux) | Measured baseline |
| `PROJECT_STATUS.md` | Current status and **"Notes for M4 / M6 (from M3)"**. You will update it |
| `packages/shared/src/deal.ts`, `targets.ts`, `transcript.ts`, `test-vectors/deal_vectors.json`, `config/*.json` | The TS reference implementations you port |

Code you build on, in `client-unity/Assets/_Game/`:
- `Scripts/Shared/`: `SimCore`, `RaceRules` (+ `RaceResult`), `WaypointBot`, `RoundParams` (`MapPoolV1` mirror, `FromDevSeed`), `RoundMessages` (`round`, `rstate`, `evt`), `MapContent`, `Hazards/*` (8 types, `PlayerStep.Run`, allowlists, vectors), `ModifierEffects`, `KinematicMotor`, `NetMessages`, `InputSanitizer`, `TickSync`
- `Scripts/Net/`: `ServerSim`, `ServerRace` (the M3 round loop you **replace** with the director), `ClientSim`, `ClientPrediction`, `Interpolation`, `MapInfo`, `MapDefinition`, `MapQa`, `RaceCheckpoint`, `Hazard*` authoring, `SimProbe`, `RaceProbeBot`, `SelfTest`, `NetBootstrap`
- `Scripts/Client/`: `SandboxView`, `RaceHud`, `RushlingVisuals`, `FollowCamera`, `UiKit`, `LobbyScreen`, `MenuScreen`
- `Editor/`: `KitBuilder`, `BlockPartyDashBuilder`, `SandboxBuilder`, `MapHashTool`, `QaDebug`, `Build`
- `scripts/m1-*.sh`, `scripts/m2-*.sh`, `scripts/m3-gate.sh`, `scripts/m2-summarize.py`

Read them before changing anything. Keep everything working: prediction (0 hazard mismatches), R12, the TCP-stall fixes, `Map_Sandbox` + kit row, Block Party Dash and its QA, and the **207 EditMode tests**.

## 1. Non-negotiable rules

1. **Never invent** Unity, NGO 2.14.0, UTP 2.7.5, Input System 1.20.1, Cinemachine 3.1.7, URP 17.3 or Unity Test Framework APIs, package versions, CLI flags or env vars. Sources of truth: `client-unity/Library/PackageCache/` and the Unity 6000.3 docs. If reality differs from a doc, follow reality and **fix the doc in the same commit**.
2. **No new packages** without asking me. Greybox from code (the `KitBuilder` / `BlockPartyDashBuilder` pattern).
3. **Authority and determinism:**
   - The server is authoritative for positions, stun, knockback, checkpoints, goo, tiles, stars, scores, qualification, elimination and the champion. Clients send inputs only.
   - Pure-function hazards stay pure (`f(def, variant, hazardSeed, roundTick)`, integer-first maths). New pose functions (goo plane, star timeline) follow the M3 rules and go into `hazard_vectors` (regenerate as `hazard_vectors_v2.json`; keep v1 passing or document why it was superseded).
   - **Reactive elements** (crumble tiles, falling blocks, stars) are server-owned and replicated with a sequence number (§3.4). Their **effects** on the motor still run in `PlayerStep.Run` on both sides, from the replicated state, so prediction stays exact.
   - Don't implement fallback F1.
4. **Config and the chain:**
   - **Do not modify `ruleset_v1.json`, `map_pool_v1.json`, `modifiers_v1.json`, `movement_v1.json`, `packages/shared/src/*` logic, or anything on-chain.** You may **add** TS scripts that generate test vectors (for example `packages/shared/scripts/gen-transcript-vectors.ts` → `test-vectors/transcript_vectors.json`).
   - Qualify counts come from the C# port of `targets.ts`, never hard-coded in client code.
5. **The event stream must be what the program will accept** (doc 06): seq 0 = DEAL, then strictly increasing seq; per round START_ROUND, then QUALIFIED / ELIMINATED per slot, then COMPLETE_ROUND, …, COMPLETE_MATCH (or ABORT). The C# transcript must equal `transcript.ts` byte for byte (parity test). M6 will forward these events to the ER unchanged.
6. **Protocol:** bump `Protocol.Version` to **4**. Old clients get the readable mismatch message. Record every message size; keep `snap` for 12 players under ~320 B.
7. **Trust and wording:** never claim "cheat-proof"; numbers are "measured" only if you ran them; everything else is UNVERIFIED. "Fun", "fair", "readable" are **my** calls after I play.
8. **8 GB MacBook Air M2:**
   - Iterate in the Editor and with the native Mac dev server (D-16). Multiplayer Play Mode with at most 1 virtual player.
   - **WebGL release builds: at most two in M4**, Editor closed. Never run the Editor, a batch build and Docker at once. At most 3 browser tabs.
   - **The Mac arm64 build is not bit-identical with WebGL/Linux** (M2 B9). Never judge cross-platform determinism from Mac-only runs; use the WebGL self-test.
   - Swap was 4–5.6 GB during M3. Close what you don't need; prefer headless batch runs.
9. **No VPS, no Hostinger, no SSH in M4** (deferred to M6 by the user).
10. **Git:** small local commits at logical checkpoints. **Never push, and never add a remote.** No build output in git.
11. **Originality** (doc 17 §1, doc 08 §1.5 and §8): Fall Rush has the *genre* feel of a knockout party game, but every map, character, screen and sound is our own. **No Fall Guys level layouts, round names, obstacle shapes, characters, UI or sounds, and no close copies.** Goo Tower, Star Scramble and Crumble Shelf are already original designs (doc 08 §5–7; Crumble Shelf uses square toy-block shelves, not hex tiles). Don't steer them towards known levels.
12. **Unity file rule:** one MonoBehaviour / ScriptableObject class per file, named after the class.

## 2. Workflow

0. If `git status` shows uncommitted changes to `docs/prompts/M4-PROMPT.md` or `PROJECT_STATUS.md`, commit them first as `docs: M4 prompt`.
1. **Before coding**, write a short plan (in your reply, then proceed without waiting) with:
   - files to create or change;
   - the `MatchDirector` states and transitions with timings;
   - the new messages and byte sizes, and the new `snap` size for 12;
   - the reactive-element design (state, seq, delta vs full, how prediction uses it);
   - the reconnect flow (token, slot restore, state resync);
   - any doc discrepancy you found.
2. Build in this order. Commit after each step with tests green:
   1. C# `Deal` / `RoundTargets` / `Transcript` ports + parity tests;
   2. `MatchDirector` + event log on the existing race only (a "1-round match");
   3. reactive elements + Survival / Skill / Final rules (EditMode-tested on analytic or tiny test scenes);
   4. maps; 5. `BotBrain`; 6. screens; 7. reconnect; 8. QA + soak + measurements; 9. docs.
3. Run every build and test yourself. Fix errors; never weaken a test to get green.
4. **Time-box:** if one blocker eats more than ~2 hours, stop and report the error, logs, what you tried and the options.
5. **Decisions:** the decisions below (§3.0) are already made. Make any further small design call yourself, record it in the proof file with the reason, and mark it 🟡 in doc 15 if it changes a documented decision.

## 3. Engineering requirements

### 3.0 Decisions already made (follow them; don't re-ask)
| # | Decision | Why |
|---|---|---|
| M4-1 | **One match per server process.** After the receipt the server returns to the lobby and starts a new match. | Matches the VPS layout (gs1/gs2) and keeps state simple |
| M4-2 | **Practice matches only in M4.** Lobby opens with ≥ 1 human; at countdown end bots fill to 12. Ranked (≥ 4 humans, on-chain) is M6. | No chain in M4 |
| M4-3 | **Scene flow stays our own** (no NGO scene management): the server loads the round's map, sends `round` naming map + hash, and every client loads it and reports `ready`. The intro starts when all connected humans are ready or after a **10 s** load timeout (late loaders spectate that round and are not eliminated for it unless they were already out). | Proven in M3; avoids NGO scene sync risk |
| M4-4 | **Deal from a local seed through the real algorithm.** `matchSeed` (32 B: `-fallrushSeed`, else random per match on the server) → C# `Deal` (port of `deal.ts`) → maps, variants, modifiers per round; `hazardSeed` derived from it. The Deal screen labels it **"Practice deal (local seed)"** and shows the seed. M6 replaces `matchSeed` with the VRF output. | M6 becomes a one-line source swap; parity-tested |
| M4-5 | **Qualify counts** from `RoundTargets(P)` (port of `targets.ts`, P = occupants at roster lock incl. bots, 4…20). | Same numbers the program derives on-chain |
| M4-6 | **Disconnect grace 20 s** (doc 05 §4): the Rushling stays in the world on neutral input (it can be knocked or fall). A reconnect with the **reconnect token** (issued by the server at join, kept in `sessionStorage` in WebGL / memory on desktop) within 20 s takes the same slot and gets a full resync. After 20 s: eliminated with `reason = DISCONNECT` at the end of the current round (immediately in the lobby: slot freed). The guest identity/Relay token replaces this in M5. | Fair and simple; no ghost freezing exploits |
| M4-7 | **Eliminated players stay connected and spectate** (camera cycles alive players, Tab). They see results, intermission and the champion. Fan Picks are P1 (not M4); leave a clearly marked hook in Intermission. | Keeps players in the match |
| M4-8 | **Survival minimum-advance** (GDD §5.2): if fewer than N survive, only survivors advance; if exactly 1 survives the Survival round **or any later round**, the match ends with that player as champion (remaining rounds skipped, recorded as such in the event log). If 0 survive, the highest-placed of the round's ranking advances as the sole survivor (never a match with no champion). | GDD rule plus a defined edge case |
| M4-9 | **Elimination reason codes** (u8, a single C# enum used everywhere, documented in doc 06's notes as M4's proposal): 0 NOT_QUALIFIED, 1 GOO, 2 FELL_OUT, 3 LOWER_SCORE, 4 TIMEOUT_RANK, 5 DISCONNECT. | The program stores `reason: u8`; M6 needs one table |
| M4-10 | **Timings:** lobby countdown 20 s (or the host's Start; in practice the first human is host), deal screen 6 s, round intro 5 s (3 s fly-through + "3-2-1-RUSH" overlapping the M3 countdown), results 6 s, intermission 8 s, champion 8 s, receipt until "Back to lobby" (auto after 20 s). Total target 7–9 min (GDD §4.1). | GDD §12 |
| M4-11 | **Lobby room** = a small playable greybox area (`Map_Lobby`): players run, jump and bump while waiting; capsules "drop in" as players join (simple placeholder animation). | Genre-typical, cheap, fun while waiting |

### 3.1 Shared core (M4a)
- **`Shared/Deal.cs`**: port of `deal.ts`, reading the map pool from a generated C# mirror of `map_pool_v1.json` (extend the existing `MapPoolV1` mirror + its hash test). **Parity test against all of `deal_vectors.json`**.
- **`Shared/RoundTargets.cs`**: port of `targets.ts`; test P = 4…20 against TS-generated vectors.
- **`Shared/Transcript.cs`**: port of `transcript.ts` (init + next, tags, `NO_SLOT`); `roster_hash` per doc 06 (`(slot, H(user_id), wallet?)`; for guests and bots use a documented placeholder user id, `TODO(M5)` for real identities). Parity test against TS-generated vectors.
- **`Shared/MatchDirector.cs`** (engine-free, driven by `SimCore` ticks): states `LOBBY_OPEN → COUNTDOWN → ROSTER_LOCKED → DEALING → DEALT → (ROUND_INTRO(r) → ROUND_ACTIVE(r) → ROUND_RESULT(r) → INTERMISSION)×4 → CHAMPION → FINALIZING → DONE → LOBBY_OPEN`, plus `ABORTED` (doc 05 §4; `FINALIZING` is a no-op in M4, `TODO(M6)` for commit + undelegate).
  - Owns the roster (slot → human / bot / reconnect token / alive), `RoundParams` per round, the per-round `IRoundRules` (Race, Survival, Skill, Final), qualification, elimination with reasons, the champion.
  - Emits **`MatchEvent`s** (seq, tag, round, slot, arg) in program order, appends them to the transcript, sends them to clients (`evt`, reliable, seq'd) and writes a local append-only **`match-<id>.jsonl`** log (git-ignored) that M5's outbox will ingest.
  - Deterministic: the same seed, roster and inputs give the same events and transcript (test).
- **`IRoundRules`** interface implemented by the existing `RaceRules` (adapt, don't rewrite) and the three new rules; each returns a `RoundResult` (ordered slots, qualified flags, per-slot reason, round stats).
- **`Shared/SurvivalRules.cs`** (GDD §5.2): goo kills on contact; first N to the summit pad qualify ("escaped", they leave the sim); timer end → every survivor qualifies, ranked by height then slot; too many survivors → top N by height; fewer than N alive → minimum-advance (M4-8).
- **`Shared/SkillRules.cs`** (GDD §5.3): stars from a seeded timeline (waves of 8 normal, a golden +3 every 15 s on an island); server-validated pickup (radius + the server position); a knockback drops **one** carried star that pops off and can be re-grabbed; 60 s; top N by score; ties: earliest time at final score, then slot.
- **`Shared/FinalRules.cs`** (GDD §5.4): tiles wobble 0.6 s after first contact, then drop; spinner bars sweep the top shelf from 60 s; below y < −6 → eliminated; last standing wins; 120 s tiebreak: lowest layer reached (higher is better), then fewer tiles touched, then slot.

### 3.2 New hazards / kit types
- **`GOO_PLANE`**: pure `y = y0 + v·max(0, t − delay)` (integer mm/ticks), kills on contact (an effect in `PlayerStep.Run`, so prediction shows your own death at the right tick). Rendered as a rising translucent purple glitter plane (greybox).
- **`CRUMBLE_TILE`**: reactive (§3.4): solid → wobbling (on first contact, server decides) → dropped (collider off) at `contactTick + 18`.
- **`STAR_PICKUP`**: positions from the seeded timeline (pure); collected state reactive (§3.4).
- **Occupancy-triggered falling blocks** (Goo Tower, doc 08 §5): reactive, same mechanism as tiles.
- Each gets a def, an allowlist, authoring component (one class per file), client visuals with telegraphs (tile wobble, goo rise warning "Goo rising!" subtitle), vectors where pure, and tests.

### 3.3 Messages (protocol v4)
- Keep `in`, `snap`, `round`, `rstate`, `evt`. Add only what you need, e.g. `match` (state, timers, roster summary: slot, name, BOT flag, alive, connected), `rx` (reactive deltas with seq), `ready` (client → server: map loaded + hash ok), `resume` (client → server: reconnect token).
- On join or reconnect the server sends a **full** state (match, round, reactive state, event log tail), then deltas. A client never has to have seen earlier messages to show the right screen.
- Record sizes; measure bytes/s per client in the heaviest round (Crumble Shelf with 243 tiles).

### 3.4 Reactive elements (tiles, falling blocks, stars)
- Server-owned compact state (bitsets / small arrays) per round, changed only by server rules, each change stamped `(seq, effectiveTick)`.
- Replicated with `rx` deltas on the reliable channel; a gap in seq triggers a full resend.
- **Prediction:** the client applies a reactive change at its `effectiveTick` in `PlayerStep.Run` (both sides use the same state-at-tick lookup). A tile's drop is scheduled 18 ticks after contact, which is longer than the RTT, so a client normally knows it before it happens. Measure how often a reactive change arrives **after** its effective tick on the 150 ms condition and how big the resulting correction is.
- Server validation: a star pickup counts only if the server's own capsule touches it; tile contact is the server's own ground contact. Never trust client claims (there are none: inputs only).

### 3.5 BotBrain (`Shared/BotBrain.cs`, built on `WaypointBot`)
- Same sanitiser and motor as humans; labelled BOT everywhere (snapshot flag, HUD, results, event log, `roster_hash` placeholder).
- **Skill tiers** (easy / normal / good) chosen per bot from the match seed, with noise: reaction delay, steering wobble, imperfect jump/dive timing, occasional wrong route, a small chance of hesitation at doors and mallets. Tune so that **a human of average skill can qualify in most rounds and bots don't all finish in a bunch**.
- Per mode: Race (routes), Survival (climb routes, avoid goo, use lifts), Skill (target the best reachable star, return to dropped stars, avoid bumpers), Final (keep moving, prefer intact tiles, move down shelves when the current one breaks, avoid spinners).
- Bots know only what a player could know (their own state, pure hazard poses, replicated reactive state).

### 3.6 Screens and HUD (greybox, `UiKit`; doc 17 §5 layout)
- **Menu:** Play (Quick Play = connect to the configured server), "Connect Wallet" stays as the M1 smoke path.
- **Lobby (in `Map_Lobby`):** player list with BOT tags, connection dots, countdown, host Start button, "Bots will fill empty slots".
- **Deal (6 s):** spinning placeholder → reveal of the 4 rounds' maps, variants and modifiers; "Practice deal (local seed)", seed hex.
- **Round intro:** map name, mode, modifier chip, goal line ("First 8 to the finish qualify"), fly-through, 3-2-1-RUSH.
- **HUD:** top-left round + modifier chip; top-right timer; top-centre qualified X / N (or "Stars: 5" / "Alive: 3"); bottom-left ping / connection / reconnecting indicator; bottom-right chain chip fixed at `● Server` (grey) with a `TODO(M6)`. F3 debug HUD stays.
- **Round result:** QUALIFIED / ELIMINATED banner per player, the round ranking.
- **Intermission (8 s):** leaderboard (alive / out, per-round places), next round preview, the Fan Picks hook (disabled, "coming soon").
- **Champion:** the winner's Rushling on a podium, simple crown pop (placeholder).
- **Receipt:** placements, per-round results, match id, seed, transcript hash (short), "Chain: not recorded (practice)"; "Back to lobby".
- **Spectate** for eliminated / finished players; accessibility: reduced motion, subtitles for cues ("Goo rising!", "Tiles crumbling!"), colorblind-safe player outlines if cheap.

### 3.7 Disconnects and reconnects (M4-6)
- WebGL: on disconnect show "Reconnecting…" and retry with the token every 2 s up to 20 s; then "You were eliminated (disconnected)" and spectate on success later, or back to the menu.
- Tests: an EditMode `MatchDirector` test (drop at round 2, reconnect at 10 s → same slot, still alive; drop for 25 s → `DISCONNECT` event); and a real run (§5.4) with the probe client killed and restarted.

## 4. Maps (M4b), code-built and re-runnable (`Editor/<Map>Builder.cs` → `Scenes/Map_<Name>.unity`)
Follow doc 08 §5–7 and the §8 checklist; reuse `KitBuilder`; record each map's content hash in `Tests/Vectors/map_hashes_v1.txt` (or `_v2`).
- **`Map_Lobby`:** small playable plaza, 12 spawns, a few bumpers and a spring pad, no eliminations, a "capsule drop" spawn effect.
- **`Map_GooTower`** (Survival): 40 m vertical capsule-machine tower; climb sections (stair blocks, moving lifts, conveyor ramps spiralling up, bumpers on narrow ledges, summit pad); goo delay 6 s, speed tuned so an average climber keeps a 6–8 s margin; occupancy-triggered falling blocks; variants (lift phase set; shortcut open A / closed B). **Constraint:** the goo never overtakes the slowest valid route at reference speed (QA asserts it).
- **`Map_StarScramble`** (Skill): Ø 36 m arena, central turn table, four outer moving-platform islands, bumpers, two spring pads to the islands; star timeline; variants (island path shapes A/B).
- **`Map_CrumbleShelf`** (Final): three shelves (y 12 / 6 / 0) of 9×9 square 1.6 m tiles with rounded corners and alternating colours; crumble rule; spinner bars on the top shelf from 60 s; elimination y < −6; variant (spinner timing). Use GPU instancing for tiles; measure draw calls and FPS.
- Every map: ≥ 2 routes/approaches where the mode allows, spawns safe and non-overlapping, `Gameplay` vs `VisualOnly` layers, allowlists pass, telegraphs readable.

## 5. Verification

### 5.1 EditMode tests (add to the 207; report counts)
- Deal / RoundTargets / Transcript parity (all vectors).
- `MatchDirector`: full state walk with fake rules; event order and seq exactly as doc 06; transcript equals the TS reference for a scripted match; minimum-advance edge cases (0 / 1 / fewer than N survivors); roster lock; no late joiners; practice bot fill; determinism (same seed → same events).
- Survival / Skill / Final rules: every ranking and tiebreak rule in GDD §5.2–5.4, goo kill timing, star drop on knockback (exactly one), pickup validation, tile wobble → drop at +18 ticks, spinner start at 60 s, last-standing win, 120 s tiebreak.
- Reactive elements: delta/full equivalence, seq gap → full resend, prediction applies changes at `effectiveTick` (prediction vs a server twin bit-identical through tile drops, goo and star pickups).
- New pose functions in the hazard vectors; map hash stability for every map.
- Reconnect (M4-6) cases.

### 5.2 Map QA (doc 08 §9), Mac dev server, headless
- Every new map × variant × allowed modifier × **100 runs** with 12 `BotBrain` bots at reference speed **and** at normal skill: no soft-lock, the mode always produces a valid result, goo never overtakes the slowest valid route (reference speed), Final always produces exactly one champion, Skill scores are consistent with pickups, median durations within the GDD §4.1 targets (Survival 90–120 s, Skill 60–75 s, Final 60–120 s). Re-run Block Party Dash QA with `BotBrain` normal skill and report its median.
- Tables into `docs/proof/m4-map-qa.md`; tune and record each change and why.

### 5.3 The unattended match soak (doc 14 M4 verify)
- **20 complete 12-bot matches** back to back on the Mac dev server, headless (`-fallrushBots 12 -fallrushMatches 20` or similar), with random seeds. Assert: every match reaches CHAMPION → DONE → back to LOBBY, exactly one champion, event seq contiguous, transcript recomputed from the jsonl log equals the server's, no exception, no soft-lock, server memory stable (RSS at match 1 vs 20). **Pass: ≥ 19/20 (GDD §14: ≥ 90 %), target 20/20.**
- Record per match: duration, rounds played, champion slot, server sim p95, RSS.

### 5.4 Real-client runs
- **Local, 3 probe clients + 9 bots, one full match** (localhost and 150 ms + jitter + 2 % loss via `scripts/m2-netem.sh`): corrections per 5 s per round (hazard / push / reactive / other), reactive-late count, hazard mismatches (must stay 0), RTT, bytes/s, server sim p95. Pass bar: < 1 non-push correction per 5 s; 0 hazard mismatches.
- **Reconnect run:** kill one probe client mid-round 2 and restart it within 20 s (same slot, still alive); kill another for 30 s (eliminated `DISCONNECT`).
- **WebGL:** one release build (Editor closed) after the maps are in; play one full match in Chrome against the Mac dev server with bots (`?bot=` script if useful); measure FPS in each map (12 Rushlings), draw calls in Crumble Shelf, build time and size vs M3 (16.22 MB). Run the hazard self-test in WebGL headless and report the digest.
- Give me exact steps to play a full match myself (Editor and local WebGL).

## 6. Docs to update in the same change
- **doc 01:** §4–5 anything that changed (timings, edge cases M4-8, reason codes), §12 the screen flow as built.
- **doc 05 §4:** the state machine as built (timings, the `FINALIZING` no-op, reconnect token as the M4 stand-in for the guest token).
- **doc 06 notes:** the M4 reason-code proposal and the exact event order the director emits (for M6).
- **doc 04:** §4 protocol v4 and sizes, reactive elements, scene flow (M4-3), reconnect; §7 screens as built; §9 BotBrain; §11 tests.
- **doc 08:** §2 the new kit types; §5–7 as built (real sizes, tuned values, hashes); §9 QA results.
- **doc 15:** decisions M4-1…M4-11 recorded (✅ made by Claude per your instruction, flag any you want changed); R14 update; any new risk.
- **`docs/proof/M4-match.md`:** everything measured, with method; the soak table; the reconnect run; WebGL FPS per map; small PNG screenshots in `docs/proof/m4/` (lobby, deal, each map, intermission, champion, receipt, spectate).
- **`PROJECT_STATUS.md`:** M4 status, notes for M5 (the jsonl event log = outbox input; reconnect token → guest token) and M6 (seed source swap, events → ER, reason codes, roster hash, `FINALIZING`).
- **`README.md`:** an "M4: run it" section (play a full match; run the soak; run QA).

## 7. Definition of done (report each item with evidence)
- [ ] Deal / RoundTargets / Transcript C# ports with parity tests green
- [ ] `MatchDirector` with all states, timings, event log (jsonl) and transcript; deterministic
- [ ] Survival, Skill and Final rules with every GDD tiebreak; minimum-advance edge cases
- [ ] `GOO_PLANE`, `CRUMBLE_TILE`, `STAR_PICKUP`, falling blocks; reactive replication with seq + full resync; prediction exact
- [ ] `Map_Lobby`, `Map_GooTower`, `Map_StarScramble`, `Map_CrumbleShelf` built from code, checklist done, hashes recorded
- [ ] `BotBrain` with skill tiers and per-mode behaviour, labelled BOT
- [ ] All screens (lobby → deal → intro → HUD → result → intermission → champion → receipt → lobby) and spectate
- [ ] Disconnect grace 20 s + reconnect to the same slot; `DISCONNECT` after grace
- [ ] EditMode tests green (counts); map QA tables; **soak ≥ 19/20 (target 20/20)**
- [ ] Local + 150 ms full-match runs, reconnect run, WebGL full match with FPS per map, self-test digest
- [ ] Docs 01 / 04 / 05 / 06 notes / 08 / 15, proof, `PROJECT_STATUS.md`, `README.md` updated
- [ ] No secrets or build output in git; local stack stopped; **no VPS action taken**

## 8. Final report format
1. What was built (files)
2. Test results (counts), parity results, map QA tables, the soak table
3. Real-client run data (localhost / 150 ms / reconnect / WebGL), with your honest read
4. Decisions you made beyond §3.0, and why
5. Deviations from the docs, and why
6. Risks and unknowns for M5 (Relay, outbox, identity) and M6 (VRF seed swap, ER events, deploy)
7. How I can play a full match myself: exact commands for Editor and local WebGL
8. Next milestone: M5 (Rush Relay) + M6 (chain integration + VPS deploy), per `docs/14`
