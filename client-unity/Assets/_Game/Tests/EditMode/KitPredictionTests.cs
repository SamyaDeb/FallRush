using System;
using System.Collections.Generic;
using FallRush.Net;
using FallRush.Shared;
using NUnit.Framework;

namespace FallRush.Tests
{
    /// <summary>M3a: client prediction against a server twin, bit-identical through every hazard type of the kit.</summary>
    public class KitPredictionTests
    {
        /// <summary>
        /// A 46 m test lane along +X (z = 0) with one of each hazard: spinner bar (x 4), conveyor pushing sideways (x 7–10),
        /// spring pad (x 12–13), turn table (x 17), gate door (x 22), mallet (x 27), bumper (x 31), bobbing loop platform (x 37).
        /// </summary>
        internal static HazardSet KitLane(int modifier = 0)
        {
            var m = ModifierEffects.Get(modifier);
            var sp = new SweeperDef
            {
                PivotXMm = 4000, PivotZMm = 3000, ArmMm = 4500, BarRadiusMm = 150, BarHeightMm = 450, PeriodTicks = 90, Phase16 = 9000, Tangential = 1,
                KnockbackCms = KnockProfile.SoftPushCms, KnockUpCms = KnockProfile.SoftUpCms, StunTicks = KnockProfile.SoftStunTicks, ImmunityTicks = KnockProfile.ImmunityFor(KnockProfile.SoftStunTicks),
            };
            var cv = new ConveyorDef { CenterXMm = 8500, CenterYMm = -150, CenterZMm = 0, HalfXMm = 1500, HalfYMm = 250, HalfZMm = 2000, BeltZMms = -1500 };
            var pd = new SpringPadDef { CenterXMm = 12500, CenterYMm = 100, CenterZMm = 0, HalfXMm = 500, HalfZMm = 1500, ThicknessMm = 400, LaunchXCms = 500, LaunchYCms = 1000 };
            var tt = new TurnTableDef { CenterXMm = 17000, CenterYMm = 150, CenterZMm = 0, RadiusMm = 2500, ThicknessMm = 400, PeriodTicks = 240, Phase16 = 100 };
            var dr = new GateDoorDef
            {
                CenterXMm = 22000, CenterYMm = 1300, CenterZMm = 0, HalfXMm = 200, HalfYMm = 1300, HalfZMm = 4000, LiftMm = 2700,
                ClosedTicks = 45, OpeningTicks = 6, OpenTicks = 60, ClosingTicks = 18, TelegraphTicks = 15, // 18 → 15 under TurboToys: 180 mm/tick ≤ 200
            };
            var ml = new MalletDef
            {
                PivotXMm = 27000, PivotYMm = 5000, PivotZMm = 0, SwingAxis = 2, ArmMm = 4400, HeadRadiusMm = 450, HeadHalfWidthMm = 700,
                AmplitudePhase16 = 9000, PeriodTicks = 100, Phase16 = 3000, TelegraphTicks = 15,
                KnockbackCms = KnockProfile.HardPushCms, KnockUpCms = KnockProfile.HardUpCms, StunTicks = KnockProfile.HardStunTicks, ImmunityTicks = KnockProfile.ImmunityFor(KnockProfile.HardStunTicks),
            };
            var bm = new BumperDef
            {
                CenterXMm = 31000, CenterYMm = 0, CenterZMm = 800, RadiusMm = 600, HeightMm = 1000, ContactMarginMm = 40,
                KnockbackCms = KnockProfile.SoftPushCms, KnockUpCms = KnockProfile.SoftUpCms, StunTicks = KnockProfile.SoftStunTicks, ImmunityTicks = KnockProfile.ImmunityFor(KnockProfile.SoftStunTicks),
            };
            var pl = new PlatformDef { CenterXMm = 37000, CenterYMm = -100, CenterZMm = 0, HalfXMm = 1500, HalfYMm = 200, HalfZMm = 2000, PeriodTicks = 120, KnotsMm = new[] { 0, 0, 0, 0, 350, 0 } };
            HazardTuning.Apply(ref sp, in m);
            HazardTuning.Apply(ref cv, in m);
            HazardTuning.Apply(ref tt, in m);
            HazardTuning.Apply(ref dr, in m);
            HazardTuning.Apply(ref ml, in m);
            HazardTuning.Apply(ref bm, in m);
            HazardTuning.Apply(ref pl, in m);
            var h = new HazardSet(new[] { sp }, new[] { pl }, new[] { cv }, new[] { pd }, new[] { tt }, new[] { dr }, new[] { ml }, new[] { bm });
            Assert.That(HazardAllowlist.Validate(h), Is.Empty);
            return h;
        }

        private sealed class KitTwin
        {
            public readonly SimCore Server;
            public readonly ClientPrediction Client;
            public readonly HazardSet ServerHazards;
            public readonly int Slot;
            private readonly int _lead, _delay;
            private readonly Queue<(uint at, uint tick, PlayerState p, SnapHeader h)> _down = new Queue<(uint, uint, PlayerState, SnapHeader)>();
            private readonly PlayerState[] _players = new PlayerState[12];
            private uint _seq;
            private uint _waitStart;
            private int _dir = 1;
            private int _lap;
            public readonly HashSet<HazardSet.SurfaceKind> Surfaces = new HashSet<HazardSet.SurfaceKind>();

            public KitTwin(int lead, int delay, int serverModifier, int clientModifier, uint roundStart = 0)
            {
                ServerHazards = KitLane(serverModifier);
                ServerHazards.RoundStartTick = roundStart;
                var clientHazards = KitLane(clientModifier);
                clientHazards.RoundStartTick = roundStart;
                var spawns = new Vec3[12];
                for (var i = 0; i < 12; i++)
                    spawns[i] = new Vec3(-2f, 0.05f, 60f + 5f * i);
                spawns[0] = new Vec3(-2f, 0.05f, 0f);
                Server = new SimCore(MovementConfig.For(serverModifier), new AnalyticWorld().Ground().AddHazards(ServerHazards), spawns, ServerHazards);
                Slot = Server.AddHuman(1);
                Client = new ClientPrediction(MovementConfig.For(clientModifier), new AnalyticWorld().Ground().AddHazards(clientHazards), clientHazards);
                _lead = lead;
                _delay = delay;
            }

            /// <summary>Runs the lane back and forth (turning at x = 40 and x = −1), steering to z = 0 from the predicted state, with hops and dives.</summary>
            private InputCmd Input(uint t)
            {
                if (Client.HasState)
                {
                    if (Client.State.PosX > 40000 && _dir > 0)
                    {
                        _dir = -1;
                        _lap++;
                    }
                    else if (Client.State.PosX < -1000 && _dir < 0)
                    {
                        _dir = 1;
                        _lap++;
                    }
                }

                var c = new InputCmd { MoveX = (sbyte)(127 * _dir) };
                if (Client.HasState)
                {
                    var z = Client.State.PosZ * 0.001f;
                    c.MoveY = (sbyte)Math.Max(-90, Math.Min(90, (int)(-z * 60f)));
                    // Every second outbound lap: stop in the doorway and let the door close on us (predicted push-out).
                    var x = Client.State.PosX;
                    if (_lap % 4 == 2 && x > 20500 && x < 23500)
                    {
                        if (_waitStart == 0)
                            _waitStart = t;
                        if (t - _waitStart < 260)
                        {
                            // Walk into the doorway whenever it is open and stand there until it closes on us.
                            c.MoveX = (sbyte)Math.Max(-127, Math.Min(127, (22000 - x) / 8));
                            return c;
                        }
                    }

                    if (_lap % 4 != 2)
                        _waitStart = 0;
                }

                if (t % 37 == 0)
                    c.Buttons = InputCmd.JumpPressed | InputCmd.JumpHeld;
                if (t % 97 == 0)
                    c.Buttons |= InputCmd.DivePressed | InputCmd.DiveHeld;
                return c;
            }

            public void Tick(uint k)
            {
                var cmd = Input(k + (uint)_lead);
                cmd.Seq = ++_seq;
                cmd.Tick = k + (uint)_lead;
                InputSanitizer.Sanitize(ref cmd);
                Client.Predict(in cmd);
                Server.Input(Slot).Offer(cmd, Server.NextTick);
                Server.Step();
                var st = Server.Slots[Slot].State;
                if (st.Surface != 0)
                    Surfaces.Add(ServerHazards.KindOf(st.Surface - 1, out _));
                if (Server.Tick % 2 == 0)
                {
                    var n = Server.WritePlayerStates(_players);
                    var h = Server.HeaderFor(Slot, n);
                    for (var i = 0; i < n; i++)
                        if (_players[i].Slot == Slot)
                            _down.Enqueue((Server.Tick + (uint)_delay, Server.Tick, _players[i], h));
                }

                while (_down.Count > 0 && _down.Peek().at <= Server.Tick)
                {
                    var m = _down.Dequeue();
                    Client.Reconcile(m.tick, in m.p, in m.h, m.tick / 30.0);
                }
            }
        }

        [Test]
        public void Prediction_MatchesServerBitForBit_ThroughEveryHazardType([Values(1, 3, 6)] int lead, [Values(2, 5)] int delay, [Values(0, 1, 3)] int modifier)
        {
            var twin = new KitTwin(lead, delay, modifier, modifier, roundStart: 40);
            for (uint k = 1; k <= 3000; k++)
                twin.Tick(k);
            var p = twin.Client;
            var hz = twin.ServerHazards;
            TestContext.WriteLine($"mod={ModifierEffects.Get(modifier).Key} snapshots={p.Snapshots} match={p.Matches} silent={p.SilentResyncs} corrections={p.Corrections} hazardMismatch={p.HazardMismatches} adopted={p.Adopted} respawns={p.Respawns} " +
                                  $"serverHits={twin.Server.HazardHits} predictedHits={p.PredictedHits} launches={hz.Launches} doorPushes={hz.DoorPushes} surfaces={string.Join(",", twin.Surfaces)} falls={twin.Server.Slots[twin.Slot].Respawns}");
            if (p.Corrections > 0)
                TestContext.WriteLine($"last mismatch at {p.LastMismatchTick}:\n auth {p.LastMismatchAuth}\n pred {p.LastMismatchPredicted}");
            Assert.That(p.Corrections, Is.EqualTo(0));
            Assert.That(p.SilentResyncs, Is.EqualTo(0));
            Assert.That(p.HazardMismatches, Is.EqualTo(0));
            Assert.That(p.Adopted, Is.EqualTo(1), "only the first snapshot");
            Assert.That(p.Matches + p.Respawns, Is.EqualTo(p.Snapshots - p.Adopted));
            // The run really exercises the kit.
            Assert.That(twin.Server.HazardHits, Is.GreaterThanOrEqualTo(3), "knock hazards hit");
            Assert.That(hz.Launches, Is.GreaterThanOrEqualTo(1), "spring pad");
            Assert.That(twin.Surfaces, Does.Contain(HazardSet.SurfaceKind.Conveyor));
            Assert.That(twin.Surfaces, Does.Contain(HazardSet.SurfaceKind.TurnTable));
            Assert.That(twin.Surfaces, Does.Contain(HazardSet.SurfaceKind.Platform));
            Assert.That(hz.DoorPushes, Is.GreaterThanOrEqualTo(1), "a door closed on the player and pushed them out");
        }

        [Test]
        public void Prediction_WithADifferentModifier_DoesNotMatch()
        {
            // Proves the modifier really reaches both sides' motor: a client that predicts with None against a MoonBounce server is corrected.
            var twin = new KitTwin(3, 3, serverModifier: 1, clientModifier: 0);
            for (uint k = 1; k <= 600; k++)
                twin.Tick(k);
            Assert.That(twin.Client.Corrections + twin.Client.SilentResyncs, Is.GreaterThan(5));
        }
    }
}
