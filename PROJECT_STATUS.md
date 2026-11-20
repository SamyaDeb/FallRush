# Fall Rush — Project Status

_Last updated: 2026-11-18 (D0, M0 complete)_

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

## Current milestone: M0 (repo + MagicBlock lifecycle spike) — ✅ complete
| Item | State |
|---|---|
| Monorepo scaffold, `.gitignore`, shared config + hashes | ✅ |
| `anchor build` on Anchor 1.0.2 + ER SDK 0.16.2 | ✅ |
| Deal parity Rust (5/5) + TS (6/6) on 100-seed vectors | ✅ |
| `pnpm m0:test:local` lifecycle on mb-stack 0.14.10 + 2 VRF oracles | ✅ 10/10 |
| Program deployed to devnet `4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4` (deploy tx `zQzTyAr95iipfmHHaBc51TogPtXxkDe85A8E3k3Z8jebsZshE6Z2AiYvnKTsiXsmxLtxxNAXR6Hq1g8XNsSXmzf`) | ✅ |
| Devnet lifecycle on devnet-as (`pnpm m0:devnet`) → `docs/proof/M0-devnet.md` | ✅ full lifecycle on devnet-as, final Solana state == ER state (2026-11-18) |

**Devnet measurements (devnet-as, from this machine, one run):** VRF request→callback 426 ms; ER send→confirm p50 227 ms, max 235 ms (≈ network RTT); commit visible on Solana 1.9 s (GetCommitmentSignature 1.9 s); commit+undelegate ownership back 1.5 s; delegate tx 17 s (public devnet RPC 429 rate-limiting). Run cost ≈ 0.011 SOL.

**Local measurements (M2 Air, local stack):** VRF request→callback 0.1–0.6 s; ER send→confirm p50 8–18 ms, p95 30–220 ms (polling); commit visible on Solana ~6.5 s; commit+undelegate ownership back ~0.4–0.5 s.

### Notes for M6 (from M0)
- **Typed-account delegation:** `#[delegate]` needs `UncheckedAccount`; we deserialize `Match` manually before the CPI — works (tested).
- **Public devnet RPC rate-limits (429)** — the delegate tx took 17 s. Use a dedicated RPC (Helius etc.) for the chain worker in M6.
- **Confirmations:** local ER intermittently drops websocket signature notifications → the chain worker must confirm by polling `getSignatureStatuses`; `GetCommitmentSignature` can take ~30 s even when the commit already landed — show "Solana ✓" from the base account state, fetch the commit sig asynchronously.
- **Config on the ER** is a cloned read-only copy; config changes on Solana may be stale on the ER (UNVERIFIED clone-refresh behaviour).
- **Ruleset hash** is compiled into the program; adding movement constants (M2) to the ruleset means a new ruleset version + program upgrade + config update (`update_config` not implemented yet).
- **`finalize_match` from `RoundResolved`** is an M0 shortcut — remove in M6.
- Missing security tests listed in docs/06 §9.

### Independent review of M0 (2026-11-18)
- Re-ran Rust deal parity (5/5) and TS tests (6/6) myself: green. Config hashes match the canonical JSON.
- Program reviewed against doc 06: signer checks, `event_seq`, target bounds and the scoped VRF callback are correct. Commits per match: 4 rounds + 1 finalize = 5 (within the limit).
- No keypairs in git history. Program is live on devnet (upgrade authority is your default wallet).
- ~~Still open: the devnet run.~~ Done: `docs/proof/M0-devnet.md`.

## Next milestones
- **M1:** Unity foundation + WebGL ↔ Linux server WebSocket spike. Prompt: `docs/prompts/M1-PROMPT.md`. **Blocked until Unity 6000.3.28f1 is installed.**

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
- [x] Create a devnet wallet and fund it.
- [ ] Start recruiting 8–12 playtesters for Nov 25 and Nov 26.

## Blockers
- Unity is not installed yet.

## Known bugs
- None yet, since no code has been written.

## Devnet proof signatures
- Program deploy: `zQzTyAr95iipfmHHaBc51TogPtXxkDe85A8E3k3Z8jebsZshE6Z2AiYvnKTsiXsmxLtxxNAXR6Hq1g8XNsSXmzf` (devnet).
- Lifecycle run: all 22 steps with signatures in `docs/proof/M0-devnet.md` (match `JCaMhk5jYaEoe1Su4nrNR9kP49g33BxKFcxC8FeFGBpy`; VRF callback `4iwqeoGX…`; round-1 commit on Solana `22iX4EiL…`; undelegate on Solana `2VLBsaMi…`).

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
