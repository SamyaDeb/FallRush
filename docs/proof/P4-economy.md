# P4 — the economy: RUSH chips, Rush Markets, Chaos Drops, receipt, Crown + capsules (proof)

Branch `p4` (worktree `../FallRush-p4`, off `main` @ `1d45a78`). Prompt `docs/prompts/P4-PROMPT.md` (autonomous run).
A number is **measured** only where this file says so; everything else is **UNVERIFIED** (§11). Devnet test chips
only, no real-money value (OC-2).

**Summary.** A player gets 1,000 RUSH chips with no popup (the burner creates and delegates its `ChipBalance`, the
session key claims the faucet), bets on 5 Rush Markets per match (Champion and Final winner settled by the program from
the on-chain Final ◆; Qualify R1–R3 settled from the server-recorded rounds ●), buys Chaos Drops that change the
on-chain Final for everyone (map-wide, 0.77 s telegraph, capped), and gets a match receipt committed to Solana from
which the program mints the champion's Crown Capsule and one VRF-rolled capsule per human (Metaplex Core). Pots were
cut (D-POTS). On devnet: a Final with 3 human finalists minted the Crown + 3 capsules (a forced failure retried, each once),
5 complete hybrid matches (Classic rounds 1–3 → the on-chain Final) with bets from ≥ 3 wallets (session keys), 3 Chaos
Drops per Final from a spectator, all 5 markets settled each time and **every payout equal to the hand calculation**.
WebGL release 22.37 MB. P4 spent 0.811 SOL.

## 1. Carry-over fixes (task 0)
| fix | as built | evidence |
|---|---|---|
| core version | `WorldAcc.core_version` (LE u16 @142 = `rush_core::VERSION`, now **2**; layout and the World offset 208 unchanged, asserted at compile time); the bundle reads it (`coreVersionBytes: 2`); Unity **refuses** an arena with another core (`CoreMismatch`, status "update the client"), per World and at join | `sim.rs` layout asserts; `p2.test.ts` "P4 WorldAcc header" |
| match serial | `WorldAcc.serial` (LE u32 @194 = `Arena::matches_run` at `start_final` / `start_sim`); bundle + Unity: a higher serial = a new match (exact), a lower one = a stale read (dropped); 0 (older program) = the P3 tick heuristic | same test |
| keeper waits for humans | `HUMANS` / `WAIT_HUMANS_S` (default 8 s; hybrid: the Classic handoff's human finalists from the jsonl roster) before `start_final` | 3/3 devnet Finals: 1/1 human joined after 81, 95, 265 ms (`docs/proof/p4/devnet-window/p4-w8-*/keeper-summary.json`) |
| input window | `set_input_lead(lead)` (authority; 8..=13, 0 = 8; stored in `WorldAcc.input_lead` @198 so `submit_input` keeps its accounts); the 16-entry ring caps it at 13; the client caps its adaptive lead at window − lead − 2 (`-fallrushOnchainWindow` / `?window=`) | security suite (6 new checks); A/B below |
| docs | P2 proof §2 `Arena` size and the P3 lines → the final doc commit | — |

**Input window A/B (D-WINDOW), devnet, Mac player auto-play via the relay, 3 Finals each** (`tools/p4/window-summary.py`):
| window | Final 1 | Final 2 | Final 3 | mean (window-weighted) | late inputs | confirm p95 worst |
|---|---|---|---|---|---|---|
| +8 (P3; the program before the upgrade) | 0.14 (max 2) | 0.33 (9) | 3.57 (33) | 1.24 | 85 | 1.8 s |
| +12 (`set_input_lead(12)`, client cap 7) | 0.32 (5) | 0.40 (3) | 34.9 (98) | 9.46 | 538 | **4.9 s** |

Corrections per 5 s (mean of the windows while playing; max in brackets). The +12 runs are no better on the two normal
Finals and one Final hit a 4.9 s confirm stall (a devnet spell, not the window). **D-WINDOW: keep +8** (`set_input_lead(0)`;
the client default stays 8; doc 15 P4-2). The knob stays for later tuning. Late confirms remain the main source of
corrections (P3 §7).

**Security suite (local stack, `tools/keeper/security.ts`): 41/41** (P2's 35 + T+12 rejected at the default window,
`set_input_lead` by a non-authority, lead 14 beyond the ring, lead 12 by the authority, T+12 accepted / T+13 rejected
at +12). `docs/proof/p4/local/security-local.json`.

## 2. Chips (task 1, doc 15 OC-12)
**Ephemeral SPL spike** (`tools/p4/espl-spike.ts`, devnet, ½-day time box; signatures in `docs/proof/p4/espl-spike.json`):
| step | result (round 1 / 2) |
|---|---|
| test mint + fund (admin authority for the spike) | ok, 1.39 s |
| deposit into the rollup (`delegateSpl`: eATA + vault + deposit + delegate) | ok, 1.21 s / 0.73 s; the rollup saw the balance after 358 / 118 ms |
| transfer inside the rollup, owner-signed (SPL Token transfer on the rollup's ATAs) | ok, 476 / 294 ms |
| `approve` a session key + transfer signed by the session key (delegate) | ok, 5.2 s / 291 ms; 349 / 234 ms |
| PDA-signed payout from a program vault | not reachable without our program CPI-ing SPL Token on the rollup |
| withdraw back to Solana (`withdrawSpl`) | **fails** in the rollup (`Custom 27`); the base-layer variant ran in round 1 and left the eATA unusable: rounds 2–5 failed at the deposit (`0x1`) |
| **round trips** | **0 / 5** |

→ **D-CHIPS: `ChipBalance` PDA** per wallet (`["chips", wallet]`, 96 B, delegated): the faucet credits it on the
rollup, bets / drops move chips into program-held vaults (a `Market`'s pool, the `DropQueue` pot) on the rollup,
`cashout` (rollup + commit) moves chips out and `mint_cashout` (Solana, permissionless, idempotent through a
`["cashout", wallet]` stamp) mints exactly the committed, not yet minted amount of the real SPL **RUSH** mint
`3s8KstqicXn1kgFyWPVq3AZXkMprDtEtT7BNTxtQccEw` (6 decimals, mint authority = PDA `["rush_mint_auth"]`, no freeze
authority; pinned in the program so `mint_cashout` can never use another mint). Revert: the spike script + the
Ephemeral SPL helpers stay in the repo.

**Faucet**: 1,000 RUSH per wallet per 24 h (the `ChipBalance` carries the stamp; the prompt's separate `FaucetStamp`
PDA folded in). Guests: the bundle initialises + delegates the `ChipBalance` with the burner (one base transaction) and
claims the faucet with the session key, no popup (`econ('chips')`).

**Program size (a constraint found here):** one Anchor instruction per economy op made the `.so` 1,306,544 B. An
upgrade needs a buffer holding the whole `.so`'s rent (6,960 lamports per byte ≈ 9.1 SOL) while both wallets held 9.08
SOL. As built: one `econ(op, a, b)` instruction with hand-checked accounts (`econ::dispatch`, one non-inlined function
per op for the 4 KB sBPF frames), the Magic program CPIs (commit, commit + undelegate, ScheduleTask, CancelTask) with
hand-encoded data (bytes asserted equal to the SDK + bincode in `magic.rs` tests) and `initialize_config` parsing the
loader accounts by hand: serde, bincode, hashbrown and ~30 KB of float formatting left the `.so`. **Result 1,102,376 B**
(+20 KB over P3's program data; extended by 24,576 B).

**Tests (LiteSVM, the real `fall_rush.so`, `crates/rush-core-sbf-check/tests/econ.rs`): 5/5** — faucet + its 24 h
limit (also under a 0 unix clock), only the wallet or its session key (a foreign signer, a token of another signer, an
expired token: rejected), `mint_cashout` mints the committed amount once and then only the difference.

## 3. Rush Markets (task 2)
5 markets per arena (`["market", arena id, k]`, 3,288 B, delegated, reused per match): Champion (k 0), Final winner
(k 1), Qualify R1–R3 (k 2–4: "pick who qualifies", every backer of a qualifier shares the pool). Shared pool, fee 0 %:
each winning bet gets `stake × pool / winning stake` rounded down; the dust stays in the market's `vault`. ≤ 64 bet
records per market (aggregated per wallet and outcome; the 65th distinct pair is refused, `MarketFull`).

| market | opens (program state) | closes (program state) | settled from |
|---|---|---|---|
| Champion | `open_markets` at match creation | the Final's RUSH tick (`WorldAcc.round_phase` mirror) / round 4 for a Classic Final | the program's Final ◆ |
| Final winner | `start_final` (arena running the match, round 4, before RUSH) | the RUSH tick | the program's Final ◆ |
| Qualify(r) | round r − 1 resolved (r = 1: before round 1) | `start_round(r)` recorded in `Match` | the server-recorded round r ● |

Refunds: aborted match; Classic fallback (a Completed match without the on-chain Final → Champion / Final winner
refunded, "● Classic fallback"); nobody backed a winner. `settle` is idempotent and pays per bet (`paid` flag);
settlement and payouts never run inside `tick()` (permissionless `econ` op, keeper-driven). A market holding unpaid
bets refuses to reopen for the next match (`MarketBusy`).

**Market maker** (`tools/keeper/econ.ts` `mmSeed`, key `devnet-mm`, labelled "mm" in the UI): seeds every market when
its window opens, stake ∝ bot tier (Easy 2, Normal 4, Good 7, Expert 10 × 1–2 RUSH; hybrid: the Classic roster's
tiers), funded by its own faucet claims only.

**Tests:** LiteSVM `markets_windows_payouts_refunds` (a late bet rejected at exactly the RUSH tick, Qualify(1) closed at
`start_round(1)`, payouts = a hand calculation (160 / 40 / 0 RUSH; 300 / 15; 100), double settle a no-op, refunds:
Classic fallback / aborted / no winner, hostile: a `ChipBalance` copy owned by another program, a bet from another
wallet's chips); unit `shared_pool_payouts_match_hand_calc` (500 random markets: every payout = ⌊stake·pool/ws⌋,
Σ ≤ pool, dust < number of winners).

## 4. Chaos Drops (task 3, doc 15 OC-9 as built)
`rush-core` `drops` (VERSION 2): the queue is in `World` (8 × {trigger tick, kind, arg}), so prediction is exact.

| kind | effect (pure function of hazard seed, drop index, tick) | price |
|---|---|---|
| Lid Frenzy | 2 flips on disjoint seeded patterns of the top shelf, 45 ticks apart; each forces its lids Lit 30 ticks (1 s) → open 30 → Lit 30, then back to the schedule; the inner 3 × 3 and lids entering their retire warning never flip | 50 |
| Bumper Rain | 3 seeded lids; at the trigger everyone within 1.5 m (and ±2.5 m of the shelf) is knocked outward (800 / 450 cm/s, stun 12) | 40 |
| Wind Gust | 2 s push, one of 8 seeded headings, +160 mm/s per tick | 30 |
| Sweeper Rush | the sweeper at 1.5× for 3 s (its phase advance is kept); sold once the sweeper sweeps (t30 ≥ 600) | 40 |

`buy_drop`: the Final's RUSH phase only, ≤ 3 per round, 150-tick (5 s) cooldown, signer = wallet or session key;
written into the arena's `DropQueue` (`["drops", arena]`), which `tick()` reads read-only (`buy_drop` never write-locks
the World) and queues at **trigger = the tick it enters the World + 23**. The chips go to the `DropQueue` pot (the drop
vault, reported in the receipt). Bots see Lid Frenzy (the Scene overlay) and read the warped sweeper.

**Vectors:** the 5 P1/P2 files are byte-identical; new `drops_v1` (48 cases on Lid Flip with 1–3 random drops):
**native = WASM (Node) = sBPF = Unity FFI (EditMode, native bundle) = the 40-case browser subset** (`p1-check.sh`,
`parity.rs`, `rc-subset.mjs`). C# view port (`ChaosDropFx`) = rush-core (digest test `ChaosDropFxTests`).

**⛳ Fairness QA** (`rush-sim final-qa-drops`, 100 Finals, 3 bots, a seeded spectator buying under the program's rules):
| schedule | median | p90 | timer endings | lid openings without ≥ 1 s warning | telegraph min | deterministic |
|---|---|---|---|---|---|---|
| no drops (= P2: 77.3 s) | 77.3 s | 95.4 s | 1 | 0 | — | 100/100 |
| random kinds, 285 drops | **76.4 s** | 96.6 s | 0 | **0** | **23 ticks** | **100/100** |
| Lid Frenzy only (285) | 79.2 s | 95.4 s | 3 | 0 | 23 | 100/100 |
| Bumper Rain only (286) | 75.3 s | 91.3 s | 2 | 0 | 23 | 100/100 |
| Wind Gust only (289) | 78.8 s | 112.5 s | 5 | 0 | 23 | 100/100 |
| Sweeper Rush only (256) | 77.1 s | 99.4 s | 4 | 0 | 23 | 100/100 |

Found and fixed on the way (D-DROPS-FAIR): (1) the first Lid Frenzy overlaid the schedule and could re-open a lid 7–15
ticks after it closed (24 violations in 10 Finals) → the forced Lit/open/Lit sequence above (0); (2) Sweeper Rush alone
gave a 44 s median: the bots predicted the bar without the warp (and the kept phase offset) → bots read the warped
sweeper (77.1 s). Telegraph and caps unchanged (23 ticks, 3 per round), all 4 kinds kept.

**⛳ CU** (LiteSVM, `bench.rs` `BENCH_DROPS=1`, 3 players, a stress schedule *above* the program's caps: Frenzy + Bumper
Rain + Wind on one trigger, then Sweeper Rush + Frenzy + Wind): **worst tick 260,535 CU** (at the 3-drop trigger), p95
with a drop active 196,018, p50 162,725; the same run without drops 246,594. ≤ 300k. Rollup samples (the keeper samples task executions every ~15 s; 11 P4 devnet Finals, all
but the 3 +8 ones with Chaos Drops): **max 254,527 CU** (hybrid 3, with Lid Frenzy, Wind Gust and Sweeper Rush), p95 per Final
114k–187k.

## 5. Receipt, Crown Capsule, capsule drops (task 4, doc 15 OC-13)
`Match.receipt` replaces doc 06's unused Fan Picks (`picks[32]` + `pick_count`, the same 1,089 B: the `Match` layout and
every later offset are unchanged): trust bits (rounds 1–3 by the game authority ●, the Final by the program ◆), core
version, human mask + wallets, the 5 market results, map-blob hash, the transcript before the Final (authority) and the
director's Final transcript (program), drop log hash + count + chips spent, finish order, the capsule VRF seed + rarity
per human, the Crown address, minted flags. Written by the permissionless `write_receipt` op once the markets are
settled; `finalize_match` refuses an on-chain Final without it.

**Second VRF draw** at match end: `request_capsules` / `capsule_callback` (the deal's const callback identity, PB0);
rarity per human = sha256(seed, slot) mod 100: Common 70 / Rare 22 / Epic 7 / Legendary 1 (unit test over 10,000 seeds:
within ±1 %); shell = one of 12 (`CosmeticPresets` 0..8 + Starlight, Lava, Glacier, Aurora).

**Follow-up call on Solana (OC-13):** `mint_rewards` (permissionless, idempotent per asset) reads the *committed*
receipt and mints by a **hand-built Metaplex Core `CreateV1` CPI** (+ `Attributes` plugin) from the program: the Crown
(`["crown", match]`) to the champion's wallet, a capsule (`["capsule", match, slot]`) to each human's wallet; asset
rent from the program's treasury PDA, update authority the program's `["assets"]` PDA. **Spike result: the CPI fits
Anchor 1.0.2 / Solana 3 without the `mpl-core` crate** (LiteSVM with the devnet Core program: Crown 79,020 CU;
double mint refused; wrong owner refused; bots get none; metadata on chain). It is **not** a Magic Action (`#[action]`):
the keeper sends it after the commit and retries until every owed asset exists. Same trust as the action (the program
decides everything from the committed receipt); the action would only automate the trigger (its escrow funding and
wiring were not tested; revert path: add the post-undelegate action to `finalize_match`, the handler is ready).
Burner / guest owners: the asset goes to the burner wallet itself; "Claim with Phantom" is a Core `TransferV1` the
burner signs (no claim PDA needed: same custody as a PDA keyed to the burner).

## 6. Pots (task 5): **cut** (D-POTS)
Cut first by the decision table: Crown Pot lobbies need Classic-server lobby code + escrow instructions; the time went to
the exit gate. OC-10 holds as built: the market maker is labelled; no instruction pays a bot.

## 7. UI (task 6, player builds)
In-app screenshots from the Mac player build (`docs/proof/p4/ui/p4-ui-20261123-060942/ui-*.png`, devnet, the player
spectating with the auto-play probe betting and buying drops through the in-game economy, session-key signed, 0
exceptions) and the WebGL release build in Chrome (`docs/proof/p4/webgl/`):
- `ui-07`: chips pill (⬡ RUSH) + the bet panel (Champion ◆ program-settled, pool, odds x18.25, stakes, "bet on-chain ✓
  (session key, no popup)", "mm = the market maker bot").
- `ui-10`: the drop bar while spectating the Final's RUSH (prices, "1 left this round · map-wide, telegraphed 0.77 s").
- `ui-16`: the receipt card (champion, "a bot: no Crown", chips this match −77 RUSH, every market's pool with ◆ / ●,
  drops, "rounds 1–3 ● server-recorded · Final ◆ program-computed · core v2", **Rollup ✓ → Solana ✓**).
- Chaos Drop telegraphs (feel runs): `docs/proof/p4/devnet-window/p4-feel-*/*-drop*-{LID_FRENZY,WIND_GUST,BUMPER_RAIN}.png`.
- Found in the first player run and fixed: the HUD canvas and the drop view were destroyed by the map scene load (an
  exception per frame); small-button labels wrapped. The deal screen's pool / odds next to each capsule (prompt task 6) was
  **not** built: the deal screen shows before markets matter in the on-chain Final (§11).

## 8. Devnet runs (task 7)
### 8.1 Crown run (`scripts/p4-devnet-crown.sh`, `docs/proof/p4/runs/p4-crown-20261123-051847`)
On-chain Final on devnet-as, arena 2, match [zZ58FK3iL5…](https://explorer.solana.com/address/zZ58FK3iL5q64tVKqzno2Br2pa4sNoi7k663rXdZ4Ga?cluster=devnet): the 3 finalists are human
wallets (bundle "stand" players joined in 81 ms; synthetic rounds 1–3 with 5 s betting gaps), plus a bundle bettor and a
bundle spectator, the market maker. Real time 1.000 (tick gap p95 40 ms). **Champion slot 7 (human).**
- Markets: Champion pool 117 RUSH ◆, Final winner 81 ◆, Qualify R1 83 ●, R2 97 ●, R3 69 ●; 54 bets; all settled + paid.
  **Payout check OK: 5/5 markets (paid_total = Σ ⌊stake·pool/ws⌋), 6/6 wallets (won delta = hand calc)** (`payout-check.json`).
- Chaos Drops by the spectator (session key): Lid Frenzy (tick 164), Bumper Rain (339), Wind Gust (519); pot 120 RUSH.
- Receipt: flags written | capsule seed | Crown | markets; trust = rounds by the authority + the Final by the program;
  capsule rarity: slot 3 Rare, slot 4 Epic, slot 7 Common.
- Follow-up call: 5 attempts = 1 forced failure (`FAIL_FIRST`, wrong owner → `Unauthorized`) + Crown + 3 capsules, each once.

| what | link |
|---|---|
| RUSH mint (PDA mint authority) | [3s8KstqicX…](https://explorer.solana.com/address/3s8KstqicXn1kgFyWPVq3AZXkMprDtEtT7BNTxtQccEw?cluster=devnet) |
| program upgrade (P4) | [2U9TnBqkcN…](https://explorer.solana.com/tx/2U9TnBqkcNbr9B9j5TVzF8kVyLtdZfZzewajajgi3heKAEqdN8nxFxAo4ncxULiKAJFDsSREXsdPHnzEdVhs86PW?cluster=devnet) |
| open_markets (rollup) | [4NZ7J8Dd2r…](https://explorer.solana.com/tx/4NZ7J8Dd2rX9LKWpQRB8JpBDUFKUPxjhRGkdWLrD3Gi6378QLJV3VGtfcpb2XByJBd9XQZmiarM9QLzpkHuAiSsr?cluster=custom&customUrl=https%3A%2F%2Fdevnet-as.magicblock.app) |
| a bet, session key (rollup) | [2AkjGY2J97…](https://explorer.solana.com/tx/2AkjGY2J973H96dMVuwik4GgZuCma9ag8jBByykyQRZJjfe5UkpNjXmbMUHC6SNGMr4NKfVreDJFYZPPMaJQDttd?cluster=custom&customUrl=https%3A%2F%2Fdevnet-as.magicblock.app) |
| a Chaos Drop, session key (rollup) | [7JMFvcWdq4…](https://explorer.solana.com/tx/7JMFvcWdq41ZxyfyCdAkvPGgGeFwk7cWo9UHhxBNEZgUe3XJiAdTAc4u1zqtGs1iVHYuP2wbWbGpQKYJhACGtSC?cluster=custom&customUrl=https%3A%2F%2Fdevnet-as.magicblock.app) |
| settle + payout, Champion (rollup) | [5cmbViiwrc…](https://explorer.solana.com/tx/5cmbViiwrcogQ7MwqrBMCw6LcA2HPnCq7Khkbw7oNCuQWdykj5HisYKGDoxDWPUAxjWK3jp7a5vGq1mNLhzfAN6B?cluster=custom&customUrl=https%3A%2F%2Fdevnet-as.magicblock.app) |
| write_receipt (rollup) | [4fGmetXhie…](https://explorer.solana.com/tx/4fGmetXhieKjE9Q5LDA4Pz9NwLejXXpPrB6m5au21HpH4vzk4a1wtSXnCHc3HuTpSbKu2Hv75AX2c3AFH3uPFiMp?cluster=custom&customUrl=https%3A%2F%2Fdevnet-as.magicblock.app) |
| capsule VRF request (rollup) | [2saNwjZgiC…](https://explorer.solana.com/tx/2saNwjZgiCWa5BWkMQJcqF485DfTxxqwAqaa4Jye2vWgPyvrLzdBuohwQTEcXB3komqfg487uDFNzxoLxX8YUBEX?cluster=custom&customUrl=https%3A%2F%2Fdevnet-as.magicblock.app) |
| finalize = receipt commit + undelegate (rollup) | [3KZFmoEL8Y…](https://explorer.solana.com/tx/3KZFmoEL8YDTikVgasvWbbbiRT3ucEqkq64HTCqMpX7Fvg3kx7n9nHGWhUCaCgGtSK6Wk1Rbj1xQ7s8r8mm2k21Q?cluster=custom&customUrl=https%3A%2F%2Fdevnet-as.magicblock.app) |
| mint_rewards: Crown (Solana) | [q9RhHBNacz…](https://explorer.solana.com/tx/q9RhHBNaczSCNwpduKUGjp455VskeZF7viCQykBkMEx7doPYjz6U1G4t9jgrALi8yuqgtAGtjX7PgDYWqVNgpqa?cluster=devnet) |
| Crown Capsule asset (Metaplex Core) | [HLNWK6F2vy…](https://explorer.solana.com/address/HLNWK6F2vyXtsHfiWDaq5BwFBoXN3BjGBWtDHhP3bV6H?cluster=devnet) |
| capsule assets (slots 3 / 4 / 7) | [8d6SFeBgci…](https://explorer.solana.com/address/8d6SFeBgcitcisjU3NMvgV5GaVFxjsH9Gk8HCPvKzbfq?cluster=devnet) [9waf89SZxi…](https://explorer.solana.com/address/9waf89SZxivESbig7WzBLkh4rVUK8RaEBKEiCzzh2SGo?cluster=devnet) [8RGjCXSPxw…](https://explorer.solana.com/address/8RGjCXSPxwpsftoySt6aR8cZAmF6zSUpWAunqQ1oJKps?cluster=devnet) |
| Claim (bundle `econ('claim')`: Core TransferV1 by the burner to a stand-in Phantom wallet) | [1ARi6L4zun…](https://explorer.solana.com/tx/1ARi6L4zunxurDG1ZJyhZofpVXp61s1xpZFB6YtArW51fkFpACwFSmLexUiu3yrSFDco8Qyr3f9BXxhpStBYp6y?cluster=devnet) |

### 8.2 Hybrid matches (`scripts/p4-devnet-hybrid.sh`): Classic rounds 1–3 → the on-chain Final, with the economy
The Classic Mac dev server (12 slots, 11 bots + the Mac player as a match probe) plays rounds 1–3; the keeper records them
on the `Match` (chain worker), opens the markets + market maker, starts the Final on the rollup, settles, writes the
receipt, finalizes, mints. Bettors: 2 bundle burners (A, B: session keys) + the market maker; a bundle spectator buys 3
drops in every Final.
| run | match | champion | real time | pools Ch / FW / Q1 / Q2 / Q3 (RUSH) | bets | drops | payout check | client hand-off |
|---|---|---|---|---|---|---|---|---|
| p4-hyb-20261123-054008 | — | — | — | keeper died at setup (`fetch failed`, network) | — | — | — | — |
| p4-hyb-20261123-054522 | [3Equ1P3S…](https://explorer.solana.com/address/3Equ1P3Smokg2M9dGyfwBq3rJUubUypZuLVXuXoSe9wQ?cluster=devnet) | 10 | 1.000 | 89 / 55 / 0 / 101 / 63 | 36 | 3 | OK (5 wallets) | no (pre-P3 server build) |
| p4-hyb-20261123-055316 | [4Jqn5R7i…](https://explorer.solana.com/address/4Jqn5R7ieYW9GmBvNMqFcgUji8Wid9vUiMdVdr5SKr5i?cluster=devnet) | 9 | 1.002 | 77 / 41 / 8 / 85 / 63 | 36 | 3 | OK (5 wallets) | no (pre-P3 server build) |
| p4-hyb-20261123-062428 | [8UsXiayM…](https://explorer.solana.com/address/8UsXiayMDGJwEtJqBm55H6y5ZSsrqQBjLcPPFLa34dPr?cluster=devnet) | 2 | 0.994 | 76 / 55 / 8 / 83 / 57 | 37 | 3 | OK (5 wallets) | no (pre-P3 server build) |
| p4-hyb-20261123-063359 | [CrE7gH7d…](https://explorer.solana.com/address/CrE7gH7dXnZFDqTE1dVtFXSGjyPTzDuaKMd3pxbrhpQC?cluster=devnet) | 1 | 1.003 | 78 / 49 / 17 / 83 / 51 | 38 | 3 | OK (5 wallets) | no (pre-P3 server build) |
| p4-hyb-20261123-073639 | [6GmxhbC4…](https://explorer.solana.com/address/6GmxhbC4ZJQfABXn6PwdyKk577v2sbPpzaVYXFkoF27r?cluster=devnet) | 1 | 1.001 | 87 / 67 / 17 / 101 / 81 | 42 | 3 | OK (5 wallets) | **yes** (Mac player bet 4 markets, session key) |

**5 complete hybrid matches** (+1 that died at setup). The first 4 ran on a dev server build from main that predates
P3's hand-off (`client-unity/Builds/MacDevServer` was dated 2026-11-21): the chain side is the real hybrid (Classic
rounds recorded by the chain worker, the Final on the rollup), but the server also played its own Final and the Mac
player never switched. The 5th (`p4-hyb-…-073639`) used a server built from this worktree: `final_prejoin` →
the Mac player bet through the in-game economy (Champion, Qualify R2, R3, Final winner; session key) → `final_onchain`
→ the Mac player's payout matched the hand calculation (+71.8 RUSH). Qualify R1 is thin in hybrids: its window is the
few seconds between the deal and Classic round 1.


### 8.3 WebGL release build (`scripts/p4-webgl.sh`, Chrome via `p3-webgl-measure.mjs`)
| check | result |
|---|---|
| build | **22,374,978 B** (≤ 25 MB; P3 22.29 MB), 1,497 s |
| a `?rcselftest=1` | all 6 files PASS (240 cases incl. `drops_v1`), core 0x10002 (summary line miscounted, §11) |
| d `?onchain=devnet` Final + economy | the page's burner got chips; the in-game economy auto-bet 3 markets and bought 2 drops (Lid Frenzy, Wind Gust); the Final completed on-chain, real time 1.000 |
| e plain Classic match (Mac dev server, `?bot=match`) | full match to `COMPLETE_MATCH` (regression OK; the first PLAY click came before the menu was interactive, now retried) |

## 9. Feel (automated)
2 devnet Finals (`p4-feel-*`, the Mac player auto-playing at window +12, a bundle spectator buying Lid Frenzy, Wind Gust,
Bumper Rain): **every drop reached the client 23 ticks (0.77 s) before it resolved (6/6, the client's `drop seen`
log)**; the keeper's own recorder saw two of them later (16, 14 ticks) only because it skips snapshots. **0
eliminations during a drop's effect or within 1 s after it** (4 eliminations in total, `drops.json` from the World
recordings). Corrections 0.13 and 1.06 per 5 s. Telegraph screenshots: `docs/proof/p4/devnet-window/p4-feel-*/*-drop*.png`.
Fairness itself is decided by the ⛳ QA (§4). `scripts/p4-feel.sh` is ready for you to play 2 Finals with drops (keyboard +
mouse; a spectator buys drops); not played yet.

## 10. SOL
| item | SOL (measured from the balances, `docs/proof/p4/sol.md`) |
|---|---|
| start (prompt §0) | admin 5.6478 + game authority 3.4904 = 9.1382 |
| Ephemeral SPL spike (2 funded test wallets, mint, ATAs) + the window A/B before the upgrade (relay burners, 3 keeper Finals) | ≈ 0.09 |
| program extend +24,576 B (permanent) + upgrade fees (the ~7.7 SOL buffer was funded by moving 2.28 SOL authority → admin and back) | 0.131 |
| economy setup on devnet: 5 markets + drop queue rent, delegations, treasury 0.06, RUSH mint, market maker | ≈ 0.21 |
| bundle players / relay burners (0.02 each; their keys are reused) | ≈ 0.18 |
| ~15 keeper Finals (Match receipt rent + fees, ~0.011 each) + Core asset rent from the treasury | ≈ 0.2 |
| **end** | admin **5.2618** + game authority **3.0658** = 8.3277: **P4 total 0.811 SOL** (budget 2.0); admin ≥ 1.5 ✓ |

## 11. UNVERIFIED
- Pots: cut (D-POTS), not built.
- The `#[action]` Magic Action for the follow-up call: not used (OC-13); minting is a keeper-sent program instruction.
- The prompt's deal-screen pool / odds next to each capsule: not built (the bet panel shows them).
- "Claim with Phantom" with a real Phantom extension: the claim path ran through the bundle with the burner signing
  (Core TransferV1, devnet signature in §8.1) to a stand-in wallet; Phantom in a browser was not driven headless.
- `cashout` → `mint_cashout` on devnet: `mint_cashout` is tested in LiteSVM (SPL minted once, then the difference);
  the rollup `cashout` + commit is UNVERIFIED on devnet.
- A market with unpaid bets after its `Match` was finalized (undelegated) cannot be paid any more (settle reads the
  `Match` on the rollup); the keeper settles and pays before `finalize_match`. A keeper crash in between needs a manual
  fix (UNVERIFIED path).
- The WebGL `?rcselftest=1` summary line printed `FAIL 6/5 files` although all 6 files (240 cases incl. drops_v1)
  passed: the self-test still counted 5 files (fixed in code after the one release build; not rebuilt).
- Sybil faucet: a new burner gets new chips (devnet test chips; a rate limit per wallet only).
- The automated feel numbers come from the auto-play probe; a human feel test with drops (`scripts/p4-feel.sh`) is
  open for you.
- The rollup CU numbers are samples (one every ~15 s per Final); LiteSVM measured every tick.

## For P5
Where the web spectator page (P5) plugs in — everything is readable on the rollup (devnet-as) and the bundle already
implements it (`packages/chain/src/econ.ts`, `RollupTransport.econ`):
- **Subscribe** (rollup): `WorldAcc` (`["world", arena]`; P2 layout + P4 header: core_version u16 @142, serial u32
  @194, input_lead @198, round_phase @199; the World's drop queue at World offset 1720), the 5 `Market`s
  (`["market", arena id, k]`, 3,288 B; layout in `econ.rs` / `decodeMarket`), the `DropQueue` (`["drops", arena]`,
  416 B), the `Match` (`Arena.match_account`; the receipt at borsh offset 295), a viewer's `ChipBalance`
  (`["chips", wallet]`, 104 B).
- **Write** (one instruction): `econ(op u8, a u64, b u64)`, discriminator `f5f15668f07d243a`, accounts = the signer, then
  per op (`econ::op`): chips init 1 `[chips, system]` (base) · faucet 2 `[chips, (token)]` · cashout 3 · mint_cashout 4
  (base) · place_bet 7 (a = slot, b = amount in base units) `[chips, market, match, arena, world, (token)]` · settle 8 ·
  buy_drop 9 (a = kind 1..4) `[chips, drop_queue, arena, world, (token)]` · write_receipt 10 · mint_rewards 11 (base).
  Signer = the wallet or its session key (+ the `SessionTokenV2` as the trailing account). Delegating a new
  `ChipBalance`: `delegate_sim(6, 0, 0)` by any payer.
- **Rules to mirror in the UI** (the program enforces them): `ChainEconomy.MarketOpen` / `DropOpen` (C#) = `open()` in
  `tools/p4/bundle-player.mjs` (JS).
- **Labels:** market `label` 1 ◆ program-settled, 2 ● server-recorded, 3 ● Classic fallback (refund), 4 aborted, 5 no
  winner; receipt `trust` bit 0 rounds 1–3 by the game authority, bit 1 the Final by the program.
