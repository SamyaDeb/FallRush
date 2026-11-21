# G4 play notes (from the user)

Add anything you notice while playing, one line each: where, what happened, what you expected. The next G4 session reads this file first.

## How to play the new maps

**Practice mode (no server, Editor or a development build):**
1. Unity Editor (close any batch job first) → open `Assets/_Game/Scenes/Boot.unity` → **Play**.
2. Menu → **Practice / tuning (dev)**. This loads Round 1 (`BlockPartyDash` = Capsule Run) with the map's hazards running locally. **R** = respawn at the last checkpoint, **F6** = tuning panel, **F3** = netcode HUD.
   - Desktop dev build: `FallRushDevServer -fallrushPractice BlockPartyDash`. WebGL development build: `?practice=BlockPartyDash`.

**A local match (you + 11 bots, all four rounds):**
```bash
pnpm m1:build:macserver                 # or: bash scripts/m1-unity.sh macserver -buildTarget OSXUniversal -executeMethod Build.MacDevServer
bash scripts/m1-macserver.sh start      # 127.0.0.1:7777
```
Then Unity Editor → `Boot.unity` → **Play** → type your name → **Play (Quick Play)**. Round 1 is Capsule Run. Stop the server with `bash scripts/m1-macserver.sh stop`.
- Force a variant / modifier (dev): `bash scripts/m1-macserver.sh start -fallrushVariant 1 -fallrushModifier MoonBounce`; otherwise the deal picks them.

## Play all four maps (G4.7; the gameplay freeze was declared 2026-11-20 18:54 UTC: your notes now steer bug fixes and G4-B)
One full match plays all four in order. Write one line per map under "Your notes" of its section below (or here): **where** (beat / side / ring), **what happened**, **what you expected**. A number helps ("hit 4 times on the slope", "round felt ~2 min").
- **Capsule Run (R1):** try both slope lines and both belt / spring routes. Write: was any line clearly the easy one? Could you see the finish arch at the start?
- **Goo Tower (R2):** climb fast once, then lag behind on purpose once. Write: did the goo feel fair (time to react), and was the round too short (~57 s with bots)?
- **Star Fountain (R3):** rush the burst, then try one golden-star spring. Write: did you see the burst coming, and did a sweeper drop feel fair?
- **Lid Flip (R4):** stand still on the top shelf and step off only on yellow. Write: was 1 s of yellow enough, could you read the magenta ring warning, and did the lower shelf save you? If a round goes to the 120 s timer, note who won (tiebreak, G4.5-8).

**The WebGL build in Chrome against the Mac dev server** (build #1 is saved at `client-unity/Builds/WebGL-g47-build1/`; copy it to `client-unity/Builds/WebGL/` first if a newer build replaced it; Editor closed, nothing else heavy running):
```bash
pnpm m1:up:mac        # Mac dev server on 127.0.0.1:7777 + the local nginx container on :8080 (Docker Desktop must be running)
```
1. Chrome → `http://localhost:8080/?autoplay=1&name=You` (hard-reload once: Cmd+Shift+R). Window about 1440 × 900.
2. **Play** (Quick Play) → you + 11 bots → all four rounds → champion. Esc → Settings → Graphics **High** / **Low** (or add `&gfx=high` / `&gfx=low`); play one match on each if you can.
3. Optional: `&hud=1` shows the netcode HUD; `&bot=match` lets a bot play for you; `?selftest=1` prints the determinism self-test in the console (View → Developer → JavaScript Console).
4. Stop: `pnpm m1:down`, then quit Docker Desktop.

## Capsule Run (G4.2, 2026-11-20)
What to look at (proof `docs/proof/G4.2-capsule-run.md`):
- **Gumball Slope:** five lanes of gumballs rolling down; dodge by timing. Is it fun or a wall? (Bots get hit 1–6 times on the way up.)
- **Wave Gates:** the open door travels across the wall; the bars flash before a door shuts. Readable?
- **Hop Field:** bobbing centre (short hops) vs the outer columns (2.2 m real jumps); the mallet gantry halfway. Do the jumps feel fair?
- **Sweep Pad:** jump the low arm, dive under the high one.
- **Mallet Run:** time your run between three mallets.
- **Belt Climb:** weave the bumpers against the belt, or take a side ledge (a gumball comes down it) and the spring to the finish.
- **Finish arch:** can you see it from the spawn? (It is at the top edge of the default camera.)
- Round length for you (bots: normal median 87 s; target 85–110 s).

Your notes:
-

## Goo Tower (G4.3, 2026-11-20)
How to play it:
- **Practice (no server):** Editor → `Boot.unity` → Play → **Practice / tuning (dev)** loads Round 1; for the tower use a desktop dev build `FallRushDevServer -fallrushPractice GooTower` (or WebGL dev `?practice=GooTower`). R = respawn, F6 = tuning, F3 = netcode HUD.
- **Local match on the tower only:** `bash scripts/m1-macserver.sh start -fallrushMap GooTower` (a one-round dev match: you + 11 bots), then Editor → `Boot.unity` → Play → Quick Play. Stop: `bash scripts/m1-macserver.sh stop`. Variant B: add `-fallrushVariant 1`.
- **Full match:** `bash scripts/m1-macserver.sh start` → Quick Play; the tower is Round 2.

What to look at (proof `docs/proof/G4.3-goo-tower.md`):
- **k0:** stairs vs falling blocks (block 1 stays; the trench under them is shallow). Two mallets swinging across the lanes near the top of the side.
- **k1:** queue for a lift while gumballs roll across the queue toward the core.
- **k2:** belt ramp vs the spring onto the shelf. **k3:** weave the three bumpers.
- **k4 (lap 2):** jumps (1.8 m gaps) on 2 m columns under a mallet swinging across them — a hit drops you a lap (into the goo after ≈ 30 s). Too punishing?
- **k5:** jump the low bar. **Corner 6:** the spring shortcut up the belt. **k7:** read the door wave, mind the gumball.
- **Round length:** bots take ≈ 57 s (target was 90–110 s); 5 escape in nearly every round. Does it feel too short? (Options in proof §4.)
- **Summit:** it is straight above the start, outside the default camera; look up to see the beacon. Is that OK?

Your notes:
-


## Star Fountain (G4.4, 2026-11-20)
How to play it:
- **Practice (no server):** a desktop dev build `FallRushDevServer -fallrushPractice StarScramble` (or WebGL dev `?practice=StarScramble`). The stars and the fountain burst show; stars are not collectable in practice. R = respawn, F6 = tuning, F3 = netcode HUD.
- **Local match on Star Fountain only:** `bash scripts/m1-macserver.sh start -fallrushMap StarScramble` (a one-round dev match: you + 11 bots), then Editor → `Boot.unity` → Play → Quick Play. Variant B: add `-fallrushVariant 1`; a modifier: `-fallrushModifier MoonBounce` (or TurboToys, BigBonk). Stop: `bash scripts/m1-macserver.sh stop`.
- **Full match:** `bash scripts/m1-macserver.sh start` → Quick Play; Star Fountain is Round 3 (5 players, 3 qualify, 60 s).

What to look at (proof `docs/proof/G4.4-star-fountain.md`):
- **The burst:** the fountain crown glows 1 s before each wave (every 8 s), then 8 stars fly out to the turntable and the outer floor. Do you see it coming and rush in?
- **The turntable:** 9 m, one turn per 30 s; the stars ride it. Easy to chase?
- **Sweepers:** three low bars on the outer ring (jump them). A hit drops one of your stars (it pops off; anyone can grab it). Stars at the sweepers' flanks are the risky ones. Fair?
- **Islands + springs:** the golden star (+3) every 15 s on one of three islands; take the mint spring in front of it and time it with the island's slide. Coming back: hop off the island's inner edge, and don't run back over the spring (it throws you out again).
- **Round length:** 60 s (unchanged). Is it decided late enough?

Your notes:
-

## Lid Flip (G4.5, 2026-11-20)
How to play it:
- **Practice (no server):** a desktop dev build `FallRushDevServer -fallrushPractice CrumbleShelf` (or WebGL dev `?practice=CrumbleShelf`). R = respawn, F6 = tuning, F3 = netcode HUD.
- **Local match on Lid Flip only:** `bash scripts/m1-macserver.sh start -fallrushMap CrumbleShelf` (a one-round dev match: you + 11 bots — 12 on the shelf, more crowded than a real Final), then Editor → `Boot.unity` → Play → Quick Play. Variant B: add `-fallrushVariant 1`; a modifier: `-fallrushModifier MoonBounce` (or ButterFloors). Stop: `bash scripts/m1-macserver.sh stop`.
- **Full match:** `bash scripts/m1-macserver.sh start` → Quick Play; Lid Flip is Round 4 (3 players, last one standing, 120 s).

What to look at (proof `docs/proof/G4.5-lid-flip.md`):
- **Yellow = move.** A lid lights yellow for 1 s, then swings open for 1 s. Is 1 s enough to step off, and can you read it while running?
- **Magenta = the ring is going.** The outer ring pulses magenta 3 s before it drops (at 25, 50 and 75 s). Clear?
- **The sweeper** arrives at 20 s (it lowers from above). Jump it; near the middle it is too slow to jump — stay off the hub.
- **The lower shelf** (purple, 5 × 5) is your one more chance. Falls from the outer two rings of the top miss it. Can you see its edge from above?
- **Round length:** bots end it in ≈ 80 s. Does it feel decided, or does it drag?
- **Your call (G4.5-8):** at the 120 s timer the tiebreak is currently by slot on this map (the old "deepest shelf" rule only knew crumble tiles). Leave it, or let "still on the top shelf" win?

Your notes:
-


## G4.8 soft look (2026-11-21)
How to compare:
- **In any build, press F7** to switch between **soft** (the new default: pastel tops, coloured padded rims, rounded blocks, patterns, soft shadows, stadium backdrop) and **candy** (G4.6). The switch is instant and changes nothing but the picture.
- WebGL: `?look=soft` or `?look=candy` in the URL picks the start look; desktop: `-fallrushLook soft|candy`.
- Side-by-side images: `docs/proof/g4/soft-compare-<map>-{spawn,action,overhead}.png` (G2/G3 → G4.6 → G4.8). Proof: `docs/proof/G4.8-soft-look.md`.

What to look at, per map:
- **Is it calmer?** Tops pastel, edges coloured. Does the course still read at a glance (where to walk, where the gaps are)?
- **Rims and bevels:** do edges look padded/soft without hiding where a platform ends?
- **Shadows:** do they help you judge jumps (soft only)? Any frame drop on Low?
- **Hazards and meaning colours** (red/pink hazards, goal, boost pads, Lid Flip's lit lid): still the loudest thing on screen?
- **Lid Flip in soft:** the lit lid is now deep amber `#FF8A00`. Do you ever miss it?
- **Sky + backdrop:** pleasant, or distracting?

Your pick (G4.8-10):
- G4.8z (2026-11-21): no pick yet → **soft stays the default and F7 stays live** until you pick.
- Look: soft / candy / mixed (say what to keep from each):
- Per map notes:
-

## G4-B art: what to try in build #4 (2026-11-21; build #3 had a broken colour grade, see the G4-B proof)
Start: `pnpm m1:up:mac` → `http://localhost:8080/?name=You`; stop with `pnpm m1:down`.
- **Look:** F7 switches soft ↔ candy (or `?look=candy`). Soft is the default until you pick.
- **Camera:** `?cam=close` (default: lower, flatter, the course ahead in view) vs `?cam=classic` (the old camera). Which one?
- **Getting hit:** walk into a mallet or sweeper — dizzy swirls round the head + a white "bonk", no gold stars any more.
- **Props:** foam mallet heads, quilted pillow bumpers (they squash and wobble when someone is knocked next to them),
  gumballs with a lemon band, foam caps on sweeper bars, inflatable start / finish arches on Capsule Run, the claw over Lid Flip.
- **Backdrop (soft):** the crowd in the stadium hops, banners wave, a cloud sea far below.
- **Lid Flip:** is a lit (amber) lid ever hard to spot, also in a shadow?
- Notes:
-
