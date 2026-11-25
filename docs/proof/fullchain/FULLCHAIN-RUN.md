# Full game on-chain: run record and evidence (2026-11-24 → 2026-11-25)

**Result: all 4 rounds of Fall Rush are computed by the `fall_rush` program in a MagicBlock Ephemeral Rollup, live on
https://fallrush.fun** (both on-chain arenas; gs3 stays a Classic server). Decision: doc 15 **OC-60** (supersedes OC-11).
Branch `fullchain` (worktree `../FallRush-fullchain`), merged into `main` by this run; never pushed.

## 1. What changed
| Part | Change | Files |
|---|---|---|
| rush-core | World **v2** for `start_match` worlds: `step_slice` skips slots that are out / done (no bot brain, no base work); v1 (the hybrid Final, archives) unchanged | `crates/rush-core/src/world.rs`, `rules/director.rs` |
| Program | `crank(budget)` = `tick()` with a slice of 1000–6000 work units (for 1.4M-CU keeper txs); `chain_final` / `chain_all`; Champion window open through on-chain R1–R3; Qualify markets of full matches labelled ◆ PROGRAM; receipt trust `ALL_PROGRAM` (4) | `programs/fall_rush/src/{sim,lib,econ,p6}.rs` |
| Keeper | full mode (`ONCHAIN_ALL` + server header `onchainAll`) → `start_sim`, 2 rotating timed tasks + the adaptive crank (`crank.ts`, in-flight cap 64), `commit_round` per round, abort on server fallback | `tools/keeper/{daemon,crank,run}.ts` |
| Game server | `GAMESERVER_ONCHAIN_ALL`: hands the whole match to the arena at roster lock; per-match Classic fallback after 60 s; stale-read fix + 3-read debounce | `ChainFinalHandoff.cs`, `ServerMatch.cs`, `ChainFinalRules.cs` |
| Client | whole-match on-chain session (per-round scene + prediction map), director-driven feed, 12-slot join, next-match rejoin, no false "Session expired" | `OnChainSession.cs`, `OnChainMatchFeed.cs`, `OnChainSwitch.cs`, `ChainDirector.cs`, `FallRushChain.jslib`, `packages/chain/src/rollup.ts` |
| Web / replay | chain-exact wasm replay across map loads (`rc_step_chain`, `rc_chain_map/load`), `/live` `/receipt` `/replay` labels, indexer `allOnChain`, judge card | `crates/rush-core-wasm`, `web/app`, `tools/p7/replay-check.mjs`, `tools/indexer` |
| Infra | images `:fc`, keepers `cpus 1.0`, kill switches `FC_ONCHAIN_ALL_A2/_A3` | `infra/prod/docker-compose.yml`, `scripts/fc-vps-deploy.sh` |

## 2. Research (before any change) — `RESEARCH.md`
- Devnet, old program, 1 task: full match 0.72×; Capsule Run 0.30× (32 task deaths). 2 tasks: 0.93×, Capsule Run 0.36×.
- Capsule Run cannot fit the 400k task: one player's step up to **866k CU** (LiteSVM). Slice size never changes the result.
- Keeper crank from one sender: **~108 tx/s** landed at 1.4M CU.
- Idle slots still ran bot brains: skipping them took R3 3.67 → 2.0 and the Final 2.1 → 1.0 slices per tick.

## 3. Program upgrades (devnet, same program id `4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4`) — `sol.md`
| when (UTC) | .so sha256 | why | cost |
|---|---|---|---|
| 2026-11-24 23:59:58 | `411b61fa…` | crank, World v2, full-match economy | 0.0063 SOL (buffer rent refunded) |
| 2026-11-25 01:31:24 | `f8929b19…` | Qualify markets of full matches labelled PROGRAM | 0.0063 SOL |
Before each: sBPF suites econ 5/5, ops, parity, p6 (LiteSVM). After the first: hybrid regression on dev arena 7 (`fc-hybrid-regress`): 1.000×, 0 restarts, 5 markets settled. Rollback: `~/.config/fall-rush/rollback/fall_rush-live-p6-f7fcc5f0.so` (the program before, byte-identical to the P6 build).

## 4. Gate 1 — PASSED (3 full matches, dev arena 7, `docs/proof/p2/keeper-fc-gate1.jsonl`)
| match | R1 Capsule Run | R2 Goo Tower | R3 Star Fountain | R4 Final | restarts |
|---|---|---|---|---|---|
| `7w8Am8A4…VRfj` | 1.00× | 1.00× | 0.99× | 1.00× | 0 |
| `86pL554E…w4zD` | 1.00× | 1.00× | 1.00× | 1.00× | 0 |
| `2cDu1tuj…1cJD` | 1.00× | 1.00× | 1.00× | 1.00× | 3 |
Replay of match 1: byte-exact.

## 5. Gate 2 — PASSED (real flow: Classic server in full mode + production keeper daemon + bettors) — `gate2/`
- Run 1 (`fc-gate2-20261125-062211`): 4/5 full (1.000× each) + 1 server fallback (keeper aborted + refunded as designed) caused by a stale read in the server → fixed.
- Run 2 (`fc-gate2-20261125-070134`): **5/5 full**, real time 1.000/1.000/0.998/1.000/1.000, 5/5 markets settled + receipt + finalize each, **replay = on-chain ✓ 5/5**.
- Run 3 (`fc-gate2-20261125-074155`, payouts): **3/3 full**, 1.000/1.001/1.000, both bettors' chip changes = the pari-mutuel hand calculation in every match, Qualify labelled PROGRAM, replay ✓ 3/3 (`gate2-bets.json`).

## 6. Production (fallrush.fun) — canary on arena 3, then both arenas
- Deploy 2026-11-25 02:43–02:45 UTC under the M3 rules: Hostinger snapshot (action 127763468, success 02:40:23), neighbours before/after (finity 200, nginx conf md5 unchanged, Ovelo healthy — `vps/neighbours.log`), rollback tags `:pre-fc`, `web.prefc`, `.env.prefc`; live arenas 2 + 3 pointed at the current Goo Tower blob `47v2Vx…` (`set-goo.ts`).
- Canary findings, each fixed and redeployed: keepers capped at 0.25 CPU starved the crank (0.2× real time) → 1.0 CPU (resumed at 38 ticks/s, then real time); a player knocked out saw "Session expired" (false); the second match of a page ran the player as a bot (jslib boot state + bundle join cache); the chain bundle was cached 4 h by the CDN (`?v=<build GUID>`).
- After the fixes: a browser player joined **two consecutive full matches** on arena 3 (`joined 1` / `joined 1`), moved with the keyboard on Capsule Run (inputs on-chain), saw the per-round maps and the on-chain result ("Match … is back on Solana (committed)").
- **Judge match `Hxf8ae7ymYhyNcjEhKuah8y1FHfV4fkVYkk4VqnqAYDn`** (arena 2, `/play?judge=1`, browser player): full on-chain, ~6 min, settled, receipt, finalize + Magic Action; **replay 21,540 / 21,540 byte-exact across 4 map loads** (`node tools/p7/replay-check.mjs Hxf8ae7y…`). PROOF.md is generated from it (every link fetched live).

## 7. Kill switch and rollback
- Back to the hybrid flow (Classic rounds 1–3 + on-chain Final): on the VPS `sed -i 's/^FC_ONCHAIN_ALL_A2=1/FC_ONCHAIN_ALL_A2=0/; s/^FC_ONCHAIN_ALL_A3=1/FC_ONCHAIN_ALL_A3=0/' /opt/fallrush/.env && docker compose up -d --no-deps fallrush-gs1 fallrush-gs2 fallrush-keeper-1 fallrush-keeper-2`. The program serves both flows.
- Full rollback of images + web: `bash scripts/fc-vps-deploy.sh rollback`. Program rollback: `SO=~/.config/fall-rush/rollback/fall_rush-live-p6-f7fcc5f0.so bash scripts/fc-devnet-upgrade.sh`.

## 8. Open / not verified
- No match with several real humans at once yet (12-player browser prediction cost with many humans unmeasured; 1 human measured fine).
- Chaos Drops in a full match not exercised on production (unchanged rule: Final only).
- Sealed bets in full matches: the close / finish ran (judge match); a sealed reveal was not exercised.
- A phone + Phantom test (user step).
