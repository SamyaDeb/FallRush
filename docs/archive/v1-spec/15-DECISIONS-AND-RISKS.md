# 15 — Architecture Decisions and Open Risks

## ADR-001: Original IP, genre-level inspiration only
**Decision:** Build an original obstacle-course knockout game.  
**Reason:** Retain familiar genre mechanics while creating distinct characters, maps, art, audio, UI, names and brand.  
**Risk:** Similarity in overall presentation can still create trade dress/IP questions; obtain legal review before commercial release.

## ADR-002: Server-authoritative physics
**Decision:** Dedicated server owns continuous simulation.  
**Reason:** Client-authoritative movement is trivially cheatable; ER is not a physics engine.  
**Trade-off:** MVP trusts server operator for physical adjudication.

## ADR-003: ER for competitive state transitions, not every frame
**Decision:** Submit round/qualification/elimination/final events, not 30–60 transforms per second.  
**Reason:** Cost, throughput, latency, compute, account write contention, and clear blockchain value.  
**Open:** exact delegated account design and supported update pattern must be validated in integration spike.

## ADR-004: VRF selects shared match sequence
**Decision:** One verified random output selects a prevalidated sequence for all players.  
**Reason:** Demonstrable fairness and variety.  
**Risk:** Oracle/fulfillment latency; pre-match request and explicit timeout policy.

## ADR-005: Wallet optional
**Decision:** Guest play first; wallet for ownership/claims.  
**Reason:** Consumer onboarding and no signatures during core gameplay.

## ADR-006: No paid cash tournaments in MVP
**Decision:** Free-entry competitions and non-redeemable achievements only.  
**Reason:** Legal uncertainty, particularly India; reduce custody and prize settlement scope.

## Open technical risks

1. **MagicBlock SDK version compatibility:** current Rust SDK notes Anchor 1.0 vs compatibility mode for older Anchor. Pin compatible versions after testing.
2. **Delegation lifecycle:** commit/finalize/undelegate exact semantics and account routing must be tested with current docs.
3. **Server signer trust:** backend authority can falsify gameplay events in MVP. Document and isolate key; production trust minimization needed.
4. **Physics parity:** server and Unity physics may diverge. Keep authoritative rules simple; use deterministic custom movement/hazard logic where possible.
5. **VRF map selection bias:** implement unbiased selection and lock eligible pool before request.
6. **Latency:** ER is not in the per-frame movement path; don't market it as rendering/gameplay latency guarantee.
7. **Player count:** target 10–12 MVP, 20 beta; only claim 40 after load tests.
8. **Mainnet security:** audits and incident response are mandatory before value-bearing operations.
9. **Legal/IP:** title, characters, maps, UI, sounds, tournament rules, NFTs and geography require review.
10. **Production hosting:** Unity dedicated server hosting, regional latency, cost and autoscaling need a measured pilot.

## Decision log template

- Date:
- Decision:
- Context:
- Alternatives:
- Consequences:
- Owner:
- Evidence/tests:
