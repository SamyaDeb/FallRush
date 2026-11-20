using System;
using System.Collections.Generic;
using FallRush.Net;
using FallRush.Shared;
using NUnit.Framework;

namespace FallRush.Tests
{
    /// <summary>M2b: deterministic hazards, platform carry, client prediction against a server twin, interpolation.</summary>
    public class HazardAndPredictionTests
    {
        private static readonly MovementConfig Cfg = MovementConfig.Default;

        // Sweeper across the z = 0 lane (pivot 3 m off it), platform floating at top y = 2 east of the lane.
        private static SweeperDef Sweeper() => new SweeperDef
        {
            PivotXMm = 0, PivotYMm = 0, PivotZMm = 3000, ArmMm = 4500, BarRadiusMm = 150, BarHeightMm = 450,
            PeriodTicks = 90, Phase16 = 0, KnockbackCms = 900, KnockUpCms = 500, StunTicks = 15, ImmunityTicks = 30,
        };

        private static PlatformDef Platform() => new PlatformDef
        {
            CenterXMm = 20000, CenterYMm = 1750, CenterZMm = 0, HalfXMm = 1500, HalfYMm = 250, HalfZMm = 1500,
            TravelXMm = 2000, PeriodTicks = 90, Phase16 = HazardMath.PhaseFromSeed(2),
        };

        private static HazardSet Hazards() => new HazardSet(new[] { Sweeper() }, new[] { Platform() });

        // ------------------------------------------------------------------ HazardMath

        [Test]
        public void Sin16_MatchesMathSin()
        {
            var max = 0.0;
            for (var p = 0; p < HazardMath.PhaseOne; p++)
            {
                var e = Math.Abs(HazardMath.Sin16(p) - Math.Sin(p * 2.0 * Math.PI / HazardMath.PhaseOne));
                if (e > max)
                    max = e;
            }

            TestContext.WriteLine($"max |Sin16 − sin| = {max:E2}");
            Assert.That(max, Is.LessThan(4e-6));
            Assert.That(HazardMath.Sin16(0), Is.EqualTo(0f));
            Assert.That(HazardMath.Sin16(16384), Is.EqualTo(1f).Within(4e-6f));
            Assert.That(HazardMath.Cos16(0), Is.EqualTo(1f).Within(4e-6f));
        }

        [Test]
        public void Phase_IsPeriodic_AndSignReverses()
        {
            for (uint t = 0; t < 400; t++)
            {
                Assert.That(HazardMath.Phase(t, 90, 123), Is.EqualTo(HazardMath.Phase(t + 90, 90, 123)));
                var fwd = HazardMath.Phase(t, 90, 0);
                var back = HazardMath.Phase(t, -90, 0);
                Assert.That((fwd + back) & 0xFFFF, Is.EqualTo(0));
            }

            Assert.That(HazardMath.Phase(uint.MaxValue, 150, 0), Is.InRange(0, 65535));
            Assert.That(HazardMath.PhaseFromSeed(1), Is.Not.EqualTo(HazardMath.PhaseFromSeed(2)));
        }

        [Test]
        public void PlatformDeltas_SumToItsDisplacement_ExactlyInMm()
        {
            var d = Platform();
            HazardMath.PlatformCenterMm(in d, 0, out var x0, out _, out _);
            var sum = 0;
            for (uint t = 1; t <= 500; t++)
                sum += (int)Math.Round(HazardMath.PlatformDelta(in d, t).X * 1000f);
            HazardMath.PlatformCenterMm(in d, 500, out var x1, out _, out _);
            Assert.That(sum, Is.EqualTo(x1 - x0));
            // Max speed ≈ 2π·A/T = 2π·2 m / 3 s ≈ 4.19 m/s → ≤ 14 cm per tick.
            for (uint t = 1; t <= 90; t++)
                Assert.That(Math.Abs(HazardMath.PlatformDelta(in d, t).X), Is.LessThan(0.141f));
        }

        [Test]
        public void Sweeper_HitsAStandingPlayer_ButNotOneJumpingOverIt()
        {
            var d = Sweeper();
            var feet = new Vec3(0f, 0f, 0f);
            // The tick where the bar is most nearly overhead: the middle of the first run of contact ticks.
            int first = -1, last = -1;
            for (var t = 0; t < 90; t++)
            {
                var c = HazardMath.SweeperContact(in d, (uint)t, feet, Cfg.Radius, Cfg.Height, out _, out _);
                if (c && first < 0)
                    first = t;
                if (first >= 0 && !c)
                {
                    last = t - 1;
                    break;
                }
            }

            Assert.That(first, Is.GreaterThanOrEqualTo(0), "the bar passes over the lane once per half turn");
            var hitTick = (first + last) / 2;
            // Bar top is at 0.6 m: feet 0.65 m up clear it; feet 0.5 m up do not.
            Assert.That(HazardMath.SweeperContact(in d, (uint)hitTick, new Vec3(0f, 0.65f, 0f), Cfg.Radius, Cfg.Height, out _, out _), Is.False);
            Assert.That(HazardMath.SweeperContact(in d, (uint)hitTick, new Vec3(0f, 0.5f, 0f), Cfg.Radius, Cfg.Height, out _, out _), Is.True);
            // Far outside the arm: never.
            for (var t = 0; t < 90; t++)
                Assert.That(HazardMath.SweeperContact(in d, (uint)t, new Vec3(0f, 0f, 9f), Cfg.Radius, Cfg.Height, out _, out _), Is.False);
        }

        [Test]
        public void SweeperHit_KnocksBack_Stuns_AndImmunityPreventsAReHit()
        {
            var hz = Hazards();
            var d = Sweeper();
            uint tick = 0;
            while (!HazardMath.SweeperContact(in d, tick, Vec3.Zero, Cfg.Radius, Cfg.Height, out _, out _))
                tick++;
            var s = new MotorState { Position = Vec3.Zero, Flags = MotorState.Grounded, CoyoteTicks = 3 };
            Assert.That(hz.ApplySweepers(ref s, tick, Cfg), Is.True);
            Assert.That(s.StunTicks, Is.EqualTo(15));
            Assert.That(s.ImmunityTicks, Is.EqualTo(30));
            Assert.That(s.Has(MotorState.Grounded), Is.False);
            Assert.That(new Vec3(s.VelX, 0, s.VelZ).Magnitude, Is.EqualTo(900f).Within(2f));
            Assert.That(s.VelY, Is.EqualTo(500));
            Assert.That(hz.ApplySweepers(ref s, tick, Cfg), Is.False, "immune");

            // Through the motor: stunned → no input for 15 ticks; airborne first, then lands.
            var world = new AnalyticWorld().Ground();
            var motor = new KinematicMotor(Cfg);
            var cmd = new InputCmd { MoveY = 127, Buttons = InputCmd.JumpPressed | InputCmd.JumpHeld };
            var startStun = s.StunTicks;
            for (var i = 0; i < startStun; i++)
            {
                var before = s.Velocity;
                motor.Step(ref s, in cmd, Cfg.Dt, world);
                Assert.That(motor.Last.Jumped, Is.False, $"stunned tick {i}");
            }

            Assert.That(s.StunTicks, Is.EqualTo(0));
        }

        [Test]
        public void Motor_IsCarriedByTheMovingPlatform()
        {
            var hz = Hazards();
            var world = new AnalyticWorld().Ground(-5f).AddPlatforms(hz);
            var motor = new KinematicMotor(Cfg);
            var d = hz.Platforms[0];
            world.BeginTick(0);
            var c0 = HazardMath.PlatformCenter(in d, 0);
            var s = new MotorState { Position = new Vec3(c0.X + 0.5f, 2.02f, c0.Z) };
            var idle = new InputCmd();
            // Settle onto it.
            for (uint t = 1; t <= 10; t++)
            {
                world.BeginTick(t);
                motor.Step(ref s, in idle, Cfg.Dt, world);
            }

            Assert.That(s.Has(MotorState.Grounded), Is.True);
            Assert.That(s.Surface, Is.EqualTo(1), "standing on platform 0");
            var offset = s.Position.X - HazardMath.PlatformCenter(in d, 10).X;
            var carried = 0;
            for (uint t = 11; t <= 190; t++)
            {
                world.BeginTick(t);
                motor.Step(ref s, in idle, Cfg.Dt, world);
                if (motor.Last.Carried)
                    carried++;
                var rel = s.Position.X - HazardMath.PlatformCenter(in d, t).X;
                Assert.That(rel, Is.EqualTo(offset).Within(0.003f), $"tick {t}: rides with the platform");
                Assert.That(s.Has(MotorState.Grounded), Is.True, $"tick {t}");
                Assert.That(s.PosY, Is.EqualTo(2000 + (int)(Cfg.Skin * 1000)).Within(3), $"tick {t}: on top (feet rest on the 1 cm skin)");
            }

            Assert.That(carried, Is.GreaterThan(170));
        }

        // ------------------------------------------------------------------ prediction vs server twin

        private sealed class Twin
        {
            public readonly SimCore Server;
            public readonly ClientPrediction Client;
            public readonly int Slot;
            public readonly int Lead, DownDelay;
            private readonly Queue<(uint deliverAt, uint tick, PlayerState p, SnapHeader h)> _down = new Queue<(uint, uint, PlayerState, SnapHeader)>();
            private readonly PlayerState[] _players = new PlayerState[12];
            private uint _seq;

            public Twin(int lead, int downDelay, Vec3 spawn)
            {
                var hz = Hazards();
                var spawns = new Vec3[12];
                for (var i = 0; i < 12; i++)
                    spawns[i] = new Vec3(spawn.X, spawn.Y, spawn.Z + 30f * i); // other slots far away
                Server = new SimCore(Cfg, new AnalyticWorld().Ground().AddPlatforms(hz), spawns, hz);
                Slot = Server.AddHuman(7);
                Client = new ClientPrediction(Cfg, new AnalyticWorld().Ground().AddPlatforms(hz), hz);
                Lead = lead;
                DownDelay = downDelay;
            }

            /// <summary>One server tick k: the client samples tick k + lead, the server simulates k, snapshots every 2 ticks.</summary>
            public void Tick(uint k, Func<uint, InputCmd> input, bool dropThisCmd = false)
            {
                var cmd = input(k + (uint)Lead);
                cmd.Seq = ++_seq;
                cmd.Tick = k + (uint)Lead;
                InputSanitizer.Sanitize(ref cmd);
                Client.Predict(in cmd);
                if (!dropThisCmd)
                    Server.Input(Slot).Offer(cmd, Server.NextTick);
                Server.Step();
                if (Server.Tick % 2 == 0)
                {
                    var n = Server.WritePlayerStates(_players);
                    var h = Server.HeaderFor(Slot, n);
                    for (var i = 0; i < n; i++)
                        if (_players[i].Slot == Slot)
                            _down.Enqueue((Server.Tick + (uint)DownDelay, Server.Tick, _players[i], h));
                }

                while (_down.Count > 0 && _down.Peek().deliverAt <= Server.Tick)
                {
                    var m = _down.Dequeue();
                    Client.Reconcile(m.tick, in m.p, in m.h, m.tick * Cfg.Dt);
                }
            }
        }

        /// <summary>Runs back and forth along z = 0 through the sweeper (x −8 … +8), hopping now and then.</summary>
        private static InputCmd Course(uint t)
        {
            var c = new InputCmd { MoveX = (sbyte)((t / 75) % 2 == 0 ? 127 : -127) };
            if (t % 37 == 0)
                c.Buttons = InputCmd.JumpPressed | InputCmd.JumpHeld;
            if (t % 97 == 0)
                c.Buttons |= InputCmd.DivePressed | InputCmd.DiveHeld;
            return c;
        }

        [Test]
        public void Prediction_MatchesServerBitForBit_ThroughSweeperHits([Values(1, 3, 6)] int lead, [Values(2, 5)] int downDelay)
        {
            var twin = new Twin(lead, downDelay, new Vec3(-8f, 0.05f, 0f));
            for (uint k = 1; k <= 1800; k++)
                twin.Tick(k, Course);

            var p = twin.Client;
            TestContext.WriteLine($"snapshots={p.Snapshots} match={p.Matches} silent={p.SilentResyncs} corrections={p.Corrections} hazardMismatch={p.HazardMismatches} adopted={p.Adopted} serverHits={twin.Server.HazardHits} predictedHits={p.PredictedHits} replay max={p.MaxReplay}");
            if (p.Corrections > 0)
                TestContext.WriteLine($"last mismatch at {p.LastMismatchTick}:\n auth {p.LastMismatchAuth}\n pred {p.LastMismatchPredicted}");
            Assert.That(twin.Server.HazardHits, Is.GreaterThanOrEqualTo(3), "the course really goes through the sweeper");
            Assert.That(p.Corrections, Is.EqualTo(0));
            Assert.That(p.SilentResyncs, Is.EqualTo(0));
            Assert.That(p.HazardMismatches, Is.EqualTo(0));
            Assert.That(p.Matches, Is.EqualTo(p.Snapshots - p.Adopted));
            Assert.That(p.Adopted, Is.EqualTo(1), "only the first snapshot");
            Assert.That(p.MaxReplay, Is.LessThanOrEqualTo(lead + downDelay + 2));
        }

        [Test]
        public void Prediction_MatchesServer_WhileRidingThePlatform()
        {
            var d = Platform();
            var c = HazardMath.PlatformCenter(in d, 0);
            var twin = new Twin(3, 3, new Vec3(c.X, 2.05f, c.Z));
            // Ride idle for a while, then walk off the end (falls → respawn), proving carry + fall are predicted.
            InputCmd Ride(uint t) => t < 400 ? new InputCmd() : new InputCmd { MoveY = 127 };
            for (uint k = 1; k <= 600; k++)
                twin.Tick(k, Ride);
            var p = twin.Client;
            TestContext.WriteLine($"snapshots={p.Snapshots} match={p.Matches} corrections={p.Corrections} respawns={p.Respawns} adopted={p.Adopted}");
            Assert.That(p.Corrections, Is.EqualTo(0));
            Assert.That(p.SilentResyncs, Is.EqualTo(0));
        }

        [Test]
        public void ADroppedCommand_IsCorrected_ThenPredictionMatchesAgain()
        {
            var twin = new Twin(3, 3, new Vec3(-8f, 0.05f, 0f));
            // The jump press at tick 300 never reaches the server (it repeats the previous command, edges cleared).
            InputCmd Input(uint t) => t == 300 ? new InputCmd { MoveX = 127, Buttons = InputCmd.JumpPressed | InputCmd.JumpHeld } : new InputCmd { MoveX = 127 };
            for (uint k = 1; k <= 600; k++)
                twin.Tick(k, Input, dropThisCmd: k + 3 == 300);
            var p = twin.Client;
            TestContext.WriteLine($"corrections={p.Corrections} lastCorr={p.LastCorrection:F3} m maxReplay={p.MaxReplay} matches={p.Matches}/{p.Snapshots}");
            Assert.That(p.Corrections, Is.GreaterThanOrEqualTo(1));
            Assert.That(p.LastCorrection, Is.GreaterThan(0.05f));
            var matchesBefore = p.Matches;
            for (uint k = 601; k <= 700; k++)
                twin.Tick(k, Input);
            Assert.That(p.Matches - matchesBefore, Is.EqualTo(50), "every later snapshot matches");
            Assert.That(p.RenderOffset.Magnitude, Is.LessThan(SnapDistance()));
        }

        private static float SnapDistance() => ClientPrediction.SnapDistance;

        [Test]
        public void ServerPush_IsReportedOncePerSnapshot_InTheRecipientHeader()
        {
            var spawns = new Vec3[12];
            for (var i = 0; i < 12; i++)
                spawns[i] = new Vec3(i < 2 ? 0.1f * i : 5f * i, 0.05f, 0f); // slots 0 and 1 overlap
            var sim = new SimCore(Cfg, new AnalyticWorld().Ground(), spawns);
            var a = sim.AddHuman(1);
            var b = sim.AddHuman(2);
            sim.Step();
            Assert.That(sim.HeaderFor(a, 2).OwnEvents & SnapHeader.EventPushed, Is.Not.Zero);
            Assert.That(sim.HeaderFor(a, 2).OwnEvents, Is.Zero, "cleared once reported");
            Assert.That(sim.HeaderFor(b, 2).OwnEvents & SnapHeader.EventPushed, Is.Not.Zero, "per recipient");
            for (var t = 0; t < 30; t++)
                sim.Step();
            sim.HeaderFor(a, 2);
            sim.Step();
            Assert.That(sim.HeaderFor(a, 2).OwnEvents, Is.Zero, "separated: no more pushes");
        }

        [Test]
        public void Smoothing_BlendsTheOffsetOut_In120ms()
        {
            var p = new ClientPrediction(Cfg, new AnalyticWorld().Ground(), HazardSet.Empty) { RenderOffset = new Vec3(0.5f, 0f, 0f) };
            for (var i = 0; i < 7; i++)
                p.UpdateSmoothing(1f / 60f); // 117 ms
            Assert.That(p.RenderOffset.Magnitude, Is.LessThan(0.5f * 0.07f));
            Assert.That(p.RenderOffset.Magnitude, Is.GreaterThan(0f));
        }

        // ------------------------------------------------------------------ interpolation

        private static PlayerState[] One(int x) => new[] { new PlayerState { Slot = 3, PosX = x, VelX = 600, Flags = MotorState.Grounded } };

        [Test]
        public void Interpolation_Steady15Hz_Uses100ms_NoExtrapolation()
        {
            var it = new Interpolation(1.0 / 30);
            var now = 10.0;
            uint tick = 300;
            var frames = 0;
            for (var f = 0; f < 600; f++)
            {
                now += 1.0 / 60;
                if (f % 4 == 0)
                {
                    tick += 2;
                    it.Add(tick, One((int)(tick * 20)), now);
                }

                it.BeginFrame(now, 1f / 60);
                Assert.That(it.Sample(3, out var feet, out _, out _), Is.True);
                if (f > 120)
                {
                    frames++;
                    Assert.That(it.Lagging, Is.False);
                }
            }

            Assert.That(it.Delay, Is.EqualTo(0.1f).Within(0.002f));
            Assert.That(it.ExtrapolatedFrames, Is.LessThan(5));
        }

        [Test]
        public void Interpolation_Stall_ExtrapolatesThenFreezesAndFlagsLagging()
        {
            var it = new Interpolation(1.0 / 30);
            var now = 10.0;
            uint tick = 300;
            for (var f = 0; f < 240; f++)
            {
                now += 1.0 / 60;
                if (f % 4 == 0)
                {
                    tick += 2;
                    it.Add(tick, One((int)(tick * 20)), now);
                }

                it.BeginFrame(now, 1f / 60);
            }

            // No more snapshots: 100 ms of buffer, then ≤ 150 ms of extrapolation, then frozen + lagging.
            var sawExtrap = false;
            Vec3 frozen = default;
            for (var f = 0; f < 60; f++)
            {
                now += 1.0 / 60;
                it.BeginFrame(now, 1f / 60);
                it.Sample(3, out var feet, out _, out _);
                sawExtrap |= it.Extrapolating;
                if (it.Lagging)
                {
                    if (frozen.X == 0f)
                        frozen = feet;
                    Assert.That(feet.X, Is.EqualTo(frozen.X).Within(1e-4f), "frozen");
                }
            }

            Assert.That(sawExtrap, Is.True);
            Assert.That(it.Lagging, Is.True);
            Assert.That(it.LagEvents, Is.EqualTo(1));
        }

        [Test]
        public void Interpolation_Jitter_RaisesTheDelay_CappedAt150ms()
        {
            var it = new Interpolation(1.0 / 30);
            var rng = new Random(5);
            var now = 10.0;
            uint tick = 300;
            // Snapshot i is sent at ideal_i and arrives 0–120 ms late, in order (TCP).
            var ideal = now;
            var next = now;
            for (var f = 0; f < 1200; f++)
            {
                now += 1.0 / 60;
                while (next <= now)
                {
                    tick += 2;
                    it.Add(tick, One((int)(tick * 20)), now);
                    ideal += 2.0 / 30;
                    next = Math.Max(next, ideal + rng.NextDouble() * 0.12);
                }

                it.BeginFrame(now, 1f / 60);
            }

            TestContext.WriteLine($"delay={it.Delay * 1000:F0} ms target={it.TargetDelay * 1000:F0} ms lateP95={it.LatenessP95Ms:F0} ms");
            Assert.That(it.Delay, Is.GreaterThan(0.11f));
            Assert.That(it.Delay, Is.LessThanOrEqualTo(0.15f + 1e-4f));
        }
    }
}
