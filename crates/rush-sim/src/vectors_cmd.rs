//! `rush-sim vectors gen|check`: the golden vector files `crates/rush-core/tests/vectors/<kind>_v1.bin`.
//!
//! File: `"RCV1" | u8 kind | u64 seed | u32 count | u64 digest | u32 map bytes | map | count × { u16 len | case bytes }`.
//! The map (for collide / motor / world) is embedded so every target runs the same cases.

use rush_core::mapblob::{builder, testmaps};
use rush_core::vectors::*;

pub const KINDS: [(&str, u8, u32); 5] = [
    ("fx_v1", KIND_FX, COUNT_FX),
    ("collide_v1", KIND_COLLIDE, COUNT_COLLIDE),
    ("motor_v1", KIND_MOTOR, COUNT_MOTOR),
    ("hazard_v2", KIND_HAZARD, COUNT_HAZARD),
    ("world_v1", KIND_WORLD, COUNT_WORLD),
];

pub fn vector_map() -> Vec<u8> {
    builder::build(&testmaps::motor_course_spec())
}

fn dir(args: &[String]) -> std::path::PathBuf {
    crate::arg(args, "--dir").map(Into::into).unwrap_or_else(|| crate::repo_root().join("crates/rush-core/tests/vectors"))
}

fn cases(kind: u8, n: u32, map: &[u8]) -> Vec<Vec<u8>> {
    use rush_core::collide::Scene;
    use rush_core::mapblob::MapView;
    let view = MapView::new(map).unwrap();
    let sc = Scene::new(view);
    let mut out = Vec::new();
    let mut buf = [0u8; MAX_CASE];
    for i in 0..n {
        let len = match kind {
            KIND_FX => fx_case(SEED, i, &mut buf),
            KIND_COLLIDE => collide_case(&sc, SEED, i, &mut buf),
            KIND_MOTOR => motor_case(&sc, SEED, i, &mut buf),
            KIND_HAZARD => hazard_case(SEED, i, &mut buf),
            _ => rush_core::world::vector_case(map, SEED, i, &mut buf),
        };
        out.push(buf[..len].to_vec());
    }
    out
}

pub fn gen(args: &[String]) -> i32 {
    let d = dir(args);
    std::fs::create_dir_all(&d).unwrap();
    let map = vector_map();
    std::fs::write(d.join("vector_map.mapblob"), &map).unwrap();
    for (name, kind, n) in KINDS {
        let cs = cases(kind, n, &map);
        let mut h = Fnv::default();
        for c in &cs {
            h.bytes(c);
        }
        let digest = run(kind, SEED, n, Some(&map));
        assert_eq!(digest, h.0, "{name}: run() digest differs from the case bytes");
        let mut f = Vec::new();
        f.extend_from_slice(b"RCV1");
        f.push(kind);
        f.extend_from_slice(&SEED.to_le_bytes());
        f.extend_from_slice(&n.to_le_bytes());
        f.extend_from_slice(&digest.to_le_bytes());
        let emb: &[u8] = if matches!(kind, KIND_FX | KIND_HAZARD) { &[] } else { &map };
        f.extend_from_slice(&(emb.len() as u32).to_le_bytes());
        f.extend_from_slice(emb);
        for c in &cs {
            f.extend_from_slice(&(c.len() as u16).to_le_bytes());
            f.extend_from_slice(c);
        }
        let p = d.join(format!("{name}.bin"));
        std::fs::write(&p, &f).unwrap();
        println!("{name:8} {n:6} cases  digest {digest:016x}  {} bytes  sha256 {}", f.len(), crate::sha256_hex(&f));
    }
    0
}

/// Parsed header + embedded map.
pub struct VecFile {
    pub kind: u8,
    pub seed: u64,
    pub count: u32,
    pub digest: u64,
    pub map: Vec<u8>,
    pub body: Vec<u8>,
}

pub fn read(p: &std::path::Path) -> VecFile {
    let f = std::fs::read(p).unwrap_or_else(|e| panic!("{}: {e}", p.display()));
    assert_eq!(&f[..4], b"RCV1");
    let kind = f[4];
    let seed = u64::from_le_bytes(f[5..13].try_into().unwrap());
    let count = u32::from_le_bytes(f[13..17].try_into().unwrap());
    let digest = u64::from_le_bytes(f[17..25].try_into().unwrap());
    let ml = u32::from_le_bytes(f[25..29].try_into().unwrap()) as usize;
    VecFile { kind, seed, count, digest, map: f[29..29 + ml].to_vec(), body: f[29 + ml..].to_vec() }
}

pub fn check(args: &[String]) -> i32 {
    let d = dir(args);
    let mut bad = 0;
    for (name, _, _) in KINDS {
        let v = read(&d.join(format!("{name}.bin")));
        let map = if v.map.is_empty() { None } else { Some(v.map.as_slice()) };
        let got = run(v.kind, v.seed, v.count, map);
        // also fold the stored case bytes
        let mut h = Fnv::default();
        let mut o = 0;
        while o < v.body.len() {
            let l = u16::from_le_bytes([v.body[o], v.body[o + 1]]) as usize;
            h.bytes(&v.body[o + 2..o + 2 + l]);
            o += 2 + l;
        }
        let ok = got == v.digest && h.0 == v.digest;
        println!("{name:8} {:6} cases native {got:016x} file {:016x} {}", v.count, v.digest, if ok { "OK" } else { "MISMATCH" });
        if !ok {
            bad += 1;
        }
    }
    if bad == 0 {
        0
    } else {
        1
    }
}
