using System;
using FallRush.Shared;

namespace FallRush.OnChain
{
    /// <summary>
    /// Star run (2026-11-25): the arena's reactive state → the client's <see cref="ReactiveSet"/>.
    /// <para>The star / tile / block views (and Classic bot brains) read <see cref="ReactiveSet"/>, which only the game
    /// server's <c>rx</c> messages filled. In a fully on-chain round the arena runs the rules and the game server sends
    /// nothing, so collected stars never vanished, knock-off drops never appeared, crumbling tiles and falling blocks never
    /// moved on screen (user: "stars collecting is not happening properly" in Star Fountain).</para>
    /// <para>Each World carries rush-core's <c>Reactive</c> (318 B at <see cref="WorldView.ReactiveOffset"/>): tile contact
    /// u32×16, block trigger u32×8, star_taken u64×9, drops 8 × {tick u32, slot u8, taken u8, x/y/z i32 mm}, drop_count
    /// u16, seq u32. This feed diffs it against what it already applied and appends the changes as entries (tick = the
    /// change's own tick when the World has it, else the World's tick).</para>
    /// </summary>
    public sealed class OnChainReactiveFeed
    {
        public const int Tiles = 16, Blocks = 8, StarWords = 9, Drops = 8, DropSize = 18;
        private const int TileOff = 0, BlockOff = TileOff + Tiles * 4, StarOff = BlockOff + Blocks * 4, DropOff = StarOff + StarWords * 8;
        private const int DropCountOff = DropOff + Drops * DropSize, SeqOff = DropCountOff + 2;

        private ReactiveSet _rx;
        private readonly uint[] _tile = new uint[Tiles];
        private readonly uint[] _block = new uint[Blocks];
        private readonly ulong[] _star = new ulong[StarWords];
        private int _dropsAdded;
        private readonly bool[] _dropTaken = new bool[Drops];
        private uint _lastSeq = uint.MaxValue;

        /// <summary>Entries applied (F3 / tests).</summary>
        public long Applied;

        /// <summary>Applies the World's reactive changes to <paramref name="rx"/>; a new ReactiveSet (new round / map) starts over.</summary>
        public void Apply(byte[] world, int offset, uint worldTick, ReactiveSet rx)
        {
            if (rx == null || world == null || offset + WorldView.ReactiveOffset + SeqOff + 4 > world.Length)
                return;
            if (!ReferenceEquals(rx, _rx))
            {
                _rx = rx;
                Array.Clear(_tile, 0, Tiles);
                Array.Clear(_block, 0, Blocks);
                Array.Clear(_star, 0, StarWords);
                Array.Clear(_dropTaken, 0, Drops);
                _dropsAdded = 0;
                _lastSeq = uint.MaxValue;
            }

            var o = offset + WorldView.ReactiveOffset;
            var seq = BitConverter.ToUInt32(world, o + SeqOff);
            if (seq == _lastSeq)
                return; // nothing changed since the last World
            _lastSeq = seq;

            for (var k = 0; k < Tiles; k++)
            {
                var t = BitConverter.ToUInt32(world, o + TileOff + 4 * k);
                if (t != 0 && t > _tile[k])
                {
                    _tile[k] = t;
                    rx.Add(RxType.TileContact, k, 255, t);
                    Applied++;
                }
            }

            for (var k = 0; k < Blocks; k++)
            {
                var t = BitConverter.ToUInt32(world, o + BlockOff + 4 * k);
                if (t != 0 && t > _block[k])
                {
                    _block[k] = t;
                    rx.Add(RxType.BlockTrigger, k, 255, t);
                    Applied++;
                }
            }

            // timeline stars: new bits = collected since the last World (the World does not keep the pickup tick)
            for (var wd = 0; wd < StarWords; wd++)
            {
                var bits = BitConverter.ToUInt64(world, o + StarOff + 8 * wd);
                var fresh = bits & ~_star[wd];
                if (fresh == 0)
                    continue;
                _star[wd] = bits;
                for (var b = 0; b < 64; b++)
                {
                    if ((fresh >> b & 1) == 0)
                        continue;
                    rx.Add(RxType.StarCollect, wd * 64 + b, 255, worldTick);
                    Applied++;
                }
            }

            // dropped stars: the ring keeps the last 8 of drop_count; the client list index must equal the drop id
            var count = BitConverter.ToUInt16(world, o + DropCountOff);
            var first = Math.Max(0, count - Drops);
            while (_dropsAdded < first)
            {
                // a drop that fell out of the ring before we saw it: a placeholder that is never visible
                rx.Add(RxType.StarDrop, HazardMath.StarDropBase + _dropsAdded, 255, 0);
                rx.Add(RxType.StarCollect, HazardMath.StarDropBase + _dropsAdded, 255, 0);
                _dropsAdded++;
            }

            for (var d = _dropsAdded; d < count; d++)
            {
                var e = o + DropOff + (d % Drops) * DropSize;
                var tick = BitConverter.ToUInt32(world, e);
                var slot = world[e + 4];
                rx.Add(RxType.StarDrop, HazardMath.StarDropBase + d, slot, tick, BitConverter.ToInt32(world, e + 6), BitConverter.ToInt32(world, e + 10), BitConverter.ToInt32(world, e + 14));
                _dropTaken[d % Drops] = false;
                Applied++;
            }
            _dropsAdded = Math.Max(_dropsAdded, count);

            for (var d = first; d < count; d++)
            {
                var e = o + DropOff + (d % Drops) * DropSize;
                if (world[e + 5] == 0 || _dropTaken[d % Drops])
                    continue;
                _dropTaken[d % Drops] = true;
                rx.Add(RxType.StarCollect, HazardMath.StarDropBase + d, 255, worldTick);
                Applied++;
            }
        }
    }
}
