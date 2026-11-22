//! Hazards (port of `Shared/Hazards/HazardMath.cs` (+ `KitDefs`, `M4Defs`, `LidGridDef`, `RollingBallDef` partials),
//! `HazardSet.cs` effects, `HazardResolve.cs`, `HazardTuning` in `ModifierEffects.cs`, `ObstaclePhase` in
//! `RoundParams.cs`). All 14 kit types; every pose is a pure function of `(def, variant, seed, t)`.
//!
//! **Hazard time.** C# hazard time is in 30 Hz ticks. rush-core counts hazard time in **1/60 s** (`t60`); every
//! supported rate (15, 20, 30 Hz) divides 60, so `t60 = (tick − roundStart) · 60/rate` is exact. Defs keep their
//! authored 30 Hz tick values (= ms timing, unchanged). Two sampling rules:
//! - *continuous poses* (platform, spinner, mallet, turn table, rolling ball, goo, conveyor belt) use `t60` exactly
//!   (at 30 Hz this is bit-identical to C#: `(2m)·65536 / (2p) = m·65536 / p`);
//! - *discrete schedules* (doors, lid waves, crumble, falling blocks, star waves) use `t30 = ⌊t60 / 2⌋`, i.e. the
//!   C# schedule sampled at each tick (a change is seen at the first tick at or after its time).
//!
//! **Trig.** C# uses a Q30 quarter-wave table; rush-core uses the shared 1,024-entry Q15 table (P1 prompt B.2). The
//! pose error this adds is tabled in the proof (`hazard_v1_vs_v2`).
//!
//! **State that is not pure** (P1 prompt E.2): crumble tile contacts, falling-block triggers, collected and dropped
//! stars (the C# `ReactiveSet`). They live in `World` (`world::Reactive`), changed only by the rules in `step()`.

pub mod defs;
pub mod lid;
pub mod vectors;

use crate::config::MotorConfig;
use crate::fx::{cos_q15, div_round, floor_div, isqrt_i64, modp, normalize_q15, round_shift, sin_q15, ONE_Q15};
use crate::mapblob::MapView;
use crate::vec::IVec3;
use defs::*;

pub const PHASE_ONE: i64 = 65536;

/// `KnockProfile` (30 Hz ticks as authored; converted with `MotorConfig::ticks30`).
pub mod knock {
    pub const SOFT_STUN_TICKS: i32 = 11;
    pub const SOFT_PUSH_CMS: i32 = 600;
    pub const SOFT_UP_CMS: i32 = 350;
    pub const HARD_STUN_TICKS: i32 = 18;
    pub const HARD_PUSH_CMS: i32 = 900;
    pub const HARD_UP_CMS: i32 = 500;
    pub const IMMUNITY_AFTER_STUN_TICKS: i32 = 15;
    /// `HazardSet.DiveHitHeightMm`.
    pub const DIVE_HIT_HEIGHT_MM: i32 = 500;
}

// ------------------------------------------------------------------ seeds and phases

/// `HazardMath.PhaseFromSeed`.
pub const fn phase_from_seed(seed: u32) -> i32 {
    let mut x = seed.wrapping_mul(0x9E37_79B1);
    x ^= x >> 15;
    x = x.wrapping_mul(0x85EB_CA77);
    x ^= x >> 13;
    (x & 0xFFFF) as i32
}

/// `ObstaclePhase.Hash`: FNV-1a 32 over the seed's 8 LE bytes then the obstacle id bytes.
pub fn obstacle_hash(seed: u64, id: &[u8]) -> u32 {
    let mut h: u32 = 2_166_136_261;
    let mut i = 0;
    while i < 8 {
        h ^= (seed >> (8 * i)) as u8 as u32;
        h = h.wrapping_mul(16_777_619);
        i += 1;
    }
    let mut k = 0;
    while k < id.len() {
        h ^= id[k] as u32;
        h = h.wrapping_mul(16_777_619);
        k += 1;
    }
    h
}

/// `ObstaclePhase.Phase16`: phase in `[min, max]` (inclusive).
pub fn obstacle_phase16(seed: u64, id: &[u8], min16: i32, max16: i32) -> i32 {
    let (lo, hi) = if max16 < min16 { (max16, min16) } else { (min16, max16) };
    let raw = phase_from_seed(obstacle_hash(seed, id)) as i64;
    let span = (hi - lo + 1) as i64;
    lo + ((raw * span) >> 16) as i32
}

/// Hazard time of tick `tick` in 1/60 s (negative before the round start).
#[inline]
pub fn t60_of(tick: u32, round_start: u32, cfg: &MotorConfig) -> i64 {
    (tick as i64 - round_start as i64) * cfg.t60_per_tick as i64
}

/// ⌊t60 / 2⌋: the 30 Hz tick a discrete schedule is sampled at.
#[inline]
pub fn t30(t60: i64) -> i64 {
    floor_div(t60, 2)
}

/// `HazardMath.PhaseAt` at `t60` (period in 30 Hz ticks; negative turns the other way).
pub fn phase_at(t60: i64, period_ticks: i32, phase16: i32) -> i32 {
    if period_ticks == 0 {
        return phase16 & 0xFFFF;
    }
    let p = (period_ticks as i64).abs() * 2;
    let m = modp(t60, p);
    let mut within = m * PHASE_ONE / p; // floor (C#: m * PhaseOne / p, non-negative)
    if period_ticks < 0 {
        within = -within;
    }
    ((within + phase16 as i64) & 0xFFFF) as i32
}

/// round(v · q / 2^15) (the Q15 counterpart of `HazardMath.MulQ30`).
#[inline]
pub fn mul_q15(v: i64, q: i32) -> i64 {
    round_shift(v * q as i64, 15)
}

/// Phase16 (65536 = one turn) → the Q16-turn argument of `sin_q15` (the same unit).
#[inline]
fn sn(p: i32) -> i32 {
    sin_q15(p)
}

#[inline]
fn cs(p: i32) -> i32 {
    cos_q15(p)
}

// ------------------------------------------------------------------ modifier tuning (HazardTuning)

/// Hazard-scope modifier effects (`modifiers_v1.json`).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct HazardModifier {
    pub hazard_speed_permille: i32,
    pub telegraph_permille: i32,
    pub knockback_permille: i32,
}

impl HazardModifier {
    pub const NONE: HazardModifier = HazardModifier { hazard_speed_permille: 1000, telegraph_permille: 1000, knockback_permille: 1000 };
}

/// `HazardTuning.ScalePeriod`.
pub fn scale_period(period: i32, speed_pm: i32) -> i32 {
    if period == 0 || speed_pm == 1000 || speed_pm <= 0 {
        return period;
    }
    let a = (period as i64).abs();
    let mut r = ((2 * a * 1000 + speed_pm as i64) / (2 * speed_pm as i64)) as i32; // floor of non-negative = half up
    if r < 1 {
        r = 1;
    }
    if period < 0 {
        -r
    } else {
        r
    }
}

pub fn scale_duration(t: i32, s: i32) -> i32 {
    if t <= 0 {
        t
    } else {
        scale_period(t, s)
    }
}

pub fn scale_value(v: i32, pm: i32) -> i32 {
    crate::fx::permille(v as i64, pm as i64) as i32
}

// ------------------------------------------------------------------ resolved defs for a round

/// Reads the defs of one round (variant, hazard seed, modifier) straight from the blob: decode → seeded phase →
/// modifier rescaling (`MapInfo.BuildHazards` / `HazardAuthoring.Resolve`), on demand, no allocation.
#[derive(Clone, Copy)]
pub struct Hazards<'m> {
    pub map: MapView<'m>,
    pub variant: u8,
    pub seed: u64,
    pub m: HazardModifier,
    /// This tick's seeded phases, when built ([`PhaseTable`]; `None` = computed per decode, same values).
    pub ph: Option<&'m PhaseTable>,
}

/// Most hazards whose seeded phase / seed a [`PhaseTable`] keeps.
pub const MAX_PHASES: usize = 64;

/// The seeded phase (`phase16` after `Hazards::phase`) of every phased hazard, and each lid grid's seed, computed
/// once per tick (P2 0.2: the obstacle hash + header reads were ≈ 400 CU per def decode on sBPF, and the bots decode
/// the same defs thousands of times a tick). Same values as the per-decode path.
#[derive(Clone, Copy)]
pub struct PhaseTable {
    pub ok: bool,
    pub base: [u8; 16],
    pub v: [i32; MAX_PHASES],
    /// The resolved lid grids (with their seeds), `lids_n` of them (0 when the map has more than [`MAX_LID_DEFS`]).
    pub lids: [LidGridDef; MAX_LID_DEFS],
    pub lids_n: u8,
}

/// Lid grids a [`PhaseTable`] keeps resolved.
pub const MAX_LID_DEFS: usize = 2;

impl Default for PhaseTable {
    fn default() -> Self {
        PhaseTable { ok: false, base: [0; 16], v: [0; MAX_PHASES], lids: [LidGridDef::default(); MAX_LID_DEFS], lids_n: 0 }
    }
}

impl PhaseTable {
    /// Fills the table from `hz` (which must have `ph: None`).
    pub fn build(&mut self, hz: &Hazards) {
        self.ok = false;
        let mut k = 0usize;
        let mut t = 1u8;
        while t <= 14 {
            self.base[t as usize] = k.min(255) as u8;
            if let Some(ty) = HazardType::from_u8(t) {
                k += hz.count(ty);
            }
            t += 1;
        }
        if k > MAX_PHASES {
            return;
        }
        let fill = |tab: &mut PhaseTable, ty: HazardType, f: &dyn Fn(usize) -> i32| {
            let b = tab.base[ty as usize] as usize;
            let n = hz.count(ty);
            let mut i = 0;
            while i < n {
                tab.v[b + i] = f(i);
                i += 1;
            }
        };
        fill(self, HazardType::SpinnerBar, &|i| hz.sweeper(i).phase16);
        fill(self, HazardType::MovingPlatform, &|i| hz.platform(i).phase16);
        fill(self, HazardType::TurnTable, &|i| hz.turn_table(i).phase16);
        fill(self, HazardType::GateDoor, &|i| hz.door(i).phase16);
        fill(self, HazardType::MalletSwing, &|i| hz.mallet(i).phase16);
        fill(self, HazardType::RollingBall, &|i| hz.ball(i).phase16);
        fill(self, HazardType::LidFlip, &|i| hz.lid_grid(i).seed as i32);
        self.lids_n = 0;
        let nl = hz.count(HazardType::LidFlip);
        if nl <= MAX_LID_DEFS {
            let mut i = 0;
            while i < nl {
                self.lids[i] = hz.lid_grid(i);
                i += 1;
            }
            self.lids_n = nl as u8;
        }
        self.ok = true;
    }

    #[inline]
    fn get(&self, ty: HazardType, i: usize) -> Option<i32> {
        let k = self.base[ty as usize] as usize + i;
        if self.ok && k < MAX_PHASES {
            Some(self.v[k])
        } else {
            None
        }
    }
}

/// Obstacle header flag: keep the def's baked phase (C# M2 sweepers / platforms without an obstacle id).
pub const ID_BAKED_PHASE: u32 = 0x100;

impl<'m> Hazards<'m> {
    pub fn count(&self, ty: HazardType) -> usize {
        self.map.hazard_count(self.variant, ty)
    }

    /// The def words at the def's own size `N` (P2 0.2: a `MAX_DEF_WORDS` buffer is 1.1 KB to clear and copy).
    #[inline]
    fn words<const N: usize>(&self, ty: HazardType, i: usize) -> [i32; N] {
        let mut w = [0i32; N];
        self.map.hazard_words(self.variant, ty, i, &mut w);
        w
    }

    fn phase(&self, ty: HazardType, i: usize, baked: i32) -> i32 {
        if let Some(v) = self.ph.and_then(|t| t.get(ty, i)) {
            return v;
        }
        let o = self.map.hazard_obstacle(self.variant, ty, i);
        if self.baked(ty, i) {
            baked
        } else {
            obstacle_phase16(self.seed, o.id, o.phase_min, o.phase_max)
        }
    }

    fn baked(&self, ty: HazardType, i: usize) -> bool {
        self.map.hazard_id_len_raw(self.variant, ty, i) & ID_BAKED_PHASE != 0
    }

    pub fn sweeper(&self, i: usize) -> SweeperDef {
        let mut d = SweeperDef::read(&self.words::<{ SweeperDef::WORDS }>(HazardType::SpinnerBar, i));
        d.phase16 = self.phase(HazardType::SpinnerBar, i, d.phase16);
        d.period_ticks = scale_period(d.period_ticks, self.m.hazard_speed_permille);
        d
    }

    pub fn platform(&self, i: usize) -> PlatformDef {
        let mut d = PlatformDef::read(&self.words::<{ PlatformDef::WORDS }>(HazardType::MovingPlatform, i));
        d.phase16 = self.phase(HazardType::MovingPlatform, i, d.phase16);
        d.period_ticks = scale_period(d.period_ticks, self.m.hazard_speed_permille);
        d
    }

    pub fn conveyor(&self, i: usize) -> ConveyorDef {
        let mut d = ConveyorDef::read(&self.words::<{ ConveyorDef::WORDS }>(HazardType::Conveyor, i));
        let s = self.m.hazard_speed_permille;
        d.belt_x = scale_value(d.belt_x, s);
        d.belt_y = scale_value(d.belt_y, s);
        d.belt_z = scale_value(d.belt_z, s);
        d
    }

    pub fn pad(&self, i: usize) -> SpringPadDef {
        SpringPadDef::read(&self.words::<{ SpringPadDef::WORDS }>(HazardType::SpringPad, i))
    }

    pub fn turn_table(&self, i: usize) -> TurnTableDef {
        let mut d = TurnTableDef::read(&self.words::<{ TurnTableDef::WORDS }>(HazardType::TurnTable, i));
        d.phase16 = self.phase(HazardType::TurnTable, i, d.phase16);
        d.period_ticks = scale_period(d.period_ticks, self.m.hazard_speed_permille);
        d
    }

    pub fn door(&self, i: usize) -> GateDoorDef {
        let mut d = GateDoorDef::read(&self.words::<{ GateDoorDef::WORDS }>(HazardType::GateDoor, i));
        d.phase16 = self.phase(HazardType::GateDoor, i, d.phase16);
        let s = self.m.hazard_speed_permille;
        d.closed_ticks = scale_duration(d.closed_ticks, s);
        d.opening_ticks = scale_duration(d.opening_ticks, s);
        d.open_ticks = scale_duration(d.open_ticks, s);
        d.closing_ticks = scale_duration(d.closing_ticks, s);
        d.offset_ticks = scale_duration(d.offset_ticks, s);
        d.telegraph_ticks = scale_value(d.telegraph_ticks, self.m.telegraph_permille);
        d
    }

    pub fn mallet(&self, i: usize) -> MalletDef {
        let mut d = MalletDef::read(&self.words::<{ MalletDef::WORDS }>(HazardType::MalletSwing, i));
        d.phase16 = self.phase(HazardType::MalletSwing, i, d.phase16);
        d.period_ticks = scale_period(d.period_ticks, self.m.hazard_speed_permille);
        d.telegraph_ticks = scale_value(d.telegraph_ticks, self.m.telegraph_permille);
        d.knockback_cms = scale_value(d.knockback_cms, self.m.knockback_permille);
        d
    }

    pub fn bumper(&self, i: usize) -> BumperDef {
        let mut d = BumperDef::read(&self.words::<{ BumperDef::WORDS }>(HazardType::Bumper, i));
        d.knockback_cms = scale_value(d.knockback_cms, self.m.knockback_permille);
        d
    }

    pub fn goo(&self, i: usize) -> GooDef {
        GooDef::read(&self.words::<{ GooDef::WORDS }>(HazardType::GooPlane, i))
    }

    pub fn tile_grid(&self, i: usize) -> TileGridDef {
        TileGridDef::read(&self.words::<{ TileGridDef::WORDS }>(HazardType::CrumbleTile, i))
    }

    pub fn star_field(&self, i: usize) -> StarFieldDef {
        StarFieldDef::read(&self.words::<{ StarFieldDef::WORDS }>(HazardType::StarPickup, i))
    }

    pub fn block(&self, i: usize) -> FallingBlockDef {
        FallingBlockDef::read(&self.words::<{ FallingBlockDef::WORDS }>(HazardType::FallingBlock, i))
    }

    pub fn ball(&self, i: usize) -> RollingBallDef {
        let mut d = RollingBallDef::read(&self.words::<{ RollingBallDef::WORDS }>(HazardType::RollingBall, i));
        d.phase16 = self.phase(HazardType::RollingBall, i, d.phase16);
        let s = self.m.hazard_speed_permille;
        d.speed_mm_s = scale_value(d.speed_mm_s, s);
        d.period_ticks = scale_period(d.period_ticks, s);
        d.telegraph_ticks = scale_value(d.telegraph_ticks, self.m.telegraph_permille);
        d.knockback_cms = scale_value(d.knockback_cms, self.m.knockback_permille);
        d
    }

    pub fn lid_grid(&self, i: usize) -> LidGridDef {
        if let Some(t) = self.ph {
            if t.ok && i < t.lids_n as usize {
                return t.lids[i];
            }
        }
        let mut d = LidGridDef::read(&self.words::<{ LidGridDef::WORDS }>(HazardType::LidFlip, i));
        d.seed = match self.ph.and_then(|t| t.get(HazardType::LidFlip, i)) {
            Some(v) => v as u32,
            None => obstacle_hash(self.seed, self.map.hazard_obstacle(self.variant, HazardType::LidFlip, i).id),
        };
        d
    }

    /// Surface id spaces (`HazardSet`): platforms, turn tables, conveyors, spring pads.
    pub fn surface_kind(&self, id: i32) -> (u8, usize) {
        if id < 0 {
            return (0, 0);
        }
        let mut i = id as usize;
        let np = self.count(HazardType::MovingPlatform);
        if i < np {
            return (1, i);
        }
        i -= np;
        let nt = self.count(HazardType::TurnTable);
        if i < nt {
            return (2, i);
        }
        i -= nt;
        let nc = self.count(HazardType::Conveyor);
        if i < nc {
            return (3, i);
        }
        i -= nc;
        if i < self.count(HazardType::SpringPad) {
            return (4, i);
        }
        (0, 0)
    }

    /// Hazard time from which the spinner bars sweep (`HazardSet.SweeperStartT`, in 30 Hz ticks; i32::MIN = always).
    pub fn sweeper_start(&self) -> i32 {
        let a = self.map.meta(crate::mapblob::meta::SWEEPER_START_A);
        let b = self.map.meta(crate::mapblob::meta::SWEEPER_START_B);
        if self.variant == 1 && b != i32::MIN {
            b
        } else {
            a
        }
    }
}

// ------------------------------------------------------------------ moving platform

/// `PlatformCenterMm` at hazard time `t60`.
pub fn platform_center(d: &PlatformDef, t60: i64) -> IVec3 {
    let phase = phase_at(t60, d.period_ticks, d.phase16);
    if d.knot_count < 2 {
        let s = sn(phase);
        return IVec3::new(
            d.center_x + mul_q15(d.travel_x as i64, s) as i32,
            d.center_y + mul_q15(d.travel_y as i64, s) as i32,
            d.center_z + mul_q15(d.travel_z as i64, s) as i32,
        );
    }
    let n = d.knot_count as i64;
    let u = phase as i64 * n; // 16.16
    let seg = u >> 16;
    let f = u & 0xFFFF;
    let i0 = (((seg + n - 1) % n) * 3) as usize;
    let i1 = (seg * 3) as usize;
    let i2 = (((seg + 1) % n) * 3) as usize;
    let i3 = (((seg + 2) % n) * 3) as usize;
    let k = &d.knots;
    IVec3::new(
        d.center_x + catmull_rom(k[i0], k[i1], k[i2], k[i3], f),
        d.center_y + catmull_rom(k[i0 + 1], k[i1 + 1], k[i2 + 1], k[i3 + 1], f),
        d.center_z + catmull_rom(k[i0 + 2], k[i1 + 2], k[i2 + 2], k[i3 + 2], f),
    )
}

/// `HazardMath.CatmullRom` (Q16 Horner chain), exact port.
fn catmull_rom(p0: i32, p1: i32, p2: i32, p3: i32, f: i64) -> i32 {
    let (p0, p1, p2, p3) = (p0 as i64, p1 as i64, p2 as i64, p3 as i64);
    let a = 2 * p1;
    let b = p2 - p0;
    let c = 2 * p0 - 5 * p1 + 4 * p2 - p3;
    let e = -p0 + 3 * p1 - 3 * p2 + p3;
    let inner = (c << 16) + f * e;
    let mid = (b << 16) + round_shift(f * inner, 16);
    let v = (a << 16) + round_shift(f * mid, 16);
    round_shift(v, 17) as i32
}

/// `PlatformDelta`: how far the platform moved over the last tick (`step60` = 60/rate).
pub fn platform_delta(d: &PlatformDef, t60: i64, step60: i64) -> IVec3 {
    platform_center(d, t60).sub(platform_center(d, t60 - step60))
}

// ------------------------------------------------------------------ sweeper / spinner bar

/// `SweeperContactMm`: capsule (feet, r, h mm) vs the bar; the unit push direction (Q15, x/z) on contact.
pub fn sweeper_contact(d: &SweeperDef, t60: i64, f: IVec3, r: i32, h: i32) -> Option<[i32; 2]> {
    let p = phase_at(t60, d.period_ticks, d.phase16);
    let c = cs(p) as i64;
    let s = sn(p) as i64;
    let by = (d.pivot_y + d.bar_height) as i64;
    let y0 = (f.y + r) as i64;
    let y1 = (f.y + h - r) as i64;
    let dv = if by < y0 { y0 - by } else if by > y1 { by - y1 } else { 0 };
    let reach = (r + d.bar_radius) as i64;
    if dv >= reach {
        return None;
    }
    let qx = (f.x - d.pivot_x) as i64;
    let qz = (f.z - d.pivot_z) as i64;
    let tt = round_shift(qx * c + qz * s, 15).clamp(-(d.arm as i64), d.arm as i64);
    let ex = qx - round_shift(c * tt, 15);
    let ez = qz - round_shift(s * tt, 15);
    let dh2 = ex * ex + ez * ez;
    if dh2 + dv * dv >= reach * reach {
        return None;
    }
    let sign: i64 = (if d.period_ticks >= 0 { 1 } else { -1 }) * (if tt >= 0 { 1 } else { -1 });
    let (tx, tz) = ((-s * sign) as i32, (c * sign) as i32);
    let nrm = if dh2 > 0 { normalize_q15(ex, 0, ez) } else { [tx, 0, tz] };
    if d.tangential != 0 && ex * (-s) * sign + ez * c * sign >= 0 {
        Some([tx, tz])
    } else {
        Some([nrm[0], nrm[2]])
    }
}

// ------------------------------------------------------------------ conveyor

/// `ConveyorDeltaMm`: belt displacement over the last tick, cumulative rounding (never drifts). `t60` exact.
pub fn conveyor_delta(d: &ConveyorDef, t60: i64, step60: i64) -> IVec3 {
    let f = |v: i32| -> i32 { (div_round(v as i64 * t60, 60) - div_round(v as i64 * (t60 - step60), 60)) as i32 };
    IVec3::new(f(d.belt_x), f(d.belt_y), f(d.belt_z))
}

// ------------------------------------------------------------------ turn table

pub fn turn_table_phase(d: &TurnTableDef, t60: i64) -> i32 {
    phase_at(t60, d.period_ticks, d.phase16)
}

/// `TurnTableCarryMm`: the rider's offset is taken into the table frame at phase `p0`, snapped to the 2 mm grid
/// there, rotated to `p1` and rounded to mm.
pub fn turn_table_carry(center: IVec3, p0: i32, p1: i32, fx: i32, fz: i32) -> (i32, i32) {
    let ox = (fx - center.x) as i64;
    let oz = (fz - center.z) as i64;
    let (c0, s0) = (cs(p0) as i64, sn(p0) as i64);
    let lx = 2 * round_shift(ox * c0 + oz * s0, 16);
    let lz = 2 * round_shift(oz * c0 - ox * s0, 16);
    let (c1, s1) = (cs(p1) as i64, sn(p1) as i64);
    let wx = round_shift(lx * c1 - lz * s1, 15);
    let wz = round_shift(lx * s1 + lz * c1, 15);
    ((wx - ox) as i32, (wz - oz) as i32)
}

// ------------------------------------------------------------------ gate door (discrete: t30)

/// `DoorCycleTick`.
pub fn door_cycle_tick(d: &GateDoorDef, t30: i64) -> i64 {
    let c = d.cycle() as i64;
    if c <= 0 {
        return 0;
    }
    let seeded = ((d.phase16 & 0xFFFF) as i64 * c) >> 16;
    modp(t30 + d.offset_ticks as i64 + seeded, c)
}

/// `DoorLiftMm` at the 30 Hz tick `t30`.
pub fn door_lift(d: &GateDoorDef, t30: i64) -> i32 {
    if d.mode == 1 {
        return if t30 < 0 {
            0
        } else if t30 >= d.opening_ticks as i64 {
            d.lift
        } else {
            (d.lift as i64 * (t30 + 1) / (d.opening_ticks.max(1) as i64)) as i32 // floor (C#)
        };
    }
    let mut k = door_cycle_tick(d, t30);
    if k < d.closed_ticks as i64 {
        return 0;
    }
    k -= d.closed_ticks as i64;
    if k < d.opening_ticks as i64 {
        return (d.lift as i64 * (k + 1) / d.opening_ticks as i64) as i32; // floor (C#)
    }
    k -= d.opening_ticks as i64;
    if k < d.open_ticks as i64 {
        return d.lift;
    }
    k -= d.open_ticks as i64;
    (d.lift as i64 * (d.closing_ticks as i64 - 1 - k) / d.closing_ticks.max(1) as i64) as i32 // floor (C#)
}

/// `DoorTicksUntilClosing` (bots, telegraphs).
pub fn door_ticks_until_closing(d: &GateDoorDef, t30: i64) -> i64 {
    if d.mode == 1 {
        return if t30 < 0 { 0 } else { i32::MAX as i64 };
    }
    let k = door_cycle_tick(d, t30);
    let close_at = (d.closed_ticks + d.opening_ticks + d.open_ticks) as i64;
    if k < (d.closed_ticks + d.opening_ticks) as i64 || k < close_at {
        close_at - k
    } else {
        0
    }
}

pub fn door_center(d: &GateDoorDef, t30: i64) -> IVec3 {
    IVec3::new(d.center_x, d.center_y + door_lift(d, t30), d.center_z)
}

// ------------------------------------------------------------------ mallet

/// `MalletHeadMm`: head-axis centre and the sign of the head's velocity along the swing axis.
pub fn mallet_head(d: &MalletDef, t60: i64) -> (IVec3, i32) {
    let p = phase_at(t60, d.period_ticks, d.phase16);
    let a = mul_q15(d.amplitude_phase16 as i64, sn(p)) as i32;
    let off = mul_q15(d.arm as i64, sn(a)) as i32;
    let mut x = d.pivot_x;
    let mut z = d.pivot_z;
    let y = d.pivot_y - mul_q15(d.arm as i64, cs(a)) as i32;
    if d.swing_axis == 0 {
        x += off;
    } else {
        z += off;
    }
    let cv = cs(p);
    let eps = ONE_Q15 / 1000; // C#: Q30 / 1000
    let vs = (if d.period_ticks >= 0 { 1 } else { -1 }) * (if cv > eps { 1 } else if cv < -eps { -1 } else { 0 });
    (IVec3::new(x, y, z), vs)
}

/// `MalletContactMm`.
pub fn mallet_contact(d: &MalletDef, t60: i64, f: IVec3, r: i32, h: i32) -> Option<[i32; 2]> {
    let (hd, vs) = mallet_head(d, t60);
    let y0 = (f.y + r) as i64;
    let y1 = (f.y + h - r) as i64;
    let hy = hd.y as i64;
    let dv = if hy < y0 { y0 - hy } else if hy > y1 { hy - y1 } else { 0 };
    let reach = (r + d.head_radius) as i64;
    if dv >= reach {
        return None;
    }
    let (dw, du) = if d.swing_axis == 0 { ((f.x - hd.x) as i64, (f.z - hd.z) as i64) } else { ((f.z - hd.z) as i64, (f.x - hd.x) as i64) };
    let au = du.abs();
    let eu = if au > d.head_half_width as i64 { au - d.head_half_width as i64 } else { 0 };
    if eu * eu + dw * dw + dv * dv >= reach * reach {
        return None;
    }
    let dir = if vs != 0 { vs } else if dw >= 0 { 1 } else { -1 };
    Some(if d.swing_axis == 0 { [dir * ONE_Q15, 0] } else { [0, dir * ONE_Q15] })
}

// ------------------------------------------------------------------ bumper

/// `BumperContactMm`.
pub fn bumper_contact(d: &BumperDef, f: IVec3, r: i32, h: i32) -> Option<[i32; 2]> {
    let top = d.center_y + d.height;
    if f.y >= top || f.y + h <= d.center_y {
        return None;
    }
    let dx = (f.x - d.center_x) as i64;
    let dz = (f.z - d.center_z) as i64;
    let reach = (d.radius + d.contact_margin + r) as i64;
    let d2 = dx * dx + dz * dz;
    if d2 >= reach * reach {
        return None;
    }
    if d2 > 0 {
        let n = normalize_q15(dx, 0, dz);
        Some([n[0], n[2]])
    } else {
        Some([ONE_Q15, 0])
    }
}

// ------------------------------------------------------------------ spring pad

/// `OnSpringPad`.
pub fn on_spring_pad(d: &SpringPadDef, f: IVec3) -> bool {
    (f.x - d.center_x).abs() <= d.half_x && (f.z - d.center_z).abs() <= d.half_z && (f.y - d.center_y).abs() < 50
}

// ------------------------------------------------------------------ goo

/// `GooYMm` at `t60`: `y0 + v · max(0, t − delay)`, capped. floor (C#: `(long)v * k / TickRate`).
pub fn goo_y(d: &GooDef, t60: i64) -> i32 {
    let k = t60 - 2 * d.delay_ticks as i64;
    if k <= 0 {
        return d.y0;
    }
    let y = d.y0 as i64 + d.rise_mm_per_sec as i64 * k / 60;
    if y >= d.max_y as i64 {
        d.max_y
    } else {
        y as i32
    }
}

/// `GooTicksToReach` in 30 Hz ticks.
pub fn goo_ticks_to_reach(d: &GooDef, y: i32) -> i64 {
    if y <= d.y0 {
        return d.delay_ticks as i64;
    }
    if d.rise_mm_per_sec <= 0 || y > d.max_y {
        return i32::MAX as i64;
    }
    let k = ((y - d.y0) as i64 * 30 + d.rise_mm_per_sec as i64 - 1) / d.rise_mm_per_sec as i64;
    d.delay_ticks as i64 + k
}

// ------------------------------------------------------------------ rolling ball (t60 exact)

pub fn ball_travel_ticks(d: &RollingBallDef) -> i64 {
    let sp = d.speed_mm_s.max(1) as i64;
    (d.len_mm() * 30 + sp - 1) / sp
}

pub fn ball_phase_offset(d: &RollingBallDef) -> i64 {
    ((d.phase16 & 0xFFFF) as i64 * d.period_ticks.max(1) as i64) >> 16
}

pub fn ball_max_alive(d: &RollingBallDef) -> i64 {
    let p = d.period_ticks.max(1) as i64;
    (ball_travel_ticks(d) + p - 1) / p
}

/// The per-def constants of [`ball_pos`] (P2 0.2: `len_mm` is an `isqrt`, and `ball_pos` ran it twice per call).
#[derive(Clone, Copy, Default)]
pub struct BallK {
    pub p: i64,
    pub off: i64,
    pub travel60: i64,
    pub len: i64,
    pub speed: i64,
    pub start: IVec3,
    pub end: IVec3,
    pub radius: i32,
    pub alive: i64,
}

impl BallK {
    pub fn of(d: &RollingBallDef) -> BallK {
        let p = d.period_ticks.max(1) as i64;
        let len = d.len_mm();
        let sp = d.speed_mm_s.max(1) as i64;
        let travel = (len * 30 + sp - 1) / sp; // = ball_travel_ticks
        BallK {
            p,
            off: ball_phase_offset(d),
            travel60: 2 * travel,
            len,
            speed: sp,
            start: IVec3::new(d.start_x, d.start_y, d.start_z),
            end: IVec3::new(d.end_x, d.end_y, d.end_z),
            radius: d.radius,
            alive: (travel + p - 1) / p, // = ball_max_alive
        }
    }

    /// [`ball_pos`] with the constants precomputed (same values).
    #[inline]
    pub fn pos(&self, t60: i64, j: i64) -> Option<(IVec3, i64)> {
        let k = floor_div(t60 + 2 * self.off, 2 * self.p) - j;
        let tk60 = 2 * (k * self.p - self.off);
        let age60 = t60 - tk60;
        if j < 0 || age60 < 0 || age60 >= self.travel60 {
            return None;
        }
        let mut num = age60 * self.speed;
        let den = 60 * self.len;
        if num > den {
            num = den;
        }
        let lerp = |a: i32, b: i32| a + div_round((b - a) as i64 * num, den) as i32;
        Some((IVec3::new(lerp(self.start.x, self.end.x), lerp(self.start.y, self.end.y), lerp(self.start.z, self.end.z)), k))
    }
}

/// `BallPosMm` at `t60`: the `j`-th newest ball (0 = newest) and its release index.
pub fn ball_pos(d: &RollingBallDef, t60: i64, j: i64) -> Option<(IVec3, i64)> {
    BallK::of(d).pos(t60, j)
}

/// `RollingBallContactMm`: newest ball first; push = roll direction + half the lateral offset.
pub fn ball_contact(d: &RollingBallDef, t60: i64, f: IVec3, r: i32, h: i32) -> Option<[i32; 2]> {
    let bk = BallK::of(d);
    let n = bk.alive.min(64);
    let reach = (r + d.radius) as i64;
    let mut j = 0;
    while j < n {
        let (b, _) = bk.pos(t60, j)?;
        let y0 = (f.y + r) as i64;
        let y1 = (f.y + h - r) as i64;
        let by = b.y as i64;
        let dv = if by < y0 { y0 - by } else if by > y1 { by - y1 } else { 0 };
        if dv < reach {
            let dx = (f.x - b.x) as i64;
            let dz = (f.z - b.z) as i64;
            if dx * dx + dz * dz + dv * dv < reach * reach {
                return Some(ball_push(d, dx, dz, reach));
            }
        }
        j += 1;
    }
    None
}

fn ball_push(d: &RollingBallDef, dx: i64, dz: i64, reach: i64) -> [i32; 2] {
    let ux = (d.end_x - d.start_x) as i64;
    let uz = (d.end_z - d.start_z) as i64;
    if ux * ux + uz * uz < 1 {
        if dx * dx + dz * dz < 1 {
            return [ONE_Q15, 0];
        }
        let n = normalize_q15(dx, 0, dz);
        return [n[0], n[2]];
    }
    let u = normalize_q15(ux, 0, uz);
    let (ux, uz) = (u[0] as i64, u[2] as i64); // Q15
    let along = div_round(dx * ux + dz * uz, ONE_Q15 as i64); // mm
    let lx = dx - div_round(along * ux, ONE_Q15 as i64);
    let lz = dz - div_round(along * uz, ONE_Q15 as i64);
    // v = u + l · 0.5 / reach (Q15)
    let vx = ux + div_round(lx * ONE_Q15 as i64, 2 * reach);
    let vz = uz + div_round(lz * ONE_Q15 as i64, 2 * reach);
    let n = normalize_q15(vx, 0, vz);
    [n[0], n[2]]
}

/// `RollingBallTelegraph1000` (0..1000) at the 30 Hz tick `t30`.
pub fn ball_telegraph1000(d: &RollingBallDef, t30: i64) -> i32 {
    let p = d.period_ticks.max(1) as i64;
    if d.telegraph_ticks <= 0 {
        return 0;
    }
    let until = p - modp(t30 + ball_phase_offset(d), p);
    if until > d.telegraph_ticks as i64 {
        0
    } else {
        (1000 * (d.telegraph_ticks as i64 - until + 1) / d.telegraph_ticks as i64) as i32
    }
}

// ------------------------------------------------------------------ stars (pure timeline)

pub const STAR_GOLDEN_BASE: i32 = 512;
pub const STAR_DROP_BASE: i32 = 1024;

/// `StarHash`: FNV-1a over (seed, salt, index) + a finaliser.
pub fn star_hash(seed: u64, salt: u32, index: u32) -> u32 {
    let mut h: u32 = 2_166_136_261;
    let mut i = 0;
    while i < 8 {
        h ^= (seed >> (8 * i)) as u8 as u32;
        h = h.wrapping_mul(16_777_619);
        i += 1;
    }
    let mut i = 0;
    while i < 4 {
        h ^= (salt >> (8 * i)) as u8 as u32;
        h = h.wrapping_mul(16_777_619);
        i += 1;
    }
    let mut i = 0;
    while i < 4 {
        h ^= (index >> (8 * i)) as u8 as u32;
        h = h.wrapping_mul(16_777_619);
        i += 1;
    }
    h ^= h >> 15;
    h = h.wrapping_mul(0x2C1B_3C6D);
    h ^= h >> 12;
    h
}

/// A timeline star: anchor, spawn and expiry (30 Hz hazard ticks), value.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct StarSlot {
    pub anchor: i32,
    pub spawn_t: i32,
    pub expire_t: i32,
    pub value: i32,
}

/// `StarSlot`.
pub fn star_slot(d: &StarFieldDef, seed: u64, id: i32) -> Option<StarSlot> {
    let n = d.anchor_count;
    if n == 0 || d.wave_size <= 0 {
        return None;
    }
    if (STAR_GOLDEN_BASE..STAR_DROP_BASE).contains(&id) {
        let g = id - STAR_GOLDEN_BASE;
        if g < 1 || d.golden_count == 0 || d.golden_every_ticks <= 0 {
            return None;
        }
        let anchor = d.golden[(star_hash(seed, 0x601D, g as u32) % d.golden_count as u32) as usize];
        let spawn = g * d.golden_every_ticks;
        return Some(StarSlot { anchor, spawn_t: spawn, expire_t: spawn + d.golden_life_ticks, value: d.golden_value });
    }
    if !(0..STAR_GOLDEN_BASE).contains(&id) {
        return None;
    }
    let wave = id / d.wave_size;
    let k = id % d.wave_size;
    // the non-golden anchors, precomputed by `StarFieldDef::read` (same list; P2 0.2)
    let mut pool = d.pool;
    let mut m = d.pool_n as usize;
    if m == 0 {
        // a def not made by `read` (tests, vectors): build it here
        let mut i = 0;
        while i < n && m < pool.len() {
            if !d.is_golden_anchor(i) {
                pool[m] = i as u8;
                m += 1;
            }
            i += 1;
        }
    }
    if k as usize >= m {
        return None;
    }
    let mut i = 0usize;
    while i <= k as usize {
        let j = i + (star_hash(seed, 0x57A2, (wave * 64 + i as i32) as u32) % (m - i) as u32) as usize;
        pool.swap(i, j);
        i += 1;
    }
    let spawn = wave * d.wave_every_ticks;
    Some(StarSlot { anchor: pool[k as usize] as i32, spawn_t: spawn, expire_t: spawn + d.wave_life_ticks, value: 1 })
}

/// `StarAnchorMm` at `t60` (static, on a platform, or on a turn table).
pub fn star_anchor(d: &StarFieldDef, hz: &Hazards, a: i32, t60: i64) -> IVec3 {
    let ai = a as usize;
    let (mut x, mut y, mut z) = (d.anchors[ai * 3], d.anchors[ai * 3 + 1], d.anchors[ai * 3 + 2]);
    let att = if ai < MAX_ANCHORS { d.attach[ai] } else { -1 };
    if att >= 1000 {
        let ti = (att - 1000) as usize;
        if ti >= hz.count(HazardType::TurnTable) {
            return IVec3::new(x, y, z);
        }
        let tt = hz.turn_table(ti);
        let p = turn_table_phase(&tt, t60);
        let (c, s) = (cs(p) as i64, sn(p) as i64);
        let wx = round_shift(x as i64 * c - z as i64 * s, 15);
        let wz = round_shift(x as i64 * s + z as i64 * c, 15);
        x = tt.center_x + wx as i32;
        z = tt.center_z + wz as i32;
        y += tt.center_y;
    } else if att >= 0 {
        let pi = att as usize;
        if pi >= hz.count(HazardType::MovingPlatform) {
            return IVec3::new(x, y, z);
        }
        let pd = hz.platform(pi);
        let pc = platform_center(&pd, t60);
        x += pc.x;
        y += pc.y + pd.half_y;
        z += pc.z;
    }
    IVec3::new(x, y, z)
}

/// `StarTouch`.
pub fn star_touch(s: IVec3, f: IVec3, r: i32, hgt: i32, reach: i32) -> bool {
    if s.y < f.y - 300 || s.y > f.y + hgt + 300 {
        return false;
    }
    let dx = (s.x - f.x) as i64;
    let dz = (s.z - f.z) as i64;
    let rr = (r + reach) as i64;
    dx * dx + dz * dz <= rr * rr
}

/// `DropPosMm`: a dropped star's 1 m hop to its landing point over the pop time (`age` in 30 Hz ticks).
pub fn drop_pos(d: &StarFieldDef, p0: IVec3, seedish: u32, age: i64) -> IVec3 {
    let dir = (seedish.wrapping_mul(2_654_435_761) >> 24) as u8;
    let ex = p0.x + (crate::fx::yaw_sin(dir) as i64 * d.drop_distance as i64 / crate::fx::YAW_SCALE as i64) as i32; // C# int division
    let ez = p0.z + (crate::fx::yaw_cos(dir) as i64 * d.drop_distance as i64 / crate::fx::YAW_SCALE as i64) as i32;
    let pop = d.drop_pop_ticks.max(1) as i64;
    if age >= pop {
        return IVec3::new(ex, p0.y, ez);
    }
    IVec3::new(
        p0.x + ((ex - p0.x) as i64 * age / pop) as i32,
        p0.y + (4000 * age * (pop - age) / (pop * pop)) as i32,
        p0.z + ((ez - p0.z) as i64 * age / pop) as i32,
    )
}

// ------------------------------------------------------------------ helpers shared with bots / QA

/// Integer √ helper re-export for hazard callers.
pub fn isqrt(v: i64) -> i64 {
    isqrt_i64(v)
}
