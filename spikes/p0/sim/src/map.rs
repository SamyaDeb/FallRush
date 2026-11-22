//! Map blob: a flat little-endian byte format read zero-copy (bytemuck) by the sim.
//!
//! Layout (all sections 4-byte aligned, offsets from the blob start):
//!   Header (64 B)
//!   [Collider; ncol]     56 B each
//!   [Vert; nvert]        16 B each (prism verts in WORLD xz + outward edge normal Q12)
//!   [DynDef; ndyn]       40 B each (sweepers / rolling balls; pose = f(seed, tick))
//!   [u32; ncx*ncz]       grid cells: (start << 8) | count   (count <= 255)
//!   [u16; nidx]          collider indices per cell (padded to 4 B)
use bytemuck::{Pod, Zeroable};

pub const MAGIC: u32 = 0x5030_4D42; // "BM0P"

pub const K_BOX: u8 = 1;
pub const K_PRISM: u8 = 2;
pub const K_SPHERE: u8 = 3;
pub const K_CAPSULE: u8 = 4; // horizontal capsule (yaw), static

pub const D_SWEEPER: u8 = 1; // rotating horizontal capsule around a pivot
pub const D_BALL: u8 = 2; // sphere moving a -> b -> a (triangle wave)

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable, Debug, Default)]
pub struct Header {
    pub magic: u32,
    pub ncol: u16,
    pub nvert: u16,
    pub ndyn: u16,
    pub ncx: u16,
    pub ncz: u16,
    pub cell_shift: u16, // cell size = 1 << cell_shift mm (2048 ~ 2 m)
    pub origin_x: i32,
    pub origin_z: i32,
    pub nidx: u32,
    pub kill_y: i32,
    pub spawn: [i32; 3],
    pub spawn_dx: i32,  // spacing between slots on x
    pub forward: u32,   // bot forward direction (1/1024 turns)
    pub kind: u32,      // 0 = arena (Lid Flip-like), 1 = course (Capsule Run-like)
    pub total_len: u32, // blob length incl. header
    pub _pad: [u32; 2],
}

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable, Debug, Default)]
pub struct Collider {
    pub kind: u8,
    pub nverts: u8,
    pub vert_off: u16,
    pub cos: i16, // Q15 yaw (boxes / capsules)
    pub sin: i16,
    pub c: [i32; 3],
    /// box: half extents (local); sphere: [r,_,_]; prism: [_, half_y, _]; capsule: [half_len, r, _]
    pub h: [i32; 3],
    pub min: [i32; 3],
    pub max: [i32; 3],
}

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable, Debug, Default)]
pub struct Vert {
    pub x: i32,
    pub z: i32,
    pub nx: i32, // outward normal of edge (this -> next), Q12
    pub nz: i32,
}

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable, Debug, Default)]
pub struct DynDef {
    pub kind: u8,
    pub _p: u8,
    pub period: u16, // ticks per revolution / per a->b->a
    pub salt: u32,
    pub a: [i32; 3], // sweeper pivot / ball start
    pub b: [i32; 3], // sweeper: [arm_half_len,_,_] / ball end
    pub r: i32,
    pub _q: i32,
}

pub struct Map<'a> {
    pub hdr: &'a Header,
    pub cols: &'a [Collider],
    pub verts: &'a [Vert],
    pub dyns: &'a [DynDef],
    pub cells: &'a [u32],
    pub idx: &'a [u16],
}

#[derive(Debug)]
pub enum MapError {
    Short,
    Magic,
    Align,
}

fn take<'a, T: Pod>(b: &'a [u8], off: &mut usize, n: usize) -> Result<&'a [T], MapError> {
    let sz = core::mem::size_of::<T>() * n;
    let end = *off + sz;
    if end > b.len() {
        return Err(MapError::Short);
    }
    let s = bytemuck::try_cast_slice(&b[*off..end]).map_err(|_| MapError::Align)?;
    *off = (end + 3) & !3;
    Ok(s)
}

impl<'a> Map<'a> {
    /// Zero-copy view over a blob. `b` must be 4-byte aligned.
    pub fn parse(b: &'a [u8]) -> Result<Map<'a>, MapError> {
        let mut off = 0usize;
        let hdr: &[Header] = take(b, &mut off, 1)?;
        let hdr = &hdr[0];
        if hdr.magic != MAGIC {
            return Err(MapError::Magic);
        }
        let cols = take(b, &mut off, hdr.ncol as usize)?;
        let verts = take(b, &mut off, hdr.nvert as usize)?;
        let dyns = take(b, &mut off, hdr.ndyn as usize)?;
        let cells = take(b, &mut off, hdr.ncx as usize * hdr.ncz as usize)?;
        let idx = take(b, &mut off, hdr.nidx as usize)?;
        Ok(Map { hdr, cols, verts, dyns, cells, idx })
    }

    #[inline]
    pub fn cell_size(&self) -> i32 {
        1i32 << self.hdr.cell_shift
    }

    /// Cell range overlapped by an XZ box (clamped to the grid).
    #[inline]
    pub fn cell_range(&self, minx: i32, minz: i32, maxx: i32, maxz: i32) -> Option<(u32, u32, u32, u32)> {
        let s = self.hdr.cell_shift as u32;
        let ox = self.hdr.origin_x;
        let oz = self.hdr.origin_z;
        let ncx = self.hdr.ncx as i32;
        let ncz = self.hdr.ncz as i32;
        let x0 = (minx - ox) >> s;
        let x1 = (maxx - ox) >> s;
        let z0 = (minz - oz) >> s;
        let z1 = (maxz - oz) >> s;
        if x1 < 0 || z1 < 0 || x0 >= ncx || z0 >= ncz {
            return None;
        }
        let c = |v: i32, n: i32| -> u32 { crate::fx::clamp(v, 0, n - 1) as u32 };
        Some((c(x0, ncx), c(z0, ncz), c(x1, ncx), c(z1, ncz)))
    }
}

/// Hash of a blob (FNV-1a 64), the spike's "map content hash".
pub fn blob_hash(b: &[u8]) -> u64 {
    crate::fx::fnv1a(crate::fx::FNV_OFFSET, b)
}
