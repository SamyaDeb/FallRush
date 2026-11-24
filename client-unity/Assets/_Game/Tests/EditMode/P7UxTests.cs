using FallRush.Client;
using FallRush.Net;
using FallRush.Shared;
using NUnit.Framework;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>P7-UX (OC-44/45): the pointer-lock policy, the bet drawer's never-auto-open rule, the keycap builder.</summary>
    public sealed class P7UxTests
    {
        [SetUp]
        public void SetUp() => CursorPolicy.Reset();

        [TearDown]
        public void TearDown() => CursorPolicy.Reset();

        [Test]
        public void AliveClickOnGame_Locks()
        {
            Assert.IsTrue(CursorPolicy.MayLock(true, false, false, false, false, false));
        }

        [Test]
        public void ClickOnUi_NeverLocks()
        {
            Assert.IsFalse(CursorPolicy.MayLock(true, true, false, false, false, false));
        }

        [Test]
        public void OpenPanel_FreesAndBlocksLock()
        {
            Assert.IsTrue(CursorPolicy.MustFree(true, false, false));
            Assert.IsFalse(CursorPolicy.MayLock(true, false, true, false, false, false));
        }

        [Test]
        public void Spectator_KeepsAFreeCursor()
        {
            CursorPolicy.ClassicOut = true;
            Assert.IsTrue(CursorPolicy.Spectating);
            Assert.IsTrue(CursorPolicy.MustFree(false, CursorPolicy.Spectating, false));
            Assert.IsFalse(CursorPolicy.MayLock(true, false, false, CursorPolicy.Spectating, false, false));
            CursorPolicy.ClassicOut = false;
            CursorPolicy.ChainOut = true;
            Assert.IsTrue(CursorPolicy.Spectating, "an on-chain Final spectator too");
        }

        [Test]
        public void Spectator_OrbitsWithTheRightButtonOnlyOverTheGame()
        {
            Assert.IsTrue(CursorPolicy.Orbit(true, false, false, false), "locked: the mouse always orbits");
            Assert.IsTrue(CursorPolicy.Orbit(false, true, true, false));
            Assert.IsFalse(CursorPolicy.Orbit(false, true, true, true), "not while over a panel");
            Assert.IsFalse(CursorPolicy.Orbit(false, true, false, false));
            Assert.IsFalse(CursorPolicy.Orbit(false, false, true, false), "an alive player with a free cursor does not orbit");
        }

        [Test]
        public void Panels_RegisterAndClose()
        {
            CursorPolicy.SetOpen("bet", true);
            CursorPolicy.SetOpen("settings", true);
            Assert.IsTrue(CursorPolicy.AnyOpenExcept("settings"));
            CursorPolicy.SetOpen("bet", false);
            Assert.IsFalse(CursorPolicy.AnyOpenExcept("settings"));
            Assert.IsTrue(CursorPolicy.AnyPanelOpen);
            Assert.AreEqual(Time.frameCount, CursorPolicy.ClosedFrame, "closing marks the frame (Esc closes one panel only)");
            var before = CursorPolicy.ClosedFrame;
            CursorPolicy.SetOpen("bet", false);
            Assert.AreEqual(before, CursorPolicy.ClosedFrame);
        }

        [Test]
        public void Overlay_BlocksLock()
        {
            Assert.IsFalse(CursorPolicy.MayLock(true, false, false, false, true, false));
            Assert.IsFalse(CursorPolicy.MayLock(true, false, false, false, false, true), "the scripted probe never locks");
        }

        [Test]
        public void BetDrawer_NeverForcesItselfOpen()
        {
            // the source rule: visibility is the toggle only (the P7 build OR-ed in "any market open while spectating")
            var src = System.IO.File.ReadAllText(System.IO.Path.Combine(Application.dataPath, "_Game/Scripts/OnChain/Economy/EconomyHud.cs"));
            StringAssert.Contains("var betVisible = _betOpen;", src);
            StringAssert.DoesNotContain("anyOpen && spectating && !finalRush", src);
        }

        [Test]
        public void Keycap_FitsItsLabel()
        {
            var root = new GameObject("root", typeof(RectTransform));
            try
            {
                var one = UiKit.Keycap(root.transform, "B", 12, 20f);
                var wide = UiKit.Keycap(root.transform, "Shift", 12, 20f);
                Assert.That(one.sizeDelta.x, Is.InRange(20f, 21.5f), "a one-letter key is (about) square");
                Assert.Greater(wide.sizeDelta.x, one.sizeDelta.x);
                Assert.AreEqual(20f, wide.sizeDelta.y, 0.01f);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // ------------------------------------------------------------------ OC-46 / OC-47: rooms + tiers (server)

        [Test]
        public void PrivateRoom_WaitsForTheHost_NoAutoCountdown()
        {
            var h = new MatchDirectorTests.Harness(new byte[32], new[] { 0, 3 });
            h.D.PrivateRoom = true;
            for (var i = 0; i < 1200; i++)
                h.Tick();
            Assert.That(h.D.Phase, Is.EqualTo(MatchPhase.LobbyOpen), "40 s later: still waiting for the host");
            Assert.That(h.D.HostStart(3), Is.False, "only the host starts");
            Assert.That(h.D.HostStart(0), Is.True);
            h.RunUntil(() => h.D.Phase == MatchPhase.RosterLocked || h.D.Phase == MatchPhase.Dealing);
            Assert.That(h.D.PlayerCount, Is.EqualTo(12), "a room fills to 12 with bots (same roster shape as public)");
        }

        [Test]
        public void PublicLobby_StillCountsDown()
        {
            var h = new MatchDirectorTests.Harness(new byte[32], new[] { 0 });
            h.RunUntil(() => h.D.Phase == MatchPhase.Countdown);
            Assert.Pass();
        }

        [Test]
        public void TierFor_Mix1_IsTheM4RuleExactly()
        {
            var seed = new byte[32];
            for (var k = 0; k < 200; k++)
            {
                seed[0] = (byte)k;
                seed[1] = (byte)(k >> 3);
                for (var s = 0; s < 12; s++)
                    Assert.That(MatchDirector.TierFor(seed, s, 1), Is.EqualTo(MatchDirector.TierFor(seed, s)));
            }
        }

        [Test]
        public void TierFor_MixesShiftTheDistribution_Deterministically()
        {
            int Good(int mix)
            {
                var n = 0;
                var seed = new byte[32];
                for (var k = 0; k < 400; k++)
                {
                    seed[0] = (byte)k;
                    seed[2] = (byte)(k >> 8);
                    for (var s = 0; s < 12; s++)
                        if (MatchDirector.TierFor(seed, s, mix) == BotTier.Good)
                            n++;
                }

                return n;
            }

            var rookie = Good(0);
            var contender = Good(1);
            var champion = Good(2);
            Assert.That(rookie, Is.LessThan(contender));
            Assert.That(contender, Is.LessThan(champion));
            Assert.That(champion / 4800f, Is.InRange(0.38f, 0.52f), "champion ≈ 45 % good");
            Assert.That(rookie / 4800f, Is.InRange(0.02f, 0.09f), "rookie ≈ 5 % good");
            Assert.That(MatchDirector.TierFor(new byte[32], 3, 2), Is.EqualTo(MatchDirector.TierFor(new byte[32], 3, 2)));
        }

        [Test]
        public void LobbyTier_IsTheHumansMostCommonTier()
        {
            var h = new MatchDirectorTests.Harness(new byte[32], new int[0]);
            Assert.That(h.D.LobbyTier, Is.EqualTo(1), "nobody: Contender");
            Assert.That(h.Sim.AddHumanAt(0, 100), Is.True);
            h.D.OnHumanJoined(0, MatchDirectorTests.Harness.Token(0), "A", 0, 2);
            Assert.That(h.Sim.AddHumanAt(1, 101), Is.True);
            h.D.OnHumanJoined(1, MatchDirectorTests.Harness.Token(1), "B", 0, 2);
            Assert.That(h.Sim.AddHumanAt(2, 102), Is.True);
            h.D.OnHumanJoined(2, MatchDirectorTests.Harness.Token(2), "C", 0, 0);
            Assert.That(h.D.LobbyTier, Is.EqualTo(2));
            h.D.PrivateRoom = true;
            h.D.RoomBotMix = 0;
            Assert.That(h.D.BotMix, Is.EqualTo(0), "a room uses the host's bot mix");
        }

        // ------------------------------------------------------------------ OC-49 / OC-50

        [Test]
        public void Interpolation_AfterReset_AcceptsArenaTicksBelowTheClassicTick()
        {
            var ip = new Interpolation(1.0 / 30.0);
            var players = new PlayerState[1];
            Assert.That(ip.Add(305896, players, 10.0), Is.True);
            Assert.That(ip.Add(12, players, 10.1), Is.False, "without the reset the arena World is refused (the bug)");
            ip.Reset();
            Assert.That(ip.Add(12, players, 10.2), Is.True, "the takeover reset lets arena ticks in");
        }

        [Test]
        public void TakeoverResetsTheSnapshotBuffer_Source()
        {
            var src = System.IO.File.ReadAllText(System.IO.Path.Combine(Application.dataPath, "_Game/Scripts/OnChain/Session/OnChainSession.cs"));
            StringAssert.Contains("cs.Remote.Reset();", src);
        }

        [Test]
        public void RoundS_HasSmallCorners()
        {
            var b = UiKit.RoundS.border;
            Assert.That(b.x, Is.EqualTo(10f).Within(0.01f));
            Assert.That(UiKit.Round.border.x, Is.EqualTo(24f).Within(0.01f), "big cards keep their soft corners");
        }

        [Test]
        public void Buttons_UseTheSmallCornerSprite()
        {
            var root = new GameObject("root", typeof(RectTransform));
            try
            {
                var btn = UiKit.Button(root.transform, "PLAY", UiKit.SunYellow, () => { });
                foreach (var img in btn.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                    if (img.name == "Fill" || img.name == "Border")
                        Assert.That(img.sprite, Is.SameAs(UiKit.RoundS), img.name);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
