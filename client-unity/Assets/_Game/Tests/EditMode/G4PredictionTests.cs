using System;
using System.Collections.Generic;
using FallRush.Net;
using FallRush.Shared;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>
    /// G4.1: client prediction against a server twin through the G4 sandbox section (the real <c>Map_Sandbox</c> scene's
    /// resolved ball lane and 7 × 7 lid grid, seeded), bit-identical: ball knocks and falls through flipped lids are
    /// predicted, so corrections stay 0. Analytic geometry (the deck) as in the M3 kit twin, because two motor worlds must
    /// not share one set of PhysX colliders in one process.
    /// </summary>
    public class G4PredictionTests
    {
        private static HazardSet SandboxG4(ulong seed, int variant)
        {
            var scene = EditorSceneManager.OpenScene("Assets/_Game/Scenes/Map_Sandbox.unity", OpenSceneMode.Single);
            MapInfo map = null;
            foreach (var root in scene.GetRootGameObjects())
                map ??= root.GetComponentInChildren<MapInfo>();
            Assert.That(map, Is.Not.Null);
            Assert.That(map.rollingBalls.Length, Is.EqualTo(1), "sandbox G4 section: one ball lane");
            Assert.That(map.lidGrids.Length, Is.EqualTo(1), "sandbox G4 section: one lid grid");
            var full = map.BuildHazards(new RoundParams { MapId = MapPoolV1.Sandbox, HazardSeed = seed, Variant = (byte)variant }, out _);
            Assert.That(map.Validate(new RoundParams { MapId = MapPoolV1.Sandbox, HazardSeed = seed, Variant = (byte)variant }), Is.Empty);
            Assert.That(full.LidGrids[0].N, Is.EqualTo(7));
            Assert.That(full.LidGrids[0].Seed, Is.Not.EqualTo(0u), "seeded from the hazard seed");
            return new HazardSet(null, null) { RollingBalls = full.RollingBalls, LidGrids = full.LidGrids, RoundStartTick = 40 };
        }

        /// <summary>The G4 deck (x 44–64, z −30…−10.5, top 0) plus the hazard set's lids.</summary>
        private static AnalyticWorld World(HazardSet hz) =>
            new AnalyticWorld().AddBox(new Vec3(44f, -1f, -30f), new Vec3(64f, 0f, -10.5f)).AddHazards(hz);

        private sealed class Twin
        {
            public readonly SimCore Server;
            public readonly ClientPrediction Client;
            public readonly int Slot;
            private readonly int _lead, _delay;
            private readonly Queue<(uint at, uint tick, PlayerState p, SnapHeader h)> _down = new Queue<(uint, uint, PlayerState, SnapHeader)>();
            private readonly PlayerState[] _players = new PlayerState[12];
            private uint _seq;

            public Twin(int lead, int delay, ulong seed, int variant)
            {
                var sh = SandboxG4(seed, variant);
                var ch = SandboxG4(seed, variant);
                var spawns = new Vec3[12];
                for (var i = 0; i < 12; i++)
                    spawns[i] = new Vec3(46f + i, 0.05f, -12f);
                Server = new SimCore(MovementConfig.Default, World(sh), spawns, sh);
                Slot = Server.AddHuman(1);
                Client = new ClientPrediction(MovementConfig.Default, World(ch), ch);
                _lead = lead;
                _delay = delay;
            }

            /// <summary>
            /// Figure-of-eight over the section, steered from the predicted state: across the ball lane (x 47 ↔ 61 at
            /// z −20), then south onto the lid grid (to z −40) and back; hops every 1.3 s.
            /// </summary>
            private InputCmd Input(uint t)
            {
                var c = new InputCmd();
                if (!Client.HasState)
                    return c;
                var x = Client.State.PosX * 0.001f;
                var z = Client.State.PosZ * 0.001f;
                var leg = (t / 150) % 4;
                var tx = leg == 0 ? 61f : leg == 1 ? 47f : leg == 2 ? 54f + 3f * (float)Math.Sin(t * 0.05) : 50f;
                var tz = leg <= 1 ? -20f : leg == 2 ? -40f : -15f;
                var dx = tx - x;
                var dz = tz - z;
                var l = (float)Math.Sqrt(dx * dx + dz * dz);
                if (l > 0.3f)
                {
                    c.MoveX = (sbyte)Math.Max(-127, Math.Min(127, (int)(dx / l * 127)));
                    c.MoveY = (sbyte)Math.Max(-127, Math.Min(127, (int)(dz / l * 127)));
                }

                if (t % 39 == 0)
                    c.Buttons = InputCmd.JumpPressed | InputCmd.JumpHeld;
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
        public void Prediction_MatchesServerBitForBit_ThroughBallLaneAndLidGrid([Values(1, 3, 6)] int lead, [Values(2, 5)] int delay, [Values(0, 1)] int variant)
        {
            var twin = new Twin(lead, delay, 0xBA11_0000UL + (ulong)(lead * 10 + delay), variant);
            for (uint k = 0; k < 3600; k++)
                twin.Tick(k);
            var p = twin.Client;
            var hits = twin.Server.HazardHits;
            var falls = twin.Server.FallsBySlot[twin.Slot];
            TestContext.WriteLine($"variant={variant} snapshots={p.Snapshots} match={p.Matches} silent={p.SilentResyncs} corrections={p.Corrections} hazardMismatch={p.HazardMismatches} respawns={p.Respawns} ballHits={hits} falls={falls} predictedHits={p.PredictedHits}");
            Assert.That(p.Snapshots, Is.GreaterThan(1000));
            Assert.That(hits, Is.GreaterThan(0), "the run crossed the ball lane and got knocked");
            Assert.That(falls, Is.GreaterThan(0), "the run fell through a flipped / retired lid at least once");
            Assert.That(p.Corrections, Is.EqualTo(0));
            Assert.That(p.HazardMismatches, Is.EqualTo(0));
        }
    }
}
