using FallRush.Shared;
using NUnit.Framework;

namespace FallRush.Tests
{
    /// <summary>G1 gameplay polish: respawning top-shelf tiles (Final), and the reactive log staying exact across cycles.</summary>
    public class G1Tests
    {
        private static TileGridDef Grid(int respawn) => new TileGridDef
        {
            OriginXMm = -7200, OriginZMm = -7200, TileMm = 1600, GapMm = 40, Cols = 9, Rows = 9, ShelfTopsMm = new[] { 12000, 6000, 0 },
            ThicknessMm = 400, WobbleTicks = 30, RespawnTicks = respawn, RespawnShelves = 1,
        };

        private static RxEntry Contact(ReactiveSet rx, int id, uint tick) =>
            new RxEntry { Seq = rx.LastSeq + 1, Type = RxType.TileContact, Id = (ushort)id, Slot = 0, Tick = tick };

        [Test]
        public void TopShelfTile_Wobbles1s_Drops_ReturnsAfter8s_ThenCyclesAgain()
        {
            var rx = new ReactiveSet(Grid(240), null, null, 1);
            const int top = 40, mid = 81 + 40;
            Assert.That(rx.Apply(Contact(rx, top, 1000)), Is.True);
            Assert.That(rx.Apply(Contact(rx, mid, 1000)), Is.True);
            Assert.That(rx.TileSolid(top, 1029), Is.True, "wobbling for 1.0 s");
            Assert.That(rx.TileSolid(top, 1030), Is.False, "dropped at contact + 30");
            Assert.That(rx.TileReturnsIn(top, 1030), Is.EqualTo(240));
            Assert.That(rx.TileSolid(top, 1269), Is.False);
            Assert.That(rx.TileSolid(top, 1270), Is.True, "back 8 s after the drop");
            Assert.That(rx.TileContactAt(top, 1270), Is.EqualTo(0u), "untouched again");
            Assert.That(rx.TileSolid(mid, 5000), Is.False, "lower shelves never return");
            Assert.That(rx.TileReturnsIn(mid, 5000), Is.EqualTo(-1));

            // A contact during the gone time is ignored; a new contact after the return starts a new cycle.
            rx.Apply(Contact(rx, top, 1100));
            Assert.That(rx.TileContactAt(top, 1100), Is.EqualTo(1000u));
            rx.Apply(Contact(rx, top, 1300));
            Assert.That(rx.TileContactAt(top, 1300), Is.EqualTo(1300u));
            Assert.That(rx.TileSolid(top, 1329), Is.True);
            Assert.That(rx.TileSolid(top, 1330), Is.False);
            // History stays exact for a rewind to an earlier tick.
            Assert.That(rx.TileSolid(top, 1040), Is.False);
            Assert.That(rx.TileSolid(top, 1280), Is.True);
        }

        [Test]
        public void RespawningTiles_FullStateEqualsDeltas()
        {
            var server = new ReactiveSet(Grid(240), null, null, 7);
            var viaDeltas = new ReactiveSet(Grid(240), null, null, 7);
            uint t = 100;
            for (var k = 0; k < 200; k++)
            {
                var id = (k * 37) % 243;
                t += 7;
                if (server.TileContactAt(id, t) != 0)
                    continue;
                var e = Contact(server, id, t);
                Assert.That(server.Apply(e), Is.True);
                Assert.That(viaDeltas.Apply(e), Is.True);
            }

            var viaFull = server.Clone();
            for (uint tick = 100; tick < t + 400; tick += 3)
                for (var i = 0; i < 243; i++)
                    Assert.That(viaFull.TileSolid(i, tick), Is.EqualTo(server.TileSolid(i, tick)).And.EqualTo(viaDeltas.TileSolid(i, tick)));
        }
    }
}
