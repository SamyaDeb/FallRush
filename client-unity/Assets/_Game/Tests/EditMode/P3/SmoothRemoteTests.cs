using System;
using System.Collections.Generic;
using System.IO;
using FallRush.Net;
using FallRush.OnChain;
using FallRush.Shared;
using NUnit.Framework;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>
    /// Smooth run (2026-11-25): other players teleported in on-chain rounds. Rollup Worlds arrive late and in bursts
    /// (load tests: confirm p50 ≈ 0.5 s, 0.5–10 complete Worlds/s), and the remote buffer was the game server's
    /// 100–150 ms one: extrapolate 150 ms → freeze → jump, and any two Worlds &gt; 2 m apart snapped. Each scenario runs
    /// the Classic tuning (shows the bug) and the on-chain tuning (the fix) on the same arrival timeline.
    /// </summary>
    public class SmoothRemoteTests
    {
        private const double Dt = 1.0 / 30;
        private const float Frame = 1f / 60;

        /// <summary>One World: tick, arrival time, x (mm).</summary>
        private struct Arrival
        {
            public uint Tick;
            public double At;
            public int X;
        }

        private static PlayerState[] Player(int xMm, int velX) => new[] { new PlayerState { Slot = 3, PosX = xMm, VelX = (short)velX, Flags = MotorState.Grounded } };

        private sealed class Run
        {
            public float MaxStep, MinStep;
            public int Frames, FrozenFrames;
            public List<float> Xs = new List<float>();
        }

        /// <summary>Plays the arrivals at 60 fps and records the rendered x of slot 3 after <paramref name="warmup"/> s.</summary>
        private static Run Play(Interpolation.Profile prof, List<Arrival> arrivals, double start, double end, double warmup, int velX = 600)
        {
            var it = new Interpolation(Dt) { Tuning = prof };
            var r = new Run { MaxStep = 0f, MinStep = float.MaxValue };
            var next = 0;
            var now = start;
            var have = false;
            var last = 0f;
            while (now < end)
            {
                now += Frame;
                while (next < arrivals.Count && arrivals[next].At <= now)
                {
                    var a = arrivals[next++];
                    it.Add(a.Tick, Player(a.X, velX), a.At);
                }

                it.BeginFrame(now, Frame);
                if (!it.Sample(3, out var feet, out _, out _))
                    continue;
                if (now - start < warmup)
                {
                    last = feet.X;
                    have = true;
                    continue;
                }

                if (have)
                {
                    var step = feet.X - last;
                    r.MaxStep = Math.Max(r.MaxStep, Math.Abs(step));
                    r.MinStep = Math.Min(r.MinStep, step);
                    r.Frames++;
                    if (Math.Abs(step) < 0.005f)
                        r.FrozenFrames++;
                }

                r.Xs.Add(feet.X);
                last = feet.X;
                have = true;
            }

            return r;
        }

        /// <summary>A runner at 6 m/s (0.2 m per tick): complete Worlds every 3–15 ticks, 0–700 ms late, in order.</summary>
        private static List<Arrival> Bursty(int seed, double seconds, out double end)
        {
            var rng = new System.Random(seed);
            var list = new List<Arrival>();
            uint tick = 300;
            var t0 = 10.0;
            var lastAt = t0;
            while (true)
            {
                tick += (uint)rng.Next(3, 16);
                var ideal = t0 + (tick - 300) * Dt;
                if (ideal - t0 > seconds)
                    break;
                var at = Math.Max(lastAt, ideal + rng.NextDouble() * 0.7); // ordered (one socket stream)
                lastAt = at;
                list.Add(new Arrival { Tick = tick, At = at, X = (int)(tick * 200) });
            }

            end = t0 + seconds;
            return list;
        }

        [Test]
        public void ClassicProfileIsTheOldConstants()
        {
            var c = Interpolation.Profile.Classic;
            Assert.AreEqual(Interpolation.MinDelay, c.MinDelay);
            Assert.AreEqual(Interpolation.MaxDelay, c.MaxDelay);
            Assert.AreEqual(Interpolation.MaxExtrapolation, c.MaxExtrapolation);
            Assert.AreEqual(Interpolation.DelaySlewPerSecond, c.DelaySlewPerSecond);
            Assert.AreEqual(0f, c.EaseTau);
            Assert.AreEqual(0f, c.BlendMs);
            Assert.AreEqual(0f, c.TeleportSpeed);
            Assert.AreSame(c, new Interpolation(Dt).Tuning, "Classic is the default (the game server's feed is unchanged)");
        }

        [Test]
        public void BurstyWorlds_RunningPlayer_OnChainIsSmooth_ClassicJumps([Values(1, 2, 3, 4)] int seed)
        {
            var arrivals = Bursty(seed, 20, out var end);
            var classic = Play(Interpolation.Profile.Classic, arrivals, 10, end + 1, 3);
            var onchain = Play(Interpolation.Profile.OnChain, arrivals, 10, end, 3);
            // a 6 m/s runner moves 0.1 m per 60 fps frame
            Assert.That(classic.MaxStep, Is.GreaterThan(0.5f), "the bug: Classic tuning jumps on this timeline");
            Assert.That(onchain.MaxStep, Is.LessThan(0.35f), "no teleport: largest frame step");
            Assert.That(onchain.MinStep, Is.GreaterThan(-0.05f), "never pulled backwards");
            Assert.That((float)onchain.FrozenFrames / onchain.Frames, Is.LessThan(0.05f), "frozen frames share");
            TestContext.WriteLine($"seed {seed}: classic max step {classic.MaxStep:F2} m frozen {classic.FrozenFrames}/{classic.Frames}; onchain max {onchain.MaxStep:F2} m frozen {onchain.FrozenFrames}/{onchain.Frames}");
        }

        [Test]
        public void SparseWorldsHalfASecondApart_DoNotSnapOnChain()
        {
            // a World every 15 ticks (0.5 s, 3 m apart at 6 m/s), each 250 ms late
            var list = new List<Arrival>();
            for (uint tick = 300; tick < 300 + 30 * 20; tick += 15)
                list.Add(new Arrival { Tick = tick, At = 10 + (tick - 300) * Dt + 0.25, X = (int)(tick * 200) });
            var classic = Play(Interpolation.Profile.Classic, list, 10, 30, 3);
            var onchain = Play(Interpolation.Profile.OnChain, list, 10, 30, 3);
            Assert.That(classic.MaxStep, Is.GreaterThan(1f), "the bug: pairs > 2 m apart snapped");
            Assert.That(onchain.MaxStep, Is.LessThan(0.2f));
        }

        [Test]
        public void RespawnTeleportStillSnapsOnChain()
        {
            var list = new List<Arrival>();
            for (uint tick = 300; tick < 300 + 30 * 6; tick += 3)
                list.Add(new Arrival { Tick = tick, At = 10 + (tick - 300) * Dt + 0.2, X = (int)(tick * 200) });
            // respawn: 30 m back to a checkpoint in one World, then standing
            var respawnTick = 300u + 30 * 6;
            for (var tick = respawnTick; tick < respawnTick + 90; tick += 3)
                list.Add(new Arrival { Tick = tick, At = 10 + (tick - 300) * Dt + 0.2, X = (int)(respawnTick * 200) - 30000 });
            var it = new Interpolation(Dt) { Tuning = Interpolation.Profile.OnChain };
            var next = 0;
            var slid = 0;
            var drops = 0;
            var landed = false;
            var spot = respawnTick * 0.2f - 30f;
            var have = false;
            var last = 0f;
            for (var now = 10.0; now < 20; now += Frame)
            {
                while (next < list.Count && list[next].At <= now)
                {
                    var a = list[next];
                    it.Add(a.Tick, Player(a.X, a.Tick >= respawnTick ? 0 : 600), a.At);
                    next++;
                }

                it.BeginFrame(now, Frame);
                if (!it.Sample(3, out var feet, out _, out _))
                    continue;
                if (have)
                {
                    var step = feet.X - last;
                    if (step < -20f)
                        drops++; // the respawn: one frame
                    else if (step < -0.5f)
                        slid++; // a slide back across the map
                }

                landed |= Math.Abs(feet.X - spot) < 0.05f;
                last = feet.X;
                have = true;
            }

            Assert.AreEqual(1, drops, "a respawn snaps in one frame");
            Assert.AreEqual(0, slid, "no slide back across the map");
            Assert.IsTrue(landed);
        }

        [Test]
        public void StallThenResume_EasesToAStopAndBlendsBack()
        {
            var list = new List<Arrival>();
            uint tick = 300;
            for (; tick < 300 + 30 * 5; tick += 3)
                list.Add(new Arrival { Tick = tick, At = 10 + (tick - 300) * Dt + 0.2, X = (int)(tick * 200) });
            // 1.5 s without a World (a rollup stall), then the backlog arrives at once and normal flow resumes
            var resumeAt = 10 + (tick - 300) * Dt + 0.2 + 1.5;
            for (; tick < 300 + 30 * 12; tick += 3)
            {
                var ideal = 10 + (tick - 300) * Dt + 0.2;
                list.Add(new Arrival { Tick = tick, At = Math.Max(ideal, resumeAt), X = (int)(tick * 200) });
            }

            var classic = Play(Interpolation.Profile.Classic, list, 10, 22, 3);
            var onchain = Play(Interpolation.Profile.OnChain, list, 10, 22, 3);
            Assert.That(classic.MaxStep, Is.GreaterThan(1f), "the bug: frozen, then a jump on resume");
            Assert.That(onchain.MaxStep, Is.LessThan(0.5f), "the catch-up is blended");
            Assert.That(onchain.MinStep, Is.GreaterThan(-0.05f));
        }

        [Test]
        public void SamplingTwiceInAFrameIsIdempotent()
        {
            var it = new Interpolation(Dt) { Tuning = Interpolation.Profile.OnChain };
            var now = 10.0;
            uint tick = 300;
            for (var f = 0; f < 300; f++)
            {
                now += Frame;
                if (f % 20 == 0)
                {
                    tick += 10;
                    it.Add(tick, Player((int)(tick * 200), 600), now);
                }

                it.BeginFrame(now, Frame);
                it.Sample(3, out var a, out _, out _);
                it.Sample(3, out var b, out _, out _);
                Assert.AreEqual(a.X, b.X, 1e-6f);
            }
        }

        // ------------------------------------------------------------------ bots from the prediction

        private static byte[] Acc(string n) => File.ReadAllBytes(Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "tools", "chain-emulator", "test", "fixtures", n)));
        private static readonly WorldAccLayout L = new WorldAccLayout();

        [Test]
        public void PredictionDrawsBotsAtThePredictedPresent_ExactAndWithoutCorrections()
        {
            var core = RushCoreAbi.Create();
            var map = File.ReadAllBytes(Path.Combine(Application.dataPath, "_Game", "Scripts", "FeelLab", "Resources", "feellab", "CrumbleShelf.bytes"));
            var a = Acc("rush.worldacc");
            var w = new WorldView(a, L.offset, L.WorldLen(a, a.Length));
            using var p = new OnChainPrediction(core, map, 30, 0b111, 0b110, 0, 120, 2);
            Assert.IsTrue(p.IsBot(1) && p.IsBot(2) && !p.IsBot(0));
            p.OnAuthoritative(a, L.offset, WorldView.Size, w.Tick, w.Tick + 6, 1.0, w.Tick);

            // the same 6 ticks by hand (own slot 0 has no command = repeats its last one, as in the prediction)
            var h = core.Init(map, RushCoreAbi.Cfg(30, 0b111, 0b110, 0));
            var world = new byte[WorldView.Size];
            Buffer.BlockCopy(a, L.offset, world, 0, WorldView.Size);
            core.Run(h, world, world.Length, 6, new byte[6 * 72], 0, null);
            var rec = new byte[12 * 36];
            core.Players(h, rec);
            core.Free(h);
            for (var s = 1; s <= 2; s++)
            {
                Assert.IsTrue(p.RenderSlot(s, 1f, out var feet), $"bot {s} has a predicted record");
                var exp = new Vector3(BitConverter.ToInt32(rec, s * 36), BitConverter.ToInt32(rec, s * 36 + 4), BitConverter.ToInt32(rec, s * 36 + 8)) * 0.001f;
                Assert.That((feet - exp).magnitude, Is.LessThan(1e-4f), $"bot {s} drawn where rush-core puts it at the present");
            }

            // the same World again: no slot correction, no offsets
            p.OnAuthoritative(a, L.offset, WorldView.Size, w.Tick, w.Tick + 6, 1.1, w.Tick);
            Assert.AreEqual(0, p.SlotCorrections);
            Assert.AreEqual(0, p.SlotSnaps);
            Assert.IsFalse(p.RenderSlot(12, 1f, out _));
        }

        [Test]
        public void PredictionSlotOffsetsDecay()
        {
            var core = RushCoreAbi.Create();
            var map = File.ReadAllBytes(Path.Combine(Application.dataPath, "_Game", "Scripts", "FeelLab", "Resources", "feellab", "CrumbleShelf.bytes"));
            var a = Acc("rush.worldacc");
            var w = new WorldView(a, L.offset, L.WorldLen(a, a.Length));
            using var p = new OnChainPrediction(core, map, 30, 0b111, 0b110, 0, 120, 2);
            p.OnAuthoritative(a, L.offset, WorldView.Size, w.Tick, w.Tick + 3, 1.0, w.Tick);
            p.StepTo(w.Tick + 12);
            p.RenderSlot(1, 1f, out var ahead);
            // an older World arrives (rebase back to tick w + 3): the drawn bot must not jump
            p.OnAuthoritative(a, L.offset, WorldView.Size, w.Tick, w.Tick + 3, 1.2, w.Tick);
            p.RenderSlot(1, 1f, out var after);
            Assert.That((after - ahead).magnitude, Is.LessThan(1e-3f), "the rebase jump went into the slot's offset");
            for (var i = 0; i < 60; i++)
                p.UpdateSlotSmoothing(1f / 60);
            // after 1 s the offset is gone: drawn = a fresh prediction of the same World at the same present
            using var fresh = new OnChainPrediction(core, map, 30, 0b111, 0b110, 0, 120, 2);
            fresh.OnAuthoritative(a, L.offset, WorldView.Size, w.Tick, w.Tick + 3, 1.0, w.Tick);
            Assert.IsTrue(p.RenderSlot(1, 1f, out var settled));
            Assert.IsTrue(fresh.RenderSlot(1, 1f, out var exact));
            Assert.That((settled - exact).magnitude, Is.LessThan(1e-4f));
        }
    
        [Test]
        public void SpectatorPredictionDrawsEveryoneFromRushCore()
        {
            // floor run: a spectator (no own slot) still runs rush-core, so every Rushling stands on the real floors
            var core = RushCoreAbi.Create();
            var map = File.ReadAllBytes(Path.Combine(Application.dataPath, "_Game", "Scripts", "FeelLab", "Resources", "feellab", "CrumbleShelf.bytes"));
            var a = Acc("rush.worldacc");
            var w = new WorldView(a, L.offset, L.WorldLen(a, a.Length));
            using var p = new OnChainPrediction(core, map, 30, 0b111, 0b110, -1, 120, 2);
            p.OnAuthoritative(a, L.offset, WorldView.Size, w.Tick, w.Tick + 6, 1.0, w.Tick);
            Assert.IsTrue(p.HasState);
            Assert.AreEqual(w.Tick + 6, p.Present);

            var h = core.Init(map, RushCoreAbi.Cfg(30, 0b111, 0b110, 0));
            var world = new byte[WorldView.Size];
            Buffer.BlockCopy(a, L.offset, world, 0, WorldView.Size);
            core.Run(h, world, world.Length, 6, new byte[6 * 72], 0, null);
            var rec = new byte[12 * 36];
            core.Players(h, rec);
            core.Free(h);
            for (var s = 0; s <= 2; s++)
            {
                Assert.IsTrue(p.RenderSlot(s, 1f, out var feet), $"slot {s} drawn");
                var exp = new Vector3(BitConverter.ToInt32(rec, s * 36), BitConverter.ToInt32(rec, s * 36 + 4), BitConverter.ToInt32(rec, s * 36 + 8)) * 0.001f;
                Assert.That((feet - exp).magnitude, Is.LessThan(1e-4f), $"slot {s} (human 0, bots 1–2) where rush-core puts it");
            }
            p.StepTo(w.Tick + 9); // stepping without own inputs works
            Assert.AreEqual(w.Tick + 9, p.Present);
            Assert.AreEqual(0, p.Corrections, "no own record to correct");
        }
}
}
