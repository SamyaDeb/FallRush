using System;
using System.Collections.Generic;

namespace FallRush.Shared
{
    /// <summary>
    /// <c>RoundRules.Survival</c> (Goo Tower, GDD §5.2, M4): goo rises (pure <see cref="GooDef"/>; touching it knocks you out,
    /// decided in <see cref="PlayerStep.Run"/> on both sides). OC-51 (P7): a <b>fall</b> (out of the map, or dropping into
    /// the goo from a ledge above it) respawns you 1 s later on the tower path, like a race checkpoint: at the highest
    /// respawn spot not above the ledge you fell from that is at least <see cref="GooMarginMm"/> above the goo. No such spot
    /// (the goo already passed your ledge) = knocked out. Standing on a ledge as the goo rises over it still knocks you out. The first N to
    /// reach the summit pad qualify at once ("escaped") and leave the simulation; the round ends when N have escaped or
    /// nobody is left climbing. On timer end every survivor qualifies, ranked by height then slot, capped at N (the rest
    /// TIMEOUT_RANK). Fewer than N alive: only the survivors qualify (the director's minimum-advance handles 0 and 1).
    /// </summary>
    public sealed class SurvivalRules : TimedRoundRules
    {
        /// <summary>A respawn spot must be this far above the goo (≈ 9 s of rise at Goo Tower's 170 mm/s).</summary>
        public const int GooMarginMm = 1500;
        /// <summary>Dropping into the goo from a ledge this far above its surface counts as a fall (respawn), not as caught.</summary>
        public const int FallIntoGooMm = 600;

        public readonly CheckpointDef Summit;
        private readonly int[] _spots; // x, y, z, yaw256 quadruples (mm), server side (from the bot routes)
        private readonly int[] _groundY = new int[SimCore.MaxSlots];
        private readonly bool[] _pending = new bool[SimCore.MaxSlots];
        private readonly bool[] _pendingGoo = new bool[SimCore.MaxSlots];
        private int _escaped;

        public SurvivalRules(CheckpointDef summit, uint timerTicks, int[] respawnSpotsMm = null)
        {
            Summit = summit;
            TimerTicks = timerTicks;
            _spots = respawnSpotsMm ?? Array.Empty<int>();
        }

        public int RespawnSpots => _spots.Length / 4;
        public int Respawned { get; private set; }

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

        protected override void OnBegin(SimCore sim, uint tick)
        {
            _escaped = 0;
            Respawned = 0;
            for (var i = 0; i < SimCore.MaxSlots; i++)
            {
                _pending[i] = _pendingGoo[i] = false;
                _groundY[i] = sim.Slots[i].State.PosY;
            }
        }

        protected override void OnRushTick(SimCore sim, uint tick)
        {
            for (var i = 0; i < SimCore.MaxSlots; i++)
            {
                if (!InPlay(i) || !sim.Slots[i].Active)
                    continue;
                ref var s = ref sim.Slots[i].State;
                if (s.Has(MotorState.Respawning))
                {
                    if (_pending[i])
                        continue; // falling: SimCore calls Respawn after the delay
                    var gooed = (sim.Slots[i].LastStep & PlayerStep.Result.Gooed) != 0;
                    var gooY = Hazards != null ? Hazards.GooYMm(tick) : int.MinValue;
                    var fell = !gooed || gooY == int.MinValue || (long)_groundY[i] - gooY >= FallIntoGooMm;
                    if (fell && _spots.Length >= 4)
                    {
                        _pending[i] = true;
                        _pendingGoo[i] = gooed;
                        continue;
                    }

                    KnockOut(i, tick, gooed ? ElimReason.Goo : ElimReason.FellOut);
                    continue;
                }

                if (s.Has(MotorState.Grounded))
                    _groundY[i] = s.PosY;
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

        /// <summary>OC-51: after a fall, back onto the tower path (or out, when the goo has passed your ledge).</summary>
        public override bool Respawn(SimCore sim, int slot)
        {
            if (!_pending[slot])
                return false;
            _pending[slot] = false;
            var floor = Hazards != null ? Hazards.GooYMm(sim.Tick) : int.MinValue;
            var min = floor == int.MinValue ? int.MinValue : floor + GooMarginMm;
            var max = _groundY[slot] + 300;
            var best = -1;
            var bestFree = -1;
            var n = _spots.Length / 4;
            for (var j = 0; j < n; j++)
            {
                var y = _spots[j * 4 + 1];
                if (y < min || y > max)
                    continue;
                if (best < 0 || y > _spots[best * 4 + 1])
                    best = j;
                if (!Occupied(sim, slot, _spots[j * 4], y, _spots[j * 4 + 2]) && (bestFree < 0 || y > _spots[bestFree * 4 + 1]))
                    bestFree = j;
            }

            var pick = bestFree >= 0 ? bestFree : best;
            if (pick < 0)
            {
                KnockOut(slot, sim.Tick, _pendingGoo[slot] ? ElimReason.Goo : ElimReason.FellOut);
                return true; // out: stays hidden (respawning flag), out of the simulation
            }

            sim.Place(slot, new Vec3(_spots[pick * 4] * 0.001f, (_spots[pick * 4 + 1] + 50) * 0.001f, _spots[pick * 4 + 2] * 0.001f), (byte)_spots[pick * 4 + 3]);
            _groundY[slot] = _spots[pick * 4 + 1];
            Respawned++;
            Emit(RaceEventType.Respawned, sim.Tick, slot, 0);
            return true;
        }

        private static bool Occupied(SimCore sim, int self, int x, int y, int z)
        {
            for (var i = 0; i < SimCore.MaxSlots; i++)
            {
                if (i == self || !sim.Slots[i].Active)
                    continue;
                ref readonly var s = ref sim.Slots[i].State;
                long dx = s.PosX - x, dz = s.PosZ - z, dy = s.PosY - y;
                if (dx * dx + dz * dz < 800L * 800L && dy > -1000 && dy < 1000)
                    return true;
            }

            return false;
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
