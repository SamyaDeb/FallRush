# P5 (full): product layer + public URL, with the P4 carry-overs

> **Autonomous run (user, 2026-11-23):** make every engineering decision yourself, choosing the best option on the evidence, and implement P5 **fully, end to end, without stopping to ask**.
> - At each ⛳ apply §3 "Decision policy".
> - Record each decision as a new `OC-n` row in `docs/15` and in the proof, with the options, the numbers, the choice, why, and how to revert.
> - **The user authorised autonomous VPS deployment for P5 under the M3 safety rules (§1.3).** Follow them exactly; they are not questions.

You are the full-stack / Solana / Unity / DevOps engineer on **Fall Rush**, a Unity 6.3 WebGL 12-player knockout party game for the MagicBlock Solana Blitz V10 hackathon (theme **Entertainment Finance**). The deadline is **Fri 27 Nov 14:00 UTC; the internal target is 12:00 UTC**. P5 must leave a **public URL that a stranger can open and play within 30 s, with no setup.**

**What works today** (`main` @ `10c6f4c`, all of P0–P4 merged):
- rounds 1–3 on the **Classic server**;
- the **Final (Lid Flip) fully on a MagicBlock Ephemeral Rollup** (`rush-core`, bots on-chain, real time);
- the Classic → on-chain switch in the Unity client;
- RUSH chips + faucet (`ChipBalance` PDA, OC-12), **Rush Markets** (◆ program-settled / ● authority-recorded), **Chaos Drops** (core v2), the receipt, and the Crown Capsule + VRF capsule drops minted by the program (`mint_rewards`, OC-13);
- the in-game economy UI;
- WebGL 22.37 MB.

All proven on devnet (`docs/proof/P4-economy.md` §8). **Nothing is public yet.**

**Read first, in full:**
1. `docs/19-ONCHAIN-PLAN.md`: §1, §5 P5 / P7, §7 video, §9.
2. `docs/15-DECISIONS-AND-RISKS.md`: OC-1…OC-13, D-5 (VPS), P0-1.
3. `docs/13-DEPLOYMENT.md` (VPS layout, nginx WebSocket + Brotli rules, port clashes) and `docs/proof/M1-transport.md` §4.7 (the VPS run that worked: sslip.io + Let's Encrypt, `finity` / Ovelo checks).
4. `docs/proof/P4-economy.md` (all, especially §2 chips spike, §5 receipt / mint, **§11 UNVERIFIED**, **"For P5"**), `docs/proof/P3-client.md` (§6 switch, §8 WebGL, §12, §14), `docs/proof/P2-onchain-sim.md` (§2, §3 keeper, §10).
5. Code: `programs/fall_rush/src/{lib.rs,sim.rs,econ.rs,state.rs,magic.rs}`, `tools/keeper/*`, `tools/chain-worker/*`, `packages/chain/src/*` (`econ.ts`), `client-unity/Assets/_Game/Scripts/{Client,OnChain,Net}/**`, `infra/*`.

---

## 0. State at hand-off (2026-11-23, checked)
- **Worktrees:** `main` @ `10c6f4c`; `../FallRush-p4` (branch `p4`, merged; leave it); `../FallRush-trailer` (**not ours, never touch**).
- **Program** `4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4`, `.so` 1,102,376 B; core version **0x10002** (Chaos Drops). RUSH mint `3s8KstqicXn1kgFyWPVq3AZXkMprDtEtT7BNTxtQccEw` (mint authority PDA `["rush_mint_auth"]`).
- **Wallets:** admin `~/.config/solana/id.json` **5.26 SOL**; game authority (keeper key) **3.07 SOL**. A Final costs about 0.011 SOL in keeper fees + rent.
- **Disk: only ~7.7 GB free** on this Mac (97 % used). See §1.1.
- **VPS:** Hostinger KVM 2 (Mumbai, Ubuntu 24.04, 2 vCPU, 8 GB, nginx 1.24, Docker 29, certbot 2.9, ufw 22/80/443).
  - **Shared with live projects:** the nginx site **`finity`** (it uses `127.0.0.1:8787` and `:3100`) and **Ovelo** (Docker, behind cloudflared).
  - Access: the `hostinger` CLI (`~/.local/bin/hostinger`, OAuth; snapshots, VM info) and SSH with the key in `~/.ssh` (see `~/.ssh/config`).
  - **The real IP / hostnames never go in git; use `<VPS_IP>`.** sslip.io names + Let's Encrypt worked in M1 (`gs1.<VPS_IP-dashed>.sslip.io`).
  - Leftovers from M1/M2: the `fallrush-gameserver:m2` image and possibly the `gs1` cert.
- **Known stale artifact:** `client-unity/Builds/MacDevServer` (and the Linux server build) on `main` **predate P3's on-chain switch**. In 4 of P4's 5 hybrid runs the server played its own Final (P4 §8.2). **Rebuild every server from `main`.**

### 0.1 P4 carry-overs (do these first, §2 task 0)
1. **Prove cash-out on devnet ("assets you can sell anytime"):** chips → `cashout` (rollup + commit) → `mint_cashout` (Solana) → real **RUSH SPL** in the player's wallet → an SPL transfer to a second wallet. Record every signature.
2. **Ephemeral SPL Token retry ⛳ (time-box 1 day):**
   - P4's spike got 0/5 round trips: the withdraw failed with `Custom 27`, and the failed attempt broke the reused eATA.
   - Redo it properly: a **fresh mint + fresh wallets per round**, the **documented order** (undelegate / commit the eATA first, then withdraw on base; read the docs.magicblock.gg Ephemeral SPL pages + the program source `SPLxh1LVZzEkX99H6rqYizhytLWPZVV296zyYDPagv2`), and a **payout from a program-held vault via our program's CPI on the rollup**.
   - **5/5 round trips → migrate chips to Ephemeral SPL** (bets, drops, payouts, faucet = SPL), keeping `ChipBalance` behind a flag as a fallback.
   - Otherwise keep OC-12 and write down the exact failure (D-ESPL).
3. **Magic Action for minting:** wire the post-undelegate action in `finalize_match` so `mint_rewards` runs **automatically on Solana after the receipt commit**. Fund its escrow (≤ 0.05 SOL; MagicBlock docs: about 0.05 lamports per compute unit + 5,000 per callback), and have the handler check the injected `escrow` signer. The keeper's permissionless `mint_rewards` stays as the retry path. ⛳ 3 devnet matches where the Crown + capsules exist **without any keeper mint call** (D-ACTION).
4. **Burner safety:** on the champion screen and `/me`, prompt "Back up your key / Claim to Phantom" whenever a burner holds a Crown or capsule. Add a key export (with a copy warning). Claim = Core `TransferV1` signed by the burner.
5. **Keeper crash between settle and finalize:**
   - make `finalize_match` refuse while any market has unpaid winners (a flag);
   - make the keeper **resume from on-chain state** after a restart (scan open arenas / matches, finish settle → receipt → finalize → mint);
   - test it by killing the keeper at each step.
6. **Small fixes:**
   - the WebGL `?rcselftest=1` summary counts 6 files (the next build includes it);
   - remove the duplicated §11 list in `docs/proof/P4-economy.md`;
   - **Deal screen: pool / odds next to each capsule** (UI only).
7. **Faucet abuse (devnet):** keep the per-wallet limit, plus a **per-IP / per-session soft limit in the web layer** (the faucet request goes through the bundle; a small rate-limit endpoint in the web container signs nothing; it only gates the UI). Write down that on-chain it is per wallet only.

---

## 1. Rules

### 1.1 Local machine (8 GB, ~7.7 GB disk free)
- **Worktree:** `git worktree add ../FallRush-p5 -b p5 main`. Unity: `cp -cR /Users/samya/Downloads/FallRush/client-unity/Library ../FallRush-p5/client-unity/Library` (APFS clone).
- **Disk hygiene:**
  - keep ≥ 5 GB free at all times, checked before every build;
  - delete your own intermediate build folders after use;
  - keep at most **one** saved WebGL build per phase;
  - `cargo clean -p` your own spike crates;
  - never delete other projects' files, Docker volumes, `../FallRush-trailer`, or anything under `~/Library/Application Support`.
  - If space runs short, free it **only** from: your worktree's build output, `~/Library/Caches/ms-playwright*`, `npm cache clean --force`, `pnpm store prune`, Solana platform-tools versions other than the one `cargo build-sbf --version` reports. Write down what you freed.
- **One heavy job at a time:** `bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p5-<what> -- <command>` (Unity, builds, the local stack, devnet runs, Chrome, Docker builds). Use `CARGO_BUILD_JOBS=2 nice -n 10` for tests.
- **The Linux server image is x86_64.** It can't run under Rosetta / QEMU on this Mac (M1 finding). Build the Linux player with Unity locally, then build and run the image **on the VPS** (`docker build` there, or `docker save | ssh … docker load`).

### 1.2 Code and chain
- **Determinism:** any `rush-core` change regenerates vectors with parity on native / WASM / sBPF / FFI and bumps `VERSION`. P5 shouldn't need core changes.
- **The hot tick never fails.** No settlement / mint inside `tick()`. The VRF callback identity stays a `const` item. Never import `sol_remaining_compute_units`.
- **Program upgrades:** allowed (same program ID only; extend as needed). Write down the size and SOL.
- **Devnet SOL budget for P5: 2.0 SOL** across both wallets, including upgrades, the escrow and test wallets. **Keep ≥ 1.5 SOL in the admin wallet** for P6 / P7. Log every spend.
- Keypairs never in git. The game authority key exists on the VPS **only** in `/opt/fallrush/.env` (root, `chmod 600`) or a mounted secret file with the same permissions.
- Pin every new npm / crate version exactly. **No Next.js** (OC-8: a Vite app). No new Unity packages.
- Commit on `p5` with your own paths: `git commit -- <paths>`. End every commit message with the attribution lines from the system reminder. A number counts as measured only if you ran it. Anything else is **UNVERIFIED**.

### 1.3 VPS rules (the M3 safety rules, mandatory on every VPS change)
1. **Take a Hostinger snapshot right before the first change of each session** (`hostinger` CLI; snapshots expire after 24 h). Write down its id.
2. **Only `fallrush-*` names:** containers, images, volumes, networks, `/opt/fallrush/**`, and nginx files `/etc/nginx/sites-available/fallrush-*.conf` (+ symlinks).
   - **Never edit, restart or remove anything else:** the `finity` site, its config, Ovelo, cloudflared, other containers.
3. **Bind every Fall Rush port to `127.0.0.1`**, avoiding `8787` and `3100` (used by `finity`). Suggested: web `3110`, gs1 `7777`, gs2 `7778`, keeper health `3111`, indexer `3112`. **Docker caps:** each game server 0.75 CPU / 1 GB; keeper 0.25 / 512 MB; web 0.25 / 256 MB; indexer 0.25 / 512 MB.
4. **nginx:** add server blocks only. Run `nginx -t` and **check its exit status** before every reload. **Reload, never restart.** Use the WebSocket rules from doc 13 (`proxy_http_version 1.1`, Upgrade / Connection headers, `proxy_read_timeout 3600`) and the Brotli headers for the WebGL `.br` files (no brotli module; Decompression Fallback off; `chmod -R a+rX` on the build).
5. **TLS:** `certbot --nginx -d <name>` per new sslip.io name. Never touch other certificates.
6. **No host-wide prune** (`docker system prune`, `docker image prune -a`, `docker volume prune`). Remove only your own `fallrush-*` items by name.
7. **Before and after every change, verify the neighbours are fine:**
   - `finity` (`curl -sI https://…` gives the same status as before, or the `finity` config md5 is unchanged);
   - Ovelo's containers still `Up` (`docker ps`);
   - write it into the proof.

   If either breaks: **roll back your own last change immediately** (remove your nginx file + reload, stop your containers). If that doesn't restore it, restore the snapshot. Record what happened.
8. The firewall stays 22 / 80 / 443 only. Never expose game ports directly.
9. No secrets or real IPs in git or in logs committed to git (redact `<VPS_IP>`).

---

## 2. Tasks, in order (each ends green and committed; ⛳ = decision point)

### 0. P4 carry-overs (§0.1, items 1–7)
Commit per item, e.g. `feat(p5-0.3): Magic Action mints rewards after the receipt commit`.

### 1. Builds from `main` (fresh)
1. Under the lock: Linux server, Mac dev server, WebGL release. ≤ 25 MB, with the fixed self-test.
2. Verify locally (Mac dev server + Chrome) that **a full hybrid match switches to the on-chain Final** with the fresh server (the P4 §8.2 stale-build bug is gone).
3. Commit the build scripts / versions (not the builds).

### 2. Arena pool + matchmaking for the hybrid match
1. **Pool: 2 arenas** (one per game server) on devnet-as, set up once by an idempotent `tools/keeper/setup.ts` (delegated, maps sealed, markets / drop queue ready). Each game server is bound to its arena by config.
2. **Matchmaking = the Classic lobby:**
   - **Quick Play** joins the fullest open game server (the lobby countdown starts at ≥ 1 human; bots fill at roster lock, as today);
   - the on-chain `join_lobby` is used only at the Final handoff (P3 / P4 flow, pre-joined at roster lock).
3. **Directory:** every game server and keeper publishes a small status JSON (players, phase, arena, match PDA) through the web container at `/api/status`. The landing page and the lobby browser read it.
4. **One keeper per arena** (hybrid mode: tails that server's jsonl through a shared `fallrush-logs` volume; chain worker + market maker + Magic Action / mint retry + resume-from-state).
5. Commit `feat(p5-2): arena pool + Quick Play + directory`.

### 3. Web app (`web/`, Vite + TS, static; served by the `fallrush-web` container behind nginx)
All pages are **mobile-friendly** and use `packages/chain` (burner wallet + session keys, rollup subscriptions, econ ops).

| Route | Content |
|---|---|
| `/` | logo, **Play now** (→ `/play`, Quick Play), live arenas from `/api/status`, recent matches (indexer), a short "how it works on MagicBlock" strip (ER / VRF / session keys / Core) with explorer links |
| `/play` | the Unity WebGL build (Brotli), loading bar, fullscreen; guest burner created silently; faucet on first visit |
| `/live/:arena` | **a 2D top-down view of the on-chain Final drawn from the `WorldAcc` subscription** (players, lids, sweeper, drops' telegraph), the live odds of each market, **Bet** and **Drop** buttons (session key), event feed, chain chip. During rounds 1–3: the Classic status + Qualify markets |
| `/replay/:match` | plays the archived Final through `rush_core.wasm` in the same 2D view. Shows the receipt hash check: "replay = on-chain ✓" |
| `/receipt/:match` | Rollup ✓ → Solana ✓ timeline; trust labels per part (● rounds 1–3 by the authority, ◆ the Final by the program); placements; market results; drops log; Crown + capsule assets; explorer links |
| `/me` | wallet (burner / Phantom), chips, **cash out to real RUSH** (task 0.1), capsules + Crown, **claim to Phantom**, **back up key**, equip a shell for the next match |

Phantom: connect via `window.solana` / wallet-standard. Burner → Phantom upgrade = claim assets + move RUSH. Commit `feat(p5-3): web app`.

### 4. Cosmetics from the wallet
- The client and `/me` read the owned Core capsules (`getProgramAccounts` on the Core program with an owner `memcmp`, or `getAssetsByOwner` if a DAS endpoint is free and works on devnet) and offer those shells.
- The Classic server **checks ownership through an RPC read** before accepting a capsule shell (a falsified id falls back to a preset).
- The on-chain `join_lobby` cosmetic stays a preset id. Cosmetic only, never stats (GDD pillar 4).
- Commit `feat(p5-4): cosmetics from owned capsules`.

### 5. Indexer / archive (`tools/indexer`, container `fallrush-indexer`)
- For each finished match it stores World recordings, input / bet / drop transactions, the receipt and the jsonl in `/opt/fallrush/archive/<match>/`.
- It serves `/api/matches`, `/api/match/:id`, `/api/replay/:id`.
- Keep the last 200 matches (pruning only its own directory).
- Commit `feat(p5-5): indexer`.

### 6. VPS deployment ⛳ (follow §1.3 to the letter)
1. Snapshot, then run the read-only neighbour checks (record them).
2. `/opt/fallrush/`: `docker-compose.yml` (prefixed `fallrush-`; ports and caps per §1.3), `.env` (600), `web/` (static build), `archive/`, `logs/`.
   - Services: `fallrush-web` (nginx:alpine serving `web/` + `/api/*` proxied to the indexer + `/api/status`), `fallrush-gs1`, `fallrush-gs2`, `fallrush-keeper-1`, `fallrush-keeper-2`, `fallrush-indexer`.
   - **⛳ If 2 game servers + 2 keepers exceed the 2 vCPU** (measure: server tick p95, the `finity` / Ovelo response times), run **1 game server + 1 arena** (D-HOST).
3. nginx `fallrush-*.conf`:
   - `play.<VPS_IP-dashed>.sslip.io` → web (static + `/api`);
   - `gs1.…` / `gs2.…` → game servers (wss).

   certbot per name; `nginx -t` exit status; reload.
4. Smoke tests from this Mac, through the public URLs only:
   - **(a)** `https://play.…` loads, `/play` reaches the menu, a guest burner is created, the faucet works;
   - **(b)** a **full hybrid match** (you as a headless Chrome player + bots): rounds 1–3 on gs1, the switch, the on-chain Final on devnet-as, the receipt page Rollup ✓ → Solana ✓, the Crown minted **by the Magic Action**;
   - **(c)** `/live` on a phone-sized viewport during that match: odds move, a bet and a drop succeed;
   - **(d)** `/replay` of that match shows "replay = on-chain ✓";
   - **(e)** `/me` cash-out to real RUSH;
   - **(f)** **two matches at the same time** (gs1 + gs2) if D-HOST kept 2;
   - **(g)** neighbours re-checked.
5. **Ops:** `restart: unless-stopped`; health checks; a `fallrush-*` systemd-free restart policy (compose only); log rotation (Docker `max-size`); a one-line `scripts/p5-deploy.sh` for redeploys (build locally → copy → `docker compose up -d` → smoke a). Keeper keys only from `/opt/fallrush/.env`.
6. Commit `feat(p5-6): VPS deployment (public URL)`, with redacted configs in `infra/prod/`.

### 7. Verification ⛳ (the exit gate of P5)
1. **From a clean browser profile** (headless Chrome, empty storage): open `https://play.…`, click Play, and be **in a lobby within 30 s with no popups** (measure). Repeat 3×.
2. **3 public hybrid matches** end to end (bots + 1–2 headless players + a `/live` spectator betting and dropping):
   - each completes;
   - payouts = hand calculation;
   - Crown + capsules minted by the Magic Action (keeper retry only if the action fails, and written down);
   - the receipt page is correct;
   - replay = on-chain.
3. **Kill tests on the VPS:** restart `fallrush-keeper-1` mid-Final (resume-from-state finishes the match) and restart `fallrush-gs1` between matches (clients reconnect). Record the recovery times.
4. **Neighbour check** after all tests (`finity` + Ovelo unchanged).
5. **24 h readiness:** leave everything running. Write `scripts/p5-health.sh` (status of every service + a smoke check) for P6 / P7 and judging.

### 8. Proof + docs
1. `docs/proof/P5-product.md`:
   - §1 carry-overs (cash-out signatures, the Ephemeral SPL retry result + OC row, Magic Action runs, the burner prompt, resume tests);
   - §2 builds;
   - §3 the pool + matchmaking;
   - §4 the web app (screenshots desktop + phone);
   - §5 cosmetics;
   - §6 the indexer;
   - §7 the VPS (snapshot id, services, caps, neighbour checks before / after each change, redacted);
   - §8 the public verification table;
   - §9 SOL;
   - §10 UNVERIFIED;
   - "For P6 / P7": the public URLs as `<VPS_IP>` placeholders in git (the real URL only in a git-ignored local note `docs/proof/p5/PUBLIC_URL.local.md` and in your final report to the user), the health script, what playtests need.
2. Update `docs/13-DEPLOYMENT.md` to as-built (hybrid services, ports, no Postgres / relay, Vite web), `docs/15` (new OC rows), `docs/19` (P5 as built), `PROJECT_STATUS.md`. Keep these in one final doc commit.
3. **Merge:** after the exit gate passes, `git merge --no-ff p5` in the main checkout (`merge: p5 (public URL, web app, arena pool, …)`), then the main checkout's EditMode + `scripts/p1-check.sh` under the lock. **Keep the worktree. Never push.**

---

## 3. Decision policy (decide yourself; never stop to ask)

**Priority order:** (1) correctness, safety, the VPS neighbours' health; (2) MagicBlock doing real, visible work; (3) a stranger's first 30 seconds and the spectator experience; (4) schedule and cost.

**For every ⛳:** measure the options, pick by priority, implement, and record an `OC-n` row + a proof paragraph.

| ID | If… | Then do this |
|---|---|---|
| D-ESPL | the Ephemeral SPL retry is < 5/5 round trips or the vault payout CPI fails | keep `ChipBalance` (OC-12); write down the exact failure + signatures; mention it as roadmap in the proof |
| D-ACTION | the Magic Action doesn't fire, fails, or the escrow can't be funded / verified | keep the keeper-sent `mint_rewards` (OC-13) as the primary path, with the action wired behind a flag; write down why |
| D-HOST | 2 game servers + 2 keepers overload the 2 vCPU (server tick p95 > 20 ms, or `finity` / Ovelo response time +50 %) | run 1 game server + 1 arena + 1 keeper |
| D-TLS | certbot fails for an sslip.io name | retry once after checking DNS; then use one name (`play.…`) and route `/gs1` and `/gs2` as WebSocket paths on it |
| D-RPC | public devnet RPC 429s break the keeper / web | throttle (≤ 1.5 tx/s base), exponential backoff, cache reads in the indexer, batch reads (`getMultipleAccounts`). No paid RPC accounts (that needs the user) |
| D-PHANTOM | the real Phantom extension can't be driven headless | test the claim / cash-out with a wallet-standard test wallet injected in Chrome; mark "real Phantom" UNVERIFIED and leave a manual checklist for the user in the proof |
| D-REPLAY | the 2D replay can't be made exact for some frames (render interpolation) | the hash check decides "replay = on-chain"; the visuals may interpolate |
| D-DISK | free space < 5 GB | free only from the §1.1 list, in that order; never anything else |
| D-SOL | the 2.0 SOL budget would run out | fewer test runs, close test accounts and leftover buffers (`solana program close --buffers`); never let the admin wallet go below 1.5 SOL |
| D-NEIGHBOUR | `finity` or Ovelo changed after one of your changes | roll back your last change; if not restored, restore the snapshot; record; continue only once both are healthy |
| D-OTHER | anything else | apply the priority order and record it |

**Hard rules (not decisions):**
- never push, never add a git remote;
- never touch `../FallRush-trailer`, the `finity` site, Ovelo, cloudflared, or any non-`fallrush-*` resource on the VPS;
- no host-wide Docker prune;
- never commit keypairs, secrets or the real IP;
- never deploy a new program ID;
- never change Classic gameplay or the signed-off motor feel.

## 4. Report to the user (short, plain words)
- **The public URL** (the real one, only in this report and the git-ignored note).
- What a stranger can do in 30 s; what a spectator on a phone can do.
- Carry-over results: cash-out to real RUSH ✓?, the Ephemeral SPL retry result, the Magic Action minting ✓?, the burner backup, the keeper resume.
- The 3 public matches (links), the kill tests, the neighbour checks (`finity` / Ovelo fine).
- VPS resources (CPU / RAM used), SOL spent, disk left on the Mac.
- UNVERIFIED items, and anything for the user to check by hand (real Phantom, a phone test on a real phone, playtesters for P7).
- That `p5` was merged into `main` (the worktree is kept).
