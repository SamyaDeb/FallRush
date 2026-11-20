using System;
using System.Collections.Generic;

namespace FallRush.Shared
{
    /// <summary>
    /// <c>RoundRules.Skill</c> (Star Scramble, GDD §5.3, M4): stars spawn from a seeded timeline (pure: waves of 8 normal
    /// stars, a golden +3 every 15 s on an island). A pickup counts only if the <b>server's own capsule</b> touches the
    /// star (radius + the server position); a knockback drops exactly one carried star, which pops off and can be grabbed
    /// again. Falls respawn (no elimination). After the timer (60 s) the top N by score qualify; ties: the earliest tick at
    /// the final score (the last score change), then slot. The rest: LOWER_SCORE.
    /// </summary>
    public sealed class SkillRules : TimedRoundRules
    {
        public long Pickups, Drops, Regrabs;

        public SkillRules(uint timerTicks) => TimerTicks = timerTicks;

        public override string Mode => "Skill";
        public override int LiveCount => QualifyCount;

        private ReactiveSet Rx => Hazards.Reactive;
        private StarFieldDef Field => Hazards.StarFields[0];

        protected override void OnRushTick(SimCore sim, uint tick)
        {
            var hz = Hazards;
            if (hz == null || hz.StarFields.Length == 0 || Rx == null)
                return;
            var f = Field;
            var r = MotorState.QuantizeMm(sim.Cfg.Radius);
            var h = MotorState.QuantizeMm(sim.Cfg.Height);

            // 1) Knockbacks: a hazard hit this tick costs one carried star (it pops off where you were).
            for (var i = 0; i < SimCore.MaxSlots; i++)
            {
                if (!InPlay(i) || !sim.Slots[i].Active)
                    continue;
                ref var p = ref Players[i];
                if ((sim.Slots[i].LastStep & PlayerStep.Result.HazardHit) == 0 || p.Score <= 0)
                    continue;
                ref readonly var s = ref sim.Slots[i].State;
                p.Score--;
                p.ScoreTick = tick;
                Rx.Add(RxType.StarDrop, Rx.Drops.Count, i, tick, s.PosX, s.PosY + 600, s.PosZ);
                Drops++;
                Emit(RaceEventType.Dropped, tick, i, p.Score);
            }

            // 2) Pickups (slot order: two players touching the same star on the same tick → the lower slot gets it).
            var t = hz.Time(tick);
            for (var i = 0; i < SimCore.MaxSlots; i++)
            {
                if (!InPlay(i) || !sim.Slots[i].Active)
                    continue;
                ref readonly var s = ref sim.Slots[i].State;
                if (s.Has(MotorState.Respawning))
                    continue;
                _live.Clear();
                LiveStars(t, tick, _live);
                foreach (var id in _live)
                {
                    HazardMath.StarSlot(in f, hz.Seed, id, out var a, out _, out _, out var value);
                    HazardMath.StarAnchorMm(in f, hz, a, t, out var x, out var y, out var z);
                    if (HazardMath.StarTouch(x, y, z, s.PosX, s.PosY, s.PosZ, r, h, f.PickupRadiusMm))
                        Collect(i, id, value, tick);
                }
                for (var d = 0; d < Rx.Drops.Count; d++)
                {
                    if (!Rx.DropAvailable(d, tick, out var e))
                        continue;
                    HazardMath.DropPosMm(in f, e.Xcm * 10, e.Ycm * 10, e.Zcm * 10, (uint)(d + 1), (int)(tick - e.Tick), out var x, out var y, out var z);
                    if (!HazardMath.StarTouch(x, y, z, s.PosX, s.PosY, s.PosZ, r, h, f.PickupRadiusMm))
                        continue;
                    Collect(i, HazardMath.StarDropBase + d, 1, tick);
                    Regrabs++;
                }
            }
        }

        private void Collect(int slot, int id, int value, uint tick)
        {
            if (Rx.StarTaken(id, tick))
                return;
            Rx.Add(RxType.StarCollect, id, slot, tick);
            ref var p = ref Players[slot];
            p.Score += value;
            p.ScoreTick = tick;
            Pickups++;
            Emit(RaceEventType.Scored, tick, slot, p.Score);
        }

        private readonly List<int> _live = new List<int>();

        /// <summary>Every timeline star alive at hazard time <paramref name="t"/> and not yet taken.</summary>
        public void LiveStars(int t, uint tick, List<int> into) => LiveStars(Hazards, t, tick, into);

        /// <summary>Same, for any hazard set (client visuals, bots): what a player can see.</summary>
        public static void LiveStars(HazardSet hz, int t, uint tick, List<int> into)
        {
            if (hz == null || hz.StarFields.Length == 0 || hz.Reactive == null)
                return;
            var f = hz.StarFields[0];
            var Rx = hz.Reactive;
            if (t < 0 || f.WaveEveryTicks <= 0)
                return;
            var w1 = t / f.WaveEveryTicks;
            var w0 = Math.Max(0, (t - f.WaveLifeTicks) / f.WaveEveryTicks);
            for (var w = w0; w <= w1; w++)
                for (var k = 0; k < f.WaveSize; k++)
                {
                    var id = w * f.WaveSize + k;
                    if (id >= HazardMath.StarGoldenBase)
                        break;
                    if (!HazardMath.StarSlot(in f, hz.Seed, id, out _, out var sp, out var ex, out _) || t < sp || t >= ex || Rx.StarTaken(id, tick))
                        continue;
                    into.Add(id);
                }

            if (f.GoldenEveryTicks > 0)
                for (var g = 1; g * f.GoldenEveryTicks <= t; g++)
                {
                    var id = HazardMath.StarGoldenBase + g;
                    if (!HazardMath.StarSlot(in f, hz.Seed, id, out _, out var sp, out var ex, out _) || t < sp || t >= ex || Rx.StarTaken(id, tick))
                        continue;
                    into.Add(id);
                }
        }

        public override bool Respawn(SimCore sim, int slot) => false; // back to the spawn after 1 s; stars kept

        protected override bool ShouldEnd(SimCore sim, uint tick) => false;

        protected override RoundResult Rank(SimCore sim, uint tick)
        {
            var all = new List<int>();
            for (var i = 0; i < SimCore.MaxSlots; i++)
                if (Players[i].In)
                    all.Add(i);
            all.Sort((a, b) =>
            {
                ref readonly var pa = ref Players[a];
                ref readonly var pb = ref Players[b];
                if (pa.Score != pb.Score)
                    return pb.Score.CompareTo(pa.Score);
                if (pa.ScoreTick != pb.ScoreTick)
                    return pa.ScoreTick.CompareTo(pb.ScoreTick);
                return a.CompareTo(b);
            });
            var order = new List<int>();
            var q = new List<bool>();
            var reasons = new List<ElimReason>();
            var values = new List<int>();
            foreach (var s in all)
            {
                var ok = order.Count < QualifyCount;
                order.Add(s);
                q.Add(ok);
                reasons.Add(ok ? ElimReason.NotQualified : ElimReason.LowerScore);
                values.Add(Players[s].Score);
            }

            return RoundResult.From(order, q, reasons, values, QualifyCount, Mode, RushTick, tick, TimerExpired);
        }

        protected override int ByteValue(int slot) => Players[slot].Score;

        protected override void StateEntries(List<byte> slots, List<uint> values)
        {
            for (var i = 0; i < SimCore.MaxSlots; i++)
                if (Players[i].In)
                {
                    slots.Add((byte)i);
                    values.Add((uint)Math.Max(0, Players[i].Score));
                }
        }
    }
}
