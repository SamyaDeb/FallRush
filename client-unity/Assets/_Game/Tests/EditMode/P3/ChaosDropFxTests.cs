using FallRush.Client;
using FallRush.Shared;
using NUnit.Framework;

namespace FallRush.Tests
{
    /// <summary>P4: the C# Chaos Drop port (views) = rush-core's drops (simulation): the digest of rush-core
    /// `drops::parity_digest` (crates/rush-core/src/drops.rs) over Lid Frenzy states + Bumper Rain spots.</summary>
    public sealed class ChaosDropFxTests
    {
        private const ulong RustDigest = 0x244836855a5b9e2cUL;

        [Test]
        public void FrenzyAndBumpersMatchRushCore()
        {
            var d = new LidGridDef { N = 9, LidMm = 1800, GapMm = 60, TopMm = 6000, RetireWarnTicks = 90, RingRetireTicks = new[] { 750, 1500, 2250 } };
            var h = 0xcbf2_9ce4_8422_2325UL;
            void B(byte v) { h ^= v; h *= 0x0100_0000_01b3UL; }
            var lids = new int[3];
            foreach (byte arg in new byte[] { 0, 7, 77, 200, 255 })
            {
                foreach (var start in new long[] { 0, 700 })
                    for (var t = start - 5; t < start + 150; t += 7)
                        for (var lid = 0; lid < 81; lid++)
                            B(ChaosDropFx.FrenzyState(in d, lid, start, arg, t, out var st, out _) ? (byte)((byte)st + 1) : (byte)0);
                ChaosDropFx.BumperLids(in d, arg, 0x1234_5678_9ABC_DEF0UL, lids);
                foreach (var x in lids)
                    foreach (var b in System.BitConverter.GetBytes(x))
                        B(b);
            }

            Assert.AreEqual(RustDigest, h, $"C# 0x{h:x16} vs rush-core 0x{RustDigest:x16}");
        }

        [Test]
        public void SweeperWarpIsHalfSpeedExtraThenHeld()
        {
            ChaosDropFx.Clear();
            ChaosDropFx.Set(i => i == 0 ? (100u, ChaosDropFx.SweeperRush, (byte)0) : (0u, (byte)0, (byte)0), 0, 0);
            Assert.AreEqual(0.0, ChaosDropFx.SweeperWarpTicks(100), 1e-9);
            Assert.AreEqual(0.5, ChaosDropFx.SweeperWarpTicks(101), 1e-9);
            Assert.AreEqual(45.0, ChaosDropFx.SweeperWarpTicks(190), 1e-9);
            Assert.AreEqual(45.0, ChaosDropFx.SweeperWarpTicks(900), 1e-9);
            ChaosDropFx.Clear();
        }
    }
}
