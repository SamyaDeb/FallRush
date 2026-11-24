# Full on-chain match: interface contract (fullchain, 2026-11-25)

All 4 rounds computed by the `fall_rush` program on the ER (`start_sim`, the on-chain director runs lobby →
R1 Capsule Run → R2 Goo Tower → R3 Star Fountain → R4 Lid Flip). All-or-nothing; per-match fallback = today's flow.

## Kill switches (default OFF = today's behaviour, byte for byte)
- Game server env `GAMESERVER_ONCHAIN_ALL=1`; keeper env `ONCHAIN_ALL=1`. Both must be on. Off → hybrid flow (Classic
  rounds 1–3 + on-chain Final) exactly as today.

## 1. Game server (Classic, Unity Linux server)
- Lobby, rooms, matchmaking, judge rooms, bots-fill, roster lock: unchanged.
- At roster lock with `GAMESERVER_ONCHAIN_ALL=1`: write the jsonl header with `"onchainAll": true` (plus everything it
  writes today), pre-join every human to its slot on-chain (`SendPrejoin`, as today), then **hand every client to the
  chain session at round 1** (the same mechanism as today's Final handoff, for the whole match) and run no Classic round.
- Liveness: if the on-chain match is not live (World `round >= 1`, arena running this match) within the handoff timeout
  (8 s, judge 45 s) → write an `ABORT`-style fallback event and play the match on Classic as today (per-match fallback).
- While on-chain: the server follows the World (director phase / round / alive) only to show lobby-side status and
  to end its match when the on-chain director reaches DONE (or Match Completed / Aborted), then returns players to the lobby.

## 2. Keeper (`tools/keeper/daemon.ts`, owner: lead)
- Header `onchainAll` + `ONCHAIN_ALL=1`: create/delegate/open_lobby/deal as today → wait for the roster's humans to
  join (`human_mask`) → `start_sim` → 2 timed tasks (rotated) + the crank (`crank(budget)`, `tools/keeper/crank.ts`)
  → `commit_round` per round → markets: Champion from creation (open through R1–R3), Qualify(r) per round,
  FinalWinner opens at R4 intro → settle → receipt → end_sim → finalize → rewards (as today).
- Chaos Drops: Final only (unchanged program rule `round == 4 && RUSH`).

## 3. Program (owner: lead) — additive; live paths unchanged
- New `crank(budget: u32)`: `tick()` with a slice of up to `budget` work units (clamped 1000…6000). Same accounts as `tick()`.
- World version 2 (`rush_core::world::WORLD_VERSION_CHAIN`) for `start_sim` worlds: idle slots (out / done) are not
  stepped. `start_final` worlds stay version 1. rush-core decodes both.
- Receipt `trust`: `FINAL_PROGRAM | ALL_PROGRAM (4)` for a full on-chain match (no `ROUNDS_AUTHORITY`); hybrid as today (1|2).
- `WorldAcc` layout unchanged (raw offsets used by clients: round at 139, round phase at 199 — unchanged).

## 4. Client (Unity WebGL + `packages/chain` bundle)
- On-chain session for a whole match: the map follows the World (`WorldAcc.map_key` / World `map_key`: deal ids
  0 BlockPartyDash (Capsule Run), 1 GooTower, 2 StarScramble (Star Fountain), 3 CrumbleShelf (Lid Flip), 254 Lobby).
  At each round intro: load that map's scene, re-init the prediction core with that map's blob.
- Match feed from the World's director state for rounds 1–4 (intro / countdown / rush / result / intermission /
  champion), qualify + eliminate per round, spectate when out; any of 12 slots may be the local player.
- Inputs: `submit_input` as today, from round 1. Economy UI: markets as today; Chaos Drops UI only in round 4.
- The new `rush_core.wasm` (World v2) must ship in the build (`client-unity/Assets/StreamingAssets/rush_core.wasm`).

## 5. Web + replay
- `/live`, `/receipt`, `/replay`: any round 1–4; trust bit 4 → every round labelled ◆ program-computed, with the line
  "round ticks are sent by MagicBlock's task scheduler and a permissionless keeper crank".
- Replay must step exactly as the chain: a wasm export that runs `step_slice` until the tick completes, and crosses
  round map loads (`director::needs_map` / `load_round`). `tools/p7/replay-check.mjs` + `/receipt` verify across all 4 rounds.
- Indexer: `finalOnChain` → also `allOnChain` from the receipt trust bit.

## Test hooks
- Dev arena **7** on devnet (`3uJwGdZSpdXN49uBPVKRgHfDLhZgc8Kv4bBUUiRnCin3`). Live arenas 2 and 3 must not be used for tests.
- Dev runner: `P2_NET=devnet ARENA=7 MODE=slice TASKS=2 CRANK=1 node --experimental-strip-types tools/keeper/run.ts`.
