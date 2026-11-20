# 12 — Testing Plan

**Status:** v2 (2026-11-18). Tests are sized for a 7-day build: critical paths are automated; everything else is a scripted manual check.

## 1. Automated

| Layer | Tool | Must cover |
|---|---|---|
| Anchor program | `anchor test` on the local MagicBlock stack (doc 07 §7) | Full lifecycle; every security test in doc 06 §7 |
| Deal parity | Rust, TS and C# tests on `deal_vectors.json` | Identical output for 100 seeds |
| Relay | `vitest` + a Postgres test container (docker) | Outbox idempotency; worker resumes after a crash with no duplicates; nonce single-use; join ticket single-use; zod validation |
| Unity EditMode | Unity Test Framework | Motor limits, HazardMath vectors, Ruleset targets, protocol round-trip, input sanitiser |
| Unity PlayMode | Unity Test Framework (server-mode scene + bots) | Checkpoint order; respawn; full 4-round match with 12 bots completes to a champion (×10, no soft-lock) |
| Map QA | Bot runner (doc 08 §9) | 100 runs per map, variant and modifier |

## 2. Integration and end-to-end (scripted)

1. **Local:** Postgres (docker) + Relay + local MagicBlock stack + a server build + 1 editor client + 11 bots → full ranked-practice match. Check that the on-chain account equals the DB result.
2. **Devnet:**
   - same as the local run, against devnet-as;
   - export `PROOF.md`;
   - verify every link opens.
3. **Network:** 3 browser clients with the Unity network simulator at 150 ms / 2% loss. Correction-rate HUD readout below the doc 04 §4.5 threshold.
4. **Failure drills:**
   - kill the Relay mid-round → the game continues; on restart the outbox drains in order;
   - block the ER endpoint → chip shows `Pending`, results land later;
   - VRF timeout → Unranked banner.
5. **Real playtest:** ≥ 8 humans on the deployed URL (the user recruits them).
   - Session 1: Nov 25.
   - Session 2: Nov 26 (recorded for the video).

## 3. Release checklist (Nov 27, before 12:00 UTC)

- [ ] Clean clone builds: `anchor build`, `pnpm -r build`, Unity WebGL + server builds
- [ ] All automated tests green (paste the output into PROJECT_STATUS)
- [ ] No secrets in the repo (`git grep -nE "[1-9A-HJ-NP-Za-km-z]{80,}"` plus gitleaks if available)
- [ ] Program ID + cluster shown in the web footer
- [ ] PROOF.md links verified
- [ ] Known limitations listed in the README
- [ ] Deployed URL survives 2 back-to-back matches
