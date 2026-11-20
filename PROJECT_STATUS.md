# Fall Rush — Project Status

_Last updated: 2026-11-18 (D0)_

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

## Current milestone
**Waiting for approval** of docs v2 and the 🟡 decisions in `docs/15-DECISIONS-AND-RISKS.md`.

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

## Known bugs
- None yet, since no code has been written.

## Devnet proof signatures
- None yet.

## Toolchain
| Tool | Version | Note |
|---|---|---|
| node | 24.21 | |
| pnpm | 10.18.3 | |
| rustc | 1.96.1 | |
| solana-cli | 3.1.10 | |
| anchor-cli | 1.2.1 | Switching to 1.0.2 via `avm` for this project |
| docker | 29.2 | |
