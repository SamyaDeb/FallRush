//! Native micro-benchmark: time per step and narrowphase tests per player per tick.
//! cargo run --release -p p0_sim --features gen --example bench
use p0_sim::{bot_input, gen, init_world, map::Map, step, world_hash, Input, Stats, N};
use std::time::Instant;

fn run(name: &str, words: &[u32], ticks: u32) {
    let b = gen::bytes(words);
    let m = Map::parse(b).unwrap();
    let mut w = init_world(&m, 42);
    let mut st_all = Stats::default();
    let mut per_tick_np: Vec<u32> = Vec::new();
    let mut airborne = 0u64;
    let t0 = Instant::now();
    for _ in 0..ticks {
        let mut inp = [Input::default(); N];
        for s in 0..N {
            inp[s] = bot_input(s, w.tick, w.seed, &m, &w.players[s]);
        }
        let mut st = Stats::default();
        step(&mut w, &inp, &m, &mut st);
        per_tick_np.push(st.narrow);
        st_all.narrow += st.narrow;
        st_all.cands += st.cands;
        st_all.sweeps += st.sweeps;
        st_all.hits += st.hits;
        airborne += w.players.iter().filter(|p| p.grounded == 0).count() as u64;
    }
    let dt = t0.elapsed();
    per_tick_np.sort();
    let pct = |p: usize| per_tick_np[(per_tick_np.len() - 1) * p / 100];
    let resp: u32 = w.players.iter().map(|p| p.respawns as u32).sum();
    println!(
        "{name}: cols={} blob={}B ticks={ticks} step={:.1}us narrow/player/tick avg={:.1} p50={:.1} p95={:.1} max={:.1} cands/player/tick={:.1} sweeps/player/tick={:.2} hits/tick={:.2} airborne={:.0}% respawns={resp} hash={:016x}",
        m.hdr.ncol,
        b.len(),
        dt.as_secs_f64() * 1e6 / ticks as f64,
        st_all.narrow as f64 / ticks as f64 / N as f64,
        pct(50) as f64 / N as f64,
        pct(95) as f64 / N as f64,
        pct(100) as f64 / N as f64,
        st_all.cands as f64 / ticks as f64 / N as f64,
        st_all.sweeps as f64 / ticks as f64 / N as f64,
        st_all.hits as f64 / ticks as f64,
        airborne as f64 * 100.0 / (ticks as f64 * N as f64),
        world_hash(&w)
    );
}

fn main() {
    let ticks = std::env::args().nth(1).map(|s| s.parse().unwrap()).unwrap_or(2000);
    run("lid_flip_like", &gen::lid_flip_like(), ticks);
    run("capsule_run_like", &gen::capsule_run_like(), ticks);
}
