//! Fall Rush: competitive match lifecycle on a MagicBlock Ephemeral Rollup (doc 06).
//! M0 implements a forward-compatible subset; see docs/06-ANCHOR-PROGRAM.md.
//!
//! Trust model: the program checks *who* wrote results, *in what order* and that counts match the
//! committed ruleset. It does not verify physical gameplay (decided by the authoritative server).

use anchor_lang::prelude::*;
use ephemeral_rollups_sdk::anchor::{commit, delegate, ephemeral, vrf, vrf_callback};
use ephemeral_rollups_sdk::cpi::DelegateConfig;
#[allow(unused_imports)]
use ephemeral_rollups_sdk::ephem::{FoldableIntentBuilder, MagicIntentBundleBuilder};
use ephemeral_rollups_sdk::vrf::consts::{program_identity_pubkey, scoped_vrf_identity_const};
use ephemeral_rollups_sdk::vrf::instructions::{
    create_request_randomness_ix_const, RequestRandomnessParams,
};
use ephemeral_rollups_sdk::vrf::types::SerializableAccountMeta;

pub mod deal;
pub mod errors;
pub mod events;
pub mod state;

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
        m.pick_count = 0;
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
            require_keys_eq!(*info.owner, crate::ID, FallRushError::InvalidStatus);
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
        MagicIntentBundleBuilder::new(
            ctx.accounts.authority.to_account_info(),
            ctx.accounts.magic_context.to_account_info(),
            ctx.accounts.magic_program.to_account_info(),
        )
        .commit(&[ctx.accounts.match_account.to_account_info()])
        .build_and_invoke()?;
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

    /// ER + commit_and_undelegate. M0 shortcut: also allowed from `RoundResolved` (doc 06 only
    /// allows `Completed`/`Aborted`); M6 removes the `RoundResolved` case.
    pub fn finalize_match(ctx: Context<FinalizeMatch>) -> Result<()> {
        let status = ctx.accounts.match_account.status;
        require!(
            matches!(
                status,
                MatchStatus::RoundResolved | MatchStatus::Completed | MatchStatus::Aborted
            ),
            FallRushError::InvalidStatus
        );
        ctx.accounts.match_account.exit(&crate::ID)?;
        MagicIntentBundleBuilder::new(
            ctx.accounts.authority.to_account_info(),
            ctx.accounts.magic_context.to_account_info(),
            ctx.accounts.magic_program.to_account_info(),
        )
        .commit_and_undelegate(&[ctx.accounts.match_account.to_account_info()])
        .build_and_invoke()?;
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
    /// Binds `program_data` to this program (pattern from magicblock-examples rewards-delegated-vrf).
    #[account(constraint = program.programdata_address()? == Some(program_data.key()) @ FallRushError::Unauthorized)]
    pub program: Program<'info, crate::program::FallRush>,
    #[account(constraint = program_data.upgrade_authority_address == Some(admin.key()) @ FallRushError::Unauthorized)]
    pub program_data: Account<'info, ProgramData>,
    pub system_program: Program<'info, System>,
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
