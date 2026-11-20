# G1 play notes (from the user)

Add anything you notice while playing, one line each: where, what happened, what you expected. The G1 session reads this file first.

## Movement and feel (2026-11-19)
- The Rushling runs **too fast**. It should feel like a knockout party game: a heavier, wobblier, slower runner with momentum, not a sprinter.
- Animations should have the genre's physical comedy: wobble, stumble on hard landings, flop/tumble when hit, belly-flop dives, getting back up. (Our own animations on Bolt; no copying.)

## Block Party Dash
- S1 Block Hop (the square blocks): the gaps are **too small** and every block sits at the same low height, so the jumps are not a challenge.

## Bugs
- "There are more and more bugs in the game." (Add specific ones below as you find them.)
-

## G1 status of each note (2026-11-19, see docs/proof/G1-polish.md)
- **Runs too fast** → **changed**: movement v2 (5 m/s, 0.18 s to full speed, a stop slide, 1.2 m hop, 30 % air control). Your check: does it feel heavy and wobbly but not sluggish? Tune live with the practice mode (README "G1: run it", F6 / F7).
- **Genre animations** → **changed**: our own procedural set on Bolt: waddle run with planted feet, idle breathing / look-around / fidget, take-off squash, flailing falls, stumble after > 3 m, belly-flop dive + slide + scramble, tumble + get-up with stars, bump wobble, qualified / eliminated / champion poses, emotes 1–4. Your check: the comedy and readability.
- **S1 gaps too small / all the same low height** → **fixed**: rebuilt with height variation (0.3–1.4 m tops) and real v2 jumps; safe left lane, risky right lane, bobbing middle. Your check.
- **(added during G1) "now the right side's gap got a little large, that's why unsuccessful jumps"** → **fixed**: right-lane gaps 2.4–2.6 → 2.0–2.1 m, rises ≤ 0.4 m, stair top 1.4 m; reference bots now fall 3–5 times per race there instead of 27–71. Your check.
- **"More and more bugs"** → the G1 bug list (proof §E) is fixed or explained item by item; please add anything you still see here, one line each.
