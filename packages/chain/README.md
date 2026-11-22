# @fall-rush/chain — `fallrush-chain.js`

The browser chain bundle for the Unity WebGL client (P3.2, `docs/19-ONCHAIN-PLAN.md` §5 P3). It holds the burner and
session keys, talks to the **emulator** (`tools/chain-emulator`, wire `docs/proof/p3/wire-v0.md`) or the **MagicBlock
ER** (web3.js), and queues World updates for Unity to poll. Everything layout-specific comes from a JSON config
(`config/emulator.json`, `config/devnet.json`), so Part 2 (P2's real program) only edits JSON.

```sh
cd packages/chain
bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p3-pnpm -- pnpm install --ignore-workspace   # own lockfile
pnpm build            # → client-unity/Assets/StreamingAssets/fallrush-chain.js (IIFE, minified), prints raw/gzip size
pnpm build -- --meta  # + bytes per package
pnpm test             # node --test 'test/*.test.ts'   (from the repo root: node --test 'packages/chain/test/*.test.ts')
```
Node 24 does not accept a bare directory for `--test`, so use the quoted glob. The test `integration.emulator.test.ts`
starts `tools/chain-emulator/src/main.mjs` on a random port (localhost only) and is skipped if that file is missing.
`bundle.test.ts` loads the built bundle in a bare `vm` context and is skipped if the bundle hasn't been built.

Dependencies: `@solana/web3.js` 1.99.0 (same version as `programs/`), `buffer` 6.0.3 (the version web3.js pins;
injected as `globalThis.Buffer` by `src/buffer-shim.ts`), dev `esbuild` 0.28.2. **`@magicblock-labs/ephemeral-rollups-sdk`
is not used.** The bundle needs no delegation or commit helpers, the ER is a plain RPC + WS endpoint for
`submit_input`, and leaving the SDK out keeps the bundle small.

## API (`globalThis.FallRushChain`)

Async calls return `Promise<string>` (JSON). Everything else is synchronous, so the `.jslib` can poll it every frame.

| call | returns |
|---|---|
| `init(configJson)` | `{ok, version, name, transport, persistentStorage, burner, sessionPubkey, session, phantom}`. Stops any previous transport. |
| `wallet()` | `{burner, sessionPubkey, session:{token, validUntil, authority}, phantom:{available, connected, pubkey}}` |
| `exportBurner()` | the burner's 64-byte secret key in base58 (for the user to copy). The bundle never logs it. |
| `importBurner(secretB58)` | `{ok, burner}` |
| `phantomConnect()` / `phantomSignMessage(text)` | `{ok, pubkey[, signature b58]}`, using `window.solana` only |
| `session(action, argsJson)` | `create` / `topup` / `revoke` / `status`, see below |
| `join(argsJson)` | `{ok, slot, arena, map, rate, coreVersion, roster, world}` (slot 255 = spectator). args: `{name, spectate, slot?, map?}` |
| `sendInputs(bytes)` | a local txid (u32). An InputBatch (wire-v0 §2) is sent fire-and-forget. A bad batch gives an `error` event. |
| `subscribe(addr)` / `unsubscribe(addr)` | `addr` is base58 or `"$world"` (default). The emulator ignores the address. |
| `drainWorlds(into: Uint8Array, maxItems)` | bytes written. Entries, oldest first: `u32 len · f64 recvMs · u32 seq · WorldView[len]` (LE). Stops before an entry that doesn't fit and keeps it queued. |
| `pendingWorlds()` / `latestTick()` | queue length / the newest tick (-1 if none yet) |
| `latestWorldBase64()` | the newest WorldView, base64 (the SendMessage alternative) |
| `drainEvents()` | a JSON array of the events since the last call (below) |
| `stats()` | `{updatesPerSec, inputBatchesPerSec, confirmP50, confirmP95, confirmN, confirmOk, confirmBad, confirmTimeouts, sendErrors, rttMs (EWMA), gaps, missingTicks, polls, pollFills, reconnects, lastTick, queueDrops, queued, eventsDropped, …transport status}` |
| `stop()` | closes sockets and timers |

**WorldView** (normalised, so C# never needs P2's offsets): `0 u32 tick · 4 u32 core_version · 8 u32 arena_seq ·
12 u16 flags · 14 u16 world_len · 16 u32×12 applied_tick · 64 World bytes (world_len)`. A field whose config offset
is `null` reads as 0. The queue keeps the newest `queue.maxWorlds` (64). Duplicate or older ticks are dropped. A change
of `arena_seq` (an arena restart) resets the tick baseline.

**Events** (each has `type` and `at` = performance.now() ms):
- `confirm {txid, status, ms, chainTick?|err?, kind?, layer?}`. Emulator statuses: `ok`, `late`, `window`, `badSlot`, `timeout`. Rollup statuses: `ok`, `err`, `timeout`.
- `gap {from, to, missing, source}`, `pollFilled {tick, prev}`, `arenaChanged`.
- `wsOpen`, `wsClosed`, `wsReconnect {transport, how?}`, `rejoined`.
- `clonerRetry`, `session {state | action, …}`, `burnerCreated`, `error {where, why}`.

The log is bounded (`queue.maxEvents`). When it overflows it drops the oldest events and counts them in `eventsDropped`.

**Session actions:**
- `create {validitySec?, topUp?, lamports?, authority?:'phantom', force?}`: sends `create_session_v2` on the base layer. The burner is authority and fee payer unless `authority` is `'phantom'`. The session key signs too.
- `topup {lamports}`: a system transfer from the burner to the session key.
- `revoke`: sends `revoke_session_v2`. The authority signs while the token is still valid. Afterwards the session key is replaced with a new one.
- `status`: the local state plus the decoded on-chain token and the session key's balance.

On the emulator, all four are simulated locally (`emulated: true`).

## Transports

**emulator** (wire-v0 §4):
- Connects a browser `WebSocket` with `binaryType = arraybuffer`.
- Join is the text `join` message. Inputs are `0x02 u32 txid InputBatch`. A `0x83` frame becomes a `confirm` event, timed from the send.
- `0x90`/`0x91` World frames are queued only while subscribed.
- While subscribed, it polls with `{"t":"get"}` every `pollMs`. It sends `ping`/`pong` every `pingMs` and keeps the RTT as an EWMA (α 0.2).
- On close it reconnects with exponential backoff and jitter (`reconnect.minMs`…`maxMs`). After reconnecting it sends `join` again and polls at once.

**rollup:**
- Uses two web3.js `Connection`s: the ER (`erHttp` and `erWs`) and the base layer.
- `submit_input` is built from `rollup.submitInput`. The fee payer and signer is the session key, with the ER blockhash refreshed every `blockhashRefreshMs`. It is sent with `sendRawTransaction(skipPreflight, maxRetries 0)` and never awaited.
- One `ConfirmPoller` per connection calls `getSignatureStatuses` with ≤ `confirmBatch` (≤ 256) signatures every `confirmPollMs`, with one call in flight at a time. Signatures not seen yet move to the back of the queue. A signature gives up after `confirmTimeoutMs`.
- `subscribe` calls `onAccountChange` on the ER and also reads the account with `getAccountInfo` every `accountPollMs`. Gaps are detected from the World tick.
- If the WS is silent for `stallMs` it re-subscribes, with backoff. After 3 stalls in a row it builds a new ER `Connection` (a new socket) and restores every subscription there.
- Base-layer sends (session create/topup/revoke, and join if `layer: base`) go through a token bucket (`baseTxPerSec` ≤ 1.5, capacity `baseBurst`), because of P0 quirk 9.
- P0 quirk 7: a send that fails with `pending request owner … Cloner` is retried every `clonerRetryEveryMs`. Join retries for up to `clonerRetryMs` (5 s); input batches for up to `inputClonerRetryMs` (300 ms), since they go stale.

## Config fields

| field | meaning |
|---|---|
| `version` | `1` |
| `name` | label returned by `init` |
| `transport` | `"emulator"` \| `"rollup"` |
| `addresses` | named addresses, usable as `"$name"` refs (`world`, `arena`, `program`, …) |
| `world.offset` | byte offset of the rush-core World inside the account (emulator 72) |
| `world.lenOffset` | offset of a u16 World length, or `null` = account length − offset (emu 68) |
| `world.tickOffset` | u32 tick (emu 12). Gap detection uses it. |
| `world.appliedTickOffset` / `appliedTickCount` | u32 × N per-slot applied tick (emu 16 / 12), or `null` |
| `world.coreVersionOffset` | u32 rush-core version (emu 8), or `null` |
| `world.flagsOffset` | u16 flags (emu 70), or `null` |
| `world.arenaSeqOffset` | u32 arena restart counter (emu 64), or `null` |
| `world.magicHex` | magic bytes at offset 0 (emu `FRWORLD0`), or `null` to skip the check |
| `storage.burnerKey` / `sessionKey` | localStorage keys (default `fallrush.burner.v1` / `fallrush.session.v1`) |
| `queue.maxWorlds` / `maxEvents` | 64 / 512 |
| `emulator.url` | `ws://127.0.0.1:8787/` |
| `emulator.pollMs` / `pingMs` / `joinTimeoutMs` | 1000 / 1000 / 5000 |
| `emulator.reconnect.minMs` / `maxMs` | 250 / 5000 |
| `rollup.erHttp` / `erWs` | `https://devnet-as.magicblock.app` / `wss://devnet-as.magicblock.app` |
| `rollup.baseHttp` / `baseWs` | base-layer RPC (devnet), WS optional |
| `rollup.commitment` | ER commitment (`processed`) |
| `rollup.baseTxPerSec` / `baseBurst` | token bucket (≤ 1.5 enforced) / capacity 1 |
| `rollup.confirmPollMs` / `confirmBatch` / `confirmTimeoutMs` | 50 / 256 / 5000 |
| `rollup.accountPollMs` / `stallMs` / `rttPollMs` / `blockhashRefreshMs` | 1000 / 2000 / 2000 (`getSlot`) / 10000 |
| `rollup.clonerRetryMs` / `clonerRetryEveryMs` / `inputClonerRetryMs` | 5000 / 250 / 300 |
| `rollup.computeUnitLimit` | adds `SetComputeUnitLimit` to `submit_input` when set |
| `rollup.submitInput` | an **InstructionSpec** (below). The InputBatch is the `$batch` ref and its first byte is `$slot`. |
| `rollup.join.ix` | optional InstructionSpec (refs `$name`, `$slot`), sent on `layer` `er` \| `base` and awaited up to `confirmTimeoutMs` |
| `rollup.join.slotTable` | `{account, offset, stride, count, keyOffset, key: authority\|session}`: our slot is the entry whose 32-byte key matches. Also gives `roster`. Without it, `join({slot})` is used. |
| `rollup.join.map` / `rate` | values returned by `join` |
| `session.programId` | `KeyspM2ssCJbqUhQ4k7sveSiY4WjnYsrXkC8oDbwde5` |
| `session.targetProgram` | the game program (base58 or `$ref`). It is a seed of the token PDA. |
| `session.validitySec` | token lifetime, capped at 7 days − 120 s |
| `session.topUp` / `topUpLamports` | `create_session_v2` `top_up` / `lamports` (program default 0.01 SOL) |

**InstructionSpec:** `{programId, discriminator (16 hex), accounts: AccountSpec[], args: ArgSpec[]}`.
- **AccountSpec** is `{address | pda:{seeds, programId?}, signer, writable}`.
- A **seed** is one of `{utf8}`, `{hex}`, `{pubkey}`, `{u8}`, `{u16le}`, `{u32le}` or `{u64le}`. A value can be a literal or a `"$ref"`.
- An **ArgSpec** `type` is one of:
  - `raw` (bytes as-is) or `vec` (borsh `Vec<u8>`: u32 LE length, then the bytes);
  - `u8`, `u16`, `u32`, `u64`, `i64` or `bool`;
  - `pubkey`;
  - `string` (borsh).

  Each arg takes a `value` or a `from: "$ref"`.
- **Built-in refs:** every `addresses` entry, plus `burner`, `authority` (the session's authority, defaulting to the burner), `session`, `sessionToken` (the SessionTokenV2 PDA), `sessionProgram`, `targetProgram` and `systemProgram`, and also `batch`, `slot` and `name` where they apply. To include the session token account in `submit_input`, list `{"address":"$sessionToken"}`.

`config/devnet.json` is a **placeholder**: `TODO_P2_*` addresses, and the emulator's World offsets. Its `submit_input` is
shaped after doc 19 (`InputBuf` PDA `["in", arena, slot]`, a `Vec<u8>` arg, discriminator `sha256("global:submit_input")[0..8]`
= `38b89a5bad3f158a`). The real values come from P2.

## SessionTokenV2 encoding (our own TS, `src/session.ts`)

Source: `~/.cargo/registry/src/index.crates.io-1949cf8c6b5b557f/session-keys-3.1.1/src/lib.rs` (gum `gpl_session`,
Anchor `>=0.28,<2.0`).

| item | value | source |
|---|---|---|
| program id | `KeyspM2ssCJbqUhQ4k7sveSiY4WjnYsrXkC8oDbwde5` | lib.rs:12 |
| `create_session_v2(top_up: Option<bool>, valid_until: Option<i64>, lamports: Option<u64>)` | discriminator `sha256("global:create_session_v2")[0..8]` = `dfe96c0741c2eb26`, then borsh options (`00` = None, `01`+value) | lib.rs:58-66 |
| create accounts (in order) | `session_token` (w, PDA, init, payer = fee_payer) · `session_signer` (s, w) · `fee_payer` (s, w) · `authority` (s) · `target_program` (executable) · `system_program` | lib.rs:270-297 |
| `revoke_session_v2()` | `sha256("global:revoke_session_v2")[0..8]` = `d33b7dbc2b9b0866`, no args | lib.rs:68-70 |
| revoke accounts | `session_token` (w; has_one fee_payer + authority, close = fee_payer) · `fee_payer` (w) · `authority` (must sign while the token is still valid) · `system_program` | lib.rs:376-413 |
| PDA seeds | `["session_token_v2", target_program, session_signer, authority]` under the program id | lib.rs:274-279, 484 |
| account | `sha256("account:SessionTokenV2")[0..8]` = `b20355fe0d748029` · authority 32 · target_program 32 · session_signer 32 · fee_payer 32 · valid_until i64 LE = **144 B** | lib.rs:439-448, 483 |
| limits | `valid_until ≤ now + 7 days` (lib.rs:324-327). Expired when `now ≥ valid_until` (lib.rs:488-492). Top-up default `LAMPORTS_PER_SOL / 100` (lib.rs:338-339). Valid when the PDA matches and the token is not expired (lib.rs:495-515). | |

The crate contains no top-up instruction, so `topup` is a plain system transfer to the session key.
