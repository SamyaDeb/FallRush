//! `rush-sim movement gen`: `movement_v4.json` from `movement_v3.json` (= v2 + the bump flag) with tick counts at
//! 15 / 20 / 30 Hz and `tickRateHz: 30` (OC-3), plus `crates/rush-core/src/config_data.rs`. Tuning edits go into
//! `crates/rush-sim/src/movement.rs::TUNING` (empty = v3 values unchanged), never the maps.
//! `rush-sim movement table`: the D.3 analytic table at the three rates (markdown).

use rush_core::config::{ms_to_ticks, MotorConfig, MovementParams, SUPPORTED_RATES};
use serde_json::{json, Map, Value};

/// Motor retunes relative to v3 as (section, key, value). Empty: v4 keeps every v3 value (OC-3 A/B pending).
pub const TUNING: &[(&str, &str, i64)] = &[];

fn canonical(v: &Value) -> String {
    match v {
        Value::Object(m) => {
            let mut keys: Vec<&String> = m.keys().collect();
            keys.sort();
            let parts: Vec<String> = keys.iter().map(|k| format!("{}:{}", serde_json::to_string(k).unwrap(), canonical(&m[*k]))).collect();
            format!("{{{}}}", parts.join(","))
        }
        Value::Array(a) => format!("[{}]", a.iter().map(canonical).collect::<Vec<_>>().join(",")),
        _ => v.to_string(),
    }
}

pub fn build_v4() -> Value {
    let root = crate::repo_root();
    let v3: Value = serde_json::from_str(&std::fs::read_to_string(root.join("packages/shared/config/movement_v3.json")).expect("movement_v3.json")).unwrap();
    let mut v = v3.clone();
    v["version"] = json!(4);
    v["tickRateHz"] = json!(30); // OC-3 (user, 2026-11-21): 30 Hz on-chain; the 15 / 20 tables stay as reference
    for (sec, key, val) in TUNING {
        v[*sec][*key] = json!(val);
    }
    v["notes"] = json!("v4 (P1 rush-core): the v3 values in integer units; tick counts per supported rate (round half up). OC-3 feel A/B decides retunes.");
    let p = params_from(&v);
    let mut ticks = Map::new();
    for r in SUPPORTED_RATES {
        let c = MotorConfig::new(&p, r, rush_core::config::MotorModifier::NONE);
        ticks.insert(
            r.to_string(),
            json!({
                "coyote": c.coyote_ticks, "jumpBuffer": c.jump_buffer_ticks, "diveCooldown": c.dive_cooldown_ticks,
                "diveLandStun": c.dive_land_stun_ticks, "diveCancelWindow": c.dive_cancel_window_ticks,
                "respawn": c.respawn_ticks, "stumble": c.stumble_ticks, "bumpStagger": c.bump_ticks,
            }),
        );
    }
    v["ticks"] = Value::Object(ticks);
    v
}

pub fn params_from(v: &Value) -> MovementParams {
    let g = |s: &str, k: &str| v[s][k].as_i64().unwrap_or_else(|| panic!("{s}.{k}")) as i32;
    MovementParams {
        version: v["version"].as_i64().unwrap() as i32,
        tick_rate_hz: v["tickRateHz"].as_i64().unwrap() as i32,
        capsule_radius: g("capsule", "radius"),
        capsule_height: g("capsule", "height"),
        capsule_skin: g("capsule", "skin"),
        run_max_speed: g("run", "maxSpeed"),
        run_ground_accel: g("run", "groundAccel"),
        run_ground_decel: g("run", "groundDecel"),
        run_air_control_permille: g("run", "airControlPermille"),
        gravity_accel: g("gravity", "accel"),
        gravity_fall_multiplier_permille: g("gravity", "fallMultiplierPermille"),
        gravity_max_fall_speed: g("gravity", "maxFallSpeed"),
        jump_apex_height: g("jump", "apexHeight"),
        jump_coyote_ms: g("jump", "coyoteMs"),
        jump_buffer_ms: g("jump", "bufferMs"),
        dive_horizontal_speed: g("dive", "horizontalSpeed"),
        dive_vertical_speed: g("dive", "verticalSpeed"),
        dive_cooldown_ms: g("dive", "cooldownMs"),
        dive_land_stun_ms: g("dive", "landStunMs"),
        dive_jump_cancel_window_ms: g("dive", "jumpCancelWindowMs"),
        dive_slide_decel: g("dive", "slideDecel"),
        stumble_min_fall_height: g("stumble", "minFallHeight"),
        stumble_duration_ms: g("stumble", "durationMs"),
        stumble_speed_permille: g("stumble", "speedPermille"),
        ground_max_slope_deg: g("ground", "maxSlopeDeg"),
        ground_step_offset: g("ground", "stepOffset"),
        ground_snap_distance: g("ground", "snapDistance"),
        ground_slide_iterations: g("ground", "slideIterations"),
        ground_ledge_tolerance: g("ground", "ledgeTolerance"),
        push_max_speed: g("push", "maxSpeed"),
        respawn_delay_ms: g("respawn", "delayMs"),
        respawn_kill_y: g("respawn", "killY"),
        bump_enabled: v["bump"]["enabled"].as_bool().unwrap_or(false),
        bump_min_closing_speed: g("bump", "minClosingSpeed"),
        bump_stagger_ms: g("bump", "staggerMs"),
    }
}

pub fn gen(_args: &[String]) -> i32 {
    let root = crate::repo_root();
    let v = build_v4();
    let canon = canonical(&v);
    let sha = crate::sha256_hex(canon.as_bytes());
    let pretty = serde_json::to_string_pretty(&v).unwrap() + "\n";
    std::fs::write(root.join("packages/shared/config/movement_v4.json"), pretty).unwrap();
    let p = params_from(&v);
    let rs = format!(
        "// @generated by `rush-sim movement gen` from packages/shared/config/movement_v4.json (do not edit by hand).\n\
use crate::config::MovementParams;\n\n\
/// sha256 of the canonical JSON of movement_v4 (sorted keys, no whitespace).\n\
pub const MOVEMENT_V4_SHA256: &str = \"{sha}\";\n\n\
/// `movement_v4.json` as integers.\n\
pub const MOVEMENT_V4: MovementParams = {p:#?};\n"
    );
    std::fs::write(root.join("crates/rush-core/src/config_data.rs"), rs).unwrap();
    println!("movement_v4.json canonical sha256 {sha}");
    // rounding table (ms → ticks)
    println!("| constant | ms | 15 Hz | 20 Hz | 30 Hz | worst error |");
    println!("|---|---|---|---|---|---|");
    for (name, ms) in [
        ("jump.coyoteMs", p.jump_coyote_ms),
        ("jump.bufferMs", p.jump_buffer_ms),
        ("dive.cooldownMs", p.dive_cooldown_ms),
        ("dive.landStunMs", p.dive_land_stun_ms),
        ("dive.jumpCancelWindowMs", p.dive_jump_cancel_window_ms),
        ("stumble.durationMs", p.stumble_duration_ms),
        ("respawn.delayMs", p.respawn_delay_ms),
    ] {
        let t: Vec<i32> = SUPPORTED_RATES.iter().map(|r| ms_to_ticks(ms, *r)).collect();
        let err = SUPPORTED_RATES.iter().zip(&t).map(|(r, k)| (*k as i64 * 1000 / *r as i64 - ms as i64).abs()).max().unwrap();
        println!("| {name} | {ms} | {} ({} ms) | {} ({} ms) | {} ({} ms) | {err} ms |", t[0], t[0] * 1000 / 15, t[1], t[1] * 50, t[2], t[2] * 1000 / 30);
    }
    0
}

pub fn table(_args: &[String]) -> i32 {
    use rush_core::collide::Scene;
    use rush_core::mapblob::MapView;
    use rush_core::motor_table::*;
    let (fb, cb) = scenes();
    let flat = Scene::new(MapView::new(&fb).unwrap());
    let course = Scene::new(MapView::new(&cb).unwrap());
    println!("| measure | target | 15 Hz | 20 Hz | 30 Hz |");
    println!("|---|---|---|---|---|");
    let row = |name: &str, target: &str, f: &dyn Fn(&MotorConfig) -> String| {
        let v: Vec<String> = RATES.iter().map(|r| f(&MotorConfig::default_at(*r))).collect();
        println!("| {name} | {target} | {} | {} | {} |", v[0], v[1], v[2]);
    };
    row("jump apex (mm)", "1,200 ± 2 %", &|c| format!("{}", apex(c, &flat)));
    row("time to 99 % run speed", "≈ 179 ms (v/a)", &|c| {
        let (k, ms) = time_to_max(c, &flat);
        format!("{k} ticks = {ms} ms")
    });
    row("dive distance to landing (mm) / belly slide (mm)", "≈ 1.6 m (C# 30 Hz)", &|c| {
        let (d, s, _) = dive_distance(c, &flat);
        format!("{d} / {s}")
    });
    for (lane, deg) in [(0, 15), (1, 30), (2, 44), (3, 45), (4, 46), (5, 50)] {
        row(&format!("ramp {deg}° height gained (mm)"), if deg <= 45 { "2,000 (walkable)" } else { "≈ 0 (blocked)" }, &|c| format!("{}", climb(c, &course, lane, 3)));
    }
    for (lane, h) in [(6, 200), (7, 250), (8, 260), (9, 300)] {
        row(&format!("step {h} mm climbed (mm)"), if h <= 250 { "climbs" } else { "blocked (0)" }, &|c| format!("{}", climb(c, &course, lane, 3)));
    }
    row("jump onto 1.15 m / 1.65 m block", "yes / no", &|c| format!("{} / {}", jump_onto(c, &course, 10, 1150), jump_onto(c, &course, 11, 1650)));
    row("coyote: jump k ticks after the edge (0, window−1, window+2)", "yes yes no", &|c| {
        let w = c.coyote_ticks as u32;
        format!("{} {} {} (window {w})", coyote_jump(c, &course, 0), coyote_jump(c, &course, w.saturating_sub(1)), coyote_jump(c, &course, w + 2))
    });
    row("jump buffer: press k ticks before landing (1, window+2)", "yes no", &|c| {
        let w = c.jump_buffer_ticks as u32;
        format!("{} {} (window {w})", buffer_jump(c, &course, 1), buffer_jump(c, &course, w + 2))
    });
    row("stumble from 3.5 m / 2.5 m", "yes / no", &|c| format!("{} / {}", stumble_from(c, &flat, 3500), stumble_from(c, &flat, 2500)));
    row("wall slide: x travelled (mm), z (face at 9,690)", "> 1,000, ≤ 9,690", &|c| {
        let (dx, z) = wall_slide(c, &course);
        format!("{dx}, {z}")
    });
    0
}
