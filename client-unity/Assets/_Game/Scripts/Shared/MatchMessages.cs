using System;
using System.Text;

namespace FallRush.Shared
{
    /// <summary>One roster row of the <c>match</c> message.</summary>
    public struct MatchSlotInfo
    {
        public const byte Active = 1, Bot = 2, Alive = 4, Connected = 8, GraceExpired = 16, Host = 32, Ready = 64, Rostered = 128;
        public const int MaxName = 16;

        public byte Slot;
        public byte Flags;
        public BotTier Tier;
        public ElimReason Reason;
        public byte EliminatedRound;
        public byte[] Places; // 4
        public byte LateMask;
        public string Name;

        public bool Has(byte f) => (Flags & f) != 0;
    }

    /// <summary>
    /// <c>match</c> (S→C, reliable, per recipient): the full match state, sent on join / reconnect and whenever the phase or
    /// the roster changes. A client never needs earlier messages to show the right screen. ≤ 452 B for 12 slots.
    /// </summary>
    public sealed class MatchStateMessage
    {
        public const byte FormatVersion = 1;
        public const int HeaderSize = 1 + 1 + 4 + 4 + 4 + 1 + 1 + 1 + 1 + 1 + 1 + 1 + 1 + 16 + 32 + 32 + 4 + 16 + 16 + 1 + 1;
        public const byte FlagPractice = 1, FlagSoak = 2, FlagLoading = 4;

        public MatchPhase Phase;
        public uint PhaseStartTick, PhaseEndTick, ServerTick;
        public byte Round, RoundCount, LoadSerial, Flags, HostSlot = 255, OwnSlot = 255, Champion = 255, PlayerCount;
        public byte[] MatchId = new byte[16];
        public byte[] Seed = new byte[32];
        public byte[] Transcript = new byte[32];
        public uint EventCount;
        public byte[] Maps = new byte[4], Variants = new byte[4], Modifiers = new byte[4], Targets = new byte[4];
        public byte[] Token = new byte[16];
        public byte MatchSerial;
        public MatchSlotInfo[] Slots = Array.Empty<MatchSlotInfo>();

        public static int MaxSize => HeaderSize + Protocol.MaxPlayers * (10 + MatchSlotInfo.MaxName);

        public int Encode(Span<byte> d)
        {
            var o = 0;
            d[o++] = FormatVersion;
            d[o++] = (byte)Phase;
            NetMessages.WriteU32(d, ref o, PhaseStartTick);
            NetMessages.WriteU32(d, ref o, PhaseEndTick);
            NetMessages.WriteU32(d, ref o, ServerTick);
            d[o++] = Round;
            d[o++] = RoundCount;
            d[o++] = LoadSerial;
            d[o++] = Flags;
            d[o++] = HostSlot;
            d[o++] = OwnSlot;
            d[o++] = Champion;
            d[o++] = PlayerCount;
            Copy(MatchId, 16, d, ref o);
            Copy(Seed, 32, d, ref o);
            Copy(Transcript, 32, d, ref o);
            NetMessages.WriteU32(d, ref o, EventCount);
            Copy(Maps, 4, d, ref o);
            Copy(Variants, 4, d, ref o);
            Copy(Modifiers, 4, d, ref o);
            Copy(Targets, 4, d, ref o);
            Copy(Token, 16, d, ref o);
            d[o++] = MatchSerial;
            d[o++] = (byte)Slots.Length;
            foreach (var s in Slots)
            {
                d[o++] = s.Slot;
                d[o++] = s.Flags;
                d[o++] = (byte)((byte)s.Tier | (byte)s.Reason << 4);
                d[o++] = s.EliminatedRound;
                Copy(s.Places, 4, d, ref o);
                d[o++] = s.LateMask;
                var name = Utf8Clamp(s.Name, MatchSlotInfo.MaxName);
                d[o++] = (byte)name.Length;
                name.CopyTo(d.Slice(o));
                o += name.Length;
            }

            return o;
        }

        private static void Copy(byte[] src, int n, Span<byte> d, ref int o)
        {
            for (var i = 0; i < n; i++)
                d[o + i] = src != null && i < src.Length ? src[i] : (byte)0;
            o += n;
        }

        public static byte[] Utf8Clamp(string s, int max)
        {
            var b = Encoding.UTF8.GetBytes(s ?? string.Empty);
            if (b.Length <= max)
                return b;
            // Cut on a character boundary.
            var n = max;
            while (n > 0 && (b[n] & 0xC0) == 0x80)
                n--;
            var r = new byte[n];
            Array.Copy(b, r, n);
            return r;
        }

        public static bool TryDecode(ReadOnlySpan<byte> s, out MatchStateMessage m)
        {
            m = null;
            if (s.Length < HeaderSize || s[0] != FormatVersion)
                return false;
            var r = new MatchStateMessage();
            var o = 1;
            r.Phase = (MatchPhase)s[o++];
            if (r.Phase > MatchPhase.Aborted)
                return false;
            r.PhaseStartTick = NetMessages.ReadU32(s, ref o);
            r.PhaseEndTick = NetMessages.ReadU32(s, ref o);
            r.ServerTick = NetMessages.ReadU32(s, ref o);
            r.Round = s[o++];
            r.RoundCount = s[o++];
            r.LoadSerial = s[o++];
            r.Flags = s[o++];
            r.HostSlot = s[o++];
            r.OwnSlot = s[o++];
            r.Champion = s[o++];
            r.PlayerCount = s[o++];
            r.MatchId = s.Slice(o, 16).ToArray(); o += 16;
            r.Seed = s.Slice(o, 32).ToArray(); o += 32;
            r.Transcript = s.Slice(o, 32).ToArray(); o += 32;
            r.EventCount = NetMessages.ReadU32(s, ref o);
            r.Maps = s.Slice(o, 4).ToArray(); o += 4;
            r.Variants = s.Slice(o, 4).ToArray(); o += 4;
            r.Modifiers = s.Slice(o, 4).ToArray(); o += 4;
            r.Targets = s.Slice(o, 4).ToArray(); o += 4;
            r.Token = s.Slice(o, 16).ToArray(); o += 16;
            r.MatchSerial = s[o++];
            int n = s[o++];
            if (n > Protocol.MaxPlayers || r.Round > 4)
                return false;
            r.Slots = new MatchSlotInfo[n];
            for (var i = 0; i < n; i++)
            {
                if (s.Length - o < 10)
                    return false;
                var e = new MatchSlotInfo { Slot = s[o++], Flags = s[o++] };
                var tr = s[o++];
                e.Tier = (BotTier)(tr & 0x0F);
                e.Reason = (ElimReason)(tr >> 4);
                e.EliminatedRound = s[o++];
                e.Places = s.Slice(o, 4).ToArray(); o += 4;
                e.LateMask = s[o++];
                int len = s[o++];
                if (len > MatchSlotInfo.MaxName || s.Length - o < len || e.Slot >= Protocol.MaxPlayers || e.Reason > ElimReason.Disconnect || e.Tier > BotTier.Good)
                    return false;
                try
                {
                    e.Name = new UTF8Encoding(false, true).GetString(s.Slice(o, len).ToArray());
                }
                catch (ArgumentException)
                {
                    return false;
                }

                o += len;
                r.Slots[i] = e;
            }

            if (o != s.Length)
                return false;
            m = r;
            return true;
        }
    }

    /// <summary><c>mevt</c> (S→C, reliable): one transcript event (<see cref="MatchEvent"/>), 12 B + arg (DEAL 44 B).</summary>
    public static class MatchEventCodec
    {
        public const int HeaderSize = 4 + 1 + 1 + 1 + 4 + 1;

        public static int Encode(Span<byte> d, in MatchEvent e)
        {
            var o = 0;
            NetMessages.WriteU32(d, ref o, e.Seq);
            d[o++] = e.Tag;
            d[o++] = e.Round;
            d[o++] = e.Slot;
            NetMessages.WriteU32(d, ref o, e.Tick);
            var n = e.Arg?.Length ?? 0;
            d[o++] = (byte)n;
            for (var i = 0; i < n; i++)
                d[o++] = e.Arg[i];
            return o;
        }

        public static bool TryDecode(ReadOnlySpan<byte> s, out MatchEvent e)
        {
            e = default;
            if (s.Length < HeaderSize)
                return false;
            var o = 0;
            e.Seq = NetMessages.ReadU32(s, ref o);
            e.Tag = s[o++];
            e.Round = s[o++];
            e.Slot = s[o++];
            e.Tick = NetMessages.ReadU32(s, ref o);
            int n = s[o++];
            if (s.Length != HeaderSize + n || n > 32 || e.Tag < Transcript.Deal || e.Tag > Transcript.Abort || e.Round > 4)
                return false;
            e.Arg = s.Slice(o, n).ToArray();
            return true;
        }
    }

    /// <summary>Small client → server commands (<c>ctl</c>, reliable, 5 B).</summary>
    public enum ControlCmd : byte
    {
        Start = 1,     // host: start the match now
        Back = 2,      // receipt dismissed ("Back to lobby")
        RxResend = 3,  // reactive state gap: send the full state (arg = last seq seen)
        Emote = 4,     // G1: play emote arg (1–4) — lobby, results, intermission, champion, receipt; relayed as `emo`
    }

    /// <summary><c>emo</c> (S→C, reliable, 2 B, G1): slot <c>u8</c> | emote id <c>u8</c> (1–4). Visual only.</summary>
    public static class EmoteCodec
    {
        public const int Size = 2;
        public const int MaxId = 4;

        public static int Encode(Span<byte> d, int slot, int id)
        {
            d[0] = (byte)slot;
            d[1] = (byte)id;
            return Size;
        }

        public static bool TryDecode(ReadOnlySpan<byte> s, out int slot, out int id)
        {
            slot = id = 0;
            if (s.Length != Size || s[0] >= Protocol.MaxPlayers || s[1] < 1 || s[1] > MaxId)
                return false;
            slot = s[0];
            id = s[1];
            return true;
        }

        /// <summary>Emotes are allowed outside round play (GDD §6: lobby, intermission, spectating).</summary>
        public static bool PhaseAllows(MatchPhase p) => p != MatchPhase.RoundIntro && p != MatchPhase.RoundActive;
    }

    public static class ControlCodec
    {
        public const int Size = 5;

        public static int Encode(Span<byte> d, ControlCmd c, uint arg)
        {
            d[0] = (byte)c;
            var o = 1;
            NetMessages.WriteU32(d, ref o, arg);
            return o;
        }

        public static bool TryDecode(ReadOnlySpan<byte> s, out ControlCmd c, out uint arg)
        {
            c = 0;
            arg = 0;
            if (s.Length != Size || s[0] < 1 || s[0] > 4)
                return false;
            c = (ControlCmd)s[0];
            var o = 1;
            arg = NetMessages.ReadU32(s, ref o);
            return true;
        }
    }

    /// <summary><c>ready</c> (C→S, reliable, 5 B): the map of load <see cref="LoadSerial"/> is loaded and its content hash matched.</summary>
    public struct ReadyMessage
    {
        public const int Size = 5;
        public byte LoadSerial;
        public uint MapHashPrefix;

        public int Encode(Span<byte> d)
        {
            d[0] = LoadSerial;
            var o = 1;
            NetMessages.WriteU32(d, ref o, MapHashPrefix);
            return o;
        }

        public static bool TryDecode(ReadOnlySpan<byte> s, out ReadyMessage m)
        {
            m = default;
            if (s.Length != Size)
                return false;
            m.LoadSerial = s[0];
            var o = 1;
            m.MapHashPrefix = NetMessages.ReadU32(s, ref o);
            return true;
        }

        public static uint Prefix(byte[] hash) => hash == null || hash.Length < 4 ? 0u : (uint)(hash[0] | hash[1] << 8 | hash[2] << 16 | hash[3] << 24);
    }
}
