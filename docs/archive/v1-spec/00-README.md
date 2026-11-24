# FALL RUSH — Engineering Documentation

**Working title:** Fall Rush (placeholder; perform trademark/domain clearance before public launch)  
**Product:** Original, colorful, multiplayer knockout party game inspired by the obstacle-course battle-royale genre.  
**Chain:** Solana  
**Hackathon:** MagicBlock Solana Blitz V10 — Entertainment Finance  
**Document status:** Build specification v1.0, 18 November 2026

## 1. Product direction

Build an original multiplayer game in which 20 players (MVP target: 10–12) compete across sequential rounds. Each round uses a different obstacle-course map or survival challenge. Players qualify or are eliminated until one champion remains.

The game should be enjoyable without a wallet. Solana is an optional ownership, proof, and settlement layer—not a requirement for movement, jumping, camera control, or ordinary play.

## 2. Core product pillars

1. **Play:** responsive character controller, readable obstacles, polished feedback, complete multi-round match.
2. **Compete:** fair server-authoritative simulation, qualification rules, match history, rankings.
3. **Collect:** optional player-owned cosmetic collectibles and achievements.
4. **Verify:** MagicBlock Ephemeral Rollups for selected competitive state transitions; MagicBlock VRF for public, reproducible random map selection; Solana for durable records and eligible asset ownership.

## 3. Scope hierarchy

### Hackathon must-have
- One complete playable match from lobby to champion.
- 8–12 concurrent participants (real clients; bots may fill empty slots and must be labelled).
- Three rounds minimum: race, survival, final. A fourth team/skill round is stretch.
- Two polished maps minimum; four map modules preferred.
- Dedicated authoritative game server.
- Anchor program deployed to devnet.
- Demonstrable MagicBlock ER delegation, state update, and commit/undelegation path.
- Demonstrable VRF request/verified callback used to select a fixed map sequence or seed.
- Explorer links, logs, architecture diagram, and a short demo.

### Post-hackathon beta
- 20 players per match, matchmaking, reconnect, spectator mode, 8–12 maps, progression, cosmetics, analytics, operations.

### Production
- Regional fleet, autoscaling, abuse prevention, audited programs, incident response, mainnet asset lifecycle, legal review, privacy/data retention policy, support operations.

## 4. Non-goals for hackathon
- Reproducing Fall Guys assets, characters, maps, music, UI, code, names, or distinctive protected expression.
- 40-player production capacity claims without load tests.
- Fully on-chain 60 Hz physics.
- Paid-entry or cash-prize competitions in India before specialist legal review.
- Player-created map editor, token launch, governance token, staking, or speculative yield.
- Trustless verification of every physical movement. The MVP uses a trusted authoritative server and clearly documents this assumption.

## 5. Repository layout

```text
fall-rush/
  client-unity/              # Unity project
  server/                    # TypeScript lobby/match orchestration + game server adapter
  programs/                  # Anchor workspace
  web/                       # Next.js landing/profile/inventory UI
  packages/contracts/        # OpenAPI, JSON schemas, shared event types
  infra/                     # Docker, deployment manifests, observability
  docs/                      # This documentation set
```

## 6. How to use these documents

Read in order: GDD → PRD → architecture → Unity → backend → Anchor → MagicBlock → maps → DB/API → security/testing/deployment → Codex prompts.

Treat `15-DECISIONS-AND-RISKS.md` as the decision log. Do not invent program IDs, MagicBlock queue addresses, SDK APIs, token mints, or deployment configuration. Pin versions after checking current official docs and examples. Record actual values in environment-specific configuration.

## 7. Source-of-truth principles

- Match simulation and physics: authoritative dedicated server.
- Persistent competitive record and ownership: Solana program accounts / supported asset standard.
- Delegated high-frequency or match-scoped on-chain state: MagicBlock ER only where the chosen account lifecycle and program integration have been tested.
- Randomness: MagicBlock VRF proof-verified callback; no `Math.random()` for outcomes claimed to be verifiable.
- UI state is never proof of an on-chain result. Verify signatures, account state, and commitment.
