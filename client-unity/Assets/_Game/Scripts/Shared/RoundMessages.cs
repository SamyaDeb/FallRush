using System;

namespace FallRush.Shared
{
    /// <summary>Round phases (doc 01 §5.1, M3 prompt §5.1). The byte value is on the wire.</summary>
    public enum RoundPhase : byte
    {
        Waiting = 0,   // sandbox, or a race waiting for players
        Intro = 1,     // camera fly-through, ≤ 4 s
        Countdown = 2, // 3-2-1, inputs ignored, start gate closed
        Rush = 3,      // gate dropped
        Ended = 4,     // results
        Sandbox = 5,   // free play (Map_Sandbox): no rules
    }

    public enum RaceEventType : byte
    {
        Checkpoint = 1, // arg = checkpoint index reached
        Finished = 2,   // arg = finish place (1-based)
        Qualified = 3,  // arg = qualify place
        Eliminated = 4, // arg = final place
        Respawned = 5,  // arg = checkpoint index respawned at
        RoundEnd = 6,   // arg = qualified count
        Phase = 7,      // arg = new RoundPhase
        Out = 8,        // M4: knocked out during the round (survival / final), arg = ElimReason (decided at round end)
        Scored = 9,     // M4: skill, arg = new score
        Dropped = 10,   // M4: skill, lost a star on a knockback, arg = new score
    }

    /// <summary><c>evt</c> (S→C, reliable, 14 B): one domain event with a sequence number (the HUD, later the chain worker).</summary>
    public struct RaceEvent
    {
        public const int Size = 4 + 1 + 4 + 1 + 4;
        public uint Seq;
        public RaceEventType Type;
        public uint Tick;
        public byte Slot;
        public int Arg;

        public int Encode(Span<byte> d)
        {
            var o = 0;
            NetMessages.WriteU32(d, ref o, Seq);
            d[o++] = (byte)Type;
            NetMessages.WriteU32(d, ref o, Tick);
            d[o++] = Slot;
            NetMessages.WriteI32(d, ref o, Arg);
            return o;
        }

        public static bool TryDecode(ReadOnlySpan<byte> s, out RaceEvent e)
        {
            e = default;
            if (s.Length != Size)
                return false;
            var o = 0;
            e.Seq = NetMessages.ReadU32(s, ref o);
            e.Type = (RaceEventType)s[o++];
            e.Tick = NetMessages.ReadU32(s, ref o);
            e.Slot = s[o++];
            e.Arg = NetMessages.ReadI32(s, ref o);
            return e.Type >= RaceEventType.Checkpoint && e.Type <= RaceEventType.Dropped && (e.Slot < Protocol.MaxPlayers || e.Slot == 255);
        }

        public override string ToString() => $"evt#{Seq} {Type} tick={Tick} slot={Slot} arg={Arg}";
    }

    /// <summary>
    /// <c>round</c> (S→C, reliable, 59 B): the <see cref="RoundParams"/> plus the server's map content hash. Sent on
    /// connect and at every new round, before the countdown. A client whose own map hash differs refuses the round.
    /// </summary>
    public struct RoundMessage
    {
        public const byte FormatVersion = 1;
        public const int Size = 1 + 1 + 1 + 1 + 1 + 8 + 4 + 1 + 1 + 2 + 2 + 4 + 32;

        public RoundParams P;
        public byte[] MapHash;

        public int Encode(Span<byte> d)
        {
            var o = 0;
            d[o++] = FormatVersion;
            d[o++] = P.MapId;
            d[o++] = P.Variant;
            d[o++] = P.Modifier;
            d[o++] = P.RoundIndex;
            NetMessages.WriteU32(d, ref o, (uint)P.HazardSeed);
            NetMessages.WriteU32(d, ref o, (uint)(P.HazardSeed >> 32));
            NetMessages.WriteU32(d, ref o, P.RoundStartTick);
            d[o++] = P.QualifyCount;
            d[o++] = P.PlayerCount;
            NetMessages.WriteU16(d, ref o, P.IntroTicks);
            NetMessages.WriteU16(d, ref o, P.CountdownTicks);
            NetMessages.WriteU32(d, ref o, P.TimerTicks);
            for (var i = 0; i < 32; i++)
                d[o++] = MapHash != null && i < MapHash.Length ? MapHash[i] : (byte)0;
            return o;
        }

        public static bool TryDecode(ReadOnlySpan<byte> s, out RoundMessage m)
        {
            m = default;
            if (s.Length != Size || s[0] != FormatVersion)
                return false;
            var o = 1;
            m.P.MapId = s[o++];
            m.P.Variant = s[o++];
            m.P.Modifier = s[o++];
            m.P.RoundIndex = s[o++];
            var lo = NetMessages.ReadU32(s, ref o);
            var hi = NetMessages.ReadU32(s, ref o);
            m.P.HazardSeed = lo | ((ulong)hi << 32);
            m.P.RoundStartTick = NetMessages.ReadU32(s, ref o);
            m.P.QualifyCount = s[o++];
            m.P.PlayerCount = s[o++];
            m.P.IntroTicks = NetMessages.ReadU16(s, ref o);
            m.P.CountdownTicks = NetMessages.ReadU16(s, ref o);
            m.P.TimerTicks = NetMessages.ReadU32(s, ref o);
            m.MapHash = s.Slice(o, 32).ToArray();
            return m.P.Variant <= 1 && m.P.Modifier < ModifiersData.All.Length;
        }
    }

    /// <summary>
    /// <c>rstate</c> (S→C, reliable, 16 + 5 B per finisher): the full round state, sent on connect and on every phase
    /// change, so the HUD is right after a reconnect. Per-player checkpoint / finished / qualified bits ride in every
    /// snapshot (<see cref="PlayerState.Race"/>).
    /// </summary>
    public struct RaceStateMessage
    {
        public const int HeaderSize = 4 + 1 + 4 + 4 + 1 + 1 + 1;
        public const int PerFinisher = 5;

        public uint LastEventSeq;
        public RoundPhase Phase;
        public uint PhaseStartTick;
        public uint RushEndTick;     // timer expiry (0 = none)
        public byte QualifyCount;
        public byte QualifiedCount;
        public byte FinisherCount;
        public byte[] FinisherSlots;  // finish order
        public uint[] FinisherTicks;

        public int Encode(Span<byte> d)
        {
            var o = 0;
            NetMessages.WriteU32(d, ref o, LastEventSeq);
            d[o++] = (byte)Phase;
            NetMessages.WriteU32(d, ref o, PhaseStartTick);
            NetMessages.WriteU32(d, ref o, RushEndTick);
            d[o++] = QualifyCount;
            d[o++] = QualifiedCount;
            d[o++] = FinisherCount;
            for (var i = 0; i < FinisherCount; i++)
            {
                d[o++] = FinisherSlots[i];
                NetMessages.WriteU32(d, ref o, FinisherTicks[i]);
            }

            return o;
        }

        public int Length => HeaderSize + FinisherCount * PerFinisher;

        public static bool TryDecode(ReadOnlySpan<byte> s, out RaceStateMessage m)
        {
            m = default;
            if (s.Length < HeaderSize)
                return false;
            var o = 0;
            m.LastEventSeq = NetMessages.ReadU32(s, ref o);
            m.Phase = (RoundPhase)s[o++];
            m.PhaseStartTick = NetMessages.ReadU32(s, ref o);
            m.RushEndTick = NetMessages.ReadU32(s, ref o);
            m.QualifyCount = s[o++];
            m.QualifiedCount = s[o++];
            m.FinisherCount = s[o++];
            if (m.FinisherCount > Protocol.MaxPlayers || s.Length != HeaderSize + m.FinisherCount * PerFinisher || m.Phase > RoundPhase.Sandbox)
                return false;
            m.FinisherSlots = new byte[m.FinisherCount];
            m.FinisherTicks = new uint[m.FinisherCount];
            for (var i = 0; i < m.FinisherCount; i++)
            {
                m.FinisherSlots[i] = s[o++];
                if (m.FinisherSlots[i] >= Protocol.MaxPlayers)
                    return false;
                m.FinisherTicks[i] = NetMessages.ReadU32(s, ref o);
            }

            return true;
        }
    }
}
