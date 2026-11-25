using System.Linq;
using FallRush.Shared;
using NUnit.Framework;

namespace FallRush.Tests
{
    /// <summary>fullchain: the Classic director's two hooks (null = unchanged) and the on-chain director view.</summary>
    public sealed class FullChainDirectorTests
    {
        private static byte[] Seed(byte b)
        {
            var s = new byte[32];
            for (var i = 0; i < 32; i++)
                s[i] = (byte)(b + i * 5);
            return s;
        }

        [Test]
        public void HoldDealt_KeepsTheDirectorInDealtUntilReleased()
        {
            var h = new MatchDirectorTests.Harness(Seed(4), new[] { 0 });
            var hold = true;
            h.D.HoldDealt = () => hold;
            h.RunUntil(() => h.D.Phase == MatchPhase.Dealt);
            for (var i = 0; i < 900; i++)
                h.Tick();
            Assert.That(h.D.Phase, Is.EqualTo(MatchPhase.Dealt), "held (the server is deciding)");
            Assert.That(h.D.Round, Is.EqualTo(0));
            hold = false;
            h.RunUntil(() => h.D.Phase == MatchPhase.RoundIntro, 5);
            Assert.That(h.D.Round, Is.EqualTo(1));
        }

        [Test]
        public void ChainAll_JudgeMatchPlaysRoundOneInsteadOfFastForwarding()
        {
            var h = new MatchDirectorTests.Harness(Seed(7), new[] { 5 });
            h.D.PrivateRoom = true;
            h.D.JudgeMode = true;
            h.D.ChainAll = () => true;
            h.RunUntil(() => h.D.Phase == MatchPhase.RoundIntro);
            Assert.That(h.D.JudgeMatch, Is.True);
            Assert.That(h.D.Round, Is.EqualTo(1), "no fast-forward: the arena plays rounds 1–3");
            Assert.That(h.Host.Loads.Last(), Is.EqualTo(h.D.PlanMaps[0]));
        }

        [Test]
        public void ChainAllFalse_JudgeStillFastForwards()
        {
            var h = new MatchDirectorTests.Harness(Seed(7), new[] { 5 });
            h.D.PrivateRoom = true;
            h.D.JudgeMode = true;
            h.D.ChainAll = () => false; // the per-match fallback
            h.RunUntil(() => h.D.Phase == MatchPhase.RoundIntro);
            Assert.That(h.D.Round, Is.EqualTo(4));
        }

        /// <summary>A World with only the director written (version 2, the rest zero).</summary>
        public static byte[] DirectorWorld(byte phase, byte round, ushort roster, ushort alive, ushort bots, byte champion, byte[] elim, int prefix = 0)
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

        [Test]
        public void ChainDirector_DecodesAndRanksTheMatch()
        {
            var elim = new byte[12];
            // 12 players (targets 8, 5, 3, 1): R1 out 8..11, R2 out 5..7, R3 out 3, 4, R4 out 1, 2; champion 0
            for (var s = 8; s < 12; s++) elim[s] = 1 << 4 | 2;
            for (var s = 5; s < 8; s++) elim[s] = 2 << 4 | 1;
            elim[3] = elim[4] = 3 << 4 | 2;
            elim[1] = 4 << 4 | 1;
            elim[2] = 4 << 4 | 7;
            var w = DirectorWorld(ChainDirector.Done, 4, 0xFFF, 0x001, 0xFFE, 0, elim, prefix: 208);
            var d = new ChainDirector(w, 208);
            Assert.That(d.Valid && d.Running && d.HasChampion, Is.True);
            Assert.That(d.Round, Is.EqualTo(4));
            Assert.That(d.Target(0), Is.EqualTo(8));
            Assert.That(d.Map(2), Is.EqualTo(MapPoolV1.StarScramble));
            Assert.That(d.Bot(0), Is.False);
            Assert.That(d.Bot(11), Is.True);
            Assert.That(d.EventCount, Is.EqualTo(37u));
            Assert.That(d.ElimRound(9), Is.EqualTo(1));
            Assert.That(d.TryResult(out var order, out var reasons), Is.True);
            Assert.That(order, Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }));
            Assert.That(reasons[2], Is.EqualTo(7));
            Assert.That(reasons[0], Is.EqualTo(0));

            var running = new ChainDirector(DirectorWorld(ChainDirector.RoundActive, 2, 0xFFF, 0x0FF, 0, 255, null), 0);
            Assert.That(running.Running && !running.HasChampion, Is.True);
            Assert.That(running.TryResult(out _, out _), Is.False);
            var lobby = new ChainDirector(DirectorWorld(ChainDirector.LobbyOpen, 0, 0, 0, 0, 255, null), 0);
            Assert.That(lobby.Running, Is.False);
        }
    }
}
