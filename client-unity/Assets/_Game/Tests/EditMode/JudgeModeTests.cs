using System.Linq;
using FallRush.Shared;
using NUnit.Framework;

namespace FallRush.Tests
{
    /// <summary>Submission (judge mode): a judge room starts at once, fast-forwards rounds 1–3 and plays the Final.</summary>
    public sealed class JudgeModeTests
    {
        private static MatchDirectorTests.Harness Judge(byte[] seed, int human = 5)
        {
            var h = new MatchDirectorTests.Harness(seed, new[] { human });
            h.D.PrivateRoom = true;
            h.D.JudgeMode = true;
            return h;
        }

        private static byte[] Seed(byte b)
        {
            var s = new byte[32];
            for (var i = 0; i < 32; i++)
                s[i] = (byte)(b + i * 7);
            return s;
        }

        [Test]
        public void JudgeRoom_StartsAlone_FastForwards13_HumanReachesTheFinalFast()
        {
            var h = Judge(Seed(3));
            h.RunUntil(() => h.D.Phase == MatchPhase.RosterLocked || h.D.Phase == MatchPhase.Dealing, 60);
            Assert.That(h.D.JudgeMatch, Is.True);
            var locked = h.D.MatchStartTick;
            h.RunUntil(() => h.D.Round == 4 && h.D.Phase == MatchPhase.RoundIntro);
            Assert.That(h.Sim.Tick - locked, Is.LessThan(30 * 15), "the Final loads within 15 s of the roster lock");
            Assert.That(h.Host.Loads.Where(m => m != MapPoolV1.Lobby).ToArray(), Is.EqualTo(new[] { h.D.PlanMaps[3] }), "only the Final's map is loaded");
            Assert.That(h.D.Roster[5].Alive, Is.True, "the judge is a finalist");
            for (var r = 1; r <= 3; r++)
            {
                var evs = h.Host.Events.Where(e => e.Round == r).ToList();
                Assert.That(evs.First().Tag, Is.EqualTo(Transcript.StartRound), $"round {r} starts");
                Assert.That(evs.Last().Tag, Is.EqualTo(Transcript.CompleteRound), $"round {r} completes");
                Assert.That(evs.Count(e => e.Tag == Transcript.Qualified), Is.EqualTo(h.D.Targets[r - 1]), $"round {r}: the usual qualify target");
                Assert.That(evs.Any(e => e.Tag == Transcript.Qualified && e.Slot == 5), Is.True, $"round {r}: the judge qualifies");
            }

            Assert.That(h.D.AliveCount(out _), Is.EqualTo(3), "3 finalists (the on-chain Final needs exactly 3)");
            h.RunMatch();
            Assert.That(h.D.MatchesCompleted, Is.EqualTo(1));
            Assert.That(h.D.JudgeMatch, Is.False, "cleared for the next match");
        }

        [Test]
        public void JudgeRoom_SameSeed_SameTranscript()
        {
            string Run()
            {
                var h = Judge(Seed(9), 2);
                h.RunUntil(() => h.D.Round == 4 && h.D.Phase == MatchPhase.RoundIntro);
                return Sha256.Hex(h.D.TranscriptHash);
            }

            Assert.That(Run(), Is.EqualTo(Run()));
        }

        [Test]
        public void JudgeFlag_WithoutARoom_IsIgnored()
        {
            var h = new MatchDirectorTests.Harness(Seed(1), new[] { 0 });
            h.D.JudgeMode = true;
            h.RunUntil(() => h.D.Phase == MatchPhase.Countdown);
            h.RunUntil(() => h.D.Phase == MatchPhase.RoundIntro);
            Assert.That(h.D.JudgeMatch, Is.False);
            Assert.That(h.D.Round, Is.EqualTo(1));
            Assert.That(h.Host.Loads.Last(), Is.EqualTo(h.D.PlanMaps[0]), "a public match plays round 1");
        }
    }
}
