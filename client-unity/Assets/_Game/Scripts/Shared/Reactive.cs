using System;
using System.Collections.Generic;

namespace FallRush.Shared
{
    /// <summary>Kinds of reactive change (M4 §3.4). The byte value is on the wire (<c>rx</c>).</summary>
    public enum RxType : byte
    {
        TileContact = 1,  // crumble tile Id first touched at Tick → wobbles, collider off at Tick + WobbleTicks
        BlockTrigger = 2, // falling block Id stood on at Tick → falls at Tick + Delay, back at Tick + Delay + Gone
        StarCollect = 3,  // star Id picked up by Slot at Tick
        StarDrop = 4,     // Slot lost a star at Tick: a dropped star (Id = StarDropBase + n) pops off at X/Y/Z
    }

    /// <summary>One reactive change: server-owned, stamped (seq, effective tick). 14 B on the wire (seq implicit).</summary>
    public struct RxEntry
    {
        public const int Size = 1 + 2 + 1 + 4 + 2 + 2 + 2;
        public uint Seq;
        public RxType Type;
        public ushort Id;
        public byte Slot;
        public uint Tick;          // absolute server tick of the change (contact / trigger / pickup / drop)
        public short Xcm, Ycm, Zcm; // StarDrop: where it dropped (cm)

        public override string ToString() => $"rx#{Seq} {Type} id={Id} slot={Slot} tick={Tick}";
    }

    /// <summary>
    /// The reactive elements of one round (crumble tiles, falling blocks, collected / dropped stars): an append-only log of
    /// <see cref="RxEntry"/>s, changed only by the server's rules, plus the state it implies <b>at any tick</b>. The server
    /// and client prediction answer "is tile i solid at tick t?" from the same log with the same function, so a replay
    /// across a tile drop is exact once the change is known (a drop is scheduled 18 ticks after the contact, longer than
    /// the RTT). Deltas and the full state are the same entries, so applying either gives the same state.
    /// </summary>
    public sealed class ReactiveSet
    {
        public readonly TileGridDef Grid;
        public readonly bool HasGrid;
        public readonly FallingBlockDef[] Blocks;
        public readonly StarFieldDef Stars;
        public readonly bool HasStars;
        public readonly ulong Seed;
        public readonly List<RxEntry> Log = new List<RxEntry>();

        private readonly uint[] _tileContact;       // latest contact per tile (0 = never)
        private readonly List<uint>[] _tileCycles;   // every contact per tile, oldest first (G1: top-shelf tiles respawn)
        private readonly List<uint>[] _blockTriggers;
        private readonly Dictionary<int, RxEntry> _collected = new Dictionary<int, RxEntry>();
        public readonly List<RxEntry> Drops = new List<RxEntry>();

        public ReactiveSet(TileGridDef? grid, FallingBlockDef[] blocks, StarFieldDef? stars, ulong seed)
        {
            HasGrid = grid.HasValue && grid.Value.Count > 0;
            Grid = grid ?? default;
            Blocks = blocks ?? Array.Empty<FallingBlockDef>();
            HasStars = stars.HasValue && stars.Value.AnchorCount > 0;
            Stars = stars ?? default;
            Seed = seed;
            _tileContact = new uint[HasGrid ? Grid.Count : 0];
            _tileCycles = new List<uint>[_tileContact.Length];
            _blockTriggers = new List<uint>[Blocks.Length];
            for (var i = 0; i < Blocks.Length; i++)
                _blockTriggers[i] = new List<uint>();
        }

        public ReactiveSet Clone()
        {
            var c = new ReactiveSet(HasGrid ? Grid : (TileGridDef?)null, Blocks, HasStars ? Stars : (StarFieldDef?)null, Seed);
            foreach (var e in Log)
                c.Apply(in e);
            return c;
        }

        /// <summary>Seq of the newest entry (0 = none).</summary>
        public uint LastSeq => (uint)Log.Count;
        public int TileCount => _tileContact.Length;

        // ------------------------------------------------------------------ state at a tick

        /// <summary>The latest contact tick of tile <paramref name="i"/> (0 = never touched).</summary>
        public uint TileContactTick(int i) => (uint)i < (uint)_tileContact.Length ? _tileContact[i] : 0;

        /// <summary>
        /// The contact tick of tile <paramref name="i"/>'s cycle in progress at <paramref name="tick"/> (wobbling, gone, or —
        /// for tiles that never return — dropped for good), or 0 if it is untouched (or back) at that tick.
        /// </summary>
        public uint TileContactAt(int i, uint tick)
        {
            if ((uint)i >= (uint)_tileContact.Length || _tileContact[i] == 0)
                return 0;
            var cycles = _tileCycles[i];
            var back = (uint)Grid.RespawnFor(i);
            for (var k = cycles.Count - 1; k >= 0; k--)
            {
                var c = cycles[k];
                if (tick < c)
                    continue;
                if (back == 0 || tick < c + (uint)Grid.WobbleTicks + back)
                    return c;
                return 0;
            }

            return 0;
        }

        /// <summary>Tile <paramref name="i"/> is solid at <paramref name="tick"/>: untouched, still wobbling, or back (G1).</summary>
        public bool TileSolid(int i, uint tick)
        {
            var c = TileContactAt(i, tick);
            return c == 0 || tick < c + (uint)Grid.WobbleTicks;
        }

        /// <summary>Ticks until a gone tile <paramref name="i"/> returns at <paramref name="tick"/> (−1: not gone, or never returns).</summary>
        public int TileReturnsIn(int i, uint tick)
        {
            var c = TileContactAt(i, tick);
            var back = Grid.RespawnFor(i);
            if (c == 0 || back == 0 || tick < c + (uint)Grid.WobbleTicks)
                return -1;
            return (int)(c + (uint)Grid.WobbleTicks + (uint)back - tick);
        }

        /// <summary>Falling block <paramref name="i"/> is solid at <paramref name="tick"/> (not in any fall window).</summary>
        public bool BlockSolid(int i, uint tick)
        {
            ref readonly var d = ref Blocks[i];
            foreach (var t in _blockTriggers[i])
                if (tick >= t + (uint)d.DelayTicks && tick < t + (uint)(d.DelayTicks + d.GoneTicks))
                    return false;
            return true;
        }

        /// <summary>The trigger tick of the block's current cycle (shaking or gone) at <paramref name="tick"/>, or 0.</summary>
        public uint BlockActiveTrigger(int i, uint tick)
        {
            ref readonly var d = ref Blocks[i];
            foreach (var t in _blockTriggers[i])
                if (tick >= t && tick < t + (uint)(d.DelayTicks + d.GoneTicks))
                    return t;
            return 0;
        }

        public bool StarTaken(int id, uint tick) => _collected.TryGetValue(id, out var e) && tick >= e.Tick;

        public bool TryGetCollect(int id, out RxEntry e) => _collected.TryGetValue(id, out e);

        /// <summary>The tick at which this change takes effect on the motor (late-arrival measurement).</summary>
        public uint EffectiveTick(in RxEntry e) => e.Type switch
        {
            RxType.TileContact => e.Tick + (uint)Grid.WobbleTicks,
            RxType.BlockTrigger => (uint)e.Id < (uint)Blocks.Length ? e.Tick + (uint)Blocks[e.Id].DelayTicks : e.Tick,
            _ => e.Tick,
        };

        // ------------------------------------------------------------------ changes

        /// <summary>Applies one entry; false (and nothing applied) if it is not the next seq (a gap: ask for the full state).</summary>
        public bool Apply(in RxEntry e)
        {
            if (e.Seq != LastSeq + 1)
                return false;
            switch (e.Type)
            {
                case RxType.TileContact:
                    if ((uint)e.Id < (uint)_tileContact.Length)
                    {
                        var t = Math.Max(1u, e.Tick);
                        if (TileContactAt(e.Id, t) == 0 && t > _tileContact[e.Id])
                        {
                            (_tileCycles[e.Id] ??= new List<uint>(2)).Add(t);
                            _tileContact[e.Id] = t;
                        }
                    }

                    break;
                case RxType.BlockTrigger:
                    if ((uint)e.Id < (uint)_blockTriggers.Length)
                        _blockTriggers[e.Id].Add(Math.Max(1u, e.Tick));
                    break;
                case RxType.StarCollect:
                    if (!_collected.ContainsKey(e.Id))
                        _collected[e.Id] = e;
                    break;
                case RxType.StarDrop:
                    Drops.Add(e);
                    break;
            }

            Log.Add(e);
            return true;
        }

        /// <summary>Server: append a change (the next seq) and apply it.</summary>
        public RxEntry Add(RxType type, int id, int slot, uint tick, int xMm = 0, int yMm = 0, int zMm = 0)
        {
            var e = new RxEntry
            {
                Seq = LastSeq + 1, Type = type, Id = (ushort)id, Slot = (byte)slot, Tick = tick,
                Xcm = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, xMm / 10)),
                Ycm = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, yMm / 10)),
                Zcm = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, zMm / 10)),
            };
            Apply(in e);
            return e;
        }

        /// <summary>Can block <paramref name="i"/> be triggered at <paramref name="tick"/> (solid, no cycle pending)?</summary>
        public bool BlockIdle(int i, uint tick) => BlockActiveTrigger(i, tick) == 0;

        /// <summary>A dropped star is grabbable / visible at hazard time? (absolute ticks; pop time, life).</summary>
        public bool DropAvailable(int dropIndex, uint tick, out RxEntry e)
        {
            e = Drops[dropIndex];
            var age = (int)(tick - e.Tick);
            return age >= Stars.DropPopTicks && age < Stars.DropLifeTicks && !StarTaken(HazardMath.StarDropBase + dropIndex, tick);
        }
    }

    /// <summary>
    /// <c>rx</c> (S→C, reliable): <c>u8 kind (0 delta, 1 full) | u8 epoch (load serial) | u32 first seq | u16 count |
    /// count × 14 B entries</c>. A delta whose first seq is not the next one is a gap: the client asks for the full state.
    /// </summary>
    public static class ReactiveCodec
    {
        public const int HeaderSize = 1 + 1 + 4 + 2;
        public const byte Delta = 0, Full = 1;

        public static int Encode(Span<byte> d, byte kind, byte epoch, List<RxEntry> log, int from, int count)
        {
            var o = 0;
            d[o++] = kind;
            d[o++] = epoch;
            NetMessages.WriteU32(d, ref o, count > 0 ? log[from].Seq : (uint)(from + 1));
            NetMessages.WriteU16(d, ref o, (ushort)count);
            for (var i = 0; i < count; i++)
            {
                var e = log[from + i];
                d[o++] = (byte)e.Type;
                NetMessages.WriteU16(d, ref o, e.Id);
                d[o++] = e.Slot;
                NetMessages.WriteU32(d, ref o, e.Tick);
                NetMessages.WriteU16(d, ref o, (ushort)e.Xcm);
                NetMessages.WriteU16(d, ref o, (ushort)e.Ycm);
                NetMessages.WriteU16(d, ref o, (ushort)e.Zcm);
            }

            return o;
        }

        public static bool TryDecode(ReadOnlySpan<byte> s, out byte kind, out byte epoch, out uint firstSeq, List<RxEntry> into)
        {
            kind = epoch = 0;
            firstSeq = 0;
            into.Clear();
            if (s.Length < HeaderSize)
                return false;
            var o = 0;
            kind = s[o++];
            epoch = s[o++];
            firstSeq = NetMessages.ReadU32(s, ref o);
            int n = NetMessages.ReadU16(s, ref o);
            if (kind > Full || s.Length != HeaderSize + n * RxEntry.Size)
                return false;
            for (var i = 0; i < n; i++)
            {
                var e = new RxEntry { Seq = firstSeq + (uint)i, Type = (RxType)s[o++] };
                e.Id = NetMessages.ReadU16(s, ref o);
                e.Slot = s[o++];
                e.Tick = NetMessages.ReadU32(s, ref o);
                e.Xcm = (short)NetMessages.ReadU16(s, ref o);
                e.Ycm = (short)NetMessages.ReadU16(s, ref o);
                e.Zcm = (short)NetMessages.ReadU16(s, ref o);
                if (e.Type < RxType.TileContact || e.Type > RxType.StarDrop)
                    return false;
                into.Add(e);
            }

            return true;
        }
    }
}
