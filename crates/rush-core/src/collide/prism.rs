//! Vertical convex prism (convex `MeshCollider` from `KitBuilder.Prism`: turn tables, bumpers, hex islands) vs the
//! upright capsule's axis segment; ray vs prism; SAT depth. Exact (no iteration): the capsule axis is vertical.

use super::{Near, Seg, S};
use crate::fx::{div_round, ONE_Q15};
use crate::mapblob::{Collider, MapView};

/// Closest point on edge a→b to q (2D, sub-mm), and its squared distance.
#[inline]
fn seg_point(ax: i64, az: i64, bx: i64, bz: i64, qx: i64, qz: i64) -> (i64, i64, i64) {
    let (ex, ez) = (bx - ax, bz - az);
    let l2 = ex * ex + ez * ez;
    let t_n = (qx - ax) * ex + (qz - az) * ez;
    let (px, pz) = if l2 == 0 || t_n <= 0 {
        (ax, az)
    } else if t_n >= l2 {
        (bx, bz)
    } else {
        (ax + div_round(ex * t_n, l2), az + div_round(ez * t_n, l2))
    };
    let (dx, dz) = (qx - px, qz - pz);
    (px, pz, dx * dx + dz * dz)
}

/// Horizontal closest point of the polygon to (x, z) (sub-mm) and the squared distance (0 inside).
///
/// P2 0.2: one pass, each vertex read once. Outside a convex polygon the closest boundary point lies on an edge
/// whose plane faces the point (n·q > off), so only those edges are measured; the first strictly closer edge in
/// index order wins, as before (the closest point of a convex set is unique).
pub fn near2d(m: &MapView, c: &Collider, x: i64, z: i64) -> (i64, i64, i64) {
    let n = c.prism_n.min(crate::mapblob::MAX_PRISM_VERTS as u32);
    if n == 0 {
        return (x, z, 0);
    }
    let mut best = (0i64, 0i64, i64::MAX);
    let mut inside = true;
    let first = m.prism_vertex(c, 0);
    let mut cur = first;
    let mut k = 0;
    while k < n {
        let next = if k + 1 == n { first } else { m.prism_vertex(c, k + 1) };
        let (ax, az, nx, nz, off) = cur;
        // n·q − off with q in sub-mm: (n·q_sub)/S − off
        if (nx as i64 * x + nz as i64 * z) - off * S > 0 {
            inside = false;
            let p = seg_point(ax as i64 * S, az as i64 * S, next.0 as i64 * S, next.1 as i64 * S, x, z);
            if p.2 < best.2 {
                best = p;
            }
        }
        cur = next;
        k += 1;
    }
    if inside {
        return (x, z, 0);
    }
    best
}

pub fn near(m: &MapView, c: &Collider, s: &Seg) -> Near {
    near_from(c, s, near2d(m, c, s.x, s.z))
}

/// [`near`] given the horizontal closest point (`near2d`'s result).
#[inline]
pub fn near_from(c: &Collider, s: &Seg, h: (i64, i64, i64)) -> Near {
    let (qx, qz, dh2) = h;
    let yb = c.y0 as i64 * S;
    let yt = c.y1 as i64 * S;
    let (sy0, sy1) = (s.y0, s.y1);
    let (seg_y, sh_y) = if sy0 > yt {
        (sy0, yt)
    } else if sy1 < yb {
        (sy1, yb)
    } else {
        let y = (sy0.max(yb) + sy1.min(yt)) / 2;
        (y, y)
    };
    let ev = seg_y - sh_y;
    Near { gap2: dh2 + ev * ev, core: [qx, sh_y, qz], seg: [s.x, seg_y, s.z], core_r: 0 }
}

/// Ray vs prism (Cyrus–Beck over the edge planes and the caps). Distance mm and the entry normal (Q15).
pub fn ray(m: &MapView, c: &Collider, o: [i64; 3], dir: [i64; 3], max: i64) -> Option<(i64, [i32; 3])> {
    // Planes: outward normal n, offset k (n·p ≤ k inside). Times as rationals (num, den > 0).
    let (mut tin_n, mut tin_d) = (0i64, 1i64);
    let (mut tout_n, mut tout_d) = (max, 1i64);
    let mut nrm = [0i32; 3];
    let mut entered = false;
    let n = c.prism_n.min(crate::mapblob::MAX_PRISM_VERTS as u32);
    let mut k = 0u32;
    while k < n + 2 {
        // edge plane k, or bottom (n), top (n + 1)
        let (pn, off): ([i32; 3], i64) = if k < n {
            let (_, _, nx, nz, off) = m.prism_vertex(c, k);
            ([nx, 0, nz], off)
        } else if k == n {
            ([0, -ONE_Q15, 0], -(c.y0 as i64) * ONE_Q15 as i64)
        } else {
            ([0, ONE_Q15, 0], c.y1 as i64 * ONE_Q15 as i64)
        };
        let no = pn[0] as i64 * o[0] + pn[1] as i64 * o[1] + pn[2] as i64 * o[2] - off; // mm·Q15 (> 0 outside)
        let nd = pn[0] as i64 * dir[0] + pn[1] as i64 * dir[1] + pn[2] as i64 * dir[2]; // Q30
        if nd == 0 {
            if no > 0 {
                return None;
            }
        } else {
            // t = −no · 2^15 / nd
            let (mut tn, mut td) = (-no << 15, nd);
            if td < 0 {
                td = -td;
                tn = -tn;
            }
            if nd < 0 {
                // entering
                if (tn as i128) * (tin_d as i128) > (tin_n as i128) * (td as i128) {
                    tin_n = tn;
                    tin_d = td;
                    nrm = pn;
                    entered = true;
                }
            } else if (tn as i128) * (tout_d as i128) < (tout_n as i128) * (td as i128) {
                tout_n = tn;
                tout_d = td;
            }
            if (tin_n as i128) * (tout_d as i128) > (tout_n as i128) * (tin_d as i128) {
                return None;
            }
        }
        k += 1;
    }
    if !entered {
        return None;
    }
    let t = div_round(tin_n, tin_d);
    if t < 0 || t > max {
        return None;
    }
    Some((t, nrm))
}

/// SAT push for the deep case (axis segment inside the prism): the smallest of the edge planes and the caps.
pub fn sat_push(m: &MapView, c: &Collider, s: &Seg, r: i64) -> (i64, [i32; 3]) {
    let n = c.prism_n.min(crate::mapblob::MAX_PRISM_VERTS as u32);
    let mut best = i64::MAX;
    let mut dir = [0, ONE_Q15, 0];
    let mut k = 0;
    while k < n {
        let (_, _, nx, nz, off) = m.prism_vertex(c, k);
        // depth along n: off − (n·q − r): distance from q to the plane inside, plus r
        let d = div_round(off * S - (nx as i64 * s.x + nz as i64 * s.z), ONE_Q15 as i64) + r;
        if d < best {
            best = d;
            dir = [nx, 0, nz];
        }
        k += 1;
    }
    let up = c.y1 as i64 * S - s.y0 + r;
    if up < best {
        best = up;
        dir = [0, ONE_Q15, 0];
    }
    let dn = s.y1 + r - c.y0 as i64 * S;
    if dn < best {
        best = dn;
        dir = [0, -ONE_Q15, 0];
    }
    (best.max(0), dir)
}
