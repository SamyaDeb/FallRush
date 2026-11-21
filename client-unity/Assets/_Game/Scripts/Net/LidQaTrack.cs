using System;
using System.Collections.Generic;
using FallRush.Shared;

namespace FallRush.Net
{
    /// <summary>
    /// G4.5 Final QA on a lid map (Lid Flip): watches one round tick by tick and records every fall and elimination.
    /// <list type="bullet">
    /// <item>A <b>departure</b> is the tick a player stops standing on a lid. Its cause: the lid <b>opened</b> under them
    /// (then <b>fair</b> = it was yellow for ≥ 30 ticks right before it flipped, or magenta ≥ 30 ticks before its ring
    /// retired), a <b>knock</b> (a hazard hit in the 30 ticks before), else a <b>jump / step</b> (the player's own move).</item>
    /// <item>A <b>fall</b> is a departure followed by landing on a lower shelf or going out; a hop back onto the same shelf is not one.</item>
    /// <item><b>Second chance:</b> top-shelf falls that left from above the lower shelf's footprint and landed on it.</item>
    /// <item><b>Inner floor:</b> at every tick ≥ <see cref="LidGridDef.MinInnerSolid"/> of each grid's inner 3 × 3 are solid.</item>
    /// </list>
    /// </summary>
    public sealed class LidQaTrack
    {
        private readonly HazardSet _hz;
        private readonly TimedRoundRules _rules;
        private readonly int[] _scratch;
        private readonly int[] _gridStart;

        private struct Slot
        {
            public bool Grounded;
            public int Grid, Lid;
            public uint LastHit;
            public int Hits;
            // pending departure
            public bool Away;
            public int AwayGrid;
            public uint AwayTick;
            public int AwayCause; // 0 jump/step, 1 opened (fair), 2 opened (unfair), 3 knock
            public bool AwayInFoot;
            public bool AwayKnocked;
            public int AwayX, AwayZ, AwayLid;
            public uint LowSince;
            public bool OutSeen;
        }

        private readonly Slot[] _s = new Slot[SimCore.MaxSlots];
        private static readonly bool Trace = Environment.GetEnvironmentVariable("FALLRUSH_QA_LID") == "1";
        private SimCore _core;
        private int _slot;

        // results
        public int FallsOpenedFair, FallsOpenedUnfair, FallsKnock, FallsSelf;
        public int TopFalls, TopFallsInFoot, TopFallsInFootCaught;
        public int OutFromTop, OutFromLow, OutKnockInvolved;
        public readonly List<float> OutTimes = new List<float>();
        public readonly List<uint> OutTicks = new List<uint>();
        public readonly List<float> LowerStay = new List<float>();
        public int InnerFloorViolations;
        public int MinInnerSolidSeen = 9;
        public readonly List<string> UnfairNotes = new List<string>();

        public LidQaTrack(HazardSet hz, TimedRoundRules rules)
        {
            _hz = hz;
            _rules = rules;
            var max = 0;
            _gridStart = new int[hz.LidGrids.Length];
            var at = 0;
            for (var g = 0; g < hz.LidGrids.Length; g++)
            {
                _gridStart[g] = at;
                at += hz.LidGrids[g].Count;
                max = Math.Max(max, hz.LidGrids[g].Count);
            }

            _scratch = new int[max];
        }

        private LidState StateAt(int grid, int lid, int t) => HazardMath.LidStateAt(in _hz.LidGrids[grid], lid, t, _scratch);

        /// <summary>
        /// The lid that holds the player up (grounded, feet within 10 cm of a lid top): the lid under the feet if solid, else
        /// the nearest solid neighbour within the capsule radius (a player can stand on a lid's edge with the feet over the gap
        /// of an open neighbour).
        /// </summary>
        private bool LidUnder(in MotorState s, int t, out int grid, out int lid)
        {
            for (var g = 0; g < _hz.LidGrids.Length; g++)
            {
                ref readonly var d = ref _hz.LidGrids[g];
                var l = d.LidUnder(s.PosX, s.PosY, s.PosZ);
                if (l < 0)
                    continue;
                grid = g;
                if (HazardMath.LidSolid(StateAt(g, l, t)))
                {
                    lid = l;
                    return true;
                }

                var best = -1;
                var bestD = long.MaxValue;
                int c0 = l % d.N, r0 = l / d.N;
                for (var dr = -1; dr <= 1; dr++)
                    for (var dc = -1; dc <= 1; dc++)
                    {
                        int c = c0 + dc, r = r0 + dr;
                        if ((dc == 0 && dr == 0) || c < 0 || r < 0 || c >= d.N || r >= d.N)
                            continue;
                        var n = r * d.N + c;
                        if (!HazardMath.LidSolid(StateAt(g, n, t)))
                            continue;
                        d.LidCenterMm(n, out var x, out _, out var z);
                        var half = d.LidMm / 2;
                        long ex = Math.Max(0, Math.Abs(s.PosX - x) - half), ez = Math.Max(0, Math.Abs(s.PosZ - z) - half);
                        var dd = ex * ex + ez * ez;
                        if (dd < bestD)
                        {
                            bestD = dd;
                            best = n;
                        }
                    }

                lid = best >= 0 ? best : l;
                return true;
            }

            grid = lid = -1;
            return false;
        }

        /// <summary>After <c>core.Step()</c> during the rush.</summary>
        public void Step(SimCore core, uint rush)
        {
            if (_rules.Phase != RoundPhase.Rush && _rules.Phase != RoundPhase.Ended)
                return;
            var tick = core.Tick;
            var t = _hz.Time(tick);
            if (_rules.Phase == RoundPhase.Rush)
            {
                var all = _hz.LidStatesAt(tick);
                for (var g = 0; g < _hz.LidGrids.Length; g++)
                {
                    ref readonly var d = ref _hz.LidGrids[g];
                    var solid = 0;
                    for (var i = 0; i < d.Count; i++)
                        if (d.Inner(i) && HazardMath.LidSolid(all[_gridStart[g] + i]))
                            solid++;
                    MinInnerSolidSeen = Math.Min(MinInnerSolidSeen, solid);
                    if (solid < d.MinInnerSolid)
                        InnerFloorViolations++;
                }
            }

            _core = core;
            for (var i = 0; i < SimCore.MaxSlots; i++)
            {
                ref var p = ref _rules.Players[i];
                if (!p.In || !core.Slots[i].Active)
                    continue;
                _slot = i;
                ref var q = ref _s[i];
                if (p.Out)
                {
                    if (!q.OutSeen)
                    {
                        q.OutSeen = true;
                        OutTimes.Add((p.OutTick - rush) / 30f);
                        OutTicks.Add(p.OutTick);
                        var fromGrid = q.Away ? q.AwayGrid : q.Grid;
                        if (fromGrid <= 0)
                            OutFromTop++;
                        else
                            OutFromLow++;
                        if (q.Away && q.AwayKnocked)
                            OutKnockInvolved++;
                        if (q.Away)
                            CountFall(ref q, landedLower: false);
                        if (q.LowSince != 0)
                            LowerStay.Add((p.OutTick - q.LowSince) / 30f);
                    }

                    continue;
                }

                ref readonly var s = ref core.Slots[i].State;
                if (core.HitsBySlot[i] != q.Hits)
                {
                    q.Hits = core.HitsBySlot[i];
                    q.LastHit = tick;
                    if (q.Away)
                    {
                        // A knock in the air (e.g. the bar while hopping it): the fall is the knock's.
                        q.AwayKnocked = true;
                        if (q.AwayCause == 0)
                            q.AwayCause = 3;
                    }
                }

                var g = -1;
                var lid = -1;
                var on = s.Has(MotorState.Grounded) && LidUnder(in s, t, out g, out lid);
                if (on)
                {
                    if (q.Away)
                    {
                        if (g > q.AwayGrid)
                            CountFall(ref q, landedLower: true);
                        q.Away = false;
                    }

                    if (g > 0 && q.LowSince == 0)
                        q.LowSince = tick;
                    q.Grounded = true;
                    q.Grid = g;
                    q.Lid = lid;
                    continue;
                }

                if (Trace && q.Away && (tick - q.AwayTick) % 2 == 0 && tick - q.AwayTick < 60)
                {
                    var lowLid = _hz.LidGrids.Length > 1 ? _hz.LidGrids[1].LidUnder(s.PosX, _hz.LidGrids[1].TopMm, s.PosZ) : -1;
                    var all = _hz.LidStatesAt(tick);
                    var lowSt = lowLid >= 0 ? all[_gridStart[1] + lowLid].ToString() : "-";
                    UnityEngine.Debug.Log($"[qa-lidpath] slot={i} age={tick - q.AwayTick} pos=({s.PosX},{s.PosY},{s.PosZ}) v=({s.VelX},{s.VelY},{s.VelZ}) g={(s.Has(MotorState.Grounded) ? 1 : 0)} flags={s.Flags} lowLid={lowLid} {lowSt}");
                }

                if (q.Grounded && !q.Away)
                {
                    // Departure from (q.Grid, q.Lid) at this tick.
                    q.Away = true;
                    q.AwayGrid = q.Grid;
                    q.AwayTick = tick;
                    q.AwayX = s.PosX;
                    q.AwayZ = s.PosZ;
                    q.AwayLid = q.Lid;
                    q.AwayKnocked = q.LastHit != 0 && tick - q.LastHit <= 30;
                    // The tick the lid stopped being solid, if it did around now (± 1 tick: step / pose order).
                    var f = int.MinValue;
                    for (var k = t - 1; k <= t + 1 && f == int.MinValue; k++)
                        if (!HazardMath.LidSolid(StateAt(q.Grid, q.Lid, k)))
                            f = k;
                    q.AwayInFoot = InLowerFootprint(s.PosX, s.PosZ, t);
                    if (f != int.MinValue)
                    {
                        var st = StateAt(q.Grid, q.Lid, f);
                        var warned = st == LidState.Retired ? LidState.RetireWarn : LidState.Lit;
                        var ok = true;
                        for (var k = 1; k <= 30; k++)
                            if (StateAt(q.Grid, q.Lid, f - k) != warned)
                            {
                                ok = false;
                                break;
                            }

                        q.AwayCause = ok ? 1 : 2;
                        if (!ok && UnfairNotes.Count < 8)
                            UnfairNotes.Add($"grid {q.Grid} lid {q.Lid} {st} at t {f}");
                    }
                    else
                        q.AwayCause = q.AwayKnocked ? 3 : 0;
                }

                q.Grounded = false;
            }
        }

        /// <summary>Over the lower shelf's solid extent at <paramref name="t"/>: its rings not yet retired (the 5 × 5, or the 3 × 3 after ring 2 goes).</summary>
        private bool InLowerFootprint(int x, int z, int t)
        {
            if (_hz.LidGrids.Length < 2)
                return false;
            ref readonly var d = ref _hz.LidGrids[1];
            var rings = d.Mid;
            while (rings > 1 && d.RingRetireTicks != null && d.Mid - rings < d.RingRetireTicks.Length && t >= d.RingRetireTicks[d.Mid - rings])
                rings--;
            var half = (2 * rings + 1) * d.PitchMm / 2;
            return Math.Abs(x - d.OriginXMm) <= half && Math.Abs(z - d.OriginZMm) <= half;
        }

        private void CountFall(ref Slot q, bool landedLower)
        {
            if (Trace && _core != null)
            {
                ref readonly var s = ref _core.Slots[_slot].State;
                UnityEngine.Debug.Log($"[qa-lidfall] slot={_slot} from grid {q.AwayGrid} t={_hz.Time(q.AwayTick)} lid={q.AwayLid} at ({q.AwayX},{q.AwayZ}) cause={(q.AwayCause == 1 ? "opened-fair" : q.AwayCause == 2 ? "opened-UNFAIR" : q.AwayCause == 3 ? "knock" : "own-move")} knocked={q.AwayKnocked} inFoot={q.AwayInFoot} → {(landedLower ? "landed lower" : "OUT")} at t={_hz.Time(_core.Tick)} pos=({s.PosX},{s.PosY},{s.PosZ}) v=({s.VelX},{s.VelY},{s.VelZ})");
            }

            switch (q.AwayCause)
            {
                case 1: FallsOpenedFair++; break;
                case 2: FallsOpenedUnfair++; break;
                case 3: FallsKnock++; break;
                default: FallsSelf++; break;
            }

            if (q.AwayGrid == 0)
            {
                TopFalls++;
                if (q.AwayInFoot)
                {
                    TopFallsInFoot++;
                    if (landedLower)
                        TopFallsInFootCaught++;
                }
            }

            q.Away = false;
        }

        /// <summary>Round over: players still on the lower shelf count their stay up to the end.</summary>
        public void Finish(uint endTick)
        {
            for (var i = 0; i < SimCore.MaxSlots; i++)
                if (_rules.Players[i].In && !_rules.Players[i].Out && _s[i].LowSince != 0)
                    LowerStay.Add((endTick - _s[i].LowSince) / 30f);
        }

        /// <summary>Two or more players went out in the same tick (anywhere / as the last two).</summary>
        public bool SameTickAny
        {
            get
            {
                for (var a = 0; a < OutTicks.Count; a++)
                    for (var b = a + 1; b < OutTicks.Count; b++)
                        if (OutTicks[a] == OutTicks[b])
                            return true;
                return false;
            }
        }

        public bool LastTwoSameTick(int alive)
        {
            if (alive > 0 || OutTicks.Count < 2)
                return false; // a survivor: the last pair was not a tie
            var max = 0u;
            foreach (var x in OutTicks)
                max = Math.Max(max, x);
            var n = 0;
            foreach (var x in OutTicks)
                n += x == max ? 1 : 0;
            return n >= 2;
        }
    }
}
