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
