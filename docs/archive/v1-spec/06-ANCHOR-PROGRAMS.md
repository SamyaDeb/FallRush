# 06 — Anchor Program Specification

## 1. Program purpose

A minimal program records match lifecycle and competitive results. It does not simulate physics. It validates authorized, ordered state transitions and creates durable receipts. Use Anchor with a pinned version compatible with the current MagicBlock SDK integration. The MagicBlock SDK repository distinguishes Anchor 1.0 support from compatibility features for earlier Anchor versions; select versions together, not independently.

## 2. Program modules

### `match_registry`
Create match, lock roster hash, set ruleset/map-sequence commitment, track status and sequence.

### `match_state`
Delegated match-scoped state for round progression, bounded participant records/qualification state, event sequence, transcript hash.

### `player_profile` (post-MVP)
Persistent public profile and achievement pointers. Avoid storing PII.

### `cosmetic_claim` (post-MVP)
Optional claim authorization for a predefined cosmetic collection; integrate Metaplex Core via CPI only after separate tests.

No tournament vault/payment program in hackathon MVP.

## 3. Suggested PDAs

Use domain-separated seeds and stable byte encodings. Exact seeds must be documented and tested.

- `match`: `[b"match", match_id_bytes]`
- `match_state`: `[b"match_state", match_pubkey]`
- `player_record`: `[b"player", match_pubkey, player_pubkey]` (if separate accounts needed)
- `profile`: `[b"profile", wallet_pubkey]`
- `achievement`: `[b"achievement", wallet_pubkey, achievement_id]`

Store bump where useful. Validate all seeds and account ownership with Anchor constraints. Avoid unchecked account deserialization.

## 4. Account data (conceptual; finalize exact Borsh layout)

`MatchRegistry`:
- version: u8/u16
- match_id: [u8; 32]
- authority: Pubkey
- ruleset_version: u16
- status: enum
- roster_hash: [u8; 32]
- map_sequence_hash: [u8; 32]
- participant_count: u8
- current_round: u8
- event_sequence: u32
- transcript_hash: [u8; 32]
- champion: Pubkey (default sentinel) / option encoding
- created_at/completed_at: i64

`MatchState`:
- match: Pubkey
- authority: Pubkey
- status: enum
- current_round: u8
- event_sequence: u32
- participant records bounded to configured max OR separate PDAs
- qualified/eliminated flags
- transcript_hash: [u8; 32]

Do not use unbounded Vec for account state. Choose separate PDAs for per-player records if account size/compute becomes problematic.

## 5. Instructions

- `initialize_match(match_id, ruleset_version, participant_count, roster_hash, map_sequence_hash)`
- `lock_match()`
- `set_randomness_result(request_id, randomness, map_sequence_hash)` — called only through validated VRF callback flow or a trusted verified callback account pattern.
- `record_round_started(round, event_sequence)`
- `record_player_qualified(player, round, placement, event_sequence, event_hash)`
- `record_player_eliminated(player, round, reason_code, event_sequence, event_hash)`
- `record_round_completed(round, qualified_count, event_sequence)`
- `complete_match(champion, transcript_hash, event_sequence)`
- `abort_match(reason_code)` — restricted authority and allowed only from non-final state.
- MagicBlock delegation/commit/undelegation instructions integrated according to SDK macros/CPIs and official examples.

## 6. Authorization model

MVP uses a match authority key held by the backend signer service to submit server-validated events. Restrict it to a match-specific PDA or authority relationship. Do not use a general treasury key as match authority. Production should consider threshold signing, a verifier/observer model, or permissionless challenge windows.

For every instruction validate:
- Correct program-owned PDA and seeds.
- Match authority / authorized signer.
- Match status allows transition.
- Correct round index.
- Event sequence exactly expected (monotonic).
- Participant belongs to locked roster.
- Participant not previously qualified/eliminated in conflicting state.
- Counts cannot exceed roster or underflow.
- Map sequence hash matches locked value.
- No completion twice.
- No arbitrary champion assignment.

## 7. State machine

`Created → Locked → RandomnessPending → Ready → InProgress → RoundResolving → InProgress ... → Completed`.
`Aborted` is terminal. Only explicit transitions allowed. Reject skipped rounds and duplicate sequence numbers.

## 8. MagicBlock integration considerations

Use official `ephemeral-rollups-sdk` Rust integration and examples for delegation/commit/finalize/undelegate. The TypeScript SDK supports clients using `@solana/web3.js`; a separate kit package exists for `@solana/kit`. Confirm exact crate/API versions before writing code. Delegated accounts and ephemeral-only accounts have different lifecycles. Use delegated state for data that must commit back; use ephemeral-only accounts only when loss at lifecycle end is acceptable.

Do not copy stale program IDs or queue addresses from blog posts. Resolve from current official SDK/docs for the selected cluster and store in config. Test full lifecycle on devnet and document account owner/routing at each stage.

## 9. VRF integration

Use MagicBlock Solana VRF SDK request/callback flow. Request randomness with a match-specific caller seed. Callback must validate the VRF program identity signer and request association. Consume random bytes only after verified callback. Use the appropriate queue for Solana or ER execution path. Avoid accepting randomness supplied by the client or game server.

## 10. Errors and events

Custom errors: Unauthorized, InvalidStatus, InvalidRound, InvalidSequence, UnknownParticipant, DuplicateTransition, InvalidCounts, InvalidRandomnessCallback, WrongMatch, AlreadyCompleted, InvalidChampion, InvalidMapCommitment.

Emit events for match created/locked, randomness accepted, round started/completed, player qualified/eliminated, match completed/aborted. Events are for indexing; account state remains authoritative.

## 11. Security tests

Test wrong signer, wrong PDA, wrong owner, wrong program, wrong round, stale/duplicate event sequence, duplicate player, unknown participant, invalid VRF signer, replayed callback, altered map hash, early completion, second completion, abort after completion, account size boundary, integer overflow, and commit/undelegation failure.
