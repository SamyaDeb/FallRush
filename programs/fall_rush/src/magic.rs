//! Magic program CPIs with hand-encoded instruction data (P4, program size; doc 15 OC-12 note): the bytes are exactly
//! what ER SDK 0.18's `MagicIntentBundleBuilder` (commit / commit + undelegate, no actions) and P2's bincode
//! `ScheduleTask` / `CancelTask` produce (bincode 1, fixint, little endian; asserted against the SDK types by the
//! tests below). Dropping the builder and `bincode` from the program removes serde (and with it ~25 KB of float
//! formatting), `hashbrown` and the intent builder from the `.so` (~70 KB), which keeps an upgrade buffer inside the
//! devnet budget.

use anchor_lang::prelude::*;
use anchor_lang::solana_program::instruction::{AccountMeta, Instruction};
use anchor_lang::solana_program::program::invoke;

/// `MagicBlockInstruction` variant indices (magicblock-magic-program-api 0.10.1).
pub const IX_SCHEDULE_TASK: u32 = 6;
pub const IX_CANCEL_TASK: u32 = 7;
pub const IX_SCHEDULE_INTENT_BUNDLE: u32 = 11;

/// `ScheduleIntentBundle` data committing the accounts at indices `2 .. 2 + n` (after payer, magic context), with
/// or without undelegation, no actions.
pub fn intent_data(n: usize, undelegate: bool) -> Vec<u8> {
    let at: Vec<u8> = (0..n).map(|i| 2 + i as u8).collect();
    intent_data_at(&at, undelegate)
}

/// `ScheduleIntentBundle` data committing the accounts at the given CPI account indices (e.g. a fee-vault commit
/// whose payer is also a committed account: index 0), with or without undelegation, no actions.
pub fn intent_data_at(at: &[u8], undelegate: bool) -> Vec<u8> {
    let n = at.len();
    let mut d = Vec::with_capacity(32 + n);
    d.extend_from_slice(&IX_SCHEDULE_INTENT_BUNDLE.to_le_bytes());
    let indices = |d: &mut Vec<u8>| {
        d.extend_from_slice(&0u32.to_le_bytes()); // CommitTypeArgs::Standalone
        d.extend_from_slice(&(n as u64).to_le_bytes());
        d.extend_from_slice(at);
    };
    if undelegate {
        d.push(0); // commit: None
        d.push(1); // commit_and_undelegate: Some
        indices(&mut d);
        d.extend_from_slice(&0u32.to_le_bytes()); // UndelegateTypeArgs::Standalone
    } else {
        d.push(1); // commit: Some
        indices(&mut d);
        d.push(0); // commit_and_undelegate: None
    }
    d.push(0); // commit_finalize: None
    d.push(0); // commit_finalize_and_undelegate: None
    d.extend_from_slice(&0u64.to_le_bytes()); // standalone_actions: []
    d
}

/// Commits (and optionally undelegates) `accounts` from the rollup, paid by `payer` (= the builder's
/// `commit(..).build_and_invoke()` / `commit_and_undelegate(..)`).
pub fn commit<'info>(payer: &AccountInfo<'info>, magic_context: &AccountInfo<'info>, magic_program: &AccountInfo<'info>, accounts: &[AccountInfo<'info>], undelegate: bool) -> Result<()> {
    keq!(magic_program.key(), ephemeral_rollups_sdk::consts::MAGIC_PROGRAM_ID, crate::errors::FallRushError::BadTask);
    let mut metas = vec![AccountMeta::new(payer.key(), true), AccountMeta::new(magic_context.key(), false)];
    let mut infos = vec![payer.clone(), magic_context.clone()];
    for a in accounts {
        metas.push(AccountMeta { pubkey: a.key(), is_signer: a.is_signer, is_writable: a.is_writable });
        infos.push(a.clone());
    }
    infos.push(magic_program.clone());
    invoke(&Instruction { program_id: magic_program.key(), accounts: metas, data: intent_data(accounts.len(), undelegate) }, &infos)?;
    Ok(())
}

/// Commits `accounts` paid by a DELEGATED program PDA (`payer`, signed with `payer_seeds`) through the validator's
/// `magic_fee_vault` (= the SDK builder's `.magic_fee_vault(v)`): `[payer (s, w), magic_context (w), fee_vault (w),
/// accounts…]`. MagicBlock core 2.0: without a delegated payer + fee vault an account commits at most 10 times
/// ("sponsored commit limit"); on this path the delegated payer covers commits instead. A committed account equal to
/// the payer is not repeated: its index is 0 (the builder's duplicate filter).
pub fn commit_with_fee_vault<'info>(payer: &AccountInfo<'info>, payer_seeds: &[&[u8]], magic_context: &AccountInfo<'info>, fee_vault: &AccountInfo<'info>, magic_program: &AccountInfo<'info>, accounts: &[AccountInfo<'info>]) -> Result<()> {
    keq!(magic_program.key(), ephemeral_rollups_sdk::consts::MAGIC_PROGRAM_ID, crate::errors::FallRushError::BadTask);
    // the fee vault itself is checked by the magic program (it must be the validator's vault)
    let mut metas = vec![AccountMeta::new(payer.key(), true), AccountMeta::new(magic_context.key(), false), AccountMeta::new(fee_vault.key(), false)];
    let mut infos = vec![payer.clone(), magic_context.clone(), fee_vault.clone()];
    let mut at = Vec::with_capacity(accounts.len());
    for a in accounts {
        if a.key() == payer.key() {
            at.push(0u8);
            continue;
        }
        at.push(metas.len() as u8);
        metas.push(AccountMeta { pubkey: a.key(), is_signer: false, is_writable: a.is_writable });
        infos.push(a.clone());
    }
    infos.push(magic_program.clone());
    anchor_lang::solana_program::program::invoke_signed(&Instruction { program_id: magic_program.key(), accounts: metas, data: intent_data_at(&at, false) }, &infos, &[payer_seeds])?;
    Ok(())
}

/// P5 0.3 (Magic Action): `ScheduleIntentBundle` committing + undelegating the accounts at indices `2 .. 2 + n` with
/// ONE post-undelegate base action (`UndelegateTypeArgs::WithBaseActions`): `ActionArgs { escrow_index, data }`,
/// `compute_units`, `escrow_authority` (index in the CPI account list; must sign the CPI), `destination_program` and
/// the action's account metas (pubkey, is_writable; no signers: the escrow is the only signer, injected by the
/// delegation program's `CallHandlerV2`). Bytes asserted against the SDK bincode in the tests below.
pub fn intent_data_with_action(n: usize, escrow_index: u8, escrow_authority: u8, compute_units: u32, destination: &Pubkey, metas: &[(Pubkey, bool)], data: &[u8]) -> Vec<u8> {
    let mut d = Vec::with_capacity(96 + n + metas.len() * 33 + data.len());
    d.extend_from_slice(&IX_SCHEDULE_INTENT_BUNDLE.to_le_bytes());
    d.push(0); // commit: None
    d.push(1); // commit_and_undelegate: Some
    d.extend_from_slice(&0u32.to_le_bytes()); // CommitTypeArgs::Standalone
    d.extend_from_slice(&(n as u64).to_le_bytes());
    for i in 0..n {
        d.push(2 + i as u8);
    }
    d.extend_from_slice(&1u32.to_le_bytes()); // UndelegateTypeArgs::WithBaseActions
    d.extend_from_slice(&1u64.to_le_bytes()); // one action
    d.push(escrow_index);
    d.extend_from_slice(&(data.len() as u64).to_le_bytes());
    d.extend_from_slice(data);
    d.extend_from_slice(&compute_units.to_le_bytes());
    d.push(escrow_authority);
    d.extend_from_slice(destination.as_ref());
    d.extend_from_slice(&(metas.len() as u64).to_le_bytes());
    for (k, w) in metas {
        d.extend_from_slice(k.as_ref());
        d.push(*w as u8);
    }
    d.push(0); // commit_finalize: None
    d.push(0); // commit_finalize_and_undelegate: None
    d.extend_from_slice(&0u64.to_le_bytes()); // standalone_actions: []
    d
}

/// Commit + undelegate `accounts` with one post-undelegate action; `escrow_auth` (a program PDA) signs the CPI with
/// `escrow_auth_seeds` and is appended after the committed accounts (its index is passed as `escrow_authority`).
#[allow(clippy::too_many_arguments)]
pub fn commit_undelegate_with_action<'info>(payer: &AccountInfo<'info>, magic_context: &AccountInfo<'info>, magic_program: &AccountInfo<'info>, accounts: &[AccountInfo<'info>], escrow_auth: &AccountInfo<'info>, escrow_auth_seeds: &[&[u8]], escrow_index: u8, compute_units: u32, metas: &[(Pubkey, bool)], data: &[u8]) -> Result<()> {
    keq!(magic_program.key(), ephemeral_rollups_sdk::consts::MAGIC_PROGRAM_ID, crate::errors::FallRushError::BadTask);
    let mut am = vec![AccountMeta::new(payer.key(), true), AccountMeta::new(magic_context.key(), false)];
    let mut infos = vec![payer.clone(), magic_context.clone()];
    for a in accounts {
        am.push(AccountMeta { pubkey: a.key(), is_signer: a.is_signer, is_writable: a.is_writable });
        infos.push(a.clone());
    }
    let auth_index = am.len() as u8;
    am.push(AccountMeta::new_readonly(escrow_auth.key(), true));
    infos.push(escrow_auth.clone());
    infos.push(magic_program.clone());
    let d = intent_data_with_action(accounts.len(), escrow_index, auth_index, compute_units, &crate::ID, metas, data);
    anchor_lang::solana_program::program::invoke_signed(&Instruction { program_id: magic_program.key(), accounts: am, data: d }, &infos, &[escrow_auth_seeds])?;
    Ok(())
}

/// bincode of `MagicBlockInstruction::ScheduleTask(ScheduleTaskArgs { task_id, execution_interval_millis, iterations,
/// instructions: [ix] })`.
pub fn schedule_task_data(task_id: i64, interval_ms: i64, iterations: i64, ix: &Instruction) -> Vec<u8> {
    let mut d = Vec::with_capacity(64 + ix.accounts.len() * 34 + ix.data.len());
    d.extend_from_slice(&IX_SCHEDULE_TASK.to_le_bytes());
    d.extend_from_slice(&task_id.to_le_bytes());
    d.extend_from_slice(&interval_ms.to_le_bytes());
    d.extend_from_slice(&iterations.to_le_bytes());
    d.extend_from_slice(&1u64.to_le_bytes());
    d.extend_from_slice(ix.program_id.as_ref());
    d.extend_from_slice(&(ix.accounts.len() as u64).to_le_bytes());
    for m in &ix.accounts {
        d.extend_from_slice(m.pubkey.as_ref());
        d.push(m.is_signer as u8);
        d.push(m.is_writable as u8);
    }
    d.extend_from_slice(&(ix.data.len() as u64).to_le_bytes());
    d.extend_from_slice(&ix.data);
    d
}

pub fn cancel_task_data(task_id: i64) -> Vec<u8> {
    let mut d = Vec::with_capacity(12);
    d.extend_from_slice(&IX_CANCEL_TASK.to_le_bytes());
    d.extend_from_slice(&task_id.to_le_bytes());
    d
}

#[cfg(test)]
mod tests {
    use super::*;
    use magicblock_magic_program_api::args::{CommitAndUndelegateArgs, CommitTypeArgs, MagicIntentBundleArgs, ScheduleTaskArgs, UndelegateTypeArgs};
    use magicblock_magic_program_api::instruction::MagicBlockInstruction;

    #[test]
    fn intent_bytes_equal_the_sdk_bincode() {
        for n in 1..4usize {
            let idx: Vec<u8> = (0..n).map(|i| 2 + i as u8).collect();
            let c = MagicIntentBundleArgs { commit: Some(CommitTypeArgs::Standalone(idx.clone())), ..Default::default() };
            assert_eq!(bincode::serialize(&MagicBlockInstruction::ScheduleIntentBundle(c)).unwrap(), intent_data(n, false));
            let u = MagicIntentBundleArgs { commit_and_undelegate: Some(CommitAndUndelegateArgs { commit_type: CommitTypeArgs::Standalone(idx), undelegate_type: UndelegateTypeArgs::Standalone }), ..Default::default() };
            assert_eq!(bincode::serialize(&MagicBlockInstruction::ScheduleIntentBundle(u)).unwrap(), intent_data(n, true));
        }
    }

    #[test]
    fn fee_vault_intent_bytes_equal_the_sdk_bincode() {
        // the payer committing itself (index 0) and a market after payer, magic context, fee vault (index 3)
        for idx in [vec![0u8], vec![3u8], vec![3u8, 4]] {
            let c = MagicIntentBundleArgs { commit: Some(CommitTypeArgs::Standalone(idx.clone())), ..Default::default() };
            assert_eq!(bincode::serialize(&MagicBlockInstruction::ScheduleIntentBundle(c)).unwrap(), intent_data_at(&idx, false));
        }
    }

    #[test]
    fn action_intent_bytes_equal_the_sdk_bincode() {
        use magicblock_magic_program_api::args::{ActionArgs, BaseActionArgs, ShortAccountMeta};
        let metas: Vec<(Pubkey, bool)> = (0..7).map(|i| (Pubkey::new_unique(), i % 2 == 0)).collect();
        let data = vec![9u8, 8, 7, 6, 5, 4, 3, 2, 1, 0, 3, 0, 0, 0, 0, 1, 4];
        let act = BaseActionArgs {
            args: ActionArgs::new(data.clone()).with_escrow_index(255),
            compute_units: 230_000,
            escrow_authority: 3,
            destination_program: crate::ID,
            accounts: metas.iter().map(|(k, w)| ShortAccountMeta { pubkey: *k, is_writable: *w }).collect(),
        };
        let u = MagicIntentBundleArgs {
            commit_and_undelegate: Some(CommitAndUndelegateArgs { commit_type: CommitTypeArgs::Standalone(vec![2]), undelegate_type: UndelegateTypeArgs::WithBaseActions { base_actions: vec![act] } }),
            ..Default::default()
        };
        assert_eq!(bincode::serialize(&MagicBlockInstruction::ScheduleIntentBundle(u)).unwrap(), intent_data_with_action(1, 255, 3, 230_000, &crate::ID, &metas, &data));
    }

    #[test]
    fn task_bytes_equal_the_sdk_bincode() {
        let ix = Instruction {
            program_id: crate::ID,
            accounts: vec![AccountMeta::new_readonly(Pubkey::new_unique(), false), AccountMeta::new(Pubkey::new_unique(), false)],
            data: vec![1, 2, 3, 4, 5, 6, 7, 8],
        };
        let want = bincode::serialize(&MagicBlockInstruction::ScheduleTask(ScheduleTaskArgs { task_id: 77, execution_interval_millis: 11, iterations: 2_000_000, instructions: vec![ix.clone()] })).unwrap();
        assert_eq!(want, schedule_task_data(77, 11, 2_000_000, &ix));
        assert_eq!(bincode::serialize(&MagicBlockInstruction::CancelTask { task_id: -5 }).unwrap(), cancel_task_data(-5));
    }
}
