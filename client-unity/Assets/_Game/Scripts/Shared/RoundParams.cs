using System;
using System.Text;

namespace FallRush.Shared
{
    /// <summary>
    /// Everything that makes one round reproducible (VRF-ready, doc 06 §4): which map, which variant, the hazard seed,
    /// the modifier, when hazard time starts, and how many qualify. The server owns it and sends it to every client in
    /// the reliable <c>round</c> message before the countdown, together with the map content hash.
    /// <para><b>Source in M3:</b> a dev seed (<see cref="FromDevSeed"/>). M6 replaces the source with the on-chain deal
    /// (<c>deal_variants[r]</c>, <c>deal_modifiers[r]</c>, <c>hazard_seed = vrf_seed</c>); that is a one-line swap in
    /// <c>ServerSim.ChooseRoundParams</c>.</para>
    /// </summary>
    public struct RoundParams
    {
        public byte MapId;
        public byte Variant;          // 0 = A, 1 = B
        public byte Modifier;         // map_pool_v1 modifier id
        public byte RoundIndex;       // 1..4 (M4); 1 in M3
        public ulong HazardSeed;      // TODO(M6): the 32-byte VRF seed; per-obstacle phase = H(vrf_seed ‖ round ‖ obstacleId)
        public uint RoundStartTick;   // hazard time 0 (= the RUSH tick for races)
        public byte QualifyCount;
        public byte PlayerCount;
        public ushort IntroTicks;
        public ushort CountdownTicks;
        public uint TimerTicks;       // round cap after RUSH (0 = none)

        public override string ToString() =>
            $"map={MapId} variant={(Variant == 0 ? "A" : "B")} modifier={ModifierEffects.Get(Modifier).Key} seed=0x{HazardSeed:x16} start={RoundStartTick} qualify={QualifyCount}/{PlayerCount} intro={IntroTicks} countdown={CountdownTicks} timer={TimerTicks}";

        /// <summary>
        /// Dev source (M3): variant and modifier come from the flags when given, otherwise they are derived from the seed with
        /// the map's variant count and modifier weights from <see cref="MapPoolV1"/> (unbiased rejection sampling on
        /// splitmix64 output; not the on-chain derivation, which is M6).
        /// </summary>
        public static RoundParams FromDevSeed(byte mapId, ulong seed, int variantOverride = -1, int modifierOverride = -1)
        {
            var p = new RoundParams { MapId = mapId, HazardSeed = seed, RoundIndex = 1 };
            var rng = seed ^ 0xF411_0B05_5EEDUL;
            var variants = MapPoolV1.VariantCount(mapId);
            p.Variant = (byte)(variantOverride >= 0 ? variantOverride % Math.Max(1, variants) : (int)Uniform(ref rng, (uint)variants));
            if (modifierOverride >= 0)
            {
                p.Modifier = (byte)modifierOverride;
            }
            else
            {
                var w = MapPoolV1.ModifierWeights(mapId);
                var total = 0u;
                foreach (var (_, weight) in w)
                    total += (uint)weight;
                var r = Uniform(ref rng, total);
                foreach (var (mod, weight) in w)
                {
                    if (r < weight)
                    {
                        p.Modifier = (byte)mod;
                        break;
                    }

                    r -= (uint)weight;
                }
            }

            return p;
        }

        private static ulong SplitMix(ref ulong s)
        {
            s += 0x9E3779B97F4A7C15UL;
            var z = s;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        private static uint Uniform(ref ulong s, uint n)
        {
            if (n <= 1)
                return 0;
            var limit = uint.MaxValue - uint.MaxValue % n;
            while (true)
            {
                var x = (uint)SplitMix(ref s);
                if (x < limit)
                    return x % n;
            }
        }

        /// <summary>
        /// Race qualify count, computed in one place: <c>ceil(P × 0.66)</c> (ruleset_v1 round 1, <c>ceil_pct_of_start</c> 66 %).
        /// TODO(M4/M6): read it from the ruleset (and the on-chain <c>target</c>) instead of this constant.
        /// </summary>
        public static int RaceQualifyCount(int players) => players <= 0 ? 0 : (players * 66 + 99) / 100;
    }

    /// <summary>
    /// The parts of <c>map_pool_v1.json</c> the server needs (variant counts and modifier weights). Mirrored by hand; an
    /// EditMode test checks these against the JSON file on disk, so they cannot drift. The file itself is unchanged
    /// (its hash may be on-chain).
    /// </summary>
    public static class MapPoolV1
    {
        public const byte BlockPartyDash = 0, GooTower = 1, StarScramble = 2, CrumbleShelf = 3;
        /// <summary>Not in the pool: the dev sandbox. Uses BlockPartyDash's tables.</summary>
        public const byte Sandbox = 255;
        /// <summary>Not in the pool: the M4 lobby room (<c>Map_Lobby</c>).</summary>
        public const byte Lobby = 254;

        /// <summary>The <c>rounds</c> table: eligible map ids per round (round 1 first). The deal picks one per round.</summary>
        public static readonly int[][] RoundMaps = { new[] { 0 }, new[] { 1 }, new[] { 2 }, new[] { 3 } };

        public static string ModeOf(byte mapId) => mapId < Modes.Length ? Modes[mapId] : mapId == Lobby ? "Lobby" : "Sandbox";

        public static string KeyOf(byte mapId) => mapId < Keys.Length ? Keys[mapId] : mapId == Lobby ? "Lobby" : "Sandbox";

        public static readonly string[] Keys = { "BlockPartyDash", "GooTower", "StarScramble", "CrumbleShelf" };
        public static readonly string[] Modes = { "Race", "Survival", "Skill", "Final" };
        public static readonly int[] Variants = { 2, 2, 2, 2 };

        public static readonly (int mod, int weight)[][] Weights =
        {
            new[] { (0, 40), (1, 15), (2, 15), (3, 15), (5, 15) },
            new[] { (0, 40) },
            new[] { (0, 40), (1, 15), (3, 15), (4, 15) },
            new[] { (0, 40), (1, 15), (2, 15) },
        };

        public static int VariantCount(byte mapId) => mapId < Variants.Length ? Variants[mapId] : 2;

        public static (int mod, int weight)[] ModifierWeights(byte mapId) => mapId < Weights.Length ? Weights[mapId] : Weights[0];

        public static bool ModifierAllowed(byte mapId, int modifier)
        {
            foreach (var (m, _) in ModifierWeights(mapId))
                if (m == modifier)
                    return true;
            return false;
        }
    }

    /// <summary>
    /// Per-obstacle phase from the round's hazard seed (doc 08 §2: φ = hash(hazard_seed, obstacleId) mapped into the
    /// obstacle's phase range). Hash: FNV-1a 32 over the seed's 8 little-endian bytes then the obstacle id's UTF-8 bytes,
    /// then <see cref="HazardMath.PhaseFromSeed"/>.
    /// TODO(M6): align with the on-chain derivation, <c>H(vrf_seed ‖ round ‖ obstacleId)</c> (doc 06 §4).
    /// </summary>
    public static class ObstaclePhase
    {
        public static uint Hash(ulong seed, string obstacleId)
        {
            var h = 2166136261u;
            for (var i = 0; i < 8; i++)
            {
                h ^= (byte)(seed >> (8 * i));
                h *= 16777619u;
            }

            var bytes = Encoding.UTF8.GetBytes(obstacleId ?? string.Empty);
            foreach (var b in bytes)
            {
                h ^= b;
                h *= 16777619u;
            }

            return h;
        }

        /// <summary>Phase16 in [<paramref name="min16"/>, <paramref name="max16"/>] (inclusive; 0..65535).</summary>
        public static int Phase16(ulong seed, string obstacleId, int min16, int max16)
        {
            if (max16 < min16)
                (min16, max16) = (max16, min16);
            var raw = HazardMath.PhaseFromSeed(Hash(seed, obstacleId));
            var span = max16 - min16 + 1;
            return min16 + (int)((long)raw * span >> 16);
        }
    }
}
