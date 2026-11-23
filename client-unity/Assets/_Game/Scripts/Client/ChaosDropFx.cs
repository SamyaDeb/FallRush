using System;
using FallRush.Shared;

namespace FallRush.Client
{
    /// <summary>
    /// P4 Chaos Drops, visual side: the on-chain Final's drop queue (from the rush-core World, set by the on-chain
    /// session each update) and C# ports of the rush-core <c>drops</c> functions the views need (Lid Frenzy lid states,
    /// Sweeper Rush time warp, Bumper Rain spots, Wind Gust direction). The simulation itself runs in rush-core
    /// (prediction + the program); these only make the lids / sweeper / bumpers look exactly like what the core steps.
    /// Empty queue (Classic, practice) = no change.
    /// </summary>
    public static class ChaosDropFx
    {
        public const byte LidFrenzy = 1, BumperRain = 2, WindGust = 3, SweeperRush = 4;
        public const int Telegraph = 23, FrenzyLit = 30, FrenzyOpen = 30, FrenzyRelit = 30, FrenzySecond = 45, FrenzyFlip = 90;
        public const int WindTicks = 60, RushTicks = 90, Bumpers = 3;
        public static readonly string[] Names = { "", "LID FRENZY", "BUMPER RAIN", "WIND GUST", "SWEEPER RUSH" };

        public struct Drop
        {
            public uint Tick;
            public byte Kind, Arg;
        }

        /// <summary>The World's drop queue (8 entries; kind 0 = empty) and the round's RUSH tick / hazard seed.</summary>
        public static readonly Drop[] Queue = new Drop[8];
        public static uint RoundStart;
        public static ulong HazardSeed;
        public static bool Any;

        public static void Set(Func<int, (uint tick, byte kind, byte arg)> at, uint roundStart, ulong hazardSeed)
        {
            Any = false;
            for (var i = 0; i < Queue.Length; i++)
            {
                var (t, k, a) = at(i);
                Queue[i] = new Drop { Tick = t, Kind = k, Arg = a };
                Any |= k != 0;
            }

            RoundStart = roundStart;
            HazardSeed = hazardSeed;
        }

        public static void Clear()
        {
            Array.Clear(Queue, 0, Queue.Length);
            Any = false;
        }

        public static int Duration(byte k) => k switch
        {
            LidFrenzy => FrenzySecond + FrenzyFlip,
            BumperRain => 1,
            WindGust => WindTicks,
            SweeperRush => RushTicks,
            _ => 0,
        };

        // ---- Lid Frenzy (rush-core drops::frenzy_pattern / frenzy_patterns / frenzy_state)
        public static bool Pattern(int p, int r, int c) => (p & 7) switch
        {
            0 => r % 3 == 0,
            1 => c % 3 == 0,
            2 => (r + c) % 4 == 0,
            3 => (r + 2 * c) % 4 == 1,
            4 => r % 3 == 2 && c % 2 == 0,
            5 => c % 3 == 2 && r % 2 == 0,
            6 => (r * 3 + c) % 4 == 2,
            _ => (r + c) % 3 == 1 && (r % 2 == c % 2),
        };

        public static (int a, int b) Patterns(byte arg)
        {
            var a = arg & 7;
            var b = (arg >> 3) & 7;
            if (b == a)
                b = (a + 3) & 7;
            return (a, b);
        }

        /// <summary>The state a Frenzy started at hazard t30 <paramref name="start"/> forces on lid <paramref name="lid"/>
        /// at <paramref name="t30"/>, and the ticks into it (for the hinge swing); false = the schedule's own state.</summary>
        public static bool FrenzyState(in LidGridDef d, int lid, long start, byte arg, long t30, out LidState st, out long into)
        {
            st = LidState.Idle;
            into = 0;
            if (d.Inner(lid) || t30 < start || t30 >= start + FrenzySecond + FrenzyFlip)
                return false;
            var (pa, pb) = Patterns(arg);
            var r = lid / d.N;
            var c = lid % d.N;
            long s;
            if (Pattern(pa, r, c))
                s = start;
            else if (Pattern(pb, r, c))
                s = start + FrenzySecond;
            else
                return false;
            if (t30 < s || t30 >= s + FrenzyFlip)
                return false;
            long rt = d.RetireTick(lid);
            if (rt != int.MaxValue && rt - d.RetireWarnTicks <= s + FrenzyFlip)
                return false;
            if (t30 < s + FrenzyLit)
                st = LidState.Lit;
            else if (t30 < s + FrenzyLit + FrenzyOpen)
            {
                st = LidState.Flipped;
                into = t30 - (s + FrenzyLit);
            }
            else
                st = LidState.Lit;
            return true;
        }

        /// <summary>Applies every queued Frenzy over the top grid's states at hazard t30 (grid 0 only); writes the
        /// swing progress of Frenzy-opened lids into <paramref name="into"/> (−1 = not a Frenzy lid).</summary>
        public static void ApplyFrenzy(in LidGridDef d, long t30, LidState[] states, long[] into)
        {
            for (var i = 0; i < d.Count && i < into.Length; i++)
                into[i] = -1;
            if (!Any)
                return;
            foreach (var q in Queue)
            {
                if (q.Kind != LidFrenzy)
                    continue;
                var start = (long)q.Tick - RoundStart;
                for (var lid = 0; lid < d.Count && lid < states.Length; lid++)
                    if (FrenzyState(in d, lid, start, q.Arg, t30, out var st, out var inn))
                    {
                        states[lid] = st;
                        into[lid] = st == LidState.Flipped ? inn : -1;
                    }
            }
        }

        // ---- Sweeper Rush (rush-core drops::sweeper_warp60, in 30 Hz sim ticks: +0.5 tick per tick for 90 ticks)
        public static double SweeperWarpTicks(double absTick)
        {
            if (!Any)
                return 0;
            var w = 0.0;
            foreach (var q in Queue)
                if (q.Kind == SweeperRush && absTick > q.Tick)
                    w += 0.5 * Math.Min(absTick - q.Tick, RushTicks);
            return w;
        }

        // ---- Bumper Rain (rush-core drops::bumper_lids)
        private static ulong SplitMix(ref ulong s)
        {
            s += 0x9E37_79B9_7F4A_7C15UL;
            var z = s;
            z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EBUL;
            return z ^ (z >> 31);
        }

        public static void BumperLids(in LidGridDef d, byte arg, ulong seed, int[] out3)
        {
            var n = (ulong)Math.Max(1, d.Count);
            var s = seed ^ ((ulong)arg << 40) ^ 0xB0B0_0001UL;
            var k = 0;
            for (var g = 0; k < Bumpers && g < 32; g++)
            {
                var l = (int)(SplitMix(ref s) % n);
                var dup = false;
                for (var j = 0; j < k; j++)
                    dup |= out3[j] == l;
                if (!dup)
                    out3[k++] = l;
            }

            for (; k < Bumpers; k++)
                out3[k] = -1;
        }

        // ---- Wind Gust: one of 8 headings (yaw byte (arg & 7) · 32 → x = sin, z = cos)
        public static (float x, float z) WindDir(byte arg)
        {
            var a = (arg & 7) * Math.PI / 4.0;
            return ((float)Math.Sin(a), (float)Math.Cos(a));
        }
    }
}
