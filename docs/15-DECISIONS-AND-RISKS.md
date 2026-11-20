# 15 — Decisions (ADR log) & Risks

**Status:** v2 (2026-11-18). ✅ = approved by you · 🟡 = Claude's default, awaiting your confirmation.

## Decisions

| ID | Decision | Status | Notes |
|---|---|---|---|
| ADR-001 | Original IP, genre-level inspiration. Gacha-capsule Rushlings with noodle arms. Fall Guys-style *feel*, original expression. | ✅ | doc 17 §1 guardrails |
| ADR-002 | Server-authoritative physics on a Unity headless dedicated server | ✅ | |
| ADR-003 | ER records competitive transitions (not frames) and **is** the live system of record for outcomes | ✅ | |
| ADR-004 | VRF deals the map, variant, modifiers and hazard seed; the **deal is derived inside the callback** | ✅ | Fixes v1 contradiction C4 |
| ADR-005 | Wallet optional; guests play everything | ✅ | |
| ADR-006 | No paid entry or cash prizes; Fan Picks are free points | ✅ | |
| D-1 | NGO 2.14 + custom prediction; FishNet as the post-hackathon alternative | ✅ | |
| **D-1b** | **Emergency fallback F1:** owner-predicted movement with server validation, used only if the Nov 22 go/no-go fails | 🟡 | Faster than switching to FishNet mid-hackathon. Weaker anti-cheat, disclosed. |
| D-2 | MagicBlock tuple = official examples: Anchor 1.0.2 + ER SDK 0.16.2 (`anchor`,`vrf`) + session-keys 3.1.1 | ✅ | doc 07 §1 |
| D-3 | VRF requested **inside the ER** (ephemeral queue) | 🟡 | Pattern from `roll-dice-delegated` / `rewards-delegated-vrf` |
| **D-4** | **WebGL client** (was: desktop). WebSocket transport everywhere. | 🟡 **changed** | Every past Blitz winner had a playable URL. UDP is impossible in browsers. |
| **D-5** | **Railway** hosting (was: VPS), with Hetzner/DO VPS + Caddy as fallback | 🟡 **changed** | WebSocket-only now; Railway is already connected; free TLS |
| D-6 | 4 rounds: Race → Survival → Skill → Final, 12→8→5→3→1 | ✅ | |
| D-7 | Art: Claude-led direction with you; capsule shape; noodle arms; logo hero "Bolt" is canonical | ✅ | |
| D-8 | Solana.Unity-SDK in the client for wallet connect; **Fan Picks on the web page** with gum-sdk session keys | ✅ / 🟡 | Picks-on-web is a 🟡 refinement (proven TS path) |
| D-9 | Drop Redis; **keep Postgres** (Railway plugin) | ✅ | |
| D-10 | Single rotatable `game_authority` per env; never upgrade/treasury | ✅ | |
| D-11 | Single `Match` PDA with slot-indexed roster; on-chain rolling transcript | 🟡 | doc 06 §1 |
| D-12 | Commit to Solana **after every round** (≤ 5 commits/match, within the 10-commit limit) | 🟡 | Visible Solana proof each round |
| D-13 | Bots run inside the server process, labelled; ranked requires ≥ 4 humans | 🟡 | |
| D-14 | Bonk Factory is stretch; R1 pool = Block Party Dash (VRF still picks variants and modifiers) | 🟡 | Time |

## Top risks (live; update daily in PROJECT_STATUS)

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | **7-day window** vs scope | High | High | Day plan + cut order (doc 14 §1); daily gate check |
| R2 | Prediction feel not good enough by Nov 22 | Med | High | Go/no-go gate; fallback F1 |
| R3 | UTP WebSockets through the Railway edge / TLS fails | Med | High | M1 spike on day 1; VPS + Caddy fallback |
| R4 | Devnet ER / VRF instability during demo or recording | Med | High | Record proof runs early; Unranked fallback; multiple recorded matches |
| R5 | Solana.Unity-SDK breaks on Unity 6 WebGL | Med | Low | Web wallet link fallback |
| R6 | WebGL build time and memory on 8 GB | Med | Med | Close the editor while building; build only once per day; measure in M1 |
| R7 | Not enough human playtesters | Med | High | Recruit now; bots fill and are labelled |
| R8 | Railway CPU jitter for a 30 Hz server | Low-Med | Med | Measure tick p95 in M6; VPS fallback |
| R9 | IP similarity complaints | Low | Med | Originality guardrails + checks (doc 17 §1, doc 08 §8) |
| R10 | Server-signer trust criticised by judges | Med | Low | Honest-trust line + roadmap in README |
