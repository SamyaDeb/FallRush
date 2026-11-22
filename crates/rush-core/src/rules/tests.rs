//! Rules / director / deal tests. Vector tests read `packages/shared/test-vectors/*.json` (the TS twins); the rule
//! scenarios re-create the C# EditMode tests named in each test's doc comment, on synthetic maps.

use super::*;
use crate::config::MovementParams;
use crate::hazards::defs::*;
use crate::input::InputCmd;
use crate::mapblob::builder::*;
use crate::mapblob::testmaps::boxc;
use crate::mapblob::{meta, mode, MapView};
use crate::motor::MotorState;
use crate::vec::IVec3;
use crate::world::{self, Hasher, StepEvents, World};
use std::string::String;
use std::vec::Vec;

// ------------------------------------------------------------------ helpers

struct Sha;
impl Hasher for Sha {
    fn sha256(&mut self, parts: &[&[u8]]) -> [u8; 32] {
        use sha2::{Digest, Sha256};
        let mut h = Sha256::new();
        for p in parts {
            h.update(p);
        }
        h.finalize().into()
    }
}

/// A minimal JSON value + parser (test only; no dependency).
#[derive(Debug, Clone)]
enum J {
    N(i64),
    S(String),
    B(bool),
    A(Vec<J>),
    O(Vec<(String, J)>),
    Null,
}

impl J {
    fn get(&self, k: &str) -> &J {
        match self {
            J::O(v) => v.iter().find(|(a, _)| a == k).map(|(_, b)| b).unwrap_or(&J::Null),
            _ => &J::Null,
        }
    }
    fn arr(&self) -> &[J] {
        match self {
            J::A(v) => v,
            _ => &[],
        }
    }
    fn n(&self) -> i64 {
        match self {
            J::N(v) => *v,
            _ => 0,
        }
    }
    fn s(&self) -> &str {
        match self {
            J::S(v) => v,
            _ => "",
        }
    }
    fn b(&self) -> bool {
        matches!(self, J::B(true))
    }
}

fn parse(s: &str) -> J {
    fn ws(b: &[u8], i: &mut usize) {
        while *i < b.len() && (b[*i] as char).is_whitespace() {
            *i += 1;
        }
    }
    fn val(b: &[u8], i: &mut usize) -> J {
        ws(b, i);
        match b[*i] {
            b'{' => {
                *i += 1;
                let mut v = Vec::new();
                loop {
                    ws(b, i);
                    if b[*i] == b'}' {
                        *i += 1;
                        return J::O(v);
                    }
                    let k = match val(b, i) {
                        J::S(s) => s,
                        _ => panic!("key"),
                    };
                    ws(b, i);
                    *i += 1;
                    let x = val(b, i);
                    v.push((k, x));
                    ws(b, i);
                    if b[*i] == b',' {
                        *i += 1;
                    }
                }
            }
            b'[' => {
                *i += 1;
                let mut v = Vec::new();
                loop {
                    ws(b, i);
                    if b[*i] == b']' {
                        *i += 1;
                        return J::A(v);
                    }
                    v.push(val(b, i));
                    ws(b, i);
                    if b[*i] == b',' {
                        *i += 1;
                    }
                }
            }
            b'"' => {
                *i += 1;
                let st = *i;
                while b[*i] != b'"' {
                    *i += 1;
                }
                let s = String::from_utf8(b[st..*i].to_vec()).unwrap();
                *i += 1;
                J::S(s)
            }
            b't' => {
                *i += 4;
                J::B(true)
            }
            b'f' => {
                *i += 5;
                J::B(false)
            }
            b'n' => {
                *i += 4;
                J::Null
            }
            _ => {
                let st = *i;
                while *i < b.len() && (b[*i] == b'-' || b[*i].is_ascii_digit()) {
                    *i += 1;
                }
                J::N(core::str::from_utf8(&b[st..*i]).unwrap().parse().unwrap())
            }
        }
    }
    let b = s.as_bytes();
    let mut i = 0;
    val(b, &mut i)
}

fn hex(s: &str) -> Vec<u8> {
    (0..s.len() / 2).map(|i| u8::from_str_radix(&s[i * 2..i * 2 + 2], 16).unwrap()).collect()
}

fn to_hex(b: &[u8]) -> String {
    b.iter().map(|x| format!("{x:02x}")).collect()
}

fn vectors(name: &str) -> J {
    let p = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../packages/shared/test-vectors").join(name);
    parse(&std::fs::read_to_string(p).unwrap())
}

fn arr32(v: &[u8]) -> [u8; 32] {
    let mut a = [0u8; 32];
    a.copy_from_slice(v);
    a
}

fn meta_for(md: u8, timer_s: i32) -> [i32; meta::WORDS] {
    let mut m = [0i32; meta::WORDS];
    m[meta::MODE] = md as i32;
    m[meta::VARIANTS] = 1;
    m[meta::KILL_Y] = -10000;
    m[meta::TIMER_CAP_S] = timer_s;
    m[meta::SWEEPER_START_A] = i32::MIN;
    m[meta::SWEEPER_START_B] = i32::MIN;
    m
}

fn haz<T>(ty: HazardType, id: &str, write: impl FnOnce(&mut [i32]) -> T) -> HazardSpec {
    let mut w = vec![0i32; ty.words()];
    write(&mut w);
    HazardSpec { ty, phase_min: 0, phase_max: 65535, id: id.as_bytes().to_vec(), words: w, baked: id.is_empty() }
}

/// One round on one map, driven with neutral (or given) inputs.
struct Round {
    blob: Vec<u8>,
    w: World,
}

impl Round {
    fn new(blob: Vec<u8>, players: usize, rate: u32) -> Round {
        let m = MapView::new(&blob).unwrap();
        let mut w = World::new(&m, rate);
        for s in 0..players {
            w.add_player(&m, s, false);
        }
        Round { blob, w }
    }
    fn begin(&mut self, target: u8, intro30: i64, countdown30: i64) -> u32 {
        let m = MapView::new(&self.blob).unwrap();
        let mut ev = StepEvents::default();
        let t = self.w.tick;
        begin_round(&mut self.w, &m, t, target, 0xFFF, intro30, countdown30, &mut ev)
    }
    fn step_with(&mut self, input: impl Fn(usize) -> InputCmd) -> StepEvents {
        let m = MapView::new(&self.blob).unwrap();
        let mut inputs = [None; 12];
        for (s, i) in inputs.iter_mut().enumerate() {
            *i = Some(input(s));
        }
        let mut ev = StepEvents::default();
        world::step(&mut self.w, &inputs, &m, &MovementParams::default(), &mut world::NoHasher, &mut ev);
        ev
    }
    fn step(&mut self) -> StepEvents {
        self.step_with(|_| InputCmd::NEUTRAL)
    }
    fn place(&mut self, s: usize, x: i32, y: i32, z: i32) {
        self.w.players[s].m = MotorState::at(IVec3::new(x, y, z), 0);
        self.w.players[s].respawn_ticks = 0;
    }
    fn run_until_rush(&mut self) {
        let mut g = 0;
        while self.w.round.phase != phase::RUSH && g < 1000 {
            self.step();
            g += 1;
        }
    }
    fn map(&self) -> MapView<'_> {
        MapView::new(&self.blob).unwrap()
    }
}

fn round_events(ev: &StepEvents, tag: u8) -> Vec<world::Event> {
    ev.as_slice().iter().filter(|e| e.kind == world::ek::ROUND && e.tag == tag).copied().collect()
}

// ------------------------------------------------------------------ deal + transcript vectors

/// TS `deal.ts` vectors (deal_vectors.json): 100 deals + the `rnd` edge cases, byte-equal.
#[test]
fn deal_vectors_byte_equal() {
    let v = vectors("deal_vectors.json");
    let mut h = Sha;
    let deals = v.get("deals").arr();
    assert!(deals.len() >= 100);
    for d in deals {
        let r = arr32(&hex(d.get("randomness").s()));
        let got = crate::deal::derive(&mut h, &r);
        let f = |k: &str| -> Vec<u8> { d.get(k).arr().iter().map(|x| x.n() as u8).collect() };
        assert_eq!(got.maps.to_vec(), f("maps"));
        assert_eq!(got.variants.to_vec(), f("variants"));
        assert_eq!(got.modifiers.to_vec(), f("modifiers"));
    }
    let rnd = v.get("rnd").arr();
    assert!(!rnd.is_empty());
    for e in rnd {
        let r = hex(e.get("randomness").s());
        let got = crate::deal::rnd_range(&mut h, &r, e.get("label").s().as_bytes(), e.get("n").n() as u64);
        assert_eq!(got as i64, e.get("out").n(), "{:?}", e.get("label"));
    }
}

/// TS `transcript.ts` / `targets.ts` vectors: targets, roster hashes, match ids, init and every chained hash.
#[test]
fn transcript_vectors_byte_equal() {
    let v = vectors("transcript_vectors.json");
    let mut h = Sha;
    for t in v.get("targets").arr() {
        let want: Vec<u8> = t.get("targets").arr().iter().map(|x| x.n() as u8).collect();
        assert_eq!(targets::round_targets(t.get("p").n() as i32).unwrap().to_vec(), want);
    }
    for r in v.get("rosters").arr() {
        let es = r.get("entries").arr();
        let wallets: Vec<Option<[u8; 32]>> = es.iter().map(|e| if e.get("wallet").s().is_empty() { None } else { Some(arr32(&hex(e.get("wallet").s()))) }).collect();
        let ids: Vec<String> = es.iter().map(|e| String::from(e.get("userId").s())).collect();
        let entries: Vec<crate::transcript::RosterEntry> =
            es.iter().enumerate().map(|(k, e)| crate::transcript::RosterEntry { bot: e.get("bot").b(), user_id: ids[k].as_bytes(), wallet: wallets[k].as_ref() }).collect();
        assert_eq!(to_hex(&crate::transcript::roster_hash(&mut h, &entries)), r.get("hash").s(), "{}", r.get("name").s());
    }
    for m in v.get("matches").arr() {
        let seed = arr32(&hex(m.get("seed").s()));
        let mid = crate::transcript::match_id(&mut h, &seed);
        assert_eq!(to_hex(&mid), m.get("matchId").s());
        let rh = arr32(&hex(m.get("rosterHash").s()));
        let mut t = crate::transcript::init(&mut h, &mid, &rh);
        assert_eq!(to_hex(&t), m.get("init").s());
        for e in m.get("events").arr() {
            t = crate::transcript::next(&mut h, &t, e.get("tag").n() as u8, e.get("seq").n() as u32, e.get("round").n() as u8, e.get("slot").n() as u8, &hex(e.get("arg").s()));
            assert_eq!(to_hex(&t), e.get("after").s());
        }
        assert_eq!(to_hex(&t), m.get("final").s());
    }
}

// ------------------------------------------------------------------ director replays of the TS matches

fn skill_arena() -> Vec<u8> {
    let mut s = crate::mapblob::testmaps::flat_arena_spec();
    s.meta[meta::MODE] = mode::SKILL as i32; // no stars, no timer: the round runs until the test ends it
    build(&s)
}

fn practice_roster_hash(h: &mut Sha, humans: &[usize]) -> [u8; 32] {
    let ids: Vec<String> = (0..12).map(|s| if humans.contains(&s) { format!("guest:practice:{s}") } else { format!("bot:{s}") }).collect();
    let e: Vec<crate::transcript::RosterEntry> = (0..12).map(|s| crate::transcript::RosterEntry { bot: !humans.contains(&s), user_id: ids[s].as_bytes(), wallet: None }).collect();
    crate::transcript::roster_hash(h, &e)
}

/// Drives a full match with the director; once a round is active, `round_end(round)` gives the rules' result to
/// inject (`None` aborts with reason 1, NO_HUMANS).
fn drive(seed: [u8; 32], humans: &[usize], rate: u32, mut round_end: impl FnMut(u8) -> Option<RoundResult>) -> (World, Vec<world::Event>) {
    let mb = skill_arena();
    let m = MapView::new(&mb).unwrap();
    let mut h = Sha;
    let mut w = World::new(&m, rate);
    for &s in humans {
        w.add_player(&m, s, false);
    }
    let rh = practice_roster_hash(&mut h, humans);
    director::start_match(&mut w, &m, &seed, 12, &rh, &mut h);
    let mut all = Vec::new();
    let params = MovementParams::default();
    for _ in 0..40_000 {
        if director::needs_map(&w).is_some() {
            director::load_round(&mut w, &m, &mut h);
        }
        let mut ev = StepEvents::default();
        world::step(&mut w, &[Some(InputCmd::NEUTRAL); 12], &m, &params, &mut h, &mut ev);
        all.extend(ev.as_slice().iter().filter(|e| e.kind == world::ek::TRANSCRIPT).copied());
        if w.dir.phase == director::phase::ROUND_ACTIVE && !is_over(&w) {
            match round_end(w.dir.round) {
                Some(r) => {
                    w.round.result = r;
                    w.round.has_result = true;
                    w.round.phase = phase::ENDED;
                }
                None => {
                    let mut ev = StepEvents::default();
                    director::abort_match(&mut w, director::abort::NO_HUMANS, &mut h, &mut ev);
                    all.extend(ev.as_slice().iter().copied());
                }
            }
        }
        if w.dir.phase >= director::phase::DONE {
            break;
        }
    }
    (w, all)
}

fn result_from(order: &[u8], qualified: usize, reasons: &[u8]) -> RoundResult {
    let mut r = RoundResult { n: order.len() as u8, ..Default::default() };
    for (k, &s) in order.iter().enumerate() {
        r.order[k] = s;
        if k < qualified {
            r.qualified |= 1 << k;
        } else {
            r.reasons[k] = reasons[k - qualified];
        }
    }
    r
}

fn check_events(w: &World, ev: &[world::Event], m: &J) {
    let want = m.get("events").arr();
    assert_eq!(ev.len(), want.len(), "{}: event count", m.get("name").s());
    for (e, x) in ev.iter().zip(want) {
        assert_eq!(
            (e.tag, e.round, e.slot, e.seq),
            (x.get("tag").n() as u8, x.get("round").n() as u8, x.get("slot").n() as u8, x.get("seq").n() as u32),
            "{}",
            m.get("name").s()
        );
    }
    assert_eq!(to_hex(&w.dir.transcript), m.get("final").s(), "{}: final transcript", m.get("name").s());
}

/// `MatchDirectorTests` replaying the TS vector "full-12p-4r" (4 rounds, scripted orders → the same transcript),
/// at every rate.
#[test]
fn director_full_match_matches_ts_transcript() {
    let v = vectors("transcript_vectors.json");
    let m = &v.get("matches").arr()[0];
    let seed = arr32(&hex(m.get("seed").s()));
    let orders: Vec<Vec<u8>> = m.get("orders").arr().iter().map(|o| o.arr().iter().map(|x| x.n() as u8).collect()).collect();
    let reasons: Vec<Vec<u8>> = m.get("reasons").arr().iter().map(|o| o.arr().iter().map(|x| x.n() as u8).collect()).collect();
    let targets = [8usize, 5, 3, 1];
    for rate in [15, 20, 30] {
        let (w, ev) = drive(seed, &[0], rate, |r| {
            let i = r as usize - 1;
            Some(result_from(&orders[i], targets[i], &reasons[i]))
        });
        check_events(&w, &ev, m);
        assert_eq!(w.dir.champion, 2);
        assert_eq!(w.dir.phase, director::phase::DONE);
        let d: Vec<u8> = m.get("deal").get("modifiers").arr().iter().map(|x| x.n() as u8).collect();
        assert_eq!(w.dir.deal.modifiers.to_vec(), d);
        assert_eq!(w.dir.targets, [8, 5, 3, 1]);
    }
}

/// TS "survival-one-survivor": one survivor in round 2 → champion; rounds 3–4 skipped.
#[test]
fn director_one_survivor_is_champion() {
    let v = vectors("transcript_vectors.json");
    let m = &v.get("matches").arr()[1];
    let seed = arr32(&hex(m.get("seed").s()));
    let (w, ev) = drive(seed, &[0, 5], 20, |r| {
        if r == 1 {
            Some(result_from(&[5, 1, 2, 3, 4, 0, 6, 7, 8, 9, 10, 11], 8, &[0, 0, 0, 0]))
        } else {
            Some(result_from(&[3, 5, 1, 2, 4, 0, 6, 7], 1, &[1; 7]))
        }
    });
    check_events(&w, &ev, m);
    assert_eq!(w.dir.champion, 3);
}

/// TS "abort-r1": aborted in round 1 (no humans).
#[test]
fn director_abort_matches_ts_transcript() {
    let v = vectors("transcript_vectors.json");
    let m = &v.get("matches").arr()[2];
    let seed = arr32(&hex(m.get("seed").s()));
    let (w, ev) = drive(seed, &[0], 20, |_| None);
    check_events(&w, &ev, m);
    assert_eq!(w.dir.phase, director::phase::ABORTED);
}

/// `MatchDirectorTests` M4-8 (nobody qualifies → the best placed advances alone) and the target cap (M4-9).
#[test]
fn director_minimum_advance_and_target_cap() {
    let (_, ev) = drive([7u8; 32], &[0], 20, |r| {
        if r == 1 {
            Some(result_from(&[0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11], 10, &[0, 0]))
        } else {
            let alive: Vec<u8> = (0..8).collect();
            Some(result_from(&alive, 0, &[1; 8]))
        }
    });
    let r1: Vec<&world::Event> = ev.iter().filter(|e| e.round == 1 && e.tag == crate::transcript::ELIMINATED).collect();
    assert_eq!(r1.len(), 4);
    assert_eq!((r1[0].slot, r1[0].arg), (8, reason::TIMEOUT_RANK as i32), "over the target → TIMEOUT_RANK");
    let r2q: Vec<&world::Event> = ev.iter().filter(|e| e.round == 2 && e.tag == crate::transcript::QUALIFIED).collect();
    assert_eq!(r2q.len(), 1);
    assert_eq!(r2q[0].slot, 0);
    assert!(ev.iter().any(|e| e.tag == crate::transcript::COMPLETE_MATCH && e.slot == 0));
}

/// The director's tick counts scale with the rate: DEAL after DealingTicks (3 s) at every rate.
#[test]
fn director_timings_scale_with_rate() {
    for rate in [15u32, 20, 30] {
        let (_, ev) = drive([9u8; 32], &[0], rate, |_| Some(result_from(&[0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11], 8, &[0; 4])));
        let deal = ev.iter().find(|e| e.tag == crate::transcript::DEAL).unwrap();
        // locked at tick 0; Dealing starts at director time 0 and lasts DealingTicks (90 at 30 Hz = 3 s)
        assert_eq!(deal.tick, 3 * rate, "{rate} Hz: 90 ticks at 30 Hz = 3 s");
    }
}

// ------------------------------------------------------------------ race (RaceRulesTests)

/// The C# test strip: 10 m wide, z ∈ [−12, 80] m; checkpoints at z = 20, 40, finish 60 m; a start gate at z = 2 m.
fn race_map(timer_s: i32) -> Vec<u8> {
    let cp = |z: i32, course: i32, rz: i32| CheckpointSpec {
        center: IVec3::new(0, 1500, z),
        half: IVec3::new(5000, 2000, 1000),
        course_mm: course,
        respawns: vec![(IVec3::new(-3000, 50, rz), 0), (IVec3::new(0, 50, rz), 0), (IVec3::new(3000, 50, rz), 0)],
    };
    let gate = GateDoorDef { center_x: 0, center_y: 1250, center_z: 2000, half_x: 5000, half_y: 1250, half_z: 200, lift: 2800, opening_ticks: 10, mode: 1, ..Default::default() };
    let gate_col = ColliderSpec { shape: aabb_box(IVec3::ZERO, IVec3::new(5000, 1250, 200)), surface: -1, dyn_kind: crate::mapblob::dyn_kind::DOOR, dyn_index: 0 };
    let spec = MapSpec {
        name: b"RaceStrip".to_vec(),
        meta: meta_for(mode::RACE, timer_s),
        colliders: vec![boxc(0, -500, 34_000, 5000, 500, 46_000), gate_col],
        spawns: (0..12).map(|i| (IVec3::new(-4000 + (i % 6) * 1600, 50, -8000 + (i / 6) * 2000), 0u8)).collect(),
        checkpoints: vec![cp(20_000, 28_000, 23_000), cp(40_000, 48_000, 43_000), cp(60_000, 68_000, 63_000)],
        course: vec![IVec3::new(0, 0, -8000), IVec3::new(0, 0, 80_000)],
        hazards: [vec![haz(HazardType::GateDoor, "", |w| gate.write(w))], Vec::new()],
        cell_mm: 2000,
        ..Default::default()
    };
    build(&spec)
}

fn fwd(mag: i8) -> InputCmd {
    InputCmd { move_x: 0, move_y: mag, buttons: 0, cam_yaw: 0 }
}

/// `RaceRulesTests.Race_12Bots_FirstNQualify_EndsAtTheNthFinisher_OrderedAndDeterministic` (12 runners at
/// different stick strengths instead of waypoint bots), at 15 / 20 / 30 Hz.
#[test]
fn race_first_n_qualify_ends_at_nth_and_deterministic() {
    let once = |rate: u32| {
        let mut r = Round::new(race_map(110), 12, rate);
        let rush = r.begin(0, 30, 60);
        assert_eq!(r.w.round.phase, phase::INTRO);
        for _ in 0..rate * 60 {
            if is_over(&r.w) {
                break;
            }
            r.step_with(|s| fwd(127 - 5 * s as i8));
        }
        assert!(is_over(&r.w), "{rate} Hz: round over");
        let res = r.w.round.result;
        assert_eq!(res.target, 8, "ceil(12 × 0.66)");
        assert_eq!(res.qualified_count(), 8);
        assert!(!res.timer_expired);
        assert_eq!(res.n, 12);
        for p in 0..12 {
            assert_eq!(res.qualified_at(p), p < 8);
        }
        let ft: Vec<u32> = (0..8).map(|p| r.w.players[res.order[p] as usize].r.done_tick).collect();
        assert!(ft.windows(2).all(|x| x[0] <= x[1]), "finish order");
        assert_eq!(res.end_tick, ft[7], "the round ends at once when N have finished");
        assert!(ft[0] - rush > 60 * rate / 8, "not faster than running");
        r.w.hash()
    };
    for rate in [15, 20, 30] {
        assert_eq!(once(rate), once(rate), "{rate} Hz deterministic");
    }
}

/// `RaceRulesTests.Countdown_IgnoresInput_StartGateClosedUntilRush`.
#[test]
fn race_countdown_ignores_input_gate_closed_until_rush() {
    let mut r = Round::new(race_map(110), 1, 30);
    let rush = r.begin(0, 30, 60);
    let z0 = r.w.players[0].m.pos.z;
    while r.w.tick < rush - 1 {
        r.step_with(|_| fwd(127));
        assert!((r.w.players[0].m.pos.z - z0).abs() <= 30, "frozen during intro / countdown");
    }
    let hz = r.w.hazards(&r.map());
    assert_eq!(crate::hazards::door_lift(&hz.door(0), crate::hazards::t30(r.w.t60(rush - 1))), 0, "gate closed before RUSH");
    for _ in 0..90 {
        r.step_with(|_| fwd(127));
    }
    assert_eq!(r.w.round.phase, phase::RUSH);
    assert!(r.w.players[0].m.pos.z > 2500, "through the gate after RUSH");
}

/// `RaceRulesTests.Checkpoints_SkippedOrTeleported_NeverCount`.
#[test]
fn race_checkpoints_skipped_or_teleported_never_count() {
    let mut r = Round::new(race_map(110), 1, 30);
    let rush = r.begin(0, 30, 60);
    while r.w.tick < rush + 5 {
        r.step();
    }
    r.place(0, 0, 50, 40_000);
    for _ in 0..30 {
        r.step();
    }
    assert_eq!(r.w.players[0].r.checkpoint, 0, "checkpoint 2 before 1");
    r.place(0, 0, 50, 20_000);
    r.step();
    assert_eq!(r.w.players[0].r.checkpoint, 0, "28 m in < 56 ticks is not reachable");
    assert!(r.w.players[0].r.rejected > 0);
    while r.w.tick < rush + 200 {
        r.step();
    }
    r.w.players[0].r.checkpoint = 0;
    let blob = r.blob.clone();
    let m = MapView::new(&blob).unwrap();
    let mut ev = StepEvents::default();
    let mut s = MotorState::at(IVec3::new(6000, 50, 20_000), 0);
    let t = r.w.tick;
    race::try_checkpoint(&mut r.w, &m, 0, &s, t, &mut ev);
    assert_eq!(r.w.players[0].r.checkpoint, 0, "around the trigger");
    s.pos.x = 0;
    race::try_checkpoint(&mut r.w, &m, 0, &s, t, &mut ev);
    assert_eq!(r.w.players[0].r.checkpoint, 1, "through it, in time");
    s.pos.z = 60_000;
    race::try_checkpoint(&mut r.w, &m, 0, &s, t + 1, &mut ev);
    assert_eq!(r.w.players[0].r.checkpoint, 1, "the finish before checkpoint 2: never");
}

/// `RaceRulesTests.Fall_RespawnsAtTheLastCheckpoint_After30Ticks_OnFreeSpots` (30 ticks = 1 s, at every rate).
#[test]
fn race_fall_respawns_at_last_checkpoint_after_1s_on_free_spots() {
    for rate in [15u32, 20, 30] {
        let mut r = Round::new(race_map(110), 2, rate);
        let rush = r.begin(0, 30, 60);
        while r.w.tick < rush + 4 * rate {
            r.step();
        }
        let blob = r.blob.clone();
        let m = MapView::new(&blob).unwrap();
        for s in 0..2 {
            let mut ev = StepEvents::default();
            let st = MotorState::at(IVec3::new(0, 50, 20_000), 0);
            let t = r.w.tick;
            race::try_checkpoint(&mut r.w, &m, s, &st, t, &mut ev);
            assert_eq!(r.w.players[s].r.checkpoint, 1);
            r.place(s, if s == 0 { 5600 } else { -5600 }, 50, 30_000);
        }
        let mut fell = 0u32;
        for _ in 0..rate * 8 {
            r.step();
            let resp = r.w.players[0].m.has(crate::motor::flag::RESPAWNING);
            if fell == 0 && resp {
                fell = r.w.tick;
            }
            if fell != 0 && !resp {
                break;
            }
        }
        assert!(fell != 0);
        assert_eq!(r.w.tick - fell, rate, "{rate} Hz: respawn exactly 1.0 s after falling");
        let pa = r.w.players[0].m;
        assert_eq!(pa.pos.z, 23_000, "at checkpoint 1's respawn row");
        for _ in 0..rate {
            r.step();
        }
        let pb = r.w.players[1].m;
        assert!((pb.pos.z - 23_000).abs() <= 400);
        assert!((pa.pos.x - pb.pos.x).abs() > 1000, "never stacked");
    }
}

/// `RaceRulesTests.TimerExpiry_RanksByCheckpoint_ThenCourseDistance_ThenCheckpointTime_ThenSlot`.
#[test]
fn race_timer_ranking_key() {
    let mut r = Round::new(race_map(10), 6, 30);
    let rush = r.begin(0, 30, 60);
    while r.w.tick < rush + 150 {
        r.step();
    }
    let set = |r: &mut Round, s: usize, cp: u8, t: u32, x: i32, z: i32| {
        r.w.players[s].r.checkpoint = cp;
        r.w.players[s].r.checkpoint_tick = t;
        r.place(s, x, 50, z);
    };
    set(&mut r, 0, 1, rush + 100, -2000, 25_000);
    set(&mut r, 1, 2, rush + 120, 0, 45_000);
    set(&mut r, 2, 1, rush + 90, 0, 35_000);
    set(&mut r, 3, 1, rush + 80, 2000, 25_000);
    set(&mut r, 4, 0, 0, -2000, 10_000);
    set(&mut r, 5, 0, 0, 2000, 10_000);
    while !is_over(&r.w) {
        r.step();
    }
    let res = r.w.round.result;
    assert!(res.timer_expired);
    assert_eq!(&res.order[..6], &[1, 2, 3, 0, 4, 5]);
    assert_eq!(res.target, 4, "ceil(6 × 0.66)");
    for k in 0..6 {
        assert_eq!(res.qualified_at(k), k < 4);
    }
    assert_eq!(res.reasons[4], reason::TIMEOUT_RANK);
}

/// `RaceRulesTests.FinishedRacers_LeaveTheSimulationAndCollisions`.
#[test]
fn race_finished_leave_simulation() {
    let mut r = Round::new(race_map(110), 2, 20);
    r.begin(0, 30, 60);
    for _ in 0..20 * 40 {
        if r.w.round.finished_count > 0 {
            break;
        }
        r.step_with(|s| fwd(if s == 0 { 127 } else { 60 }));
    }
    let f = r.w.round.finish_order[0] as usize;
    assert!(!simulates(&r.w, f) && !collides(&r.w, f));
    let frozen = r.w.players[f].m;
    for _ in 0..10 {
        r.step_with(|s| fwd(if s == 0 { 127 } else { 60 }));
    }
    assert_eq!(r.w.players[f].m, frozen, "frozen at the finish");
}

/// `RaceRulesTests.CourseDistance_IsAlongThePolyline` (integer mm).
#[test]
fn race_course_distance_along_polyline() {
    let s = MapSpec { cell_mm: 2000, course: vec![IVec3::new(0, 0, 0), IVec3::new(0, 0, 10_000), IVec3::new(10_000, 0, 10_000)], ..Default::default() };
    let b = build(&s);
    let m = MapView::new(&b).unwrap();
    assert_eq!(race::course_distance(&m, 0, 0, 5000), 5000);
    assert!((race::course_distance(&m, 5000, 0, 10_500) - 15_000).abs() <= 1, "along the course, not straight-line");
}

// ------------------------------------------------------------------ survival (M4RulesTests, GooTowerTests)

/// Ground at y = 0 and a 1 m-thick summit box (top 5 m) at z = 20 m; the summit trigger sits on it.
fn survival_map(timer_s: i32, goo: Option<GooDef>) -> Vec<u8> {
    let hz = goo.map(|g| vec![haz(HazardType::GooPlane, "", |w| g.write(w))]).unwrap_or_default();
    let spec = MapSpec {
        meta: meta_for(mode::SURVIVAL, timer_s),
        colliders: vec![crate::mapblob::testmaps::ground(30_000), boxc(0, 4500, 20_000, 3000, 500, 3000)],
        spawns: (0..12).map(|i| (IVec3::new(-11_000 + 2000 * i, 50, 0), 0u8)).collect(),
        checkpoints: vec![CheckpointSpec { center: IVec3::new(0, 6000, 20_000), half: IVec3::new(1500, 1000, 1500), course_mm: 0, respawns: Vec::new() }],
        hazards: [hz, Vec::new()],
        cell_mm: 2000,
        ..Default::default()
    };
    build(&spec)
}

/// `M4RulesTests.Survival_FirstNToTheSummit_Qualify_LeaveTheSim_RoundEndsAtTheNth`.
#[test]
fn survival_first_n_to_summit_qualify() {
    let mut r = Round::new(survival_map(120, None), 6, 20);
    r.begin(3, ROUND_INTRO_30, ROUND_COUNTDOWN_30);
    r.run_until_rush();
    for (k, s) in [4usize, 1, 5].iter().enumerate() {
        r.place(*s, -1000 + k as i32 * 1000, 5010, 20_000);
        r.step();
        assert!(r.w.players[*s].r.has(sf::DONE));
        assert!(!simulates(&r.w, *s), "escaped: out of the simulation");
    }
    assert!(is_over(&r.w), "ends at the N-th escape");
    let res = r.w.round.result;
    assert_eq!(&res.order[..3], &[4, 1, 5]);
    assert_eq!(res.qualified_count(), 3);
}

/// `M4RulesTests.Survival_TimerEnd_SurvivorsQualify_ByHeightThenSlot_CappedAtN`.
#[test]
fn survival_timer_end_by_height_then_slot() {
    let mut r = Round::new(survival_map(2, None), 5, 20);
    r.begin(3, ROUND_INTRO_30, ROUND_COUNTDOWN_30);
    r.run_until_rush();
    r.place(0, 15_000, 50, 15_000);
    r.place(1, -2500, 5010, 17_500); // on the summit box, outside the summit trigger: highest
    r.place(2, 15_000, 50, 10_000);
    r.place(3, 15_000, 50, 5000);
    r.place(4, -15_000, 50, 5000);
    while !is_over(&r.w) {
        r.step();
    }
    let res = r.w.round.result;
    assert!(res.timer_expired);
    assert_eq!(&res.order[..5], &[1, 0, 2, 3, 4], "height, then slot");
    for k in 0..5 {
        assert_eq!(res.qualified_at(k), k < 3);
    }
    assert_eq!(res.reasons[3], reason::TIMEOUT_RANK);
}

/// `M4RulesTests.Goo_KnocksOutOnContact_AtTheExactTick` + `Survival_FewerThanNAlive_OnlySurvivorsQualify_OutsRankedByLastToGo`.
#[test]
fn survival_goo_knockout_outs_ranked_last_to_go() {
    // goo y0 −2 m, +1 m/s after 1 s, capped at 3 m (below the 5 m summit box top)
    let goo = GooDef { y0: -2000, rise_mm_per_sec: 1000, delay_ticks: 30, max_y: 3000, ..Default::default() };
    let mut r = Round::new(survival_map(0, Some(goo)), 4, 20);
    let rush = r.begin(3, ROUND_INTRO_30, ROUND_COUNTDOWN_30);
    r.run_until_rush();
    r.place(3, -2500, 5010, 17_500);
    r.place(2, 15_000, 50, 15_000);
    let mut outs = Vec::new();
    for _ in 0..20 * 20 {
        let ev = r.step();
        for e in round_events(&ev, evt::OUT) {
            assert_eq!(e.arg, reason::GOO as i32);
            outs.push((e.slot, e.tick));
        }
        if outs.len() == 3 {
            break;
        }
    }
    // feet at y = 10 below the goo when y_goo > 10: t > 1 s + 2.01 s → at 20 Hz the first tick with goo_y > 10
    let first = outs[0].1 - rush;
    let goo_at = |t: u32| crate::hazards::goo_y(&goo, t as i64 * 3);
    assert!(goo_at(first) > 10 && goo_at(first - 1) <= 10, "knocked out at the exact tick");
    assert_eq!(outs.iter().map(|o| o.0).collect::<Vec<_>>(), vec![0, 1, 2]);
    // only the survivor is left climbing; the timer is off and nobody escapes, so end it by the timer rule
    let blob = r.blob.clone();
    let m = MapView::new(&blob).unwrap();
    let mut ev = StepEvents::default();
    r.w.round.timer_expired = true;
    let t = r.w.tick;
    timed::end_tick_force_end(&mut r.w, &m, t, &mut ev);
    let res = r.w.round.result;
    assert_eq!(res.order[0], 3, "the survivor first");
    assert_eq!(res.qualified_count(), 1, "fewer than N alive: only the survivors qualify");
    assert_eq!(res.reasons[1], reason::GOO);
}

// ------------------------------------------------------------------ skill (M4RulesTests, StarFountainTests)

fn skill_map(timer_s: i32, bumper: bool) -> Vec<u8> {
    let mut f = StarFieldDef { anchor_count: 12, ..Default::default() };
    for a in 0..12 {
        f.anchors[a * 3] = -11_000 + 2000 * a as i32;
        f.anchors[a * 3 + 1] = 300;
        f.anchors[a * 3 + 2] = 10_000;
        f.attach[a] = -1;
    }
    f.golden_count = 1;
    f.golden[0] = 11;
    f.wave_every_ticks = 150;
    f.wave_size = 8;
    f.wave_life_ticks = 300;
    f.golden_every_ticks = 450;
    f.golden_life_ticks = 240;
    f.golden_value = 3;
    f.pickup_radius = 400;
    f.drop_pop_ticks = 15;
    f.drop_life_ticks = 240;
    f.drop_distance = 1000;
    let mut hz = vec![haz(HazardType::StarPickup, "", |w| f.write(w))];
    if bumper {
        let b = BumperDef { center_x: 20_000, center_y: 0, center_z: 0, radius: 500, height: 1500, contact_margin: 60, knockback_cms: 600, knock_up_cms: 350, stun_ticks: 11, immunity_ticks: 26 };
        hz.push(haz(HazardType::Bumper, "", |w| b.write(w)));
    }
    let spec = MapSpec {
        meta: meta_for(mode::SKILL, timer_s),
        colliders: vec![crate::mapblob::testmaps::ground(40_000)],
        spawns: (0..12).map(|i| (IVec3::new(-11_000 + 2000 * i, 50, 0), 0u8)).collect(),
        hazards: [hz, Vec::new()],
        cell_mm: 2000,
        ..Default::default()
    };
    build(&spec)
}

/// `M4RulesTests.StarTimeline_IsPure_WavesOf8DistinctNonGoldenAnchors_GoldenEvery15s`.
#[test]
fn skill_star_timeline_pure_distinct() {
    let r = Round::new(skill_map(60, false), 1, 20);
    let f = r.w.hazards(&r.map()).star_field(0);
    for seed in [1u64, 99, 12345] {
        for wave in 0..10 {
            let mut seen = Vec::new();
            for k in 0..8 {
                let s = crate::hazards::star_slot(&f, seed, wave * 8 + k).unwrap();
                assert!(!seen.contains(&s.anchor) && s.anchor != 11, "distinct, non-golden");
                assert_eq!(s.spawn_t, wave * 150);
                seen.push(s.anchor);
            }
        }
        let g = crate::hazards::star_slot(&f, seed, crate::hazards::STAR_GOLDEN_BASE + 1).unwrap();
        assert_eq!((g.anchor, g.spawn_t, g.value), (11, 450, 3));
    }
}

/// `M4RulesTests.Skill_PickupOnlyByTheServerCapsule_KnockbackDropsExactlyOne_ReGrabbable`.
#[test]
fn skill_pickup_drop_regrab() {
    let mut r = Round::new(skill_map(60, true), 2, 20);
    r.begin(1, ROUND_INTRO_30, ROUND_COUNTDOWN_30);
    r.run_until_rush();
    let f = r.w.hazards(&r.map()).star_field(0);
    let s0 = crate::hazards::star_slot(&f, r.w.hazard_seed, 0).unwrap();
    let a = s0.anchor as usize;
    r.place(0, f.anchors[a * 3], 10, f.anchors[a * 3 + 2]);
    r.step();
    assert_eq!(r.w.players[0].r.score, 1, "picked up");
    r.place(0, 20_000 - 750, 10, 0);
    let mut dropped = 0;
    for _ in 0..10 {
        dropped += round_events(&r.step(), evt::DROPPED).len();
        if dropped > 0 {
            break;
        }
    }
    assert_eq!(dropped, 1, "a knockback drops exactly one");
    assert_eq!(r.w.players[0].r.score, 0);
    assert_eq!(r.w.rx.drop_count, 1);
    let d = r.w.rx.drops[0];
    let land = crate::hazards::drop_pos(&f, d.pos, 1, 100);
    r.place(1, land.x, 10, land.z);
    for _ in 0..30 {
        r.step();
    }
    assert_eq!(r.w.players[1].r.score, 1, "re-grabbed after the pop time");
    assert!(r.w.rx.drops[0].taken);
}

/// `M4RulesTests.Skill_Ranking_TopNByScore_TiesEarliestTimeAtFinalScore_ThenSlot`.
#[test]
fn skill_ranking() {
    let mut r = Round::new(skill_map(1, false), 5, 20);
    r.begin(3, ROUND_INTRO_30, ROUND_COUNTDOWN_30);
    r.run_until_rush();
    for (s, (sc, t)) in [(3, 50), (5, 40), (5, 30), (3, 50), (0, 0)].iter().enumerate() {
        r.w.players[s].r.score = *sc;
        r.w.players[s].r.score_tick = *t;
        r.place(s, 30_000, 10, -30_000 + 2000 * s as i32);
    }
    while !is_over(&r.w) {
        r.step();
    }
    let res = r.w.round.result;
    assert_eq!(&res.order[..5], &[2, 1, 0, 3, 4]);
    assert_eq!(res.qualified_count(), 3);
    assert_eq!(res.reasons[3], reason::LOWER_SCORE);
}

// ------------------------------------------------------------------ final (M4RulesTests, LidFlipTests)

fn lid_def(top: i32) -> LidGridDef {
    let mut d = LidGridDef {
        n: 5,
        lid: 1800,
        gap: 60,
        thickness: 400,
        top,
        lit_ticks: 30,
        flipped_ticks: 45,
        first_wave_ticks: 9000,
        wave_interval_count: 1,
        waves_per_stage: 8,
        flip_permille_count: 1,
        retire_warn_ticks: 90,
        min_inner_solid: 5,
        ..Default::default()
    };
    d.wave_intervals[0] = 90;
    d.flip_permille[0] = 200;
    d
}

/// Two 5 × 5 lid grids: top at 6 m, lower at 0 m (same footprint).
fn lid_map(timer_s: i32) -> Vec<u8> {
    let mut hz = Vec::new();
    let mut cols = Vec::new();
    for (g, top) in [6000, 0].iter().enumerate() {
        let d = lid_def(*top);
        hz.push(haz(HazardType::LidFlip, if g == 0 { "lid_top" } else { "lid_low" }, |w| d.write(w)));
        for lid in 0..d.count() {
            let (x, y, z) = d.lid_center(lid);
            cols.push(ColliderSpec { shape: aabb_box(IVec3::new(x, y, z), IVec3::new(900, 200, 900)), surface: -1, dyn_kind: crate::mapblob::dyn_kind::LID, dyn_index: (g as i32 * 25 + lid) as u16 });
        }
    }
    let spec = MapSpec {
        meta: meta_for(mode::FINAL, timer_s),
        colliders: cols,
        spawns: (0..12).map(|i| (IVec3::new(-3720 + 1860 * (i % 5), 6010, -3720 + 1860 * (i / 5)), 0u8)).collect(),
        hazards: [hz, Vec::new()],
        cell_mm: 2000,
        ..Default::default()
    };
    build(&spec)
}

/// `M4RulesTests.Final_LidMap_TracksTheShelfStoodOn_AndCountsTheDrop`.
#[test]
fn final_lid_tracks_shelf_and_drop() {
    let mut r = Round::new(lid_map(120), 3, 20);
    r.begin(1, ROUND_INTRO_30, ROUND_COUNTDOWN_30);
    r.run_until_rush();
    for _ in 0..5 {
        r.step();
    }
    assert_eq!(r.w.players[0].r.lid_shelf, 0);
    r.place(0, 0, 10, 0);
    for _ in 0..5 {
        r.step();
    }
    let p = r.w.players[0].r;
    assert_eq!((p.lid_shelf, p.lid_falls), (1, 1));
    assert!(p.lid_drop_tick > 0);
}

/// `M4RulesTests.Final_LidMap_TimerTiebreak_TopShelfThenFewerFallsThenLaterDropThenSlot` (the G4.13 tiebreak).
#[test]
fn final_lid_tiebreak_shelf_falls_later_drop_slot() {
    let mut r = Round::new(lid_map(120), 5, 20);
    r.begin(1, ROUND_INTRO_30, ROUND_COUNTDOWN_30);
    r.run_until_rush();
    for (s, (shelf, falls, drop)) in [(1u8, 1u8, 100u32), (0, 2, 50), (1, 1, 200), (0, 1, 90), (0, 2, 50)].iter().enumerate() {
        r.w.players[s].r.lid_shelf = *shelf;
        r.w.players[s].r.lid_falls = *falls;
        r.w.players[s].r.lid_drop_tick = *drop;
    }
    let blob = r.blob.clone();
    let m = MapView::new(&blob).unwrap();
    r.w.round.timer_expired = true;
    let mut ev = StepEvents::default();
    let t = r.w.tick;
    timed::end_tick_force_end(&mut r.w, &m, t, &mut ev);
    let res = r.w.round.result;
    // top shelf: fewer falls (3), then 1 / 4 tied → slot; lower shelf: later drop first (2 before 0)
    assert_eq!(&res.order[..5], &[3, 1, 4, 2, 0]);
    assert_eq!(res.qualified_count(), 1);
    assert_eq!(res.values[0], 1);
}

/// `M4RulesTests.Final_LastStanding_Wins` / `LidFlipTests.FullRound_*_OneChampion`: falls knock out; the last wins.
#[test]
fn final_last_standing_wins() {
    let mut r = Round::new(lid_map(120), 3, 20);
    r.begin(1, ROUND_INTRO_30, ROUND_COUNTDOWN_30);
    r.run_until_rush();
    r.place(0, 20_000, 1000, 20_000);
    r.place(1, -20_000, 1000, 20_000);
    for _ in 0..20 * 10 {
        r.step();
        if is_over(&r.w) {
            break;
        }
    }
    assert!(is_over(&r.w));
    let res = r.w.round.result;
    assert_eq!(res.order[0], 2);
    assert_eq!(res.qualified_count(), 1);
    assert_eq!(res.reasons[1], reason::FELL_OUT);
}

/// `M4RulesTests.Final_TimerAt120s_EndsWithOneQualifier`.
#[test]
fn final_timer_ends_with_one_qualifier() {
    let mut r = Round::new(lid_map(2), 4, 20);
    r.begin(1, ROUND_INTRO_30, ROUND_COUNTDOWN_30);
    r.run_until_rush();
    let rush = r.w.round.rush_tick;
    while !is_over(&r.w) {
        r.step();
    }
    assert_eq!(r.w.tick, rush + 40, "2 s timer at 20 Hz");
    assert_eq!(r.w.round.result.qualified_count(), 1);
    assert!(r.w.round.result.timer_expired);
}

// ------------------------------------------------------------------ size

#[test]
fn world_size_with_rules_and_director() {
    let b = build(&crate::mapblob::testmaps::flat_arena_spec());
    let m = MapView::new(&b).unwrap();
    let w = World::new(&m, 20);
    let mut buf = [0u8; 4096];
    let n = w.to_bytes(&mut buf);
    std::println!("World canonical size (rules + director): {n} bytes");
    assert!(n <= world::WORLD_MAX_BYTES);
}
