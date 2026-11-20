using System;
using System.Collections.Generic;
using FallRush.Shared;
using NUnit.Framework;

namespace FallRush.Tests
{
    public class MotorTests
    {
        private static readonly MovementConfig Cfg = MovementConfig.Default;
        private static float Dt => Cfg.Dt;

        private static InputCmd Cmd(int mx, int my, byte buttons = 0, uint seq = 0) =>
            new InputCmd { Seq = seq, Tick = seq, MoveX = (sbyte)mx, MoveY = (sbyte)my, Buttons = buttons };

        private static MotorState At(float x, float y, float z) => new MotorState { Position = new Vec3(x, y, z) };

        /// <summary>Drops onto the ground and stands still until grounded.</summary>
        private static MotorState Settle(KinematicMotor m, IMotorWorld w, MotorState s)
        {
            for (var i = 0; i < 60 && !s.Has(MotorState.Grounded); i++)
                m.Step(ref s, Cmd(0, 0), Dt, w);
            for (var i = 0; i < 5; i++)
                m.Step(ref s, Cmd(0, 0), Dt, w);
            Assert.That(s.Has(MotorState.Grounded), Is.True, "should settle on the ground");
            return s;
        }

        private static float HSpeed(in MotorState s) => (float)Math.Sqrt(s.VelX * s.VelX + s.VelZ * s.VelZ) * 0.01f;

        // ------------------------------------------------------------------ run

        [TestCase(0, 127)]
        [TestCase(127, 127)]   // diagonal must be clamped to length 1
        [TestCase(-90, 90)]
        public void MaxSpeed_ReachedAndNeverExceeded(int mx, int my)
        {
            var w = new AnalyticWorld().Ground();
            var m = new KinematicMotor(Cfg);
            var s = Settle(m, w, At(0, 0.05f, 0));
            var reachedAt = -1;
            for (var t = 0; t < 90; t++)
            {
                m.Step(ref s, Cmd(mx, my), Dt, w);
                var v = HSpeed(s);
                Assert.That(v, Is.LessThanOrEqualTo(Cfg.MaxSpeed + 0.006f), $"tick {t}: {v} m/s");
                if (reachedAt < 0 && v >= Cfg.MaxSpeed - 0.006f)
                    reachedAt = t;
                Assert.That(s.Has(MotorState.Grounded), Is.True, $"tick {t} left the ground");
            }

            // 60 m/s² → 7 m/s in ~0.117 s = 4 ticks.
            Assert.That(reachedAt, Is.InRange(0, 5), "time to max speed (ticks)");
        }

        [Test]
        public void Deceleration_StopsWithoutInput()
        {
            var w = new AnalyticWorld().Ground();
            var m = new KinematicMotor(Cfg);
            var s = Settle(m, w, At(0, 0.05f, 0));
            for (var t = 0; t < 20; t++)
                m.Step(ref s, Cmd(0, 127), Dt, w);
            var stopTicks = 0;
            while (HSpeed(s) > 0f && stopTicks < 60)
            {
                m.Step(ref s, Cmd(0, 0), Dt, w);
                stopTicks++;
            }

            // 45 m/s² from 7 m/s → 0.156 s ≈ 5 ticks.
            Assert.That(stopTicks, Is.InRange(4, 6));
        }

        // ------------------------------------------------------------------ jump

        [Test]
        public void JumpApex_Is1600mm_Within2cm()
        {
            var w = new AnalyticWorld().Ground();
            var m = new KinematicMotor(Cfg);
            var s = Settle(m, w, At(0, 0.05f, 0));
            var startY = s.PosY;
            m.Step(ref s, Cmd(0, 0, InputCmd.JumpPressed | InputCmd.JumpHeld), Dt, w);
            Assert.That(m.Last.Jumped, Is.True);
            var maxY = s.PosY;
            for (var t = 0; t < 60; t++)
            {
                m.Step(ref s, Cmd(0, 0, InputCmd.JumpHeld), Dt, w);
                maxY = Math.Max(maxY, s.PosY);
            }

            var apex = (maxY - startY) / 1000f;
            Assert.That(apex, Is.EqualTo(1.6f).Within(0.02f), $"apex {apex:F4} m");
            Assert.That(s.Has(MotorState.Grounded), Is.True, "lands again");
        }

        [Test]
        public void NoDoubleJump()
        {
            var w = new AnalyticWorld().Ground();
            var m = new KinematicMotor(Cfg);
            var s = Settle(m, w, At(0, 0.05f, 0));
            m.Step(ref s, Cmd(0, 0, InputCmd.JumpPressed), Dt, w);
            var jumps = 1;
            for (var t = 0; t < 8; t++)
            {
                m.Step(ref s, Cmd(0, 0, InputCmd.JumpPressed), Dt, w);
                if (m.Last.Jumped)
                    jumps++;
            }

            Assert.That(jumps, Is.EqualTo(1));
        }

        /// <summary>Runs off a 1 m ledge; returns the first tick whose end state is airborne.</summary>
        private static List<InputCmd> LedgeRun(out int firstAirTick, out IMotorWorld world, out MotorState start)
        {
            var w = new AnalyticWorld().Ground(-20f).AddBox(new Vec3(-5, -1, -10), new Vec3(5, 1, 0));
            var m = new KinematicMotor(Cfg);
            start = Settle(m, w, At(0, 1.05f, -4));
            var s = start;
            var cmds = new List<InputCmd>();
            firstAirTick = -1;
            for (var t = 0; t < 60; t++)
            {
                var c = Cmd(0, 127);
                cmds.Add(c);
                m.Step(ref s, c, Dt, w);
                if (!s.Has(MotorState.Grounded))
                {
                    firstAirTick = t;
                    break;
                }
            }

            Assert.That(firstAirTick, Is.GreaterThan(0));
            world = w;
            return cmds;
        }

        [TestCase(1, true)]
        [TestCase(2, true)]
        [TestCase(3, true)]   // coyote = 3 ticks = 100 ms
        [TestCase(4, false)]
        [TestCase(6, false)]
        public void Coyote_JumpAllowedFor3TicksAfterLeavingGround(int ticksAfter, bool expectJump)
        {
            var run = LedgeRun(out var air, out var w, out var start);
            var m = new KinematicMotor(Cfg);
            var s = start;
            for (var t = 0; t <= air; t++)
                m.Step(ref s, run[t], Dt, w);
            for (var k = 1; k < ticksAfter; k++)
                m.Step(ref s, Cmd(0, 127), Dt, w);
            m.Step(ref s, Cmd(0, 127, InputCmd.JumpPressed), Dt, w);
            Assert.That(m.Last.Jumped, Is.EqualTo(expectJump));
        }

        [TestCase(0, true)]
        [TestCase(1, true)]
        [TestCase(2, true)]
        [TestCase(3, true)]   // buffer = 4 ticks (120 ms → 3.6, rounded up): presses up to 3 ticks before the landing tick
        [TestCase(4, false)]
        [TestCase(6, false)]
        public void JumpBuffer_PressShortlyBeforeLanding_Jumps(int ticksBeforeLanding, bool expectJump)
        {
            var w = new AnalyticWorld().Ground();
            var m = new KinematicMotor(Cfg);
            var s0 = At(0, 3f, 0);

            // Find the landing tick L (first tick whose end state is grounded).
            var probe = s0;
            var landing = -1;
            for (var t = 0; t < 120 && landing < 0; t++)
            {
                m.Step(ref probe, Cmd(0, 0), Dt, w);
                if (probe.Has(MotorState.Grounded))
                    landing = t;
            }

            Assert.That(landing, Is.GreaterThan(6));
            var s = s0;
            var jumped = false;
            for (var t = 0; t < landing + 10; t++)
            {
                var press = t == landing - ticksBeforeLanding;
                m.Step(ref s, Cmd(0, 0, press ? InputCmd.JumpPressed : (byte)0), Dt, w);
                if (m.Last.Jumped)
                {
                    jumped = true;
                    Assert.That(t, Is.EqualTo(landing + 1), "buffered jump fires on the first grounded tick");
                }
            }

            Assert.That(jumped, Is.EqualTo(expectJump));
        }

        // ------------------------------------------------------------------ dive

        [Test]
        public void Dive_Impulse_And_Cooldown1s()
        {
            var w = new AnalyticWorld().Ground();
            var m = new KinematicMotor(Cfg);
            var s = Settle(m, w, At(0, 0.05f, 0));
            m.Step(ref s, Cmd(0, 127, InputCmd.DivePressed), Dt, w);
            Assert.That(m.Last.Dived, Is.True);
            Assert.That(HSpeed(s), Is.EqualTo(Cfg.DiveHorizontal).Within(0.01f));
            Assert.That(s.Has(MotorState.Diving), Is.True);

            var nextDive = -1;
            for (var t = 1; t <= 40 && nextDive < 0; t++)
            {
                m.Step(ref s, Cmd(0, 127, InputCmd.DivePressed), Dt, w);
                if (m.Last.Dived)
                    nextDive = t;
            }

            Assert.That(nextDive, Is.EqualTo(Cfg.DiveCooldownTicks), "second dive exactly 30 ticks (1.0 s) later");
        }

        [Test]
        public void Dive_WithoutMoveInput_UsesCameraYaw()
        {
            var w = new AnalyticWorld().Ground();
            var m = new KinematicMotor(Cfg);
            var s = Settle(m, w, At(0, 0.05f, 0));
            var c = Cmd(0, 0, InputCmd.DivePressed);
            c.CamYaw = 64 << 8; // +X
            m.Step(ref s, c, Dt, w);
            Assert.That(s.VelX, Is.EqualTo(900).Within(1));
            Assert.That(s.VelZ, Is.EqualTo(0).Within(1));
        }

        /// <summary>Dives and returns the tick of touch-down.</summary>
        private static int DiveAndLand(KinematicMotor m, IMotorWorld w, ref MotorState s)
        {
            m.Step(ref s, Cmd(0, 127, InputCmd.DivePressed), Dt, w);
            Assert.That(m.Last.Dived, Is.True);
            for (var t = 1; t < 60; t++)
            {
                m.Step(ref s, Cmd(0, 127), Dt, w);
                if (m.Last.Landed)
                    return t;
            }

            Assert.Fail("dive never landed");
            return -1;
        }

        [TestCase(1, true)]
        [TestCase(3, true)]
        [TestCase(5, true)]    // window = 5 ticks (150 ms)
        [TestCase(6, false)]
        [TestCase(9, false)]
        public void DiveLanding_JumpCancelWindow(int ticksAfterLanding, bool expectCancel)
        {
            var w = new AnalyticWorld().Ground();
            var m = new KinematicMotor(Cfg);
            var s = Settle(m, w, At(0, 0.05f, 0));
            DiveAndLand(m, w, ref s);
            Assert.That(s.Has(MotorState.DiveLanding), Is.True);
            Assert.That(s.LandTicks, Is.EqualTo(Cfg.DiveLandStunTicks));

            for (var k = 1; k < ticksAfterLanding; k++)
                m.Step(ref s, Cmd(0, 127), Dt, w);
            m.Step(ref s, Cmd(0, 127, InputCmd.JumpPressed), Dt, w);
            Assert.That(m.Last.Jumped, Is.EqualTo(expectCancel));
            Assert.That(m.Last.JumpCancelled, Is.EqualTo(expectCancel));
        }

        [Test]
        public void DiveLanding_StunsFor11Ticks_ThenControlReturns()
        {
            var w = new AnalyticWorld().Ground();
            var m = new KinematicMotor(Cfg);
            var s = Settle(m, w, At(0, 0.05f, 0));
            DiveAndLand(m, w, ref s);
            for (var k = 1; k <= Cfg.DiveLandStunTicks; k++)
            {
                m.Step(ref s, Cmd(0, 127, k > Cfg.DiveCancelWindowTicks ? InputCmd.JumpPressed : (byte)0), Dt, w);
                Assert.That(m.Last.Jumped, Is.False, $"tick {k} after landing is still stunned");
            }

            Assert.That(s.Has(MotorState.DiveLanding), Is.False);
            m.Step(ref s, Cmd(0, 127, InputCmd.JumpPressed), Dt, w);
            Assert.That(m.Last.Jumped, Is.True, "control returns after 0.35 s");
        }

        // ------------------------------------------------------------------ slopes and steps

        private static float ClimbRamp(float degrees)
        {
            var w = new AnalyticWorld().Ground().RampZ(0f, degrees);
            var m = new KinematicMotor(Cfg);
            var s = Settle(m, w, At(0, 0.05f, -2f));
            for (var t = 0; t < 60; t++)
                m.Step(ref s, Cmd(0, 127), Dt, w);
            return s.PosY / 1000f;
        }

        [TestCase(15f)]
        [TestCase(30f)]
        [TestCase(44f)]
        public void Slope_WalkableUpTo45(float deg) => Assert.That(ClimbRamp(deg), Is.GreaterThan(1.0f));

        [TestCase(50f)]
        [TestCase(60f)]
        public void Slope_Over45_Blocks(float deg) => Assert.That(ClimbRamp(deg), Is.LessThan(0.15f));

        private static MotorState RunAtStep(float height)
        {
            var w = new AnalyticWorld().Ground().AddBox(new Vec3(-3, -1, 1), new Vec3(3, height, 5));
            var m = new KinematicMotor(Cfg);
            var s = Settle(m, w, At(0, 0.05f, -1f));
            for (var t = 0; t < 25; t++)
                m.Step(ref s, Cmd(0, 127), Dt, w);
            return s;
        }

        [Test]
        public void Step_0p2m_IsClimbed()
        {
            var s = RunAtStep(0.2f);
            Assert.That(s.PosY / 1000f, Is.EqualTo(0.2f + Cfg.Skin).Within(0.005f));
            Assert.That(s.PosZ / 1000f, Is.GreaterThan(1.5f));
            Assert.That(s.Has(MotorState.Grounded), Is.True);
        }

        [Test]
        public void Step_0p3m_Blocks()
        {
            var s = RunAtStep(0.3f);
            Assert.That(s.PosY / 1000f, Is.LessThan(0.05f));
            Assert.That(s.PosZ / 1000f, Is.LessThan(1.0f));
        }

        [Test]
        public void Wall_StopsAndSlides()
        {
            // Running diagonally into a wall at z = 2 keeps the X component.
            var w = new AnalyticWorld().Ground().AddBox(new Vec3(-50, -1, 2), new Vec3(50, 3, 3));
            var m = new KinematicMotor(Cfg);
            var s = Settle(m, w, At(0, 0.05f, 0));
            for (var t = 0; t < 30; t++)
                m.Step(ref s, Cmd(90, 90), Dt, w);
            Assert.That(s.PosZ / 1000f, Is.LessThan(2f - Cfg.Radius + 0.001f));
            Assert.That(s.PosX / 1000f, Is.GreaterThan(2f));
        }

        // ------------------------------------------------------------------ determinism + quantisation

        private static List<InputCmd> RandomInputLog(int seed, int n)
        {
            var rng = new Random(seed);
            var log = new List<InputCmd>(n);
            int mx = 0, my = 0;
            for (var i = 0; i < n; i++)
            {
                if (rng.Next(10) == 0)
                {
                    mx = rng.Next(-127, 128);
                    my = rng.Next(-127, 128);
                }

                byte b = 0;
                if (rng.Next(12) == 0)
                    b |= InputCmd.JumpPressed;
                if (rng.Next(40) == 0)
                    b |= InputCmd.DivePressed;
                var c = new InputCmd { Seq = (uint)i + 1, Tick = (uint)i + 1, MoveX = (sbyte)mx, MoveY = (sbyte)my, Buttons = b, CamYaw = (ushort)rng.Next(65536) };
                InputSanitizer.Sanitize(ref c);
                log.Add(c);
            }

            return log;
        }

        private static AnalyticWorld Course() => new AnalyticWorld()
            .Ground()
            .AddBox(new Vec3(-3, -1, 4), new Vec3(3, 0.2f, 8))
            .AddBox(new Vec3(5, -1, -6), new Vec3(9, 1.5f, 6))
            .AddBox(new Vec3(-12, -1, -12), new Vec3(12, 3, -11))
            .AddBox(new Vec3(-12, -1, 11), new Vec3(12, 3, 12));

        [Test]
        public void Determinism_SameInputLog_BitIdenticalStates()
        {
            var log = RandomInputLog(1234, 1800);
            var a = new KinematicMotor(Cfg);
            var b = new KinematicMotor(Cfg);
            var wa = Course();
            var wb = Course();
            var sa = At(0, 0.5f, 0);
            var sb = At(0, 0.5f, 0);
            for (var i = 0; i < log.Count; i++)
            {
                a.Step(ref sa, log[i], Dt, wa);
                b.Step(ref sb, log[i], Dt, wb);
                Assert.That(sa, Is.EqualTo(sb), $"diverged at tick {i}");
            }
        }

        [Test]
        public void Replay_FromAnyRecordedState_Reproduces()
        {
            // The client rewinds to an authoritative state and replays; that must reproduce the original run exactly.
            var log = RandomInputLog(99, 600);
            var m = new KinematicMotor(Cfg);
            var w = Course();
            var history = new MotorState[log.Count + 1];
            var s = At(0, 0.5f, 0);
            history[0] = s;
            for (var i = 0; i < log.Count; i++)
            {
                m.Step(ref s, log[i], Dt, w);
                history[i + 1] = s;
            }

            for (var from = 0; from < log.Count; from += 37)
            {
                var r = history[from];
                for (var i = from; i < Math.Min(from + 10, log.Count); i++)
                    m.Step(ref r, log[i], Dt, w);
                Assert.That(r, Is.EqualTo(history[Math.Min(from + 10, log.Count)]), $"replay from {from}");
            }
        }

        [Test]
        public void Quantisation_RoundTrip_IsStable()
        {
            var rng = new Random(7);
            for (var i = 0; i < 10000; i++)
            {
                var p = new Vec3((float)(rng.NextDouble() * 1000 - 500), (float)(rng.NextDouble() * 100 - 50), (float)(rng.NextDouble() * 1000 - 500));
                var v = new Vec3((float)(rng.NextDouble() * 60 - 30), (float)(rng.NextDouble() * 60 - 30), (float)(rng.NextDouble() * 60 - 30));
                var s = new MotorState { Position = p, Velocity = v };
                var again = new MotorState { Position = s.Position, Velocity = s.Velocity };
                Assert.That(again.PosX, Is.EqualTo(s.PosX));
                Assert.That(again.PosY, Is.EqualTo(s.PosY));
                Assert.That(again.PosZ, Is.EqualTo(s.PosZ));
                Assert.That(again.VelX, Is.EqualTo(s.VelX));
                Assert.That(again.VelY, Is.EqualTo(s.VelY));
                Assert.That(again.VelZ, Is.EqualTo(s.VelZ));
                Assert.That(Math.Abs(s.PosX / 1000.0 - p.X), Is.LessThanOrEqualTo(0.0005 + 1e-4));
                Assert.That(Math.Abs(s.VelY / 100.0 - v.Y), Is.LessThanOrEqualTo(0.005 + 1e-4));
            }

            Assert.That(MotorState.QuantizeMm(0.0005f), Is.EqualTo(1));
            Assert.That(MotorState.QuantizeMm(-0.0005f), Is.EqualTo(-1));
            Assert.That(MotorState.QuantizeCm(1000f), Is.EqualTo(short.MaxValue));
        }

        [Test]
        public void Yaw256_TableAndNearestDirection()
        {
            Assert.That(Yaw256.SinQ(0), Is.EqualTo(0));
            Assert.That(Yaw256.SinQ(64), Is.EqualTo(32767));
            Assert.That(Yaw256.CosQ(0), Is.EqualTo(32767));
            Assert.That(Yaw256.FromVector(0, 127, 9), Is.EqualTo(0));
            Assert.That(Yaw256.FromVector(127, 0, 9), Is.EqualTo(64));
            Assert.That(Yaw256.FromVector(0, -127, 9), Is.EqualTo(128));
            Assert.That(Yaw256.FromVector(-127, 0, 9), Is.EqualTo(192));
            Assert.That(Yaw256.FromVector(90, 90, 9), Is.EqualTo(32));
            Assert.That(Yaw256.FromVector(0, 0, 9), Is.EqualTo(9));
        }
    }
}
