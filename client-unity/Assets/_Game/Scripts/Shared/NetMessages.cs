using System;

namespace FallRush.Shared
{
    /// <summary>One player in a snapshot (23 bytes, protocol v3): doc 04 §4.1 with i32 mm positions and the race byte.</summary>
    public struct PlayerState
    {
        public const int Size = 1 + 12 + 6 + 1 + 1 + 1 + 1;

        // Race byte (v3): low nibble = checkpoints reached (0..15), then status bits.
        public const byte RaceCheckpointMask = 0x0F;
        public const byte RaceFinished = 1 << 4;
        public const byte RaceQualified = 1 << 5;
        public const byte RaceEliminated = 1 << 6;

        public byte Slot;
        public int PosX, PosY, PosZ;
        public short VelX, VelY, VelZ;
        public byte Yaw;
        public byte Flags;
        public byte StunTicks;
        /// <summary>Per-player round state (<see cref="RaceCheckpointMask"/>, <see cref="RaceFinished"/>, …); 0 outside races.</summary>
        public byte Race;

        public static PlayerState From(byte slot, in MotorState m, byte extraFlags = 0, byte race = 0) => new PlayerState
        {
            Race = race,
            Slot = slot,
            PosX = m.PosX, PosY = m.PosY, PosZ = m.PosZ,
            VelX = m.VelX, VelY = m.VelY, VelZ = m.VelZ,
            Yaw = m.Yaw,
            Flags = (byte)(m.Flags | extraFlags),
            StunTicks = m.StunTicks,
        };
    }

    /// <summary>Per-recipient snapshot header (18 bytes).</summary>
    public struct SnapHeader
    {
        public const int Size = 4 + 4 + 1 + 1 + 6 + 1 + 1;
        /// <summary><see cref="OwnEvents"/>: the server pushed this player apart from another one since its last snapshot (not predicted).</summary>
        public const byte EventPushed = 1 << 0;
        public const byte NoSlot = 255;
        /// <summary><see cref="InputLead"/> value meaning "no input received in this window".</summary>
        public const sbyte LeadUnknown = sbyte.MinValue;

        public uint ServerTick;
        /// <summary>Seq of the newest command of this recipient the server has applied (0 = none yet).</summary>
        public uint AckSeq;
        /// <summary>Min over the window of (cmd.tick − next server tick) at arrival; clock-sync feedback.</summary>
        public sbyte InputLead;
        public byte OwnSlot;
        // Owner-only motor timers, so a client rewind restores the complete MotorState.
        public byte OwnCoyote, OwnJumpBuffer, OwnDiveCooldown, OwnLand, OwnImmunity, OwnSurface;
        /// <summary>Server-only events since this recipient's previous snapshot (<see cref="EventPushed"/>), so the client can tell an expected correction from a prediction error.</summary>
        public byte OwnEvents;
        public byte Count;
    }

    /// <summary>
    /// Binary codecs for the M2 movement messages (little-endian; strict decode, no exceptions on bad input).
    /// <list type="bullet">
    /// <item><c>in</c> (C→S): <c>u8 count (1–3)</c> + count × <see cref="InputCmd"/> (13 B), newest first. 40 B with 3.</item>
    /// <item><c>snap</c> (S→C): <see cref="SnapHeader"/> (18 B) + count × <see cref="PlayerState"/> (23 B). 294 B with 12.</item>
    /// <item><c>round</c>, <c>rstate</c>, <c>evt</c> (S→C, reliable, v3): <see cref="RoundMessage"/>, <see cref="RaceStateMessage"/>, <see cref="RaceEvent"/>.</item>
    /// </list>
    /// </summary>
    public static class NetMessages
    {
        public const int MaxInputCmds = 3;
        public const int InputMaxBytes = 1 + MaxInputCmds * InputCmd.Size;
        public const int SnapMaxBytes = SnapHeader.Size + Protocol.MaxPlayers * PlayerState.Size;

        // ---------------- in ----------------

        public static int EncodeInput(Span<byte> dst, ReadOnlySpan<InputCmd> cmdsNewestFirst)
        {
            var n = cmdsNewestFirst.Length;
            if (n < 1 || n > MaxInputCmds)
                throw new ArgumentOutOfRangeException(nameof(cmdsNewestFirst), "1–3 commands");
            var len = 1 + n * InputCmd.Size;
            if (dst.Length < len)
                throw new ArgumentException("buffer too small", nameof(dst));
            dst[0] = (byte)n;
            var o = 1;
            for (var i = 0; i < n; i++)
            {
                ref readonly var c = ref cmdsNewestFirst[i];
                WriteU32(dst, ref o, c.Seq);
                WriteU32(dst, ref o, c.Tick);
                dst[o++] = (byte)c.MoveX;
                dst[o++] = (byte)c.MoveY;
                dst[o++] = c.Buttons;
                WriteU16(dst, ref o, c.CamYaw);
            }

            return len;
        }

        public static bool TryDecodeInput(ReadOnlySpan<byte> src, Span<InputCmd> dst, out int count, out string error)
        {
            count = 0;
            if (src.Length < 1)
            {
                error = "in: empty";
                return false;
            }

            int n = src[0];
            if (n < 1 || n > MaxInputCmds)
            {
                error = $"in: bad count {n}";
                return false;
            }

            if (src.Length != 1 + n * InputCmd.Size)
            {
                error = $"in: length {src.Length} != {1 + n * InputCmd.Size}";
                return false;
            }

            if (dst.Length < n)
            {
                error = "in: destination too small";
                return false;
            }

            var o = 1;
            for (var i = 0; i < n; i++)
            {
                InputCmd c;
                c.Seq = ReadU32(src, ref o);
                c.Tick = ReadU32(src, ref o);
                c.MoveX = (sbyte)src[o++];
                c.MoveY = (sbyte)src[o++];
                c.Buttons = src[o++];
                c.CamYaw = ReadU16(src, ref o);
                dst[i] = c;
            }

            count = n;
            error = null;
            return true;
        }

        // ---------------- snap ----------------

        public static int EncodeSnapHeader(Span<byte> dst, in SnapHeader h)
        {
            if (dst.Length < SnapHeader.Size)
                throw new ArgumentException("buffer too small", nameof(dst));
            var o = 0;
            WriteU32(dst, ref o, h.ServerTick);
            WriteU32(dst, ref o, h.AckSeq);
            dst[o++] = (byte)h.InputLead;
            dst[o++] = h.OwnSlot;
            dst[o++] = h.OwnCoyote;
            dst[o++] = h.OwnJumpBuffer;
            dst[o++] = h.OwnDiveCooldown;
            dst[o++] = h.OwnLand;
            dst[o++] = h.OwnImmunity;
            dst[o++] = h.OwnSurface;
            dst[o++] = h.OwnEvents;
            dst[o++] = h.Count;
            return o;
        }

        public static int EncodePlayers(Span<byte> dst, ReadOnlySpan<PlayerState> players)
        {
            var len = players.Length * PlayerState.Size;
            if (players.Length > Protocol.MaxPlayers)
                throw new ArgumentOutOfRangeException(nameof(players));
            if (dst.Length < len)
                throw new ArgumentException("buffer too small", nameof(dst));
            var o = 0;
            for (var i = 0; i < players.Length; i++)
            {
                ref readonly var p = ref players[i];
                dst[o++] = p.Slot;
                WriteI32(dst, ref o, p.PosX);
                WriteI32(dst, ref o, p.PosY);
                WriteI32(dst, ref o, p.PosZ);
                WriteU16(dst, ref o, (ushort)p.VelX);
                WriteU16(dst, ref o, (ushort)p.VelY);
                WriteU16(dst, ref o, (ushort)p.VelZ);
                dst[o++] = p.Yaw;
                dst[o++] = p.Flags;
                dst[o++] = p.StunTicks;
                dst[o++] = p.Race;
            }

            return o;
        }

        /// <summary>Header + body in one buffer (tests and tools; the server writes the shared body once per tick).</summary>
        public static int EncodeSnap(Span<byte> dst, SnapHeader h, ReadOnlySpan<PlayerState> players)
        {
            h.Count = (byte)players.Length;
            var o = EncodeSnapHeader(dst, h);
            return o + EncodePlayers(dst.Slice(o), players);
        }

        public static bool TryDecodeSnap(ReadOnlySpan<byte> src, out SnapHeader h, Span<PlayerState> players, out string error)
        {
            h = default;
            if (src.Length < SnapHeader.Size)
            {
                error = "snap: truncated header";
                return false;
            }

            var o = 0;
            h.ServerTick = ReadU32(src, ref o);
            h.AckSeq = ReadU32(src, ref o);
            h.InputLead = (sbyte)src[o++];
            h.OwnSlot = src[o++];
            h.OwnCoyote = src[o++];
            h.OwnJumpBuffer = src[o++];
            h.OwnDiveCooldown = src[o++];
            h.OwnLand = src[o++];
            h.OwnImmunity = src[o++];
            h.OwnSurface = src[o++];
            h.OwnEvents = src[o++];
            h.Count = src[o++];

            if (h.Count > Protocol.MaxPlayers)
            {
                error = $"snap: count {h.Count} > {Protocol.MaxPlayers}";
                return false;
            }

            if (h.OwnSlot != SnapHeader.NoSlot && h.OwnSlot >= Protocol.MaxPlayers)
            {
                error = $"snap: bad own slot {h.OwnSlot}";
                return false;
            }

            if (src.Length != SnapHeader.Size + h.Count * PlayerState.Size)
            {
                error = $"snap: length {src.Length} != {SnapHeader.Size + h.Count * PlayerState.Size}";
                return false;
            }

            if (players.Length < h.Count)
            {
                error = "snap: destination too small";
                return false;
            }

            for (var i = 0; i < h.Count; i++)
            {
                PlayerState p;
                p.Slot = src[o++];
                p.PosX = ReadI32(src, ref o);
                p.PosY = ReadI32(src, ref o);
                p.PosZ = ReadI32(src, ref o);
                p.VelX = (short)ReadU16(src, ref o);
                p.VelY = (short)ReadU16(src, ref o);
                p.VelZ = (short)ReadU16(src, ref o);
                p.Yaw = src[o++];
                p.Flags = src[o++];
                p.StunTicks = src[o++];
                p.Race = src[o++];
                if (p.Slot >= Protocol.MaxPlayers)
                {
                    error = $"snap: bad slot {p.Slot}";
                    return false;
                }

                players[i] = p;
            }

            error = null;
            return true;
        }

        // ---------------- little-endian helpers ----------------

        internal static void WriteU16(Span<byte> d, ref int o, ushort v)
        {
            d[o++] = (byte)v;
            d[o++] = (byte)(v >> 8);
        }

        internal static void WriteU32(Span<byte> d, ref int o, uint v)
        {
            d[o++] = (byte)v;
            d[o++] = (byte)(v >> 8);
            d[o++] = (byte)(v >> 16);
            d[o++] = (byte)(v >> 24);
        }

        internal static void WriteI32(Span<byte> d, ref int o, int v) => WriteU32(d, ref o, (uint)v);

        internal static ushort ReadU16(ReadOnlySpan<byte> s, ref int o)
        {
            var v = (ushort)(s[o] | (s[o + 1] << 8));
            o += 2;
            return v;
        }

        internal static uint ReadU32(ReadOnlySpan<byte> s, ref int o)
        {
            var v = (uint)(s[o] | (s[o + 1] << 8) | (s[o + 2] << 16) | (s[o + 3] << 24));
            o += 4;
            return v;
        }

        internal static int ReadI32(ReadOnlySpan<byte> s, ref int o) => (int)ReadU32(s, ref o);
    }
}
