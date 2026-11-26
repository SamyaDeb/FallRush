# Trust fix: the game authority can no longer forge results of an on-chain match (2026-11-26)

## What was wrong
- Five legacy instructions recorded Classic-server rounds: `start_round`, `record_qualified`, `record_eliminated`, `complete_round`, `complete_match` (`programs/fall_rush/src/lib.rs`).
- They checked only three things: the game-authority signer, the Match status and `seq`. They did not check whether the program itself was simulating the match.
- In a full on-chain match, `mirror_match` sets the status to `RoundActive` while the director plays. The `seq` field is readable from the account, so the check doesn't block anyone. That let the server key:
  - eliminate players;
  - call `complete_match` with a champion of its choice. `Completed` is sticky in `mirror_match`, and `champion_slot` is only rewritten at the director's CHAMPION phase, minutes later.
- `settle` then paid that slot from every market, and the Crown mint used it.
- The on-chain Final of a hybrid match (`start_final`) had the same hole.
- Found by a review of the demo claims ("our server can't pick a winner"). The hole was never used: our keeper sends these instructions only for Classic rounds 1–3 of hybrid matches, before `start_final`.

## Fix
`Match::require_authority_rounds()` (`state.rs`) refuses all five instructions once `sim::chain_final` is set (`SIM_STARTED | FINAL_MODE`). The refusal is a new error, `LegacyEventInChainMode` = 6048, appended so that existing codes keep their numbers. Classic matches are unchanged. Commit `5015ca7` (main: `e65d642`).

## Evidence
- **The exploit, as a test:** LiteSVM `legacy_events_blocked_in_fullchain` (`crates/rush-core-sbf-check/tests/econ.rs`).
  - On the old live binary (03e85a05) the forge goes through. An attacker's 10 RUSH bet on the forged champion takes the whole 110 RUSH pool, and the Crown is minted to the attacker.
  - On the fix, the first legacy call fails with 6048 and the Match is untouched.
- **Suites on the deployed build** (built in `../FallRush-fullchain`, the tree that reproduces the live bytes):

  | Suite | Result |
  |---|---|
  | econ | 9/9 pass |
  | p6 | 4/4 pass |
  | harden | 1/1 pass |
  | ops | 1/1 pass |

- **Reproducibility:** rebuilding the unfixed source gives exactly 03e85a05. The same 20 pre-existing sBPF stack-size warnings appear with and without the fix.

## Deploy
- **Devnet upgrade:** 2026-11-26 14:13 UTC, signature `32TH52NTHigLqWyf2DkwdLS4FQnPMjDLnvjxehKGMeYG3ui9a1wBnY8rfTKfPfDkiu6vTubX3SQLp1WpkjfGcsHx`, slot 526534775.
- **Bytes on chain:** sha256 `75d7597b976792f03ffc7d1ddc8d2237986204ef6fc54186b48ac7820cd709c6`, checked with `solana program dump`.
- **State before the upgrade:** both arenas idle, with 0 humans.
- **Rollback:** `SO=~/.config/fall-rush/rollback/fall_rush-live-03e85a05.so bash scripts/fc-devnet-upgrade.sh` (run from `../FallRush-fullchain`).
- **Out of scope:**
  - Account layouts and the keeper: unchanged.
  - Clients: never call these instructions.
