//! `rush-sim replay --run <docs/proof/p2/runs/<run>> [--seed <hex32>] [--parts K] [--maps <dir>]` (P2.7): re-runs a
//! recorded on-chain match natively and compares the World bytes at every recorded snapshot.
//! - The start: `director::start_match` from the VRF seed (from `summary.json` `onSolana.vrfSeed` or `--seed`) with
//!   the program's roster (12 bots "bot:<slot>") on the lobby map, exactly as `fall_rush::sim::start_sim`.
//! - Then `world::step_part` part by part (as `tick()`: part 0 first loads the round map when the director asks),
//!   no human inputs in P2 runs, comparing each recorded `(tick, part_pending)` snapshot byte for byte.
use rush_core::collide::Scene;
use rush_core::config::MovementParams;
use rush_core::mapblob::MapView;
use rush_core::world::{self, StepEvents, World, MAX_PLAYERS};
use std::path::PathBuf;

pub fn cmd(args: &[String]) -> i32 {
    let run = PathBuf::from(crate::arg(args, "--run").expect("--run <dir>"));
    let maps_dir = crate::arg(args, "--maps").map(PathBuf::from).unwrap_or_else(|| crate::repo_root().join("maps"));
    let summary: serde_json::Value = std::fs::read_to_string(run.join("summary.json")).ok().and_then(|s| serde_json::from_str(&s).ok()).unwrap_or(serde_json::Value::Null);
    // the chain's step: slices (fall_rush >= P2.4) unless --parts K (the earlier fixed-part program)
    let slice = crate::arg(args, "--parts").is_none() && summary["mode"].as_str() != Some("parts");
    let parts: u8 = crate::arg(args, "--parts").and_then(|v| v.parse().ok()).or_else(|| summary["parts"].as_u64().map(|v| v as u8)).unwrap_or(4);
    // recorded snapshots
    let rec = std::fs::read(run.join("world.bin")).expect("world.bin");
    let mut snaps: Vec<(u32, u8, Vec<u8>)> = Vec::new();
    let mut o = 0;
    while o + 7 <= rec.len() {
        let t = u32::from_le_bytes(rec[o..o + 4].try_into().unwrap());
        let p = rec[o + 4];
        let n = u16::from_le_bytes([rec[o + 5], rec[o + 6]]) as usize;
        snaps.push((t, p, rec[o + 7..o + 7 + n].to_vec()));
        o += 7 + n;
    }
    // the seed: the match's VRF randomness on Solana (summary.json), else --seed, else (partial runs) the recorded
    // World's own director seed
    let seed_src;
    let seed_hex = match crate::arg(args, "--seed").or_else(|| summary["onSolana"]["vrfSeed"].as_str().map(String::from)) {
        Some(h) => {
            seed_src = "vrf";
            h
        }
        None => {
            seed_src = "world";
            let mut w0 = World::new(&MapView::new(&std::fs::read(maps_dir.join("Lobby.mapblob")).unwrap()).unwrap(), 30);
            assert!(w0.load_from(&snaps[0].2), "first snapshot does not decode");
            w0.dir.seed.iter().map(|b| format!("{b:02x}")).collect()
        }
    };
    let mut seed = [0u8; 32];
    for i in 0..32 {
        seed[i] = u8::from_str_radix(&seed_hex[2 * i..2 * i + 2], 16).unwrap();
    }
    let (maps, lobby) = crate::qa::load_maps(&maps_dir);
    let lm = MapView::new(&lobby).unwrap();
    let mut h = crate::qa::Sha;
    let mut w = World::new(&lm, 30);
    let ids: Vec<String> = (0..MAX_PLAYERS).map(|s| format!("bot:{s}")).collect();
    let e: Vec<rush_core::transcript::RosterEntry> = (0..MAX_PLAYERS).map(|s| rush_core::transcript::RosterEntry { bot: true, user_id: ids[s].as_bytes(), wallet: None }).collect();
    let rh = rush_core::transcript::roster_hash(&mut h, &e);
    w.reset(&lm, 30);
    // a Final (fall_rush start_final): the Match's roster hash, the 3 finalists, the earlier rounds' eliminations
    let mj: serde_json::Value = std::fs::read_to_string(run.join("match.json")).ok().and_then(|s| serde_json::from_str(&s).ok()).unwrap_or(serde_json::Value::Null);
    let is_final = summary["finalists"].as_array().is_some_and(|a| a.len() == 3);
    if is_final {
        let mut alive = 0u16;
        for f in summary["finalists"].as_array().unwrap() {
            alive |= 1 << f.as_u64().unwrap();
        }
        let mut rh2 = [0u8; 32];
        let rhx = mj["rosterHash"].as_str().expect("match.json rosterHash (tools/keeper/matchdump.ts)");
        for i in 0..32 {
            rh2[i] = u8::from_str_radix(&rhx[2 * i..2 * i + 2], 16).unwrap();
        }
        let mut elim = [0u8; MAX_PLAYERS];
        for (i, sl) in mj["slots"].as_array().unwrap().iter().enumerate().take(MAX_PLAYERS) {
            let r = sl["round"].as_u64().unwrap() as u8;
            // the Classic rounds' eliminations (round 1..3); the Final's own are the director's
            if sl["state"].as_u64() == Some(3) && r <= 3 {
                elim[i] = (r.min(15) << 4) | (sl["reason"].as_u64().unwrap() as u8 & 0xF);
            }
        }
        let pc = mj["playerCount"].as_u64().unwrap_or(12) as u8;
        rush_core::rules::director::start_final(&mut w, &lm, &seed, pc, &rh2, alive, &elim, &mut h);
    } else {
        rush_core::rules::director::start_match(&mut w, &lm, &seed, MAX_PLAYERS as u8, &rh, &mut h);
    }
    let params = MovementParams::default();
    let mut part: u8 = 0;
    let mut cur: Vec<u8> = lobby.clone();
    let mut ev = StepEvents::default();
    let (mut compared, mut first_bad) = (0usize, None);
    let mut buf = vec![0u8; 4096];
    // order within a tick: the position after each call (1.. then 0 = complete); stale out-of-order snapshots (the
    // recorder merges polling and websocket updates) are skipped
    let span: u64 = if slice { 13 } else { parts as u64 };
    let seq = |t: u32, p: u8| -> u64 { t as u64 * (span + 1) + if p == 0 { span } else { p as u64 } };
    let mut skipped = 0usize;
    for (t, p, bytes) in &snaps {
        if seq(*t, *p) < seq(w.tick, part) {
            skipped += 1;
            continue;
        }
        // advance natively to (t, p): world tick t with part p pending
        let mut guard = 0;
        while !(w.tick == *t && part == *p) && guard < 200_000 {
            guard += 1;
            if part == 0 {
                if let Some(id) = rush_core::rules::director::needs_map(&w) {
                    cur = maps.iter().find(|(k, _)| *k == id).unwrap().1.clone();
                    let v = MapView::new(&cur).unwrap();
                    rush_core::rules::director::load_round(&mut w, &v, &mut h);
                }
            }
            let id = if w.round_index >= 1 { w.map_key } else { rush_core::deal::LOBBY };
            let blob = if id == rush_core::deal::LOBBY { &lobby } else { &maps.iter().find(|(k, _)| *k == id).unwrap().1 };
            let v = MapView::new(blob).unwrap();
            let mut sc = Scene::new(v);
            ev.n = 0;
            if slice {
                part = world::step_slice(&mut w, &[None; MAX_PLAYERS], &v, &params, &mut h, &mut ev, &mut sc, part, world::SLICE_WORK);
            } else {
                world::step_part(&mut w, &[None; MAX_PLAYERS], &v, &params, &mut h, &mut ev, &mut sc, part, parts);
                part = if part + 1 >= parts { 0 } else { part + 1 };
            }
        }
        let n = w.to_bytes(&mut buf);
        compared += 1;
        if buf[..n] != bytes[..] && first_bad.is_none() {
            first_bad = Some((*t, *p));
        }
    }
    let _ = cur;
    // Final mode: the Match's event chain is Classic's, closed by TAG_COMPLETE_MATCH (6) carrying this transcript:
    // sha256(prev ‖ 6 ‖ seq_le ‖ 4 ‖ champion ‖ director transcript) must equal the Match on Solana
    let mut receipt_ok = serde_json::Value::Null;
    if let (Some(prev), Some(seq), Some(fin)) = (summary["preFinal"]["transcript"].as_str(), summary["preFinal"]["eventSeq"].as_u64(), mj["transcript"].as_str()) {
        use sha2::{Digest, Sha256};
        let hx = |x: &str| (0..x.len() / 2).map(|i| u8::from_str_radix(&x[2 * i..2 * i + 2], 16).unwrap()).collect::<Vec<u8>>();
        let mut hs = Sha256::new();
        hs.update(hx(prev));
        hs.update([6u8]);
        hs.update((seq as u32).to_le_bytes());
        hs.update([4u8, w.dir.champion]);
        hs.update(w.dir.transcript);
        let got: String = hs.finalize().iter().map(|b| format!("{b:02x}")).collect();
        receipt_ok = serde_json::Value::Bool(got == fin);
    }
    let res = serde_json::json!({ "receipt_matches_match_on_solana": receipt_ok, "run": run.display().to_string(), "snapshots": snaps.len(), "compared": compared, "skipped_stale": skipped, "identical": first_bad.is_none(), "first_mismatch": first_bad.map(|(t, p)| format!("tick {t} part {p}")), "last_tick": snaps.last().map(|s| s.0), "mode": if slice { "slice".to_string() } else { format!("{parts} parts") }, "seed_from": seed_src, "transcript": w.dir.transcript.iter().map(|b| format!("{b:02x}")).collect::<String>(), "summary_transcript": summary["onSolana"]["transcript"] });
    println!("{res}");
    if first_bad.is_none() {
        0
    } else {
        1
    }
}
