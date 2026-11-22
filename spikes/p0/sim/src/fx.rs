//! Integer helpers: isqrt, Q15 sin/cos on a 1024-step circle, FNV-1a hash.
use crate::trig_table::SIN_Q;

/// Q12 fixed point "1.0" used for unit normals.
pub const ONE: i32 = 4096;

/// floor(sqrt(n)) by integer Newton iteration (start above the root).
#[inline]
pub fn isqrt(n: u64) -> u64 {
    if n < 2 {
        return n;
    }
    let bits = 64 - n.leading_zeros();
    let mut x: u64 = 1u64 << ((bits + 1) / 2);
    loop {
        let y = (x + n / x) >> 1;
        if y >= x {
            return x;
        }
        x = y;
    }
}

/// sin of angle `a` in 1/1024 turns, Q15.
#[inline]
pub fn sin1024(a: u32) -> i32 {
    let a = a & 1023;
    let q = a >> 8; // quadrant
    let i = (a & 255) as usize;
    match q {
        0 => SIN_Q[i] as i32,
        1 => SIN_Q[256 - i] as i32,
        2 => -(SIN_Q[i] as i32),
        _ => -(SIN_Q[256 - i] as i32),
    }
}

#[inline]
pub fn cos1024(a: u32) -> i32 {
    sin1024(a.wrapping_add(256))
}

#[inline]
pub fn clamp(v: i32, lo: i32, hi: i32) -> i32 {
    if v < lo {
        lo
    } else if v > hi {
        hi
    } else {
        v
    }
}

/// Length of an integer 3-vector (mm), saturating into i32.
#[inline]
pub fn len3(x: i64, y: i64, z: i64) -> i32 {
    isqrt((x * x + y * y + z * z) as u64) as i32
}

/// Normalise (x,y,z) given its length `l` > 0 into Q12.
#[inline]
pub fn norm3(x: i64, y: i64, z: i64, l: i32) -> [i32; 3] {
    let l = l as i64;
    [
        (x * ONE as i64 / l) as i32,
        (y * ONE as i64 / l) as i32,
        (z * ONE as i64 / l) as i32,
    ]
}

pub const FNV_OFFSET: u64 = 0xcbf2_9ce4_8422_2325;
pub const FNV_PRIME: u64 = 0x0000_0100_0000_01b3;

#[inline]
pub fn fnv1a(mut h: u64, bytes: &[u8]) -> u64 {
    for b in bytes {
        h ^= *b as u64;
        h = h.wrapping_mul(FNV_PRIME);
    }
    h
}

/// SplitMix64: deterministic integer PRNG step.
#[inline]
pub fn mix64(mut z: u64) -> u64 {
    z = z.wrapping_add(0x9E37_79B9_7F4A_7C15);
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
    z ^ (z >> 31)
}
