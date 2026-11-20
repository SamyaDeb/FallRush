# 11 — Security, Anti-Cheat and Trust Model

## 1. Threat model

Threat actors include modified clients, speed/teleport hacks, packet replay, input flooding, checkpoint spoofing, bot farms, Sybil wallets, backend compromise, RPC spoofing, transaction replay, malicious map configs, and smart-contract authority abuse.

## 2. Trust statement for MVP

The dedicated game server is trusted to simulate physics and adjudicate gameplay. The Anchor program validates that authorized match transitions are well-formed, ordered, and associated with the locked match. MagicBlock ER records selected state transitions. VRF provides verifiable randomness for map selection. This is not trustless verification of every player movement.

Publish this statement in the demo and docs. Do not claim “fully on-chain game” or “cheat-proof” unless that is actually true.

## 3. Gameplay anti-cheat

- Server authoritative position/velocity.
- Client sends inputs only.
- Clamp input axes and rate.
- Validate max speed, acceleration, jump impulse, cooldowns, air control.
- Validate checkpoint order and reachability.
- Detect impossible displacement and repeated boundary violations.
- Server owns hazard schedule, collision, fall/elimination.
- Rate-limit reconnect and lobby creation.
- Use anomaly scoring and human review before permanent sanctions.
- Do not rely on client clock.

## 4. Chain security

- PDA seeds and account owner validated on every instruction.
- Explicit authority checks and state machine checks.
- Monotonic event sequence; reject replay and duplicate transitions.
- Bounded participant count and account data.
- Checked arithmetic and explicit enum validation.
- Verify VRF callback identity and request association.
- Restrict match signer to match-specific actions.
- No treasury withdrawal authority in game server key.
- Use separate keys per environment.
- Mainnet upgrade authority in multisig; cold treasury separated from hot operations.

## 5. Transaction safety

- Idempotency key for every chain action.
- Store signature before retry decision.
- Distinguish submitted, processed, confirmed, finalized, failed.
- Retry only safe transient errors; re-read account state before retrying uncertain submissions.
- No duplicate mint/claim or payout.
- Verify mint/program IDs and cluster configuration.

## 6. Backend security

- TLS, JWT/session expiry, refresh rotation.
- Nonce-based wallet signature challenge.
- Secrets manager; no secrets in Unity builds or Git.
- Restrict admin endpoints; require MFA and audit log.
- DB least privilege and encrypted backups.
- DDoS/rate limiting and packet size caps.
- Dependency scanning and pinned lockfiles.

## 7. Mainnet launch gates

- Independent program audit.
- Threat model review and remediation.
- Testnet soak and load test.
- Multisig configured and tested.
- Incident response and emergency pause tested.
- RPC redundancy and alerting.
- Legal review for jurisdictions and any prize/economy model.
- Privacy policy, terms, age policy, content moderation and support process.
