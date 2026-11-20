using System;
using System.Collections.Generic;

namespace FallRush.Shared
{
    /// <summary>
    /// <c>RoundRules.Survival</c> (Goo Tower, GDD §5.2, M4): goo rises (pure <see cref="GooDef"/>; touching it knocks you out,
    /// decided in <see cref="PlayerStep.Run"/> on both sides); falling out of the map knocks you out too. The first N to
    /// reach the summit pad qualify at once ("escaped") and leave the simulation; the round ends when N have escaped or
    /// nobody is left climbing. On timer end every survivor qualifies, ranked by height then slot, capped at N (the rest
    /// TIMEOUT_RANK). Fewer than N alive: only the survivors qualify (the director's minimum-advance handles 0 and 1).
    /// </summary>
    public sealed class SurvivalRules : TimedRoundRules
    {
        public readonly CheckpointDef Summit;
        private int _escaped;

        public SurvivalRules(CheckpointDef summit, uint timerTicks)
        {
            Summit = summit;
            TimerTicks = timerTicks;
        }

        public override string Mode => "Survival";
        public int Escaped => _escaped;

        /// <summary>Alive = still climbing + escaped.</summary>
        public override int LiveCount
        {
            get
            {
                var n = 0;
                for (var i = 0; i < SimCore.MaxSlots; i++)
                    if (Players[i].In && !Players[i].Out)
                        n++;
                return n;
            }
        }

        protected override void OnBegin(SimCore sim, uint tick) => _escaped = 0;

        protected override void OnRushTick(SimCore sim, uint tick)
        {
            for (var i = 0; i < SimCore.MaxSlots; i++)
            {
                if (!InPlay(i) || !sim.Slots[i].Active)
                    continue;
                ref var s = ref sim.Slots[i].State;
                if (s.Has(MotorState.Respawning))
                {
                    KnockOut(i, tick, (sim.Slots[i].LastStep & PlayerStep.Result.Gooed) != 0 ? ElimReason.Goo : ElimReason.FellOut);
                    continue;
                }

                ref var p = ref Players[i];
                if (s.PosY > p.Height)
                    p.Height = s.PosY;
                if (_escaped < QualifyCount && Summit.Contains(s.PosX, s.PosY, s.PosZ))
                {
                    p.Done = true;
                    p.DoneTick = tick;
                    _escaped++;
                    s.VelX = s.VelY = s.VelZ = 0;
                    Emit(RaceEventType.Finished, tick, i, _escaped);
                }
            }
        }

        protected override bool ShouldEnd(SimCore sim, uint tick) => _escaped >= QualifyCount || CountInPlay() == 0;

        protected override RoundResult Rank(SimCore sim, uint tick)
        {
            var escaped = new List<int>();
            var alive = new List<int>();
            var outs = new List<int>();
            for (var i = 0; i < SimCore.MaxSlots; i++)
            {
                if (!Players[i].In)
                    continue;
                if (Players[i].Done)
                    escaped.Add(i);
                else if (Players[i].Out)
                    outs.Add(i);
                else
                    alive.Add(i);
            }

            escaped.Sort((a, b) => Players[a].DoneTick != Players[b].DoneTick ? Players[a].DoneTick.CompareTo(Players[b].DoneTick) : a.CompareTo(b));
            alive.Sort((a, b) =>
            {
                var ya = sim.Slots[a].State.PosY;
                var yb = sim.Slots[b].State.PosY;
                return ya != yb ? yb.CompareTo(ya) : a.CompareTo(b);
            });
            outs.Sort((a, b) => Players[a].OutTick != Players[b].OutTick ? Players[b].OutTick.CompareTo(Players[a].OutTick) : a.CompareTo(b));

            var order = new List<int>();
            var q = new List<bool>();
            var reasons = new List<ElimReason>();
            var values = new List<int>();
            var qualified = 0;
            foreach (var s in escaped)
            {
                order.Add(s);
                q.Add(true);
                reasons.Add(ElimReason.NotQualified);
                values.Add((int)(Players[s].DoneTick - RushTick));
                qualified++;
            }

            foreach (var s in alive)
            {
                // Timer end: survivors qualify, highest first, up to N. Ended because N escaped: the rest did not make it.
                var ok = TimerExpired && qualified < QualifyCount;
                order.Add(s);
                q.Add(ok);
                reasons.Add(ok ? ElimReason.NotQualified : TimerExpired ? ElimReason.TimeoutRank : ElimReason.NotQualified);
                values.Add(sim.Slots[s].State.PosY);
                if (ok)
                    qualified++;
            }

            foreach (var s in outs)
            {
                order.Add(s);
                q.Add(false);
                reasons.Add(Players[s].OutReason);
                values.Add(Players[s].Height);
            }

            return RoundResult.From(order, q, reasons, values, QualifyCount, Mode, RushTick, tick, TimerExpired);
        }

        protected override int ByteValue(int slot) => 0;

        protected override void StateEntries(List<byte> slots, List<uint> values)
        {
            for (var i = 0; i < SimCore.MaxSlots; i++)
                if (Players[i].In && Players[i].Done)
                {
                    slots.Add((byte)i);
                    values.Add(Players[i].DoneTick);
                }
        }
    }
}
