using System;
using System.Collections.Generic;
using FallRush.Net;
using FallRush.Shared;
using NUnit.Framework;

namespace FallRush.Tests
{
    /// <summary>
    /// M4 §5.1: Survival / Skill / Final rules (every ranking and tiebreak rule of GDD §5.2–5.4), goo kill timing, star
    /// drop on knockback (exactly one), pickup validation, tile wobble → drop at +18 ticks, spinner start at 60 s,
    /// last-standing win, the 120 s tiebreak; reactive delta/full equivalence and seq gaps; prediction vs a server twin
    /// bit-identical through tile drops, goo and star pickups.
    /// </summary>
    public class M4RulesTests
    {
        private static readonly MovementConfig Cfg = MovementConfig.Default;

        private static Vec3[] Spawns(float y = 0.05f)
        {
            var s = new Vec3[12];
            for (var i = 0; i < 12; i++)
                s[i] = new Vec3(-11f + 2f * i, y, -20f);
            return s;
        }

        private static GooDef Goo(int y0 = -500, int rise = 500, int delay = 30, int max = 40000) =>
            new GooDef { Y0Mm = y0, RiseMmPerSec = rise, DelayTicks = delay, MaxYMm = max, HalfXMm = 20000, HalfZMm = 20000 };

        private static CheckpointDef Box(int x, int y, int z, int half) =>
            new CheckpointDef { CenterXMm = x, CenterYMm = y, CenterZMm = z, HalfXMm = half, HalfYMm = half, HalfZMm = half, RespawnMm = Array.Empty<int>() };

        // ------------------------------------------------------------------ goo

        [Test]
        public void GooPose_IsPureInteger_AndReachIsExact()
        {
            var g = Goo(-500, 500, 30, 3000);
            Assert.That(HazardMath.GooYMm(in g, -100), Is.EqualTo(-500));
            Assert.That(HazardMath.GooYMm(in g, 30), Is.EqualTo(-500));
            Assert.That(HazardMath.GooYMm(in g, 60), Is.EqualTo(0));
            Assert.That(HazardMath.GooYMm(in g, 61), Is.EqualTo(16), "floor(500 · 31 / 30) − 500");
            Assert.That(HazardMath.GooYMm(in g, 100000), Is.EqualTo(3000), "capped");
            Assert.That(HazardMath.GooTicksToReach(in g, 0), Is.EqualTo(60));
            Assert.That(HazardMath.GooTicksToReach(in g, 1), Is.EqualTo(61));
            for (var y = -500; y <= 3000; y += 37)
            {
                var t = HazardMath.GooTicksToReach(in g, y);
                Assert.That(HazardMath.GooYMm(in g, t), Is.GreaterThanOrEqualTo(y));
                if (t > g.DelayTicks)
                    Assert.That(HazardMath.GooYMm(in g, t - 1), Is.LessThan(y));
            }
        }

        [Test]
        public void Goo_KnocksOutOnContact_AtTheExactTick_OnServerAndInPrediction()
        {
            var hz = new HazardSet(null, null) { Goo = new[] { Goo() } };
            var sim = new SimCore(Cfg, new AnalyticWorld().Ground(), Spawns(), hz);
            var rules = new SurvivalRules(Box(0, 0, 90000, 1000), 3000) { IntroTicks = 0, CountdownTicks = 0 };
            sim.Rules = rules;
            var a = sim.AddHuman(1);
            var rush = rules.Begin(sim, sim.Tick);
            hz.RoundStartTick = rush;
            var clientHz = new HazardSet(null, null) { Goo = new[] { Goo() }, RoundStartTick = rush };
            var pred = new ClientPrediction(Cfg, new AnalyticWorld().Ground(), clientHz);
            uint gooedAt = 0, predictedAt = 0;
            for (var k = 0; k < 200 && gooedAt == 0; k++)
            {
                var cmd = new InputCmd { Seq = (uint)k + 1, Tick = sim.NextTick };
                pred.Predict(in cmd);
                if (k == 0)
                {
                    var players = new PlayerState[12];
                    var n = sim.WritePlayerStates(players);
                    pred.Reconcile(sim.Tick, in players[0], sim.HeaderFor(a, n), 0);
                }

                sim.Input(a).Offer(cmd, sim.NextTick);
                sim.Step();
                if (predictedAt == 0 && pred.HasState && pred.State.Has(MotorState.Respawning))
                    predictedAt = pred.Tick;
                if ((sim.Slots[a].LastStep & PlayerStep.Result.Gooed) != 0)
                    gooedAt = sim.Tick;
            }

            Assert.That(gooedAt - rush, Is.EqualTo(61u), "goo surface 16 mm > feet 0 at hazard tick 61");
            Assert.That(predictedAt, Is.EqualTo(gooedAt), "prediction shows your own goo at the right tick");
            Assert.That(rules.Players[a].Out, Is.True);
            Assert.That(rules.Players[a].OutReason, Is.EqualTo(ElimReason.Goo));
        }

        // ------------------------------------------------------------------ survival

        private sealed class Round<T> where T : TimedRoundRules
        {
            public readonly SimCore Sim;
            public readonly T Rules;
            public readonly HazardSet Hz;
            public uint Rush;

            public Round(T rules, HazardSet hz, AnalyticWorld world, int humans, int target)
            {
                Hz = hz;
                Sim = new SimCore(Cfg, world.AddHazards(hz), Spawns(), hz);
                Rules = rules;
                Rules.IntroTicks = 0;
                Rules.CountdownTicks = 0;
                Rules.Target = target;
                Sim.Rules = rules;
                for (var i = 0; i < humans; i++)
                    Sim.AddHuman((ulong)(i + 1));
            }

            public void Start()
            {
                Rush = Rules.Begin(Sim, Sim.Tick);
                Hz.RoundStartTick = Rush;
                Sim.Step();
            }

            public void StepUntil(Func<bool> cond, int max = 20000)
            {
                for (var i = 0; i < max && !cond(); i++)
                    Sim.Step();
            }
        }

        [Test]
        public void Survival_FirstNToTheSummit_Qualify_LeaveTheSim_RoundEndsAtTheNth()
        {
            var r = new Round<SurvivalRules>(new SurvivalRules(Box(0, 1000, 30000, 2000), 3000), new HazardSet(null, null) { Goo = new[] { Goo(-5000, 100, 3000) } }, new AnalyticWorld().Ground(), 4, 2);
            r.Start();
            r.Sim.Place(2, new Vec3(0f, 0.05f, 30f), 0);
            for (var i = 0; i < 10; i++)
                r.Sim.Step();
            Assert.That(r.Rules.Players[2].Done, Is.True, "escaped");
            Assert.That(r.Rules.Simulates(r.Sim, 2), Is.False, "escaped players leave the simulation");
            Assert.That(r.Rules.IsOver, Is.False);
            r.Sim.Place(0, new Vec3(0.5f, 0.05f, 30.5f), 0);
            r.StepUntil(() => r.Rules.IsOver, 20);
            Assert.That(r.Rules.IsOver, Is.True, "ends when N have escaped");
            var res = r.Rules.RoundOutcome;
            Assert.That(res.Order, Is.EqualTo(new[] { 2, 0, 1, 3 }));
            Assert.That(res.Qualified, Is.EqualTo(new[] { true, true, false, false }));
            Assert.That(res.Reasons[2], Is.EqualTo(ElimReason.NotQualified));
        }

        [Test]
        public void Survival_TimerEnd_SurvivorsQualify_ByHeightThenSlot_CappedAtN()
        {
            var world = new AnalyticWorld().Ground()
                .AddBox(new Vec3(-10f, 0f, 9f), new Vec3(-8f, 2f, 11f))   // 2 m
                .AddBox(new Vec3(-6f, 0f, 9f), new Vec3(-4f, 4f, 11f))    // 4 m
                .AddBox(new Vec3(-2f, 0f, 9f), new Vec3(0f, 2f, 11f));    // 2 m
            var r = new Round<SurvivalRules>(new SurvivalRules(Box(0, 0, 90000, 1000), 120), new HazardSet(null, null) { Goo = new[] { Goo(-5000, 100, 9000) } }, world, 5, 3);
            r.Start();
            r.Sim.Place(0, new Vec3(-9f, 2.05f, 10f), 0);
            r.Sim.Place(2, new Vec3(-5f, 4.05f, 10f), 0);
            r.Sim.Place(3, new Vec3(-1f, 2.05f, 10f), 0);
            r.StepUntil(() => r.Rules.IsOver);
            var res = r.Rules.RoundOutcome;
            Assert.That(res.TimerExpired, Is.True);
            Assert.That(res.Order, Is.EqualTo(new[] { 2, 0, 3, 1, 4 }), "height ↓, then slot ↑");
            Assert.That(res.Qualified, Is.EqualTo(new[] { true, true, true, false, false }), "every survivor qualifies, capped at N");
            Assert.That(res.Reasons[3], Is.EqualTo(ElimReason.TimeoutRank));
        }

        [Test]
        public void Survival_FewerThanNAlive_OnlySurvivorsQualify_OutsRankedByLastToGo()
        {
            var world = new AnalyticWorld().Ground().AddBox(new Vec3(-10f, 0f, 9f), new Vec3(-8f, 6f, 11f));
            var r = new Round<SurvivalRules>(new SurvivalRules(Box(0, 0, 90000, 1000), 600), new HazardSet(null, null) { Goo = new[] { Goo(-500, 600, 0, 3000) } }, world, 4, 3);
            r.Start();
            r.Sim.Place(0, new Vec3(-9f, 6.05f, 10f), 0);
            r.Sim.Place(3, new Vec3(-3f, 0.05f, 0f), 0);
            r.StepUntil(() => r.Rules.IsOver);
            var res = r.Rules.RoundOutcome;
            Assert.That(res.QualifiedCount, Is.EqualTo(1), "only the survivor");
            Assert.That(res.Order[0], Is.EqualTo(0));
            for (var k = 1; k < res.Order.Length; k++)
            {
                Assert.That(res.Reasons[k], Is.EqualTo(ElimReason.Goo));
                Assert.That(res.Qualified[k], Is.False);
            }

            Assert.That(res.Order, Is.EqualTo(new[] { 0, 1, 2, 3 }), "same goo tick → slot order");
        }

        // ------------------------------------------------------------------ skill (stars)

        private static StarFieldDef Field(int anchors = 16, int golden = 4, int[] attach = null)
        {
            var a = new int[(anchors + golden) * 3];
            for (var i = 0; i < anchors + golden; i++)
            {
                a[i * 3] = -8000 + (i % 5) * 4000;
                a[i * 3 + 1] = 500;
                a[i * 3 + 2] = (i / 5) * 4000;
            }

            var g = new int[golden];
            for (var i = 0; i < golden; i++)
                g[i] = anchors + i;
            return new StarFieldDef
            {
                AnchorsMm = a, Attach = attach ?? NewFilled(anchors + golden, -1), Golden = g, WaveEveryTicks = 240, WaveSize = 8, WaveLifeTicks = 480,
                GoldenEveryTicks = 450, GoldenLifeTicks = 300, GoldenValue = 3, PickupRadiusMm = 400, DropPopTicks = 15, DropLifeTicks = 300, DropDistanceMm = 1200,
            };
        }

        private static int[] NewFilled(int n, int v)
        {
            var r = new int[n];
            for (var i = 0; i < n; i++)
                r[i] = v;
            return r;
        }

        [Test]
        public void StarTimeline_IsPure_WavesOf8DistinctNonGoldenAnchors_GoldenEvery15s()
        {
            var f = Field();
            for (var wave = 0; wave < 8; wave++)
            {
                var seen = new HashSet<int>();
                for (var k = 0; k < 8; k++)
                {
                    Assert.That(HazardMath.StarSlot(in f, 77, wave * 8 + k, out var a, out var sp, out var ex, out var v), Is.True);
                    Assert.That(a, Is.LessThan(16), "normal stars avoid the golden island anchors");
                    Assert.That(seen.Add(a), Is.True, $"wave {wave}: distinct anchors");
                    Assert.That(sp, Is.EqualTo(wave * 240));
                    Assert.That(ex, Is.EqualTo(wave * 240 + 480));
                    Assert.That(v, Is.EqualTo(1));
                    HazardMath.StarSlot(in f, 77, wave * 8 + k, out var a2, out _, out _, out _);
                    Assert.That(a2, Is.EqualTo(a), "pure");
                }
            }

            Assert.That(HazardMath.StarSlot(in f, 77, HazardMath.StarGoldenBase, out _, out _, out _, out _), Is.False, "no golden star at t = 0");
            for (var g = 1; g <= 3; g++)
            {
                Assert.That(HazardMath.StarSlot(in f, 77, HazardMath.StarGoldenBase + g, out var a, out var sp, out _, out var v), Is.True);
                Assert.That(sp, Is.EqualTo(g * 450), "a golden star every 15 s");
                Assert.That(v, Is.EqualTo(3));
                Assert.That(a, Is.GreaterThanOrEqualTo(16), "on an island");
            }

            var differs = false;
            for (var id = 0; id < 64; id++)
            {
                HazardMath.StarSlot(in f, 77, id, out var a, out _, out _, out _);
                HazardMath.StarSlot(in f, 78, id, out var b, out _, out _, out _);
                differs |= a != b;
            }

            Assert.That(differs, Is.True, "the seed changes the timeline");
        }

        [Test]
        public void StarOnATurnTable_RotatesWithIt_Pure()
        {
            var tt = new TurnTableDef { CenterXMm = 0, CenterYMm = 300, CenterZMm = 0, RadiusMm = 4000, ThicknessMm = 400, PeriodTicks = 240 };
            var hz = new HazardSet(null, null, null, null, new[] { tt }, null, null, null);
            var f = Field(attach: null);
            f.AnchorsMm = new[] { 2000, 500, 0 };
            f.Attach = new[] { 1000 };
            f.Golden = Array.Empty<int>();
            HazardMath.StarAnchorMm(in f, hz, 0, 0, out var x0, out var y0, out var z0);
            HazardMath.StarAnchorMm(in f, hz, 0, 60, out var x1, out _, out var z1);
            HazardMath.StarAnchorMm(in f, hz, 0, 240, out var x2, out _, out var z2);
            Assert.That((x0, y0, z0), Is.EqualTo((2000, 800, 0)));
            Assert.That(Math.Abs(x1) <= 1 && Math.Abs(Math.Abs(z1) - 2000) <= 1, Is.True, $"a quarter turn: ({x1},{z1})");
            Assert.That((x2, z2), Is.EqualTo((2000, 0)), "a full turn returns");
        }

        private static HazardSet SkillSet(BumperDef[] bumpers = null)
        {
            var hz = new HazardSet(null, null, null, null, null, null, null, bumpers) { StarFields = new[] { Field() }, Seed = 77 };
            hz.InitReactive();
            return hz;
        }

        private static BumperDef Bumper(int x, int z) => new BumperDef
        {
            CenterXMm = x, CenterYMm = 0, CenterZMm = z, RadiusMm = 600, HeightMm = 1000, ContactMarginMm = 40,
            KnockbackCms = KnockProfile.SoftPushCms, KnockUpCms = KnockProfile.SoftUpCms, StunTicks = KnockProfile.SoftStunTicks, ImmunityTicks = KnockProfile.ImmunityFor(KnockProfile.SoftStunTicks),
        };

        [Test]
        public void Skill_PickupOnlyByTheServerCapsule_KnockbackDropsExactlyOne_ReGrabbable()
        {
            var hz = SkillSet(new[] { Bumper(20000, 20000) });
            var r = new Round<SkillRules>(new SkillRules(1800), hz, new AnalyticWorld().Ground(), 3, 1);
            r.Start();
            var f = hz.StarFields[0];
            HazardMath.StarSlot(in f, hz.Seed, 0, out var a0, out _, out _, out _);
            HazardMath.StarAnchorMm(in f, hz, a0, 1, out var sx, out _, out var sz);
            // Slot 1 stands 1 m from the star (outside the 0.31 + 0.4 m reach); slot 0 stands on it.
            r.Sim.Place(1, new Vec3(sx * 0.001f + 1.0f, 0.05f, sz * 0.001f), 0);
            r.Sim.Step();
            Assert.That(r.Rules.Players[1].Score, Is.EqualTo(0), "not touching: no pickup");
            r.Sim.Place(0, new Vec3(sx * 0.001f, 0.05f, sz * 0.001f), 0);
            r.Sim.Step();
            Assert.That(r.Rules.Players[0].Score, Is.EqualTo(1), "server-validated pickup");
            Assert.That(hz.Reactive.StarTaken(0, r.Sim.Tick), Is.True);
            r.Sim.Place(1, new Vec3(sx * 0.001f, 0.05f, sz * 0.001f), 0);
            r.Sim.Step();
            Assert.That(r.Rules.Players[1].Score, Is.EqualTo(0), "a taken star cannot be taken again");

            // Knockback into the bumper: exactly one star drops.
            var before = hz.Reactive.Drops.Count;
            r.Sim.Place(0, new Vec3(19.0f, 0.05f, 20f), 64);
            var hit = false;
            for (var i = 0; i < 20 && !hit; i++)
            {
                r.Sim.Input(0).Offer(new InputCmd { Seq = (uint)(i + 1), Tick = r.Sim.NextTick, MoveX = 127 }, r.Sim.NextTick);
                r.Sim.Step();
                hit = (r.Sim.Slots[0].LastStep & PlayerStep.Result.HazardHit) != 0;
            }

            Assert.That(hit, Is.True, "ran into the bumper");
            Assert.That(r.Rules.Players[0].Score, Is.EqualTo(0), "lost one star");
            Assert.That(hz.Reactive.Drops.Count - before, Is.EqualTo(1), "exactly one drop");
            var drop = hz.Reactive.Drops[before];
            // It pops for 15 ticks, then anyone can grab it where it landed.
            HazardMath.DropPosMm(in f, drop.Xcm * 10, drop.Ycm * 10, drop.Zcm * 10, (uint)(before + 1), 100, out var dx, out var dy, out var dz);
            r.Sim.Place(2, new Vec3(dx * 0.001f, dy * 0.001f - 0.6f, dz * 0.001f), 0);
            for (var i = 0; i < 30 && r.Rules.Players[2].Score == 0; i++)
                r.Sim.Step();
            Assert.That(r.Rules.Players[2].Score, Is.EqualTo(1), "the dropped star is re-grabbed");
            Assert.That(r.Rules.Regrabs, Is.EqualTo(1));
        }

        [Test]
        public void Skill_Ranking_TopNByScore_TiesEarliestTimeAtFinalScore_ThenSlot()
        {
            var hz = SkillSet();
            var r = new Round<SkillRules>(new SkillRules(1800), hz, new AnalyticWorld().Ground(), 5, 3);
            r.Start();
            var p = r.Rules.Players;
            (p[0].Score, p[0].ScoreTick) = (3, 100);
            (p[1].Score, p[1].ScoreTick) = (3, 90);
            (p[2].Score, p[2].ScoreTick) = (3, 90);
            (p[3].Score, p[3].ScoreTick) = (5, 300);
            (p[4].Score, p[4].ScoreTick) = (0, 0);
            var res = r.Rules.RankNow(r.Sim, r.Sim.Tick, true);
            Assert.That(res.Order, Is.EqualTo(new[] { 3, 1, 2, 0, 4 }));
            Assert.That(res.Qualified, Is.EqualTo(new[] { true, true, true, false, false }));
            Assert.That(res.Reasons[3], Is.EqualTo(ElimReason.LowerScore));
            r.StepUntil(() => r.Rules.IsOver);
            Assert.That(r.Rules.RoundOutcome.TimerExpired, Is.True, "60 s timer ends the round");
        }

        // ------------------------------------------------------------------ final (crumble shelf)

        internal static TileGridDef Grid() => new TileGridDef
        {
            OriginXMm = -7200, OriginZMm = -7200, TileMm = 1600, GapMm = 40, Cols = 9, Rows = 9, ShelfTopsMm = new[] { 12000, 6000, 0 }, ThicknessMm = 400, WobbleTicks = 18,
        };

        private static HazardSet FinalSet(SweeperDef[] sweepers = null, int sweeperStart = int.MinValue)
        {
            var hz = new HazardSet(sweepers, null) { Grids = new[] { Grid() }, SweeperStartT = sweeperStart, KillYMm = -6000 };
            hz.InitReactive();
            return hz;
        }

        [Test]
        public void Final_TileWobbles_ThenDrops_At18TicksAfterFirstContact()
        {
            var hz = FinalSet();
            var r = new Round<FinalRules>(new FinalRules(3600), hz, new AnalyticWorld(), 2, 1);
            // Both stand on top-shelf tiles far apart.
            r.Sim.Place(0, new Vec3(-6.4f, 12.05f, -6.4f), 0);
            r.Sim.Place(1, new Vec3(6.4f, 12.05f, 6.4f), 0);
            r.Sim.Reconfigure(Cfg, r.Sim.World, hz, new[] { new Vec3(-6.4f, 12.05f, -6.4f), new Vec3(6.4f, 12.05f, 6.4f), V(), V(), V(), V(), V(), V(), V(), V(), V(), V() });
            r.Start();
            var tile = hz.Grids[0].TileUnder(-6400, 12000, -6400);
            Assert.That(tile, Is.EqualTo(0));
            r.StepUntil(() => hz.Reactive.TileContactTick(tile) != 0, 60);
            var c = hz.Reactive.TileContactTick(tile);
            Assert.That(c, Is.Not.Zero, "contact recorded (server ground contact)");
            Assert.That(hz.Reactive.TileSolid(tile, c + 17), Is.True, "wobbling for 0.6 s");
            Assert.That(hz.Reactive.TileSolid(tile, c + 18), Is.False, "dropped at contact + 18 ticks");
            uint lastOnTop = 0;
            for (var i = 0; i < 40; i++)
            {
                r.Sim.Step();
                ref readonly var s = ref r.Sim.Slots[0].State;
                if (s.Has(MotorState.Grounded) && s.PosY > 11900)
                    lastOnTop = r.Sim.Tick;
            }

            Assert.That(lastOnTop, Is.EqualTo(c + 17), "standing on it until the drop, falling from the drop tick");
            Assert.That(r.Sim.Slots[0].State.PosY, Is.LessThan(11000));
        }

        private static Vec3 V() => new Vec3(30f, 20f, 30f);

        [Test]
        public void Final_SpinnerBars_SweepOnlyFrom60s()
        {
            var sp = new SweeperDef
            {
                PivotXMm = 0, PivotYMm = 12000, PivotZMm = 0, ArmMm = 7000, BarRadiusMm = 150, BarHeightMm = 450, PeriodTicks = 200, Tangential = 1,
                KnockbackCms = KnockProfile.SoftPushCms, KnockUpCms = KnockProfile.SoftUpCms, StunTicks = KnockProfile.SoftStunTicks, ImmunityTicks = KnockProfile.ImmunityFor(KnockProfile.SoftStunTicks),
            };
            var hz = FinalSet(new[] { sp }, 1800);
            int before = -1, after = -1;
            for (var t = 1500; t < 2200 && (before < 0 || after < 0); t++)
            {
                if (!HazardMath.SweeperContactMm(in sp, t, 3000, 12000, 0, 310, 1000, out _, out _))
                    continue;
                if (t < 1800 && before < 0)
                    before = t;
                if (t >= 1800 && after < 0)
                    after = t;
            }

            Assert.That(before, Is.GreaterThanOrEqualTo(0));
            Assert.That(after, Is.GreaterThanOrEqualTo(1800));
            var s = new MotorState { PosX = 3000, PosY = 12000, PosZ = 0 };
            Assert.That(hz.ApplyKnocks(ref s, (uint)before, Cfg), Is.False, "retracted before 60 s");
            s = new MotorState { PosX = 3000, PosY = 12000, PosZ = 0 };
            Assert.That(hz.ApplyKnocks(ref s, (uint)after, Cfg), Is.True, "sweeping from 60 s");
        }

        [Test]
        public void Final_LastStanding_Wins_TimerTiebreak_LowestLayerThenFewerTilesThenSlot()
        {
            var hz = FinalSet();
            var r = new Round<FinalRules>(new FinalRules(3600), hz, new AnalyticWorld().Ground(), 4, 1);
            r.Start();
            var p = r.Rules.Players;
            (p[0].Deepest, p[0].Tiles) = (1, 5);
            (p[1].Deepest, p[1].Tiles) = (0, 9);
            (p[2].Deepest, p[2].Tiles) = (0, 4);
            (p[3].Deepest, p[3].Tiles) = (0, 4);
            var res = r.Rules.RankNow(r.Sim, r.Sim.Tick, true);
            Assert.That(res.Order, Is.EqualTo(new[] { 2, 3, 1, 0 }), "higher layer, then fewer tiles, then slot");
            Assert.That(res.Qualified, Is.EqualTo(new[] { true, false, false, false }));
            Assert.That(res.Reasons[1], Is.EqualTo(ElimReason.TimeoutRank));

            // Last standing: three fall out → the round ends at once and the survivor wins.
            p[0].Out = p[1].Out = p[3].Out = true;
            p[0].OutTick = 10;
            p[1].OutTick = 30;
            p[3].OutTick = 20;
            p[0].OutReason = p[1].OutReason = p[3].OutReason = ElimReason.FellOut;
            r.Sim.Step();
            Assert.That(r.Rules.IsOver, Is.True);
            res = r.Rules.RoundOutcome;
            Assert.That(res.Order, Is.EqualTo(new[] { 2, 1, 3, 0 }), "the survivor, then the last to fall first");
            Assert.That(res.QualifiedCount, Is.EqualTo(1));
            Assert.That(res.TimerExpired, Is.False);
        }

        [Test]
        public void Final_TimerAt120s_EndsWithOneQualifier()
        {
            var hz = FinalSet();
            var r = new Round<FinalRules>(new FinalRules(120), hz, new AnalyticWorld().Ground(), 3, 1);
            r.Start();
            r.StepUntil(() => r.Rules.IsOver);
            Assert.That(r.Rules.RoundOutcome.TimerExpired, Is.True);
            Assert.That(r.Rules.RoundOutcome.QualifiedCount, Is.EqualTo(1));
        }

        // ------------------------------------------------------------------ hazard vectors v2 (cross-platform)

        [Test]
        public void HazardVectorsV2_MatchTheCommittedVectors_AndKeepV1()
        {
            var all = HazardVectorsV2.ComputeAll();
            var path = System.IO.Path.Combine(UnityEngine.Application.dataPath, "_Game", "Tests", "Vectors", "hazard_vectors_v2.json");
            var json = (Dictionary<string, object>)new MovementConfigTests.MiniJson(System.IO.File.ReadAllText(path)).Parse();
            var cases = (List<object>)json["cases"];
            Assert.That(cases.Count, Is.EqualTo(HazardVectorsV2.Count));
            int goo = 0, gooContact = 0, stars = 0, starOk = 0, tiles = 0, tileHit = 0;
            for (var i = 0; i < cases.Count; i++)
            {
                var row = (List<object>)cases[i];
                for (var f = 0; f < HazardVectorsV2.Width; f++)
                    Assert.That(all[i * HazardVectorsV2.Width + f], Is.EqualTo((int)(long)row[f]), $"case {i} field {f}");
                var type = all[i * HazardVectorsV2.Width];
                if (type == HazardVectorsV2.Goo) { goo++; gooContact += all[i * HazardVectorsV2.Width + 7]; }
                if (type == HazardVectorsV2.Star) { stars++; starOk += all[i * HazardVectorsV2.Width + 2]; }
                if (type == HazardVectorsV2.Tile) { tiles++; tileHit += all[i * HazardVectorsV2.Width + 6] >= 0 ? 1 : 0; }
            }

            // The first 1,000 cases are v1, unchanged.
            var v1 = HazardVectors.ComputeAll();
            for (var i = 0; i < v1.Length; i++)
                Assert.That(all[i], Is.EqualTo(v1[i]));
            Assert.That(HazardVectors.Digest(v1), Is.EqualTo("fc06826c90569570fe8cda386312687e40dc7543edf322d85415feb6a046e1b8"), "v1 digest unchanged");
            Assert.That(HazardVectorsV2.Digest(all), Is.EqualTo((string)json["digest"]));
            Assert.That(HazardVectorsV2.Digest(all), Is.EqualTo(HazardVectorsV2Expected.Digest), "generated C# table is current");
            Assert.That(HazardVectorsV2.SelfTest(HazardVectorsV2Expected.Data, HazardVectorsV2Expected.Digest, "editmode", out var line), Is.True, line);
            TestContext.WriteLine($"{line}\ngoo {goo} (contact {gooContact}), stars {stars} (valid {starOk}), tiles {tiles} (on a tile {tileHit})");
            Assert.That(gooContact, Is.InRange(goo / 5, goo * 4 / 5), "goo cases exercise contact and no contact");
            Assert.That(starOk, Is.GreaterThan(stars / 2));
            Assert.That(tileHit, Is.InRange(tiles / 5, tiles * 4 / 5));
        }

        // ------------------------------------------------------------------ reactive replication

        [Test]
        public void Reactive_DeltaChunksAndFull_GiveTheSameState_AndASeqGapIsRefused()
        {
            var blocks = new[] { new FallingBlockDef { CenterYMm = 1000, HalfXMm = 1000, HalfYMm = 250, HalfZMm = 1000, DelayTicks = 18, GoneTicks = 120 } };
            var server = new ReactiveSet(Grid(), blocks, Field(), 5);
            var rnd = new Random(3);
            for (var i = 0; i < 300; i++)
            {
                var tick = (uint)(100 + i * 3);
                switch (i % 4)
                {
                    case 0: server.Add(RxType.TileContact, rnd.Next(243), i % 12, tick); break;
                    case 1: if (server.BlockIdle(0, tick)) server.Add(RxType.BlockTrigger, 0, i % 12, tick); break;
                    case 2: server.Add(RxType.StarCollect, rnd.Next(64), i % 12, tick); break;
                    default: server.Add(RxType.StarDrop, server.Drops.Count, i % 12, tick, 1234, 600, -4321); break;
                }
            }

            var buf = new byte[ReactiveCodec.HeaderSize + 80 * RxEntry.Size];
            var tmp = new List<RxEntry>();
            var viaDeltas = new ReactiveSet(Grid(), blocks, Field(), 5);
            for (var from = 0; from < server.Log.Count; from += 37)
            {
                var n = Math.Min(37, server.Log.Count - from);
                var len = ReactiveCodec.Encode(buf, ReactiveCodec.Delta, 9, server.Log, from, n);
                Assert.That(ReactiveCodec.TryDecode(new ReadOnlySpan<byte>(buf, 0, len), out var kind, out var epoch, out var first, tmp), Is.True);
                Assert.That((kind, epoch, first), Is.EqualTo((ReactiveCodec.Delta, (byte)9, (uint)from + 1)));
                foreach (var e in tmp)
                    Assert.That(viaDeltas.Apply(in e), Is.True);
            }

            var viaFull = new ReactiveSet(Grid(), blocks, Field(), 5);
            foreach (var e in server.Log)
                viaFull.Apply(in e);
            for (var t = 0u; t < 1400; t += 7)
            {
                for (var i = 0; i < 243; i++)
                    Assert.That(viaDeltas.TileSolid(i, t), Is.EqualTo(server.TileSolid(i, t)).And.EqualTo(viaFull.TileSolid(i, t)));
                Assert.That(viaDeltas.BlockSolid(0, t), Is.EqualTo(server.BlockSolid(0, t)).And.EqualTo(viaFull.BlockSolid(0, t)));
                for (var id = 0; id < 64; id++)
                    Assert.That(viaDeltas.StarTaken(id, t), Is.EqualTo(server.StarTaken(id, t)));
            }

            Assert.That(viaDeltas.Drops.Count, Is.EqualTo(server.Drops.Count));
            Assert.That(viaDeltas.Drops[3].Xcm, Is.EqualTo(123));
            // A gap: entry 6 offered to a set that has 3 → refused (the client asks for the full state).
            var gap = new ReactiveSet(Grid(), blocks, Field(), 5);
            for (var i = 0; i < 3; i++)
                gap.Apply(server.Log[i]);
            Assert.That(gap.Apply(server.Log[5]), Is.False);
            Assert.That(gap.LastSeq, Is.EqualTo(3u));
            // Truncated / bad-type messages are refused.
            var len2 = ReactiveCodec.Encode(buf, ReactiveCodec.Full, 1, server.Log, 0, 2);
            Assert.That(ReactiveCodec.TryDecode(new ReadOnlySpan<byte>(buf, 0, len2 - 1), out _, out _, out _, tmp), Is.False);
            buf[ReactiveCodec.HeaderSize] = 99;
            Assert.That(ReactiveCodec.TryDecode(new ReadOnlySpan<byte>(buf, 0, len2), out _, out _, out _, tmp), Is.False);
        }

        // ------------------------------------------------------------------ prediction vs a server twin

        /// <summary>
        /// A server <see cref="SimCore"/> with the given rules and a <see cref="ClientPrediction"/> on its own hazard copy.
        /// Reactive entries reach the client <paramref name="delay"/> ticks after the server made them (like <c>rx</c>),
        /// snapshots too; the client's inputs are stamped <paramref name="lead"/> ticks ahead.
        /// </summary>
        private sealed class Twin
        {
            public readonly SimCore Server;
            public readonly ClientPrediction Client;
            public readonly HazardSet ServerHz, ClientHz;
            private readonly int _lead, _delay;
            private readonly Queue<(uint at, uint tick, PlayerState p, SnapHeader h)> _down = new Queue<(uint, uint, PlayerState, SnapHeader)>();
            private readonly Queue<(uint at, RxEntry e)> _rx = new Queue<(uint, RxEntry)>();
            private readonly PlayerState[] _players = new PlayerState[12];
            private int _rxSent;
            private uint _seq;
            public long RxLate;
            public Func<uint, MotorState, InputCmd> Input;

            public Twin(Func<HazardSet> make, Func<HazardSet, AnalyticWorld> world, IRoundRules rules, int lead, int delay, Vec3[] spawns, int extraBots)
            {
                ServerHz = make();
                ClientHz = make();
                Server = new SimCore(Cfg, world(ServerHz), spawns, ServerHz);
                Server.AddHuman(1);
                for (var i = 0; i < extraBots; i++)
                    Server.AddBot();
                Server.BotInput = (slot, tick, s) => new InputCmd { Tick = tick };
                rules.IntroTicks = 0;
                rules.CountdownTicks = 0;
                Server.Rules = rules;
                var rush = rules.Begin(Server, 0);
                ServerHz.RoundStartTick = ClientHz.RoundStartTick = rush;
                Client = new ClientPrediction(Cfg, world(ClientHz), ClientHz);
                _lead = lead;
                _delay = delay;
            }

            public void Tick(uint k)
            {
                var t = k + (uint)_lead;
                var cmd = Input(t, Client.HasState ? Client.State : default);
                cmd.Seq = ++_seq;
                cmd.Tick = t;
                InputSanitizer.Sanitize(ref cmd);
                Client.Predict(in cmd);
                Server.Input(0).Offer(cmd, Server.NextTick);
                Server.Step();
                var rx = ServerHz.Reactive;
                if (rx != null)
                    for (; _rxSent < rx.Log.Count; _rxSent++)
                        _rx.Enqueue((Server.Tick + (uint)_delay, rx.Log[_rxSent]));
                if (Server.Tick % 2 == 0)
                {
                    var n = Server.WritePlayerStates(_players);
                    var h = Server.HeaderFor(0, n);
                    for (var i = 0; i < n; i++)
                        if (_players[i].Slot == 0)
                            _down.Enqueue((Server.Tick + (uint)_delay, Server.Tick, _players[i], h));
                }

                while (_rx.Count > 0 && _rx.Peek().at <= Server.Tick)
                {
                    var e = _rx.Dequeue().e;
                    Assert.That(ClientHz.Reactive.Apply(in e), Is.True);
                    if (ClientHz.Reactive.EffectiveTick(in e) <= Client.Tick && e.Type == RxType.TileContact)
                        RxLate++;
                }

                while (_down.Count > 0 && _down.Peek().at <= Server.Tick)
                {
                    var m = _down.Dequeue();
                    Client.Reconcile(m.tick, in m.p, in m.h, m.tick / 30.0);
                }
            }
        }

        private static InputCmd Steer(in MotorState s, float tx, float tz, bool jump)
        {
            var dx = tx - s.PosX * 0.001f;
            var dz = tz - s.PosZ * 0.001f;
            var l = (float)Math.Sqrt(dx * dx + dz * dz);
            var c = new InputCmd();
            if (l > 0.05f)
            {
                c.MoveX = (sbyte)Math.Round(dx / l * 127);
                c.MoveY = (sbyte)Math.Round(dz / l * 127);
            }

            if (jump)
                c.Buttons = InputCmd.JumpPressed | InputCmd.JumpHeld;
            return c;
        }

        [Test]
        public void G1_Prediction_BitForBit_WithBellySlides_Stumbles_AndRespawningTiles([Values(1, 6)] int lead, [Values(2, 5)] int delay)
        {
            HazardSet Make()
            {
                var g = Grid();
                g.WobbleTicks = 30;
                g.RespawnTicks = 240;
                g.RespawnShelves = 1;
                var h = new HazardSet(null, null) { Grids = new[] { g }, KillYMm = -6000 };
                h.InitReactive();
                return h;
            }

            AnalyticWorld World(HazardSet h) => new AnalyticWorld().AddHazards(h).AddBox(new Vec3(20f, -10f, 20f), new Vec3(24f, 20f, 24f));
            var spawns = Spawns();
            spawns[0] = new Vec3(0.8f, 12.05f, 0.8f);
            spawns[1] = new Vec3(21f, 20.05f, 21f);
            spawns[2] = new Vec3(23f, 20.05f, 23f);
            var rules = new FinalRules(9000) { Target = 1 };
            var twin = new Twin(Make, World, rules, lead, delay, spawns, 2);
            twin.Input = (t, s) =>
            {
                var a = t * 0.05;
                var c = Steer(s, (float)(4.0 * Math.Cos(a)), (float)(4.0 * Math.Sin(a)), t % 37 == 0);
                if (t % 53 == 0)
                    c.Buttons |= InputCmd.DivePressed | InputCmd.DiveHeld; // belly slides
                return c;
            };
            int slides = 0, stumbles = 0;
            var was = default(MotorState);
            for (uint k = 1; k <= 2400; k++)
            {
                twin.Tick(k);
                var st = twin.Server.Slots[0].State;
                if (st.Has(MotorState.DiveLanding) && !was.Has(MotorState.DiveLanding))
                    slides++;
                if (st.Has(MotorState.Stumbling) && !was.Has(MotorState.Stumbling))
                    stumbles++;
                was = st;
            }

            var p = twin.Client;
            TestContext.WriteLine($"lead={lead} delay={delay} slides={slides} stumbles={stumbles} corrections={p.Corrections} silent={p.SilentResyncs} hazardMismatch={p.HazardMismatches} rxLate={twin.RxLate}");
            Assert.That(slides, Is.GreaterThan(3), "belly slides exercised");
            Assert.That(stumbles, Is.GreaterThan(0), "a 6 m shelf drop stumbles");
            Assert.That(p.Corrections, Is.EqualTo(0));
            Assert.That(p.HazardMismatches, Is.EqualTo(0));
        }

        [Test]
        public void Prediction_MatchesTheServer_BitForBit_ThroughTileDropsAndGoo([Values(1, 3, 6)] int lead, [Values(2, 5)] int delay)
        {
            HazardSet Make()
            {
                var h = new HazardSet(null, null) { Grids = new[] { Grid() }, Goo = new[] { Goo(-5000, 400, 900, 3000) }, KillYMm = -6000 };
                h.InitReactive();
                return h;
            }

            // Two still bots stand on a tall safe pillar (out of the grid, above the goo) so the round never ends.
            AnalyticWorld World(HazardSet h) => new AnalyticWorld().AddHazards(h).AddBox(new Vec3(20f, -10f, 20f), new Vec3(24f, 20f, 24f));
            var spawns = Spawns();
            spawns[0] = new Vec3(0.8f, 12.05f, 0.8f);
            spawns[1] = new Vec3(21f, 20.05f, 21f);
            spawns[2] = new Vec3(23f, 20.05f, 23f);
            var rules = new FinalRules(9000) { Target = 1 };
            var twin = new Twin(Make, World, rules, lead, delay, spawns, 2);
            twin.Input = (t, s) =>
            {
                // Chase a point running round a 3 m circle over the middle of the shelves (crumbling them), hop now and then.
                var a = t * 0.045;
                return Steer(s, (float)(3.0 * Math.Cos(a)), (float)(3.0 * Math.Sin(a)), t % 41 == 0);
            };
            for (uint k = 1; k <= 2400; k++)
                twin.Tick(k);
            var p = twin.Client;
            var drops = 0;
            for (var i = 0; i < 243; i++)
                if (twin.ServerHz.Reactive.TileContactTick(i) != 0)
                    drops++;
            TestContext.WriteLine($"lead={lead} delay={delay} snapshots={p.Snapshots} match={p.Matches} corrections={p.Corrections} silent={p.SilentResyncs} respawns={p.Respawns} tiles touched={drops} rxLate={twin.RxLate} out={rules.Players[0].Out} reason={rules.Players[0].OutReason} deepest={rules.Players[0].Deepest}");
            if (p.Corrections > 0)
                TestContext.WriteLine($"last mismatch at {p.LastMismatchTick}:\n auth {p.LastMismatchAuth}\n pred {p.LastMismatchPredicted}");
            Assert.That(drops, Is.GreaterThan(20), "the run crumbles many tiles");
            Assert.That(rules.Players[0].Deepest, Is.GreaterThanOrEqualTo(1), "fell through to a lower shelf");
            Assert.That(rules.Players[0].Out, Is.True, "finally out");
            Assert.That(twin.RxLate, Is.EqualTo(0), "every drop known before its tick (lead + delay < 18)");
            Assert.That(p.Corrections, Is.EqualTo(0));
            Assert.That(p.SilentResyncs, Is.EqualTo(0));
            Assert.That(p.HazardMismatches, Is.EqualTo(0));
        }

        [Test]
        public void Prediction_MatchesTheServer_BitForBit_ThroughStarPickupsAndDrops([Values(1, 6)] int lead, [Values(2, 5)] int delay)
        {
            HazardSet Make()
            {
                var h = new HazardSet(null, null, null, null, null, null, null, new[] { Bumper(0, 6000), Bumper(-6000, 2000), Bumper(6000, 2000) }) { StarFields = new[] { Field() }, Seed = 77 };
                h.InitReactive();
                return h;
            }

            var rules = new SkillRules(9000) { Target = 1 };
            var spawns = Spawns();
            spawns[0] = new Vec3(0f, 0.05f, 0f);
            spawns[1] = new Vec3(40f, 0.05f, 40f);
            var twin = new Twin(Make, h => new AnalyticWorld().Ground().AddHazards(h), rules, lead, delay, spawns, 1);
            // Visit star anchors and run into the bumpers in turn (from the predicted state).
            var f = Field();
            var targets = new List<(float x, float z)>();
            for (var i = 0; i < f.AnchorCount; i++)
            {
                targets.Add((f.AnchorsMm[i * 3] * 0.001f, f.AnchorsMm[i * 3 + 2] * 0.001f));
                if (i % 4 == 3)
                    targets.Add(i % 8 == 3 ? (0f, 6f) : (-6f, 2f));
            }

            var at = 0;
            uint since = 0;
            twin.Input = (t, s) =>
            {
                var (tx, tz) = targets[at % targets.Count];
                var dx = tx - s.PosX * 0.001f;
                var dz = tz - s.PosZ * 0.001f;
                if (dx * dx + dz * dz < 0.5f || t - since > 120)
                {
                    at++;
                    since = t;
                }

                return Steer(s, tx, tz, t % 67 == 0);
            };
            for (uint k = 1; k <= 2400; k++)
                twin.Tick(k);
            var p = twin.Client;
            TestContext.WriteLine($"lead={lead} delay={delay} pickups={rules.Pickups} drops={rules.Drops} hits={twin.Server.HazardHits} corrections={p.Corrections} silent={p.SilentResyncs}");
            Assert.That(rules.Pickups, Is.GreaterThan(5), "stars were picked up");
            Assert.That(rules.Drops, Is.GreaterThan(0), "a knockback dropped a carried star");
            Assert.That(twin.Server.HazardHits, Is.GreaterThan(0), "bumper knockbacks (predicted)");
            Assert.That(p.Corrections, Is.EqualTo(0));
            Assert.That(p.SilentResyncs, Is.EqualTo(0));
            Assert.That(p.HazardMismatches, Is.EqualTo(0));
        }
    }
}
