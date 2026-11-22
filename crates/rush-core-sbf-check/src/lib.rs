//! rush-core on sBPF: a minimal Solana program that runs golden-vector cases and returns the digest via return
//! data (P1 prompt A.5). Instruction data: `kind u8 | seed u64 | start u32 | n u32 | state u64` (25 B); account 0
//! (optional, read-only) holds the map blob. Return data: the 8-byte digest after cases `start..start+n`, continuing
//! `state`, so a long vector file runs in chunks under the compute limit. Also logs the compute units used.
#![allow(unexpected_cfgs)]

use solana_program::account_info::AccountInfo;
use solana_program::entrypoint::ProgramResult;
use solana_program::program::set_return_data;
use solana_program::program_error::ProgramError;
use solana_program::pubkey::Pubkey;

#[cfg(not(feature = "no-entrypoint"))]
solana_program::entrypoint!(process);

pub fn process(_program: &Pubkey, accounts: &[AccountInfo], data: &[u8]) -> ProgramResult {
    if data.len() < 25 {
        return Err(ProgramError::InvalidInstructionData);
    }
    let kind = data[0];
    let seed = u64::from_le_bytes(data[1..9].try_into().unwrap());
    let start = u32::from_le_bytes(data[9..13].try_into().unwrap());
    let n = u32::from_le_bytes(data[13..17].try_into().unwrap());
    let state = u64::from_le_bytes(data[17..25].try_into().unwrap());
    let digest = match accounts.first() {
        Some(acc) => {
            let map = acc.try_borrow_data()?;
            rush_core::vectors::run_from(kind, seed, start, n, if map.is_empty() { None } else { Some(&map[..]) }, state)
        }
        None => rush_core::vectors::run_from(kind, seed, start, n, None, state),
    };
    set_return_data(&digest.to_le_bytes());
    Ok(())
}
