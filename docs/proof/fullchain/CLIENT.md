# Full on-chain match: Unity server + client + browser bundle (fullchain, 2026-11-25)

Implements CONTRACT.md §1 (game server) and §4 (client). Kill switch: game server env `GAMESERVER_ONCHAIN_ALL=1`
(or `-fallrushOnchainAll`), only on top of today's rollup handoff (`GAMESERVER_ONCHAIN_FINAL=1` + net `devnet`/`local`).
Off = today's hybrid flow: no new header field, no new status field, no new message field, director hooks null.

## Game server (Classic, `client-unity/Assets/_Game/Scripts/Net`)
- **Header** (`ServerMatch.OpenLog`, the jsonl `type:"match"` line written at roster lock): `"onchainAll":true` when the
  flag is on and the match is a normal 4-round deal (no dev plan). Same line, same moment as today.
- **Prejoin** (same tick, right after the header): `fonc` `{"t":"final_prejoin","all":true,…,"slot_map":[humans]}`.
- **Liveness** (`ChainFinalHandoff.BeginAll`, from roster lock): live when, all in one read loop (300 ms): Arena status
  byte @1409 == 2 (running), `Arena.match_account` (@40, 32 B) is a Match whose `match_id` (@10, 16 B) = this match's id,
  World director phase (World @208, director @+1910) in ROSTER_LOCKED…DONE, and WorldAcc tick (@128) advancing.
  Timeout **60 s (judge 90 s)** from roster lock, env `GAMESERVER_ONCHAIN_ALL_TIMEOUT` (seconds) overrides. The contract's
  8 s / 45 s is too short here: the keeper must create + delegate, wait for the VRF deal and the humans' joins (≤ 10 s /
  20 s) before `start_sim`. While deciding, the Classic director holds in DEALT (`MatchDirector.HoldDealt`).
- **Live**: jsonl line `{"type":"onchainAll","state":"live","arena":…}`, every client gets
  `fonc` `{"t":"final_onchain","all":true,"net","arena","match","url":"","slot_map":[all 12 roster slots]}` (also re-sent
  to a client that reconnects). The server runs no Classic round: it loads round 1, wraps its rules in
  `ChainFinalRules(…, all:true)` (nobody simulated, inputs off) and holds there.
- **End**: polling the WorldAcc at 2 Hz (+ Arena at 0.5 Hz): director DONE with a champion (or CHAMPION and the arena
  has left this match) → ranking champion first, others by elimination round → the Classic director ends the match
  after "round 1" with that champion (transcript: QUALIFIED champion / ELIMINATED rest in round 1, COMPLETE_MATCH).
  jsonl `{"type":"onchainAll","state":"done","champion":n}`. ABORTED / another match on the arena / no tick progress for
  120 s → `"state":"aborted","why":…` and the server aborts its match (ServerStop).
- **Fallback** (not live in time): jsonl `{"type":"onchainAll","state":"fallback","why":…}`, the server plays the whole
  match on Classic (judge: fast-forward as today). **No hybrid Final handoff for that match** (the arena may be mid
  start_sim for it).
- **status.json** (flag on only): `"onchainAll":bool,"chainRound":n,"chainPhase":p` (director phase 0–12).
- Judge rooms: `MatchDirector.ChainAll` turns off the judge fast-forward while the arena plays the match.

## Client (`OnChain/**`) + bundle (`packages/chain/src`)
- `OnChainSwitch`: `fonc.all` → `OnChainSession.FullMatch` (pre-join and confirm). Any of the 12 slots can be ours.
- `OnChainSession` (FullMatch): joins with `full:true`; the map follows the World's `map_key` every World
  (`FollowWorldMap`: `Resources/feellab/<key>` blob + scene `Map_<key>`, prediction re-created on the new blob; lobby
  254 → `Map_Lobby`); no prediction once our slot is eliminated (spectate); screens go back to the game server only when
  its match is in LobbyOpen (or Aborted), not at its Champion. Fallback: if the server's match reaches RoundActive
  without `final_onchain`, the pre-joined session leaves.
- `OnChainMatchFeed.Full`: match state from the World's director (`Shared/ChainDirector.cs`): phase = director phase
  (= `MatchPhase`), round 1–4, deal maps/variants/modifiers, targets, seed, transcript, event count, roster, bots,
  alive, elimination round + reason, champion, places per round from each round's result; race byte from slot rules
  (checkpoint / done / qualified / eliminated); events Finished, Qualified (by result rank bit), Eliminated, RoundEnd.
  Players out of earlier rounds are hidden between rounds. Hybrid path unchanged.
- `WorldView.Valid` accepts World version 1 and 2.
- Economy: "chain runs this match" = Match flags 0x40 (start_final) **or** 0x20 (start_sim). Chaos Drops still only
  round 4 RUSH; FinalWinner live at R4 intro; Champion market rule unchanged (open while round < 4). Receipt line:
  trust bit 4 → "all 4 rounds ◆ program-computed".
- Bundle `rollup.ts` `p2Join`: `full` → roster = slots 0…playerCount−1, no finalist demotion, `map` = the World's
  map_key name (no `CrumbleShelf` default in full mode; hybrid default unchanged). Rebuilt:
  `node packages/chain/scripts/build.mjs` → `client-unity/Assets/StreamingAssets/fallrush-chain.js`.
- `rush_core.wasm` (StreamingAssets) + `Plugins/macOS/rush_core.bundle` rebuilt from this worktree's `crates/`
  (`cargo build --offline --release -p rush-core-wasm --target wasm32-unknown-unknown`, `-p rush-core-ffi`); rc_version
  0x10002 unchanged; `tools/p3/rc-subset.mjs` and `ffi-expect.mjs` outputs byte-identical to the committed expectations.

## How to test
- `node --test --test-force-exit --test-concurrency=1 test/rollup.test.ts test/p2.test.ts test/bundle.test.ts` (packages/chain).
- EditMode: `bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh fc-editmode -- bash scripts/m1-unity.sh fc-editmode --tests -runTests -testPlatform EditMode -testResults $PWD/.m1-logs/fc-editmode.xml`.
  New: `Tests/EditMode/FullChainDirectorTests.cs` (director hooks, ChainDirector decode/ranking),
  `Tests/EditMode/P3/FullChainTests.cs` (feed rounds 1–4, result events/places, champion, spectators, hybrid unchanged,
  server result/abort/timer logic, base58 + match id, fonc `all`, scenes + blobs, prediction on a v2 World).
- Live (UNVERIFIED): dev arena 7, keeper `ONCHAIN_ALL=1`, server `GAMESERVER_ONCHAIN_FINAL=1 GAMESERVER_ONCHAIN_FINAL_NET=devnet
  GAMESERVER_ONCHAIN_ARENA=<arena 7> GAMESERVER_ONCHAIN_WORLD=<its WorldAcc> GAMESERVER_ONCHAIN_ALL=1`, WebGL client.

## Results (2026-11-25)
- packages/chain: 17/17 pass (rollup, p2, bundle); `tsc` on rollup.ts: same 2 pre-existing errors as before the change.
- EditMode (whole suite, worktree): 490 total, 489 passed, 0 failed, 1 skipped (pre-existing, emulator not built); the
  16 new fullchain tests pass, incl. prediction on a v2 World with the rebuilt core.
- WebGL build: NOT run (3 GB free; the build scripts require ≥ 5 GB).

## UNVERIFIED
- Nothing ran against a live arena: liveness, the 60 s window, per-round scene loads in WebGL, 12-player browser
  prediction cost, the bundle's full join on devnet, reconnect mid-match.
- `Resources/feellab/GooTower.bytes` must equal the arena's sealed Goo Tower blob (RESEARCH §1: on-chain blob stale).
- A human whose 20 s disconnect grace expires while the server holds round 1 is DISCONNECT-eliminated by the Classic
  director; if that is the arena's champion, the server's own champion (status / transcript) can differ from the chain's.
- The server's Classic transcript for a full match is a one-round placeholder (the chain's transcript is the truth).
