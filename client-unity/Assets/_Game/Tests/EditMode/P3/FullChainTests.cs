using System;
using System.IO;
using System.Linq;
using FallRush.Net;
using FallRush.OnChain;
using FallRush.Shared;
using NUnit.Framework;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>
    /// fullchain (docs/proof/fullchain/CONTRACT.md §1 + §4): the whole-match feed (rounds 1–4 from the World's director),
    /// the game server's full-match result / failure logic, the all-mode rules, the fonc flag, base58 and the map follow.
    /// Worlds = the emulator fixtures (a 3-player Lid Flip) turned into version-2 worlds with a director written in.
    /// </summary>
    public class FullChainTests
    {
        /// <summary>An inner round that ended (its timer ran out): the all-mode rules must not care.</summary>
        private sealed class StubRules : IRoundRules
        {
            public string Mode => "race";
            public RoundPhase Phase => RoundPhase.Ended;
            public bool IsOver => true;
            public int IntroTicks { get; set; }
            public int CountdownTicks { get; set; }
            public uint TimerTicks => 2700;
            public uint RushTick => 150;
            public int Target { get; set; } = 8;
            public bool[] Participants { get; set; } = new bool[12];
            public uint Begin(SimCore sim, uint tick) => tick + 150;
            public RoundResult RoundOutcome => null;
            public System.Collections.Generic.List<RaceEvent> Pending { get; } = new System.Collections.Generic.List<RaceEvent>();
            public RaceStateMessage StateMessage() => new RaceStateMessage { FinisherSlots = Array.Empty<byte>(), FinisherTicks = Array.Empty<uint>() };
            public event Action<RoundPhase> PhaseChanged { add { } remove { } }
            public int LiveCount => 0;
            public bool InRound(int slot) => false;
            public void BeginTick(SimCore sim, uint tick) { }
            public void EndTick(SimCore sim, uint tick) { }
            public bool AcceptsInput(SimCore sim, int slot) => false;
            public bool Simulates(SimCore sim, int slot) => false;
            public bool Collides(SimCore sim, int slot) => false;
            public bool Respawn(SimCore sim, int slot) => false;
            public byte RaceByte(int slot) => 0;
        }

        private static byte[] Acc(string n) => File.ReadAllBytes(Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "tools", "chain-emulator", "test", "fixtures", n)));
        private static readonly WorldAccLayout L = new WorldAccLayout();

        /// <summary>A fixture as a v2 world of a whole match: director phase / round, roster 0–2 (1, 2 bots).</summary>
        private static byte[] Full(string fixture, byte dirPhase, byte round, ushort alive = 0b111, byte champion = 255, byte[] elim = null)
        {
            var a = Acc(fixture);
            var d = DirectorWorld(dirPhase, round, 0b111, alive, 0b110, champion, elim);
            Buffer.BlockCopy(d, ChainDirector.InWorld, a, L.offset + ChainDirector.InWorld, ChainDirector.Size);
            a[L.offset] = 2; // World version 2 (start_sim)
            a[L.offset + 6] = round; // World.round_index
            return a;
        }

        /// <summary>A World with only the director written (version 2, the rest zero).</summary>
        private static byte[] DirectorWorld(byte phase, byte round, ushort roster, ushort alive, ushort bots, byte champion, byte[] elim, int prefix = 0)
        {
            var b = new byte[prefix + 2037];
            b[prefix] = 2;
            var o = prefix + ChainDirector.InWorld;
            b[o] = phase;
            System.BitConverter.GetBytes(1000u).CopyTo(b, o + 1);
            System.BitConverter.GetBytes(1180u).CopyTo(b, o + 5);
            b[o + 9] = round;
            b[o + 10] = 4;
            b[o + 11] = 12;
            for (var i = 0; i < 32; i++)
                b[o + 12 + i] = (byte)(0xA0 + i);
            System.BitConverter.GetBytes(37u).CopyTo(b, o + 76);
            for (var r = 0; r < 4; r++)
            {
                b[o + 80 + r] = (byte)r;
                b[o + 84 + r] = (byte)(r & 1);
                b[o + 88 + r] = 0;
            }

            new byte[] { 8, 5, 3, 1 }.CopyTo(b, o + 92);
            System.BitConverter.GetBytes(roster).CopyTo(b, o + 96);
            System.BitConverter.GetBytes(alive).CopyTo(b, o + 98);
            System.BitConverter.GetBytes(bots).CopyTo(b, o + 100);
            b[o + 109] = champion;
            if (elim != null)
                System.Array.Copy(elim, 0, b, o + 110, 12);
            return b;
        }

        private static WorldView View(byte[] a) => new WorldView(a, L.offset, L.WorldLen(a, a.Length));

        [Test]
        public void WorldViewAcceptsVersion2AndReadsTheDirector()
        {
            var a = Full("rush.worldacc", ChainDirector.RoundActive, 2);
            var w = View(a);
            Assert.IsTrue(w.Valid);
            Assert.AreEqual(2, w.Version);
            Assert.AreEqual(ChainDirector.RoundActive, w.Director.Phase);
            Assert.AreEqual(2, w.Director.Round);
            a[L.offset] = 3;
            Assert.IsFalse(View(a).Valid, "an unknown World version is still refused");
        }

        [Test]
        public void FullFeedFollowsTheDirectorRound()
        {
            var f = new OnChainMatchFeed { OwnSlot = 0, Full = true };
            f.Names[0] = "Me";
            var rush = View(Full("rush.worldacc", ChainDirector.RoundActive, 2));
            f.OnWorld(in rush, 10.0);
            Assert.AreEqual(MatchPhase.RoundActive, f.Match.Phase);
            Assert.AreEqual(2, f.Match.Round, "round from the director, not the Final's 4");
            Assert.AreEqual(4, f.Match.RoundCount);
            Assert.AreEqual(2, f.Round.RoundIndex);
            CollectionAssert.AreEqual(new byte[] { 8, 5, 3, 1 }, f.Match.Targets);
            CollectionAssert.AreEqual(new byte[] { 0, 1, 2, 3 }, f.Match.Maps);
            Assert.AreEqual(1000u, f.Match.PhaseStartTick);
            Assert.AreEqual(1180u, f.Match.PhaseEndTick);
            Assert.AreEqual(37u, f.Match.EventCount);
            Assert.AreEqual(0xA0, f.Match.Seed[0], "the arena's seed");
            Assert.AreEqual(255, f.Match.Champion);
            Assert.AreEqual(3, f.Match.Slots.Length);
            Assert.AreEqual("Me", f.Match.Slots[0].Name);
            Assert.IsTrue(f.Match.Slots[1].Has(MatchSlotInfo.Bot));
            Assert.IsTrue(f.Match.Slots.All(s => s.Has(MatchSlotInfo.Alive)));
            Assert.AreEqual(MatchPhase.Intermission, f.PhaseAt(View(Full("rush.worldacc", ChainDirector.Intermission, 2)), 11.0));
            Assert.AreEqual(MatchPhase.Dealing, f.PhaseAt(View(Full("rush.worldacc", ChainDirector.Dealing, 0)), 11.0));
        }

        [Test]
        public void FullFeedRoundResultQualifiesByRankAndRecordsPlaces()
        {
            var f = new OnChainMatchFeed { OwnSlot = 0, Full = true };
            f.OnWorld(View(Full("rush.worldacc", ChainDirector.RoundActive, 2)), 10.0);
            var a = Full("ended.worldacc", ChainDirector.RoundResult, 2);
            // the fixture's result: order 1, 2, 0 (champion 1); a round-2 result qualifies ranks 0 and 1
            var q = L.offset + WorldView.RoundOffset + WorldView.RoundResultInRound + 13;
            a[q] = 0b011;
            a[q + 1] = 0;
            var ended = View(a);
            f.OnWorld(in ended, 20.0);
            Assert.AreEqual(MatchPhase.RoundResult, f.Match.Phase);
            var quals = f.NewEvents.Where(e => e.Type == RaceEventType.Qualified).Select(e => (int)e.Slot).ToArray();
            CollectionAssert.AreEquivalent(new[] { 1, 2 }, quals);
            var elims = f.NewEvents.Where(e => e.Type == RaceEventType.Eliminated).Select(e => (int)e.Slot).ToArray();
            CollectionAssert.AreEqual(new[] { 0 }, elims);
            var end = f.NewEvents.Single(e => e.Type == RaceEventType.RoundEnd);
            Assert.AreEqual(2, end.Arg);
            Assert.AreEqual(1, f.Match.Slots.Single(s => s.Slot == 1).Places[1], "round 2 place");
            Assert.AreEqual(3, f.Match.Slots.Single(s => s.Slot == 0).Places[1]);
            Assert.AreEqual(PlayerState.RaceQualified, OnChainMatchFeed.RaceByte(in ended, 2) & PlayerState.RaceQualified);
            // the result is sent once
            f.OnWorld(View(Full("ended.worldacc", ChainDirector.RoundResult, 2)), 21.0);
            Assert.IsFalse(f.NewEvents.Any(e => e.Type == RaceEventType.RoundEnd));
        }

        [Test]
        public void FullFeedChampionAndEliminations()
        {
            var elim = new byte[12];
            elim[0] = 4 << 4 | 2;
            elim[2] = 3 << 4 | 1;
            var f = new OnChainMatchFeed { OwnSlot = 0, Full = true };
            f.OnWorld(View(Full("ended.worldacc", ChainDirector.Champion, 4, alive: 0b010, champion: 1, elim: elim)), 5.0);
            Assert.AreEqual(MatchPhase.Champion, f.Match.Phase);
            Assert.AreEqual(1, f.Match.Champion);
            var s0 = f.Match.Slots.Single(s => s.Slot == 0);
            Assert.IsFalse(s0.Has(MatchSlotInfo.Alive));
            Assert.AreEqual(4, s0.EliminatedRound);
            Assert.AreEqual((ElimReason)2, s0.Reason);
            Assert.AreEqual(3, f.Match.Slots.Single(s => s.Slot == 2).EliminatedRound);
            Assert.IsTrue(f.Match.Slots.Single(s => s.Slot == 1).Has(MatchSlotInfo.Alive));
        }

        [Test]
        public void FullFeedHidesPlayersOutOfEarlierRoundsBetweenRounds()
        {
            // the round has not begun (phase WAITING = free): slot 0 is out of the match since round 1
            var a = Full("rush.worldacc", ChainDirector.RoundIntro, 2, alive: 0b110);
            a[L.offset + WorldView.RoundOffset + 1] = WorldView.PhaseWaiting;
            var f = new OnChainMatchFeed { OwnSlot = 0, Full = true };
            f.OnWorld(View(a), 1.0);
            var slots = f.Players.Take(f.PlayerCount).Select(p => (int)p.Slot).ToArray();
            CollectionAssert.AreEquivalent(new[] { 1, 2 }, slots);
            var hybrid = new OnChainMatchFeed { OwnSlot = 0 };
            hybrid.OnWorld(View(a), 1.0);
            Assert.AreEqual(3, hybrid.PlayerCount, "Full off: the Final's feed is unchanged");
        }

        [Test]
        public void HybridFeedUnchangedOnAVersion1World()
        {
            var f = new OnChainMatchFeed { OwnSlot = 0 };
            f.OnWorld(View(Acc("rush.worldacc")), 10.0);
            Assert.AreEqual(4, f.Match.Round);
            Assert.AreEqual(4, f.Round.RoundIndex);
            Assert.AreEqual(1, f.Match.Targets[3]);
        }

        private static byte[] WorldAcc(byte dirPhase, byte round, uint tick, ushort alive, byte champion, byte[] elim)
        {
            var b = DirectorWorld(dirPhase, round, 0b111, alive, 0b110, champion, elim, prefix: 208);
            BitConverter.GetBytes(tick).CopyTo(b, 128);
            return b;
        }

        [Test]
        public void ServerTakesTheArenaChampionAtDone()
        {
            var h = new ChainFinalHandoff();
            var elim = new byte[12];
            elim[0] = 4 << 4 | 2;
            elim[2] = 2 << 4 | 1;
            h.FeedAllForTest(WorldAcc(ChainDirector.RoundActive, 4, 900, 0b011, 255, elim), null);
            Assert.AreEqual(ChainFinalHandoff.State.Live, h.Phase);
            Assert.AreEqual(4, h.ChainRound);
            h.FeedAllForTest(WorldAcc(ChainDirector.Champion, 4, 950, 0b010, 1, elim), null);
            Assert.AreEqual(ChainFinalHandoff.State.Live, h.Phase, "CHAMPION while the arena still runs this match: wait for DONE");
            h.FeedAllForTest(WorldAcc(ChainDirector.Done, 4, 1200, 0b010, 1, elim), null);
            Assert.AreEqual(ChainFinalHandoff.State.Done, h.Phase);
            CollectionAssert.AreEqual(new[] { 1, 0, 2 }, h.ResultOrder);
            Assert.AreEqual((ElimReason)2, h.ResultReasons[1]);
            Assert.IsFalse(h.ChainFailed);

            var r = new ChainFinalRules(new StubRules(), h, all: true);
            Assert.IsTrue(r.IsOver);
            CollectionAssert.AreEqual(new[] { 1, 0, 2 }, r.RoundOutcome.Order);
            Assert.AreEqual(1, r.RoundOutcome.QualifiedCount, "one qualifier: the director ends the match with the arena's champion");
            Assert.IsFalse(r.TimedOut);
        }

        [Test]
        public void ServerAbortsWhenTheArenaAborts()
        {
            var h = new ChainFinalHandoff();
            h.FeedAllForTest(WorldAcc(ChainDirector.Aborted, 2, 500, 0b111, 255, null), null);
            Assert.IsTrue(h.ChainFailed);
            Assert.IsNull(h.ResultOrder);
            var r = new ChainFinalRules(new StubRules(), h, all: true);
            r.EndTick(null, 1);
            Assert.IsTrue(r.TimedOut, "the server's match aborts (refunds)");

            var g = new ChainFinalHandoff();
            g.ForceChainFailedForTest("stalled");
            var rr = new ChainFinalRules(new StubRules(), g, all: true);
            rr.EndTick(null, 1);
            Assert.IsTrue(rr.TimedOut);
        }

        [Test]
        public void AllModeIgnoresTheRoundTimer()
        {
            var h = new ChainFinalHandoff();
            h.FeedAllForTest(WorldAcc(ChainDirector.RoundActive, 1, 10, 0b111, 255, null), null);
            var r = new ChainFinalRules(new StubRules(), h, all: true);
            for (uint t = 1; t < ChainFinalRules.ResultGraceTicks + 9000; t += 97)
                r.EndTick(null, t);
            Assert.IsFalse(r.TimedOut, "a full match runs minutes past round 1's timer");
            Assert.IsFalse(r.IsOver);
        }

        [Test]
        public void Base58AndMatchId()
        {
            Assert.AreEqual("11111111111111111111111111111111", ChainFinalHandoff.Base58Encode(new byte[32], 0, 32));
            var pk = ShellCheck.Base58(OnChainSession.ProgramId);
            Assert.AreEqual(32, pk.Length);
            Assert.AreEqual(OnChainSession.ProgramId, ChainFinalHandoff.Base58Encode(pk, 0, 32));
            var acc = new byte[40];
            for (var i = 0; i < 16; i++)
                acc[10 + i] = (byte)(0xC0 + i);
            Assert.IsTrue(ChainFinalHandoff.SameId(acc, 10, "c0c1c2c3c4c5c6c7c8c9cacbcccdcecf"));
            Assert.IsFalse(ChainFinalHandoff.SameId(acc, 10, "c0c1c2c3c4c5c6c7c8c9cacbcccdce00"));
        }

        [Test]
        public void FoncAllFlagAndScenes()
        {
            Assert.IsTrue(OnChainSwitch.IsWholeMatch("{\"t\":\"final_onchain\",\"net\":\"devnet\",\"arena\":\"x\",\"match\":\"ab\",\"url\":\"\",\"all\":true,\"slot_map\":[{\"slot\":7,\"name\":\"Ann\",\"kind\":\"human\"}]}"));
            Assert.IsFalse(OnChainSwitch.IsWholeMatch("{\"t\":\"final_onchain\",\"arena\":\"x\",\"slot_map\":[]}"));
            var map = OnChainSwitch.ParseSlotMap("{\"t\":\"final_prejoin\",\"all\":true,\"slot_map\":[{\"slot\":7,\"name\":\"Ann\",\"kind\":\"human\"},{\"slot\":11,\"name\":\"B\",\"kind\":\"bot\"}]}");
            Assert.AreEqual(7, OnChainSwitch.PickSlot(map, 7), "any of 12 slots");
            Assert.AreEqual(-1, OnChainSwitch.PickSlot(map, 11));
            Assert.AreEqual("Map_BlockPartyDash", OnChainSession.SceneForWorldMap(MapPoolV1.BlockPartyDash));
            Assert.AreEqual("Map_GooTower", OnChainSession.SceneForWorldMap(MapPoolV1.GooTower));
            Assert.AreEqual("Map_StarScramble", OnChainSession.SceneForWorldMap(MapPoolV1.StarScramble));
            Assert.AreEqual("Map_CrumbleShelf", OnChainSession.SceneForWorldMap(MapPoolV1.CrumbleShelf));
            Assert.AreEqual("Map_Lobby", OnChainSession.SceneForWorldMap(MapPoolV1.Lobby));
            foreach (var id in new byte[] { 0, 1, 2, 3, MapPoolV1.Lobby })
                Assert.IsNotNull(Resources.Load<TextAsset>("feellab/" + MapPoolV1.KeyOf(id)), $"map blob for {MapPoolV1.KeyOf(id)}");
        }

        [Test]
        public void PredictionRunsAVersion2World()
        {
            var core = RushCoreAbi.Create();
            var map = File.ReadAllBytes(Path.Combine(Application.dataPath, "_Game", "Scripts", "FeelLab", "Resources", "feellab", "CrumbleShelf.bytes"));
            var a = Full("rush.worldacc", ChainDirector.RoundActive, 4);
            var w = View(a);
            using var p = new OnChainPrediction(core, map, 30, 0b111, 0b110, 0, 120, 2);
            p.OnAuthoritative(a, L.offset, WorldView.Size, w.Tick, w.Tick + 3, 1.0, w.Tick);
            Assert.IsTrue(p.HasState, "the core decodes a version-2 World (rush_core.wasm / bundle rebuilt with World v2)");
            Assert.AreEqual(w.Tick + 3, p.Present);
        }
    }
}
