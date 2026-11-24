# P7: final polish + hardening + launch prep (playtests and submission are the user's)

> **Autonomous run (user, 2026-11-24).** Make every engineering and design decision yourself, choosing the best option on the evidence, and run each Part **end to end without stopping to ask**.
> - At each ⛳ apply §4, the decision policy.
> - Record each decision as a new `OC-n` row in `docs/15` and in the proof, with the options, the numbers, the choice, why, and how to revert. **The next free number is OC-29.**
> - The **only** stops are the two **USER GATES** (§3 A4, §3 B2). At a gate, write the report, then wait for the user's message.
> - The user has already authorised VPS redeploys for P7 under the M3 safety rules (§2.3). Follow those rules exactly; they are not questions.
> - The user does these parts by hand, so never do them yourself: the **playtests** (people), **making the repo public / pushing**, the **submission form**, and the **demo video** (later, not in this prompt). You prepare everything they need (`docs/prompts/P7-USER-STEPS.md`).

You are the full-stack / Solana / Unity / web / DevOps engineer on **Fall Rush**. It is a Unity 6.3 WebGL, 12-player, 4-round knockout party game with Rushlings (gacha-capsule toys with noodle arms), entered in the MagicBlock Solana Blitz V10 hackathon (theme **Entertainment Finance**).
- **Deadline:** Fri 27 Nov 14:00 UTC. The user submits by **12:00 UTC**.
- **Judging:** "creativity, technical depth, and how compellingly they showcase what's possible on Solana"; an earlier edition said "depth of integration, creativity, and polish".
- **What's left:**
  - The game works and the chain does real work.
  - It still **looks like a dev build in its menus, panels and web pages**.
  - It has **not been load-tested** at playtest scale.
  - The **repo isn't ready to go public**.
- **P7 fixes those three things.**

**What works today** (`main` @ `6a015e5`, P0–P6 merged; checked 2026-11-24):

- **Public game.**
  - Landing at **https://fallrush.fun**: Cloudflare Worker `fallrush-front` → VPS. The `web/site` React/GSAP landing owns `/`; the web app home is `/arenas`.
  - Game at `https://play.<VPS_IP-dashed>.sslip.io` (real URL in the git-ignored `../FallRush-p5/docs/proof/p5/PUBLIC_URL.local.md`).
  - Stranger → lobby with no popups: burner wallet + session key + guest gas + 1,000 RUSH chips.
- **Hybrid match.**
  - Rounds 1–3 run on the Classic server (gs1 = arena 2, gs2 = arena 3).
  - **The Final (Lid Flip) runs on the MagicBlock rollup at 30 Hz**, driven by one keeper per arena.
- **Economy and identity.**
  - RUSH chips; Rush Markets with live odds on `/live`; Chaos Drops.
  - **The sealed Champion market** sits on a MagicBlock Private ER (TEE, OC-21…23), with a Path B commit-reveal fallback.
  - The receipt is committed to Solana.
  - **The Crown and capsules are minted by a Magic Action** in the undelegation tx.
  - Weekly on-chain seasons + trophies, and achievements.
  - `/replay` is byte-exact; `/receipt`, `/me` (cash-out, backup, Phantom), `/season`.

**Read first, in full:**
1. `docs/19-ONCHAIN-PLAN.md`: §1 (architecture + trust model), §5 **P7** and P8, §7 (video storyboard, for the README only), §9, §10 (what stays exactly as it is).
2. `docs/proof/P6-privacy-seasons.md`: all of it, especially §7, **§9 UNVERIFIED** and **"For P7"** (incl. the PROOF.md link draft). Also `docs/proof/P5-product.md` §7, §8, §10.
3. `docs/15-DECISIONS-AND-RISKS.md` OC-1…OC-28, D-5; `docs/13-DEPLOYMENT.md` §2a / §2a.1; `docs/18-HACKATHON-STRATEGY.md` §4 and §6 (submission package).
4. `docs/17-ART-DIRECTION.md`:
   - §1, the originality guardrails: **no Fall Guys fonts, banners, UI layouts or "Qualified!" design**;
   - §3.0, the soft toy look;
   - §8, the signature moments.

   The user's look feedback: soft pastel surfaces, saturated colour only on rims, props and hazards; chunky, bevelled, soft shadows; never neon slabs, never near-white.
5. Code:
   - **Unity:** `client-unity/Assets/_Game/Scripts/Client/` (`UiKit.cs`, `UiPress.cs`, `MatchHud.cs`, `ResultBanner.cs`, `CapsuleMachineWidget.cs`, `ChampionPodium.cs`, `ScreenFx.cs`, `MenuScreen.cs`, `LobbyScreen.cs`, `SettingsPanel.cs`, `Nameplates.cs`, `FollowCamera.cs`, `FxDirector.cs`, `AudioDirector.cs`, `GameAudio.cs`, `LookState.cs`, `GameSettings`); `OnChain/Economy/{EconomyHud.cs,ChaosDropView.cs}`.
   - **Web app:** `web/app/src/` (`style.css`, `main.ts`, `pages/*.ts`, `lib/view2d.ts`).
   - **Style reference, read-only:** `web/site/src/` (`styles.css`, `components/CapsuleLoader.tsx`, `components/Juice.tsx`, `three/poster.ts`).
   - **Chain / ops:** `tools/keeper/*`, `tools/indexer/server.ts`, `tools/p6/*`, `tools/load/*`, `infra/prod/*`, `scripts/p5-*.sh`, `scripts/p6-*.sh`.

---

## 0. State at hand-off (checked 2026-11-24; re-check each at the start)
- **Worktrees:**
  - `main` @ `6a015e5` is the main checkout.
  - `../FallRush-p5`, `../FallRush-p6` (merged; **keep**; p5 holds `PUBLIC_URL.local.md`).
  - **Never touch:** `../FallRush-trailer` and `../FallRush-landing` (other sessions).
- **Program** `4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4`: data length 1,198,896 B, upgrade authority = admin.
- **RUSH mint:** `3s8KstqicXn1kgFyWPVq3AZXkMprDtEtT7BNTxtQccEw`.
- **Wallets at P6 end:**
  - admin `~/.config/solana/id.json` **3.44 SOL**;
  - game authority **2.29 SOL**;
  - guest-gas funder `tools/keeper/.keys/devnet-funder.json` **≈ 0.57 SOL** (≈ 45 guests at 0.012). Re-check with `solana balance`.
- **VPS:** stack `/opt/fallrush` (compose project `fallrush`):

  | Service | Port |
  |---|---|
  | web | :3110 |
  | gs1 | :7777 |
  | gs2 | :7778 |
  | keeper-1 | :3111 |
  | keeper-2 | :3113 |
  | indexer | :3112 |

  - The next free port is **3114**.
  - Access: `~/.config/fall-rush/vps.env`.
  - nginx `sites-available/fallrush-p5.conf` → `sites-enabled/zz-fallrush-p5.conf`.
  - Front Worker deploy: `scripts/front-deploy.sh`.
  - The last snapshot was taken 2026-11-23 17:16 UTC and **has expired. Take a fresh one before your first VPS change.**
- **Open from P6, §9:**
  - no real phone, stranger or Phantom yet (the playtest covers these);
  - TEE under load;
  - stranger → lobby 29–46 s on a slow uplink;
  - match 3's first-tick replay mismatch;
  - the `?autobet=` hook fires twice;
  - public RPC 429s when many clients start from one IP.
- **Open from landing:** the waitlist Worker redeploy with `ALLOWED_ORIGINS` incl. `https://fallrush.fun` (`scripts/landing-deploy-cf.sh`) was denied by the permission system. The Turnstile widget hostname may also need fallrush.fun.
- **Disk:** check `df -h /` (the P6 runs went down to ~11 GB free).

---

## 1. Schedule (UTC); D-LATE in §4 says what to cut

| By | What | Who |
|---|---|---|
| **Wed 25, 12:00** | Part A done: polish + hardening deployed, playtest kit ready → **USER GATE 1** | agent |
| Wed 25, 13:00–16:00 | Playtest 1 (18:30–21:30 IST) | user |
| **Thu 26, 06:00** | Part B done: playtest fixes deployed → **USER GATE 2** | agent |
| Thu 26, ~10:00 | Playtest 2, two clean recorded matches (the user's video footage) | user |
| **Thu 26, 18:00** | Part C done: PROOF.md, README, submission pack, secret scan, RC frozen + merged | agent |
| Fri 27, by 12:00 | Repo public + push + submission form + X post | user |

If the user's notes come late, keep going on Part C. Part C doesn't depend on the playtest. Fold the fixes in when the notes arrive (D-PLAYTEST-LATE).

---

## 2. Rules

### 2.1 Local machine (8 GB RAM, shared)
- **Worktree:**
  - `git worktree add ../FallRush-p7 -b p7 main`.
  - Unity Library: `cp -cR /Users/samya/Downloads/FallRush/client-unity/Library ../FallRush-p7/client-unity/Library` (APFS clone).
  - Web: `pnpm install --frozen-lockfile` in the worktree.
- **One heavy job at a time:** `bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p7-<what> -- <command>`. This covers Unity, builds, Chrome/Playwright runs, load tests, Docker builds and devnet runs. Use `CARGO_BUILD_JOBS=2 nice -n 10` for cargo.
- **Unity batch builds rewrite `ProjectSettings`** (`m_EnableMultiplayerRoles`, `webGLExceptionSupport`). Revert those two after every build, as in P6.
- **Disk hygiene:**
  - Keep ≥ 5 GB free, checked before every build. Keep **one** saved WebGL build for P7.
  - If space runs short, free **only** these, in this order:
    1. your own build output;
    2. `../FallRush-p5` and `../FallRush-p6` build output only (`programs/target`, `crates/target`, `client-unity/Builds`; both merged);
    3. `~/Library/Caches/ms-playwright*`;
    4. `npm cache clean --force`, then `pnpm store prune`.
  - Write down what you freed.
- **The Linux server image is x86_64.** Build `Build.LinuxDevServer` locally only if server code changed, and build / load the image on the VPS, as in P5 / P6.

### 2.2 Code and chain
- **Polish is render / UI / web only.**
  - No change to maps, round rules, timings, the motor (`movement_v4`), `rush-core`, map blobs or hashes, transcript format, or the Classic server's gameplay.
  - The UI may read more state; it may never change what the server or the chain decides.
  - **Map hashes and `rush_core.wasm` must be byte-identical at the end.** Check this.
- Bug fixes in the keeper, indexer, web or chain bundle are allowed. Any **program** change is a last resort (same program ID, compatible layouts, record size + SOL); polish never needs one.
- **The hot tick never fails.** Nothing new goes into `tick()`.
- **Devnet SOL budget for P7: 1.5 SOL** across all wallets, including load-test bettors and the funder top-up. **Keep ≥ 1.0 SOL in the admin wallet through judging.** Log every spend in `docs/proof/p7/sol.md`.
- Pin every new npm version exactly. **No new frameworks in `web/app`** (it stays Vite + TS, no React). **No new Unity packages**, and no tween library: write your own.
- Keypairs, secrets and the real IP never go in git.
- Commit on `p7` with your own paths: `git commit -- <paths>`. End every commit message with the attribution lines from the system reminder.
- A number counts as measured only if you ran it; anything else is **UNVERIFIED**.

### 2.3 VPS (the M3 safety rules, mandatory on every VPS change)
1. **Take a Hostinger snapshot right before the first change of the session**, and write down its id. Snapshots expire after 24 h, so take another before any change after that.
2. **Only `fallrush-*` names:** containers, images, volumes, `/opt/fallrush/**`, and nginx `sites-available/fallrush-*.conf`. **Never edit, restart or remove anything else**: `finity`, its config, Ovelo, cloudflared, other containers.
3. **Ports:** every Fall Rush port on `127.0.0.1`, avoiding `8787` and `3100`. Keep the P5 caps. A new service (e.g. a watchdog) gets ≤ 0.1 CPU / 64 MB and the next free port, if it needs one.
4. **nginx:** edit only `fallrush-*.conf`. Run `nginx -t` and **check its exit status** before every reload. **Reload, never restart.**
5. **No host-wide prune.** Remove only your own `fallrush-*` items, by name.
6. **Neighbours:** before and after every change, run `scripts/p6-neighbours.sh` and append to `docs/proof/p7/vps/neighbours.log`.
   - If finity or Ovelo breaks: roll back your last change at once.
   - If that doesn't restore it, restore the snapshot.
   - Record what happened.
7. **Firewall** stays 22 / 80 / 443.
8. **Logs:** no real IP or secrets in committed logs (use `<VPS_IP>`).
9. **Keep the public game up:**
   - Redeploy one service at a time, never while a human is in a match (check `/api/status`).
   - Use `scripts/p6-vps-deploy.sh` (it keeps `web/site`) or a P7 copy of it.
   - Run `scripts/p5-health.sh` after each redeploy.

---

## 3. Tasks, in order (each ends green and committed; ⛳ = decision point)

### Part A — polish, hardening, playtest kit (autonomous)

#### A0. Hand-off
- Run `scripts/p5-health.sh` and record how the stack did since P6 (`docs/proof/p7/vps/health-start.txt`).
- Check the wallets and disk, then create the worktree.
- **Before-screenshots** (for the proof's before/after):
  - every in-game panel listed in A1 from the current WebGL build;
  - every web page at 390×844 and 1440×900.
  - Save them in `docs/proof/p7/polish/before/`.

#### A1. In-game polish ⛳ (D-POLISH) — the headline of Part A
Goal: every screen a player sees feels designed and alive, in the soft-toy look, with no dev leftovers. All UI is code-built uGUI (`UiKit.cs`) with hand-written `Update()` animation.

What's already good, so keep it:
- the capsule-split transition (`ScreenFx`);
- the capsule-machine countdown;
- `ResultBanner`, the claw, and the podium crown drop.

What's missing: almost every panel just switches on and off.

1. **`Client/UiMotion.cs`, a shared, allocation-free tween helper** (no package):
   - **Panel enter:** scale 0.92 → 1 with overshoot (back-out), plus alpha 0 → 1, 0.22 s. **Exit:** 0.15 s.
   - **Row stagger:** 40 ms per row.
   - **Number count-up:** ease-out, ≤ 0.6 s.
   - **Bump pulse:** scale 1 → 1.12 → 1, 0.18 s.
   - **Slide-in:** from an edge, 0.25 s.
   - **Shake:** for a refused action.
   - Runs on unscaled time.
   - `GameSettings.ReducedMotion` → fades only, no scale, slide or shake.
   - Hook it into the existing `Show*` / `Hide*` paths, so callers just ask for "show" and "hide".
2. **`UiKit` cards** (one builder used everywhere), in the soft look:
   - pastel body, a saturated rim or header ribbon in the card's hue, a 2-tone bevel (lighter top edge), a soft drop shadow;
   - an icon slot, a title in FR_Display (outlined), a body in FR_Body (tabular digits), footer chips;
   - three sizes (S / M / L);
   - hues from the existing palette constants (`UiKit`, `Net/PaletteTokens.cs`).
   - Our own design (doc 17 §1): no copied banner or "Qualified!" shapes from any other game.
3. **Designed cards:**
   - **Deal** (`MatchHud.BuildDeal` + `CapsuleMachineWidget`):
     - The 4 round rows become **4 round cards** that pop out of the capsule machine one per round. Each shows the map name, the mode icon (race / survival / skill / final) and the qualify target.
     - The VRF seed chip shows the **real explorer link** for the on-chain deal, or "Classic deal" in practice. Delete the "on ranked matches (M6)" string.
   - **Round result** (`ShowResults`): after the existing banner, a **Qualified / Eliminated card** with place, time or score, and one stat line. The list rows stagger in, and your row is highlighted.
   - **Intermission leaderboard** (`BuildIntermission`):
     - rows stagger in, points count up, and changed ranks slide to their new place;
     - "next round" shows a mini card of the next map;
     - remove the disabled "Fan Picks: coming soon" button (or replace it with a working "Open /live to bet" link).
   - **Champion** (`ChampionPodium` + a 2D overlay): a **CHAMPION card** that lands after the crown drop, with:
     - the name, crown icon, rounds survived and the Final time;
     - "+1 crown this season" (from the P6 season data, UI only);
     - the P6 achievement badges earned.

     This replaces the counter text "CHAMPION: name". Remove the "greybox / placeholder" comments and update them to what the code is now.
   - **Receipt** (`ShowReceipt` + `EconomyHud` receipt), in this sequence:
     1. "Rollup ✓" ticks in;
     2. a connector line draws;
     3. "Solana ✓" ticks in;
     4. **minted asset tiles** pop in (Crown / capsule, rarity colour + name, "in your wallet");
     5. badges;
     6. buttons "View on explorer" and "Open receipt" (opens `/receipt/:match`).

     In practice mode it shows a clear "Practice: not recorded" card, not "—".
   - **Bet panel** (`EconomyHud`, B key):
     - split it out of the single text string into a card;
     - market rows with **animated odds bars**;
     - the **🔒 sealed** chip on the Champion market ("revealed at close");
     - a stake stepper with a payout preview that counts;
     - a confirm bump; a refused bet shakes.
   - **Chaos Drops:** each drop is a small card button with its price and a **cooldown ring**. A fired drop shows a **banner that slides in** ("<buyer> dropped Goo Surge!") and a ring flash.
   - **Lobby** (`ShowLobby`): roster rows stagger in as players join, with a "bot" label as today, and the player count bumps.
4. **HUD:**
   - The timer badge pulses and turns amber ≤ 10 s, then red ≤ 5 s, with a tick SFX from the existing set.
   - The qualified counter bumps on change.
   - The chain chip states are each screenshot-checked (`docs/proof/G2G3-look-juice.md:93` was never checked).
   - The **spectate pill becomes a small card**: who you're watching, "Tab: next", and players left.
5. **Juice** (visual only, off under reduced motion, **no hit-stop and no time-scale change**):
   - small `FollowCamera.Shake` punches when you're eliminated (claw grab), when you qualify, when a Chaos Drop fires, and when the crown lands;
   - a confetti burst from `FxDirector` behind the champion card;
   - a UI SFX on every card enter / button, from the existing `AudioDirector` cues; add no new audio files unless a cue is missing.
6. **Glyphs** ⛳ (D-GLYPH):
   - In a **player build**, check every emoji / symbol the UI uses (🌀 👑 ⚠ 🔒 ◆ ● ✓ etc.; `EconomyHud.cs:380,425,476`).
   - Replace any that render as boxes with sprites from a small **UI icon atlas** (crown, swirl, lock, warning, capsule, check, mode icons), drawn in code or as one PNG.
   - Add an EditMode test that every HUD string's characters are covered by the font chain or mapped to an icon.
7. **Clean-up:**
   - Hide the dev menu buttons (Practice / tuning, G4 hazards sandbox, Feel Lab) in release builds (a dev flag or `Debug.isDebugBuild`; keep them reachable with a URL flag for us).
   - Rename "Play On-Chain (preview)" to what it really is today, or hide it if the public flow doesn't use it. Decide and record.
   - `LobbyScreen` "Enter Sandbox (M2)" → product wording.
   - Remove every leftover M-number / "coming soon" / TODO string a player can see (`grep` the Scripts folder for `M5|M6|M7|coming soon|TODO|greybox` in string literals).
   - Delete `Assets/WebGLTemplates/xNFT`.
   - Animate the main menu panel and title in, and make the settings panel open and close with UiMotion.
8. **Verify A1:**
   - EditMode green (≥ 420 + yours: UiMotion maths, card layout sizes, glyph coverage).
   - `scripts/p1-check.sh` green; map hashes + `rush_core.wasm` unchanged.
   - **One WebGL release build** ≤ 25 MB, Decompression Fallback on.
   - **After-screenshots from the player build**, never the Editor (the G4.6–G4.12 grade bug only showed in player builds): every card, soft and candy (F7), 1920×1080 and 1280×720, into `docs/proof/p7/polish/after/`.
   - Lid Flip FPS in Chrome no worse than the P6 build (same machine, same run, both measured).
   - Self-review against a checklist in the proof:
     - every screen has an enter animation;
     - no placeholder text;
     - no tofu glyphs;
     - readable at 720p;
     - reduced motion works;
     - soft-look rules (doc 17 §3.0);
     - originality guardrails (doc 17 §1).

#### A2. Web app polish ⛳ (D-WEB) (`web/app`, Vite + TS, no framework)
Goal: the web app looks like it belongs to the fallrush.fun landing and gives judges a **shareable, verifiable receipt**.

1. **Base:**
   - tokens in `style.css` matching `web/site/src/styles.css` (colours, Lilita One / Varela Round, radius, shadow, the soft palette); copy the values, don't import from `web/site`;
   - `.card`, `.tile`, `.badge`, `.stat`, `.chip`, `.stepper` components;
   - CSS keyframes (enter, stagger via `--i`, pulse) and a tiny `lib/motion.ts` count-up helper;
   - `prefers-reduced-motion` honoured;
   - one shared header (logo → fallrush.fun, Play, Live, Season, Me) and footer ("Built on Solana · MagicBlock").
2. **Pages:**
   - **`/play`:**
     - a capsule loader with real progress and the download size;
     - check `Cache-Control` / `Content-Encoding` for the Unity `.data` / `.wasm` through the front Worker and nginx, so the second visit is near-instant and the first is compressed;
     - record the before / after load times. P6 measured stranger → lobby at 29–46 s on a slow uplink.
   - **`/live`** (phone-first, 390×844):
     - animated odds bars and the sealed chip;
     - drop cards with a cooldown ring;
     - feed items that slide in;
     - a clear "you bet X on Y" card;
     - keep the 2D Final view (`lib/view2d.ts`) smooth.
   - **`/receipt/:match`** (the most important page for judges), a **receipt card**:
     - the champion, the 4 rounds, minted asset tiles;
     - a "Verified" stepper (Rollup ✓ → Solana ✓ → Replay ✓, where the replay check runs in the page or links to `/replay`);
     - market results with payouts, achievements;
     - a "Proof" drawer holding the explorer links and hashes (no more raw hash dumps in the main view);
     - **"Download card"**: a canvas PNG, the same approach as `web/site/src/three/poster.ts`, rewritten in plain TS;
     - OG/Twitter meta (title, description, a static OG image) so a shared link previews well;
     - aborted matches keep the "aborted · every stake refunded" card.
   - **`/me`:**
     - asset tiles with **2D SVG capsule art** in rarity colours (Crown tile special), name, rarity, explorer link, and an "equip" toggle where it exists today;
     - chips and season stats as stat cards;
     - key backup / restore and cash-out as clear cards with confirmations.
   - **`/season`:** a top-3 podium for each board, then the table, your rank, a live countdown, and the on-chain account link as today.
   - **`/arenas`** (web app home) and **`/replay`:** tidy cards and controls, no layout jumps.
3. **Verify A2:**
   - Playwright screenshots of every page at 390×844 and 1440×900 into `docs/proof/p7/polish/after/web/`;
   - **0 console errors**;
   - `/play` and `/live` load measured;
   - `pnpm -C web/app build` green;
   - the `web/site` landing **unchanged** (diff it).

#### A3. Hardening ⛳ (D-LOAD, D-CHAOS) — doc 19 P7.1 / P7.2, scaled to what's deployed
1. **Load:**
   - Run both public arenas at once (gs1 + gs2): bots plus **2–3 scripted humans each** (the `tools/p6/crown-harness.ts` / `scripts/p6-two-humans.sh` pattern), plus **~30 headless spectator bettors** (Node, the chain bundle, own burners; open markets + sealed Champion bets + Chaos Drops), back to back for **30 minutes**.
   - Run it from wherever keeps the M3 caps and RPC limits (the Mac is preferred; one heavy job).
   - Record:
     - CU per tick (max / p95);
     - missed or late ticks, and the on-chain tick gap p95;
     - input confirm time p50 / p95;
     - bet / drop confirm time;
     - 429 count;
     - TEE book fill (32 entries per match: what happens at 33?);
     - VPS CPU / memory per container;
     - keeper memory;
     - matches finished vs aborted;
     - payouts = hand calc on a sample.
   - Table in the proof.
2. **Chaos tests.** Each one gets a documented recovery and its measured time:
   - kill a keeper mid-Final;
   - kill the gs1 game server mid-round 2 (what do the humans see? does the match abort cleanly with refunds?);
   - throttle a client (Chrome "Slow 4G" + 150 ms): corrections, feel;
   - revoke a session mid-match;
   - force a follow-up (Magic Action) failure → keeper retry;
   - make devnet-tee unreachable for a Final → the Path B self-reveal settles;
   - indexer down → the game still plays and the receipt pages degrade gracefully.
3. **Fix what they find**, plus the known items:
   - match 3's first-tick replay mismatch (archive timing: make the indexer archive the tick-0 snapshot after the first input batch, or record inputs from tick 0; then `/replay` must pass on a 2-human match);
   - the `?autobet=` double fire (test hook);
   - the 429s on many clients from one IP: backoff, jitter, `getMultipleAccounts`, indexer caching (D-RPC).
4. **Judging readiness** (the URL must stay up through the results, ~3–5 days after the 27th):
   - **A watchdog:** a `fallrush-watchdog` container, or a systemd timer named `fallrush-*`. Every 60 s it hits `/api/status` and each service's health. It restarts **only our** containers after 3 failures, logs to `/opt/fallrush/logs/watchdog.log` (rotated), and never touches anything else.
   - Check that the **Classic fallback** works and is reachable from the UI if the rollup is down (OR-8).
   - Top up the guest-gas funder to cover **≥ 100 guests**: ≈ 1.2 SOL in the funder at 0.012 SOL each, so about +0.65 SOL. Measure the real per-guest cost first; if it is lower, size the top-up to it. Count it in the P7 budget (D-SOL).
   - Make sure the TEE book reopen, the season keeper and the indexer prune all run unattended for 5 days (log sizes, disk, memory).
5. **Deploy** under §2.3: fresh snapshot → neighbours → one service at a time, between matches → health + neighbours after each.
6. **Waitlist CORS:**
   - Redeploy the waitlist Worker with `ALLOWED_ORIGINS` including `https://fallrush.fun` and `https://www.fallrush.fun` (`scripts/landing-deploy-cf.sh`), and check the Turnstile hostnames.
   - Then sign up from fallrush.fun once, using a test address you control, and delete that row afterwards if the tooling allows.
   - **If the permission system denies it again, don't work around it.** Add the exact command to `docs/prompts/P7-USER-STEPS.md` under "Run these yourself" and move on.

#### A4. Playtest kit → **USER GATE 1**
Write `docs/proof/p7/playtest-kit.md`, ready for the user to share as-is:

1. **For testers:**
   - the link (fallrush.fun → Play);
   - "how to play / bet / spectate / what a sealed bet is" in ≤ 120 words, plus controls;
   - "use a phone for `/live`";
   - "if you have Phantom, try claiming on `/me`".
2. **A feedback form** to copy into Google Forms or a chat:
   - device / browser;
   - time to get into the lobby;
   - bugs (what, when, screenshot);
   - feel 1–5, the cards / UI 1–5, markets fun 1–5, Chaos Drops fun 1–5;
   - "the one thing to fix".
3. **The run plan:**
   - 3–4 matches;
   - how many on phones and on Phantom;
   - the spectators bet on `/live`;
   - one tester deliberately closes their tab during a Final (the reveal path).
4. **What you'll watch live:** `/api/status`, keeper / gs logs, the error list. Prepare a small `scripts/p7-watch.sh` that tails these.

Then **stop and report to the user** (§5, short), and wait for the "playtest 1 notes" message.

### Part B — after playtest 1

#### B1. Fixes
- Turn the notes (pasted text or a file path) plus your own logs from the session into `docs/proof/p7/playtest1.md`: a triaged list (P0 = broken / blocks play, P1 = clearly bad, P2 = nice to have), each with its cause.
- **Fix all P0 and P1** within the §2.2 rules.
  - A feel complaint about the motor is **not** fixed by retuning: record it for after the hackathon.
  - The deferred G4.13 items stay in P8.
- Rebuild where needed, redeploy under §2.3, and re-verify (EditMode, p1-check, one public match, screenshots of what changed).

#### B2. **USER GATE 2**
Give the user the playtest 2 checklist (in `P7-USER-STEPS.md` §3):
- two clean full matches;
- what to record (OBS 1080p60, game + `/live` on a phone + an explorer tab);
- which moments the doc 19 §7 storyboard needs (deal + VRF tx, round 1 + F3 tick health, a phone bet + a drop, the Final + settlement, the crown + capsules, the receipt + replay ✓).

Then wait for "playtest 2 done". **If the notes don't come, go on with Part C anyway** (D-PLAYTEST-LATE).

### Part C — launch prep (no video; the user makes it later)
1. **`PROOF.md`** (repo root): one explorer link per system, each **re-opened and checked live today**, with a one-line "what this proves". Start from P6's "For P7" draft + P5's list. The list:
   - arena delegate;
   - VRF deal callback;
   - a `tick()` tx (with its CU);
   - an input tx signed by a session key;
   - an open bet;
   - a **sealed bet** (TEE book close + reveal);
   - a Chaos Drop;
   - a round commit;
   - finalize + the Magic Action;
   - the **Crown** and a **capsule** Core asset (created in the undelegation tx, fee payer = validator);
   - a **season trophy**;
   - the RUSH mint;
   - the program address.

   Add "how to verify it yourself" (the replay tool) and the honest trust line (what is and isn't proven, doc 19 §1).
2. **`README.md`, rewritten for judges** (first screen = pitch + play link + a GIF or screenshot from the new UI). It covers:
   - "Play now" (fallrush.fun) and `<VIDEO_URL>` (placeholder; the user fills it in);
   - what it is in 3 lines;
   - **what runs on MagicBlock**, as a table: ER tick, session keys, VRF, Private ER sealed bets, Magic Actions, commits;
   - the architecture diagram (Mermaid, from doc 19 §1) and the trust model;
   - "Replay a match yourself" (exact commands);
   - **`rush-core` as a reusable "3D character controller for Ephemeral Rollups"** (no_std integer Rust, native = WASM = sBPF);
   - how to run locally, links to PROOF.md and `docs/proof/`, the roadmap (mainnet + legal review, permissionless arenas, community maps, the full 4 rounds on-chain), team / credits, licences.
   - Add **`LICENSE`** (MIT for code) and check `ASSET_LICENSES.md` covers every font, audio file and model.
3. **`docs/proof/p7/submission.md`**, ready to paste into build.magicblock.app:
   - project name, one-line and long description, the "Entertainment Finance" angle (spectator markets, sealed bets, Chaos Drops, assets you own);
   - the explorer link to lead with, the program address;
   - every form field with its text, an X post draft (≤ 280 chars + a thread of 4);
   - **leave the prize wallet and the video URL blank** for the user.
4. **Public-repo safety scan** ⛳ (D-SECRET) — report only, publish nothing:
   - scan the **whole git history** (`git log -p --all`, plus `git rev-list --objects --all` for file names) for keypair JSON arrays, `.env`, `id.json`, private keys / seeds, the real VPS IP (from `vps.env`), the front key, Worker secrets, `*.local.md`, emails;
   - check the repo size and any large blobs (`git count-objects -vH`, the biggest objects); `.gitignore` covers `Library/`, builds, `.keys/`;
   - write the results to `docs/proof/p7/secret-scan.md`, **without quoting the secrets**: path + commit + type only.
   - If history is dirty, prepare a **clean-history option** (e.g. a fresh orphan branch `public` built from the tree at the RC commit, with the bad files removed and proof docs kept) and explain its trade-off in `P7-USER-STEPS.md`. **Never rewrite `main`, and never push.**
5. **Release-candidate freeze:**
   - record the RC commit, the WebGL build hash and size, each VPS container's image id, the program data hash, and the map hashes in `docs/proof/P7-launch.md`;
   - after the freeze, only P0 fixes (D-FREEZE).
   - Run one final public match on the RC and record it (links).
6. **Proof + docs + merge:**
   - `docs/proof/P7-launch.md`:
     - §1 polish (before / after screenshots, checklist, FPS);
     - §2 web;
     - §3 load table;
     - §4 chaos table;
     - §5 fixes (incl. replay first-tick);
     - §6 playtests 1 / 2;
     - §7 VPS (snapshot ids, changes, neighbours, watchdog);
     - §8 SOL;
     - §9 secret scan summary;
     - §10 RC;
     - §11 UNVERIFIED;
     - "For judging week".
   - Update `docs/15` (OC-29…), `docs/19` (P7 as built), `docs/13` (watchdog), `PROJECT_STATUS.md`. One final doc commit.
   - **Merge:** in the main checkout, `git merge --no-ff p7` (`merge: p7 (final polish, hardening, launch prep)`). Then run the main checkout's EditMode + `scripts/p1-check.sh` under the lock. Fix on `p7` and merge again if needed. **Keep the worktree. Never push.**
7. Update `docs/prompts/P7-USER-STEPS.md` with the final facts (RC commit, the exact push commands, which branch to publish (`main` or `public`), the commands the user must run themselves).

---

## 4. Decision policy (decide yourself; never stop to ask, except at the two USER GATES)

**Priority order:**
1. Correctness, fund safety, the VPS neighbours' health, the live game staying up.
2. Nothing a judge sees looks broken or unfinished.
3. MagicBlock doing real, visible work.
4. The player / spectator experience.
5. Schedule and cost.

**For every ⛳:** measure the options, pick by priority, implement, and record an `OC-n` row + a proof paragraph.

| ID | If… | Then do this |
|---|---|---|
| D-POLISH | a polish idea touches gameplay, timing, hashes, the motor or `rush-core` | drop it, or do the purely visual part only |
| D-WEB | a page needs a component library or framework | write it in plain TS + CSS; no new framework |
| D-GLYPH | a glyph renders as a box in the player build | use a sprite from the UI icon atlas; never rely on emoji fonts in WebGL |
| D-PERF | the polished UI costs > 3 FPS or > 0.5 MB of build size | simplify the card (no shadow layers, fewer graphics, pooled objects); measure again |
| D-LOAD | the RPC or the Mac can't carry the full load profile | scale down (fewer bettors, 1 arena), record the ceiling you reached and why, and size the playtest to it |
| D-CHAOS | a chaos test exposes a failure without a recovery | fix it if it can happen during judging; otherwise document the recovery steps in "For judging week" |
| D-RPC | public devnet RPC 429s | backoff + jitter, ≤ 1.5 tx/s base per process, `getMultipleAccounts`, indexer caching. No paid RPC accounts |
| D-PLAYTEST-LATE | the user's notes haven't arrived by Thu 26 06:00 UTC | continue with Part C; fix from your own logs; fold in their notes when they arrive (P0 only after the RC freeze) |
| D-FREEZE | a bug is found after the RC freeze | P0 only (broken for a judge); fix, rebuild, re-verify, re-record the RC |
| D-SECRET | the history contains a secret or the real IP | don't rewrite; prepare the clean-history `public` branch option + rotate advice (what to rotate, how) for the user |
| D-SOL | the 1.5 SOL budget would run out | fewer load bettors; close test accounts; never let admin drop below 1.0 SOL |
| D-DISK | free space < 5 GB | free only from the §2.1 list, in that order |
| D-NEIGHBOUR | finity or Ovelo changed after one of your changes | roll back your last change; if not restored, restore the snapshot; continue only once both are healthy |
| D-LATE | behind the §1 schedule | cut in this order: (1) `/replay` styling; (2) the spectate card; (3) camera punches; (4) leaderboard rank-slide; (5) `/season` podium; (6) `/me` SVG art (keep tiles). **Never cut:** the Deal / result / champion / receipt / bet cards, the glyph fix, the dev-button clean-up, the web receipt card, the load + chaos tables, the watchdog, PROOF.md, README, the submission pack, the secret scan, the RC freeze, the merge |
| D-OTHER | anything else | apply the priority order and record it |

**Hard rules (not decisions):**
- Never push, never add a git remote, never make anything public, never submit a form, never post on X.
- Never touch `../FallRush-trailer` or `../FallRush-landing`. Never change `web/site` source (read it for reference only).
- Never touch the `finity` site, Ovelo, cloudflared, or any non-`fallrush-*` resource on the VPS. No host-wide Docker prune.
- Never commit keypairs, secrets or the real IP.
- Never deploy a new program ID.
- Never change Classic gameplay, the signed-off motor feel, map hashes or `rush-core`.
- Don't make the demo video (the user does it later); only leave `<VIDEO_URL>` placeholders.

## 5. Reports to the user (short, plain words)
**At USER GATE 1:**
- what the polish changed (3–5 lines + the path to the before / after screenshots);
- the load and chaos results in one line each;
- the public URL is up, and the neighbours are fine;
- the playtest kit path and the exact message to send back.

**At USER GATE 2:** what was fixed from playtest 1, what wasn't and why, and the playtest 2 checklist.

**At the end:**
- PROOF.md / README / submission pack paths;
- the secret-scan result (clean / dirty + the option);
- the RC commit, and that `p7` was merged into `main` (worktree kept);
- SOL used and left, and disk left;
- what's UNVERIFIED;
- **the user's remaining steps, in order** (from `P7-USER-STEPS.md`): video, push, submit, judging-week checks.
