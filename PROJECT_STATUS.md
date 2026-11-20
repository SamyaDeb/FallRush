# Fall Rush — Project Status

_Last updated: 2026-11-18 (D0, M0 in progress)_

**Hackathon:** MagicBlock Blitz V10, running Nov 20 to Nov 27, 2026.
- **Submission deadline:** Nov 27, 14:00 UTC (19:30 IST).
- **Our internal target:** submit by 12:00 UTC.

## Completed
- **Engineering audit** of the v1 docs. See `ENGINEERING_REPORT.md`; it is now superseded by `docs/`.
- **Research, verified against these sources:**
  - Blitz V10 rules and past winners
  - MagicBlock SDK 0.16.2 APIs and `magicblock-examples`
  - Unity 6.3 / NGO 2.14
  - hosting options
  - Metaplex Core
- **Docs v2**, written in `docs/` (00–18):
  - art direction
  - hackathon strategy
  - implementation plan

## Current milestone: M0 (repo + MagicBlock lifecycle spike)
| Item | State |
|---|---|
| Monorepo scaffold, `.gitignore`, shared config + hashes | ✅ |
| `anchor build` on Anchor 1.0.2 + ER SDK 0.16.2 | ✅ |
| Deal parity Rust (5/5) + TS (6/6) on 100-seed vectors | ✅ |
| `pnpm m0:test:local` lifecycle on mb-stack 0.14.10 + 2 VRF oracles | ✅ 10/10 |
| Program deployed to devnet `4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4` (deploy tx `zQzTyAr95iipfmHHaBc51TogPtXxkDe85A8E3k3Z8jebsZshE6Z2AiYvnKTsiXsmxLtxxNAXR6Hq1g8XNsSXmzf`) | ✅ |
| Devnet lifecycle on devnet-as (`pnpm m0:devnet`) → `docs/proof/M0-devnet.md` | ⏳ **blocked: fund game authority `3EYRtprzYq7Zk9AKP15pgTtTUMYMYeqVUsHkUXrKAthz` (~1 devnet SOL)** |

**Local measurements (M2 Air, local stack — not devnet):** VRF request→callback 0.1–0.6 s; ER send→confirm p50 8–18 ms, p95 30–220 ms (polling); commit visible on Solana ~6.5 s; commit+undelegate ownership back ~0.4–0.5 s.

### Notes for M6 (from M0)
- **Typed-account delegation:** `#[delegate]` needs `UncheckedAccount`; we deserialize `Match` manually before the CPI — works (tested).
- **Confirmations:** local ER intermittently drops websocket signature notifications → the chain worker must confirm by polling `getSignatureStatuses`; `GetCommitmentSignature` can take ~30 s even when the commit already landed — show "Solana ✓" from the base account state, fetch the commit sig asynchronously.
- **Config on the ER** is a cloned read-only copy; config changes on Solana may be stale on the ER (UNVERIFIED clone-refresh behaviour).
- **Ruleset hash** is compiled into the program; adding movement constants (M2) to the ruleset means a new ruleset version + program upgrade + config update (`update_config` not implemented yet).
- **`finalize_match` from `RoundResolved`** is an M0 shortcut — remove in M6.
- Missing security tests listed in docs/06 §9.

## Next milestones
Both are scheduled for Nov 19 (D1):
- **M0:** repo setup plus a MagicBlock lifecycle spike.
- **M1:** Unity foundation, with a WebGL ↔ Linux server WebSocket spike.

## Decisions pending (🟡)
| ID | Decision | Default |
|---|---|---|
| D-1b | Netcode fallback | F1 |
| D-3 | Where the VRF runs | In the ER |
| D-4 | Client platform | WebGL |
| D-5 | Hosting | Railway |
| D-8 | Where Fan Picks are made | Web |
| D-11 | Match account design | Single PDA |
| D-12 | Commits to Solana | Once per round |
| D-13 | Bots | Inside the server process |
| D-14 | Bonk Factory map | Stretch goal |

## Your tasks for today
- [ ] Install Unity 6000.3.28f1 LTS with the Web, Linux Dedicated Server and Linux Mono modules.
- [ ] Register for the event on Luma (luma.com/hedv5sgh).
- [ ] Create a Railway project.
- [ ] Create a devnet wallet and fund it.
- [ ] Start recruiting 8–12 playtesters for Nov 25 and Nov 26.

## Blockers
- Unity is not installed yet.
- M0 devnet run needs devnet SOL on the game authority `3EYRtprzYq7Zk9AKP15pgTtTUMYMYeqVUsHkUXrKAthz`. Your default wallet has 0.85 devnet SOL left after the deploy.

## Known bugs
- None yet, since no code has been written.

## Devnet proof signatures
- Program deploy: `zQzTyAr95iipfmHHaBc51TogPtXxkDe85A8E3k3Z8jebsZshE6Z2AiYvnKTsiXsmxLtxxNAXR6Hq1g8XNsSXmzf` (devnet).
- Lifecycle run: pending funding → `docs/proof/M0-devnet.md`.

## Toolchain
| Tool | Version | Note |
|---|---|---|
| node | 24.21 | |
| pnpm | 10.18.3 | |
| rustc | 1.96.1 | |
| solana-cli | 3.1.10 | |
| anchor-cli | 1.0.2 (project, via `[toolchain]`) / 1.2.1 (global default) | 1.0.2 built from source, see docs/07 §9 |
| ephemeral-validator | 0.14.10 | global npm (mb-stack, vrf-oracle) |
| docker | 29.2 | |
