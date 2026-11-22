//! Sphere collider vs the upright capsule's axis segment; ray vs sphere.

use super::{Near, Seg, S};
use crate::fx::{isqrt_i64, ONE_Q15};
use crate::mapblob::Collider;

pub fn near(c: &Collider, s: &Seg) -> Near {
    let cx = c.center.x as i64 * S;
    let cy = c.center.y as i64 * S;
    let cz = c.center.z as i64 * S;
    let y = cy.clamp(s.y0, s.y1);
    let (dx, dy, dz) = (s.x - cx, y - cy, s.z - cz);
    Near { gap2: dx * dx + dy * dy + dz * dz, core: [cx, cy, cz], seg: [s.x, y, s.z], core_r: c.half.x as i64 * S }
}

/// Ray vs sphere: distance (mm) and outward normal at the entry.
pub fn ray(c: &Collider, o: [i64; 3], dir: [i64; 3], max: i64) -> Option<(i64, [i32; 3])> {
    // |o + d·t − c|² = R², d unit (Q15): t = b − √(b² − (|m|² − R²)), b = −m·d.
    let m = [o[0] - c.center.x as i64, o[1] - c.center.y as i64, o[2] - c.center.z as i64];
    let r = c.half.x as i64;
    let b = -crate::fx::div_round(m[0] * dir[0] + m[1] * dir[1] + m[2] * dir[2], ONE_Q15 as i64);
    let cc = m[0] * m[0] + m[1] * m[1] + m[2] * m[2] - r * r;
    if cc <= 0 {
        return None; // origin inside
    }
    let disc = b * b - cc;
    if disc < 0 || b < 0 {
        return None;
    }
    let t = b - isqrt_i64(disc);
    if t < 0 || t > max {
        return None;
    }
    let p = [
        m[0] + crate::fx::div_round(dir[0] * t, ONE_Q15 as i64),
        m[1] + crate::fx::div_round(dir[1] * t, ONE_Q15 as i64),
        m[2] + crate::fx::div_round(dir[2] * t, ONE_Q15 as i64),
    ];
    let n = crate::fx::normalize_q15(p[0], p[1], p[2]);
    Some((t, n))
}
