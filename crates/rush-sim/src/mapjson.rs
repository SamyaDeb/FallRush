//! `.mapjson` (the Unity exporter's human-readable output, `Editor/MapBlobExport.cs`) → `MapSpec` → blob.
//!
//! ```json
//! { "key": "BlockPartyDash",
//!   "meta": { "mapId": 0, "mode": 1, "variants": 2, "killY": -20000, "timerCapS": 0, "introMs": 3000, "countdownMs": 3000,
//!             "targetMin": 75, "targetMax": 110, "sweeperStartA": null, "sweeperStartB": null, "botArea": [cx, cz, hx, hz], "version": 1 },
//!   "colliders": [ { "shape": "obb", "center": [x,y,z], "half": [x,y,z], "axes": [[..],[..],[..]], "surface": -1, "dyn": "static", "dynIndex": 0, "name": ".." },
//!                  { "shape": "prism", "verts": [[x,z],..], "y0": 0, "y1": 1000, .. }, { "shape": "sphere", "center": [..], "radius": 1000, .. } ],
//!   "spawns": [[x,y,z,yaw], ×12], "checkpoints": [ { "center", "half", "courseMm", "respawns": [[x,y,z,yaw]] } ],
//!   "course": [[x,y,z]], "hazards": [ [ { "type": 1, "id": "..", "phaseMin": 0, "phaseMax": 65535, "baked": false, "def": [words] } ], [ ..variant B.. ] ],
//!   "botRoutes": [words] }
//! ```
//! All lengths mm, axes Q15, def words in the rush-core `hazards::defs` field order.

use rush_core::hazards::defs::HazardType;
use rush_core::mapblob::builder::*;
use rush_core::mapblob::{dyn_kind, meta};
use rush_core::vec::IVec3;
use serde_json::Value;

fn i(v: &Value) -> i32 {
    v.as_i64().unwrap_or(0) as i32
}

fn v3(v: &Value) -> IVec3 {
    let a = v.as_array().expect("[x,y,z]");
    IVec3::new(i(&a[0]), i(&a[1]), i(&a[2]))
}

pub fn parse(text: &str) -> Result<MapSpec, String> {
    let j: Value = serde_json::from_str(text).map_err(|e| e.to_string())?;
    let mut s = MapSpec { name: j["key"].as_str().unwrap_or("").as_bytes().to_vec(), cell_mm: 2000, ..Default::default() };
    let m = &j["meta"];
    let mut mw = [0i32; meta::WORDS];
    mw[meta::MAP_ID] = i(&m["mapId"]);
    mw[meta::MODE] = i(&m["mode"]);
    mw[meta::VARIANTS] = i(&m["variants"]).max(1);
    mw[meta::KILL_Y] = if m["killY"].is_null() { -20000 } else { i(&m["killY"]) };
    mw[meta::TIMER_CAP_S] = i(&m["timerCapS"]);
    mw[meta::INTRO_MS] = i(&m["introMs"]);
    mw[meta::COUNTDOWN_MS] = i(&m["countdownMs"]);
    mw[meta::TARGET_MIN_S] = i(&m["targetMin"]);
    mw[meta::TARGET_MAX_S] = i(&m["targetMax"]);
    mw[meta::SWEEPER_START_A] = if m["sweeperStartA"].is_null() { i32::MIN } else { i(&m["sweeperStartA"]) };
    mw[meta::SWEEPER_START_B] = if m["sweeperStartB"].is_null() { i32::MIN } else { i(&m["sweeperStartB"]) };
    if let Some(a) = m["botArea"].as_array() {
        mw[meta::BOT_AREA_CX] = i(&a[0]);
        mw[meta::BOT_AREA_CZ] = i(&a[1]);
        mw[meta::BOT_AREA_HX] = i(&a[2]);
        mw[meta::BOT_AREA_HZ] = i(&a[3]);
    }
    mw[meta::MAP_VERSION] = i(&m["version"]);
    s.meta = mw;
    for c in j["colliders"].as_array().ok_or("colliders")? {
        let shape = match c["shape"].as_str().unwrap_or("") {
            "obb" => {
                let ax = c["axes"].as_array().ok_or("axes")?;
                ShapeSpec::Obb { center: v3(&c["center"]), half: v3(&c["half"]), axes: [v3(&ax[0]), v3(&ax[1]), v3(&ax[2])] }
            }
            "prism" => ShapeSpec::Prism {
                verts: c["verts"].as_array().ok_or("verts")?.iter().map(|p| (i(&p[0]), i(&p[1]))).collect(),
                y0: i(&c["y0"]),
                y1: i(&c["y1"]),
            },
            "sphere" => ShapeSpec::Sphere { center: v3(&c["center"]), radius: i(&c["radius"]) },
            "capsule" => ShapeSpec::Capsule { a: v3(&c["a"]), b: v3(&c["b"]), radius: i(&c["radius"]) },
            o => return Err(format!("unknown shape {o}")),
        };
        let dk = match c["dyn"].as_str().unwrap_or("static") {
            "platform" => dyn_kind::PLATFORM,
            "door" => dyn_kind::DOOR,
            "lid" => dyn_kind::LID,
            "tile" => dyn_kind::TILE,
            "block" => dyn_kind::BLOCK,
            _ => dyn_kind::STATIC,
        };
        s.colliders.push(ColliderSpec { shape, surface: if c["surface"].is_null() { -1 } else { i(&c["surface"]) }, dyn_kind: dk, dyn_index: i(&c["dynIndex"]) as u16 });
    }
    for p in j["spawns"].as_array().ok_or("spawns")? {
        s.spawns.push((IVec3::new(i(&p[0]), i(&p[1]), i(&p[2])), i(&p[3]) as u8));
    }
    if let Some(cps) = j["checkpoints"].as_array() {
        for c in cps {
            s.checkpoints.push(CheckpointSpec {
                center: v3(&c["center"]),
                half: v3(&c["half"]),
                course_mm: i(&c["courseMm"]),
                respawns: c["respawns"].as_array().map(|a| a.iter().map(|p| (IVec3::new(i(&p[0]), i(&p[1]), i(&p[2])), i(&p[3]) as u8)).collect()).unwrap_or_default(),
            });
        }
    }
    if let Some(c) = j["course"].as_array() {
        s.course = c.iter().map(v3).collect();
    }
    if let Some(vs) = j["hazards"].as_array() {
        for (v, list) in vs.iter().enumerate().take(2) {
            for h in list.as_array().ok_or("hazards[v]")? {
                let ty = HazardType::from_u8(i(&h["type"]) as u8).ok_or("hazard type")?;
                let words: Vec<i32> = h["def"].as_array().ok_or("def")?.iter().map(i).collect();
                if words.len() != ty.words() {
                    return Err(format!("{ty:?} def has {} words, expected {}", words.len(), ty.words()));
                }
                s.hazards[v].push(HazardSpec {
                    ty,
                    phase_min: i(&h["phaseMin"]),
                    phase_max: if h["phaseMax"].is_null() { 65535 } else { i(&h["phaseMax"]) },
                    id: h["id"].as_str().unwrap_or("").as_bytes().to_vec(),
                    words,
                    baked: h["baked"].as_bool().unwrap_or(false),
                });
            }
        }
    }
    if let Some(b) = j["botRoutes"].as_array() {
        s.bot_routes = b.iter().map(i).collect();
    }
    Ok(s)
}
