//! Capsule sweep, raycast and depenetration (the `IMotorWorld` queries) over a posed [`Scene`].

use super::grid::for_each;
use super::{ctr, obb, prism, Bits, Hit, Near, Scene, Seg, MAX_SUBSTEPS, S, SUBSTEP_MM, TOI_BISECT};
use crate::fx::{div_round, isqrt_i64, normalize_q15, ONE_Q15};
use crate::mapblob::{shape, Collider};
use crate::vec::IVec3;

/// Clearance (mm) beyond which a candidate's AABB is skipped in a sweep sample (see `capsule_sweep`).
pub const AABB_SKIP_MM: i64 = 8;

/// Candidates a sweep keeps decoded (see `capsule_sweep`).
const DEC_CACHE: usize = 8;

/// Most candidates one query keeps (ascending collider order; bounded).
pub const MAX_CANDIDATES: usize = 64;

/// A candidate: the collider decoded and posed on demand (only its index is kept, so the candidate list stays
/// small on the 4 KB sBPF stack).
#[derive(Clone, Copy)]
struct Cand {
    i: u32,
    c: Collider,
    off: IVec3,
}

/// Collects the candidate indices of a query box into a bounded array.
fn collect(sc: &Scene, mn: IVec3, mx: IVec3, out: &mut [u16; MAX_CANDIDATES]) -> usize {
    let mut bits: Bits = [0; crate::mapblob::MAX_COLLIDERS / 64];
    sc.gather(mn, mx, &mut bits);
    let mut n = 0;
    for_each(&bits, |i| {
        out[n] = i as u16;
        n += 1;
        n < MAX_CANDIDATES
    });
    n
}

#[inline]
fn cand(sc: &Scene, list: &[u16; MAX_CANDIDATES], k: usize) -> Option<Cand> {
    sc.posed(list[k] as u32).map(|(c, off)| Cand { i: list[k] as u32, c, off })
}

#[inline]
fn reach2(r: i32, n: &Near) -> i64 {
    let k = r as i64 * S + n.core_r;
    k * k
}

/// Feet (sub-mm) after moving `s_sub` sub-mm along the Q15 unit `dir`.
#[inline]
fn feet_at(f: [i64; 3], dir: IVec3, s_sub: i64) -> [i64; 3] {
    [
        f[0] + div_round(dir.x as i64 * s_sub, ONE_Q15 as i64),
        f[1] + div_round(dir.y as i64 * s_sub, ONE_Q15 as i64),
        f[2] + div_round(dir.z as i64 * s_sub, ONE_Q15 as i64),
    ]
}

impl<'m> Scene<'m> {
    /// `IMotorWorld.CapsuleSweep`: sweeps the upright capsule (feet `feet`, radius `r`, height `h`, mm) along the
    /// Q15 unit `dir` up to `max` mm. Returns the first contact (distance travelled, contact point, normal from the
    /// closest features, surface id).
    pub fn capsule_sweep(&self, feet: IVec3, r: i32, h: i32, dir: IVec3, max: i64) -> Option<Hit> {
        if max <= 0 || dir.is_zero() {
            return None;
        }
        self.counters.add(ctr::SWEEPS, 1);
        let end = feet.add(dir.scale_q15(max));
        let pad = r + 2;
        let mn = IVec3::new(feet.x.min(end.x) - pad, feet.y.min(end.y) - 2, feet.z.min(end.z) - pad);
        let mx = IVec3::new(feet.x.max(end.x) + pad, feet.y.max(end.y) + h + 2, feet.z.max(end.z) + pad);
        let mut cands = [0u16; MAX_CANDIDATES];
        let n = collect(self, mn, mx, &mut cands);
        if n == 0 {
            return None;
        }
        let f0 = [feet.x as i64 * S, feet.y as i64 * S, feet.z as i64 * S];
        // Posed AABB of each candidate (P2 0.2): a capsule whose box is more than AABB_SKIP_MM clear of it cannot
        // touch it, so the decode + closest-feature test is skipped (exact: the blob AABBs are conservative).
        let mut ab = [[0i32; 6]; MAX_CANDIDATES];
        let mut k = 0;
        while k < n {
            let (dk, di, amin, amax) = self.map.collider_aabb(cands[k] as u32);
            let off = self.pose_of(dk, di).unwrap_or(IVec3::ZERO);
            ab[k] = [amin.x + off.x, amin.y + off.y, amin.z + off.z, amax.x + off.x, amax.y + off.y, amax.z + off.z];
            k += 1;
        }
        let clear = |k: usize, f: [i64; 3]| -> bool {
            let b = &ab[k];
            let m = AABB_SKIP_MM * S;
            let rr = r as i64 * S;
            f[0] + rr < b[0] as i64 * S - m
                || f[0] - rr > b[3] as i64 * S + m
                || f[2] + rr < b[2] as i64 * S - m
                || f[2] - rr > b[5] as i64 * S + m
                || f[1] + h as i64 * S < b[1] as i64 * S - m
                || f[1] > b[4] as i64 * S + m
        };
        // Decoded candidates, memoised for the first DEC_CACHE (P2 0.2: each sample / bisection step re-decoded them).
        let dec: [core::cell::Cell<Option<Cand>>; DEC_CACHE] = Default::default();
        let get = |k: usize| -> Option<Cand> {
            if k < DEC_CACHE {
                if let Some(c) = dec[k].get() {
                    return Some(c);
                }
                let c = cand(self, &cands, k);
                dec[k].set(c);
                c
            } else {
                cand(self, &cands, k)
            }
        };
        // Ignore colliders already overlapped at the start (PhysX CapsuleCast semantics).
        let s0 = Seg::of(f0, r, h);
        let mut live = [false; MAX_CANDIDATES];
        let mut any = false;
        let mut k = 0;
        while k < n {
            if clear(k, f0) {
                live[k] = true; // gathered candidates are enabled; clear of the start → not overlapped
                any |= live[k];
            } else if let Some(cd) = &get(k) {
                let nr = self.near(&cd.c, cd.off, &s0);
                live[k] = nr.gap2 >= reach2(r, &nr);
                any |= live[k];
            }
            k += 1;
        }
        if !any {
            return None;
        }
        let overlap_at = |s_sub: i64| -> bool {
            let fs = feet_at(f0, dir, s_sub);
            let seg = Seg::of(fs, r, h);
            let mut k = 0;
            while k < n {
                if live[k] && !clear(k, fs) {
                    if let Some(cd) = &get(k) {
                        let nr = self.near(&cd.c, cd.off, &seg);
                        if nr.gap2 < reach2(r, &nr) {
                            return true;
                        }
                    }
                }
                k += 1;
            }
            false
        };
        // Samples every ≤ SUBSTEP_MM.
        let total = max * S;
        let steps = ((max + SUBSTEP_MM - 1) / SUBSTEP_MM).clamp(1, MAX_SUBSTEPS);
        let mut lo = 0i64;
        let mut hi = -1i64;
        let mut i = 1;
        while i <= steps {
            self.counters.add(ctr::SUBSTEPS, 1);
            let s = div_round(total * i, steps);
            if overlap_at(s) {
                hi = s;
                break;
            }
            lo = s;
            i += 1;
        }
        if hi < 0 {
            return None;
        }
        let mut b = 0;
        while b < TOI_BISECT && hi - lo > 1 {
            self.counters.add(ctr::BISECT, 1);
            let mid = (lo + hi) / 2;
            if overlap_at(mid) {
                hi = mid;
            } else {
                lo = mid;
            }
            b += 1;
        }
        // The contact collider: overlapping at `hi`, smallest clearance at `lo` (ties: lowest index).
        let f_hi = feet_at(f0, dir, hi);
        let seg_lo = Seg::of(feet_at(f0, dir, lo), r, h);
        let seg_hi = Seg::of(f_hi, r, h);
        let mut best: Option<(i64, usize, Near)> = None;
        let mut k = 0;
        while k < n {
            if live[k] && !clear(k, f_hi) {
                if let Some(cd) = &get(k) {
                    let nh = self.near(&cd.c, cd.off, &seg_hi);
                    if nh.gap2 < reach2(r, &nh) {
                        let nl = self.near(&cd.c, cd.off, &seg_lo);
                        let clear = isqrt_i64(nl.gap2) - (r as i64 * S + nl.core_r);
                        if best.map_or(true, |bb| clear < bb.0) {
                            best = Some((clear, k, nl));
                        }
                    }
                }
            }
            k += 1;
        }
        let (_, k, nl) = best?;
        let cd = get(k)?;
        let (normal, point) = contact(&nl, dir);
        Some(Hit { distance: div_round(lo, S), point, normal, surface: cd.c.surface, collider: cd.i })
    }

    /// `IMotorWorld.Raycast`: first hit along the Q15 unit `dir` from `origin` within `max` mm.
    pub fn raycast(&self, origin: IVec3, dir: IVec3, max: i64) -> Option<Hit> {
        if max <= 0 || dir.is_zero() {
            return None;
        }
        let end = origin.add(dir.scale_q15(max));
        let mn = IVec3::new(origin.x.min(end.x) - 1, origin.y.min(end.y) - 1, origin.z.min(end.z) - 1);
        let mx = IVec3::new(origin.x.max(end.x) + 1, origin.y.max(end.y) + 1, origin.z.max(end.z) + 1);
        let mut cands = [0u16; MAX_CANDIDATES];
        let n = collect(self, mn, mx, &mut cands);
        let d = dir.to_i64();
        let mut best: Option<(i64, IVec3, u32, i32)> = None;
        let mut k = 0;
        while k < n {
            if let Some(cd) = &cand(self, &cands, k) {
                self.counters.add(ctr::NARROW, 1);
                let o = [(origin.x - cd.off.x) as i64, (origin.y - cd.off.y) as i64, (origin.z - cd.off.z) as i64];
                let hit = match cd.c.kind {
                    shape::OBB => obb::ray(&cd.c, o, d, max),
                    shape::PRISM => prism::ray(&self.map, &cd.c, o, d, max),
                    shape::SPHERE => super::sphere::ray(&cd.c, o, d, max),
                    _ => super::capsule::ray(&cd.c, o, d, max),
                };
                if let Some((t, nrm)) = hit {
                    if best.map_or(true, |bb| t < bb.0) {
                        best = Some((t, IVec3::new(nrm[0], nrm[1], nrm[2]), cd.i, cd.c.surface));
                    }
                }
            }
            k += 1;
        }
        let (t, normal, i, surface) = best?;
        Some(Hit { distance: t, point: origin.add(dir.scale_q15(t)), normal, surface, collider: i })
    }

    /// `IMotorWorld.ComputePenetration`: the summed separating translation (mm) of every overlapped collider, each
    /// pushed out along its closest-feature direction by its depth + 0.5 mm (C#: `dist + 0.0005`), or `None`.
    pub fn penetration(&self, feet: IVec3, r: i32, h: i32) -> Option<IVec3> {
        let mn = IVec3::new(feet.x - r - 2, feet.y - 2, feet.z - r - 2);
        let mx = IVec3::new(feet.x + r + 2, feet.y + h + 2, feet.z + r + 2);
        let mut cands = [0u16; MAX_CANDIDATES];
        let n = collect(self, mn, mx, &mut cands);
        let f0 = [feet.x as i64 * S, feet.y as i64 * S, feet.z as i64 * S];
        let seg = Seg::of(f0, r, h);
        let mut total = [0i64; 3];
        let mut any = false;
        let mut k = 0;
        while k < n {
            if let Some(cd) = &cand(self, &cands, k) {
                let nr = self.near(&cd.c, cd.off, &seg);
                let reach = r as i64 * S + nr.core_r;
                if nr.gap2 < reach * reach {
                    let gap = isqrt_i64(nr.gap2);
                    let (depth, dir) = if gap > 0 {
                        let v = [nr.seg[0] - nr.core[0], nr.seg[1] - nr.core[1], nr.seg[2] - nr.core[2]];
                        (reach - gap, normalize_q15(v[0], v[1], v[2]))
                    } else {
                        let ls = seg.shifted(cd.off);
                        match cd.c.kind {
                            shape::OBB => obb::sat_push(&cd.c, &ls, r as i64 * S),
                            shape::PRISM => prism::sat_push(&self.map, &cd.c, &ls, r as i64 * S),
                            _ => (reach, [0, ONE_Q15, 0]),
                        }
                    };
                    let d = depth + S / 2; // + 0.5 mm
                    total[0] += div_round(dir[0] as i64 * d, ONE_Q15 as i64);
                    total[1] += div_round(dir[1] as i64 * d, ONE_Q15 as i64);
                    total[2] += div_round(dir[2] as i64 * d, ONE_Q15 as i64);
                    any = true;
                }
            }
            k += 1;
        }
        if !any {
            return None;
        }
        // Round up the magnitude so a correction is never lost to rounding (sub-mm → mm, away from zero).
        let up = |v: i64| if v >= 0 { (v + S - 1) / S } else { -((-v + S - 1) / S) };
        Some(IVec3::new(up(total[0]) as i32, up(total[1]) as i32, up(total[2]) as i32))
    }
}

/// Normal (Q15, from the shape toward the capsule axis) and contact point (mm) from closest features.
fn contact(nr: &Near, dir: IVec3) -> (IVec3, IVec3) {
    let v = [nr.seg[0] - nr.core[0], nr.seg[1] - nr.core[1], nr.seg[2] - nr.core[2]];
    let mut n = normalize_q15(v[0], v[1], v[2]);
    if n == [0, 0, 0] {
        n = [-dir.x, -dir.y, -dir.z];
    }
    let p = [
        nr.core[0] + div_round(n[0] as i64 * nr.core_r, ONE_Q15 as i64),
        nr.core[1] + div_round(n[1] as i64 * nr.core_r, ONE_Q15 as i64),
        nr.core[2] + div_round(n[2] as i64 * nr.core_r, ONE_Q15 as i64),
    ];
    (IVec3::new(n[0], n[1], n[2]), IVec3::new(div_round(p[0], S) as i32, div_round(p[1], S) as i32, div_round(p[2], S) as i32))
}
