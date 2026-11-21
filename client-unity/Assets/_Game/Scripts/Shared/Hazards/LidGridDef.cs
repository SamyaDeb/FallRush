using System;

namespace FallRush.Shared
{
    /// <summary>The state of one lid at a tick (G4.1b). Solid = <see cref="Idle"/>, <see cref="Lit"/>, <see cref="RetireWarn"/>.</summary>
    public enum LidState : byte
    {
        Idle = 0,
        Lit = 1,        // about to flip (telegraph, still solid)
        Flipped = 2,    // turned over: no collider
        Retired = 3,    // its ring is gone for good: no collider
        RetireWarn = 4, // its ring retires soon (blinking, still solid)
    }

    /// <summary>
    /// <c>LID_FLIP</c> (G4.1b, the Final's floor): an N × N grid of lids (N odd). Waves light a seeded share of the alive
    /// lids (<see cref="LitTicks"/>), then flip them over (<see cref="FlippedTicks"/>, no collider), then back. The wave
    /// interval shrinks stage by stage. <b>Ring retire:</b> the outer rings are removed for good at
    /// <see cref="RingRetireTicks"/> (outermost first), blinking for <see cref="RetireWarnTicks"/> before; the inner 3 × 3
    /// never retires. Every lid's state is a pure function of <c>(def, per-obstacle seed, lid, t)</c>, integers only; no
    /// replication. In M6 the per-obstacle seed is derived from the MagicBlock VRF seed (no change here).
    /// </summary>
    [Serializable]
    public struct LidGridDef
    {
        public int OriginXMm, OriginZMm;   // grid centre (the centre lid's centre)
        public int TopMm;                  // walkable top
        public int N;                      // lids per side, odd, 5–11
        public int LidMm, GapMm, ThicknessMm;
        public int LitTicks, FlippedTicks;
        public int FirstWaveTicks;         // hazard time of wave 0
        public int[] WaveIntervals;        // ticks between wave starts, per stage (e.g. 90, 75, 60, 45); the last repeats
        public int WavesPerStage;
        public int[] FlipPermille;         // per stage: share of the alive lids lit per wave (e.g. 200, 250, 300, 350)
        public int[] RingRetireTicks;      // ring m, m−1, … retire at these hazard times (increasing)
        public int RetireWarnTicks;
        public int MinInnerSolid;          // ≥ this many of the inner 3 × 3 are solid at every tick

        /// <summary>Per-obstacle seed (<c>ObstaclePhase.Hash(hazardSeed, obstacleId)</c>), set at resolve; not authored, not hashed.</summary>
        [NonSerialized] public uint Seed;

        public int Count => N * N;
        public int Mid => (N - 1) / 2;
        public int PitchMm => LidMm + GapMm;

        /// <summary>The plan page's Final: 9 × 9 lids of 1.8 m, waves 90 → 45 ticks, 20 → 35 %, a ring every 25 s.</summary>
        public static LidGridDef Default(int ox, int top, int oz, int n = 9) => new LidGridDef
        {
            OriginXMm = ox, OriginZMm = oz, TopMm = top, N = n, LidMm = 1800, GapMm = 60, ThicknessMm = 400,
            LitTicks = 30, FlippedTicks = 45, FirstWaveTicks = 90,
            WaveIntervals = new[] { 90, 75, 60, 45 }, WavesPerStage = 8, FlipPermille = new[] { 200, 250, 300, 350 },
            RingRetireTicks = n >= 9 ? new[] { 750, 1500, 2250 } : n == 7 ? new[] { 750, 1500 } : new[] { 750 },
            RetireWarnTicks = 90, MinInnerSolid = 5,
        };

        public int Ring(int lid)
        {
            var c = lid % N - Mid;
            var r = lid / N - Mid;
            return Math.Max(Math.Abs(c), Math.Abs(r));
        }

        public bool Inner(int lid) => Ring(lid) <= 1;

        /// <summary>Hazard time the lid's ring retires (<see cref="int.MaxValue"/> = never).</summary>
        public int RetireTick(int lid)
        {
            var k = Mid - Ring(lid);
            return RingRetireTicks != null && k >= 0 && k < RingRetireTicks.Length && Ring(lid) >= 2 ? RingRetireTicks[k] : int.MaxValue;
        }

        public void LidCenterMm(int lid, out int x, out int y, out int z)
        {
            x = OriginXMm + (lid % N - Mid) * PitchMm;
            z = OriginZMm + (lid / N - Mid) * PitchMm;
            y = TopMm - ThicknessMm / 2;
        }

        /// <summary>The lid under feet (x, z mm) with feet within ±10 cm of the top, or −1.</summary>
        public int LidUnder(int fx, int fy, int fz)
        {
            if (Math.Abs(fy - TopMm) > 100)
                return -1;
            var half = N * PitchMm / 2;
            var u = fx - (OriginXMm - half);
            var v = fz - (OriginZMm - half);
            if (u < 0 || v < 0)
                return -1;
            int c = u / PitchMm, r = v / PitchMm;
            return c < N && r < N ? r * N + c : -1;
        }
    }

    public static partial class HazardMath
    {
        // ------------------------------------------------------------------ lid flip: wave schedule (closed form)

        private static int Interval(in LidGridDef d, int stage) => d.WaveIntervals == null || d.WaveIntervals.Length == 0 ? 60 : Math.Max(1, d.WaveIntervals[Math.Min(stage, d.WaveIntervals.Length - 1)]);

        private static int Stages(in LidGridDef d) => d.WaveIntervals == null || d.WaveIntervals.Length == 0 ? 1 : d.WaveIntervals.Length;

        public static int LidStage(in LidGridDef d, int w) => Math.Min(w / Math.Max(1, d.WavesPerStage), Stages(in d) - 1);

        /// <summary>Start tick of wave <paramref name="w"/> (≥ 0): a sum over at most <c>stages</c> terms, no loop over past waves.</summary>
        public static int LidWaveStart(in LidGridDef d, int w)
        {
            var per = Math.Max(1, d.WavesPerStage);
            var stage = LidStage(in d, w);
            long t = d.FirstWaveTicks;
            for (var s = 0; s < stage; s++)
                t += (long)per * Interval(in d, s);
            t += (long)(w - stage * per) * Interval(in d, stage);
            return (int)Math.Min(int.MaxValue, t);
        }

        /// <summary>The last wave started at or before <paramref name="t"/> (−1 before wave 0).</summary>
        public static int LidLastWave(in LidGridDef d, int t)
        {
            if (t < d.FirstWaveTicks)
                return -1;
            var per = Math.Max(1, d.WavesPerStage);
            long b = d.FirstWaveTicks;
            var n = Stages(in d);
            for (var s = 0; s < n; s++)
            {
                var iv = Interval(in d, s);
                var span = (long)per * iv;
                if (s == n - 1 || t < b + span)
                    return (int)(s * per + (t - b) / iv);
                b += span;
            }

            return -1;
        }

        /// <summary>Most waves whose flipped windows can overlap at one tick: ⌈Flipped / min interval⌉.</summary>
        public static int LidMaxOverlap(in LidGridDef d)
        {
            var min = int.MaxValue;
            for (var s = 0; s < Stages(in d); s++)
                min = Math.Min(min, Interval(in d, s));
            return Math.Max(1, (d.FlippedTicks + min - 1) / min);
        }

        /// <summary>Inner lids one wave may flip: ⌊(9 − MinInnerSolid) / max overlap⌋ (the fairness cap).</summary>
        public static int LidInnerCap(in LidGridDef d) => Math.Max(0, (9 - d.MinInnerSolid) / LidMaxOverlap(in d));

        private static ulong SplitMix64(ref ulong s)
        {
            s += 0x9E3779B97F4A7C15UL;
            var z = s;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>
        /// The lids wave <paramref name="w"/> lights, written to <paramref name="scratch"/>[0, count) (scratch ≥ N²). Alive =
        /// lids that stay un-warned and un-retired for the whole wave window; k = ⌊alive · permille / 1000⌋; the set = the
        /// first k of a seeded partial Fisher–Yates permutation (SplitMix64 of <c>seed ⊕ w</c>) of the alive lids in index
        /// order; inner lids beyond <see cref="LidInnerCap"/> are dropped in permutation order (fairness).
        /// </summary>
        public static int LidWaveSet(in LidGridDef d, int w, int[] scratch)
        {
            if (w < 0)
                return 0;
            var start = LidWaveStart(in d, w);
            long end = (long)start + d.LitTicks + d.FlippedTicks;
            var a = 0;
            for (var lid = 0; lid < d.Count; lid++)
                if ((long)d.RetireTick(lid) - d.RetireWarnTicks >= end)
                    scratch[a++] = lid;
            var st = LidStage(in d, w);
            var pm = d.FlipPermille == null || d.FlipPermille.Length == 0 ? 0 : d.FlipPermille[Math.Min(st, d.FlipPermille.Length - 1)];
            var k = (int)((long)a * pm / 1000);
            var rng = ((ulong)d.Seed << 32 | d.Seed) ^ ((ulong)(uint)w * 0xD1B54A32D192ED03UL);
            var cap = LidInnerCap(in d);
            var inner = 0;
            var kept = 0;
            for (var j = 0; j < k; j++)
            {
                var r = j + (int)(SplitMix64(ref rng) % (ulong)(a - j));
                var c = scratch[r];
                scratch[r] = scratch[j];
                scratch[j] = c;
                if (d.Inner(c))
                {
                    if (inner >= cap)
                        continue; // dropped: fairness
                    inner++;
                }

                scratch[kept++] = c; // kept ≤ j: overwrites only processed positions
            }

            return kept;
        }

        // ------------------------------------------------------------------ lid flip: state

        private static LidState RingState(in LidGridDef d, int lid, int t)
        {
            var rt = d.RetireTick(lid);
            if (t >= rt)
                return LidState.Retired;
            if ((long)t >= (long)rt - d.RetireWarnTicks)
                return LidState.RetireWarn;
            return LidState.Idle;
        }

        /// <summary>
        /// One lid's state at hazard time <paramref name="t"/>: Retired / RetireWarn from its ring; else Flipped or Lit if a
        /// wave whose window covers t lit it (Flipped wins); else Idle. Checks only the waves that can cover t. O(waves · N²)
        /// for one lid (tests, vectors); the per-tick path is <see cref="LidStates"/>.
        /// </summary>
        public static LidState LidStateAt(in LidGridDef d, int lid, int t, int[] scratch)
        {
            var rs = RingState(in d, lid, t);
            if (rs != LidState.Idle)
                return rs;
            var best = LidState.Idle;
            var window = d.LitTicks + d.FlippedTicks;
            for (var w = LidLastWave(in d, t); w >= 0; w--)
            {
                var start = LidWaveStart(in d, w);
                if ((long)start + window <= t)
                    break;
                var n = LidWaveSet(in d, w, scratch);
                for (var i = 0; i < n; i++)
                {
                    if (scratch[i] != lid)
                        continue;
                    var s = t < start + d.LitTicks ? LidState.Lit : LidState.Flipped;
                    if (s == LidState.Flipped)
                        return s;
                    best = s;
                }
            }

            return best;
        }

        /// <summary>Every lid's state at <paramref name="t"/> into <paramref name="states"/> (N²); each covering wave is drawn once. No allocation.</summary>
        public static void LidStates(in LidGridDef d, int t, LidState[] states, int[] scratch)
        {
            for (var lid = 0; lid < d.Count; lid++)
                states[lid] = RingState(in d, lid, t);
            var window = d.LitTicks + d.FlippedTicks;
            for (var w = LidLastWave(in d, t); w >= 0; w--)
            {
                var start = LidWaveStart(in d, w);
                if ((long)start + window <= t)
                    break;
                var s = t < start + d.LitTicks ? LidState.Lit : LidState.Flipped;
                var n = LidWaveSet(in d, w, scratch);
                for (var i = 0; i < n; i++)
                {
                    var lid = scratch[i];
                    var cur = states[lid];
                    if (cur == LidState.Idle || (cur == LidState.Lit && s == LidState.Flipped))
                        states[lid] = s;
                }
            }
        }

        /// <summary>Solid lids have a collider: Idle, Lit, RetireWarn.</summary>
        public static bool LidSolid(LidState s) => s == LidState.Idle || s == LidState.Lit || s == LidState.RetireWarn;

        /// <summary>Ticks into the current Flipped window (for the 6-tick flip animation), or −1 when the lid is not flipped. Visual helper.</summary>
        public static int LidFlippedFor(in LidGridDef d, int lid, int t, int[] scratch)
        {
            var window = d.LitTicks + d.FlippedTicks;
            for (var w = LidLastWave(in d, t); w >= 0; w--)
            {
                var start = LidWaveStart(in d, w);
                if ((long)start + window <= t)
                    break;
                if (t < start + d.LitTicks)
                    continue;
                var n = LidWaveSet(in d, w, scratch);
                for (var i = 0; i < n; i++)
                    if (scratch[i] == lid)
                        return t - start - d.LitTicks;
            }

            return -1;
        }
    }
}
