//! Hazard defs (ports of the C# def structs in `Shared/Hazards/HazardMath.cs`, `KitDefs.cs`, `M4Defs.cs`,
//! `LidGridDef.cs`, `RollingBallDef.cs`). Integer fields in the C# order and units (mm, cm/s, **30 Hz ticks**,
//! phase16). Arrays are fixed-size and inline (the map blob stores every def as a fixed number of `i32` words).
//!
//! Durations stay in 30 Hz ticks as authored ("hazard timing in ms" is never changed); `hazards::time` converts them
//! at the configured rate.

/// Kit type ids (= `HazardType`, part of the map content hash).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum HazardType {
    SpinnerBar = 1,
    MovingPlatform = 2,
    Conveyor = 3,
    SpringPad = 4,
    TurnTable = 5,
    GateDoor = 6,
    MalletSwing = 7,
    Bumper = 8,
    GooPlane = 9,
    CrumbleTile = 10,
    StarPickup = 11,
    FallingBlock = 12,
    RollingBall = 13,
    LidFlip = 14,
}

impl HazardType {
    pub const fn from_u8(v: u8) -> Option<HazardType> {
        Some(match v {
            1 => HazardType::SpinnerBar,
            2 => HazardType::MovingPlatform,
            3 => HazardType::Conveyor,
            4 => HazardType::SpringPad,
            5 => HazardType::TurnTable,
            6 => HazardType::GateDoor,
            7 => HazardType::MalletSwing,
            8 => HazardType::Bumper,
            9 => HazardType::GooPlane,
            10 => HazardType::CrumbleTile,
            11 => HazardType::StarPickup,
            12 => HazardType::FallingBlock,
            13 => HazardType::RollingBall,
            14 => HazardType::LidFlip,
            _ => return None,
        })
    }

    /// Words of the def record (after the 12-word obstacle header).
    pub const fn words(self) -> usize {
        match self {
            HazardType::SpinnerBar => SweeperDef::WORDS,
            HazardType::MovingPlatform => PlatformDef::WORDS,
            HazardType::Conveyor => ConveyorDef::WORDS,
            HazardType::SpringPad => SpringPadDef::WORDS,
            HazardType::TurnTable => TurnTableDef::WORDS,
            HazardType::GateDoor => GateDoorDef::WORDS,
            HazardType::MalletSwing => MalletDef::WORDS,
            HazardType::Bumper => BumperDef::WORDS,
            HazardType::GooPlane => GooDef::WORDS,
            HazardType::CrumbleTile => TileGridDef::WORDS,
            HazardType::StarPickup => StarFieldDef::WORDS,
            HazardType::FallingBlock => FallingBlockDef::WORDS,
            HazardType::RollingBall => RollingBallDef::WORDS,
            HazardType::LidFlip => LidGridDef::WORDS,
        }
    }
}

/// Scalar-only def structs: fields in order, read from / written to `i32` words.
macro_rules! scalar_def {
    ($(#[$m:meta])* $name:ident { $($f:ident),* $(,)? }) => {
        $(#[$m])*
        #[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
        pub struct $name { $(pub $f: i32),* }
        impl $name {
            pub const WORDS: usize = [$(stringify!($f)),*].len();
            pub fn read(w: &[i32]) -> Self {
                let mut _i = 0usize;
                $name { $($f: { let v = w[_i]; _i += 1; v }),* }
            }
            pub fn write(&self, out: &mut [i32]) {
                let mut _i = 0usize;
                $( out[_i] = self.$f; _i += 1; )*
            }
        }
    };
}

scalar_def!(
    /// `SweeperDef`: a bar rotating about a vertical axis (SPINNER_BAR).
    SweeperDef { pivot_x, pivot_y, pivot_z, arm, bar_radius, bar_height, period_ticks, phase16, knockback_cms, knock_up_cms, stun_ticks, immunity_ticks, tangential }
);
scalar_def!(
    /// `ConveyorDef`.
    ConveyorDef { center_x, center_y, center_z, half_x, half_y, half_z, pitch_cdeg, belt_x, belt_y, belt_z }
);
scalar_def!(
    /// `SpringPadDef`.
    SpringPadDef { center_x, center_y, center_z, half_x, half_z, thickness, launch_x_cms, launch_y_cms, launch_z_cms }
);
scalar_def!(
    /// `TurnTableDef`.
    TurnTableDef { center_x, center_y, center_z, radius, thickness, period_ticks, phase16 }
);
scalar_def!(
    /// `GateDoorDef`.
    GateDoorDef { center_x, center_y, center_z, half_x, half_y, half_z, lift, closed_ticks, opening_ticks, open_ticks, closing_ticks, offset_ticks, telegraph_ticks, phase16, mode }
);
scalar_def!(
    /// `MalletDef`.
    MalletDef { pivot_x, pivot_y, pivot_z, swing_axis, arm, head_radius, head_half_width, amplitude_phase16, period_ticks, phase16, telegraph_ticks, knockback_cms, knock_up_cms, stun_ticks, immunity_ticks }
);
scalar_def!(
    /// `BumperDef`.
    BumperDef { center_x, center_y, center_z, radius, height, contact_margin, knockback_cms, knock_up_cms, stun_ticks, immunity_ticks }
);
scalar_def!(
    /// `GooDef`.
    GooDef { y0, rise_mm_per_sec, delay_ticks, max_y, center_x, center_z, half_x, half_z, warn_ticks }
);
scalar_def!(
    /// `FallingBlockDef`.
    FallingBlockDef { center_x, center_y, center_z, half_x, half_y, half_z, delay_ticks, gone_ticks }
);
scalar_def!(
    /// `RollingBallDef` (`LenMm` is derived, not stored: [`RollingBallDef::len_mm`]).
    RollingBallDef { start_x, start_y, start_z, end_x, end_y, end_z, radius, speed_mm_s, period_ticks, phase16, telegraph_ticks, knockback_cms, knock_up_cms, stun_ticks, immunity_ticks }
);

/// Most knots of a looping platform path (C#: 2–8).
pub const MAX_KNOTS: usize = 8;

/// `PlatformDef`: a box on a sine path or a closed Catmull-Rom loop through knots.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct PlatformDef {
    pub center_x: i32,
    pub center_y: i32,
    pub center_z: i32,
    pub half_x: i32,
    pub half_y: i32,
    pub half_z: i32,
    pub travel_x: i32,
    pub travel_y: i32,
    pub travel_z: i32,
    pub period_ticks: i32,
    pub phase16: i32,
    /// Number of knots (0 = sine path).
    pub knot_count: i32,
    /// x, y, z per knot (mm offsets from the centre).
    pub knots: [i32; MAX_KNOTS * 3],
}

impl PlatformDef {
    pub const WORDS: usize = 12 + MAX_KNOTS * 3;
    pub fn read(w: &[i32]) -> Self {
        let mut d = PlatformDef {
            center_x: w[0],
            center_y: w[1],
            center_z: w[2],
            half_x: w[3],
            half_y: w[4],
            half_z: w[5],
            travel_x: w[6],
            travel_y: w[7],
            travel_z: w[8],
            period_ticks: w[9],
            phase16: w[10],
            knot_count: w[11].clamp(0, MAX_KNOTS as i32),
            knots: [0; MAX_KNOTS * 3],
        };
        d.knots.copy_from_slice(&w[12..12 + MAX_KNOTS * 3]);
        d
    }
    pub fn write(&self, o: &mut [i32]) {
        let s = [
            self.center_x,
            self.center_y,
            self.center_z,
            self.half_x,
            self.half_y,
            self.half_z,
            self.travel_x,
            self.travel_y,
            self.travel_z,
            self.period_ticks,
            self.phase16,
            self.knot_count,
        ];
        o[..12].copy_from_slice(&s);
        o[12..12 + MAX_KNOTS * 3].copy_from_slice(&self.knots);
    }
}

/// Most shelves of a crumble grid.
pub const MAX_SHELVES: usize = 4;

/// `TileGridDef` (CRUMBLE_TILE).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct TileGridDef {
    pub origin_x: i32,
    pub origin_z: i32,
    pub tile: i32,
    pub gap: i32,
    pub cols: i32,
    pub rows: i32,
    pub shelf_count: i32,
    pub shelf_tops: [i32; MAX_SHELVES],
    pub thickness: i32,
    pub wobble_ticks: i32,
    pub respawn_ticks: i32,
    pub respawn_shelves: i32,
}

impl TileGridDef {
    pub const WORDS: usize = 7 + MAX_SHELVES + 4;
    pub fn read(w: &[i32]) -> Self {
        let mut tops = [0; MAX_SHELVES];
        tops.copy_from_slice(&w[7..7 + MAX_SHELVES]);
        let o = 7 + MAX_SHELVES;
        TileGridDef {
            origin_x: w[0],
            origin_z: w[1],
            tile: w[2],
            gap: w[3],
            cols: w[4],
            rows: w[5],
            shelf_count: w[6].clamp(0, MAX_SHELVES as i32),
            shelf_tops: tops,
            thickness: w[o],
            wobble_ticks: w[o + 1],
            respawn_ticks: w[o + 2],
            respawn_shelves: w[o + 3],
        }
    }
    pub fn write(&self, out: &mut [i32]) {
        out[..7].copy_from_slice(&[self.origin_x, self.origin_z, self.tile, self.gap, self.cols, self.rows, self.shelf_count]);
        out[7..7 + MAX_SHELVES].copy_from_slice(&self.shelf_tops);
        let o = 7 + MAX_SHELVES;
        out[o..o + 4].copy_from_slice(&[self.thickness, self.wobble_ticks, self.respawn_ticks, self.respawn_shelves]);
    }
    pub const fn per_shelf(&self) -> i32 {
        self.cols * self.rows
    }
    pub const fn count(&self) -> i32 {
        self.per_shelf() * self.shelf_count
    }
    pub const fn shelf(&self, id: i32) -> i32 {
        if self.per_shelf() == 0 {
            0
        } else {
            id / self.per_shelf()
        }
    }
    /// `RespawnFor`: gone time before the tile returns (0 = never).
    pub const fn respawn_for(&self, id: i32) -> i32 {
        if self.respawn_ticks > 0 && self.shelf(id) < self.respawn_shelves {
            self.respawn_ticks
        } else {
            0
        }
    }
    /// `TileUnder`: the tile under grounded feet (±80 mm of a shelf top), or −1.
    pub fn tile_under(&self, fx: i32, fy: i32, fz: i32) -> i32 {
        let mut s = 0;
        while s < self.shelf_count as usize {
            if (fy - self.shelf_tops[s]).abs() > 80 {
                s += 1;
                continue;
            }
            let dx = fx - self.origin_x;
            let dz = fz - self.origin_z;
            if dx < 0 || dz < 0 || self.tile <= 0 {
                return -1;
            }
            let col = dx / self.tile; // floor (C#: int division of non-negatives)
            let row = dz / self.tile;
            if col >= self.cols || row >= self.rows {
                return -1;
            }
            return s as i32 * self.per_shelf() + row * self.cols + col;
        }
        -1
    }
}

/// Most star anchors (C#: the timeline pool is a 64-entry stackalloc).
pub const MAX_ANCHORS: usize = 64;
/// Most golden anchors.
pub const MAX_GOLDEN: usize = 16;

/// `StarFieldDef` (STAR_PICKUP).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct StarFieldDef {
    pub anchor_count: i32,
    pub anchors: [i32; MAX_ANCHORS * 3],
    /// −1 static, p = platform p, 1000 + t = turn table t.
    pub attach: [i32; MAX_ANCHORS],
    pub golden_count: i32,
    pub golden: [i32; MAX_GOLDEN],
    pub wave_every_ticks: i32,
    pub wave_size: i32,
    pub wave_life_ticks: i32,
    pub golden_every_ticks: i32,
    pub golden_life_ticks: i32,
    pub golden_value: i32,
    pub pickup_radius: i32,
    pub drop_pop_ticks: i32,
    pub drop_life_ticks: i32,
    pub drop_distance: i32,
    /// Derived on read (P2 0.2, not in the blob): the non-golden anchors in ascending order (`star_slot`'s pool).
    pub pool: [u8; MAX_ANCHORS],
    pub pool_n: u8,
}

impl Default for StarFieldDef {
    fn default() -> Self {
        StarFieldDef {
            anchor_count: 0,
            anchors: [0; MAX_ANCHORS * 3],
            attach: [-1; MAX_ANCHORS],
            golden_count: 0,
            golden: [0; MAX_GOLDEN],
            wave_every_ticks: 0,
            wave_size: 0,
            wave_life_ticks: 0,
            golden_every_ticks: 0,
            golden_life_ticks: 0,
            golden_value: 0,
            pickup_radius: 0,
            drop_pop_ticks: 0,
            drop_life_ticks: 0,
            drop_distance: 0,
            pool: [0; MAX_ANCHORS],
            pool_n: 0,
        }
    }
}

impl StarFieldDef {
    pub const WORDS: usize = 1 + MAX_ANCHORS * 3 + MAX_ANCHORS + 1 + MAX_GOLDEN + 10;
    pub fn read(w: &[i32]) -> Self {
        let mut d = StarFieldDef { anchor_count: w[0].clamp(0, MAX_ANCHORS as i32), ..Default::default() };
        let mut o = 1;
        d.anchors.copy_from_slice(&w[o..o + MAX_ANCHORS * 3]);
        o += MAX_ANCHORS * 3;
        d.attach.copy_from_slice(&w[o..o + MAX_ANCHORS]);
        o += MAX_ANCHORS;
        d.golden_count = w[o].clamp(0, MAX_GOLDEN as i32);
        o += 1;
        d.golden.copy_from_slice(&w[o..o + MAX_GOLDEN]);
        o += MAX_GOLDEN;
        d.wave_every_ticks = w[o];
        d.wave_size = w[o + 1];
        d.wave_life_ticks = w[o + 2];
        d.golden_every_ticks = w[o + 3];
        d.golden_life_ticks = w[o + 4];
        d.golden_value = w[o + 5];
        d.pickup_radius = w[o + 6];
        d.drop_pop_ticks = w[o + 7];
        d.drop_life_ticks = w[o + 8];
        d.drop_distance = w[o + 9];
        let mut i = 0;
        while i < d.anchor_count && (d.pool_n as usize) < MAX_ANCHORS {
            if !d.is_golden_anchor(i) {
                d.pool[d.pool_n as usize] = i as u8;
                d.pool_n += 1;
            }
            i += 1;
        }
        d
    }
    pub fn write(&self, out: &mut [i32]) {
        out[0] = self.anchor_count;
        let mut o = 1;
        out[o..o + MAX_ANCHORS * 3].copy_from_slice(&self.anchors);
        o += MAX_ANCHORS * 3;
        out[o..o + MAX_ANCHORS].copy_from_slice(&self.attach);
        o += MAX_ANCHORS;
        out[o] = self.golden_count;
        o += 1;
        out[o..o + MAX_GOLDEN].copy_from_slice(&self.golden);
        o += MAX_GOLDEN;
        out[o..o + 10].copy_from_slice(&[
            self.wave_every_ticks,
            self.wave_size,
            self.wave_life_ticks,
            self.golden_every_ticks,
            self.golden_life_ticks,
            self.golden_value,
            self.pickup_radius,
            self.drop_pop_ticks,
            self.drop_life_ticks,
            self.drop_distance,
        ]);
    }
    pub fn is_golden_anchor(&self, a: i32) -> bool {
        let mut i = 0;
        while i < self.golden_count as usize {
            if self.golden[i] == a {
                return true;
            }
            i += 1;
        }
        false
    }
}

/// Most stages / retire rings of a lid grid schedule.
pub const MAX_STAGES: usize = 8;

/// `LidGridDef` (LID_FLIP). `seed` is the per-obstacle seed (resolved per round, not stored in the blob).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct LidGridDef {
    pub origin_x: i32,
    pub origin_z: i32,
    pub top: i32,
    pub n: i32,
    pub lid: i32,
    pub gap: i32,
    pub thickness: i32,
    pub lit_ticks: i32,
    pub flipped_ticks: i32,
    pub first_wave_ticks: i32,
    pub wave_interval_count: i32,
    pub wave_intervals: [i32; MAX_STAGES],
    pub waves_per_stage: i32,
    pub flip_permille_count: i32,
    pub flip_permille: [i32; MAX_STAGES],
    pub ring_retire_count: i32,
    pub ring_retire_ticks: [i32; MAX_STAGES],
    pub retire_warn_ticks: i32,
    pub min_inner_solid: i32,
    pub loop_ticks: i32,
    pub seed: u32,
}

impl LidGridDef {
    pub const WORDS: usize = 10 + 1 + MAX_STAGES + 1 + 1 + MAX_STAGES + 1 + MAX_STAGES + 3;
    pub fn read(w: &[i32]) -> Self {
        let mut d = LidGridDef {
            origin_x: w[0],
            origin_z: w[1],
            top: w[2],
            n: w[3],
            lid: w[4],
            gap: w[5],
            thickness: w[6],
            lit_ticks: w[7],
            flipped_ticks: w[8],
            first_wave_ticks: w[9],
            ..Default::default()
        };
        let mut o = 10;
        d.wave_interval_count = w[o].clamp(0, MAX_STAGES as i32);
        o += 1;
        d.wave_intervals.copy_from_slice(&w[o..o + MAX_STAGES]);
        o += MAX_STAGES;
        d.waves_per_stage = w[o];
        o += 1;
        d.flip_permille_count = w[o].clamp(0, MAX_STAGES as i32);
        o += 1;
        d.flip_permille.copy_from_slice(&w[o..o + MAX_STAGES]);
        o += MAX_STAGES;
        d.ring_retire_count = w[o].clamp(0, MAX_STAGES as i32);
        o += 1;
        d.ring_retire_ticks.copy_from_slice(&w[o..o + MAX_STAGES]);
        o += MAX_STAGES;
        d.retire_warn_ticks = w[o];
        d.min_inner_solid = w[o + 1];
        d.loop_ticks = w[o + 2];
        d
    }
    pub fn write(&self, out: &mut [i32]) {
        out[..10].copy_from_slice(&[
            self.origin_x,
            self.origin_z,
            self.top,
            self.n,
            self.lid,
            self.gap,
            self.thickness,
            self.lit_ticks,
            self.flipped_ticks,
            self.first_wave_ticks,
        ]);
        let mut o = 10;
        out[o] = self.wave_interval_count;
        o += 1;
        out[o..o + MAX_STAGES].copy_from_slice(&self.wave_intervals);
        o += MAX_STAGES;
        out[o] = self.waves_per_stage;
        o += 1;
        out[o] = self.flip_permille_count;
        o += 1;
        out[o..o + MAX_STAGES].copy_from_slice(&self.flip_permille);
        o += MAX_STAGES;
        out[o] = self.ring_retire_count;
        o += 1;
        out[o..o + MAX_STAGES].copy_from_slice(&self.ring_retire_ticks);
        o += MAX_STAGES;
        out[o..o + 3].copy_from_slice(&[self.retire_warn_ticks, self.min_inner_solid, self.loop_ticks]);
    }
    pub const fn count(&self) -> i32 {
        self.n * self.n
    }
    pub const fn mid(&self) -> i32 {
        (self.n - 1) / 2
    }
    pub const fn pitch(&self) -> i32 {
        self.lid + self.gap
    }
    pub fn ring(&self, lid: i32) -> i32 {
        let c = lid % self.n - self.mid();
        let r = lid / self.n - self.mid();
        c.abs().max(r.abs())
    }
    pub fn inner(&self, lid: i32) -> bool {
        self.ring(lid) <= 1
    }
    /// `RetireTick` (i32::MAX = never).
    pub fn retire_tick(&self, lid: i32) -> i32 {
        let ring = self.ring(lid);
        let k = self.mid() - ring;
        if k >= 0 && k < self.ring_retire_count && ring >= 2 {
            self.ring_retire_ticks[k as usize]
        } else {
            i32::MAX
        }
    }
    /// `LidCenterMm`.
    pub fn lid_center(&self, lid: i32) -> (i32, i32, i32) {
        (
            self.origin_x + (lid % self.n - self.mid()) * self.pitch(),
            self.top - self.thickness / 2, // C#: int division (thickness even in every map)
            self.origin_z + (lid / self.n - self.mid()) * self.pitch(),
        )
    }
    /// `LidUnder`: the lid under the feet (±100 mm of the top), or −1.
    pub fn lid_under(&self, fx: i32, fy: i32, fz: i32) -> i32 {
        if (fy - self.top).abs() > 100 || self.pitch() <= 0 {
            return -1;
        }
        let half = self.n * self.pitch() / 2; // C#: int division
        let u = fx - (self.origin_x - half);
        let v = fz - (self.origin_z - half);
        if u < 0 || v < 0 {
            return -1;
        }
        let c = u / self.pitch();
        let r = v / self.pitch();
        if c < self.n && r < self.n {
            r * self.n + c
        } else {
            -1
        }
    }
}

impl RollingBallDef {
    /// `BallLenMm`: path length, integer sqrt, at least 1.
    pub fn len_mm(&self) -> i64 {
        let dx = (self.end_x - self.start_x) as i64;
        let dy = (self.end_y - self.start_y) as i64;
        let dz = (self.end_z - self.start_z) as i64;
        crate::fx::isqrt_i64(dx * dx + dy * dy + dz * dz).max(1)
    }
}

impl GateDoorDef {
    pub const fn cycle(&self) -> i32 {
        self.closed_ticks + self.opening_ticks + self.open_ticks + self.closing_ticks
    }
    /// `DoorNormalAxis`: 0 (X) or 2 (Z), the thinner half extent.
    pub const fn normal_axis(&self) -> i32 {
        if self.half_x <= self.half_z {
            0
        } else {
            2
        }
    }
}
