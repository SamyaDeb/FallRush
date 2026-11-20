# Fall Rush

An original 12-player, 4-round knockout party game for the **MagicBlock Solana Blitz V10** hackathon. Match results are recorded on a MagicBlock Ephemeral Rollup and committed to Solana; the map/modifier deal comes from MagicBlock VRF. Specs live in [`docs/`](docs/00-README.md).

**Trust model:** the authoritative game server decides physics and who qualified. The `fall_rush` program accepts results only from the configured game authority, in strict sequence, for roster slots, with counts consistent with the committed ruleset; the deal is derived on-chain from VRF. It does **not** prove that gameplay was fair.

## Layout
| Path | What |
|---|---|
| `programs/` | Anchor 1.0.2 workspace: `fall_rush/` program, `tests/lifecycle.ts` |
| `packages/shared/` | canonical `ruleset_v1` / `map_pool_v1` JSON + hashes, `deal.ts`, test vectors |
| `scripts/` | local test runner, devnet deploy + lifecycle |
| `docs/` | engineering docs v2 (`docs/proof/` = devnet proofs) |

## M0: run it
Prereqs: Node 24, pnpm 10, Rust, solana-cli 3.1.x, `avm` with Anchor **1.0.2** (see docs/07 §9 for the install workaround), and `npm i -g @magicblock-labs/ephemeral-validator@0.14.10`.

```bash
pnpm install
pnpm m0:test:local      # build (localnet feature) + Rust/TS deal parity + mb-stack + 2 vrf-oracles + lifecycle test; stops the stack
pnpm hash               # sha256 of the canonical config JSON
pnpm m0:devnet:deploy   # build (devnet queue) + deploy/upgrade on devnet with ~/.config/solana/id.json
pnpm m0:devnet          # full lifecycle on devnet + devnet-as ER; writes docs/proof/M0-devnet.md
```
The devnet game-authority key lives outside the repo at `~/.config/fall-rush/devnet-game-authority.json`.
