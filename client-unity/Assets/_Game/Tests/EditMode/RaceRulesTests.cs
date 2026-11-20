using System;
using FallRush.Shared;
using NUnit.Framework;

namespace FallRush.Tests
{
    /// <summary>M3b: RaceRules (engine-free) with an analytic test course: phases, ordered checkpoints, reachability, respawn, finish/qualify, timer ranking, determinism.</summary>
    public class RaceRulesTests
    {
        private static readonly MovementConfig Cfg = MovementConfig.Default;

        // Straight 10 m wide strip z ∈ [−12, 80] (void outside |x| > 5), checkpoints at z = 20 and 40, finish at z = 60.
        private static CheckpointDef Cp(int z, int courseMm, int respawnZ) => new CheckpointDef
        {
            CenterXMm = 0, CenterYMm = 1500, CenterZMm = z, HalfXMm = 5000, HalfYMm = 2000, HalfZMm = 1000, CourseMm = courseMm,
            RespawnMm = new[] { -3000, 50, respawnZ, 0, 0, 50, respawnZ, 0, 3000, 50, respawnZ, 0 },
        };

        private static readonly CheckpointDef[] Cps = { Cp(20000, 28000, 23000), Cp(40000, 48000, 43000), Cp(60000, 68000, 63000) };
        private static readonly int[] Line = { 0, 0, -8000, 0, 0, 80000 };

        private static HazardSet Course()
        {
            var gate = new GateDoorDef { CenterXMm = 0, CenterYMm = 1250, CenterZMm = 2000, HalfXMm = 5000, HalfYMm = 1250, HalfZMm = 200, LiftMm = 2800, OpeningTicks = 10, Mode = 1 };
            return new HazardSet(null, null, null, null, null, new[] { gate }, null, null) { KillYMm = -10000, RoundStartTick = 0x7FFF0000u };
        }

        private static AnalyticWorld World(HazardSet hz) =>
            new AnalyticWorld().AddBox(new Vec3(-5f, -1f, -12f), new Vec3(5f, 0f, 80f)).AddHazards(hz);

        private static Vec3[] Spawns()
        {
            var s = new Vec3[12];
            for (var i = 0; i < 12; i++)
                s[i] = new Vec3(-4f + (i % 6) * 1.6f, 0.05f, -8f + (i / 6) * 2f);
            return s;
        }

        private static BotRoute[] Routes() => new[]
        {
            new BotRoute { Section = 0, Name = "s0", Points = new[] { W(0, 22000) } },
            new BotRoute { Section = 1, Name = "s1", Points = new[] { W(0, 42000) } },
            new BotRoute { Section = 2, Name = "s2", Points = new[] { W(0, 62000) } },
        };

        private static Waypoint W(int x, int z) => new Waypoint { XMm = x, ZMm = z, RadiusMm = 800 };

        private sealed class Race
        {
            public readonly SimCore Sim;
            public readonly RaceRules Rules;
            public readonly HazardSet Hz;
            public readonly WaypointBot[] Bots = new WaypointBot[12];

            public Race(int bots, uint timer, uint seed = 1)
            {
                Hz = Course();
                Sim = new SimCore(Cfg, World(Hz), Spawns(), Hz);
                Rules = new RaceRules(Cps, Line, timer) { IntroTicks = 30, CountdownTicks = 60 };
                Sim.Rules = Rules;
                for (var i = 0; i < bots; i++)
                {
                    Sim.AddBot();
                    Bots[i] = new WaypointBot(i, Routes(), Hz, Cfg, seed);
                }

                Sim.BotInput = (slot, tick, s) => Bots[slot].Next(tick, in s, Rules.Racers[slot].Checkpoint);
            }

            public uint Start()
            {
                var rush = Rules.Begin(Sim, Sim.Tick);
                Hz.RoundStartTick = rush;
                return rush;
            }

            public void RunToEnd(int maxTicks = 6000)
            {
                for (var i = 0; i < maxTicks && !Rules.IsOver; i++)
                    Sim.Step();
            }
        }

        [Test]
        public void Race_12Bots_FirstNQualify_EndsAtTheNthFinisher_OrderedAndDeterministic()
        {
            string Once()
            {
                var r = new Race(12, 3300);
                var rush = r.Start();
                Assert.That(r.Rules.Phase, Is.EqualTo(RoundPhase.Intro));
                r.RunToEnd();
                Assert.That(r.Rules.IsOver, Is.True);
                var res = r.Rules.Result;
                Assert.That(res.QualifyCount, Is.EqualTo(8), "ceil(12 × 0.66)");
                Assert.That(Array.FindAll(res.Qualified, q => q).Length, Is.EqualTo(8), "exactly N qualify");
                Assert.That(res.TimerExpired, Is.False);
                Assert.That(res.Order.Length, Is.EqualTo(12));
                for (var p = 1; p < 8; p++)
                    Assert.That(res.FinishTicks[p], Is.GreaterThanOrEqualTo(res.FinishTicks[p - 1]), "finish order");
                Assert.That(res.EndTick, Is.EqualTo(res.FinishTicks[7]), "the round ends at once when N have finished");
                for (var p = 0; p < 12; p++)
                    Assert.That(res.Qualified[p], Is.EqualTo(p < 8));
                Assert.That(res.FinishTicks[0] - rush, Is.GreaterThan(60000 / 7300 * 30), "not faster than running");
                return res.ToString();
            }

            var a = Once();
            var b = Once();
            TestContext.WriteLine(a);
            Assert.That(b, Is.EqualTo(a), "the same inputs give the same result");
        }

        [Test]
        public void Countdown_IgnoresInput_StartGateClosedUntilRush()
        {
            var r = new Race(1, 3300);
            var rush = r.Start();
            var z0 = r.Sim.Slots[0].State.PosZ;
            while (r.Sim.Tick < rush - 1)
            {
                r.Sim.Step();
                Assert.That(r.Sim.Slots[0].State.PosZ, Is.EqualTo(z0).Within(30), $"tick {r.Sim.Tick}: frozen during intro/countdown ({r.Rules.Phase})");
            }

            Assert.That(HazardMath.DoorLiftMm(in r.Hz.Doors[0], r.Hz.Time(rush - 1)), Is.EqualTo(0), "gate closed before RUSH");
            for (var i = 0; i < 90; i++) // v2: 5 m/s needs ~2 s to reach the gate 9.5 m ahead
                r.Sim.Step();
            Assert.That(r.Rules.Phase, Is.EqualTo(RoundPhase.Rush));
            Assert.That(HazardMath.DoorOpen(in r.Hz.Doors[0], r.Hz.Time(r.Sim.Tick)), Is.True);
            Assert.That(r.Sim.Slots[0].State.PosZ, Is.GreaterThan(2500), "through the gate after RUSH");
        }

        [Test]
        public void Checkpoints_SkippedOrTeleported_NeverCount()
        {
            var r = new Race(0, 3300);
            var a = r.Sim.AddHuman(1);
            var rush = r.Start();
            while (r.Sim.Tick < rush + 5)
                r.Sim.Step();

            // (1) The harness moves the player straight into checkpoint 2: not the next one → never counts.
            r.Sim.Place(a, new Vec3(0f, 0.05f, 40f), 0);
            for (var i = 0; i < 30; i++)
                r.Sim.Step();
            Assert.That(r.Rules.Racers[a].Checkpoint, Is.EqualTo(0), "checkpoint 2 before 1");

            // (2) Teleported into checkpoint 1 too soon after RUSH: fails the reachability check.
            r.Sim.Place(a, new Vec3(0f, 0.05f, 20f), 0);
            r.Sim.Step();
            Assert.That(r.Rules.Racers[a].Checkpoint, Is.EqualTo(0), "28 m in < 56 ticks is not reachable");
            Assert.That(r.Rules.Racers[a].RejectedCheckpoints, Is.GreaterThan(0));

            // (3) Going around a checkpoint (outside its trigger) does not count either: a narrow trigger on a second race.
            var narrow = (CheckpointDef[])Cps.Clone();
            narrow[0].HalfXMm = 2000;
            var rules = new RaceRules(narrow, Line, 3300);
            var sim = new SimCore(Cfg, World(Course()), Spawns(), Course());
            sim.Rules = rules;
            var b = sim.AddHuman(2);
            var rush2 = rules.Begin(sim, sim.Tick);
            while (sim.Tick < rush2 + 200)
                sim.Step();
            var racer = rules.Racers[b];
            rules.TryCheckpoint(b, ref racer, new MotorState { PosX = 4000, PosY = 50, PosZ = 20000 }, sim.Tick);
            Assert.That(racer.Checkpoint, Is.EqualTo(0), "around the trigger");
            rules.TryCheckpoint(b, ref racer, new MotorState { PosX = 0, PosY = 50, PosZ = 20000 }, sim.Tick);
            Assert.That(racer.Checkpoint, Is.EqualTo(1), "through it, in time: counts");
            rules.TryCheckpoint(b, ref racer, new MotorState { PosX = 0, PosY = 50, PosZ = 60000 }, sim.Tick + 1);
            Assert.That(racer.Checkpoint, Is.EqualTo(1), "the finish before checkpoint 2: never");
        }

        [Test]
        public void Fall_RespawnsAtTheLastCheckpoint_After30Ticks_OnFreeSpots()
        {
            var r = new Race(0, 3300);
            var a = r.Sim.AddHuman(1);
            var b = r.Sim.AddHuman(2);
            var rush = r.Start();
            while (r.Sim.Tick < rush + 120)
                r.Sim.Step();
            // Both reach checkpoint 1 legitimately (in time), then walk off the edge.
            foreach (var s in new[] { a, b })
            {
                var racer = r.Rules.Racers[s];
                r.Rules.TryCheckpoint(s, ref racer, new MotorState { PosX = 0, PosY = 50, PosZ = 20000 }, r.Sim.Tick);
                r.Rules.Racers[s] = racer;
                Assert.That(r.Rules.Racers[s].Checkpoint, Is.EqualTo(1));
                r.Sim.Place(s, new Vec3(s == a ? 5.6f : -5.6f, 0.05f, 30f), 0); // past the edge: falls
            }

            uint fellAt = 0;
            for (var i = 0; i < 200; i++)
            {
                r.Sim.Step();
                if (fellAt == 0 && r.Sim.Slots[a].State.Has(MotorState.Respawning))
                    fellAt = r.Sim.Tick;
                if (fellAt != 0 && !r.Sim.Slots[a].State.Has(MotorState.Respawning))
                    break;
            }

            Assert.That(fellAt, Is.Not.EqualTo(0u));
            Assert.That(r.Sim.Tick - fellAt, Is.EqualTo(30u), "respawn exactly 30 ticks (1.0 s) after falling");
            var pa = r.Sim.Slots[a].State;
            Assert.That(pa.PosZ, Is.EqualTo(23000), "at checkpoint 1's respawn row");
            Assert.That(pa.Yaw, Is.EqualTo(0), "facing the course");
            for (var i = 0; i < 5; i++)
                r.Sim.Step();
            var pb = r.Sim.Slots[b].State;
            Assert.That(pb.PosZ, Is.EqualTo(23000).Within(400));
            Assert.That(Math.Abs(pa.PosX - pb.PosX), Is.GreaterThan(1000), "two respawns never stack on one spot");
        }

        [Test]
        public void TimerExpiry_RanksByCheckpoint_ThenCourseDistance_ThenCheckpointTime_ThenSlot()
        {
            var r = new Race(0, 300);
            var s = new int[6];
            for (var i = 0; i < 6; i++)
                s[i] = r.Sim.AddHuman((ulong)(i + 1));
            var rush = r.Start();
            while (r.Sim.Tick < rush + 150)
                r.Sim.Step();
            // Hand-placed states (nobody moves: humans send no input).
            void Set(int slot, int cp, uint cpTick, float x, float z)
            {
                r.Rules.Racers[slot].Checkpoint = cp;
                r.Rules.Racers[slot].CheckpointTick = cpTick;
                r.Sim.Place(slot, new Vec3(x, 0.05f, z), 0);
            }

            Set(s[0], 1, rush + 100, -2f, 25f); // cp1, 23 m to cp2
            Set(s[1], 2, rush + 120, 0f, 45f);  // cp2: best
            Set(s[2], 1, rush + 90, 0f, 35f);   // cp1, 13 m to cp2: ahead of s0
            Set(s[3], 1, rush + 80, 2f, 25f);   // cp1, same course distance as s0 (same z), earlier cp tick → ahead of s0
            Set(s[4], 0, 0, -2f, 10f);          // cp0
            Set(s[5], 0, 0, 2f, 10f);           // cp0, same as s4 → slot order
            r.RunToEnd();
            var res = r.Rules.Result;
            TestContext.WriteLine(res.ToString());
            Assert.That(res.TimerExpired, Is.True);
            Assert.That(res.Order, Is.EqualTo(new[] { s[1], s[2], s[3], s[0], s[4], s[5] }));
            Assert.That(res.QualifyCount, Is.EqualTo(4), "ceil(6 × 0.66)");
            Assert.That(res.Qualified, Is.EqualTo(new[] { true, true, true, true, false, false }), "timer: the rest qualify by ranking key");
        }

        [Test]
        public void FinishedRacers_LeaveTheSimulationAndCollisions()
        {
            var r = new Race(2, 3300);
            r.Start();
            for (var i = 0; i < 3000 && r.Rules.FinishedCount == 0; i++)
                r.Sim.Step();
            Assert.That(r.Rules.FinishedCount, Is.GreaterThan(0));
            var f = r.Rules.FinishOrder[0];
            var frozen = r.Sim.Slots[f].State;
            Assert.That(r.Rules.Collides(r.Sim, f) || r.Rules.Simulates(r.Sim, f), Is.False);
            for (var i = 0; i < 20 && !r.Rules.IsOver; i++)
                r.Sim.Step();
            Assert.That(r.Sim.Slots[f].State.Equals(frozen), Is.True, "frozen at the finish");
            Assert.That((r.Rules.RaceByte(f) & PlayerState.RaceFinished) != 0, Is.True);
        }

        [Test]
        public void CourseDistance_IsAlongThePolyline()
        {
            var rules = new RaceRules(Cps, new[] { 0, 0, 0, 0, 0, 10000, 10000, 0, 10000 }, 0); // an L: 10 m along Z, then 10 m along X
            Assert.That(rules.CourseDistance(0f, 0f, 5f), Is.EqualTo(5f).Within(1e-4f));
            Assert.That(rules.CourseDistance(5f, 0f, 10.5f), Is.EqualTo(15f).Within(1e-3f), "measured along the course, not straight-line");
        }
    }
}
