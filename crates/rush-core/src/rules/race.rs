//! `RoundRules.Race` (port of `Shared/RaceRules.cs`): ordered checkpoints with the reachability check, respawn at
//! the last checkpoint on a free spot, the first N finishers qualify (the round ends at the N-th), and on timer
//! expiry the rest are ranked by checkpoint ↓, course distance to the next checkpoint ↑, checkpoint tick ↑, slot ↑.
//!
//! **Intentional difference:** C# measures the course distance in float metres and compares at mm resolution;
//! rush-core measures it in integer mm (segment lengths by `isqrt`, the projection by `div_round`). Ties within
//! ±1 mm can order differently from C#; nothing else changes.

// Fixed slot loops index several parallel arrays (fixed iteration order, P1 §1.2).
#![allow(clippy::needless_range_loop, clippy::manual_range_contains)]

use super::{emit, reason, sf, sort_slots, RoundResult};
use crate::fx::{div_round, isqrt_i64};
use crate::mapblob::MapView;
use crate::motor::MotorState;
use crate::world::{StepEvents, World, MAX_PLAYERS};

/// `RaceRules.MaxCourseSpeedMms`.
pub const MAX_COURSE_SPEED_MMS: i64 = 15_000;

/// `TryCheckpoint`: only the next checkpoint counts, and only if it is reachable in the time taken.
pub fn try_checkpoint(w: &mut World, map: &MapView, slot: usize, s: &MotorState, tick: u32, ev: &mut StepEvents) {
    let n = map.checkpoint_count().min(15);
    let next = w.players[slot].r.checkpoint as usize;
    if next >= n {
        return;
    }
    let c = map.checkpoint(next);
    if !c.contains(s.pos.x, s.pos.y, s.pos.z) {
        return;
    }
    let prev_mm = if next == 0 { 0 } else { map.checkpoint(next - 1).course_mm };
    let since = if next == 0 { tick.wrapping_sub(w.round.rush_tick) } else { tick.wrapping_sub(w.players[slot].r.checkpoint_tick) } as i64;
    // C#: (long)(c.CourseMm − prevMm) · 30 / MaxCourseSpeedMms (floor) — at the world rate.
    let min_ticks = (c.course_mm - prev_mm) as i64 * w.rate as i64 / MAX_COURSE_SPEED_MMS;
    if since < min_ticks {
        w.players[slot].r.rejected = w.players[slot].r.rejected.saturating_add(1);
        return;
    }
    {
        let r = &mut w.players[slot].r;
        r.checkpoint = (next + 1) as u8;
        r.checkpoint_tick = tick;
    }
    if next + 1 < n {
        emit(w, ev, super::evt::CHECKPOINT, tick, slot as u8, (next + 1) as i32);
        return;
    }
    // Finish.
    w.round.finished_count += 1;
    let place = w.round.finished_count;
    {
        let r = &mut w.players[slot].r;
        r.set(sf::DONE, true);
        r.done_tick = tick;
        r.place = place;
    }
    if (place as usize) <= MAX_PLAYERS {
        w.round.finish_order[place as usize - 1] = slot as u8;
    }
    emit(w, ev, super::evt::FINISHED, tick, slot as u8, place as i32);
    if w.round.qualified_count < w.round.qualify_count {
        w.players[slot].r.set(sf::QUALIFIED, true);
        w.round.qualified_count += 1;
        let q = w.round.qualified_count;
        emit(w, ev, super::evt::QUALIFIED, tick, slot as u8, q as i32);
    }
    if w.round.qualified_count >= w.round.qualify_count {
        w.round.end_pending = true;
    }
}

/// `RaceRules.EndTick` (Rush phase only).
pub fn end_tick(w: &mut World, map: &MapView, tick: u32, ev: &mut StepEvents) {
    for i in 0..MAX_PLAYERS {
        let r = w.players[i].r;
        if !r.has(sf::IN) || r.has(sf::DONE) || !w.players[i].active() {
            continue;
        }
        let s = w.players[i].m;
        if s.has(crate::motor::flag::RESPAWNING) {
            continue;
        }
        try_checkpoint(w, map, i, &s, tick, ev);
        if w.players[i].r.has(sf::DONE) {
            w.players[i].m.vel = crate::vec::IVec3::ZERO; // frozen at the finish
        }
    }
    if w.round.end_pending {
        w.round.end_pending = false;
        end(w, map, tick, ev);
        return;
    }
    let mut active = 0;
    for i in 0..MAX_PLAYERS {
        let r = w.players[i].r;
        if r.has(sf::IN) && w.players[i].active() && !r.has(sf::DONE) {
            active += 1;
        }
    }
    if w.round.rush_end != 0 && tick >= w.round.rush_end {
        w.round.timer_expired = true;
        end(w, map, tick, ev);
    } else if active == 0 && w.round.finished_count > 0 {
        end(w, map, tick, ev);
    }
}

/// Course arc length (mm) of the closest point of the polyline to `p` (`CourseDistance`, integer).
pub fn course_distance(map: &MapView, x: i32, y: i32, z: i32) -> i64 {
    let n = map.course_len().min(256);
    if n < 2 {
        return 0;
    }
    let mut best = i64::MAX;
    let mut best_s = 0i64;
    let mut acc = 0i64;
    for i in 1..n {
        let a = map.course_point(i - 1);
        let b = map.course_point(i);
        let (ux, uy, uz) = ((b.x - a.x) as i64, (b.y - a.y) as i64, (b.z - a.z) as i64);
        let len2 = ux * ux + uy * uy + uz * uz;
        let len = isqrt_i64(len2);
        let (px, py, pz) = ((x - a.x) as i64, (y - a.y) as i64, (z - a.z) as i64);
        // t in Q16, clamped to [0, 1]
        let t = if len2 > 0 { div_round((px * ux + py * uy + pz * uz) << 16, len2).clamp(0, 1 << 16) } else { 0 };
        let cx = div_round(ux * t, 1 << 16) - px;
        let cy = div_round(uy * t, 1 << 16) - py;
        let cz = div_round(uz * t, 1 << 16) - pz;
        let d2 = cx * cx + cy * cy + cz * cz;
        if d2 < best {
            best = d2;
            best_s = acc + div_round(len * t, 1 << 16);
        }
        acc += len;
    }
    best_s
}

/// `RemainingToNext`: course distance (mm) from the player to checkpoint `cp` (the next one), ≥ 0.
pub fn remaining_to_next(map: &MapView, cp: usize, s: &MotorState) -> i64 {
    if cp >= map.checkpoint_count() {
        return 0;
    }
    let target = map.checkpoint(cp).course_mm as i64;
    (target - course_distance(map, s.pos.x, s.pos.y, s.pos.z)).max(0)
}

/// `RaceRules.End`: finishers by place, then the ranking key; the timer's remaining places qualify by the key.
pub fn end(w: &mut World, map: &MapView, tick: u32, ev: &mut StepEvents) {
    let mut order = [0u8; MAX_PLAYERS];
    let mut n = 0usize;
    for k in 0..(w.round.finished_count as usize).min(MAX_PLAYERS) {
        order[n] = w.round.finish_order[k];
        n += 1;
    }
    let mut rest = [0u8; MAX_PLAYERS];
    let mut nr = 0usize;
    let mut dist = [0i64; MAX_PLAYERS];
    for i in 0..MAX_PLAYERS {
        let r = w.players[i].r;
        if r.has(sf::IN) && !r.has(sf::DONE) {
            rest[nr] = i as u8;
            nr += 1;
            let s = if w.players[i].active() { w.players[i].m } else { MotorState::default() };
            dist[i] = remaining_to_next(map, r.checkpoint as usize, &s);
        }
    }
    let pl = w.players;
    sort_slots(&mut rest[..nr], |a, b| {
        let (ra, rb) = (pl[a as usize].r, pl[b as usize].r);
        if ra.checkpoint != rb.checkpoint {
            return rb.checkpoint < ra.checkpoint; // more checkpoints first
        }
        if dist[a as usize] != dist[b as usize] {
            return dist[a as usize] < dist[b as usize];
        }
        if ra.checkpoint_tick != rb.checkpoint_tick {
            return ra.checkpoint_tick < rb.checkpoint_tick;
        }
        a < b
    });
    for &s in &rest[..nr] {
        order[n] = s;
        n += 1;
    }
    let mut res = RoundResult { n: n as u8, target: w.round.qualify_count, start_tick: w.round.rush_tick, end_tick: tick, timer_expired: w.round.timer_expired, ..Default::default() };
    for p in 0..n {
        let slot = order[p] as usize;
        res.order[p] = slot as u8;
        if !w.players[slot].r.has(sf::QUALIFIED) && w.round.qualified_count < w.round.qualify_count {
            w.players[slot].r.set(sf::QUALIFIED, true);
            w.round.qualified_count += 1;
            let q = w.round.qualified_count;
            emit(w, ev, super::evt::QUALIFIED, tick, slot as u8, q as i32);
        }
        let r = w.players[slot].r;
        let q = r.has(sf::QUALIFIED);
        if q {
            res.qualified |= 1 << p;
        } else {
            w.players[slot].r.set(sf::ELIMINATED, true);
            emit(w, ev, super::evt::ELIMINATED, tick, slot as u8, (p + 1) as i32);
        }
        res.reasons[p] = if !q && w.round.timer_expired && r.done_tick == 0 { reason::TIMEOUT_RANK } else { reason::NOT_QUALIFIED };
        res.values[p] = if r.has(sf::DONE) { tick_diff(r.done_tick, w.round.rush_tick) } else { -(r.checkpoint as i32) };
    }
    w.round.result = res;
    w.round.has_result = true;
    let qc = w.round.qualified_count;
    emit(w, ev, super::evt::ROUND_END, tick, 255, qc as i32);
    super::set_phase(w, super::phase::ENDED, tick, ev);
}

fn tick_diff(a: u32, b: u32) -> i32 {
    a.wrapping_sub(b) as i32
}

/// `RaceRules.Respawn`: at the last checkpoint, on the first free spot (round-robin, nobody within 0.8 m).
pub fn respawn(w: &mut World, map: &MapView, slot: usize, ev: &mut StepEvents) -> bool {
    let r = w.players[slot].r;
    let cp = r.checkpoint as usize;
    if cp == 0 || cp > map.checkpoint_count() || !r.has(sf::IN) {
        return false;
    }
    let c = map.checkpoint(cp - 1);
    let n = c.respawn_count as usize;
    if n == 0 {
        return false;
    }
    let start = w.round.respawn_cursor[cp.min(15)] as usize;
    let mut pick = start % n;
    for k in 0..n.min(32) {
        let j = (start + k) % n;
        let (p, _) = map.respawn(c.respawn_start + j as u32);
        if !occupied(w, slot, p.x, p.y, p.z) {
            pick = j;
            break;
        }
    }
    let (p, yaw) = map.respawn(c.respawn_start + pick as u32);
    let bot = w.players[slot].m.flags & crate::motor::flag::BOT;
    w.players[slot].m = MotorState::at(p, yaw);
    w.players[slot].m.flags |= bot;
    w.players[slot].respawn_ticks = 0;
    w.round.respawn_cursor[cp.min(15)] = ((pick + 1) % n) as u8;
    emit(w, ev, super::evt::RESPAWNED, w.tick, slot as u8, cp as i32);
    true
}

fn occupied(w: &World, me: usize, x: i32, y: i32, z: i32) -> bool {
    for i in 0..MAX_PLAYERS {
        if i == me || !w.players[i].active() {
            continue;
        }
        let s = &w.players[i].m;
        let (dx, dz, dy) = ((s.pos.x - x) as i64, (s.pos.z - z) as i64, (s.pos.y - y) as i64);
        if dx * dx + dz * dz < 800 * 800 && dy > -1000 && dy < 1000 {
            return true;
        }
    }
    false
}
