# P2 decision 1 (user, 2026-11-22): the Final goes on-chain first

**Paste this into the running P2 session.**

The user decided on gate 0.2. Record it as doc 15 **OC-11** in your final doc commit.

1. **The Final (Lid Flip / `CrumbleShelf`) runs fully on the rollup first.**
   - Capsule Run, Goo Tower and Star Fountain stay on the **Classic server** for now. This is the round-by-round fallback from P0-1.
   - Keep cutting the motor's compute cost in later work, so those maps can move on-chain one at a time. Order: Star Fountain, then Goo Tower, then Capsule Run.
2. **Bots: measure first, then choose.** The Final has **3 players** (12 → 8 → 5 → 3), not 12.
   - Measure Lid Flip at 3 players, at 30 Hz, with bots inside `step()`, including a pile-up stress vector.
   - If the worst tick is ≤ 300k CU, **keep bots on-chain** (option A). It's more trustless.
   - If not, move bots **off-chain** (option B): `tools/bot-runner` (TS + `rush_core.wasm`, same `bot_input`) sends bot inputs through bot session keys, and bots are labelled `kind = bot` in the roster.
   - Also record the 12-player number, so the Lobby / practice arena budget is known.
3. **Scope changes to P2:**
   - **0.2 gate:** applies to **Lid Flip at 3 players** (and the Lobby if it runs on-chain). The other maps are measured and reported, but they don't block P2.
   - **0.3 bot pacing:** Lid Flip only (G4.5 §2 Final targets at 30 Hz). Capsule Run and Goo Tower pacing in `rush-core` is no longer blocking. Keep the numbers in the proof.
   - **P2.5:** enable Lid Flip only.
   - **P2.9 exit gate:** 5 unattended **on-chain Finals** on devnet-as at 30 Hz with 3 players (bots), CU max ≤ 300k, replay identical 5/5.
4. **New task P2.10, hybrid bridge** (after P2.9 passes). It's the part that makes one match span both modes:
   - **Classic rounds 1–3 → `Match`:** a TS chain worker (`tools/chain-worker`) tails the Classic server's `match-logs/match-<id>.jsonl` (the M4 outbox format). It sends the existing `start_round` / `record_qualified` / `record_eliminated` / `complete_round` (+ commit) instructions, signed by the game authority, idempotent through `event_seq`.
   - **Handoff:** `start_final(match, roster[3], vrf-dealt hazard seed)`, game-authority-signed. It initialises the on-chain Final arena from the `Match` state after round 3 (the program checks the 3 qualified slots are the ones recorded), then the keeper schedules ticks.
   - **End:** `tick()` decides the champion, then `finalize_match` commits + undelegates as planned.
   - **Proof:** one devnet run where a Classic server match (bots) plays rounds 1–3, hands off, the Final runs on-chain, and the receipt on Solana matches the jsonl + the on-chain Final.
5. **For P3, which starts in parallel (P3a):** put in your proof §10:
   - the `WorldAcc` byte layout and the offset of `rush_core::World` inside it;
   - the arena / input account addresses and seeds;
   - the input batch format (3 ticks, stamped T+3);
   - the session-key flow `join_lobby` expects;
   - how a client finds the arena for a given match.

   P3a builds against an emulator until then.
