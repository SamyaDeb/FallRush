# P6 (full): privacy (sealed bets) + seasons + achievements, with the P5 carry-overs

> **Autonomous run (user, 2026-11-23):** make every engineering decision yourself, choosing the best option on the evidence, and implement P6 **fully, end to end, without stopping to ask**.
> - At each ⛳ apply §3 "Decision policy".
> - Record each decision as a new `OC-n` row in `docs/15` (next free number is **OC-21**) and in the proof, with the options, the numbers, the choice, why, and how to revert.
> - **The user extended the autonomous VPS authorisation to P6 (and P7) under the M3 safety rules (§1.3).** Follow them exactly; they are not questions.

You are the full-stack / Solana / Unity / DevOps engineer on **Fall Rush**, a Unity 6.3 WebGL 12-player knockout party game for the MagicBlock Solana Blitz V10 hackathon (theme **Entertainment Finance**). The deadline is **Fri 27 Nov 14:00 UTC; the internal target is 12:00 UTC**. **P7 (playtests, video, README, submission) needs ~1.5 days, so P6 must be merged by Wed 25 Nov 06:00 UTC at the latest.** If you are running late, cut scope in the order given in §3 (D-LATE), never quality.

P6 adds three things on top of the live product:
1. **Sealed bets** — bets nobody can read until the market closes, using a **MagicBlock Private Ephemeral Rollup (TEE)** if it works, else commit-reveal on the normal rollup.
2. **Seasons** — a weekly `Season` board (champions, predictors, drop MVPs) built from match receipts, and a **season trophy** Core asset minted at season end.
3. **Achievements** on the receipt and `/me` — first crown, 3-round survivor, perfect predictor.

**What works today** (`main` @ `eda9adb`, all of P0–P5 merged; checked 2026-11-23 ~14:00 UTC):
- **Public URL** live on the shared VPS (`https://play.<VPS_IP-dashed>.sslip.io`; the real one is in the git-ignored `../FallRush-p5/docs/proof/p5/PUBLIC_URL.local.md`). `/`, `/play`, `/api/status`, `/api/matches` answer 200 in 0.2–0.35 s; gs1 / gs2 up; both keepers `alive`.
- Stranger → lobby in 18–21 s, no popups (burner + session key + guest gas + 1,000 RUSH chips).
- Hybrid match: rounds 1–3 on the Classic server, **the Final (Lid Flip) on the MagicBlock rollup**, 2 arenas (2, 3), one keeper daemon each (resumes from on-chain state).
- Economy: RUSH chips (`ChipBalance`, OC-12/OC-15), Rush Markets (◆ / ●, pari-mutuel, live odds on `/live`), Chaos Drops, receipt committed to Solana, **capsules minted by a Magic Action in the undelegation tx** (OC-16; independently re-checked: Core asset `2gjaZepp…` created by tx `4QbsE8AM…`, fee payer = validator `MAS1…`, logs `ProcessUndelegation → MintRewardsAction → Create`), cash-out to real RUSH SPL, `/replay` byte-exact, `/receipt`, `/me` (backup, restore, Phantom claim).

**Read first, in full:**
1. `docs/19-ONCHAIN-PLAN.md`: §1, §5 **P6** / P7, §7 video, §9.
2. `docs/15-DECISIONS-AND-RISKS.md`: OC-1…OC-20, D-5 (VPS), P0-1.
3. `docs/proof/P5-product.md` (all, especially §1, §7, §8, **§10 UNVERIFIED**, **"For P6 / P7"**), `docs/proof/P4-economy.md` (§3 markets, §5 receipt / mint), `docs/13-DEPLOYMENT.md` §2a (as built).
4. Code: `programs/fall_rush/src/{lib.rs,econ.rs,state.rs,magic.rs,sim.rs}`, `tools/keeper/{daemon.ts,econ.ts,lib.ts}`, `tools/indexer/server.ts`, `packages/chain/src/*` (`econ.ts`), `web/app/src/**` (`pages/live.ts`, `pages/receipt.ts`, `pages/me.ts`), `client-unity/Assets/_Game/Scripts/{OnChain,Client}/**` (`EconomyHud.cs`, `MatchHud.cs`), `infra/prod/*`, `scripts/p5-*.sh`.
5. MagicBlock docs (docs.magicblock.gg): **Private Ephemeral Rollups / TEE** (endpoint `devnet-tee.magicblock.app`, `EphemeralPermission`, `access-control`, `verifyTeeRpcIntegrity`, `getAuthToken`), Magic Actions (multiple base actions per undelegation), and the SDK 0.18 release notes. Check what actually exists on devnet today; don't trust the plan's names blindly.

---

## 0. State at hand-off (2026-11-23 ~14:00 UTC, checked)
- **Worktrees:** `main` @ `eda9adb`; `../FallRush-p5` (branch `p5`, merged; **keep it**, it holds the git-ignored `PUBLIC_URL.local.md`); `../FallRush-trailer` (**not ours, never touch**).
- **Program** `4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4` (upgraded in P5, `.so` 1,136,936 B data length), core version **0x10002**. RUSH mint `3s8KstqicXn1kgFyWPVq3AZXkMprDtEtT7BNTxtQccEw`. Magic Action escrow `7VLpUGHS…` (0.02 SOL).
- **Wallets:** admin `~/.config/solana/id.json` **4.41 SOL**; game authority **2.62 SOL**; guest-gas funder `tools/keeper/.keys/devnet-funder.json` **≈ 0.31 SOL** (≈ 25 new guests at 0.012 each).
- **VPS:** stack `/opt/fallrush` (compose project `fallrush`, `infra/prod/`): web :3110, gs1 :7777 (arena 2), gs2 :7778 (arena 3), keeper-1 :3111, keeper-2 :3113, indexer :3112; nginx `sites-available/fallrush-p5.conf` → `sites-enabled/zz-fallrush-p5.conf`; certs play / gs1 / gs2. Access: `~/.config/fall-rush/vps.env` (VPS_IP, SSH_KEY, FINITY_URL) used by `scripts/p5-*.sh`. **Snapshot 472001 expires 2026-11-24 10:12 UTC: take a fresh one before your first VPS change.**
- **Disk: ~11 GB free** on this Mac (95 %). See §1.1.

### 0.1 P5 carry-overs (do these first, §2 task 0)
1. **ProjectSettings drift (left uncommitted in `../FallRush-p5`):** `ProjectSettings.asset` `webGLDecompressionFallback: 0 → 1` and `MultiplayerManager.asset` `m_EnableMultiplayerRoles: 0 → 1`. `ProjectSetup.cs:96` sets the fallback in code, so builds are right, but `main`'s asset disagrees.
   - Commit the fallback = 1 value on `p6` (it is what the shipped 22.48 MB build used, and the parallel loader depends on it).
   - For `m_EnableMultiplayerRoles`: find what set it (the Linux dev server build?), keep it only if a build needs it, otherwise leave it at 0. Write down which.
   - Don't edit `../FallRush-p5`; just reproduce the right values on `p6`.
2. **Aborted bot-only matches pollute the lists:** the two latest entries on `/api/matches` (`6wGmze3K…` arena 2, `Gi65GcAP…` arena 3) have `humanMask 0`, `champion 255`, `finalOnChain false`, `mintedMask 0`, pools 45–47 RUSH (the market maker's seed).
   - Verify on devnet that those matches were aborted cleanly and **every stake was refunded** (market `refund` flag, maker balance). If not, fix the keeper's abort path.
   - Hide matches with no human and no Final from the landing page's "recent matches" and the default `/api/matches` (add `?all=1` to include them); label them "aborted" on `/receipt`.
   - Ideally don't open markets (or seed the maker) until a human is seated; decide (D-OTHER).
3. **Crown via the Magic Action is UNVERIFIED** (a bot won every devnet Final in P5). ⛳ D-CROWN: prove it on devnet in P6. Preferred: a scripted "human" client whose inputs come from the existing bot brain (a headless WebGL / dev-client run with `humanMask` set, the same input path a human uses, no gameplay change). Repeat matches until that slot wins the Final (bots win 1 in 3 among 3 finalists), then check the Crown Core asset was created in the undelegation tx with fee payer = the validator, and **0 keeper mint calls**. Budget ≤ 8 matches; if it never wins, record that and keep LiteSVM as the proof.
4. **Two humans in one public match** (UNVERIFIED): run one with two clean Chrome profiles on gs1 (both seated, both get a Final hand-off or a spectator hand-off if knocked out, receipts list both, the action mints for both if both qualify). Also check what happens with > 4 humans' assets (the action mints ≤ 4; the keeper must mint the rest).
5. **Keeper kills at other stages** (UNVERIFIED in P5): run `scripts/p5-kill-test.sh` at settle, receipt, and finalize once each on devnet; record the recovery time. (Mid-Final already passed: 74 s.)
6. **Gas funder top-up for the P7 playtest:** move SOL into the funder so it covers ≥ 60 new guests (≈ 0.75 SOL total). Log it in `docs/proof/p6/sol.md`.
7. **Health after the unattended run:** at the start, run `scripts/p5-health.sh` and record how the stack did since P5 ended (container restarts, memory, disk on the VPS, log sizes, indexer archive count). Fix anything that's growing without bound (log rotation, archive prune, keeper memory).

---

## 1. Rules

### 1.1 Local machine (8 GB, ~11 GB disk free)
- **Worktree:** `git worktree add ../FallRush-p6 -b p6 main`. Unity: `cp -cR /Users/samya/Downloads/FallRush/client-unity/Library ../FallRush-p6/client-unity/Library` (APFS clone).
- **Disk hygiene:**
  - keep ≥ 5 GB free at all times, checked before every build;
  - delete your own intermediate build folders after use; keep at most **one** saved WebGL build per phase;
  - never delete other projects' files, Docker volumes, `../FallRush-trailer`, the source / proof of `../FallRush-p5`, or anything under `~/Library/Application Support`.
  - If space runs short, free it **only** from, in this order: your worktree's build output; **`../FallRush-p5`'s build output only** (`programs/target`, `crates/target`, `client-unity/Builds`, ≈ 3 GB; P5 is merged); `~/Library/Caches/ms-playwright*`; `npm cache clean --force`; `pnpm store prune`; Solana platform-tools versions other than the one `cargo build-sbf --version` reports. Write down what you freed.
- **One heavy job at a time:** `bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p6-<what> -- <command>` (Unity, builds, the local stack, devnet runs, Chrome, Docker builds). Use `CARGO_BUILD_JOBS=2 nice -n 10` for tests. Other projects (e.g. `vyce`) may be running their own tests; the machine is shared.
- **The Linux server image is x86_64**: build the Linux player with Unity locally (`Build.LinuxDevServer`), build / load the image **on the VPS** (as in P5; `apt-get` inside `docker build` hangs there, base the image on the existing one).

### 1.2 Code and chain
- **Determinism:** P6 should need **no `rush-core` change**. If one is unavoidable, regenerate vectors with parity on native / WASM / sBPF / FFI and bump `VERSION`.
- **The hot tick never fails.** No settlement, season, or mint work inside `tick()`. The VRF callback identity stays a `const` item. Never import `sol_remaining_compute_units`. Every new instruction on the rollup stays well under 200k CU (measure).
- **Program upgrades:** allowed (same program ID only; extend as needed). Write down the size and SOL. **Account layout changes must stay compatible** with the live `Market` / `Match` / `Receipt` accounts (add new account types or use reserved / pad space; never reinterpret existing fields), or migrate explicitly and record it.
- **Devnet SOL budget for P6: 1.5 SOL** across all wallets, including upgrades, the funder top-up (§0.1.6), test wallets and matches. **Keep ≥ 1.5 SOL in the admin wallet** for P7. Log every spend in `docs/proof/p6/sol.md`.
- Keypairs never in git. Secrets on the VPS only in `/opt/fallrush/.env` (root, `chmod 600`) or a mounted secret file with the same permissions.
- Pin every new npm / crate version exactly. **No Next.js** (OC-8). No new Unity packages.
- Commit on `p6` with your own paths: `git commit -- <paths>`. End every commit message with the attribution lines from the system reminder. A number counts as measured only if you ran it. Anything else is **UNVERIFIED**.

### 1.3 VPS rules (the M3 safety rules, mandatory on every VPS change)
1. **Take a Hostinger snapshot right before the first change of each session** (`hostinger` CLI; snapshots expire after 24 h). Write down its id. If your session runs past its expiry, take another before the next change.
2. **Only `fallrush-*` names:** containers, images, volumes, networks, `/opt/fallrush/**`, and nginx files `/etc/nginx/sites-available/fallrush-*.conf` (+ symlinks). **Never edit, restart or remove anything else:** the `finity` site, its config, Ovelo, cloudflared, other containers.
3. **Every Fall Rush port on `127.0.0.1`**, avoiding `8787` and `3100` (finity). Keep the P5 caps (game server 0.75 CPU / 1 GB; keeper 0.25 / 512 MB; web 0.25 / 256 MB; indexer 0.25 / 512 MB). A new service (e.g. a TEE relay) gets ≤ 0.25 / 256 MB and the next free port (3114+).
4. **nginx:** edit only `fallrush-p5.conf` (or add a `fallrush-*.conf`). Run `nginx -t` and **check its exit status** before every reload. **Reload, never restart.**
5. **TLS:** `certbot --nginx -d <name>` only for a new sslip.io name of ours. Never touch other certificates.
6. **No host-wide prune** (`docker system prune`, `docker image prune -a`, `docker volume prune`). Remove only your own `fallrush-*` items by name.
7. **Before and after every change, verify the neighbours** with `scripts/p5-neighbours.sh` (finity https status + config md5, raw-IP behaviour, Ovelo containers `Up`) and append to `docs/proof/p6/vps/neighbours.log`. If either breaks: **roll back your own last change immediately**; if that doesn't restore it, restore the snapshot. Record what happened.
8. Firewall stays 22 / 80 / 443. Never expose game ports directly.
9. No secrets or real IPs in git or in committed logs (redact `<VPS_IP>`).
10. **Keep the public game up:** redeploy one service at a time, never while a match with a human is in progress (check `/api/status`), and re-run `scripts/p5-health.sh` after each.

---

## 2. Tasks, in order (each ends green and committed; ⛳ = decision point)

### 0. P5 carry-overs (§0.1, items 1–7)

### 1. Sealed bets ⛳ (D-SEAL; the headline of P6)
**Goal:** a player's bet (outcome + amount) can't be read by anyone else until the market closes; after close the totals are public and settlement / payouts work exactly as today. The UI shows it ("🔒 sealed — revealed at close").

**Scope (D-SEAL-SCOPE, pre-decided):** keep the existing open pari-mutuel markets with live odds (they are the spectator show and in the §7 video). Make **one market per match sealed**: the **Champion** market (who wins the Final). If both paths below work, consider sealing the Qualify markets too, but only if the live-odds experience on `/live` stays good.

**Path A — Private Ephemeral Rollup (preferred: real MagicBlock privacy).** Time-box: **6 hours** from the first TEE call.
1. Spike first, standalone (`tools/p6/tee-spike.ts`): connect to the devnet TEE endpoint, `verifyTeeRpcIntegrity`, `getAuthToken`, delegate a test PDA with `EphemeralPermission { is_private: true }` and no members, write to it, and **prove a non-member read returns no data** (and a member / post-reveal read does).
2. Then the design: a `SealedBook` per sealed market on the private rollup; `place_sealed_bet` (session key, chips escrowed: the chip debit has to happen where the `ChipBalance` lives, so design how the stake moves — e.g. debit on devnet-as into a per-match sealed escrow, with only the outcome hidden on the TEE; or another sound split you choose and justify); at close, the program reveals the totals and the per-bettor stakes into the public `Market` (commit to Solana → delegate to devnet-as, or whatever the docs support today). Record the close → revealed latency.
3. The browser: TEE auth inside the WebGL bundle and on `/live` (the session key signs; no popup).
4. ⛳ **Go** if the spike passes all of: non-member read empty, member read correct, 5/5 sealed bets placed and revealed on devnet, close → reveal ≤ 30 s, works from WebGL and `/live`. **Otherwise Path B**, and write down the exact failure (signatures, errors) as roadmap.

**Path B — commit-reveal on the normal rollup (the plan's pre-agreed fallback).**
1. `commit_bet(market, hash, amount)`: `hash = sha256(outcome ‖ amount ‖ salt ‖ bettor)`; the amount is debited and held, the outcome is hidden. Note: the **amount** is visible in this path; say so in the UI ("🔒 pick sealed").
2. `reveal_bet(outcome, salt)` after close, before settle. **The client reveals automatically** (bundle + `/live` keep the salt in localStorage, keyed by market + bettor; also include it in the burner key backup). Unrevealed by settle → **forfeited to the pool** (that rule is what stops a bettor from committing both sides and revealing only the winner; don't change it to "refunded").
3. The keeper waits a fixed reveal window (e.g. 10 s after close, measure what clients need) before settling.
4. Checks: a wrong salt / outcome is refused; double reveal refused; reveal after the window refused; forfeits counted in the receipt.

**Both paths:** LiteSVM tests (happy path, tampering, forfeits / non-members, payouts = hand calculation), the receipt lists sealed bets (◆ settled by the program), `/receipt` and the in-game Deal / bet UI show the sealed state, and the indexer archives the reveals so `/replay` and the receipt check still verify.

### 2. Seasons ⛳ (D-SEASON)
1. **Accounts (on Solana base, not delegated):** `Season { id, starts_at, ends_at, status, top_champions[10], top_predictors[10], top_drop_mvps[10], n_matches, trophies_minted }` and `PlayerSeason [season, wallet] { crowns, finals, rounds_survived, net_market_rush (i64), bets_won, bets_lost, drops_bought, drop_knockouts?, achievements: u32 }`. Season id = ISO week (`year*100 + week`); define `drop MVP` precisely (e.g. most Chaos Drops bought that fired, or knockouts during a drop window — pick one that the receipt can prove, and write it down).
2. **Recording** from the committed receipt, **idempotent** (a `SEASON_RECORDED` bit on the Receipt, or the season's own bitmap), **permissionless** (anyone can call it for a finalized match):
   - ⛳ **preferred:** a **second post-undelegate base action** in the same `finalize_match_action` (mint rewards + record season in the undelegation tx; more real Magic Action usage). Check the action's CU budget and escrow cost per match.
   - **fallback:** a keeper-sent `record_season(match)` after finalize, with retry on resume.
   - Per-bettor net market RUSH must be provable: either the receipt / markets already hold it, or record per-bettor results at payout time. Choose and justify.
3. **Season end:** `close_season` (permissionless after `ends_at`) freezes the board and mints a **season trophy Core asset** to each top-1 (champion, predictor, drop MVP), update authority = the program's `assets` PDA (same as capsules). Prove it end to end on devnet with a **short test season** (e.g. a 2-hour season id in a separate namespace / flag) so a real trophy mint is in the proof; the live weekly season keeps running for P7.
4. **UI:** a season board on `/` (top 3 of each) and `/season` (full top 10, your rank, countdown to close); `/me` shows your season stats; the in-game champion screen shows "+1 crown this season" style lines (UI only). The indexer can cache the board, but the page must show that the numbers come from the on-chain `Season` account (explorer link), and a check that the indexer's rebuild-from-receipts matches the on-chain board.

### 3. Achievements (D-ACH)
- **First crown** (your first Final win ever), **3-round survivor** (qualified from rounds 1, 2 and 3), **perfect predictor** (≥ 2 bets in one match, all won). Optional, only if cheap: "Chaos agent" (a drop you bought fired in a Final).
- Set as bits in `PlayerSeason.achievements` (+ a lifetime `PlayerProfile` bit if needed for "first crown ever") by the same recording path as §2, derived only from the receipt / markets (provable).
- Show them on `/receipt/:match` (badges per player), `/me`, and the in-game champion / receipt card. No minting per achievement (keep cost down) unless it's free in the existing action; decide and record.

### 4. Builds
- EditMode tests green (≥ 418 + yours); `scripts/p1-check.sh` green; program LiteSVM suites green (security + econ + new sealed / season tests).
- One WebGL release build (≤ 25 MB, Decompression Fallback on); `Build.LinuxDevServer` only if the server code changed.
- Program upgrade on devnet (same id), `rush_core.wasm` unchanged unless §1.2 applies.

### 5. Deploy ⛳ (follow §1.3 to the letter)
- Fresh snapshot; neighbours before.
- Redeploy only what changed (web, indexer, keepers, game servers), one at a time, between matches; neighbours + health after each.
- Make sure the keepers' resume path handles the new stages (sealed close / reveal, season record) after a restart: run one keeper kill during the reveal window.

### 6. Verification ⛳ (the exit gate of P6)
1. **Privacy check:** during an open sealed market, a non-member / outsider reads the sealed data (raw `getAccountInfo` on the TEE or the commit hashes on devnet-as) and **cannot tell the pick**; after close everything is public and matches. Record the raw responses.
2. **3 public hybrid matches** with at least one sealed bet each (from a phone-sized `/live` and from the in-game UI), payouts = hand calculation, reveals / forfeits right, receipt ✓, replay ✓.
3. **Season board updates** from those matches (on-chain `Season` + `PlayerSeason` accounts, indexer rebuild matches), achievements shown where earned.
4. **Test season closed** on devnet with a real trophy mint (asset + signature).
5. The carry-over checks (§0.1): Crown by Magic Action (or recorded why not), the 2-human match, the extra kill tests, aborted matches hidden + refunds verified.
6. Stranger → lobby still ≤ 30 s (3 clean-profile runs), and `/live` still smooth.
7. Neighbours fine throughout; `scripts/p5-health.sh` all green at the end.

### 7. Proof + docs + merge
1. `docs/proof/P6-privacy-seasons.md`: §1 carry-overs; §2 sealed bets (path taken, spike results, signatures, raw privacy evidence, latency, OC row); §3 seasons (accounts, recording path, CU, cost per match, test-season trophy); §4 achievements; §5 builds; §6 VPS (snapshot id, changes, neighbour checks, redacted); §7 public verification table (explorer links); §8 SOL; §9 UNVERIFIED; "For P7": what the playtest needs, the scripts, known issues, and **a PROOF.md link list draft** (P7.6: one explorer link per system, including a sealed bet and a season trophy).
2. Update `docs/15` (OC-21…), `docs/19` (P6 as built), `docs/13` (if services changed), `PROJECT_STATUS.md`. One final doc commit.
3. **Merge:** after the exit gate passes, in the main checkout `git merge --no-ff p6` (`merge: p6 (sealed bets, seasons, achievements, …)`), then the main checkout's EditMode + `scripts/p1-check.sh` under the lock. Commit any fix on `p6` and merge again. **Keep the worktree. Never push.**

---

## 3. Decision policy (decide yourself; never stop to ask)

**Priority order:** (1) correctness, fund safety, the VPS neighbours' health, the live game staying up; (2) MagicBlock doing real, visible work; (3) the player / spectator experience; (4) schedule and cost.

**For every ⛳:** measure the options, pick by priority, implement, and record an `OC-n` row + a proof paragraph.

| ID | If… | Then do this |
|---|---|---|
| D-SEAL | the Private ER spike fails any go criterion in §2.1 within 6 h, or the TEE auth can't run in WebGL / `/live` | ship Path B (commit-reveal) as the default; keep the spike code + results in `tools/p6/` and the proof as roadmap. If the TEE works from `/live` but not WebGL, you may ship TEE for `/live` spectators and commit-reveal in game only if both settle into the same market correctly; otherwise B everywhere |
| D-SEAL-SCOPE | sealing a market would remove the live odds the spectators see | seal only the Champion market; keep the others open |
| D-REVEAL | in Path B, clients miss the reveal window in tests (closed tab, slow network) | lengthen the window (≤ 20 s); show a "your pick will be revealed — keep this tab open" note; keep forfeit-on-no-reveal |
| D-SEASON | the second base action doesn't fit (CU, escrow, SDK limits) or fails on devnet | keeper-sent permissionless `record_season` (+ retry on resume); keep the action path behind a flag |
| D-CROWN | the bot-driven "human" never wins the Final within 8 matches, or the harness would need a gameplay change | stop; keep LiteSVM as the Crown proof; mark UNVERIFIED with the run list |
| D-LAYOUT | a feature would need to change an existing account layout | add a new account type instead (or reserved space); never break the live accounts |
| D-CU | a new rollup instruction > 200k CU, or anything threatens the tick budget | move the work to base / the keeper, or split it; never into `tick()` |
| D-RPC | public devnet RPC 429s | throttle (≤ 1.5 tx/s base), backoff, cache reads in the indexer, `getMultipleAccounts`. No paid RPC accounts |
| D-DISK | free space < 5 GB | free only from the §1.1 list, in that order |
| D-SOL | the 1.5 SOL budget would run out | fewer test runs; close test accounts / buffers (`solana program close --buffers`); never let the admin wallet go below 1.5 SOL |
| D-NEIGHBOUR | `finity` or Ovelo changed after one of your changes | roll back your last change; if not restored, restore the snapshot; record; continue only once both are healthy |
| D-LATE | it's past Tue 24 Nov 18:00 UTC and sealed bets aren't merged-ready | cut in this order: optional achievements → `/season` full page (keep the top-3 strip) → sealing Qualify markets → Path A (ship B). **Never cut:** sealed Champion market, season recording, the test-season trophy, the proof, the merge by Wed 06:00 UTC |
| D-OTHER | anything else | apply the priority order and record it |

**Hard rules (not decisions):**
- never push, never add a git remote;
- never touch `../FallRush-trailer`, the `finity` site, Ovelo, cloudflared, or any non-`fallrush-*` resource on the VPS;
- no host-wide Docker prune;
- never commit keypairs, secrets or the real IP;
- never deploy a new program ID;
- never change Classic gameplay or the signed-off motor feel; no `rush-core` simulation change.

## 4. Report to the user (short, plain words)
- Which sealed-bet path shipped (Private ER or commit-reveal), why, and the privacy evidence in one line.
- Seasons: the board live? the test-season trophy minted (link)? recorded by a Magic Action or the keeper?
- Achievements shown where.
- Carry-overs: Crown by Magic Action ✓?, 2-human match, extra kill tests, aborted matches + refunds, health after the unattended run.
- The 3 public matches (links), the neighbour checks (`finity` / Ovelo fine), stranger → lobby time.
- SOL spent (and what's left for P7), disk left on the Mac.
- UNVERIFIED items, and what the user must do for P7 (playtesters: ≥ 8 humans + 3 spectators, real phones, real Phantom).
- That `p6` was merged into `main` (the worktree is kept).
