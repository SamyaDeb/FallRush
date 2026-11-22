//! Vertical capsule collider (players are capsules too; static capsule colliders are rare) vs the upright capsule's
//! axis segment; ray vs a vertical capsule (approximated by its bounding cylinder + caps as spheres).

use super::{Near, Seg, S};
use crate::mapblob::Collider;

/// Core segment: `center` (bottom point a) to `axes[0]` (top point b), radius `half.x`.
pub fn near(c: &Collider, s: &Seg) -> Near {
    let ax = c.center.x as i64 * S;
    let az = c.center.z as i64 * S;
    let (ya, yb) = (c.center.y as i64 * S, c.axes[0].y as i64 * S);
    let (sy0, sy1) = (s.y0, s.y1);
    let (seg_y, core_y) = if sy0 > yb {
        (sy0, yb)
    } else if sy1 < ya {
        (sy1, ya)
    } else {
        let y = (sy0.max(ya) + sy1.min(yb)) / 2;
        (y, y)
    };
    let (dx, dz, dy) = (s.x - ax, s.z - az, seg_y - core_y);
    Near { gap2: dx * dx + dz * dz + dy * dy, core: [ax, core_y, az], seg: [s.x, seg_y, s.z], core_r: c.half.x as i64 * S }
}

/// Ray vs vertical capsule: the nearer of the two end spheres and the side cylinder.
pub fn ray(c: &Collider, o: [i64; 3], dir: [i64; 3], max: i64) -> Option<(i64, [i32; 3])> {
    let mut best: Option<(i64, [i32; 3])> = None;
    for end in [c.center, c.axes[0]] {
        let mut sc = *c;
        sc.center = end;
        if let Some(h) = super::sphere::ray(&sc, o, dir, max) {
            if best.map_or(true, |b| h.0 < b.0) {
                best = Some(h);
            }
        }
    }
    // Side: |m + d·t/2^15|² = r² in x–z → A t² + 2B t + C = 0 with A = |d|² (Q30), B = (m·d)·2^15, C = (|m|² − r²)·2^30.
    let r = c.half.x as i64;
    let m = [o[0] - c.center.x as i64, o[2] - c.center.z as i64];
    let d = [dir[0], dir[2]];
    let a = (d[0] * d[0] + d[1] * d[1]) as i128;
    let bq = ((m[0] * d[0] + m[1] * d[1]) as i128) << 15;
    let cq = ((m[0] * m[0] + m[1] * m[1] - r * r) as i128) << 30;
    if a > 0 && cq > 0 && bq < 0 {
        let disc = bq * bq - a * cq;
        if disc >= 0 {
            let t = ((-bq - crate::fx::isqrt_u128(disc as u128) as i128) / a) as i64;
            if t >= 0 && t <= max {
                let y = o[1] + crate::fx::div_round(dir[1] * t, crate::fx::ONE_Q15 as i64);
                if y >= c.center.y as i64 && y <= c.axes[0].y as i64 && best.map_or(true, |bb| t < bb.0) {
                    let px = m[0] + crate::fx::div_round(d[0] * t, crate::fx::ONE_Q15 as i64);
                    let pz = m[1] + crate::fx::div_round(d[1] * t, crate::fx::ONE_Q15 as i64);
                    best = Some((t, crate::fx::normalize_q15(px, 0, pz)));
                }
            }
        }
    }
    best
}
