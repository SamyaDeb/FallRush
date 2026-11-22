using System.IO;
using FallRush.OnChain;
using FallRush.Shared;
using NUnit.Framework;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>P3-D: the C# World decoder and the World → match-screen feed on the emulator's fixtures
    /// (tools/chain-emulator/test/fixtures: a seeded 3-player Lid Flip Final, slot 0 human, 1–2 bots, champion 1).</summary>
    public class P3WorldTests
    {
        private static byte[] Acc(string n) => File.ReadAllBytes(Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "tools", "chain-emulator", "test", "fixtures", n)));
        private static readonly WorldAccLayout L = new WorldAccLayout();

        private static WorldView View(byte[] a) => new WorldView(a, L.offset, L.WorldLen(a, a.Length));

        [Test]
        public void DecodesTheFixtures()
        {
            var a = Acc("waiting.worldacc");
            var w = View(a);
            Assert.IsTrue(w.Valid);
            Assert.AreEqual(6u, w.Tick);
            Assert.AreEqual(6u, L.Tick(a));
            Assert.AreEqual(0x10001u, L.CoreVersion(a));
            Assert.AreEqual(WorldView.PhaseSandbox, w.Phase);
            Assert.AreEqual(0b111u, w.ActiveMask);

            a = Acc("rush.worldacc");
            w = View(a);
            Assert.AreEqual(463u, w.Tick);
            Assert.AreEqual(WorldView.ModeFinal, w.Mode);
            Assert.AreEqual(WorldView.PhaseRush, w.Phase);
            Assert.AreEqual(163u, w.RushTick);
            Assert.AreEqual(163u, w.RoundStart);
            Assert.AreEqual(3763u, w.RushEnd);
            Assert.AreEqual(60, w.IntroTicks);
            Assert.AreEqual(90, w.CountdownTicks);
            Assert.AreEqual(3, w.MapKey);
            Assert.IsTrue(w.IsBot(1) && w.IsBot(2) && !w.IsBot(0));

            a = Acc("ended.worldacc");
            w = View(a);
            Assert.AreEqual(1276u, w.Tick);
            Assert.AreEqual(1276u, L.Applied(a, 0));
            Assert.AreEqual(3, L.Flags(a));
            Assert.AreEqual(WorldView.PhaseEnded, w.Phase);
            Assert.IsTrue(w.HasResult);
            Assert.AreEqual(3, w.ResultCount);
            Assert.AreEqual(1, w.Champion);
            Assert.AreEqual(2, w.ResultOrder(1));
            Assert.AreEqual(2, w.ResultReason(1));
            Assert.AreEqual(-5126, w.PosX(0));
            Assert.AreEqual(-13107, w.PosZ(0));
            Assert.IsTrue(w.Out(0));
            Assert.AreEqual(523u, w.OutTick(0));
            Assert.AreEqual(2, w.OutReason(0));
            Assert.AreEqual(1, w.Falls(0));
        }

        [Test]
        public void FeedBuildsTheFinalScreens()
        {
            var f = new OnChainMatchFeed { OwnSlot = 0 };
            f.Names[0] = "Me";
            var rush = View(Acc("rush.worldacc"));
            f.OnWorld(in rush, 10.0);
            Assert.IsTrue(f.HasRound);
            Assert.AreEqual(163u, f.Round.RoundStartTick);
            Assert.AreEqual(RoundPhase.Rush, f.Race.Phase);
            Assert.AreEqual(MatchPhase.RoundActive, f.Match.Phase);
            Assert.AreEqual(3, f.Race.FinisherCount, "ALIVE 3 at tick 463");
            Assert.AreEqual(4, f.Match.Round);
            Assert.AreEqual(3, f.Match.Slots.Length);
            Assert.AreEqual("Me", f.Match.Slots[0].Name);
            Assert.IsTrue(f.Match.Slots[1].Has(MatchSlotInfo.Bot));

            var ended = View(Acc("ended.worldacc"));
            f.OnWorld(in ended, 20.0);
            Assert.AreEqual(1, f.Match.Champion);
            Assert.AreEqual(MatchPhase.RoundResult, f.Match.Phase);
            Assert.AreEqual(MatchPhase.Champion, f.PhaseAt(in ended, 20.0 + OnChainMatchFeed.ResultSeconds + 1));
            Assert.AreEqual(MatchPhase.Done, f.PhaseAt(in ended, 20.0 + OnChainMatchFeed.ResultSeconds + OnChainMatchFeed.ChampionSeconds + 1));
            Assert.AreEqual(1, f.Match.Slots[1].Places[3]);
            Assert.AreEqual(4, f.Match.Slots[0].EliminatedRound);
            // slot 0 is out: not in the snapshot; OUT + result events were raised
            foreach (var p in new System.ArraySegment<PlayerState>(f.Players, 0, f.PlayerCount))
                Assert.AreNotEqual(0, p.Slot);
            var outs = 0;
            var qual = 0;
            foreach (var e in f.NewEvents)
            {
                if (e.Type == RaceEventType.Out) outs++;
                if (e.Type == RaceEventType.Qualified) { qual++; Assert.AreEqual(1, e.Slot); }
            }

            Assert.AreEqual(1, qual);
            Assert.GreaterOrEqual(outs, 2);
        }

        [Test]
        public void PredictionRebasesExactlyOnTheArenaWorld()
        {
            var core = RushCoreAbi.Create();
            var map = File.ReadAllBytes(Path.Combine(Application.dataPath, "_Game", "Scripts", "FeelLab", "Resources", "feellab", "CrumbleShelf.bytes"));
            var a = Acc("rush.worldacc");
            var w = View(a);
            using var p = new OnChainPrediction(core, map, 30, 0b111, 0b110, 0, 120, 2);
            var cmd = new InputCmd { MoveX = 90 };
            for (var t = w.Tick + 1; t <= w.Tick + 6; t++)
                p.SetInput(t, in cmd);
            p.OnAuthoritative(a, L.offset, WorldView.Size, w.Tick, w.Tick + 6, 1.0, w.Tick);
            Assert.AreEqual(w.Tick + 6, p.Present);
            var h1 = p.Hash();
            // the same replay done by hand: set the World and step 6 ticks
            var h = core.Init(map, RushCoreAbi.Cfg(30, 0b111, 0b110, 0));
            var world = new byte[WorldView.Size];
            System.Buffer.BlockCopy(a, L.offset, world, 0, WorldView.Size);
            var inputs = new byte[6 * 72];
            for (var k = 0; k < 6; k++)
                RushCoreAbi.PutInput(inputs, k * 72, 0, 90, 0, 0, 0);
            core.Run(h, world, world.Length, 6, inputs, 0, null);
            Assert.AreEqual(core.Hash(h), h1);
            // a second World at the same tick with the same inputs = no correction
            p.OnAuthoritative(a, L.offset, WorldView.Size, w.Tick, w.Tick + 6, 1.1, w.Tick);
            Assert.AreEqual(0, p.Corrections);
            core.Free(h);
        }
    }
}
