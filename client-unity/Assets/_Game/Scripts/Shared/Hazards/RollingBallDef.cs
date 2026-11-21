using System;

namespace FallRush.Shared
{
    /// <summary>
    /// <c>ROLLING_BALL</c> (G4.1a): a lane of balls rolling at a constant speed from <c>Start</c> to <c>End</c> (ball centres,
    /// mm). Ball k is released at <c>t_k = k·PeriodTicks − phaseOffset</c> and despawns at the end; several balls of a lane
    /// can be alive at once. A knock volume (<c>knock_hard</c> along the roll direction), never a surface: no collider.
    /// The chute light telegraphs each release for <see cref="TelegraphTicks"/>. Pose = f(def, variant, seed, t), integers only.
    /// </summary>
    [Serializable]
    public struct RollingBallDef
    {
        public int StartXMm, StartYMm, StartZMm;    // ball centre at release
        public int EndXMm, EndYMm, EndZMm;          // ball centre where it despawns
        public int RadiusMm;                        // 600–1200
        public int SpeedMmS;                        // 2 000–7 000
        public int PeriodTicks;                     // release interval, 30–300
        public int Phase16;                         // seeded offset, fraction of the period (0..65535)
        public int TelegraphTicks;                  // chute light before each release (default 15)
        public int KnockbackCms, KnockUpCms, StunTicks, ImmunityTicks;

        /// <summary>Path length (mm, integer sqrt), derived at load by <see cref="HazardMath.PrepareBall"/>; not authored, not hashed.</summary>
        [NonSerialized] public int LenMm;

        /// <summary>Defaults: r 1.0 m, 4 m/s, a ball every 2 s, 0.5 s telegraph, the mallet's <c>knock_hard</c>.</summary>
        public static RollingBallDef Default(int sx, int sy, int sz, int ex, int ey, int ez) => new RollingBallDef
        {
            StartXMm = sx, StartYMm = sy, StartZMm = sz, EndXMm = ex, EndYMm = ey, EndZMm = ez,
            RadiusMm = 1000, SpeedMmS = 4000, PeriodTicks = 60, TelegraphTicks = 15,
            KnockbackCms = KnockProfile.HardPushCms, KnockUpCms = KnockProfile.HardUpCms,
            StunTicks = KnockProfile.HardStunTicks, ImmunityTicks = KnockProfile.ImmunityFor(KnockProfile.HardStunTicks),
        };
    }

    public static partial class HazardMath
    {
        // ------------------------------------------------------------------ integer helpers

        /// <summary>floor(√v) for v ≥ 0, integers only (bit-by-bit; identical on every platform).</summary>
        public static long IntSqrt(long v)
        {
            if (v <= 0)
                return 0;
            var x = (ulong)v;
            ulong r = 0, bit = 1UL << 62;
            while (bit > x)
                bit >>= 2;
            while (bit != 0)
            {
                if (x >= r + bit)
                {
                    x -= r + bit;
                    r = (r >> 1) + bit;
                }
                else
                {
                    r >>= 1;
                }

                bit >>= 2;
            }

            return (long)r;
        }

        /// <summary>floor(a / b) for b &gt; 0.</summary>
        public static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        // ------------------------------------------------------------------ rolling ball

        /// <summary>Fills <see cref="RollingBallDef.LenMm"/> (call once when the def is resolved; both sides do the same).</summary>
        public static void PrepareBall(ref RollingBallDef d) => d.LenMm = BallLenMm(in d);

        public static int BallLenMm(in RollingBallDef d)
        {
            if (d.LenMm > 0)
                return d.LenMm;
            long dx = d.EndXMm - d.StartXMm, dy = d.EndYMm - d.StartYMm, dz = d.EndZMm - d.StartZMm;
            return (int)Math.Max(1, IntSqrt(dx * dx + dy * dy + dz * dz));
        }

        /// <summary>Ticks from release to despawn: ⌈len·30 / speed⌉.</summary>
        public static int BallTravelTicks(in RollingBallDef d)
        {
            var sp = Math.Max(1, d.SpeedMmS);
            return (int)(((long)BallLenMm(in d) * TickRate + sp - 1) / sp);
        }

        /// <summary>The release offset in ticks: the seeded <see cref="RollingBallDef.Phase16"/> as a fraction of the period.</summary>
        public static int BallPhaseOffset(in RollingBallDef d) => (int)((long)(d.Phase16 & 0xFFFF) * Math.Max(1, d.PeriodTicks) >> 16);

        /// <summary>Most balls a lane can have alive at one tick: ⌈travel / period⌉.</summary>
        public static int BallMaxAlive(in RollingBallDef d)
        {
            var p = Math.Max(1, d.PeriodTicks);
            return (BallTravelTicks(in d) + p - 1) / p;
        }

        /// <summary>
        /// The <paramref name="j"/>-th newest ball alive at <paramref name="t"/> (0 = the newest). False when there is no such
        /// ball. <paramref name="k"/> is the ball's release index (it may be negative: lanes run before the round starts).
        /// Position = integer lerp start → end by <c>(t − t_k)·speed / (30·len)</c>, rounded to mm.
        /// </summary>
        public static bool BallPosMm(in RollingBallDef d, int t, int j, out int x, out int y, out int z, out long k)
        {
            var p = Math.Max(1, d.PeriodTicks);
            var off = BallPhaseOffset(in d);
            k = FloorDiv((long)t + off, p) - j;
            var tk = k * p - off;
            long age = t - tk;
            var travel = BallTravelTicks(in d);
            if (j < 0 || age < 0 || age >= travel)
            {
                x = y = z = 0;
                return false;
            }

            long len = BallLenMm(in d);
            long num = age * Math.Max(1, d.SpeedMmS);
            long den = TickRate * len;
            if (num > den)
                num = den;
            x = d.StartXMm + (int)RoundDiv((long)(d.EndXMm - d.StartXMm) * num, den);
            y = d.StartYMm + (int)RoundDiv((long)(d.EndYMm - d.StartYMm) * num, den);
            z = d.StartZMm + (int)RoundDiv((long)(d.EndZMm - d.StartZMm) * num, den);
            return true;
        }

        /// <summary>
        /// Upright capsule (feet mm, radius r, height h) against every ball of the lane at <paramref name="t"/>, newest first;
        /// the first contact wins. Push (unit, XZ): the roll direction plus half the normalised lateral offset of the player
        /// from the ball's line, so an off-centre hit also shoves you sideways (into the crowd, doc 08 rule 4.4). Floats only
        /// for that direction, as for the other knock hazards.
        /// </summary>
        public static bool RollingBallContactMm(in RollingBallDef d, int t, int fx, int fy, int fz, int r, int h, out float pushX, out float pushZ)
        {
            pushX = pushZ = 0f;
            var n = BallMaxAlive(in d);
            long reach = r + d.RadiusMm;
            for (var j = 0; j < n; j++)
            {
                if (!BallPosMm(in d, t, j, out var bx, out var by, out var bz, out _))
                    break;
                var y0 = fy + r;
                var y1 = fy + h - r;
                long dv = by < y0 ? y0 - by : by > y1 ? by - y1 : 0;
                if (dv >= reach)
                    continue;
                long dx = fx - bx, dz = fz - bz;
                if (dx * dx + dz * dz + dv * dv >= reach * reach)
                    continue;
                BallPush(in d, dx, dz, reach, out pushX, out pushZ);
                return true;
            }

            return false;
        }

        private static void BallPush(in RollingBallDef d, long dx, long dz, long reach, out float px, out float pz)
        {
            float ux = d.EndXMm - d.StartXMm, uz = d.EndZMm - d.StartZMm;
            var ul = (float)Math.Sqrt(ux * ux + uz * uz);
            if (ul < 1f)
            {
                // A vertical drop: push away from the ball.
                var ol = (float)Math.Sqrt((float)dx * dx + (float)dz * dz);
                if (ol < 1f)
                {
                    px = 1f;
                    pz = 0f;
                    return;
                }

                px = dx / ol;
                pz = dz / ol;
                return;
            }

            ux /= ul;
            uz /= ul;
            var along = dx * ux + dz * uz;
            var lx = dx - along * ux;
            var lz = dz - along * uz;
            var k = 0.5f / reach;
            var vx = ux + lx * k;
            var vz = uz + lz * k;
            var vl = (float)Math.Sqrt(vx * vx + vz * vz);
            px = vx / vl;
            pz = vz / vl;
        }

        public static bool RollingBallContact(in RollingBallDef d, int t, Vec3 feet, float r, float h, out float pushX, out float pushZ) =>
            RollingBallContactMm(in d, t, MotorState.QuantizeMm(feet.X), MotorState.QuantizeMm(feet.Y), MotorState.QuantizeMm(feet.Z),
                MotorState.QuantizeMm(r), MotorState.QuantizeMm(h), out pushX, out pushZ);

        /// <summary>Chute light, 0..1000: rises over <see cref="RollingBallDef.TelegraphTicks"/> and reaches 1000 on the tick before each release. Integer.</summary>
        public static int RollingBallTelegraph1000(in RollingBallDef d, int t)
        {
            var p = Math.Max(1, d.PeriodTicks);
            if (d.TelegraphTicks <= 0)
                return 0;
            var until = p - Mod(t + BallPhaseOffset(in d), p); // 1..p ticks until the next release
            return until > d.TelegraphTicks ? 0 : 1000 * (d.TelegraphTicks - until + 1) / d.TelegraphTicks;
        }

        /// <summary>Chute light 0..1 for rendering (fractional ticks); same curve as <see cref="RollingBallTelegraph1000"/>.</summary>
        public static float RollingBallTelegraph(in RollingBallDef d, double t)
        {
            var p = Math.Max(1, d.PeriodTicks);
            if (d.TelegraphTicks <= 0)
                return 0f;
            var m = (t + BallPhaseOffset(in d)) % p;
            if (m < 0)
                m += p;
            var until = p - m;
            var w = (d.TelegraphTicks + 1 - until) / d.TelegraphTicks;
            return (float)(w < 0 ? 0 : w > 1 ? 1 : w);
        }

        /// <summary>
        /// Render pose of the <paramref name="j"/>-th newest ball at fractional hazard time <paramref name="t"/> (metres) and the
        /// distance it has rolled (m, for the spin). Visual only; whole ticks agree with <see cref="BallPosMm"/> to ≤ 1 mm.
        /// </summary>
        public static bool BallRenderPos(in RollingBallDef d, double t, int j, out double x, out double y, out double z, out double rolledM)
        {
            var p = Math.Max(1, d.PeriodTicks);
            var off = BallPhaseOffset(in d);
            var k = Math.Floor((t + off) / p) - j;
            var age = t - (k * p - off);
            var len = (double)BallLenMm(in d);
            var dist = age * d.SpeedMmS / TickRate;
            x = y = z = rolledM = 0;
            if (j < 0 || age < 0 || dist >= len)
                return false;
            var f = dist / len;
            x = (d.StartXMm + (d.EndXMm - d.StartXMm) * f) * 0.001;
            y = (d.StartYMm + (d.EndYMm - d.StartYMm) * f) * 0.001;
            z = (d.StartZMm + (d.EndZMm - d.StartZMm) * f) * 0.001;
            rolledM = dist * 0.001;
            return true;
        }
    }
}
