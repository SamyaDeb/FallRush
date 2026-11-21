using System;
using FallRush.Shared;
using NUnit.Framework;

namespace FallRush.Tests
{
    /// <summary>
    /// G4.1c recorded crowd: 12 scripted players on flat ground in two rows running head-on through each other (straight
    /// and offset pairs), turning back every 3 s, hopping now and then; 900 ticks. <see cref="Digest"/> hashes every state
    /// of every tick. The expected digest was recorded on the movement_v2 build (before the bump code existed).
    /// </summary>
    internal static class BumpScenario
    {
        public static Vec3[] Spawns()
        {
            var s = new Vec3[12];
            for (var i = 0; i < 6; i++)
            {
                s[i] = new Vec3(-7f, 0.05f, -5f + 2f * i);
                s[i + 6] = new Vec3(7f, 0.05f, -5f + 2f * i + (i % 2 == 0 ? 0f : 0.35f)); // odd rows: glancing
            }

            return s;
        }

        public static InputCmd Input(int slot, uint tick, MotorState s)
        {
            var dir = slot < 6 ? 1 : -1;
            if ((tick / 90) % 2 == 1)
                dir = -dir;
            var c = new InputCmd { Tick = tick, MoveX = (sbyte)(127 * dir) };
            // Slots 2 and 9 run at half stick (slower), so "the slower one" differs from "the other one".
            if (slot == 2 || slot == 9)
                c.MoveX = (sbyte)(60 * dir);
            if ((tick + (uint)slot * 13) % 71 == 0)
                c.Buttons = InputCmd.JumpPressed | InputCmd.JumpHeld;
            return c;
        }

        public static SimCore Run(MovementConfig cfg, int ticks, out string digest)
        {
            var sim = new SimCore(cfg, new AnalyticWorld().Ground(), Spawns());
            for (var i = 0; i < 12; i++)
                sim.AddBot();
            sim.BotInput = Input;
            var w = new CanonicalWriter();
            for (var t = 0; t < ticks; t++)
            {
                sim.Step();
                for (var i = 0; i < 12; i++)
                {
                    ref readonly var s = ref sim.Slots[i].State;
                    w.I32(s.PosX).I32(s.PosY).I32(s.PosZ).I32(s.VelX).I32(s.VelY).I32(s.VelZ).I32(s.Flags).I32(s.LandTicks).I32(s.StunTicks).I32(s.Yaw);
                }
            }

            digest = Sha256.Hex(w.Sha256());
            return sim;
        }
    }

    /// <summary>G4.1c bump stagger (movement_v3 <c>bump</c>, flag off by default).</summary>
    public class BumpStaggerTests
    {
        /// <summary>Recorded with <see cref="BumpScenario"/> on the movement_v2 build (commit d80fa7d, before the bump code): 900 ticks, 284 pushes.</summary>
        public const string V2CrowdDigest = "c11cbb9fd88e20aad699d6e20c3c866daaca33344aa84dc5176c81d2df546a6a";

        [Test]
        public void Config_ShipsOff_TicksFromMs()
        {
            var c = MovementConfig.Default;
            Assert.That(c.BumpEnabled, Is.False, "shipped off: the user decides after playing");
            Assert.That(c.BumpMinClosingMms, Is.EqualTo(4000));
            Assert.That(c.BumpTicks, Is.EqualTo(8), "⌈250 ms · 30 / 1000⌉");
            var on = MovementConfig.WithBump(0, true);
            Assert.That(on.BumpEnabled, Is.True);
            Assert.That((on.MaxSpeed, on.Gravity, on.JumpSpeed, on.StumbleSpeedFactor), Is.EqualTo((c.MaxSpeed, c.Gravity, c.JumpSpeed, c.StumbleSpeedFactor)));
            Assert.That(MovementConfig.WithBump(0, false), Is.SameAs(c));
        }

        [Test]
        public void FlagOff_CrowdIsBitIdenticalToTheV2Recording()
        {
            var sim = BumpScenario.Run(MovementConfig.Default, 900, out var digest);
            Assert.That(digest, Is.EqualTo(V2CrowdDigest), "movement_v3 with the flag off = movement_v2, tick for tick");
            Assert.That(sim.BumpEvents, Is.EqualTo(0));
            Assert.That(sim.PushEvents, Is.EqualTo(284));
        }

        [Test]
        public void FlagOn_Deterministic_StaggersHappen_AndDifferFromOff()
        {
            var cfg = MovementConfig.WithBump(0, true);
            var a = BumpScenario.Run(cfg, 900, out var da);
            var b = BumpScenario.Run(cfg, 900, out var db);
            Assert.That(da, Is.EqualTo(db), "same inputs, same outcome (pairs in slot order)");
            Assert.That(a.BumpEvents, Is.EqualTo(b.BumpEvents));
            Assert.That(a.BumpEvents, Is.GreaterThan(0));
            Assert.That(da, Is.Not.EqualTo(V2CrowdDigest));
            TestContext.WriteLine($"bumps={a.BumpEvents} pushes={a.PushEvents}");
        }

        /// <summary>Two bots, placed and given velocities, then one step with the given sticks.</summary>
        private static SimCore Pair(MovementConfig cfg, Vec3 pa, short vxa, short vza, sbyte stickA, Vec3 pb, short vxb, short vzb, sbyte stickB, byte stunB = 0)
        {
            var sp = new Vec3[12];
            for (var i = 0; i < 12; i++)
                sp[i] = new Vec3(40f + 3f * i, 0.05f, 40f);
            var sim = new SimCore(cfg, new AnalyticWorld().Ground(), sp);
            sim.AddBot();
            sim.AddBot();
            sim.Place(0, pa, 0);
            sim.Place(1, pb, 0);
            sim.Slots[0].State.Flags = MotorState.Grounded;
            sim.Slots[1].State.Flags = MotorState.Grounded;
            sim.Slots[0].State.VelX = vxa;
            sim.Slots[0].State.VelZ = vza;
            sim.Slots[1].State.VelX = vxb;
            sim.Slots[1].State.VelZ = vzb;
            sim.Slots[1].State.StunTicks = stunB;
            sim.BotInput = (slot, tick, s) => new InputCmd { Tick = tick, MoveX = slot == 0 ? stickA : stickB };
            sim.Step();
            return sim;
        }

        private static bool Staggered(in MotorState s) => s.Has(MotorState.Stumbling) && s.LandTicks == 8;

        [Test]
        public void Rule_HeadOnTie_BothStagger_SlowerOnly_GlancingNone_StunnedExempt_FlagOffNone()
        {
            var on = MovementConfig.WithBump(0, true);
            // Head-on at 5 + 5 m/s, equal speeds: both.
            var s = Pair(on, new Vec3(-0.25f, 0f, 0f), 500, 0, 127, new Vec3(0.25f, 0f, 0f), -500, 0, -127);
            Assert.That(Staggered(in s.Slots[0].State) && Staggered(in s.Slots[1].State), Is.True, "tie: both");
            Assert.That(s.BumpEvents, Is.EqualTo(2));
            Assert.That(s.Slots[0].State.StunTicks, Is.EqualTo(0), "a stagger is not a stun");
            // 5 m/s into a player standing still: closing 5 m/s ≥ 4, only the slower (standing) one.
            s = Pair(on, new Vec3(-0.25f, 0f, 0f), 500, 0, 127, new Vec3(0.25f, 0f, 0f), 0, 0, 0);
            Assert.That(Staggered(in s.Slots[0].State), Is.False, "the faster runner keeps going");
            Assert.That(Staggered(in s.Slots[1].State), Is.True, "the slower one stumbles");
            // Below the threshold (3 m/s into a standing player): nothing.
            s = Pair(on, new Vec3(-0.25f, 0f, 0f), 300, 0, 0, new Vec3(0.25f, 0f, 0f), 0, 0, 0);
            Assert.That(s.BumpEvents, Is.EqualTo(0));
            // Glancing: running past side by side (contact normal ≈ ⟂ to the motion): closing ≈ 0.4 m/s.
            s = Pair(on, new Vec3(0f, 0f, 0f), 500, 0, 127, new Vec3(0.05f, 0f, 0.55f), 0, 0, 0);
            Assert.That(s.BumpEvents, Is.EqualTo(0), "glancing contacts do not stagger");
            // The slower one is stunned: exempt (and the faster one is not the slower): no stagger at all.
            s = Pair(on, new Vec3(-0.25f, 0f, 0f), 500, 0, 127, new Vec3(0.25f, 0f, 0f), 0, 0, 0, stunB: 10);
            Assert.That(s.BumpEvents, Is.EqualTo(0));
            Assert.That(s.Slots[1].State.Has(MotorState.Stumbling), Is.False);
            // Flag off: the same head-on never staggers.
            s = Pair(MovementConfig.Default, new Vec3(-0.25f, 0f, 0f), 500, 0, 127, new Vec3(0.25f, 0f, 0f), -500, 0, -127);
            Assert.That(s.BumpEvents, Is.EqualTo(0));
            Assert.That(s.Slots[0].State.Has(MotorState.Stumbling) || s.Slots[1].State.Has(MotorState.Stumbling), Is.False);
        }

        [Test]
        public void Stagger_UsesTheStumble_InputLive_SpeedCapped_EndsAfter8Ticks()
        {
            var cfg = MovementConfig.WithBump(0, true);
            var motor = new KinematicMotor(cfg);
            var w = new AnalyticWorld().Ground();
            var s = new MotorState { Flags = MotorState.Grounded, VelX = 500 };
            Assert.That(SimCore.Stagger(ref s, cfg), Is.True);
            Assert.That(SimCore.Stagger(ref s, cfg), Is.False, "already stumbling");
            var maxDuring = 0;
            for (var t = 1; t <= 12; t++)
            {
                motor.Step(ref s, new InputCmd { MoveX = 127, Tick = (uint)t }, cfg.Dt, w);
                if (t <= 8)
                {
                    maxDuring = Math.Max(maxDuring, Math.Abs((int)s.VelX));
                    Assert.That(s.StunTicks, Is.EqualTo(0));
                }

                Assert.That(s.Has(MotorState.Stumbling), Is.EqualTo(t < 8), $"t={t}");
            }

            Assert.That(maxDuring, Is.LessThan(500), "slowed while staggered");
            Assert.That(Math.Abs((int)s.VelX), Is.GreaterThan(maxDuring), "speed comes back after the stagger");
            var dive = new MotorState { Flags = MotorState.Diving };
            Assert.That(SimCore.Stagger(ref dive, cfg), Is.False, "diving players are exempt");
            var imm = new MotorState { ImmunityTicks = 3 };
            Assert.That(SimCore.Stagger(ref imm, cfg), Is.False, "immune players are exempt");
        }
    }
}
