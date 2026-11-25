using System;
using System.Collections.Generic;

namespace FallRush.Shared
{
    /// <summary>
    /// fullchain (docs/proof/fullchain/CONTRACT.md): read-only view of the on-chain director (<c>rush_core::rules::director::DirectorState</c>)
    /// inside a canonical World (<c>World::to_bytes</c>, version 1 or 2, same layout): 123 B at World offset 1910.
    /// Field order = <c>DirectorState::enc</c>. Pure (no Unity), shared by the game server (liveness / result of a full
    /// on-chain match) and the client feed; tested in EditMode.
    /// </summary>
    public readonly struct ChainDirector
    {
        /// <summary>Offset of the director in the World, and its size.</summary>
        public const int InWorld = 1910, Size = 123;

        // rush-core director::phase (= MatchPhase)
        public const byte LobbyOpen = 0, RosterLocked = 2, Dealing = 3, Dealt = 4, RoundIntro = 5, RoundActive = 6, RoundResult = 7,
            Intermission = 8, Champion = 9, Finalizing = 10, Done = 11, Aborted = 12;

        private readonly byte[] _b;
        private readonly int _o;

        /// <param name="worldOffset">Offset of the World's first byte (its version) in <paramref name="b"/>.</param>
        public ChainDirector(byte[] b, int worldOffset)
        {
            _b = b;
            _o = worldOffset + InWorld;
        }

        public bool Valid => _b != null && _o >= InWorld && _o + Size <= _b.Length;

        private ushort U16(int o) => (ushort)(_b[_o + o] | _b[_o + o + 1] << 8);
        private uint U32(int o) => BitConverter.ToUInt32(_b, _o + o);

        public byte Phase => _b[_o];
        public uint PhaseStart => U32(1);
        public uint PhaseEnd => U32(5);
        public byte Round => _b[_o + 9];
        public byte RoundCount => _b[_o + 10];
        public byte PlayerCount => _b[_o + 11];
        public void Seed(byte[] dst) => Buffer.BlockCopy(_b, _o + 12, dst, 0, 32);
        public void Transcript(byte[] dst) => Buffer.BlockCopy(_b, _o + 44, dst, 0, 32);
        public uint EventCount => U32(76);
        public byte Map(int r) => _b[_o + 80 + r];
        public byte Variant(int r) => _b[_o + 84 + r];
        public byte Modifier(int r) => _b[_o + 88 + r];
        public byte Target(int r) => _b[_o + 92 + r];
        public ushort RosterMask => U16(96);
        public ushort AliveMask => U16(98);
        public ushort BotMask => U16(100);
        public bool InRoster(int s) => (RosterMask >> s & 1) != 0;
        public bool Alive(int s) => (AliveMask >> s & 1) != 0;
        public bool Bot(int s) => (BotMask >> s & 1) != 0;
        public bool MapReady => _b[_o + 108] != 0;
        /// <summary>255 until CHAMPION.</summary>
        public byte ChampionSlot => _b[_o + 109];
        /// <summary>Elimination round of <paramref name="s"/> (0 = not eliminated).</summary>
        public int ElimRound(int s) => _b[_o + 110 + s] >> 4;
        public byte ElimReason(int s) => (byte)(_b[_o + 110 + s] & 0xF);
        public byte LastAbort => _b[_o + 122];

        /// <summary>The director has locked a roster and runs (or ran) a match: ROSTER_LOCKED … DONE.</summary>
        public bool Running => Phase >= RosterLocked && Phase <= Done;

        /// <summary>The match result is known (CHAMPION, FINALIZING, DONE with a champion slot).</summary>
        public bool HasChampion => Phase >= Champion && Phase <= Done && ChampionSlot < 12 && InRoster(ChampionSlot);

        /// <summary>
        /// The match's final ranking: the champion, then the others by elimination round (later = better), then slot.
        /// Reasons by rank (the champion's = 0). False before <see cref="HasChampion"/>.
        /// </summary>
        public bool TryResult(out int[] order, out byte[] reasons)
        {
            order = null;
            reasons = null;
            if (!Valid || !HasChampion)
                return false;
            var champ = ChampionSlot;
            var rest = new List<int>(12);
            for (var s = 0; s < 12; s++)
                if (InRoster(s) && s != champ)
                    rest.Add(s);
            var self = this;
            rest.Sort((a, b) =>
            {
                var ra = self.ElimRound(a) == 0 ? 99 : self.ElimRound(a);
                var rb = self.ElimRound(b) == 0 ? 99 : self.ElimRound(b);
                return ra != rb ? rb.CompareTo(ra) : a.CompareTo(b);
            });
            order = new int[rest.Count + 1];
            reasons = new byte[rest.Count + 1];
            order[0] = champ;
            for (var k = 0; k < rest.Count; k++)
            {
                order[k + 1] = rest[k];
                reasons[k + 1] = ElimReason(rest[k]);
            }

            return true;
        }
    }
}
