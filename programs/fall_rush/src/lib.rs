//! Fall Rush: competitive match lifecycle on a MagicBlock Ephemeral Rollup (doc 06).
//! M0 implements a forward-compatible subset; see docs/06-ANCHOR-PROGRAM.md.
//!
//! Trust model: the program checks *who* wrote results, *in what order* and that counts match the
//! committed ruleset. It does not verify physical gameplay (decided by the authoritative server).

use anchor_lang::prelude::*;
use ephemeral_rollups_sdk::anchor::{commit, delegate, ephemeral, vrf, vrf_callback};
use ephemeral_rollups_sdk::cpi::DelegateConfig;
use ephemeral_rollups_sdk::vrf::consts::{program_identity_pubkey, scoped_vrf_identity_const};
use ephemeral_rollups_sdk::vrf::instructions::{
    create_request_randomness_ix_const, RequestRandomnessParams,
};
use ephemeral_rollups_sdk::vrf::types::SerializableAccountMeta;

/// P6 (program size): `keq!` = `require_keys_eq!` without Anchor's "Left / Right" pubkey logging (each call site inlined
/// `core::fmt` code; ~100 call sites). Same error codes.
macro_rules! keq {
    ($a:expr, $b:expr, $e:expr $(,)?) => {
        if $a != $b {
            return Err(anchor_lang::error::Error::from($e));
        }
    };
}

pub mod deal;
pub mod econ;
pub mod errors;
pub mod events;
pub mod magic;
pub mod p6;
pub mod sim;
pub mod state;

#[cfg(target_os = "solana")]
pub mod sdiv;

use errors::FallRushError;
use events::*;
use state::*;

declare_id!("4yehffTS5zxw9vqHpmiAgxoqqUDBVgrCTyH5eyL4Dsk4");

/// Request signer for VRF draws: `PDA(["identity"], crate::ID)`, derived at compile time
/// (SDK 0.18; replaces the runtime lookup inside the removed `create_request_scoped_randomness_ix`).
pub const PROGRAM_IDENTITY: Pubkey = program_identity_pubkey(&crate::ID);

/// The only signer allowed to call `deal_callback`: the scoped VRF identity
/// `PDA(["identity", crate::ID], VRF program)`. Declared here (not injected by `#[vrf_callback]`)
/// because SDK 0.18.0's macro calls `scoped_vrf_identity_const` in a runtime expression: the
/// ed25519 off-curve check then runs on-chain and burns the full 200k CU (measured, PB0 §3).
pub const VRF_CALLBACK_IDENTITY: Pubkey =
    Pubkey::new_from_array(scoped_vrf_identity_const(&crate::ID).0);

/// VRF queue pinned per build: local MagicBlock stack vs devnet/mainnet ER (doc 07 §4.3).
#[cfg(feature = "localnet")]
pub const VRF_QUEUE: Pubkey = ephemeral_rollups_sdk::vrf::consts::DEFAULT_EPHEMERAL_TEST_QUEUE;
#[cfg(not(feature = "localnet"))]
pub const VRF_QUEUE: Pubkey = ephemeral_rollups_sdk::vrf::consts::DEFAULT_EPHEMERAL_QUEUE;

#[ephemeral]
#[program]
pub mod fall_rush {
    use super::*;

    /// Solana. Once only (PDA `init`). Signer must be the program's upgrade authority.
    pub fn initialize_config(
        ctx: Context<InitializeConfig>,
        game_authority: Pubkey,
        ruleset_version: u16,
        ruleset_hash: [u8; 32],
        map_pool_version: u16,
        map_pool_hash: [u8; 32],
    ) -> Result<()> {
        require!(
            upgrade_authority_of(&ctx.accounts.program, &ctx.accounts.program_data) == Some(ctx.accounts.admin.key()),
            FallRushError::Unauthorized
        );
        require!(
            ruleset_version == RULESET_V1_VERSION && ruleset_hash == RULESET_V1_HASH,
            FallRushError::InvalidRuleset
        );
        require!(
            map_pool_version == MAP_POOL_V1_VERSION && map_pool_hash == MAP_POOL_V1_HASH,
            FallRushError::InvalidMapPool
        );
        let cfg = &mut ctx.accounts.config;
        cfg.version = 1;
        cfg.bump = ctx.bumps.config;
        cfg.admin = ctx.accounts.admin.key();
        cfg.game_authority = game_authority;
        cfg.ruleset_version = ruleset_version;
        cfg.ruleset_hash = ruleset_hash;
        cfg.map_pool_version = map_pool_version;
        cfg.map_pool_hash = map_pool_hash;
        cfg.paused = false;
        cfg.match_count = 0;
        Ok(())
    }

    /// Solana. Creates the Match PDA with on-chain round targets and slots 0..n alive.
    pub fn create_match(
        ctx: Context<CreateMatch>,
        match_id: [u8; 16],
        roster_hash: [u8; 32],
        player_count: u8,
    ) -> Result<()> {
        let cfg = &mut ctx.accounts.config;
        require!(!cfg.paused, FallRushError::Paused);
        let targets = round_targets(player_count)?;

        let m = &mut ctx.accounts.match_account;
        m.version = 1;
        m.bump = ctx.bumps.match_account;
        m.match_id = match_id;
        m.status = MatchStatus::Created;
        m.flags = 0;
        m.ruleset_version = cfg.ruleset_version;
        m.ruleset_hash = cfg.ruleset_hash;
        m.map_pool_version = cfg.map_pool_version;
        m.map_pool_hash = cfg.map_pool_hash;
        m.roster_hash = roster_hash;
        m.player_count = player_count;
        m.current_round = 0;
        m.round_targets = targets;
        m.event_seq = 0;
        m.transcript_hash = transcript_init(&match_id, &roster_hash);
        for i in 0..player_count as usize {
            m.slots[i] = SlotResult {
                state: SLOT_ALIVE,
                round: 0,
                placement: 0,
                reason: 0,
            };
        }
        m.champion_slot = NO_SLOT;
        m.receipt = Receipt::default();
        m.created_at = Clock::get()?.unix_timestamp;

        cfg.match_count = cfg
            .match_count
            .checked_add(1)
            .ok_or(FallRushError::Overflow)?;

        emit!(MatchCreated {
            match_id,
            player_count,
            round_targets: targets,
        });
        Ok(())
    }

    /// Solana. Delegates the Match PDA to the ER validator in `remaining_accounts[0]`.
    /// The account is an `UncheckedAccount` (required by `#[delegate]`), so status is checked by
    /// deserializing it manually first.
    pub fn delegate_match(ctx: Context<DelegateMatch>, match_id: [u8; 16]) -> Result<()> {
        {
            let info = ctx.accounts.match_account.to_account_info();
            keq!(*info.owner, crate::ID, FallRushError::InvalidStatus);
            let data = info.try_borrow_data()?;
            let m = Match::try_deserialize(&mut &data[..])?;
            require!(m.status == MatchStatus::Created, FallRushError::InvalidStatus);
            require!(m.match_id == match_id, FallRushError::InvalidStatus);
        }
        let validator = ctx.remaining_accounts.first().map(|a| a.key());
        ctx.accounts.delegate_match_account(
            &ctx.accounts.authority,
            &[MATCH_SEED, &match_id],
            DelegateConfig {
                validator,
                ..Default::default()
            },
        )?;
        emit!(MatchDelegated {
            match_id,
            validator
        });
        Ok(())
    }

    /// ER. Requests a scoped VRF draw; the deal is derived inside `deal_callback`.
    /// `Dealing` is accepted too so the worker can retry once with the same caller seed
    /// (doc 07 §5); a second callback is rejected by `deal_callback`'s status check.
    pub fn request_deal(ctx: Context<RequestDeal>) -> Result<()> {
        let (match_key, caller_seed, match_id) = {
            let m = &mut ctx.accounts.match_account;
            require!(
                m.status == MatchStatus::Created || m.status == MatchStatus::Dealing,
                FallRushError::InvalidStatus
            );
            m.status = MatchStatus::Dealing;
            (m.key(), deal_caller_seed(m), m.match_id)
        };
        // Scoped request (`data[0] = 10`): the oracle signs the callback with
        // `PDA(["identity", crate::ID], VRF program)`, which `#[vrf_callback]` checks.
        let ix = create_request_randomness_ix_const(RequestRandomnessParams {
            payer: ctx.accounts.authority.key(),
            oracle_queue: ctx.accounts.oracle_queue.key(),
            callback_program_id: crate::ID,
            callback_discriminator: instruction::DealCallback::DISCRIMINATOR.to_vec(),
            caller_seed,
            accounts_metas: Some(vec![SerializableAccountMeta {
                pubkey: match_key,
                is_signer: false,
                is_writable: true,
            }]),
            ..Default::default()
        }, PROGRAM_IDENTITY);
        ctx.accounts
            .invoke_signed_vrf(&ctx.accounts.authority.to_account_info(), &ix)?;
        emit!(DealRequested {
            match_id,
            caller_seed
        });
        Ok(())
    }

    /// ER. VRF callback; `#[vrf_callback]` requires the scoped VRF identity as signer.
    pub fn deal_callback(ctx: Context<DealCallback>, randomness: [u8; 32]) -> Result<()> {
        let m = &mut ctx.accounts.match_account;
        require!(
            m.status == MatchStatus::Dealing,
            FallRushError::InvalidVrfCallback
        );
        let d = deal::derive_deal(&randomness);
        m.vrf_seed = randomness;
        m.deal_maps = d.maps;
        m.deal_variants = d.variants;
        m.deal_modifiers = d.modifiers;
        m.dealt_at = Clock::get()?.unix_timestamp;
        m.status = MatchStatus::Dealt;
        m.append(TAG_DEAL, 0, NO_SLOT, &randomness)?;
        emit!(DealReady {
            match_id: m.match_id,
            vrf_seed: randomness,
            maps: d.maps,
            variants: d.variants,
            modifiers: d.modifiers,
        });
        Ok(())
    }

    /// ER. `Dealt` (round 1) or `RoundResolved(round-1)` → `RoundActive(round)`.
    pub fn start_round(ctx: Context<GameplayEvent>, round: u8, seq: u32) -> Result<()> {
        let m = &mut ctx.accounts.match_account;
        m.check_seq(seq)?;
        require!(
            m.status == MatchStatus::Dealt || m.status == MatchStatus::RoundResolved,
            FallRushError::InvalidStatus
        );
        require!(
            (round as usize) <= ROUNDS && round == m.current_round + 1,
            FallRushError::InvalidRound
        );
        m.current_round = round;
        m.status = MatchStatus::RoundActive;
        m.append(TAG_START_ROUND, round, NO_SLOT, &[])?;
        emit!(RoundStarted {
            match_id: m.match_id,
            round,
            seq
        });
        Ok(())
    }

    /// ER. Alive slot qualifies with placement = qualified_count + 1, never above the target.
    pub fn record_qualified(
        ctx: Context<GameplayEvent>,
        round: u8,
        slot: u8,
        placement: u8,
        seq: u32,
    ) -> Result<()> {
        let m = &mut ctx.accounts.match_account;
        m.check_seq(seq)?;
        m.require_active_round(round)?;
        let i = m.alive_slot(slot)?;
        let qualified = m.count_slots(SLOT_QUALIFIED);
        require!(
            qualified < m.target(round)?,
            FallRushError::TargetExceeded
        );
        require!(
            placement == qualified + 1,
            FallRushError::InvalidPlacement
        );
        m.slots[i] = SlotResult {
            state: SLOT_QUALIFIED,
            round,
            placement,
            reason: 0,
        };
        m.append(TAG_QUALIFIED, round, slot, &[placement])?;
        emit!(PlayerQualified {
            match_id: m.match_id,
            round,
            slot,
            placement,
            seq
        });
        Ok(())
    }

    /// ER. Alive slot is eliminated in `round`.
    pub fn record_eliminated(
        ctx: Context<GameplayEvent>,
        round: u8,
        slot: u8,
        reason: u8,
        seq: u32,
    ) -> Result<()> {
        let m = &mut ctx.accounts.match_account;
        m.check_seq(seq)?;
        m.require_active_round(round)?;
        let i = m.alive_slot(slot)?;
        m.slots[i] = SlotResult {
            state: SLOT_ELIMINATED,
            round,
            placement: 0,
            reason,
        };
        m.append(TAG_ELIMINATED, round, slot, &[reason])?;
        emit!(PlayerEliminated {
            match_id: m.match_id,
            round,
            slot,
            reason,
            seq
        });
        Ok(())
    }

    /// ER + commit. No alive slots, 1 ≤ qualified ≤ target. Qualified slots return to alive for
    /// the next round (their `round`/`placement` keep this round's result). Then commits the
    /// Match account to Solana (still delegated).
    pub fn complete_round(ctx: Context<CompleteRound>, round: u8, seq: u32) -> Result<()> {
        {
            let m = &mut ctx.accounts.match_account;
            m.check_seq(seq)?;
            m.require_active_round(round)?;
            require!(
                m.count_slots(SLOT_ALIVE) == 0,
                FallRushError::RoundNotResolved
            );
            let qualified = m.count_slots(SLOT_QUALIFIED);
            require!(qualified >= 1, FallRushError::RoundNotResolved);
            require!(
                qualified <= m.target(round)?,
                FallRushError::TargetExceeded
            );
            for i in 0..m.player_count as usize {
                if m.slots[i].state == SLOT_QUALIFIED {
                    m.slots[i].state = SLOT_ALIVE;
                }
            }
            m.status = MatchStatus::RoundResolved;
            m.append(TAG_COMPLETE_ROUND, round, NO_SLOT, &[qualified])?;
            emit!(RoundCompleted {
                match_id: m.match_id,
                round,
                qualified,
                seq
            });
        }
        ctx.accounts.match_account.exit(&crate::ID)?;
        magic::commit(&ctx.accounts.authority.to_account_info(), &ctx.accounts.magic_context.to_account_info(), &ctx.accounts.magic_program.to_account_info(), &[ctx.accounts.match_account.to_account_info()], false)?;
        Ok(())
    }

    /// ER. `RoundResolved` with exactly one remaining (alive) slot → champion, `Completed`.
    pub fn complete_match(ctx: Context<GameplayEvent>, seq: u32) -> Result<()> {
        let m = &mut ctx.accounts.match_account;
        m.check_seq(seq)?;
        require!(
            m.status == MatchStatus::RoundResolved,
            FallRushError::InvalidStatus
        );
        require!(m.count_slots(SLOT_ALIVE) == 1, FallRushError::InvalidChampion);
        let champion = m.slots[..m.player_count as usize]
            .iter()
            .position(|s| s.state == SLOT_ALIVE)
            .ok_or(FallRushError::InvalidChampion)? as u8;
        m.champion_slot = champion;
        m.status = MatchStatus::Completed;
        m.completed_at = Clock::get()?.unix_timestamp;
        let round = m.current_round;
        m.append(TAG_COMPLETE_MATCH, round, champion, &[])?;
        emit!(MatchCompleted {
            match_id: m.match_id,
            champion_slot: champion,
            transcript_hash: m.transcript_hash,
        });
        Ok(())
    }

    // ================================================================== P2 on-chain simulation (sim.rs)

    /// Solana. Arena + World + EventLog PDAs for arena `id` (reused across matches). `parts` = parts per tick.
    pub fn init_arena(ctx: Context<InitArena>, id: u8, parts: u8) -> Result<()> {
        let mut a = ctx.accounts.arena.load_init()?;
        a.authority = ctx.accounts.authority.key();
        a.id = id;
        a.parts = parts.clamp(1, sim::N as u8);
        a.status = sim::arena_status::IDLE;
        a.bump = ctx.bumps.arena;
        let mut w = ctx.accounts.world.load_init()?;
        w.arena = ctx.accounts.arena.key();
        w.parts = a.parts;
        w.results_round = 255;
        let mut l = ctx.accounts.event_log.load_init()?;
        l.arena = ctx.accounts.arena.key();
        Ok(())
    }

    /// Solana. The InputBuf of `slot` (owner set when a player joins).
    pub fn init_input(ctx: Context<InitInput>, id: u8, slot: u8) -> Result<()> {
        let _ = id;
        require!((slot as usize) < sim::N, FallRushError::InvalidSlot);
        let mut ib = ctx.accounts.input.load_init()?;
        ib.arena = ctx.accounts.arena.key();
        ib.slot = slot;
        let mut a = ctx.accounts.arena.load_mut()?;
        a.inputs[slot as usize] = ctx.accounts.input.key();
        Ok(())
    }

    /// Solana. Arena settings: map blob accounts (deal map ids 0..3, lobby) and parts per tick.
    pub fn set_arena(ctx: Context<SetArena>, maps: [Pubkey; sim::MAP_SLOTS], parts: u8, slot_ms: u8) -> Result<()> {
        let mut a = ctx.accounts.arena.load_mut()?;
        a.maps = maps;
        a.parts = parts.clamp(1, sim::N as u8);
        a.slot_ms = slot_ms.max(1);
        Ok(())
    }

    /// Solana. Writes a chunk of a map blob into a program-owned account the client created
    /// (`MAP_HDR + len` bytes). The first call claims it for the game authority.
    pub fn upload_map_chunk(ctx: Context<MapWrite>, map_id: u8, offset: u32, total_len: u32, data: Vec<u8>) -> Result<()> {
        let info = ctx.accounts.map.to_account_info();
        keq!(*info.owner, crate::ID, FallRushError::Unauthorized);
        let mut d = info.try_borrow_mut_data()?;
        require!(d.len() >= sim::MAP_HDR + total_len as usize, FallRushError::BadMap);
        if d[0..8] != sim::MAP_MAGIC {
            require!(d[0..8].iter().all(|b| *b == 0), FallRushError::Unauthorized);
            d[0..8].copy_from_slice(&sim::MAP_MAGIC);
            d[8..40].copy_from_slice(ctx.accounts.authority.key().as_ref());
        }
        require!(d[8..40] == *ctx.accounts.authority.key().as_ref(), FallRushError::Unauthorized);
        require!(d[44] == 0, FallRushError::BadMap); // sealed blobs are immutable
        d[40..44].copy_from_slice(&total_len.to_le_bytes());
        d[45] = map_id;
        let o = sim::MAP_HDR + offset as usize;
        require!(data.len() <= sim::MAP_CHUNK && o + data.len() <= sim::MAP_HDR + total_len as usize, FallRushError::BadMap);
        d[o..o + data.len()].copy_from_slice(&data);
        Ok(())
    }

    /// Solana. Seals a blob: sha256 must equal `maps/map_blob_hashes_v1.txt` (passed in, checked here) and the blob
    /// must parse.
    pub fn seal_map(ctx: Context<MapWrite>, sha256: [u8; 32]) -> Result<()> {
        let info = ctx.accounts.map.to_account_info();
        keq!(*info.owner, crate::ID, FallRushError::Unauthorized);
        let mut d = info.try_borrow_mut_data()?;
        require!(d.len() >= sim::MAP_HDR && d[0..8] == sim::MAP_MAGIC && d[8..40] == *ctx.accounts.authority.key().as_ref(), FallRushError::BadMap);
        let len = u32::from_le_bytes([d[40], d[41], d[42], d[43]]) as usize;
        let body = &d[sim::MAP_HDR..sim::MAP_HDR + len];
        let got = solana_sha256_hasher::hashv(&[body]).to_bytes();
        if got != sha256 {
            msg!("seal_map: sha256 {:?} len {} != expected", &got[..8], len);
        }
        require!(got == sha256, FallRushError::BadMap);
        require!(rush_core::mapblob::MapView::new(body).is_ok(), FallRushError::BadMap);
        d[46..48].copy_from_slice(&[0, 0]);
        d[48..80].copy_from_slice(&sha256);
        d[44] = 1;
        Ok(())
    }

    /// Solana. Delegates one sim PDA to the ER validator (remaining_accounts[0], optional).
    /// kind 0 arena · 1 world · 2 event log · 3 input `slot` · 4 market `slot` · 5 drop queue · 6 chips (P4) · 7 sealed
    /// escrow · 8 sealed book (P6).
    pub fn delegate_sim(ctx: Context<DelegateSim>, kind: u8, id: u8, slot: u8) -> Result<()> {
        let validator = ctx.remaining_accounts.first().map(|a| a.key());
        // P4: kind 6 = a player's `["chips", wallet]` (any payer: guests delegate their own); the sim / economy
        // accounts only by the game authority
        if kind == 6 {
            let wallet = {
                let d = ctx.accounts.pda.try_borrow_data()?;
                require!(d.len() >= 40 && d[..8] == *econ::ChipBalance::DISCRIMINATOR, FallRushError::Unauthorized);
                Pubkey::new_from_array(d[8..40].try_into().unwrap())
            };
            let cfg = DelegateConfig { validator, ..Default::default() };
            ctx.accounts.delegate_pda(&ctx.accounts.authority, &[econ::CHIPS_SEED, wallet.as_ref()], cfg)?;
            return Ok(());
        }
        keq!(ctx.accounts.authority.key(), ctx.accounts.config.game_authority, FallRushError::Unauthorized);
        let idb = [id];
        let sb = [slot];
        let arena = Pubkey::find_program_address(&[sim::ARENA_SEED, &idb], &crate::ID).0;
        let cfg = DelegateConfig { validator, ..Default::default() };
        match kind {
            0 => ctx.accounts.delegate_pda(&ctx.accounts.authority, &[sim::ARENA_SEED, &idb], cfg)?,
            1 => ctx.accounts.delegate_pda(&ctx.accounts.authority, &[sim::WORLD_SEED, arena.as_ref()], cfg)?,
            2 => ctx.accounts.delegate_pda(&ctx.accounts.authority, &[sim::EVLOG_SEED, arena.as_ref()], cfg)?,
            3 => ctx.accounts.delegate_pda(&ctx.accounts.authority, &[sim::INPUT_SEED, arena.as_ref(), &sb], cfg)?,
            // P4: market k = `slot` (0 Champion, 1 FinalWinner, 2..4 Qualify 1..3), the arena's drop queue
            4 => ctx.accounts.delegate_pda(&ctx.accounts.authority, &[econ::MARKET_SEED, &idb, &sb], cfg)?,
            5 => ctx.accounts.delegate_pda(&ctx.accounts.authority, &[econ::DROPQ_SEED, arena.as_ref()], cfg)?,
            // P6: the arena's sealed escrow (devnet-as) / sealed book (the TEE validator, passed as remaining_accounts[0])
            7 => ctx.accounts.delegate_pda(&ctx.accounts.authority, &[p6::ESCROW_SEED, arena.as_ref()], cfg)?,
            8 => ctx.accounts.delegate_pda(&ctx.accounts.authority, &[p6::BOOK_SEED, arena.as_ref()], cfg)?,
            _ => return err!(FallRushError::InvalidSlot),
        }
        Ok(())
    }

    /// ER. Opens the lobby for the next match: roster cleared, the arena points at `match_account`.
    /// remaining_accounts: the 12 InputBufs (owners cleared).
    pub fn open_lobby<'info>(ctx: Context<'info, ArenaAuth<'info>>, match_account: Pubkey) -> Result<()> {
        let mut a = ctx.accounts.arena.load_mut()?;
        keq!(a.authority, ctx.accounts.authority.key(), FallRushError::Unauthorized);
        require!(a.status != sim::arena_status::RUNNING, FallRushError::BadArenaState);
        a.match_account = match_account;
        a.owners = [Pubkey::default(); sim::N];
        a.wallets = [Pubkey::default(); sim::N];
        a.human_mask = 0;
        a.cosmetic = [0; sim::N];
        a.status = sim::arena_status::LOBBY;
        for info in ctx.remaining_accounts.iter() {
            require!(a.inputs.contains(&info.key()), FallRushError::InvalidSlot);
            let ib = AccountLoader::<sim::InputBuf>::try_from(info)?;
            let mut ib = ib.load_mut()?;
            ib.owner = Pubkey::default();
            ib.token = Pubkey::default();
            ib.ring = [sim::InEntry { tick: 0, input: [0; 4] }; sim::RING];
            ib.latest = 0;
        }
        Ok(())
    }

    /// ER. A player takes `slot`: arena in Lobby, slot free, one slot per wallet. The signer is either the wallet
    /// itself or a session signer with a valid `SessionTokenV2` (authority = `wallet`, target = this program, not
    /// expired; seeds checked). The signer becomes the slot's InputBuf writer, the wallet its roster identity.
    pub fn join_lobby(ctx: Context<JoinLobby>, slot: u8, cosmetic: u8, wallet: Pubkey) -> Result<()> {
        let me = ctx.accounts.player.key();
        let token_key = match &ctx.accounts.session_token {
            Some(t) => {
                sim_session_check(t, &me, &wallet)?;
                t.key()
            }
            None => {
                keq!(me, wallet, FallRushError::Unauthorized);
                Pubkey::default()
            }
        };
        let mut a = ctx.accounts.arena.load_mut()?;
        require!(a.status == sim::arena_status::LOBBY, FallRushError::BadArenaState);
        let s = slot as usize;
        require!(s < sim::N, FallRushError::InvalidSlot);
        require!(a.human_mask >> s & 1 == 0, FallRushError::SlotTaken);
        require!(!a.wallets.contains(&wallet) && !a.owners.contains(&me), FallRushError::AlreadyJoined);
        keq!(a.inputs[s], ctx.accounts.input.key(), FallRushError::InvalidSlot);
        a.owners[s] = me;
        a.wallets[s] = wallet;
        a.human_mask |= 1 << s;
        a.cosmetic[s] = cosmetic;
        let mut ib = ctx.accounts.input.load_mut()?;
        ib.owner = me;
        ib.token = token_key;
        Ok(())
    }

    /// ER. The Final on-chain after Classic rounds 1–3 (doc 15 OC-11): game authority; the `Match` after round 3 with
    /// exactly `slots` alive. Then the keeper schedules ticks.
    pub fn start_final(ctx: Context<StartSim>, slots: [u8; 3]) -> Result<()> {
        let mut a = ctx.accounts.arena.load_mut()?;
        keq!(a.authority, ctx.accounts.authority.key(), FallRushError::Unauthorized);
        require!(a.status == sim::arena_status::LOBBY, FallRushError::BadArenaState);
        keq!(a.match_account, ctx.accounts.match_account.key(), FallRushError::BadArenaState);
        keq!(a.maps[sim::MAP_LOBBY_SLOT], ctx.accounts.lobby_map.key(), FallRushError::BadMap);
        let info = ctx.accounts.lobby_map.to_account_info();
        let d = info.try_borrow_data()?;
        let lobby = sim::map_bytes(&info, &d).ok_or(error!(FallRushError::BadMap))?;
        let mut w = ctx.accounts.world.load_mut()?;
        keq!(w.arena, ctx.accounts.arena.key(), FallRushError::BadArenaState);
        let m = &mut ctx.accounts.match_account;
        require!(m.flags & sim::MATCH_SIM_STARTED == 0, FallRushError::InvalidStatus);
        sim::start_final(&mut a, &mut w, m, lobby, &slots)
    }

    /// ER. Roster lock + start: free slots become bots, the director starts from the match's VRF seed.
    pub fn start_sim(ctx: Context<StartSim>) -> Result<()> {
        let mut a = ctx.accounts.arena.load_mut()?;
        keq!(a.authority, ctx.accounts.authority.key(), FallRushError::Unauthorized);
        require!(a.status == sim::arena_status::LOBBY, FallRushError::BadArenaState);
        keq!(a.match_account, ctx.accounts.match_account.key(), FallRushError::BadArenaState);
        keq!(a.maps[sim::MAP_LOBBY_SLOT], ctx.accounts.lobby_map.key(), FallRushError::BadMap);
        let info = ctx.accounts.lobby_map.to_account_info();
        let d = info.try_borrow_data()?;
        let lobby = sim::map_bytes(&info, &d).ok_or(error!(FallRushError::BadMap))?;
        let mut w = ctx.accounts.world.load_mut()?;
        keq!(w.arena, ctx.accounts.arena.key(), FallRushError::BadArenaState);
        // a Match runs once: an arena can never re-enter an old match
        let m = &mut ctx.accounts.match_account;
        require!(m.flags & sim::MATCH_SIM_STARTED == 0, FallRushError::InvalidStatus);
        m.flags |= sim::MATCH_SIM_STARTED;
        sim::start_sim(&mut a, &mut w, m, lobby)
    }

    /// ER. The hot tick (see `sim::tick`). Accounts: arena, world, event log, match, 5 maps, 12 InputBufs — all in
    /// remaining_accounts, checked by hand; never fails.
    pub fn tick<'info>(ctx: Context<'info, Tick>) -> Result<()> {
        sim::tick(ctx.remaining_accounts)
    }

    /// ER. 3 inputs for ticks `start .. start + 3`, signed by the slot's owner.
    pub fn submit_input(ctx: Context<SubmitInput>, slot: u8, start: u32, batch: [u8; 12]) -> Result<()> {
        let w = ctx.accounts.world.load()?;
        let mut ib = ctx.accounts.input.load_mut()?;
        require!(ib.slot == slot && ib.arena == w.arena, FallRushError::InvalidSlot);
        // a session signer's token must still be there (not revoked) and not expired
        if ib.token != Pubkey::default() {
            let t = ctx.accounts.session_token.as_ref().ok_or(error!(FallRushError::SessionInvalid))?;
            keq!(t.key(), ib.token, FallRushError::SessionInvalid);
            require!(!t.is_expired()?, FallRushError::SessionInvalid);
        }
        sim::submit_input(&mut ib, &ctx.accounts.owner.key(), w.tick, w.lead(), start, &batch)
    }

    /// ER. The arena's forward input window (P4 0.4): `lead` ticks (8 ..= 13; 0 = the default 8). Authority only.
    pub fn set_input_lead(ctx: Context<ArenaWorldAuth>, lead: u8) -> Result<()> {
        let a = ctx.accounts.arena.load()?;
        keq!(a.authority, ctx.accounts.authority.key(), FallRushError::Unauthorized);
        let mut w = ctx.accounts.world.load_mut()?;
        keq!(w.arena, ctx.accounts.arena.key(), FallRushError::BadArenaState);
        require!(lead == 0 || (sim::INPUT_WINDOW..=sim::INPUT_LEAD_MAX).contains(&(lead as u32)), FallRushError::InputOutOfWindow);
        w.input_lead = lead;
        Ok(())
    }

    /// ER. Schedules the timed task driving `tick()` (any payer: the keeper is permissionless). The task id comes
    /// from the arena's next nonce. remaining_accounts = the tick account list.
    pub fn schedule_ticks<'info>(ctx: Context<'info, ScheduleTicks<'info>>, interval_ms: i64, iterations: i64) -> Result<()> {
        let world = ctx.remaining_accounts.get(1).ok_or(error!(FallRushError::BadTask))?.key();
        let tid = {
            let mut a = ctx.accounts.arena.load_mut()?;
            a.nonce = a.nonce.wrapping_add(1);
            a.task_id = sim::task_id(&world, a.nonce);
            a.task_id
        };
        sim::schedule(&ctx.accounts.payer.to_account_info(), &ctx.accounts.magic_program.to_account_info(), ctx.remaining_accounts, tid, interval_ms, iterations)
    }

    /// ER. Cancels a task. remaining_accounts[0] = the task context account (MagicContext).
    pub fn cancel_ticks<'info>(ctx: Context<'info, CancelTicks<'info>>, task_id: i64) -> Result<()> {
        let tc = ctx.remaining_accounts.first().ok_or(error!(FallRushError::BadTask))?;
        sim::cancel(&ctx.accounts.payer.to_account_info(), &ctx.accounts.magic_program.to_account_info(), tc, task_id)
    }

    /// ER. Commits `Match` once per resolved round (any payer). Bits 1..=4 of `Match::flags` mark committed rounds,
    /// so at most 4 round commits + the finalize commit happen per match (under the 10-commit limit).
    pub fn commit_round(ctx: Context<CommitRound>, round: u8) -> Result<()> {
        {
            let m = &mut ctx.accounts.match_account;
            require!(round >= 1 && round as usize <= ROUNDS && m.current_round >= round, FallRushError::NothingToCommit);
            require!(
                matches!(m.status, MatchStatus::RoundResolved | MatchStatus::Completed) || m.current_round > round,
                FallRushError::NothingToCommit
            );
            let bit = 1u8 << round;
            require!(m.flags & bit == 0, FallRushError::NothingToCommit);
            m.flags |= bit;
        }
        ctx.accounts.match_account.exit(&crate::ID)?;
        magic::commit(&ctx.accounts.payer.to_account_info(), &ctx.accounts.magic_context.to_account_info(), &ctx.accounts.magic_program.to_account_info(), &[ctx.accounts.match_account.to_account_info()], false)?;
        Ok(())
    }

    /// ER. The director reached DONE: the arena is free for the next match (the keeper then runs finalize_match).
    pub fn end_sim(ctx: Context<ArenaAuth>) -> Result<()> {
        let mut a = ctx.accounts.arena.load_mut()?;
        keq!(a.authority, ctx.accounts.authority.key(), FallRushError::Unauthorized);
        let w = ctx.accounts.world.load()?;
        require!(w.phase >= rush_core::rules::director::phase::DONE || a.status != sim::arena_status::RUNNING, FallRushError::BadArenaState);
        a.status = sim::arena_status::DONE;
        Ok(())
    }

    /// ER. Stops the arena (authority): no more ticks run; the keeper cancels the task and aborts the match.
    pub fn abort_arena(ctx: Context<ArenaAuth>) -> Result<()> {
        let mut a = ctx.accounts.arena.load_mut()?;
        keq!(a.authority, ctx.accounts.authority.key(), FallRushError::Unauthorized);
        a.status = sim::arena_status::ABORTED;
        Ok(())
    }

    /// ER + commit_and_undelegate. Only from `Completed` / `Aborted` (doc 06; P2 0.4 removed the M0
    /// `RoundResolved` shortcut: the on-chain director decides when a match is over).
    pub fn finalize_match(ctx: Context<FinalizeMatch>) -> Result<()> {
        let status = ctx.accounts.match_account.status;
        require!(
            matches!(status, MatchStatus::Completed | MatchStatus::Aborted),
            FallRushError::InvalidStatus
        );
        // P4: an on-chain Final commits with its receipt (markets settled, drop log, VRF capsule seed requested)
        let m = &ctx.accounts.match_account;
        // P5 0.5: also any match whose markets were opened (Classic fallback included), so a keeper crash between
        // settle and finalize can never strand unpaid bets (the receipt requires every market PAID)
        if (m.flags & sim::MATCH_FINAL_MODE != 0 || m.flags & sim::MATCH_ECON != 0) && status == MatchStatus::Completed {
            require!(m.receipt.flags & econ::rflag::WRITTEN != 0, FallRushError::ReceiptNotReady);
        }
        ctx.accounts.match_account.exit(&crate::ID)?;
        magic::commit(&ctx.accounts.authority.to_account_info(), &ctx.accounts.magic_context.to_account_info(), &ctx.accounts.magic_program.to_account_info(), &[ctx.accounts.match_account.to_account_info()], true)?;
        Ok(())
    }

    /// P5 0.3 (doc 15 D-ACTION): `finalize_match` + a **Magic Action**: the receipt commit + undelegation carries one
    /// post-undelegate base action that runs `mint_rewards_action` on Solana right after the Match is back, minting
    /// the Crown + capsules owed by the committed receipt (≤ `econ::ACTION_MAX_ITEMS`; the keeper's permissionless
    /// `mint_rewards` stays the retry path). The program PDA `["escrow_auth"]` signs the CPI as the escrow authority.
    pub fn finalize_match_action(ctx: Context<FinalizeMatchAction>) -> Result<()> {
        let status = ctx.accounts.match_account.status;
        require!(matches!(status, MatchStatus::Completed | MatchStatus::Aborted), FallRushError::InvalidStatus);
        let m = &ctx.accounts.match_account;
        if (m.flags & sim::MATCH_FINAL_MODE != 0 || m.flags & sim::MATCH_ECON != 0) && status == MatchStatus::Completed {
            require!(m.receipt.flags & econ::rflag::WRITTEN != 0, FallRushError::ReceiptNotReady);
        }
        let mk = m.key();
        let (f, h) = (m.receipt.flags, m.receipt.human_mask);
        let mut whats: Vec<u8> = Vec::with_capacity(econ::ACTION_MAX_ITEMS);
        if status == MatchStatus::Completed && f & econ::rflag::WRITTEN != 0 {
            if f & (econ::rflag::CROWN | econ::rflag::NO_CROWN) == 0 && (m.champion_slot as usize) < 12 && h >> m.champion_slot & 1 == 1 {
                whats.push(0);
            }
            let mut s = 0u8;
            while s < 12 && whats.len() < econ::ACTION_MAX_ITEMS {
                if f & econ::rflag::SEEDED != 0 && h >> s & 1 == 1 && m.receipt.minted_mask >> s & 1 == 0 {
                    whats.push(1 + s);
                }
                s += 1;
            }
        }
        let (tre, assets) = (econ::pda(&[econ::TREASURY_SEED]).0, econ::pda(&[econ::ASSETS_SEED]).0);
        let mut metas: Vec<(Pubkey, bool)> = Vec::with_capacity(7 * whats.len());
        for &w in &whats {
            let slot = if w == 0 { m.champion_slot } else { w - 1 };
            let asset = if w == 0 { econ::pda(&[econ::CROWN_SEED, mk.as_ref()]).0 } else { econ::pda(&[econ::CAPSULE_SEED, mk.as_ref(), &[slot]]).0 };
            metas.extend_from_slice(&[(mk, true), (asset, true), (m.receipt.wallets[slot as usize], false), (tre, true), (assets, false), (econ::MPL_CORE, false), (anchor_lang::solana_program::system_program::ID, false)]);
        }
        ctx.accounts.match_account.exit(&crate::ID)?;
        let (payer, mc, mp, ma) = (ctx.accounts.authority.to_account_info(), ctx.accounts.magic_context.to_account_info(), ctx.accounts.magic_program.to_account_info(), ctx.accounts.match_account.to_account_info());
        if whats.is_empty() {
            return magic::commit(&payer, &mc, &mp, &[ma], true);
        }
        let mut data = instruction::MintRewardsAction::DISCRIMINATOR.to_vec();
        data.extend_from_slice(&(whats.len() as u32).to_le_bytes());
        data.extend_from_slice(&whats);
        let cu = 40_000 + 110_000 * whats.len() as u32;
        let bump = [ctx.bumps.escrow_auth];
        let seeds: [&[u8]; 2] = [econ::ESCROW_AUTH_SEED, &bump];
        magic::commit_undelegate_with_action(&payer, &mc, &mp, &[ma], &ctx.accounts.escrow_auth.to_account_info(), &seeds, econ::ESCROW_INDEX, cu, &metas, &data)
    }

    /// P5 0.3: the Magic Action's destination on Solana (accounts: 7 per item + the injected source program, escrow
    /// authority and escrow signer; checked in `econ::mint_rewards_action`). Never fails on an already-minted item.
    pub fn mint_rewards_action<'info>(ctx: Context<'info, MintRewardsAction>, whats: Vec<u8>) -> Result<()> {
        econ::mint_rewards_action(ctx.remaining_accounts, &whats)
    }

    // ================================================================== P4 economy (econ.rs)

    /// P4 economy, one entry point (program size, doc 15 OC-12 / proof §2): `op` (`econ::op`) with two numeric args;
    /// every account is in remaining_accounts and checked by hand (layouts in `econ::dispatch`). Settlement, payouts
    /// and minting are separate ops, never inside `tick()`.
    pub fn econ<'info>(ctx: Context<'info, Econ<'info>>, op: u8, a: u64, b: u64) -> Result<()> {
        econ::dispatch(&ctx.accounts.signer, ctx.remaining_accounts, op, a, b)
    }

    /// P6 (sealed bets, seasons, achievements; `p6.rs`): one entry point like `econ`, plus 32 bytes of data (a
    /// commitment, a salt or a match key). Every account in remaining_accounts, checked by hand (`p6::op`).
    pub fn p6<'info>(ctx: Context<'info, Econ<'info>>, op: u8, a: u64, b: u64, data: [u8; 32]) -> Result<()> {
        p6::dispatch(&ctx.accounts.signer, ctx.remaining_accounts, op, a, b, data)
    }

    /// ER, game authority: the second VRF draw at match end (capsule rarity), once the receipt is written.
    pub fn request_capsules(ctx: Context<RequestDeal>) -> Result<()> {
        let (match_key, caller_seed) = {
            let m = &mut ctx.accounts.match_account;
            require!(m.receipt.flags & econ::rflag::WRITTEN != 0 && m.status == MatchStatus::Completed, FallRushError::ReceiptNotReady);
            require!(m.receipt.flags & econ::rflag::SEEDED == 0, FallRushError::InvalidVrfCallback);
            m.receipt.flags |= econ::rflag::REQUESTED;
            (m.key(), solana_sha256_hasher::hashv(&[b"fallrush-capsules", &m.match_id, &m.vrf_seed]).to_bytes())
        };
        let ix = create_request_randomness_ix_const(RequestRandomnessParams {
            payer: ctx.accounts.authority.key(),
            oracle_queue: ctx.accounts.oracle_queue.key(),
            callback_program_id: crate::ID,
            callback_discriminator: instruction::CapsuleCallback::DISCRIMINATOR.to_vec(),
            caller_seed,
            accounts_metas: Some(vec![SerializableAccountMeta { pubkey: match_key, is_signer: false, is_writable: true }]),
            ..Default::default()
        }, PROGRAM_IDENTITY);
        ctx.accounts.invoke_signed_vrf(&ctx.accounts.authority.to_account_info(), &ix)?;
        Ok(())
    }

    /// ER. VRF callback (the deal's const identity check, PB0): the capsule seed and every human's rarity.
    pub fn capsule_callback(ctx: Context<DealCallback>, randomness: [u8; 32]) -> Result<()> {
        let m = &mut ctx.accounts.match_account;
        require!(m.receipt.flags & econ::rflag::REQUESTED != 0 && m.receipt.flags & econ::rflag::SEEDED == 0, FallRushError::InvalidVrfCallback);
        m.receipt.capsule_seed = randomness;
        for s in 0..12u8 {
            m.receipt.rarity[s as usize] = if m.receipt.human_mask >> s & 1 == 1 { econ::rarity_of(&randomness, s) } else { 255 };
        }
        m.receipt.flags |= econ::rflag::SEEDED;
        Ok(())
    }

    /// ER or Solana (wherever the account currently lives). game_authority or admin.
    pub fn abort_match(ctx: Context<AbortMatch>, reason: u8) -> Result<()> {
        let m = &mut ctx.accounts.match_account;
        require!(
            !matches!(m.status, MatchStatus::Completed | MatchStatus::Aborted),
            FallRushError::AlreadyFinal
        );
        let seq = m.event_seq;
        m.status = MatchStatus::Aborted;
        let round = m.current_round;
        m.append(TAG_ABORT, round, NO_SLOT, &[reason])?;
        emit!(MatchAborted {
            match_id: m.match_id,
            reason,
            seq
        });
        Ok(())
    }
}

#[derive(Accounts)]
pub struct InitializeConfig<'info> {
    #[account(mut)]
    pub admin: Signer<'info>,
    #[account(init, payer = admin, space = 8 + GameConfig::INIT_SPACE, seeds = [CONFIG_SEED], bump)]
    pub config: Account<'info, GameConfig>,
    /// Binds `program_data` to this program (pattern from magicblock-examples rewards-delegated-vrf). P4: parsed by
    /// hand (`upgrade_authority_of`), so the program links no bincode deserializer (program size).
    /// CHECK: this program's account (`address = crate::ID`); its ProgramData address is read in the handler
    #[account(address = crate::ID)]
    pub program: UncheckedAccount<'info>,
    /// CHECK: owned by the upgradeable loader; layout checked in the handler
    pub program_data: UncheckedAccount<'info>,
    pub system_program: Program<'info, System>,
}

/// The upgrade authority of this program from the loader accounts (bpf_loader_upgradeable layouts: Program =
/// u32 2 ‖ programdata address; ProgramData = u32 3 ‖ u64 slot ‖ Option<Pubkey>).
fn upgrade_authority_of(program: &AccountInfo, program_data: &AccountInfo) -> Option<Pubkey> {
    let loader = anchor_lang::solana_program::bpf_loader_upgradeable::ID;
    if program.owner != &loader || program_data.owner != &loader {
        return None;
    }
    let p = program.try_borrow_data().ok()?;
    if p.len() < 36 || p[..4] != [2, 0, 0, 0] || p[4..36] != *program_data.key.as_ref() {
        return None;
    }
    let d = program_data.try_borrow_data().ok()?;
    if d.len() < 45 || d[..4] != [3, 0, 0, 0] || d[12] != 1 {
        return None;
    }
    Some(Pubkey::new_from_array(d[13..45].try_into().ok()?))
}

#[derive(Accounts)]
#[instruction(match_id: [u8; 16])]
pub struct CreateMatch<'info> {
    #[account(mut, address = config.game_authority @ FallRushError::Unauthorized)]
    pub authority: Signer<'info>,
    #[account(mut, seeds = [CONFIG_SEED], bump = config.bump)]
    pub config: Account<'info, GameConfig>,
    #[account(init, payer = authority, space = 8 + Match::INIT_SPACE, seeds = [MATCH_SEED, &match_id], bump)]
    pub match_account: Box<Account<'info, Match>>,
    pub system_program: Program<'info, System>,
}

// NOTE: SDK ≤ 0.16 stripped every "del" substring from the `#[account(...)]` attribute of the
// field marked `del`; 0.18 matches the marker as a whole constraint. The `del` field must stay an
// `UncheckedAccount` (0.18 compile check, SIMD-0460).
#[delegate]
#[derive(Accounts)]
#[instruction(match_id: [u8; 16])]
pub struct DelegateMatch<'info> {
    #[account(mut, address = config.game_authority @ FallRushError::Unauthorized)]
    pub authority: Signer<'info>,
    #[account(seeds = [CONFIG_SEED], bump = config.bump)]
    pub config: Account<'info, GameConfig>,
    /// CHECK: Match PDA; seeds checked here, owner + status checked in the handler.
    #[account(mut, del, seeds = [MATCH_SEED, &match_id], bump)]
    pub match_account: UncheckedAccount<'info>,
}

#[vrf]
#[derive(Accounts)]
pub struct RequestDeal<'info> {
    #[account(mut, address = config.game_authority @ FallRushError::Unauthorized)]
    pub authority: Signer<'info>,
    #[account(seeds = [CONFIG_SEED], bump = config.bump)]
    pub config: Account<'info, GameConfig>,
    #[account(mut, seeds = [MATCH_SEED, &match_account.match_id], bump = match_account.bump)]
    pub match_account: Box<Account<'info, Match>>,
    /// CHECK: pinned to the SDK's ephemeral queue constant (test queue under `localnet`).
    #[account(mut, address = VRF_QUEUE)]
    pub oracle_queue: UncheckedAccount<'info>,
}

#[vrf_callback]
#[derive(Accounts)]
pub struct DealCallback<'info> {
    /// Same field (name, position, check) `#[vrf_callback]` would inject; a const address keeps
    /// the PDA derivation at compile time.
    #[account(address = VRF_CALLBACK_IDENTITY)]
    pub vrf_program_identity: Signer<'info>,
    #[account(mut, seeds = [MATCH_SEED, &match_account.match_id], bump = match_account.bump)]
    pub match_account: Box<Account<'info, Match>>,
}

#[derive(Accounts)]
pub struct GameplayEvent<'info> {
    #[account(address = config.game_authority @ FallRushError::Unauthorized)]
    pub authority: Signer<'info>,
    #[account(seeds = [CONFIG_SEED], bump = config.bump)]
    pub config: Account<'info, GameConfig>,
    #[account(mut, seeds = [MATCH_SEED, &match_account.match_id], bump = match_account.bump)]
    pub match_account: Box<Account<'info, Match>>,
}

#[commit]
#[derive(Accounts)]
pub struct CompleteRound<'info> {
    #[account(mut, address = config.game_authority @ FallRushError::Unauthorized)]
    pub authority: Signer<'info>,
    #[account(seeds = [CONFIG_SEED], bump = config.bump)]
    pub config: Account<'info, GameConfig>,
    #[account(mut, seeds = [MATCH_SEED, &match_account.match_id], bump = match_account.bump)]
    pub match_account: Box<Account<'info, Match>>,
}

#[commit]
#[derive(Accounts)]
pub struct FinalizeMatch<'info> {
    #[account(mut, address = config.game_authority @ FallRushError::Unauthorized)]
    pub authority: Signer<'info>,
    #[account(seeds = [CONFIG_SEED], bump = config.bump)]
    pub config: Account<'info, GameConfig>,
    #[account(mut, seeds = [MATCH_SEED, &match_account.match_id], bump = match_account.bump)]
    pub match_account: Box<Account<'info, Match>>,
}

#[commit]
#[derive(Accounts)]
pub struct FinalizeMatchAction<'info> {
    #[account(mut, address = config.game_authority @ FallRushError::Unauthorized)]
    pub authority: Signer<'info>,
    #[account(seeds = [CONFIG_SEED], bump = config.bump)]
    pub config: Account<'info, GameConfig>,
    #[account(mut, seeds = [MATCH_SEED, &match_account.match_id], bump = match_account.bump)]
    pub match_account: Box<Account<'info, Match>>,
    /// CHECK: the escrow authority PDA (no data); signs the Magic program CPI via invoke_signed.
    #[account(seeds = [econ::ESCROW_AUTH_SEED], bump)]
    pub escrow_auth: UncheckedAccount<'info>,
}

/// P5 0.3: every account is in remaining_accounts (the action's metas + the injected escrow accounts).
#[derive(Accounts)]
pub struct MintRewardsAction {}

#[derive(Accounts)]
pub struct AbortMatch<'info> {
    #[account(
        constraint = authority.key() == config.game_authority || authority.key() == config.admin
            @ FallRushError::Unauthorized
    )]
    pub authority: Signer<'info>,
    #[account(seeds = [CONFIG_SEED], bump = config.bump)]
    pub config: Account<'info, GameConfig>,
    #[account(mut, seeds = [MATCH_SEED, &match_account.match_id], bump = match_account.bump)]
    pub match_account: Box<Account<'info, Match>>,
}


// ---------------------------------------------------------------- P2 sim accounts

#[derive(Accounts)]
#[instruction(id: u8)]
pub struct InitArena<'info> {
    #[account(mut, address = config.game_authority @ FallRushError::Unauthorized)]
    pub authority: Signer<'info>,
    #[account(seeds = [CONFIG_SEED], bump = config.bump)]
    pub config: Account<'info, GameConfig>,
    #[account(init, payer = authority, space = 8 + core::mem::size_of::<sim::Arena>(), seeds = [sim::ARENA_SEED, &[id]], bump)]
    pub arena: AccountLoader<'info, sim::Arena>,
    #[account(init, payer = authority, space = 8 + core::mem::size_of::<sim::WorldAcc>(), seeds = [sim::WORLD_SEED, arena.key().as_ref()], bump)]
    pub world: AccountLoader<'info, sim::WorldAcc>,
    #[account(init, payer = authority, space = 8 + core::mem::size_of::<sim::EventLog>(), seeds = [sim::EVLOG_SEED, arena.key().as_ref()], bump)]
    pub event_log: AccountLoader<'info, sim::EventLog>,
    pub system_program: Program<'info, System>,
}

#[derive(Accounts)]
#[instruction(id: u8, slot: u8)]
pub struct InitInput<'info> {
    #[account(mut, address = config.game_authority @ FallRushError::Unauthorized)]
    pub authority: Signer<'info>,
    #[account(seeds = [CONFIG_SEED], bump = config.bump)]
    pub config: Account<'info, GameConfig>,
    #[account(mut, seeds = [sim::ARENA_SEED, &[id]], bump)]
    pub arena: AccountLoader<'info, sim::Arena>,
    #[account(init, payer = authority, space = 8 + core::mem::size_of::<sim::InputBuf>(), seeds = [sim::INPUT_SEED, arena.key().as_ref(), &[slot]], bump)]
    pub input: AccountLoader<'info, sim::InputBuf>,
    pub system_program: Program<'info, System>,
}

#[derive(Accounts)]
pub struct SetArena<'info> {
    #[account(address = config.game_authority @ FallRushError::Unauthorized)]
    pub authority: Signer<'info>,
    #[account(seeds = [CONFIG_SEED], bump = config.bump)]
    pub config: Account<'info, GameConfig>,
    #[account(mut)]
    pub arena: AccountLoader<'info, sim::Arena>,
}

#[derive(Accounts)]
pub struct MapWrite<'info> {
    #[account(address = config.game_authority @ FallRushError::Unauthorized)]
    pub authority: Signer<'info>,
    #[account(seeds = [CONFIG_SEED], bump = config.bump)]
    pub config: Account<'info, GameConfig>,
    /// CHECK: program-owned raw blob account (header checked by hand)
    #[account(mut)]
    pub map: UncheckedAccount<'info>,
}

#[delegate]
#[derive(Accounts)]
pub struct DelegateSim<'info> {
    /// The game authority (kinds 0–5, checked in the handler) or any payer (kind 6, chips).
    #[account(mut)]
    pub authority: Signer<'info>,
    #[account(seeds = [CONFIG_SEED], bump = config.bump)]
    pub config: Account<'info, GameConfig>,
    /// CHECK: one of the sim PDAs; its seeds are rebuilt in the handler and checked by the delegation program
    #[account(mut, del)]
    pub pda: UncheckedAccount<'info>,
}

#[derive(Accounts)]
pub struct ArenaAuth<'info> {
    pub authority: Signer<'info>,
    #[account(mut)]
    pub arena: AccountLoader<'info, sim::Arena>,
    pub world: AccountLoader<'info, sim::WorldAcc>,
}

#[derive(Accounts)]
pub struct ArenaWorldAuth<'info> {
    pub authority: Signer<'info>,
    pub arena: AccountLoader<'info, sim::Arena>,
    #[account(mut)]
    pub world: AccountLoader<'info, sim::WorldAcc>,
}

#[derive(Accounts)]
pub struct JoinLobby<'info> {
    pub player: Signer<'info>,
    #[account(mut)]
    pub arena: AccountLoader<'info, sim::Arena>,
    #[account(mut)]
    pub input: AccountLoader<'info, sim::InputBuf>,
    /// The player's session token when `player` is a session signer (session-keys 3.1.1, `SessionTokenV2`).
    pub session_token: Option<Box<Account<'info, session_keys::SessionTokenV2>>>,
}

/// `SessionTokenV2` checks (session-keys' own `validate` form, without its trait plumbing): this program, this
/// signer, this wallet, the token PDA's seeds, not expired.
pub(crate) fn sim_session_check(t: &Account<session_keys::SessionTokenV2>, signer: &Pubkey, wallet: &Pubkey) -> Result<()> {
    keq!(t.target_program, crate::ID, FallRushError::SessionInvalid);
    keq!(t.session_signer, *signer, FallRushError::SessionInvalid);
    keq!(t.authority, *wallet, FallRushError::SessionInvalid);
    let (pda, _) = Pubkey::find_program_address(
        &[session_keys::SessionTokenV2::SEED_PREFIX.as_bytes(), crate::ID.as_ref(), signer.as_ref(), wallet.as_ref()],
        &session_keys::ID,
    );
    keq!(pda, t.key(), FallRushError::SessionInvalid);
    require!(!t.is_expired()?, FallRushError::SessionInvalid);
    Ok(())
}

#[derive(Accounts)]
pub struct StartSim<'info> {
    pub authority: Signer<'info>,
    #[account(mut)]
    pub arena: AccountLoader<'info, sim::Arena>,
    #[account(mut)]
    pub world: AccountLoader<'info, sim::WorldAcc>,
    #[account(mut)]
    pub match_account: Box<Account<'info, Match>>,
    /// CHECK: the lobby map blob (key checked against the arena, header checked by hand)
    pub lobby_map: UncheckedAccount<'info>,
}

#[derive(Accounts)]
pub struct Tick {}

#[derive(Accounts)]
pub struct SubmitInput<'info> {
    pub owner: Signer<'info>,
    #[account(mut)]
    pub input: AccountLoader<'info, sim::InputBuf>,
    pub world: AccountLoader<'info, sim::WorldAcc>,
    /// Required when the slot was joined with a session key (`InputBuf::token`).
    pub session_token: Option<Box<Account<'info, session_keys::SessionTokenV2>>>,
}

#[derive(Accounts)]
pub struct ScheduleTicks<'info> {
    #[account(mut)]
    pub payer: Signer<'info>,
    #[account(mut)]
    pub arena: AccountLoader<'info, sim::Arena>,
    /// CHECK: MagicBlock magic program
    #[account(address = ephemeral_rollups_sdk::consts::MAGIC_PROGRAM_ID)]
    pub magic_program: UncheckedAccount<'info>,
}

#[derive(Accounts)]
pub struct CancelTicks<'info> {
    #[account(mut)]
    pub payer: Signer<'info>,
    /// CHECK: MagicBlock magic program
    #[account(address = ephemeral_rollups_sdk::consts::MAGIC_PROGRAM_ID)]
    pub magic_program: UncheckedAccount<'info>,
}

#[commit]
#[derive(Accounts)]
pub struct CommitRound<'info> {
    #[account(mut)]
    pub payer: Signer<'info>,
    #[account(mut, seeds = [MATCH_SEED, &match_account.match_id], bump = match_account.bump)]
    pub match_account: Box<Account<'info, Match>>,
}

// ---------------------------------------------------------------- P4 economy accounts

/// `econ`: the signer (wallet, session key, game authority or any payer, per op); the rest in remaining_accounts.
#[derive(Accounts)]
pub struct Econ<'info> {
    #[account(mut)]
    pub signer: Signer<'info>,
}

#[cfg(test)]
mod identity_tests {
    use super::*;

    /// The compile-time VRF PDAs must equal the runtime derivation (SDK 0.18 port, PB0).
    #[test]
    fn const_vrf_identities_match_find_program_address() {
        let (req, _) = Pubkey::find_program_address(&[b"identity"], &crate::ID);
        assert_eq!(PROGRAM_IDENTITY, req);
        let vrf_program = ephemeral_rollups_sdk::vrf::consts::VRF_PROGRAM_ID;
        let (cb, _) = Pubkey::find_program_address(&[b"identity", crate::ID.as_ref()], &vrf_program);
        assert_eq!(VRF_CALLBACK_IDENTITY, cb);
    }
}
