using System;
using System.Collections.Generic;
using FallRush.Net;
using FallRush.Shared;
using NUnit.Framework;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>G4.1b <c>LID_FLIP</c>: pure state, closed-form schedule, ring retire, fairness, colliders = state.</summary>
    public class LidGridTests
    {
        internal static LidGridDef Grid(int n = 9, uint seed = 0xC0FFEEu)
        {
            var d = LidGridDef.Default(0, 0, 0, n);
            d.Seed = seed;
            return d;
        }

        private static readonly uint[] Seeds = { 1u, 0xC0FFEEu, 0xDEADBEEFu, 123456789u };

        [Test]
        public void Schedule_ClosedForm_EqualsTheStepwiseSum_AndItsInverse()
        {
            var d = Grid();
            // 8 waves at 90, then 8 at 75, 8 at 60, then 45 forever, from tick 90.
            var t = d.FirstWaveTicks;
            for (var w = 0; w < 80; w++)
            {
                Assert.That(HazardMath.LidWaveStart(in d, w), Is.EqualTo(t), $"wave {w}");
                t += d.WaveIntervals[Math.Min(w / 8, 3)];
            }

            Assert.That(HazardMath.LidLastWave(in d, d.FirstWaveTicks - 1), Is.EqualTo(-1));
            for (var tt = 0; tt < 5000; tt++)
            {
                var w = HazardMath.LidLastWave(in d, tt);
                if (w < 0)
                {
                    Assert.That(tt, Is.LessThan(d.FirstWaveTicks));
                    continue;
                }

                Assert.That(HazardMath.LidWaveStart(in d, w), Is.LessThanOrEqualTo(tt));
                Assert.That(HazardMath.LidWaveStart(in d, w + 1), Is.GreaterThan(tt), $"t={tt}");
            }

            Assert.That(HazardMath.LidMaxOverlap(in d), Is.EqualTo(1), "45 flipped ticks / 45 ticks: windows never overlap");
            Assert.That(HazardMath.LidInnerCap(in d), Is.EqualTo(4), "(9 − 5) / 1");
        }

        [Test]
        public void State_IsPure_SingleLidEqualsAllLids_ForSeveralSizesAndSeeds()
        {
            foreach (var n in new[] { 5, 7, 9, 11 })
                foreach (var seed in Seeds)
                {
                    var d = Grid(n, seed);
                    var all = new LidState[d.Count];
                    var all2 = new LidState[d.Count];
                    var scratch = new int[d.Count];
                    for (var t = -30; t < 3200; t += 3)
                    {
                        HazardMath.LidStates(in d, t, all, scratch);
                        HazardMath.LidStates(in d, t, all2, new int[d.Count]);
                        Assert.That(all2, Is.EqualTo(all), "same inputs, same output");
                        if (t % 21 != 0)
                            continue;
                        for (var lid = 0; lid < d.Count; lid++)
                            Assert.That(HazardMath.LidStateAt(in d, lid, t, scratch), Is.EqualTo(all[lid]), $"n={n} seed={seed:X} t={t} lid={lid}");
                    }
                }
        }

        [Test]
        public void RingRetire_OuterRingsGoAtTheirTicks_WarnBefore_InnerNeverRetires_NoFlipAfterOrDuringWarn()
        {
            var d = Grid();
            var scratch = new int[d.Count];
            var corner = 0;                   // ring 4
            var ring3 = 1 * 9 + 1;            // (1,1)
            var ring2 = 2 * 9 + 2;
            var centre = 4 * 9 + 4;
            Assert.That((d.Ring(corner), d.Ring(ring3), d.Ring(ring2), d.Ring(centre)), Is.EqualTo((4, 3, 2, 0)));
            Assert.That((d.RetireTick(corner), d.RetireTick(ring3), d.RetireTick(ring2)), Is.EqualTo((750, 1500, 2250)));
            Assert.That(d.RetireTick(centre), Is.EqualTo(int.MaxValue));
            Assert.That(d.RetireTick(3 * 9 + 3), Is.EqualTo(int.MaxValue), "ring 1 (inner 3 × 3) never retires");
            Assert.That(HazardMath.LidStateAt(in d, corner, 660 - 1, scratch), Is.Not.EqualTo(LidState.RetireWarn));
            Assert.That(HazardMath.LidStateAt(in d, corner, 660, scratch), Is.EqualTo(LidState.RetireWarn));
            Assert.That(HazardMath.LidStateAt(in d, corner, 749, scratch), Is.EqualTo(LidState.RetireWarn));
            Assert.That(HazardMath.LidStateAt(in d, corner, 750, scratch), Is.EqualTo(LidState.Retired));
            Assert.That(HazardMath.LidStateAt(in d, corner, 30000, scratch), Is.EqualTo(LidState.Retired));
            Assert.That(HazardMath.LidStateAt(in d, ring2, 2250, scratch), Is.EqualTo(LidState.Retired));
            foreach (var seed in Seeds)
            {
                d.Seed = seed;
                for (var w = 0; w < 120; w++)
                {
                    var start = HazardMath.LidWaveStart(in d, w);
                    var n = HazardMath.LidWaveSet(in d, w, scratch);
                    for (var i = 0; i < n; i++)
                        Assert.That((long)d.RetireTick(scratch[i]) - d.RetireWarnTicks, Is.GreaterThanOrEqualTo((long)start + d.LitTicks + d.FlippedTicks),
                            "a lid never lights while its ring is warned or retired");
                }

                // After the last ring retires only the inner 3 × 3 is left; waves keep flipping inside it.
                var states = new LidState[d.Count];
                var flippedInner = 0;
                for (var t = 2250; t < 4000; t++)
                {
                    HazardMath.LidStates(in d, t, states, scratch);
                    for (var lid = 0; lid < d.Count; lid++)
                        if (!d.Inner(lid))
                            Assert.That(states[lid], Is.EqualTo(LidState.Retired));
                        else if (states[lid] == LidState.Flipped)
                            flippedInner++;
                }

                Assert.That(flippedInner, Is.GreaterThan(0), "the last 3 × 3 still flips");
            }
        }

        [Test]
        public void Fairness_AtLeastMinInnerSolidAtEveryTick_EvenAtHalfTheLidsPerWave()
        {
            foreach (var n in new[] { 5, 9, 11 })
                foreach (var seed in Seeds)
                {
                    var d = Grid(n, seed);
                    d.FlipPermille = new[] { 500 };
                    d.FlippedTicks = 120; // windows overlap (⌈120/45⌉ = 3 waves): the cap shrinks to ⌊4/3⌋ = 1
                    Assert.That(HazardAllowlist.Validate(new HazardSet(null, null) { LidGrids = new[] { d } }), Is.Empty);
                    var states = new LidState[d.Count];
                    var scratch = new int[d.Count];
                    var minSolid = 9;
                    for (var t = 0; t < 6000; t++)
                    {
                        HazardMath.LidStates(in d, t, states, scratch);
                        var solid = 0;
                        for (var lid = 0; lid < d.Count; lid++)
                            if (d.Inner(lid) && HazardMath.LidSolid(states[lid]))
                                solid++;
                        minSolid = Math.Min(minSolid, solid);
                        Assert.That(solid, Is.GreaterThanOrEqualTo(d.MinInnerSolid), $"n={n} seed={seed:X} t={t}");
                    }

                    Assert.That(minSolid, Is.LessThan(9), "the inner lids do flip (fairness is a floor, not a freeze)");
                }
        }

        [Test]
        public void Waves_ShareOfAliveLids_RisesStageByStage()
        {
            var d = Grid();
            var scratch = new int[d.Count];
            // Wave 0 (stage 0, 81 alive, 20 %): 16 lids (minus inner lids beyond the cap).
            var n0 = HazardMath.LidWaveSet(in d, 0, scratch);
            Assert.That(n0, Is.InRange(16 - 5, 16));
            var perStage = new double[4];
            foreach (var seed in Seeds)
            {
                d.Seed = seed;
                for (var w = 0; w < 32; w++)
                {
                    var start = HazardMath.LidWaveStart(in d, w);
                    var alive = 0;
                    for (var lid = 0; lid < d.Count; lid++)
                        if ((long)d.RetireTick(lid) - d.RetireWarnTicks >= start + d.LitTicks + d.FlippedTicks)
                            alive++;
                    perStage[w / 8] += (double)HazardMath.LidWaveSet(in d, w, scratch) / alive;
                }
            }

            for (var s = 1; s < 4; s++)
                Assert.That(perStage[s], Is.GreaterThan(perStage[s - 1]), $"stage {s} flips a larger share than stage {s - 1}");
            // Different seeds give different patterns (it is seeded, not fixed).
            d.Seed = 1;
            var a = new int[d.Count];
            var na = HazardMath.LidWaveSet(in d, 3, a);
            d.Seed = 2;
            var b = new int[d.Count];
            var nb = HazardMath.LidWaveSet(in d, 3, b);
            Assert.That(new ArraySegment<int>(a, 0, na), Is.Not.EqualTo(new ArraySegment<int>(b, 0, nb)));
        }

        [Test]
        public void Colliders_MatchTheState_InThePhysxMotorWorld_AtEveryTick_IncludingRewinds()
        {
            var d = Grid(5);
            var hz = new HazardSet(null, null) { LidGrids = new[] { d } };
            hz.InitLids();
            var objects = new List<GameObject>();
            try
            {
                var cols = new Collider[d.Count];
                for (var i = 0; i < d.Count; i++)
                {
                    var go = new GameObject($"Lid{i}");
                    objects.Add(go);
                    d.LidCenterMm(i, out var x, out var y, out var z);
                    go.transform.position = new Vector3(x * 0.001f, y * 0.001f, z * 0.001f);
                    var bc = go.AddComponent<BoxCollider>();
                    bc.size = new Vector3(d.LidMm * 0.001f, d.ThicknessMm * 0.001f, d.LidMm * 0.001f);
                    cols[i] = bc;
                }

                var host = new GameObject("ProbeHost");
                objects.Add(host);
                var probe = PhysxMotorWorld.CreateProbe(host.transform, 0.31f, 1f);
                var world = new PhysxMotorWorld(Physics.defaultPhysicsScene, 1 << 0, probe, hz, new MapInfo.Colliders { Lids = cols });
                var scratch = new int[d.Count];
                var ticks = new List<uint>();
                for (uint t = 0; t < 1200; t++)
                    ticks.Add(t);
                for (uint t = 1100; t > 900; t -= 7)
                    ticks.Add(t); // a rewind: posed back in time, the same floor again
                var flips = 0;
                foreach (var t in ticks)
                {
                    world.BeginTick(t);
                    for (var i = 0; i < d.Count; i++)
                    {
                        var st = HazardMath.LidStateAt(in d, i, (int)t, scratch);
                        Assert.That(cols[i].enabled, Is.EqualTo(HazardMath.LidSolid(st)), $"t={t} lid={i} {st}");
                        if (st == LidState.Flipped)
                            flips++;
                    }
                }

                Assert.That(flips, Is.GreaterThan(0));
            }
            finally
            {
                foreach (var o in objects)
                    UnityEngine.Object.DestroyImmediate(o);
            }
        }

        [Test]
        public void PlayerOnAFlippedLid_FallsThrough_SameOnEverySide()
        {
            var d = Grid(5);
            d.TopMm = 0;
            var hz = new HazardSet(null, null) { LidGrids = new[] { d } };
            var w = new AnalyticWorld().AddHazards(hz);
            var motor = new KinematicMotor(MovementConfig.Default);
            var scratch = new int[d.Count];
            // Find the first wave that flips the centre-adjacent lid 12 (ring 0 is the centre; 12 = centre for n 5).
            var lid = -1;
            var flipTick = -1;
            for (var t = 0; t < 3000 && lid < 0; t++)
                for (var i = 0; i < d.Count; i++)
                    if (HazardMath.LidStateAt(in d, i, t, scratch) == LidState.Flipped && d.Ring(i) == 2)
                    {
                        lid = i;
                        flipTick = t;
                        break;
                    }

            Assert.That(lid, Is.GreaterThanOrEqualTo(0));
            d.LidCenterMm(lid, out var x, out _, out var z);
            var s = new MotorState { PosX = x, PosY = 0, PosZ = z, Flags = MotorState.Grounded };
            for (var t = flipTick - 20; t < flipTick + 10; t++)
            {
                w.BeginTick((uint)t);
                PlayerStep.Run(motor, ref s, new InputCmd { Tick = (uint)t }, (uint)t, w, hz);
                if (t < flipTick)
                    Assert.That(s.PosY, Is.InRange(0, 20), "stands on the lid (skin above its top) while it is idle / lit");
            }

            Assert.That(s.PosY, Is.LessThan(-100), "falls once the lid flips");
        }

        [Test]
        public void Allowlist_DefaultPasses_BadShapesRefused()
        {
            Assert.That(HazardAllowlist.Validate(new HazardSet(null, null) { LidGrids = new[] { Grid() } }), Is.Empty);
            var even = Grid();
            even.N = 8;
            Assert.That(HazardAllowlist.Validate(new HazardSet(null, null) { LidGrids = new[] { even } }), Has.Some.Contains("odd"));
            var rings = Grid(7);
            rings.RingRetireTicks = new[] { 750, 1500, 2250 }; // n 7 has rings 3, 2 only before the inner 3 × 3
            Assert.That(HazardAllowlist.Validate(new HazardSet(null, null) { LidGrids = new[] { rings } }), Has.Some.Contains("never retires"));
            var order = Grid();
            order.RingRetireTicks = new[] { 1500, 750, 2250 };
            Assert.That(HazardAllowlist.Validate(new HazardSet(null, null) { LidGrids = new[] { order } }), Has.Some.Contains("increase"));
            var fair = Grid();
            fair.MinInnerSolid = 4;
            Assert.That(HazardAllowlist.Validate(new HazardSet(null, null) { LidGrids = new[] { fair } }), Has.Some.Contains("minInnerSolid"));
        }
    }
}
