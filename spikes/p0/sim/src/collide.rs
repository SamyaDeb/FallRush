//! Narrowphase: distance from the player's capsule axis (an upright segment) to each primitive.
//!
//! The player capsule is always upright, so its axis is a vertical segment (x, z, ya..yb).
//! Every query returns the signed distance from that segment to the primitive's surface
//! (negative = the segment is inside the primitive) and the contact normal (Q12, pointing from
//! the primitive towards the player). A capsule of radius R touches when `dist < R`.
//!
//! Shortcuts vs the Unity motor (noted in the proof):
//! - OBBs / capsules / prisms are yaw-only (no pitch/roll). Tilted ramps are not modelled.
//! - Sweeps are found by bisection on the time of impact over these exact distances
//!   (conservative, 6 halvings = 1/64 of the motion) instead of analytic capsule casts.
use crate::fx::{clamp, isqrt, len3, norm3, ONE};
use crate::map::{Collider, Map, Vert, K_BOX, K_CAPSULE, K_PRISM, K_SPHERE};

#[derive(Clone, Copy, Debug)]
pub struct Seg {
    pub x: i32,
    pub z: i32,
    pub ya: i32,
    pub yb: i32,
}

#[derive(Clone, Copy, Debug)]
pub struct Probe {
    pub dist: i32,
    pub n: [i32; 3],
}

pub const FAR: Probe = Probe { dist: i32::MAX, n: [0, ONE, 0] };

/// Unnormalised probe result: the vector from the closest point on the primitive core to the
/// segment (`e`, mm), the primitive's own radius (`rad`, spheres/capsules) — or, when the segment
/// is inside a box/prism, the exit depth + normal. `touch()` is sqrt-free; `finish()` normalises.
#[derive(Clone, Copy, Debug)]
pub struct Raw {
    pub e: [i64; 3],
    pub rad: i32,
    pub inside: i32, // > 0: penetration depth (box/prism interior), normal in `n_in`
    pub n_in: [i32; 3],
}

pub const RAW_FAR: Raw = Raw { e: [1 << 30, 0, 0], rad: 0, inside: 0, n_in: [0, ONE, 0] };

impl Raw {
    /// true when an upright capsule of contact radius `rc` overlaps (no sqrt).
    #[inline]
    pub fn touch(&self, rc: i32) -> bool {
        if self.inside > 0 {
            return true;
        }
        let l = (rc + self.rad) as i64;
        self.e[0] * self.e[0] + self.e[1] * self.e[1] + self.e[2] * self.e[2] < l * l
    }
    #[inline]
    pub fn finish(&self) -> Probe {
        if self.inside > 0 {
            return Probe { dist: -self.inside, n: self.n_in };
        }
        let l = len3(self.e[0], self.e[1], self.e[2]);
        if l == 0 {
            return Probe { dist: -self.rad, n: [0, ONE, 0] };
        }
        Probe { dist: l - self.rad, n: norm3(self.e[0], self.e[1], self.e[2], l) }
    }
}

#[inline]
fn raw(e0: i64, e1: i64, e2: i64, rad: i32) -> Raw {
    Raw { e: [e0, e1, e2], rad, inside: 0, n_in: [0, ONE, 0] }
}

/// Vertical interval [ya,yb] vs [lo,hi]: (gap along y signed toward the segment, point y on seg)
#[inline]
fn y_gap(ya: i32, yb: i32, lo: i32, hi: i32) -> i32 {
    if yb < lo {
        yb - lo // negative: segment below
    } else if ya > hi {
        ya - hi // positive: segment above
    } else {
        0
    }
}

#[inline]
fn rot_back(co: i64, si: i64, x: i64, z: i64) -> (i64, i64) {
    // local -> world: dx = lx*c - lz*s ; dz = lx*s + lz*c
    ((x * co - z * si) >> 15, (x * si + z * co) >> 15)
}

pub fn probe_box(c: &Collider, s: &Seg) -> Raw {
    let dx = (s.x - c.c[0]) as i64;
    let dz = (s.z - c.c[2]) as i64;
    let co = c.cos as i64;
    let si = c.sin as i64;
    let lx = ((dx * co + dz * si) >> 15) as i32;
    let lz = ((-dx * si + dz * co) >> 15) as i32;
    let (hx, hy, hz) = (c.h[0], c.h[1], c.h[2]);
    let lo = c.c[1] - hy;
    let hi = c.c[1] + hy;
    let qx = clamp(lx, -hx, hx);
    let qz = clamp(lz, -hz, hz);
    let ex = (lx - qx) as i64;
    let ez = (lz - qz) as i64;
    let ey = y_gap(s.ya, s.yb, lo, hi) as i64;
    if ex == 0 && ez == 0 && ey == 0 {
        // Inside: exit along the shallowest face.
        let dxp = hx - lx.abs();
        let dzp = hz - lz.abs();
        let dup = hi - s.ya; // push up
        let ddn = s.yb - lo; // push down
        let mut best = dxp;
        let mut n = [if lx >= 0 { ONE } else { -ONE }, 0, 0];
        if dzp < best {
            best = dzp;
            n = [0, 0, if lz >= 0 { ONE } else { -ONE }];
        }
        if dup < best {
            best = dup;
            n = [0, ONE, 0];
        }
        if ddn < best {
            best = ddn;
            n = [0, -ONE, 0];
        }
        let (nx, nz) = rot_back(co, si, n[0] as i64, n[2] as i64);
        return Raw { e: [0, 0, 0], rad: 0, inside: best.max(1), n_in: [nx as i32, n[1], nz as i32] };
    }
    let (wx, wz) = if ex == 0 && ez == 0 { (0, 0) } else { rot_back(co, si, ex, ez) };
    raw(wx, ey, wz, 0)
}

pub fn probe_prism(c: &Collider, verts: &[Vert], s: &Seg) -> Raw {
    let vs = &verts[c.vert_off as usize..c.vert_off as usize + c.nverts as usize];
    let lo = c.c[1] - c.h[1];
    let hi = c.c[1] + c.h[1];
    let mut max_sd = i32::MIN;
    let mut max_i = 0usize;
    for (i, v) in vs.iter().enumerate() {
        let sd = (((s.x - v.x) as i64 * v.nx as i64 + (s.z - v.z) as i64 * v.nz as i64) >> 12) as i32;
        if sd > max_sd {
            max_sd = sd;
            max_i = i;
        }
    }
    let ey = y_gap(s.ya, s.yb, lo, hi) as i64;
    if max_sd <= 0 {
        if ey != 0 {
            return raw(0, ey, 0, 0);
        }
        let dside = -max_sd;
        let dup = hi - s.ya;
        let ddn = s.yb - lo;
        let v = &vs[max_i];
        let mut best = dside;
        let mut n = [v.nx, 0, v.nz];
        if dup < best {
            best = dup;
            n = [0, ONE, 0];
        }
        if ddn < best {
            best = ddn;
            n = [0, -ONE, 0];
        }
        return Raw { e: [0, 0, 0], rad: 0, inside: best.max(1), n_in: n };
    }
    // Outside: for a convex polygon the closest boundary point lies on an edge adjacent to the
    // max-separation edge's neighbourhood; check every edge with positive separation (cheap test first).
    let mut best_d2 = i64::MAX;
    let mut bx = 0i64;
    let mut bz = 0i64;
    let k = vs.len();
    for i in 0..k {
        let a = &vs[i];
        let sd = ((s.x - a.x) as i64 * a.nx as i64 + (s.z - a.z) as i64 * a.nz as i64) >> 12;
        if sd <= 0 {
            continue; // the closest point is never on an edge the point is inside of
        }
        let b = &vs[if i + 1 == k { 0 } else { i + 1 }];
        let ex = (b.x - a.x) as i64;
        let ez = (b.z - a.z) as i64;
        let px = (s.x - a.x) as i64;
        let pz = (s.z - a.z) as i64;
        let ll = ex * ex + ez * ez;
        let t = (px * ex + pz * ez).clamp(0, ll);
        let qx = if ll > 0 { a.x as i64 + ex * t / ll } else { a.x as i64 };
        let qz = if ll > 0 { a.z as i64 + ez * t / ll } else { a.z as i64 };
        let dx = s.x as i64 - qx;
        let dz = s.z as i64 - qz;
        let d2 = dx * dx + dz * dz;
        if d2 < best_d2 {
            best_d2 = d2;
            bx = dx;
            bz = dz;
        }
    }
    raw(bx, ey, bz, 0)
}

pub fn probe_sphere(cx: i32, cy: i32, cz: i32, r: i32, s: &Seg) -> Raw {
    let py = clamp(cy, s.ya, s.yb);
    raw((s.x - cx) as i64, (py - cy) as i64, (s.z - cz) as i64, r)
}

/// Horizontal capsule from (ax,y,az) to (bx,y,bz), radius r.
pub fn probe_hcapsule(ax: i32, az: i32, bx: i32, bz: i32, y: i32, r: i32, s: &Seg) -> Raw {
    let ex = (bx - ax) as i64;
    let ez = (bz - az) as i64;
    let px = (s.x - ax) as i64;
    let pz = (s.z - az) as i64;
    let ll = ex * ex + ez * ez;
    let t = (px * ex + pz * ez).clamp(0, ll.max(1));
    let qx = ax as i64 + if ll > 0 { ex * t / ll } else { 0 };
    let qz = az as i64 + if ll > 0 { ez * t / ll } else { 0 };
    let py = clamp(y, s.ya, s.yb);
    raw(s.x as i64 - qx, (py - y) as i64, s.z as i64 - qz, r)
}

#[inline]
pub fn probe_static(m: &Map, ci: usize, s: &Seg) -> Raw {
    let c = &m.cols[ci];
    match c.kind {
        K_BOX => probe_box(c, s),
        K_PRISM => probe_prism(c, m.verts, s),
        K_SPHERE => probe_sphere(c.c[0], c.c[1], c.c[2], c.h[0], s),
        K_CAPSULE => {
            let hl = c.h[0] as i64;
            let dx = ((c.cos as i64 * hl) >> 15) as i32;
            let dz = ((c.sin as i64 * hl) >> 15) as i32;
            probe_hcapsule(c.c[0] - dx, c.c[2] - dz, c.c[0] + dx, c.c[2] + dz, c.c[1], c.h[1], s)
        }
        _ => RAW_FAR,
    }
}

/// Height of the top surface of a static collider directly under (x,z), if any (downward ray).
pub fn top_under(m: &Map, ci: usize, x: i32, z: i32) -> Option<i32> {
    let c = &m.cols[ci];
    match c.kind {
        K_BOX => {
            let dx = (x - c.c[0]) as i64;
            let dz = (z - c.c[2]) as i64;
            let co = c.cos as i64;
            let si = c.sin as i64;
            let lx = ((dx * co + dz * si) >> 15) as i32;
            let lz = ((-dx * si + dz * co) >> 15) as i32;
            if lx.abs() <= c.h[0] && lz.abs() <= c.h[2] {
                Some(c.c[1] + c.h[1])
            } else {
                None
            }
        }
        K_PRISM => {
            let vs = &m.verts[c.vert_off as usize..c.vert_off as usize + c.nverts as usize];
            for v in vs {
                let sd = ((x - v.x) as i64 * v.nx as i64 + (z - v.z) as i64 * v.nz as i64) >> 12;
                if sd > 0 {
                    return None;
                }
            }
            Some(c.c[1] + c.h[1])
        }
        K_SPHERE => {
            let dx = (x - c.c[0]) as i64;
            let dz = (z - c.c[2]) as i64;
            let r = c.h[0] as i64;
            let d2 = dx * dx + dz * dz;
            if d2 <= r * r {
                Some(c.c[1] + isqrt((r * r - d2) as u64) as i32)
            } else {
                None
            }
        }
        _ => None,
    }
}
