//! Float oracles for the integer maths (floats are allowed in test oracles only, never in `src/`).
use rush_core::fx::*;
use rush_core::input::*;

#[test]
fn sin_error_bound() {
    let mut max = 0f64;
    for p in 0..65536 {
        let e = (sin_q15(p) as f64 / 32768.0 - (2.0 * core::f64::consts::PI * p as f64 / 65536.0).sin()).abs();
        if e > max {
            max = e;
        }
    }
    assert!(max < 4.0e-5, "max sin error {max}");
    assert_eq!(sin_q15(0), 0);
    assert_eq!(sin_q15(16384), 32768);
    assert_eq!(cos_q15(0), 32768);
    assert_eq!(cos_q15(deg_to_turn_q16(45)), 23170);
}

#[test]
fn normalize_len() {
    for (x, y, z) in [(1i64, 0, 0), (3, 4, 0), (1, 1, 1), (-7, 2, 9), (100000, 1, -3), (0, -1, 0)] {
        let n = normalize_q15(x, y, z);
        let l = ((n[0] as f64).powi(2) + (n[1] as f64).powi(2) + (n[2] as f64).powi(2)).sqrt();
        assert!((l - 32768.0).abs() < 2.0, "{x},{y},{z} → {n:?} len {l}");
    }
    assert_eq!(normalize_q15(0, 0, 0), [0, 0, 0]);
    assert_eq!(normalize_q15(0, 5, 0), [0, 32768, 0]);
}

#[test]
fn sanitize_matches_float_rule() {
    for x in -128i32..=127 {
        for y in -128i32..=127 {
            let mut c = InputCmd { move_x: x as i8, move_y: y as i8, buttons: 0xFF, cam_yaw: 0 };
            sanitize(&mut c);
            // C# reference with doubles
            let (mut fx, mut fy) = (x.max(-127), y.max(-127));
            let l2 = fx * fx + fy * fy;
            if l2 > 127 * 127 {
                let len = (l2 as f64).sqrt();
                fx = (fx as f64 * 127.0 / len) as i32;
                fy = (fy as f64 * 127.0 / len) as i32;
                while fx * fx + fy * fy > 127 * 127 {
                    fx -= fx.signum();
                    fy -= fy.signum();
                }
            }
            assert_eq!((c.move_x as i32, c.move_y as i32), (fx, fy), "({x},{y})");
            assert_eq!(c.buttons, KNOWN_BUTTONS);
        }
    }
}


/// P1 prompt §1.2: no `f32` / `f64` anywhere in rush-core's sources.
#[test]
fn no_floats_in_src() {
fn walk(d: &std::path::Path, bad: &mut Vec<String>) {
    for e in std::fs::read_dir(d).unwrap() {
        let p = e.unwrap().path();
        if p.is_dir() {
            walk(&p, bad);
        } else if p.extension().is_some_and(|x| x == "rs") {
            let s = std::fs::read_to_string(&p).unwrap();
            for (n, l) in s.lines().enumerate() {
                let code = l.split("//").next().unwrap();
                for w in ["f32", "f64"] {
                    let b = code.as_bytes();
                    let mut i = 0;
                    while let Some(k) = code[i..].find(w) {
                        let at = i + k;
                        let before = at == 0 || !(b[at - 1].is_ascii_alphanumeric() || b[at - 1] == b'_');
                        let after = at + 3 >= b.len() || !(b[at + 3].is_ascii_alphanumeric() || b[at + 3] == b'_');
                        if before && after {
                            bad.push(format!("{}:{}: {}", p.display(), n + 1, l.trim()));
                        }
                        i = at + 3;
                    }
                }
            }
        }
    }
}
let mut bad = Vec::new();
walk(&std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("src"), &mut bad);
assert!(bad.is_empty(), "floats in rush-core/src:\n{}", bad.join("\n"));
}
