//! Match director (port of `Shared/MatchDirector.cs`, the match flow): RosterLocked → Dealing → Dealt → per round
//! RoundIntro (map load) → RoundActive → RoundResult → Intermission → Champion → Finalizing → Done. Tick counts are
//! the C# 30 Hz constants converted at the world rate. Transcript events (tags 1–7) go into `StepEvents`; the
//! rolling transcript hash (`transcript.rs`) is kept here.
//!
//! **Runner contract** (the program in P2, `rush-sim` off-chain). Maps change per round and the director cannot load
//! one, so the caller supplies it:
//! 1. `start_match(w, lobby_map, seed, size, roster_hash, h)` locks the roster (bots fill slots `< size`).
//! 2. every tick: `if let Some(id) = needs_map(w) { load blob id; load_round(w, &view, h) }`, then
//!    `world::step(w, inputs, &current_map, …)` — `step` calls [`before_tick`] / [`after_tick`].
//! 3. the match is over at `w.dir.phase == DONE` (or `ABORTED`); [`back_to_lobby`] resets it.
//!
//! **Not ported** (server connection handling, not rules): the lobby countdown with humans, host start, ready flags
//! and the 10 s load timeout (`load_round` = everyone ready), the 20 s disconnect timer (the runner sets the
//! `grace_expired` / `late` bits; their effects at round end — M4-6 DISCONNECT, M4-3 late loaders — are ported),
//! names, cosmetic presets, the no-humans abort.

// Fixed slot loops index several parallel arrays (fixed iteration order, P1 §1.2).
#![allow(clippy::needless_range_loop, clippy::manual_range_contains)]

use crate::codec::{Codec, Dec, Enc};
use crate::deal::{self, DealResult};
use crate::mapblob::MapView;
use crate::rules::{self, phase as rphase, reason, RoundResult};
use crate::transcript as tr;
use crate::world::{ek, Event, Hasher, StepEvents, World, MAX_PLAYERS};

/// `MatchPhase`.
pub mod phase {
    pub const LOBBY_OPEN: u8 = 0;
    pub const COUNTDOWN: u8 = 1;
    pub const ROSTER_LOCKED: u8 = 2;
    pub const DEALING: u8 = 3;
    pub const DEALT: u8 = 4;
    pub const ROUND_INTRO: u8 = 5;
    pub const ROUND_ACTIVE: u8 = 6;
    pub const ROUND_RESULT: u8 = 7;
    pub const INTERMISSION: u8 = 8;
    pub const CHAMPION: u8 = 9;
    pub const FINALIZING: u8 = 10;
    pub const DONE: u8 = 11;
    pub const ABORTED: u8 = 12;
}

/// `AbortReason`.
pub mod abort {
    pub const NO_HUMANS: u8 = 1;
    pub const NO_PLAYERS: u8 = 2;
    pub const SERVER_STOP: u8 = 3;
}

/// `MatchDirector` timings, 30 Hz ticks.
pub const DEALING_30: i64 = 90;
pub const DEALT_30: i64 = 90;
pub const RESULT_30: i64 = 180;
pub const INTERMISSION_30: i64 = 240;
pub const CHAMPION_30: i64 = 240;
pub const DONE_30: i64 = 600;
pub const ABORT_30: i64 = 150;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct DirectorState {
    pub phase: u8,
    pub phase_start: u32,
    /// 0 = open-ended.
    pub phase_end: u32,
    pub round: u8,
    pub round_count: u8,
    pub player_count: u8,
    pub seed: [u8; 32],
    pub transcript: [u8; 32],
    /// Transcript events so far (= the next seq).
    pub tseq: u32,
    pub deal: DealResult,
    pub targets: [u8; 4],
    /// Masks (bit = slot): in the roster, alive, bot, late this round, disconnect grace expired.
    pub roster: u16,
    pub alive: u16,
    pub bots: u16,
    pub late: u16,
    pub grace_expired: u16,
    pub protect_late: bool,
    pub round_begun: bool,
    /// The runner supplied this round's map (`load_round`).
    pub map_ready: bool,
    pub champion: u8,
    /// Per slot: elimination round (high nibble, 0 = not eliminated) and reason (low nibble).
    pub elim: [u8; MAX_PLAYERS],
    pub last_abort: u8,
}

impl Codec for DirectorState {
    fn enc(&self, e: &mut Enc) {
        e.u8(self.phase);
        e.u32(self.phase_start);
        e.u32(self.phase_end);
        e.u8(self.round);
        e.u8(self.round_count);
        e.u8(self.player_count);
        e.put(&self.seed);
        e.put(&self.transcript);
        e.u32(self.tseq);
        e.put(&self.deal.maps);
        e.put(&self.deal.variants);
        e.put(&self.deal.modifiers);
        e.put(&self.targets);
        for v in [self.roster, self.alive, self.bots, self.late, self.grace_expired] {
            e.u16(v);
        }
        e.bool(self.protect_late);
        e.bool(self.round_begun);
        e.bool(self.map_ready);
        e.u8(self.champion);
        e.put(&self.elim);
        e.u8(self.last_abort);
    }
    fn dec(d: &mut Dec) -> Self {
        DirectorState {
            phase: d.u8(),
            phase_start: d.u32(),
            phase_end: d.u32(),
            round: d.u8(),
            round_count: d.u8(),
            player_count: d.u8(),
            seed: d.take(),
            transcript: d.take(),
            tseq: d.u32(),
            deal: DealResult { maps: d.take(), variants: d.take(), modifiers: d.take() },
            targets: d.take(),
            roster: d.u16(),
            alive: d.u16(),
            bots: d.u16(),
            late: d.u16(),
            grace_expired: d.u16(),
            protect_late: d.bool(),
            round_begun: d.bool(),
            map_ready: d.bool(),
            champion: d.u8(),
            elim: d.take(),
            last_abort: d.u8(),
        }
    }
}

#[inline]
fn bit(m: u16, i: usize) -> bool {
    (m >> i) & 1 == 1
}

fn dur(w: &World, t30: i64) -> u32 {
    rules::t30_to_ticks(t30, w.rate) as u32
}

fn set_phase(w: &mut World, p: u8, now: u32, duration: u32) {
    w.dir.phase = p;
    w.dir.phase_start = now;
    w.dir.phase_end = if duration > 0 { now + duration } else { 0 };
}

/// `Emit`: a transcript event (seq = events so far) and the rolling hash.
#[allow(clippy::too_many_arguments)]
fn emit<H: Hasher>(w: &mut World, h: &mut H, ev: &mut StepEvents, tag: u8, round: u8, slot: u8, arg: &[u8], now: u32) {
    let seq = w.dir.tseq;
    w.dir.transcript = tr::next(h, &w.dir.transcript, tag, seq, round, slot, arg);
    w.dir.tseq += 1;
    w.event_seq = w.event_seq.wrapping_add(1);
    let a = if arg.len() == 1 { arg[0] as i32 } else { 0 };
    ev.push(Event { kind: ek::TRANSCRIPT, tag, slot, round, arg: a, tick: now, seq });
}

/// `LockRoster`: bots fill every free slot below `size` (4…12), bot tiers from the seed, round targets, the deal and
/// the transcript init. `roster_hash` comes from `transcript::roster_hash`. DEAL follows after the dealing phase.
pub fn start_match<H: Hasher>(w: &mut World, lobby: &MapView, seed: &[u8; 32], size: u8, roster_hash: &[u8; 32], h: &mut H) {
    let now = w.tick;
    let mut size = (size as usize).clamp(rules::targets::MIN_PLAYERS as usize, MAX_PLAYERS);
    for i in 0..MAX_PLAYERS {
        if w.players[i].active() && !w.players[i].is_bot() && i >= size {
            size = i + 1;
        }
    }
    let mut d = DirectorState { seed: *seed, player_count: size as u8, champion: 255, ..Default::default() };
    for i in 0..size {
        if !w.players[i].active() {
            w.add_player(lobby, i, true);
        }
        d.roster |= 1 << i;
        d.alive |= 1 << i;
        if w.players[i].is_bot() {
            d.bots |= 1 << i;
            w.players[i].bot.tier = deal::bot_tier(h, seed, i as u8);
        }
    }
    d.targets = rules::targets::round_targets(size as i32).unwrap_or([1, 1, 1, 1]);
    let mid = tr::match_id(h, seed);
    d.transcript = tr::init(h, &mid, roster_hash);
    d.deal = deal::derive(h, seed);
    d.round_count = deal::ROUNDS as u8;
    w.dir = d;
    set_phase(w, phase::ROSTER_LOCKED, now, 1);
}

/// The map the runner must load now (`Some(map_id)`), or `None`.
pub fn needs_map(w: &World) -> Option<u8> {
    if w.dir.phase == phase::ROUND_INTRO && !w.dir.map_ready && w.dir.round >= 1 {
        Some(w.dir.deal.maps[(w.dir.round - 1) as usize % 4])
    } else {
        None
    }
}

/// The runner loaded this round's map (`ParamsFor`): map key, variant, modifier, hazard seed. The round begins in
/// the next `before_tick` (everyone counts as ready; slots with a `late` bit spectate it, M4-3).
pub fn load_round<H: Hasher>(w: &mut World, map: &MapView, h: &mut H) {
    let r = w.dir.round.max(1);
    let i = (r - 1) as usize % 4;
    let id = w.dir.deal.maps[i];
    let deal_index = if (id as usize) < 4 { i } else { 0 };
    w.map_key = map.map_id();
    w.variant = w.dir.deal.variants[deal_index] % (deal::variant_count(id).max(1) as u8);
    w.modifier = w.dir.deal.modifiers[deal_index];
    w.hazard_seed = deal::hazard_seed(h, &w.dir.seed, r);
    w.round_index = r;
    w.dir.map_ready = true;
}

fn alive_count(w: &World) -> (u32, u8) {
    let mut n = 0;
    let mut last = 255;
    for i in 0..MAX_PLAYERS {
        if bit(w.dir.roster, i) && bit(w.dir.alive, i) {
            n += 1;
            last = i as u8;
        }
    }
    (n, last)
}

fn enter_round_intro(w: &mut World, r: u8, now: u32) {
    w.dir.round = r;
    w.dir.round_begun = false;
    w.dir.map_ready = false;
    w.dir.late = 0;
    w.round.phase = rphase::WAITING; // no rules until the round begins (C#: SetRules(null))
    set_phase(w, phase::ROUND_INTRO, now, 0);
}

fn begin_round<H: Hasher>(w: &mut World, map: &MapView, now: u32, h: &mut H, ev: &mut StepEvents) {
    let r = w.dir.round;
    let target = w.dir.targets[(r - 1) as usize % 4] as i32;
    let mut participants = 0u16;
    let (mut late, mut players) = (0i32, 0i32);
    for i in 0..MAX_PLAYERS {
        if !bit(w.dir.roster, i) || !bit(w.dir.alive, i) {
            continue;
        }
        if bit(w.dir.late, i) && !bit(w.dir.bots, i) {
            late += 1;
            continue;
        }
        participants |= 1 << i;
        players += 1;
    }
    w.dir.protect_late = late > 0 && target - late >= 1;
    let rules_target = if w.dir.protect_late { target - late } else { target };
    let t = rules_target.min(players.max(1)).max(1) as u8;
    rules::begin_round(w, map, now, t, participants, rules::ROUND_INTRO_30, rules::ROUND_COUNTDOWN_30, ev);
    w.dir.round_begun = true;
    emit(w, h, ev, tr::START_ROUND, r, tr::NO_SLOT, &[], now);
}

/// `EnterRoundResult`: late loaders and unranked players appended, M4-6 DISCONNECT, the target cap (M4-9
/// TIMEOUT_RANK), M4-8 minimum advance; then QUALIFIED (placement order), ELIMINATED, COMPLETE_ROUND.
pub fn enter_round_result<H: Hasher>(w: &mut World, now: u32, h: &mut H, ev: &mut StepEvents) {
    let r = w.dir.round;
    let res: RoundResult = if w.round.has_result { w.round.result } else { RoundResult::default() };
    let mut order = [0u8; MAX_PLAYERS];
    let mut q = [false; MAX_PLAYERS];
    let mut why = [0u8; MAX_PLAYERS];
    let mut n = 0usize;
    let mut seen = 0u16;
    for k in 0..(res.n as usize).min(MAX_PLAYERS) {
        order[n] = res.order[k];
        q[n] = res.qualified_at(k);
        why[n] = res.reasons[k];
        seen |= 1 << res.order[k];
        n += 1;
    }
    for pass in 0..2 {
        for i in 0..MAX_PLAYERS {
            if !bit(w.dir.roster, i) || !bit(w.dir.alive, i) || bit(seen, i) || n >= MAX_PLAYERS {
                continue;
            }
            if pass == 0 && !bit(w.dir.late, i) {
                continue; // late loaders first, then anyone the rules did not rank
            }
            order[n] = i as u8;
            q[n] = pass == 0 && w.dir.protect_late;
            why[n] = reason::NOT_QUALIFIED;
            seen |= 1 << i;
            n += 1;
        }
    }
    for k in 0..n {
        if bit(w.dir.grace_expired, order[k] as usize) {
            q[k] = false;
            why[k] = reason::DISCONNECT;
        }
    }
    let target = w.dir.targets[(r.max(1) - 1) as usize % 4];
    let mut count = 0u8;
    for k in 0..n {
        if !q[k] {
            continue;
        }
        if count >= target {
            q[k] = false;
            why[k] = reason::TIMEOUT_RANK;
            continue;
        }
        count += 1;
    }
    if count == 0 {
        for k in 0..n {
            if bit(w.dir.grace_expired, order[k] as usize) {
                continue;
            }
            q[k] = true;
            why[k] = reason::NOT_QUALIFIED;
            count = 1;
            break;
        }
    }
    let mut fin = RoundResult { n: n as u8, target, start_tick: res.start_tick, end_tick: res.end_tick, timer_expired: res.timer_expired, ..Default::default() };
    for k in 0..n {
        fin.order[k] = order[k];
        fin.reasons[k] = why[k];
        if q[k] {
            fin.qualified |= 1 << k;
        }
        fin.values[k] = (0..res.n as usize).find(|&j| res.order[j] == order[k]).map_or(0, |j| res.values[j]);
    }
    w.round.result = fin;
    w.round.has_result = true;
    if count == 0 {
        abort_match(w, abort::NO_PLAYERS, h, ev);
        return;
    }
    let mut placement = 0u8;
    for k in 0..n {
        if q[k] {
            placement += 1;
            emit(w, h, ev, tr::QUALIFIED, r, order[k], &[placement], now);
        }
    }
    for k in 0..n {
        if q[k] {
            continue;
        }
        let s = order[k] as usize;
        w.dir.alive &= !(1 << s);
        w.dir.elim[s] = r << 4 | (why[k] & 15);
        emit(w, h, ev, tr::ELIMINATED, r, order[k], &[why[k]], now);
    }
    emit(w, h, ev, tr::COMPLETE_ROUND, r, tr::NO_SLOT, &[count], now);
    let d = dur(w, RESULT_30);
    set_phase(w, phase::ROUND_RESULT, now, d);
}

fn first_qualified(w: &World) -> u8 {
    let r = &w.round.result;
    (0..r.n as usize).find(|&k| r.qualified_at(k)).map_or(255, |k| r.order[k])
}

fn enter_champion<H: Hasher>(w: &mut World, slot: u8, now: u32, h: &mut H, ev: &mut StepEvents) {
    w.dir.champion = slot;
    for i in 0..MAX_PLAYERS {
        if bit(w.dir.roster, i) && bit(w.dir.alive, i) && i as u8 != slot {
            w.dir.alive &= !(1 << i);
            w.dir.elim[i] = w.dir.round << 4 | reason::TIMEOUT_RANK;
        }
    }
    let r = w.dir.round;
    emit(w, h, ev, tr::COMPLETE_MATCH, r, slot, &[], now);
    let d = dur(w, CHAMPION_30);
    set_phase(w, phase::CHAMPION, now, d);
}

/// `Abort`: an ABORT event (if a match is running) and the aborted phase.
pub fn abort_match<H: Hasher>(w: &mut World, why: u8, h: &mut H, ev: &mut StepEvents) {
    let now = w.tick;
    w.dir.last_abort = why;
    if w.dir.phase >= phase::ROSTER_LOCKED && w.dir.phase != phase::ABORTED {
        let r = w.dir.round;
        emit(w, h, ev, tr::ABORT, r, tr::NO_SLOT, &[why], now);
    }
    let d = dur(w, ABORT_30);
    set_phase(w, phase::ABORTED, now, d);
}

/// `PreStep`, run by `world::step` before the players of `tick`. C# runs it before `SimCore.Tick++`, so the
/// director's "now" is `tick − 1` here.
pub fn before_tick<H: Hasher>(w: &mut World, map: &MapView, tick: u32, h: &mut H, ev: &mut StepEvents) {
    let now = tick.wrapping_sub(1);
    let end_due = w.dir.phase_end != 0 && now >= w.dir.phase_end;
    match w.dir.phase {
        phase::ROSTER_LOCKED => {
            let d = dur(w, DEALING_30);
            set_phase(w, phase::DEALING, now, d);
        }
        phase::DEALING if end_due => {
            let d = dur(w, DEALT_30);
            set_phase(w, phase::DEALT, now, d);
            let seed = w.dir.seed;
            emit(w, h, ev, tr::DEAL, 0, tr::NO_SLOT, &seed, now);
        }
        phase::DEALT if end_due => enter_round_intro(w, 1, now),
        phase::ROUND_INTRO => {
            if !w.dir.round_begun {
                if w.dir.map_ready {
                    begin_round(w, map, now, h, ev);
                }
            } else if w.round.phase == rphase::RUSH {
                let d = if w.round.rush_end > 0 { w.round.rush_end.saturating_sub(now) } else { 0 };
                set_phase(w, phase::ROUND_ACTIVE, now, d);
            } else if rules::is_over(w) {
                enter_round_result(w, now, h, ev);
            }
        }
        phase::ROUND_ACTIVE => {
            if rules::is_over(w) {
                enter_round_result(w, now, h, ev);
            }
        }
        phase::ROUND_RESULT if end_due => {
            let (alive, last) = alive_count(w);
            if alive <= 1 || w.dir.round >= w.dir.round_count {
                let c = if alive == 1 { last } else { first_qualified(w) };
                enter_champion(w, c, now, h, ev);
            } else {
                let d = dur(w, INTERMISSION_30);
                set_phase(w, phase::INTERMISSION, now, d);
            }
        }
        phase::INTERMISSION if end_due => {
            let r = w.dir.round + 1;
            enter_round_intro(w, r, now);
        }
        phase::CHAMPION if end_due => set_phase(w, phase::FINALIZING, now, 1),
        phase::FINALIZING if end_due => {
            let d = dur(w, DONE_30);
            set_phase(w, phase::DONE, now, d);
        }
        _ => {}
    }
}

/// `AfterStep` (empty in C#).
pub fn after_tick<H: Hasher>(_w: &mut World, _map: &MapView, _tick: u32, _h: &mut H, _ev: &mut StepEvents) {}

/// `EnterLobby`: bots leave, the director resets (the runner loads the lobby map).
pub fn back_to_lobby(w: &mut World) {
    for i in 0..MAX_PLAYERS {
        if w.players[i].is_bot() {
            w.players[i] = crate::world::Player::default();
        }
    }
    w.dir = DirectorState { champion: 255, ..Default::default() };
    w.round_index = 0;
    w.round.mode = crate::mapblob::mode::LOBBY;
    w.round.phase = rphase::SANDBOX;
}
