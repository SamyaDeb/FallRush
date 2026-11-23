# P4 (full): the economy: RUSH chips, Rush Markets, Chaos Drops, pots, Crown + capsules

> **Autonomous run (user, 2026-11-22):** make every engineering decision yourself, choosing the best option on the evidence, and implement P4 **fully, end to end, without stopping to ask**.
> - At each decision point (⛳) apply §3 "Decision policy".
> - Record each decision in `docs/15` (a new OC row) and in the proof, with the options, the measured numbers, the choice and why.
> - The rules in §1 are guardrails, not questions: follow them without asking. A guardrail that would block the work has a decided way around it in §3.

You are the Solana / Rust + Unity engineer on **Fall Rush**, a Unity 6.3 WebGL 12-player knockout party game for the MagicBlock Solana Blitz V10 hackathon, theme **Entertainment Finance**: "apps where you trade, collect, compete and win assets you can sell anytime".

**What works today** (on `main` @ `1d45a78`):
- rounds 1–3 run on the **Classic server**;
- the **Final (Lid Flip) runs fully on a MagicBlock Ephemeral Rollup** in real time, through `rush-core` (our integer Rust core, identical natively, in WASM and on-chain) with bots on-chain;
- a chain worker records rounds 1–3 into the `Match` account;
- `start_final` hands the 3 finalists over;
- the receipt commits to Solana;
- the Unity client switches to the rollup at the Final and plays it with prediction. The user said: "as good as Classic, nothing laggy".

**P4 turns that into Entertainment Finance:**
- **RUSH chips** (devnet SPL, no real money);
- **Rush Markets** settled by the program from recorded results;
- **Chaos Drops** that spectators buy to change the on-chain Final;
- optional humans-only **pots**;
- the champion's **Crown Capsule** + VRF-rolled **capsule drops** as Metaplex Core assets;
- all of it **inside the game UI**.

**Read first, in full:**
1. `docs/19-ONCHAIN-PLAN.md`: §1, §5 P4, §7 video, §8 OC-2 / OC-9 / OC-10, §9.
2. `docs/15-DECISIONS-AND-RISKS.md`: OC-1…OC-11, P0-1.
3. `docs/proof/P2-onchain-sim.md` (all, especially §2 accounts, §3 keeper, §9 UNVERIFIED, §10 inputs to P3) and `docs/proof/P3-client.md` (§4, §6, §7, §12, **§13 Requests for P2**, **§14 For P4**).
4. `docs/proof/PB0.md` "For main": a VRF callback identity must be a `const` item, or it costs more than 200k CU.
5. `docs/proof/P0-feasibility.md` §4: task budget 400k CU; one failed execution kills the task; base RPC 429s above ~1.5 tx/s.
6. Code:
   - `programs/fall_rush/src/{lib.rs,sim.rs,state.rs}`;
   - `crates/rush-core/src/{world.rs,rules/,hazards/}` (World already reserves a **Chaos Drop queue, `DROP_QUEUE = 8`**, empty);
   - `tools/keeper/*`, `tools/chain-worker/*`;
   - `packages/chain/src/*`;
   - `client-unity/Assets/_Game/Scripts/{Client/MatchHud.cs, OnChain/**}`.

---

## 0. State at hand-off (2026-11-22 ~21:45 UTC, checked)
- **`main` @ `1d45a78`**: P0, PB0, P1, P2 and P3 are all merged. No other phase session is running. The only other worktree is `../FallRush-trailer` (branch `trailer`; not part of these phases, **don't touch it**).
- **Program** `4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4` (devnet; `.so` about 1.03 MB; Anchor 1.0.2, ER SDK 0.18.0, session-keys 3.1.1). Instructions:
  - M0: `initialize_config`, `create_match`, `delegate_match`, `request_deal`, `deal_callback`, `start_round`, `record_qualified`, `record_eliminated`, `complete_round`, `complete_match`, `finalize_match`, `abort_match`;
  - sim: `init_arena`, `init_input`, `set_arena`, `upload_map_chunk`, `seal_map`, `delegate_sim`, `open_lobby`, `join_lobby` (`SessionTokenV2`), `start_final`, `start_sim`, `tick`, `submit_input`, `schedule_ticks`, `cancel_ticks`, `commit_round`, `end_sim`, `abort_arena`.
  - `Match` has reserved, unused `picks[32]` + `pick_count` fields (doc 06 Fan Picks). Reuse or replace them, writing down which.
- **The Final's tick:** 3 players + bots on-chain. Worst tick on the rollup **262k CU** (P2), against the 400k task budget. **Anything added to `step()` (Chaos Drops) must keep the worst tick ≤ 300k.**
- **Wallets:**
  - admin (`~/.config/solana/id.json`) **5.65 SOL**;
  - game authority (P2's keeper key) **3.49 SOL**;
  - a Final costs about 0.011 SOL in keeper fees + `Match` rent.
- **Disk:** about 19 GB free (cleaned 2026-11-22). Keep build output inside your worktree; no duplicate Unity `Library` (use the APFS clone).
- **Old QA raw logs** (`.m1-logs/g4b`, `g413`) were deleted on purpose; the proofs keep the summaries.

### 0.1 Carry-over fixes (P3's requests to P2, still open; do these first, §2 task 0)
1. A **core version** in `WorldAcc`, and bump `rush_core::VERSION` on any behaviour change. TOI 8 → 6 kept `0x10001`. **Chaos Drops change behaviour, so bump it.** The client refuses an arena with a mismatched core.
2. A **match serial** (restart counter) in `WorldAcc` (clients currently infer a new match from the tick restarting).
3. **Keeper "wait for humans":** `open_lobby` → wait until N human joins **or** T seconds before `start_final`. Defaults: N = number of human finalists from the Classic handoff, T = 8 s. That makes human Finals deterministic, and it's needed for bets and drops from real people.
4. **Input accept window:** measure widening `submit_input`'s +8-tick lead window (to +12 / +16). Late confirms (0.5–1 s, P3 §7) are the main source of corrections. Keep it only if the security suite still passes and corrections drop.
5. Docs: fix P2 proof §2 (`Arena` is 1,432 B, not 1,904). Add the P3 lines to `PROJECT_STATUS.md` (P3 "For main": on-chain Final playable on devnet, feel = Classic per the user, WebGL 22.29 MB) and a doc 15 note: **OC-6 done.**

---

## 1. Rules
- **Worktree:** `git worktree add ../FallRush-p4 -b p4 main`. Unity: `cp -cR /Users/samya/Downloads/FallRush/client-unity/Library ../FallRush-p4/client-unity/Library` (APFS clone, no 5 GB re-import).
- **You own (on `p4`):** everything except `../FallRush-trailer`. Keep doc edits to `docs/15`, `docs/19` and `PROJECT_STATUS.md` in **one final doc commit**.
- **8 GB M2 Air, one heavy job at a time:** `bash /Users/samya/Downloads/FallRush/scripts/heavy-lock.sh p4-<what> -- <command>` for:
  - anchor / sbf builds, LiteSVM, the local stack, devnet runs;
  - Unity batch runs, Mac / WebGL builds, Chrome;
  - long `rush-sim` runs.

  Use `CARGO_BUILD_JOBS=2 nice -n 10` for everyday tests.
- **No VPS / Hostinger / SSH. Never push, never add a remote.** No new Unity packages. New Rust / npm dependencies are pinned exactly and justified (candidates: `mpl-core`, `@metaplex-foundation/mpl-core` + `umi`, the Ephemeral SPL Token crate / TS). Check each one builds against Anchor 1.0.2 / Solana 3.0 crates **before** relying on it.
- **Determinism:** any `rush-core` change regenerates every golden vector with parity on **native, WASM (Node + Chrome self-test), sBPF and the Unity FFI**, and bumps `VERSION`. Classic C# gameplay is untouched.
- **The hot tick never fails** (P2 rules: `NotDue` no-ops, no `require!` on normal-play paths). **Settlement, minting and payouts never run inside `tick()`.** They're separate permissionless or keeper instructions.
- **The VRF callback identity is a `const` item** (PB0). Never import `sol_remaining_compute_units` (the rollup can't clone the program).
- **The payout follow-up call (`#[action]`) is not atomic with the commit** (MagicBlock docs). Every handler is **idempotent** (a `paid` / `minted` flag), checks the injected `escrow` signer, and the keeper retries until done.
- **No real money.** RUSH is a devnet test token. Its mint authority is a program PDA. README line: "devnet test chips only, no real-money value"; mainnet needs a legal review (OC-2).
- **Fairness:**
  - **Chaos Drops are map-wide, telegraphed 0.75 s (≈ 23 ticks at 30 Hz), capped and never aimed at a slot** (OC-9).
  - **Pots pay humans only; bots never take a share** (OC-10).
  - **Markets close by program rule, never by the keeper's choice.**
- **Trust labelling.**
  - Round 1–3 results are **recorded by the game authority** (Classic server → chain worker).
  - Final results are **computed by the program**.
  - Every market and the receipt show which kind settled it ("◆ program-settled" vs "● server-recorded").
- **Devnet SOL:** a P4 budget of **2.0 SOL** across both wallets (admin 5.65 + authority 3.49), program upgrades and `solana program extend` included.
  - Spend it as needed and log every spend in the proof.
  - Keep at least **1.5 SOL in the admin wallet** for P5–P7.
  - If the budget would run out, apply §3 D-SOL rather than stopping.
- **Verify in player builds** (Mac player + WebGL in Chrome), not only in the Editor.
- Commit on `p4` with your own paths: `git commit -- <paths>`. End every commit message with the attribution lines from the system reminder. A number counts as measured only if you ran it. Anything else is **UNVERIFIED**.

---

## 2. Tasks, in order (each ends green and committed; gates are marked ⛳)

### 0. Carry-over fixes (§0.1)
1–4 in the program / keeper / client, plus their tests (security suite still passing, now ≥ 35).
- Measure corrections per 5 s on 3 devnet Finals before and after the window change (Mac player via the relay, P3's `scripts/p3-devnet-final.sh`).
- Commit `fix(p4-0): core version + match serial in WorldAcc, keeper waits for humans, input window <n>`.

### 1. RUSH chips + faucet ⛳ (spike decision)
1. **Spike (time-box ½ day):** the **Ephemeral SPL Token** program (`SPLxh1LVZzEkX99H6rqYizhytLWPZVV296zyYDPagv2`, v0.1.0; docs.magicblock.gg Ephemeral SPL Token pages, including "smart-contract integration"). Steps:
   1. mint RUSH on devnet;
   2. deposit into the rollup token account;
   3. transfer inside the rollup from a session-key signer to a program-held vault;
   4. pay out from the vault (PDA-signed);
   5. withdraw back to Solana.

   Record every signature and timing.
   - **PASS** → chips move inside the rollup.
   - **FAIL / too immature** → **fallback:** a program-internal `ChipBalance` PDA per wallet (delegated, credited by the faucet). It's minted to real SPL only on **`claim_chips`** (commit + undelegate + a mint by a PDA authority).
   - **Write down the choice as doc 15 OC-12.**
2. **RUSH mint:** devnet SPL, 6 decimals, mint authority = program PDA `["rush_mint_auth"]`. Freeze authority: none.
3. **`claim_faucet()`:** 1,000 RUSH per wallet (or burner) per 24 h (a `FaucetStamp` PDA). Guests get chips silently on first launch, with no popup (bundle + session key).
4. Tests (LiteSVM + local stack): the faucet rate limit; transfers only by the owner's wallet or session key; vault payout only by the program.
5. Commit `feat(p4-1): RUSH chips (<rollup SPL | ChipBalance>) + faucet`.

### 2. Rush Markets (shared pool: winners split the losers' stakes)
1. **`Market` account** (rollup, delegated), one per (match, kind, round):

   | Kind | Outcomes | Opens | Closes (program rule) | Settles from |
   |---|---|---|---|---|
   | `Champion` | 12 slots | `create_match` | the Final's `RUSH` tick in World | **program** (Final champion) ◆ |
   | `FinalWinner` | the 3 finalists | `start_final` | the Final's `RUSH` tick | **program** ◆ |
   | `Qualify(r)` r = 1..3 | yes / no per slot (or "pick who qualifies") | round r−1 resolved | `start_round(r)` recorded in `Match` | **authority-recorded** round r results ● |
2. **`place_bet(market, outcome, amount)`:**
   - signed by a session key;
   - checks `open ≤ now < close` **by program state** (Match / World), never by the client;
   - moves chips into the market vault;
   - aggregates per outcome (≤ 64 individual bet records, the rest folded into per-wallet totals; account ≤ 10,240 B).
3. **`settle_market(market)`:** permissionless, called by the keeper right after the result exists (never inside `tick()`).
   - Shared-pool payout: each winner gets `stake × pool / winning_stake`, rounded down; dust goes to the vault, with fee 0 %.
   - Refund everyone if nobody won, the match aborted, or the Final fell back to Classic (P3 failure path: then `Champion` / `FinalWinner` refund, marked "● Classic fallback").
   - Idempotent.
4. **Market-maker bot** (in `tools/keeper` or a new `tools/market-maker`, **labelled `mm` in the UI**): seeds every market with a small pool spread by bot tier, so a solo judge sees real odds. It's funded by the faucet logic, never minted beyond it.
5. **Tests:**
   - a late bet is rejected at exactly the close tick;
   - double settle is a no-op;
   - payout sums match a hand calculation (property test);
   - the refund paths;
   - session-key-only signing;
   - hostile accounts.
6. Commit `feat(p4-2): Rush Markets (program-settled + authority-recorded)`.

### 3. Chaos Drops (spectators change the on-chain Final) ⛳ (fairness + CU)
1. **`rush-core`:** implement the World drop queue. Kinds for Lid Flip (map-wide, telegraphed 23 ticks, short):
   - **Lid Frenzy:** 2 extra lid flips, pattern from the hazard seed + drop index;
   - **Bumper Rain:** 3 falling bumpers at seed-chosen spots;
   - **Wind Gust:** a 2 s side push, direction from the seed;
   - **Sweeper Rush:** the sweeper at 1.5× for 3 s.

   Effects are pure functions of `(seed, drop index, tick)`, so prediction stays exact (the client sees the queue in World). **Bump `VERSION`**; regenerate vectors with parity on all four targets.
2. **Program `buy_drop(kind)`:**
   - during the Final's RUSH phase only;
   - price in RUSH per kind;
   - **max 3 per round, 5 s global cooldown**;
   - queues at `World.tick + 23` into a **separate `DropQueue` account** that `tick()` reads, so `buy_drop` never write-locks World;
   - chips go to the pot / market vault (write down where).
3. **⛳ Fairness QA:** `rush-sim final-qa` with drops (100 Finals, a random allowed drop schedule): the G4.5 Final targets still hold (median 60–120 s, every lid fall after ≥ 1 s of warning), no unfair instant kills (drops never resolve in < 23 ticks), deterministic 100/100.
4. **⛳ CU:** worst tick with a drop active ≤ 300k (LiteSVM + rollup samples).
5. **Client:**
   - telegraph visuals for each kind (reuse G3 VFX / hazard colours);
   - a banner "🌀 LID FRENZY by <name> ◆" with a rollup ✓ chip;
   - a **drop bar** for eliminated players / spectators during the on-chain Final (prices, cooldown, remaining this round).
6. Commit `feat(p4-3): Chaos Drops on the on-chain Final`.

### 4. Receipt, Crown Capsule, capsule drops
1. **`MatchReceipt`** (written at finalize; on Solana after commit): match id, ruleset + map-blob hashes, core version, VRF seed, roster hash (bots flagged), the round 1–3 transcript (authority) + the Final transcript (program), champion, placements, market results, drop log hash, the trust label per part.
2. **Follow-up call on Solana after the receipt commit (`#[action]` handler):**
   - **(a)** pays out pots (task 5) if chips settle on Solana;
   - **(b)** mints the **Crown Capsule** (Metaplex Core, program `CoREENxT6tW1HoK8ypY1SxRMZTcVPm7R94rH4PZNhX7d`) to the champion's wallet, or to a **claimable PDA** for burner / guest champions. Attributes: `match`, `matchPda`, `season`, `rounds`, `players`, `finalOnChain = true`;
   - **(c)** mints **VRF capsule drops**: 1 per human. Rarity from a **second VRF request at match end** (const callback identity), Common 70 / Rare 22 / Epic 7 / Legendary 1; shell variant from `CosmeticPresets` (+ new variants).

   Idempotent flags per receipt; the keeper retries until done.
   - **Spike first:** an `mpl-core` CPI from Anchor 1.0.2 in the action handler.
   - **If the crate doesn't fit:** fallback is the keeper minting with `@metaplex-foundation/mpl-core` (authority-signed, after it reads the committed receipt), written down as less trustless (OC-13).
3. **Client:**
   - champion screen: the Crown pops; each human sees their capsule's rarity reveal;
   - receipt panel: Rollup ✓ → Solana ✓, market results, asset links;
   - **"Claim with Phantom"** moves a PDA-held asset to a connected wallet.
4. Tests: double mint is impossible; a claim only by the right wallet; the rarity distribution over 10k simulated seeds is within ± 1 % of the table.
5. Commit `feat(p4-4): receipt + Crown Capsule + VRF capsule drops`.

### 5. Skill pots (optional; first to cut if time runs short)
1. "Crown Pot" lobbies on the Classic server: an entry stake of 100 RUSH escrowed on-chain at roster lock (a `Pot` account). **Humans only, minimum 2 humans**; bots fill slots but never share.
2. Ladder by human rank across the match: 60 / 25 / 15. Settled in the receipt follow-up call or by `settle_pot`. Refund if fewer than 2 humans or an abort.
3. Tests + 1 devnet run with 2 browsers.
4. Commit `feat(p4-5): skill pots (humans only)`.

### 6. In-game UI (Unity, reuse the G3 UI kit; Classic visuals unchanged)
- **Before the match / between rounds:** a bet panel (markets for this match, odds, your stake, ◆ / ● labels). Chips in the HUD corner (faucet claim on first launch).
- **Eliminated / spectating:** bet panel + the drop bar (task 3) during the on-chain Final.
- **Deal screen:** pool sizes + odds next to each capsule.
- **Champion / receipt:** the Crown + capsule reveal; market payouts ("+340 RUSH"); claim.
- **F3:** a market / drop debug section.
- All chain calls go through `packages/chain` (config-driven instruction specs, session signer, base throttle), per P3 §14.
- Commit `feat(p4-6): economy UI`.

### 7. Devnet verification ⛳ (the exit gate of P4)
1. **3 full hybrid matches on devnet** (Classic rounds 1–3 → on-chain Final), each with:
   - bets from ≥ 3 wallets (the Mac player + 2 browser burners + the market maker), at least one via a session key;
   - ≥ 2 Chaos Drops bought during the Final by a spectator browser;
   - `Champion`, `FinalWinner` and ≥ 1 `Qualify` market settled.
2. **1 pot match** with 2 humans (if task 5 shipped).
3. **Check:** every payout equals a hand calculation (a script); Crown + capsules visible in a devnet wallet / Core explorer; a forced follow-up call failure → keeper retry → done once.
4. **Feel (automated):** play 2 devnet Finals with drops using the Mac player's auto-play probe.
   - Record corrections per 5 s, the telegraph-to-effect time, deaths within 1 s of a drop resolving, and screenshots of each drop's telegraph.
   - Fairness is decided by the ⛳ QA in task 3, not by a human.
   - Prepare `scripts/p4-feel.sh` so the user can play 2 Finals with drops later (optional, recorded in the proof if they do).
5. Record every signature into `docs/proof/P4-economy.md` (explorer links: RUSH mint, a faucet claim, a bet, a drop, a settle, a receipt commit, the action / mint, a Crown asset, a capsule asset, a claim). This feeds PROOF.md (P7).

### 8. Proof + docs
1. `docs/proof/P4-economy.md`:
   - §1 carry-overs;
   - §2 chips (the spike result, OC-12);
   - §3 markets;
   - §4 Chaos Drops (QA + CU);
   - §5 receipt / assets (OC-13 if fallback);
   - §6 pots;
   - §7 UI screenshots (player builds);
   - §8 devnet runs;
   - §9 the automated feel numbers (the user's answer only if they play the optional run);
   - §10 SOL;
   - §11 UNVERIFIED;
   - "For P5" (where the web spectator page plugs in: accounts to subscribe, instruction specs).
2. One WebGL release build (≤ 25 MB) + Chrome checks (rcselftest, an on-chain Final with a bet + a drop on devnet, Classic regression).
3. Final doc commit: `docs/15` (OC-12, OC-13, OC-6 done, OC-9 / OC-10 as built), `docs/19` (P4 as built), `PROJECT_STATUS.md`.

   Commit `docs(p4): proof`. **Never push.**

---

## 3. Decision policy (decide yourself; never stop to ask)

**For every ⛳ or open choice:**
1. Measure the options (or reason from existing measurements when measuring isn't possible).
2. Pick by this priority: **(1)** correctness, determinism and safety guardrails; **(2)** what makes MagicBlock do real work (on-chain, program-settled, trustless); **(3)** the player's experience (readable, fair, responsive); **(4)** schedule and cost.
3. Implement the pick.
4. Record it as `OC-n` in `docs/15` plus a proof paragraph: options, numbers, choice, why, how to revert.

**Pre-decided defaults:**

| ID | If… | Then do this |
|---|---|---|
| D-CHIPS | the Ephemeral SPL spike fails or is flaky (< 5/5 round trips) | use the `ChipBalance` PDA fallback, with real SPL via `claim_chips`. If both fail, use a `ChipBalance` PDA on the rollup only, plus a receipt-time mint of the net winnings. **Never ship without chips** |
| D-DROPS-CU | the worst tick with a drop is > 300k | (1) make the drop effect cheaper (precompute, fewer bumpers, shorter); (2) give drops their own `tick()` slice via P2's `step_part`; (3) drop the most expensive kind. Keep ≥ 2 kinds |
| D-DROPS-FAIR | the fairness QA fails | lengthen the telegraph (23 → 30 → 45 ticks), lower the per-round cap (3 → 2), weaken the effect; re-run until PASS. Keep ≥ 2 kinds |
| D-CORE | the `mpl-core` CPI doesn't fit Anchor 1.0.2 / Solana 3.0 | the keeper mints with `@metaplex-foundation/mpl-core` after reading the committed receipt (authority-signed; OC-13, labelled "● authority-minted"). If Core mints fail altogether, mint SPL NFTs (supply 1, 0 decimals, metadata in the receipt) and write down why |
| D-WINDOW | a wider input window doesn't lower corrections or breaks a security test | keep +8 |
| D-POTS | time or complexity pressure | **cut pots first** (task 5), then extra market kinds (keep `Champion` + `FinalWinner`), then capsule drops (keep the Crown). Never cut chips, the program-settled markets, Chaos Drops, the Crown or the UI |
| D-SOL | the 2.0 SOL budget would run out | cut devnet re-runs to the minimum for the exit gate, close spike / test accounts to reclaim rent (`solana program close` for leftover buffers), lower the number of debugging runs. Never go below 1.5 SOL in the admin wallet |
| D-BLOCKED | a dependency or tool is blocked (an RPC rate limit, a broken crate) | pin an older working version, or implement the minimal piece yourself; write down the workaround |
| D-OTHER | any other choice | apply the priority order above and record it |

**Still hard rules (not decisions):**
- never push, never add a remote;
- no VPS / Hostinger / SSH;
- never touch `../FallRush-trailer`;
- never change Classic gameplay or the signed-off motor feel;
- never commit keypairs;
- never deploy a new program ID (only upgrade `4yehff…`).

## 4. Report to the user (short, plain words)
- What a player can now do: get chips, bet, drop chaos, win a Crown, roll a capsule, claim.
- Which parts are program-settled vs server-recorded.
- Devnet proof links (one per feature), payout check result, SOL spent.
- The Final with drops: QA numbers, CU, and your own verdict.
- UNVERIFIED items; anything cut (pots?) and why.
- **Merge:** when the exit gate (task 7) passes and the proof is committed, merge `p4` into `main` yourself (`git merge --no-ff p4` in the main checkout, message `merge: p4 (economy ...)`), then run the main checkout's EditMode tests + `scripts/p1-check.sh` under the lock. **Keep the worktree** (the user removes it). Never push.
