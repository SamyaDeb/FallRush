# PB0 — ER SDK 0.18 upgrade (proof)

**Branch:** `pb0` (worktree `../FallRush-pb0`), from `main` @ `2c89cc0`. Date: 2026-11-21.
**Scope:** doc 19 §5 PB0 + `docs/prompts/PB0-PROMPT.md`. Task E (`rush-core` skeleton) was **moved to P1** on 2026-11-21 (prompt edit), so it is not in this branch.

---

## 1. API findings (read from the crate/npm source before editing)

### 1.1 Versions

| Item | Value | Source |
|---|---|---|
| Newest 0.18.x on crates.io | **`ephemeral-rollups-sdk 0.18.0`** (no 0.18.1+; latest overall is 0.20.0) | `cargo info ephemeral-rollups-sdk@0.18` |
| It pulls | `ephemeral-vrf-sdk 0.18.0` (+ `ephemeral-vrf-sdk-vrf-macro 0.18.0`), `ephemeral-rollups-sdk-attribute-{action,commit,delegate,ephemeral,ephemeral-accounts} 0.18.0`, `magicblock-delegation-program-api 3.1.0`, **`magicblock-magic-program-api 0.10.1`** (same as 0.16.2); `magicblock-magic-program-api 2.0.0` only behind the `magic-accounts` feature; `hydra-api 0.2.1` only behind the new `crank` feature | `ephemeral-rollups-sdk-0.18.0/Cargo.toml` |
| anchor-lang | still `=1.0.2` (feature `anchor` = `anchor-modern`) | same |
| npm `@magicblock-labs/ephemeral-rollups-sdk` | **0.18.0** = dist-tag `legacy` (`latest` = 0.20.0) | `npm view … dist-tags` |
| npm 0.18.0 peer | `@solana/web3.js ^1.99.0` (we pin 1.98.4 → pnpm peer warning only, see §6) | `npm view …@0.18.0 peerDependencies` |
| session-keys | 3.1.1 resolves next to SDK 0.18.0 + anchor-lang 1.0.2 with **one** `anchor-lang` (1.0.2). `solana-program` appears as 2.3.0 (pulled by the SDK itself) **and** 3.0.0 (anchor) — the same pair already in today's 0.16.2 `Cargo.lock`, so nothing new | scratch crate `cargo tree -i anchor-lang` / `-i solana-program@2.3.0` |

Paths below are relative to `~/.cargo/registry/src/index.crates.io-*/`.

### 1.2 VRF (`ephemeral-vrf-sdk-0.18.0`)

- **`create_request_scoped_randomness_ix` is gone** (it existed in vrf-sdk 0.4.1, the one SDK 0.16.2 pulls).
- Replacement: `instructions::create_request_randomness_ix_const(params: RequestRandomnessParams, program_identity: Pubkey)` — `src/instructions.rs:83`. Sets `data[0] = 10` (scoped, regular priority) — **the same discriminator byte** the old scoped fn set. Accounts: `[payer (w,s), program_identity (r,s), oracle_queue (w), system_program, slot_hashes]` — same order as 0.4.1.
- `create_request_randomness_ix(params)` still exists but is `#[deprecated]` (derives the identity at runtime). `create_request_high_priority_scoped_randomness_ix_const` = `data[0] = 11`.
- `RequestRandomnessParams` (`src/instructions.rs:7`) unchanged: `payer, oracle_queue, callback_program_id, callback_discriminator, accounts_metas, caller_seed, callback_args` (`Default`).
- Identity helpers (`src/consts.rs`): `program_identity_pubkey(&ID)` / `program_identity_pda(&ID)` = `PDA(["identity"], our program)` (the request signer, const-derived). Callback signer: `scoped_vrf_identity_const(&ID)` = `PDA(["identity", our program], VRF program)` — the same PDA 0.4.1 computed at runtime with `scoped_vrf_identity`.
- Constants unchanged: `VRF_PROGRAM_ID Vrf1RN…`, `DEFAULT_QUEUE Cuj97…`, `DEFAULT_EPHEMERAL_QUEUE 5hBR57…`, `DEFAULT_EPHEMERAL_TEST_QUEUE Sc9MJU…`, `DEFAULT_TEST_QUEUE GKE6d7…` (`src/consts.rs`). The rollups SDK re-exports all of it as `ephemeral_rollups_sdk::vrf::*` (`ephemeral-rollups-sdk-0.18.0/src/vrf.rs`).
- **`#[vrf]`** (`ephemeral-vrf-sdk-vrf-macro-0.18.0/src/lib.rs`): adds `program_identity` (`seeds=[b"identity"], bump`), `vrf_program`, `slot_hashes`, `system_program` if missing; **now a compile error if there is no `oracle_queue` field** (we have one). `invoke_signed_vrf(payer, ix)` now uses the const bump, **forces `data[0]` to 10 or 11** (3/11 → 11, else 10) and passes `system_program` in the account infos (0.4.1 didn't).
- **`#[vrf_callback]`**: inserts `vrf_program_identity: Signer` with `address = scoped_vrf_identity_const(&crate::ID)` — semantics identical to 0.4.1, so the "direct callback call fails" test must still hold.

### 1.3 Delegation (`ephemeral-rollups-sdk-attribute-delegate-0.18.0`, `ephemeral-rollups-sdk-0.18.0/src/cpi.rs`)

- `#[delegate]` + field marker `del` → `delegate_<field>(payer, seeds, DelegateConfig)`. Two changes:
  1. The `del` marker is now matched as a **whole constraint**, not a substring — the old "strips every `del` substring" trap (our NOTE in `lib.rs`) is fixed.
  2. A `del` field **must be `UncheckedAccount`/`AccountInfo`** (compile error otherwise; SIMD-0460 direct mapping). Ours already is.
- `DelegateConfig { commit_frequency_ms: u32, validator: Option<Pubkey> }` + `Default` — unchanged (`src/cpi.rs:41`).

### 1.4 Commits / intent bundles (`ephemeral-rollups-sdk-0.18.0/src/ephem/`)

- **Our program already uses `MagicIntentBundleBuilder`** (moved in M0), so no port is needed. The builder code is byte-identical to 0.16.2 apart from new Magic-ATA helpers in `ephem/mod.rs` (`create_magic_ata_ix`, `close_magic_ata_ix`, `is_magic_ata_token_account`; validator-v2 only).
- `MagicIntentBundleBuilder::new(payer, magic_context, magic_program)` (`mod.rs:198`), `.magic_fee_vault(vault)` (`:214`), `.commit(&[..]) -> CommitIntentBuilder` (`:223`), `.commit_and_undelegate(&[..]) -> CommitAndUndelegateIntentBuilder` (`:238`), `.add_standalone_action(s)` (`:253/:266`), `.build()` / `.build_and_invoke()` / `.build_and_invoke_signed(seeds)` (`:326/:380/:386`).
- `CommitIntentBuilder::add_post_commit_action(s)` (`commit_intent_builder.rs:21/:33`); `CommitAndUndelegateIntentBuilder::add_post_commit_action(s)` + `add_post_undelegate_action(s)` (`cau_intent_builder.rs:24-73`). Terminal calls on sub-builders come from `trait FoldableIntentBuilder` (`mod.rs:578`) — hence the `FoldableIntentBuilder` import.
- Old helpers `commit_accounts` / `commit_and_undelegate_accounts` / `create_schedule_commit_ix` live in `ephem/deprecated/v0.rs`, the v1 builder in `deprecated/v1.rs` (`#[deprecated(since = "0.7.0")]`).
- `#[commit]` (attribute-commit 0.18.0, **no source change** vs 0.16.2): adds `magic_program: Program<MagicProgram>` and `magic_context` (`address = MAGIC_CONTEXT_ID`, mut).

### 1.5 `#[action]` and `#[ephemeral]` (no source change vs 0.16.2)

- `#[action]` (attribute-action 0.18.0): adds `escrow_auth: UncheckedAccount` and `escrow: UncheckedAccount` if missing. The handler must check them itself (magicblock-examples `ebfb9b8` / `e137826` "validate Magic Action handlers"). Used in P4.
- `#[ephemeral]` (attribute-ephemeral 0.18.0): injects `process_undelegation(ctx, account_seeds)` + `InitializeAfterUndelegation` (with the `undelegate-buffer` PDA check).

### 1.6 Task scheduling ("crank")

- `ephemeral_rollups_sdk::crank` is **now behind a `crank` cargo feature** (0.16.2: always on). It holds `legacy::ScheduleCrankCpi { payer, magic_program, instruction_accounts, args: ScheduleTaskArgs }` — `#[deprecated]` in favour of `crank::hydra::ephemeral::create::CreateCrankCpi` (hydra-api 0.2.1, the new crank program).
- `ScheduleTaskArgs { task_id: i64, execution_interval_millis: i64, iterations: i64, instructions: Vec<Instruction> }` — `magicblock-magic-program-api-0.10.1/src/args.rs:173`, sent as `MagicBlockInstruction::ScheduleTask`. Whether hydra cranks run on validator 1.1.1 (devnet-as) is **UNVERIFIED** → P0.2 must test the legacy `ScheduleTask` path first.
- We don't use cranks yet, so the `crank` feature stays off in PB0.

### 1.7 TS SDK 0.18.0 (npm tarball)

- Export surface vs 0.16.2: same `.d.ts` files plus `instructions/magic-program/magicAta`. `GetCommitmentSignature(sig, erConnection)` keeps its signature (`lib/utils.d.ts:2`); its body now retries the lookup (30 s), errors if the scheduling tx failed, and has a v2 "outbox" fallback. **No renamed exports** for what we use.

### 1.8 magicblock-examples

- HEAD `3376663` (2026-11-18, "run every example on MagicSVM 0.3.0") still pins **0.16.2** in `roll-dice/anchor/programs/roll-dice-delegated`, `rewards-delegated-vrf/anchor/…`, `session-keys/anchor/…` (+ session-keys 3.1.1). `crank-counter/anchor` pins an SDK **git rev `0fc4604`**. **No examples commit has moved to 0.18 yet**, so there is no reference program for 0.18; we follow the crate source above. Their CI is now MagicSVM-only (`.github/workflows/test-magicsvm.yml`), no `ephemeral-validator` version to copy.
- npm `@magicblock-labs/ephemeral-validator` versions: … `0.13.20`, `0.14.10`, **`1.1.1`** (only 1.1.x release), `2.0.0…2.0.3` (latest). Installed locally: 0.14.10.

### 1.9 What has to change in our code

1. `request_deal`: `create_request_scoped_randomness_ix(p)` → `create_request_randomness_ix_const(p, program_identity_pubkey(&crate::ID))`. Same params, same `data[0] = 10`, same accounts.
2. `Cargo.toml` pin + `Cargo.lock`. Nothing else in Rust (commits already on the intent builder).
3. `programs/package.json`: TS SDK `0.16.2` → `0.18.0`. No source edits needed in `tests/lifecycle.ts` / `scripts/devnet-lifecycle.ts`.

---

## 2. The build

| Item | Value |
|---|---|
| Command | `heavy-lock.sh pb0-build -- bash -c 'cd programs && anchor build -- --features localnet'` |
| Toolchain | anchor-cli 1.0.2, solana-cli 3.1.10 (platform-tools as M0) |
| Time | **430 s** cold (fresh worktree `target/`, incl. the IDL test build); incremental rebuild inside `m0:test:local` well under a minute (whole run 56 s) |
| Warnings | **0** (`.pb0-logs/build-localnet.log`, `.m0-logs/build.log`) |
| `.so` size | **481,128 B** final (M0/0.16.2: ~482 KB). The first 0.18 build was 511,424 B — it carried the runtime PDA derivation of the bug below |
| `Cargo.lock` | only the SDK crates moved (`ephemeral-rollups-sdk*` 0.16.2 → 0.18.0, `ephemeral-vrf-sdk*` 0.4.1 → 0.18.0); the Anchor 1.0.2 macro-crate pins from M0 (doc 07 §9 #1) are kept |
| Program keypair | the worktree had no `target/deploy/fall_rush-keypair.json` (gitignored); copied from the main checkout so the ID stays `4yehff…` (never committed) |

### Code changes (`programs/fall_rush/src/lib.rs`)

1. `request_deal`: `create_request_scoped_randomness_ix(p)` → `create_request_randomness_ix_const(p, PROGRAM_IDENTITY)` with `pub const PROGRAM_IDENTITY = program_identity_pubkey(&crate::ID)`. Caller seed, `accounts_metas`, callback discriminator and `data[0] = 10` unchanged.
2. **SDK 0.18.0 bug workaround** in `DealCallback`: the `#[vrf_callback]`-injected constraint `address = Pubkey::new_from_array(scoped_vrf_identity_const(&crate::ID).0)` is a `const fn` called in a **runtime** expression, so the ed25519 off-curve check runs on-chain. Measured with `scripts/pb0-probe.sh` on the local ER: `deal_callback` → **200,000 / 200,000 CU, `ProgramFailedToComplete`** ("exceeded CUs meter"), so neither the attacker test nor the oracle's real callback could reach any check (first run: 4/10 lifecycle). Fix: declare `vrf_program_identity: Signer` ourselves (the macro then skips injection) with `address = VRF_CALLBACK_IDENTITY`, a crate-level `const`. Same name, same position (first field), same PDA → IDL account list unchanged.
3. New unit test `identity_tests::const_vrf_identities_match_find_program_address` (2/2 lib tests green).
4. Commits: no change (already `MagicIntentBundleBuilder` since M0). No account layout, seed, event, deal, transcript or instruction change.

TS: `@magicblock-labs/ephemeral-rollups-sdk` 0.16.2 → **0.18.0**; `@solana/web3.js` 1.98.4 → **1.99.0** (SDK 0.18 depends on `^1.99.0`; without the bump pnpm installs a second web3.js copy for the SDK). No source edits in `tests/lifecycle.ts` / `scripts/devnet-lifecycle.ts` (no renamed exports).

## 3. Local lifecycle (`pnpm m0:test:local`, 2026-11-21 ~08:30 UTC)

Local stack unchanged: **`@magicblock-labs/ephemeral-validator@0.14.10`** (`mb-stack`) + 2 `vrf-oracle`. No 1.1.x / 2.x local validator was needed.

| Run | Result |
|---|---|
| #1 (before the callback fix) | parity Rust 5/5, TS 6/6; lifecycle **4/10** — `deal_callback` direct call: no error code (`ProgramFailedToComplete`); VRF callback never observed in 30 s; rounds/finalize cascade-failed on status `Dealing` |
| #2 (final, 56 s total) | parity **Rust 5/5, TS 6/6**; lifecycle **10/10** |

Run #2 timings (local, from the test's own log):

| Step | ms |
|---|---|
| delegate (Solana) | 424 |
| request_deal ER confirm | 65 |
| VRF callback observed | 115 |
| complete_round ER confirm | 11 |
| commit landed on Solana | 8,552 |
| `GetCommitmentSignature` | 29,937 (the known local websocket stall, doc 07 §9 #8 — still there with TS SDK 0.18) |
| undelegate landed on Solana | 500 |
| ER send→confirm, 50 txs | p50 8, p95 49, max 65 |

The direct-callback attack fails again with Anchor `ConstraintAddress` (2012) as in M0.

## 4. Devnet-as — **NOT RUN (blocked on funding)**

`solana balance -u devnet` = **0.853195385 SOL** (wallet `71Wd3S…`, 2026-11-21). The program upgrade needs ~3.4 SOL of buffer; the prompt's floor is 4 SOL. Per the rules, no deploy was attempted. To finish D once funded (faucet.solana.com):

```bash
cd ../FallRush-pb0
solana balance -u devnet                      # ≥ 4 SOL
bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh pb0-devnet-deploy -- pnpm m0:devnet:deploy   # anchor build (devnet queue) + upgrade 4yehff…
solana program close --buffers -u devnet      # only if a buffer is left over
bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh pb0-devnet -- pnpm m0:devnet
```

Devnet program on chain is still the **0.16.2 build** from M0.

## 5. `rush-core` skeleton

**Moved to P1** (prompt edit 2026-11-21: "Skip it in PB0; P1 owns `crates/**`"). Nothing under `crates/` in this branch, so commit 3 (`feat(pb0): rush-core skeleton + fx base`) does not exist.

## 6. UNVERIFIED

- **Devnet-as with SDK 0.18** (deploy, delegate, VRF in the rollup, commits, undelegate): not run, funding (§4).
- Whether devnet-as (validator 1.1.1) needs the same callback workaround: the CU burn is in our program, not the validator, so it should fail the same way without it — not measured on devnet.
- Real `deal_callback` CU after the fix (only "fulfilled in 115 ms" is measured).
- Hydra crank (`crank::hydra`) vs legacy `ScheduleTask` on validator 1.1.1 (P0.2).
- TS SDK 0.18 `GetCommitmentSignature` on devnet (local: still the 30 s websocket stall).
- pnpm peer warning `ws → utf-8-validate ^5` (found 6.0.6) — pre-existing, harmless so far.

## Notes on process

- Running `mb-stack --version` (and `--help`) **starts the stack**. I did that once outside the lock at ~07:45 UTC while G4.13's WebGL measurement was running; it exited at once (port clash) and left nothing running, but it may have cost that measurement a second of CPU.
- `m0-test-local.sh` refuses to start if `pgrep -f` finds `mb-stack`/`vrf-oracle`/… anywhere — including in an unrelated shell's command line that only mentions those words (hit twice by my own probe commands). Run the lock command on its own line.

---

## For main (merge later)

**`PROJECT_STATUS.md`** (add under the on-chain plan / PB0):
- PB0 (branch `pb0`): ER SDK **0.18.0** (Rust + TS) landed; `@solana/web3.js` 1.99.0. Local lifecycle **10/10** + deal parity 5/5 + 6/6 on ephemeral-validator 0.14.10. **Devnet-as re-run pending funding** (0.853 SOL, needs ≥ 4). Proof `docs/proof/PB0.md`.
- SDK 0.18.0 `#[vrf_callback]` burns 200k CU (runtime PDA) → we declare the identity field with a const. Any new VRF callback (P2/P4 capsules) must do the same.
- `rush-core` skeleton moved to P1.

**doc 15 row** (decisions): `OC-4 | ER SDK 0.18.x on Anchor 1.0.2 and devnet-as; not 0.20 | ✅ local (PB0: 0.18.0, lifecycle 10/10 local, parity 5/5 + 6/6, docs/proof/PB0.md); devnet re-run pending funding`. Optionally a risk row: `SDK macro regressions (0.18.0 vrf_callback CU burn) — mitigated by local lifecycle on every SDK bump`.

**For P0 / P1:**
- Build against `ephemeral-rollups-sdk =0.18.0`; enable the `crank` feature for task scheduling (it is no longer on by default). `ScheduleCrankCpi` is deprecated in favour of `crank::hydra::…::CreateCrankCpi` — test which one devnet-as 1.1.1 runs.
- Any `const fn` PDA helper from the SDK (`scoped_vrf_identity_const`, `program_identity_pda`, `const_crypto::ed25519::derive_program_address`) must be used in a `const` item, never in a constraint or handler expression: at runtime it costs > 200k CU.
- The local 0.14.10 stack works with 0.18 programs; no validator change needed. `mb-stack --version/--help` starts the stack — keep it under the heavy lock.
- `programs/tests/pb0-probe.ts` + `scripts/pb0-probe.sh` simulate ER instructions on a delegated match and print logs + CU; handy for P0.1-style CU reads on the local rollup.
