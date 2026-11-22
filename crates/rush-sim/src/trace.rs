//! `rush-sim trace replay <trace.json> [--rate R]` and `rush-sim trace compare [--dir docs/proof/p1/traces]`
//! (P1 prompt L.2–L.4). Replays a C# reference trace's input timeline (re-timed to the rate with the shared rule in
//! `Editor/P1Traces.cs`) through rush-core on the exported blob of the same scene, then compares at matching times.

use rush_core::config::MovementParams;
use rush_core::input::{InputCmd, DIVE_HELD, DIVE_PRESSED, JUMP_HELD, JUMP_PRESSED};
use rush_core::mapblob::MapView;
use rush_core::motor::flag;
use rush_core::vec::IVec3;
use rush_core::world::{self, NoHasher, StepEvents, World, MAX_PLAYERS};
use serde_json::Value;
use std::path::{Path, PathBuf};

/// One player sample: position (mm), velocity (mm/s), flags, stun, step result.
#[derive(Clone, Copy, Debug, Default)]
#[allow(dead_code)]
pub struct Sample {
    pub p: IVec3,
    pub v: IVec3,
    pub flags: u8,
    pub stun: u8,
    pub res: u8,
}

pub struct Trace {
    pub scene: String,
    pub script: String,
    pub rate: u32,
    pub seed: u64,
    pub ticks: u32,
    pub start: Vec<[i64; 7]>,
    pub moves: Vec<[i64; 4]>,
    pub jumps: Vec<i64>,
    pub dives: Vec<i64>,
    /// frames[k-1][player]
    pub frames: Vec<Vec<Sample>>,
}

fn ints(v: &Value) -> Vec<i64> {
    v.as_array().map(|a| a.iter().map(|x| x.as_i64().unwrap_or(0)).collect()).unwrap_or_default()
}

pub fn load(p: &Path) -> Trace {
    let j: Value = serde_json::from_str(&std::fs::read_to_string(p).unwrap()).unwrap();
    let start: Vec<[i64; 7]> = j["start"].as_array().unwrap().iter().map(|s| ints(s).try_into().unwrap()).collect();
    let n = start.len();
    let frames = j["frames"]
        .as_array()
        .unwrap()
        .iter()
        .map(|f| {
            let a = ints(f);
            (0..n)
                .map(|i| {
                    let b = 1 + i * 9;
                    Sample {
                        p: IVec3::new(a[b] as i32, a[b + 1] as i32, a[b + 2] as i32),
                        v: IVec3::new(a[b + 3] as i32, a[b + 4] as i32, a[b + 5] as i32),
                        flags: a[b + 6] as u8,
                        stun: a[b + 7] as u8,
                        res: a[b + 8] as u8,
                    }
                })
                .collect()
        })
        .collect();
    Trace {
        scene: j["scene"].as_str().unwrap().into(),
        script: j["script"].as_str().unwrap().into(),
        rate: j["rate"].as_u64().unwrap() as u32,
        seed: j["seed"].as_u64().unwrap(),
        ticks: j["ticks"].as_u64().unwrap() as u32,
        start,
        moves: j["moves"].as_array().unwrap().iter().map(|m| ints(m).try_into().unwrap()).collect(),
        jumps: ints(&j["jumps"]),
        dives: ints(&j["dives"]),
        frames,
    }
}

fn first_tick_at_or_after(ms: i64, rate: i64) -> i64 {
    (ms * rate + 999) / 1000 + 1
}

/// The shared input rule (`P1Traces.InputAt`).
pub fn input_at(t: &Trace, k: i64, rate: i64) -> InputCmd {
    let ms = (k - 1) * 1000 / rate;
    let mut c = InputCmd::NEUTRAL;
    for m in &t.moves {
        if ms >= m[0] && ms < m[1] {
            c.move_x = m[2] as i8;
            c.move_y = m[3] as i8;
        }
    }
    for &j in &t.jumps {
        if first_tick_at_or_after(j, rate) == k {
            c.buttons |= JUMP_PRESSED;
        }
        if ms >= j && ms < j + 100 {
            c.buttons |= JUMP_HELD;
        }
    }
    for &j in &t.dives {
        if first_tick_at_or_after(j, rate) == k {
            c.buttons |= DIVE_PRESSED;
        }
        if ms >= j && ms < j + 100 {
            c.buttons |= DIVE_HELD;
        }
    }
    c
}

/// Replays the trace's inputs at `rate` on `map`; returns frames[k-1][player] for k = 1..=ticks·rate/30.
pub fn replay(t: &Trace, map: &[u8], rate: u32, params: &MovementParams) -> Vec<Vec<Sample>> {
    let view = MapView::new(map).expect("blob");
    let mut w = World::new(&view, rate);
    w.hazard_seed = t.seed;
    w.variant = 0;
    w.round_start = 0;
    for (i, s) in t.start.iter().enumerate() {
        w.add_player(&view, i, false);
        w.players[i].m.pos = IVec3::new(s[0] as i32, s[1] as i32, s[2] as i32);
        w.players[i].m.yaw = s[3] as u8;
        w.players[i].m.vel = IVec3::new(s[4] as i32, s[5] as i32, s[6] as i32);
    }
    let n = t.start.len();
    let ticks = t.ticks as i64 * rate as i64 / t.rate as i64;
    let mut out = Vec::new();
    let mut ev = StepEvents::default();
    for k in 1..=ticks {
        let mut inputs = [None; MAX_PLAYERS];
        inputs[0] = Some(input_at(t, k, rate as i64));
        for inp in inputs.iter_mut().take(n).skip(1) {
            *inp = Some(InputCmd::NEUTRAL);
        }
        ev.n = 0;
        world::step(&mut w, &inputs, &view, params, &mut NoHasher, &mut ev);
        out.push((0..n).map(|i| {
            let p = &w.players[i];
            Sample { p: p.m.pos, v: p.m.vel, flags: p.m.flags, stun: p.m.stun, res: p.last_step }
        }).collect());
    }
    out
}

/// Outcome features compared for agreement.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Outcome {
    pub fell: bool,
    pub gooed: bool,
    pub stunned: bool,
    pub launched: bool,
    pub ends_grounded: bool,
    /// Final feet height, nearest 0.25 m (reached / missed a platform, climbed a step).
    pub end_level: i32,
}

fn outcome(f: &[Vec<Sample>], who: usize) -> Outcome {
    let last = f.last().map(|x| x[who]).unwrap_or_default();
    Outcome {
        fell: f.iter().any(|x| x[who].res & world::step_result::FELL_OUT != 0),
        gooed: f.iter().any(|x| x[who].res & world::step_result::GOOED != 0),
        stunned: f.iter().any(|x| x[who].stun > 0),
        launched: f.iter().any(|x| x[who].res & world::step_result::LAUNCHED != 0),
        ends_grounded: last.flags & flag::GROUNDED != 0,
        // only meaningful when standing (a body still falling into the void is at an arbitrary height)
        end_level: if last.flags & flag::GROUNDED != 0 { ((last.p.y as f64) / 250.0).round() as i32 } else { i32::MIN },
    }
}

/// Jump apex: the highest airborne point above the last grounded height before it (0 when never airborne after a
/// grounded tick).
fn apex(f: &[Vec<Sample>]) -> i32 {
    let mut ground = None;
    let mut best = 0;
    for x in f {
        let s = x[0];
        if s.flags & flag::GROUNDED != 0 {
            ground = Some(s.p.y);
        } else if let Some(g) = ground {
            best = best.max(s.p.y - g);
        }
    }
    best
}

/// Time (ms) of the first landing after being airborne, ignoring the spawn settle (the first 200 ms), or −1.
fn landing_ms(f: &[Vec<Sample>], rate: u32) -> i64 {
    let mut air = false;
    let skip = (rate as usize * 200) / 1000;
    for (k, x) in f.iter().enumerate().skip(skip) {
        let g = x[0].flags & flag::GROUNDED != 0;
        if !g && x[0].flags & flag::RESPAWNING == 0 {
            air = true;
        } else if g && air {
            return (k as i64 + 1) * 1000 / rate as i64;
        }
    }
    -1
}

pub struct Row {
    pub name: String,
    pub secs: f64,
    pub max_drift: f64,
    pub final_drift: f64,
    pub apex_cs: i32,
    pub apex_rc: i32,
    pub land_cs: i64,
    pub land_rc: i64,
    pub out_cs: Outcome,
    pub out_rc: Outcome,
}

impl Row {
    pub fn outcomes_agree(&self) -> bool {
        self.out_cs == self.out_rc
    }
    pub fn drift_ok(&self) -> bool {
        self.final_drift <= if self.secs <= 2.5 { 150.0 } else { 300.0 }
    }
    pub fn apex_ok(&self) -> bool {
        !self.name.contains("jump") || self.apex_cs < 300 || (self.apex_rc - self.apex_cs).abs() as f64 <= self.apex_cs as f64 * 0.02
    }
}

pub fn compare_one(t: &Trace, map: &[u8], rate: u32, params: &MovementParams) -> Row {
    let rc = replay(t, map, rate, params);
    let cs = &t.frames;
    let (mut max_d, mut fin) = (0f64, 0f64);
    // Frames where either body is falling out of the map (more than 2 m below its start) are not drift: both fell
    // (the outcome check covers that), and free-fall bodies diverge by construction.
    let floor = t.start[0][1] as i32 - 2000;
    // matching times: C# tick k (k/30 s) ↔ rush-core tick j (j/rate s) when k·rate = j·30
    for (j, x) in rc.iter().enumerate() {
        let jj = j as u64 + 1;
        if !(jj * t.rate as u64).is_multiple_of(rate as u64) {
            continue;
        }
        let k = (jj * t.rate as u64 / rate as u64) as usize;
        if k == 0 || k > cs.len() {
            continue;
        }
        let a = cs[k - 1][0].p;
        let b = x[0].p;
        let d = (((a.x - b.x) as f64).powi(2) + ((a.y - b.y) as f64).powi(2) + ((a.z - b.z) as f64).powi(2)).sqrt();
        // a respawn teleport difference of a tick is not drift
        if cs[k - 1][0].flags & flag::RESPAWNING == 0 && x[0].flags & flag::RESPAWNING == 0 && a.y > floor && b.y > floor {
            max_d = max_d.max(d);
            fin = d;
        }
    }
    Row {
        name: format!("{}-{}", t.scene.trim_start_matches("Map_"), t.script),
        secs: t.ticks as f64 / t.rate as f64,
        max_drift: max_d,
        final_drift: fin,
        apex_cs: apex(cs),
        apex_rc: apex(&rc),
        land_cs: landing_ms(cs, t.rate),
        land_rc: landing_ms(&rc, rate),
        out_cs: outcome(cs, 0),
        out_rc: outcome(&rc, 0),
    }
}

/// scene name → blob path, from maps/*.mapjson "scene".
pub fn scene_blobs(maps: &Path) -> Vec<(String, PathBuf)> {
    let mut v = Vec::new();
    if let Ok(rd) = std::fs::read_dir(maps) {
        for e in rd.flatten() {
            let p = e.path();
            if p.extension().is_some_and(|x| x == "mapjson") {
                if let Ok(j) = serde_json::from_str::<Value>(&std::fs::read_to_string(&p).unwrap_or_default()) {
                    if let Some(s) = j["scene"].as_str() {
                        v.push((s.to_string(), p.with_extension("mapblob")));
                    }
                }
            }
        }
    }
    v
}

pub fn cmd(args: &[String]) -> i32 {
    let root = crate::repo_root();
    let params = MovementParams::default();
    let blobs = scene_blobs(&root.join("maps"));
    let blob_for = |scene: &str| -> Vec<u8> {
        let p = blobs.iter().find(|(s, _)| s == scene).unwrap_or_else(|| panic!("no exported blob for {scene}")).1.clone();
        std::fs::read(p).unwrap()
    };
    if args.get(1).map(|s| s.as_str()) == Some("replay") {
        let t = load(Path::new(&args[2]));
        let rate: u32 = crate::arg(args, "--rate").and_then(|r| r.parse().ok()).unwrap_or(20);
        let map = crate::arg(args, "--map").map(|m| std::fs::read(m).unwrap()).unwrap_or_else(|| blob_for(&t.scene));
        let r = compare_one(&t, &map, rate, &params);
        println!("{} @ {rate} Hz: max drift {:.0} mm, final {:.0} mm, apex C# {} / rc {}, landing C# {} / rc {} ms, outcomes C# {:?} rc {:?}", r.name, r.max_drift, r.final_drift, r.apex_cs, r.apex_rc, r.land_cs, r.land_rc, r.out_cs, r.out_rc);
        return 0;
    }
    // compare: every trace × 3 rates → markdown
    let dir = crate::arg(args, "--dir").map(PathBuf::from).unwrap_or_else(|| root.join("docs/proof/p1/traces"));
    let mut files: Vec<PathBuf> = std::fs::read_dir(&dir).expect("traces dir").flatten().map(|e| e.path()).filter(|p| p.extension().is_some_and(|x| x == "json")).collect();
    files.sort();
    let mut md = String::from("# P1-L: C# reference traces vs rush-core\n\nGenerated by `rush-sim trace compare` from `docs/proof/p1/traces/*.json` (C# `KinematicMotor` + `PhysxMotorWorld`, 30 Hz) replayed through rush-core on the exported blobs. Drift = feet distance at matching times. Pass bar: outcomes identical; final drift ≤ 150 mm (≤ 2.5 s scripts) / ≤ 300 mm (longer); jump apex within ±2 %.\n\n");
    let mut fails = 0;
    for rate in [30u32, 20, 15] {
        md += &format!("## {rate} Hz\n\n| script | s | max drift mm | final drift mm | apex C# / rc | landing ms C# / rc | outcomes | pass |\n|---|---|---|---|---|---|---|---|\n");
        for f in &files {
            let t = load(f);
            let map = blob_for(&t.scene);
            let r = compare_one(&t, &map, rate, &params);
            let pass = r.outcomes_agree() && r.drift_ok() && r.apex_ok();
            if !pass {
                fails += 1;
            }
            let oc = if r.outcomes_agree() { "same".to_string() } else { format!("**C# {:?} / rc {:?}**", r.out_cs, r.out_rc) };
            md += &format!("| {} | {:.1} | {:.0} | {:.0} | {} / {} | {} / {} | {} | {} |\n", r.name, r.secs, r.max_drift, r.final_drift, r.apex_cs, r.apex_rc, r.land_cs, r.land_rc, oc, if pass { "✓" } else { "✗" });
        }
        md += "\n";
    }
    let out = crate::arg(args, "--out").map(PathBuf::from).unwrap_or_else(|| root.join("docs/proof/p1/trace-compare.md"));
    std::fs::write(&out, &md).unwrap();
    print!("{md}");
    println!("{fails} failing rows → {}", out.display());
    if fails == 0 {
        0
    } else {
        1
    }
}
