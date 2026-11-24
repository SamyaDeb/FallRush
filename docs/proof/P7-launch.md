# P7 · final polish, hardening, launch prep (proof)

Branch `p7` from `main` @ `6a015e5` (P0–P6 merged). Worktree `../FallRush-p7`. Decisions OC-29 … in [`p7/decisions.md`](p7/decisions.md) (copied into doc 15). Every number below was measured in this run unless marked **UNVERIFIED**.

## 1. In-game polish (A1)

**What changed** (all render / UI; no map, rule, timing, motor, rush-core or transcript change):
- **`UiMotion` + `UiAnim`** (OC-29): enter (scale 0.92 → 1 back-out + fade, 0.22 s), exit 0.15 s, 40 ms row stagger, bump 1 → 1.12 → 1 (0.18 s), slide-in 0.25 s, shake, count-up ≤ 0.6 s; unscaled time; reduced motion = fades only. Every panel goes through `SetVisible` (show / hide), rows animate a non-layout "face".
- **One card builder** (`UiKit.MakeCard`, S / M / L): soft drop shadow, saturated rim, pastel body (`PaletteTokens.SoftTopHex`), 2-tone bevel, rim ribbon with an icon and the outlined Lilita title. Plain panels (menu, settings, lobby screen) became soft cream panels with a lavender rim.
- **Designed cards**: the **Deal** (4 round cards pop out of the capsule machine, 0.4 s apart: map, mode icon, target, the round's market; the provenance chip says "Rounds 1–3: Classic deal (game server seed)" and turns into **"VRF ✓ MagicBlock VRF seeds the on-chain Final"** with a *View the VRF request* button once the arena keeper's `request_deal` lands (OC-32)); the **round result** (rows stagger in, your row highlighted, then your own Qualified / Eliminated card with place, time or score); the **leaderboard** (stagger, "rounds survived" count-up, changed ranks slide, the next map as a mini card, a working *Bet on /live* button instead of the dead "Fan Picks: coming soon"); the **CHAMPION card** after the crown drop (name, rounds survived, Final time, "+1 crown this season", badges; confetti + a camera punch); the **receipt** (Rollup ✓ → connector → Solana ✓ → minted asset tiles → badges → *View on explorer* / *Open receipt*; practice: a clear "not recorded" card); the **bet panel** (market tabs, odds bars that ease to the stake share, the sealed chip, a stake stepper with a counting payout preview, confirm bump / refused shake); **Chaos Drop** cards with price + cooldown ring and a banner that slides in ("<buyer> dropped Lid frenzy!") with a camera punch; the **lobby** (rows stagger in as players join, the count bumps); the **spectate card** (who, *Tab: next*, players left).
- **HUD**: the timer pulses and turns amber ≤ 10 s, red ≤ 5 s with a tick each second; the qualified counter bumps; LAGGING is no longer shown for the Classic link while the Final runs on the rollup.
- **Juice** (visual only, off with reduced motion, no hit-stop / time-scale change): camera punches on eliminated (claw grab), qualified, a Chaos Drop, the crown landing; UI sounds from the existing cues.
- **Glyphs** (D-GLYPH, OC-30): a code-drawn icon atlas (`UiIcons`, 17 icons) replaces every emoji; an EditMode test fails on any character outside the font chain.
- **Clean-up** (OC-31): dev buttons behind `DevMenu` (`?dev=1`), "On-chain Final only (dev)", "Enter the arena", no M-number / TODO / greybox / "coming soon" string a player can see, `WebGLTemplates/xNFT` deleted, the menu title / panel animate in, settings open and close with UiMotion.

**Bugs found by playing the new build on the public stack, all fixed**: the bet panel covered alive players' view in rounds 1–3 (OC-33, visible in the P6 before-shot 05); `ChainEconomy` threw "Collection was modified" 4× per page load (OC-35); the `?autobet=` double fire was two hooks (OC-35); the Chaos Drop bar stayed up into the next lobby; the on-chain receipt opened only ~40 s after the Final (now at the Final's end, then the steps tick in); the CHAMPION card was hidden by the panel switcher / under the receipt card; the receipt now steps aside when the next match's rounds start; public matches were labelled "practice" (the Classic server runs them with the practice flag; OC-32).

| Check | Result |
|---|---|
| EditMode | **432 tests: 431 passed, 0 failed, 1 skipped** (P3 emulator binary not built in this worktree, as in P6); +12 P7 tests (UiMotion curves, CountUp, card sizes + hues, MakeCard layers, receipt / chip layout heights, icon atlas, glyph coverage). The layout test also caught `ReceiptCard.Reset()` colliding with Unity's magic `Reset` |
| `scripts/p1-check.sh` | see §10 (run at the merge) |
| Map hashes + `rush_core.wasm` | unchanged (§10: byte-compared) |
| WebGL release | **22,838,711 B** (build #6, live; ≤ 25 MB; P6 22,833,701 B: +5 KB, D-PERF ✓), Decompression Fallback on; six release builds were made in P7 (the prompt asked for one: each public test match through the new build found bugs listed above, fixed and rebuilt), one is kept |
| Lid Flip FPS, Chrome, this Mac | P6 build (before run): fpsMedian 59, 1 % low 53 · P7 build (after runs): **59 / 53** (vsync-bound; draw calls median 77–122 with cards up, budget unchanged) |
| Before / after screenshots | `p7/polish/before/{game,web}/`, `p7/polish/after/{game,web}/` (player builds only) |

**Self-review checklist** (from the after-screenshots): every screen has an enter animation ✓ · no placeholder text ✓ (VRF chip states what is true at each moment) · no tofu glyphs ✓ (atlas icons; Lilita / Varela / Noto Symbols cover the rest; one TMP warning remains: TMP falls back from "…" to truncate in two labels, harmless) · readable at 720p ✓ (body ≥ 12 units at 1280 × 720) · reduced motion ✓ (fades only; unit-tested curves) · soft-look rules ✓ (pastel bodies S 0.14–0.22, saturated rims; cream panels, never pure white) · originality ✓ (our capsule ribbon cards, our crown; no banner / result layout from another game).

## 2. Web app (A2)
- Tokens copied from the landing (`web/site/src/styles.css`: ink, magenta, sun, grape, mint, Lilita One / Varela Round, the chunky ink drop) + the soft pastels; components `.card` (ribbon header), `.tile`, `.badge`, `.stat`, `.chip`, `.stepper`, `.verify`, odds bars, cooldown rings, drawer, podium; CSS keyframes (enter, `--i` stagger, pulse, bump, shake) + `lib/motion.ts` count-up; `prefers-reduced-motion` honoured. One shared header (logo → fallrush.fun, Play, Live, Season, Me) and footer ("Built on Solana · MagicBlock Ephemeral Rollups"). No framework (Vite + TS, no React).
- **`/receipt/:match`**: the receipt card (champion tile with capsule art, the **Verified** stepper *Rollup ✓ → Solana ✓ → Replay ✓* where the **replay check runs in the page** with `rush_core.wasm` (phones: on a tap), the 4 rounds with their trust label, minted asset tiles, market results **with payouts**, sealed bets, achievements as badges, Chaos Drops, and a **Proof drawer** with every explorer link and hash), **Download card** (1080 × 1350 PNG drawn on a canvas), *Copy link*, OG / Twitter meta + a static `og.png`; aborted matches keep the "aborted · every stake refunded" chip.
- **`/live`** (phone-first): markets built once and updated in place (odds bars animate, nothing jumps), the sealed chip, a stake stepper, drop cards with cooldown rings, a "you bet X on Y" card, a toast on every drop, feed items slide in.
- **`/me`**: stat cards (chips, real RUSH, crowns, matches), asset tiles with SVG capsule art in rarity colours (Crown special), equip / claim, cash-out and Phantom as cards, the key backup as a warning card with confirmations.
- **`/season`**: a top-3 podium per board, then the table, your stats as cards, a live countdown. **`/arenas`**, **`/replay`** (fixed-size controls), **`/play`** (a capsule loader filling with the real download progress: "downloading 12.3 / 21.4 MB").
- Unity files through the front Worker: `Cache-Control: public, max-age=3600`, `cf-cache-status: DYNAMIC` (the Worker passes through; the `.unityweb` files are Brotli bytes decoded by Unity, fetched in 5 parallel ranges). A second visit within an hour is served from the browser cache.
- Checks: `tsc --noEmit` clean, `vite build` green, **0 console errors** on every page (before and after, 390 × 844 and 1440 × 900), `web/site` unchanged (no file under `web/site/src` in the P7 diff).

## 3. Load (A3.1, D-LOAD) — `p7/load/run1/`
Public stack, **both arenas at once, back to back for 30 min** (19:55–20:25 UTC + the last matches), from this Mac: one WebGL player per arena (restarted after every archived match) + **30 headless spectator bettors** (the chain bundle, own burners + session keys; open bets, sealed Champion bets, Chaos Drops).

| Metric | Result |
|---|---|
| Matches | **15** (8 with the on-chain Final completed; 7 aborted = a harness artifact: the player script restarted Chrome after each archive, by which time the Classic server had seated it in the next match, so that match lost its only human in round 1 → Classic abort → on-chain abort, **every market refunded**; 7 restarts = 7 aborts) |
| `tick()` CU | max **253,760**; p95 per match median 188,033, worst 226,514 (budget 400k) |
| On-chain tick gap p95 | 41–43 ms (30 Hz = 33 ms); real time ×0.9996–1.0002; **0 stall restarts** |
| Input confirm (WebGL players) | p50 **218 ms** (median of the samples), p95 worst 1,086 ms |
| Open bets | 63 ok, p50 352 ms / p95 1,208 ms; 30 refused by the program (27 `MarketClosed` = the window closed between read and send, 3 `InsufficientChips`) |
| Sealed Champion bets | **144 ok**, p50 1,342 ms / p95 3,765 ms (TEE pick + rollup commit); 5 refused (4 `InsufficientChips`, 1 `SealedState`: the market closed meanwhile); every sealed bet in a finished match revealed (0 forfeits) |
| Chaos Drops | 12 ok, p50 894 ms; 8 refused (7 `DropCooldown`, 1 `DropCap`): the program's rules working |
| Payouts = hand calc | **33/33 settled markets** match pari-mutuel (pool × stake ÷ winning stake, integer) |
| 429s | **8,269** absorbed by the bundle's backoff (all from this one IP); a first attempt with concurrent onboarding got 3/30 wallets ready (370 × 429) → onboarding made sequential: **30/30** (D-LOAD, OC-39) |
| VPS peaks | keepers ≤ 16.4 % / 88 MiB, indexer ≤ 15.4 % / 65 MiB, gs ≤ 9.7 % / 160 MiB, web ≤ 3.5 %; load1 ≤ 1.02 |
| Neighbours | finity 200 on 83/84 samples (1 sample timed out at 8 s from this Mac at peak Mac load); neighbour checks before/after 200; Ovelo healthy |
| TEE book fill | see §3.1 |

Not reached (D-LOAD): "2–3 scripted humans per arena" (this 8 GB Mac runs 2 WebGL clients next to 30 bettors; extra humans would be more Chrome instances) and 4 arenas × 12 clients (only 2 arenas are deployed). Per-guest onboarding cost measured: **0.0111 SOL** (session key + chips account + fees), so the 0.012 SOL guest gas is right-sized.

### 3.1 Sealed book capacity (33rd entry)
`p7/load/fill/`: 34 bettors (30 + 4 new wallets), all sealing on arena 3 in the same match, plus a WebGL player. **Twice (two matches): 32 accepted, the 33rd and 34th refused by the program with `SealedFull` (6043)**, no stake taken; the TEE picks of all 32 landed. Match `7PNcMz6D…` (completed): **32/32 revealed from the private book, 0 forfeits, the Champion market paid 556.999998 of 557 RUSH** (integer rounding); match `7aKSTZvx…` (aborted by the harness restart, as in §3): 32/32 revealed, the 495 RUSH pool refunded exactly. The 32-entry book is a hard, clean limit; a bettor who hits it sees "sealed bet refused" and can still bet the open markets.

## 4. Chaos (A3.2) — `p7/chaos/`
| Test | What happened | Recovery (measured) |
|---|---|---|
| **Kill a keeper mid-Final** (keeper-1 restarted the moment it logged `final`, a throttled 1080p WebGL player in the match) | the timed task kept ticking on the rollup; the new keeper process resumed from on-chain state (`resume → final → sealed_finish → settle → receipt → capsules → finalize → rewards → season`) | **92 s** to the archived, settled match (P5 74 s, P6 80 s) |
| **Kill gs1 / gs2 mid-round 2** | the players' WebSocket drops; the server is back in the lobby in **6 s** and reconnecting clients are seated in the new lobby. **First run (gs2): the keeper's bridge waited for events from the dead match (up to 30 min), the arena recorded nothing → fixed (OC-40).** Re-run on gs1 with the fix: the keeper took the Classic-abort path **17 s** after the restart | **27 s** restart → on-chain abort + every market refunded exactly (paid = pool, label "aborted"); the stuck first-run match was aborted and refunded on the redeploy |
| **Throttle a client** (Chrome "Slow 4G" 562.5 ms + 150 ms, 1.6 / 0.75 Mbps; DevTools throttles HTTP, not WebSocket frames) | Classic rounds unaffected (corrections 1 per 5 s, max 31 cm); on-chain inputs (HTTP RPC) confirm **p50 1,216 / p95 1,650 ms** (unthrottled p50 218 ms), RTT 766 ms, **0 rejected inputs**, the Final played to the end | none needed (the 4-tick input lead + the late-input window absorb it) |
| **Revoke a session mid-match** (a bettor wallet: bet with the session key → `revoke` on Solana → bet again → new session) | after the revoke the bundle signs with the guest's own burner (no popup) and the bet confirms in **288 ms**; re-creating the session failed only because that test wallet had no SOL left for the new token's rent | instant (signer fallback); a guest with SOL re-creates the session on the next action |
| **Force a Magic Action failure → keeper retry** | not forced on devnet in P7 (it needs the action's escrow drained mid-match on the public stack). The path is the P5/P6 one: the keeper reads `mintedMask` after finalize and mints what the action did not (`mint_rewards`, idempotent; P5 match 4 "action (after resume)", P6 keeper kills at receipt / finalize recovered in 12–13 s) | documented; **UNVERIFIED** as a forced failure |
| **devnet-tee unreachable for a Final** (keeper-2 started with `TEE_URL=devnet-tee.invalid`; match `7VYVju1j…`, a WebGL player placed a sealed bet) | the keeper could not open or read the private book (no `book_open` / `book_close`, 0 reveals from the book); the market closed on schedule; **the player's browser revealed its own pick (Path B commit-reveal) 15.1 s after the close**; the program settled: 1/1 revealed, 0 forfeits, the Champion market paid in full, the match completed and archived | 15 s (self-reveal at close + 6 s, then the program's settle); a bettor who closed the tab before the reveal would forfeit (documented in the playtest kit) |
| **Indexer down** (stopped mid-match) | `/api/status` 502, the web pages load (static) and say "status unavailable", the match in progress continues (the keepers archive to disk; the game servers don't use the indexer); new guests can't get gas while it is down. First run: the watchdog restarted it 2 min 8 s after the stop, **but also both game servers** (their check read `/api/status`): a live match aborted, refunded (OC-43). Fixed and re-run: **only the indexer was restarted** (2 min 31 s after the stop, healthy at 3 min 17 s), the match in progress finished normally | ≤ 3 min 20 s (3 checks × 60 s + start) |

## 5. Fixes
- Replay first-tick (OC-34): the Final's tick 0 → 1 is the program's map load; the verifier starts after it. P6 match 3 (2 humans): **2728/2728 byte-exact, transcript ✓** (`node tools/p7/replay-check.mjs BoZFkeAsWHiW4WtZGY3kgo4zBx1FcGbHyTgYL51yA3mN`).
- `?autobet=` double fire (OC-35), `ChainEconomy` iteration exception (OC-35), D-RPC 429 backoff (OC-36), the UI bugs in §1.

## 6. Playtests
*(playtest 1 / 2: after the user's sessions)*

## 7. VPS
- Snapshot **472001** (Hostinger VM 2163833) at 2026-11-23 19:51:15 UTC before the first change (expires 2026-11-24 19:51 UTC).
- Order (`scripts/p7-vps-deploy.sh`): stage the node image `fallrush-node:p7` + web → web (`web.p6bak` kept, `web/site` kept) → indexer → keeper-1 → gs2 + keeper-2 → the watchdog. One service at a time, none while a human was in a match; neighbours after every step (`p7/vps/neighbours.log`): finity https 200 / http 301, finity config md5 `c6a6cca8…` unchanged, raw-IP 404 / 200 unchanged, Ovelo `Up (healthy)`. Nothing rolled back. gs1 / gs2 images unchanged (no server code change).
- **Watchdog** (OC-37): `fallrush-watchdog.timer` (60 s) + `.service` (`CPUQuota=10 %`, `MemoryMax=64M`), restarts only `fallrush-*` after 3 failed checks, log `/opt/fallrush/logs/watchdog.log`.

## 8. SOL
See [`p7/sol.md`](p7/sol.md).

## 9. Secret scan
See [`p7/secret-scan.md`](p7/secret-scan.md): history clean of keys and of the server address; the owner's personal e-mail is in every commit's author field (decision for the owner, options in the user steps).

## 10. Release candidate
*(at the freeze)*

## 11. UNVERIFIED
*(at the end)*

## For judging week
*(at the end)*

## UX rework (P7-UX, OC-44…48) — deployed 2026-11-24 08:54 UTC
- Build #9: WebGL 22,848,905 B; EditMode 449 (448 passed, 0 failed); Linux server image `fallrush-gameserver:p7ux` (protocol v7).
- Local stack test (Mac dev server + indexer + /play, two browsers): host creates a room → the second browser joins by the invite link → a third without the code is refused ("hosting a private room") → the host's START MATCH runs the Deal and round 1 with both players + bots. Found + fixed on the way: the room race (the host connected before the server's 1 s room poll: the server now re-reads the room file at approval), a pointer-lock race (a move + click in one frame took the lock and the button never clicked: fresh UI raycast at the click), the turntable's outline pass (a dark arc).
- VPS (M3 rules): snapshot 472001 re-taken 08:50 UTC (expires 2026-11-25 08:50); order indexer → gs2 → gs1 (image `:p7ux`, rooms mount) → `fallrush-gs3` (Classic-only room server, 127.0.0.1:7779, 5.6 GB available before) → nginx `/gs3` location on the gs1 name (`nginx -t`, reload; backup `/opt/fallrush/fallrush-p5.conf.p7ux-bak`) → web. Neighbours checked before and after every step: finity 200/301, conf md5 unchanged, Ovelo up. `fallrush-gameserver:p5` kept for a rollback.
- Live smoke on https://fallrush.fun/play: room 1 → arena gs1 (gs2 stays public), room 2 → gs3 via `wss://gs1…/gs3`, Quick Match → gs2 "Rookie lobby" with the 20 s countdown; every tab closed before a match started (nothing recorded on-chain).

## UX2 polish (OC-49, OC-50) — deployed 2026-11-24 10:07 UTC (web only, WebGL #11, 22,847,020 B)
- Audit: `p7/ux2/audit.md` (13 rows, before/after shots from the live site and from build #10 served locally against the live API). EditMode 453 (452 passed, 0 failed).
- The Final spectator floor bug (OC-49) is fixed and verified on a live hybrid Final; the HUD has no pills (OC-50).
- Two recorded matches for the audit (bots + 2 guests each). Neighbours before / after the web switch unchanged.
- 10:33 UTC: web #13 (22,843,334 B; EditMode 452/453, 0 failed) closes the three open UX2 items: the spectator camera skips a watched player who fell out of the Final (A14), the end screen shows "ON-CHAIN RECEIPT" (left) and "FINAL STANDINGS" (right) top-aligned instead of two "MATCH RECEIPT" cards (A15, `p7/ux2/after/v12-S-102517-Done4.jpg`), the spectator camera sits closer and looks down from ≥ 32° except on Goo Tower (A11). One more recorded check match. Neighbours unchanged.
