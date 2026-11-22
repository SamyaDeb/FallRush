//! Golden vectors (P1 prompt B.4, C.5, D.4, E.3, F): deterministic cases generated from an index, run identically on
//! every target (native, WASM, sBPF, the Unity FFI). Each case writes its inputs and outputs as little-endian bytes;
//! a run folds every case's bytes into an FNV-1a 64 digest. `rush-sim vectors gen` writes the case bytes to
//! `tests/vectors/<kind>_v1.bin` with the digest in the header; every target recomputes the digest and must match.

use crate::collide::Scene;
use crate::config::MotorConfig;
use crate::fx;
use crate::input::InputCmd;
use crate::mapblob::MapView;
use crate::motor::{self, MotorState};
use crate::vec::IVec3;

pub const KIND_FX: u8 = 1;
pub const KIND_COLLIDE: u8 = 2;
pub const KIND_MOTOR: u8 = 3;
pub const KIND_HAZARD: u8 = 4;
pub const KIND_WORLD: u8 = 5;

/// Most bytes one case writes.
pub const MAX_CASE: usize = 192;

/// FNV-1a 64.
#[derive(Clone, Copy, Debug)]
pub struct Fnv(pub u64);

impl Default for Fnv {
    fn default() -> Self {
        Fnv(0xcbf2_9ce4_8422_2325)
    }
}

impl Fnv {
    #[inline]
    pub fn bytes(&mut self, b: &[u8]) {
        let mut i = 0;
        while i < b.len() {
            self.0 ^= b[i] as u64;
            self.0 = self.0.wrapping_mul(0x0100_0000_01b3);
            i += 1;
        }
    }
}

/// SplitMix64 stream for case inputs.
#[derive(Clone, Copy, Debug)]
pub struct Rng(pub u64);

impl Rng {
    pub fn next(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }

    /// Uniform in [lo, hi] (modulo bias is irrelevant for vectors).
    pub fn range(&mut self, lo: i64, hi: i64) -> i64 {
        lo + (self.next() % ((hi - lo + 1) as u64)) as i64
    }
}

/// A byte writer over a fixed buffer.
pub struct W<'a> {
    pub b: &'a mut [u8],
    pub n: usize,
}

impl W<'_> {
    pub fn i64(&mut self, v: i64) {
        self.b[self.n..self.n + 8].copy_from_slice(&v.to_le_bytes());
        self.n += 8;
    }
    pub fn i32(&mut self, v: i32) {
        self.b[self.n..self.n + 4].copy_from_slice(&v.to_le_bytes());
        self.n += 4;
    }
    pub fn u8(&mut self, v: u8) {
        self.b[self.n] = v;
        self.n += 1;
    }
    pub fn v3(&mut self, v: IVec3) {
        self.i32(v.x);
        self.i32(v.y);
        self.i32(v.z);
    }
}

fn case_rng(kind: u8, seed: u64, i: u32) -> Rng {
    let mut r = Rng(seed ^ ((kind as u64) << 56) ^ (i as u64).wrapping_mul(0xA24B_AED4_963E_E407));
    r.next();
    r
}

/// fx case `i`: every maths helper on random operands.
pub fn fx_case(seed: u64, i: u32, out: &mut [u8]) -> usize {
    let mut r = case_rng(KIND_FX, seed, i);
    let mut w = W { b: out, n: 0 };
    let a = r.range(-(1 << 40), 1 << 40);
    let b = r.range(1, 1 << 20) * if r.next() & 1 == 0 { 1 } else { -1 };
    let p = r.range(0, 65535) as i32;
    let (x, y, z) = (r.range(-200_000, 200_000), r.range(-200_000, 200_000), r.range(-200_000, 200_000));
    let (mx, mz) = (r.range(-127, 127) as i32, r.range(-127, 127) as i32);
    let u = r.next();
    for v in [a, b, p as i64, x, y, z, mx as i64, mz as i64] {
        w.i64(v);
    }
    w.i64(fx::div_round(a, b));
    w.i64(fx::round_shift(a, (u % 30) as u32 + 1));
    w.i64(fx::isqrt_u64(u) as i64);
    w.i64(fx::isqrt_round(a.abs()));
    w.i64(fx::mul_div(a, x, b));
    w.i32(fx::sin_q15(p));
    w.i32(fx::cos_q15(p));
    let n = fx::normalize_q15(x, y, z);
    w.i32(n[0]);
    w.i32(n[1]);
    w.i32(n[2]);
    w.u8(fx::atan2_yaw256(mx, mz, (u >> 8) as u8));
    w.u8(fx::atan2_yaw256(x as i32, z as i32, 3));
    let d = fx::yaw_dir_q15((u >> 16) as u8);
    w.i32(d[0]);
    w.i32(d[1]);
    w.n
}

/// collide case `i` on `map`: a random sweep, raycast and penetration query.
pub fn collide_case(sc: &Scene, seed: u64, i: u32, out: &mut [u8]) -> usize {
    let mut r = case_rng(KIND_COLLIDE, seed, i);
    let mut w = W { b: out, n: 0 };
    let p = IVec3::new(r.range(-20_000, 20_000) as i32, r.range(-500, 4000) as i32, r.range(-20_000, 20_000) as i32);
    let d = {
        let n = fx::normalize_q15(r.range(-1000, 1000), r.range(-1000, 300), r.range(-1000, 1000));
        IVec3::new(n[0], n[1], n[2])
    };
    let dist = r.range(0, 4000);
    w.v3(p);
    w.v3(d);
    w.i64(dist);
    match sc.capsule_sweep(p, 310, 1000, d, dist) {
        Some(h) => {
            w.u8(1);
            w.i64(h.distance);
            w.v3(h.point);
            w.v3(h.normal);
            w.i32(h.surface);
        }
        None => w.u8(0),
    }
    match sc.raycast(p, d, dist + 2000) {
        Some(h) => {
            w.u8(1);
            w.i64(h.distance);
            w.v3(h.normal);
        }
        None => w.u8(0),
    }
    match sc.penetration(p, 310, 1000) {
        Some(c) => {
            w.u8(1);
            w.v3(c);
        }
        None => w.u8(0),
    }
    w.n
}

/// motor case `i` on `map`: a random start state stepped 8 ticks with random inputs at rate 15, 20 or 30.
pub fn motor_case(sc: &Scene, seed: u64, i: u32, out: &mut [u8]) -> usize {
    let mut r = case_rng(KIND_MOTOR, seed, i);
    let mut w = W { b: out, n: 0 };
    let rate = [15u32, 20, 30][r.range(0, 2) as usize];
    let c = MotorConfig::default_at(rate);
    let mut s = MotorState::at(IVec3::new(r.range(-20_000, 20_000) as i32, r.range(0, 3000) as i32, r.range(-20_000, 20_000) as i32), r.range(0, 255) as u8);
    s.vel = IVec3::new(r.range(-6000, 6000) as i32, r.range(-8000, 6000) as i32, r.range(-6000, 6000) as i32);
    s.flags = (r.next() as u8) & (motor::flag::GROUNDED | motor::flag::DIVING | motor::flag::STUMBLING);
    s.land = if s.has(motor::flag::STUMBLING) { 3 } else { 0 };
    w.u8(rate as u8);
    w.v3(s.pos);
    w.v3(s.vel);
    let mut k = 0;
    while k < 8 {
        let mut cmd = InputCmd { move_x: r.range(-127, 127) as i8, move_y: r.range(-127, 127) as i8, buttons: (r.next() as u8) & 0x0F, cam_yaw: r.next() as u16 };
        crate::input::sanitize(&mut cmd);
        motor::step(&c, &mut s, &cmd, sc);
        k += 1;
    }
    w.v3(s.pos);
    w.v3(s.vel);
    for b in [s.yaw, s.flags, s.stun, s.coyote, s.jump_buffer, s.dive_cooldown, s.land, s.immunity, s.surface] {
        w.u8(b);
    }
    w.n
}

/// hazard case `i`: random defs of every type at random times (see `hazards::vectors`).
pub fn hazard_case(seed: u64, i: u32, out: &mut [u8]) -> usize {
    crate::hazards::vectors::case(seed, i, out)
}

/// Runs `n` cases of `kind` (from `seed`), folding every case's bytes. `map` is required for collide / motor / world.
pub fn run(kind: u8, seed: u64, n: u32, map: Option<&[u8]>) -> u64 {
    if kind == KIND_WORLD {
        // native / WASM: the scratch World lives on this (large) stack
        let mut w = match map.and_then(|m| MapView::new(m).ok()) {
            Some(v) => crate::world::World::new(&v, 20),
            None => return Fnv::default().0,
        };
        return run_from(kind, seed, 0, n, map, Fnv::default().0, Some(&mut w));
    }
    run_from(kind, seed, 0, n, map, Fnv::default().0, None)
}

/// Cases `start .. start + n`, continuing the digest `state` (chunked runs on sBPF).
/// `world` is the scratch World for `KIND_WORLD` (the sBPF program passes a heap one).
pub fn run_from(kind: u8, seed: u64, start: u32, n: u32, map: Option<&[u8]>, state: u64, mut world: Option<&mut crate::world::World>) -> u64 {
    let mut h = Fnv(state);
    if kind == KIND_COLLIDE || kind == KIND_MOTOR {
        if let Some(v) = map.and_then(|m| MapView::new(m).ok()) {
            scene_cases(kind, v, seed, start, n, &mut h);
        }
        return h.0;
    }
    let mut buf = [0u8; MAX_CASE];
    let mut i = start;
    while i < start + n {
        let len = match kind {
            KIND_FX => fx_case(seed, i, &mut buf),
            KIND_HAZARD => hazard_case(seed, i, &mut buf),
            KIND_WORLD => match (map, world.as_deref_mut()) {
                (Some(m), Some(w)) => crate::world::vector_case(w, m, seed, i, &mut buf),
                _ => 0,
            },
            _ => 0,
        };
        h.bytes(&buf[..len]);
        i += 1;
    }
    h.0
}

/// Collide / motor cases over one `Scene` (own frame: sBPF stack frames are 4 KB and a `Scene` is ≈ 2 KB).
#[inline(never)]
fn scene_cases(kind: u8, v: MapView, seed: u64, start: u32, n: u32, h: &mut Fnv) {
    let sc = Scene::new(v);
    let mut buf = [0u8; MAX_CASE];
    let mut i = start;
    while i < start + n {
        let len = if kind == KIND_COLLIDE { collide_case(&sc, seed, i, &mut buf) } else { motor_case(&sc, seed, i, &mut buf) };
        h.bytes(&buf[..len]);
        i += 1;
    }
}

/// Default seeds and counts (the committed `.bin` files).
pub const SEED: u64 = 0x005E_ED0F_FA11_0001;
pub const COUNT_FX: u32 = 10_000;
pub const COUNT_COLLIDE: u32 = 4_000;
pub const COUNT_MOTOR: u32 = 2_000;
pub const COUNT_HAZARD: u32 = 6_000;
pub const COUNT_WORLD: u32 = 64;
