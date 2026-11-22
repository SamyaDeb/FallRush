//! Movement constants (port of `Shared/MovementConfig.cs` + `MovementTuning.cs` + `ModifierEffects` motor scope).
//!
//! [`MovementParams`] holds the integers of `movement_v4.json` (mm, mm/s, mm/s², ms, deg, permille), generated into
//! [`crate::config_data`]. [`MotorConfig::new`] builds the per-rate integer constants: every ms constant becomes
//! ticks at the configured tick rate with round-half-up (`MovementConfig.MsToTicks`), so the rate is a parameter.
//!
//! **Unit change vs C#:** C# `MotorState` velocity is cm/s (i16). rush-core keeps velocity in **mm/s** (i32): finer,
//! and it avoids a second quantisation per tick at 15/20 Hz. Positions stay mm.

use crate::fx::{cos_q15, deg_to_turn_q16, div_round, isqrt_round, permille};

/// The integers of a movement JSON (v3/v4 keys). `Default` = `movement_v4.json` (see `config_data`).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct MovementParams {
    pub version: i32,
    pub tick_rate_hz: i32,
    pub capsule_radius: i32,
    pub capsule_height: i32,
    pub capsule_skin: i32,
    pub run_max_speed: i32,
    pub run_ground_accel: i32,
    pub run_ground_decel: i32,
    pub run_air_control_permille: i32,
    pub gravity_accel: i32,
    pub gravity_fall_multiplier_permille: i32,
    pub gravity_max_fall_speed: i32,
    pub jump_apex_height: i32,
    pub jump_coyote_ms: i32,
    pub jump_buffer_ms: i32,
    pub dive_horizontal_speed: i32,
    pub dive_vertical_speed: i32,
    pub dive_cooldown_ms: i32,
    pub dive_land_stun_ms: i32,
    pub dive_jump_cancel_window_ms: i32,
    pub dive_slide_decel: i32,
    pub stumble_min_fall_height: i32,
    pub stumble_duration_ms: i32,
    pub stumble_speed_permille: i32,
    pub ground_max_slope_deg: i32,
    pub ground_step_offset: i32,
    pub ground_snap_distance: i32,
    pub ground_slide_iterations: i32,
    pub ground_ledge_tolerance: i32,
    pub push_max_speed: i32,
    pub respawn_delay_ms: i32,
    pub respawn_kill_y: i32,
    pub bump_enabled: bool,
    pub bump_min_closing_speed: i32,
    pub bump_stagger_ms: i32,
}

impl Default for MovementParams {
    fn default() -> Self {
        crate::config_data::MOVEMENT_V4
    }
}

/// Motor-scope modifier effects (`modifiers_v1.json`: gravity, apex and ground-decel permille).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct MotorModifier {
    pub gravity_permille: i32,
    pub jump_apex_permille: i32,
    pub ground_decel_permille: i32,
}

impl MotorModifier {
    pub const NONE: MotorModifier = MotorModifier { gravity_permille: 1000, jump_apex_permille: 1000, ground_decel_permille: 1000 };
}

/// Tick rates rush-core supports: they divide 60, so hazard time is exact in 1/60 s units (see `hazards`).
pub const SUPPORTED_RATES: [u32; 3] = [15, 20, 30];

/// `MovementConfig.MsToTicks`: round half up (100 ms → 3 at 30 Hz, 2 at 20 Hz).
#[inline]
pub const fn ms_to_ticks(ms: i32, rate: u32) -> i32 {
    ((ms as i64 * rate as i64 * 2 + 1000) / 2000) as i32 // floor of a non-negative (= round half up)
}

/// The integer motor constants at one tick rate.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct MotorConfig {
    pub rate: u32,
    /// 60 / rate: hazard time units (1/60 s) per tick.
    pub t60_per_tick: u32,
    pub radius: i32,
    pub height: i32,
    pub skin: i32,
    pub max_speed: i32,
    pub ground_accel: i32,
    pub ground_decel: i32,
    pub air_accel: i32,
    pub gravity: i32,
    pub fall_multiplier_permille: i32,
    pub max_fall_speed: i32,
    pub jump_apex: i32,
    /// √(2 g apex), rounded, mm/s.
    pub jump_speed: i32,
    pub dive_horizontal: i32,
    pub dive_vertical: i32,
    pub slide_decel: i32,
    /// √(2 · g · fallMult · minFall), mm/s.
    pub stumble_impact_speed: i32,
    pub stumble_speed_permille: i32,
    /// cos(max slope) in Q15.
    pub max_slope_cos: i32,
    pub step_offset: i32,
    pub snap_distance: i32,
    pub ledge_tolerance: i32,
    pub slide_iterations: i32,
    pub push_max_speed: i32,
    pub kill_y: i32,
    pub bump_enabled: bool,
    pub bump_min_closing: i32,
    pub bump_ticks: i32,
    pub coyote_ticks: i32,
    pub jump_buffer_ticks: i32,
    pub dive_cooldown_ticks: i32,
    pub dive_land_stun_ticks: i32,
    pub dive_cancel_window_ticks: i32,
    pub respawn_ticks: i32,
    pub stumble_ticks: i32,
}

impl MotorConfig {
    /// Port of `MovementConfig(in ModifierEffects m, in MovementTuning t)` at tick rate `rate` (15, 20 or 30).
    pub fn new(p: &MovementParams, rate: u32, m: MotorModifier) -> MotorConfig {
        let rate = if rate == 0 { 20 } else { rate };
        let gravity = permille(p.gravity_accel as i64, m.gravity_permille as i64) as i32;
        let apex = permille(p.jump_apex_height as i64, m.jump_apex_permille as i64) as i32;
        let jump_speed = isqrt_round(2 * gravity as i64 * apex as i64) as i32;
        let fall_g = div_round(gravity as i64 * p.gravity_fall_multiplier_permille as i64, 1000);
        let stumble_impact = isqrt_round(2 * fall_g * p.stumble_min_fall_height as i64) as i32;
        MotorConfig {
            rate,
            t60_per_tick: 60 / rate,
            radius: p.capsule_radius,
            height: p.capsule_height,
            skin: p.capsule_skin,
            max_speed: p.run_max_speed,
            ground_accel: p.run_ground_accel,
            ground_decel: permille(p.run_ground_decel as i64, m.ground_decel_permille as i64) as i32,
            air_accel: div_round(p.run_ground_accel as i64 * p.run_air_control_permille as i64, 1000) as i32,
            gravity,
            fall_multiplier_permille: p.gravity_fall_multiplier_permille,
            max_fall_speed: p.gravity_max_fall_speed,
            jump_apex: apex,
            jump_speed,
            dive_horizontal: p.dive_horizontal_speed,
            dive_vertical: p.dive_vertical_speed,
            slide_decel: permille(p.dive_slide_decel as i64, m.ground_decel_permille as i64) as i32,
            stumble_impact_speed: stumble_impact,
            stumble_speed_permille: p.stumble_speed_permille,
            max_slope_cos: cos_q15(deg_to_turn_q16(p.ground_max_slope_deg as i64)),
            step_offset: p.ground_step_offset,
            snap_distance: p.ground_snap_distance,
            ledge_tolerance: p.ground_ledge_tolerance,
            slide_iterations: p.ground_slide_iterations.clamp(1, 8),
            push_max_speed: p.push_max_speed,
            kill_y: p.respawn_kill_y,
            bump_enabled: p.bump_enabled,
            bump_min_closing: p.bump_min_closing_speed,
            // ⌈ms · rate / 1000⌉ (as C#: ⌈250 · 30 / 1000⌉ = 8)
            bump_ticks: ((p.bump_stagger_ms as i64 * rate as i64 + 999) / 1000) as i32,
            coyote_ticks: ms_to_ticks(p.jump_coyote_ms, rate),
            jump_buffer_ticks: ms_to_ticks(p.jump_buffer_ms, rate),
            dive_cooldown_ticks: ms_to_ticks(p.dive_cooldown_ms, rate),
            dive_land_stun_ticks: ms_to_ticks(p.dive_land_stun_ms, rate),
            dive_cancel_window_ticks: ms_to_ticks(p.dive_jump_cancel_window_ms, rate),
            respawn_ticks: ms_to_ticks(p.respawn_delay_ms, rate),
            stumble_ticks: ms_to_ticks(p.stumble_duration_ms, rate),
        }
    }

    /// The default config (movement_v4, no modifier) at `rate`.
    pub fn default_at(rate: u32) -> MotorConfig {
        MotorConfig::new(&MovementParams::default(), rate, MotorModifier::NONE)
    }

    /// 30 Hz ticks (the unit hazard defs are authored in) → ticks at this rate, round half up. Used for stun and
    /// immunity durations from hazard defs.
    #[inline]
    pub const fn ticks30(&self, t30: i32) -> i32 {
        if t30 <= 0 {
            return t30;
        }
        ((t30 as i64 * self.rate as i64 * 2 + 30) / 60) as i32
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ticks_at_rates() {
        let c30 = MotorConfig::default_at(30);
        assert_eq!((c30.coyote_ticks, c30.jump_buffer_ticks, c30.dive_cooldown_ticks, c30.dive_land_stun_ticks), (3, 4, 30, 14));
        assert_eq!((c30.dive_cancel_window_ticks, c30.respawn_ticks, c30.stumble_ticks, c30.bump_ticks), (5, 30, 9, 8));
        let c20 = MotorConfig::default_at(20);
        assert_eq!((c20.coyote_ticks, c20.jump_buffer_ticks, c20.respawn_ticks), (2, 2, 20));
        assert_eq!(c20.max_slope_cos, 23170);
        assert_eq!(c20.jump_speed, 6928);
        assert_eq!(c30.ticks30(11), 11);
        assert_eq!(c20.ticks30(11), 7);
        assert_eq!(c20.ticks30(18), 12);
        let c15 = MotorConfig::default_at(15);
        assert_eq!(c15.ticks30(11), 6);
    }
}
