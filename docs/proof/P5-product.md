# P5 — product layer + public URL (proof)

Branch `p5` (worktree `../FallRush-p5`, off `main` @ `d155702`). Prompt `docs/prompts/P5-PROMPT.md` (autonomous run,
VPS authorised under the M3 safety rules). A number is **measured** only where this file says so; everything else is
**UNVERIFIED** (§10). Devnet only; RUSH has no real-money value (OC-2). The real IP / hostnames are `<VPS_IP>` /
`<VPS_IP-dashed>` here (the real URL is only in the git-ignored `docs/proof/p5/PUBLIC_URL.local.md`).

**Summary.** Fall Rush is public at `https://play.<VPS_IP-dashed>.sslip.io`. A stranger clicks **Play now** and is
seated in a game-server lobby in **18.0 / 18.3 / 20.9 s** (3 clean-profile runs, no popups; a silent guest wallet gets
gas, a session key and 1,000 RUSH chips meanwhile). Rounds 1–3 run on the Classic server, the Final on the MagicBlock
rollup; a phone spectator on `/live` watches the on-chain Final in 2D, bets and buys Chaos Drops with a session key.
After each match the program writes the receipt, commits it to Solana, and a **Magic Action** mints the capsules on
Solana in the undelegation transaction itself (validator-paid; 0 keeper mint calls in 7 of 7 devnet matches). `/replay`
re-simulates the archived Final through `rush_core.wasm` and matches it byte for byte. Six public hybrid matches
(two pairs concurrently on gs1 + gs2), payouts = hand calculation in all of them, kill tests recovered, neighbours
(finity, Ovelo) unchanged throughout. P5 spent **≈ 1.30 SOL** of the 2.0 budget (0.33 of it still held: gas funder 0.31, action escrow 0.02).

## 1. Carry-overs (task 0)
| item | result | evidence |
|---|---|---|
| 0.1 cash-out to real RUSH | **✓** fresh burner → faucet (session key) → `cashout` 250 RUSH on the rollup (304 ms) → commit on Solana 1.4 s → `mint_cashout` → **250.000000 RUSH SPL** in the burner's ATA → SPL transfer of 100 to a second wallet (balances 150 / 100). Public `/me` repeat: 10 RUSH, commit 1.3 s | `p5/p5-cashout-*.json`: cashout `WMNKz668…`, mint_cashout `4qnt6mkN…`, transfer `21VDTpgE…`; `p5/public/me/` |
| 0.2 Ephemeral SPL retry ⛳ | **5/5 round trips** (fresh mint + wallets per round; base `delegateSpl` → rollup sees 100 tokens (1.6–9.7 s) → ER `undelegateIx` → eATA back on Solana (2–25 s) → base `withdrawSplIx` → 100 tokens back). P4's 0/5 = its base withdraw tx sent to the rollup (`Custom 27` = delegation program `DelegationRecordInvalidAccountOwner`). **D-ESPL: chips stay `ChipBalance`** (OC-15): the vault-payout CPI + migration was not attempted (another upgrade + a bets/drops/payouts/faucet rewrite 3 days before the deadline) | `p5/espl-retry.json`, `tools/p5/espl-retry.ts` |
| 0.3 Magic Action ⛳ | **✓ primary path (D-ACTION, OC-16)**. `finalize_match_action` (program upgraded, same id) schedules commit + undelegate with one post-undelegate base action; escrow `7VLpUGHS…` (0.02 SOL). Devnet: the human's capsule minted in the **undelegation tx, fee payer = the validator `MAS1…`**: `ProcessUndelegation → MintRewardsAction → Core Create` (local rehearsal `4Y9JAE5o…`, public match 5 `4QbsE8AM…`); keeper `mint_rewards` attempts 0 in all 7 matches. Bytes = SDK bincode (unit test). The Crown path did not fire: a bot won every devnet Final (§10) | `tools/keeper/daemon.ts` (ACTION=1), `programs/fall_rush/src/{lib,econ,magic}.rs` |
| 0.4 burner safety | receipt card: "⚠ Guest wallet: your Crown / capsule lives only in this browser…" + **Back up your key** (→ `/me?backup=1`) + Claim with Phantom; `/me`: key export behind a confirm warning + copy, restore, claim to Phantom (Core `TransferV1` by the burner), move RUSH to Phantom | `EconomyHud.cs`, `web/app/src/pages/me.ts` |
| 0.5 keeper crash safety | program: `MATCH_ECON` flag; `write_receipt` needs every market **PAID**; finalize needs the receipt (LiteSVM: an unpaid winner blocks the receipt, 5/5 econ tests). Keeper daemon resumes from on-chain state. **Kill test: `fallrush-keeper-1` restarted mid-Final (public match 4) → recovered in 74 s** (resume → re-schedule the Final task → settle → receipt → capsules → finalize → rewards; payouts OK, replay ✓) | `p5/public/kill.log` |
| 0.6 small fixes | rcselftest file count (in the P5 builds); duplicated §11 removed from the P4 proof (final doc commit); **Deal screen**: each capsule shows its market (Qualify R1–R3 / Champion) pool + favourite odds | `MatchHud.cs` / `EconomyHud.cs` |
| 0.7 faucet abuse | `/api/faucet-gate`: per-IP soft limit (20 / day), signs nothing; on-chain the faucet stays per wallet (1,000 RUSH / 24 h). Plus guest gas `/api/gas` (OC-18): 0.012 SOL once per burner, ≤ 20 per IP per day, ≤ 60 per hour | `tools/indexer/server.ts` |

## 2. Builds (task 1)
| build | result (measured, `p5/builds.txt`) |
|---|---|
| EditMode tests | **418 / 418** passed |
| Linux server | `Build.Server` (Dedicated Server subtarget) fails to compile (P3 finding, G4 editor tools use the client assembly) → **`Build.LinuxDevServer`** (the Mac dev server recipe on Linux x86_64) 157 MB; runs on the VPS (`[gs] listening`, 30 Hz) |
| Mac dev server | built from this branch (61 s) |
| WebGL release | **22,480,859 B** (≤ 25 MB) with Decompression Fallback (`.unityweb`); 22.39 MB without |
| rush-core | view/replay exports added (`rc_lids`, `rc_sweepers`, `rc_drops_queued`, `rc_ingest_drop`; no simulation change, core v2): 6/6 vector files OK native = WASM |

**Local hybrid rehearsal** (`scripts/p5-local-hybrid.sh`, fresh Mac dev server + keeper daemon + web app + WebGL in
Chrome): found 3 bugs before the VPS (the keeper took the rollup's clone of an undelegated Match for a delegated one;
the WebGL pre-join's switch timer started at roster lock; the bundle's arena-id lookup), then a complete hybrid match:
rounds 1–3 bridged, the server handed the Final to arena 3, Final real time 0.9997, Magic Action mint. **The P4 §8.2
stale-server bug is gone** (the server reported `on-chain Final live` and did not play its own Final).

## 3. Arena pool + matchmaking (task 2)
- **Pool = arenas 2 + 3** (arena 3 set up by `tools/p5/setup-arena.ts`, reusing arena 2's sealed map accounts;
  markets + drop queue delegated); one game server + one keeper per arena; bundle configs per arena
  (`devnet-<arena>.json`, chosen from the hand-off).
- **Quick Play** (`/play`): the fullest open server from `/api/status` (`?server=` + `?quickplay=1`: the menu joins
  without a second click); the lobby countdown starts at ≥ 1 human, bots fill at lock.
- **Directory**: each game server writes `status.json` (players, phase, round, match id) into its log volume; its
  keeper publishes it with its own stage at `/status`; the indexer merges both at `/api/status`.
- **Keeper daemon** (`tools/keeper/daemon.ts`): tails the server's jsonl, every stage idempotent against on-chain state
  (create / delegate / lobby / markets / VRF deal / rounds 1–3 / start_final / timed task + stall restarts / settle /
  receipt / capsule VRF / finalize (+ action) / mint retry), Classic `ABORT` → abort on-chain (refunds), archive.

## 4. Web app (task 3; `web/app`, Vite + TS, 37 KB JS)
`/` landing (live arenas, recent matches, MagicBlock strip with explorer links) · `/play` (Unity, parallel download,
guest wallet, Quick Play) · `/live/:arena` (2D top-down Final from the `WorldAcc` subscription decoded by
`rush_core.wasm`: lids idle / lit / open / retired, the sweeper bar, players (humans ringed); live odds per market;
Bet + Chaos Drop buttons mirroring the program's windows; feed; chain chip) · `/replay/:match` · `/receipt/:match`
(Rollup ✓ → Solana ✓, ● / ◆ trust, placements, markets, drops, assets, hashes) · `/me`. Screenshots (phone 390 × 844):
`p5/public/match-5/live/shots/` (live Final, bet, drop), `p5/public/match-1/replay/shots/`, `…/receipt/shots/`,
`p5/public/me/shots/`.

## 5. Cosmetics (task 4)
`/me` lists the wallet's Fall Rush capsules (`getProgramAccounts` on Core, owner + update-authority memcmp) and equips
one (localStorage); `/play` passes `?shell=&wallet=`; the hello carries them; the server checks the Core asset over
RPC (owner, update authority = the program's `assets` PDA, `shell` attribute) before applying the shell (OC-19;
`ShellCheckTests`: owner / authority / missing / unknown shell refused). Equipping a real minted capsule end to end in
a public match: UNVERIFIED (§10).

## 6. Indexer (task 5)
`tools/indexer/server.ts` (container `fallrush-indexer`): the keepers archive `world2.bin` (every World snapshot + the
80-byte header with the latched inputs), the Classic jsonl, the keeper log and `summary.json` (signatures, markets with
every bet, drops, receipt, assets) in `/opt/fallrush/archive/<match>/`; the indexer adds `txs.json` (market / drop
signatures read from the rollup), serves `/api/matches`, `/api/match/:id`, `/api/replay/:id/<file>`, keeps 200.

**Replay check** (`/replay`): snapshot T + the inputs the program latched for T+1 (+ any DropQueue purchase entering the
World, `rc_ingest_drop`) → byte-equal to snapshot T+1; plus the receipt's Final transcript in the final World.

## 7. VPS (task 6; `infra/prod/`, doc 13 §2a)
- Snapshot **472001** (2026-11-23 10:12 UTC, expires +24 h) before the first change.
- Services: fallrush-web :3110, gs1 :7777, gs2 :7778, keeper-1 :3111, keeper-2 :3113, indexer :3112 (all 127.0.0.1;
  caps 0.75 / 1 GB per server, 0.25 / 512 MB keepers + indexer, 0.25 / 256 MB web); `restart: unless-stopped`, health
  checks, log rotation. nginx: `sites-available/fallrush-p5.conf` enabled as `zz-fallrush-p5.conf` (loads after finity:
  finity stays the default server); certbot for play / gs1 / gs2; `nginx -t` exit 0 before every reload.
- **Neighbour checks** (`p5/vps/neighbours.log`, 7 points: baseline, after nginx, after certbot, after the first
  containers, after the keeper redeploy, after gs2, final): finity https 200 / http 301 every time, finity config md5
  `c6a6cca8…` unchanged, raw-IP http 404 / https 200 unchanged, Ovelo `Up (healthy)` throughout. Nothing rolled back.
- **D-HOST (OC-20): 2 servers kept.** Two concurrent matches: game servers CPU p50 ≈ 5 %, max ≈ 9 %, ≤ 140 MiB; tick
  work p95 median 1.1 ms / worst 3.5 ms (≤ 20 ms), 30 Hz; keepers ≤ 16 %; load1 ≤ 1.41; finity https p50 0.362 s.
- Found on the way: the `fallrush-web` container cached the indexer's IP (502 after a redeploy: Docker DNS resolver
  added); a VPS `apt-get` inside `docker build` hung (image based on the M3 image instead).

## 8. Public verification (task 7)
**Stranger → lobby** (clean profile, landing → click Play now → seated, `p5/public/lobby-*`): **18.0 s, 18.3 s, 20.9 s**,
0 popups (download 3.0–3.8 s, Unity ready 10–14 s). Earlier attempts while this Mac's uplink was corrupting packets
(sshd `message authentication code incorrect`): 57 s (single-stream download, before the parallel loader), 40.8 s,
12.3 s, 2 timeouts.

| # | match (Solana) | server | spectator (phone) | payouts = hand calc | rewards | replay = on-chain | receipt |
|---|---|---|---|---|---|---|---|
| 1 | [EwaCEPNC…](https://explorer.solana.com/address/EwaCEPNC9ts7RgaXrtTdNRX1LTZSR8HidYJDaX6vB8h6?cluster=devnet) | gs1 | blocked (guest-gas IP limit) | ✓ 5/5 | capsule by the action | ✓ 3582/3582 | ✓ |
| 2 | [6E4qWTB1…](https://explorer.solana.com/address/6E4qWTB1BzndsDb6SCLZTm6yBdVtiy54rakm9mWMGgRK?cluster=devnet) | gs1 ∥ 3 | blocked (bundle arena-id bug) | ✓ 5/5 | action | ✓ | ✓ |
| 3 | [HTvuAQ5Y…](https://explorer.solana.com/address/HTvuAQ5YXVivH3ae3tAVqMctBfEZFKzfUcra7ugNdijS?cluster=devnet) | gs2 ∥ 2 | — | ✓ 5/5 | action | ✓ | ✓ |
| 4 | [6VAvjafw…](https://explorer.solana.com/address/6VAvjafwE847cAUX3xGZnn2jAsyg7rn2u4BV8t3EYkx3?cluster=devnet) | gs1 + keeper kill | **bet ✓ (session key), Lid Frenzy ✓ ×2** (duplicate: fixed) | ✓ 5/5 | action (after resume) | ✓ 1836/1836, 2 drops | ✓ |
| 5 | [J8bNNian…](https://explorer.solana.com/address/J8bNNian5HfPf6dvA5xuDLdFfnHYXyzWAVjo89XZWSCx?cluster=devnet) | gs1 ∥ 6 | **bet ✓, Lid Frenzy ✓** | ✓ 5/5 | action `4QbsE8AM…` | ✓ | ✓ |
| 6 | [3MHyMVjc…](https://explorer.solana.com/address/3MHyMVjcjNv3JFjVbgFKGAeKSUxMpwAVntdNXY1XFpJT?cluster=devnet) | gs2 ∥ 5 | **bet ✓, Lid Frenzy ✓ on-chain** (UI: "unconfirmed", no resend) | ✓ 5/5 | action | ✓ | ✓ |

All six: the Classic → on-chain switch happened (client `[onchain] switch`), Final real time 0.9997–1.0003, rollup
CU max 196k–263k. **Kill tests**: keeper-1 mid-Final → recovered in 74 s; **gs1 restarted between matches → back in
8 s, the connected client reconnected with its token and was seated again after 10 s**. `/me` cash-out ✓ and Phantom
connect (stand-in wallet, D-PHANTOM) ✓. `scripts/p5-health.sh` (final run `p5/vps/health-final.txt`): 6/6 services
healthy, gs1 / gs2 WebSocket 101, finity 200, Ovelo up.

Bugs found by the public runs and fixed: the guest-gas per-IP limit (4 → 20 per day); the bundle's arena-id lookup
(every economy call failed on `/live` and in-game); a confirmation timeout made the bundle resend a bet / drop (a
double Lid Frenzy in match 4) → never resend after a timeout; the replay did not apply Chaos Drops entering the World;
the Classic server's `ABORT` (all humans left) stalled the keeper's chain worker.

## 9. SOL
| item | SOL |
|---|---|
| start (P4 end) | admin 5.2618 + game authority 3.0658 = 8.3276 |
| program upgrade (extend +30,464 B, fees; buffer moved authority → admin and back) | 0.19 |
| cash-out proof + ESPL retry (5 mints, 5 funded wallets) | ≈ 0.13 |
| Magic Action escrow (held, reusable) | 0.02 |
| arena 3 setup (arena, world, event log, 12 inputs, 5 markets, drop queue, delegations) | ≈ 0.12 |
| guest-gas funder (0.30 + 0.25 top-up; 0.31 still held) | 0.55 |
| keeper matches (≈ 12 devnet matches incl. rehearsals: receipt rent, fees, capsule rent, market maker) | ≈ 0.24 |
| **end** | admin **4.4083** + authority **2.6225** (+ funder 0.31, escrow 0.02): **P5 ≈ 1.30 SOL incl. 0.33 still held**; admin ≥ 1.5 ✓ |

## 10. UNVERIFIED
- **Crown Capsule via the Magic Action**: a bot won every devnet Final in P5; the Crown is the same handler path
  (`mint_one(0)`, LiteSVM-tested) but did not run on devnet through the action.
- The Ephemeral SPL vault-payout CPI on the rollup and the chips migration (D-ESPL keeps OC-12).
- Real Phantom (extension) connect / claim / move RUSH: driven with a stand-in `window.solana` (D-PHANTOM). Manual
  checklist: install Phantom (devnet), open `/me`, Connect Phantom, Claim a capsule, Move RUSH; check both on the
  explorer.
- A real phone (only a 390 × 844 mobile-emulated Chrome); a real stranger on another network (the 18–21 s figures are
  from this Mac; the parallel loader needs ≥ ~1 MB/s for 30 s).
- Wearing an equipped capsule shell in a public match (server check unit-tested only).
- Keeper kills at other stages (settle / receipt / finalize) on devnet: the resume path is the same `finish()`; only
  mid-Final was killed.
- 24 h soak: the stack is left running; `scripts/p5-health.sh` for P6 / P7.
- Two humans in one public match; more than 3 humans (the action mints ≤ 4 assets, the keeper the rest).

## For P6 / P7
- URLs: `https://play.<VPS_IP-dashed>.sslip.io` (+ `gs1.` / `gs2.`); the real one in `docs/proof/p5/PUBLIC_URL.local.md`
  (git-ignored) and the P5 report.
- Health: `bash scripts/p5-health.sh`; redeploy: `bash scripts/p5-deploy.sh [web|node|gs|all] --gs2`, small web
  changes `scripts/p5-deploy-quick.sh`; kill tests `scripts/p5-kill-test.sh`; neighbours `scripts/p5-neighbours.sh`.
  All need `~/.config/fall-rush/vps.env` (VPS_IP, SSH_KEY, FINITY_URL).
- Playtests need: the gas funder topped up (≈ 0.012 SOL per new guest; 0.31 SOL now ≈ 25 guests), humans on 2+
  devices, a fresh Hostinger snapshot before any VPS change (472001 expires 2026-11-24 10:12 UTC).
