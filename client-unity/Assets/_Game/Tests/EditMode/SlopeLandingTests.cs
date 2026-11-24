using FallRush.Shared;
using NUnit.Framework;

namespace FallRush.Tests
{
    /// <summary>
    /// OC-56: a jump that comes down on an up-slope lands (opt-in worlds: Capsule Run, Goo Tower, Sandbox). Without the opt-in
    /// the old rush-core rule stays (the on-chain Final): sliding onto the slope turns the fall into a climb and the capsule
    /// skates above it without landing.
    /// </summary>
    public class SlopeLandingTests
    {
        private static readonly MovementConfig Cfg = MovementConfig.Default;

        private static int TicksToLand(bool slopeLanding, float degrees)
        {
            var w = new AnalyticWorld { SlopeLanding = slopeLanding }.RampZ(-20f, degrees);
            var m = new KinematicMotor(Cfg);
            var s = new MotorState { Position = new Vec3(0f, 20f * Mathf.Tan(degrees) + 0.05f, 0f) };
            for (var i = 0; i < 30 && !s.Has(MotorState.Grounded); i++)
                m.Step(ref s, new InputCmd(), Cfg.Dt, w);
            Assert.That(s.Has(MotorState.Grounded), Is.True, "settles on the ramp");
            uint t = 1;
            for (var i = 0; i < 8; i++, t++)
                m.Step(ref s, new InputCmd { Seq = t, Tick = t, MoveY = 127 }, Cfg.Dt, w);
            m.Step(ref s, new InputCmd { Seq = t, Tick = t, MoveY = 127, Buttons = InputCmd.JumpPressed | InputCmd.JumpHeld }, Cfg.Dt, w);
            t++;
            Assert.That(s.Has(MotorState.Grounded), Is.False, "jumped");
            for (var i = 1; i <= 45; i++, t++)
            {
                m.Step(ref s, new InputCmd { Seq = t, Tick = t, MoveY = 127, Buttons = InputCmd.JumpHeld }, Cfg.Dt, w);
                if (s.Has(MotorState.Grounded))
                    return i;
            }

            return -1;
        }

        [TestCase(8f)]
        [TestCase(11f)]
        [TestCase(20f)]
        [TestCase(27f)]
        public void HopUpSlope_Lands_WhenOptedIn(float degrees)
        {
            var n = TicksToLand(true, degrees);
            Assert.That(n, Is.InRange(5, 22), $"landed after {n} ticks on {degrees}° (flat hop ≈ 19)");
        }

        [Test]
        public void HopUpSlope_OldRule_Unchanged_WithoutOptIn()
        {
            Assert.That(TicksToLand(false, 8f), Is.EqualTo(-1), "rush-core rule (on-chain Final) kept: no landing on an up-slope");
        }

        private static class Mathf
        {
            public static float Tan(float deg) => (float)System.Math.Tan(deg * System.Math.PI / 180.0);
        }
    }
}
