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
    let mut d = Vec::with_capacity(32 + n);
    d.extend_from_slice(&IX_SCHEDULE_INTENT_BUNDLE.to_le_bytes());
    let indices = |d: &mut Vec<u8>| {
        d.extend_from_slice(&0u32.to_le_bytes()); // CommitTypeArgs::Standalone
        d.extend_from_slice(&(n as u64).to_le_bytes());
        for i in 0..n {
            d.push(2 + i as u8);
        }
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
    require_keys_eq!(magic_program.key(), ephemeral_rollups_sdk::consts::MAGIC_PROGRAM_ID, crate::errors::FallRushError::BadTask);
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
