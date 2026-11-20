# 12 — Testing Plan

## 1. Test pyramid

- Unit: movement rules, qualification, state machine, sequence mapping, PDA derivation, serialization.
- Integration: API/DB, game server, chain adapter, VRF callback, delegation lifecycle.
- PlayMode: Unity scenes, obstacle collision, respawn, camera, round transitions.
- Multiplayer: 2, 8, 12, then 20 clients; latency, packet loss, disconnects.
- Security: replay, forged inputs, wrong signer, wrong PDA, invalid callback, duplicate event.
- Load/soak: concurrent matches, server CPU/memory, RPC/ER queue backlog.

## 2. Critical test cases

### Gameplay
- Player cannot jump beyond configured impulse.
- Player cannot move beyond maximum speed.
- Checkpoint 2 cannot be accepted before checkpoint 1.
- Fall/respawn does not reset valid checkpoint unless rules specify.
- Eliminated player cannot re-enter or qualify.
- Ties resolved deterministically.
- Timer expiry and server restart have deterministic outcome policy.

### Match lifecycle
- Duplicate start event is idempotent.
- Match cannot start without locked roster and valid map sequence.
- Round cannot skip.
- Completed match cannot be completed again.
- Abort cannot overwrite completed state.
- Disconnect grace period behaves consistently.

### MagicBlock / Solana
- Delegate → ER write → commit/finalize → undelegate → Solana read.
- Wrong endpoint fails safely.
- VRF invalid proof does not invoke callback.
- Wrong callback signer rejected.
- Replayed callback rejected.
- Duplicate chain worker event does not double-apply.
- Failed commit leaves result pending, not finalized.

### Client/server networking
- 100–250 ms RTT simulation.
- 1–5% packet loss.
- Burst jitter.
- Client reconnect during race and intermission.
- Client sends NaN, infinity, huge values, stale seq, oversized message.
- Server process crash policy and match abort/recovery.

## 3. Performance targets (initial hypotheses, validate by measurement)

- Server simulation tick p95 under 10 ms at 12 players on chosen instance; revise after profiling.
- Snapshot age p95 under 150 ms in target test region.
- No unbounded memory growth during 2-hour soak.
- Match state transitions are durable and ordered.
- No chain RPC in the critical simulation loop.

These are engineering targets, not guaranteed platform performance claims.

## 4. Test automation

- Unity EditMode and PlayMode tests in CI.
- Rust `cargo test` and `cargo test-sbf` where supported.
- Anchor integration tests on local validator / Surfpool / documented MagicBlock local environment.
- API contract tests from OpenAPI.
- Deterministic replay tests using recorded input streams.
- Load test with simulated clients; do not use bots as proof of human user growth.

## 5. Release checklist

- Clean build from fresh checkout.
- All tests pass.
- Lockfiles committed.
- No secrets in repository or build.
- Devnet program ID and cluster shown in UI.
- Demo transactions verified.
- Known limitations disclosed.
- Rollback and incident contacts documented.
