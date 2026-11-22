//! Oriented box (`BoxCollider`) vs the upright capsule's axis segment; ray vs box; SAT depth.
//!
//! All distance work is in sub-millimetres (`S` = 1/16 mm) so rounding stays well below the 10 mm skin.

use super::{Near, Seg, S};
use crate::fx::{div_round, ONE_Q15};
use crate::mapblob::Collider;

#[inline]
fn half(c: &Collider, i: usize) -> i64 {
    [c.half.x, c.half.y, c.half.z][i] as i64
}

/// Box-local coordinate `i` of point `p` (sub-mm): dot(p − c, axis_i) / 2^15, all in sub-mm.
#[inline]
fn local(c: &Collider, p: [i64; 3], i: usize) -> i64 {
    let a = c.axes[i];
    let d = [p[0] - c.center.x as i64 * S, p[1] - c.center.y as i64 * S, p[2] - c.center.z as i64 * S];
    div_round(d[0] * a.x as i64 + d[1] * a.y as i64 + d[2] * a.z as i64, ONE_Q15 as i64)
}

/// Squared distance from point `p` (sub-mm) to the box, and the closest box point.
fn point_dist2(c: &Collider, p: [i64; 3]) -> (i64, [i64; 3]) {
    let mut d2 = 0i64;
    let mut q = [c.center.x as i64 * S, c.center.y as i64 * S, c.center.z as i64 * S];
    let mut i = 0;
    while i < 3 {
        let l = local(c, p, i);
        let h = half(c, i) * S;
        let cl = l.clamp(-h, h);
        let e = l - cl;
        d2 += e * e;
        let a = c.axes[i];
        q[0] += div_round(a.x as i64 * cl, ONE_Q15 as i64);
        q[1] += div_round(a.y as i64 * cl, ONE_Q15 as i64);
        q[2] += div_round(a.z as i64 * cl, ONE_Q15 as i64);
        i += 1;
    }
    (d2, q)
}

/// Closest features between the vertical segment and the box.
pub fn near(c: &Collider, s: &Seg) -> Near {
    if c.flags & 1 != 0 {
        return near_yaw(c, s);
    }
    // General box: d²(y) along the vertical segment is convex; integer ternary search (bounded), then the best of
    // the last few samples. `y` in whole mm.
    let f = |y: i64| -> (i64, [i64; 3]) { point_dist2(c, [s.x, y, s.z]) };
    let (mut lo, mut hi) = (s.y0, s.y1);
    let mut it = 0;
    while hi - lo > 2 && it < 40 {
        let m1 = lo + (hi - lo) / 3;
        let m2 = hi - (hi - lo) / 3;
        if f(m1).0 <= f(m2).0 {
            hi = m2;
        } else {
            lo = m1;
        }
        it += 1;
    }
    let mut best = f(lo);
    let mut by = lo;
    let mut y = lo + 1;
    while y <= hi {
        let v = f(y);
        if v.0 < best.0 {
            best = v;
            by = y;
        }
        y += 1;
    }
    Near { gap2: best.0, core: best.1, seg: [s.x, by, s.z], core_r: 0 }
}

/// Yaw-only box (local y = world +Y): exact. Horizontal rectangle distance + vertical interval gap.
fn near_yaw(c: &Collider, s: &Seg) -> Near {
    let cx = c.center.x as i64 * S;
    let cz = c.center.z as i64 * S;
    let (ax, az) = (c.axes[0], c.axes[2]);
    let dx = s.x - cx;
    let dz = s.z - cz;
    let lx = div_round(dx * ax.x as i64 + dz * ax.z as i64, ONE_Q15 as i64);
    let lz = div_round(dx * az.x as i64 + dz * az.z as i64, ONE_Q15 as i64);
    let hx = half(c, 0) * S;
    let hz = half(c, 2) * S;
    let clx = lx.clamp(-hx, hx);
    let clz = lz.clamp(-hz, hz);
    let ex = lx - clx;
    let ez = lz - clz;
    let qx = cx + div_round(ax.x as i64 * clx + az.x as i64 * clz, ONE_Q15 as i64);
    let qz = cz + div_round(ax.z as i64 * clx + az.z as i64 * clz, ONE_Q15 as i64);
    let yb = (c.center.y as i64 - c.half.y as i64) * S;
    let yt = (c.center.y as i64 + c.half.y as i64) * S;
    let (sy0, sy1) = (s.y0, s.y1);
    let (seg_y, box_y) = if sy0 > yt {
        (sy0, yt)
    } else if sy1 < yb {
        (sy1, yb)
    } else {
        // Overlapping heights: any common y; take the middle of the overlap (horizontal normal).
        let y = (sy0.max(yb) + sy1.min(yt)) / 2;
        (y, y)
    };
    let ev = seg_y - box_y;
    // Inside the rectangle and vertically overlapping: intersecting.
    let qx = if ex == 0 && ez == 0 { s.x } else { qx };
    let qz = if ex == 0 && ez == 0 { s.z } else { qz };
    Near { gap2: ex * ex + ez * ez + ev * ev, core: [qx, box_y, qz], seg: [s.x, seg_y, s.z], core_r: 0 }
}

/// Ray (origin and Q15 direction, mm) vs box: (distance mm, outward normal Q15) of the entry, if within `max`.
pub fn ray(c: &Collider, o: [i64; 3], dir: [i64; 3], max: i64) -> Option<(i64, [i32; 3])> {
    // Slab test in box-local coordinates. Times as rationals num/den with den > 0, compared by cross-multiplying.
    let mut t_in_n: i64 = 0; // t_in = 0 / 1
    let mut t_in_d: i64 = 1;
    let mut t_out_n: i64 = max;
    let mut t_out_d: i64 = 1;
    let mut n_axis = 3usize;
    let mut i = 0;
    while i < 3 {
        let a = c.axes[i];
        let lo = (o[0] - c.center.x as i64) * a.x as i64 + (o[1] - c.center.y as i64) * a.y as i64 + (o[2] - c.center.z as i64) * a.z as i64; // mm·Q15
        let ld = dir[0] * a.x as i64 + dir[1] * a.y as i64 + dir[2] * a.z as i64; // Q30
        let h = half(c, i) * ONE_Q15 as i64; // mm·Q15
        if ld == 0 {
            if lo < -h || lo > h {
                return None;
            }
        } else {
            // t (mm) = (±h − lo) · 2^15 / ld
            let (mut n1, mut n2) = ((-h - lo) << 15, (h - lo) << 15);
            let mut d = ld;
            if d < 0 {
                d = -d;
                n1 = -n1;
                n2 = -n2;
            }
            // entering = min, leaving = max (in time order)
            let (en, ex) = if n1 <= n2 { (n1, n2) } else { (n2, n1) };
            // t_in = max(t_in, en/d)
            if (en as i128) * (t_in_d as i128) > (t_in_n as i128) * (d as i128) {
                t_in_n = en;
                t_in_d = d;
                n_axis = i;
            }
            if (ex as i128) * (t_out_d as i128) < (t_out_n as i128) * (d as i128) {
                t_out_n = ex;
                t_out_d = d;
            }
            if (t_in_n as i128) * (t_out_d as i128) > (t_out_n as i128) * (t_in_d as i128) {
                return None;
            }
        }
        i += 1;
    }
    if n_axis == 3 {
        return None; // origin inside the box (PhysX raycasts do not report it either)
    }
    let t = div_round(t_in_n, t_in_d);
    if t < 0 || t > max {
        return None;
    }
    // The entry face normal: −sign(ld) · axis.
    let a = c.axes[n_axis];
    let ld = dir[0] * a.x as i64 + dir[1] * a.y as i64 + dir[2] * a.z as i64;
    let sgn = if ld > 0 { -1 } else { 1 };
    Some((t, [a.x * sgn, a.y * sgn, a.z * sgn]))
}

/// SAT depth of the capsule (segment + radius `r`, sub-mm) inside the box along the box axes: the smallest push
/// (sub-mm) and its direction (Q15). Used only when the axis segment intersects the box (deep case).
pub fn sat_push(c: &Collider, s: &Seg, r: i64) -> (i64, [i32; 3]) {
    let mut best = i64::MAX;
    let mut dir = [0, ONE_Q15, 0];
    let mid = [s.x, (s.y0 + s.y1) / 2, s.z];
    let hs = (s.y1 - s.y0) / 2;
    let mut i = 0;
    while i < 3 {
        let a = c.axes[i];
        let cproj = local(c, mid, i);
        let ext = div_round(hs * (a.y as i64).abs(), ONE_Q15 as i64) + r;
        let h = half(c, i) * S;
        // push along +axis: box max − capsule min; along −axis: capsule max − box min.
        let up = h - (cproj - ext);
        let dn = (cproj + ext) + h;
        if up < best {
            best = up;
            dir = [a.x, a.y, a.z];
        }
        if dn < best {
            best = dn;
            dir = [-a.x, -a.y, -a.z];
        }
        i += 1;
    }
    (best.max(0), dir)
}
