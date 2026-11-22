//! Collision kit (doc 19 P1.2): OBB, vertical convex prism, sphere and capsule colliders from the map blob, queried by
//! the upright player capsule. [`Scene`] is rush-core's `IMotorWorld` (`Shared/IMotorWorld.cs`): the five queries
//! keep the C# meanings:
//!
//! | C# `IMotorWorld` | rush-core |
//! |---|---|
//! | `CapsuleSweep(feet, r, h, dir, max, out hit)` | [`Scene::capsule_sweep`] |
//! | `Raycast(origin, dir, max, out hit)` | [`Scene::raycast`] |
//! | `ComputePenetration(feet, r, h, out corr)` | [`Scene::penetration`] |
//! | `BeginTick(tick)` | `world::pose_scene` (fills the per-tick poses of a `Scene`) |
//! | `SurfaceDelta(id, feet, out delta)` | [`Scene::surface_delta`] |
//!
//! **Sweep method** (P1 prompt C.3): swept-AABB grid broadphase → samples every ≤ 200 mm along the move → closest-
//! point overlap tests (segment vs shape, exact for every shape except tilted boxes, which use a bounded ternary
//! search) → 8-step bisection of the time of impact → the normal from the closest features at that point. All loops
//! are bounded. Like PhysX `CapsuleCast`, colliders the capsule already overlaps at the start are ignored (the motor
//! depenetrates first).

pub mod capsule;
pub mod grid;
pub mod obb;
pub mod prism;
pub mod sphere;
pub mod sweep;

use crate::mapblob::{dyn_kind, shape, Collider, MapView};
use crate::vec::IVec3;

/// Sub-millimetres per mm used inside distance queries.
pub const S: i64 = 16;
/// Longest sweep sample step (mm).
pub const SUBSTEP_MM: i64 = 200;
/// Most samples per sweep (a 12.8 m sweep; longer sweeps use longer steps).
pub const MAX_SUBSTEPS: i64 = 64;
/// Time-of-impact bisection steps.
pub const TOI_BISECT: u32 = 8;
/// Most moving platforms / doors a map may have.
pub const MAX_PLATFORMS: usize = 16;
pub const MAX_DOORS: usize = 16;
/// Most lids / tiles / falling blocks (solid bitsets).
pub const MAX_LIDS: usize = 256;
pub const MAX_TILES: usize = 64;
pub const MAX_BLOCKS: usize = 64;

/// The upright capsule's axis segment (sub-mm): (x, z) and the y range of the sphere centres.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Seg {
    pub x: i64,
    pub z: i64,
    pub y0: i64,
    pub y1: i64,
}

impl Seg {
    /// Axis of the capsule with feet at `f` (sub-mm), radius `r` and height `h` (mm).
    #[inline]
    pub fn of(f: [i64; 3], r: i32, h: i32) -> Seg {
        Seg { x: f[0], z: f[2], y0: f[1] + r as i64 * S, y1: f[1] + (h - r) as i64 * S }
    }

    #[inline]
    pub fn shifted(&self, off: IVec3) -> Seg {
        Seg { x: self.x - off.x as i64 * S, z: self.z - off.z as i64 * S, y0: self.y0 - off.y as i64 * S, y1: self.y1 - off.y as i64 * S }
    }
}

/// Closest features of a segment and a shape's core (the shape minus its own radius `core_r`, sub-mm).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Near {
    /// Squared distance segment ↔ core (sub-mm²).
    pub gap2: i64,
    pub core: [i64; 3],
    pub seg: [i64; 3],
    pub core_r: i64,
}

/// A query hit (`MotorHit`): distance (mm), contact point (mm), normal (Q15), surface id (−1 = static).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Hit {
    pub distance: i64,
    pub point: IVec3,
    pub normal: IVec3,
    pub surface: i32,
    pub collider: u32,
}

/// Work counters per scene (feature `counters`); zero-sized otherwise.
#[derive(Default, Debug)]
pub struct Counters {
    #[cfg(feature = "counters")]
    pub c: core::cell::Cell<[u32; 6]>,
}

/// Counter slots.
pub mod ctr {
    pub const NARROW: usize = 0;
    pub const SUBSTEPS: usize = 1;
    pub const SLIDES: usize = 2;
    pub const BISECT: usize = 3;
    pub const BOT_RAYS: usize = 4;
    pub const SWEEPS: usize = 5;
}

/// Process-wide totals since the last [`take_tick_counters`] (feature `counters`; off-chain runners only).
#[cfg(feature = "counters")]
pub static TOTALS: [core::sync::atomic::AtomicU32; 6] = [const { core::sync::atomic::AtomicU32::new(0) }; 6];

/// Takes (and zeroes) the totals counted since the last call: one `step()` = one tick when called after each step.
pub fn take_tick_counters() -> [u32; 6] {
    #[cfg(feature = "counters")]
    {
        let mut o = [0u32; 6];
        for (k, v) in o.iter_mut().enumerate() {
            *v = TOTALS[k].swap(0, core::sync::atomic::Ordering::Relaxed);
        }
        o
    }
    #[cfg(not(feature = "counters"))]
    {
        [0; 6]
    }
}

impl Counters {
    #[inline]
    pub fn add(&self, _k: usize, _n: u32) {
        #[cfg(feature = "counters")]
        {
            let mut a = self.c.get();
            a[_k] = a[_k].saturating_add(_n);
            self.c.set(a);
            TOTALS[_k].fetch_add(_n, core::sync::atomic::Ordering::Relaxed);
        }
    }

    pub fn get(&self) -> [u32; 6] {
        #[cfg(feature = "counters")]
        {
            self.c.get()
        }
        #[cfg(not(feature = "counters"))]
        {
            [0; 6]
        }
    }

    pub fn reset(&self) {
        #[cfg(feature = "counters")]
        self.c.set([0; 6]);
    }
}

/// Per-tick surface motions (filled by `world::pose_scene` from the hazards).
#[derive(Clone, Copy, Debug, Default)]
pub struct SurfaceMotion {
    /// 0 none, 1 platform (linear delta), 2 turn table (rotation), 3 conveyor (belt delta).
    pub kind: u8,
    pub delta: IVec3,
    /// Turn table: centre and phases at t − 1 and t (Q16 turns).
    pub center: IVec3,
    pub p0: i32,
    pub p1: i32,
}

/// Most moving surfaces.
pub const MAX_SURFACES: usize = 16;

/// The posed collision world for one tick (rush-core's `IMotorWorld`).
pub struct Scene<'m> {
    pub map: MapView<'m>,
    pub platform_off: [IVec3; MAX_PLATFORMS],
    pub door_off: [IVec3; MAX_DOORS],
    pub lid_solid: [u64; MAX_LIDS / 64],
    pub tile_solid: [u64; MAX_TILES / 64],
    pub block_solid: [u64; MAX_BLOCKS / 64],
    pub surfaces: [SurfaceMotion; MAX_SURFACES],
    pub counters: Counters,
}

/// Bitset over collider indices.
pub type Bits = [u64; crate::mapblob::MAX_COLLIDERS / 64];

impl<'m> Scene<'m> {
    /// A scene with everything solid and nothing moved (static worlds, tests).
    pub fn new(map: MapView<'m>) -> Scene<'m> {
        Scene {
            map,
            platform_off: [IVec3::ZERO; MAX_PLATFORMS],
            door_off: [IVec3::ZERO; MAX_DOORS],
            lid_solid: [u64::MAX; MAX_LIDS / 64],
            tile_solid: [u64::MAX; MAX_TILES / 64],
            block_solid: [u64::MAX; MAX_BLOCKS / 64],
            surfaces: [SurfaceMotion::default(); MAX_SURFACES],
            counters: Counters::default(),
        }
    }

    /// Collider `i` posed for this tick and its pose offset, or `None` when it is switched off.
    #[inline]
    pub fn posed(&self, i: u32) -> Option<(Collider, IVec3)> {
        let c = self.map.collider(i);
        let k = c.dyn_index as usize;
        let bit = |b: &[u64], k: usize| k / 64 < b.len() && (b[k / 64] >> (k % 64)) & 1 == 1;
        let off = match c.dyn_kind {
            dyn_kind::PLATFORM => self.platform_off.get(k).copied().unwrap_or(IVec3::ZERO),
            dyn_kind::DOOR => self.door_off.get(k).copied().unwrap_or(IVec3::ZERO),
            dyn_kind::LID => {
                if !bit(&self.lid_solid, k) {
                    return None;
                }
                IVec3::ZERO
            }
            dyn_kind::TILE => {
                if !bit(&self.tile_solid, k) {
                    return None;
                }
                IVec3::ZERO
            }
            dyn_kind::BLOCK => {
                if !bit(&self.block_solid, k) {
                    return None;
                }
                IVec3::ZERO
            }
            _ => IVec3::ZERO,
        };
        Some((c, off))
    }

    /// Closest features between the capsule axis `s` and collider `c` posed at `off`.
    #[inline]
    pub fn near(&self, c: &Collider, off: IVec3, s: &Seg) -> Near {
        self.counters.add(ctr::NARROW, 1);
        let ls = s.shifted(off);
        let mut n = match c.kind {
            shape::OBB => obb::near(c, &ls),
            shape::PRISM => prism::near(&self.map, c, &ls),
            shape::SPHERE => sphere::near(c, &ls),
            _ => capsule::near(c, &ls),
        };
        let o = [off.x as i64 * S, off.y as i64 * S, off.z as i64 * S];
        n.core = [n.core[0] + o[0], n.core[1] + o[1], n.core[2] + o[2]];
        n.seg = [n.seg[0] + o[0], n.seg[1] + o[1], n.seg[2] + o[2]];
        n
    }

    /// `IMotorWorld.SurfaceDelta`: how surface `id` moves a rider whose feet are at `feet` this tick (mm).
    pub fn surface_delta(&self, id: i32, feet: IVec3) -> Option<IVec3> {
        if id < 0 || id as usize >= MAX_SURFACES {
            return None;
        }
        let m = &self.surfaces[id as usize];
        match m.kind {
            1 | 3 => Some(m.delta),
            2 => {
                let (dx, dz) = crate::hazards::turn_table_carry(m.center, m.p0, m.p1, feet.x, feet.z);
                Some(IVec3::new(dx, 0, dz))
            }
            _ => None,
        }
    }
}

#[cfg(test)]
mod tests;
