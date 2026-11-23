using System;
using System.IO;
using FallRush.FeelLab;
using NUnit.Framework;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>P1-N: rush_core.bundle loads in the Editor, its version matches, and stepping each map blob through the
    /// FFI gives the same World hash as rush-sim (native parity through P/Invoke; expected hashes from
    /// <c>rush-sim ffi-expect</c>, docs/proof/p1/ffi_expected.txt). P3: the bundle is built from P2's branch
    /// (Plugins/rush_core_version.txt), so the expected hashes are those of that core and its map blobs
    /// (Tests/Vectors/rushcore/ffi_expected.txt by tools/p3/ffi-expect.mjs; blobs in FeelLab/Resources/feellab).</summary>
    public class P1FeelLabTests
    {
        private static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

        [Test]
        public void BundleLoadsAndVersionMatches()
        {
            Assert.AreEqual(0x10002u, RushCoreNative.rc_version(), "ABI 1, rush-core 2 (P4 Chaos Drops)");
            Assert.AreEqual(2048u, RushCoreNative.rc_world_size());
        }

        [Test]
        public void FfiStepMatchesRushSim()
        {
            var lines = File.ReadAllLines(Path.Combine(Application.dataPath, "_Game", "Tests", "Vectors", "rushcore", "ffi_expected.txt"));
            Assert.Greater(lines.Length, 0);
            var checkedSeeds = 0;
            foreach (var l in lines)
            {
                var p = l.Split(' ');
                if (p.Length != 3)
                    continue;
                var blob = File.ReadAllBytes(Path.Combine(Application.dataPath, "_Game", "Scripts", "FeelLab", "Resources", "feellab", p[0] + ".bytes"));
                var seed = ulong.Parse(p[1]);
                var h = RushCoreNative.rc_init(blob, (uint)blob.Length, RushCoreNative.Cfg(20, 0xFFF, 0xAAA, seed));
                Assert.GreaterOrEqual(h, 0, p[0]);
                for (var i = 0; i < 200; i++)
                    RushCoreNative.rc_step(h, null);
                var got = RushCoreNative.rc_hash(h).ToString("x16");
                RushCoreNative.rc_free(h);
                Assert.AreEqual(p[2], got, $"{p[0]} seed {seed}");
                checkedSeeds++;
            }

            Assert.GreaterOrEqual(checkedSeeds, 3);
        }
    }
}
