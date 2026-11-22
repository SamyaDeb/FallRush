//! Offline, deterministic map generator (integer only) + grid broadphase builder.
//! (a) `lid_flip_like()`  ~120 colliders: 9x9 + 5x5 lids, bumpers, rails, 1 sweeper.
//! (b) `capsule_run_like()` ~300 colliders: floor/walls/obstacles OBBs + 20 prisms + 6 spheres,
//!     2 sweepers + 6 rolling balls.
use crate::fx::{cos1024, isqrt, mix64, sin1024, ONE};
use crate::map::*;
use alloc::vec::Vec;

pub struct Builder {
    pub cols: Vec<Collider>,
    pub verts: Vec<Vert>,
    pub dyns: Vec<DynDef>,
    pub hdr: Header,
}

impl Builder {
    pub fn new(kind: u32) -> Self {
        Builder { cols: Vec::new(), verts: Vec::new(), dyns: Vec::new(), hdr: Header { magic: MAGIC, kind, cell_shift: 11, ..Header::default() } }
    }

    pub fn obb(&mut self, c: [i32; 3], h: [i32; 3], yaw: u32) {
        let co = (cos1024(yaw)).clamp(-32767, 32767) as i16;
        let si = (sin1024(yaw)).clamp(-32767, 32767) as i16;
        let ex = ((co as i64).abs() * h[0] as i64 + (si as i64).abs() * h[2] as i64) >> 15;
        let ez = ((si as i64).abs() * h[0] as i64 + (co as i64).abs() * h[2] as i64) >> 15;
        let (ex, ez) = (ex as i32 + 2, ez as i32 + 2);
        self.cols.push(Collider {
            kind: K_BOX,
            cos: co,
            sin: si,
            c,
            h,
            min: [c[0] - ex, c[1] - h[1], c[2] - ez],
            max: [c[0] + ex, c[1] + h[1], c[2] + ez],
            ..Collider::default()
        });
    }

    /// Regular convex prism: `sides` verts on radius r, centre c (y = mid height), half height hy.
    pub fn prism(&mut self, c: [i32; 3], r: i32, sides: u32, hy: i32, yaw: u32) {
        let off = self.verts.len() as u16;
        let mut pts: Vec<(i32, i32)> = Vec::new();
        for k in 0..sides {
            let a = yaw + k * 1024 / sides;
            pts.push((c[0] + ((cos1024(a) as i64 * r as i64) >> 15) as i32, c[2] + ((sin1024(a) as i64 * r as i64) >> 15) as i32));
        }
        for k in 0..sides as usize {
            let (ax, az) = pts[k];
            let (bx, bz) = pts[(k + 1) % sides as usize];
            // CCW in (x,z) -> outward normal = (ez, -ex) for edge e = b - a when ordered by increasing angle
            let ex = (bx - ax) as i64;
            let ez = (bz - az) as i64;
            let l = isqrt((ex * ex + ez * ez) as u64) as i64;
            let (nx, nz) = (ez * ONE as i64 / l, -ex * ONE as i64 / l);
            self.verts.push(Vert { x: ax, z: az, nx: nx as i32, nz: nz as i32 });
        }
        self.cols.push(Collider {
            kind: K_PRISM,
            nverts: sides as u8,
            vert_off: off,
            c,
            h: [r, hy, r],
            min: [c[0] - r - 2, c[1] - hy, c[2] - r - 2],
            max: [c[0] + r + 2, c[1] + hy, c[2] + r + 2],
            ..Collider::default()
        });
    }

    pub fn sphere(&mut self, c: [i32; 3], r: i32) {
        self.cols.push(Collider { kind: K_SPHERE, c, h: [r, 0, 0], min: [c[0] - r, c[1] - r, c[2] - r], max: [c[0] + r, c[1] + r, c[2] + r], ..Collider::default() });
    }

    pub fn hcapsule(&mut self, c: [i32; 3], half_len: i32, r: i32, yaw: u32) {
        let co = cos1024(yaw).clamp(-32767, 32767) as i16;
        let si = sin1024(yaw).clamp(-32767, 32767) as i16;
        let ex = ((co as i64).abs() * half_len as i64 >> 15) as i32 + r;
        let ez = ((si as i64).abs() * half_len as i64 >> 15) as i32 + r;
        self.cols.push(Collider { kind: K_CAPSULE, cos: co, sin: si, c, h: [half_len, r, 0], min: [c[0] - ex, c[1] - r, c[2] - ez], max: [c[0] + ex, c[1] + r, c[2] + ez], ..Collider::default() });
    }

    pub fn sweeper(&mut self, pivot: [i32; 3], half_len: i32, r: i32, period: u16, salt: u32) {
        self.dyns.push(DynDef { kind: D_SWEEPER, period, salt, a: pivot, b: [half_len, 0, 0], r, ..DynDef::default() });
    }

    pub fn ball(&mut self, a: [i32; 3], b: [i32; 3], r: i32, period: u16, salt: u32) {
        self.dyns.push(DynDef { kind: D_BALL, period, salt, a, b, r, ..DynDef::default() });
    }

    /// Build the uniform grid and serialise. Returned as Vec<u32> so the bytes are 4-aligned.
    pub fn finish(mut self) -> Vec<u32> {
        let mut minx = i32::MAX;
        let mut minz = i32::MAX;
        let mut maxx = i32::MIN;
        let mut maxz = i32::MIN;
        for c in &self.cols {
            minx = minx.min(c.min[0]);
            minz = minz.min(c.min[2]);
            maxx = maxx.max(c.max[0]);
            maxz = maxz.max(c.max[2]);
        }
        let s = self.hdr.cell_shift as u32;
        let cs = 1i32 << s;
        minx -= cs;
        minz -= cs;
        let ncx = ((maxx - minx) >> s) + 2;
        let ncz = ((maxz - minz) >> s) + 2;
        self.hdr.origin_x = minx;
        self.hdr.origin_z = minz;
        self.hdr.ncx = ncx as u16;
        self.hdr.ncz = ncz as u16;
        let mut cells: Vec<u32> = Vec::new();
        let mut idx: Vec<u16> = Vec::new();
        for cz in 0..ncz {
            for cx in 0..ncx {
                let x0 = minx + (cx << s);
                let z0 = minz + (cz << s);
                let x1 = x0 + cs;
                let z1 = z0 + cs;
                let start = idx.len() as u32;
                let mut cnt = 0u32;
                for (i, c) in self.cols.iter().enumerate() {
                    if c.max[0] >= x0 && c.min[0] < x1 && c.max[2] >= z0 && c.min[2] < z1 {
                        idx.push(i as u16);
                        cnt += 1;
                    }
                }
                assert!(cnt <= 255);
                cells.push((start << 8) | cnt);
            }
        }
        self.hdr.ncol = self.cols.len() as u16;
        self.hdr.nvert = self.verts.len() as u16;
        self.hdr.ndyn = self.dyns.len() as u16;
        self.hdr.nidx = idx.len() as u32;
        let mut out: Vec<u8> = Vec::new();
        let pad = |v: &mut Vec<u8>| {
            while v.len() % 4 != 0 {
                v.push(0)
            }
        };
        out.extend_from_slice(bytemuck::bytes_of(&self.hdr));
        out.extend_from_slice(bytemuck::cast_slice(&self.cols));
        out.extend_from_slice(bytemuck::cast_slice(&self.verts));
        out.extend_from_slice(bytemuck::cast_slice(&self.dyns));
        out.extend_from_slice(bytemuck::cast_slice(&cells));
        out.extend_from_slice(bytemuck::cast_slice(&idx));
        pad(&mut out);
        let total = out.len() as u32;
        let o = core::mem::offset_of!(Header, total_len);
        out[o..o + 4].copy_from_slice(&total.to_le_bytes());
        let mut words = alloc::vec![0u32; out.len() / 4];
        bytemuck::cast_slice_mut::<u32, u8>(&mut words).copy_from_slice(&out);
        words
    }
}

pub fn bytes(words: &[u32]) -> &[u8] {
    bytemuck::cast_slice(words)
}

/// (a) Lid Flip-like arena, ~120 colliders.
pub fn lid_flip_like() -> Vec<u32> {
    let mut b = Builder::new(0);
    // 9x9 lids, 2.1 m wide with 0.1 m gaps, top at y = 0. Six "open" lids are dropped 2 m (holes).
    let open = [10usize, 22, 34, 40, 58, 70];
    for i in 0..81usize {
        let gx = (i % 9) as i32 - 4;
        let gz = (i / 9) as i32 - 4;
        let y = if open.contains(&i) { -2150 } else { -150 };
        b.obb([gx * 2200, y, gz * 2200], [1050, 150, 1050], 0);
    }
    // 5x5 upper tier north of the grid, top at +200 (reachable by step-up from the grid edge lids)
    for i in 0..25i32 {
        let gx = i % 5 - 2;
        let gz = i / 5;
        b.obb([gx * 1500, 50, 10600 + gz * 1500], [700, 150, 700], 0);
    }
    // 6 bumper prisms (octagons) standing on lids
    for k in 0..6u32 {
        let a = k * 1024 / 6 + 40;
        let r = 5500i64;
        b.prism([(cos1024(a) as i64 * r >> 15) as i32, 400, (sin1024(a) as i64 * r >> 15) as i32], 600, 8, 400, 0);
    }
    // 4 corner posts + 2 side rails (boxes) and 2 static capsule rails
    for (x, z) in [(-10500, -10500), (10500, -10500), (-10500, 10500), (10500, 10500)] {
        b.obb([x, 500, z], [300, 650, 300], 0);
    }
    b.obb([-10800, 200, 0], [150, 350, 9000], 0);
    b.obb([10800, 200, 0], [150, 350, 9000], 0);
    b.hcapsule([0, 350, -10800], 9000, 200, 0);
    b.hcapsule([0, 650, 17800], 3800, 200, 0);
    // sweeper: 18 m bar at knee height, one turn per 6 s
    b.sweeper([0, 450, 0], 9000, 250, 120, 0xA1);
    b.hdr.spawn = [0, 0, -1500];
    b.hdr.spawn_dx = 1500;
    b.hdr.kill_y = -6000;
    b.hdr.forward = 256;
    b.finish()
}

/// (b) Capsule Run-like course, ~300 colliders along +z.
pub fn capsule_run_like() -> Vec<u32> {
    let mut b = Builder::new(1);
    let mut rng = 0x5EED_C0DE_u64;
    let mut rnd = |m: u64| -> i32 {
        rng = mix64(rng);
        (rng % m) as i32
    };
    // 45 floor segments (16 m x 4 m), every 9th leaves a 1.2 m jump gap; tops at 0.
    for k in 0..45i32 {
        let z = k * 4000 + 2000;
        let half_z = if k % 9 == 8 { 1400 } else { 2000 };
        b.obb([0, -250, z - (2000 - half_z)], [8000, 250, half_z], 0);
    }
    // 90 side walls
    for k in 0..45i32 {
        let z = k * 4000 + 2000;
        b.obb([-8250, 600, z], [250, 600, 2000], 0);
        b.obb([8250, 600, z], [250, 600, 2000], 0);
    }
    // 100 obstacles: low steps (200 tall, step-up), blocks (800 tall), random yaw
    for k in 0..100i32 {
        let z = 8000 + k * 1650 + rnd(800) as i32;
        let x = rnd(13000) - 6500;
        let tall = k % 3 != 0;
        let hy = if tall { 400 } else { 100 };
        b.obb([x, hy, z], [400 + rnd(500), hy, 400 + rnd(500)], rnd(256) as u32);
    }
    // 15 stair steps (200 mm each) crossing the course at z ~ 120 m
    for k in 0..15i32 {
        let top = (k % 8) * 200 + 200;
        b.obb([0, top / 2, 120_000 + k * 800], [7600, top / 2, 400], 0);
    }
    // 24 beams overhead / mid-height bars on the walls
    for k in 0..24i32 {
        b.obb([rnd(12000) - 6000, 1600, 10_000 + k * 7000], [3000, 150, 200], rnd(1024) as u32);
    }
    // 20 prisms: 10 turntable discs (12-gon, low), 10 bumpers (octagon, tall)
    for k in 0..20i32 {
        let z = 14_000 + k * 8000;
        let x = rnd(10000) - 5000;
        if k % 2 == 0 {
            b.prism([x, 50, z], 1500, 12, 100, 0);
        } else {
            b.prism([x, 500, z], 500, 8, 500, rnd(128) as u32);
        }
    }
    // 6 static spheres (domes)
    for k in 0..6i32 {
        b.sphere([rnd(10000) - 5000, -200, 30_000 + k * 25_000], 900);
    }
    // dynamic: 2 sweepers + 6 rolling balls across the course
    b.sweeper([0, 450, 60_000], 7000, 250, 100, 0xB1);
    b.sweeper([0, 450, 150_000], 7000, 250, 80, 0xB2);
    for k in 0..6i32 {
        let z = 40_000 + k * 22_000;
        b.ball([-7000, 1000, z], [7000, 1000, z + 3000], 1000, 80 + (k as u16) * 10, 0xC0 + k as u32);
    }
    b.hdr.spawn = [0, 0, 2000];
    b.hdr.spawn_dx = 1200;
    b.hdr.kill_y = -6000;
    b.hdr.forward = 256;
    b.finish()
}
