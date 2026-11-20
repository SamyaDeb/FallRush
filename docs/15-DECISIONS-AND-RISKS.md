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
| **D-1b** | **Fix first, then fallback F1.** If the Nov 22 go/no-go fails, diagnose and fix the server-authoritative prediction first (doc 04 §4.5 fix window, until **Mon Nov 23 12:00 UTC**). Only if it still fails, switch to F1: owner-predicted movement with server validation. | ✅ (revised 2026-11-18) | You chose fix-first. F1 is the last resort: weaker anti-cheat, disclosed. |
| D-2 | MagicBlock tuple = official examples: Anchor 1.0.2 + ER SDK 0.16.2 (`anchor`,`vrf`) + session-keys 3.1.1 | ✅ | doc 07 §1 |
| D-3 | VRF requested **inside the ER** (ephemeral queue) | ✅ | Proven on devnet in M0 (callback 426 ms). | Pattern from `roll-dice-delegated` / `rewards-delegated-vrf` |
| **D-4** | **WebGL client** (was: desktop). WebSocket transport everywhere. | ✅ | Every past Blitz winner had a playable URL. UDP is impossible in browsers. |
| **D-5** | **Hostinger VPS** (your existing KVM 2: 2 vCPU, 8 GB, Ubuntu 24.04, Mumbai). Docker Compose for Fall Rush services bound to `127.0.0.1`; the VPS's existing **nginx** terminates TLS on new subdomains (Let's Encrypt). Replaces Railway. | ✅ (2026-11-18) | Your choice. Mumbai is close to `devnet-as` (Asia) and to you (≈45 ms ping measured). The VPS already serves another site; Fall Rush must not touch it (doc 13 §2). |
| D-6 | 4 rounds: Race → Survival → Skill → Final, 12→8→5→3→1 | ✅ | |
| D-7 | Art: Claude-led direction with you; capsule shape; noodle arms; logo hero "Bolt" is canonical | ✅ | |
| D-8 | Solana.Unity-SDK in the client for wallet connect; **Fan Picks on the web page** with gum-sdk session keys | ✅ | Proven TS session-key path |
| D-9 | Drop Redis; **keep Postgres** (Docker container on the VPS, `postgres:16`, volume + nightly `pg_dump`) | ✅ | |
| D-10 | Single rotatable `game_authority` per env; never upgrade/treasury | ✅ | |
| D-11 | Single `Match` PDA with slot-indexed roster; on-chain rolling transcript | ✅ | doc 06 §1 |
| D-12 | Commit to Solana **after every round** (≤ 5 commits/match, within the 10-commit limit) | ✅ | Visible Solana proof each round |
| D-13 | Bots run inside the server process, labelled; ranked requires ≥ 4 humans | ✅ | |
| D-14 | Bonk Factory is stretch; R1 pool = Block Party Dash (VRF still picks variants and modifiers) | ✅ | Time |
| **D-15** | **Solana.Unity-SDK pinned to upstream `main` commit `1da8fd7f`** instead of tag v1.2.9 | ✅ (2026-11-18) | v1.2.9 breaks every IL2CPP/WebGL build (BouncyCastle DLL file name, upstream #257). `1da8fd7f` = v1.2.9 + the fix (2 commits). Alternative: the doc 04 §8 web wallet-link fallback |
| **D-16** | **Local transport tests on Apple Silicon use a native macOS dev server**; the Linux x86_64 Mono image is first run on the VPS | ✅ (you chose it in M1) | Mono Linux crashes under Rosetta and QEMU (`x86-codegen.h:410`). Shipped server stays Linux x86_64 Mono. Option kept open: IL2CPP Linux server (runs under emulation, slower builds) |

## Top risks (live; update daily in PROJECT_STATUS)

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | **7-day window** vs scope | High | High | Day plan + cut order (doc 14 §1); daily gate check |
| R2 | Prediction feel not good enough by Nov 22 | Med | High | Go/no-go gate; fallback F1. **M2a (measured):** the shared motor is bit-deterministic for the same inputs (analytic + PhysX EditMode); state = wire precision (mm, cm/s); tick-keyed inputs with a feedback clock (lead held at 2 ticks, 0 late on localhost). Server cost with 12 occupants ≤ 0.7 ms frame work p95 (Mac). **M2b (measured, docs/proof/M2-netcode.md Part B):** 3 course-running clients, corrections per 5 s while running the course: localhost **0.00**; 150 ms RTT ±15 ms jitter + 2 % TCP segment loss **0.08–0.17**, all in the first 10 s (clock convergence), 0 afterwards; WebGL (wasm) client vs Mac server ≈ 0.25 with ~0 cm visible shift (flag-only), mean drift 3.7 mm. Sweeper hits 0 mismatches. **Real VPS (Mumbai):** Mac clients 0.21–0.25 per 5 s (arm64 ↔ x86_64 drift), **WebGL client ↔ Linux server 0 corrections, 99.8 % bit-identical**, VPS frame work p95 ≤ 1.05 ms at 4 % CPU. A TCP-stall kick found on the VPS is fixed (proof B10). Recommendation: **go** (no fix window needed; the feel verdict is yours) |
| R3 | UTP WebSockets through a TLS-terminating nginx proxy fails | **Retired** | High | **M1 (measured):** local nginx 1.24 TLS (same origin, 101 in access log, 10-min soak, restart + reconnect) **and the VPS** (Let's Encrypt on `gs1.<VPS_IP>.sslip.io`, 2 Chrome clients, RTT p50 66 / p95 83 ms) all pass. Fallback not needed |
| R4 | Devnet ER / VRF instability during demo or recording | Med | High | Record proof runs early; Unranked fallback; multiple recorded matches |
| R5 | Solana.Unity-SDK breaks on Unity 6 WebGL | **Happened** (tag v1.2.9) → **Low** with the fix | Low | **M1 (measured):** v1.2.9 fails the WebGL link (BouncyCastle, upstream #257); upstream fix `1da8fd7f` builds WebGL + server, +4.0 MB compressed, wallet picker opens in Chrome. Real Phantom connect UNVERIFIED. Fallback still: web wallet link (doc 04 §8) |
| R6 | WebGL build time and memory on 8 GB | Med | Med | **M1 (measured):** release 523 s first / 294 s incremental; dev 714 s first / 238 s warm; peak RSS 1.1–1.5 GB; swap is used when other apps are open, and two licensing time-outs happened under heavy swap. Runtime: wasm heap 96 MB in Lobby (2 GB max). Keep: Editor closed, few apps open, ≤ 2 builds per hour |
| R7 | Not enough human playtesters | Med | High | Recruit now; bots fill and are labelled |
| R8 | 2 vCPU VPS shared with another site: CPU contention for two 30 Hz Unity servers + Relay + Postgres + web | Med | Med | Measure tick p95 with 2 matches in M6; Docker CPU limits; run 1 game server if needed; upgrade to KVM 4 as last resort |
| R9 | IP similarity complaints | Low | Med | Originality guardrails + checks (doc 17 §1, doc 08 §8) |
| R10 | Server-signer trust criticised by judges | Med | Low | Honest-trust line + roadmap in README |
| R11 | Linux game server image can only be run on real x86_64 (Mono cannot run under emulation on the Apple Silicon dev box) | Low | Med | **M1:** image runs on the VPS. Keep testing server builds on the VPS (or any x86_64 box) before each deploy; local tests use the Mac dev server |
| R13 | VPS port clash with the existing site (`127.0.0.1:8787`, `:3100` in use) | **Known** | Med | Pick relay/web ports that are free (check `ss -ltn`) in M6; doc 13 §2 |
| R15 | arm64 (Mac) builds are not bit-identical with x86_64/wasm (corrections every ~20–25 s on Mac ↔ VPS; WebGL ↔ VPS is bit-identical) | Low | Low | Only the dev tool is affected. Don't tune prediction from Mac-client numbers; measure with WebGL. Hypothesis (unverified): FMA contraction or arm64 PhysX |
| R14 | Push-driven corrections: player–player separation is server-only, so bunched players see corrections (measured: up to 11 per 5 s when 3 bots shared one lane in an early run) | Med | Med | Counted separately (`ownEvents`); lanes in the gate. **M3 (measured, crowded 12-player race):** client-side push prediction against remotes extrapolated from the newest snapshot is on (corrections + pushes per 5 s while racing, localhost: 2.73 → 0.73 mean). Still over the < 1 bar for one of three clients on localhost (1.12) and at 150 ms + loss (1.2–1.7); non-push corrections stay ≤ 0.4. Options for M4 🟡: softer pushes (e.g. 1.5 m/s), or accept push corrections (they are smoothed, ~10 cm) |
| R16 | Reference bots finish Block Party Dash in 31–34 s (median) vs the 75–95 s target | High | Med | Course may be too short/easy for humans or fine once humans play; decide after the playtest: add length (S1 blocks, S2 bridge) or harder timing. 🟡 |
| R17 | VPS link from the dev machine was degraded on 2026-11-19 (85 % ICMP loss); the M3 WebGL ↔ Linux gate could not be measured | Med | Med | Re-run `scripts/m3-gate.sh` / WebGL `?bot=race` against gs1 on a healthy link before M4 |
| R12 | Hidden browser tab freezes Unity WebGL (no `requestAnimationFrame`) → UTP 30 s timeout → player dropped | Med | Med | **Fixed for 10/30/60 s on the desktop path (M2b, measured with a main-thread stall):** the drop was not the timeout but NGO's reliable pipeline re-sending its window every 500 ms into the stalled client (16.7 KB/s), which overflowed UTP's receive queue on wake-up (`ProtocolError`). Fix: our `WebSocketDriverConstructor` (NGO's driver, reliable resend 10–30 s; TCP already guarantees delivery), server pauses snapshots after 1 s without input, server disconnect timeout 90 s, client queue 1024. **A real hidden Chrome tab is UNVERIFIED** (Playwright keeps pages visible); steps in the proof file |
