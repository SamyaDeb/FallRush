# PB0: housekeeping for the on-chain plan (one session, alone; runs in parallel with P0)

You are the Solana/Rust engineer on **Fall Rush**, a Unity 6.3 WebGL 12-player knockout party game for the MagicBlock Solana Blitz V10 hackathon. On 2026-11-21 the user approved moving the whole game onto a MagicBlock Ephemeral Rollup. The plan is **`/Users/samya/Downloads/FallRush/docs/19-ONCHAIN-PLAN.md`**. Read it in full first, especially §2, §3, §5 PB0 and §8.

This session does **PB0**. That means upgrading the MagicBlock SDK, proving the existing match lifecycle still works, and creating the `rush-core` crate skeleton.

**Two other sessions run at the same time:**
- **G4.13** (Unity post-freeze fixes, on `main`);
- **P0** (feasibility spikes, in `../FallRush-p0`).

The parallel rules in §1 are mandatory.

## 0. State at hand-off (2026-11-21)
- **Repo** `/Users/samya/Downloads/FallRush`, branch `main`.
  - G4.13 has committed `8fcd5ca` (A), `5904331` (B) and `2c89cc0` (C), and is still running (WebGL build #5, measurements, soak).
  - `docs/19-ONCHAIN-PLAN.md`, `docs/prompts/PB0-PROMPT.md`, `docs/prompts/P0-PROMPT.md` and `scripts/heavy-lock.sh` are **uncommitted on main**. Read them from the main checkout path; don't copy them.
- **Program** `programs/fall_rush`:
  - Anchor **1.0.2**, `ephemeral-rollups-sdk = "=0.16.2"` (features `anchor`, `vrf`), `solana-sha256-hasher = "=3.1.0"`.
  - The `localnet` cargo feature pins the VRF test queue.
  - Program ID `4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4`, on devnet, upgrade authority = the default wallet `~/.config/solana/id.json`.
  - The `.so` is about 482 KB.
- **Tests:**
  - `pnpm m0:test:local` (`scripts/m0-test-local.sh`): anchor build, deal parity Rust 5/5 + TS 6/6, local `mb-stack` + 2 `vrf-oracle`, lifecycle 10/10, always tears down.
  - `pnpm m0:devnet` (`scripts/devnet-lifecycle.ts`): the devnet-as lifecycle (proof `docs/proof/M0-devnet.md`).
- **Devnet wallet balance:** 0.853 SOL (2026-11-21). A program upgrade needs a buffer of about 3.4 SOL (refunded after). **Before any devnet deploy, check `solana balance`. If it's below 4 SOL, stop and ask the user to fund it** (faucet.solana.com). Never deploy a new program ID.
- **Research findings** (doc 19 §11; check each against the crate source before relying on it):
  - SDK **0.18.0** (2026-11-18) supports validators v1 + v2 and still accepts `anchor-lang` 1.0; npm dist-tag `legacy`.
  - **0.20 is v2-only and needs Anchor ≥ 1.2.2. Don't use it.**
  - `create_request_scoped_randomness_ix` was **removed in 0.17**. The replacement is reported as `create_request_randomness_ix_const(RequestRandomnessParams{..}, program_identity_pubkey(&ID))` with scoped callbacks by default, and the callback signer `PDA(["identity", program_id])`.
  - The commit helpers are deprecated in favour of `MagicIntentBundleBuilder` (`ephemeral_rollups_sdk::ephem`, plus the `FoldableIntentBuilder` import for chained builders).
  - Session keys crate 3.1.1 is the latest.
  - devnet-as runs validator 1.1.1.
  - Local stack today: `@magicblock-labs/ephemeral-validator@0.14.10` / `mb-stack` (the doc 07 CI note says 1.1.1 is broken for commits). It is **UNVERIFIED** whether SDK 0.18 works with the local 0.14.10 validator.

## 1. Rules (parallel-session rules first)
- **Work in your own worktree:** `git worktree add ../FallRush-pb0 -b pb0 main`. Never edit, stage or commit anything in the main checkout `/Users/samya/Downloads/FallRush`; G4.13 owns it.
- **You own** `programs/**`, `docs/07-MAGICBLOCK-INTEGRATION.md`, `docs/proof/PB0.md`, `scripts/pb0-*.sh` and the TS deps of `programs/package.json` (and `packages/shared` only if a TS SDK bump forces it).
  - **Never touch:** `client-unity/**`, `spikes/**`, `tools/load/**`, `docs/proof/P0*` (P0 owns these), `PROJECT_STATUS.md`, `docs/15-*`.
  - Put any status or decision updates for those last two in a section "For main (merge later)" at the end of `docs/proof/PB0.md`.
- **8 GB M2 Air: one heavy job at a time, across all sessions.** Wrap every heavy command in the shared lock:
  `bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh pb0-<what> -- <command>`
  - Heavy means: `anchor build`, `cargo build-sbf`, the first `cargo build` of the program workspace, `pnpm m0:test:local` (local stack on fixed ports 8899/7799/6699; P0 uses the same ports), `pnpm m0:devnet`, Docker.
  - The lock also waits while any Unity / QA / soak / browser-measure job runs.
  - `cargo test` / `cargo clippy` of the small `no_std` `rush-core` crate may run without the lock.
  - Never bypass or delete the lock, and never kill another session's process.
- **No VPS / Hostinger / SSH. Never push, never add a remote.** No Unity at all in this phase.
- No new crates beyond the SDK upgrade and what `rush-core` needs (none in PB0). Pin exact versions (`=x.y.z`).
- Keypairs never go in git (`.gitignore` covers `*keypair*.json`). Never print a secret key.
- A number counts as measured only if you ran it. Anything else is **UNVERIFIED**.
- Commit on branch `pb0` with your own paths: `git commit -- <paths>`. End every commit message with the attribution lines from the system reminder.

## 2. Tasks, in order

### A. Read the 0.18 source; write down the API changes before editing
1. `cargo search ephemeral-rollups-sdk` / crates.io: find the newest **0.18.x** and pin it.
2. Fetch the source (`cargo fetch` in a scratch crate, or read `~/.cargo/registry/src/*/ephemeral-rollups-sdk-0.18.*`).
3. Write down, with file paths, the exact items for:
   - the VRF request function + params, the `#[vrf]` / `#[vrf_callback]` macro outputs, and the queue constants (`DEFAULT_EPHEMERAL_QUEUE`, `DEFAULT_EPHEMERAL_TEST_QUEUE`);
   - delegation (`#[delegate]`, `DelegateConfig`);
   - `#[commit]`, `MagicIntentBundleBuilder` (`commit`, `commit_and_undelegate`, `add_post_commit_actions`, `build_and_invoke`);
   - `#[action]` (the follow-up call handler on Solana, used in P4);
   - `#[ephemeral]`;
   - the task-scheduling API (`ScheduleTask` in `magicblock-magic-program-api`, which version 0.18 pulls).
4. Check the session keys 3.1.1 dependency tree is compatible with 0.18: `cargo tree -i solana-program` / `anchor-lang`. No duplicate major versions that break the build.
5. Compare with `magicblock-labs/magicblock-examples`: is there a commit that moved to 0.18? Read its `roll-dice-delegated` / `rewards-delegated-vrf` / crank / session examples, and note the commit hash.
6. Write the findings into `docs/proof/PB0.md` §1 **before** changing code.

### B. Upgrade the program to 0.18.x
1. `programs/fall_rush/Cargo.toml`: `ephemeral-rollups-sdk = { version = "=0.18.x", features = ["anchor", "vrf"] }`. Keep Anchor `=1.0.2`. Bump `solana-sha256-hasher` only if 0.18 forces it.
2. Port `request_deal` to the new VRF request API.
   - Keep the **caller seed exactly**: `sha256("fallrush-deal" ‖ match_id ‖ ruleset_hash ‖ map_pool_hash)`.
   - Keep `accounts_metas` and the callback discriminator.
   - The callback must still be scoped (only the VRF identity for our program may call it). The test "a direct callback call fails" must still pass.
3. Port `complete_round` / `finalize_match` commits to `MagicIntentBundleBuilder`. Behaviour is unchanged: commit per round, commit + undelegate at finalize.
4. **Don't change** account layouts, seeds, events, the deal derivation, the transcript or instruction semantics. **Deal parity Rust 5/5 + TS 6/6 must stay green with identical vectors.**
5. TS: bump `@magicblock-labs/ephemeral-rollups-sdk` to the same 0.18.x (exact pin) in `programs/package.json`, and fix `tests/lifecycle.ts` + `scripts/devnet-lifecycle.ts` for renamed exports (for example the commit-signature helper).
6. Build: `heavy-lock.sh pb0-build -- bash -c 'cd programs && anchor build -- --features localnet'`. Record the build time and `.so` size.

### C. Prove the lifecycle locally
1. `heavy-lock.sh pb0-local -- pnpm m0:test:local`. Target: **10/10 lifecycle + 5/5 + 6/6**.
2. If the local 0.14.10 validator is incompatible with 0.18:
   - try the newest local `ephemeral-validator` / `mb-stack` on the **1.1.x line** that the 0.18 examples use;
   - write down the exact versions;
   - never use the 2.x line (that's for SDK 0.20).
   - If still broken after 2 hours, stop and report the logs. Don't hack around it.

### D. Prove it on devnet-as (only with ≥ 4 SOL)
1. `solana balance`. If below 4 SOL, **stop and ask the user to fund**.
2. Build without `localnet` (devnet queue): `anchor build`. Then upgrade the existing program ID (`anchor upgrade` / `solana program deploy --program-id …`). Record the signature and the SOL spent. Close any leftover buffer (`solana program close --buffers`) and record the refund.
3. `heavy-lock.sh pb0-devnet -- pnpm m0:devnet`. The full lifecycle on devnet-as: delegate, VRF in the rollup, per-round commit, commit + undelegate, final Solana state == rollup state. Record every signature and the timings (same table as `docs/proof/M0-devnet.md`).

### E. `rush-core` skeleton (`crates/rush-core`): **MOVED TO P1 (2026-11-21). Skip it in PB0; P1 owns `crates/**`.**
1. A standalone crate with its own `[workspace]`, so it doesn't join the Anchor workspace yet (P2 links it as a path dependency).
   - `#![no_std]`, `#![forbid(unsafe_code)]`;
   - crate-level `#![deny(clippy::float_arithmetic)]`;
   - **no floats anywhere** (also add a test that greps `src/` for `f32`/`f64` and fails).
2. Modules from doc 19 §2 as stubs with doc comments only: `fx`, `vec`, `motor`, `collide/{obb,prism,sphere,capsule,sweep,grid}`, `hazards`, `rules`, `bots`, `world`, `mapblob`.
3. Implement and test **just** the base of `fx`:
   - `isqrt_u64` (exact floor, proptest-free unit tests at edge values);
   - a Q15 sin/cos table of 1024 entries, generated by `tools/gen-trig` (a small native binary, floats allowed there only) into `src/fx/trig_table.rs`, with its sha256 recorded in a test;
   - `sin_q15(angle_q16_turns)` / `cos_q15`.
4. `scripts/pb0-rush-core-check.sh`:
   - `cargo test`;
   - `cargo clippy -- -D warnings`;
   - `cargo build --release --target wasm32-unknown-unknown` (installed);
   - a check that it builds for sBPF, via a tiny `crates/rush-core-sbf-check` cdylib that calls `isqrt_u64` and `sin_q15`, built with `cargo build-sbf` under the heavy lock.
5. A golden vector file `crates/rush-core/tests/vectors/fx_v1.bin`: 1,000 inputs → outputs. Same results native and WASM (run the WASM in Node with a 10-line loader, `tools/wasm-run.mjs`). sBPF parity is P0.6's / P1's job, so just note it.

### F. Docs + proof
1. `docs/07-MAGICBLOCK-INTEGRATION.md`: the §1 table to 0.18.x (and any local-validator change), the §4 code samples to the new API names, a changelog line at the top.
2. `docs/proof/PB0.md`:
   - §1 API findings;
   - §2 the build (time, size, warnings);
   - §3 local lifecycle;
   - §4 devnet signatures + timings + SOL spent;
   - §5 `rush-core` skeleton;
   - §6 UNVERIFIED items;
   - "For main (merge later)": the `PROJECT_STATUS.md` lines, a doc 15 row (OC-4 → ✅ with the evidence), and anything P0 / P1 must know.
3. Commits on `pb0`:
   - `chore(pb0): ER SDK 0.18 API notes`
   - `chore(pb0): ER SDK 0.18 upgrade (VRF, intent bundles)`
   - `feat(pb0): rush-core skeleton + fx base`
   - `docs(pb0): proof`

   Never push.

## 3. Report to the user (short, plain words)
- The SDK version you landed on, and what changed in the code.
- Local lifecycle result, devnet result (with signatures), SOL spent.
- `rush-core` skeleton state, and WASM / sBPF build status.
- Anything UNVERIFIED, anything that needs the user (funding, a validator choice).
- The merge note: branch `pb0` is ready to merge into `main` **after G4.13 has committed its closeout**. Ask before merging, and before removing the worktree.
