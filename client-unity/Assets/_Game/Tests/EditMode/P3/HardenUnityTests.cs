using FallRush.OnChain;
using NUnit.Framework;

namespace FallRush.Tests
{
    /// <summary>harden #13: the session banner, the whole-match join supervision and the join JSON escaping.</summary>
    public class HardenUnityTests
    {
        [Test]
        public void SessionBannerOnlyOnSessionInvalidAndClearsWhenAnInputLands()
        {
            var h = new SessionHealth();
            // late / window / bad slot / timeout confirms (a far player): no banner
            foreach (var st in new byte[] { 1, 2, 3, 4, 255 })
                Assert.IsFalse(h.OnConfirm(st));
            Assert.IsFalse(h.Expired);
            // a late streak (applied tick behind every sent tick) is lateness, not an expired key
            for (uint t = 100; t < 200; t++)
                h.OnSentTick(t - 7, t);
            Assert.IsFalse(h.Expired);
            Assert.IsTrue(h.OnConfirm(SessionHealth.StatusSessionInvalid), "6027 raises it");
            Assert.IsFalse(h.OnConfirm(SessionHealth.StatusSessionInvalid), "once");
            Assert.IsTrue(h.Expired);
            h.OnSentTick(205, 210);
            Assert.IsTrue(h.Expired, "still late: stays up");
            h.OnSentTick(211, 211);
            Assert.IsFalse(h.Expired, "an applied tick clears it");
            h.OnConfirm(SessionHealth.StatusSessionInvalid);
            h.OnConfirm(0);
            Assert.IsFalse(h.Expired, "an accepted batch clears it");
        }

        [Test]
        public void JoinSupervisorRejoinsAsSpectatorThenGivesUp()
        {
            var s = new JoinSupervisor();
            Assert.AreEqual(JoinSupervisor.Action.None, s.Step(0, true, false, false, null), "not confirmed yet");
            Assert.AreEqual(JoinSupervisor.Action.None, s.Step(100, true, true, false, null), "confirmed now");
            Assert.AreEqual(JoinSupervisor.Action.None, s.Step(114, true, true, false, null));
            Assert.AreEqual(JoinSupervisor.Action.RejoinSpectate, s.Step(115.1, true, true, false, null), "15 s after the confirmation");
            Assert.IsTrue(s.Rejoined);
            Assert.AreEqual(JoinSupervisor.Action.None, s.Step(116, true, true, false, null));
            Assert.AreEqual(JoinSupervisor.Action.GiveUp, s.Step(117, true, true, false, "join failed: no gas"), "the spectator join failed too");
            Assert.AreEqual(JoinSupervisor.Action.None, s.Step(118, true, true, false, "join failed: no gas"), "once");
        }

        [Test]
        public void JoinSupervisorActsAtOnceOnAnErrorAndNeverWhenJoinedOrClassic()
        {
            var s = new JoinSupervisor();
            Assert.AreEqual(JoinSupervisor.Action.RejoinSpectate, s.Step(50, true, true, false, "join failed: x"));
            Assert.AreEqual(JoinSupervisor.Action.None, s.Step(51, true, true, true, null), "the spectator join landed");
            Assert.AreEqual(JoinSupervisor.Action.None, s.Step(500, true, true, true, null));

            var ok = new JoinSupervisor();
            Assert.AreEqual(JoinSupervisor.Action.None, ok.Step(0, true, true, true, null));
            Assert.AreEqual(JoinSupervisor.Action.None, ok.Step(60, true, true, true, "a later link error"));

            var hybrid = new JoinSupervisor(); // the Final-only switch / standalone: unchanged (no supervision)
            Assert.AreEqual(JoinSupervisor.Action.None, hybrid.Step(0, false, true, false, "join failed"));
            Assert.AreEqual(JoinSupervisor.Action.None, hybrid.Step(100, false, true, false, "join failed"));
        }

        [Test]
        public void UnjoinedPrejoinEndsWhenTheServerMovesOnWithoutTheArena()
        {
            // harden #13 review: a pre-join whose join never resolved used to linger for good (its DealPendingOnChain then
            // hid the deal of every following Classic match); it ends on the server's fallback like a joined one
            Assert.IsTrue(PrejoinRules.Abandoned(FallRush.Shared.MatchPhase.RoundActive, true, 1), "whole match: the server plays round 1 itself");
            Assert.IsFalse(PrejoinRules.Abandoned(FallRush.Shared.MatchPhase.RoundActive, false, 60), "hybrid: Classic rounds run before the Final");
            Assert.IsFalse(PrejoinRules.Abandoned(FallRush.Shared.MatchPhase.Dealt, true, 60), "the deal hold");
            Assert.IsFalse(PrejoinRules.Abandoned(FallRush.Shared.MatchPhase.RoundIntro, true, 60), "the server's held round");
            Assert.IsFalse(PrejoinRules.Abandoned(FallRush.Shared.MatchPhase.LobbyOpen, true, 5), "a stale lobby message right after the pre-join");
            Assert.IsTrue(PrejoinRules.Abandoned(FallRush.Shared.MatchPhase.LobbyOpen, true, 25));
            Assert.IsTrue(PrejoinRules.Abandoned(FallRush.Shared.MatchPhase.Champion, false, 25), "hybrid: the server ran the Final itself");
        }

        [Test]
        public void JoinNameIsJsonEscaped()
        {
            Assert.AreEqual("Rushling", ChainJson.JoinName(null));
            Assert.AreEqual("Rushling", ChainJson.JoinName(""));
            Assert.AreEqual("Bob", ChainJson.JoinName("Bob"));
            Assert.AreEqual("a\\\"b\\\\c", ChainJson.Escape("a\"b\\c"));
            Assert.AreEqual("x\\u000ay\\u0009z\\u0000", ChainJson.Escape("x\ny\tz\0"));
            Assert.AreEqual("\\u007f\\u2028", ChainJson.Escape("\u007f\u2028"));
            Assert.AreEqual("😀ü", ChainJson.Escape("😀ü"), "surrogate pairs and non-ASCII pass through");
            Assert.AreEqual("\uFFFDa", ChainJson.Escape("\uD800a"), "a lone surrogate is replaced");
            var json = "{\"name\":\"" + ChainJson.JoinName("evil\"name\n\u0001") + "\"}";
            var parsed = UnityEngine.JsonUtility.FromJson<NameOnly>(json);
            Assert.AreEqual("evil\"name\n\u0001", parsed.name);
        }

        [System.Serializable] private sealed class NameOnly { public string name; }
    }
}
