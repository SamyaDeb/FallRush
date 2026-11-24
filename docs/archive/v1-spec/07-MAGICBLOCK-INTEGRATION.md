# 07 — MagicBlock Integration Guide

## 1. Goals

Demonstrate two meaningful MagicBlock capabilities:
1. Ephemeral Rollups: delegated match account, low-latency execution of selected match-state instructions, then commit/finalize/undelegate to Solana.
2. Solana VRF: proof-verified randomness used to choose the same map sequence for all players.

## 2. What ER is and is not

ER is an execution environment for Solana transactions and delegated accounts. It is not a Unity physics engine, UDP game server, or automatic anti-cheat system. Keep 3D rendering and continuous physical simulation in Unity/server. Use ER for state transitions whose on-chain execution benefits from low latency and composability.

## 3. Account lifecycle

1. Initialize match PDA on Solana.
2. Delegate required match account(s) using the official delegation program / SDK flow.
3. Resolve the correct connection endpoint based on delegation status (use `magic-resolver` or current documented resolver approach where applicable).
4. Send program instructions to the correct ER endpoint.
5. Verify transaction confirmation and resulting delegated account state.
6. Commit/finalize state using SDK-supported flow.
7. Undelegate/finalize back to Solana when match ends.

Do not assume every account is automatically routed. Do not use ephemeral-only account type for persistent records. Test delegate → ER update → commit/finalize → undelegate → read on Solana in a minimal spike before game integration.

## 4. SDK selection and version pinning

Current MagicBlock SDK repository lists:
- Rust `ephemeral-rollups-sdk` for program integration, CPIs and Anchor macros.
- TypeScript `@magicblock-labs/ephemeral-rollups-sdk` for `@solana/web3.js` clients.
- TypeScript `@magicblock-labs/ephemeral-rollups-kit` for `@solana/kit` clients.
- `magic-resolver` for connection routing by delegation status.

The repo notes compatibility differences between Anchor 1.0 and earlier Anchor versions. Pin a known compatible tuple of Solana CLI, Anchor, Rust, SDK crate, TypeScript SDK and web3/kit packages. Commit lockfiles. Do not use floating `latest` in CI.

## 5. VRF flow

1. Create a unique match randomness request seed bound to match ID, ruleset version and map-pool version.
2. Program requests randomness and specifies callback instruction/accounts.
3. Oracle generates random value plus proof.
4. VRF program verifies proof on-chain.
5. Only verified callback runs; callback validates VRF program identity signer and request context.
6. Program stores random output/derived map sequence commitment.
7. Backend reads verified state and distributes same map sequence to all clients.

Do not request again merely because the sequence is undesirable. Use a deterministic unbiased selection method and prevalidated map pool. If VRF fails or times out, match stays in `RANDOMNESS_PENDING` or becomes unranked/aborted under explicit policy. Never substitute client-side randomness for ranked matches.

## 6. Match state update granularity

Do not write every player transform or frame to ER. Candidate updates:
- Match locked / roster hash.
- Map sequence commitment.
- Round started.
- Player qualified / eliminated.
- Round completed.
- Final champion and transcript hash.

For the hackathon, keep a single compact match state account and use a small number of transitions. If separate player records are needed, delegate only accounts required for ER instructions and test compute/account limits.

## 7. Session keys

Use a scoped session key only if current MagicBlock examples and supported authorization flow fit the program. Scope to a match, permitted instructions, expiry, and rate/sequence policy. Never let a session key transfer tokens, change program authority, alter map pool, or withdraw funds. The main wallet authorizes the session, then gameplay uses restricted authority. Revoke/expire after match.

## 8. TypeScript chain adapter

Implement an adapter with methods: `initializeMatch`, `requestRandomness`, `readRandomnessResult`, `delegateMatch`, `sendErInstruction`, `readErAccount`, `commitState`, `undelegate`, `readSolanaFinalState`. Every call records cluster, program ID, signature, slot, commitment, request ID, and error. Add retry/backoff for network errors; don't blindly retry non-idempotent instructions.

## 9. Demo proof checklist

- Explorer link to Solana initialization.
- Delegation transaction / account status.
- ER transaction signature for round or qualification update.
- Verified VRF request/callback signature and stored result.
- Commit/finalize/undelegate evidence.
- Final Solana account state matching the match summary.
- Screen recording showing all clients saw same map sequence and result.

## 10. Common failure modes

- Wrong endpoint after account delegation.
- Incompatible SDK/Anchor versions.
- Treating ephemeral-only state as persistent.
- Callback missing VRF signer validation.
- Duplicate event submission.
- Client claims result without server validation.
- UI displays submitted transaction as finalized.
- Using VRF output before callback proof verification.

## 11. Official references

Use `16-REFERENCES.md`; primary starting points are MagicBlock Ephemeral Rollups SDK repo, delegation program, Solana VRF repo, and MagicBlock docs. Read current examples rather than assuming API names from this conceptual guide are drop-in code.
