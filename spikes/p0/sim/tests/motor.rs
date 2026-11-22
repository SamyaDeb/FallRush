use p0_sim::gen::{bytes, Builder};
use p0_sim::{init_world, map::Map, step, Input, Stats, N};

fn flat_with(f: impl FnOnce(&mut Builder)) -> Vec<u32> {
    let mut b = Builder::new(1);
    b.obb([0, -250, 0], [20000, 250, 20000], 0);
    f(&mut b);
    b.hdr.spawn = [0, 0, -10000];
    b.hdr.spawn_dx = 1500;
    b.hdr.kill_y = -6000;
    b.finish()
}

fn run(words: &[u32], inp: Input, ticks: u32) -> p0_sim::World {
    let m = Map::parse(bytes(words)).unwrap();
    let mut w = init_world(&m, 1);
    for _ in 0..ticks {
        let mut st = Stats::default();
        step(&mut w, &[inp; N], &m, &mut st);
    }
    w
}

#[test]
fn stands_still_on_floor() {
    let w = run(&flat_with(|_| {}), Input::default(), 100);
    for p in &w.players {
        assert!(p.pos[1].abs() <= 10, "{:?}", p);
        assert_eq!(p.grounded, 1);
    }
}

#[test]
fn runs_at_max_speed() {
    let w = run(&flat_with(|_| {}), Input { mx: 0, mz: 127, btn: 0, yaw: 0 }, 40);
    let p = w.players[0];
    assert!((p.vel[2] - 5000).abs() < 50, "{:?}", p);
    assert!(p.pos[1].abs() <= 10);
}

#[test]
fn wall_blocks() {
    // wall across z = -5000
    let w = run(&flat_with(|b| b.obb([0, 1000, -5000], [20000, 1000, 200], 0)), Input { mx: 0, mz: 127, btn: 0, yaw: 0 }, 60);
    for p in &w.players {
        assert!(p.pos[2] <= -5200 - 300 && p.pos[2] > -5600, "{:?}", p.pos);
    }
}

#[test]
fn steps_up_200mm() {
    let w = run(&flat_with(|b| b.obb([0, 100, 0], [20000, 100, 8000], 0)), Input { mx: 0, mz: 127, btn: 0, yaw: 0 }, 60);
    for p in &w.players {
        assert!(p.pos[2] > -7000, "did not pass the step: {:?}", p.pos);
        assert!((p.pos[1] - 200).abs() <= 10, "{:?}", p.pos);
    }
}

#[test]
fn jump_apex_about_1200() {
    let words = flat_with(|_| {});
    let m = Map::parse(bytes(&words)).unwrap();
    let mut w = init_world(&m, 1);
    let mut apex = 0;
    for t in 0..40 {
        let btn = (t == 2) as u8;
        let mut st = Stats::default();
        step(&mut w, &[Input { mx: 0, mz: 0, btn, yaw: 0 }; N], &m, &mut st);
        apex = apex.max(w.players[0].pos[1]);
    }
    assert!(apex > 1000 && apex < 1400, "apex {apex}");
    assert!(w.players[0].pos[1].abs() <= 10);
}

#[test]
#[ignore]
fn debug_step() {
    let words = flat_with(|b| b.obb([0, 100, 0], [20000, 100, 8000], 0));
    let m = Map::parse(bytes(&words)).unwrap();
    let mut w = init_world(&m, 1);
    for _ in 0..30 {
        let mut st = Stats::default();
        step(&mut w, &[Input { mx: 0, mz: 127, btn: 0, yaw: 0 }; N], &m, &mut st);
        let p = w.players[0];
        println!("t={} pos={:?} vel={:?} g={} sweeps={}", w.tick, p.pos, p.vel, p.grounded, st.sweeps);
    }
}

#[test]
fn split_step_is_identical() {
    use p0_sim::{bot_input, step_part, world_hash};
    for words in [p0_sim::gen::lid_flip_like(), p0_sim::gen::capsule_run_like()] {
        let m = Map::parse(bytes(&words)).unwrap();
        let mut a = init_world(&m, 9);
        let mut b = a;
        for _ in 0..300 {
            let mut inp = [Input::default(); N];
            for s in 0..N {
                inp[s] = bot_input(s, a.tick, a.seed, &m, &a.players[s]);
            }
            step(&mut a, &inp, &m, &mut Stats::default());
            step_part(&mut b, &inp, &m, &mut Stats::default(), 0, 6, false);
            step_part(&mut b, &inp, &m, &mut Stats::default(), 6, N, true);
            assert_eq!(world_hash(&a), world_hash(&b));
        }
    }
}
