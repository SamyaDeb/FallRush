using System;
using System.Text;

namespace FallRush.Shared
{
    /// <summary>
    /// The match deal (doc 06 §4): bit-identical C# port of <c>packages/shared/src/deal.ts</c> (itself a port of
    /// <c>programs/fall_rush/src/deal.rs</c>), checked against all of <c>deal_vectors.json</c> by an EditMode test.
    /// <code>
    /// rnd(r, label, n):  d = sha256(r ‖ label); read d as eight little-endian u32 words; the first x &lt; zone
    ///                    (zone = floor(2^32 / n) · n) yields x % n; if all eight are rejected, d = sha256(d) and repeat.
    /// weighted(r, label, table): k = rnd(r, label, Σweights); pick by cumulative weight.
    /// round i = 1..4 (ASCII digit): map = pool.rounds[i].maps[rnd("map"+i, len)], variant = rnd("var"+i, variants[map]),
    ///                               modifier = weighted("mod"+i, modifierWeights[map]).
    /// </code>
    /// In M4 the 32-byte input is a <b>practice seed</b> (local); M6 feeds the verified VRF output through the same function.
    /// </summary>
    public static class Deal
    {
        public const int Rounds = 4;

        public struct Result
        {
            public byte[] Maps, Variants, Modifiers;

            public override string ToString() =>
                $"maps=[{string.Join(",", Maps)}] variants=[{string.Join(",", Variants)}] modifiers=[{string.Join(",", Modifiers)}]";
        }

        public static uint RndRange(ReadOnlySpan<byte> r, ReadOnlySpan<byte> label, ulong n)
        {
            if (n < 1 || n > 0xFFFF_FFFFUL)
                throw new ArgumentOutOfRangeException(nameof(n), $"invalid range {n}");
            var zone = (0x1_0000_0000UL / n) * n; // ≤ 2^32
            var buf = new byte[r.Length + label.Length];
            r.CopyTo(buf);
            label.CopyTo(buf.AsSpan(r.Length));
            var d = Sha256.Hash(buf);
            while (true)
            {
                for (var i = 0; i < 8; i++)
                {
                    var x = (uint)(d[i * 4] | d[i * 4 + 1] << 8 | d[i * 4 + 2] << 16 | d[i * 4 + 3] << 24);
                    if (x < zone)
                        return (uint)(x % n);
                }

                d = Sha256.Hash(d);
            }
        }

        public static int Weighted(ReadOnlySpan<byte> r, ReadOnlySpan<byte> label, (int mod, int weight)[] table)
        {
            var total = 0UL;
            foreach (var e in table)
                total += (ulong)e.weight;
            var k = (long)RndRange(r, label, total);
            foreach (var e in table)
            {
                if (k < e.weight)
                    return e.mod;
                k -= e.weight;
            }

            throw new InvalidOperationException("unreachable");
        }

        private static byte[] Label(string prefix, int round) => Encoding.ASCII.GetBytes(prefix + round);

        /// <summary>The deal for 32 bytes of randomness, from the <see cref="MapPoolV1"/> mirror of <c>map_pool_v1.json</c>.</summary>
        public static Result Derive(ReadOnlySpan<byte> randomness)
        {
            if (randomness.Length != 32)
                throw new ArgumentException("randomness must be 32 bytes", nameof(randomness));
            var res = new Result { Maps = new byte[Rounds], Variants = new byte[Rounds], Modifiers = new byte[Rounds] };
            for (var i = 0; i < Rounds; i++)
            {
                var round = i + 1;
                var choices = MapPoolV1.RoundMaps[i];
                var mapId = choices[RndRange(randomness, Label("map", round), (ulong)choices.Length)];
                res.Maps[i] = (byte)mapId;
                res.Variants[i] = (byte)RndRange(randomness, Label("var", round), (ulong)MapPoolV1.VariantCount((byte)mapId));
                res.Modifiers[i] = (byte)Weighted(randomness, Label("mod", round), MapPoolV1.ModifierWeights((byte)mapId));
            }

            return res;
        }

        /// <summary>
        /// Round <paramref name="round"/>'s hazard seed from the match seed (M4): the first 8 bytes (LE) of
        /// <c>sha256("fallrush-hazard-v1" ‖ seed ‖ round)</c>. TODO(M6): the obstacle phase becomes
        /// <c>H(vrf_seed ‖ round ‖ obstacleId)</c> with the full 32 bytes (doc 06 §4); the u64 is the M3 wire format.
        /// </summary>
        public static ulong HazardSeed(ReadOnlySpan<byte> matchSeed, int round)
        {
            var tag = Encoding.ASCII.GetBytes("fallrush-hazard-v1");
            var buf = new byte[tag.Length + matchSeed.Length + 1];
            tag.CopyTo(buf, 0);
            matchSeed.CopyTo(buf.AsSpan(tag.Length));
            buf[buf.Length - 1] = (byte)round;
            var d = Sha256.Hash(buf);
            ulong s = 0;
            for (var i = 7; i >= 0; i--)
                s = s << 8 | d[i];
            return s;
        }
    }
}
