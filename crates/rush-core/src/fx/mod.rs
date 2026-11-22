//! Fixed-point maths (doc 19 §3, P1 prompt B). Integers only.
//!
//! **Rounding rule (one helper):** every division in rush-core goes through [`div_round`] (round half away from
//! zero) unless the code says otherwise. The documented exceptions are floor divisions of *time* (hazard schedule
//! sampling, [`floor_div`]) and the C# ports that floor on purpose (`(long)… / …` in `HazardMath`), each marked
//! `// floor (C#: …)` where used. Shifts: `>> 15` / `>> 16` appear only through [`round_shift`], which rounds
//! half away from zero like `HazardMath.RoundShift`.
//!
//! **Q formats:**
//! - Q15: `32768 = 1.0` (unit vectors, sin/cos). Stored in `i32`.
//! - Q16 turns: `65536 = one full turn` (angles; = C#'s `phase16`).
//! - yaw256: `u8`, `256 = one turn`, 0 faces +Z, increasing toward +X (= `Yaw256`).

pub mod trig_table;

use trig_table::SIN_Q15;

/// 1.0 in Q15.
pub const ONE_Q15: i32 = 32768;
/// One full turn in Q16 turns.
pub const TURN_Q16: i32 = 65536;

/// round(a / b), half away from zero. `b != 0` (a zero divisor returns 0 instead of trapping).
#[inline]
pub const fn div_round(a: i64, b: i64) -> i64 {
    if b == 0 {
        return 0;
    }
    let (a, b) = if b < 0 { (-a, -b) } else { (a, b) };
    // (a + ⌊b/2⌋) / b rounds half up for even b; for odd b an exact .5 cannot occur.
    if a >= 0 {
        (a + b / 2) / b
    } else {
        -((-a + b / 2) / b)
    }
}

/// round(a / 2^n), half away from zero (= `HazardMath.RoundShift`). `1 <= n <= 62`.
#[inline]
pub const fn round_shift(a: i64, n: u32) -> i64 {
    let half = 1i64 << (n - 1);
    if a >= 0 {
        (a + half) >> n
    } else {
        -((-a + half) >> n)
    }
}

/// floor(a / b) for `b > 0` (time sampling only; see the module rounding rule).
#[inline]
pub const fn floor_div(a: i64, b: i64) -> i64 {
    if a >= 0 {
        a / b
    } else {
        -((-a + b - 1) / b)
    }
}

/// Non-negative modulo for `m > 0`.
#[inline]
pub const fn modp(a: i64, m: i64) -> i64 {
    let r = a % m;
    if r < 0 {
        r + m
    } else {
        r
    }
}

/// round(a · b / c), half away from zero, saturating to the `i64` range. 128-bit intermediate.
#[inline]
pub fn mul_div(a: i64, b: i64, c: i64) -> i64 {
    if c == 0 {
        return 0;
    }
    let (n, c) = if c < 0 { (-(a as i128 * b as i128), -(c as i128)) } else { (a as i128 * b as i128, c as i128) };
    let q = if n >= 0 { (n + c / 2) / c } else { -((-n + c / 2) / c) };
    if q > i64::MAX as i128 {
        i64::MAX
    } else if q < i64::MIN as i128 {
        i64::MIN
    } else {
        q as i64
    }
}

/// Saturating `i64 → i32`.
#[inline]
pub const fn sat_i32(v: i64) -> i32 {
    if v > i32::MAX as i64 {
        i32::MAX
    } else if v < i32::MIN as i64 {
        i32::MIN
    } else {
        v as i32
    }
}

/// floor(√v), integers only, bit by bit (= `HazardMath.IntSqrt`, identical on every target).
pub const fn isqrt_u64(v: u64) -> u64 {
    if v == 0 {
        return 0;
    }
    let mut x = v;
    let mut r: u64 = 0;
    let mut bit: u64 = 1 << 62;
    while bit > x {
        bit >>= 2;
    }
    while bit != 0 {
        if x >= r + bit {
            x -= r + bit;
            r = (r >> 1) + bit;
        } else {
            r >>= 1;
        }
        bit >>= 2;
    }
    r
}

/// floor(√v) for 128-bit input (Newton iteration, bounded).
pub fn isqrt_u128(v: u128) -> u128 {
    if v < (1u128 << 64) {
        return isqrt_u64(v as u64) as u128;
    }
    let mut x: u128 = 1u128 << ((128 - v.leading_zeros()).div_ceil(2));
    let mut i = 0;
    while i < 80 {
        let y = (x + v / x) >> 1;
        if y >= x {
            break;
        }
        x = y;
        i += 1;
    }
    while x * x > v {
        x -= 1;
    }
    x
}

/// floor(√v) for signed input (≤ 0 → 0).
#[inline]
pub const fn isqrt_i64(v: i64) -> i64 {
    if v <= 0 {
        0
    } else {
        isqrt_u64(v as u64) as i64
    }
}

/// round(√v) (nearest integer) for `v ≥ 0`.
#[inline]
pub const fn isqrt_round(v: i64) -> i64 {
    let r = isqrt_i64(v);
    // (r + 0.5)² = r² + r + 0.25 → round up when v > r² + r.
    if v > r * r + r {
        r + 1
    } else {
        r
    }
}

/// Q15 multiply: round(a · b / 2^15).
#[inline]
pub const fn mul_q15(a: i64, b: i64) -> i64 {
    round_shift(a * b, 15)
}

/// Q16 multiply: round(a · b / 2^16).
#[inline]
pub const fn mul_q16(a: i64, b: i64) -> i64 {
    round_shift(a * b, 16)
}

/// Scale `v` by `permille / 1000`, rounded (= `HazardTuning.ScaleValue` / `MovementConfig.Scale`).
#[inline]
pub const fn permille(v: i64, pm: i64) -> i64 {
    if pm == 1000 {
        v
    } else {
        div_round(v * pm, 1000)
    }
}

/// Length of an integer 3-vector (rounded to the nearest unit).
#[inline]
pub const fn len3(x: i64, y: i64, z: i64) -> i64 {
    isqrt_round(x * x + y * y + z * z)
}

/// Unit vector in Q15 (length 32768 ± 1) along (x, y, z). The zero vector returns zero.
/// Inputs up to ±2^28 (268 km in mm) are exact; larger inputs are pre-shifted.
pub fn normalize_q15(x: i64, y: i64, z: i64) -> [i32; 3] {
    let (mut x, mut y, mut z) = (x, y, z);
    // Keep x² + y² + z² < 2^62.
    while x.abs() > (1 << 29) || y.abs() > (1 << 29) || z.abs() > (1 << 29) {
        x = round_shift(x, 4);
        y = round_shift(y, 4);
        z = round_shift(z, 4);
    }
    let l2 = x * x + y * y + z * z;
    if l2 == 0 {
        return [0, 0, 0];
    }
    // Use an up-scaled length for precision when the vector is short.
    let mut sh = 0u32;
    while l2 << (2 * (sh + 1)) < (1i64 << 60) && sh < 15 {
        sh += 1;
    }
    let l = isqrt_round(l2 << (2 * sh)); // = |v| · 2^sh
    let k = ONE_Q15 as i64;
    [
        div_round((x << sh) * k, l) as i32,
        div_round((y << sh) * k, l) as i32,
        div_round((z << sh) * k, l) as i32,
    ]
}

/// 2D unit vector in Q15 along (x, z).
#[inline]
pub fn normalize2_q15(x: i64, z: i64) -> [i32; 2] {
    let n = normalize_q15(x, 0, z);
    [n[0], n[2]]
}

/// sin(2π · turn / 65536) in Q15: the 1,024-entry table, linearly interpolated (64 sub-steps per entry).
#[inline]
pub const fn sin_q15(turn_q16: i32) -> i32 {
    let p = (turn_q16 & 0xFFFF) as usize;
    let i = p >> 6;
    let f = (p & 63) as i64;
    let a = SIN_Q15[i] as i64;
    let b = SIN_Q15[i + 1] as i64;
    if f == 0 {
        a as i32
    } else {
        (a + round_shift((b - a) * f, 6)) as i32
    }
}

/// cos(2π · turn / 65536) in Q15.
#[inline]
pub const fn cos_q15(turn_q16: i32) -> i32 {
    sin_q15(turn_q16.wrapping_add(16384))
}

/// Degrees → Q16 turns (rounded).
#[inline]
pub const fn deg_to_turn_q16(deg: i64) -> i32 {
    div_round(deg * 65536, 360) as i32
}

/// Integer `atan2` as yaw256, matching `Yaw256.FromVector` exactly (the nearest of the 256 table directions by dot
/// product, first index on ties). A fast octant estimate narrows the candidates; the exact argmax runs over a small
/// window around it. Exhaustively tested against the 256-way brute force for |x|, |z| ≤ 127 and sampled beyond.
pub fn atan2_yaw256(x: i32, z: i32, fallback: u8) -> u8 {
    if x == 0 && z == 0 {
        return fallback;
    }
    // Estimate: angle from +Z toward +X in Q16 turns via the octant + a rational approximation.
    let (ax, az) = ((x as i64).abs(), (z as i64).abs());
    // t = min/max in Q16; atan(t) ≈ t·(π/4) − t(t−1)(0.2447 + 0.0663t) (max error 0.0015 rad) → in turns.
    let (mn, mx) = if ax < az { (ax, az) } else { (az, ax) };
    let t = div_round(mn << 16, mx); // Q16, 0..=65536
    // atan(t)/(2π) in Q16 turns ≈ t·8192/65536 + correction; the window absorbs the residual (< 0.6 yaw steps).
    let corr = div_round(t * (65536 - t), 65536) * 2848 / 65536; // ≈ 0.0435 turns·t(1−t) peak ≈ 712 (Q16)
    let base = div_round(t * 8192, 65536) + corr; // 0..8192 = 0..45°
    // Angle measured from +Z toward +X.
    let a = if ax <= az { base } else { 16384 - base }; // first quadrant: x ≥ 0, z ≥ 0
    let ang = match (x >= 0, z >= 0) {
        (true, true) => a,
        (true, false) => 32768 - a,
        (false, false) => 32768 + a,
        (false, true) => 65536 - a,
    };
    let est = div_round(ang, 256) as i32; // yaw256 estimate
    let mut best = i64::MIN;
    let mut best_i = 0i32;
    // The table argmax with ties to the lowest index: scan the window in ascending *index* order.
    let mut idx = [0i32; 7];
    let mut n = 0;
    let mut k = -3;
    while k <= 3 {
        idx[n] = (est + k) & 255;
        n += 1;
        k += 1;
    }
    // Sort the 7 indices ascending (insertion sort, bounded).
    let mut i = 1;
    while i < 7 {
        let v = idx[i];
        let mut j = i;
        while j > 0 && idx[j - 1] > v {
            idx[j] = idx[j - 1];
            j -= 1;
        }
        idx[j] = v;
        i += 1;
    }
    let mut i = 0;
    while i < 7 {
        let c = idx[i];
        let d = x as i64 * yaw_sin(c as u8) as i64 + z as i64 * yaw_cos(c as u8) as i64;
        if d > best {
            best = d;
            best_i = c;
        }
        i += 1;
    }
    best_i as u8
}

/// `Yaw256`'s literal table: round(sin(2π·i/256) · 32767).
pub const YAW_SIN: [i16; 256] = [
    0, 804, 1608, 2410, 3212, 4011, 4808, 5602, 6393, 7179, 7962, 8739, 9512, 10278, 11039, 11793, 12539, 13279, 14010, 14732,
    15446, 16151, 16846, 17530, 18204, 18868, 19519, 20159, 20787, 21403, 22005, 22594, 23170, 23731, 24279, 24811, 25329,
    25832, 26319, 26790, 27245, 27683, 28105, 28510, 28898, 29268, 29621, 29956, 30273, 30571, 30852, 31113, 31356, 31580,
    31785, 31971, 32137, 32285, 32412, 32521, 32609, 32678, 32728, 32757, 32767, 32757, 32728, 32678, 32609, 32521, 32412,
    32285, 32137, 31971, 31785, 31580, 31356, 31113, 30852, 30571, 30273, 29956, 29621, 29268, 28898, 28510, 28105, 27683,
    27245, 26790, 26319, 25832, 25329, 24811, 24279, 23731, 23170, 22594, 22005, 21403, 20787, 20159, 19519, 18868, 18204,
    17530, 16846, 16151, 15446, 14732, 14010, 13279, 12539, 11793, 11039, 10278, 9512, 8739, 7962, 7179, 6393, 5602, 4808,
    4011, 3212, 2410, 1608, 804, 0, -804, -1608, -2410, -3212, -4011, -4808, -5602, -6393, -7179, -7962, -8739, -9512, -10278,
    -11039, -11793, -12539, -13279, -14010, -14732, -15446, -16151, -16846, -17530, -18204, -18868, -19519, -20159, -20787,
    -21403, -22005, -22594, -23170, -23731, -24279, -24811, -25329, -25832, -26319, -26790, -27245, -27683, -28105, -28510,
    -28898, -29268, -29621, -29956, -30273, -30571, -30852, -31113, -31356, -31580, -31785, -31971, -32137, -32285, -32412,
    -32521, -32609, -32678, -32728, -32757, -32767, -32757, -32728, -32678, -32609, -32521, -32412, -32285, -32137, -31971,
    -31785, -31580, -31356, -31113, -30852, -30571, -30273, -29956, -29621, -29268, -28898, -28510, -28105, -27683, -27245,
    -26790, -26319, -25832, -25329, -24811, -24279, -23731, -23170, -22594, -22005, -21403, -20787, -20159, -19519, -18868,
    -18204, -17530, -16846, -16151, -15446, -14732, -14010, -13279, -12539, -11793, -11039, -10278, -9512, -8739, -7962,
    -7179, -6393, -5602, -4808, -4011, -3212, -2410, -1608, -804,
];

/// Scale of [`YAW_SIN`] (= `Yaw256.Scale`).
pub const YAW_SCALE: i32 = 32767;

/// `Yaw256.SinQ`.
#[inline]
pub const fn yaw_sin(yaw: u8) -> i32 {
    YAW_SIN[yaw as usize] as i32
}

/// `Yaw256.CosQ`.
#[inline]
pub const fn yaw_cos(yaw: u8) -> i32 {
    YAW_SIN[yaw.wrapping_add(64) as usize] as i32
}

/// `Yaw256.Dir` as a Q15 unit vector (x = sin, z = cos), rescaled from the 32767 table: round(v · 32768 / 32767).
#[inline]
pub const fn yaw_dir_q15(yaw: u8) -> [i32; 2] {
    [
        div_round(yaw_sin(yaw) as i64 * 32768, 32767) as i32,
        div_round(yaw_cos(yaw) as i64 * 32768, 32767) as i32,
    ]
}

/// `Yaw256.FromVector`, the 256-way brute force (reference for tests and the C# port).
pub fn yaw_from_vector_brute(x: i32, z: i32, fallback: u8) -> u8 {
    if x == 0 && z == 0 {
        return fallback;
    }
    let mut best = i64::MIN;
    let mut best_i = 0u8;
    let mut i = 0u32;
    while i < 256 {
        let d = x as i64 * yaw_sin(i as u8) as i64 + z as i64 * yaw_cos(i as u8) as i64;
        if d > best {
            best = d;
            best_i = i as u8;
        }
        i += 1;
    }
    best_i
}

/// `Yaw256.FromCamYaw`: camera yaw (u16, 65536 per turn) → facing (u8).
#[inline]
pub const fn yaw_from_cam(cam: u16) -> u8 {
    (cam >> 8) as u8
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn trig_table_hash() {
        use sha2::{Digest, Sha256};
        let src = include_bytes!("trig_table.rs");
        let h = Sha256::digest(src);
        let hex: std::string::String = h.iter().map(|b| format!("{b:02x}")).collect();
        assert_eq!(hex, "0362199ccdb6413a3e78ea97c91a74ff068d50dfba8003a98dc762381ac2f82e");
    }

    #[test]
    fn div_round_half_away() {
        assert_eq!(div_round(5, 2), 3);
        assert_eq!(div_round(-5, 2), -3);
        assert_eq!(div_round(4, 2), 2);
        assert_eq!(div_round(7, 3), 2);
        assert_eq!(div_round(8, 3), 3);
        assert_eq!(div_round(-8, 3), -3);
        assert_eq!(div_round(5, -2), -3);
        assert_eq!(div_round(-5, -2), 3);
        assert_eq!(div_round(0, 7), 0);
        assert_eq!(div_round(1, 3), 0);
        assert_eq!(div_round(3, 6), 1);
        assert_eq!(div_round(-3, 6), -1);
        for a in -300i64..300 {
            for b in [1i64, 2, 3, 4, 7, 10, 30, 1000] {
                // integer oracle: round half away = sign · ⌊(2|a| + b) / 2b⌋
                let r = (2 * a.abs() + b) / (2 * b) * a.signum();
                assert_eq!(div_round(a, b), r, "{a}/{b}");
            }
        }
    }

    #[test]
    fn round_shift_matches_csharp() {
        assert_eq!(round_shift(3, 1), 2);
        assert_eq!(round_shift(-3, 1), -2);
        assert_eq!(round_shift(1 << 30, 30), 1);
        assert_eq!(round_shift((1 << 29) - 1, 30), 0);
    }

    #[test]
    fn isqrt_exact() {
        for v in 0u64..20000 {
            let r = isqrt_u64(v);
            assert!(r * r <= v && (r + 1) * (r + 1) > v);
        }
        assert_eq!(isqrt_u64(u64::MAX), 4294967295);
        assert_eq!(isqrt_round(48_000_000), 6928);
    }

    #[test]
    fn atan2_matches_yaw256_exhaustive() {
        for x in -127..=127 {
            for z in -127..=127 {
                assert_eq!(atan2_yaw256(x, z, 7), yaw_from_vector_brute(x, z, 7), "({x},{z})");
            }
        }
        // Larger vectors (spawn facings use ×1000).
        let mut s = 12345u64;
        for _ in 0..20000 {
            s = s.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
            let x = ((s >> 33) as i32 % 2001) - 1000;
            let z = ((s >> 13) as i32 % 2001) - 1000;
            assert_eq!(atan2_yaw256(x, z, 0), yaw_from_vector_brute(x, z, 0), "({x},{z})");
        }
    }

    #[test]
    fn mul_div_saturates() {
        assert_eq!(mul_div(i64::MAX, 4, 2), i64::MAX);
        assert_eq!(mul_div(7, 3, 2), 11);
        assert_eq!(mul_div(-7, 3, 2), -11);
    }
}
