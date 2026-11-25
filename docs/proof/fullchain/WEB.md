# Full on-chain match: web + replay (fullchain, 2026-11-25)

CONTRACT.md §5. Everything here is additive: hybrid matches (rounds 1–3 Classic, Final on-chain) keep today's
labels and today's replay path byte for byte.

## What changed
- **wasm ABI** (`crates/rush-core-wasm/src/abi.rs`, shared with rush-core-ffi; ABI version stays 1, `rc_version` 0x10002):
  - `rc_chain_map(h, slot, ptr, len)`: registers the blob of program map slot 0..3 (deal ids 0 Capsule Run, 1 Goo
    Tower, 2 Star Fountain, 3 Lid Flip) or 4 (lobby). From then on the handle's map follows the World exactly as
    `sim.rs tick_inner` picks it (`round_index >= 1 ? map_key : LOBBY`), also on `rc_set_world`.
  - `rc_chain_load(h)`: the part-0 map load (`director::needs_map` → `load_round`).
  - `rc_step_part(h, inputs, from, budget)`: ONE program execution (`step_slice`, fresh Scene + events, map load at part 0).
  - `rc_step_chain(h, inputs, budget)`: a whole tick as the chain runs it (`step_slice` from part 0 until 0).
  - `rc_round(h)`: director round | phase << 8 | round_index << 16 | map_key << 24.
  - Old exports unchanged; a handle without `rc_chain_map` behaves exactly as before.
- **`tools/p7/replay-check.mjs`**: takes a match address (indexer) or a local `world.bin` / `world2.bin`. Full mode
  (receipt trust bit 4, a version 2 World, or a dev-runner `world.bin`) checks **every snapshot incl. mid-tick parts**
  from the lobby through R1–R4 with the chain-exact step; hybrid mode = the old Final-only `rc_step` path.
  Map blobs: `maps/`, `--maps dir`, `--map-<Name> path`, or `--sealed` (reads the 5 sealed map accounts in
  `summary.maps` from Solana). `--full` / `--final` force a mode.
- **web/app**: `lib/core.ts` `CoreWorld.createChain()` (+ `stepChain`, `stepPart`, `chainLoad`, `round`);
  `lib/verify.ts` `verifyFull` (same algorithm, uses the sealed blobs from `summary.maps` when the summary has them);
  `/receipt`: trust bit 4 → every round ◆ program, per-round "N of M advanced" from the Match's SlotResults, the line
  "round ticks are sent by MagicBlock's task scheduler and a permissionless keeper crank", replay over 4 rounds;
  `/replay`: all 4 rounds, per-round byte-exact counts, label = round + map; `/live`: any round 1–4 drawn on the World's
  map (full = World version 2 or the World in rounds 1–3), phase "◆ On-chain round r/4 · map"; `view2d` frames the
  players when the map has no lid grid; `/arenas` list pill "◆ all 4 rounds on-chain"; `scripts/assets.mjs` copies all
  5 map blobs and honours `RUSH_CORE_WASM=<path>`.
- **Indexer**: `brief()` adds `allOnChain` (receipt trust & 4) and `trust`; `finalOnChain` is also true for full
  matches; `/api/matches` returns `counts {total, finalOnChain, allOnChain}` (of the listed, non-hidden set).

## Build
```
cd crates && bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh fullchain-wasm -- env CARGO_BUILD_JOBS=2 \
  CARGO_TARGET_DIR=/Users/samya/Downloads/FallRush-fullchain/crates/target nice -n 10 \
  cargo build --offline --release -p rush-core-wasm --target wasm32-unknown-unknown
# → crates/target/wasm32-unknown-unknown/release/rush_core_wasm.wasm  (324,835 B, sha256 5f69a7d9…87490a5)
```
(`scripts/p4-build-core.sh` builds the same file and installs it in StreamingAssets.)

## Tests (2026-11-25)
| test | result |
|---|---|
| `replay-check.mjs docs/proof/p2/runs/fc-devnet-1-m1/world.bin --maps <sealed blobs>` | 47,171 / 47,171 byte-exact (47,141 mid-tick), 4 map loads, R0–R4 all exact, transcript ✓ |
| `replay-check.mjs docs/proof/p2/runs/fc-devnet-t2-m1/world.bin --sealed` | 37,785 / 37,785 byte-exact, 4 map loads, transcript ✓ |
| same, repo `maps/` (current GooTower ≠ sealed) | 37,240 / 37,785: R2 only fails (tick 4591) — negative control |
| hybrid `replay-check.mjs 3Jnhbr…` old + new wasm | 2,893 / 2,893 both, 1 drop, transcript ✓ |
| hybrid `3Jnhbr… --full` (chain-exact path on a Final archive with inputs + a drop) | 2,893 / 2,893 |
| `web/app` `lib/verify.ts` bundled with esbuild, Node harness, both local runs + 3Jnhbr… | same numbers; sealed blobs fetched from devnet; old wasm → clear error |
| vector parity `tools/p3/rc-subset.mjs` + `ffi-expect.mjs`, new vs old wasm | identical; ffi = committed `ffi_expected.txt` |
| `tsc -p web/app/tsconfig.json`, `cargo clippy/test -p rush-core-wasm`, `cargo check -p rush-core-ffi` | clean |
| indexer on a scratch ARCHIVE (trust 3 + trust 6) | `counts {2, 2, 1}`, flags right |

The local runs are World v1 (old program): the v1 full-match path is proven; **the v2 path (idle slots skipped)
is only exercised by the same code (`step_slice` branches on `w.version`), not yet by a v2 devnet recording.**

## UNVERIFIED
- A World v2 recording from the upgraded program (none exists yet); a full match with human inputs (`world2.bin`
  header) and Chaos Drops in the full path.
- `/live`, `/receipt`, `/replay` DOM in a browser (type-checked + logic run in Node only; vite not installed here).
- Receipt round lines rely on the program mirroring SlotResult `{state, round}` for full matches (sim.rs `mirror_match`)
  and on `finalTranscript` holding the director's whole-match transcript; market open windows in `/live`
  (`marketOpen`) still mirror today's C# rules.
- Replay is exact only with the blobs the program read: live arenas must re-upload Goo Tower (RESEARCH §1) or the
  summary must list `maps` (then `/receipt` + `--sealed` use the sealed copies).
- 12-player view framing on Capsule Run (follows the pack; not tuned).
