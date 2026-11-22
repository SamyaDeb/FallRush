//! `rush-sim`: the headless rush-core runner (P1 prompt I).
//!
//! ```text
//! rush-sim mapblob build <in.mapjson> <out.mapblob> | inspect <blob> | hash <blob> | synth <flat|course> <out>
//! rush-sim movement gen | table
//! rush-sim vectors gen [--dir <dir>] | check [--dir <dir>]
//! rush-sim run --map <blob> --seed <hex> --rate <15|20|30> --bots 12 [--rounds all] [--json]
//! rush-sim trace replay <trace.json> --map <blob> [--rate R]
//! rush-sim qa <blob> --runs N --rate R [--json]
//! ```

mod hazcmp;
mod mapjson;
mod movement;
mod vectors_cmd;

use std::path::PathBuf;

pub fn repo_root() -> PathBuf {
    let here = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    here.parent().unwrap().parent().unwrap().to_path_buf()
}

pub fn sha256_hex(b: &[u8]) -> String {
    use sha2::{Digest, Sha256};
    Sha256::digest(b).iter().map(|x| format!("{x:02x}")).collect()
}

pub fn arg(args: &[String], name: &str) -> Option<String> {
    args.iter().position(|a| a == name).and_then(|i| args.get(i + 1).cloned())
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let a: Vec<&str> = args.iter().map(|s| s.as_str()).collect();
    let code = match a.as_slice() {
        ["mapblob", rest @ ..] => mapblob_cmd(rest),
        ["movement", "gen", ..] => movement::gen(&args),
        ["movement", "table", ..] => movement::table(&args),
        ["hazard", "compare", ..] => hazcmp::compare(&args),
        ["vectors", "gen", ..] => vectors_cmd::gen(&args),
        ["vectors", "check", ..] => vectors_cmd::check(&args),
        _ => {
            eprintln!("usage: see the doc comment in crates/rush-sim/src/main.rs");
            2
        }
    };
    std::process::exit(code);
}

fn mapblob_cmd(a: &[&str]) -> i32 {
    use rush_core::mapblob::{builder, testmaps, MapView};
    match a {
        ["build", input, out] => {
            let text = std::fs::read_to_string(input).expect("read mapjson");
            let spec = match mapjson::parse(&text) {
                Ok(s) => s,
                Err(e) => {
                    eprintln!("{input}: {e}");
                    return 1;
                }
            };
            let blob = builder::build(&spec);
            std::fs::write(out, &blob).expect("write blob");
            println!("{out} {} bytes sha256 {}", blob.len(), sha256_hex(&blob));
            0
        }
        ["synth", which, out] => {
            let spec = match *which {
                "flat" => testmaps::flat_arena_spec(),
                "course" => testmaps::motor_course_spec(),
                _ => {
                    eprintln!("unknown synthetic map {which}");
                    return 2;
                }
            };
            let blob = builder::build(&spec);
            std::fs::write(out, &blob).expect("write");
            println!("{out} {} bytes sha256 {}", blob.len(), sha256_hex(&blob));
            0
        }
        ["hash", path] => {
            let b = std::fs::read(path).expect("read");
            println!("{}  {}", sha256_hex(&b), path);
            0
        }
        ["inspect", path] => {
            let b = std::fs::read(path).expect("read");
            match MapView::new(&b) {
                Ok(m) => {
                    inspect(&m, &b);
                    0
                }
                Err(e) => {
                    eprintln!("{path}: {e:?}");
                    1
                }
            }
        }
        _ => 2,
    }
}

pub fn inspect(m: &rush_core::mapblob::MapView, b: &[u8]) {
    use rush_core::hazards::defs::HazardType;
    use rush_core::mapblob::shape;
    let mut kinds = [0u32; 5];
    let mut dyns = [0u32; 6];
    for i in 0..m.collider_count {
        let c = m.collider(i);
        kinds[c.kind as usize % 5] += 1;
        dyns[c.dyn_kind as usize % 6] += 1;
    }
    let g = m.grid_header();
    println!("name {} map_id {} mode {} variants {} kill_y {}", String::from_utf8_lossy(m.name()), m.map_id(), m.mode(), m.variants(), m.kill_y());
    println!(
        "colliders {} (obb {} prism {} sphere {} capsule {}) static {} platform {} door {} lid {} tile {} block {} global {}",
        m.collider_count, kinds[shape::OBB as usize], kinds[shape::PRISM as usize], kinds[shape::SPHERE as usize], kinds[shape::CAPSULE as usize],
        dyns[0], dyns[1], dyns[2], dyns[3], dyns[4], dyns[5], m.global_count
    );
    println!("grid cell {} origin ({},{},{}) dims {}x{}x{} non-empty cells {}", g[0], g[1], g[2], g[3], g[4], g[5], g[6], g[7]);
    println!("checkpoints {} course points {} bot words {}", m.checkpoint_count(), m.course_len(), m.bot_words());
    for v in 0..2u8 {
        let mut line = format!("hazards[{}]", if v == 0 { "A" } else { "B" });
        for t in 1..=14u8 {
            let ty = HazardType::from_u8(t).unwrap();
            let n = m.hazard_count(v, ty);
            if n > 0 {
                line += &format!(" {ty:?}x{n}");
            }
        }
        println!("{line}");
    }
    println!("bytes {} sha256 {}", b.len(), sha256_hex(b));
}
