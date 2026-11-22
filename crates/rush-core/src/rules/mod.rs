//! Round rules (ports of `Shared/RaceRules.cs`, `SurvivalRules.cs`, `SkillRules.cs`, `FinalRules.cs`,
//! `TimedRoundRules.cs`, `RoundTargets.cs`, `ModifierEffects.cs`) and the match director (`MatchDirector.cs`).
//!
//! State lives in `World` ([`SlotRules`] per player, [`RoundState`] per round); the functions here are the
//! `ISimRules` hooks `world::step` calls, dispatched on the round mode (no trait objects, no allocation).
//! Durations authored in 30 Hz ticks (intro, countdown, timers) are converted at the configured rate.

pub mod director;
pub mod race;
pub mod targets;
pub mod timed;
#[cfg(test)]
mod tests;

use crate::codec::{Codec, Dec, Enc};
use crate::mapblob::MapView;
use crate::world::{StepEvents, World, MAX_PLAYERS};

/// `RoundPhase`.
pub mod phase {
    pub const WAITING: u8 = 0;
    pub const INTRO: u8 = 1;
    pub const COUNTDOWN: u8 = 2;
    pub const RUSH: u8 = 3;
    pub const ENDED: u8 = 4;
    pub const SANDBOX: u8 = 5;
}

/// `RaceEventType` (round events; the HUD's `evt`).
pub mod evt {
    pub const CHECKPOINT: u8 = 1;
    pub const FINISHED: u8 = 2;
    pub const QUALIFIED: u8 = 3;
    pub const ELIMINATED: u8 = 4;
    pub const RESPAWNED: u8 = 5;
    pub const ROUND_END: u8 = 6;
    pub const PHASE: u8 = 7;
    pub const OUT: u8 = 8;
    pub const SCORED: u8 = 9;
    pub const DROPPED: u8 = 10;
}

/// `ElimReason`.
pub mod reason {
    pub const NOT_QUALIFIED: u8 = 0;
    pub const GOO: u8 = 1;
    pub const FELL_OUT: u8 = 2;
    pub const LOWER_SCORE: u8 = 3;
    pub const TIMEOUT_RANK: u8 = 4;
    pub const DISCONNECT: u8 = 5;
}

/// [`SlotRules::flags`] bits.
pub mod sf {
    /// Takes part in the round (`Racer.In` / `Player.In`).
    pub const IN: u8 = 1 << 0;
    /// Knocked out (`Player.Out`).
    pub const OUT: u8 = 1 << 1;
    /// Left early with a result (race finished / survival escaped; `Done`).
    pub const DONE: u8 = 1 << 2;
    pub const QUALIFIED: u8 = 1 << 3;
    pub const ELIMINATED: u8 = 1 << 4;
}

/// Per-player round state: the union of `RaceRules.Racer` and `TimedRoundRules.Player`.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct SlotRules {
    pub flags: u8,
    /// Race: checkpoints reached.
    pub checkpoint: u8,
    pub checkpoint_tick: u32,
    /// Race finish / survival escape tick (`FinishTick` / `DoneTick`).
    pub done_tick: u32,
    /// Race finish place (1-based), 0 = not finished.
    pub place: u8,
    pub out_tick: u32,
    pub out_reason: u8,
    /// Skill: stars. Survival: unused. Final: unused.
    pub score: i32,
    pub score_tick: u32,
    /// Survival: max height (mm).
    pub height: i32,
    /// Final: deepest shelf reached; distinct tiles; lid shelf / falls / last drop tick.
    pub deepest: u8,
    pub tiles: u16,
    /// Final (crumble): tiles stood on (bit per tile, ≤ `world::MAX_RX_TILES`).
    pub touched: u32,
    pub lid_shelf: u8,
    pub lid_falls: u8,
    pub lid_drop_tick: u32,
    /// Race: reachability rejections (anti-cheat statistic).
    pub rejected: u16,
}

impl SlotRules {
    #[inline]
    pub const fn has(&self, f: u8) -> bool {
        self.flags & f != 0
    }
    #[inline]
    pub fn set(&mut self, f: u8, on: bool) {
        if on {
            self.flags |= f;
        } else {
            self.flags &= !f;
        }
    }
}

impl Codec for SlotRules {
    fn enc(&self, e: &mut Enc) {
        e.u8(self.flags);
        e.u8(self.checkpoint);
        e.u32(self.checkpoint_tick);
        e.u32(self.done_tick);
        e.u8(self.place);
        e.u32(self.out_tick);
        e.u8(self.out_reason);
        e.i32(self.score);
        e.u32(self.score_tick);
        e.i32(self.height);
        e.u8(self.deepest);
        e.u16(self.tiles);
        e.u32(self.touched);
        e.u8(self.lid_shelf);
        e.u8(self.lid_falls);
        e.u32(self.lid_drop_tick);
        e.u16(self.rejected);
    }
    fn dec(d: &mut Dec) -> Self {
        SlotRules {
            flags: d.u8(),
            checkpoint: d.u8(),
            checkpoint_tick: d.u32(),
            done_tick: d.u32(),
            place: d.u8(),
            out_tick: d.u32(),
            out_reason: d.u8(),
            score: d.i32(),
            score_tick: d.u32(),
            height: d.i32(),
            deepest: d.u8(),
            tiles: d.u16(),
            touched: d.u32(),
            lid_shelf: d.u8(),
            lid_falls: d.u8(),
            lid_drop_tick: d.u32(),
            rejected: d.u16(),
        }
    }
}

/// A round's result (`RoundResult`): order (slots, place 1 first), qualified flags, reasons, values.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct RoundResult {
    pub n: u8,
    pub order: [u8; MAX_PLAYERS],
    pub qualified: u16,
    pub reasons: [u8; MAX_PLAYERS],
    pub values: [i32; MAX_PLAYERS],
    pub target: u8,
    pub start_tick: u32,
    pub end_tick: u32,
    pub timer_expired: bool,
}

impl RoundResult {
    pub fn qualified_at(&self, k: usize) -> bool {
        (self.qualified >> k) & 1 == 1
    }
    pub fn qualified_count(&self) -> u8 {
        (self.qualified & ((1u32 << self.n) - 1) as u16).count_ones() as u8
    }
}

impl Codec for RoundResult {
    fn enc(&self, e: &mut Enc) {
        e.u8(self.n);
        for v in self.order {
            e.u8(v);
        }
        e.u16(self.qualified);
        for v in self.reasons {
            e.u8(v);
        }
        for v in self.values {
            e.i32(v);
        }
        e.u8(self.target);
        e.u32(self.start_tick);
        e.u32(self.end_tick);
        e.bool(self.timer_expired);
    }
    fn dec(d: &mut Dec) -> Self {
        let mut r = RoundResult { n: d.u8(), ..Default::default() };
        for v in r.order.iter_mut() {
            *v = d.u8();
        }
        r.qualified = d.u16();
        for v in r.reasons.iter_mut() {
            *v = d.u8();
        }
        for v in r.values.iter_mut() {
            *v = d.i32();
        }
        r.target = d.u8();
        r.start_tick = d.u32();
        r.end_tick = d.u32();
        r.timer_expired = d.bool();
        r
    }
}

/// Per-round state (`RaceRules` / `TimedRoundRules` fields).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct RoundState {
    /// `mapblob::mode` of the round (0 sandbox: free play, no rules).
    pub mode: u8,
    pub phase: u8,
    pub phase_start: u32,
    pub rush_tick: u32,
    /// 0 = no timer.
    pub rush_end: u32,
    pub intro_ticks: u16,
    pub countdown_ticks: u16,
    /// Requested qualify count (0 = the mode default).
    pub target: u8,
    pub qualify_count: u8,
    pub qualified_count: u8,
    pub finished_count: u8,
    pub timer_expired: bool,
    pub end_pending: bool,
    /// Race: respawn round-robin cursor per checkpoint (≤ 15 checkpoints).
    pub respawn_cursor: [u8; 16],
    /// Race finishers in order.
    pub finish_order: [u8; MAX_PLAYERS],
    pub result: RoundResult,
    pub has_result: bool,
    /// Round event sequence (`EventSeq`).
    pub event_seq: u32,
}

impl Codec for RoundState {
    fn enc(&self, e: &mut Enc) {
        e.u8(self.mode);
        e.u8(self.phase);
        e.u32(self.phase_start);
        e.u32(self.rush_tick);
        e.u32(self.rush_end);
        e.u16(self.intro_ticks);
        e.u16(self.countdown_ticks);
        e.u8(self.target);
        e.u8(self.qualify_count);
        e.u8(self.qualified_count);
        e.u8(self.finished_count);
        e.bool(self.timer_expired);
        e.bool(self.end_pending);
        for v in self.respawn_cursor {
            e.u8(v);
        }
        for v in self.finish_order {
            e.u8(v);
        }
        self.result.enc(e);
        e.bool(self.has_result);
        e.u32(self.event_seq);
    }
    fn dec(d: &mut Dec) -> Self {
        let mut s = RoundState {
            mode: d.u8(),
            phase: d.u8(),
            phase_start: d.u32(),
            rush_tick: d.u32(),
            rush_end: d.u32(),
            intro_ticks: d.u16(),
            countdown_ticks: d.u16(),
            target: d.u8(),
            qualify_count: d.u8(),
            qualified_count: d.u8(),
            finished_count: d.u8(),
            timer_expired: d.bool(),
            end_pending: d.bool(),
            ..Default::default()
        };
        for v in s.respawn_cursor.iter_mut() {
            *v = d.u8();
        }
        for v in s.finish_order.iter_mut() {
            *v = d.u8();
        }
        s.result = RoundResult::dec(d);
        s.has_result = d.bool();
        s.event_seq = d.u32();
        s
    }
}

// ------------------------------------------------------------------ helpers

/// Emits a round event.
#[inline]
pub fn emit(w: &mut World, ev: &mut StepEvents, tag: u8, tick: u32, slot: u8, arg: i32) {
    crate::world::emit_round(w, ev, tag, tick, slot, arg);
}

/// `SetPhase`.
pub fn set_phase(w: &mut World, p: u8, tick: u32, ev: &mut StepEvents) {
    w.round.phase = p;
    w.round.phase_start = tick;
    emit(w, ev, evt::PHASE, tick, 255, p as i32);
}

/// Stable insertion sort of slots by `before(a, b)` (a ranks first). ≤ 12 entries.
pub fn sort_slots(v: &mut [u8], before: impl Fn(u8, u8) -> bool) {
    let mut i = 1;
    while i < v.len() {
        let x = v[i];
        let mut j = i;
        while j > 0 && before(x, v[j - 1]) {
            v[j] = v[j - 1];
            j -= 1;
        }
        v[j] = x;
        i += 1;
    }
}

/// 30 Hz ticks (C# authored durations) → ticks at `rate`, round half up (= `MotorConfig::ticks30`).
pub fn t30_to_ticks(t30: i64, rate: u8) -> i64 {
    if t30 <= 0 {
        t30
    } else {
        (t30 * rate as i64 * 2 + 30) / 60
    }
}

/// C# defaults (30 Hz ticks): `MatchDirector.RoundIntroTicks` / `RoundCountdownTicks`.
pub const ROUND_INTRO_30: i64 = 60;
pub const ROUND_COUNTDOWN_30: i64 = 90;

/// `IRoundRules.Begin` (+ the director's `BeginRound` set-up): starts the round on `map` now (the intro starts at
/// `tick + 1`, `tick` = the last simulated tick). `target` = qualify count (0 = the mode default), `participants` =
/// slot mask (bit set = races; players eliminated earlier are left out). Intro / countdown in 30 Hz ticks
/// (`ROUND_INTRO_30`, `ROUND_COUNTDOWN_30` are the director's), converted at `w.rate`. Clears the reactive state.
/// Returns the RUSH tick (= hazard time 0, also written to `w.round_start`).
#[allow(clippy::too_many_arguments)]
pub fn begin_round(w: &mut World, map: &MapView, tick: u32, target: u8, participants: u16, intro30: i64, countdown30: i64, ev: &mut StepEvents) -> u32 {
    let mode = map.mode() as u8;
    let ev_seq = w.round.event_seq;
    w.round = RoundState { mode, event_seq: ev_seq, ..Default::default() };
    w.rx = crate::world::Reactive::default();
    if mode == crate::mapblob::mode::SANDBOX || mode == crate::mapblob::mode::LOBBY {
        w.round.phase = phase::SANDBOX;
        return tick;
    }
    let mut players = 0i32;
    for i in 0..MAX_PLAYERS {
        w.players[i].r = SlotRules::default();
        if w.players[i].active() && (participants >> i) & 1 == 1 {
            w.players[i].r.set(sf::IN, true);
            players += 1;
        }
    }
    w.round.qualify_count = if mode == crate::mapblob::mode::RACE {
        players.min(if target > 0 { target as i32 } else { targets::race_qualify_count(players) })
    } else {
        players.min(if target > 0 { target as i32 } else { players }).max(1)
    } as u8;
    w.round.target = target;
    let intro = t30_to_ticks(intro30, w.rate) as u32;
    let countdown = t30_to_ticks(countdown30, w.rate) as u32;
    w.round.intro_ticks = intro as u16;
    w.round.countdown_ticks = countdown as u16;
    let rush = tick + 1 + intro + countdown;
    w.round.rush_tick = rush;
    let timer = map.meta(crate::mapblob::meta::TIMER_CAP_S).max(0) as u32 * w.rate as u32;
    w.round.rush_end = if timer > 0 { rush + timer } else { 0 };
    w.round_start = rush;
    for i in 0..MAX_PLAYERS {
        if w.players[i].r.has(sf::IN) {
            w.players[i].m = w.spawn_state(map, i);
            w.players[i].respawn_ticks = 0;
        }
    }
    set_phase(w, if intro > 0 { phase::INTRO } else { phase::COUNTDOWN }, tick + 1, ev);
    rush
}

// ------------------------------------------------------------------ hooks called by world::step (ISimRules)

/// Free play (sandbox / lobby) when no round runs.
#[inline]
fn free(w: &World) -> bool {
    w.round.mode == crate::mapblob::mode::SANDBOX || w.round.mode == crate::mapblob::mode::LOBBY || w.round.phase == phase::WAITING || w.round.phase == phase::SANDBOX
}

#[inline]
fn race(w: &World) -> bool {
    w.round.mode == crate::mapblob::mode::RACE
}

/// Before any player of `tick` is stepped (phase changes).
pub fn begin_tick(w: &mut World, _map: &MapView, tick: u32, ev: &mut StepEvents) {
    if free(w) {
        return;
    }
    match w.round.phase {
        phase::INTRO if tick >= w.round.phase_start + w.round.intro_ticks as u32 => set_phase(w, phase::COUNTDOWN, tick, ev),
        phase::COUNTDOWN if tick >= w.round.rush_tick => set_phase(w, phase::RUSH, tick, ev),
        _ => {}
    }
    if race(w) && w.round.phase != phase::ENDED {
        // A player who left after the intro is out of the round.
        for i in 0..MAX_PLAYERS {
            if !w.players[i].active() && w.players[i].r.has(sf::IN) {
                w.players[i].r.set(sf::IN, false);
            }
        }
    }
}

/// False during the intro / countdown, after the end, and for players out of the round.
pub fn accepts_input(w: &World, slot: usize) -> bool {
    if free(w) {
        return true;
    }
    if race(w) {
        let p = w.round.phase;
        if p == phase::INTRO || p == phase::COUNTDOWN || p == phase::ENDED {
            return false;
        }
        return !w.players[slot].r.has(sf::DONE);
    }
    w.round.phase == phase::RUSH && timed::in_play(w, slot)
}

/// False for players out of the simulation (finished, knocked out, not in the round).
pub fn simulates(w: &World, slot: usize) -> bool {
    if free(w) {
        return true;
    }
    let r = &w.players[slot].r;
    r.has(sf::IN) && !r.has(sf::OUT) && !r.has(sf::DONE)
}

/// False for players out of player–player collisions.
pub fn collides(w: &World, slot: usize) -> bool {
    simulates(w, slot)
}

/// Respawn after a fall: true = placed by the rules (race: last checkpoint); false = back to the spawn point.
pub fn respawn(w: &mut World, map: &MapView, slot: usize, ev: &mut StepEvents) -> bool {
    if free(w) || !race(w) {
        return false;
    }
    race::respawn(w, map, slot, ev)
}

/// After the players and the separation of `tick` (checkpoints, scores, round end).
pub fn end_tick(w: &mut World, map: &MapView, _sc: &crate::collide::Scene, tick: u32, ev: &mut StepEvents) {
    if free(w) || w.round.phase != phase::RUSH {
        return;
    }
    if race(w) {
        race::end_tick(w, map, tick, ev);
    } else {
        timed::end_tick(w, map, tick, ev);
    }
}

/// The round is over (`IsOver`).
pub fn is_over(w: &World) -> bool {
    w.round.phase == phase::ENDED
}
