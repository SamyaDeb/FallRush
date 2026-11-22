//! Long-run tail of per-tick narrowphase tests (proxy for CU). cargo run --release --example tail -- <seeds> <ticks>
use p0_sim::{bot_input, gen, init_world, map::Map, step, Input, Stats, N};
fn main() {
    let a: Vec<String> = std::env::args().collect();
    let seeds: u64 = a[1].parse().unwrap();
    let ticks: u32 = a[2].parse().unwrap();
    for (name, words) in [("lid", gen::lid_flip_like()), ("run", gen::capsule_run_like())] {
        let m = Map::parse(gen::bytes(&words)).unwrap();
        let mut v: Vec<u32> = Vec::new();
        for seed in 1..=seeds {
            let mut w = init_world(&m, seed);
            for _ in 0..ticks {
                let mut inp = [Input::default(); N];
                for s in 0..N { inp[s] = bot_input(s, w.tick, w.seed, &m, &w.players[s]); }
                let mut st = Stats::default();
                step(&mut w, &inp, &m, &mut st);
                v.push(st.narrow);
            }
        }
        v.sort();
        let p = |q: f64| v[((v.len() - 1) as f64 * q) as usize];
        let over = |t: u32| v.iter().filter(|x| **x >= t).count();
        println!("{name}: ticks {} narrow p50 {} p95 {} p99 {} p99.9 {} p99.99 {} max {} | >=400 {} >=479 {} >=550 {}", v.len(), p(0.5), p(0.95), p(0.99), p(0.999), p(0.9999), v[v.len()-1], over(400), over(479), over(550));
    }
}
