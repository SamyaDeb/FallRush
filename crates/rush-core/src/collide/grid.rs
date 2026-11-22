//! Uniform-grid broadphase (built offline into the blob, P1 prompt C.4). Cells are `cell_mm` cubes; each non-empty
//! cell lists its static colliders in ascending order. Moving colliders (platforms, doors) and very large static
//! ones are in the GLOBAL list, tested by every query. Candidates come back as a bitset, so iteration is in ascending
//! collider order (determinism) and duplicates vanish.

use super::{Bits, Scene};
use crate::fx::floor_div;
use crate::vec::IVec3;

/// Most grid cells one query may visit; a bigger query tests every collider instead (still bounded by the count).
pub const MAX_QUERY_CELLS: i64 = 64;

#[inline]
fn overlaps(amin: IVec3, amax: IVec3, bmin: IVec3, bmax: IVec3) -> bool {
    amin.x <= bmax.x && amax.x >= bmin.x && amin.y <= bmax.y && amax.y >= bmin.y && amin.z <= bmax.z && amax.z >= bmin.z
}

impl<'m> Scene<'m> {
    /// Colliders whose (posed, enabled) AABB overlaps `[mn, mx]` (mm), into `out` (cleared first).
    ///
    /// P2 0.2: the raw grid candidates come from a memo of an inflated box ([`BP_INFLATE_MM`]) when the query lies
    /// inside it. The raw set of a containing box is a superset of the query's own, and the fine filter keeps exactly
    /// the colliders whose posed AABB overlaps the query, so the result is the same set either way.
    pub fn gather(&self, mn: IVec3, mx: IVec3, out: &mut Bits) {
        *out = [0; crate::mapblob::MAX_COLLIDERS / 64];
        let raw: Bits = {
            let hit = {
                let c = self.bp_cache.borrow();
                c.valid && c.mn.x <= mn.x && c.mn.y <= mn.y && c.mn.z <= mn.z && c.mx.x >= mx.x && c.mx.y >= mx.y && c.mx.z >= mx.z
            };
            if hit {
                self.bp_cache.borrow().raw
            } else {
                let d = IVec3::new(BP_INFLATE_MM, BP_INFLATE_MM, BP_INFLATE_MM);
                let (imn, imx) = (mn.sub(d), mx.add(d));
                let mut r: Bits = [0; crate::mapblob::MAX_COLLIDERS / 64];
                self.raw_candidates(imn, imx, &mut r);
                let mut c = self.bp_cache.borrow_mut();
                c.valid = true;
                c.mn = imn;
                c.mx = imx;
                c.raw = r;
                r
            }
        };
        // Fine filter: posed AABB overlap and enabled.
        let mut w = 0;
        while w < raw.len() {
            let mut bits = raw[w];
            while bits != 0 {
                let b = bits.trailing_zeros();
                bits &= bits - 1;
                let i = (w as u32) * 64 + b;
                let (dk, dx, amin, amax) = self.map.collider_aabb(i);
                if let Some(off) = self.pose_of(dk, dx) {
                    if overlaps(amin.add(off), amax.add(off), mn, mx) {
                        out[w] |= 1 << b;
                    }
                }
            }
            w += 1;
        }
    }

    /// GLOBAL list + the grid cells overlapping `[mn, mx]` (every collider when that is more than
    /// [`MAX_QUERY_CELLS`] cells), before the fine filter.
    fn raw_candidates(&self, mn: IVec3, mx: IVec3, raw: &mut Bits) {
        let ng = self.map.global_count.min(crate::mapblob::MAX_COLLIDERS as u32);
        let mut k = 0;
        while k < ng {
            let i = self.map.global(k) as usize;
            if i < crate::mapblob::MAX_COLLIDERS {
                raw[i / 64] |= 1 << (i % 64);
            }
            k += 1;
        }
        let h = self.map.grid_header();
        let cell = h[0] as i64;
        if cell > 0 {
            let (ox, oy, oz) = (h[1] as i64, h[2] as i64, h[3] as i64);
            let (nx, ny, nz) = (h[4] as i64, h[5] as i64, h[6] as i64);
            let cx0 = floor_div(mn.x as i64 - ox, cell).max(0);
            let cy0 = floor_div(mn.y as i64 - oy, cell).max(0);
            let cz0 = floor_div(mn.z as i64 - oz, cell).max(0);
            let cx1 = floor_div(mx.x as i64 - ox, cell).min(nx - 1);
            let cy1 = floor_div(mx.y as i64 - oy, cell).min(ny - 1);
            let cz1 = floor_div(mx.z as i64 - oz, cell).min(nz - 1);
            if cx1 >= cx0 && cy1 >= cy0 && cz1 >= cz0 {
                let cells = (cx1 - cx0 + 1) * (cy1 - cy0 + 1) * (cz1 - cz0 + 1);
                if cells > MAX_QUERY_CELLS {
                    let n = self.map.collider_count;
                    let mut i = 0u32;
                    while i < n {
                        raw[i as usize / 64] |= 1 << (i % 64);
                        i += 1;
                    }
                } else {
                    let mut ix = cx0;
                    while ix <= cx1 {
                        let mut iy = cy0;
                        while iy <= cy1 {
                            // ids of this (ix, iy) column are consecutive in iz
                            let base = (ix * ny + iy) * nz;
                            self.map.grid_cells_in((base + cz0) as u32, (base + cz1) as u32, |start, count| {
                                let mut j = 0;
                                while j < count {
                                    let ci = self.map.grid_index(start + j) as usize;
                                    if ci < crate::mapblob::MAX_COLLIDERS {
                                        raw[ci / 64] |= 1 << (ci % 64);
                                    }
                                    j += 1;
                                }
                            });
                            iy += 1;
                        }
                        ix += 1;
                    }
                }
            }
        }
    }
}

/// Inflation (mm, each side) of a memoised broadphase box.
pub const BP_INFLATE_MM: i32 = 1024;

/// Iterates the set bits of a bitset in ascending order (bounded by its size).
#[inline]
pub fn for_each(bits: &Bits, mut f: impl FnMut(u32) -> bool) {
    let mut w = 0;
    while w < bits.len() {
        let mut b = bits[w];
        while b != 0 {
            let t = b.trailing_zeros();
            b &= b - 1;
            if !f(w as u32 * 64 + t) {
                return;
            }
        }
        w += 1;
    }
}
