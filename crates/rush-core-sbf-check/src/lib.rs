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
    if data.first() == Some(&BENCH_OP) {
        return bench_op(accounts, data);
    }
    if data.first() == Some(&BENCH_STEP) {
        return bench_step(accounts, data);
    }
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

/// P2 0.2 CU bench: `[0x20, what]`, accounts `[map (ro), world (rw)]`. The world account holds canonical `World`
/// bytes; `what` 0 = decode + one `step()` (all bots / no human input) + encode back, 1 = decode + encode only (the
/// codec overhead), 2 = step on a `World` decoded but never re-encoded (the step alone, minus the write).
/// 3 = like 0 with a compute-unit log at every `Hasher::mark` (profile).
pub const BENCH_STEP: u8 = 0x20;

/// A heap `Scene` (≈ 3.5 KB): built in its own frame so the temporary never shares a 4 KB frame with other locals.
#[inline(never)]
fn new_scene(v: rush_core::mapblob::MapView) -> Box<rush_core::collide::Scene> {
    Box::new(rush_core::collide::Scene::new(v))
}

struct ProfHasher;

impl rush_core::world::Hasher for ProfHasher {
    fn sha256(&mut self, _parts: &[&[u8]]) -> [u8; 32] {
        [0; 32]
    }
    fn mark(&mut self, tag: u8) {
        solana_program::log::sol_log_64(0xC0, tag as u64, 0, 0, 0);
        solana_program::log::sol_log_compute_units();
    }
}

#[inline(never)]
fn bench_step(accounts: &[AccountInfo], data: &[u8]) -> ProgramResult {
    let what = data.get(1).copied().unwrap_or(0);
    let (map_acc, world_acc) = match accounts {
        [m, w, ..] => (m, w),
        _ => return Err(ProgramError::NotEnoughAccountKeys),
    };
    let map = map_acc.try_borrow_data()?;
    let v = rush_core::mapblob::MapView::new(&map).map_err(|_| ProgramError::InvalidAccountData)?;
    let layout = std::alloc::Layout::new::<rush_core::world::World>();
    let mut world: Box<rush_core::world::World> = unsafe {
        let p = std::alloc::alloc_zeroed(layout) as *mut rush_core::world::World;
        if p.is_null() {
            return Err(ProgramError::AccountDataTooSmall);
        }
        Box::from_raw(p)
    };
    let mut wd = world_acc.try_borrow_mut_data()?;
    if !world.load_from(&wd) {
        return Err(ProgramError::InvalidAccountData);
    }
    if what != 1 {
        let mut ev: Box<rush_core::world::StepEvents> = Box::default();
        let params = rush_core::config::MovementParams::default();
        let mut sc = new_scene(v);
        if what == 3 {
            rush_core::world::step_with(&mut world, &[None; rush_core::world::MAX_PLAYERS], &v, &params, &mut ProfHasher, &mut ev, &mut sc);
        } else {
            rush_core::world::step_with(&mut world, &[None; rush_core::world::MAX_PLAYERS], &v, &params, &mut rush_core::world::NoHasher, &mut ev, &mut sc);
        }
    }
    if what != 2 {
        let n = world.to_bytes(&mut wd);
        set_return_data(&(n as u32).to_le_bytes());
    }
    Ok(())
}

/// P2 0.2 primitive bench: `[0x21, op, n u16 LE, arg u32 LE]`, account 0 = map blob. Runs `op` `n` times on black-boxed
/// inputs (the harness subtracts op 0, the empty loop). `arg` = a collider index for the per-collider ops.
pub const BENCH_OP: u8 = 0x21;

#[inline(never)]
fn bench_op(accounts: &[AccountInfo], data: &[u8]) -> ProgramResult {
    use core::hint::black_box as bb;
    use rush_core::fx;
    use rush_core::vec::IVec3;
    if data.len() < 8 {
        return Err(ProgramError::InvalidInstructionData);
    }
    let op = data[1];
    let n = u16::from_le_bytes([data[2], data[3]]) as u32;
    let arg = u32::from_le_bytes(data[4..8].try_into().unwrap());
    let map = accounts.first().ok_or(ProgramError::NotEnoughAccountKeys)?.try_borrow_data()?;
    let v = rush_core::mapblob::MapView::new(&map).map_err(|_| ProgramError::InvalidAccountData)?;
    let sc = rush_core::collide::Scene::new(v);
    let mut acc = 0i64;
    let mut i = 0u32;
    while i < n {
        let x = bb(123_456_789i64 + i as i64);
        acc = acc.wrapping_add(match op {
            0 => x,
            1 => fx::div_round(bb(x), bb(32768)),
            2 => fx::mul_div(bb(x), bb(77_777), bb(32768)),
            3 => fx::isqrt_i64(bb(x)),
            4 => fx::normalize_q15(bb(x), bb(4000), bb(-x / 3))[0] as i64,
            5 => v.collider(bb(arg)).center.x as i64,
            6 => {
                let c = v.collider(arg);
                let (sp, _) = v.spawn(0);
                let f = [sp.x as i64 * 16, sp.y as i64 * 16 + bb(0), sp.z as i64 * 16];
                let s = rush_core::collide::Seg::of(f, 450, 1800);
                sc.near(&c, IVec3::ZERO, &s).gap2
            }
            7 => {
                let (sp, _) = v.spawn(0);
                sc.capsule_sweep(bb(sp), 450, 1800, IVec3::new(0, -32768, 0), bb(100)).map(|h| h.distance).unwrap_or(0)
            }
            8 => {
                let (sp, _) = v.spawn(0);
                sc.raycast(bb(sp.add(IVec3::new(0, 500, 0))), IVec3::new(0, -32768, 0), bb(3000)).map(|h| h.distance).unwrap_or(0)
            }
            9 => {
                let (sp, _) = v.spawn(0);
                sc.penetration(bb(sp), 450, 1800).map(|h| h.x as i64).unwrap_or(0)
            }
            10 => {
                let (sp, _) = v.spawn(0);
                let mut bits = [0u64; rush_core::mapblob::MAX_COLLIDERS / 64];
                sc.gather(bb(sp.sub(IVec3::new(500, 0, 500))), bb(sp.add(IVec3::new(500, 2000, 500))), &mut bits);
                bits[0] as i64
            }
            11 => {
                let (sp, _) = v.spawn(0);
                sc.capsule_sweep(bb(sp.add(IVec3::new(0, 20, 0))), 450, 1800, IVec3::new(32768, 0, 0), bb(300)).map(|h| h.distance).unwrap_or(0)
            }
            12 => fx::sin_q15(bb(x as i32)) as i64,
            13 => (bb(x) / bb(-32768)) as i64,
            14 => ((bb(x) as u64) / bb(32768u64)) as i64,
            15 => bb(x) / 3,
            16 => fx::idiv(bb(x), 3),
            17 => (bb(x as i32) / 10) as i64,
            18 => (bb(x) * 7 / 1000) as i64,
            19 => fx::div_round(bb(x), bb(1000)),
            _ => 0,
        });
        i += 1;
    }
    set_return_data(&acc.to_le_bytes());
    Ok(())
}

/// sBPF v0 has no signed divide: LLVM lowers `i64 /` and `%` (also by constants) to the compiler-builtins routines,
/// which cost ≈ 240–350 CU each (P2 0.2, measured). These strong definitions replace the weak builtins with the
/// magnitude form over the native unsigned divide (≈ 11 CU). Same results as Rust's `/` and `%` (truncation; Rust
/// itself rejects a zero divisor and `MIN / -1` before calling them).
#[cfg(target_os = "solana")]
pub mod sdiv {
    #[no_mangle]
    pub extern "C" fn __divdi3(a: i64, b: i64) -> i64 {
        let q = (a.unsigned_abs() / b.unsigned_abs()) as i64;
        if (a < 0) != (b < 0) { q.wrapping_neg() } else { q }
    }
    #[no_mangle]
    pub extern "C" fn __moddi3(a: i64, b: i64) -> i64 {
        let r = (a.unsigned_abs() % b.unsigned_abs()) as i64;
        if a < 0 { r.wrapping_neg() } else { r }
    }
    #[no_mangle]
    pub extern "C" fn __divsi3(a: i32, b: i32) -> i32 {
        let q = (a.unsigned_abs() / b.unsigned_abs()) as i32;
        if (a < 0) != (b < 0) { q.wrapping_neg() } else { q }
    }
    #[no_mangle]
    pub extern "C" fn __modsi3(a: i32, b: i32) -> i32 {
        let r = (a.unsigned_abs() % b.unsigned_abs()) as i32;
        if a < 0 { r.wrapping_neg() } else { r }
    }
}
