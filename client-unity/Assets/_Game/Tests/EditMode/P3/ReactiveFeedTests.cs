using System;
using FallRush.OnChain;
using FallRush.Shared;
using NUnit.Framework;

namespace FallRush.Tests
{
    /// <summary>Star run (2026-11-25): the arena's reactive block reaches the client's ReactiveSet (stars, drops, tiles, blocks).</summary>
    public class ReactiveFeedTests
    {
        private const int R = WorldView.ReactiveOffset;
        private const int Star = R + 16 * 4 + 8 * 4, Drop = Star + 9 * 8, DropCount = Drop + 8 * 18, Seq = DropCount + 2;

        private static byte[] World() => new byte[WorldView.Size];

        private static void SetStar(byte[] w, int id) => w[Star + id / 64 * 8 + id % 64 / 8] |= (byte)(1 << (id % 8));

        private static void SetDrop(byte[] w, int d, uint tick, byte slot, bool taken, int x, int y, int z)
        {
            var e = Drop + d % 8 * 18;
            BitConverter.GetBytes(tick).CopyTo(w, e);
            w[e + 4] = slot;
            w[e + 5] = (byte)(taken ? 1 : 0);
            BitConverter.GetBytes(x).CopyTo(w, e + 6);
            BitConverter.GetBytes(y).CopyTo(w, e + 10);
            BitConverter.GetBytes(z).CopyTo(w, e + 14);
        }

        private static void Seq_(byte[] w, uint s) => BitConverter.GetBytes(s).CopyTo(w, Seq);

        [Test]
        public void TakenStarsDropsTilesAndBlocksReachTheReactiveSet()
        {
            var rx = new ReactiveSet(null, null, null, 0);
            var f = new OnChainReactiveFeed();
            var w = World();
            SetStar(w, 5);
            SetStar(w, 513); // golden
            SetDrop(w, 0, 900, 3, false, 1234, 5670, -890);
            SetDrop(w, 1, 950, 4, true, 10, 20, 30);
            BitConverter.GetBytes((ushort)2).CopyTo(w, DropCount);
            BitConverter.GetBytes(777u).CopyTo(w, R + 4 * 2);       // tile 2 touched at 777
            BitConverter.GetBytes(888u).CopyTo(w, R + 16 * 4 + 4);  // block 1 triggered at 888
            Seq_(w, 7);
            f.Apply(w, 0, 1000, rx);

            Assert.IsTrue(rx.StarTaken(5, 1000));
            Assert.IsTrue(rx.StarTaken(513, 1000));
            Assert.IsFalse(rx.StarTaken(6, 1000));
            Assert.AreEqual(2, rx.Drops.Count, "drop list index = drop id");
            Assert.AreEqual(900u, rx.Drops[0].Tick);
            Assert.AreEqual(3, rx.Drops[0].Slot);
            Assert.AreEqual(123, rx.Drops[0].Xcm);
            Assert.AreEqual(-89, rx.Drops[0].Zcm);
            Assert.IsFalse(rx.StarTaken(HazardMath.StarDropBase + 0, 1000));
            Assert.IsTrue(rx.StarTaken(HazardMath.StarDropBase + 1, 1000), "a taken drop");
            Assert.IsTrue(rx.Log.Exists(e => e.Type == RxType.TileContact && e.Id == 2 && e.Tick == 777), "tile contact logged");
            Assert.IsTrue(rx.Log.Exists(e => e.Type == RxType.BlockTrigger && e.Id == 1 && e.Tick == 888), "block trigger logged");
            var applied = f.Applied;

            f.Apply(w, 0, 1010, rx); // same seq: nothing
            Seq_(w, 8);
            f.Apply(w, 0, 1020, rx); // new seq, same content: nothing new
            Assert.AreEqual(applied, f.Applied);

            SetStar(w, 6);
            Seq_(w, 9);
            f.Apply(w, 0, 1030, rx);
            Assert.IsTrue(rx.StarTaken(6, 1030));
            Assert.IsFalse(rx.StarTaken(6, 1029), "taken from the World's tick");

            var next = new ReactiveSet(null, null, null, 0); // a new round: start over
            f.Apply(w, 0, 1040, next);
            Assert.IsTrue(next.StarTaken(5, 1040) && next.StarTaken(6, 1040));
            Assert.AreEqual(2, next.Drops.Count);
        }

        [Test]
        public void DropsThatLeftTheRingKeepTheIdsAligned()
        {
            var rx = new ReactiveSet(null, null, null, 0);
            var f = new OnChainReactiveFeed();
            var w = World();
            // 11 drops ever: the ring holds ids 3..10; we join late
            for (var d = 3; d < 11; d++)
                SetDrop(w, d, (uint)(1000 + d), (byte)d, false, d * 100, 0, 0);
            BitConverter.GetBytes((ushort)11).CopyTo(w, DropCount);
            Seq_(w, 1);
            f.Apply(w, 0, 1100, rx);
            Assert.AreEqual(11, rx.Drops.Count);
            Assert.AreEqual(1010u, rx.Drops[10].Tick, "drop id 10 at list index 10");
            Assert.IsTrue(rx.StarTaken(HazardMath.StarDropBase + 0, 1100), "placeholders are never visible");
        }
    }
}
