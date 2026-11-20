# Fall Rush: Engineering Docs v2

**The provably fair knockout party game.** 12 wind-up capsule toys, 4 chaotic rounds, one crown.

Built for **MagicBlock Solana Blitz V10** (Entertainment Finance). Window: Nov 20 – **Nov 27, 2026, 14:00 UTC**.

**Status:** v2, 2026-11-18. The original v1 spec lives in `../fall-rush-docs/`; it is frozen and kept for reference only. v2 supersedes it wherever they differ. Doc 15 records the user's decisions.

## Reading order

| # | Doc | What it covers |
|---|---|---|
| 18 | [Hackathon Strategy](18-HACKATHON-STRATEGY.md) | Rules, what wins, pitch, proof checklist, 3-min demo script |
| 14 | [Implementation Plan](14-IMPLEMENTATION-PLAN.md) | Day-by-day milestones, owners, cut order |
| 15 | [Decisions & Risks](15-DECISIONS-AND-RISKS.md) | ADR log (approved and pending), risk register |
| 01 | [GDD](01-GDD.md) | Rounds, modes, movement, Chaos Capsules, Fan Picks, progression, UX |
| 17 | [Art Direction](17-ART-DIRECTION.md) | Rushlings, world, palette, shading, feel, UI, audio, originality guardrails |
| 08 | [Maps & Obstacle Kit](08-MAP-DESIGN.md) | 4 (+1) maps, deterministic hazard kit, QA |
| 02 | [PRD](02-PRD.md) | Requirements, priorities, acceptance criteria |
| 03 | [Architecture](03-TECHNICAL-ARCHITECTURE.md) | System diagram, source of truth, repo layout |
| 04 | [Unity Client & Server](04-UNITY-CLIENT.md) | Versions, WebSocket transport, prediction design, motor, builds |
| 05 | [Rush Relay Backend](05-BACKEND-RUSH-RELAY.md) | Services, internal contract, canonical state machine, chain worker |
| 06 | [Anchor Program](06-ANCHOR-PROGRAM.md) | Accounts, instructions, on-chain deal, errors, security tests |
| 07 | [MagicBlock Integration](07-MAGICBLOCK-INTEGRATION.md) | Pinned versions, IDs, verified code patterns, failure policy, local stack |
| 09 | [Database](09-DATABASE-SCHEMA.md) | Postgres schema |
| 10 | [API Contracts](10-API-CONTRACTS.md) | REST + realtime events |
| 11 | [Security](11-SECURITY-ANTI-CHEAT.md) | Trust statement, threats → controls, keys |
| 12 | [Testing](12-TESTING-PLAN.md) | Automated, end-to-end, playtests, release checklist |
| 13 | [Deployment](13-DEPLOYMENT.md) | Environments, Hostinger VPS, env vars, release steps |
| 16 | [References](16-REFERENCES.md) | Verified sources |

## Non-negotiables

- **Original IP.** The feel is genre-inspired; the expression is ours (doc 17 §1).
- **No wallet needed to play.** No popups during gameplay.
- **No invented SDK functions, program IDs or versions.** Everything chain-related is taken from the pinned SDK source (doc 07).
- **Honest trust model.** Never call the game "fully on-chain" or "cheat-proof".
- **No paid entry, no cash prizes, no token.**
- **No secrets in git.** Lockfiles are committed.
