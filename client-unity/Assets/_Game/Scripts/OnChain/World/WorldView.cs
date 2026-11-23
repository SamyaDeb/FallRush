using System;

namespace FallRush.OnChain
{
    /// <summary>
    /// P3-D: read-only view of a canonical <c>rush_core::World</c> (<c>World::to_bytes</c>, rush-core 1 / World v1).
    /// Offsets are the ones <c>rcx_layout</c> measures by encoding (tools/chain-emulator/core): header 22 B, 12 players of
    /// 115 B from 22, reactive 318 B, drop queue 48 B, round state 142 B at 1768, director 123 B at 1910, 2037 B total.
    /// A World of another size is rejected (<see cref="Valid"/>), so a rush-core layout change fails loudly
    /// (P3CoreTests checks these numbers against the core in use).
    /// </summary>
    public readonly struct WorldView
    {
        public const int Size = 2037;
        public const int PlayersOffset = 22, PlayerStride = 115, SlotRulesInPlayer = 46;
        public const int ReactiveOffset = PlayersOffset + 12 * PlayerStride; // 1402
        public const int RoundOffset = 1768, RoundResultInRound = 52;

        // rush-core rules::phase / mapblob::mode / SlotRules flags
        public const byte PhaseWaiting = 0, PhaseIntro = 1, PhaseCountdown = 2, PhaseRush = 3, PhaseEnded = 4, PhaseSandbox = 5;
        public const byte ModeFinal = 4;
        public const byte SlotIn = 1, SlotOut = 2, SlotDone = 4;
        public const byte PlayerActive = 1, PlayerBot = 2;

        private readonly byte[] _b;
        private readonly int _o;
        public readonly int Length;

        public WorldView(byte[] b, int offset, int length)
        {
            _b = b;
            _o = offset;
            Length = length;
        }

        public bool Valid => _b != null && Length == Size && _o + Length <= _b.Length && _b[_o] == 1;

        private byte U8(int o) => _b[_o + o];
        private ushort U16(int o) => (ushort)(_b[_o + o] | _b[_o + o + 1] << 8);
        private int I32(int o) => BitConverter.ToInt32(_b, _o + o);
        private uint U32(int o) => BitConverter.ToUInt32(_b, _o + o);

        public uint Tick => U32(1);
        public byte Rate => U8(5);
        public byte RoundIndex => U8(6);
        public byte MapKey => U8(7);
        public byte Variant => U8(8);
        public byte Modifier => U8(9);
        public ulong HazardSeed => BitConverter.ToUInt64(_b, _o + 10);
        public uint RoundStart => U32(18);

        // ---- players
        private int P(int slot) => PlayersOffset + slot * PlayerStride;
        public int PosX(int s) => I32(P(s));
        public int PosY(int s) => I32(P(s) + 4);
        public int PosZ(int s) => I32(P(s) + 8);
        public int VelX(int s) => I32(P(s) + 12);
        public int VelY(int s) => I32(P(s) + 16);
        public int VelZ(int s) => I32(P(s) + 20);
        public byte Yaw(int s) => U8(P(s) + 24);
        public byte MotorFlags(int s) => U8(P(s) + 25);
        public byte Stun(int s) => U8(P(s) + 26);
        public byte SlotFlags(int s) => U8(P(s) + 33);
        public byte RespawnTicks(int s) => U8(P(s) + 34);
        public byte LastStep(int s) => U8(P(s) + 35);
        public ushort Falls(int s) => U16(P(s) + 42);
        public bool Active(int s) => (SlotFlags(s) & PlayerActive) != 0;
        public bool IsBot(int s) => (SlotFlags(s) & PlayerBot) != 0;
        public byte RulesFlags(int s) => U8(P(s) + SlotRulesInPlayer);
        public byte Place(int s) => U8(P(s) + SlotRulesInPlayer + 10);
        public uint OutTick(int s) => U32(P(s) + SlotRulesInPlayer + 11);
        public byte OutReason(int s) => U8(P(s) + SlotRulesInPlayer + 15);
        public bool InRound(int s) => (RulesFlags(s) & SlotIn) != 0;
        public bool Out(int s) => (RulesFlags(s) & SlotOut) != 0;

        /// <summary>The 36-B <c>rc_player</c> record of <paramref name="slot"/> (motor 33 B + flags, respawn, last step).</summary>
        public void PlayerRecord(int slot, byte[] dst, int dstOffset) => Buffer.BlockCopy(_b, _o + P(slot), dst, dstOffset, 36);

        // ---- round state
        public byte Mode => U8(RoundOffset);
        public byte Phase => U8(RoundOffset + 1);
        public uint PhaseStart => U32(RoundOffset + 2);
        public uint RushTick => U32(RoundOffset + 6);
        public uint RushEnd => U32(RoundOffset + 10);
        public ushort IntroTicks => U16(RoundOffset + 14);
        public ushort CountdownTicks => U16(RoundOffset + 16);
        public byte QualifyCount => U8(RoundOffset + 19);
        public byte QualifiedCount => U8(RoundOffset + 20);
        public byte FinishedCount => U8(RoundOffset + 21);
        public bool HasResult => U8(RoundOffset + 137) != 0;
        public uint RoundEventSeq => U32(RoundOffset + 138);
        public byte ResultCount => U8(RoundOffset + RoundResultInRound);
        public byte ResultOrder(int k) => U8(RoundOffset + RoundResultInRound + 1 + k);
        public ushort ResultQualified => U16(RoundOffset + RoundResultInRound + 13);
        /// <summary>Elimination reason of the player at result rank <paramref name="k"/> (indexed by rank, like <c>order</c>).</summary>
        public byte ResultReason(int k) => U8(RoundOffset + RoundResultInRound + 15 + k);
        public uint ResultEndTick => U32(RoundOffset + RoundResultInRound + 80);

        /// <summary>The Final's champion (first of the result order), or 255 before the result.</summary>
        public byte Champion => HasResult && ResultCount > 0 ? ResultOrder(0) : (byte)255;

        public uint ActiveMask
        {
            get
            {
                var m = 0u;
                for (var s = 0; s < 12; s++)
                    if (Active(s))
                        m |= 1u << s;
                return m;
            }
        }
    }

    /// <summary>
    /// P3-D: the arena account (<c>WorldAcc</c>) around the World. Offsets come from the chain config (emulator v0 =
    /// docs/proof/p3/wire-v0.md §3; P2's real layout replaces them at G-layout).
    /// </summary>
    [Serializable]
    public sealed class WorldAccLayout
    {
        public int offset = 72;
        public int lenOffset = 68;
        public int tickOffset = 12;
        public int appliedTickOffset = 16;
        public int coreVersionOffset = 8;
        /// <summary>P4 0.2: the match serial (u32; 0 or −1 = none: infer a new match from the tick restarting).</summary>
        public int arenaSeqOffset = -1;
        public int flagsOffset = 70;
        /// <summary>When the account has no length field (fixed layout): the World length.</summary>
        public int fixedLen;

        public int WorldLen(byte[] acc, int accLen)
        {
            if (fixedLen > 0)
                return Math.Min(fixedLen, accLen - offset);
            if (lenOffset < 0 || lenOffset + 2 > accLen)
                return accLen - offset;
            return Math.Min(acc[lenOffset] | acc[lenOffset + 1] << 8, accLen - offset);
        }

        public uint Tick(byte[] acc) => BitConverter.ToUInt32(acc, tickOffset);
        public uint Applied(byte[] acc, int slot) => appliedTickOffset < 0 ? 0u : BitConverter.ToUInt32(acc, appliedTickOffset + slot * 4);
        public uint CoreVersion(byte[] acc) => coreVersionOffset < 0 ? 0u : BitConverter.ToUInt32(acc, coreVersionOffset);
        public uint ArenaSeq(byte[] acc) => arenaSeqOffset < 0 ? 0u : BitConverter.ToUInt32(acc, arenaSeqOffset);

        /// <summary>P4 0.1: an arena core (rush_core::VERSION, or a full rc_version) matches the client core when the
        /// low 16 bits (rush_core::VERSION) agree; 0 = unknown (an older program), accepted.</summary>
        public static bool CoreMatches(uint arena, uint client) => arena == 0 || (arena & 0xFFFF) == (client & 0xFFFF);
        public ushort Flags(byte[] acc) => flagsOffset < 0 ? (ushort)0 : (ushort)(acc[flagsOffset] | acc[flagsOffset + 1] << 8);
    }
}
