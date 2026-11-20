using System;
using System.Collections.Generic;

namespace FallRush.Shared
{
    /// <summary>
    /// Shared phase logic of the M4 round modes (Survival, Skill, Final): Intro → Countdown (inputs ignored) → Rush
    /// (hazard time 0) → Ended, participants, the timer, gameplay events and the per-slot "out" state. Engine-free.
    /// </summary>
    public abstract class TimedRoundRules : IRoundRules
    {
        public struct Player
        {
            public bool In;          // takes part
            public bool Out;         // knocked out (goo / fell / …) — no longer simulated
            public uint OutTick;
            public ElimReason OutReason;
            public bool Done;        // left the round early with a result (survival: escaped) — frozen, still drawn
            public uint DoneTick;
            public bool Qualified;
            public int Score;        // skill: stars; survival: height at the end (mm); final: unused
            public uint ScoreTick;   // skill: tick of the last score change
            public int Deepest;      // final: deepest shelf reached (0 = top)
            public int Tiles;        // final: distinct tiles stood on
            public int Height;       // survival: max height reached (mm)
        }

        public readonly Player[] Players = new Player[SimCore.MaxSlots];

        public abstract string Mode { get; }
        public RoundPhase Phase { get; private set; } = RoundPhase.Waiting;
        public uint PhaseStartTick { get; private set; }
        public bool IsOver => Phase == RoundPhase.Ended;
        public int IntroTicks { get; set; } = 60;
        public int CountdownTicks { get; set; } = 90;
        public uint TimerTicks { get; set; }
        public uint RushTick { get; private set; }
        public uint RushEndTick { get; private set; }
        public int Target { get; set; }
        public bool[] Participants { get; set; }
        public RoundResult RoundOutcome { get; protected set; }
        public List<RaceEvent> Pending { get; } = new List<RaceEvent>();
        public uint EventSeq { get; private set; }
        public bool TimerExpired { get; private set; }
        public event Action<RoundPhase> PhaseChanged;
        public int QualifyCount { get; protected set; }
        public abstract int LiveCount { get; }

        protected SimCore Sim { get; private set; }
        protected HazardSet Hazards => Sim?.Hazards;

        public virtual uint Begin(SimCore sim, uint tick)
        {
            Sim = sim;
            var players = 0;
            for (var i = 0; i < SimCore.MaxSlots; i++)
            {
                Players[i] = default;
                if (sim.Slots[i].Active && (Participants == null || Participants[i]))
                {
                    Players[i].In = true;
                    players++;
                }
            }

            QualifyCount = Math.Max(1, Math.Min(players, Target > 0 ? Target : players));
            TimerExpired = false;
            RoundOutcome = null;
            RushTick = tick + 1 + (uint)IntroTicks + (uint)CountdownTicks;
            RushEndTick = TimerTicks > 0 ? RushTick + TimerTicks : 0;
            for (var i = 0; i < SimCore.MaxSlots; i++)
                if (Players[i].In)
                    sim.Slots[i].State = sim.SpawnState(i);
            OnBegin(sim, tick);
            SetPhase(IntroTicks > 0 ? RoundPhase.Intro : RoundPhase.Countdown, tick + 1);
            return RushTick;
        }

        protected virtual void OnBegin(SimCore sim, uint tick)
        {
        }

        protected void SetPhase(RoundPhase p, uint tick)
        {
            Phase = p;
            PhaseStartTick = tick;
            Emit(RaceEventType.Phase, tick, 255, (int)p);
            PhaseChanged?.Invoke(p);
        }

        protected void Emit(RaceEventType type, uint tick, int slot, int arg) =>
            Pending.Add(new RaceEvent { Seq = ++EventSeq, Type = type, Tick = tick, Slot = (byte)slot, Arg = arg });

        public void BeginTick(SimCore sim, uint tick)
        {
            switch (Phase)
            {
                case RoundPhase.Intro when tick >= PhaseStartTick + (uint)IntroTicks:
                    SetPhase(RoundPhase.Countdown, tick);
                    break;
                case RoundPhase.Countdown when tick >= RushTick:
                    SetPhase(RoundPhase.Rush, tick);
                    break;
            }
        }

        public void EndTick(SimCore sim, uint tick)
        {
            if (Phase != RoundPhase.Rush)
                return;
            OnRushTick(sim, tick);
            if (Phase == RoundPhase.Ended)
                return;
            if (ShouldEnd(sim, tick))
            {
                End(sim, tick);
                return;
            }

            if (RushEndTick != 0 && tick >= RushEndTick)
            {
                TimerExpired = true;
                End(sim, tick);
            }
        }

        /// <summary>Per-tick rules during Rush (after every player was stepped).</summary>
        protected abstract void OnRushTick(SimCore sim, uint tick);

        /// <summary>Ends the round early (escapes complete, last standing, nobody left).</summary>
        protected abstract bool ShouldEnd(SimCore sim, uint tick);

        /// <summary>Builds <see cref="RoundOutcome"/> (ranking, qualified flags, reasons) and moves to Ended.</summary>
        protected abstract RoundResult Rank(SimCore sim, uint tick);

        /// <summary>The ranking as it would be if the round ended now (tests, QA, HUD); does not end the round.</summary>
        public RoundResult RankNow(SimCore sim, uint tick, bool timerExpired)
        {
            var was = TimerExpired;
            TimerExpired = timerExpired;
            var r = Rank(sim, tick);
            TimerExpired = was;
            return r;
        }

        private void End(SimCore sim, uint tick)
        {
            var res = Rank(sim, tick);
            RoundOutcome = res;
            for (var k = 0; k < res.Order.Length; k++)
            {
                ref var p = ref Players[res.Order[k]];
                p.Qualified = res.Qualified[k];
                if (res.Qualified[k])
                    Emit(RaceEventType.Qualified, tick, res.Order[k], k + 1);
                else
                    Emit(RaceEventType.Eliminated, tick, res.Order[k], k + 1);
            }

            Emit(RaceEventType.RoundEnd, tick, 255, res.QualifiedCount);
            SetPhase(RoundPhase.Ended, tick);
        }

        /// <summary>Knocks a player out now (goo, fall): out of the simulation and collisions, drawn hidden (respawning flag).</summary>
        protected void KnockOut(int slot, uint tick, ElimReason reason)
        {
            ref var p = ref Players[slot];
            if (p.Out || p.Done)
                return;
            p.Out = true;
            p.OutTick = tick;
            p.OutReason = reason;
            Emit(RaceEventType.Out, tick, slot, (int)reason);
        }

        public bool InPlay(int slot) => Players[slot].In && !Players[slot].Out && !Players[slot].Done;

        public virtual bool AcceptsInput(SimCore sim, int slot) => Phase == RoundPhase.Rush && InPlay(slot);
        public bool Simulates(SimCore sim, int slot) => Phase == RoundPhase.Waiting || InPlay(slot);
        public bool Collides(SimCore sim, int slot) => Simulates(sim, slot);
        public bool InRound(int slot) => Phase == RoundPhase.Waiting || Players[slot].In;

        /// <summary>After a fall: true = no respawn (the rules decide); false = back to the spawn point.</summary>
        public virtual bool Respawn(SimCore sim, int slot) => false;

        public virtual byte RaceByte(int slot)
        {
            ref readonly var p = ref Players[slot];
            if (!p.In)
                return 0;
            var b = (byte)(Math.Min(15, Math.Max(0, ByteValue(slot))) & PlayerState.RaceCheckpointMask);
            if (p.Done)
                b |= PlayerState.RaceFinished;
            if (p.Qualified)
                b |= PlayerState.RaceQualified;
            if (p.Out)
                b |= PlayerState.RaceEliminated;
            return b;
        }

        /// <summary>The low nibble of the snapshot race byte (skill: stars, final: deepest shelf).</summary>
        protected virtual int ByteValue(int slot) => 0;

        /// <summary>rstate: the header plus mode entries (slot, value): escapes (tick) / scores / alive.</summary>
        public RaceStateMessage StateMessage()
        {
            var slots = new List<byte>();
            var values = new List<uint>();
            StateEntries(slots, values);
            return new RaceStateMessage
            {
                LastEventSeq = EventSeq, Phase = Phase, PhaseStartTick = PhaseStartTick, RushEndTick = RushEndTick,
                QualifyCount = (byte)QualifyCount, QualifiedCount = (byte)Math.Min(255, LiveCount), FinisherCount = (byte)slots.Count,
                FinisherSlots = slots.ToArray(), FinisherTicks = values.ToArray(),
            };
        }

        protected virtual void StateEntries(List<byte> slots, List<uint> values)
        {
        }

        protected int CountInPlay()
        {
            var n = 0;
            for (var i = 0; i < SimCore.MaxSlots; i++)
                if (InPlay(i))
                    n++;
            return n;
        }
    }
}
