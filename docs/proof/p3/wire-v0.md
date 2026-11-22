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
70  u16    flags          (bit 0: arena live, bit 1: final over)
72  World bytes (rush_core::World::to_bytes, world_len ≤ 2048)
```
Config keys: `world.offset` (72), `world.lenOffset` (68), `world.tickOffset` (12), `world.appliedTickOffset` (16),
`world.coreVersionOffset` (8), `world.flagsOffset` (70). Real P2 values replace these at G-layout.

## 4. Emulator transport (local WebSocket, default `ws://127.0.0.1:8787/`)
Text frames are JSON control; binary frames carry inputs and World.

Client → emulator:
| frame | meaning |
|---|---|
| text `{"t":"join","name":"…","session":"<base58 session pubkey>","authority":"<base58 burner pubkey>","spectate":false}` | join the arena (gets a human slot) or spectate |
| binary `0x02 u32 txid InputBatch` | `submit_input` (fire-and-forget; a confirm comes back) |
| text `{"t":"get"}` | backup poll (1 s): replies with a `0x91` World frame (never dropped) |
| text `{"t":"ping","c":<client ms>}` | RTT probe → `{"t":"pong","c":…}` (shaped like everything else) |

Emulator → client:
| frame | meaning |
|---|---|
| text `{"t":"joined","slot":0,"arena":"emu:1","map":"CrumbleShelf","rate":30,"coreVersion":65537,"roster":[{"slot":0,"name":"…","kind":"human"},{"slot":1,"kind":"bot"},…]}` | join result (`slot` 255 = spectator) |
| text `{"t":"full"}` / `{"t":"err","why":"…"}` | join refused |
| binary `0x83 u32 txid u8 status u32 chain_tick` | confirm (status 0 ok, 1 late (tick already stepped), 2 out of window, 3 bad slot) after the confirm latency |
| binary `0x90 u32 notif_seq WorldAcc` | account notification, every tick (may be dropped: loss) |
| binary `0x91 u32 notif_seq WorldAcc` | poll reply |

Shaping (both directions, per frame): one-way delay `delayMs ± jitterMs` (uniform; default 45 ± 10), notification loss
(default 0.1 %), confirm latency (default 120 ms after arrival). Frames stay in order per direction (like a WS).

## 5. Emulator control (HTTP on the same port)
- `GET /status` → `{arena, seq, phase, tick, players, …}`
- `POST /final` body `{"match":"<id>","seed":"<16 hex>","roster":[{"slot":0,"name":"…","kind":"human"|"bot"},…]}` →
  restarts the arena as the Lid Flip Final with exactly this roster + hazard seed (task F: the Classic server calls it
  at the end of round 3). Reply `{"ok":true,"arena":"emu:<seq>","startsInMs":…}`.
- `POST /reset` → back to the default arena.
