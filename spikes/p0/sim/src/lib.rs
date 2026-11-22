//! P0 spike sim (throw-away). Integer-only, no_std, no alloc in the hot path.
//! It is NOT rush-core: it exists to measure the compute-cost shape of the real motor
//! (swept capsule vs OBB / prism / sphere / capsule, ≤3 slides, step-up, ground snap,
//! depenetration, 66 player pairs, moving hazards) on sBPF, native and WASM.
#![no_std]
#![deny(clippy::float_arithmetic)]

#[cfg(feature = "gen")]
extern crate alloc;

pub mod collide;
pub mod fx;
#[cfg(feature = "gen")]
pub mod gen;
pub mod map;
mod trig_table;

use bytemuck::{Pod, Zeroable};
use collide::{probe_hcapsule, probe_sphere, probe_static, Raw, Seg};
use fx::{clamp, cos1024, isqrt, mix64, sin1024, ONE};
use map::{Map, D_BALL, D_SWEEPER};

pub const N: usize = 12;
pub const DT_MS: i64 = 50;
pub const TICK_HZ: i64 = 1000 / DT_MS;

// movement_v2.json, per tick at 20 Hz
pub const R: i32 = 310;
pub const H: i32 = 1000;
pub const SKIN: i32 = 10;
pub const RC: i32 = R + SKIN; // contact radius
pub const RUN: i32 = 5000;
pub const ACC_T: i32 = (28000 * DT_MS / 1000) as i32; // 1400 mm/s per tick
pub const DEC_T: i32 = (30000 * DT_MS / 1000) as i32;
pub const AIR_PERMILLE: i32 = 300;
pub const G_T: i32 = (20000 * DT_MS / 1000) as i32; // 1000
pub const FALL_PERMILLE: i32 = 1350;
pub const MAX_FALL: i32 = 30000;
pub const JUMP_V: i32 = 6928; // isqrt(2*20000*1200)
pub const STEP: i32 = 250;
pub const SNAP: i32 = 250;
pub const PUSH_T: i32 = (2500 * DT_MS / 1000) as i32; // 125 mm per tick
pub const COYOTE_T: u8 = 2;
pub const JBUF_T: u8 = 2;
pub const COS45_Q12: i32 = 2896;
pub const MAX_CANDS: usize = 96;
pub const MAX_DYN: usize = 8;
pub const TOI_ITERS: u32 = 6; // 1/64 resolution

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable, Debug, Default, PartialEq, Eq)]
pub struct Input {
    pub mx: i8,
    pub mz: i8,
    pub btn: u8, // bit0 jump
    pub yaw: u8,
}

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable, Debug, Default, PartialEq, Eq)]
pub struct Player {
    pub pos: [i32; 3],
    pub vel: [i32; 3],
    pub yaw: u8,
    pub grounded: u8,
    pub coyote: u8,
    pub jbuf: u8,
    pub last_btn: u8,
    pub respawns: u8,
    pub _pad: [u8; 2],
}

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable, Debug, Default, PartialEq, Eq)]
pub struct World {
    pub tick: u32,
    pub _pad: u32,
    pub seed: u64,
    pub players: [Player; N],
}

#[derive(Clone, Copy, Default, Debug)]
pub struct Stats {
    pub narrow: u32, // narrowphase primitive tests
    pub cands: u32,  // broadphase candidates (sum over players)
    pub sweeps: u32,
    pub hits: u32,
}

#[derive(Clone, Copy, Default)]
struct DynPose {
    kind: u8,
    ax: i32,
    az: i32,
    bx: i32,
    bz: i32,
    y: i32,
    r: i32,
}

pub fn world_hash(w: &World) -> u64 {
    fx::fnv1a(fx::FNV_OFFSET, bytemuck::bytes_of(w))
}

pub fn init_world(m: &Map, seed: u64) -> World {
    let mut w = World { tick: 0, _pad: 0, seed, players: [Player::default(); N] };
    for s in 0..N {
        w.players[s] = spawn_player(m, s);
    }
    w
}

fn spawn_player(m: &Map, s: usize) -> Player {
    let h = m.hdr;
    let col = (s % 6) as i32 - 3;
    let row = (s / 6) as i32;
    Player {
        pos: [h.spawn[0] + col * h.spawn_dx + h.spawn_dx / 2, h.spawn[1], h.spawn[2] + row * h.spawn_dx],
        grounded: 1,
        ..Player::default()
    }
}

fn dyn_poses(m: &Map, seed: u64, tick: u32, out: &mut [DynPose; MAX_DYN]) -> usize {
    let n = m.dyns.len().min(MAX_DYN);
    for (i, d) in m.dyns.iter().take(n).enumerate() {
        let period = d.period.max(2) as u64;
        let ph = (mix64(seed ^ d.salt as u64) % period + tick as u64) % period;
        match d.kind {
            D_SWEEPER => {
                let a = (ph * 1024 / period) as u32;
                let hl = d.b[0] as i64;
                let dx = ((cos1024(a) as i64 * hl) >> 15) as i32;
                let dz = ((sin1024(a) as i64 * hl) >> 15) as i32;
                out[i] = DynPose { kind: D_SWEEPER, ax: d.a[0] - dx, az: d.a[2] - dz, bx: d.a[0] + dx, bz: d.a[2] + dz, y: d.a[1], r: d.r };
            }
            _ => {
                // triangle wave a -> b -> a
                let half = period / 2;
                let f = if ph < half { ph } else { period - ph } as i64; // 0..half
                let hh = half.max(1) as i64;
                let x = d.a[0] + ((d.b[0] - d.a[0]) as i64 * f / hh) as i32;
                let y = d.a[1] + ((d.b[1] - d.a[1]) as i64 * f / hh) as i32;
                let z = d.a[2] + ((d.b[2] - d.a[2]) as i64 * f / hh) as i32;
                out[i] = DynPose { kind: D_BALL, ax: x, az: z, bx: x, bz: z, y, r: d.r };
            }
        }
    }
    n
}

struct Ctx<'a, 'm> {
    m: &'a Map<'m>,
    cands: [u16; MAX_CANDS],
    nc: usize,
    dyn_: &'a [DynPose],
    dsel: [u8; MAX_DYN],
    nds: usize,
    st: &'a mut Stats,
}

#[inline]
fn seg_at(p: &[i32; 3]) -> Seg {
    Seg { x: p[0], z: p[2], ya: p[1] + R, yb: p[1] + H - R }
}

impl<'a, 'm> Ctx<'a, 'm> {
    /// Broadphase: grid cells overlapped by the player's reach this tick, dedup with a bitset.
    fn gather(&mut self, p: &[i32; 3], reach: i32, ylo: i32, yhi: i32) {
        self.nc = 0;
        // moving hazards: AABB reject per player (pose is fixed for this tick)
        self.nds = 0;
        for (k, d) in self.dyn_.iter().enumerate() {
            let (x0, x1) = (d.ax.min(d.bx) - d.r, d.ax.max(d.bx) + d.r);
            let (z0, z1) = (d.az.min(d.bz) - d.r, d.az.max(d.bz) + d.r);
            if x1 < p[0] - reach || x0 > p[0] + reach || z1 < p[2] - reach || z0 > p[2] + reach || d.y + d.r < ylo || d.y - d.r > yhi {
                continue;
            }
            self.dsel[self.nds] = k as u8;
            self.nds += 1;
        }
        let mut seen = [0u64; 8]; // up to 512 colliders
        let (x0, z0, x1, z1) = match self.m.cell_range(p[0] - reach, p[2] - reach, p[0] + reach, p[2] + reach) {
            Some(r) => r,
            None => return,
        };
        let ncx = self.m.hdr.ncx as u32;
        for cz in z0..=z1 {
            for cx in x0..=x1 {
                let cell = self.m.cells[(cz * ncx + cx) as usize];
                let start = (cell >> 8) as usize;
                let cnt = (cell & 0xff) as usize;
                for &ci in &self.m.idx[start..start + cnt] {
                    let w = (ci >> 6) as usize;
                    let b = 1u64 << (ci & 63);
                    if w >= 8 || seen[w] & b != 0 {
                        continue;
                    }
                    seen[w] |= b;
                    let c = &self.m.cols[ci as usize];
                    // AABB reject (xz reach + y band)
                    if c.max[0] < p[0] - reach || c.min[0] > p[0] + reach || c.max[2] < p[2] - reach || c.min[2] > p[2] + reach || c.max[1] < ylo || c.min[1] > yhi {
                        continue;
                    }
                    if self.nc < MAX_CANDS {
                        self.cands[self.nc] = ci;
                        self.nc += 1;
                    }
                }
            }
        }
        self.st.cands += self.nc as u32;
    }

    #[inline]
    fn probe_i(&mut self, i: usize, s: &Seg) -> Raw {
        self.st.narrow += 1;
        if i < self.nc {
            probe_static(self.m, self.cands[i] as usize, s)
        } else {
            let d = &self.dyn_[self.dsel[i - self.nc] as usize];
            if d.kind == D_SWEEPER {
                probe_hcapsule(d.ax, d.az, d.bx, d.bz, d.y, d.r, s)
            } else {
                probe_sphere(d.ax, d.y, d.az, d.r, s)
            }
        }
    }

    #[inline]
    fn total(&self) -> usize {
        self.nc + self.nds
    }

    /// Swept capsule: earliest time of impact in 1/64 of `delta` and the contact normal.
    fn sweep(&mut self, pos: &[i32; 3], delta: &[i32; 3]) -> (i32, Option<[i32; 3]>) {
        self.st.sweeps += 1;
        let end = [pos[0] + delta[0], pos[1] + delta[1], pos[2] + delta[2]];
        let s1 = seg_at(&end);
        let s0 = seg_at(pos);
        let mut best_t = 64i32;
        let mut best_n: Option<[i32; 3]> = None;
        for i in 0..self.total() {
            let p1 = self.probe_i(i, &s1);
            if !p1.touch(RC) {
                continue;
            }
            let p0 = self.probe_i(i, &s0);
            let (t, n) = if p0.touch(RC) {
                let n0 = p0.finish().n;
                let dn = delta[0] as i64 * n0[0] as i64 + delta[1] as i64 * n0[1] as i64 + delta[2] as i64 * n0[2] as i64;
                if dn >= 0 {
                    continue;
                }
                (0, n0)
            } else {
                let mut lo = 0i32;
                let mut hi = 64i32;
                let mut hr = p1;
                for _ in 0..TOI_ITERS {
                    let mid = (lo + hi) >> 1;
                    let q = [pos[0] + delta[0] * mid / 64, pos[1] + delta[1] * mid / 64, pos[2] + delta[2] * mid / 64];
                    let pm = self.probe_i(i, &seg_at(&q));
                    if pm.touch(RC) {
                        hi = mid;
                        hr = pm;
                    } else {
                        lo = mid;
                    }
                }
                if lo >= best_t {
                    continue;
                }
                (lo, hr.finish().n)
            };
            if t < best_t {
                best_t = t;
                best_n = Some(n);
            }
        }
        if best_n.is_some() {
            self.st.hits += 1;
        }
        (best_t, best_n)
    }

    /// Move with up to 3 slide iterations. Returns (ground contact seen, any wall hit).
    fn slide_move(&mut self, pos: &mut [i32; 3], vel: &mut [i32; 3], delta: [i32; 3]) -> (bool, bool) {
        let mut rem = delta;
        let mut ground = false;
        let mut wall = false;
        for _ in 0..3 {
            if rem == [0, 0, 0] {
                break;
            }
            let (t, n) = self.sweep(pos, &rem);
            for k in 0..3 {
                pos[k] += rem[k] * t / 64;
            }
            let n = match n {
                None => break,
                Some(n) => n,
            };
            for k in 0..3 {
                rem[k] = rem[k] * (64 - t) / 64;
            }
            if n[1] > COS45_Q12 {
                ground = true;
            } else {
                wall = true;
            }
            let d = (rem[0] as i64 * n[0] as i64 + rem[1] as i64 * n[1] as i64 + rem[2] as i64 * n[2] as i64) >> 12;
            if d < 0 {
                for k in 0..3 {
                    rem[k] -= ((n[k] as i64 * d) >> 12) as i32;
                }
            }
            let dv = (vel[0] as i64 * n[0] as i64 + vel[1] as i64 * n[1] as i64 + vel[2] as i64 * n[2] as i64) >> 12;
            if dv < 0 {
                for k in 0..3 {
                    vel[k] -= ((n[k] as i64 * dv) >> 12) as i32;
                }
            }
        }
        (ground, wall)
    }

    fn depenetrate(&mut self, pos: &mut [i32; 3], vel: &mut [i32; 3]) {
        for _ in 0..2 {
            let s = seg_at(pos);
            let mut moved = false;
            for i in 0..self.total() {
                let r = self.probe_i(i, &s);
                if !r.touch(R) {
                    continue;
                }
                let p = r.finish();
                if p.dist < R {
                    let push = (RC - p.dist) as i64;
                    for k in 0..3 {
                        pos[k] += ((p.n[k] as i64 * push) >> 12) as i32;
                    }
                    let dv = (vel[0] as i64 * p.n[0] as i64 + vel[1] as i64 * p.n[1] as i64 + vel[2] as i64 * p.n[2] as i64) >> 12;
                    if dv < 0 {
                        for k in 0..3 {
                            vel[k] -= ((p.n[k] as i64 * dv) >> 12) as i32;
                        }
                    }
                    if i >= self.nc {
                        // hazard knockback (sweeper / ball)
                        vel[0] += p.n[0] * 3; // ~12000 mm/s along n (Q12 * 3)
                        vel[2] += p.n[2] * 3;
                        vel[1] = vel[1].max(3000);
                    }
                    moved = true;
                }
            }
            if !moved {
                break;
            }
        }
    }
}

/// One 50 ms step for all 12 players. Fixed slot order; returns nothing but mutates `w`.
pub fn step(w: &mut World, inputs: &[Input; N], m: &Map, st: &mut Stats) {
    step_part(w, inputs, m, st, 0, N, true);
}

/// Part of a step: players `lo..hi`; `finish` also runs the 66 pair pushes and advances the tick.
/// `step_part(0,6,false)` then `step_part(6,12,true)` is bit-identical to `step` (2-instruction tick).
pub fn step_part(w: &mut World, inputs: &[Input; N], m: &Map, st: &mut Stats, lo: usize, hi: usize, finish: bool) {
    let mut dyn_ = [DynPose::default(); MAX_DYN];
    let nd = dyn_poses(m, w.seed, w.tick, &mut dyn_);
    let mut ctx = Ctx { m, cands: [0; MAX_CANDS], nc: 0, dyn_: &dyn_[..nd], dsel: [0; MAX_DYN], nds: 0, st };

    for s in lo..hi {
        let inp = inputs[s];
        let mut p = w.players[s];
        // --- input -> horizontal velocity
        let mx = inp.mx as i32;
        let mz = inp.mz as i32;
        let mag = isqrt((mx * mx + mz * mz) as u64) as i32;
        let (tx, tz) = if mag > 0 {
            let m2 = mag.min(127);
            (mx * RUN / mag * m2 / 127, mz * RUN / mag * m2 / 127)
        } else {
            (0, 0)
        };
        let mut lim = if mag > 0 { ACC_T } else { DEC_T };
        if p.grounded == 0 {
            lim = lim * AIR_PERMILLE / 1000;
        }
        let dvx = tx - p.vel[0];
        let dvz = tz - p.vel[2];
        let dl = isqrt((dvx as i64 * dvx as i64 + dvz as i64 * dvz as i64) as u64) as i32;
        if dl <= lim {
            p.vel[0] = tx;
            p.vel[2] = tz;
        } else {
            p.vel[0] += (dvx as i64 * lim as i64 / dl as i64) as i32;
            p.vel[2] += (dvz as i64 * lim as i64 / dl as i64) as i32;
        }
        if mag > 0 {
            p.yaw = inp.yaw;
        }
        // --- jump (buffer + coyote)
        let pressed = inp.btn & 1 != 0 && p.last_btn & 1 == 0;
        p.last_btn = inp.btn;
        if pressed {
            p.jbuf = JBUF_T;
        } else if p.jbuf > 0 {
            p.jbuf -= 1;
        }
        if p.grounded != 0 {
            p.coyote = COYOTE_T;
        } else if p.coyote > 0 {
            p.coyote -= 1;
        }
        let mut jumped = false;
        if p.jbuf > 0 && p.coyote > 0 {
            p.vel[1] = JUMP_V;
            p.jbuf = 0;
            p.coyote = 0;
            p.grounded = 0;
            jumped = true;
        }
        // --- gravity
        if p.grounded == 0 {
            let g = if p.vel[1] < 0 { G_T * FALL_PERMILLE / 1000 } else { G_T };
            p.vel[1] = (p.vel[1] - g).max(-MAX_FALL);
        } else {
            p.vel[1] = p.vel[1].min(0);
        }
        let delta = [
            (p.vel[0] as i64 * DT_MS / 1000) as i32,
            (p.vel[1] as i64 * DT_MS / 1000) as i32 - if p.grounded != 0 && !jumped { SKIN } else { 0 },
            (p.vel[2] as i64 * DT_MS / 1000) as i32,
        ];
        // --- broadphase once per player per tick
        let reach = R + delta[0].abs().max(delta[2].abs()) + STEP + SKIN;
        let ylo = p.pos[1] + delta[1].min(0) - SNAP - SKIN;
        let yhi = p.pos[1] + H + delta[1].max(0) + STEP;
        ctx.gather(&p.pos, reach, ylo, yhi);

        let was_grounded = p.grounded != 0;
        let start = p.pos;
        let mut pos = p.pos;
        let mut vel = p.vel;
        let (_g, wall) = ctx.slide_move(&mut pos, &mut vel, delta);

        // --- step-up attempt: up, across, down
        let want_h = delta[0] as i64 * delta[0] as i64 + delta[2] as i64 * delta[2] as i64;
        let got_h = (pos[0] - start[0]) as i64 * (pos[0] - start[0]) as i64 + (pos[2] - start[2]) as i64 * (pos[2] - start[2]) as i64;
        let blocked = want_h > 100 && got_h * 4 < want_h; // < 50 % of the wanted distance
        if was_grounded && !jumped && (wall || blocked) {
            let mut q = start;
            let mut v2 = p.vel;
            let (t_up, _) = ctx.sweep(&q, &[0, STEP, 0]);
            q[1] += STEP * t_up / 64;
            let h = [delta[0], 0, delta[2]];
            let (t_h, _) = ctx.sweep(&q, &h);
            q[0] += h[0] * t_h / 64;
            q[2] += h[2] * t_h / 64;
            let (t_dn, n_dn) = ctx.sweep(&q, &[0, -(STEP + SKIN), 0]);
            q[1] -= (STEP + SKIN) * t_dn / 64;
            let prog_slide = (pos[0] - start[0]) as i64 * (pos[0] - start[0]) as i64 + (pos[2] - start[2]) as i64 * (pos[2] - start[2]) as i64;
            let prog_step = (q[0] - start[0]) as i64 * (q[0] - start[0]) as i64 + (q[2] - start[2]) as i64 * (q[2] - start[2]) as i64;
            if n_dn.is_some() && (q[1] > start[1] + SKIN || n_dn.map_or(false, |n| n[1] > COS45_Q12)) && prog_step > prog_slide {
                pos = q;
                v2[1] = 0;
                vel = v2;
            }
        }

        // grounded contacts never launch the player upward (edge / slope ride-up)
        if was_grounded && !jumped && vel[1] > 0 {
            vel[1] = 0;
        }
        // --- ground probe + snap: short capsule sweep down (like the motor's ground cast)
        let below = if was_grounded && !jumped { SNAP } else { SKIN };
        if vel[1] <= 0 {
            let (t, n) = ctx.sweep(&pos, &[0, -below, 0]);
            match n {
                Some(n) if n[1] > COS45_Q12 => {
                    pos[1] -= below * t / 64;
                    vel[1] = 0;
                    p.grounded = 1;
                }
                _ => p.grounded = 0,
            }
        } else {
            p.grounded = 0;
        }

        ctx.depenetrate(&mut pos, &mut vel);
        p.pos = pos;
        p.vel = vel;

        if p.pos[1] < m.hdr.kill_y {
            let r = p.respawns.wrapping_add(1);
            p = spawn_player(m, s);
            p.respawns = r;
        }
        w.players[s] = p;
    }

    if !finish {
        return;
    }
    // --- player vs player soft push (66 pairs)
    for a in 0..N {
        for b in (a + 1)..N {
            let pa = w.players[a].pos;
            let pb = w.players[b].pos;
            ctx.st.narrow += 1;
            if (pa[1] - pb[1]).abs() >= H {
                continue;
            }
            let dx = (pb[0] - pa[0]) as i64;
            let dz = (pb[2] - pa[2]) as i64;
            let d2 = dx * dx + dz * dz;
            let rr = (2 * R) as i64;
            if d2 >= rr * rr {
                continue;
            }
            let d = isqrt(d2 as u64) as i64;
            let (nx, nz) = if d == 0 { (ONE as i64, 0) } else { (dx * ONE as i64 / d, dz * ONE as i64 / d) };
            let push = clamp(((rr - d) / 2) as i32, 0, PUSH_T) as i64;
            w.players[a].pos[0] -= ((nx * push) >> 12) as i32;
            w.players[a].pos[2] -= ((nz * push) >> 12) as i32;
            w.players[b].pos[0] += ((nx * push) >> 12) as i32;
            w.players[b].pos[2] += ((nz * push) >> 12) as i32;
        }
    }
    w.tick = w.tick.wrapping_add(1);
}

/// Scripted bot input: random walk + periodic jumps. Deterministic in (slot, tick, seed, state).
pub fn bot_input(slot: usize, tick: u32, seed: u64, m: &Map, p: &Player) -> Input {
    let epoch = tick / 24;
    let h = mix64(seed ^ ((slot as u64) << 40) ^ epoch as u64);
    let hj = mix64(seed.rotate_left(17) ^ ((slot as u64) << 32) ^ tick as u64);
    let mut ang: u32;
    if m.hdr.kind == 0 {
        // arena: random heading, steer back toward the centre when far out
        ang = (h & 1023) as u32;
        let cx = m.hdr.spawn[0] as i64;
        let cz = m.hdr.spawn[2] as i64 + 3000;
        let dx = cx - p.pos[0] as i64;
        let dz = cz - p.pos[2] as i64;
        if dx * dx + dz * dz > 7000 * 7000 {
            // pick the 1/1024 heading closest to the centre by coarse search (16 dirs)
            let mut best = 0u32;
            let mut bd = i64::MIN;
            for k in 0..16u32 {
                let a = k * 64;
                let d = cos1024(a) as i64 * dx + sin1024(a) as i64 * dz;
                if d > bd {
                    bd = d;
                    best = a;
                }
            }
            ang = best.wrapping_add((h >> 12) as u32 % 64).wrapping_sub(32) & 1023;
        }
    } else {
        ang = m.hdr.forward.wrapping_add(((h & 511) as u32).wrapping_sub(256)) & 1023;
    }
    let idle = (h >> 20) % 10 == 0;
    let (mx, mz) = if idle {
        (0, 0)
    } else {
        ((cos1024(ang) * 127 / 32767) as i8, (sin1024(ang) * 127 / 32767) as i8)
    };
    let jump = hj % 30 == 0 || hj % 30 == 1;
    Input { mx, mz, btn: jump as u8, yaw: (ang >> 2) as u8 }
}
