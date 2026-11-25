using System;
using System.Collections.Generic;
using FallRush.Shared;

namespace FallRush.Net
{
    /// <summary>
    /// P3-F (server): the Final's rules while the Final runs on an arena (<see cref="ChainFinalHandoff"/>). Phases,
    /// timer and HUD state come from the inner Classic rules (intro → countdown → rush on the server clock), but the
    /// server simulates nobody (inputs off, players frozen and out of collisions), and the round ends with the arena's
    /// result: <see cref="IsOver"/> once the handoff has it, <see cref="RoundOutcome"/> = the arena's placements, so the
    /// director's ROUND_RESULT / CHAMPION / receipt and the jsonl log carry the on-chain champion.
    /// </summary>
    public sealed class ChainFinalRules : IRoundRules
    {
        private readonly IRoundRules _inner;
        private readonly ChainFinalHandoff _h;
        private bool _endedSent;
        private uint _innerEndedAt;

        /// <summary>
        /// Beta audit: the arena never produced a result <see cref="ResultGraceTicks"/> after the Final's own timer ran out
        /// (rollup task died, keeper down). The server aborts the match (refunds) instead of waiting forever.
        /// </summary>
        public bool TimedOut { get; private set; }

        public const uint ResultGraceTicks = 120 * 30;

        public ChainFinalRules(IRoundRules inner, ChainFinalHandoff handoff)
        {
            _inner = inner;
            _h = handoff;
        }

        /// <summary>
        /// fullchain: the whole match runs on the arena (<see cref="ChainFinalHandoff.All"/>). The server holds its round 1
        /// with these rules until the arena's director has a champion (<see cref="ChainFinalHandoff.ResultOrder"/>: the
        /// champion first, the others by elimination round); the director then ends the match after this one round
        /// (one qualifier = the champion). <see cref="TimedOut"/> = the arena's match failed (ABORTED / gone / stalled),
        /// not the round timer (a full match runs many minutes past it).
        /// </summary>
        public ChainFinalRules(IRoundRules inner, ChainFinalHandoff handoff, bool all) : this(inner, handoff) => All = all;

        public readonly bool All;

        public string Mode => _inner.Mode;
        public RoundPhase Phase => _h.ResultOrder != null ? RoundPhase.Ended : _inner.Phase == RoundPhase.Ended ? RoundPhase.Rush : _inner.Phase;
        public bool IsOver => _h.ResultOrder != null;
        public int IntroTicks { get => _inner.IntroTicks; set => _inner.IntroTicks = value; }
        public int CountdownTicks { get => _inner.CountdownTicks; set => _inner.CountdownTicks = value; }
        public uint TimerTicks => _inner.TimerTicks;
        public uint RushTick => _inner.RushTick;
        public int Target { get => _inner.Target; set => _inner.Target = value; }
        public bool[] Participants { get => _inner.Participants; set => _inner.Participants = value; }
        public uint Begin(SimCore sim, uint tick) => _inner.Begin(sim, tick);
        public List<RaceEvent> Pending => _inner.Pending;
        public RaceStateMessage StateMessage() => _inner.StateMessage();

        public event Action<RoundPhase> PhaseChanged
        {
            add => _inner.PhaseChanged += value;
            remove => _inner.PhaseChanged -= value;
        }

        public int LiveCount => _inner.LiveCount;
        public bool InRound(int slot) => _inner.InRound(slot);

        public RoundResult RoundOutcome
        {
            get
            {
                var o = _h.ResultOrder;
                if (o == null)
                    return null;
                var n = o.Length;
                var r = new RoundResult
                {
                    Mode = Mode, Target = 1, Order = (int[])o.Clone(), Qualified = new bool[n], Reasons = (ElimReason[])_h.ResultReasons.Clone(),
                    Value = new int[n], StartTick = RushTick, EndTick = RushTick + _h.ResultTicks,
                };
                if (n > 0)
                    r.Qualified[0] = true;
                return r;
            }
        }

        // ---- ISimRules: the server simulates nobody in an on-chain Final
        public void BeginTick(SimCore sim, uint tick) => _inner.BeginTick(sim, tick);

        public void EndTick(SimCore sim, uint tick)
        {
            _h.Poll();
            if (All)
            {
                if (_inner.Phase != RoundPhase.Ended)
                    _inner.EndTick(sim, tick);
                if (_h.ChainFailed)
                    TimedOut = true;
            }
            else if (_inner.Phase != RoundPhase.Ended)
                _inner.EndTick(sim, tick); // phases + timer; nobody moves, so nobody falls
            else if (_h.ResultOrder == null)
            {
                if (_innerEndedAt == 0)
                    _innerEndedAt = Math.Max(1, tick);
                else if (!TimedOut && tick - _innerEndedAt > ResultGraceTicks)
                {
                    TimedOut = true;
                    UnityEngine.Debug.LogWarning($"[gs] on-chain Final: no result {ResultGraceTicks / 30} s after the timer; aborting the match");
                }
            }
            if (_h.ResultOrder != null && !_endedSent)
            {
                _endedSent = true;
                Pending.Add(new RaceEvent { Type = RaceEventType.Phase, Tick = tick, Slot = 255, Arg = (int)RoundPhase.Ended });
            }
        }

        public bool AcceptsInput(SimCore sim, int slot) => false;
        public bool Simulates(SimCore sim, int slot) => false;
        public bool Collides(SimCore sim, int slot) => false;
        public bool Respawn(SimCore sim, int slot) => false;
        public byte RaceByte(int slot) => _inner.RaceByte(slot);
    }
}
