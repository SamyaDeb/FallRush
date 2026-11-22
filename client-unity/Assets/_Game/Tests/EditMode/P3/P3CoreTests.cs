using System;
using System.IO;
using FallRush.OnChain;
using NUnit.Framework;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>P3-A: the native core through <see cref="IRushCore"/> = P2's golden vectors (full files) = the 200-case
    /// subset the browser checks (<c>?rcselftest=1</c>); the batched <see cref="IRushCore.Run"/> = plain stepping.</summary>
    public class P3CoreTests
    {
        private static string Vec => Path.Combine(Application.dataPath, "_Game", "Tests", "Vectors", "rushcore");
        private static byte[] Blob(string key) => File.ReadAllBytes(Path.Combine(Application.dataPath, "_Game", "Scripts", "FeelLab", "Resources", "feellab", key + ".bytes"));

        [Test]
        public void VersionFileNamesTheP2Commit()
        {
            var t = File.ReadAllText(Path.Combine(Application.dataPath, "_Game", "Plugins", "rush_core_version.txt"));
            StringAssert.Contains("p2_commit ", t);
            var core = RushCoreAbi.Create();
            Assert.IsTrue(core.Ready, core.Error);
            StringAssert.Contains($"rc_version 0x{core.Version:x}", t);
        }

        [Test]
        public void GoldenVectorFilesMatch()
        {
            var core = RushCoreAbi.Create();
            var files = Directory.GetFiles(Vec, "*_v*.bin");
            Assert.GreaterOrEqual(files.Length, 5);
            foreach (var f in files)
            {
                var b = File.ReadAllBytes(f);
                Assert.AreEqual("RCV1", System.Text.Encoding.ASCII.GetString(b, 0, 4), f);
                var kind = b[4];
                var seed = BitConverter.ToUInt64(b, 5);
                var count = BitConverter.ToUInt32(b, 13);
                var digest = BitConverter.ToUInt64(b, 17);
                var ml = BitConverter.ToInt32(b, 25);
                byte[] map = null;
                if (ml > 0)
                {
                    map = new byte[ml];
                    Buffer.BlockCopy(b, 29, map, 0, ml);
                }

                Assert.AreEqual(digest.ToString("x16"), core.Vectors(kind, seed, count, map).ToString("x16"), Path.GetFileName(f));
            }
        }

        [Test]
        public void BrowserSubsetMatchesNative()
        {
            var core = RushCoreAbi.Create();
            var n = RcSelfTest.Run(core, name => File.ReadAllBytes(Path.Combine(Vec, name)), File.ReadAllText(Path.Combine(Vec, "subset_expected.txt")), out var report);
            Assert.AreEqual(5, n, report);
            StringAssert.Contains("PASS", report);
            StringAssert.DoesNotContain("FAIL", report);
        }

        [Test]
        public void BatchedRunEqualsPlainStepsAndRebases()
        {
            var core = RushCoreAbi.Create();
            var map = Blob("CrumbleShelf");
            var cfg = RushCoreAbi.Cfg(30, 0b111, 0b110, 7);
            var a = core.Init(map, cfg);
            var b = core.Init(map, cfg);
            const int N = 60;
            var inputs = new byte[N * RushCoreAbi.InputsPerTick];
            for (var k = 0; k < N; k++)
                RushCoreAbi.PutInput(inputs, k * RushCoreAbi.InputsPerTick, 0, 100, 0, (byte)(k % 20 == 0 ? 3 : 0), 0);
            var outP = new byte[N * 12 * 36];
            Assert.AreEqual(N, core.Run(a, null, 0, N, inputs, 0b111, outP));
            var one = new byte[RushCoreAbi.InputsPerTick];
            for (var k = 0; k < N; k++)
            {
                Buffer.BlockCopy(inputs, k * RushCoreAbi.InputsPerTick, one, 0, one.Length);
                core.Run(b, null, 0, 1, one, 0, null);
            }

            Assert.AreEqual(core.Hash(b), core.Hash(a));
            var players = new byte[432];
            core.Players(b, players);
            for (var i = 0; i < 36; i++)
                Assert.AreEqual(players[i], outP[((N - 1) * 12) * 36 + i], "slot 0 record byte " + i);
            Assert.AreEqual((ulong)N | 30ul << 32, core.Clock(a));

            // rebase: b's World onto a fresh handle, then one more identical tick on both
            var w = new byte[RushCoreAbi.WorldMaxBytes];
            var len = core.WorldBytes(b, w);
            Assert.Greater(len, 1000);
            var c = core.Init(map, cfg);
            core.Run(c, w, len, 0, null, 0, null);
            Assert.AreEqual(core.Hash(b), core.Hash(c));
            core.Run(c, null, 0, 1, one, 0, null);
            core.Run(b, null, 0, 1, one, 0, null);
            Assert.AreEqual(core.Hash(b), core.Hash(c));
            core.Free(a);
            core.Free(b);
            core.Free(c);
        }
    }
}
