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
    // The scratch World (≈ 2 KB) lives on the heap: sBPF stack frames are 4 KB. Every field is an integer / bool /
    // array of them, so the zeroed bytes are a valid value; `reset` then sets it up in place.
    // (`Box::new_zeroed` is unstable on the SBF toolchain's rustc; `alloc_zeroed` + `Box::from_raw` is the stable form.)
    let layout = std::alloc::Layout::new::<rush_core::world::World>();
    let mut world: Box<rush_core::world::World> = unsafe {
        let p = std::alloc::alloc_zeroed(layout) as *mut rush_core::world::World;
        if p.is_null() {
            return Err(ProgramError::AccountDataTooSmall);
        }
        Box::from_raw(p)
    };
    let digest = match accounts.first() {
        Some(acc) => {
            let map = acc.try_borrow_data()?;
            if let Ok(v) = rush_core::mapblob::MapView::new(&map) {
                world.reset(&v, 20);
            }
            rush_core::vectors::run_from(kind, seed, start, n, if map.is_empty() { None } else { Some(&map[..]) }, state, Some(&mut world))
        }
        None => rush_core::vectors::run_from(kind, seed, start, n, None, state, None),
    };
    set_return_data(&digest.to_le_bytes());
    Ok(())
}
