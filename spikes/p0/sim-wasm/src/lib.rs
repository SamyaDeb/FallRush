//! wasm32-unknown-unknown C ABI over p0_sim (no wasm-bindgen). JS: alloc -> copy map -> hash.
#![deny(clippy::float_arithmetic)]
use p0_sim::{bot_input, init_world, map::Map, step, world_hash, Input, Stats, N};

/// Allocate `words` u32 (4-aligned) and leak it; returns the byte pointer.
#[no_mangle]
pub extern "C" fn p0_alloc(words: u32) -> *mut u8 {
    let v = vec![0u32; words as usize].into_boxed_slice();
    Box::leak(v).as_mut_ptr() as *mut u8
}

/// World hash after `steps` scripted-bot steps from `seed` on the map at (ptr,len). 0 = bad map.
#[no_mangle]
pub extern "C" fn p0_hash(ptr: *const u8, len: u32, seed: u64, steps: u32) -> u64 {
    let b = unsafe { core::slice::from_raw_parts(ptr, len as usize) };
    let m = match Map::parse(b) {
        Ok(m) => m,
        Err(_) => return 0,
    };
    let mut w = init_world(&m, seed);
    for _ in 0..steps {
        let mut inp = [Input::default(); N];
        for s in 0..N {
            inp[s] = bot_input(s, w.tick, w.seed, &m, &w.players[s]);
        }
        step(&mut w, &inp, &m, &mut Stats::default());
    }
    world_hash(&w)
}
