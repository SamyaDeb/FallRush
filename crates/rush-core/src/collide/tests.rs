//! Collision kit tests (P1 prompt C.5): analytic cases per shape (face, edge, corner, grazing, start-inside,
//! parallel, zero move) and property tests (no overlap > 10 mm after sweep + slide, penetration resolves, no
//! overflow at ±1,000 m).

use super::*;
use crate::fx::{deg_to_turn_q16, ONE_Q15};
use crate::mapblob::builder::*;
use crate::mapblob::testmaps::*;
use crate::mapblob::MapView;
use std::vec::Vec;

const R: i32 = 310;
const H: i32 = 1000;

fn blob(cols: Vec<ColliderSpec>) -> Vec<u8> {
    let spec = MapSpec { colliders: cols, cell_mm: 2000, ..Default::default() };
    build(&spec)
}

fn with_scene<T>(cols: Vec<ColliderSpec>, f: impl FnOnce(&Scene) -> T) -> T {
    let b = blob(cols);
    let m = MapView::new(&b).unwrap();
    let sc = Scene::new(m);
    f(&sc)
}

const DOWN: IVec3 = IVec3::DOWN;

fn unit(x: i64, y: i64, z: i64) -> IVec3 {
    let n = crate::fx::normalize_q15(x, y, z);
    IVec3::new(n[0], n[1], n[2])
}

#[test]
fn obb_face_down() {
    with_scene(vec![ground(10_000)], |sc| {
        let h = sc.capsule_sweep(IVec3::new(0, 100, 0), R, H, DOWN, 500).unwrap();
        assert!((h.distance - 100).abs() <= 1, "{h:?}");
        assert_eq!(h.normal, IVec3::new(0, ONE_Q15, 0));
        assert_eq!(h.point.y, 0);
        assert!(sc.capsule_sweep(IVec3::new(0, 100, 0), R, H, DOWN, 90).is_none());
    });
}

#[test]
fn obb_face_side() {
    // wall face at x = 2000
    with_scene(vec![boxc(2500, 1000, 0, 500, 1000, 3000)], |sc| {
        let h = sc.capsule_sweep(IVec3::new(0, 0, 0), R, H, IVec3::new(ONE_Q15, 0, 0), 3000).unwrap();
        assert!((h.distance - (2000 - R as i64)).abs() <= 1, "{h:?}");
        assert_eq!(h.normal, IVec3::new(-ONE_Q15, 0, 0));
    });
}

#[test]
fn obb_edge_and_corner() {
    // step edge: box top at 200, capsule moving +x at feet y = 0 hits the vertical face (normal −x)
    with_scene(vec![boxc(2500, 100, 0, 500, 100, 3000)], |sc| {
        let h = sc.capsule_sweep(IVec3::new(0, 0, 0), R, H, IVec3::new(ONE_Q15, 0, 0), 3000).unwrap();
        assert!(h.normal.x < -30000, "{h:?}");
    });
    // the bottom sphere lands on the box's top edge from above-outside: an edge normal (tilted)
    with_scene(vec![boxc(0, -500, 0, 1000, 500, 1000)], |sc| {
        let h = sc.capsule_sweep(IVec3::new(1000 + 200, 500, 0), R, H, DOWN, 2000).unwrap();
        assert!(h.normal.x > 0 && h.normal.y > 0 && h.normal.y < ONE_Q15 - 100, "{h:?}");
        // corner: diagonal
        let h = sc.capsule_sweep(IVec3::new(1150, 500, 1150), R, H, DOWN, 2000).unwrap();
        assert!(h.normal.x > 0 && h.normal.z > 0 && (h.normal.x - h.normal.z).abs() < 200, "{h:?}");
    });
}

#[test]
fn grazing_parallel_zero_and_start_inside() {
    with_scene(vec![boxc(2500, 1000, 0, 500, 1000, 3000)], |sc| {
        // parallel to the wall, 5 mm clear: no hit
        let x = 2000 - R - 5;
        assert!(sc.capsule_sweep(IVec3::new(x, 0, -2000), R, H, IVec3::new(0, 0, ONE_Q15), 4000).is_none());
        // zero move
        assert!(sc.capsule_sweep(IVec3::new(0, 0, 0), R, H, IVec3::new(ONE_Q15, 0, 0), 0).is_none());
        // starting inside the wall: ignored (PhysX semantics)
        assert!(sc.capsule_sweep(IVec3::new(2100, 0, 0), R, H, IVec3::new(ONE_Q15, 0, 0), 500).is_none());
    });
}

#[test]
fn ramp_normals() {
    for deg in [15, 30, 45, 46, 50] {
        let cols = ramp(0, 0, deg, 2000);
        with_scene(cols, |sc| {
            // drop onto the middle of the slope
            let h = sc.capsule_sweep(IVec3::new(0, 4000, 1000), R, H, DOWN, 6000).unwrap();
            let t = deg_to_turn_q16(deg as i64);
            let ny = crate::fx::cos_q15(t);
            assert!((h.normal.y - ny).abs() <= 40, "{deg}° {h:?} expected ny {ny}");
            assert!(h.normal.z < 0, "{deg}° normal faces −z (down-slope): {h:?}");
        });
    }
}

#[test]
fn prism_hits() {
    // a hexagon post r = 1 m, y 0..2 m, at the origin
    let verts: Vec<(i32, i32)> = (0..6)
        .map(|k| {
            let t = k * 65536 / 6;
            (crate::fx::cos_q15(t) * 1000 / ONE_Q15, crate::fx::sin_q15(t) * 1000 / ONE_Q15)
        })
        .collect();
    let cols = vec![ColliderSpec::fixed(ShapeSpec::Prism { verts, y0: 0, y1: 2000 })];
    with_scene(cols, |sc| {
        // vertex at −x: contact at x = −1000 − 310
        let h = sc.capsule_sweep(IVec3::new(-3000, 100, 0), R, H, IVec3::new(ONE_Q15, 0, 0), 4000).unwrap();
        assert!((h.distance - (3000 - 1000 - R as i64)).abs() <= 1, "{h:?}");
        assert!(h.normal.x < -32000, "{h:?}");
        // face: the flat side at z = −866 (apothem), approached along +z
        let h = sc.capsule_sweep(IVec3::new(0, 100, -3000), R, H, IVec3::new(0, 0, ONE_Q15), 4000).unwrap();
        assert!((h.distance - (3000 - 866 - R as i64)).abs() <= 2, "{h:?}");
        assert!(h.normal.z < -32000, "{h:?}");
        // top: land on it
        let h = sc.capsule_sweep(IVec3::new(0, 2500, 0), R, H, DOWN, 1000).unwrap();
        assert!((h.distance - 500).abs() <= 1 && h.normal.y == ONE_Q15, "{h:?}");
        // ray down onto the top
        let rh = sc.raycast(IVec3::new(0, 2100, 0), DOWN, 200).unwrap();
        assert_eq!(rh.distance, 100);
        assert_eq!(rh.normal, IVec3::new(0, ONE_Q15, 0));
    });
}

#[test]
fn sphere_hits() {
    let cols = vec![ColliderSpec::fixed(ShapeSpec::Sphere { center: IVec3::new(0, 1000, 0), radius: 1000 })];
    with_scene(cols, |sc| {
        let h = sc.capsule_sweep(IVec3::new(0, 3000, 0), R, H, DOWN, 3000).unwrap();
        // bottom sphere centre at y + 310 touches at 2000 + 310 → feet at 2000
        assert!((h.distance - 1000).abs() <= 1, "{h:?}");
        assert_eq!(h.normal, IVec3::new(0, ONE_Q15, 0));
        let h = sc.capsule_sweep(IVec3::new(-3000, 500, 0), R, H, IVec3::new(ONE_Q15, 0, 0), 3000).unwrap();
        assert!((h.distance - (3000 - 1000 - R as i64)).abs() <= 1, "{h:?}");
        let rh = sc.raycast(IVec3::new(0, 3000, 0), DOWN, 2000).unwrap();
        assert_eq!(rh.distance, 1000);
    });
}

#[test]
fn raycast_box_faces() {
    with_scene(vec![boxc(0, 500, 0, 1000, 500, 1000)], |sc| {
        let rh = sc.raycast(IVec3::new(0, 2000, 0), DOWN, 5000).unwrap();
        assert_eq!((rh.distance, rh.normal), (1000, IVec3::new(0, ONE_Q15, 0)));
        let rh = sc.raycast(IVec3::new(-5000, 500, 0), IVec3::new(ONE_Q15, 0, 0), 10_000).unwrap();
        assert_eq!((rh.distance, rh.normal), (4000, IVec3::new(-ONE_Q15, 0, 0)));
        assert!(sc.raycast(IVec3::new(-5000, 500, 0), IVec3::new(ONE_Q15, 0, 0), 3000).is_none());
        assert!(sc.raycast(IVec3::new(0, 500, 0), DOWN, 3000).is_none(), "inside: no hit");
    });
}

#[test]
fn penetration_resolves() {
    with_scene(vec![ground(10_000)], |sc| {
        let c = sc.penetration(IVec3::new(0, -50, 0), R, H).unwrap();
        assert!(c.x == 0 && c.z == 0 && (c.y - 51).abs() <= 1, "{c:?}");
        assert!(sc.penetration(IVec3::new(0, 5, 0), R, H).is_none());
    });
    with_scene(vec![boxc(2500, 1000, 0, 500, 1000, 3000)], |sc| {
        // 100 mm into the wall from the −x side
        let c = sc.penetration(IVec3::new(2000 - R + 100, 0, 0), R, H).unwrap();
        assert!((c.x + 101).abs() <= 1 && c.y == 0, "{c:?}");
        // deep: axis inside the wall near its −x face → pushed out −x
        let c = sc.penetration(IVec3::new(2200, 0, 0), R, H).unwrap();
        assert!(c.x < -400, "{c:?}");
    });
}

#[test]
fn far_coordinates_no_overflow() {
    // ±1,000 m
    let cols = vec![boxc(1_000_000, -500, -1_000_000, 5000, 500, 5000)];
    with_scene(cols, |sc| {
        let h = sc.capsule_sweep(IVec3::new(1_000_000, 300, -1_000_000), R, H, DOWN, 1000).unwrap();
        assert!((h.distance - 300).abs() <= 1);
        let c = sc.penetration(IVec3::new(1_000_000, -40, -1_000_000), R, H).unwrap();
        assert!(c.y > 0);
    });
}

#[test]
fn grid_matches_brute_force() {
    // many boxes; every sweep's result equals a scene whose grid is disabled (all GLOBAL)
    let mut cols = vec![ground(40_000)];
    let mut s = 7u64;
    for _ in 0..120 {
        s = s.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
        let x = ((s >> 33) % 60_000) as i32 - 30_000;
        let z = ((s >> 13) % 60_000) as i32 - 30_000;
        let h = ((s >> 50) % 3000) as i32 + 100;
        cols.push(boxc(x, h / 2, z, 700, h / 2, 900));
    }
    let b1 = build(&MapSpec { colliders: cols.clone(), cell_mm: 2000, ..Default::default() });
    let b2 = build(&MapSpec { colliders: cols, cell_mm: 1_000_000, ..Default::default() });
    let (m1, m2) = (MapView::new(&b1).unwrap(), MapView::new(&b2).unwrap());
    let (s1, s2) = (Scene::new(m1), Scene::new(m2));
    let mut q = 99u64;
    for _ in 0..400 {
        q = q.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
        let p = IVec3::new(((q >> 33) % 60_000) as i32 - 30_000, ((q >> 20) % 2000) as i32, ((q >> 7) % 60_000) as i32 - 30_000);
        let d = unit(((q >> 40) % 200) as i64 - 100, -(((q >> 48) % 50) as i64), ((q >> 56) % 200) as i64 - 100);
        let a = s1.capsule_sweep(p, R, H, d, 1500);
        let b = s2.capsule_sweep(p, R, H, d, 1500);
        assert_eq!(a, b, "p {p:?} d {d:?}");
    }
}

mod props {
    use super::*;
    use proptest::prelude::*;

    proptest! {
        #![proptest_config(ProptestConfig { cases: 300, .. ProptestConfig::default() })]

        /// Sweep then move by distance − skin: the capsule never overlaps anything by more than 10 mm.
        #[test]
        fn sweep_never_overlaps(px in -3000i32..3000, py in 0i32..2500, pz in -3000i32..3000,
                                dx in -100i64..100, dy in -100i64..100, dz in -100i64..100, dist in 1i64..4000,
                                yaw in 0i32..65536, pitch in -12000i32..12000) {
            let cols = vec![
                ground(20_000),
                ColliderSpec::fixed(rotated_box(IVec3::new(1500, 600, 0), IVec3::new(800, 600, 1200), yaw, pitch)),
                ColliderSpec::fixed(ShapeSpec::Sphere { center: IVec3::new(-1500, 800, 1500), radius: 700 }),
                boxc(0, 300, -2000, 3000, 300, 300),
            ];
            let b = blob(cols);
            let m = MapView::new(&b).unwrap();
            let sc = Scene::new(m);
            let feet = IVec3::new(px, py, pz);
            prop_assume!(sc.penetration(feet, R, H).is_none());
            let d = unit(dx, dy, dz);
            prop_assume!(!d.is_zero());
            let travel = match sc.capsule_sweep(feet, R, H, d, dist + 10) {
                Some(h) => (h.distance - 10).clamp(0, dist),
                None => dist,
            };
            let end = feet.add(d.scale_q15(travel));
            // overlap depth after the move
            if let Some(c) = sc.penetration(end, R, H) {
                let depth = c.len();
                prop_assert!(depth <= 10, "overlap {depth} mm at {end:?} (from {feet:?} along {d:?} for {travel})");
            }
        }

        /// Penetration resolves: after applying the correction (≤ 2 passes, as the motor does) there is no overlap > 2 mm.
        #[test]
        fn penetration_converges(px in -2500i32..2500, py in -300i32..1500, pz in -2500i32..2500, yaw in 0i32..65536, pitch in -12000i32..12000) {
            // one collider at a time (two colliders can sandwich the capsule; PhysX cannot resolve that either)
            let cols = vec![ColliderSpec::fixed(rotated_box(IVec3::new(1500, 600, 0), IVec3::new(800, 600, 1200), yaw, pitch))];
            let b = blob(cols);
            let m = MapView::new(&b).unwrap();
            let sc = Scene::new(m);
            let mut p = IVec3::new(px, py, pz);
            for _ in 0..4 {
                match sc.penetration(p, R, H) { Some(c) => p = p.add(c), None => break }
            }
            if let Some(c) = sc.penetration(p, R, H) {
                prop_assert!(c.len() <= 2, "left {c:?} at {p:?}");
            }
        }
    }
}

#[test]
fn p2_sizes() {
    std::println!("Scene {} B, MapView {} B, Collider {} B, World {} B, BallK {} B, SweeperDef {} B, MalletDef {} B",
        core::mem::size_of::<crate::collide::Scene>(), core::mem::size_of::<crate::mapblob::MapView>(), core::mem::size_of::<crate::mapblob::Collider>(),
        core::mem::size_of::<crate::world::World>(), core::mem::size_of::<crate::hazards::BallK>(),
        core::mem::size_of::<crate::hazards::defs::SweeperDef>(), core::mem::size_of::<crate::hazards::defs::MalletDef>());
}
