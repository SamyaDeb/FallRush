# Harden run: audit, debate, fixes and live deploy of the full on-chain game (2026-11-25)

Follow-up to [FULLCHAIN-RUN.md](FULLCHAIN-RUN.md). The user asked to "check each and every issue and edge case, make the game
more optimized for on chain using meaningful MagicBlock, multi agents, argue, report and fix". Branch `harden` (worktree
`../FallRush-fullchain`), merged into `main` at the end of this run; never pushed.

## 1. Audit and debate (multi-agent)
- 6 independent auditors (program, keeper, game server, client, live VPS read-only, MagicBlock research) → **66 findings**
  (15 high, 26 medium, 25 low).
- A **skeptic** re-checked each one against the code (31 real, 32 real-but-overrated, 1 not real, 2 unclear); a **red team**
  argued the other way and added **7 missed** edge cases (guest funder runway, judge sent to a busy arena, two tabs on one
  burner, deploys mid-match, …). A **planner** settled the disagreements into 19 ranked items with one program-upgrade batch
  and one WebGL batch.
- The ranked plan and every finding: `scratchpad audit.json` summarised here; the plan items map to the commits below.

## 2. What was fixed (12 commits on `harden`, each area implemented by one agent and attacked by a second)
| Area | Fixes | Commits |
|---|---|---|
| Program (devnet upgrade, same id) | an early champion (round 2/3) refunds the unplayable Qualify markets so the receipt + finalize can run (before: the match could never finalize and the next `open_markets` failed MarketBusy, wedging the arena); Qualify bets refused with < 2 alive; tick/mirror never overwrite an Aborted match; `round_phase` refreshed at start (the sealed Champion window no longer closes at Final start in hybrid mode); late inputs skipped instead of clobbering the input ring | b917ef7, d9ef960 |
| Keeper | balances + low-balance flags in `/status` and `/api/health`, treasury top-up before finalize; `ensureFinal` (stop arena → abort match, retried, never swallowed) on every failure path; MarketBusy recovery; `runFinal` try/finally (no leaked cranks/tasks/subscriptions); seasons auto-paused at MagicBlock's 10-commit cap (no 75 s wait after each match); market maker only seeds playable rounds; RPC request timeout (15 s); task rotation off the main loop (see §5) | fecf686, 72997b2, 5d31af6, 34fd51a, + this run's last commit |
| Indexer / web | judge never sent to an arena whose keeper is busy (409 + auto-retry); season board "paused"; versioned `/chain` bundle + wasm; gzip replays (~20×); meta copy says all 4 rounds | f6863b6, 7a5b16f, 5d31af6 |
| Browser bundle | late joiner spectates instead of hanging; dead raw socket reopens with backoff; session renewed with a 15-min margin; two tabs at roster lock: one plays, the other spectates; stop() cancels a pending join | f6863b6, 7a5b16f |
| Unity client (WebGL build) | Deal screen says "dealing on-chain · MagicBlock VRF"; no false "Session expired"; failed join → spectate/notice instead of a frozen screen; feed resets per round; stale join results dropped | 0f3d3c0, 46ff8ee |
| Ops | `infra/prod/deploy-gate.sh`: recreate a container only when both keepers are idle and both arena servers are in the lobby with 0 humans; watchdog tolerant during on-chain matches; load-test tool `tools/fullchain/load.ts` | fecf686, 1a68442, d0f2543 |

Wallets topped up from user-funded devnet SOL: game authority 1.01 → 4.01, guest funder 0.75 → 2.75, treasury 0.04 → 0.94
(`harden-balances.txt`).

## 3. Tests
- Unity EditMode 495/496 passed, 0 failed (`harden-builds.txt`); WebGL release 22.9 MB.
- Program: native 16 + 5, LiteSVM econ 8/8, p6 4/4, ops, parity, harden — the 5 new cases fail on the previous .so.
- Keeper / indexer / load unit tests 44/44; chain bundle 50/51 (the 1 failure also fails on main: emulator wasm missing).
- Judge match `Hxf8ae7y…` still replays byte-exact with the harden code.

## 4. Devnet and production
| when (UTC) | what | result |
|---|---|---|
| 08:24–08:47 | 3 full matches, dev arena 7, new keeper + Classic server + 2 bettors (`gate2/fc-gate2-20261125-135438`) | 3/3 at 1.000×, 5/5 markets settled each, payouts = hand calculation, replay 3/3 byte-exact |
| ~08:55 | **program upgrade** `4yehff…` f8929b19 → **ca83561b** (same id; previous .so kept for rollback) | dumped program = new build |
| 09:07 | 11-client load test (new program) | early champion at round 2 → markets settled + finalized (the upgrade's fix, live); keeper 1.000× real time; replay byte-exact |
| 09:14 | Hostinger snapshot (action 127830627); neighbours before = after (finity 200, conf md5 unchanged, Ovelo healthy) | `vps/neighbours.log` |
| 09:14–09:33 | first live match on the new program (a real player): settled + finalized normally; the deploy gate waited 19 min for that player | |
| 09:34–09:37 | **deployed**: keepers + indexer (`fallrush-node:hd`), web + new WebGL + nginx config, deploy gate + watchdog | all healthy |
| 09:39 | **incident**: the new "server abandoned" detector aborted a healthy judge canary match 31 s in (refunds + finalize worked). Cause: the Classic server records ABORT whenever a browser drops its game-server socket, while the player keeps playing on-chain (a real player's match at 09:15–09:23 had the same server ABORT and finished fine on-chain under the old keeper) | detector switched off (env at 09:42, code default off in 34fd51a, redeployed 09:45) |
| 09:47–09:53 | **production canary** (browser, `/play?judge=1`, arena 2): match `Ep74zGZRpuoeU3FGor9Ud35SP3oiWApqpgRpuYCJN2Uj` | joined for the whole match, all 4 maps, 0.998× real time, settle + receipt + capsule + finalize via Magic Action (1.6 s to Solana), replay byte-exact |

| 10:26–10:34 | 5-player load test with the final keeper (rotation off the loop), dev arena 7: match `23yjQhHv…` | 0.999× real time, 0 restarts, settle + receipt + capsule + finalize, replay byte-exact |
| 10:36–10:37 | keepers redeployed with the rotation fix (deploy gate) | healthy |
| 10:37–10:45 | **final production canary** (browser, judge, arena 2): match `DNXpj1VxtL2euef4REfs8Z9pqJ76FTF6EmS5HCGtrMEs` | joined the whole match, 1.000× real time, 0 restarts, finalize via Magic Action, capsule minted, replay byte-exact |

**Live WebGL client**: another session (dizzy2) deployed its own WebGL build at 09:54 UTC, after this run's 09:36 web deploy.
The live client is therefore the dizzy2 build (2e25593290d8); harden's Unity commits 0f3d3c0 + 46ff8ee (on-chain Deal
screen, honest session banner, join supervision → spectate) are merged in `main` e07c624 but **not live**: one WebGL build
from `main` is needed, blocked by disk (1.6–3.8 GB free, a build needs ~5 GB). Everything else of harden is live and works
with that client (the final canary above ran on it). The stale staged `web.hd` on the VPS was renamed `web.hd-stale-nodizzy`.

## 5. Found by the load tests
- **Task rotation blocked the keeper's loop**: rotating the 2 timed tasks awaited `schedule_ticks` with 20 tries × 15 s; when
  the rollup was slow to confirm, the loop stopped for 10+ min (no end-of-match check, no deadline). Longer matches (humans
  surviving → ~16k ticks instead of ~11k) rotate more often and were hit. Fix: rotation runs beside the loop with 3 tries,
  the stall path waits for it, cleanup awaits it.
- **Harness bug**: the load / gate scripts counted the previous run's resumed match as "done" and stopped the keeper during
  round 4 of the new match. Fixed (only non-resumed matches count). The keeper's resume of a leftover match worked every
  time (settle, receipt, capsule, finalize).
- **Many players from one machine**: with 11 headless players in one process on one IP, each got ~1 World update/s and 30%
  of inputs arrived after their tick (confirm p50 0.5 s, p95 2.6 s); with 5 players p50 0.26 s, p95 0.85 s. Real players are
  on separate devices; a real multi-device match is still the open measurement.
- **Crank gate** (send crank txs only while the World is behind): saved ~2.5% of crank sends — it reopens within 50 ms on
  every heavy tick. Left **off**; not worth it.

## 6. Rollback
- Live services: `bash scripts/hd-vps-deploy.sh rollback` (node `:pre-hd`, `web.prehd`, `nginx-web.conf.prehd`, watchdog
  `.prehd`). Full on-chain mode off: `FC_ONCHAIN_ALL_A2/_A3=0` (FULLCHAIN-RUN §7).
- Program: redeploy `programs/target/rollback/fall_rush.prev.so` (f8929b19) with `scripts/fc-devnet-upgrade.sh`.

## 7. Still open
- A real match with several humans on separate devices (input latency with many humans is unproven; the chain side held
  real time with 5–11 input streams).
- Phone + Phantom test (user step).
- Server-side: the Classic server's "no humans" abort during an on-chain match is only a log artefact now (the keeper ignores
  it); a Unity server build to skip it was deferred.
- MagicBlock ideas evaluated and deferred: Hydra cranks with a 1.4M CU limit instead of the keeper crank (gated spike, not
  run), commit-cap reset for the season accounts (needs undelegate/re-delegate), tick-debt cap (changes pacing).
