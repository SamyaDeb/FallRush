//! Replay an all-bot run natively and report per-tick narrowphase stats near a tick of interest.
//! cargo run -p p0_sim --features gen --example replay -- <lid|run> <seed> <ticks> <focus>
use p0_sim::{bot_input, gen, init_world, map::Map, step_part, Input, Stats, N};
fn main() {
    let a: Vec<String> = std::env::args().collect();
    let words = if a[1] == "run" { gen::capsule_run_like() } else { gen::lid_flip_like() };
    let seed: u64 = a[2].parse().unwrap();
    let ticks: u32 = a[3].parse().unwrap();
    let focus: u32 = a[4].parse().unwrap();
    let m = Map::parse(gen::bytes(&words)).unwrap();
    let mut w = init_world(&m, seed);
    let mut worst = (0u32, 0u32, 0u32);
    for _ in 0..ticks {
        let mut inp = [Input::default(); N];
        for s in 0..N { inp[s] = bot_input(s, w.tick, w.seed, &m, &w.players[s]); }
        let t = w.tick;
        let mut s0 = Stats::default();
        let mut s1 = Stats::default();
        step_part(&mut w, &inp, &m, &mut s0, 0, 6, false);
        step_part(&mut w, &inp, &m, &mut s1, 6, N, true);
        if s0.narrow.max(s1.narrow) > worst.1.max(worst.2) { worst = (t, s0.narrow, s1.narrow); }
        if t + 3 >= focus && t <= focus + 3 { println!("tick {t}: part0 narrow {} sweeps {} | part1 narrow {} sweeps {}", s0.narrow, s0.sweeps, s1.narrow, s1.sweeps); }
    }
    println!("worst tick {} part0 {} part1 {}", worst.0, worst.1, worst.2);
}
