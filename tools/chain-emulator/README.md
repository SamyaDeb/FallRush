# chain-emulator (P3)

A fake MagicBlock Ephemeral Rollup for Fall Rush: one arena running the game simulation **rush-core** in real time
at **30 Hz**, so the Unity client can be built before the real on-chain program exists. Node 24, ESM, **no npm
dependencies** (the WebSocket server is hand-written over `node:http`). The wire contract is
[`docs/proof/p3/wire-v0.md`](../../docs/proof/p3/wire-v0.md).

## Run

```sh
node tools/chain-emulator/src/main.mjs                       # Lid Flip Final, slot 0 human + 2 bots, ws://127.0.0.1:8797/
node tools/chain-emulator/src/main.mjs --players 12          # 1 human + 11 bots
node tools/chain-emulator/src/main.mjs --humans 0 --once     # 3 bots play one Final, then exit
node tools/chain-emulator/src/main.mjs --map Lobby           # free-play lobby, no round
node tools/chain-emulator/src/main.mjs --static client-unity/Builds/WebGL --no-shaping
node tools/chain-emulator/src/probe-client.mjs --seconds 10  # headless client: joins, sends inputs, prints stats
```

| flag | default | meaning |
|---|---|---|
| `--port` | 8797 | HTTP + WebSocket port |
| `--host` | 127.0.0.1 | bind address |
| `--map` | CrumbleShelf | `bin/maps/<Key>.mapblob` (CrumbleShelf = Lid Flip Final; Lobby / Sandbox = free play; the race / survival / skill maps run one round with the mode-default target) |
| `--players` / `--humans` | 3 / 1 | roster size; humans take the lowest slots, the rest are bots |
| `--delay` / `--jitter` | 45 / 10 ms | one-way delay ± uniform jitter, both directions, per frame, in order |
| `--loss` | 0.001 | fraction of `0x90` notifications dropped |
| `--confirm` | 120 ms | input confirm latency after arrival (then the shaped delay back) |
| `--no-shaping` | | delay, jitter, loss = 0 (confirm latency stays unless `--confirm 0`) |
| `--seed` | random | hazard seed, 16 hex; restarts use splitmix64 of the previous seed |
| `--autostart` | 20 s | wait for human slots before the round starts; unfilled slots then get no inputs |
| `--restart` | 8 s | after ENDED, start a fresh arena (`arena_seq` + 1; joined connections keep their slot) |
| `--once` | | exit after the first round instead of restarting |
| `--static <dir>` | | also serve files (MIME for .wasm/.js/.data/.json/.html; `x.br` / `x.gz` → `Content-Encoding` br / gzip + the MIME of `x`) |
| `--log <file>` | | append JSONL lifecycle events (arena_start, join, leave, round_begin, round_end with placements, final, reset) |

HTTP on the same port: `GET /status`, `POST /final` (`{"match","seed","roster":[{"slot","kind","name"}]}` → restarts as
the Final with exactly that roster; humans then `{"t":"join","slot":N}`), `POST /reset` (back to the CLI arena).

## Tests

```sh
node --test "tools/chain-emulator/test/*.test.mjs"     # or: npm test --prefix tools/chain-emulator
node tools/chain-emulator/test/make-fixtures.mjs       # regenerate test/fixtures (only if rush-core changed)
```
(Node 24 does not accept a bare directory for `--test`; the quoted glob avoids running `helpers.mjs` /
`make-fixtures.mjs` as tests.) Suites: WS handshake + framing; arena determinism against P2's client core
`bin/rush_core.wasm`; input rules (late / window / bad slot / exact tick / missing → repeat); shaping (loss, order,
jitter) + the drift-free ticker; `/final`, `/reset`, `/status`, static files over real sockets; a full 3-bot Final
on a fake clock; the WorldAcc fixtures.

## Layout

| file | |
|---|---|
| `src/main.mjs` | CLI |
| `src/server.mjs` | HTTP control, static files, WS connections, frames (`0x02` in, `0x83` / `0x90` / `0x91` out), shaping per connection |
| `src/arena.mjs` | the arena: rc_init, input buffers (`submit`), begin_round, step, WorldAcc, results; `Ticker` (30 Hz, drift-free, ≤ 5 catch-up ticks) |
| `src/core.mjs` | WASM wrapper (rc_* / rcx_* C ABI) |
| `src/world-view.mjs` | `decodeWorldAcc(bytes, layout)` — the reference decoder the C# client ports |
| `src/ws.mjs` | RFC 6455 server (masked client frames, text/binary/ping/pong/close, ≤ 64 KiB, no fragmentation) |
| `src/shaping.mjs` | delay / jitter / loss links, fake clock, seeded PRNG |
| `src/probe-client.mjs` | headless client for manual checks |
| `bin/` | `rush_emu_core.wasm` (rush-core + rcx_*), `rush_core.wasm` (P2 client core, tests only), `maps/` — built by `scripts/p3-build-core.sh` |
| `core/` | Rust source of `rush_emu_core.wasm` |
| `test/fixtures/` | `waiting/rush/ended.worldacc` (WorldAcc bytes, no frame header) + `expected.json` (decoded) of a seeded 3-player Final |

World layout (`rcx_layout`, rush-core 0x10001): header 22 B, players at 22 (stride 115; slot rules at +46, bot state
at +89), reactive 318 B, drop queue 48 B, round state at 1768 (142 B; result at +52), director at 1910 (123 B),
total 2037 B. WorldAcc = 72 + 2037 = 2109 B; a `0x90` frame is 2114 B (≈ 63 KB/s per client at 30 Hz).

## What it mimics

- The ER arena account model: one `World` written once per tick by a 30 Hz `tick()`, `submit_input` writing a
  per-slot buffer that accepts commands for ticks > World.tick and ≤ World.tick + 8, rush-core's missing-input rule
  (repeat the last command, press edges cleared, 3 ticks, then neutral), `applied_tick` per slot.
- `start_final`: begin_round with the director's Final parameters; bots run inside rush-core's step.
- Account-subscription transport: a notification per tick that can be lost, a backup poll, a confirm per
  transaction after a fixed latency, one-way delay with jitter, in-order delivery.

## What it does not

- No signatures, no session keys, no fees, no transactions or blockhashes: `session` / `authority` in `join` are
  only recorded. Slot ownership is "this WebSocket connection".
- No match director (no deal, no transcript, no rounds 1–3): `round_index`, `variant`, `modifier` stay 0 and the
  hazard seed is used as given. No Chaos Drops, markets, or settlement. No timed-task failure modes, no CU limits,
  no account size limits, no RPC rate limits; the tick never stalls except when Node itself does.
