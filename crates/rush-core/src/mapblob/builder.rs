//! Off-chain map blob builder (feature `builder`; uses `alloc`). Used by `rush-sim mapblob build` (from the Unity
//! exporter's `.mapjson`) and by the synthetic test maps. Integer maths only, like the reader.

extern crate alloc;
use alloc::vec::Vec;

use super::*;
use crate::fx::{isqrt_round, normalize_q15, ONE_Q15};

/// A collider shape in map-blob form.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ShapeSpec {
    /// Centre, half extents (mm), three Q15 axes (columns of the rotation: local x, y, z in world space).
    Obb { center: IVec3, half: IVec3, axes: [IVec3; 3] },
    /// Vertical convex prism: XZ vertices (any order; the convex hull is taken), bottom and top y.
    Prism { verts: Vec<(i32, i32)>, y0: i32, y1: i32 },
    Sphere { center: IVec3, radius: i32 },
    /// Vertical capsule: bottom and top segment points + radius.
    Capsule { a: IVec3, b: IVec3, radius: i32 },
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ColliderSpec {
    pub shape: ShapeSpec,
    pub surface: i32,
    pub dyn_kind: u8,
    pub dyn_index: u16,
}

impl ColliderSpec {
    pub fn fixed(shape: ShapeSpec) -> Self {
        ColliderSpec { shape, surface: -1, dyn_kind: dyn_kind::STATIC, dyn_index: 0 }
    }
}

/// Axis-aligned box helper.
pub fn aabb_box(center: IVec3, half: IVec3) -> ShapeSpec {
    ShapeSpec::Obb { center, half, axes: [IVec3::new(ONE_Q15, 0, 0), IVec3::new(0, ONE_Q15, 0), IVec3::new(0, 0, ONE_Q15)] }
}

/// Box rotated by `pitch_q16` about +X (a ramp rising toward −Z for positive angles, as Unity's X rotation does) then
/// `yaw_q16` about +Y.
pub fn rotated_box(center: IVec3, half: IVec3, yaw_q16: i32, pitch_q16: i32) -> ShapeSpec {
    use crate::fx::{cos_q15, sin_q15};
    let (cy, sy) = (cos_q15(yaw_q16) as i64, sin_q15(yaw_q16) as i64);
    let (cp, sp) = (cos_q15(pitch_q16) as i64, sin_q15(pitch_q16) as i64);
    let o = ONE_Q15 as i64;
    // R = Ry · Rx. Columns: R·ex, R·ey, R·ez.
    // Rx: ex → (1,0,0); ey → (0,cp,sp); ez → (0,−sp,cp)   (Unity left-handed: +X rotation tips +Y toward +Z)
    // Ry: (x,y,z) → (x·cy + z·sy, y, −x·sy + z·cy)
    let ry = |v: [i64; 3]| -> IVec3 {
        let x = crate::fx::div_round(v[0] * cy + v[2] * sy, o);
        let z = crate::fx::div_round(-v[0] * sy + v[2] * cy, o);
        IVec3::new(x as i32, v[1] as i32, z as i32)
    };
    let ax = ry([o, 0, 0]);
    let ay = ry([0, cp, sp]);
    let az = ry([0, -sp, cp]);
    ShapeSpec::Obb { center, half, axes: [ax, ay, az] }
}

#[derive(Clone, Debug, Default)]
pub struct CheckpointSpec {
    pub center: IVec3,
    pub half: IVec3,
    pub course_mm: i32,
    pub respawns: Vec<(IVec3, u8)>,
}

/// One hazard record: type, phase range, obstacle id, def words.
#[derive(Clone, Debug)]
pub struct HazardSpec {
    pub ty: HazardType,
    pub phase_min: i32,
    pub phase_max: i32,
    pub id: Vec<u8>,
    pub words: Vec<i32>,
    /// Keep the def's phase16 (C# M2 sweepers / platforms without an obstacle id).
    pub baked: bool,
}

#[derive(Clone, Debug, Default)]
pub struct MapSpec {
    pub name: Vec<u8>,
    pub meta: [i32; meta::WORDS],
    pub colliders: Vec<ColliderSpec>,
    pub spawns: Vec<(IVec3, u8)>,
    pub checkpoints: Vec<CheckpointSpec>,
    pub course: Vec<IVec3>,
    /// Variant A, variant B (B may be empty = same as A).
    pub hazards: [Vec<HazardSpec>; 2],
    pub bot_routes: Vec<i32>,
    /// Grid cell size (mm).
    pub cell_mm: i32,
}

/// Static colliders spanning more cells than this go to the GLOBAL list (tested by every query).
pub const MAX_CELLS_PER_COLLIDER: i64 = 48;

fn hull(points: &[(i32, i32)]) -> Vec<(i32, i32)> {
    // Andrew's monotone chain, counter-clockwise (in the x–z plane, seen from +Y with x right and z up).
    let mut p: Vec<(i32, i32)> = points.to_vec();
    p.sort();
    p.dedup();
    if p.len() < 3 {
        return p;
    }
    let cross = |o: (i32, i32), a: (i32, i32), b: (i32, i32)| -> i64 {
        (a.0 - o.0) as i64 * (b.1 - o.1) as i64 - (a.1 - o.1) as i64 * (b.0 - o.0) as i64
    };
    let mut lower: Vec<(i32, i32)> = Vec::new();
    for &q in &p {
        while lower.len() >= 2 && cross(lower[lower.len() - 2], lower[lower.len() - 1], q) <= 0 {
            lower.pop();
        }
        lower.push(q);
    }
    let mut upper: Vec<(i32, i32)> = Vec::new();
    for &q in p.iter().rev() {
        while upper.len() >= 2 && cross(upper[upper.len() - 2], upper[upper.len() - 1], q) <= 0 {
            upper.pop();
        }
        upper.push(q);
    }
    lower.pop();
    upper.pop();
    lower.extend(upper);
    lower
}

/// AABB of a shape.
pub fn shape_aabb(s: &ShapeSpec) -> (IVec3, IVec3) {
    match s {
        ShapeSpec::Obb { center, half, axes } => {
            // extent_i = Σ_k |axis_k[i]| · half_k
            let mut e = [0i64; 3];
            for k in 0..3 {
                let h = [half.x, half.y, half.z][k] as i64;
                let a = axes[k];
                e[0] += (a.x as i64).abs() * h;
                e[1] += (a.y as i64).abs() * h;
                e[2] += (a.z as i64).abs() * h;
            }
            let o = ONE_Q15 as i64;
            let ex = IVec3::new(((e[0] + o - 1) / o) as i32 + 1, ((e[1] + o - 1) / o) as i32 + 1, ((e[2] + o - 1) / o) as i32 + 1);
            (center.sub(ex), center.add(ex))
        }
        ShapeSpec::Prism { verts, y0, y1 } => {
            let (mut mnx, mut mnz, mut mxx, mut mxz) = (i32::MAX, i32::MAX, i32::MIN, i32::MIN);
            for &(x, z) in verts {
                mnx = mnx.min(x);
                mnz = mnz.min(z);
                mxx = mxx.max(x);
                mxz = mxz.max(z);
            }
            (IVec3::new(mnx, *y0, mnz), IVec3::new(mxx, *y1, mxz))
        }
        ShapeSpec::Sphere { center, radius } => {
            let r = IVec3::new(*radius, *radius, *radius);
            (center.sub(r), center.add(r))
        }
        ShapeSpec::Capsule { a, b, radius } => {
            let r = IVec3::new(*radius, *radius, *radius);
            let mn = IVec3::new(a.x.min(b.x), a.y.min(b.y), a.z.min(b.z)).sub(r);
            let mx = IVec3::new(a.x.max(b.x), a.y.max(b.y), a.z.max(b.z)).add(r);
            (mn, mx)
        }
    }
}

fn push_i32(out: &mut Vec<u8>, v: i32) {
    out.extend_from_slice(&v.to_le_bytes());
}

fn push_v3(out: &mut Vec<u8>, v: IVec3) {
    push_i32(out, v.x);
    push_i32(out, v.y);
    push_i32(out, v.z);
}

/// Builds the blob. Panics on invalid input (exporter bugs should fail loudly).
pub fn build(spec: &MapSpec) -> Vec<u8> {
    assert!(spec.colliders.len() <= MAX_COLLIDERS, "{} colliders > {MAX_COLLIDERS}", spec.colliders.len());
    let cell = if spec.cell_mm > 0 { spec.cell_mm } else { 2000 };

    // ---- colliders + prisms ----
    let mut col = Vec::new();
    let mut prisms: Vec<u8> = Vec::new();
    let mut prism_words = 0u32;
    let mut aabbs = Vec::new();
    for c in &spec.colliders {
        let (mn, mx) = shape_aabb(&c.shape);
        aabbs.push((mn, mx));
        let mut w = [0i32; COLLIDER_WORDS];
        let kind = match &c.shape {
            ShapeSpec::Obb { .. } => shape::OBB,
            ShapeSpec::Prism { .. } => shape::PRISM,
            ShapeSpec::Sphere { .. } => shape::SPHERE,
            ShapeSpec::Capsule { .. } => shape::CAPSULE,
        };
        w[0] = (kind as u32 | (c.dyn_kind as u32) << 8 | (c.dyn_index as u32) << 16) as i32;
        w[1] = c.surface;
        w[2..5].copy_from_slice(&[mn.x, mn.y, mn.z]);
        w[5..8].copy_from_slice(&[mx.x, mx.y, mx.z]);
        match &c.shape {
            ShapeSpec::Obb { center, half, axes } => {
                w[8..11].copy_from_slice(&[center.x, center.y, center.z]);
                w[11..14].copy_from_slice(&[half.x, half.y, half.z]);
                for k in 0..3 {
                    w[14 + k * 3..17 + k * 3].copy_from_slice(&[axes[k].x, axes[k].y, axes[k].z]);
                }
                let yaw_only = axes[1] == IVec3::new(0, ONE_Q15, 0) && axes[0].y == 0 && axes[2].y == 0;
                w[23] = yaw_only as i32;
            }
            ShapeSpec::Prism { verts, y0, y1 } => {
                let h = hull(verts);
                assert!(h.len() >= 3 && h.len() <= MAX_PRISM_VERTS, "prism with {} hull vertices", h.len());
                assert!(y1 > y0, "prism y range");
                w[11] = prism_words as i32;
                w[12] = h.len() as i32;
                w[14] = *y0;
                w[15] = *y1;
                // centre (for penetration fallbacks): vertex mean
                let (sx, sz) = h.iter().fold((0i64, 0i64), |a, v| (a.0 + v.0 as i64, a.1 + v.1 as i64));
                w[8] = (sx / h.len() as i64) as i32;
                w[9] = (*y0 + *y1) / 2;
                w[10] = (sz / h.len() as i64) as i32;
                for k in 0..h.len() {
                    let a = h[k];
                    let b = h[(k + 1) % h.len()];
                    // CCW hull (x right, z up): outward normal of edge a→b is (dz, −dx).
                    let ex = (b.0 - a.0) as i64;
                    let ez = (b.1 - a.1) as i64;
                    let n = normalize_q15(ez, 0, -ex);
                    let off = n[0] as i64 * a.0 as i64 + n[2] as i64 * a.1 as i64;
                    for v in [a.0, a.1, n[0], n[2], off as u32 as i32, (off >> 32) as i32] {
                        push_i32(&mut prisms, v);
                    }
                    prism_words += PRISM_VERTEX_WORDS as u32;
                }
            }
            ShapeSpec::Sphere { center, radius } => {
                w[8..11].copy_from_slice(&[center.x, center.y, center.z]);
                w[11] = *radius;
            }
            ShapeSpec::Capsule { a, b, radius } => {
                assert!(a.x == b.x && a.z == b.z && b.y >= a.y, "only vertical capsules");
                w[8..11].copy_from_slice(&[a.x, a.y, a.z]);
                w[11] = *radius;
                w[14..17].copy_from_slice(&[b.x, b.y, b.z]);
            }
        }
        for v in w {
            push_i32(&mut col, v);
        }
    }

    // ---- grid (static colliders only; moving and huge ones → GLOBAL) ----
    let mut global: Vec<u32> = Vec::new();
    let mut mn = IVec3::new(i32::MAX, i32::MAX, i32::MAX);
    let mut mx = IVec3::new(i32::MIN, i32::MIN, i32::MIN);
    for (i, c) in spec.colliders.iter().enumerate() {
        if c.dyn_kind == dyn_kind::PLATFORM || c.dyn_kind == dyn_kind::DOOR {
            continue;
        }
        let (a, b) = aabbs[i];
        mn = IVec3::new(mn.x.min(a.x), mn.y.min(a.y), mn.z.min(a.z));
        mx = IVec3::new(mx.x.max(b.x), mx.y.max(b.y), mx.z.max(b.z));
    }
    if mn.x == i32::MAX {
        mn = IVec3::ZERO;
        mx = IVec3::ZERO;
    }
    let fl = |v: i32, o: i32| -> i64 { crate::fx::floor_div(v as i64 - o as i64, cell as i64) };
    let org = IVec3::new(
        (crate::fx::floor_div(mn.x as i64, cell as i64) * cell as i64) as i32,
        (crate::fx::floor_div(mn.y as i64, cell as i64) * cell as i64) as i32,
        (crate::fx::floor_div(mn.z as i64, cell as i64) * cell as i64) as i32,
    );
    let nx = fl(mx.x, org.x) + 1;
    let ny = fl(mx.y, org.y) + 1;
    let nz = fl(mx.z, org.z) + 1;
    let mut entries: Vec<(u32, u32)> = Vec::new(); // (cell id, collider)
    for (i, c) in spec.colliders.iter().enumerate() {
        if c.dyn_kind == dyn_kind::PLATFORM || c.dyn_kind == dyn_kind::DOOR {
            global.push(i as u32);
            continue;
        }
        let (a, b) = aabbs[i];
        let (x0, x1) = (fl(a.x, org.x), fl(b.x, org.x));
        let (y0, y1) = (fl(a.y, org.y), fl(b.y, org.y));
        let (z0, z1) = (fl(a.z, org.z), fl(b.z, org.z));
        let cells = (x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
        if cells > MAX_CELLS_PER_COLLIDER {
            global.push(i as u32);
            continue;
        }
        for ix in x0..=x1 {
            for iy in y0..=y1 {
                for iz in z0..=z1 {
                    let id = ((ix * ny + iy) * nz + iz) as u32;
                    entries.push((id, i as u32));
                }
            }
        }
    }
    entries.sort();
    let mut grid = Vec::new();
    let mut idx = Vec::new();
    let mut cells: Vec<(u32, u32, u32)> = Vec::new();
    for (k, &(id, ci)) in entries.iter().enumerate() {
        if cells.last().map(|c| c.0) != Some(id) {
            cells.push((id, k as u32, 0));
        }
        cells.last_mut().unwrap().2 += 1;
        push_i32(&mut idx, ci as i32);
    }
    for v in [cell, org.x, org.y, org.z, nx as i32, ny as i32, nz as i32, cells.len() as i32] {
        push_i32(&mut grid, v);
    }
    for (id, s, n) in &cells {
        push_i32(&mut grid, *id as i32);
        push_i32(&mut grid, *s as i32);
        push_i32(&mut grid, *n as i32);
    }
    let mut glob = Vec::new();
    for g in &global {
        push_i32(&mut glob, *g as i32);
    }

    // ---- the rest ----
    let mut meta_b = Vec::new();
    for v in spec.meta {
        push_i32(&mut meta_b, v);
    }
    let mut spawns = Vec::new();
    for i in 0..12 {
        let (p, y) = spec.spawns.get(i).copied().unwrap_or((IVec3::new(-11000 + 2000 * i as i32, 100, 0), 0));
        push_v3(&mut spawns, p);
        push_i32(&mut spawns, y as i32);
    }
    let mut cps = Vec::new();
    let mut rsp = Vec::new();
    let mut rk = 0u32;
    for c in &spec.checkpoints {
        push_v3(&mut cps, c.center);
        push_v3(&mut cps, c.half);
        push_i32(&mut cps, c.course_mm);
        push_i32(&mut cps, rk as i32);
        push_i32(&mut cps, c.respawns.len() as i32);
        for (p, y) in &c.respawns {
            push_v3(&mut rsp, *p);
            push_i32(&mut rsp, *y as i32);
            rk += 1;
        }
    }
    let mut course = Vec::new();
    for p in &spec.course {
        push_v3(&mut course, *p);
    }
    let mut haz = [Vec::new(), Vec::new()];
    for (v, out) in haz.iter_mut().enumerate() {
        let mut list: Vec<&HazardSpec> = spec.hazards[v].iter().collect();
        list.sort_by_key(|h| h.ty as u8); // stable: keeps authoring order within a type
        for h in list {
            assert_eq!(h.words.len(), h.ty.words(), "{:?} def words", h.ty);
            assert!(h.id.len() <= 32, "obstacle id longer than 32 bytes");
            push_i32(out, h.ty as i32);
            push_i32(out, h.words.len() as i32);
            push_i32(out, h.phase_min);
            push_i32(out, h.phase_max);
            push_i32(out, (h.id.len() as u32 | if h.baked { crate::hazards::ID_BAKED_PHASE } else { 0 }) as i32);
            let mut idb = [0u8; 32];
            idb[..h.id.len()].copy_from_slice(&h.id);
            out.extend_from_slice(&idb);
            for w in &h.words {
                push_i32(out, *w);
            }
        }
    }
    let mut bots = Vec::new();
    for w in &spec.bot_routes {
        push_i32(&mut bots, *w);
    }
    let mut name = spec.name.clone();
    while name.len() % 4 != 0 {
        name.push(0);
    }
    let [ha, hb] = haz;
    let sections: [(u32, Vec<u8>); 14] = [
        (tag::META, meta_b),
        (tag::COLLIDERS, col),
        (tag::PRISMS, prisms),
        (tag::GRID, grid),
        (tag::GRID_IDX, idx),
        (tag::SPAWNS, spawns),
        (tag::CHECKPOINTS, cps),
        (tag::RESPAWNS, rsp),
        (tag::COURSE, course),
        (tag::HAZARDS_A, ha),
        (tag::HAZARDS_B, hb),
        (tag::BOT_ROUTES, bots),
        (tag::NAME, name),
        (tag::GLOBAL, glob),
    ];
    let mut out = Vec::new();
    out.extend_from_slice(&MAGIC);
    out.extend_from_slice(&VERSION.to_le_bytes());
    out.extend_from_slice(&0u16.to_le_bytes());
    out.extend_from_slice(&(sections.len() as u32).to_le_bytes());
    let mut off = 12 + sections.len() * 12;
    for (t, b) in &sections {
        out.extend_from_slice(&t.to_le_bytes());
        out.extend_from_slice(&(off as u32).to_le_bytes());
        out.extend_from_slice(&(b.len() as u32).to_le_bytes());
        off += b.len();
    }
    for (_, b) in &sections {
        out.extend_from_slice(b);
    }
    let _ = isqrt_round(0);
    out
}

/// A hazard spec from a def (helper for synthetic maps and the JSON importer).
pub fn hazard<const N: usize>(ty: HazardType, id: &str, words: [i32; N]) -> HazardSpec {
    HazardSpec { ty, phase_min: 0, phase_max: 65535, id: id.as_bytes().to_vec(), words: words.to_vec(), baked: false }
}

/// Words of a def (`write` into a fresh vec).
pub fn def_words(ty: HazardType, write: impl FnOnce(&mut [i32])) -> Vec<i32> {
    let mut w = alloc::vec![0i32; ty.words()];
    write(&mut w);
    w
}
