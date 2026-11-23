//! rush-emu-core: P2's rush-core C ABI (`abi.rs`, included verbatim) plus emulator-only calls (`rcx_*`) for what the
//! on-chain program does itself: begin a round, report the last step's events, describe the World byte layout.
//! The client never loads this file; it uses P2's `rush_core_wasm.wasm`.
#![allow(clippy::missing_safety_doc)]
include!("../../../../crates/rush-core-wasm/src/abi.rs");

use rush_core::world::Event;

thread_local! {
    static LAST: RefCell<Vec<Event>> = const { RefCell::new(Vec::new()) };
}

/// `rules::begin_round` on the context's own map at the current tick (the program's `start_final` does this).
/// Returns the RUSH tick, or 0 on a bad handle.
#[no_mangle]
pub extern "C" fn rcx_begin_round(h: i32, target: u32, participants: u32, intro30: i32, countdown30: i32) -> u32 {
    with(h, |c| {
        let map = MapView::new(&c.map).unwrap();
        let mut ev = StepEvents::default();
        let t = c.w.tick;
        let r = rush_core::rules::begin_round(&mut c.w, &map, t, target as u8, participants as u16, intro30 as i64, countdown30 as i64, &mut ev);
        LAST.with(|l| *l.borrow_mut() = ev.as_slice().to_vec());
        r
    })
    .unwrap_or(0)
}

/// The director's intro / countdown lengths in 30 Hz ticks: `intro | countdown << 16`.
#[no_mangle]
pub extern "C" fn rcx_round_timing() -> u32 {
    (rush_core::rules::ROUND_INTRO_30 as u32) | (rush_core::rules::ROUND_COUNTDOWN_30 as u32) << 16
}

/// `rc_step` that also keeps the step's events for `rcx_events`.
///
/// # Safety
/// As `rc_step`.
#[no_mangle]
pub unsafe extern "C" fn rcx_step(h: i32, inputs_ptr: *const u8) -> u32 {
    let b = slice(inputs_ptr, MAX_PLAYERS * 6);
    with(h, |c| {
        let mut inputs = [None; MAX_PLAYERS];
        if b.len() == MAX_PLAYERS * 6 {
            for (s, inp) in inputs.iter_mut().enumerate() {
                *inp = read_cmd(&b[s * 6..s * 6 + 6]);
            }
        }
        let map = MapView::new(&c.map).unwrap();
        let mut ev = StepEvents::default();
        world::step(&mut c.w, &inputs, &map, &c.params, &mut Sha, &mut ev);
        LAST.with(|l| *l.borrow_mut() = ev.as_slice().to_vec());
        ev.n as u32
    })
    .unwrap_or(0)
}

/// The last `rcx_step` / `rcx_begin_round` events, 16 B each: kind, tag, slot, round, arg i32, tick u32, seq u32.
///
/// # Safety
/// `out_ptr` → `cap` writable bytes.
#[no_mangle]
pub unsafe extern "C" fn rcx_events(out_ptr: *mut u8, cap: u32) -> u32 {
    if out_ptr.is_null() {
        return 0;
    }
    let out = std::slice::from_raw_parts_mut(out_ptr, cap as usize);
    LAST.with(|l| {
        let mut n = 0;
        for e in l.borrow().iter() {
            if (n + 1) * 16 > out.len() {
                break;
            }
            let o = &mut out[n * 16..n * 16 + 16];
            o[0] = e.kind;
            o[1] = e.tag;
            o[2] = e.slot;
            o[3] = e.round;
            o[4..8].copy_from_slice(&e.arg.to_le_bytes());
            o[8..12].copy_from_slice(&e.tick.to_le_bytes());
            o[12..16].copy_from_slice(&e.seq.to_le_bytes());
            n += 1;
        }
        n as u32
    })
}

/// World byte layout, measured by encoding (so it follows rush-core): 16 × u32 LE =
/// header len, player stride, players offset, reactive len, drop queue len, round state offset, round len,
/// director offset, director len, total len, slot rules offset in a player, bot state offset in a player,
/// round-result offset in the round state, 0, 0, 0.
#[no_mangle]
pub unsafe extern "C" fn rcx_layout(out_ptr: *mut u8) -> u32 {
    use rush_core::codec::Enc;
    let out = std::slice::from_raw_parts_mut(out_ptr, 64);
    let len = |f: &dyn Fn(&mut Enc)| {
        let mut b = [0u8; 4096];
        let mut e = Enc::new(&mut b);
        f(&mut e);
        e.n as u32
    };
    let p = rush_core::world::Player::default();
    let header = 1 + 4 + 1 + 1 + 1 + 1 + 1 + 8 + 4u32;
    let stride = len(&|e| p.enc(e));
    let rx = len(&|e| rush_core::world::Reactive::default().enc(e));
    let dq = (rush_core::world::DROP_QUEUE * 6) as u32;
    let round = len(&|e| rush_core::rules::RoundState::default().enc(e));
    let dir = len(&|e| rush_core::rules::director::DirectorState::default().enc(e));
    let players_off = header;
    let round_off = players_off + stride * MAX_PLAYERS as u32 + rx + dq;
    let dir_off = round_off + round;
    let total = dir_off + dir + 4;
    let motor = len(&|e| p.m.enc(e));
    let slot_rules_off = motor + 3 + 6 + 4;
    let sr = len(&|e| p.r.enc(e));
    let bot_off = slot_rules_off + sr;
    let rr_off = 1 + 1 + 4 + 4 + 4 + 2 + 2 + 1 + 1 + 1 + 1 + 1 + 1 + 16 + MAX_PLAYERS as u32;
    let v = [header, stride, players_off, rx, dq, round_off, round, dir_off, dir, total, slot_rules_off, bot_off, rr_off, 0, 0, 0];
    for (i, x) in v.iter().enumerate() {
        out[i * 4..i * 4 + 4].copy_from_slice(&x.to_le_bytes());
    }
    total
}
