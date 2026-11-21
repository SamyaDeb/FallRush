# 15 — Decisions (ADR log) & Risks

**Status:** v2 (2026-11-18). ✅ = approved by you · 🟡 = Claude's default, awaiting your confirmation.

## Decisions

| ID | Decision | Status | Notes |
|---|---|---|---|
| ADR-001 | Original IP, genre-level inspiration. Gacha-capsule Rushlings with noodle arms. Fall Guys-style *feel*, original expression. | ✅ | doc 17 §1 guardrails |
| ADR-002 | Server-authoritative physics on a Unity headless dedicated server | ✅ | |
| ADR-003 | ER records competitive transitions (not frames) and **is** the live system of record for outcomes | ✅ | |
| ADR-004 | VRF deals the map, variant, modifiers and hazard seed; the **deal is derived inside the callback** | ✅ | Fixes v1 contradiction C4 |
| ADR-005 | Wallet optional; guests play everything | ✅ | |
| ADR-006 | No paid entry or cash prizes; Fan Picks are free points | ✅ | |
| D-1 | NGO 2.14 + custom prediction; FishNet as the post-hackathon alternative | ✅ | |
| **D-1b** | **Fix first, then fallback F1.** If the Nov 22 go/no-go fails, diagnose and fix the server-authoritative prediction first (doc 04 §4.5 fix window, until **Mon Nov 23 12:00 UTC**). Only if it still fails, switch to F1: owner-predicted movement with server validation. | ✅ (revised 2026-11-18) | You chose fix-first. F1 is the last resort: weaker anti-cheat, disclosed. |
| D-2 | MagicBlock tuple = official examples: Anchor 1.0.2 + ER SDK 0.16.2 (`anchor`,`vrf`) + session-keys 3.1.1 | ✅ | doc 07 §1 |
| D-3 | VRF requested **inside the ER** (ephemeral queue) | ✅ | Proven on devnet in M0 (callback 426 ms). | Pattern from `roll-dice-delegated` / `rewards-delegated-vrf` |
| **D-4** | **WebGL client** (was: desktop). WebSocket transport everywhere. | ✅ | Every past Blitz winner had a playable URL. UDP is impossible in browsers. |
| **D-5** | **Hostinger VPS** (your existing KVM 2: 2 vCPU, 8 GB, Ubuntu 24.04, Mumbai). Docker Compose for Fall Rush services bound to `127.0.0.1`; the VPS's existing **nginx** terminates TLS on new subdomains (Let's Encrypt). Replaces Railway. | ✅ (2026-11-18) | Your choice. Mumbai is close to `devnet-as` (Asia) and to you (≈45 ms ping measured). The VPS already serves another site; Fall Rush must not touch it (doc 13 §2). |
| D-6 | 4 rounds: Race → Survival → Skill → Final, 12→8→5→3→1 | ✅ | |
| D-7 | Art: Claude-led direction with you; capsule shape; noodle arms; logo hero "Bolt" is canonical | ✅ | |
| D-8 | Solana.Unity-SDK in the client for wallet connect; **Fan Picks on the web page** with gum-sdk session keys | ✅ | Proven TS session-key path |
| D-9 | Drop Redis; **keep Postgres** (Docker container on the VPS, `postgres:16`, volume + nightly `pg_dump`) | ✅ | |
| D-10 | Single rotatable `game_authority` per env; never upgrade/treasury | ✅ | |
| D-11 | Single `Match` PDA with slot-indexed roster; on-chain rolling transcript | ✅ | doc 06 §1 |
| D-12 | Commit to Solana **after every round** (≤ 5 commits/match, within the 10-commit limit) | ✅ | Visible Solana proof each round |
| D-13 | Bots run inside the server process, labelled; ranked requires ≥ 4 humans | ✅ | |
| D-14 | Bonk Factory is stretch; R1 pool = Block Party Dash (VRF still picks variants and modifiers) | ✅ | Time |
| **D-15** | **Solana.Unity-SDK pinned to upstream `main` commit `1da8fd7f`** instead of tag v1.2.9 | ✅ (2026-11-18) | v1.2.9 breaks every IL2CPP/WebGL build (BouncyCastle DLL file name, upstream #257). `1da8fd7f` = v1.2.9 + the fix (2 commits). Alternative: the doc 04 §8 web wallet-link fallback |
| **D-16** | **Local transport tests on Apple Silicon use a native macOS dev server**; the Linux x86_64 Mono image is first run on the VPS | ✅ (you chose it in M1) | Mono Linux crashes under Rosetta and QEMU (`x86-codegen.h:410`). Shipped server stays Linux x86_64 Mono. Option kept open: IL2CPP Linux server (runs under emulation, slower builds) |

### M4 decisions (2026-11-19; ✅ = made by Claude per your instruction in the M4 prompt — say if you want any changed)

| ID | Decision | Status | Notes |
|---|---|---|---|
| M4-1 | One match per server process; after the receipt the server returns to the lobby | ✅ | as built (`ServerMatch`) |
| M4-2 | Practice matches only; the lobby opens with ≥ 1 human; bots fill to 12 at the roster lock | ✅ | ranked (≥ 4 humans, on-chain) is M6 |
| M4-3 | Our own scene flow (no NGO scene management): `round` names map + hash, clients `ready`, 10 s load timeout | ✅ | late loaders: protected (advance) if the round can still qualify ≥ 1 without them, else ranked last — **🟡 detail chosen by Claude** |
| M4-4 | Deal from a local 32-byte seed through the real `Deal` port; "Practice deal (local seed)" | ✅ | per-round hazard seed = `sha256("fallrush-hazard-v1" ‖ seed ‖ r)[0..8]` 🟡 |
| M4-5 | Qualify counts from `RoundTargets(P)` | ✅ | the last round of a shortened dev plan crowns 1 🟡 |
| M4-6 | 20 s disconnect grace, reconnect token, `DISCONNECT` at the end of the round | ✅ | a DC'd top finisher's place is **not** handed down (qualified count may drop below the target) 🟡 |
| M4-7 | Eliminated players stay connected and spectate (Tab) | ✅ | Fan Picks hook in Intermission, disabled |
| M4-8 | Survival minimum-advance; one survivor → champion; zero → the highest placed | ✅ | applied after **every** round by the director |
| M4-9 | Reason codes 0 NOT_QUALIFIED, 1 GOO, 2 FELL_OUT, 3 LOWER_SCORE, 4 TIMEOUT_RANK, 5 DISCONNECT | ✅ | doc 06 §10 |
| M4-10 | Timings: lobby 20 s, deal 6 s, intro 5 s, results 6 s, intermission 8 s, champion 8 s, receipt ≤ 20 s | ✅ | intro = 2 s fly-through start + 3 s 3-2-1 |
| M4-11 | `Map_Lobby` playable plaza, capsules drop in | ✅ | |
| M4-12 | Goo Tower: 24 m sides (not 18), ledge lips on the bumper lap, goo 0.30 m/s after 6 s | 🟡 | tuned by the map QA (docs/proof/m4-map-qa.md) |
| M4-13 | Map QA runs Survival / Skill / Final with the players those rounds have in a 12-player match (8 / 5 / 3), not 12 | 🟡 | a 12-bot Final consumes 81 tiles in ~3 s; the prompt said 12 |
| M4-14 | Bots' noise (wobble, reaction delay, slower stick) is off during precise moves (gap jumps, landings, platforms, airborne) | 🟡 | with it on, normal bots fell ~15× per race |

### G1 decisions (2026-11-19; made by Claude per the G1 prompt, recorded in docs/proof/G1-polish.md §G — say if you want any changed)
| ID | Decision | State | Notes |
|---|---|---|---|
| G1-1 | Movement v2 = the prompt's start values unchanged (5 m/s, 28/30, 30 %, 20/1.35, 1.2 m, dive 7/2.5) + belly slide 0.45 s at 12 m/s², stumble > 3 m: 0.3 s at 55 % | 🟡 (GDD §7 v1 table replaced; feel = your call) | tune with the practice mode (F6/F7) after your playtest |
| G1-2 | Stumble / slide reuse `LandTicks` + one new flag bit (`Stumbling`); snapshot layout unchanged, protocol v5 | ✅ | |
| G1-3 | Turn rate is visual only (≤ 720°/s); the motor facing stays instant so the dive direction is exactly the input; the motor's turn cost is the lower accel | 🟡 | prompt allowed "a small motor cost if it helps" |
| G1-4 | Final: wobble 1.0 s + top shelf respawn 8 s (no grid change) | ✅ | bots ≈ 73 s |
| G1-5 | Goo Tower: geometry fixes only (4 blocks, single-step gap, slower belts); goo speed kept 0.3 m/s | 🟡 | bots 85–89 s, 1–5 s under the 90 s floor; humans are slower |
| G1-6 | S1 right-lane gaps 2.0–2.1 m (≈ 65–70 % of reach), below the prompt's 70–85 % | 🟡 | your playtest note: the 2.4–2.6 m first cut failed too often |
| G1-7 | Developer labels tagged `EditorOnly` (Unity strips them from player builds), titles / CHECKPOINT / FINISH kept as signs | ✅ | |
| G1-8 | WebGL static batching turned on (Standalone already had it) | ✅ | draw calls |
| G1-9 | Emotes relayed by the server as `emo` (not logged, not in the transcript), rate 1/s/player, outside round play | ✅ | |
| G1-10 | Bots approach precise jumps at 70 % stick when the hop apex > 1.3 m (MoonBounce) | ✅ | MoonBounce races hit the 150 s timer otherwise |
| G1-11 | Initial connect: 6 tries (1, 2, 4, 4, 4 s backoff) on top of UTP's own 10 × 1 s | ✅ | |

### G2 / G3 decisions (2026-11-19; made by Claude per the G2+G3 prompt, recorded in docs/proof/G2G3-look-juice.md — say if you want any changed)
| ID | Decision | Status | Note |
|---|---|---|---|
| G2-1 | Toon shading is a hand-written URP HLSL shader (`FallRush/Toon`), not Shader Graph | 🟡 (doc 17 §3.2 said "a single Shader Graph") | headless batch builds; reviewable text; outline pass in the same shader; only instancing × fog variants |
| G2-2 | Lighting is flat (one sun, trilight ambient, cel ramp, vertex AO), **not baked** | 🟡 (doc 17 §3.2 said "baked indirect, light probes") | 8 GB machine, code-rebuilt maps, no lightmap size; toon look |
| G2-3 | One art pass (`MapArt` + `MapDressing`) run by every builder: toy-block meshes, merged hazard groups, toon materials by role, sky, dressing; colliders / hazard defs / transforms untouched | ✅ | map hashes unchanged (tested) |
| G2-4 | A Rushling is one body renderer (lid hinged and key spun in the shader) + one limbs mesh; ≤ 2,330 tris | ✅ | draw calls: ~20 → 2 renderers per Rushling |
| G2-5 | "My Rushling": humans **pick** one of 12 presets on the menu (saved locally, sent in hello); bots get a preset from their slot | 🟡 (doc 17 §2.4 said "assigned at random by the server") | the prompt's G2.2 wording; no stats |
| G2-6 | `match` slot rows carry the preset (format v2) → **protocol v6** (`hello` unchanged: the byte existed) | ✅ | old clients are refused with the readable mismatch message |
| G2-7 | Lobby capsule machine gets a box collider matching the visual (the greybox glass box had none: collision = visuals) | 🟡 | lobby only; no QA impact (no eliminations, not hashed); soak re-run |
| G2-8 | Bolt's noodle-arm tube winding fixed (faced inward since M2; the outline exposed it) | ✅ | visual only |
| G2-9 | Checkpoint banners hang at 5 m (above the follow camera), signs hide within 8 m of the camera | ✅ | the camera passed through 3 m banners (step 0 class of bug) |
| G3-1 | Fonts: Lilita One (display), Varela Round (body, tabular digits), Noto Sans Symbols 2 (fallback), all OFL, as **static** TMP SDF atlases | ✅ | every glyph renders in WebGL; TTFs not in builds |
| G3-2 | All music / SFX are **synthesised by us** (`tools/audio/synth.py`), no CC0 packs | ✅ | no licence risk; the user judges the sound |
| G3-3 | No AudioMixer asset: Music / SFX / UI buses, ducking and pooling in code (`AudioDirector`) | 🟡 (prompt said "Mixer with … groups") | a mixer asset cannot be created from code; works the same on every platform |
| G3-4 | Hit-stop = the stunned Rushling's visual pose holds 60 ms (Drive skipped); nothing else pauses | ✅ | rule 4: sim / tick / input / prediction untouched |
| G3-5 | The claw plucks only Rushlings still visible at the round result (fell-out / gooed players already vanished: poof / goo splash instead) | ✅ | client-only presentation |
| G3-6 | `IChainAdapter` + `PracticeChainAdapter`; chip states cycle with F9 (dev) or `?chain=cycle` | ✅ | contract in doc 04 §7.y |

### G4 decisions (2026-11-19; from the approved G4 plan page and `docs/prompts/G4-PROMPT.md` §3 defaults — say if you want any changed)

| ID | Decision | Status | Notes |
|---|---|---|---|
| G4-1 | **Shared routes, no free path.** Doc 08 §1 rule 4 replaced by the plan page's rules 1–7: every route has a hazard, the slowest valid route of a section costs 1.0–1.25× the fastest, one main route per section with short risky shortcuts | ✅ | measured per section with `-fallrushQaRoutes`; baseline `docs/proof/g4/route-cost-before.md` |
| G4-2 | **Lid Flip replaces the reactive crumble grid in the Final** (`LID_FLIP`: pure-function lid states from the hazard seed + ring retire). The `CRUMBLE_TILE` grid and `ReactiveSet` stay in the code as the fallback (G4 prompt §2.4 cut order) | ✅ | no reactive replication; in M6 the flip pattern comes from the VRF seed |
| G4-3 | **Display names separate from map keys** (D1): Capsule Run / Goo Tower / Star Fountain / Lid Flip. Keys, ids, modes and scene names unchanged (the map pool hash is on-chain) | ✅ | `Shared/MapDisplayNames.cs`; a rename is one line |
| G4-4 | **Two-part freeze:** gameplay freeze Sun Nov 22 23:59 UTC (maps, hazards, rules, palette); art freeze Mon Nov 23 23:59 UTC (at the latest Tue Nov 24 12:00 UTC), render meshes/materials only. M5/M6 dates unchanged | ✅ | doc 14 §1 |
| G4-5 | **Bump stagger behind a flag, default off** (D2): `movement_v3.json` `bump.enabled=false`; closing speed ≥ 4 m/s → the slower player stumbles 0.25 s (no stun, input live). Server-authoritative, predicted against remotes like the push | 🟡 | your call after playing; on/off numbers next to R14 |

### G4.2 decisions (2026-11-20; Capsule Run, recorded in docs/proof/G4.2-capsule-run.md §7 — say if you want any changed)
| # | Decision | By | Where |
|---|---|---|---|
| G4.2-1 | Sweep Pad disc r 9.3 m, arms 9 m (spec 12 / 11.4 m): the allowlist caps arms at 9 m and was not widened | ✅ | builder |
| G4.2-2 | Slope release periods 104–138 ticks (spec 50–80: a wall for every bot) | ✅ | builder, proof §4 |
| G4.2-3 | Hop Field 42 → 58 m (prompt §6 lever for a short median); course ≈ 229 m; outer gaps 2.2 m, centre blocks 3 × 2.2 m | ✅ | builder |
| G4.2-4 | Grille gate doors (see-through; collider = doorway) so the finish arch stays visible from the spawn | ✅ | builder, MapArt |
| G4.2-5 | Cross-ball variant = speed / period (the release light is on one wall) | ✅ | builder |
| G4.2-6 | Slope release telegraph = flush hatches (no overhead dispenser in the spawn view) | ✅ | builder |
| G4.2-7 | CP4 deck 15.6 m (spec 7 m) so the ledges connect; ledge ball lanes + belt edge walls (no free spring route) | ✅ | builder |
| G4.2-8 | 11 gate × column routes (columns within 4.6 m of the door) | ✅ | builder |
| G4.2-9 | Bot additions: look-ahead `WaitClear` (Arg n), ball reflex, take-off points ride the block, run-up after a wait, 8 s wait timeout, tier timing slop / "go" hesitation; other maps' QA unchanged cell for cell | ✅ | WaypointBot, BotBrain |
| G4.2-10 | **Per-cell 85–110 s and outer-slope-line hits ≤ 300 / 100 not met; ship the committed layout and document** (QA review 2026-11-20: slope hits carry the time, MoonBounce / TurboToys scale it ≈ 1.25–1.4×) | **you** | proof §4.2 |
| G4.2-11 | Fixed G4.1's ROLLING_BALL pool (never stored: no client drew a ball) | ✅ | KitBuilder |
| G4.3-1 | Spiral starts on the east face (k0 north), not the south: the belt sides k2 / k6 must run along Z (`CONVEYOR` pitches about X only) | ✅ | GooTowerBuilder |
| G4.3-2 | k0 inner stairs 12 × 0.23 m instead of 8 × 0.34 m (motor step offset 0.25 m: 0.34 m steps would need a hop each) | ✅ | proof §2 |
| G4.3-3 | k0: block 1 is a static rest block (3 falling blocks, not 4), trench floor 0.7 m under each block, falling delay 0.6 → 0.8 s, both mallets at s +2.5 swinging across (G4.3d: along the steps / jumps the look-ahead found no window), flat rails beside the blocks, lap-1 mallets soft knock — bots queued / trapped at y 0 were gooed at 12 s; hard knocks cleared the 0.4 m rail | ✅ | proof §5 #11 |
| G4.3-4 | k1 lifts 2.4 × 3 m (two 3 × 3 m lifts don't fit a 5 m ledge); the gumball rolls **inward** from a dispenser on the rail (outward, a knock cleared the rail into the goo) | **you** (direction) | proof §2 |
| G4.3-5 | k2 shelf at 7.7 m with a ramp to corner 3 (at corner height the landing counted as k3), belt 0.6 m/s (plan ≈ 1.5) for the 1.25 ratio | ✅ | proof §5 #7 |
| G4.3-6 | k4: columns 2.0 m, gaps 1.8 m (movement-v2 safe), 0.2 m rise per gap and 0.45 m onto corner 5 (plan 1.6 m, 2.2–2.4 m, ≤ 0.4); mallet across at 40°, 270 / 300 ticks — lap-2 falls land in the goo (45 falls / 100 at 2.0 m gaps) | ✅ | proof §5 #3–4 |
| G4.3-7 | k6 bumpers on the outer half, passed on the core side; corner-6 spring on the corner's front quarter | ✅ | proof §5 #9 |
| G4.3-8 | k7: a gumball crossing in front of the doors (existing type, inward) — door push-outs alone gave no contact | ✅ | proof §5 #10 |
| G4.3-9 | Goo 235 → **170 mm/s**, delay 6 → **3 s** | ✅ | proof §5 #5 |
| G4.3-13 | k5 variant B = a slower period (84 vs 70 ticks), same direction (plan: direction): turned the other way the bar threw outer-arc bots off lap 2 | ✅ | proof §5 #15 |
| G4.3-10 | **Round 2 median 57 s (target 90–110): shipped at the closest tuning.** Escapes 4.85–4.99 (5 in ≈ 85–99 % of rounds), margins 8.6–9.5 s, route cost PASS. Longer cycles (G4.3d) gained ≤ 10 s and broke ratios / margins (reverted); options in proof §4 | **you** | proof §4 |
| G4.3-11 | The summit (22 m above the start corner) is outside the default spawn frame; a beacon marks it (visible looking up) | **you** | proof §8 |
| G4.3-12 | Bot code: jump arcs in the look-ahead; Survival bots start a side at its first point; out-of-reach re-acquire; `RouteQa` for Survival; goo probe per route index. Other maps' guard QA PASS | ✅ | WaypointBot, BotBrain, RouteQa, ModeQa |
| G4.4-0 | **Round 2 target 50–65 s (short, tense climb)**, was 90–110 s (doc 08) / 90–120 s (GDD §4.1). G4.3 proof §4 option 1, applied by default (no other choice in `docs/playtest/G4-notes.md`). Measured: normal median 56.8 s, reference 50.8 s, p90 62.8–64.8 s; longer cycles gained ≤ 10 s and broke ratios / margins. Match ≈ 6.1 min from roster lock (≈ 6.8 min lobby → receipt), under the GDD's 7–9 min line. **G4.5 update (measured):** with Lid Flip as the Final (normal median 79.5 s) the 20-match soak's game time is 340–460 s, **median 6.6 min** from roster lock (≈ 7.3 min lobby → receipt) | ✅ (default; say if you want option 2 or 3) | GDD §4.1, doc 08 §5 |
| G4.4-1 | **Turntable = a 9 m disc round the fountain** (plan: a ring 6–11 m): the allowlist caps TURN_TABLE at r 9 m and was not widened; 900 ticks per turn (rim 1.9 m/s, cap 4) | ✅ | builder, doc 08 §6 |
| G4.4-2 | **Outer star anchors at the sweepers' flanks** (two per sweeper, inside its 3.5 m sweep circle: A r 13 m ± 12°, B r 13.5 m ± 10°), not midway between sweepers: a radial line 30° off a sweeper clears its sweep by 3.25 m, so stars there cost no contact (the M4 map measured 0.0 drops) | ✅ | proof §5 |
| G4.4-3 | Spring launch 5.8 → **7.0 m/s** out (up 10): players touch the pad's near edge, and 5.8 landed ≈ r 19.5 (short of the island), 6.5 on its inner edge | ✅ | proof §5 |
| G4.4-4 | Sweeper period **90 ticks** (plan 90–120; 105 left normal bots at 0.94 drops per round in A); arm 3.5 m, bar 0.35 m | ✅ | proof §5 |
| G4.4-5 | Wave life 16 → **6 s**, golden life 10 → **12 s** (still under the 15 s spacing); waves every 8 s × 8 and golden +3 every 15 s unchanged | ✅ | proof §5 |
| G4.4-6 | **12 spawns** on the outer ring (prompt: 5): the scene keeps `Protocol.MaxPlayers` spawns (hashed); a 12-player match's Skill round uses 5 of them | ✅ | builder |
| G4.4-7 | No bumpers (the M4 six removed): the three sweepers met the drop target, so the ≤ 3 turntable-edge bumpers were not needed | ✅ | proof §3 |
| G4.4-8 | Bot code (Skill only): burst preference (the crown glow, then the arcs), turntable lead, the sweeper jump reflex, timed springs with a flight lock, an angled island return, pad avoidance, no MoonBounce 70 % air stick in Skill; probe clients use the same brain on Skill maps. Other maps' guard identical cell for cell (Block Party Dash, Crumble Shelf), Goo Tower PASS | ✅ | BotBrain, RaceProbeBot, ModeQa |
| G4.4-9 | The burst is **view only** (`StarBurst`, pure in t); the DEV practice mode now draws the stars and the burst too (not collectable there). `SkillRules`, the star timeline and hazard vectors v1/v2/v3 unchanged | ✅ | ReactiveViews, PracticeMode |
| G4.4-10 | Round timer stays **60 s** (it is map content, `timerCapSeconds`; the plan's 60–75 s did not need more) | ✅ | builder |
| G4.4-11 | The plan page (artifact) could not be read from this session (not found for this account); built from `G4-PROMPT.md` §4 and the G4.4 prompt §2 | ✅ (say if the page differs) | proof §1 |
| G4.4-12 | Fixed a G2 dressing bug: the Star Fountain island undersides were ≈ 15 m slabs at floor height (sized in world metres under a scaled parent), reading as ground across the gap; render only, map hash unchanged | ✅ | MapDressing, proof §6 |
| G4.4-13 | Prediction probe on Star Fountain ran with 12 players (3 probe clients + 9 bots: the dev one-round match fills to 12), not 5; 150 ms + loss met (0.17–0.25 non-push / 5 s, 0 hazard mismatches); one localhost client 0.50 / 5 s in its round windows (1 run) | 🟡 | proof §5 |
| G4.5-1 | Lid Flip lids drawn **instanced** (`LidGridView`, ≤ 9 batches for 106 lids); the hinge swing is **90° down** (a trapdoor), not 180° (a 180° turn about an edge lays the lid on its neighbour) | ✅ | proof §1 |
| G4.5-2 | **Open 45 → 30 ticks, shortest wave interval 45 → 60**: with lit + open > interval a lid could close already yellow and flip after ≈ 15 ticks (< the 1 s telegraph); def data only, lid maths and vectors unchanged | ✅ | proof §7, test `EveryFlip_FollowsAFullSecondOfYellow` |
| G4.5-3 | Stage shares 16 → 50 % (A) / 18 → 50 % (B); lower shelf from 20 s (A; B 18 s), 35 → 50 %; sweeper 134 ticks (tip 11.8 m/s) from 20 s; lower ring 2 at 75 s (60 s tried, reverted) | ✅ | proof §3 |
| G4.5-4 | **12 spawns** on ring 2, not 3 (spawns are per slot; the 3 finalists can be any slots); slots 0–2 at 120° | ✅ | builder |
| G4.5-5 | Sweeper hung from the claw (cable + hub), **no post** (every lid flips, the centre one too) | ✅ | builder |
| G4.5-6 | Rounds are ended by **bot misjudgement** (late notice 14–26 ticks, 30 % normal, +20 points after the last ring retires) + sweeper knocks; the map never forces a fall (fair 1 s telegraph, ≥ 5 inner lids solid) | ✅ | proof §3–4 |
| G4.5-7 | Normal medians 76.5–83.4 s: inside 60–100; the 80–95 aim met in 2 of 6 cells (raising them broke the second-chance target) | 🟡 | proof §2 |
| G4.5-8 | **Timer tiebreak on a lid map = slot**: `FinalRules` ranks timer survivors by deepest *crumble* shelf, then tiles touched — both 0 on Lid Flip. Normal bots reach the timer 0–3 %, flawless bots 29–57 %. Not changed (rules frozen) | ❓ your call: (a) leave, (b) lid-aware tiebreak in `FinalRules` (top shelf beats lower, then fewer falls, then slot) | proof §7 |
| G4.5-9 | Prediction probe on Lid Flip ran with 12 players (dev one-round match), not 3; localhost 0.00 and 150 ms + loss ≤ 0.18 non-push / 5 s, 0 hazard mismatches; the 150 ms leg needed Docker Desktop started locally | ✅ | proof §5 |

### G4.6 decisions (2026-11-20; candy palette + grade, recorded in docs/proof/G4.6-candy-palette.md §9 — say if you want any changed)
| # | Decision | By | Where |
|---|---|---|---|
| G4.6-1 | Top tokens = the plan hues at V 0.92, S ≤ 0.74 (the plan hexes had V 1.0; Lemon / Tangerine S 0.77 / 0.76, outside the 0.45–0.75 / 0.85–0.95 band); sides = the plan's values | ✅ | `PaletteTokens`, doc 17 §3.1 |
| G4.6-2 | Meaning tokens take the plan values (hit pink `#FF2D8A`, lemon `#FFD43B`, mint `#2FE0A8`, goo `#9B3CFF`, sky `#2E9BFF → #A6E3FF`); brand Magenta / Sun Yellow / Grape unchanged for the UI | ✅ | `PaletteTokens` |
| G4.6-3 | Palette = on-screen colours: vertex colours (and lid materials) are baked through the exact inverse of the grade and the map light; pre-tonemap gain ×1.6 (colour filter), post-exposure stays −0.15. Neutral maps white to 0.82 and sat +22 clipped linear tokens to S 1.0 (measured); the G2 near-white tops came from un-converted vertex colours in a linear project | ✅ | `MapArt.Bake`, `GraphicsQuality.AlbedoFor`; proof §3 |
| G4.6-4 | Low keeps Tonemapping + Colour Adjustments (post on, bloom + vignette off); Editor proxy +0.14 ms; WebGL cost UNVERIFIED (G4.7 build #1); URP HDR grading mode set at runtime | ✅ | `GraphicsQuality`; proof §7 |
| G4.6-5 | Lid Flip rings 1–0 Sour apple → **Bubblegum**: Sour apple was 0.06 luma from the lit lemon (needs ≥ 0.15); rings 4 and 1–0 share a batch (lid batches stay 5) | ✅ | `LidGridView`; proof §5 |
| G4.6-6 | Mint reserved: moving-platform rims and turntable spokes / stripes → the section hue darkened toward ink (movers read a shade darker); machine Grape deepened toward ink `#5F25BD` (light grape was ΔE 9 from Grape fizz on Goo Tower) | ✅ | `MapArt` |
| G4.6-7 | View rim on Rushlings, stripe posts and hit hazards, off on up-facing faces (the sun rim too); outline thickness tiers not done | ✅ | `Toon.shader` |
| G4.6-8 | Sky not compensated (reads cooler / greyer after the grade): the menu cameras run no post, a compensated sky material would over-brighten the menu | 🟡 look call | proof §9 |
| G4.6-9 | Sweep Pad stays Bubblegum with the pink / yellow sweepers: the stripe's yellow band reads (0.29 luma vs the floor) but the pink half merges with the floor | 🟡 your call | proof §5 |
| G4.6-10 | Goo Tower falling blocks stay hazard-striped (outside the hue rule); the Lobby tile parity rule lives in `MapArt` (LobbyBuilder untouched) | ✅ | `MapArt` |

### G4.7 decisions (2026-11-20; QA + freeze, recorded in docs/proof/G4.7-qa-freeze.md — say if you want any changed)
| # | Decision | By | Where |
|---|---|---|---|
| G4.7-1 | **C1 = (a)**: `FinalRules` unchanged; on a lid map a timer ending goes by slot, documented in doc 08 §7 and the Final HUD tip; (b) designed, not applied | ✅ (lead's default) / ❓ yours | proof §1.1, §6 |
| G4.7-2 | **C2**: the round-end hazard mismatch was a push-explained count; `HazardMismatches` now counts only without a server-only cause (`ExplainedHazardMismatches` apart). Dev statistic, no gameplay change | ✅ | `ClientPrediction`, proof §1.2 |
| G4.7-3 | **C3**: no lid tick mismatch; rare large 150 ms corrections documented as expected under 150 ms + loss (did not reproduce: max 29 cm, then 0) | ✅ | proof §1.3, §6 |
| G4.7-4 | **C4**: Capsule Run 150 ms FAIL (1.42) in the QA run, PASS on re-run (0.20); evidence = filled input under TCP stalls; recorded, not accepted; re-measure in M6 | ✅ | proof §6 |
| G4.7-5 | **WebGL browser measurements deferred** (user: other projects loading the CPU); build #1 saved at `client-unity/Builds/WebGL-g47-build1/` (18.98 MB) | user | proof §5 |
| G4.7-6 | Measurement hooks (dev only): `[gfx]` log + `?post=0` (GraphicsQuality), per-slot input counters in the server log | ✅ | `e84d386` |
| G4.7-7 | **Gameplay freeze declared 2026-11-20 18:54 UTC** | ✅ | PROJECT_STATUS |

### G4.8 decisions (2026-11-20; soft toy look, recorded in docs/proof/G4.8-soft-look.md — say if you want any changed)
| # | Decision | By | Where |
|---|---|---|---|
| G4.8-1 | Look switch = **two colour channels** on one geometry set: COLOR = soft albedo, uv3 = (candy albedo, 2 + pattern + 8 × role); the toon shader picks by the global `_FR_Look`. **Candy = the G4.6 colours, light, grade, sky and clouds**, drawn on the new chunky geometry (rounded edges, rims, thickness are shared): two geometry sets would double the map meshes in the build | ✅ | `LookState`, `Toon.shader`, `ToyMesh` |
| G4.8-2 | Soft grade: **no tonemapper**, saturation +6, contrast +8, exposure 0, no gain. Neutral cannot show the pastel tops (V 0.93–0.98) within the albedo cap without a ≥ 2.4× gain, which washes every unbaked colour (Rushlings) out. Without it, mid tones match the candy grade within 0.05 (CPU model) | ✅ | `GraphicsQuality.SoftGrade` |
| G4.8-3 | Soft tokens: pastel tops at V 0.975 (S 0.16; Lemon 0.22; **Soda blue 0.14**, so it stays ΔE ≥ 15 off the soft sky horizon); sides S 0.45 V 0.86; rims = the plan hues | ✅ | `PaletteTokens`, doc 17 §3.0 |
| G4.8-4 | Soft sky `#66B5F2` → `#9CD2F5` + a `#DDEFFB` haze band (sky S 0.58, under the 0.6 budget line); fog = horizon | ✅ | `SkyGradient.shader` globals |
| G4.8-5 | Lid Flip soft: lit = deeper amber **`#FF8A00`** (the G4.6 yellow is only 0.08 luma from pastel lids); the lower edge is its mid tone deepened 30 % toward ink. Both are ≥ 0.15 luma for normal, deutan and protan vision (tested) | ✅ | `LidGridView` |
| G4.8-6 | Padded rim = a tube (r 0.12) centred 0.03 m inside the edge and 0.04 m under the top: crown + 0.08 m, outer + 0.09 m (≤ 0.15). Only on walkable blocks ≥ 0.9 m wide. Only on **exposed** edge runs: probes 0.25 m outside every 0.4 m; a side mostly abutting a neighbour at about the same height (or higher) gets no rim and a tight 0.03 m radius (no groove) | ✅ | `MapArt.BlockShape`, `ToyMesh` |
| G4.8-7 | Chunky: a walkable static block or prism under 0.6 m is drawn down to 0.6 m **only if a 2 m box under it is empty** (no walk volume filled); never above or beyond the collider top | ✅ | `MapArt` |
| G4.8-8 | Sun shadows in the soft look only: 1 cascade, 60 m, soft, 2048 High / 1024 Low, strength 0.6; in full shadow the sun term drops to 0.25 (a soft lavender patch). Low keeps them unless the WebGL measurement says otherwise | 🟡 WebGL check | `GraphicsQuality.ApplyShadows`; proof §6 |
| G4.8-9 | One pattern per hue (so per section): Soda blue / Lemon stripes, Tangerine / Grape dots, Sour apple checks, Bubblegum chevrons; ±5 % value, world space, fwidth AA; never on belts, movers or hazards | ✅ | `MapArt.PatternFor`, `Toon.shader` |
| G4.8-10 | **F7 is live in every build** (release too) until you pick a look; then the other look stays behind `?look=` only. In practice mode F7 also prints the tuning JSON (dev only) | 🟡 your pick | `SandboxView` |
| G4.8-11 | Palette tests skip role 3 (rim crowns / rounded edges: trim) and role 4 (hazard-coded parts) as "walkable tops"; moving-platform / turntable stripes in soft = the hue's mid tone deepened 30 % toward ink (the G4.6 rule gave grey on pastel; at 15 % the Soda blue stripe sat ΔE 15.0 from the soft sky top) | ✅ | `PaletteTests`, `MapArt` |
| G4.8-12 | Soft clouds and the stadium backdrop use their own seed (the G4.6 dressing layout is unchanged); the backdrop sits 260 m beyond the course rectangle, at the horizon band | ✅ | `MapDressing` |

## Top risks (live; update daily in PROJECT_STATUS)

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | **7-day window** vs scope | High | High | Day plan + cut order (doc 14 §1); daily gate check |
| R2 | Prediction feel not good enough by Nov 22 | Med | High | Go/no-go gate; fallback F1. **M2a (measured):** the shared motor is bit-deterministic for the same inputs (analytic + PhysX EditMode); state = wire precision (mm, cm/s); tick-keyed inputs with a feedback clock (lead held at 2 ticks, 0 late on localhost). Server cost with 12 occupants ≤ 0.7 ms frame work p95 (Mac). **M2b (measured, docs/proof/M2-netcode.md Part B):** 3 course-running clients, corrections per 5 s while running the course: localhost **0.00**; 150 ms RTT ±15 ms jitter + 2 % TCP segment loss **0.08–0.17**, all in the first 10 s (clock convergence), 0 afterwards; WebGL (wasm) client vs Mac server ≈ 0.25 with ~0 cm visible shift (flag-only), mean drift 3.7 mm. Sweeper hits 0 mismatches. **Real VPS (Mumbai):** Mac clients 0.21–0.25 per 5 s (arm64 ↔ x86_64 drift), **WebGL client ↔ Linux server 0 corrections, 99.8 % bit-identical**, VPS frame work p95 ≤ 1.05 ms at 4 % CPU. A TCP-stall kick found on the VPS is fixed (proof B10). Recommendation: **go** (no fix window needed; the feel verdict is yours) |
| R3 | UTP WebSockets through a TLS-terminating nginx proxy fails | **Retired** | High | **M1 (measured):** local nginx 1.24 TLS (same origin, 101 in access log, 10-min soak, restart + reconnect) **and the VPS** (Let's Encrypt on `gs1.<VPS_IP>.sslip.io`, 2 Chrome clients, RTT p50 66 / p95 83 ms) all pass. Fallback not needed |
| R4 | Devnet ER / VRF instability during demo or recording | Med | High | Record proof runs early; Unranked fallback; multiple recorded matches |
| R5 | Solana.Unity-SDK breaks on Unity 6 WebGL | **Happened** (tag v1.2.9) → **Low** with the fix | Low | **M1 (measured):** v1.2.9 fails the WebGL link (BouncyCastle, upstream #257); upstream fix `1da8fd7f` builds WebGL + server, +4.0 MB compressed, wallet picker opens in Chrome. Real Phantom connect UNVERIFIED. Fallback still: web wallet link (doc 04 §8) |
| R6 | WebGL build time and memory on 8 GB | Med | Med | **M1 (measured):** release 523 s first / 294 s incremental; dev 714 s first / 238 s warm; peak RSS 1.1–1.5 GB; swap is used when other apps are open, and two licensing time-outs happened under heavy swap. Runtime: wasm heap 96 MB in Lobby (2 GB max). Keep: Editor closed, few apps open, ≤ 2 builds per hour |
| R7 | Not enough human playtesters | Med | High | Recruit now; bots fill and are labelled |
| R8 | 2 vCPU VPS shared with another site: CPU contention for two 30 Hz Unity servers + Relay + Postgres + web | Med | Med | Measure tick p95 with 2 matches in M6; Docker CPU limits; run 1 game server if needed; upgrade to KVM 4 as last resort |
| R9 | IP similarity complaints | Low | Med | Originality guardrails + checks (doc 17 §1, doc 08 §8) |
| R10 | Server-signer trust criticised by judges | Med | Low | Honest-trust line + roadmap in README |
| R11 | Linux game server image can only be run on real x86_64 (Mono cannot run under emulation on the Apple Silicon dev box) | Low | Med | **M1:** image runs on the VPS. Keep testing server builds on the VPS (or any x86_64 box) before each deploy; local tests use the Mac dev server |
| R13 | VPS port clash with the existing site (`127.0.0.1:8787`, `:3100` in use) | **Known** | Med | Pick relay/web ports that are free (check `ss -ltn`) in M6; doc 13 §2 |
| R15 | arm64 (Mac) builds are not bit-identical with x86_64/wasm (corrections every ~20–25 s on Mac ↔ VPS; WebGL ↔ VPS is bit-identical) | Low | Low | Only the dev tool is affected. Don't tune prediction from Mac-client numbers; measure with WebGL. Hypothesis (unverified): FMA contraction or arm64 PhysX |
| R14 | Push-driven corrections: player–player separation is server-only, so bunched players see corrections (measured: up to 11 per 5 s when 3 bots shared one lane in an early run) | Med | Med | Counted separately (`ownEvents`); lanes in the gate. **M3 (measured, crowded 12-player race):** client-side push prediction against remotes extrapolated from the newest snapshot is on (corrections + pushes per 5 s while racing, localhost: 2.73 → 0.73 mean). Still over the < 1 bar for one of three clients on localhost (1.12) and at 150 ms + loss (1.2–1.7); non-push corrections stay ≤ 0.4. Options for M4 🟡: softer pushes (e.g. 1.5 m/s), or accept push corrections (they are smoothed, ~10 cm) **M4 (measured, full matches, 3 probe clients):** push corrections 0.35 / 5 s localhost (Star Scramble crowd 1.1–1.4), 0.72 / 5 s at 150 ms (Star Scramble 2.2); non-push 0.00 / 0.18. Kept as accepted. **G4.1c bump stagger (measured 2026-11-19, `scripts/g4-bump-measure.sh 150`, M3 crowd setup: Block Party Dash, 3 probe clients + 9 bots, one 150 s run per cell, corrections + push corrections per 5 s while racing, per client):** flag **off** localhost 0.00 / 0.00 / 0.75 (mean 0.25), 150 ms + 2 % loss 0.17 / 0.42 / 0.00 (mean 0.20); flag **on** localhost 0.20 / 0.00 / 0.17 (mean 0.12), 150 ms 1.67 / 0.17 / 0.33 (mean 0.72; one client over the < 1 bar). Bumps are rare in this crowd (16–17 server staggers per run); hazard mismatches 0 everywhere. One run per cell, so the on/off difference at 150 ms is within run-to-run noise; not bad enough to drop. **Shipped off**; your call after playing (`-fallrushBump 1` / `?bump=1` on both sides). |
| R16 | Reference bots finish Block Party Dash in 31–34 s (median) vs the 75–95 s target | High | Med | Course may be too short/easy for humans or fine once humans play; decide after the playtest: add length (S1 blocks, S2 bridge) or harder timing. 🟡 |
| R17 | VPS link from the dev machine was degraded on 2026-11-19 (85 % ICMP loss); the M3 WebGL ↔ Linux gate could not be measured | Med | Med | Re-run `scripts/m3-gate.sh` / WebGL `?bot=race` against gs1 on a healthy link before M4 |
| R18 | Round durations with bots differ from the GDD §4.1 targets (Survival ~65 s, Final ~30 s with normal bots; humans UNVERIFIED) | Med | Med | Playtest decides; knobs: Goo Tower length / goo speed, Crumble wobble time (GDD 0.6 s) or tile count. M4 QA tables record the bot numbers |
| R20 | WebGL release halted once (exception support None) at a map load; fixed by "Explicitly Thrown Exceptions Only", root cause unknown | Low | High | M4 §H; keep the setting; watch the console in playtests |
| R21 | Draw calls 490 (Block Party Dash) / 282 (Goo Tower) vs a 150 budget; 59 FPS on the M2 now | Med | Low | static batching / merged greybox meshes in M7 |
| R22 | At 150 ms + 2 % loss one of three clients failed the initial connect (10 × 1 s attempts); **G4.2 (2026-11-20): 2 of 3, then 3 of 3 in two reruns through the local Docker netem relay** | Med | Med | more attempts / a longer timeout for WebGL in M5–M6; the Reconnect button works. **G4.3 (2026-11-20): cause found and fixed for the desktop probe** — UTP desktop WebSocketLayer allows the HTTP upgrade one `ConnectTimeoutMS` (1 s) with no retry; a lost segment (1 s RTO) blows it. Probe clients now pass `-fallrushConnectTimeoutMs 4000`; 3 / 3 connect. WebGL already allows timeout × attempts (10 s) |
| R19 | Our own scene flow: a slow WebGL load past 10 s spectates a round (protected unless it is the final) | Low | Med | measured load times in the WebGL run; raise the timeout if needed |
| R12 | Hidden browser tab freezes Unity WebGL (no `requestAnimationFrame`) → UTP 30 s timeout → player dropped | Med | Med | **Fixed for 10/30/60 s on the desktop path (M2b, measured with a main-thread stall):** the drop was not the timeout but NGO's reliable pipeline re-sending its window every 500 ms into the stalled client (16.7 KB/s), which overflowed UTP's receive queue on wake-up (`ProtocolError`). Fix: our `WebSocketDriverConstructor` (NGO's driver, reliable resend 10–30 s; TCP already guarantees delivery), server pauses snapshots after 1 s without input, server disconnect timeout 90 s, client queue 1024. **A real hidden Chrome tab is UNVERIFIED** (Playwright keeps pages visible); steps in the proof file |
