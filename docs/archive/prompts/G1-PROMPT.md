You are the senior gameplay engineer and game-feel designer on **Fall Rush**, and you also know Solana and MagicBlock well. Fall Rush is an original 12-player knockout party game for the MagicBlock Solana Blitz V10 hackathon, and the deadline is **2026-11-27 14:00 UTC**.

Your task is **G1: gameplay polish**, the first part of Phase A in `docs/14` (v3). M4 delivered a complete 4-round match that works (soak 20/20, 254 EditMode tests), but **it does not feel good yet**: the player moves too fast, the jumps are not a challenge, the animations lack the genre's physical comedy, and there are many small bugs. G1 makes the game **feel right and work cleanly**, so that G2 (look) and G3 (juice) can put art on top of a game that will not change underneath.

Run it end to end without a checkpoint pause. Stop only for a blocker (§2.4) or a decision that is genuinely mine (§2.5).

Out of scope: art, materials, lighting, audio and VFX (G2/G3); `programs/`, `server/`, `web/`, any chain call (M5/M6); **any VPS action** (deferred to M6); new maps or modes.

## 0. Context: read these first, fully

The repo is `/Users/samya/Downloads/FallRush` (git, `main`, M0–M4 committed).

| Doc | What it gives you |
|---|---|
| **`docs/playtest/G1-notes.md`** | **The user's own play notes. Every item in it must be addressed and reported.** |
| `docs/14-IMPLEMENTATION-PLAN.md` §1 (v3 calendar) and §2 G1 | Why G1 exists, its exit check, the game freeze on Nov 22 |
| `docs/01-GDD.md` §4–7 (match, modes, controls, movement), §12–13 (UX flow, accessibility) | Design intent |
| `docs/17-ART-DIRECTION.md` §1 (originality), §2.3 (procedural animation), §4 (camera and game feel) | Feel and animation direction |
| `docs/04-UNITY-CLIENT.md` §4–7, §9, §11 | Netcode, motor, visuals, bots, tests as built |
| `docs/08-MAP-DESIGN.md` §2–9 | Kit, maps as built, QA |
| `docs/proof/M4-match.md` (esp. §D, §H, §J, §K, §M) and `docs/proof/M3-race.md` §I | Measured state and known gaps |
| `PROJECT_STATUS.md` | M4 status, decisions open for the user, notes |

Read the code before changing it (`client-unity/Assets/_Game/`): `Shared/KinematicMotor.cs`, `MovementConfig*.cs`, `Hazards/*`, `SimCore`, `MatchDirector`, the rules, `BotBrain`/`WaypointBot`; `Net/ServerMatch`, `ServerSim`, `ClientSim`, `ClientPrediction`, `NetBootstrap`; `Client/RushlingVisuals`, `ProcMesh`, `FollowCamera`, `MatchHud` (and the other screens), `SandboxView`, `UiKit`, `ReactiveViews`; `Editor/*Builder.cs`, `KitBuilder`. Keep every M2–M4 guarantee: prediction exact (0 hazard mismatches), R12, the TCP-stall fixes, reconnect, the soak, determinism and parity tests.

## 1. Non-negotiable rules

1. **Never invent** Unity / NGO 2.14.0 / UTP 2.7.5 / Input System 1.20.1 / Cinemachine 3.1.7 / URP 17.3 APIs, versions, flags or env vars. Sources of truth: `client-unity/Library/PackageCache/` and the Unity 6000.3 docs. Fix docs that disagree with reality in the same commit.
2. **No new packages** without asking me.
3. **Authority and determinism stay intact.** Everything that affects movement (new motor states such as stumble, slide, tumble) lives in the shared motor / `PlayerStep.Run`, is integer-quantised like the rest, and is covered by the prediction-vs-server-twin tests. Pure visuals stay client-only. Don't implement fallback F1.
4. **Config:** the new movement values go into a **new `packages/shared/config/movement_v2.json`** (canonical JSON + sha256 via `pnpm hash`, regenerated C# constants, hash test), and the game switches to it. Keep `movement_v1.json` unchanged for history. **Do not modify `ruleset_v1.json`, `map_pool_v1.json`, `modifiers_v1.json` or anything on-chain.** Modifier effects still apply on top of the new base values (re-check MoonBounce / ButterFloors QA).
5. **Originality.** We want the *genre feel* of a knockout party game (the "Fall Guys feel": bouncy, clumsy, physical comedy). We copy **no** Fall Guys assets, animations, characters, level layouts, names, UI or sounds. Bolt stays our capsule-toy Rushling; its animation is our own procedural animation built on the principles in §3.2.
6. **Trust and wording:** numbers are "measured" only if you ran them. Feel values from any outside reference are **targets to tune**, not facts; there are no official public numbers for Fall Guys movement. "Feels good" is **my** call.
7. **8 GB MacBook Air M2:** iterate in the Editor and the Mac dev server; at most **two WebGL release builds** in G1, Editor closed; never the Editor + a batch build + Docker at once; at most 3 browser tabs. The Mac arm64 build is not bit-identical with WebGL/Linux (M2 B9): judge determinism with the WebGL self-test.
8. **No VPS / Hostinger / SSH.**
9. **Git:** small local commits per fix group. **Never push, never add a remote.** No build output in git.
10. **Unity file rule:** one MonoBehaviour / ScriptableObject class per file, named after the class.

## 2. Workflow

0. If `git status` shows uncommitted `docs/prompts/G1-PROMPT.md`, `docs/playtest/G1-notes.md` or `PROJECT_STATUS.md`, commit them first as `docs: G1 prompt + play notes`.
1. **Study the reference, then our game, then plan** (write this plan in your reply, then proceed):
   1. **Genre reference (feel only).** Summarise, from your own knowledge and public descriptions (no official numbers exist), how knockout party-game characters move and react: momentum and acceleration, run speed **relative to body size and level scale**, a short, slightly floaty jump, the dive as a reach extender and belly-flop, stumbling after big falls, tumbling/flopping when hit and getting back up, bumping in crowds, the camera. Turn it into **our own feel targets** in body-heights per second, seconds to full speed, jump height in body heights, etc. Our Rushling is **1.0 m tall** and today runs **7 m/s = 7 body heights per second**, which is why it feels like a sprinter.
   2. **Our game.** Play full matches yourself (Editor + Mac dev server, and one WebGL run against it; `?bot=match` / `-fallrushProbeScript match` for unattended runs with screenshots) and **review a screenshot of every phase and every map**. Build a numbered bug list: the user's notes, the known issues below (§3.5), and everything you find.
   3. **Plan:** the new movement values with reasons; the animation work; the map retunes per section; the bug list with a fix approach and priority (P0 breaks play / P1 visible / P2 minor).
2. Build in this order, committing after each step with tests green: movement v2 + motor states → animation → map retunes + QA → UX / bug fixes → measurements + docs.
3. Run every build and test yourself. Fix errors; never weaken a test to get green.
4. **Time-box:** if one blocker eats more than ~2 hours, stop and report the error, logs, what you tried and the options.
5. **Decisions:** §3 makes the main ones. Make further small calls yourself, record them in the proof with the reason, and mark them 🟡 in doc 15 if they change a documented decision. Ask me only about things that change the game's identity.

## 3. Requirements

### 3.1 Movement v2 (the user: "too fast")
Starting values, to be tuned by playing (record final values and why):

| Constant | v1 | **v2 start** | Intent |
|---|---|---|---|
| Max run speed | 7.0 m/s | **5.0 m/s** (≈ 5 body heights/s) | A jog with weight, not a sprint |
| Ground acceleration | 60 m/s² | **≈ 28 m/s²** (≈ 0.18 s to full speed) | Momentum you can feel |
| Ground deceleration | 45 m/s² | **≈ 30 m/s²** | A little slide when you stop |
| Air control | 35 % | **30 %** | Commit to jumps |
| Gravity / fall multiplier | 24 / 1.35 | **≈ 20 / 1.35** | Slightly floatier |
| Jump apex | 1.6 m | **≈ 1.2 m** | A hop, not a leap |
| Dive | 9 / 3 m/s, cooldown 1 s | **≈ 7 / 2.5 m/s**, + a short **belly slide** on landing | Reach extender, comedic |
| Turn rate | instant | **limit facing turn speed** (visual + a small motor cost if it helps feel) | Weight |

New motor states (shared, predicted, tested):
- **Stumble:** landing after a fall of more than ~3 m → ~0.3 s at reduced speed with a wobble (no input loss).
- **Belly slide:** a dive that lands slides on the ground and decelerates; jump-cancel stays as the M2 skill tech.
- **Tumble:** knockback from a hard hazard is shown as a flop/roll (visual) during the existing stun; keep the immunity rules.

After the change, **re-run every map's QA** and retune geometry that was built for 7 m/s and a 1.6 m apex: gap widths, ledge and wall heights, spring-pad launches, lift and platform timings, door cycles, goo speed, star placement, timers. Movement v2 lengthens every round; check the GDD duration targets again (Race 75–110 s for humans; bot reference vs normal tiers).

### 3.2 Animation (the user: "follow the genre's animations")
Our own procedural animation on Bolt (`RushlingVisuals`, client-only, allocation-free per frame), built on these principles:
- **Run:** waddle and body bob tied to speed, peg feet stepping in time with ground speed (no foot sliding), arms swinging loosely, slight lean into acceleration and turns.
- **Idle:** breathing squash, blinking, small look-around; occasional fidget.
- **Jump / fall:** anticipation squash, stretch on take-off, arms up and flailing while falling, landing squash; **stumble** pose and recovery after a big fall.
- **Dive:** belly-flop pose, slide, scramble back up.
- **Hit / tumble:** flop over in the knock direction, roll, bounce, then a "get up" pop with stars; reads clearly from the follow camera.
- **Crowd:** bump reaction when pushed (wobble), even though pushes stay server-only.
- **Celebrations:** qualified (jump for joy), eliminated (slump), champion (crown pose); 2–4 **emotes** in lobby/intermission (keys 1–4, GDD §6), synced as a small message.
- Remote players interpolate the same states (send the needed animation flags in the snapshot if they are not derivable; keep `snap` small and record its size).

### 3.3 Map retunes
- **Block Party Dash S1 Block Hop** (the user's note: the gaps are too small and every block sits at the same low height): wider gaps that need a real jump at v2 (about 70–85 % of the v2 standing-jump distance), **height variation** (steps up and down of 0.3–0.7 m, a few higher "stair" blocks), varied block sizes, and the bobbing middle row tuned to v2 timing. Keep a safer slower lane (smaller gaps) and a riskier faster one.
- Every other section and map: retune for v2 so each obstacle is a real challenge and fair. Two routes per section where the mode allows. Keep the M3 lengthened course and the M4 fixes.
- **The Final is too short** (bots 27–32 s vs GDD 60–120 s): tile wobble 0.6 → **1.0 s**, and **top-shelf tiles respawn** after ~8 s (telegraphed), so the final lasts and stays tense. Measure; adjust the grid if still short.
- **Goo Tower is short** (bots ≈ 66 s vs 90–120 s): v2 movement will lengthen it; then tune goo speed / an extra lap so the slowest valid route keeps a 6–8 s margin.
- Re-run the map QA (100 runs per map × variant × allowed modifier, reference + normal bots) and the **20-match soak**; both must pass.

### 3.4 BotBrain
- Retune for v2 (jump timing, dive use, door/mallet waits) and add the new states (stumble recovery, slide).
- Bots should feel human: not all finishing in a bunch; an average human can qualify in most rounds.

### 3.5 Bug fixes and UX (known list; add everything you find)
From the M4 proof and a screenshot review:
1. **Nameplates:** world-space labels are oversized near the camera, overlap, stack ("BOT BOT") and clutter the view. Make them screen-space, constant size, distance-faded, occlusion-aware, name with a small BOT tag, your own marker distinct.
2. **Developer text in the world** ("MID: JUMP", "CONVEYOR RAMP (3.5 m/s against: safe, slower)", "SPRING HOPS (fast, risky)", section titles): remove from release builds. Replace with simple in-world signs or icons only where they help (G2 makes them pretty).
3. **Display names:** "BlockPartyDash" → "Block Party Dash" (all maps); modifier chips with friendly names ("No Chaos Capsule" → "No modifier" or a clear label); mode goal lines per round.
4. **Camera:** clips into geometry and gets blocked in Goo Tower (screenshot `play-gootower.png`); fix occlusion handling (deoccluder / pull-in / fade), a better default height and distance for v2 speed, and smooth spectate transitions.
5. **Screen layout:** the champion name overlaps the Rushling; the "QUALIFIED X/N" banner overlaps the world title area; the bottom help bar is always on (show it for a few seconds, then on F1).
6. **Connection:** WebGL initial connect gives up after 10 × 1 s through a lossy link (M4 §F); raise attempts with backoff and show "Connecting… (attempt n)" with a Retry button.
7. **Hidden tab and tab close/reopen within 20 s** (R12 and the reconnect token) are UNVERIFIED in a real browser: write exact steps for me in the proof and test as far as automation allows.
8. **Draw calls over budget** on Block Party Dash and Goo Tower (M4 §H): fix cheap wins now (static batching / instancing of repeated blocks); materials and art budgets are G2.
9. **Repo hygiene:** `client-unity/Assets/Resources/PerformanceTestRunInfo.json` / `PerformanceTestRunSettings.json` (+ `.meta`) appear after test runs: git-ignore them, don't commit them.
10. Every item in `docs/playtest/G1-notes.md` "Bugs" and anything you find while playing.

### 3.6 Settings and onboarding (small)
- Settings: mouse sensitivity, invert Y, FOV, camera distance, reduced motion, master/music/SFX volume (sliders wired for G3), show nameplates on/off. Saved locally.
- Name entry on the menu (passed as `?name=` / hello name today).
- One-line mode hint on each round intro, and a short controls card in the lobby.

### 3.7 Developer tuning aid
- A **local practice mode** (dev builds and Editor only) that runs the shared motor and a map's hazards on the client without a server, with an F-key panel to adjust movement values live and print them as JSON. It speeds up feel tuning. It must not ship enabled in release builds.

## 4. Verification
- EditMode tests green (report counts; add tests for movement v2, stumble, slide, every changed hazard, the respawning tiles, the bug fixes that are testable).
- Prediction vs server twin bit-identical with movement v2 through every hazard and reactive element; hazard vectors re-generated if pose inputs changed (v3 file if needed; old ones still pass or documented).
- Map QA: all maps × variants × modifiers × 100 runs, reference + normal bots: PASS, with median durations vs GDD targets in a table.
- Soak: 20/20 complete matches.
- Real-client runs: localhost and 150 ms + jitter + 2 % loss full match; non-push corrections < 1 per 5 s; 0 hazard mismatches; 0 reactive late (or measured).
- **WebGL release:** one full match in Chrome; FPS per map (target 60), draw calls, build size vs M4 (16.45 MB); self-test digests.
- Screenshots of every phase and map after the fixes (small PNGs in `docs/proof/g1/`), plus a before/after pair for nameplates, S1 Block Hop and the camera in Goo Tower.

## 5. Docs to update
- `docs/01-GDD.md` §6–7 (controls, movement v2 table), §5.4 (Final timing changes).
- `docs/04-UNITY-CLIENT.md` §5 (motor v2 and new states), §6 (animation as built), §7 (screens/UX), §11 (tests).
- `docs/08-MAP-DESIGN.md` §3–7 and §9 (retunes and QA results).
- `docs/17-ART-DIRECTION.md` §2.3 (animation as built; what G2/G3 should build on).
- `docs/15-DECISIONS-AND-RISKS.md` (decisions G1-n, risks).
- **`docs/proof/G1-polish.md`**: the reference summary and our feel targets, movement v1 → v2 with reasons, the full bug list with status, QA / soak / real-client / WebGL numbers, before/after screenshots.
- `docs/playtest/G1-notes.md`: mark each user item as fixed / changed / needs the user's check.
- `PROJECT_STATUS.md`: G1 status and notes for G2/G3 (budgets, what visuals exist, what to restyle).
- `README.md`: "G1: run it" (play a match; the local practice tuning mode).

## 6. Definition of done
- [ ] Reference study + our feel targets written; plan posted
- [ ] `movement_v2.json` + hash + generated constants + tests; stumble, belly slide, tumble; prediction exact
- [ ] Animation set (run, idle, jump/fall, stumble, dive/slide, tumble/get-up, bump, celebrations, emotes)
- [ ] S1 Block Hop rebuilt (wider gaps, height variation); all maps retuned for v2; Final and Goo Tower within or near GDD targets
- [ ] BotBrain retuned
- [ ] Every bug in §3.5 and in the user's notes fixed or explained
- [ ] Settings, name entry, mode hints, controls card; local practice tuning mode (dev only)
- [ ] Tests, map QA, soak 20/20, real-client runs, WebGL full match at ~60 FPS
- [ ] Docs, proof, notes file, status, README updated; no build output or secrets in git; **no VPS action**

## 7. Final report format
1. The genre reference summary and our feel targets
2. Movement v1 → v2 (final values and why)
3. Animation work done
4. Map retunes (per section) and the QA table with durations
5. Bug list with status (P0/P1/P2)
6. Test, soak, real-client and WebGL numbers
7. Decisions made and deviations, and why
8. What I should check when I play (exact commands), and what G2/G3 should know
