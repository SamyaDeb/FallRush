# 13 — Deployment and Mainnet Runbook

## 1. Environments

- `local`: local Unity + local API/DB + local Solana validator or supported local test setup.
- `devnet`: hackathon integration and demo.
- `staging`: production-like servers, dedicated RPC, load tests, no real value.
- `mainnet`: only after security, legal, operations, and user-support gates.

Separate keypairs, program IDs, RPC endpoints, token mints, asset collections, and database credentials per environment.

## 2. Configuration

Example server environment variables (placeholders only):

```env
APP_ENV=development
SOLANA_CLUSTER=devnet
SOLANA_RPC_URL=
SOLANA_WS_URL=
ANCHOR_PROGRAM_ID=
MAGICBLOCK_ER_ENDPOINT=
MAGICBLOCK_VRF_QUEUE=
MATCH_AUTHORITY_SECRET_REF=
DATABASE_URL=
REDIS_URL=
SESSION_SIGNING_KEY_REF=
ASSET_COLLECTION_ADDRESS=
```

Do not commit `.env` or real key material. Do not invent or copy program IDs from unverified sources. Confirm addresses from current official docs for selected cluster.

## 3. Devnet release

1. Build reproducibly with pinned Rust/Anchor dependencies.
2. Run unit/integration/security tests.
3. Deploy program to devnet.
4. Verify program ID and IDL.
5. Initialize test collection only if needed.
6. Run end-to-end match and ER lifecycle.
7. Save explorer signatures and logs.
8. Load test server with target player count.
9. Verify recovery and abort paths.

## 4. Production infrastructure

- Dedicated RPC provider with SLA and geographic coverage.
- Secondary RPC and tested failover.
- Backend services in multiple zones/regions as traffic warrants.
- Dedicated game servers with autoscaling and health checks.
- PostgreSQL backups and restore drills.
- Redis persistence only if required; DB remains durable source for operational data.
- Metrics, logs, traces, alerting, on-call rotation.
- DDoS protection, WAF, rate limits, secret manager.

Public Solana RPC endpoints are shared and not intended for production application traffic. Use a private provider and tested fallback.

## 5. Mainnet gates

Do not launch mainnet because the hackathon ends. Require:
- Independent Anchor program audit and remediation.
- MagicBlock lifecycle tested under failure conditions.
- Program upgrade authority moved to multisig.
- Treasury separation and withdrawal limits.
- Legal assessment of collectibles, marketplace, competitions and jurisdictions.
- Terms, privacy, moderation, support, incident response.
- RPC failover and monitoring.
- Mainnet canary with low-value/non-financial features first.
- Public status page and rollback/pausing policy.

## 6. Rollout sequence

1. Mainnet read-only profile and match receipts.
2. Cosmetic mint/claim with no cash value, capped supply, user-initiated.
3. Marketplace only after royalty/transfer/metadata behavior is tested.
4. Tournament economy only after legal review, independent audit, escrow design, dispute/refund policy, and jurisdiction controls.

## 7. Incident response

For suspicious match results: freeze ranked queue, preserve server transcript/logs, mark match disputed, stop reward claim, investigate, publish status. For contract vulnerability: pause affected instructions if pause design exists, notify multisig, secure assets, communicate transparently, and follow incident plan. Never promise a refund or outcome before verification.
