using System;
using System.Collections.Generic;
using System.Text;

namespace FallRush.Shared
{
    /// <summary>
    /// The rolling match transcript (doc 06 §3/§9): bit-identical port of <c>packages/shared/src/transcript.ts</c> and the
    /// program's <c>Match::append</c>.
    /// <code>
    /// init = sha256("fallrush-transcript-v1" ‖ match_id[16] ‖ roster_hash[32])
    /// next = sha256(prev ‖ tag u8 ‖ seq u32-LE ‖ round u8 ‖ slot u8 ‖ arg bytes)
    /// </code>
    /// </summary>
    public static class Transcript
    {
        public const byte Deal = 1, StartRound = 2, Qualified = 3, Eliminated = 4, CompleteRound = 5, CompleteMatch = 6, Abort = 7;
        public const byte NoSlot = 255;

        public static string TagName(byte tag) => tag switch
        {
            Deal => "DEAL",
            StartRound => "START_ROUND",
            Qualified => "QUALIFIED",
            Eliminated => "ELIMINATED",
            CompleteRound => "COMPLETE_ROUND",
            CompleteMatch => "COMPLETE_MATCH",
            Abort => "ABORT",
            _ => $"TAG{tag}",
        };

        public static byte[] Init(ReadOnlySpan<byte> matchId, ReadOnlySpan<byte> rosterHash)
        {
            if (matchId.Length != 16 || rosterHash.Length != 32)
                throw new ArgumentException("match_id is 16 bytes, roster_hash 32");
            var h = new Sha256();
            h.Update(Encoding.ASCII.GetBytes("fallrush-transcript-v1"));
            h.Update(matchId);
            h.Update(rosterHash);
            return h.Final();
        }

        public static byte[] Next(ReadOnlySpan<byte> prev, byte tag, uint seq, byte round, byte slot, ReadOnlySpan<byte> arg)
        {
            var h = new Sha256();
            h.Update(prev);
            Span<byte> mid = stackalloc byte[7];
            mid[0] = tag;
            mid[1] = (byte)seq;
            mid[2] = (byte)(seq >> 8);
            mid[3] = (byte)(seq >> 16);
            mid[4] = (byte)(seq >> 24);
            mid[5] = round;
            mid[6] = slot;
            h.Update(mid);
            h.Update(arg);
            return h.Final();
        }
    }

    /// <summary>
    /// <c>roster_hash</c> (doc 06 §1: commits to <c>(slot, H(user_id), wallet?)</c> per slot). <b>M4 proposal</b>, mirrored by
    /// <c>packages/shared/scripts/gen-transcript-vectors.ts</c> and parity-tested:
    /// <code>
    /// roster_hash = sha256("fallrush-roster-v1" ‖ u8 P ‖ per slot 0…P−1: u8 slot ‖ u8 kind (0 guest, 1 bot)
    ///                       ‖ sha256(utf8 user_id) ‖ u8 hasWallet ‖ [wallet 32 B])
    /// </code>
    /// Placeholder user ids until M5 issues guest identities (TODO(M5)): bots <c>bot:&lt;slot&gt;</c>, practice guests
    /// <c>guest:practice:&lt;slot&gt;</c>.
    /// </summary>
    public static class RosterHash
    {
        public struct Entry
        {
            public byte Slot;
            public bool Bot;
            public string UserId;
            public byte[] Wallet; // 32 B or null
        }

        public static string PlaceholderUserId(int slot, bool bot) => bot ? $"bot:{slot}" : $"guest:practice:{slot}";

        public static byte[] Compute(IReadOnlyList<Entry> roster)
        {
            var h = new Sha256();
            h.Update(Encoding.ASCII.GetBytes("fallrush-roster-v1"));
            h.Update(new[] { (byte)roster.Count });
            for (var i = 0; i < roster.Count; i++)
            {
                var e = roster[i];
                if (e.Slot != i)
                    throw new ArgumentException("roster slots must be 0…P−1 in order");
                h.Update(new[] { e.Slot, e.Bot ? (byte)1 : (byte)0 });
                h.Update(Sha256.Hash(Encoding.UTF8.GetBytes(e.UserId ?? string.Empty)));
                if (e.Wallet != null)
                {
                    if (e.Wallet.Length != 32)
                        throw new ArgumentException("wallet must be 32 bytes");
                    h.Update(new byte[] { 1 });
                    h.Update(e.Wallet);
                }
                else
                {
                    h.Update(new byte[] { 0 });
                }
            }

            return h.Final();
        }
    }
}
