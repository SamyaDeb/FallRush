//! Round rules (ports of `Shared/RaceRules.cs`, `SurvivalRules.cs`, `SkillRules.cs`, `FinalRules.cs`,
//! `TimedRoundRules.cs`, `RoundTargets.cs`, `ModifierEffects.cs`) and the match director (`MatchDirector.cs`).
//!
//! State lives in `World` ([`SlotRules`] per player, [`RoundState`] per round); the functions here are the
//! `ISimRules` hooks `world::step` calls, dispatched on the round mode (no trait objects, no allocation).
//! Durations authored in 30 Hz ticks (intro, countdown, timers) are converted at the configured rate.

pub mod director;
pub mod targets;

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

// ------------------------------------------------------------------ hooks called by world::step (ISimRules)

/// Free play (sandbox / lobby) when no round runs.
#[inline]
fn free(w: &World) -> bool {
    w.round.mode == crate::mapblob::mode::SANDBOX || w.round.mode == crate::mapblob::mode::LOBBY || w.round.phase == phase::WAITING || w.round.phase == phase::SANDBOX
}

/// Before any player of `tick` is stepped (phase changes).
pub fn begin_tick(w: &mut World, _map: &MapView, tick: u32, ev: &mut StepEvents) {
    if free(w) {
        return;
    }
    let _ = (tick, ev);
}

/// False during the countdown and for players out of the round: the command is replaced by a neutral one.
pub fn accepts_input(w: &World, _slot: usize) -> bool {
    if free(w) {
        return true;
    }
    w.round.phase == phase::RUSH
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

/// Respawn after a fall: true = placed by the rules; false = back to the spawn point.
pub fn respawn(_w: &mut World, _map: &MapView, _slot: usize, _ev: &mut StepEvents) -> bool {
    false
}

/// After the players and the separation of `tick` (checkpoints, scores, round end).
pub fn end_tick(w: &mut World, _map: &MapView, _sc: &crate::collide::Scene, tick: u32, ev: &mut StepEvents) {
    if free(w) {
        return;
    }
    let _ = (tick, ev);
}
