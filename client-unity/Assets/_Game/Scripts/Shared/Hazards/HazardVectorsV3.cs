using System;
using System.Text;

namespace FallRush.Shared
{
    /// <summary>
    /// Cross-platform determinism vectors v3 (G4.1): the 1,400 v2 cases (<see cref="HazardVectorsV2"/>, unchanged; v1 digest
    /// <c>fc06826c…</c>, v2 <c>d4b2a73a…</c>) followed by <see cref="G4Count"/> cases of the G4 pure functions: rolling-ball
    /// pose (both variants: the variant-B def reverses the lane), contact through <see cref="HazardSet.ApplyKnocks"/>,
    /// the chute telegraph, lid-grid states over full schedules (several N and seeds), ring retire geometry and the
    /// fairness floor. Same width, integers only. Committed as <c>Tests/Vectors/hazard_vectors_v3.json</c> and
    /// <see cref="HazardVectorsV3Expected"/>.
    /// </summary>
    public static class HazardVectorsV3
    {
        public const int Version = 3;
        public const int G4Count = 600;
        public const int Count = HazardVectorsV2.Count + G4Count;
        public const int Width = HazardVectors.Width;
        public const int BallPose = 13, BallContact = 14, BallTelegraph = 15, LidStates = 16, LidRing = 17, LidFair = 18;

        private static ulong SplitMix(ref ulong s)
        {
            s += 0x9E3779B97F4A7C15UL;
            var z = s;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        private static int R(ref ulong s, int lo, int hi) => lo + (int)(SplitMix(ref s) % (ulong)(hi - lo + 1));

        private static RollingBallDef Ball(ref ulong s, out int variant)
        {
            var a = new RollingBallDef
            {
                StartXMm = R(ref s, -40000, 40000), StartYMm = R(ref s, 0, 12000), StartZMm = R(ref s, -40000, 40000),
                RadiusMm = R(ref s, 600, 1200), SpeedMmS = R(ref s, 2000, 7000), PeriodTicks = R(ref s, 30, 300), TelegraphTicks = R(ref s, 9, 30),
                KnockbackCms = KnockProfile.HardPushCms, KnockUpCms = KnockProfile.HardUpCms, StunTicks = KnockProfile.HardStunTicks,
                ImmunityTicks = KnockProfile.ImmunityFor(KnockProfile.HardStunTicks),
            };
            a.EndXMm = a.StartXMm + R(ref s, -40000, 40000);
            a.EndYMm = a.StartYMm - R(ref s, 0, 6000);
            a.EndZMm = a.StartZMm + R(ref s, -40000, 40000);
            variant = R(ref s, 0, 1);
            var d = a;
            if (variant == 1)
            {
                // Variant B: the same lane rolling the other way, half the period.
                d.StartXMm = a.EndXMm;
                d.StartYMm = a.EndYMm;
                d.StartZMm = a.EndZMm;
                d.EndXMm = a.StartXMm;
                d.EndYMm = a.StartYMm;
                d.EndZMm = a.StartZMm;
                d.PeriodTicks = Math.Max(30, a.PeriodTicks / 2);
            }

            HazardResolve.SetPhase(ref d, R(ref s, 0, 65535));
            return d;
        }

        private static LidGridDef Lids(ref ulong s)
        {
            var n = new[] { 5, 7, 9, 11 }[R(ref s, 0, 3)];
            var d = LidGridDef.Default(R(ref s, -20000, 20000), R(ref s, 0, 20000), R(ref s, -20000, 20000), n);
            d.Seed = (uint)SplitMix(ref s);
            return d;
        }

        private static uint Fnv(LidState[] st, int n)
        {
            var h = 2166136261u;
            for (var i = 0; i < n; i++)
            {
                h ^= (byte)st[i];
                h *= 16777619u;
            }

            return h;
        }

        public static void Compute(int i, int[] o)
        {
            if (i < HazardVectorsV2.Count)
            {
                HazardVectorsV2.Compute(i, o);
                return;
            }

            Array.Clear(o, 0, Width);
            var k = i - HazardVectorsV2.Count;
            var s = 0xFA11_0004_0004UL + (ulong)k * 104723UL;
            var type = BallPose + k % 6;
            o[0] = type;
            switch (type)
            {
                case BallPose:
                {
                    var d = Ball(ref s, out var v);
                    var t = R(ref s, -600, 9000);
                    var j = R(ref s, 0, 3);
                    var alive = HazardMath.BallPosMm(in d, t, j, out var x, out var y, out var z, out var kk);
                    o[1] = v;
                    o[2] = d.LenMm;
                    o[3] = t;
                    o[4] = HazardMath.BallTravelTicks(in d);
                    o[5] = HazardMath.BallMaxAlive(in d) * 16 + j;
                    o[6] = alive ? 1 : 0;
                    o[7] = HazardMath.BallPhaseOffset(in d);
                    o[8] = (int)kk;
                    o[9] = x;
                    o[10] = y;
                    o[11] = z;
                    break;
                }
                case BallContact:
                {
                    var d = Ball(ref s, out var v);
                    var t = R(ref s, 0, 9000);
                    var cfg = MovementConfig.Default;
                    var st = new MotorState { VelX = (short)R(ref s, -700, 700), VelZ = (short)R(ref s, -700, 700), Flags = R(ref s, 0, 3) == 0 ? MotorState.Diving : MotorState.Grounded };
                    // Feet near the newest ball so contacts happen about half the time.
                    HazardMath.BallPosMm(in d, t, 0, out var bx, out var by, out var bz, out _);
                    st.PosX = bx + R(ref s, -2000, 2000);
                    st.PosY = by - d.RadiusMm + R(ref s, -400, 1400);
                    st.PosZ = bz + R(ref s, -2000, 2000);
                    var hz = new HazardSet(null, null) { RollingBalls = new[] { d } };
                    o[1] = v;
                    o[2] = st.PosX;
                    o[3] = t;
                    o[4] = st.PosY;
                    o[5] = st.PosZ;
                    o[6] = d.RadiusMm;
                    o[7] = st.Flags;
                    o[8] = hz.ApplyKnocks(ref st, unchecked((uint)t), cfg) ? 1 : 0;
                    o[9] = st.VelX;
                    o[10] = st.VelY * 1000 + st.StunTicks * 10 + (st.ImmunityTicks > 0 ? 1 : 0);
                    o[11] = st.VelZ;
                    break;
                }
                case BallTelegraph:
                {
                    var d = Ball(ref s, out var v);
                    var t0 = R(ref s, -300, 9000);
                    o[1] = v;
                    o[2] = d.PeriodTicks;
                    o[3] = t0;
                    o[4] = d.TelegraphTicks;
                    o[5] = HazardMath.BallPhaseOffset(in d);
                    for (var q = 0; q < 6; q++)
                        o[6 + q] = HazardMath.RollingBallTelegraph1000(in d, t0 + q * 3);
                    break;
                }
                case LidStates:
                {
                    var d = Lids(ref s);
                    var t = R(ref s, -60, 4200);
                    var st = new LidState[d.Count];
                    var scratch = new int[d.Count];
                    HazardMath.LidStates(in d, t, st, scratch);
                    var c = new int[5];
                    for (var q = 0; q < d.Count; q++)
                        c[(int)st[q]]++;
                    var lid = R(ref s, 0, d.Count - 1);
                    o[1] = d.N;
                    o[2] = (int)(d.Seed & 0x7FFFFFFF);
                    o[3] = t;
                    o[4] = (int)(Fnv(st, d.Count) & 0x7FFFFFFF);
                    o[5] = c[0];
                    o[6] = c[1];
                    o[7] = c[2];
                    o[8] = c[3] * 1000 + c[4];
                    var w = HazardMath.LidLastWave(in d, t);
                    o[9] = w;
                    o[10] = w >= 0 ? HazardMath.LidWaveStart(in d, w) : -1;
                    o[11] = lid * 8 + (int)HazardMath.LidStateAt(in d, lid, t, scratch);
                    break;
                }
                case LidRing:
                {
                    var d = Lids(ref s);
                    var lid = R(ref s, 0, d.Count - 1);
                    d.LidCenterMm(lid, out var x, out var y, out var z);
                    var fx = x + R(ref s, -1200, 1200);
                    var fz = z + R(ref s, -1200, 1200);
                    var fy = d.TopMm + R(ref s, -150, 150);
                    o[1] = d.N;
                    o[2] = lid;
                    o[3] = d.Ring(lid);
                    o[4] = d.RetireTick(lid) == int.MaxValue ? -1 : d.RetireTick(lid);
                    o[5] = d.Inner(lid) ? 1 : 0;
                    o[6] = x;
                    o[7] = fy - y; // feet above the lid centre (centre y = top − thickness / 2)
                    o[8] = z;
                    o[9] = fx;
                    o[10] = fz;
                    o[11] = d.LidUnder(fx, fy, fz);
                    break;
                }
                default:
                {
                    var d = Lids(ref s);
                    d.FlipPermille = new[] { R(ref s, 100, 500) };
                    d.FlippedTicks = R(ref s, 30, 150);
                    d.WaveIntervals = new[] { R(ref s, 30, 120), R(ref s, 30, 120) };
                    var t0 = R(ref s, 0, 3000);
                    var st = new LidState[d.Count];
                    var scratch = new int[d.Count];
                    var min = 9;
                    var flips = 0;
                    for (var t = t0; t < t0 + 120; t++)
                    {
                        HazardMath.LidStates(in d, t, st, scratch);
                        var solid = 0;
                        for (var q = 0; q < d.Count; q++)
                        {
                            if (d.Inner(q) && HazardMath.LidSolid(st[q]))
                                solid++;
                            if (st[q] == LidState.Flipped)
                                flips++;
                        }

                        min = Math.Min(min, solid);
                    }

                    var w = Math.Max(0, HazardMath.LidLastWave(in d, t0));
                    o[1] = d.N;
                    o[2] = d.FlippedTicks;
                    o[3] = t0;
                    o[4] = d.FlipPermille[0];
                    o[5] = d.WaveIntervals[0] * 1000 + d.WaveIntervals[1];
                    o[6] = HazardMath.LidMaxOverlap(in d);
                    o[7] = HazardMath.LidInnerCap(in d);
                    o[8] = min;
                    o[9] = flips;
                    o[10] = HazardMath.LidWaveSet(in d, w, scratch);
                    o[11] = (int)(d.Seed & 0x7FFFFFFF);
                    break;
                }
            }
        }

        public static int[] ComputeAll()
        {
            var all = new int[Count * Width];
            var o = new int[Width];
            for (var i = 0; i < Count; i++)
            {
                Compute(i, o);
                Array.Copy(o, 0, all, i * Width, Width);
            }

            return all;
        }

        public static string Digest(int[] all)
        {
            var w = new CanonicalWriter();
            w.I32(Version).I32(Count).I32(Width);
            foreach (var v in all)
                w.I32(v);
            return Sha256.Hex(w.Sha256());
        }

        public static bool SelfTest(int[] expected, string expectedDigest, string platform, out string line)
        {
            var all = ComputeAll();
            var digest = Digest(all);
            var mismatches = 0;
            var sb = new StringBuilder();
            for (var i = 0; i < Count; i++)
                for (var f = 0; f < Width; f++)
                {
                    var a = all[i * Width + f];
                    var e = expected != null && expected.Length == Count * Width ? expected[i * Width + f] : int.MinValue;
                    if (a == e)
                        continue;
                    mismatches++;
                    if (mismatches <= 8)
                        sb.Append($" case {i} (type {all[i * Width]}) field {f}: got {a} expected {e};");
                }

            var ok = mismatches == 0;
            line = $"[selftest] {(ok ? "PASS" : "FAIL")} platform={platform} hazard_vectors_v{Version} cases={Count} fields={Count * Width} mismatches={mismatches} digest={digest} expected={expectedDigest}{(ok ? "" : " first:" + sb)}";
            return ok;
        }
    }
}
