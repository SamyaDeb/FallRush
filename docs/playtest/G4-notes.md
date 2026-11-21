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
- **k0:** stairs vs falling blocks (block 1 stays; the trench under them is shallow). Mallets at the top of the side.
- **k1:** queue for a lift while gumballs roll across the queue toward the core.
- **k2:** belt ramp vs the spring onto the shelf. **k3:** weave the three bumpers.
- **k4 (lap 2):** real jumps on narrow columns under a mallet swinging across them — a hit drops you a lap (into the goo after ≈ 30 s). Too punishing?
- **k5:** jump the low bar. **Corner 6:** the spring shortcut up the belt. **k7:** read the door wave, mind the gumball.
- **Round length:** bots take ≈ 59 s (target was 90–110 s) and normal bots average 4.1–4.3 escapes, not 5. Does it feel too short / too lethal? (Options in proof §4.)
- **Summit:** it is straight above the start, outside the default camera; look up to see the beacon. Is that OK?

Your notes:
-

