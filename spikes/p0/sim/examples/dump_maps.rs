//! Writes the two map blobs + native reference hashes for the TS tools / P0.6.
//! cargo run --release -p p0_sim --features gen --example dump_maps -- <out_dir> [seeds] [steps]
use p0_sim::{bot_input, gen, init_world, map::Map, map::blob_hash, step, world_hash, Input, Stats, N};
use std::io::Write;

fn main() {
    let a: Vec<String> = std::env::args().collect();
    let out = a.get(1).cloned().unwrap_or_else(|| ".p0-logs".into());
    let seeds: u64 = a.get(2).map(|s| s.parse().unwrap()).unwrap_or(100);
    let steps: u32 = a.get(3).map(|s| s.parse().unwrap()).unwrap_or(200);
    std::fs::create_dir_all(&out).unwrap();
    for (name, words) in [("lid", gen::lid_flip_like()), ("run", gen::capsule_run_like())] {
        let b = gen::bytes(&words);
        std::fs::write(format!("{out}/map_{name}.bin"), b).unwrap();
        let m = Map::parse(b).unwrap();
        let mut f = std::fs::File::create(format!("{out}/native_{name}.txt")).unwrap();
        writeln!(f, "# map {name} blob_hash {:016x} len {} steps {steps}", blob_hash(b), b.len()).unwrap();
        for seed in 1..=seeds {
            let mut w = init_world(&m, seed);
            for _ in 0..steps {
                let mut inp = [Input::default(); N];
                for s in 0..N {
                    inp[s] = bot_input(s, w.tick, w.seed, &m, &w.players[s]);
                }
                step(&mut w, &inp, &m, &mut Stats::default());
            }
            writeln!(f, "{seed} {:016x}", world_hash(&w)).unwrap();
        }
        println!("{name}: {} B, blob_hash {:016x}", b.len(), blob_hash(b));
    }
}
