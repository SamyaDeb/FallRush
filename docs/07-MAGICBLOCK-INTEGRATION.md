# 07 — MagicBlock Integration (ER + VRF + Session Keys)

**Status:** v3 (2026-11-21).

**Changelog:**
- v3 (2026-11-21, PB0): ER SDK **0.16.2 → 0.18.0** (Rust + TS), `@solana/web3.js` 1.98.4 → 1.99.0 (dedupes the SDK's copy). VRF request → `create_request_randomness_ix_const` + `program_identity_pubkey(&ID)`. Commits were already on `MagicIntentBundleBuilder`. Evidence: `docs/proof/PB0.md`.
- v2 (2026-11-18, M0): verified against SDK 0.16.2 + local/devnet runs (§9).

**How this was checked:** every API name, ID and version below was read from these sources on 2026-11-18:
- the **published `ephemeral-rollups-sdk` 0.16.2 crate source** (v3: re-read against **0.18.0**, see `docs/proof/PB0.md` §1);
- `magicblock-labs/magicblock-examples` at commit `101e8fd` (2026-11-13);
- MagicBlock docs.

Anything not checked is marked **UNVERIFIED**.

**Rule:** if the SDK and this doc disagree, the SDK source wins. Fix this doc in the same PR.

---

## 1. Pinned toolchain (ADR-008)

This is MagicBlock's own example tuple (their CI: `.github/workflows/test-examples.yml` in magicblock-examples).

| Component | Version | Note |
|---|---|---|
| anchor-cli / anchor-lang | **1.0.2** | `avm install 1.0.2 && avm use 1.0.2`. The local machine has 1.2.1; switch per project via `[toolchain] anchor_version = "1.0.2"` |
| ephemeral-rollups-sdk (Rust) | **0.18.0**, features `["anchor","vrf"]` (was 0.16.2 until PB0) | Pulls `ephemeral-vrf-sdk 0.18.0`, `magicblock-delegation-program-api 3.1.0`, `magicblock-magic-program-api 0.10.1`. Task scheduling (`crank`) is now an opt-in cargo feature. magicblock-examples (HEAD `3376663`) are still on 0.16.2 |
| session-keys (Rust) | **3.1.1**, features `["no-entrypoint"]` | Same as the official `session-keys` example |
| Solana CLI | 3.1.9 in CI (local 3.1.10, expected fine; UNVERIFIED) | |
| Rust | 1.89.0 in CI (local 1.96.1; pin via `rust-toolchain.toml` if builds fail) | |
| @coral-xyz/anchor (TS) | **0.32.1** | Every example uses this with Anchor 1.0.2 programs |
| @magicblock-labs/ephemeral-rollups-sdk (TS) | **0.18.0** (npm dist-tag `legacy`) | Matches the crate. Same exports we use as 0.16.2 |
| @solana/web3.js | **1.99.0** (exact) | TS SDK 0.18.0 depends on `^1.99.0`; pinning 1.99.0 keeps a single copy | |
| @magicblock-labs/gum-sdk (session keys TS) | ^3.0.10 → pin the exact version | |
| Local ER stack | `@magicblock-labs/ephemeral-validator@0.14.10` | CI note: 1.1.1 is broken for commits |

**Not adopted:**
- SDK 0.20.x (needs Anchor ≥ 1.2.2; examples have not migrated). Revisit after the hackathon.
- BOLT (Anchor 0.x, solana-program 2).
- SOAR (stale).

## 2. Program IDs & endpoints (VERIFIED)

| Name | Value |
|---|---|
| Delegation program | `DELeGGvXpWV2fqJUhqcF5ZSYMS4JTLjteaAMARRSaeSh` |
| Magic program | `Magic11111111111111111111111111111111111111` |
| Magic context | `MagicContext1111111111111111111111111111111` |
| VRF program | `Vrf1RNUjXmQGjmQrQLvJHs9SNkvDJEsRVFPkfSQUwGz` |
| VRF queue: base | `Cuj97ggrhhidhbu39TijNVqE74xvKJ69gDervRUXAxGh` |
| VRF queue: ER (**we use this one**) | `5hBR571xnXppuCPveTrctfTU7tJLSN94nq7kv7FRK5Tc` |
| VRF queue: local base | `GKE6d7iv8kCBrsxr78W3xVdjGLLLJnxsGiuzrsZCGEvb` |
| VRF queue: local ER | `Sc9MJUngNbQXSXGP3F67KvKwVnhaYn6kcioxXNVowYT` |
| Session keys program | `KeyspM2ssCJbqUhQ4k7sveSiY4WjnYsrXkC8oDbwde5` |
| Metaplex Core | `CoREENxT6tW1HoK8ypY1SxRMZTcVPm7R94rH4PZNhX7d` |

**Devnet ER validators.** The endpoint you send to **must match** the validator you delegated to.

| Region | Endpoint | Validator identity |
|---|---|---|
| Asia (examples default) | `https://devnet-as.magicblock.app` | `MAS1Dt9qreoRMQ14YQuhg8UTZMMzDdKhmkZMECCzk57` |
| EU | `https://devnet-eu.magicblock.app` | `MEUGGrYPxKk17hCr7wpT6s8dtNokZj5U2L57vjYMS8e` |
| US | `https://devnet-us.magicblock.app` | `MUS3hc9TCw4cGC12vHNoYcCGzJG1txjgQLZWVoeNHNd` |
| Local | `http://localhost:7799` | `mAGicPQYBMvcYveUZA5F5UNNwyHvfYh5xkLS2Fr1mev` |
| Router | `https://devnet-router.magicblock.app` | routes by account owner |

**Validator choice — default `devnet-as` (`MAS1…`):**
- Most examples target it.
- It reported core 1.1.1 on 2026-11-18, which matches the 0.16.x / 0.18.x line.
- `devnet-us` reported core 2.0.0, which is for the 0.20 line. **Do not use US with 0.16.2 / 0.18.0** until proven.
- The M0 spike confirms the choice.

## 3. Account lifecycle for one match

```
SOLANA (base)                                        EPHEMERAL ROLLUP (devnet-as)
1 create_match ──► Match PDA (owner: fall_rush)
2 delegate_match ─► owner: Delegation Program ─────► Match available & writable on ER
                                                    3 request_deal (VRF, ER queue)
                                                    4 deal_callback  ◄── VRF oracle (scoped identity signer)
                                                    5 start_round / record_qualified / record_eliminated …
                                                    6 submit_pick (session-key signer)
                                                    7 complete_round(r) + commit ───┐
8 Match on Solana updated (still delegated) ◄──────────────────────────────────────┘ (repeat per round, ≤4)
                                                    9 complete_match + commit_and_undelegate ─┐
10 Match owner: fall_rush again; final receipt ◄─────────────────────────────────────────────┘
```

**Rules:**
- Accounts must exist on Solana **before** delegation.
- While delegated, the account is **locked on Solana**. All writes go to the ER.
- Commits after the first cost 0.001 SOL each, and without a delegated fee payer **only 10 commits are allowed**. We use **at most 5** per match: 4 round commits and 1 commit + undelegate. (The fee figures were checked on v1 validators.)

## 4. Rust patterns (from SDK 0.18.0 source; M0 used 0.16.2 and the examples)

### 4.1 Program skeleton

```rust
use ephemeral_rollups_sdk::anchor::{commit, delegate, ephemeral, vrf, vrf_callback};
use ephemeral_rollups_sdk::cpi::DelegateConfig;
use ephemeral_rollups_sdk::ephem::MagicIntentBundleBuilder;
use ephemeral_rollups_sdk::vrf; // vrf::instructions, vrf::types, vrf::consts, vrf::rnd

#[ephemeral]            // injects process_undelegation handler
#[program]
pub mod fall_rush { /* … */ }
```

### 4.2 Delegate (base layer)

```rust
#[delegate]
#[derive(Accounts)]
pub struct DelegateMatch<'info> {
    #[account(mut, address = config.game_authority)] pub authority: Signer<'info>,
    #[account(seeds = [CONFIG_SEED], bump = config.bump)] pub config: Account<'info, GameConfig>,
    /// CHECK: delegated PDA, seeds verified by `del` + seeds constraint
    #[account(mut, del, seeds = [MATCH_SEED, &match_id], bump)] pub match_account: UncheckedAccount<'info>,
}
// handler:
ctx.accounts.delegate_match_account(
    &ctx.accounts.authority,
    &[MATCH_SEED, &match_id],
    DelegateConfig { validator: ctx.remaining_accounts.first().map(|a| a.key()), ..Default::default() },
)?;
```

The macro adds:
- `buffer_*`, `delegation_record_*`, `delegation_metadata_*`
- `owner_program`, `delegation_program`, `system_program`

It generates `delegate_<field>(payer, seeds, config)`.

**Resolved (M0):** the `del` field stays an `UncheckedAccount`; the handler deserializes `Match` manually to check owner + status first. SDK 0.18 makes a typed `del` field a compile error (SIMD-0460 direct mapping), and matches the `del` marker as a whole constraint (the 0.16 substring-stripping trap in §9 #4 is gone).

### 4.3 VRF request inside the ER

```rust
#[vrf]
#[derive(Accounts)]
pub struct RequestDeal<'info> {
    #[account(mut, address = config.game_authority)] pub authority: Signer<'info>,
    pub config: Account<'info, GameConfig>,
    #[account(mut, seeds=[MATCH_SEED,&match_account.match_id], bump=match_account.bump)]
    pub match_account: Account<'info, Match>,
    /// CHECK: pinned queue
    #[account(mut, address = vrf::consts::DEFAULT_EPHEMERAL_QUEUE)] pub oracle_queue: UncheckedAccount<'info>,
}
// once, at crate level (compile-time PDA(["identity"], crate::ID)):
pub const PROGRAM_IDENTITY: Pubkey = vrf::consts::program_identity_pubkey(&crate::ID);
// handler (SDK 0.18: `create_request_scoped_randomness_ix` was removed in 0.17):
let ix = vrf::instructions::create_request_randomness_ix_const(vrf::types::RequestRandomnessParams {
    payer: ctx.accounts.authority.key(),
    oracle_queue: ctx.accounts.oracle_queue.key(),
    callback_program_id: crate::ID,
    callback_discriminator: crate::instruction::DealCallback::DISCRIMINATOR.to_vec(),
    caller_seed: match_caller_seed,       // H("fallrush-deal" ‖ match_id ‖ ruleset_hash ‖ map_pool_hash)
    accounts_metas: Some(vec![vrf::types::SerializableAccountMeta {
        pubkey: ctx.accounts.match_account.key(), is_signer: false, is_writable: true }]),
    ..Default::default()
}, PROGRAM_IDENTITY);   // data[0] = 10 = scoped, regular priority (same byte as the old fn)
ctx.accounts.invoke_signed_vrf(&ctx.accounts.authority.to_account_info(), &ix)?;
```

**Notes:**
- `#[vrf]` adds `program_identity` (`[b"identity"]` PDA), `vrf_program`, `slot_hashes` and `system_program`.
- For **localnet**, the queue constraint must be switched to `DEFAULT_EPHEMERAL_TEST_QUEUE` by a cargo feature (`localnet`).
- `create_request_high_priority_scoped_randomness_ix_const` (`data[0] = 11`) also exists. Use it if devnet fulfilment is slow. The non-`_const` variants are `#[deprecated]` in 0.18 (runtime `find_program_address`).
- 0.18's `#[vrf]` is a compile error without an `oracle_queue` field, and `invoke_signed_vrf` now also passes `system_program`.

### 4.4 VRF callback

```rust
#[vrf_callback]           // inserts vrf_program_identity: Signer with address = scoped_vrf_identity_const(&crate::ID)
                          // = PDA(["identity", crate::ID], VRF program); same PDA as 0.16's runtime version
#[derive(Accounts)]
pub struct DealCallback<'info> {
    #[account(mut)] pub match_account: Account<'info, Match>,
}
pub fn deal_callback(ctx: Context<DealCallback>, randomness: [u8; 32]) -> Result<()> { /* derive deal */ }
```

**Never use the deprecated global `VRF_PROGRAM_IDENTITY` check.** It lets anyone call the callback.

**SDK 0.18.0 bug — declare the identity field yourself.** The injected constraint is `address = Pubkey::new_from_array(scoped_vrf_identity_const(&crate::ID).0)` — a `const fn`, but called in a runtime expression, so the ed25519 off-curve check runs on-chain: `deal_callback` used **200,000 / 200,000 CU** and failed with `ProgramFailedToComplete` (measured on the local ER, PB0 §3). The macro skips injection when a field named `vrf_program_identity` exists, so:

```rust
pub const VRF_CALLBACK_IDENTITY: Pubkey =
    Pubkey::new_from_array(vrf::consts::scoped_vrf_identity_const(&crate::ID).0);

#[vrf_callback]
#[derive(Accounts)]
pub struct DealCallback<'info> {
    #[account(address = VRF_CALLBACK_IDENTITY)]   // first field, same name/position as the injected one
    pub vrf_program_identity: Signer<'info>,
    #[account(mut)] pub match_account: Account<'info, Match>,
}
```

### 4.5 Commit (per round) & commit + undelegate (final)

```rust
#[commit]  // appends magic_program, magic_context
#[derive(Accounts)]
pub struct CompleteRound<'info> {
    #[account(mut, address = config.game_authority)] pub authority: Signer<'info>,
    pub config: Account<'info, GameConfig>,
    #[account(mut, seeds=[MATCH_SEED,&match_account.match_id], bump=match_account.bump)]
    pub match_account: Account<'info, Match>,
}
// after mutating state:
ctx.accounts.match_account.exit(&crate::ID)?;
MagicIntentBundleBuilder::new(
    ctx.accounts.authority.to_account_info(),
    ctx.accounts.magic_context.to_account_info(),
    ctx.accounts.magic_program.to_account_info())
  .commit(&[ctx.accounts.match_account.to_account_info()])          // or .commit_and_undelegate(&[…])
  .build_and_invoke()?;
```

`build_and_invoke` on chained builders needs `use ephemeral_rollups_sdk::ephem::FoldableIntentBuilder;`.

Builder API (0.18.0 `src/ephem/`, unchanged from 0.16.2): `.magic_fee_vault(vault)` (delegated fee payer, lifts the 10-commit limit), `.commit(..).add_post_commit_action(s)(..)`, `.commit_and_undelegate(..).add_post_commit_action(s)/add_post_undelegate_action(s)(..)`, `.add_standalone_action(s)`, `.build()` / `.build_and_invoke()` / `.build_and_invoke_signed(seeds)`. Follow-up handlers on Solana use `#[action]` (adds `escrow_auth` + `escrow`; the handler must validate them). The old `commit_accounts` / `commit_and_undelegate_accounts` helpers are in `ephem::deprecated`.

### 4.5b Timed tasks ("crank", P0.2 / P2.3)

0.18 moves `ephemeral_rollups_sdk::crank` behind the **`crank` cargo feature** (not enabled yet). `crank::ScheduleCrankCpi { payer, magic_program, instruction_accounts, args: ScheduleTaskArgs { task_id, execution_interval_millis, iterations, instructions } }` sends `MagicBlockInstruction::ScheduleTask` (magic-program-api 0.10.1); it is `#[deprecated]` in favour of `crank::hydra::ephemeral::create::CreateCrankCpi` (hydra-api 0.2.1). Which one validator 1.1.1 runs is **UNVERIFIED** (P0.2).

### 4.6 Session keys (Fan Picks)

```rust
use session_keys::{session_auth_or, Session, SessionError, SessionTokenV2};

#[derive(Accounts, Session)]
pub struct SubmitPick<'info> {
    #[account(mut)] pub signer: Signer<'info>,                 // wallet or session signer
    /// CHECK: the picker's wallet (authority of the session)
    pub picker: UncheckedAccount<'info>,
    #[account(mut, seeds=[MATCH_SEED,&match_account.match_id], bump=match_account.bump)]
    pub match_account: Account<'info, Match>,
    #[session(signer = signer, authority = picker.key())]
    pub session_token: Option<Account<'info, SessionTokenV2>>,
}
#[session_auth_or(ctx.accounts.picker.key() == ctx.accounts.signer.key(), SessionError::InvalidToken)]
pub fn submit_pick(ctx: Context<SubmitPick>, round: u8, slot: u8) -> Result<()> { … }
```

- **Client** (web): `new SessionTokenManager(wallet, connection).program.methods.createSessionV2(topUp, validUntil, topUpLamports)`, with `targetProgram = fall_rush`.
- **The session token lives on Solana.** The ER reads it. The official example combines `Session` with `#[commit]` on the ER, so the pattern is proven.
- UNVERIFIED for our program until M6 testing.

## 5. TypeScript client patterns (Rush Relay chain worker & web)

- **Two providers:**
  - base: `new Connection(SOLANA_RPC_URL)`;
  - ER: `new Connection(ER_RPC_URL, { wsEndpoint: ER_WS_URL })` with `new Program(idl, erProvider)`.
- **Delegate:** sent to the base provider, with the validator identity passed as `remainingAccounts[0]` (`{pubkey: VALIDATOR, isSigner:false, isWritable:false}`).
- **ER instructions:** sent to the ER provider. Once the match is delegated, never send them to base.
- **Waiting for VRF:**
  1. Subscribe `erConnection.onAccountChange(matchPda, …, "processed")` **before** sending `request_deal`.
  2. Resolve when `status == Dealt` (M0 correction: the enum value is `Dealt`; `DealReady` is the event name).
  3. Timeout **20 s**, then retry the request once with the same caller seed (idempotent by status check).
  4. Then go **Unranked** (§6).
- **Commit signature:** after a commit instruction on the ER, get the Solana-side signature via the SDK's `GetCommitmentSignature(erTxSig, erConnection)`. It is used in the `advanced-magic.ts` example; check the exact export name at M0.
- **Live feed (web):** `erConnection.onAccountChange(matchPda)`, then decode with the Anchor coder and render the events diff. No backend is needed for the live view.

## 6. Failure policy

| Failure | Behaviour |
|---|---|
| VRF not fulfilled in 20 s + 1 retry | The server deals from a **published fallback**, `H(match_id ‖ "fallback")`. The match is flagged **Unranked**: no on-chain receipt claims, no Crown Capsule. The UI shows "Unranked — randomness unavailable". |
| ER transaction fails (transient) | Retry with backoff (0.5, 1, 2, 4 s). The `event_seq` check makes retries idempotent: a duplicate returns `InvalidSequence`, which the worker treats as already applied after re-reading the account. |
| ER down mid-match | The game **continues**. Events queue in the outbox and the chain chip shows `Pending`. Results are submitted when the ER recovers. If it hasn't recovered by match end + 10 min → `abort_match` (if reachable) and the match is shown as "Off-chain result". |
| Commit fails | The match stays delegated. The worker retries the commit. The UI never shows "Solana ✓" until the commit signature is confirmed on base. |
| Wrong endpoint | Every ER call asserts that the account owner on base is the Delegation Program and that the configured validator matches the delegation record (bytes 8..40, per the `rewards-delegated-vrf` example). |

## 7. Local development

```bash
npm install -g @magicblock-labs/ephemeral-validator@0.14.10   # provides mb-stack, vrf-oracle, …
RUST_LOG=info mb-stack --reset \
  --upgradeable-program <FALL_RUSH_ID> target/deploy/fall_rush.so <UPGRADE_AUTHORITY>
# wait for "MagicBlock stack is ready"; ports: base 8899/8900, ER 7799/7800, QFS 6699
VRF_ORACLE_SKIP_PREFLIGHT=true RPC_URL=http://localhost:8899 WEBSOCKET_URL=ws://localhost:8900 vrf-oracle &
VRF_ORACLE_SKIP_PREFLIGHT=true RPC_URL=http://localhost:7799 WEBSOCKET_URL=ws://localhost:7800 vrf-oracle &
```

**Environment variables** for tests, matching the examples' `scripts/local-env.sh`:
- `EPHEMERAL_PROVIDER_ENDPOINT=http://localhost:7799`
- `EPHEMERAL_WS_ENDPOINT=ws://localhost:7800`
- `VALIDATOR=mAGicPQYBMvcYveUZA5F5UNNwyHvfYh5xkLS2Fr1mev`
- `VRF_EPHEMERAL_QUEUE=Sc9MJUngNbQXSXGP3F67KvKwVnhaYn6kcioxXNVowYT`

**Memory:** running the stack plus two oracles on 8 GB is fine, but stop it while Unity is open.

## 8. Demo proof

See 18-HACKATHON-STRATEGY §4. The chain worker writes every signature into `chain_txs`. `PROOF.md` is generated from that table by `pnpm proof <matchId>`.

---

## 9. M0 corrections (2026-11-18, verified against SDK 0.16.2 source + local/devnet runs)

| # | Where | Was | Reality (source) |
|---|---|---|---|
| 1 | §1 Anchor | "anchor-lang 1.0.2" was assumed to pull matching macro crates | `anchor-lang =1.0.2` declares `^1.0.2` on its macro crates, so cargo resolved `anchor-derive-accounts` & co. to **1.2.2** → `init` codegen calls `system_program::create_account_allow_prefund`, which 1.0.2 lacks. **Fix:** `cargo update -p <anchor-*> --precise 1.0.2` for all 10 macro/syn crates and `anchor-lang-idl --precise 0.1.2` (same as magicblock-examples' `Cargo.lock`). Committed in `programs/Cargo.lock`. |
| 2 | §4 hashing | doc 06 says `solana_program::hash::hashv` | anchor-lang 1.0.2 does not re-export `hash`. We depend on `solana-sha256-hasher =3.1.0` (already in the tree via `magicblock-delegation-program-api 3.1.0`). |
| 3 | §1 install | `avm install 1.0.2` | The prebuilt download timed out repeatedly (avm 1.1.2 HTTP client; curl reached the same asset). `avm install 1.0.2 --from-source` with rustc 1.96.1 failed at link: Apple `ld` (LLVM 17) cannot read LLVM 22 bitcode. **Worked:** `CARGO_PROFILE_RELEASE_LTO=off avm install 1.0.2 --from-source`. avm then sets 1.0.2 as global default; restore with `avm use <previous>` — the project still gets 1.0.2 via `[toolchain]`. No `rust-toolchain.toml` needed: the program builds with platform-tools v1.52 (solana-cli 3.1.10) and host rustc 1.96.1. |
| 4 | §4.2 `#[delegate]` | — | The macro detects the delegated field by the substring `del` in its `#[account(...)]` and strips **every** `del` from that attribute string. Keep identifiers containing `del` out of it. The `del` field must be `UncheckedAccount`; the payer must be `Signer` + `mut`. |
| 5 | §4.3 `#[vrf]` | — | `invoke_signed_vrf` hard-codes a field named **`oracle_queue`**, and rewrites any request discriminator to the scoped variant (3/11 → 11 high priority, else 10). Constants verified: `DEFAULT_EPHEMERAL_QUEUE = 5hBR…`, `DEFAULT_EPHEMERAL_TEST_QUEUE = Sc9M…`, `DEFAULT_TEST_QUEUE = GKE6…`. |
| 6 | §4.4 callback | — | Calling `deal_callback` directly with a normal signer fails with Anchor **`ConstraintAddress` (2012)** on the injected `vrf_program_identity` (tested). |
| 7 | §4.5 | `FoldableIntentBuilder` import "needed" | `build_and_invoke` on `CommitIntentBuilder` is a `FoldableIntentBuilder` trait method; we import it (the counter example compiles without an explicit import — not re-checked). |
| 8 | §5 confirmations | `rpc()` default confirm | **Local ER 0.14.10 intermittently never delivers the websocket `signatureSubscribe` notification**, although the tx executed in the same second (probe: slot/blockTime = send time). web3.js then waits its full 30 s. **Confirm ER txs by polling `getSignatureStatuses`** (done in tests + devnet script). `GetCommitmentSignature` uses `confirmTransaction` internally and hit the same ~30 s locally while the commit was already visible on Solana after ~6.5 s — don't block UI on it. |
| 9 | §5 commit sig | "check the exact export name" | `GetCommitmentSignature(txSig, erConnection): Promise<string>` is a root export of TS SDK 0.16.2 (`lib/utils.js`); it parses `ScheduledCommitSent signature:` / `signature[0]:` log lines. |
| 10 | §7 local stack | command list | mb-stack also needs the validator fixture account: `--account mAGicPQYBMvcYveUZA5F5UNNwyHvfYh5xkLS2Fr1mev <fixture.json>` (copied to `programs/tests/fixtures/`). mb-stack does **not** forward child logs, and **`mb-stack --help` starts the stack**. The examples' script runs `solana config set --url localhost` (global!) — ours doesn't. |
| 11 | §7 Node 24 | — | ts-mocha 10 + Node 24: Node's native TS type-stripping loads `.ts` as ESM and bypasses ts-node. Run with `NODE_OPTIONS=--no-experimental-strip-types`. |
| 12 | §8 ER explorer | — | MagicBlock docs (docs.magicblock.gg → get-started → how-integrate-your-program → anchor) link ER txs as `https://solscan.io/tx/<sig>?cluster=custom&customUrl=https://devnet.magicblock.app`. Using it with `devnet-as` is **UNVERIFIED**. |
| 13 | §4.4 callback (PB0, SDK 0.18.0) | macro-injected `vrf_program_identity` | Injected check derives the PDA at runtime → 200k CU, `ProgramFailedToComplete`; the VRF oracle's callback fails the same way, so no deal ever lands. Fix: our own field with a `const` address (§4.4). Unit test `identity_tests` checks both const PDAs against `find_program_address`. |
| 14 | §7 local stack (PB0) | — | SDK 0.18.0 programs run on the local `ephemeral-validator 0.14.10` stack unchanged (lifecycle 10/10). `mb-stack --version` also starts the stack (like `--help`). `m0-test-local.sh`'s "already running" guard is a `pgrep -f` on names like `mb-stack`/`vrf-oracle`, so it also trips on any shell whose command line merely contains those words. |

## 10. P2 on-chain simulation (2026-11-22, measured on devnet-as)
- **Timed task:** hand-built `MagicBlockInstruction::ScheduleTask` / `CancelTask` CPI (`magicblock-magic-program-api =0.10.1`,
  `bincode =1.3.3`), the scheduled `tick()` has no signers and every account in `remaining_accounts`; task id
  FNV-1a64(program ‖ world ‖ nonce) & i64::MAX, a new nonce per schedule. **At most one task execution per 10 ms slot**
  (100 / s measured with an 8 ms interval). A failed execution ends the task: `tick()` never returns an error, and the
  keeper re-schedules after 500 ms without progress (0 needed in the 5 Finals).
- **Real-time gate:** tick *n* starts once *n*/30 s of slot clock (`Arena.slot_ms` = 10) passed since the first tick
  (11 ms task interval → 1.02 executions per tick for the Final).
- **sBPF cost traps (P2 0.2):** signed `i64 /` and `%` are ≈ 240–350 CU library calls on sBPF v0 (unsigned 12 CU): the
  program defines `__divdi3 / __moddi3 / __divsi3 / __modsi3` (`programs/fall_rush/src/sdiv.rs`). 4 KB stack frames:
  the 3.8 KB `Scene` and the 2 KB `World` live on the heap; borsh `Match` code overflows a frame at `opt-level = "z"`
  (`fall_rush` is built at `"s"`, rush-core at 3 without overflow checks, dependencies at `"z"`).
- **Read-only clones:** map blobs up to **53.7 KB** (Capsule Run) clone into the rollup read-only (P0 had only shown
  28.8 KB). The session-keys `SessionTokenV2` (on Solana) is read by the rollup; a revoked (closed) token is seen as
  missing on the next transaction (local stack).
- **Commits:** per match 3 `complete_round` (Classic rounds, hybrid) + 1 `commit_round` + `finalize_match` = 5 < 10.
