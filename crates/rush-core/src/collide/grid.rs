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
    pub fn gather(&self, mn: IVec3, mx: IVec3, out: &mut Bits) {
        *out = [0; crate::mapblob::MAX_COLLIDERS / 64];
        let mut raw: Bits = [0; crate::mapblob::MAX_COLLIDERS / 64];
        // GLOBAL list.
        let ng = self.map.global_count.min(crate::mapblob::MAX_COLLIDERS as u32);
        let mut k = 0;
        while k < ng {
            let i = self.map.global(k) as usize;
            if i < crate::mapblob::MAX_COLLIDERS {
                raw[i / 64] |= 1 << (i % 64);
            }
            k += 1;
        }
        // Grid cells.
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
                            let mut iz = cz0;
                            while iz <= cz1 {
                                let id = ((ix * ny + iy) * nz + iz) as u32;
                                if let Some((start, count)) = self.map.grid_cell(id) {
                                    let mut j = 0;
                                    while j < count {
                                        let ci = self.map.grid_index(start + j) as usize;
                                        if ci < crate::mapblob::MAX_COLLIDERS {
                                            raw[ci / 64] |= 1 << (ci % 64);
                                        }
                                        j += 1;
                                    }
                                }
                                iz += 1;
                            }
                            iy += 1;
                        }
                        ix += 1;
                    }
                }
            }
        }
        // Fine filter: posed AABB overlap and enabled.
        let mut w = 0;
        while w < raw.len() {
            let mut bits = raw[w];
            while bits != 0 {
                let b = bits.trailing_zeros();
                bits &= bits - 1;
                let i = (w as u32) * 64 + b;
                if let Some((c, off)) = self.posed(i) {
                    if overlaps(c.aabb_min.add(off), c.aabb_max.add(off), mn, mx) {
                        out[w] |= 1 << b;
                    }
                }
            }
            w += 1;
        }
    }
}

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
