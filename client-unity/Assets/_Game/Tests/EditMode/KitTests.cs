using System;
using System.Collections.Generic;
using System.IO;
using FallRush.Net;
using FallRush.Shared;
using NUnit.Framework;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>M3a: the obstacle kit — poses, effects, carry, conveyor, spring, doors, knock rules, modifiers, allowlists.</summary>
    public class KitTests
    {
        private static readonly MovementConfig Cfg = MovementConfig.Default;

        private static InputCmd Move(int mx, int my, byte buttons = 0) => new InputCmd { MoveX = (sbyte)mx, MoveY = (sbyte)my, Buttons = buttons };

        private static readonly InputCmd Idle = default;

        private static SweeperDef Spinner(int heightMm, int periodTicks = 90) => new SweeperDef
        {
            PivotXMm = 0, PivotYMm = 0, PivotZMm = 3000, ArmMm = 4500, BarRadiusMm = 150, BarHeightMm = heightMm, PeriodTicks = periodTicks,
            KnockbackCms = KnockProfile.SoftPushCms, KnockUpCms = KnockProfile.SoftUpCms, StunTicks = KnockProfile.SoftStunTicks,
            ImmunityTicks = KnockProfile.ImmunityFor(KnockProfile.SoftStunTicks), Tangential = 1,
        };

        private static MalletDef Mallet() => new MalletDef
        {
            PivotXMm = 0, PivotYMm = 5000, PivotZMm = 0, SwingAxis = 2, ArmMm = 4400, HeadRadiusMm = 450, HeadHalfWidthMm = 700,
            AmplitudePhase16 = 10000, PeriodTicks = 120, TelegraphTicks = 15,
            KnockbackCms = KnockProfile.HardPushCms, KnockUpCms = KnockProfile.HardUpCms, StunTicks = KnockProfile.HardStunTicks,
            ImmunityTicks = KnockProfile.ImmunityFor(KnockProfile.HardStunTicks),
        };

        private static BumperDef Bumper(int x, int z) => new BumperDef
        {
            CenterXMm = x, CenterYMm = 0, CenterZMm = z, RadiusMm = 600, HeightMm = 1000, ContactMarginMm = 40,
            KnockbackCms = KnockProfile.SoftPushCms, KnockUpCms = KnockProfile.SoftUpCms, StunTicks = KnockProfile.SoftStunTicks,
            ImmunityTicks = KnockProfile.ImmunityFor(KnockProfile.SoftStunTicks),
        };

        private static HazardSet Set(SweeperDef[] sw = null, PlatformDef[] pl = null, ConveyorDef[] cv = null, SpringPadDef[] pd = null,
            TurnTableDef[] tt = null, GateDoorDef[] dr = null, MalletDef[] ml = null, BumperDef[] bm = null) =>
            new HazardSet(sw, pl, cv, pd, tt, dr, ml, bm);

        private static MotorState Settle(KinematicMotor m, AnalyticWorld w, MotorState s, ref uint tick, HazardSet hz)
        {
            for (var i = 0; i < 90 && !s.Has(MotorState.Grounded); i++)
            {
                w.BeginTick(++tick);
                PlayerStep.Run(m, ref s, in Idle, tick, w, hz);
            }

            for (var i = 0; i < 3; i++)
            {
                w.BeginTick(++tick);
                PlayerStep.Run(m, ref s, in Idle, tick, w, hz);
            }

            Assert.That(s.Has(MotorState.Grounded), Is.True, "settled");
            return s;
        }

        // ------------------------------------------------------------------ poses

        [Test]
        public void EveryPose_IsPeriodic_AndRoundRelative()
        {
            var sp = Spinner(450, 150);
            var pl = new PlatformDef { HalfXMm = 1000, HalfYMm = 250, HalfZMm = 1000, PeriodTicks = 240, KnotsMm = new[] { -2000, 0, -2000, 2000, 0, -2000, 2000, 300, 2000, -2000, 0, 2000 } };
            var tt = new TurnTableDef { RadiusMm = 3000, ThicknessMm = 300, PeriodTicks = -300, Phase16 = 1234 };
            var dr = new GateDoorDef { HalfXMm = 200, HalfYMm = 1300, HalfZMm = 1500, LiftMm = 2600, ClosedTicks = 40, OpeningTicks = 6, OpenTicks = 50, ClosingTicks = 15, OffsetTicks = 17, TelegraphTicks = 15 };
            var ml = Mallet();
            for (var t = -500; t < 1000; t += 7)
            {
                Assert.That(HazardMath.PhaseAt(t, sp.PeriodTicks, sp.Phase16), Is.EqualTo(HazardMath.PhaseAt(t + 150, sp.PeriodTicks, sp.Phase16)));
                HazardMath.PlatformCenterMm(in pl, t, out var x0, out var y0, out var z0);
                HazardMath.PlatformCenterMm(in pl, t + 240, out var x1, out var y1, out var z1);
                Assert.That((x1, y1, z1), Is.EqualTo((x0, y0, z0)));
                HazardMath.TurnTableCarryMm(in tt, t, 2500, 100, out var a0, out var b0);
                HazardMath.TurnTableCarryMm(in tt, t + 300, 2500, 100, out var a1, out var b1);
                Assert.That((a1, b1), Is.EqualTo((a0, b0)));
                Assert.That(HazardMath.DoorLiftMm(in dr, t), Is.EqualTo(HazardMath.DoorLiftMm(in dr, t + HazardMath.DoorCycle(in dr))));
                HazardMath.MalletHeadMm(in ml, t, out var hx0, out var hy0, out var hz0, out _);
                HazardMath.MalletHeadMm(in ml, t + 120, out var hx1, out var hy1, out var hz1, out _);
                Assert.That((hx1, hy1, hz1), Is.EqualTo((hx0, hy0, hz0)));
            }

            // Catmull-Rom passes through its knots.
            for (var k = 0; k < 4; k++)
            {
                HazardMath.PlatformCenterMm(in pl, k * 60, out var x, out var y, out var z);
                Assert.That((x, y, z), Is.EqualTo((pl.KnotsMm[k * 3], pl.KnotsMm[k * 3 + 1], pl.KnotsMm[k * 3 + 2])), $"knot {k}");
            }

            // Round-relative time: the same pose at the same t, whatever the absolute round start.
            var a = Set(new[] { sp });
            var b = Set(new[] { sp });
            b.RoundStartTick = 12345;
            Assert.That(b.Time(12345 + 77), Is.EqualTo(a.Time(77)));
            Assert.That(b.Time(12000), Is.EqualTo(-345), "negative before the round starts");
            Assert.That(HazardMath.DoorLiftMm(in dr, -1), Is.EqualTo(HazardMath.DoorLiftMm(in dr, HazardMath.DoorCycle(in dr) - 1)));
        }

        [Test]
        public void TurboToys_RoundsPeriodsToWholeTicks_TelegraphKept()
        {
            var turbo = ModifierEffects.Get(3);
            Assert.That(turbo.Key, Is.EqualTo("TurboToys"));
            Assert.That(HazardTuning.ScalePeriod(150, 1200), Is.EqualTo(125));
            Assert.That(HazardTuning.ScalePeriod(90, 1200), Is.EqualTo(75));
            Assert.That(HazardTuning.ScalePeriod(100, 1200), Is.EqualTo(83));  // 83.33
            Assert.That(HazardTuning.ScalePeriod(-150, 1200), Is.EqualTo(-125));
            Assert.That(HazardTuning.ScalePeriod(1, 1200), Is.EqualTo(1));
            Assert.That(HazardTuning.ScalePeriod(150, 1000), Is.EqualTo(150));
            var d = new GateDoorDef { ClosedTicks = 60, OpeningTicks = 6, OpenTicks = 60, ClosingTicks = 15, OffsetTicks = 30, TelegraphTicks = 15 };
            HazardTuning.Apply(ref d, in turbo);
            Assert.That((d.ClosedTicks, d.OpeningTicks, d.OpenTicks, d.ClosingTicks, d.OffsetTicks), Is.EqualTo((50, 5, 50, 13, 25)));
            Assert.That(d.TelegraphTicks, Is.EqualTo(15), "telegraph unchanged (telegraphPermille 1000)");
            var c = new ConveyorDef { BeltXMms = 2000, BeltZMms = -1500 };
            HazardTuning.Apply(ref c, in turbo);
            Assert.That((c.BeltXMms, c.BeltZMms), Is.EqualTo((2400, -1800)));
            var sp = Spinner(450, 150);
            HazardTuning.Apply(ref sp, ModifierEffects.Get(0));
            Assert.That(sp.PeriodTicks, Is.EqualTo(150), "None changes nothing");
        }

        [Test]
        public void ObstaclePhase_FromSeedAndId_StableAndInRange()
        {
            var a = ObstaclePhase.Phase16(42, "bpd_spinner_01", 0, 65535);
            Assert.That(ObstaclePhase.Phase16(42, "bpd_spinner_01", 0, 65535), Is.EqualTo(a));
            Assert.That(ObstaclePhase.Phase16(43, "bpd_spinner_01", 0, 65535), Is.Not.EqualTo(a));
            Assert.That(ObstaclePhase.Phase16(42, "bpd_spinner_02", 0, 65535), Is.Not.EqualTo(a));
            for (ulong s = 0; s < 500; s++)
                Assert.That(ObstaclePhase.Phase16(s, "x", 1000, 3000), Is.InRange(1000, 3000));
            Assert.That(ObstaclePhase.Phase16(7, "x", 500, 500), Is.EqualTo(500));
            // FNV-1a 32 reference value (seed 0, empty id): 8 zero bytes into the offset basis.
            Assert.That(ObstaclePhase.Hash(0, ""), Is.EqualTo(0x9be17165u));
        }

        [Test]
        public void Variant_ResolvesFromTheSameScene()
        {
            var go = new GameObject("spinner");
            try
            {
                var sw = go.AddComponent<HazardSweeper>();
                sw.obstacleId = "kit_spinner";
                sw.def = Spinner(450, 150);
                sw.variantB = Spinner(450, -120);
                sw.hasVariantB = true;
                var a = sw.Resolve(new RoundParams { Variant = 0, HazardSeed = 9 });
                var b = sw.Resolve(new RoundParams { Variant = 1, HazardSeed = 9 });
                var bt = sw.Resolve(new RoundParams { Variant = 1, HazardSeed = 9, Modifier = 3 });
                Assert.That(a.PeriodTicks, Is.EqualTo(150));
                Assert.That(b.PeriodTicks, Is.EqualTo(-120));
                Assert.That(bt.PeriodTicks, Is.EqualTo(-100), "variant B + TurboToys");
                Assert.That(a.Phase16, Is.EqualTo(b.Phase16), "phase from seed + id, not variant");
                Assert.That(sw.Resolve(new RoundParams { HazardSeed = 10 }).Phase16, Is.Not.EqualTo(a.Phase16));
                sw.hasVariantB = false;
                Assert.That(sw.Resolve(new RoundParams { Variant = 1 }).PeriodTicks, Is.EqualTo(150), "no B def → A");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void DevSeed_DerivesVariantAndModifier_FromThePoolWeights()
        {
            var counts = new int[6];
            var variants = new int[2];
            for (ulong s = 0; s < 4000; s++)
            {
                var p = RoundParams.FromDevSeed(MapPoolV1.BlockPartyDash, s);
                counts[p.Modifier]++;
                variants[p.Variant]++;
                Assert.That(MapPoolV1.ModifierAllowed(MapPoolV1.BlockPartyDash, p.Modifier), Is.True);
            }

            TestContext.WriteLine($"modifiers {string.Join(",", counts)} variants {variants[0]}/{variants[1]}");
            Assert.That(counts[4], Is.EqualTo(0), "BigBonk is not allowed on Block Party Dash");
            Assert.That(counts[0], Is.InRange(1400, 1800)); // 40 %
            Assert.That(counts[1], Is.InRange(450, 750));   // 15 %
            Assert.That(variants[0], Is.InRange(1800, 2200));
            var fixedP = RoundParams.FromDevSeed(MapPoolV1.BlockPartyDash, 5, 1, 2);
            Assert.That((fixedP.Variant, fixedP.Modifier), Is.EqualTo(((byte)1, (byte)2)));
            Assert.That(RoundParams.FromDevSeed(0, 77).Modifier, Is.EqualTo(RoundParams.FromDevSeed(0, 77).Modifier), "deterministic");
            Assert.That(RoundParams.RaceQualifyCount(12), Is.EqualTo(8));
            Assert.That(RoundParams.RaceQualifyCount(4), Is.EqualTo(3));
            Assert.That(RoundParams.RaceQualifyCount(3), Is.EqualTo(2));
        }

        // ------------------------------------------------------------------ knock hazards

        private static int FirstContactTick(Func<int, bool> contact, int period)
        {
            int first = -1, last = -1;
            for (var t = 0; t < period * 2; t++)
            {
                var c = contact(t);
                if (c && first < 0)
                    first = t;
                if (first >= 0 && !c)
                {
                    last = t - 1;
                    break;
                }
            }

            Assert.That(first, Is.GreaterThanOrEqualTo(0), "the hazard passes the test spot");
            return (first + last) / 2;
        }

        [Test]
        public void SpinnerBar_LowBarIsJumped_HighBarIsDivedUnder()
        {
            var low = Spinner(350);
            var high = Spinner(950);
            const int r = 310, h = 1000;
            var tl = FirstContactTick(t => HazardMath.SweeperContactMm(in low, t, 0, 0, 0, r, h, out _, out _), 90);
            Assert.That(HazardMath.SweeperContactMm(in low, tl, 0, 550, 0, r, h, out _, out _), Is.False, "jumping 55 cm clears the low bar");
            Assert.That(HazardMath.SweeperContactMm(in low, tl, 0, 0, 0, r, HazardSet.DiveHitHeightMm, out _, out _), Is.True, "diving into the low bar still hits");

            var th = FirstContactTick(t => HazardMath.SweeperContactMm(in high, t, 0, 0, 0, r, h, out _, out _), 90);
            Assert.That(HazardMath.SweeperContactMm(in high, th, 0, 500, 0, r, h, out _, out _), Is.True, "jumping into the high bar hits");
            for (var fy = 0; fy <= 190; fy += 10) // a dive rises at most 19 cm (3 m/s up)
                Assert.That(HazardMath.SweeperContactMm(in high, th, 0, fy, 0, r, HazardSet.DiveHitHeightMm, out _, out _), Is.False, $"diving under the high bar at feet {fy} mm");

            // Through PlayerStep: a diving player at the crossing is never hit by the high bar.
            var hz = Set(new[] { high });
            var s = new MotorState { PosY = 100, Flags = MotorState.Diving };
            for (var t = 0; t < 90; t++)
            {
                var c = s;
                Assert.That(hz.ApplyKnocks(ref c, (uint)t, Cfg), Is.False);
            }

            var standing = new MotorState { Flags = MotorState.Grounded };
            Assert.That(hz.ApplyKnocks(ref standing, (uint)th, Cfg), Is.True);
        }

        [Test]
        public void KnockSoft_vs_KnockHard_Values()
        {
            var sp = Set(new[] { Spinner(450) });
            var t = FirstContactTick(tt => HazardMath.SweeperContactMm(in sp.Sweepers[0], tt, 0, 0, 0, 310, 1000, out _, out _), 90);
            var s = new MotorState { Flags = MotorState.Grounded, CoyoteTicks = 3, Surface = 3 };
            Assert.That(sp.ApplyKnocks(ref s, (uint)t, Cfg), Is.True);
            Assert.That(Math.Sqrt(s.VelX * s.VelX + s.VelZ * s.VelZ), Is.EqualTo(600).Within(2));
            Assert.That((s.VelY, s.StunTicks, s.ImmunityTicks), Is.EqualTo(((short)350, (byte)11, (byte)26)));
            Assert.That(s.Has(MotorState.Grounded) || s.Surface != 0 || s.CoyoteTicks != 0, Is.False);

            var ml = Set(ml: new[] { Mallet() });
            var d = ml.Mallets[0];
            HazardMath.MalletHeadMm(in d, 0, out var hx, out var hy, out var hzz, out _);
            Assert.That(hy, Is.EqualTo(600), "head axis 0.6 m above the floor at the bottom of the swing");
            var s2 = new MotorState { PosX = hx, PosY = 0, PosZ = hzz, Flags = MotorState.Grounded };
            Assert.That(ml.ApplyKnocks(ref s2, 0, Cfg), Is.True);
            Assert.That((Math.Abs((int)s2.VelZ), s2.VelX, s2.VelY, s2.StunTicks, s2.ImmunityTicks), Is.EqualTo((900, (short)0, (short)500, (byte)18, (byte)33)));
            Assert.That(s2.VelZ, Is.GreaterThan(0), "pushed along the head's travel (+Z at phase 0)");

            // A player jumping over the bottom of the swing (feet above the head) is not hit.
            var s3 = new MotorState { PosX = hx, PosY = 1080, PosZ = hzz };
            Assert.That(ml.ApplyKnocks(ref s3, 0, Cfg), Is.False);

            var bm = Set(bm: new[] { Bumper(0, 0) });
            var s4 = new MotorState { PosX = 600 + 310 + 30, PosY = 0, PosZ = 0, Flags = MotorState.Grounded };
            Assert.That(bm.ApplyKnocks(ref s4, 0, Cfg), Is.True, "touching the bumper (within the 4 cm margin)");
            Assert.That((s4.VelX, s4.VelZ, s4.StunTicks), Is.EqualTo(((short)600, (short)0, (byte)11)), "radial knock_soft");
            var s5 = new MotorState { PosX = 600 + 310 + 60 };
            Assert.That(bm.ApplyKnocks(ref s5, 0, Cfg), Is.False);
            var s6 = new MotorState { PosX = 700, PosY = 1010 };
            Assert.That(bm.ApplyKnocks(ref s6, 0, Cfg), Is.False, "jumping over the 1 m bumper");
        }

        [Test]
        public void Immunity_LastsHalfASecondAfterTheStun_NoStunLockBetweenAdjacentHazards()
        {
            // Crossfire: two spinners in opposite directions and a mallet all sweep the same spot; the player is pinned there
            // every tick (worst case: a player who never moves away). Every hit must be followed by the stun, then ≥ 15
            // ticks of control before the next one.
            var a = Spinner(450, 60);
            var b = Spinner(450, -75);
            b.PivotZMm = -3000;
            var m = Mallet();
            m.PeriodTicks = 66;
            var hz = Set(new[] { a, b }, ml: new[] { m });
            var w = new AnalyticWorld().Ground().AddHazards(hz);
            var motor = new KinematicMotor(Cfg);
            var s = new MotorState { Flags = MotorState.Grounded };
            var hits = new List<int>();
            var controlRun = 0;
            var minControlBeforeHit = int.MaxValue;
            for (var t = 1; t <= 1200; t++)
            {
                s.PosX = 0;
                s.PosZ = 0;
                s.PosY = 0;
                s.VelX = s.VelZ = 0;
                w.BeginTick((uint)t);
                var r = PlayerStep.Run(motor, ref s, in Idle, (uint)t, w, hz);
                if ((r & PlayerStep.Result.HazardHit) != 0)
                {
                    if (hits.Count > 0)
                        minControlBeforeHit = Math.Min(minControlBeforeHit, controlRun);
                    hits.Add(t);
                    controlRun = 0;
                }
                else if (s.StunTicks == 0)
                {
                    controlRun++;
                }
            }

            TestContext.WriteLine($"hits={hits.Count} minControlBeforeHit={minControlBeforeHit} ticks");
            Assert.That(hits.Count, Is.GreaterThan(10));
            for (var i = 1; i < hits.Count; i++)
                Assert.That(hits[i] - hits[i - 1], Is.GreaterThanOrEqualTo(KnockProfile.SoftStunTicks + KnockProfile.ImmunityAfterStunTicks), "immunity");
            Assert.That(minControlBeforeHit, Is.GreaterThanOrEqualTo(KnockProfile.ImmunityAfterStunTicks), "≥ 0.5 s of control after every stun");
        }

        // ------------------------------------------------------------------ carry

        [Test]
        public void LoopPlatform_RiderEndsWhereThePlatformDoes_MmExact()
        {
            var pl = new PlatformDef
            {
                CenterXMm = 0, CenterYMm = 1750, CenterZMm = 0, HalfXMm = 1500, HalfYMm = 250, HalfZMm = 1500, PeriodTicks = 240,
                KnotsMm = new[] { -2500, 0, -2500, 2500, 0, -2500, 2500, 0, 2500, -2500, 0, 2500 },
            };
            var hz = Set(pl: new[] { pl });
            var w = new AnalyticWorld().Ground(-5f).AddHazards(hz);
            var motor = new KinematicMotor(Cfg);
            uint tick = 0;
            HazardMath.PlatformCenterMm(in pl, 0, out var cx, out _, out var cz);
            var s = Settle(motor, w, new MotorState { PosX = cx + 400, PosY = 2050, PosZ = cz - 300 }, ref tick, hz);
            HazardMath.PlatformCenterMm(in pl, (int)tick, out var px, out _, out var pz);
            var offX = s.PosX - px;
            var offZ = s.PosZ - pz;
            var start = s;
            for (var i = 0; i < 240; i++)
            {
                w.BeginTick(++tick);
                PlayerStep.Run(motor, ref s, in Idle, tick, w, hz);
                HazardMath.PlatformCenterMm(in pl, (int)tick, out px, out _, out pz);
                Assert.That((s.PosX - px, s.PosZ - pz), Is.EqualTo((offX, offZ)), $"tick {tick}: mm-exact offset");
                Assert.That(s.Has(MotorState.Grounded), Is.True);
            }

            Assert.That((s.PosX, s.PosY, s.PosZ), Is.EqualTo((start.PosX, start.PosY, start.PosZ)), "one loop later: the same millimetre");
        }

        [Test]
        public void TurnTable_OneTurn_ReturnsWithin1mm_NeverFlung([Values(240, -300, 900)] int period)
        {
            var tt = new TurnTableDef { CenterXMm = 1000, CenterYMm = 300, CenterZMm = -500, RadiusMm = 4000, ThicknessMm = 300, PeriodTicks = period, Phase16 = 4321 };
            Assert.That(HazardAllowlist.Validate(Set(tt: new[] { tt })), Is.Empty);
            var hz = Set(tt: new[] { tt });
            var w = new AnalyticWorld().Ground(-5f).AddHazards(hz);
            var motor = new KinematicMotor(Cfg);
            uint tick = 0;
            var s = Settle(motor, w, new MotorState { PosX = 1000 + 3200, PosY = 330, PosZ = -500 + 900 }, ref tick, hz);
            var start = s;
            var r0 = Math.Sqrt((double)(s.PosX - 1000) * (s.PosX - 1000) + (double)(s.PosZ + 500) * (s.PosZ + 500));
            var turn = Math.Abs(period);
            for (var i = 0; i < turn; i++)
            {
                w.BeginTick(++tick);
                PlayerStep.Run(motor, ref s, in Idle, tick, w, hz);
                var r = Math.Sqrt((double)(s.PosX - 1000) * (s.PosX - 1000) + (double)(s.PosZ + 500) * (s.PosZ + 500));
                Assert.That(r, Is.EqualTo(r0).Within(2.0), $"tick {tick}: radius kept (never flung)");
                Assert.That(s.Has(MotorState.Grounded), Is.True, $"tick {tick}");
            }

            var err = Math.Sqrt((double)(s.PosX - start.PosX) * (s.PosX - start.PosX) + (double)(s.PosZ - start.PosZ) * (s.PosZ - start.PosZ));
            TestContext.WriteLine($"period {period}: after one turn {err:F2} mm from the start");
            Assert.That(err, Is.LessThanOrEqualTo(1.0));
        }

        [Test]
        public void JumpingOffAMovingSurface_InheritsItsVelocity_WalkingOffDoesNot()
        {
            var pl = new PlatformDef { CenterXMm = 0, CenterYMm = 1750, HalfXMm = 3000, HalfYMm = 250, HalfZMm = 3000, PeriodTicks = 300, TravelXMm = 3000 };
            var hz = Set(pl: new[] { pl });
            var w = new AnalyticWorld().Ground(-5f).AddHazards(hz);
            var motor = new KinematicMotor(Cfg);
            uint tick = 0;
            var s = Settle(motor, w, new MotorState { PosY = 2050 }, ref tick, hz);
            // Ride to the fast middle of the sine, then jump with no move input.
            while (HazardMath.PhaseAt((int)tick + 1, 300, 0) != 0)
            {
                w.BeginTick(++tick);
                PlayerStep.Run(motor, ref s, in Idle, tick, w, hz);
            }

            w.BeginTick(++tick);
            var delta = HazardMath.PlatformDelta(in pl, (int)tick);
            var jump = new InputCmd { Buttons = InputCmd.JumpPressed | InputCmd.JumpHeld };
            PlayerStep.Run(motor, ref s, in jump, tick, w, hz);
            Assert.That(motor.Last.Jumped && motor.Last.Inherited, Is.True);
            Assert.That(s.VelX, Is.EqualTo(MotorState.QuantizeCm(delta.X * 30f)).Within(1), "keeps the platform's speed");
            Assert.That(s.VelX, Is.GreaterThan(150), "≈ 1.9 m/s at the sine's fastest point");
            var vx = s.VelX;
            for (var i = 0; i < 10; i++)
            {
                w.BeginTick(++tick);
                PlayerStep.Run(motor, ref s, in Idle, tick, w, hz);
            }

            Assert.That(s.VelX, Is.EqualTo(vx), "no air drag without input");

            // Walking off a conveyor edge: no inheritance.
            var cv = new ConveyorDef { CenterXMm = 0, CenterYMm = -250, CenterZMm = 0, HalfXMm = 2000, HalfYMm = 250, HalfZMm = 2000, BeltXMms = 2000 };
            var hz2 = Set(cv: new[] { cv });
            var w2 = new AnalyticWorld().Ground(-5f).AddHazards(hz2);
            tick = 0;
            var s2 = Settle(motor, w2, new MotorState { PosX = 1500, PosY = 20 }, ref tick, hz2);
            var ran = false;
            for (var i = 0; i < 40 && s2.Has(MotorState.Grounded); i++)
            {
                w2.BeginTick(++tick);
                PlayerStep.Run(motor, ref s2, in Idle, tick, w2, hz2);
                ran = true;
            }

            Assert.That(ran && !s2.Has(MotorState.Grounded), Is.True, "carried off the end");
            Assert.That(s2.VelX, Is.EqualTo(0), "walked/carried off: no inherited velocity");
        }

        [Test]
        public void Conveyor_SteadyStateSpeed_WithAndAgainstTheBelt()
        {
            var cv = new ConveyorDef { CenterXMm = 0, CenterYMm = -250, CenterZMm = 0, HalfXMm = 100000, HalfYMm = 250, HalfZMm = 3000, BeltXMms = 2000 };
            var hz = Set(cv: new[] { cv });
            var w = new AnalyticWorld().Ground(-5f).AddHazards(hz);
            var motor = new KinematicMotor(Cfg);
            foreach (var (dir, expect) in new[] { (127, 7000), (-127, -3000), (0, 2000) }) // v2: run 5 m/s ± belt 2 m/s
            {
                uint tick = 0;
                var s = Settle(motor, w, new MotorState { PosY = 20 }, ref tick, hz);
                var cmd = Move(dir, 0);
                for (var i = 0; i < 20; i++)
                {
                    w.BeginTick(++tick);
                    PlayerStep.Run(motor, ref s, in cmd, tick, w, hz);
                }

                var x0 = s.PosX;
                for (var i = 0; i < 60; i++)
                {
                    w.BeginTick(++tick);
                    PlayerStep.Run(motor, ref s, in cmd, tick, w, hz);
                }

                var speed = (s.PosX - x0) / 2.0; // mm per second over 60 ticks
                TestContext.WriteLine($"input {dir}: ground speed {speed:F0} mm/s (expected {expect})");
                Assert.That(speed, Is.EqualTo(expect).Within(10));
            }
        }

        [Test]
        public void SpringPad_ReachesItsDesignedApexAndLanding_Within5cm()
        {
            var pad = new SpringPadDef { CenterXMm = 0, CenterYMm = 100, CenterZMm = 0, HalfXMm = 500, HalfZMm = 500, ThicknessMm = 100, LaunchXCms = 600, LaunchYCms = 1200, LaunchZCms = 0 };
            var hz = Set(pd: new[] { pad });
            var w = new AnalyticWorld().Ground().AddHazards(hz);
            var motor = new KinematicMotor(Cfg);
            var s = new MotorState { PosX = 0, PosY = 110, PosZ = 0 };
            uint tick = 0;
            var launched = false;
            var launchX = 0;
            var launchY = 0;
            var maxY = 0;
            MotorState prev = s;
            float landX = float.NaN;
            for (var i = 0; i < 120; i++)
            {
                prev = s;
                w.BeginTick(++tick);
                var r = PlayerStep.Run(motor, ref s, in Idle, tick, w, hz);
                if ((r & PlayerStep.Result.Launched) != 0)
                {
                    Assert.That(launched, Is.False, "once per landing");
                    launched = true;
                    launchX = s.PosX;
                    launchY = s.PosY;
                    continue;
                }

                if (!launched)
                    continue;
                maxY = Math.Max(maxY, s.PosY);
                if (s.Has(MotorState.Grounded))
                {
                    // Where the trajectory crossed y = 0 inside this tick (exact per-step ballistics from the previous state).
                    var g = Cfg.Gravity * (prev.VelY <= 0 ? Cfg.FallMultiplier : 1f);
                    var vy = prev.VelY * 0.01f;
                    var y0 = prev.PosY * 0.001f;
                    var tau = (vy + (float)Math.Sqrt(vy * vy + 2f * g * y0)) / g;
                    landX = prev.PosX * 0.001f + prev.VelX * 0.01f * tau - launchX * 0.001f;
                    break;
                }
            }

            Assert.That(launched, Is.True);
            const float vy0 = 12f, vx0 = 6f;
            var g0 = Cfg.Gravity;
            var apex = vy0 * vy0 / (2f * g0);
            var drop = apex + launchY * 0.001f;
            var design = vx0 * (vy0 / g0 + (float)Math.Sqrt(2f * drop / (g0 * Cfg.FallMultiplier)));
            TestContext.WriteLine($"apex {(maxY - launchY) * 0.001f:F3} m (design {apex:F3}), landing {landX:F3} m (design {design:F3})");
            Assert.That((maxY - launchY) * 0.001f, Is.EqualTo(apex).Within(0.05f));
            Assert.That(landX, Is.EqualTo(design).Within(0.05f));
        }

        // ------------------------------------------------------------------ doors

        private static GateDoorDef Door() => new GateDoorDef
        {
            CenterXMm = 5000, CenterYMm = 1300, CenterZMm = 0, HalfXMm = 200, HalfYMm = 1300, HalfZMm = 2000, LiftMm = 2700,
            ClosedTicks = 60, OpeningTicks = 6, OpenTicks = 60, ClosingTicks = 15, TelegraphTicks = 15,
        };

        [Test]
        public void GateDoor_BlocksWhileClosed_PassableWhileOpen()
        {
            var d = Door();
            var hz = Set(dr: new[] { d });
            var w = new AnalyticWorld().Ground().AddHazards(hz);
            var motor = new KinematicMotor(Cfg);
            uint tick = 0;
            var s = Settle(motor, w, new MotorState { PosX = 0, PosY = 20 }, ref tick, hz);
            var run = Move(127, 0);
            // Closed for ticks 0..59: run into it.
            while (tick < 55)
            {
                w.BeginTick(++tick);
                PlayerStep.Run(motor, ref s, in run, tick, w, hz);
            }

            Assert.That(s.PosX, Is.LessThan(5000 - 200 - 300), "blocked by the closed door");
            // Opening 60..65, open 66..125: keeps running and passes.
            while (tick < 100)
            {
                w.BeginTick(++tick);
                PlayerStep.Run(motor, ref s, in run, tick, w, hz);
            }

            Assert.That(s.PosX, Is.GreaterThan(5000 + 200 + 400), "through the open door");
            Assert.That(HazardMath.DoorOpen(in d, 70), Is.True);
            Assert.That(HazardMath.DoorOpen(in d, 30), Is.False);
            Assert.That(HazardMath.DoorTicksUntilClosing(in d, 100), Is.EqualTo(26));
        }

        [Test]
        public void GateDoor_ClosingOnAPlayer_PushesThemOut_NeverStuck([Values(4900, 5000, 5150)] int standX)
        {
            var d = Door();
            var hz = Set(dr: new[] { d });
            var w = new AnalyticWorld().Ground().AddHazards(hz);
            var motor = new KinematicMotor(Cfg);
            uint tick = 70; // open
            var s = Settle(motor, w, new MotorState { PosX = standX, PosY = 20 }, ref tick, hz);
            var pushed = false;
            while (tick < 145) // closes 126..140
            {
                w.BeginTick(++tick);
                var r = PlayerStep.Run(motor, ref s, in Idle, tick, w, hz);
                pushed |= (r & PlayerStep.Result.DoorPushed) != 0;
                // Never inside the door's volume.
                hz.DoorCenterMm(0, tick, out _, out var cy, out _);
                var bottom = cy - d.HalfYMm;
                var inside = Math.Abs(s.PosX - d.CenterXMm) < d.HalfXMm + 310 && s.PosY + 1000 > bottom && s.PosY < cy + d.HalfYMm;
                Assert.That(inside, Is.False, $"tick {tick}: inside the closing door");
            }

            Assert.That(pushed, Is.True);
            Assert.That(HazardMath.DoorLiftMm(in d, (int)tick), Is.EqualTo(0), "closed now");
            var side = Math.Sign(s.PosX - d.CenterXMm);
            Assert.That(side, Is.EqualTo(standX >= d.CenterXMm ? 1 : -1), "out on the side they were on");
            // Not stuck: walk away freely.
            var x0 = s.PosX;
            var away = Move(127 * side, 0);
            for (var i = 0; i < 15; i++)
            {
                w.BeginTick(++tick);
                PlayerStep.Run(motor, ref s, in away, tick, w, hz);
            }

            Assert.That(Math.Abs(s.PosX - x0), Is.GreaterThan(1000));
        }

        // ------------------------------------------------------------------ modifiers

        private static float Apex(MovementConfig cfg)
        {
            var w = new AnalyticWorld().Ground();
            var m = new KinematicMotor(cfg);
            var s = new MotorState { PosY = 50 };
            for (var i = 0; i < 40; i++)
                m.Step(ref s, in Idle, cfg.Dt, w);
            var y0 = s.PosY;
            var jump = new InputCmd { Buttons = InputCmd.JumpPressed | InputCmd.JumpHeld };
            m.Step(ref s, in jump, cfg.Dt, w);
            var max = s.PosY;
            for (var i = 0; i < 60; i++)
            {
                m.Step(ref s, in Idle, cfg.Dt, w);
                max = Math.Max(max, s.PosY);
            }

            return (max - y0) / 1000f;
        }

        [Test]
        public void MoonBounce_Apex1p5m_ButterFloors_Decel()
        {
            var moon = MovementConfig.For(1);
            Assert.That(moon.Gravity, Is.EqualTo(14f).Within(1e-5f));   // v2: 20 × 0.7
            Assert.That(moon.JumpApex, Is.EqualTo(1.5f).Within(1e-5f)); // v2: 1.2 × 1.25
            var a = Apex(moon);
            TestContext.WriteLine($"MoonBounce apex {a:F4} m, None apex {Apex(Cfg):F4} m");
            Assert.That(a, Is.EqualTo(1.5f).Within(0.02f), "1.2 × 1.25");
            Assert.That(ReferenceEquals(MovementConfig.For(0), MovementConfig.Default) && ReferenceEquals(MovementConfig.For(3), MovementConfig.Default), Is.True, "None / TurboToys leave the motor alone");

            var butter = MovementConfig.For(2);
            Assert.That(butter.GroundDecel, Is.EqualTo(12f).Within(1e-5f), "30 × 0.4");
            int StopTicks(MovementConfig c)
            {
                var w = new AnalyticWorld().Ground();
                var m = new KinematicMotor(c);
                var s = new MotorState { PosY = 50 };
                for (var i = 0; i < 40; i++)
                    m.Step(ref s, Move(0, 127), c.Dt, w);
                var n = 0;
                while (s.VelZ > 0 && n < 100)
                {
                    m.Step(ref s, in Idle, c.Dt, w);
                    n++;
                }

                return n;
            }

            Assert.That(StopTicks(Cfg), Is.EqualTo(5));     // v2: 5 / 30 s = 5.0 ticks
            Assert.That(StopTicks(butter), Is.EqualTo(13)); // v2: 5 / 12 s = 12.5 ticks
        }

        // ------------------------------------------------------------------ allowlist

        [Test]
        public void Allowlist_RefusesOutOfRangeDefs()
        {
            var ok = Set(new[] { Spinner(450, 150) }, ml: new[] { Mallet() }, bm: new[] { Bumper(0, 0) }, dr: new[] { Door() },
                tt: new[] { new TurnTableDef { RadiusMm = 3000, PeriodTicks = 300 } },
                pd: new[] { new SpringPadDef { HalfXMm = 500, HalfZMm = 500, LaunchYCms = 1200, LaunchXCms = 600 } },
                cv: new[] { new ConveyorDef { HalfYMm = 250, BeltXMms = 2000 } });
            Assert.That(HazardAllowlist.Validate(ok), Is.Empty);

            var fast = Spinner(450, 20);
            var noImmunity = Spinner(450, 150);
            noImmunity.ImmunityTicks = 5;
            var door = Door();
            door.TelegraphTicks = 0;
            var tt = new TurnTableDef { RadiusMm = 8000, PeriodTicks = 90 };
            var bad = HazardAllowlist.Validate(Set(new[] { fast, noImmunity }, dr: new[] { door }, tt: new[] { tt }));
            foreach (var e in bad)
                TestContext.WriteLine(e);
            Assert.That(bad.Exists(e => e.StartsWith("SPINNER_BAR[0]: |periodTicks|=20")), Is.True);
            Assert.That(bad.Exists(e => e.StartsWith("SPINNER_BAR[1]: immunityTicks=5")), Is.True);
            Assert.That(bad.Exists(e => e.StartsWith("GATE_DOOR[0]: telegraphTicks=0")), Is.True);
            Assert.That(bad.Exists(e => e.StartsWith("TURN_TABLE[0]: rimSpeedMms")), Is.True);
        }

        // ------------------------------------------------------------------ config files

        [Test]
        public void ModifiersV1_TypedValues_HashMatch_FileOnDisk_AndMapPoolIds()
        {
            var canon = ModifierEffects.ToCanonicalJson();
            Assert.That(canon, Is.EqualTo(ModifiersData.CanonicalJson));
            Assert.That(Sha256.Hex(Sha256.Hash(System.Text.Encoding.UTF8.GetBytes(canon))), Is.EqualTo(ModifiersData.Sha256Hex));
            var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "packages", "shared", "config"));
            var disk = MovementConfigTests.Canonicalize(new MovementConfigTests.MiniJson(File.ReadAllText(Path.Combine(dir, ModifiersData.File))).Parse());
            Assert.That(Sha256.Hex(Sha256.Hash(System.Text.Encoding.UTF8.GetBytes(disk))), Is.EqualTo(ModifiersData.Sha256Hex),
                "modifiers_v1.json changed: run `pnpm gen:modifiers` and commit ModifiersData.g.cs");

            var pool = (Dictionary<string, object>)new MovementConfigTests.MiniJson(File.ReadAllText(Path.Combine(dir, "map_pool_v1.json"))).Parse();
            var mods = (List<object>)pool["modifiers"];
            Assert.That(mods.Count, Is.EqualTo(ModifiersData.All.Length));
            for (var i = 0; i < mods.Count; i++)
            {
                var m = (Dictionary<string, object>)mods[i];
                Assert.That((long)m["id"], Is.EqualTo(ModifiersData.All[i].Id));
                Assert.That((string)m["key"], Is.EqualTo(ModifiersData.All[i].Key));
            }

            // MapPoolV1 (C# mirror) equals the JSON: variant counts and modifier weights.
            var maps = (List<object>)pool["maps"];
            Assert.That(maps.Count, Is.EqualTo(MapPoolV1.Keys.Length));
            for (var i = 0; i < maps.Count; i++)
            {
                var m = (Dictionary<string, object>)maps[i];
                Assert.That((string)m["key"], Is.EqualTo(MapPoolV1.Keys[i]));
                Assert.That((string)m["mode"], Is.EqualTo(MapPoolV1.Modes[i]));
                Assert.That((long)m["variants"], Is.EqualTo(MapPoolV1.Variants[i]));
                var w = (List<object>)m["modifierWeights"];
                Assert.That(w.Count, Is.EqualTo(MapPoolV1.Weights[i].Length));
                for (var k = 0; k < w.Count; k++)
                {
                    var e = (Dictionary<string, object>)w[k];
                    Assert.That(((long)e["modifier"], (long)e["weight"]), Is.EqualTo(((long)MapPoolV1.Weights[i][k].mod, (long)MapPoolV1.Weights[i][k].weight)));
                }
            }
        }

        // ------------------------------------------------------------------ determinism vectors + hashes

        [Test]
        public void HazardVectors_MatchTheCommittedVectors()
        {
            var all = HazardVectors.ComputeAll();
            var path = Path.Combine(Application.dataPath, "_Game", "Tests", "Vectors", "hazard_vectors_v1.json");
            Assert.That(File.Exists(path), Is.True, path);
            var json = (Dictionary<string, object>)new MovementConfigTests.MiniJson(File.ReadAllText(path)).Parse();
            var cases = (List<object>)json["cases"];
            Assert.That(cases.Count, Is.EqualTo(HazardVectors.Count));
            var contacts = 0;
            for (var i = 0; i < cases.Count; i++)
            {
                var row = (List<object>)cases[i];
                for (var f = 0; f < HazardVectors.Width; f++)
                    Assert.That(all[i * HazardVectors.Width + f], Is.EqualTo((int)(long)row[f]), $"case {i} {HazardVectors.Fields[f]}");
                if (all[i * HazardVectors.Width + 8] == 1)
                    contacts++;
            }

            Assert.That(HazardVectors.Digest(all), Is.EqualTo((string)json["digest"]));
            Assert.That(HazardVectors.Digest(all), Is.EqualTo(HazardVectorsExpected.Digest), "generated C# table is current");
            Assert.That(HazardVectors.SelfTest(HazardVectorsExpected.Data, "editmode", out var line), Is.True, line);
            TestContext.WriteLine(line);
            TestContext.WriteLine($"cases with a contact / launch / push: {contacts}");
            Assert.That(contacts, Is.InRange(150, 700), "the vectors exercise hits and misses");
        }

        [Test]
        public void MapContentHash_IsStable_AndSensitive()
        {
            MapContent Make() => new MapContent
            {
                MapId = 0, Version = 1, KillYMm = -10000, SpawnsMm = new[] { 1, 2, 3, 0 },
                Checkpoints = new[] { new CheckpointDef { CenterXMm = 5, HalfXMm = 6000, RespawnMm = new[] { 1, 0, 1, 0 }, CourseMm = 40000 } },
                Variants = new[] { Set(new[] { Spinner(450) }), Set(new[] { Spinner(450, -90) }) },
                ObstacleIds = new[] { "1:bpd_spinner_01" }, PhaseRanges = new[] { 0, 65535 }, CourseLineMm = new[] { 0, 0, 0, 0, 0, 160000 },
            };
            var h = Sha256.Hex(Make().Hash());
            Assert.That(Sha256.Hex(Make().Hash()), Is.EqualTo(h));
            var m = Make();
            m.Variants[1].Sweepers[0].PeriodTicks = -91;
            Assert.That(Sha256.Hex(m.Hash()), Is.Not.EqualTo(h), "a variant B def is part of the hash");
            m = Make();
            m.KillYMm = -9999;
            Assert.That(Sha256.Hex(m.Hash()), Is.Not.EqualTo(h));
            m = Make();
            m.Variants[0].Sweepers[0].Phase16 = 999;
            Assert.That(Sha256.Hex(m.Hash()), Is.EqualTo(h), "phase comes from the round seed, not the map");
            // SHA-256 known answer ("abc").
            Assert.That(Sha256.Hex(Sha256.Hash(System.Text.Encoding.ASCII.GetBytes("abc"))), Is.EqualTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
            Assert.That(Sha256.Hex(Sha256.Hash(new byte[0])), Is.EqualTo("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"));
            var big = new byte[1000];
            for (var i = 0; i < big.Length; i++)
                big[i] = (byte)i;
            using var sha = System.Security.Cryptography.SHA256.Create();
            Assert.That(Sha256.Hex(Sha256.Hash(big)), Is.EqualTo(Sha256.Hex(sha.ComputeHash(big))));
        }

        [Test]
        public void RoundMessages_RoundTrip()
        {
            var p = new RoundParams { MapId = 0, Variant = 1, Modifier = 3, RoundIndex = 1, HazardSeed = 0xDEADBEEF_01234567UL, RoundStartTick = 123456, QualifyCount = 8, PlayerCount = 12, IntroTicks = 90, CountdownTicks = 90, TimerTicks = 3300 };
            var hash = new byte[32];
            hash[0] = 0xAB;
            hash[31] = 0xCD;
            var buf = new byte[RoundMessage.Size];
            Assert.That(new RoundMessage { P = p, MapHash = hash }.Encode(buf), Is.EqualTo(59));
            Assert.That(RoundMessage.TryDecode(buf, out var m), Is.True);
            Assert.That(m.P.ToString(), Is.EqualTo(p.ToString()));
            Assert.That(m.MapHash, Is.EqualTo(hash));
            Assert.That(RoundMessage.TryDecode(buf.AsSpan(0, 58), out _), Is.False);

            var e = new RaceEvent { Seq = 77, Type = RaceEventType.Finished, Tick = 99999, Slot = 4, Arg = 3 };
            var eb = new byte[RaceEvent.Size];
            Assert.That(e.Encode(eb), Is.EqualTo(14));
            Assert.That(RaceEvent.TryDecode(eb, out var e2), Is.True);
            Assert.That(e2.ToString(), Is.EqualTo(e.ToString()));

            var st = new RaceStateMessage { LastEventSeq = 9, Phase = RoundPhase.Rush, PhaseStartTick = 100, RushEndTick = 3400, QualifyCount = 8, QualifiedCount = 2, FinisherCount = 2, FinisherSlots = new byte[] { 3, 7 }, FinisherTicks = new uint[] { 1500, 1600 } };
            var sb = new byte[st.Length];
            Assert.That(st.Encode(sb), Is.EqualTo(16 + 10));
            Assert.That(RaceStateMessage.TryDecode(sb, out var st2), Is.True);
            Assert.That((st2.Phase, st2.FinisherSlots[1], st2.FinisherTicks[1], st2.QualifiedCount), Is.EqualTo((RoundPhase.Rush, (byte)7, 1600u, (byte)2)));
            sb[15] = 13;
            Assert.That(RaceStateMessage.TryDecode(sb, out _), Is.False);
        }
    }
}
