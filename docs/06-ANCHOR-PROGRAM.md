# 06 — Anchor Program Specification: `fall_rush`

**Status:** v2 (2026-11-18). Replaces `fall-rush-docs/06-ANCHOR-PROGRAMS.md`.
**Toolchain:** see 07 §1 (Anchor 1.0.2 + ER SDK 0.16.2 + session-keys 3.1.1).

**What it does:** the program records the competitive lifecycle of a match. It does not simulate physics. It enforces:
- authorised signers
- ordered events
- roster-consistent outcomes
- rule-consistent counts
- an on-chain VRF deal

---

## 1. Design decisions (resolving v1 contradictions)

| v1 problem | v2 resolution |
|---|---|
| `MatchRegistry` + `MatchState` duplicated fields | **One `Match` PDA** (delegated during play; it is the receipt afterwards) + one `GameConfig` PDA |
| Roster keyed by `Pubkey`, but guests have no wallet | Roster by **slot index 0..19**. `roster_hash` commits to `(slot, H(user_id), wallet?)` per slot |
| `map_sequence_hash` supplied before VRF / by the caller | At creation, commit `ruleset_hash` + `map_pool_hash`. **The VRF callback derives the deal on-chain.** |
| Caller-supplied `transcript_hash` | **The program computes a rolling hash** of every event it accepts |
| Champion could be assigned arbitrarily | `complete_match` requires exactly one qualified slot in the final round, and that slot is the champion |

## 2. Accounts

### 2.1 `GameConfig`: seeds `[b"config"]`

```rust
pub struct GameConfig {
    pub version: u8,                 // 1
    pub bump: u8,
    pub admin: Pubkey,               // deployer multisig later; can rotate game_authority / pause
    pub game_authority: Pubkey,      // Rush Relay signer (env-specific); NOT upgrade authority, NOT treasury
    pub ruleset_version: u16,
    pub ruleset_hash: [u8; 32],      // sha256 of canonical ruleset_v1.json
    pub map_pool_version: u16,
    pub map_pool_hash: [u8; 32],     // sha256 of canonical map_pool_v1.json
    pub paused: bool,
    pub match_count: u64,
}
```

### 2.2 `Match`: seeds `[b"match", match_id]` with `match_id: [u8; 16]` (UUIDv4 bytes)

```rust
pub const MAX_SLOTS: usize = 20;
pub const MAX_PICKS: usize = 32;
pub const ROUNDS: usize = 4;

pub struct Match {
    pub version: u8, pub bump: u8,
    pub match_id: [u8; 16],
    pub status: MatchStatus,
    pub flags: u8,                     // bit0 = unranked
    pub ruleset_version: u16, pub ruleset_hash: [u8; 32],
    pub map_pool_version: u16, pub map_pool_hash: [u8; 32],
    pub roster_hash: [u8; 32],
    pub player_count: u8,
    pub current_round: u8,             // 0 = none, 1..=4
    pub round_targets: [u8; ROUNDS],   // derived on-chain from player_count (GDD §4.1)
    pub event_seq: u32,                // next expected sequence
    pub transcript_hash: [u8; 32],     // rolling: H(prev ‖ event_bytes)
    // VRF deal
    pub vrf_seed: [u8; 32],
    pub deal_maps: [u8; ROUNDS],       // map index per round
    pub deal_variants: [u8; ROUNDS],
    pub deal_modifiers: [u8; ROUNDS],
    // per-slot results
    pub slots: [SlotResult; MAX_SLOTS],
    pub champion_slot: u8,             // 255 = none
    // fan picks
    pub picks: [Pick; MAX_PICKS], pub pick_count: u8,
    pub created_at: i64, pub dealt_at: i64, pub completed_at: i64,
}
pub struct SlotResult { pub state: u8 /*0 empty,1 alive,2 qualified,3 eliminated*/,
                        pub round: u8, pub placement: u8, pub reason: u8 }
pub struct Pick { pub picker: Pubkey, pub round: u8, pub slot: u8 }
```

**Size:** fixed, about 1.5 KB (exact `INIT_SPACE` is computed at build time). There are no `Vec`s.

### 2.3 `MatchStatus`

```
Created → Dealing → Dealt → RoundActive(r) → RoundResolved(r) → … → Completed
   └──────────────┴───────┴──────────── Aborted (terminal) ◄── abort_match from any non-terminal state
```

The **canonical** state machine is shared with the game server (doc 05 §4) and the database. One enum, one mapping table.

## 3. Instructions

| # | Instruction | Layer | Signer | Preconditions → effects |
|---|---|---|---|---|
| 1 | `initialize_config(game_authority, ruleset_version, ruleset_hash, map_pool_version, map_pool_hash)` | Solana | admin (upgrade authority at deploy) | Once only |
| 2 | `update_config(...)` / `set_paused(bool)` | Solana | admin | Rotate the authority or the hashes |
| 3 | `create_match(match_id, roster_hash, player_count)` | Solana | game_authority | Not paused; 4 ≤ player_count ≤ 20. Copies hashes and versions from config, derives `round_targets`, sets slots `0..n` to alive, `status=Created`. |
| 4 | `delegate_match(match_id)` | Solana | game_authority | `status==Created` (read through manual deserialization before the delegate CPI). Validator in `remaining_accounts[0]`. |
| 5 | `request_deal()` | **ER** | game_authority | `status==Created` → `Dealing`. Requests VRF with `caller_seed = sha256("fallrush-deal" ‖ match_id ‖ ruleset_hash ‖ map_pool_hash)`. |
| 6 | `deal_callback(randomness)` | **ER** | VRF scoped identity | `status==Dealing` → `Dealt`. Stores `vrf_seed`. Derives the deal (§4). Appends to the transcript. |
| 7 | `start_round(round, seq)` | ER | game_authority | `Dealt` or `RoundResolved(round-1)`; `round == current_round+1` → `RoundActive(round)` |
| 8 | `record_qualified(round, slot, placement, seq)` | ER | game_authority | `RoundActive(round)`; slot alive; qualified count so far < target; `placement == qualified_count+1` |
| 9 | `record_eliminated(round, slot, reason, seq)` | ER | game_authority | `RoundActive(round)`; slot alive |
| 10 | `complete_round(round, seq)` + **commit** | ER | game_authority | `RoundActive(round)`; **no alive slots remain** (all qualified or eliminated); 1 ≤ qualified ≤ target. Qualified slots return to `alive` for the next round, and their result is recorded in `round`. → `RoundResolved(round)`. Commits the account to Solana. |
| 11 | `submit_pick(round, slot)` | ER | wallet **or session key** (§07 4.6) | `RoundResolved(round-1)` (intermission) or `Dealt` for round 1. The slot is alive. One pick per picker per round. `pick_count < 32`. |
| 12 | `complete_match(seq)` | ER | game_authority | `RoundResolved(4)` (or an earlier round if only one player is left) with exactly 1 qualified → `champion_slot`, `Completed` |
| 13 | `finalize_match()` | ER | game_authority | `Completed` or `Aborted` → **commit_and_undelegate** |
| 14 | `abort_match(reason)` | ER or Solana | game_authority or admin | Not `Completed` / `Aborted` → `Aborted` |
| 15 | `process_undelegation` | — | injected by `#[ephemeral]` | Delegation program callback |

**Sequence rule:**
- Every event-bearing instruction takes `seq` and requires `seq == match.event_seq`, then increments it.
- This makes retries idempotent (a duplicate fails with `InvalidSequence`, and the worker then re-reads) and gives a total order.

**Transcript:**
`transcript_hash = sha256(transcript_hash ‖ ix_tag ‖ seq_le ‖ round ‖ slot ‖ arg)`, using `solana_program::hash::hashv`.

The game server computes the same chain off-chain, and the receipt page shows that both match.

## 4. On-chain deal derivation (VRF callback)

```
r = randomness (32 bytes)
deal_maps[0]      = pool.round1_maps[ rnd(r, "map1") ]     // MVP pool: [BlockPartyDash] (+BonkFactory when shipped)
deal_maps[1..3]   = fixed per mode in map_pool v1           // GooTower, StarScramble, CrumbleShelf
deal_variants[i]  = rnd(r, "var"‖i)  in 0..variant_count[i]
deal_modifiers[i] = weighted(rnd(r, "mod"‖i), modifier_table[deal_maps[i]])
hazard_seed       = vrf_seed (clients use H(vrf_seed ‖ round ‖ obstacleId))
```

**Pool layout:**
- `rnd(r, label)` = `sha256(r ‖ label)`, then **rejection sampling** on a u32 to an unbiased range.
- The pool tables (counts and weights) are compiled into the program as `const` for map pool v1.
- Their canonical JSON hash must equal `config.map_pool_hash`. This is asserted in tests.
- A new pool needs a new program version. That is acceptable for the hackathon.

**Shared test vectors:** the same function exists in `packages/shared/deal.ts` and `Deal.cs`, all three checked against `deal_vectors.json` (100 seeds).

## 5. Errors

`Unauthorized, Paused, InvalidStatus, InvalidRound, InvalidSequence, InvalidSlot, SlotNotAlive, TargetExceeded, InvalidPlacement, RoundNotResolved, InvalidPlayerCount, InvalidChampion, PickWindowClosed, DuplicatePick, PicksFull, InvalidVrfCallback, AlreadyFinal`.

## 6. Events (for indexing; account state remains authoritative)

`MatchCreated, MatchDelegated, DealRequested, DealReady{maps,variants,modifiers}, RoundStarted, PlayerQualified, PlayerEliminated, RoundCompleted{qualified}, PickSubmitted, MatchCompleted{champion_slot, transcript_hash}, MatchAborted`.

## 7. Security tests (`programs/fall_rush/tests/`)

Each test below must fail with the specific error:
- wrong signer on each instruction
- wrong PDA / match id
- callback not from the scoped VRF identity (call `deal_callback` directly)
- replayed callback
- duplicate `seq`, skipped `seq`
- skipped round
- qualifying an eliminated slot
- qualifying over target
- completing a round with alive slots
- completing the match with 2 qualified
- abort after completion
- a pick outside the window, a duplicate pick, a session token for the wrong authority
- `player_count` out of range
- paused config

**Lifecycle tests on the local ER stack:**
- create → delegate → deal → 4 rounds → finalize
- after finalize, the account is owned by `fall_rush` on Solana with the expected final values

## 8. Trust statement (copied to README)

The program guarantees:
- the match setup came from verified VRF;
- results were written in order, by the configured game authority, for roster slots only, with counts consistent with the ruleset.

It **does not** prove that physical gameplay was fair. That is decided by our authoritative server.

**Post-hackathon trust minimisation:**
- signed input transcripts and replay verification;
- independent observers;
- multisig for `admin`.

---

## 9. M0 implementation notes & corrections (2026-11-18)

M0 implements instructions 1, 3–10, 12–15 (no `update_config`/`set_paused`/`submit_pick` yet). Account layouts are final (§2), so M6 needs no migration.

- **Status encoding:** `MatchStatus` is fieldless (`Created, Dealing, Dealt, RoundActive, RoundResolved, Completed, Aborted`); `RoundActive(r)`/`RoundResolved(r)` = status + `current_round == r`.
- **Config hashes are compiled in:** `initialize_config` requires `ruleset_hash`/`map_pool_hash` (v1) to equal constants in `state.rs`; a Rust test asserts those equal the canonical JSON hashes. New errors: `InvalidRuleset`, `InvalidMapPool`, `Overflow`. `initialize_config` requires the signer to be the program's upgrade authority (ProgramData check, as in `rewards-delegated-vrf`).
- **Sequence:** `seq == event_seq` is checked **first** in every event handler, so a retried event always fails with `InvalidSequence`. `deal_callback` records the deal as event **seq 0**; `abort_match(reason)` takes no `seq` and consumes the current one.
- **Transcript:** `init = sha256("fallrush-transcript-v1" ‖ match_id ‖ roster_hash)`; `next = sha256(prev ‖ tag ‖ seq_le32 ‖ round ‖ slot ‖ arg)`; tags 1 DEAL(arg=randomness) 2 START_ROUND 3 QUALIFIED(arg=placement) 4 ELIMINATED(arg=reason) 5 COMPLETE_ROUND(arg=qualified) 6 COMPLETE_MATCH(slot=champion) 7 ABORT(arg=reason); slot 255 = none. Mirrored in `packages/shared/src/transcript.ts` and checked in the lifecycle test.
- **Deal (§4) made precise:** labels are `map<r>`, `var<r>`, `mod<r>` with the ASCII round digit; `rnd` reads the digest as 8 LE u32 words, accepts `x < floor(2^32/n)·n`, rehashes the digest after 8 rejections. Pool v1: every map has 2 variants; modifier weights None 40 / others 15 (provisional, GDD §8 allowed-maps table; Goo Tower = None only).
- **`request_deal`** accepts `Created` **or `Dealing`** (doc 07 §5 retry); a second callback fails `InvalidVrfCallback` (the replay error). Direct callback from a non-VRF signer fails `ConstraintAddress`.
- **`complete_round`:** `RoundNotResolved` if any slot is alive or nobody qualified; qualified slots return to alive (keeping `round`/`placement`).
- **`complete_match`:** `RoundResolved` and exactly one alive slot (`InvalidChampion` otherwise).
- **`finalize_match` M0 shortcut:** also allowed from `RoundResolved` (devnet M0 run finalizes after round 1). **Remove in M6.**
- **Tests:** security tests not yet written for: paused config (no `set_paused` yet), picks/session keys, wrong PDA/match id, replayed callback with a real oracle. Covered: wrong signer (create, gameplay, abort, config), direct callback, dup/skipped seq, skipped round, eliminated slot, over target, alive slots, champion with >1 alive, abort after final, player_count out of range.
