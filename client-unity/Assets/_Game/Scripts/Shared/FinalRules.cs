using System;
using System.Collections.Generic;

namespace FallRush.Shared
{
    /// <summary>
    /// <c>RoundRules.Final</c> (Crumble Shelf, GDD §5.4, M4): standing on a tile (the server's own ground contact) starts
    /// its 0.6 s wobble, then it drops (reactive, replicated with <c>rx</c>; the collider goes off on both sides at
    /// contact + 18 ticks). Spinner bars sweep the top shelf from 60 s (pure, <see cref="HazardSet.SweeperStartT"/>).
    /// Falling below the kill-Y (−6 m) knocks you out. The last Rushling standing wins; at the 120 s timer the tiebreak is
    /// the lowest shelf reached (higher is better), then fewer tiles touched, then slot.
    /// On a lid map (Lid Flip: lid grids, no crumble grid; decision G4.5-8 = (b)) the timer tiebreak is the shelf last
    /// stood on (top beats lower), then fewer falls, then the later drop to a lower shelf, then slot.
    /// </summary>
    public sealed class FinalRules : TimedRoundRules
    {
        private readonly bool[][] _touched = new bool[SimCore.MaxSlots][];

        public FinalRules(uint timerTicks) => TimerTicks = timerTicks;

        public override string Mode => "Final";

        /// <summary>Lid map: lid grids and no crumble grid (fixed per round). The crumble path is unchanged.</summary>
        private bool LidMode => Hazards != null && Hazards.Grids.Length == 0 && Hazards.LidGrids != null && Hazards.LidGrids.Length > 0;
        public override int LiveCount => CountInPlay();

        protected override void OnBegin(SimCore sim, uint tick)
        {
            var n = Hazards != null && Hazards.Grids.Length > 0 ? Hazards.Grids[0].Count : 0;
            for (var i = 0; i < SimCore.MaxSlots; i++)
                _touched[i] = new bool[n];
        }

        protected override void OnRushTick(SimCore sim, uint tick)
        {
            var hz = Hazards;
            var rx = hz?.Reactive;
            var grid = hz != null && hz.Grids.Length > 0 ? hz.Grids[0] : default;
            for (var i = 0; i < SimCore.MaxSlots; i++)
            {
                if (!InPlay(i) || !sim.Slots[i].Active)
                    continue;
                ref readonly var s = ref sim.Slots[i].State;
                if (s.Has(MotorState.Respawning))
                {
                    KnockOut(i, tick, ElimReason.FellOut);
                    continue;
                }

                if (LidMode)
                {
                    if (s.Has(MotorState.Grounded))
                        TrackLidShelf(ref Players[i], in s, tick);
                    continue;
                }

                if (rx == null || !s.Has(MotorState.Grounded))
                    continue;
                var tile = grid.TileUnder(s.PosX, s.PosY, s.PosZ);
                if (tile < 0 || !rx.TileSolid(tile, tick))
                    continue;
                ref var p = ref Players[i];
                if (!_touched[i][tile])
                {
                    _touched[i][tile] = true;
                    p.Tiles++;
                }

                var shelf = grid.Shelf(tile);
                if (shelf > p.Deepest)
                    p.Deepest = shelf;
                if (rx.TileContactAt(tile, tick) == 0)
                    rx.Add(RxType.TileContact, tile, i, tick);
            }
        }

        private void TrackLidShelf(ref Player p, in MotorState s, uint tick)
        {
            var lids = Hazards.LidGrids;
            for (var g = 0; g < lids.Length; g++)
            {
                if (lids[g].LidUnder(s.PosX, s.PosY, s.PosZ) < 0)
                    continue;
                if (g > p.LidShelf)
                {
                    p.LidFalls++;
                    p.LidDropTick = tick;
                }

                p.LidShelf = g;
                return;
            }
        }

        protected override bool ShouldEnd(SimCore sim, uint tick) => CountInPlay() <= 1;

        private int Tiebreak(int a, int b)
        {
            ref readonly var pa = ref Players[a];
            ref readonly var pb = ref Players[b];
            if (LidMode)
            {
                if (pa.LidShelf != pb.LidShelf)
                    return pa.LidShelf.CompareTo(pb.LidShelf);       // still on the top shelf = better
                if (pa.LidFalls != pb.LidFalls)
                    return pa.LidFalls.CompareTo(pb.LidFalls);       // fewer falls = better
                if (pa.LidDropTick != pb.LidDropTick)
                    return pb.LidDropTick.CompareTo(pa.LidDropTick); // dropped later = better
                return a.CompareTo(b);
            }

            if (pa.Deepest != pb.Deepest)
                return pa.Deepest.CompareTo(pb.Deepest); // stayed on a higher shelf = better
            if (pa.Tiles != pb.Tiles)
                return pa.Tiles.CompareTo(pb.Tiles);     // fewer tiles touched = better
            return a.CompareTo(b);
        }

        protected override RoundResult Rank(SimCore sim, uint tick)
        {
            var alive = new List<int>();
            var outs = new List<int>();
            for (var i = 0; i < SimCore.MaxSlots; i++)
            {
                if (!Players[i].In)
                    continue;
                if (Players[i].Out)
                    outs.Add(i);
                else
                    alive.Add(i);
            }

            alive.Sort(Tiebreak);
            outs.Sort((a, b) => Players[a].OutTick != Players[b].OutTick ? Players[b].OutTick.CompareTo(Players[a].OutTick) : Tiebreak(a, b));
            var order = new List<int>();
            var q = new List<bool>();
            var reasons = new List<ElimReason>();
            var values = new List<int>();
            foreach (var s in alive)
            {
                var ok = order.Count < QualifyCount;
                order.Add(s);
                q.Add(ok);
                reasons.Add(ok ? ElimReason.NotQualified : ElimReason.TimeoutRank);
                values.Add(Value(s));
            }

            foreach (var s in outs)
            {
                order.Add(s);
                q.Add(false);
                reasons.Add(ElimReason.FellOut);
                values.Add(Value(s));
            }

            return RoundResult.From(order, q, reasons, values, QualifyCount, Mode, RushTick, tick, TimerExpired);
        }

        private int Value(int s) => LidMode ? Players[s].LidShelf * 1000 + Players[s].LidFalls : Players[s].Deepest * 1000 + Players[s].Tiles;

        protected override int ByteValue(int slot) => Players[slot].Deepest;

        protected override void StateEntries(List<byte> slots, List<uint> values)
        {
            for (var i = 0; i < SimCore.MaxSlots; i++)
                if (InPlay(i))
                {
                    slots.Add((byte)i);
                    values.Add((uint)Players[i].Tiles);
                }
        }
    }
}
