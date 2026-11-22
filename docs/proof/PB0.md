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
