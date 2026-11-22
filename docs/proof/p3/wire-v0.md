# P3 wire format v0 (emulator; provisional until P2 proof §10)

All integers little-endian. Everything layout-specific is read from the **chain config JSON** (`packages/chain/config/*.json`)
so Part 2 (P2's real `WorldAcc`) only changes the config, never code.

## 1. Input command (5 B) — rush-core `InputCmd::to_bytes`
`move_x i8, move_y i8, buttons u8, cam_yaw u16`. Buttons: 1 jump held, 2 jump pressed, 4 dive held, 8 dive pressed.

## 2. Input batch (`InputBatch`, the `submit_input` arg) — 3 ticks per transaction
```
0  u8   slot
1  u32  first_tick      (stamp of the first command; command k is for tick first_tick + k)
5  u8   count           (1..3; the client always sends 3)
6  count × 5 B InputCmd
```
= 21 B for 3 ticks. The client sends one batch every 3 ticks (100 ms at 30 Hz). With `T` = the client's estimate of the
chain's current tick at send time, the batch carries ticks `T+3, T+4, T+5` (lead `L = 3`, configurable).
The arena accepts a stamped command only for a tick it has not stepped yet and within `World.tick + 8`
(doc 19 `submit_input`); at tick `t` it uses the command stamped `t` if present, else "missing" (rush-core's
`InputTrack` repeats the last command, press edges cleared, for 3 ticks, then neutral).

## 3. World account (`WorldAcc`, emulator v0)
```
0   u8[8]  magic "FRWORLD0"
8   u32    core_version   (rc_version of the arena's rush-core)
12  u32    tick           (= World.tick)
16  u32×12 applied_tick   (per slot: newest stamped tick consumed; 0 = none yet)
64  u32    arena_seq      (increments when the arena restarts)
68  u16    world_len
70  u16    flags          (bit 0: arena live = the round has begun (INTRO … ENDED), bit 1: final over = round phase ENDED)
72  World bytes (rush_core::World::to_bytes, world_len ≤ 2048)
```
Emulator notes (v0): before the round begins the World is in free play: `round.mode` 0 and `round.phase` 5 (SANDBOX),
not 0 (WAITING), and `flags` = 0. The emulator does not run the match director, so in an emulator Final
`round_index`, `variant` and `modifier` stay 0 and `hazard_seed` is the arena seed as given (no `deal::hazard_seed`);
`dir` (DirectorState) stays default. Section offsets inside the World come from `rcx_layout` (tools/chain-emulator
README); rush-core 0x10001: players at 22 (stride 115, slot rules +46), round state at 1768 (result +52), world_len 2037.
Config keys: `world.offset` (72), `world.lenOffset` (68), `world.tickOffset` (12), `world.appliedTickOffset` (16),
`world.coreVersionOffset` (8), `world.flagsOffset` (70). Real P2 values replace these at G-layout.

## 4. Emulator transport (local WebSocket, default `ws://127.0.0.1:8787/`)
Text frames are JSON control; binary frames carry inputs and World.

Client → emulator:
| frame | meaning |
|---|---|
| text `{"t":"join","name":"…","session":"<base58 session pubkey>","authority":"<base58 burner pubkey>","spectate":false,"slot":3}` | join the arena (gets a human slot) or spectate. `slot` (optional): take exactly this human slot (a `/final` roster slot); without it the lowest free human slot. Re-sending `join` on the same connection keeps its slot |
| binary `0x02 u32 txid InputBatch` | `submit_input` (fire-and-forget; a confirm comes back) |
| text `{"t":"get"}` | backup poll (1 s): replies with a `0x91` World frame (never dropped) |
| text `{"t":"ping","c":<client ms>}` | RTT probe → `{"t":"pong","c":…}` (shaped like everything else) |

Emulator → client:
| frame | meaning |
|---|---|
| text `{"t":"joined","slot":0,"arena":"emu:1","map":"CrumbleShelf","rate":30,"coreVersion":65537,"match":null,"seed":"<16 hex>","roster":[{"slot":0,"name":"…","kind":"human"},{"slot":1,"kind":"bot"},…]}` | join result (`slot` 255 = spectator). Also re-sent to every joined connection whenever a new arena starts (restart, `/final`, `/reset`); a connection whose slot is not a human slot of the new roster gets slot 255 and must `join` again |
| text `{"t":"full"}` / `{"t":"err","why":"…"}` | join refused (`err`: slot not a human slot / taken; also bad JSON, unknown `t`) |
| binary `0x83 u32 txid u8 status u32 chain_tick` | confirm after the confirm latency. Per batch: each command is checked alone (tick > World.tick and ≤ World.tick + 8) and stored if in window; status 0 = ≥ 1 command stored, 1 late = none stored and some were for stepped ticks, 2 out of window = none stored, all beyond + 8 (also a malformed batch), 3 bad slot (not this connection's slot). `chain_tick` = World.tick when the batch executed |
| binary `0x90 u32 notif_seq WorldAcc` | account notification, every tick (may be dropped: loss). `notif_seq` is one counter per emulator process, +1 per published World (every tick and once at each arena start), so a gap = lost notifications |
| binary `0x91 u32 notif_seq WorldAcc` | poll reply: the latest published World and its `notif_seq` (never dropped) |

Shaping (both directions, per frame): one-way delay `delayMs ± jitterMs` (uniform; default 45 ± 10), notification loss
(default 0.1 %), confirm latency (default 120 ms after arrival). Frames stay in order per direction (like a WS).
Loss applies to `0x90` only; text frames, confirms and `0x91` are never dropped. Inputs are validated when they
arrive (after the client → emulator delay), against the World.tick at that moment.

Arena lifecycle (emulator): new World (every roster slot active, bots flagged) → free play until every human slot
has joined or `--autostart` s → `begin_round` with the director's Final parameters (target 1, participants = the
roster, intro 60 / countdown 90 ticks at 30 Hz: RUSH = begin tick + 151) → ENDED → `--restart` s later a new arena
(`arena_seq` + 1, next seed = splitmix64(seed), same roster; joined connections keep their slot).

## 5. Emulator control (HTTP on the same port)
- `GET /status` → `{arena, seq, map, seed, match, state ("waiting"|"round"|"ended"|"stopped"), phase, phaseName, tick,
  rushTick, rushEnd, notifSeq, roster, players[{slot, kind, name, joined, applied, pos, out}], inputs{batches, accepted,
  late, window, badSlot} (accepted / late / window count commands), stepMs{avg, max, ticks}, result, results,
  connections, shaping}`
- `POST /final` body `{"match":"<id>","seed":"<16 hex>","roster":[{"slot":0,"name":"…","kind":"human"|"bot"},…]}` →
  restarts the arena as the Lid Flip Final with exactly this roster + hazard seed (task F: the Classic server calls it
  at the end of round 3). Reply `{"ok":true,"arena":"emu:<seq>","startsInMs":…}`; `startsInMs` = the autostart
  wait if the roster has humans (the round begins earlier, as soon as every human slot has joined), else one tick.
  Humans join their roster slot with `{"t":"join","slot":N,…}`. Bad roster / seed → 400 `{"ok":false,"err":"…"}`.
- `POST /reset` → back to the default arena.
