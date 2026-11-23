# P6 — privacy (sealed bets) + seasons + achievements (proof)

Branch `p6` (worktree `../FallRush-p6`, off `main` @ `eda9adb`; main merged in at `319552e` for the landing). Prompt
`docs/prompts/P6-PROMPT.md` (autonomous run, VPS authorised under the M3 rules). A number is **measured** only where
this file says so; everything else is **UNVERIFIED** (§9). Devnet only; RUSH has no real-money value (OC-2). The real
IP / hostnames are `<VPS_IP>` / `<VPS_IP-dashed>` here.

**Summary.** The Champion market is now **sealed with a MagicBlock Private Ephemeral Rollup** (Path A, OC-21): the
pick sits in a private book on the TEE rollup (`devnet-tee`) that nobody can read, not even our game authority; the
stake + a sha256 commitment sit on the public rollup next to the chips; at close the book turns public and the program
checks every reveal against its commitment, so settlement and payouts are unchanged. Commit-reveal (Path B) is built
in as the fallback (the bettor's saved salt). Sealed bets work from the WebGL game (C# bet panel path) and from a phone
`/live` page, with the TEE's Intel TDX attestation verified in the browser. **Seasons** (weekly `Season` boards,
`PlayerSeason`, `PlayerProfile`) are recorded on Solana from committed program state only, and a **test season was
closed with real Season Trophy mints**. **Achievements** (first crown, 3-round survivor, perfect predictor, chaos agent)
are bits on `PlayerSeason`. Carry-over: the **Crown via the Magic Action is now proven on devnet** (a scripted human
driven by rush-core's own bot brain won the on-chain Final). P6 is live on the public URL.

## 1. Carry-overs (task 0)
| item | result | evidence |
|---|---|---|
| 0.1 ProjectSettings drift | `webGLDecompressionFallback: 1` committed (`8c75283`). `m_EnableMultiplayerRoles` **stays 0**: it is written by the Unity batch build itself (the Dedicated Server package flips it on *any* batch build here, a WebGL build too, together with `webGLExceptionSupport`); no shipped build uses Multiplayer Roles (WebGL + `Build.LinuxDevServer` are Player-subtarget builds). After every P6 build the two values are reverted in git | `docs/proof/p6/builds.txt` |
| 0.2 aborted bot-only matches | devnet: `6wGmze3K…`, `Gi65GcAP…`, `AZ8A14KG…` all Aborted (status 6), every market PAID with label ABORTED, `paidTotal = pool` (refunds), receipts committed (`tools/p6/aborted-check.ts`). The indexer hides matches with **no human and no on-chain Final** from `/` and `/api/matches` (`?all=1` lists them), `/receipt` shows "aborted · every stake refunded". **D-OTHER (OC-28): the market maker seeds only when the roster has a human** | §6 |
| 0.3 Crown via the Magic Action ⛳ D-CROWN | **✓ proven on devnet in 2 matches (budget 8).** Harness: two scripted humans (`tools/p6/crown-harness.ts`) = the game's chain bundle in Node (own burner + session key, `join_lobby` in a Classic finalist's slot, session-key `submit_input`) whose commands come from rush-core's `bots::bot_input` (tier REFERENCE) on a look-ahead copy of the on-chain World (a feature-gated `rc_bot_input` in a separate harness wasm; the shipped `rush_core.wasm` is unchanged; no gameplay change). Match 2 `ELgfjhXt…`: slot 7 (human) won; **Crown Core asset `Eoi1XaekwQz2CB4BvHCXxK2P4Uuxx1PCn7FXxB8wAi2z` created in tx `2oasoN6DUGxy…` whose fee payer is the validator `MAS1Dt9q…`, logs = undelegation → `MintRewardsAction` → Core create; keeper `mint_rewards` attempts 0**. Both humans' capsules were minted by the action in both matches (minted mask 144) | `docs/proof/p6/crown/` |
| 0.4 two humans in one public match | on-chain: 2 humans (slots 4 + 7) in the same Final, both capsules + the Crown by the action (above). Public gs1 run with two clean Chrome profiles: **✓ match 3** (both seated, both in the Final, both capsules by the action). **> 4 humans' assets cannot occur in the hybrid**: only the 3 finalists join the on-chain lobby, so `human_mask` ≤ 3 bits → ≤ 4 items (Crown + 3 capsules) = `ACTION_MAX_ITEMS`; the keeper retry path stays for anything still owed | §7 |
| 0.5 keeper kills at other stages | **reveal window** (`sealed_reveal`, keeper-1, public match 1): recovered in **80 s** (resume → Final → reveal → finish → settle → receipt → capsules → finalize → rewards → season). **settle** (keeper-2, public match 2): recovered in **12 s**. **receipt** (keeper-1, match 3): **13 s**. **finalize** (keeper-2, match 4): **13 s** | `docs/proof/p6/public/kill.log` |
| 0.6 gas funder top-up | +0.46411 SOL → **0.75 SOL** (≥ 60 guests at 0.012) | `docs/proof/p6/sol.md` |
| 0.7 health after the unattended run | 6/6 `fallrush-*` healthy, 0 restarts, keepers ≤ 95 MiB, VPS disk 6 % used, docker logs rotated (10 MB × 3), archive 35 MB / 15 matches (indexer prunes to 200), per-match jsonl ≈ 10 KB, keeper log 22 KB: nothing grows without bound at P7 scale | `docs/proof/p6/vps/health-start.txt` |

## 2. Sealed bets (task 1, D-SEAL → Path A, OC-21…OC-23)
**Spike 1** (MagicBlock's own deployed `private-counter`, `tools/p6/tee-spike.ts`, `p6/tee-spike.json`):
`verifyTeeRpcIntegrity` 2.0 s, `getAuthToken` 0.3–0.6 s, delegate to the TEE validator `MTEWGuqx…`, permission private
with one member: **member read = data (count 3); outsider token and no token: `value: null`**; Solana base still shows
the pre-delegation copy (count 0); `set_privacy(false)` 0.34 s → outsider reads the data; commit + undelegate 3.1 s.
CORS: `devnet-tee` and Phala's PCCS both send `Access-Control-Allow-Origin: *` (browser-usable).

**Design (program `p6.rs`, one `p6(op, a, b, data[32])` instruction):**
- `SealedBook` (`["sbook", arena]`, delegated to `MTEW…`) + `EphemeralPermission { is_private: true, members: [] }`
  created/updated by the book PDA (hand-encoded Permission Program CPIs, SDK 0.18 layout). `PICK` (TEE): outcome,
  amount, salt, signed by the wallet or its session key.
- `SealedEscrow` (`["sealed", arena]`, devnet-as): `COMMIT` debits the chips and stores `sha256(outcome ‖ amount ‖ salt
  ‖ wallet)`; the Champion window (program state) bounds it; the `Market`'s `_pad[0]` marks it sealed-pending.
- Close: `CLOSE` (permissionless, once the window closed) → `BOOK_CLOSE` (authority, TEE: book public) → `REVEAL`
  (permissionless; the preimage must match) moves each stake into the Champion `Market` as an ordinary bet → `FINISH`
  (all revealed, or after the 20 s window + match over: forfeit to the pool on a program Final / refund otherwise).
- Fallback: the bundle saves (match, outcome, amount, salt, hash) in localStorage **before** sending (and in the `/me`
  key backup); the client self-reveals 6 s into the window if the book reveal did not happen (Path B).

**Spike 2 / go criteria (Fall Rush program on devnet, `p6/sealed-setup.json`):** after a `PICK`, raw reads of the
book from an outsider token, **the bettor's own token, the game authority's token** and no token all return
`value: null`; after `BOOK_CLOSE` an outsider reads the exact entry (salt matches) **279 ms** later. One bug found and
fixed: an existing ephemeral permission has **0 lamports** (its rent sits in the vault), so "lamports == 0" ≠ "absent"
(re-opens sent a second create → `InvalidAccountData`); fixed by checking owner + data (program upgrade 2).

| go criterion | result |
|---|---|
| non-member read empty | ✓ outsider / bettor / game authority / no token: `value: null` (§2 spike 2; public match 2 mid-market read) |
| member / post-reveal read correct | ✓ post-close entry = the pick, salt matches |
| 5/5 sealed bets placed + revealed | ✓ 5/5 (e2e 1, TEE pick 0.24–0.42 s + commit 0.25–0.40 s each), 2/2 (e2e 2), 3/3 (public match 2), 1/1 (public match 1) |
| close → reveal ≤ 30 s | ✓ **1.4 s** and **1.1 s** (close seen → all revealed) |
| WebGL + `/live` | ✓ WebGL: `ChainEconomy.Bet(0, …)` → bundle `sealedBet` (session key, no popup) in public matches 1 + 2; `/live` (phone 390 × 844, clean profile): pick in the book 1.8 s, **TEE attestation verified in the browser (Intel TDX quote via Phala's DCAP verifier, 3.8 s)**, revealed at close (public match 2) |

**Privacy evidence (raw).** Mid-market, e2e 1: book `{"result":{"value":null}}` for an outsider; the escrow on
devnet-as shows only `{wallet, amount, hash, outcome: 255}` per entry. Files: `p6/e2e/*/bettors.json` (`privacy`),
`p6/sealed-setup.json` (`privateReads`, `publicAfterClose`).

**Tests:** LiteSVM `crates/rush-core-sbf-check/tests/p6.rs` 3/3: happy path, wrong salt / outcome / entry, double
reveal, reveal before close / after finish refused, commits closed at the RUSH tick, settle refused while pending,
forfeit to the pool, **payouts = hand calculation** (pool 250 = 30 open + 100 + 50 + 10 + 20 revealed + 40 forfeited;
a 100 on the winner gets 250), abort refunds, `PICK` by wallet / session key only. CU: commit 16.3k, reveal 12.7k.

## 3. Seasons (task 2, D-SEASON → keeper path, OC-24 / OC-25)
- Accounts on Solana: `Season` (`["season", id]`: id, status, starts / ends, n_matches, n_players, 3 × top 10,
  trophies bitmask), `PlayerSeason` (`["pseason", id, wallet]`: crowns, finals, rounds survived, matches, net market
  RUSH i64, bets won / lost, drops, achievements, a ring of the last 4 recorded matches), `PlayerProfile`
  (`["profile", wallet]`: lifetime crowns + first-crown match).
- Recording: `write_receipt` stores `bets_hash`; `COMMIT_ECON` commits the arena's 5 markets + drop queue **one per
  intent** (6 in one intent: "ScheduleCommit ERR: intent is too large to ever fit into transaction"); `RECORD`
  (permissionless) re-hashes the committed copies against the receipt, so every number is derived from program state.
  A second post-undelegate base action was **not** used (the action's base tx already carries ≤ 28 mint accounts; the
  record needs 9 + n more and the copies first): keeper-sent with retries, resume-safe.
- CU (LiteSVM): record 64.7k for 3 wallets; trophy mint 45.4k. Cost per match ≈ 0.0025 SOL per new PlayerSeason
  (first match of a wallet in a season) + commit fees.
- **Test season 1000001 (2 h, 15:18:59 → 17:18:59 UTC) closed on devnet**: 4 matches, 8 players; `CLOSE_SEASON`
  `5QbbnA7g…`; **Champion trophy `6KmXyYtgzkW8aNXhfvx3nh7i2bGwfxXLUmGQYZmftEsN`** (tx `3BkuaooG…`, to the crowned
  harness human `G7ZcJAaE…`), **Predictor trophy `83ggmMHN4ptV77CyiWRwtqYeaXfMDUvX5dWRrCgYGoo1`** (tx `4PFNmCPZ…`, to
  the local test market maker, a test wallet); no drop MVP that season. Update authority = the program's `assets` PDA.
- The weekly season `202648` (`AE5…`/`/season`) records the public matches; the indexer's rebuild from the archived
  receipts **matches the on-chain boards** (`/api/season`: `matches: true` on all three).
- UI: `/` (now `/arenas` after the landing merge) top-3 strip; `/season[/:id]` full top 10, your rank, countdown, the
  on-chain account link + the rebuild check; `/me` season stats; the in-game receipt card "+1 crown this season".

## 4. Achievements (task 3, D-ACH)
Bits in `PlayerSeason.achievements` set by `RECORD` from the receipt + committed copies: 1 first crown (lifetime
profile), 2 three-round survivor (reached the Final), 4 perfect predictor (≥ 2 bets in one match, all won), 8 chaos
agent (a drop in an on-chain Final), 16 crown. No mint per achievement (cost; OC-25). Shown on `/receipt/:match`
(badges per human / bettor), `/me`, `/season`, the in-game receipt card. LiteSVM: first crown + survivor + chaos agent
for the champion, perfect predictor for a 2-for-2 bettor.

## 5. Builds (task 4)
| build | result |
|---|---|
| program | 3 devnet upgrades (same id); data 1,136,936 → **1,198,896 B** (+61,960 B, 0.315 SOL); `keq!` cut 58 KB |
| LiteSVM | econ 5/5, p6 3/3 |
| EditMode | **420: 419 passed, 1 skipped** (`P3SwitchTests` needs the P3 emulator binary, not built in this worktree), 0 failed (incl. 2 new P6 JSON tests) |
| WebGL release | **22,833,701 B** (≤ 25 MB), Decompression Fallback on; `fallrush-tee.js` (320 KB, on demand) next to the chain bundle (383 KB) |
| Linux server | not rebuilt (no server code change) |
| rush-core | no change; `rush_core.wasm` unchanged (the harness export is feature-gated, separate build) |

## 6. VPS (task 5; `scripts/p6-vps-deploy.sh`)
- Snapshot **472001** refreshed at 15:06 UTC before the first change (later retaken at 17:16 UTC by the landing session).
- Order: stage (node image `fallrush-node:p6`, web build) → indexer → keeper-1 → web (`web.p5bak` kept; since the
  landing merge the web step never touches `web/site`) → gs2 + keeper-2; a keeper fix (season visibility race) went to
  keeper-1 and keeper-2 later. The script **waited ~8 min while a human was in a match** before switching keeper-2.
- Neighbour checks (`p6/vps/neighbours.log`) before and after every change: finity https 200 (0.22–0.41 s) / http 301,
  finity config md5 `c6a6cca8…` unchanged, raw-IP 404 / 200 unchanged, Ovelo `Up (healthy)`. Nothing rolled back.
- Health after the deploy: 6/6 healthy, gs1 / gs2 WebSocket 101, funder 0.75 SOL (`p6/vps/health-after-deploy.txt`).

## 7. Public verification (task 6)
| # | match (Solana) | server | sealed bets | payouts = hand calc | season | replay | receipt | kill |
|---|---|---|---|---|---|---|---|---|
| 1 | [3u7DbW2o…](https://explorer.solana.com/address/3u7DbW2owNavGmra1dD2Rom64bokf1W1D75CW6vaiJC9?cluster=devnet) | gs1 | 1 (in-game) ✓ revealed | ✓ 4/4 markets | backfilled by hand (race, fixed) | ✓ | ✓ | keeper-1 in the reveal window → 80 s |
| 2 | [CfxB3v1K…](https://explorer.solana.com/address/CfxB3v1KNSGVCKJZuFP5GAy3BW1cHerTZUf3q3ginEdg?cluster=devnet) | gs2 | 3 (in-game ×2, phone ×1) ✓ 3/3 | ✓ 4/4 | ✓ weekly | ✓ | ✓ | keeper-2 at settle → 12 s |
| 3 | [BoZFkeAs…](https://explorer.solana.com/address/BoZFkeAsWHiW4WtZGY3kgo4zBx1FcGbHyTgYL51yA3mN?cluster=devnet) | gs1, **2 humans** (two clean profiles) | 2 (both players, in-game) ✓ 2/2 | ✓ 3/3 | ✓ weekly | ✗ 2728/2729 ticks (first tick only; transcript ✓) | ✓ | keeper-1 at receipt → 13 s |
| 4 | [7uNXxpkH…](https://explorer.solana.com/address/7uNXxpkH3hiYd1VzHf1n2ahz7ncAtvfFKds3ukaVP7jh?cluster=devnet) | gs2 | 2 (in-game) ✓ 2/2 | ✓ 4/4 | ✓ weekly | ✓ | ✓ | keeper-2 at finalize → 13 s |

Match 3: both humans were seated on gs1, both handed off to the on-chain Final (`human_mask` 0b11), the receipt lists
both wallets (`FNhseg…`, `G2WuMc…`), and **the Magic Action minted both capsules** (minted mask 0b11, keeper mint
attempts 0). Its replay check failed only on the first transition (tick 0 → 1; the other 2,728 ticks are byte-exact
and the receipt's Final transcript is in the final World): most likely the two humans' first input batch landed after
the tick-0 snapshot was archived (archive timing, not a determinism break; UNVERIFIED cause). Its phone smoke crashed on
a DevTools error (the phone path is proven in match 2).

**Stranger → lobby** (clean profiles, `/arenas` → Play → seated; the landing at `/` belongs to the landing session and
renders its links in JS): **30.2 s, 46.0 s, 29.3 s**, 0 popups. The download dominates (21.5–39.3 s; P5 3.0–3.8 s):
this Mac's uplink was slow during the runs (one stream 0.28 MB/s from the VPS; load average 14.6 from other sessions);
guest setup 8.6–14.5 s, Unity start ≈ 2 s after the download. 1 of 3 within 30 s from this Mac (UNVERIFIED from a
normal connection). `/live` stayed smooth during the Final (match 2: bet, attestation, reveal on time).

Match 2 signatures: escrow open `4Z57bB4N…`, book open (TEE) `4f5BiyZF…`, close `3Hc42r8M…`, book close (TEE)
`2jQgdTM5…`, reveal `5hHtAaQa…`, finish `3RY8BbBL…`, receipt `369St2Wa…`, commit_econ `j7eGeDGJ…`, finalize + action
`69kN69k5…`, season `2pGn9Ewv…`. The WebGL test hook (`?autobet=`) placed two sealed bets per match (one per
`ChainEconomy` poll race): a test-hook quirk, not a product path.

## 8. SOL
See `docs/proof/p6/sol.md`. Start: admin 4.3783 + authority 2.6117 (+ funder 0.2979). End: admin **3.4419** + authority **2.2891** + funder 0.5699 (all wallets 7.2879 → 6.3009): **P6 used 0.99 SOL** of the 1.5 budget (program extend 0.315 + 3 upgrades' fees ≈ 0.02; sealed accounts + test seasons + PlayerSeasons ≈ 0.12; harness / bettor / spike wallets ≈ 0.16; guest gas sent 0.18; matches' rent and fees the rest). Admin ≥ 1.5 ✓.

## 9. UNVERIFIED
- A real phone, a real stranger on another network, real Phantom (as P5).
- The TEE path under P7 load (many concurrent sealed bettors; the book holds 32 entries per match).
- The TEE book's permission behaviour at undelegation (we never undelegate the book; it is reused).
- Season Trophy for a weekly season (the weekly 202648 closes 2026-11-30, after the hackathon).
- `m_EnableMultiplayerRoles`: why the package flips it (observed, not traced).
- Stranger → lobby ≤ 30 s from a normal connection (this Mac measured 29–46 s on a slow uplink).
- The cause of match 3's first-tick replay mismatch.

## For P7
- Playtest: ≥ 8 humans + 3 spectators; `/live` shows "🔒 sealed" on the Champion market; the client keeps the salt.
  Guest gas 0.75 SOL ≈ 60 guests. Close/reopen of the TEE book is per match (keeper); if devnet-tee is down, bettors'
  self-reveal still settles (Path B) — keep the tab open ~10 s after the Final starts.
- Scripts: `scripts/p6-vps-deploy.sh` (staged, keeps `web/site`), `p6-public-match.sh`, `p6-two-humans.sh`,
  `p6-kill-test.sh`, `p6-crown.sh`, `tools/p6/close-season.ts`, `tools/p6/record.ts` (manual backfill),
  `tools/p6/open-season.ts`; health `scripts/p5-health.sh`; neighbours `scripts/p6-neighbours.sh`.
- Known issues: the public devnet RPC rate-limits (429) when several clients start from one IP; the autobet hook fires
  twice; the landing now owns `/` (web app home at `/arenas`).
- **PROOF.md link list draft (P7.6):** sealed bet — book close (TEE) `2jQgdTM5…`, reveal `5hHtAaQa…`
  (devnet-as) and the escrow `["sealed", arena 3]`; season trophy — `6KmXyYtg…` (Core asset) / tx `3BkuaooG…`; Crown by
  the Magic Action — asset `Eoi1Xaek…`, undelegation tx `2oasoN6D…` (fee payer MAS1…); the program
  `4yehffTS…`; RUSH mint `3s8Kstqi…`; plus P5's list (arena delegate, VRF deal, tick, input, bet, Chaos Drop, round
  commit, finalize, capsule).
