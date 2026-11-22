//! The M4 timed modes: `TimedRoundRules` (port of `Shared/TimedRoundRules.cs`) with `SurvivalRules.cs` (Goo Tower),
//! `SkillRules.cs` (Star Fountain) and `FinalRules.cs` (Lid Flip, incl. the G4.13 lid tiebreak: shelf, then falls,
//! then later drop, then slot; and the crumble-tile variant: deepest shelf, then fewer tiles, then slot).

// Fixed slot loops index several parallel arrays (fixed iteration order, P1 §1.2).
#![allow(clippy::needless_range_loop, clippy::manual_range_contains)]

use super::{emit, reason, sf, sort_slots, RoundResult};
use crate::hazards::defs::HazardType;
use crate::hazards::{self, STAR_DROP_BASE, STAR_GOLDEN_BASE};
use crate::mapblob::{mode, MapView};
use crate::motor::flag;
use crate::vec::IVec3;
use crate::world::{step_result, StepEvents, World, MAX_DROPS, MAX_PLAYERS, MAX_RX_TILES, STAR_BITS};

#[inline]
pub fn in_play(w: &World, i: usize) -> bool {
    let r = &w.players[i].r;
    r.has(sf::IN) && !r.has(sf::OUT) && !r.has(sf::DONE)
}

fn count_in_play(w: &World) -> usize {
    (0..MAX_PLAYERS).filter(|&i| in_play(w, i)).count()
}

/// `KnockOut`.
fn knock_out(w: &mut World, slot: usize, tick: u32, why: u8, ev: &mut StepEvents) {
    let r = &mut w.players[slot].r;
    if r.has(sf::OUT) || r.has(sf::DONE) {
        return;
    }
    r.set(sf::OUT, true);
    r.out_tick = tick;
    r.out_reason = why;
    emit(w, ev, super::evt::OUT, tick, slot as u8, why as i32);
}

/// `TimedRoundRules.EndTick` (Rush phase only).
pub fn end_tick(w: &mut World, map: &MapView, tick: u32, ev: &mut StepEvents) {
    match w.round.mode {
        mode::SURVIVAL => survival_tick(w, map, tick, ev),
        mode::SKILL => skill_tick(w, map, tick, ev),
        _ => final_tick(w, map, tick, ev),
    }
    if w.round.phase == super::phase::ENDED {
        return;
    }
    let should_end = match w.round.mode {
        mode::SURVIVAL => w.round.finished_count >= w.round.qualify_count || count_in_play(w) == 0,
        mode::SKILL => false,
        _ => count_in_play(w) <= 1,
    };
    if should_end {
        end(w, map, tick, ev);
        return;
    }
    if w.round.rush_end != 0 && tick >= w.round.rush_end {
        w.round.timer_expired = true;
        end(w, map, tick, ev);
    }
}

fn end(w: &mut World, map: &MapView, tick: u32, ev: &mut StepEvents) {
    let res = match w.round.mode {
        mode::SURVIVAL => survival_rank(w, tick),
        mode::SKILL => skill_rank(w, tick),
        _ => final_rank(w, map, tick),
    };
    for k in 0..res.n as usize {
        let slot = res.order[k] as usize;
        let q = res.qualified_at(k);
        w.players[slot].r.set(sf::QUALIFIED, q);
        emit(w, ev, if q { super::evt::QUALIFIED } else { super::evt::ELIMINATED }, tick, slot as u8, (k + 1) as i32);
    }
    w.round.result = res;
    w.round.has_result = true;
    emit(w, ev, super::evt::ROUND_END, tick, 255, res.qualified_count() as i32);
    super::set_phase(w, super::phase::ENDED, tick, ev);
}

fn push(res: &mut RoundResult, slot: u8, q: bool, why: u8, v: i32) {
    let k = res.n as usize;
    if k >= MAX_PLAYERS {
        return;
    }
    res.order[k] = slot;
    if q {
        res.qualified |= 1 << k;
    }
    res.reasons[k] = why;
    res.values[k] = v;
    res.n += 1;
}

fn new_result(w: &World, tick: u32) -> RoundResult {
    RoundResult { target: w.round.qualify_count, start_tick: w.round.rush_tick, end_tick: tick, timer_expired: w.round.timer_expired, ..Default::default() }
}

// ------------------------------------------------------------------ survival

fn survival_tick(w: &mut World, map: &MapView, tick: u32, ev: &mut StepEvents) {
    let nc = map.checkpoint_count();
    let summit = if nc > 0 { Some(map.checkpoint(nc - 1)) } else { None };
    for i in 0..MAX_PLAYERS {
        if !in_play(w, i) || !w.players[i].active() {
            continue;
        }
        let s = w.players[i].m;
        if s.has(flag::RESPAWNING) {
            let why = if w.players[i].last_step & step_result::GOOED != 0 { reason::GOO } else { reason::FELL_OUT };
            knock_out(w, i, tick, why, ev);
            continue;
        }
        if s.pos.y > w.players[i].r.height {
            w.players[i].r.height = s.pos.y;
        }
        if let Some(c) = summit {
            if w.round.finished_count < w.round.qualify_count && c.contains(s.pos.x, s.pos.y, s.pos.z) {
                w.players[i].r.set(sf::DONE, true);
                w.players[i].r.done_tick = tick;
                w.round.finished_count += 1;
                w.players[i].m.vel = IVec3::ZERO;
                let e = w.round.finished_count;
                emit(w, ev, super::evt::FINISHED, tick, i as u8, e as i32);
            }
        }
    }
}

fn survival_rank(w: &World, tick: u32) -> RoundResult {
    let (mut esc, mut alive, mut outs) = ([0u8; MAX_PLAYERS], [0u8; MAX_PLAYERS], [0u8; MAX_PLAYERS]);
    let (mut ne, mut na, mut no) = (0, 0, 0);
    for i in 0..MAX_PLAYERS {
        let r = &w.players[i].r;
        if !r.has(sf::IN) {
            continue;
        }
        if r.has(sf::DONE) {
            esc[ne] = i as u8;
            ne += 1;
        } else if r.has(sf::OUT) {
            outs[no] = i as u8;
            no += 1;
        } else {
            alive[na] = i as u8;
            na += 1;
        }
    }
    let pl = w.players;
    sort_slots(&mut esc[..ne], |a, b| {
        let (ta, tb) = (pl[a as usize].r.done_tick, pl[b as usize].r.done_tick);
        if ta != tb {
            ta < tb
        } else {
            a < b
        }
    });
    sort_slots(&mut alive[..na], |a, b| {
        let (ya, yb) = (pl[a as usize].m.pos.y, pl[b as usize].m.pos.y);
        if ya != yb {
            ya > yb
        } else {
            a < b
        }
    });
    sort_slots(&mut outs[..no], |a, b| {
        let (ta, tb) = (pl[a as usize].r.out_tick, pl[b as usize].r.out_tick);
        if ta != tb {
            ta > tb // last to go first
        } else {
            a < b
        }
    });
    let mut res = new_result(w, tick);
    let mut q = 0u8;
    for &s in &esc[..ne] {
        push(&mut res, s, true, reason::NOT_QUALIFIED, pl[s as usize].r.done_tick.wrapping_sub(w.round.rush_tick) as i32);
        q += 1;
    }
    for &s in &alive[..na] {
        let ok = w.round.timer_expired && q < w.round.qualify_count;
        let why = if ok { reason::NOT_QUALIFIED } else if w.round.timer_expired { reason::TIMEOUT_RANK } else { reason::NOT_QUALIFIED };
        push(&mut res, s, ok, why, pl[s as usize].m.pos.y);
        if ok {
            q += 1;
        }
    }
    for &s in &outs[..no] {
        push(&mut res, s, false, pl[s as usize].r.out_reason, pl[s as usize].r.height);
    }
    res
}

// ------------------------------------------------------------------ skill

fn star_taken(w: &World, id: i32) -> bool {
    if (0..STAR_BITS as i32).contains(&id) {
        (w.rx.star_taken[id as usize / 64] >> (id % 64)) & 1 == 1
    } else if id >= STAR_DROP_BASE {
        let d = (id - STAR_DROP_BASE) as u32;
        let first = (w.rx.drop_count as u32).saturating_sub(MAX_DROPS as u32);
        if d < first || d >= w.rx.drop_count as u32 {
            return true; // overwritten / not yet dropped
        }
        w.rx.drops[d as usize % MAX_DROPS].taken
    } else {
        true
    }
}

fn collect(w: &mut World, slot: usize, id: i32, value: i32, tick: u32, ev: &mut StepEvents) {
    if star_taken(w, id) {
        return;
    }
    if (0..STAR_BITS as i32).contains(&id) {
        w.rx.star_taken[id as usize / 64] |= 1u64 << (id % 64);
    } else {
        let d = (id - STAR_DROP_BASE) as usize;
        w.rx.drops[d % MAX_DROPS].taken = true;
    }
    w.rx.seq = w.rx.seq.wrapping_add(1);
    let r = &mut w.players[slot].r;
    r.score += value;
    r.score_tick = tick;
    let sc = r.score;
    emit(w, ev, super::evt::SCORED, tick, slot as u8, sc);
}

/// `SkillRules.LiveStars` at the 30 Hz hazard tick `t`: up to `out.len()` timeline ids, normal then golden.
pub fn live_stars(w: &World, f: &crate::hazards::defs::StarFieldDef, seed: u64, t: i64, out: &mut [i32; 96]) -> usize {
    let mut n = 0;
    if t < 0 || f.wave_every_ticks <= 0 {
        return 0;
    }
    let every = f.wave_every_ticks as i64;
    let w1 = t / every;
    let w0 = ((t - f.wave_life_ticks as i64) / every).max(0); // C#: int division (truncation) then max 0
    let mut wv = w0;
    while wv <= w1 && n < out.len() {
        let mut k = 0;
        while k < f.wave_size && n < out.len() {
            let id = (wv * f.wave_size as i64 + k as i64) as i32;
            if id >= STAR_GOLDEN_BASE {
                break;
            }
            if let Some(s) = hazards::star_slot(f, seed, id) {
                if t >= s.spawn_t as i64 && t < s.expire_t as i64 && !star_taken(w, id) {
                    out[n] = id;
                    n += 1;
                }
            }
            k += 1;
        }
        wv += 1;
    }
    if f.golden_every_ticks > 0 {
        let mut g = 1i64;
        while g * f.golden_every_ticks as i64 <= t && g < 64 && n < out.len() {
            let id = STAR_GOLDEN_BASE + g as i32;
            if let Some(s) = hazards::star_slot(f, seed, id) {
                if t >= s.spawn_t as i64 && t < s.expire_t as i64 && !star_taken(w, id) {
                    out[n] = id;
                    n += 1;
                }
            }
            g += 1;
        }
    }
    n
}

fn skill_tick(w: &mut World, map: &MapView, tick: u32, ev: &mut StepEvents) {
    let hz = w.hazards(map);
    if hz.count(HazardType::StarPickup) == 0 {
        return;
    }
    let f = hz.star_field(0);
    let cfg_r = 310; // C#: QuantizeMm(Cfg.Radius); every config has r 310 / h 1000
    let cfg_h = 1000;
    // 1) knockbacks drop one carried star where you were
    for i in 0..MAX_PLAYERS {
        if !in_play(w, i) || !w.players[i].active() {
            continue;
        }
        if w.players[i].last_step & step_result::HAZARD_HIT == 0 || w.players[i].r.score <= 0 {
            continue;
        }
        let s = w.players[i].m;
        w.players[i].r.score -= 1;
        w.players[i].r.score_tick = tick;
        let d = w.rx.drop_count as usize;
        // C# stores cm (truncated toward zero) and reads them back × 10.
        w.rx.drops[d % MAX_DROPS] = crate::world::DroppedStar {
            tick,
            slot: i as u8,
            taken: false,
            pos: IVec3::new(s.pos.x / 10 * 10, (s.pos.y + 600) / 10 * 10, s.pos.z / 10 * 10),
        };
        w.rx.drop_count = w.rx.drop_count.wrapping_add(1);
        w.rx.seq = w.rx.seq.wrapping_add(1);
        let sc = w.players[i].r.score;
        emit(w, ev, super::evt::DROPPED, tick, i as u8, sc);
    }
    // 2) pickups in slot order
    let t60 = w.t60(tick);
    let t = hazards::t30(t60);
    let step60 = 60 / w.rate.max(1) as i64;
    let mut live = [0i32; 96];
    for i in 0..MAX_PLAYERS {
        if !in_play(w, i) || !w.players[i].active() {
            continue;
        }
        let s = w.players[i].m;
        if s.has(flag::RESPAWNING) {
            continue;
        }
        let n = live_stars(w, &f, w.hazard_seed, t, &mut live);
        for &id in &live[..n] {
            let sl = match hazards::star_slot(&f, w.hazard_seed, id) {
                Some(x) => x,
                None => continue,
            };
            let p = hazards::star_anchor(&f, &hz, sl.anchor, t60);
            if hazards::star_touch(p, s.pos, cfg_r, cfg_h, f.pickup_radius) {
                collect(w, i, id, sl.value, tick, ev);
            }
        }
        let first = (w.rx.drop_count as u32).saturating_sub(MAX_DROPS as u32);
        for d in first..w.rx.drop_count as u32 {
            let e = w.rx.drops[d as usize % MAX_DROPS];
            let age60 = tick.wrapping_sub(e.tick) as i64 * step60;
            if e.taken || age60 < 2 * f.drop_pop_ticks as i64 || age60 >= 2 * f.drop_life_ticks as i64 {
                continue;
            }
            let p = hazards::drop_pos(&f, e.pos, d + 1, hazards::t30(age60));
            if !hazards::star_touch(p, s.pos, cfg_r, cfg_h, f.pickup_radius) {
                continue;
            }
            collect(w, i, STAR_DROP_BASE + d as i32, 1, tick, ev);
        }
    }
}

fn skill_rank(w: &World, tick: u32) -> RoundResult {
    let mut all = [0u8; MAX_PLAYERS];
    let mut n = 0;
    for i in 0..MAX_PLAYERS {
        if w.players[i].r.has(sf::IN) {
            all[n] = i as u8;
            n += 1;
        }
    }
    let pl = w.players;
    sort_slots(&mut all[..n], |a, b| {
        let (pa, pb) = (pl[a as usize].r, pl[b as usize].r);
        if pa.score != pb.score {
            return pa.score > pb.score;
        }
        if pa.score_tick != pb.score_tick {
            return pa.score_tick < pb.score_tick;
        }
        a < b
    });
    let mut res = new_result(w, tick);
    for (k, &s) in all[..n].iter().enumerate() {
        let ok = k < w.round.qualify_count as usize;
        push(&mut res, s, ok, if ok { reason::NOT_QUALIFIED } else { reason::LOWER_SCORE }, pl[s as usize].r.score);
    }
    res
}

// ------------------------------------------------------------------ final

fn lid_mode(w: &World, map: &MapView) -> bool {
    let hz = w.hazards(map);
    hz.count(HazardType::CrumbleTile) == 0 && hz.count(HazardType::LidFlip) > 0
}

/// `ReactiveSet.TileContactAt(i, tick) == 0`: untouched, or back after its respawn.
fn tile_idle(w: &World, d: &crate::hazards::defs::TileGridDef, k: usize, tick: u32) -> bool {
    let c = w.rx.tile_contact[k];
    if c == 0 || tick < c {
        return true;
    }
    let back = d.respawn_for(k as i32) as u32;
    back != 0 && tick >= c + d.wobble_ticks as u32 + back
}

fn final_tick(w: &mut World, map: &MapView, tick: u32, ev: &mut StepEvents) {
    let hz = w.hazards(map);
    let lids = lid_mode(w, map);
    let has_grid = hz.count(HazardType::CrumbleTile) > 0;
    let grid = if has_grid { Some(hz.tile_grid(0)) } else { None };
    let ng = hz.count(HazardType::LidFlip).min(8);
    for i in 0..MAX_PLAYERS {
        if !in_play(w, i) || !w.players[i].active() {
            continue;
        }
        let s = w.players[i].m;
        if s.has(flag::RESPAWNING) {
            knock_out(w, i, tick, reason::FELL_OUT, ev);
            continue;
        }
        if lids {
            if s.has(flag::GROUNDED) {
                // TrackLidShelf
                for g in 0..ng {
                    if hz.lid_grid(g).lid_under(s.pos.x, s.pos.y, s.pos.z) < 0 {
                        continue;
                    }
                    let r = &mut w.players[i].r;
                    if g as u8 > r.lid_shelf {
                        r.lid_falls = r.lid_falls.saturating_add(1);
                        r.lid_drop_tick = tick;
                    }
                    r.lid_shelf = g as u8;
                    break;
                }
            }
            continue;
        }
        let d = match grid {
            Some(d) => d,
            None => continue,
        };
        if !s.has(flag::GROUNDED) {
            continue;
        }
        let tile = d.tile_under(s.pos.x, s.pos.y, s.pos.z);
        if tile < 0 || tile as usize >= MAX_RX_TILES || !crate::world::tile_solid(w, &d, tile as usize, tick) {
            continue;
        }
        let k = tile as usize;
        let r = &mut w.players[i].r;
        if r.touched & (1 << k) == 0 {
            r.touched |= 1 << k;
            r.tiles = r.tiles.saturating_add(1);
        }
        let shelf = d.shelf(tile) as u8;
        if shelf > r.deepest {
            r.deepest = shelf;
        }
        if tile_idle(w, &d, k, tick) {
            w.rx.tile_contact[k] = tick.max(1);
            w.rx.seq = w.rx.seq.wrapping_add(1);
        }
    }
}

/// `FinalRules.Tiebreak`: true when `a` ranks before `b`.
fn tiebreak(w: &World, lids: bool, a: u8, b: u8) -> bool {
    let (pa, pb) = (w.players[a as usize].r, w.players[b as usize].r);
    if lids {
        if pa.lid_shelf != pb.lid_shelf {
            return pa.lid_shelf < pb.lid_shelf; // still on the top shelf = better
        }
        if pa.lid_falls != pb.lid_falls {
            return pa.lid_falls < pb.lid_falls; // fewer falls = better
        }
        if pa.lid_drop_tick != pb.lid_drop_tick {
            return pa.lid_drop_tick > pb.lid_drop_tick; // dropped later = better
        }
        return a < b;
    }
    if pa.deepest != pb.deepest {
        return pa.deepest < pb.deepest;
    }
    if pa.tiles != pb.tiles {
        return pa.tiles < pb.tiles;
    }
    a < b
}

fn final_rank(w: &World, map: &MapView, tick: u32) -> RoundResult {
    let lids = lid_mode(w, map);
    let (mut alive, mut outs) = ([0u8; MAX_PLAYERS], [0u8; MAX_PLAYERS]);
    let (mut na, mut no) = (0, 0);
    for i in 0..MAX_PLAYERS {
        let r = &w.players[i].r;
        if !r.has(sf::IN) {
            continue;
        }
        if r.has(sf::OUT) {
            outs[no] = i as u8;
            no += 1;
        } else {
            alive[na] = i as u8;
            na += 1;
        }
    }
    sort_slots(&mut alive[..na], |a, b| tiebreak(w, lids, a, b));
    sort_slots(&mut outs[..no], |a, b| {
        let (ta, tb) = (w.players[a as usize].r.out_tick, w.players[b as usize].r.out_tick);
        if ta != tb {
            ta > tb
        } else {
            tiebreak(w, lids, a, b)
        }
    });
    let value = |s: u8| {
        let r = &w.players[s as usize].r;
        if lids {
            r.lid_shelf as i32 * 1000 + r.lid_falls as i32
        } else {
            r.deepest as i32 * 1000 + r.tiles as i32
        }
    };
    let mut res = new_result(w, tick);
    for (k, &s) in alive[..na].iter().enumerate() {
        let ok = k < w.round.qualify_count as usize;
        push(&mut res, s, ok, if ok { reason::NOT_QUALIFIED } else { reason::TIMEOUT_RANK }, value(s));
    }
    for &s in &outs[..no] {
        push(&mut res, s, false, reason::FELL_OUT, value(s));
    }
    res
}

/// Ends the round now with the ranking as it stands (`TimedRoundRules.RankNow` + End; tests, QA, director aborts).
pub fn end_tick_force_end(w: &mut World, map: &MapView, tick: u32, ev: &mut StepEvents) {
    end(w, map, tick, ev);
}
