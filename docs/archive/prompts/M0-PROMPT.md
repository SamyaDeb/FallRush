You are the senior Solana / MagicBlock engineer on **Fall Rush**, an original 12-player knockout party game being built for the MagicBlock Solana Blitz V10 hackathon. The deadline is 2026-11-27 14:00 UTC.

Your task is **Milestone M0: repo foundation + MagicBlock lifecycle spike**. Do M0 only. Do not touch Unity, the backend service, or the web app.

## 0. Context: read these first, fully

The repo is at `/Users/samya/Downloads/FallRush`. Read these before writing anything:

| Doc | What it gives you |
|---|---|
| `docs/00-README.md` | Index and the rules you cannot break |
| `docs/07-MAGICBLOCK-INTEGRATION.md` | **Primary spec.** Pinned versions, program IDs, verified code patterns, local stack |
| `docs/06-ANCHOR-PROGRAM.md` | Final program design. M0 builds a forward-compatible subset of it |
| `docs/14-IMPLEMENTATION-PLAN.md` §2 "M0" | Scope and exit criteria |
| `docs/15-DECISIONS-AND-RISKS.md` | Approved decisions |
| `docs/11-SECURITY-ANTI-CHEAT.md` §3 | Keys policy |
| `docs/12-TESTING-PLAN.md` | Test expectations |
| `PROJECT_STATUS.md` | Current status. You will update it |

`fall-rush-docs/` is the frozen v1 spec. Where it conflicts with `docs/`, ignore it.

## 1. Non-negotiable rules

1. **Never invent** SDK functions, macros, program IDs, PDA seeds, account layouts, env vars or package versions.
   - Source of truth: the **published `ephemeral-rollups-sdk` 0.16.2 crate source**, and MagicBlock's official examples at `github.com/magicblock-labs/magicblock-examples` commit `101e8fd` (2026-11-13). Clone the examples into the scratchpad, not into the repo.
   - Read these examples before coding: `counter/anchor`, `roll-dice/anchor/programs/roll-dice-delegated`, `rewards-delegated-vrf`, and `scripts/` (`test-locally.sh`, `local-env.sh`).
   - If reality differs from doc 07, **follow the SDK and fix doc 07 in the same change**, noting the correction.
2. **Pinned tuple (approved, D-2):**

   | Component | Version / setting |
   |---|---|
   | anchor-cli / anchor-lang | **1.0.2** (`avm install 1.0.2`; `[toolchain] anchor_version = "1.0.2"` in Anchor.toml; the machine currently has 1.2.1) |
   | ephemeral-rollups-sdk | **=0.16.2**, features `["anchor","vrf"]` |
   | @coral-xyz/anchor | **0.32.1** |
   | @magicblock-labs/ephemeral-rollups-sdk | **0.16.2** |
   | @solana/web3.js | exact version pinned at install (^1.98.x line) |
   | Local stack | `@magicblock-labs/ephemeral-validator@0.14.10` |

   - MagicBlock CI uses Solana 3.1.9 and Rust 1.89.0. The local machine has solana-cli 3.1.10 and rustc 1.96.1. If the build fails because of the toolchain, add `rust-toolchain.toml` with 1.89.0 and record it.
   - Commit all lockfiles. No floating `latest` anywhere.
   - Do **not** use SDK 0.20.x, BOLT, or session keys in M0.
3. **Keys:**
   - Nothing secret goes in git. Create `.gitignore` before the first commit, and make it cover `.env`, `*.json` keypairs outside `target/deploy` (and `target/` entirely), and `.anchor/`.
   - Localnet uses the default `~/.config/solana/id.json`.
   - For devnet, create a **separate** game-authority keypair at `~/.config/fall-rush/devnet-game-authority.json`. It must sit outside the repo.
   - Ask me to fund it, and tell me the address and roughly how much SOL is needed. The program upgrade authority stays on my default wallet.
4. **Trust and wording:** never describe anything as "trustless" or "cheat-proof". Don't claim something works until a test or run proves it. Label anything unproven as UNVERIFIED.
5. **8 GB MacBook Air M2:** run the MagicBlock local stack only while testing, and stop it at the end (it also uses ports 8899, 7799 and 6699).
6. **Git:**
   - `git init` with a `main` branch.
   - Make small local commits at logical checkpoints with clear messages.
   - **Never push, and never add a remote.**
7. **Global installs:** ask me before any global install other than the pinned `ephemeral-validator`.

## 2. Workflow

1. **Before coding**, give me a short plan with:
   - the files you will create
   - the exact versions you will pin
   - any discrepancy you found between doc 07 and the real SDK or examples

   Then proceed without waiting for approval. M0 is already approved.
2. Implement in this order:
   1. scaffold
   2. program
   3. local tests
   4. devnet script
3. Run the builds and tests yourself. **Fix the errors; don't hide them.** Never skip, comment out or weaken a failing assertion to get green.
4. **Time-box:** if one blocker eats more than ~2 hours (for example, the VRF callback never arrives, or the commit fails), stop. Report the exact error, logs, what you tried, and the options (e.g. high-priority VRF ix, a different devnet validator such as `devnet-eu`, SDK 0.17.3). Let me decide.

## 3. Deliverables

### 3.1 Monorepo scaffold (root)

- `package.json` (pnpm workspace; private)
- `pnpm-workspace.yaml` (`programs`, `packages/*`, `server`, `web` — the last two can be absent for now)
- `.gitignore`, `.editorconfig`, `.nvmrc` (24), `README.md` (short: what this is, how to run M0)
- `.env.example` (placeholders only; variable names from doc 13 §3)
- `docs/` stays as is
- `packages/shared/` with `config/ruleset_v1.json` and `config/map_pool_v1.json`
  - Minimal canonical JSON, consistent with GDD §4.1 and doc 06 §4, and the MVP pool: R1 `[BlockPartyDash]`, R2 GooTower, R3 StarScramble, R4 CrumbleShelf.
  - Add a tiny `hash.ts` script that prints the sha256 of the canonical (sorted-key, no-whitespace) JSON.

### 3.2 Anchor program `programs/fall_rush` (a forward-compatible subset of doc 06)

**Accounts**
- `GameConfig` at `[b"config"]`, with these fields:
  - version, bump, admin, game_authority
  - ruleset_version, ruleset_hash
  - map_pool_version, map_pool_hash
  - paused, match_count
- `Match` at `[b"match", match_id: [u8;16]]`. **Use the doc 06 §2.2 layout now, with fixed arrays and no Vec**, so M6 doesn't need a migration.
  - M0 only exercises these fields: `status`, `player_count`, `current_round`, `round_targets`, `event_seq`, `transcript_hash`, `vrf_seed`, `deal_*`, and `slots`.
  - The `picks` array exists but is unused.

**Instructions in M0**
- `initialize_config`
- `create_match(match_id, roster_hash, player_count)`
  - Derives `round_targets` on-chain from `player_count` (GDD §4.1).
  - Sets slots `0..n` to alive.
- `delegate_match(match_id)`
  - Uses `#[delegate]`. The validator comes from `remaining_accounts[0]`.
  - Check status `Created` by manually deserializing the `UncheckedAccount` first.
- `request_deal()`
  - Runs on the ER, with `#[vrf]`.
  - Uses `create_request_scoped_randomness_ix`.
  - The queue is pinned with an `address =` constraint: `DEFAULT_EPHEMERAL_QUEUE`, or `DEFAULT_EPHEMERAL_TEST_QUEUE` under a cargo feature `localnet`.
  - `caller_seed = sha256("fallrush-deal" ‖ match_id ‖ ruleset_hash ‖ map_pool_hash)`.
- `deal_callback(randomness: [u8;32])`
  - Uses `#[vrf_callback]` (the scoped identity, **not** the deprecated global identity).
  - Stores `vrf_seed`.
  - Derives `deal_maps`, `deal_variants` and `deal_modifiers` with **rejection sampling** via `sha256(r ‖ label)`, as in doc 06 §4.
  - Appends to the transcript.
- `start_round(round, seq)`, `record_qualified(round, slot, placement, seq)`, `record_eliminated(round, slot, reason, seq)`
  - Each enforces the doc 06 §3 preconditions.
  - Each enforces `seq == event_seq`, then increments it.
  - Each updates the rolling transcript hash with `hashv`.
- `complete_round(round, seq)`
  - Uses `#[commit]`.
  - Preconditions: no alive slots remain, and qualified ≤ target.
  - Then `MagicIntentBundleBuilder::…commit(&[match]).build_and_invoke()`, calling `exit(&crate::ID)` first.
- `finalize_match()`
  - Uses `#[commit]`.
  - Valid from `RoundResolved` or `Completed`; this M0 shortcut is fine, but write it down.
  - Calls `commit_and_undelegate`.
  - **Optional in M0:** `complete_match`, if time allows.
- `abort_match(reason)`

**Also required**
- Errors and events as in doc 06 §5–6.
- Signer checks: `game_authority` from config, admin for config.
- Checked arithmetic.

### 3.3 Local tests `programs/tests/lifecycle.ts` (ts-mocha, following the examples' structure)

Run them on the local MagicBlock stack:
- `mb-stack --reset` with the program loaded
- two `vrf-oracle` processes (base and ER)
- env vars as in the examples' `local-env.sh`
- `anchor test --skip-local-validator`, or the examples' equivalent

Give me a single script, `pnpm m0:test:local`, that starts the stack, waits for readiness, runs the tests and tears everything down.

**Required assertions**
1. After `delegate_match`, the account owner on base is `DELeGGvXpWV2fqJUhqcF5ZSYMS4JTLjteaAMARRSaeSh`.
2. `request_deal` on the ER leads to a callback within 30 s. The account then has status `Dealt`, `vrf_seed` is non-zero, and the deal fields are within range.
3. **Calling `deal_callback` directly with a normal signer fails.**
4. A duplicate `seq` fails, and so does a skipped `seq` (assert the specific error).
5. These fail with specific errors:
   - qualifying an eliminated slot
   - qualifying over target
   - `complete_round` while slots are still alive
   - wrong signer on a gameplay instruction
6. After `complete_round` with commit, fetching the account **from base** shows the updated `current_round` / `event_seq`, once the commit lands. Poll with a timeout.
7. After `finalize_match`, the owner on base is the `fall_rush` program and the final fields match the ER state.

**Deal parity**
- Write a TS port of the deal derivation in `packages/shared/src/deal.ts`.
- Generate `packages/shared/test-vectors/deal_vectors.json` (100 seeds).
- Add a Rust unit test that loads the same vectors and asserts identical output.

### 3.4 Devnet run: `scripts/devnet-lifecycle.ts` (`pnpm m0:devnet`)

1. **Deploy to devnet**, and record the program ID in `Anchor.toml`, `declare_id!` and `packages/shared`.
2. **Run the full lifecycle** against `devnet-as`:
   - ER `https://devnet-as.magicblock.app`
   - validator `MAS1Dt9qreoRMQ14YQuhg8UTZMMzDdKhmkZMECCzk57`
   - VRF queue `5hBR571xnXppuCPveTrctfTU7tJLSN94nq7kv7FRK5Tc`
   - Steps: config (if missing) → create → delegate → deal → round 1 events → `complete_round` (commit) → finalize.
3. **Print every signature and the layer** it ran on (Solana or ER), plus account state snapshots between steps.
4. **Write a markdown proof table** to `docs/proof/M0-devnet.md`.
   - Solana tx links use `https://explorer.solana.com/tx/<sig>?cluster=devnet`.
   - For ER txs, find the documented way to view them (MagicBlock docs/explorer). If none is documented, mark them UNVERIFIED and include the raw signature plus the RPC used.
5. **Get the Solana-side signatures of the commits** using the SDK helper the examples use (`GetCommitmentSignature` in `advanced-magic.ts`; verify the export name in TS SDK 0.16.2). Add them to the table.

## 4. Definition of done (report each item with evidence)

- [ ] `anchor build` passes on the pinned tuple (show the versions actually used)
- [ ] `pnpm m0:test:local` is green; paste the summarised test output
- [ ] Deal parity test is green in Rust and TS
- [ ] Devnet lifecycle completed; `docs/proof/M0-devnet.md` has all signatures
- [ ] No secrets in git (show `git status` and a grep for keypair-like strings)
- [ ] `docs/07` and `docs/06` corrected wherever reality differed (list the changes)
- [ ] `PROJECT_STATUS.md` updated: M0 done, proof link, open issues, and anything that matters for M6 (for example, typed-account delegation limits, VRF latency measured on devnet, commit latency)
- [ ] Local stack stopped

## 5. Final report format

1. What was built (files)
2. Versions actually pinned
3. Test results (passed/failed counts, with any failure explained)
4. Devnet proof summary: measured latencies for VRF callback, ER tx confirm and commit landing on Solana
5. Deviations from the docs, and why
6. Risks and unknowns for M6
7. How I can run it myself: exact commands
8. Next milestone: M1 (Unity foundation + WebGL↔server WebSocket spike), per `docs/14`
