//! Chaos Drops (P4 task 3, doc 19 P4.4, doc 15 OC-9): spectators buy a map-wide event on the on-chain Final.
//!
//! Rules (fairness, OC-9):
//! - **map-wide**: an effect never looks at who is where or which slot bought it; it is a pure function of
//!   `(hazard seed, drop index, tick)` through the queued `(tick, kind, arg)` in `World::drop_queue`;
//! - **telegraphed**: the program queues a drop at `trigger = tick it appears in World + TELEGRAPH`; nothing happens
//!   before the trigger, so every client sees ≥ 0.77 s (23 ticks at 30 Hz) of warning, and a lid a drop flips is Lit
//!   for a further 1 s before it opens;
//! - **capped**: ≤ [`MAX_PER_ROUND`] per round, [`COOLDOWN_TICKS`] between purchases (program rules, `buy_drop`);
//! - **short**: every effect is over within [`duration`] ticks.
//!
//! The queue is part of `World` (its canonical bytes), so prediction stays exact: a client steps the same effects.

use crate::fx::{div_round, normalize_q15, yaw_dir_q15, ONE_Q15};
use crate::hazards::defs::LidGridDef;
use crate::hazards::lid::LidState;
use crate::vec::IVec3;
use crate::world::{QueuedDrop, DROP_QUEUE};

/// Drop kinds (`QueuedDrop::kind`; 0 = empty entry).
pub mod kind {
    pub const NONE: u8 = 0;
    /// 2 extra lid flips on the top grid (pattern from `arg`); inner 3 × 3 lids never flip.
    pub const LID_FRENZY: u8 = 1;
    /// 3 bumpers land on the top grid at seed-chosen lids and knock everyone near them outward.
    pub const BUMPER_RAIN: u8 = 2;
    /// A 2 s side push, one of 8 directions.
    pub const WIND_GUST: u8 = 3;
    /// The sweeper at 1.5× speed for 3 s.
    pub const SWEEPER_RUSH: u8 = 4;
    pub const COUNT: u8 = 4;
}

/// Ticks between the drop appearing in `World` and its effect (0.77 s at 30 Hz; OC-9 "0.75 s").
pub const TELEGRAPH: u32 = 23;
/// Most drops per round (program rule; the queue holds [`DROP_QUEUE`]).
pub const MAX_PER_ROUND: u8 = 3;
/// Ticks between two purchases (5 s at 30 Hz; program rule).
pub const COOLDOWN_TICKS: u32 = 150;
/// The sweeper only sweeps from hazard t30 600 on Lid Flip (`sweeperStartA/B`): Sweeper Rush is sold from then on.
pub const SWEEPER_RUSH_FROM_T30: i64 = 600;
/// The lid grid Lid Frenzy and Bumper Rain use (Lid Flip: grid 0 = the 9 × 9 top shelf).
pub const TOP_GRID: usize = 0;

// ---- Lid Frenzy: two flips on disjoint lid sets, the second 45 ticks after the first. Each flip forces its lids
// Lit 30 ticks (1 s warning) → open 30 → Lit 30 (re-warned) → back to the schedule, so whatever the schedule does
// next is again preceded by ≥ 1 s of Lit. Lids that would retire during the flip and the inner 3 × 3 never flip.
pub const FRENZY_LIT: i64 = 30;
pub const FRENZY_OPEN: i64 = 30;
pub const FRENZY_RELIT: i64 = 30;
pub const FRENZY_SECOND: i64 = 45;
pub const FRENZY_FLIP: i64 = FRENZY_LIT + FRENZY_OPEN + FRENZY_RELIT;
// ---- Bumper Rain: 3 impacts at the trigger tick; radius, knock (cm/s, 30 Hz ticks)
pub const BUMPERS: usize = 3;
pub const BUMPER_RADIUS_MM: i64 = 1500;
pub const BUMPER_BAND_MM: i32 = 2500;
pub const BUMPER_PUSH_CMS: i32 = 800;
pub const BUMPER_UP_CMS: i32 = 450;
pub const BUMPER_STUN30: i32 = 12;
pub const BUMPER_IMM30: i32 = 30;
// ---- Wind Gust: 60 ticks; velocity added per tick (mm/s) along one of 8 directions
pub const WIND_TICKS: u32 = 60;
pub const WIND_DV_MMS: i64 = 160;
// ---- Sweeper Rush: 90 ticks at +50 % angular speed
pub const RUSH_TICKS: u32 = 90;

/// RUSH chip prices per kind (whole RUSH; the program multiplies by 10⁶). Index = kind.
pub const PRICE_RUSH: [u64; 5] = [0, 50, 40, 30, 40];

/// Ticks from the trigger until the effect is over (visuals keep the queue entry until then).
pub const fn duration(k: u8) -> u32 {
    match k {
        kind::LID_FRENZY => (FRENZY_SECOND + FRENZY_FLIP) as u32,
        kind::BUMPER_RAIN => 1,
        kind::WIND_GUST => WIND_TICKS,
        kind::SWEEPER_RUSH => RUSH_TICKS,
        _ => 0,
    }
}

fn splitmix64(s: &mut u64) -> u64 {
    *s = s.wrapping_add(0x9E37_79B9_7F4A_7C15);
    let mut z = *s;
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
    z ^ (z >> 31)
}

/// The drop's `arg` byte from the round's hazard seed and the drop index (0-based this match) and kind. The program
/// computes it when it queues the drop; clients read it from `World`.
pub fn arg_for(hazard_seed: u64, index: u8, k: u8) -> u8 {
    let mut s = hazard_seed ^ (0xC4A0_5D20_u64 << 8 | index as u64) ^ ((k as u64) << 56);
    (splitmix64(&mut s) >> 24) as u8
}

/// Is kind `k` on sale at hazard time `t30` (30 Hz ticks since the RUSH tick)?
pub fn on_sale(k: u8, t30: i64) -> bool {
    match k {
        kind::LID_FRENZY | kind::BUMPER_RAIN | kind::WIND_GUST => t30 >= 0,
        kind::SWEEPER_RUSH => t30 >= SWEEPER_RUSH_FROM_T30,
        _ => false,
    }
}

/// Queues drop `index` of kind `k` into `w.drop_queue` (first empty entry) to trigger at `trigger`. Returns false when
/// the queue is full or the kind is invalid. Program only (`tick()` part 0 from the `DropQueue` account).
pub fn enqueue(q: &mut [QueuedDrop; DROP_QUEUE], k: u8, arg: u8, trigger: u32) -> bool {
    if k == kind::NONE || k > kind::COUNT {
        return false;
    }
    let mut i = 0;
    while i < DROP_QUEUE {
        if q[i].kind == kind::NONE {
            q[i] = QueuedDrop { tick: trigger, kind: k, arg };
            return true;
        }
        i += 1;
    }
    false
}

/// Queued (non-empty) entries.
pub fn queued(q: &[QueuedDrop; DROP_QUEUE]) -> usize {
    let mut n = 0;
    let mut i = 0;
    while i < DROP_QUEUE {
        if q[i].kind != kind::NONE {
            n += 1;
        }
        i += 1;
    }
    n
}

// ------------------------------------------------------------------ Lid Frenzy

/// Pattern `p` (0..8) on an n × n grid: is lid (row r, col c) in it? About a quarter of the lids each; the inner
/// 3 × 3 is excluded by the caller.
pub const fn frenzy_pattern(p: u8, r: i32, c: i32) -> bool {
    match p & 7 {
        0 => r % 3 == 0,
        1 => c % 3 == 0,
        2 => (r + c) % 4 == 0,
        3 => (r + 2 * c) % 4 == 1,
        4 => r % 3 == 2 && c % 2 == 0,
        5 => c % 3 == 2 && r % 2 == 0,
        6 => (r * 3 + c) % 4 == 2,
        _ => (r + c) % 3 == 1 && (r % 2 == c % 2),
    }
}

/// The two flips of a Lid Frenzy with `arg`: patterns (first, second).
pub const fn frenzy_patterns(arg: u8) -> (u8, u8) {
    let a = arg & 7;
    let mut b = (arg >> 3) & 7;
    if b == a {
        b = (a + 3) & 7;
    }
    (a, b)
}

/// The state Lid Frenzy forces on lid `lid` of grid `d` at hazard time `t30` for a drop triggered at `start_t30` with
/// `arg` (None = the schedule's own state). Flip k (start s_k): Lit [s, s+30), open [s+30, s+60), Lit [s+60, s+90).
/// A lid flips in at most one of the two (the second pattern skips the first's lids), never when inner, and never
/// when it enters its retire warning before the flip is over (so a Retired lid never shows solid and every retire
/// keeps its own 3 s warning).
pub fn frenzy_state(d: &LidGridDef, lid: i32, start_t30: i64, arg: u8, t30: i64) -> Option<LidState> {
    if d.inner(lid) || t30 < start_t30 || t30 >= start_t30 + FRENZY_SECOND + FRENZY_FLIP {
        return None;
    }
    let (pa, pb) = frenzy_patterns(arg);
    let r = lid / d.n;
    let c = lid % d.n;
    let in_a = frenzy_pattern(pa, r, c);
    let s = if in_a {
        start_t30
    } else if frenzy_pattern(pb, r, c) {
        start_t30 + FRENZY_SECOND
    } else {
        return None;
    };
    if t30 < s || t30 >= s + FRENZY_FLIP {
        return None;
    }
    let rt = d.retire_tick(lid) as i64;
    if rt != i32::MAX as i64 && rt - (d.retire_warn_ticks as i64) <= s + FRENZY_FLIP {
        return None;
    }
    Some(if t30 < s + FRENZY_LIT {
        LidState::Lit
    } else if t30 < s + FRENZY_LIT + FRENZY_OPEN {
        LidState::Flipped
    } else {
        LidState::Lit
    })
}

/// Lid Frenzy's forced state replaces the schedule's for its lids (eligibility in [`frenzy_state`] keeps Retired /
/// RetireWarn lids out).
pub fn overlay(_base: LidState, f: LidState) -> LidState {
    f
}

// ------------------------------------------------------------------ Bumper Rain

/// The `BUMPERS` lids (distinct, on grid `d`) a Bumper Rain with `arg` lands on.
pub fn bumper_lids(d: &LidGridDef, arg: u8, hazard_seed: u64) -> [i32; BUMPERS] {
    let n = d.count().max(1) as u64;
    let mut s = hazard_seed ^ ((arg as u64) << 40) ^ 0xB0B0_0001;
    let mut out = [-1i32; BUMPERS];
    let mut k = 0;
    let mut guard = 0;
    while k < BUMPERS && guard < 32 {
        let l = (splitmix64(&mut s) % n) as i32;
        let mut dup = false;
        let mut j = 0;
        while j < k {
            if out[j] == l {
                dup = true;
            }
            j += 1;
        }
        if !dup {
            out[k] = l;
            k += 1;
        }
        guard += 1;
    }
    out
}

/// Centre (mm, top surface) of lid `lid` of grid `d` (same placement as the map builder: row-major from the
/// grid origin, pitch = lid + gap, centred).
pub fn lid_center(d: &LidGridDef, lid: i32) -> IVec3 {
    let p = d.pitch();
    let m = d.mid();
    let r = lid / d.n;
    let c = lid % d.n;
    IVec3::new(d.origin_x + (c - m) * p, d.top, d.origin_z + (r - m) * p)
}

/// Bumper Rain knock for a player standing at `pos`: Some((Q15 push direction x, z)) when within the radius of an
/// impact and within the vertical band of the top shelf.
pub fn bumper_hit(d: &LidGridDef, arg: u8, hazard_seed: u64, pos: IVec3) -> Option<[i32; 2]> {
    let lids = bumper_lids(d, arg, hazard_seed);
    let mut k = 0;
    while k < BUMPERS {
        if lids[k] >= 0 {
            let c = lid_center(d, lids[k]);
            let dy = pos.y - c.y;
            if dy > -BUMPER_BAND_MM && dy < BUMPER_BAND_MM {
                let dx = (pos.x - c.x) as i64;
                let dz = (pos.z - c.z) as i64;
                if dx * dx + dz * dz < BUMPER_RADIUS_MM * BUMPER_RADIUS_MM {
                    if dx == 0 && dz == 0 {
                        return Some(yaw_dir_q15(arg));
                    }
                    let n = normalize_q15(dx, 0, dz);
                    return Some([n[0], n[2]]);
                }
            }
        }
        k += 1;
    }
    None
}

// ------------------------------------------------------------------ Wind Gust

/// The wind's unit direction (Q15 x, z): one of 8 headings from `arg`.
pub const fn wind_dir(arg: u8) -> [i32; 2] {
    yaw_dir_q15((arg & 7) * 32)
}

/// Velocity (mm/s, x and z) a Wind Gust adds this tick.
pub fn wind_dv(arg: u8) -> (i32, i32) {
    let d = wind_dir(arg);
    (div_round(d[0] as i64 * WIND_DV_MMS, ONE_Q15 as i64) as i32, div_round(d[1] as i64 * WIND_DV_MMS, ONE_Q15 as i64) as i32)
}

// ------------------------------------------------------------------ per-tick queries (World)

/// Wind this tick (sum of active gusts), mm/s per tick.
pub fn wind_at(q: &[QueuedDrop; DROP_QUEUE], tick: u32) -> (i32, i32) {
    let (mut x, mut z) = (0, 0);
    let mut i = 0;
    while i < DROP_QUEUE {
        let e = q[i];
        if e.kind == kind::WIND_GUST && tick >= e.tick && tick < e.tick + WIND_TICKS {
            let (dx, dz) = wind_dv(e.arg);
            x += dx;
            z += dz;
        }
        i += 1;
    }
    (x, z)
}

/// Extra sweeper time (t60 units) at `tick`: each Sweeper Rush adds half a tick of hazard time per tick for
/// [`RUSH_TICKS`] (1.5× angular speed), then keeps the offset (the bar does not jump back).
pub fn sweeper_warp60(q: &[QueuedDrop; DROP_QUEUE], tick: u32, t60_per_tick: i64) -> i64 {
    let mut w = 0i64;
    let mut i = 0;
    while i < DROP_QUEUE {
        let e = q[i];
        if e.kind == kind::SWEEPER_RUSH && tick > e.tick {
            let n = (tick - e.tick).min(RUSH_TICKS) as i64;
            w += n * t60_per_tick / 2;
        }
        i += 1;
    }
    w
}

/// The Bumper Rain drops landing exactly at `tick` (bit i = queue entry i).
pub fn bumpers_at(q: &[QueuedDrop; DROP_QUEUE], tick: u32) -> u8 {
    let mut m = 0u8;
    let mut i = 0;
    while i < DROP_QUEUE {
        if q[i].kind == kind::BUMPER_RAIN && q[i].tick == tick {
            m |= 1 << i;
        }
        i += 1;
    }
    m
}

/// Lid Frenzy starts in hazard t30 (`round_start` = the RUSH tick; 30 Hz), for the scene overlay: up to 4.
pub fn frenzies(q: &[QueuedDrop; DROP_QUEUE], round_start: u32, rate: u8) -> ([i64; 4], [u8; 4], u8) {
    let mut s = [0i64; 4];
    let mut a = [0u8; 4];
    let mut n = 0u8;
    let mul = (30 / rate.max(1) as i64).max(1);
    let mut i = 0;
    while i < DROP_QUEUE {
        if q[i].kind == kind::LID_FRENZY && (n as usize) < 4 {
            s[n as usize] = (q[i].tick as i64 - round_start as i64) * mul;
            a[n as usize] = q[i].arg;
            n += 1;
        }
        i += 1;
    }
    (s, a, n)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn top() -> LidGridDef {
        LidGridDef { origin_x: 0, origin_z: 0, top: 6000, n: 9, lid: 1800, gap: 60, ..Default::default() }
    }

    #[test]
    fn frenzy_never_flips_inner_and_keeps_safe_lids() {
        let d = top();
        for arg in 0..=255u8 {
            for t in -5..120 {
                let mut open = 0;
                for lid in 0..81 {
                    let s = frenzy_state(&d, lid, 0, arg, t);
                    if d.inner(lid) {
                        assert!(s.is_none());
                    }
                    if s == Some(LidState::Flipped) {
                        open += 1;
                    }
                }
                assert!(open <= 40, "arg {arg} t {t}: {open} open");
            }
        }
    }

    #[test]
    fn frenzy_warns_one_second_before_opening() {
        let d = top();
        for arg in [0u8, 7, 77, 200] {
            for lid in 0..81 {
                let mut lit_run = 0;
                let mut prev = LidState::Idle;
                for t in 0..300 {
                    let s = frenzy_state(&d, lid, 0, arg, t).unwrap_or(LidState::Idle);
                    if s == LidState::Flipped && prev != LidState::Flipped {
                        assert!(lit_run >= FRENZY_LIT, "lid {lid} opened at {t} after {lit_run} lit");
                    }
                    lit_run = if s == LidState::Lit { lit_run + 1 } else { 0 };
                    prev = s;
                }
            }
        }
    }

    #[test]
    fn bumpers_distinct_and_on_grid() {
        let d = top();
        for a in 0..=255u8 {
            let l = bumper_lids(&d, a, 0x1234_5678);
            assert!(l.iter().all(|&x| (0..81).contains(&x)));
            assert!(l[0] != l[1] && l[1] != l[2] && l[0] != l[2]);
        }
    }

    #[test]
    fn sweeper_warp_is_half_speed_extra_then_held() {
        let mut q = [QueuedDrop::default(); DROP_QUEUE];
        assert!(enqueue(&mut q, kind::SWEEPER_RUSH, 0, 100));
        assert_eq!(sweeper_warp60(&q, 100, 2), 0);
        assert_eq!(sweeper_warp60(&q, 101, 2), 1);
        assert_eq!(sweeper_warp60(&q, 190, 2), 90);
        assert_eq!(sweeper_warp60(&q, 500, 2), 90);
    }

    #[test]
    fn wind_8_directions_and_window() {
        let mut q = [QueuedDrop::default(); DROP_QUEUE];
        assert!(enqueue(&mut q, kind::WIND_GUST, 2, 50));
        assert_eq!(wind_at(&q, 49), (0, 0));
        let (x, z) = wind_at(&q, 50);
        assert!((x * x + z * z - (WIND_DV_MMS * WIND_DV_MMS) as i32).abs() < 400);
        assert_eq!(wind_at(&q, 50 + WIND_TICKS), (0, 0));
    }
}

#[cfg(test)]
mod parity_digest {
    use super::*;

    /// The digest the C# port (`ChaosDropFx`, Unity EditMode `ChaosDropFxTests`) must reproduce: Lid Frenzy states over
    /// args × hazard times × lids of a 9 × 9 grid with retirement rings, the sweeper warp, the bumper spots.
    pub fn digest() -> u64 {
        let mut d = LidGridDef { origin_x: 0, origin_z: 0, top: 6000, n: 9, lid: 1800, gap: 60, ring_retire_count: 3, retire_warn_ticks: 90, ..Default::default() };
        d.ring_retire_ticks[..3].copy_from_slice(&[750, 1500, 2250]);
        let mut h = crate::vectors::Fnv::default();
        for arg in [0u8, 7, 77, 200, 255] {
            for start in [0i64, 700] {
                let mut t = start - 5;
                while t < start + 150 {
                    for lid in 0..81 {
                        let v = frenzy_state(&d, lid, start, arg, t).map(|s| s as u8 + 1).unwrap_or(0);
                        h.bytes(&[v]);
                    }
                    t += 7;
                }
            }
            let b = bumper_lids(&d, arg, 0x1234_5678_9ABC_DEF0);
            for x in b {
                h.bytes(&x.to_le_bytes());
            }
        }
        h.0
    }

    #[test]
    fn print_digest() {
        println!("CHAOS_DROP_DIGEST {:016x}", digest());
    }
}
