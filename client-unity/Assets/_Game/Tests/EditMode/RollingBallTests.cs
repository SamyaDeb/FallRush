using System;
using System.Collections.Generic;
using FallRush.Shared;
using NUnit.Framework;

namespace FallRush.Tests
{
    /// <summary>G4.1a <c>ROLLING_BALL</c>: pure pose, contact, telegraph, allowlist, knock through <see cref="PlayerStep"/>.</summary>
    public class RollingBallTests
    {
        private static MovementConfig Cfg => MovementConfig.Default;

        /// <summary>A lane rolling down −Z from z 30 m to z 0 at r 1 m (centre 1 m above the floor), 4 m/s, every 2 s.</summary>
        internal static RollingBallDef Lane(int phase16 = 0)
        {
            var d = RollingBallDef.Default(0, 1000, 30000, 0, 1000, 0);
            d.Phase16 = phase16;
            HazardMath.PrepareBall(ref d);
            return d;
        }

        private static HazardSet Set(params RollingBallDef[] balls) => new HazardSet(null, null) { RollingBalls = balls };

        [Test]
        public void Pose_IsPure_IntegerLerp_ReleaseAndDespawn()
        {
            var d = Lane(12345);
            Assert.That(d.LenMm, Is.EqualTo(30000));
            Assert.That(HazardMath.BallTravelTicks(in d), Is.EqualTo(225), "30 m at 4 m/s = 7.5 s");
            Assert.That(HazardMath.BallMaxAlive(in d), Is.EqualTo(4), "⌈225 / 60⌉");
            var off = HazardMath.BallPhaseOffset(in d);
            Assert.That(off, Is.EqualTo(12345 * 60 >> 16));
            // Ball k = 3 is released at t = 3·60 − off: exactly at the start there.
            var tk = 3 * 60 - off;
            Assert.That(HazardMath.BallPosMm(in d, tk, 0, out var x, out var y, out var z, out var k), Is.True);
            Assert.That((x, y, z, k), Is.EqualTo((0, 1000, 30000, 3L)));
            // 30 ticks later it has rolled 4 m (exact integer lerp).
            Assert.That(HazardMath.BallPosMm(in d, tk + 30, 0, out _, out _, out z, out k), Is.True);
            Assert.That((z, k), Is.EqualTo((26000, 3L)));
            // Same inputs, same output (no state between calls); its own release index stays the same while it rolls.
            for (var t = -400; t < 2000; t += 7)
                for (var j = 0; j < 4; j++)
                {
                    var a = HazardMath.BallPosMm(in d, t, j, out var ax, out var ay, out var az, out var ak);
                    var b = HazardMath.BallPosMm(in d, t, j, out var bx, out var by, out var bz, out var bk);
                    Assert.That((a, ax, ay, az, ak), Is.EqualTo((b, bx, by, bz, bk)));
                    if (a)
                        Assert.That(az, Is.InRange(0, 30000), "on the segment");
                }

            // The ball released at tk is gone after its travel (no collider, no contact past the end).
            var gone = tk + 225;
            for (var j = 0; j < 4; j++)
                if (HazardMath.BallPosMm(in d, gone, j, out _, out _, out _, out var kk))
                    Assert.That(kk, Is.Not.EqualTo(3L), "ball 3 despawned at the end");
            // Steady state: exactly ⌈travel/period⌉ or one fewer alive at any tick.
            for (var t = 0; t < 600; t++)
            {
                var n = 0;
                while (n < 8 && HazardMath.BallPosMm(in d, t, n, out _, out _, out _, out _))
                    n++;
                Assert.That(n, Is.InRange(3, 4), $"t={t}");
            }
        }

        [Test]
        public void Pose_RenderMatchesSim_ToAMillimetre()
        {
            var d = Lane(40000);
            for (var t = 0; t < 400; t++)
                for (var j = 0; j < 4; j++)
                {
                    var sim = HazardMath.BallPosMm(in d, t, j, out var x, out var y, out var z, out _);
                    var ren = HazardMath.BallRenderPos(in d, t, j, out var rx, out var ry, out var rz, out _);
                    Assert.That(ren, Is.EqualTo(sim), $"t={t} j={j}");
                    if (sim)
                        Assert.That(Math.Abs(rz * 1000 - z), Is.LessThanOrEqualTo(1.0), $"t={t} j={j}");
                }
        }

        [Test]
        public void Contact_CentreHitPushesDownTheLane_OffCentrePushesSideways_JumpingOverClears()
        {
            var d = Lane();
            Assert.That(HazardMath.BallPosMm(in d, 100, 0, out var bx, out var by, out var bz, out _), Is.True);
            // Standing right in front of the ball (it rolls toward −Z).
            Assert.That(HazardMath.RollingBallContactMm(in d, 100, 0, 0, bz - 1200, 310, 1000, out var px, out var pz), Is.True);
            Assert.That(pz, Is.EqualTo(-1f).Within(1e-5f));
            Assert.That(px, Is.EqualTo(0f).Within(1e-5f));
            // Off-centre to +X: pushed down the lane and toward +X (the lateral side the player is on).
            Assert.That(HazardMath.RollingBallContactMm(in d, 100, 900, 0, bz - 600, 310, 1000, out px, out pz), Is.True);
            Assert.That(pz, Is.LessThan(-0.85f));
            Assert.That(px, Is.GreaterThan(0.1f));
            Assert.That(px * px + pz * pz, Is.EqualTo(1f).Within(1e-4f));
            // Too far to the side, or feet above the ball's top + capsule radius (a jump over it): no contact.
            Assert.That(HazardMath.RollingBallContactMm(in d, 100, 1400, 0, bz, 310, 1000, out _, out _), Is.False);
            // The capsule's bottom sphere clears the ball once the feet are above its top (centre + r).
            Assert.That(HazardMath.RollingBallContactMm(in d, 100, 0, by + d.RadiusMm + 10, bz, 310, 1000, out _, out _), Is.False);
            Assert.That(HazardMath.RollingBallContactMm(in d, 100, 0, by + d.RadiusMm - 10, bz, 310, 1000, out _, out _), Is.True);
        }

        [Test]
        public void Telegraph_RisesBeforeEachRelease_ZeroOtherwise()
        {
            var d = Lane(20000);
            var off = HazardMath.BallPhaseOffset(in d);
            var release = 5 * 60 - off;
            Assert.That(HazardMath.RollingBallTelegraph1000(in d, release - 1), Is.EqualTo(1000), "full on the tick before");
            Assert.That(HazardMath.RollingBallTelegraph1000(in d, release - 15), Is.EqualTo(1000 / 15));
            Assert.That(HazardMath.RollingBallTelegraph1000(in d, release - 16), Is.EqualTo(0));
            Assert.That(HazardMath.RollingBallTelegraph1000(in d, release), Is.EqualTo(0), "released: next one is a period away");
            var prev = -1;
            for (var t = release - 15; t < release; t++)
            {
                var w = HazardMath.RollingBallTelegraph1000(in d, t);
                Assert.That(w, Is.GreaterThan(prev));
                Assert.That(HazardMath.RollingBallTelegraph(in d, t) * 1000f, Is.EqualTo(w).Within(1f));
                prev = w;
            }
        }

        [Test]
        public void PlayerStep_KnockHard_ThenImmune_NoSecondHit()
        {
            var d = Lane();
            var hz = Set(d);
            var w = new AnalyticWorld().Ground().AddHazards(hz);
            var motor = new KinematicMotor(Cfg);
            // Stand still in the lane at z = 10 m and wait for the next ball.
            var s = new MotorState { PosX = 0, PosY = 0, PosZ = 10000, Flags = MotorState.Grounded };
            var hits = new List<int>();
            for (var t = 1; t <= 600; t++)
            {
                w.BeginTick((uint)t);
                var r = PlayerStep.Run(motor, ref s, new InputCmd { Tick = (uint)t }, (uint)t, w, hz);
                if ((r & PlayerStep.Result.HazardHit) != 0)
                {
                    hits.Add(t);
                    Assert.That(s.StunTicks, Is.EqualTo(KnockProfile.HardStunTicks));
                    Assert.That(s.ImmunityTicks, Is.EqualTo(KnockProfile.ImmunityFor(KnockProfile.HardStunTicks)));
                    Assert.That(s.VelZ, Is.EqualTo(-KnockProfile.HardPushCms), "down the lane");
                    Assert.That(s.VelY, Is.EqualTo(KnockProfile.HardUpCms));
                }

                if (s.Has(MotorState.Respawning))
                    break;
                // Walk back to the spot between hits (the test is about the hit rules, not the knockback distance).
                if (s.StunTicks == 0 && s.Has(MotorState.Grounded))
                {
                    s.PosX = 0;
                    s.PosZ = 10000;
                }
            }

            Assert.That(hits.Count, Is.GreaterThanOrEqualTo(3), "a ball every 2 s");
            for (var i = 1; i < hits.Count; i++)
                Assert.That(hits[i] - hits[i - 1], Is.GreaterThanOrEqualTo(KnockProfile.ImmunityFor(KnockProfile.HardStunTicks)), "immune between hits");
        }

        [Test]
        public void Allowlist_Ranges_SpacingAndSpawnVolume()
        {
            var ok = Lane();
            var e = HazardAllowlist.Validate(Set(ok));
            Assert.That(e, Is.Empty, string.Join("; ", e));
            var bad = ok;
            bad.RadiusMm = 500;
            Assert.That(HazardAllowlist.Validate(Set(bad)), Has.Some.Contains("radiusMm"));
            bad = ok;
            bad.SpeedMmS = 8000;
            Assert.That(HazardAllowlist.Validate(Set(bad)), Has.Some.Contains("speedMmS"));
            bad = ok;
            bad.PeriodTicks = 30; // 4 m/s × 1 s = 4 m ≥ 2 m: fine
            Assert.That(HazardAllowlist.Validate(Set(bad)), Is.Empty);
            bad.SpeedMmS = 2000;
            bad.RadiusMm = 1200; // 2 m/s × 1 s = 2 m < 2.4 m diameter: overlapping balls
            Assert.That(HazardAllowlist.Validate(Set(bad)), Has.Some.Contains("spacingMm"));
            // Path starting on a spawn: refused.
            var spawns = new[] { 0, 0, 30000, 5000, 0, 0 };
            Assert.That(HazardAllowlist.Validate(Set(ok), spawns), Has.Some.Contains("spawn volume 0"));
            Assert.That(HazardAllowlist.Validate(Set(ok), new[] { 5000, 0, 0 }), Is.Empty);
        }

        [Test]
        public void Hash_OnlyWhenPresent_NoBallsEncodesLikeBefore()
        {
            var empty = new HazardSet(null, null);
            Assert.That(empty.HasG4, Is.False);
            var w0 = new CanonicalWriter();
            HazardEncoding.Write(w0, empty, true);
            // Without G4 defs the encoding is the M3 layout: 8 kit counts of 0, nothing else.
            Assert.That(w0.Length, Is.EqualTo(8 * 4));
            var w1 = new CanonicalWriter();
            HazardEncoding.Write(w1, Set(Lane(1234)), true);
            var w2 = new CanonicalWriter();
            HazardEncoding.Write(w2, Set(Lane(999)), true);
            Assert.That(w1.Bytes.ToArray(), Is.EqualTo(w2.Bytes.ToArray()), "the seeded phase is not hashed");
            Assert.That(w1.Length, Is.GreaterThan(w0.Length));
        }
    }
}
