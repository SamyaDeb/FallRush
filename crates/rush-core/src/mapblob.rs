//! Map blob format (read-only at runtime; doc 19 P1.6, P1 prompt K). Little-endian, versioned, section directory.
//!
//! ```text
//! header   magic "FRMB" | u16 version | u16 flags | u32 section count
//! dir      count × { u32 tag | u32 byte offset | u32 byte length }
//! sections (each 4-byte aligned, i32 words unless noted)
//! ```
//! The blob is built offline (`rush-sim mapblob build`, from the Unity exporter's `.mapjson`) with [`builder`], and
//! read in place by [`MapView`] (no allocation, random access by index), so the program can use a `MapBlob` account
//! directly.

use crate::hazards::defs::HazardType;
use crate::vec::IVec3;

pub const MAGIC: [u8; 4] = *b"FRMB";
pub const VERSION: u16 = 1;

/// Section tags.
pub mod tag {
    pub const META: u32 = 1;
    pub const COLLIDERS: u32 = 2;
    pub const PRISMS: u32 = 3;
    pub const GRID: u32 = 4;
    pub const GRID_IDX: u32 = 5;
    pub const SPAWNS: u32 = 6;
    pub const CHECKPOINTS: u32 = 7;
    pub const RESPAWNS: u32 = 8;
    pub const COURSE: u32 = 9;
    pub const HAZARDS_A: u32 = 10;
    pub const HAZARDS_B: u32 = 11;
    pub const BOT_ROUTES: u32 = 12;
    pub const NAME: u32 = 13;
    /// Colliders tested by every query: moving (platforms, doors) and very large static ones.
    pub const GLOBAL: u32 = 14;
}

/// META words.
pub mod meta {
    pub const MAP_ID: usize = 0;
    pub const MODE: usize = 1;
    pub const VARIANTS: usize = 2;
    pub const KILL_Y: usize = 3;
    pub const TIMER_CAP_S: usize = 4;
    pub const INTRO_MS: usize = 5;
    pub const COUNTDOWN_MS: usize = 6;
    pub const TARGET_MIN_S: usize = 7;
    pub const TARGET_MAX_S: usize = 8;
    pub const SWEEPER_START_A: usize = 9;
    pub const SWEEPER_START_B: usize = 10;
    pub const BOT_AREA_CX: usize = 11;
    pub const BOT_AREA_CZ: usize = 12;
    pub const BOT_AREA_HX: usize = 13;
    pub const BOT_AREA_HZ: usize = 14;
    pub const MAP_VERSION: usize = 15;
    pub const WORDS: usize = 16;
}

/// Map modes (byte in META).
pub mod mode {
    pub const SANDBOX: u8 = 0;
    pub const RACE: u8 = 1;
    pub const SURVIVAL: u8 = 2;
    pub const SKILL: u8 = 3;
    pub const FINAL: u8 = 4;
    pub const LOBBY: u8 = 5;
}

/// Collider shapes.
pub mod shape {
    pub const OBB: u8 = 1;
    pub const PRISM: u8 = 2;
    pub const SPHERE: u8 = 3;
    pub const CAPSULE: u8 = 4;
}

/// How a collider is posed / switched per tick.
pub mod dyn_kind {
    pub const STATIC: u8 = 0;
    /// Moved to `PlatformCenter(t)` (geometry stored relative to the platform transform).
    pub const PLATFORM: u8 = 1;
    /// Moved to `DoorCenter(t)` (geometry relative to the door transform).
    pub const DOOR: u8 = 2;
    /// Solid while lid `index` is solid (`LidSolid`).
    pub const LID: u8 = 3;
    /// Solid while crumble tile `index` is solid.
    pub const TILE: u8 = 4;
    /// Solid while falling block `index` is solid.
    pub const BLOCK: u8 = 5;
}

/// Words per collider record.
pub const COLLIDER_WORDS: usize = 24;
/// Words per prism vertex: x, z, nx, nz (Q15 outward edge normal from this vertex to the next), off lo, off hi.
pub const PRISM_VERTEX_WORDS: usize = 6;
/// Most prism vertices (Prism32 meshes; the prompt's 16 is raised to 32, see the proof §8).
pub const MAX_PRISM_VERTS: usize = 32;
/// Most colliders per map (query bitsets are sized for it).
pub const MAX_COLLIDERS: usize = 1024;
/// Hazard record header words: type, def words, phase min, phase max, id length, id (32 bytes = 8 words).
pub const HAZ_HDR_WORDS: usize = 13;
/// Largest def (StarFieldDef).
pub const MAX_DEF_WORDS: usize = crate::hazards::defs::StarFieldDef::WORDS;
/// Grid header words: cell, origin x/y/z, nx, ny, nz, cell count.
pub const GRID_HDR_WORDS: usize = 8;
/// Words per checkpoint: centre 3, half 3, course mm, respawn start, respawn count.
pub const CHECKPOINT_WORDS: usize = 9;

/// A decoded collider (copy).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Collider {
    pub kind: u8,
    pub dyn_kind: u8,
    pub dyn_index: u16,
    /// Surface id (`HazardSet` order), −1 = plain static geometry.
    pub surface: i32,
    pub aabb_min: IVec3,
    pub aabb_max: IVec3,
    /// OBB centre / sphere centre / capsule bottom point.
    pub center: IVec3,
    /// OBB half extents; sphere / capsule: x = radius.
    pub half: IVec3,
    /// OBB axes (Q15). Capsule: axes[0] = top point.
    pub axes: [IVec3; 3],
    /// Prism: first vertex word in PRISMS, vertex count, bottom and top y.
    pub prism_off: u32,
    pub prism_n: u32,
    pub y0: i32,
    pub y1: i32,
    /// Bit 0: OBB is yaw-only (axes[1] = +Y).
    pub flags: u32,
}

/// A read-only view of a map blob. `MapView::new` validates the header and caches the section offsets.
#[derive(Clone, Copy, Debug)]
pub struct MapView<'a> {
    pub data: &'a [u8],
    sec: [(u32, u32); 16],
    pub collider_count: u32,
    pub global_count: u32,
    haz_off: [[u32; 16]; 2],
    haz_cnt: [[u16; 16]; 2],
    haz_len_ok: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BlobError {
    TooShort,
    BadMagic,
    BadVersion,
    BadSection,
    TooManyColliders,
    BadHazards,
}

#[inline]
fn rd_u32(d: &[u8], o: usize) -> u32 {
    u32::from_le_bytes([d[o], d[o + 1], d[o + 2], d[o + 3]])
}

#[inline]
fn rd_i32(d: &[u8], o: usize) -> i32 {
    rd_u32(d, o) as i32
}

impl<'a> MapView<'a> {
    pub fn new(data: &'a [u8]) -> Result<MapView<'a>, BlobError> {
        if data.len() < 12 {
            return Err(BlobError::TooShort);
        }
        if data[0..4] != MAGIC {
            return Err(BlobError::BadMagic);
        }
        if u16::from_le_bytes([data[4], data[5]]) != VERSION {
            return Err(BlobError::BadVersion);
        }
        let n = rd_u32(data, 8) as usize;
        if n > 32 || data.len() < 12 + n * 12 {
            return Err(BlobError::BadSection);
        }
        let mut sec = [(0u32, 0u32); 16];
        let mut i = 0;
        while i < n {
            let t = rd_u32(data, 12 + i * 12);
            let off = rd_u32(data, 16 + i * 12);
            let len = rd_u32(data, 20 + i * 12);
            if (off as usize) + (len as usize) > data.len() || off % 4 != 0 || len % 4 != 0 {
                return Err(BlobError::BadSection);
            }
            if (t as usize) < 16 {
                sec[t as usize] = (off, len);
            }
            i += 1;
        }
        let mut v = MapView { data, sec, collider_count: 0, global_count: 0, haz_off: [[0; 16]; 2], haz_cnt: [[0; 16]; 2], haz_len_ok: true };
        v.collider_count = sec[tag::COLLIDERS as usize].1 / (COLLIDER_WORDS as u32 * 4);
        v.global_count = sec[tag::GLOBAL as usize].1 / 4;
        if v.collider_count as usize > MAX_COLLIDERS {
            return Err(BlobError::TooManyColliders);
        }
        // Index the hazard records of both variants by type (records of one type are contiguous, in HazardSet order).
        let mut var = 0;
        while var < 2 {
            let (off, len) = sec[(tag::HAZARDS_A + var as u32) as usize];
            let mut p = off as usize;
            let end = (off + len) as usize;
            while p < end {
                if p + HAZ_HDR_WORDS * 4 > end {
                    return Err(BlobError::BadHazards);
                }
                let ty = rd_u32(data, p) as usize;
                let words = rd_u32(data, p + 4) as usize;
                if ty == 0 || ty >= 16 || HazardType::from_u8(ty as u8).map(|t| t.words()) != Some(words) {
                    return Err(BlobError::BadHazards);
                }
                if v.haz_cnt[var][ty] == 0 {
                    v.haz_off[var][ty] = p as u32;
                }
                v.haz_cnt[var][ty] += 1;
                p += (HAZ_HDR_WORDS + words) * 4;
            }
            var += 1;
        }
        Ok(v)
    }

    #[inline]
    fn section(&self, t: u32) -> (usize, usize) {
        let (o, l) = self.sec[t as usize];
        (o as usize, l as usize)
    }

    /// Word `i` of section `t` (0 if out of range).
    #[inline]
    pub fn word(&self, t: u32, i: usize) -> i32 {
        let (o, l) = self.section(t);
        if i * 4 + 4 > l {
            0
        } else {
            rd_i32(self.data, o + i * 4)
        }
    }

    /// Number of words in section `t`.
    #[inline]
    pub fn words(&self, t: u32) -> usize {
        self.section(t).1 / 4
    }

    pub fn meta(&self, i: usize) -> i32 {
        self.word(tag::META, i)
    }

    pub fn map_id(&self) -> u8 {
        self.meta(meta::MAP_ID) as u8
    }

    pub fn mode(&self) -> i32 {
        self.meta(meta::MODE)
    }

    pub fn kill_y(&self) -> i32 {
        self.meta(meta::KILL_Y)
    }

    pub fn variants(&self) -> u8 {
        (self.meta(meta::VARIANTS) as u8).max(1)
    }

    pub fn collider(&self, i: u32) -> Collider {
        let (o, _) = self.section(tag::COLLIDERS);
        let b = o + i as usize * COLLIDER_WORDS * 4;
        // One bounds check for the whole record (P2 0.2: 24 checked word reads cost ≈ 1,000 CU on sBPF).
        let rec: &[u8; COLLIDER_WORDS * 4] = match self.data.get(b..b + COLLIDER_WORDS * 4) {
            Some(r) => r.try_into().unwrap_or(&[0; COLLIDER_WORDS * 4]),
            None => &[0; COLLIDER_WORDS * 4],
        };
        let w = |k: usize| i32::from_le_bytes([rec[k * 4], rec[k * 4 + 1], rec[k * 4 + 2], rec[k * 4 + 3]]);
        let v3 = |k: usize| IVec3::new(w(k), w(k + 1), w(k + 2));
        let h = w(0) as u32;
        let kind = (h & 0xFF) as u8;
        let mut c = Collider {
            kind,
            dyn_kind: ((h >> 8) & 0xFF) as u8,
            dyn_index: (h >> 16) as u16,
            surface: w(1),
            aabb_min: v3(2),
            aabb_max: v3(5),
            center: v3(8),
            half: v3(11),
            axes: [v3(14), v3(17), v3(20)],
            prism_off: 0,
            prism_n: 0,
            y0: 0,
            y1: 0,
            flags: w(23) as u32,
        };
        if kind == shape::PRISM {
            c.prism_off = w(11) as u32;
            c.prism_n = w(12) as u32;
            c.y0 = w(14);
            c.y1 = w(15);
            c.half = IVec3::ZERO;
            c.axes = [IVec3::ZERO; 3];
        }
        c
    }

    /// The broadphase fields of collider `i` only: (dyn_kind, dyn_index, aabb_min, aabb_max). Same values as
    /// [`Self::collider`], 7 words instead of 24 (P2 0.2).
    #[inline]
    pub fn collider_aabb(&self, i: u32) -> (u8, u16, IVec3, IVec3) {
        let (o, _) = self.section(tag::COLLIDERS);
        let b = o + i as usize * COLLIDER_WORDS * 4;
        let rec: &[u8; 32] = match self.data.get(b..b + 32) {
            Some(r) => r.try_into().unwrap_or(&[0; 32]),
            None => &[0; 32],
        };
        let w = |k: usize| i32::from_le_bytes([rec[k * 4], rec[k * 4 + 1], rec[k * 4 + 2], rec[k * 4 + 3]]);
        let h = w(0) as u32;
        (((h >> 8) & 0xFF) as u8, (h >> 16) as u16, IVec3::new(w(2), w(3), w(4)), IVec3::new(w(5), w(6), w(7)))
    }

    /// Prism vertex `k` of a collider: (x, z, nx, nz, off) with `off = n · v` (mm·Q15).
    #[inline]
    pub fn prism_vertex(&self, c: &Collider, k: u32) -> (i32, i32, i32, i32, i64) {
        let base = (c.prism_off + k * PRISM_VERTEX_WORDS as u32) as usize;
        // One bounds check per vertex record (as `word`: out of range reads as zeros).
        let (o, l) = self.section(tag::PRISMS);
        const N: usize = PRISM_VERTEX_WORDS * 4;
        let rec: &[u8; N] = if base * 4 + N <= l {
            match self.data.get(o + base * 4..o + base * 4 + N) {
                Some(r) => r.try_into().unwrap_or(&[0; N]),
                None => &[0; N],
            }
        } else {
            &[0; N]
        };
        let w = |k: usize| i32::from_le_bytes([rec[k * 4], rec[k * 4 + 1], rec[k * 4 + 2], rec[k * 4 + 3]]);
        let lo = w(4) as u32 as i64;
        let hi = w(5) as i64;
        (w(0), w(1), w(2), w(3), (hi << 32) | lo)
    }

    /// Global collider `k` (index into COLLIDERS).
    #[inline]
    pub fn global(&self, k: u32) -> u32 {
        self.word(tag::GLOBAL, k as usize) as u32
    }

    // ---- grid ----

    pub fn grid_header(&self) -> [i32; GRID_HDR_WORDS] {
        let mut h = [0; GRID_HDR_WORDS];
        let mut i = 0;
        while i < GRID_HDR_WORDS {
            h[i] = self.word(tag::GRID, i);
            i += 1;
        }
        h
    }

    /// The collider-index range of grid cell `id` (binary search over the sorted cell list), or `None`.
    pub fn grid_cell(&self, id: u32) -> Option<(u32, u32)> {
        let n = self.word(tag::GRID, 7) as u32;
        let (mut lo, mut hi) = (0u32, n);
        let mut guard = 0;
        while lo < hi && guard < 40 {
            let mid = (lo + hi) / 2;
            let base = GRID_HDR_WORDS + mid as usize * 3;
            let cid = self.word(tag::GRID, base) as u32;
            if cid == id {
                return Some((self.word(tag::GRID, base + 1) as u32, self.word(tag::GRID, base + 2) as u32));
            }
            if cid < id {
                lo = mid + 1;
            } else {
                hi = mid;
            }
            guard += 1;
        }
        None
    }

    /// The grid cells with ids in `[id0, id1]`, ascending, as (start, count) into `f` (P2 0.2: one lower-bound search
    /// per run of consecutive ids instead of one search per cell; same cells, same order).
    pub fn grid_cells_in(&self, id0: u32, id1: u32, mut f: impl FnMut(u32, u32)) {
        let (o, l) = self.section(tag::GRID);
        let sec = match self.data.get(o..o + l) {
            Some(x) => x,
            None => return,
        };
        let rd = |i: usize| -> u32 {
            match sec.get(i * 4..i * 4 + 4) {
                Some(b) => u32::from_le_bytes([b[0], b[1], b[2], b[3]]),
                None => 0,
            }
        };
        let n = rd(7);
        let (mut lo, mut hi) = (0u32, n);
        let mut guard = 0;
        while lo < hi && guard < 40 {
            let mid = (lo + hi) / 2;
            if rd(GRID_HDR_WORDS + mid as usize * 3) < id0 {
                lo = mid + 1;
            } else {
                hi = mid;
            }
            guard += 1;
        }
        let mut k = lo;
        while k < n {
            let b = GRID_HDR_WORDS + k as usize * 3;
            let cid = rd(b);
            if cid > id1 {
                break;
            }
            f(rd(b + 1), rd(b + 2));
            k += 1;
        }
    }

    #[inline]
    pub fn grid_index(&self, k: u32) -> u32 {
        self.word(tag::GRID_IDX, k as usize) as u32
    }

    // ---- spawns, checkpoints, course ----

    /// Spawn `slot`: feet (mm) and yaw256.
    pub fn spawn(&self, slot: usize) -> (IVec3, u8) {
        let b = (slot % 12) * 4;
        (
            IVec3::new(self.word(tag::SPAWNS, b), self.word(tag::SPAWNS, b + 1), self.word(tag::SPAWNS, b + 2)),
            self.word(tag::SPAWNS, b + 3) as u8,
        )
    }

    pub fn checkpoint_count(&self) -> usize {
        self.words(tag::CHECKPOINTS) / CHECKPOINT_WORDS
    }

    /// Checkpoint `i`: centre, half extents, course mm, respawn range.
    pub fn checkpoint(&self, i: usize) -> Checkpoint {
        let b = i * CHECKPOINT_WORDS;
        let w = |k: usize| self.word(tag::CHECKPOINTS, b + k);
        Checkpoint {
            center: IVec3::new(w(0), w(1), w(2)),
            half: IVec3::new(w(3), w(4), w(5)),
            course_mm: w(6),
            respawn_start: w(7) as u32,
            respawn_count: w(8) as u32,
        }
    }

    /// Respawn spot `k` (global index): feet and yaw.
    pub fn respawn(&self, k: u32) -> (IVec3, u8) {
        let b = k as usize * 4;
        (
            IVec3::new(self.word(tag::RESPAWNS, b), self.word(tag::RESPAWNS, b + 1), self.word(tag::RESPAWNS, b + 2)),
            self.word(tag::RESPAWNS, b + 3) as u8,
        )
    }

    pub fn course_len(&self) -> usize {
        self.words(tag::COURSE) / 3
    }

    pub fn course_point(&self, i: usize) -> IVec3 {
        IVec3::new(self.word(tag::COURSE, i * 3), self.word(tag::COURSE, i * 3 + 1), self.word(tag::COURSE, i * 3 + 2))
    }

    // ---- hazards ----

    /// Hazards of `ty` in `variant` (falls back to variant A when the blob has one variant).
    pub fn hazard_count(&self, variant: u8, ty: HazardType) -> usize {
        let v = if variant as usize >= 2 || self.sec[tag::HAZARDS_B as usize].1 == 0 { 0 } else { variant as usize };
        self.haz_cnt[v][ty as usize] as usize
    }

    /// Byte offset of hazard record `(variant, ty, i)`.
    fn hazard_rec(&self, variant: u8, ty: HazardType, i: usize) -> usize {
        let v = if variant as usize >= 2 || self.sec[tag::HAZARDS_B as usize].1 == 0 { 0 } else { variant as usize };
        self.haz_off[v][ty as usize] as usize + i * (HAZ_HDR_WORDS + ty.words()) * 4
    }

    /// The def words of hazard `(variant, ty, i)` into `out` (≥ `ty.words()`).
    pub fn hazard_words(&self, variant: u8, ty: HazardType, i: usize, out: &mut [i32]) {
        let p = self.hazard_rec(variant, ty, i) + HAZ_HDR_WORDS * 4;
        let n = ty.words().min(out.len());
        // One bounds check for the record.
        let rec = match self.data.get(p..p + n * 4) {
            Some(r) => r,
            None => return,
        };
        let mut k = 0;
        while k < n {
            out[k] = i32::from_le_bytes([rec[k * 4], rec[k * 4 + 1], rec[k * 4 + 2], rec[k * 4 + 3]]);
            k += 1;
        }
    }

    /// Obstacle header of hazard `(variant, ty, i)`: phase range and the obstacle id bytes.
    pub fn hazard_obstacle(&self, variant: u8, ty: HazardType, i: usize) -> Obstacle<'a> {
        let p = self.hazard_rec(variant, ty, i);
        let len = ((rd_u32(self.data, p + 16) & 0xFF) as usize).min(32);
        Obstacle { phase_min: rd_i32(self.data, p + 8), phase_max: rd_i32(self.data, p + 12), id: &self.data[p + 20..p + 20 + len] }
    }

    /// The raw id-length word of a hazard record (bit 8 = `hazards::ID_BAKED_PHASE`).
    pub fn hazard_id_len_raw(&self, variant: u8, ty: HazardType, i: usize) -> u32 {
        rd_u32(self.data, self.hazard_rec(variant, ty, i) + 16)
    }

    pub fn haz_len_ok(&self) -> bool {
        self.haz_len_ok
    }

    /// Bot route words (format owned by `bots`).
    pub fn bot_word(&self, i: usize) -> i32 {
        self.word(tag::BOT_ROUTES, i)
    }

    pub fn bot_words(&self) -> usize {
        self.words(tag::BOT_ROUTES)
    }

    /// The map key (UTF-8 bytes).
    pub fn name(&self) -> &'a [u8] {
        let (o, l) = self.section(tag::NAME);
        let mut n = l;
        while n > 0 && self.data[o + n - 1] == 0 {
            n -= 1;
        }
        &self.data[o..o + n]
    }
}

/// A checkpoint box (`CheckpointDef`).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Checkpoint {
    pub center: IVec3,
    pub half: IVec3,
    pub course_mm: i32,
    pub respawn_start: u32,
    pub respawn_count: u32,
}

impl Checkpoint {
    /// `CheckpointDef.Contains`.
    pub fn contains(&self, x: i32, y: i32, z: i32) -> bool {
        (x - self.center.x).abs() <= self.half.x && (y - self.center.y).abs() <= self.half.y && (z - self.center.z).abs() <= self.half.z
    }
}

/// A hazard's obstacle id and phase range (`HazardAuthoring`).
#[derive(Clone, Copy, Debug)]
pub struct Obstacle<'a> {
    pub phase_min: i32,
    pub phase_max: i32,
    pub id: &'a [u8],
}

#[cfg(any(test, feature = "builder"))]
pub mod builder;
#[cfg(any(test, feature = "builder"))]
pub mod testmaps;
