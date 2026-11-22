//! LID_FLIP wave schedule and lid states (exact port of `LidGridDef.cs`'s `HazardMath` partial). Closed form,
//! integers only; time in 30 Hz ticks (`t30`, sampled per tick, see `hazards`). No allocation: scratch is a fixed
//! array of [`MAX_LIDS_PER_GRID`].

use super::defs::LidGridDef;
use crate::fx::modp;

/// N ≤ 15 → ≤ 225 lids per grid.
pub const MAX_LIDS_PER_GRID: usize = 225;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
#[repr(u8)]
pub enum LidState {
    #[default]
    Idle = 0,
    Lit = 1,
    Flipped = 2,
    Retired = 3,
    RetireWarn = 4,
}

/// `LidSolid`: Idle, Lit, RetireWarn have a collider.
#[inline]
pub fn lid_solid(s: LidState) -> bool {
    matches!(s, LidState::Idle | LidState::Lit | LidState::RetireWarn)
}

pub fn local_t(d: &LidGridDef, t: i64) -> i64 {
    if d.loop_ticks > 0 {
        modp(t, d.loop_ticks as i64)
    } else {
        t
    }
}

fn interval(d: &LidGridDef, stage: i64) -> i64 {
    if d.wave_interval_count == 0 {
        60
    } else {
        (d.wave_intervals[(stage.min(d.wave_interval_count as i64 - 1)) as usize] as i64).max(1)
    }
}

fn stages(d: &LidGridDef) -> i64 {
    if d.wave_interval_count == 0 {
        1
    } else {
        d.wave_interval_count as i64
    }
}

/// `LidStage`.
pub fn stage(d: &LidGridDef, w: i64) -> i64 {
    (w / (d.waves_per_stage.max(1) as i64)).min(stages(d) - 1)
}

/// `LidWaveStart`.
pub fn wave_start(d: &LidGridDef, w: i64) -> i64 {
    let per = d.waves_per_stage.max(1) as i64;
    let st = stage(d, w);
    let mut t = d.first_wave_ticks as i64;
    let mut s = 0;
    while s < st {
        t += per * interval(d, s);
        s += 1;
    }
    t += (w - st * per) * interval(d, st);
    t.min(i32::MAX as i64)
}

/// `LidLastWave` (−1 before wave 0).
pub fn last_wave(d: &LidGridDef, t: i64) -> i64 {
    if t < d.first_wave_ticks as i64 {
        return -1;
    }
    let per = d.waves_per_stage.max(1) as i64;
    let mut b = d.first_wave_ticks as i64;
    let n = stages(d);
    let mut s = 0;
    while s < n {
        let iv = interval(d, s);
        let span = per * iv;
        if s == n - 1 || t < b + span {
            return s * per + (t - b) / iv; // floor (non-negative)
        }
        b += span;
        s += 1;
    }
    -1
}

/// `LidMaxOverlap`.
pub fn max_overlap(d: &LidGridDef) -> i64 {
    let mut min = i64::MAX;
    let mut s = 0;
    while s < stages(d) {
        min = min.min(interval(d, s));
        s += 1;
    }
    ((d.flipped_ticks as i64 + min - 1) / min).max(1)
}

/// `LidInnerCap`.
pub fn inner_cap(d: &LidGridDef) -> i64 {
    ((9 - d.min_inner_solid as i64) / max_overlap(d)).max(0)
}

fn splitmix64(s: &mut u64) -> u64 {
    *s = s.wrapping_add(0x9E37_79B9_7F4A_7C15);
    let mut z = *s;
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
    z ^ (z >> 31)
}

/// `LidWaveSet`: the lids wave `w` lights, into `scratch[0..count)`.
pub fn wave_set(d: &LidGridDef, w: i64, scratch: &mut [i32; MAX_LIDS_PER_GRID]) -> usize {
    if w < 0 {
        return 0;
    }
    let start = wave_start(d, w);
    let end = start + d.lit_ticks as i64 + d.flipped_ticks as i64;
    let count = (d.count() as usize).min(MAX_LIDS_PER_GRID);
    let mut a = 0usize;
    let mut lid = 0;
    while lid < count {
        if d.retire_tick(lid as i32) as i64 - d.retire_warn_ticks as i64 >= end {
            scratch[a] = lid as i32;
            a += 1;
        }
        lid += 1;
    }
    let st = stage(d, w);
    let pm = if d.flip_permille_count == 0 { 0 } else { d.flip_permille[(st.min(d.flip_permille_count as i64 - 1)) as usize] as i64 };
    let k = (a as i64 * pm / 1000) as usize; // floor (C#)
    let seed = d.seed as u64;
    let mut rng = (seed << 32 | seed) ^ ((w as u32 as u64).wrapping_mul(0xD1B5_4A32_D192_ED03));
    let cap = inner_cap(d);
    let mut inner = 0i64;
    let mut kept = 0usize;
    let mut j = 0usize;
    while j < k {
        let r = j + (splitmix64(&mut rng) % (a - j) as u64) as usize;
        scratch.swap(r, j);
        let c = scratch[j];
        if d.inner(c) {
            if inner >= cap {
                j += 1;
                continue;
            }
            inner += 1;
        }
        scratch[kept] = c;
        kept += 1;
        j += 1;
    }
    kept
}

fn ring_state(d: &LidGridDef, lid: i32, t: i64) -> LidState {
    let rt = d.retire_tick(lid) as i64;
    if t >= rt {
        LidState::Retired
    } else if t >= rt - d.retire_warn_ticks as i64 {
        LidState::RetireWarn
    } else {
        LidState::Idle
    }
}

/// `LidStates`: every lid's state at `t` (30 Hz ticks) into `states[0..N²)`.
pub fn states(d: &LidGridDef, t: i64, states: &mut [LidState; MAX_LIDS_PER_GRID], scratch: &mut [i32; MAX_LIDS_PER_GRID]) {
    let t = local_t(d, t);
    let count = (d.count() as usize).min(MAX_LIDS_PER_GRID);
    let mut lid = 0;
    while lid < count {
        states[lid] = ring_state(d, lid as i32, t);
        lid += 1;
    }
    let window = d.lit_ticks as i64 + d.flipped_ticks as i64;
    let mut w = last_wave(d, t);
    let mut guard = 0;
    while w >= 0 && guard < 64 {
        let start = wave_start(d, w);
        if start + window <= t {
            break;
        }
        let s = if t < start + d.lit_ticks as i64 { LidState::Lit } else { LidState::Flipped };
        let n = wave_set(d, w, scratch);
        let mut i = 0;
        while i < n {
            let l = scratch[i] as usize;
            let cur = states[l];
            if cur == LidState::Idle || (cur == LidState::Lit && s == LidState::Flipped) {
                states[l] = s;
            }
            i += 1;
        }
        w -= 1;
        guard += 1;
    }
}

/// `LidFlippedFor`: ticks into the current Flipped window, or −1 (visual helper / bots).
pub fn flipped_for(d: &LidGridDef, lid: i32, t: i64, scratch: &mut [i32; MAX_LIDS_PER_GRID]) -> i64 {
    let t = local_t(d, t);
    let window = d.lit_ticks as i64 + d.flipped_ticks as i64;
    let mut w = last_wave(d, t);
    let mut guard = 0;
    while w >= 0 && guard < 64 {
        let start = wave_start(d, w);
        if start + window <= t {
            break;
        }
        if t >= start + d.lit_ticks as i64 {
            let n = wave_set(d, w, scratch);
            let mut i = 0;
            while i < n {
                if scratch[i] == lid {
                    return t - start - d.lit_ticks as i64;
                }
                i += 1;
            }
        }
        w -= 1;
        guard += 1;
    }
    -1
}

#[cfg(test)]
mod tests {
    use super::*;

    fn default_def(n: i32) -> LidGridDef {
        let mut d = LidGridDef {
            origin_x: 0,
            origin_z: 0,
            top: 0,
            n,
            lid: 1800,
            gap: 60,
            thickness: 400,
            lit_ticks: 30,
            flipped_ticks: 45,
            first_wave_ticks: 90,
            wave_interval_count: 4,
            waves_per_stage: 8,
            flip_permille_count: 4,
            ring_retire_count: 3,
            retire_warn_ticks: 90,
            min_inner_solid: 5,
            seed: 12345,
            ..Default::default()
        };
        d.wave_intervals[..4].copy_from_slice(&[90, 75, 60, 45]);
        d.flip_permille[..4].copy_from_slice(&[200, 250, 300, 350]);
        d.ring_retire_ticks[..3].copy_from_slice(&[750, 1500, 2250]);
        d
    }

    #[test]
    fn inner_never_below_min() {
        let d = default_def(9);
        let mut st = [LidState::Idle; MAX_LIDS_PER_GRID];
        let mut sc = [0; MAX_LIDS_PER_GRID];
        for t in 0..4000 {
            states(&d, t, &mut st, &mut sc);
            let solid_inner = (0..81).filter(|&l| d.inner(l) && lid_solid(st[l as usize])).count();
            assert!(solid_inner >= 5, "t={t} inner solid {solid_inner}");
        }
    }

    #[test]
    fn schedule_closed_form() {
        let d = default_def(9);
        assert_eq!(wave_start(&d, 0), 90);
        assert_eq!(wave_start(&d, 8), 90 + 8 * 90);
        assert_eq!(last_wave(&d, 89), -1);
        assert_eq!(last_wave(&d, 90), 0);
        for w in 0..60 {
            let s = wave_start(&d, w);
            assert_eq!(last_wave(&d, s), w);
        }
        assert_eq!(max_overlap(&d), 1);
        assert_eq!(inner_cap(&d), 4);
    }
}
