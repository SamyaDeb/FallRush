//! Skill and Final plans: ports of `BotBrain.SkillPlan` (+ `AvoidBumpers`, `AvoidPads`), `BotBrain.FinalPlan`
//! (crumble shelves) and `LidPlan` / `PickLid` (G4.5 Lid Flip). Integers only.

use super::nav::react_to_bars;
use super::{before, bf, lo, steer_to, BotState, Ctx, Plan};
use crate::fx::{div_round, isqrt_i64, normalize_q15, ONE_Q15};
use crate::hazards::defs::{HazardType, StarFieldDef};
use crate::hazards::lid::{self, LidState, MAX_LIDS_PER_GRID};
use crate::hazards::{self, Hazards, STAR_DROP_BASE, STAR_GOLDEN_BASE};
use crate::input::{InputCmd, DIVE_HELD, DIVE_PRESSED, JUMP_HELD, JUMP_PRESSED};
use crate::motor::flag;
use crate::world::{MAX_DROPS, MAX_PLAYERS, MAX_RX_TILES, STAR_BITS};

fn bars(c: &Ctx, cmd: &mut InputCmd) {
    let (mut j, mut d) = (false, false);
    react_to_bars(c, cmd, &mut j, &mut d);
    if j {
        cmd.buttons |= JUMP_PRESSED | JUMP_HELD;
    }
    if d {
        cmd.buttons |= DIVE_PRESSED | DIVE_HELD;
    }
}

fn star_taken(c: &Ctx, id: i32) -> bool {
    id >= 0 && (id as usize) < STAR_BITS && (c.w.rx.star_taken[id as usize / 64] >> (id as usize % 64)) & 1 == 1
}

/// Island (platform index) a star anchor rides, or −1.
fn island_of(f: &StarFieldDef, a: i32) -> i32 {
    let at = if (a as usize) < f.attach.len() { f.attach[a as usize] } else { -1 };
    if (0..1000).contains(&at) {
        at
    } else {
        -1
    }
}

/// Star position for scoring: (x, z, value), or `None` when not a live, reachable target.
fn star_target(c: &Ctx, hz: &Hazards, f: &StarFieldDef, id: i32, t30: i64) -> Option<(i32, i32, i64)> {
    let ss = hazards::star_slot(f, hz.seed, id)?;
    if t30 < ss.spawn_t as i64 || t30 >= ss.expire_t as i64 || star_taken(c, id) {
        return None;
    }
    if island_of(f, ss.anchor) >= 0 {
        return None; // spring-pad island trips are not ported (see bots/mod.rs)
    }
    let t60 = c.t60(0).max(2 * ss.spawn_t as i64);
    let p = hazards::star_anchor(f, hz, ss.anchor, t60);
    let (dx, dz) = ((p.x - c.s.pos.x) as i64, (p.z - c.s.pos.z) as i64);
    // Aim where it will be when we get there (≤ 1 s lead at 5 m/s).
    let lead30 = (isqrt_i64(dx * dx + dz * dz) * 30 / 5000).min(30);
    let p = if lead30 > 0 { hazards::star_anchor(f, hz, ss.anchor, t60 + 2 * lead30) } else { p };
    Some((p.x, p.z, ss.value as i64))
}

/// `SkillPlan` (simplified, see bots/mod.rs).
#[inline(never)] // keeps bot_input under the 4 KB sBPF stack frame
pub(crate) fn skill(c: &Ctx, b: &mut BotState) -> Plan {
    let hz = c.sc.hz(c.w);
    if hz.count(HazardType::StarPickup) == 0 {
        return Plan::default();
    }
    let f = hz.star_field(0);
    let s = c.s;
    let t30 = hazards::t30(c.t60(0));
    let range = c.sk.star_pick_range_mm;
    let mut best: i32 = -1;
    let mut best_score = i64::MIN;
    let (mut bx, mut bz) = (0, 0);
    let consider = |id: i32, x: i32, z: i32, value: i64, best: &mut i32, best_score: &mut i64, bx: &mut i32, bz: &mut i32| {
        let (dx, dz) = ((x - s.pos.x) as i64, (z - s.pos.z) as i64);
        let d2 = dx * dx + dz * dz;
        let dist = isqrt_i64(d2);
        if dist > range {
            return;
        }
        let mut score = value * 4_000_000 / (dist + 3000) + if id == b.target as i32 { 150 } else { 0 };
        let mut o = 0;
        while o < MAX_PLAYERS {
            if let Some(m) = c.other(o) {
                let (ox, oz) = ((x - m.pos.x) as i64, (z - m.pos.z) as i64);
                if ox * ox + oz * oz < d2 {
                    score -= 250;
                }
            }
            o += 1;
        }
        if score > *best_score {
            *best_score = score;
            *best = id;
            *bx = x;
            *bz = z;
        }
    };
    if t30 >= 0 && f.wave_every_ticks > 0 && f.wave_size > 0 {
        let w1 = t30 / f.wave_every_ticks as i64;
        let w0 = ((t30 - f.wave_life_ticks as i64) / f.wave_every_ticks as i64).max(0).max(w1 - 16);
        let mut w = w0;
        while w <= w1 {
            let mut k = 0;
            while k < f.wave_size.min(16) {
                let id = (w * f.wave_size as i64 + k as i64) as i32;
                if id >= STAR_GOLDEN_BASE {
                    break;
                }
                if let Some((x, z, v)) = star_target(c, &hz, &f, id, t30) {
                    consider(id, x, z, v, &mut best, &mut best_score, &mut bx, &mut bz);
                }
                k += 1;
            }
            w += 1;
        }
        if f.golden_every_ticks > 0 {
            let gmax = (t30 / f.golden_every_ticks as i64).min(63);
            let mut g = 1;
            while g <= gmax {
                let id = STAR_GOLDEN_BASE + g as i32;
                if let Some((x, z, v)) = star_target(c, &hz, &f, id, t30) {
                    consider(id, x, z, v, &mut best, &mut best_score, &mut bx, &mut bz);
                }
                g += 1;
            }
        }
    }
    // Dropped stars (ring slots): grabbable after the pop; still popping ones count too (players run at them).
    let mut k = 0;
    while k < MAX_DROPS {
        let d = c.w.rx.drops[k];
        let age = c.tick as i64 - d.tick as i64;
        if d.tick != 0 && !d.taken && age >= 0 && age < c.t(f.drop_life_ticks) as i64 {
            let p = hazards::drop_pos(&f, d.pos, k as u32 + 1, f.drop_pop_ticks as i64);
            consider(STAR_DROP_BASE + k as i32, p.x, p.z, 1, &mut best, &mut best_score, &mut bx, &mut bz);
        }
        k += 1;
    }
    b.target = best as i16;
    let rr = isqrt_i64(s.pos.x as i64 * s.pos.x as i64 + s.pos.z as i64 * s.pos.z as i64);
    let mut cmd = if best < 0 {
        // Nothing worth it: head for the fountain ring (r 5 m on our side), where the bursts land.
        if rr < 1 {
            InputCmd::NEUTRAL
        } else {
            steer_to(&s, div_round(s.pos.x as i64 * 5000, rr) as i32, div_round(s.pos.z as i64 * 5000, rr) as i32, 60)
        }
    } else {
        steer_to(&s, bx, bz, 60)
    };
    if rr > 16_800 {
        cmd = steer_to(&s, 0, 0, 60); // stay off the rim
    }
    avoid_bumpers(c, &hz, &mut cmd);
    if s.has(flag::GROUNDED) {
        avoid_pads(c, &hz, &mut cmd);
    }
    bars(c, &mut cmd);
    let hop_every = (47 * c.cfg.rate / 30).max(1);
    if s.has(flag::GROUNDED) && c.tick % hop_every == c.slot as u32 % hop_every && rr < 14_000 {
        cmd.buttons |= JUMP_PRESSED | JUMP_HELD; // a hop now and then (players do)
    }
    Plan::of(cmd)
}

/// Re-normalise an accumulated stick (ax, az) to length 127, keeping the camera yaw.
fn set_stick(cmd: &mut InputCmd, ax: i64, az: i64) {
    let l = isqrt_i64(ax * ax + az * az);
    if l < 1 {
        return;
    }
    cmd.move_x = div_round(ax * 127, l) as i8;
    cmd.move_y = div_round(az * 127, l) as i8;
}

/// `AvoidBumpers`: detour round a bumper whose disc (+1.2 m) is on the way ahead; push away within 2.2 m.
fn avoid_bumpers(c: &Ctx, hz: &Hazards, cmd: &mut InputCmd) {
    let (mut ax, mut az) = (cmd.move_x as i64 * 16, cmd.move_y as i64 * 16);
    let ml = isqrt_i64(ax * ax + az * az);
    let n = hz.count(HazardType::Bumper);
    let s = &c.s;
    let mut i = 0;
    while i < n {
        let d = hz.bumper(i);
        let (bx, bz) = ((d.center_x - s.pos.x) as i64, (d.center_z - s.pos.z) as i64);
        let dist = isqrt_i64(bx * bx + bz * bz);
        let r = d.radius as i64;
        if ml > 16 && dist < 6000 {
            let u = normalize_q15(ax, 0, az);
            let (ux, uz) = (u[0] as i64, u[2] as i64);
            let along = div_round(bx * ux + bz * uz, ONE_Q15 as i64);
            let side = div_round(bx * uz - bz * ux, ONE_Q15 as i64);
            let rr = r + 1200;
            if along > 0 && side.abs() < rr {
                let push = (rr - side.abs()) * 160 * 16 / rr * if side >= 0 { -1 } else { 1 };
                ax += div_round(uz * push, ONE_Q15 as i64);
                az += div_round(-ux * push, ONE_Q15 as i64);
            }
        }
        let reach = r + 2200;
        if dist < reach && dist > 1 {
            let w = (reach - dist) * 260 * 16 / reach;
            ax -= div_round(bx * w, dist);
            az -= div_round(bz * w, dist);
        }
        i += 1;
    }
    set_stick(cmd, ax, az);
}

/// `AvoidPads`: detour round a spring pad (+0.9 m) on the way ahead.
fn avoid_pads(c: &Ctx, hz: &Hazards, cmd: &mut InputCmd) {
    let (mut ax, mut az) = (cmd.move_x as i64 * 16, cmd.move_y as i64 * 16);
    if ax == 0 && az == 0 {
        return;
    }
    let u = normalize_q15(ax, 0, az);
    let (ux, uz) = (u[0] as i64, u[2] as i64);
    let n = hz.count(HazardType::SpringPad);
    let s = &c.s;
    let mut i = 0;
    while i < n {
        let p = hz.pad(i);
        let (bx, bz) = ((p.center_x - s.pos.x) as i64, (p.center_z - s.pos.z) as i64);
        let dist = isqrt_i64(bx * bx + bz * bz);
        let r = p.half_x.max(p.half_z) as i64 * 14 / 10 + 900;
        if dist <= 5000 && dist > 1 {
            let along = div_round(bx * ux + bz * uz, ONE_Q15 as i64);
            let side = div_round(bx * uz - bz * ux, ONE_Q15 as i64);
            if along > -300 && side.abs() < r {
                let push = (r - side.abs()) * 200 * 16 / r * if side >= 0 { -1 } else { 1 };
                ax += div_round(uz * push, ONE_Q15 as i64);
                az += div_round(-ux * push, ONE_Q15 as i64);
            }
        }
        i += 1;
    }
    set_stick(cmd, ax, az);
}

/// `FinalPlan`: Lid Flip grids, else crumble shelves.
#[inline(never)] // keeps bot_input under the 4 KB sBPF stack frame
pub(crate) fn final_plan(c: &Ctx, b: &mut BotState) -> Plan {
    let hz = c.sc.hz(c.w);
    if hz.count(HazardType::LidFlip) > 0 {
        return Plan::of(lid_plan(c, b, &hz));
    }
    if hz.count(HazardType::CrumbleTile) > 0 {
        return Plan::of(crumble_plan(c, b, &hz));
    }
    Plan::default()
}

// ------------------------------------------------------------------ crumble shelves

fn touched(c: &Ctx, d: &crate::hazards::defs::TileGridDef, i: usize) -> bool {
    if i >= MAX_RX_TILES {
        return false;
    }
    let ct = c.w.rx.tile_contact[i];
    if ct == 0 || c.tick < ct {
        return false;
    }
    let back = d.respawn_for(i as i32) as u32;
    !(back != 0 && c.tick >= ct + d.wobble_ticks as u32 + back)
}

fn tile_center(d: &crate::hazards::defs::TileGridDef, id: i32) -> (i32, i32) {
    let k = id % d.per_shelf().max(1);
    (d.origin_x + (k % d.cols) * d.tile + d.tile / 2, d.origin_z + (k / d.cols) * d.tile + d.tile / 2)
}

fn crumble_plan(c: &Ctx, b: &mut BotState, hz: &Hazards) -> InputCmd {
    let g = hz.tile_grid(0);
    let s = c.s;
    let shelves = g.shelf_count.max(1);
    let mut shelf = shelves - 1;
    let mut i = 0;
    while i < shelves {
        if s.pos.y >= g.shelf_tops[i as usize] - 300 {
            shelf = i;
            break;
        }
        i += 1;
    }
    let per = g.per_shelf().max(1);
    let (lo_id, hi_id) = (shelf * per, ((shelf + 1) * per).min(MAX_RX_TILES as i32));
    let mut intact = 0;
    let mut i = lo_id;
    while i < hi_id {
        if !touched(c, &g, i as usize) {
            intact += 1;
        }
        i += 1;
    }
    let need_new = b.target < 0 || b.target as i32 / per != shelf || touched(c, &g, b.target as usize) || {
        let (cx, cz) = tile_center(&g, b.target as i32);
        let (dx, dz) = ((cx - s.pos.x) as i64, (cz - s.pos.z) as i64);
        dx * dx + dz * dz < 500 * 500
    };
    if need_new {
        let drop_down = intact < 6 && shelf < shelves - 1;
        let mut best = -1i32;
        let mut best_score = i64::MAX;
        let mut i = lo_id;
        while i < hi_id {
            let t = touched(c, &g, i as usize);
            if drop_down != t {
                i += 1;
                continue;
            }
            let (x, z) = tile_center(&g, i);
            let (dx, dz) = ((x - s.pos.x) as i64, (z - s.pos.z) as i64);
            let mut d = isqrt_i64(dx * dx + dz * dz);
            if d < 400 && !drop_down {
                d += 2500;
            } else if !drop_down && d > 2400 {
                d += (d - 2400) * 2;
            }
            let mut crowd = 0i64;
            let mut o = 0;
            while o < MAX_PLAYERS {
                if let Some(m) = c.other(o) {
                    let (ox, oz) = ((x - m.pos.x) as i64, (z - m.pos.z) as i64);
                    if ox.abs() < 3000 && oz.abs() < 3000 {
                        let od = isqrt_i64(ox * ox + oz * oz);
                        if od < 3000 {
                            crowd += (3000 - od) * 8 / 10;
                        }
                    }
                }
                o += 1;
            }
            let score = d + crowd + (b.rand() % 600) as i64;
            if score < best_score {
                best_score = score;
                best = i;
            }
            i += 1;
        }
        b.target = best as i16;
    }
    if b.target < 0 {
        return InputCmd::NEUTRAL;
    }
    let (tx, tz) = tile_center(&g, b.target as i32);
    let mut cmd = steer_to(&s, tx, tz, 60);
    bars(c, &mut cmd);
    cmd
}

// ------------------------------------------------------------------ lid flip (G4.5)

#[inline]
fn lid_safe(st: LidState) -> bool {
    st == LidState::Idle
}

/// The highest lid grid whose top is at or below the feet + 0.3 m, or −1.
fn lid_grid_under(c: &Ctx, hz: &Hazards) -> i32 {
    let n = hz.count(HazardType::LidFlip).min(4);
    let mut best = -1i32;
    let mut best_top = i32::MIN;
    let mut g = 0;
    while g < n {
        let d = hz.lid_grid(g);
        if c.s.pos.y >= d.top - 300 && d.top > best_top {
            best = g as i32;
            best_top = d.top;
        }
        g += 1;
    }
    best
}

/// `LidPlan`. State reuse (Final only): `section` = grid, `wp` = noticed lid (255 none), `aux_tick` = re-score tick,
/// `progress_tick` = ignore-until (with `bf::LID_IGNORE`).
fn lid_plan(c: &Ctx, b: &mut BotState, hz: &Hazards) -> InputCmd {
    let gi = lid_grid_under(c, hz);
    if gi < 0 {
        return InputCmd::NEUTRAL;
    }
    let d = hz.lid_grid(gi as usize);
    let count = (d.count() as usize).min(MAX_LIDS_PER_GRID);
    let s = c.s;
    let t30 = hazards::t30(c.t60(0));
    let mut seen = [LidState::Idle; MAX_LIDS_PER_GRID];
    c.sc.lid_states(gi as usize, &d, (t30 - c.sk.lid_lag as i64).max(0), &mut seen);
    let grounded = s.has(flag::GROUNDED);
    let under = if grounded { d.lid_under(s.pos.x, s.pos.y, s.pos.z) } else { -1 };
    let under_lit = under >= 0 && !lid_safe(seen[under as usize]);
    if under_lit && under != b.wp as i32 {
        b.wp = under as u8;
        b.set(bf::LID_IGNORE, false);
        // A bad moment: the lid lighting under us is noticed 14–26 ticks (30 Hz) late; more once only the 3 × 3s are left.
        let g0 = hz.lid_grid(0);
        let late = g0.ring_retire_count > 0 && t30 >= g0.ring_retire_ticks[(g0.ring_retire_count - 1) as usize] as i64;
        let pct = c.sk.lid_misjudge_pct + if late && !c.reference { 20 } else { 0 };
        if b.rand() % 100 < pct {
            b.set(bf::LID_IGNORE, true);
            b.progress_tick = lo(c.tick + c.t(14 + (b.rand() % 13) as i32) as u32);
        }
    } else if !under_lit {
        b.wp = 255;
    }
    if under_lit && b.has(bf::LID_IGNORE) && before(c.tick, b.progress_tick) {
        let mut stay = InputCmd::NEUTRAL;
        bars(c, &mut stay);
        return stay;
    }
    let panic = under_lit && b.has(bf::LID_IGNORE);
    if panic {
        b.set(bf::LID_IGNORE, false);
    }
    let ss = hz.sweeper_start();
    let sweeping = hz.count(HazardType::SpinnerBar) > 0 && (ss == i32::MIN || t30 + 30 >= ss as i64);
    let target_bad = b.target < 0 || b.section as i32 != gi || b.target as usize >= count || !lid_safe(seen[b.target as usize]);
    if target_bad || under_lit || !before(c.tick, b.aux_tick) {
        let prev = if b.section as i32 == gi { b.target as i32 } else { -1 };
        b.target = pick_lid(c, b, &d, &seen, prev, sweeping, under) as i16;
        b.section = gi as u8;
        b.aux_tick = lo(c.tick + c.t(9) as u32);
    }
    if b.target < 0 {
        let mut hold = steer_to(&s, d.origin_x, d.origin_z, 60);
        if grounded && under >= 0 && lid_safe(seen[under as usize]) {
            hold.move_x = 0;
            hold.move_y = 0;
        }
        bars(c, &mut hold);
        return hold;
    }
    let (tx, _, tz) = d.lid_center(b.target as i32);
    let mut cmd = steer_to(&s, tx, tz, 60);
    let (dx, dz) = ((tx - s.pos.x) as i64, (tz - s.pos.z) as i64);
    let dist = isqrt_i64(dx * dx + dz * dz);
    if panic && grounded {
        if b.rand() % 2 == 0 {
            cmd.move_x = 0;
            cmd.move_y = 0;
        }
        cmd.buttons |= JUMP_PRESSED | JUMP_HELD;
    } else if dist < 220 && grounded {
        cmd.move_x = 0;
        cmd.move_y = 0;
    } else if grounded && dist > 900 {
        // Hop an open lid between us and the target: look 0.8 m ahead.
        let ax = s.pos.x + div_round(dx * 800, dist) as i32;
        let az = s.pos.z + div_round(dz * 800, dist) as i32;
        let ahead = d.lid_under(ax, d.top, az);
        if ahead >= 0 && ahead != under && !lid::lid_solid(seen[ahead as usize]) {
            cmd.buttons |= JUMP_PRESSED | JUMP_HELD;
        }
    }
    bars(c, &mut cmd);
    cmd
}

/// `PickLid`: score the safe-looking lids (distance, ring, crowd, sweeper hub, stay bonus), then re-score the best
/// five with the way there (open / yellow lids crossed every 0.6 m).
fn pick_lid(c: &Ctx, b: &mut BotState, d: &crate::hazards::defs::LidGridDef, seen: &[LidState; MAX_LIDS_PER_GRID], current: i32, sweeping: bool, under: i32) -> i32 {
    let s = c.s;
    let count = (d.count() as usize).min(MAX_LIDS_PER_GRID);
    let mut top = [(-1i32, i64::MAX); 5];
    let mut i = 0;
    while i < count {
        if !lid_safe(seen[i]) {
            i += 1;
            continue;
        }
        let (x, _, z) = d.lid_center(i as i32);
        let (dx, dz) = ((x - s.pos.x) as i64, (z - s.pos.z) as i64);
        let ring = d.ring(i as i32) as i64;
        let mut score = isqrt_i64(dx * dx + dz * dz) + ring * 650;
        if ring >= 2 && ring == d.mid() as i64 {
            score += 900;
        }
        let ii = i as i32;
        if sweeping && ring == 0 {
            score += 2600;
        } else if sweeping && ring == 1 && (ii % d.n == d.mid() || ii / d.n == d.mid()) {
            score += 700;
        }
        if ii == current {
            score -= 700;
        }
        if ii == under {
            score -= 400;
        }
        let mut o = 0;
        while o < MAX_PLAYERS {
            if let Some(m) = c.other(o) {
                if (m.pos.y - d.top).abs() <= 1500 {
                    let (ox, oz) = ((x - m.pos.x) as i64, (z - m.pos.z) as i64);
                    // cheap exact reject before the square root (od < 2200 needs |ox|, |oz| < 2200)
                    if ox.abs() < 2200 && oz.abs() < 2200 {
                        let od = isqrt_i64(ox * ox + oz * oz);
                        if od < 2200 {
                            score += (2200 - od) * 11 / 10;
                        }
                    }
                }
            }
            o += 1;
        }
        if !c.reference {
            score += (b.rand() % 500) as i64;
        }
        let mut j = 0;
        while j < 5 {
            if score < top[j].1 {
                let mut m = 4;
                while m > j {
                    top[m] = top[m - 1];
                    m -= 1;
                }
                top[j] = (ii, score);
                break;
            }
            j += 1;
        }
        i += 1;
    }
    let mut best = -1;
    let mut best_score = i64::MAX;
    for (id, base) in top {
        if id < 0 {
            continue;
        }
        let (x, _, z) = d.lid_center(id);
        let (dx, dz) = ((x - s.pos.x) as i64, (z - s.pos.z) as i64);
        let dist = isqrt_i64(dx * dx + dz * dz);
        let mut score = base;
        let steps = (dist / 600).min(40);
        let mut last = under;
        let mut q = 1;
        while q < steps {
            let px = s.pos.x + (dx * q / steps) as i32;
            let pz = s.pos.z + (dz * q / steps) as i32;
            let l = d.lid_under(px, d.top, pz);
            if l != last && l != id {
                last = l;
                if l < 0 {
                    score += 6000;
                } else {
                    let st = seen[l as usize];
                    if st == LidState::Lit {
                        score += 1800;
                    } else if !lid::lid_solid(st) {
                        score += 1200;
                    } else if st == LidState::RetireWarn {
                        score += 600;
                    }
                }
            }
            q += 1;
        }
        if score < best_score {
            best_score = score;
            best = id;
        }
    }
    best
}
