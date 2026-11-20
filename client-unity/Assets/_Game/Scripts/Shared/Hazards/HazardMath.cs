using System;

namespace FallRush.Shared
{
    /// <summary>Obstacle kit type ids (doc 08 §2). The byte value is part of the map content hash.</summary>
    public enum HazardType : byte
    {
        SpinnerBar = 1,
        MovingPlatform = 2,
        Conveyor = 3,
        SpringPad = 4,
        TurnTable = 5,
        GateDoor = 6,
        MalletSwing = 7,
        Bumper = 8,
    }

    /// <summary>
    /// Collision profiles (doc 08 §2, as built in M3). Knockback is a velocity <b>override</b>, never an accumulated force.
    /// After a stun ends the player is immune for 0.5 s, so two adjacent hazards can never stun-lock them.
    /// </summary>
    public static class KnockProfile
    {
        /// <summary>0.35 s at 30 Hz = 10.5 → 11 ticks (round half up, like every other ms → tick conversion).</summary>
        public const int SoftStunTicks = 11;
        public const int SoftPushCms = 600;
        /// <summary>Lift so the knocked player leaves the ground (otherwise ground friction eats 6 m/s in 4 ticks).</summary>
        public const int SoftUpCms = 350;
        /// <summary>0.6 s = 18 ticks.</summary>
        public const int HardStunTicks = 18;
        public const int HardPushCms = 900;
        public const int HardUpCms = 500;
        /// <summary>0.5 s of immunity after the stun ends (doc 08 §2).</summary>
        public const int ImmunityAfterStunTicks = 15;

        public static int ImmunityFor(int stunTicks) => stunTicks + ImmunityAfterStunTicks;
    }

    /// <summary>
    /// A bar rotating about a vertical axis (it spans both sides of the pivot): the M2 sweeper and the M3 <c>SPINNER_BAR</c>.
    /// Integer data (mm, cm/s, ticks) so both sides start from bit-identical parameters.
    /// </summary>
    [Serializable]
    public struct SweeperDef
    {
        public int PivotXMm, PivotYMm, PivotZMm; // ground point under the axis
        public int ArmMm;                        // axis → tip; the bar spans 2 × arm through the axis
        public int BarRadiusMm;
        public int BarHeightMm;                  // bar axis height above the pivot point
        public int PeriodTicks;                  // ticks per revolution; negative turns the other way (= direction)
        public int Phase16;                      // start angle, 0..65535 = one turn (derive from a seed)
        public int KnockbackCms;                 // horizontal knockback speed
        public int KnockUpCms;
        public int StunTicks;
        public int ImmunityTicks;
        /// <summary>1 = push along the bar's direction of travel (SPINNER_BAR, doc 08 knock_soft "tangential"); 0 = away from the bar (M2 sweeper).</summary>
        public int Tangential;
    }

    /// <summary>
    /// A box moving on a looping path. Without knots: the M2 sine, <c>centre(t) = Centre + Travel · sin(2π·t/Period + φ)</c>.
    /// With knots (M3 <c>MOVING_PLATFORM</c>): a closed uniform Catmull-Rom loop through the knots (mm offsets from the
    /// centre), sampled by phase. Two knots give a smooth ease-in/ease-out bob between them.
    /// </summary>
    [Serializable]
    public struct PlatformDef
    {
        public int CenterXMm, CenterYMm, CenterZMm;
        public int HalfXMm, HalfYMm, HalfZMm;
        public int TravelXMm, TravelYMm, TravelZMm; // sine amplitude vector (used when KnotsMm is empty)
        public int PeriodTicks;
        public int Phase16;
        /// <summary>Closed loop knots as x,y,z triplets in mm relative to the centre (2–8 knots), or null for the sine path.</summary>
        public int[] KnotsMm;
    }

    /// <summary>
    /// Deterministic hazard poses and effects (doc 08 §1, doc 04 §4.3 "LocalWorld(tick)"). Every pose is
    /// <c>f(def, variant, hazardSeed, t)</c> with <c>t = serverTick − roundStartTick</c> (round-relative hazard time,
    /// <see cref="HazardSet.Time"/>), computed with integer phase and float polynomials only: no <c>Math.Sin</c>, no time,
    /// no randomness, no state between ticks. The server and the predicting client evaluate the same pose for the same tick.
    /// <para>Each kit type has a def struct and its pose / contact functions in <c>Shared/Hazards/</c>; the effects are
    /// applied by <see cref="HazardSet"/> inside <see cref="PlayerStep.Run"/>.</para>
    /// </summary>
    public static partial class HazardMath
    {
        public const int PhaseOne = 65536;

        /// <summary>Start phase (0..65535) from a hazard seed; a fixed integer hash (murmur3 finaliser style).</summary>
        public static int PhaseFromSeed(uint seed)
        {
            var x = seed * 0x9E3779B1u;
            x ^= x >> 15;
            x *= 0x85EBCA77u;
            x ^= x >> 13;
            return (int)(x & 0xFFFF);
        }

        /// <summary>Phase (0..65535) at absolute tick <paramref name="tick"/> (M2 form; equal to <see cref="PhaseAt"/> for t ≥ 0).</summary>
        public static int Phase(uint tick, int periodTicks, int phase16)
        {
            if (periodTicks == 0)
                return phase16 & 0xFFFF;
            var p = (ulong)Math.Abs((long)periodTicks);
            var within = (long)((ulong)tick % p * PhaseOne / p);
            if (periodTicks < 0)
                within = -within;
            return (int)((within + phase16) & 0xFFFF);
        }

        /// <summary>Phase (0..65535) at signed hazard time <paramref name="t"/> (ticks since the round start; negative before it).</summary>
        public static int PhaseAt(int t, int periodTicks, int phase16)
        {
            if (periodTicks == 0)
                return phase16 & 0xFFFF;
            var p = Math.Abs((long)periodTicks);
            var m = t % p;
            if (m < 0)
                m += p;
            var within = m * PhaseOne / p;
            if (periodTicks < 0)
                within = -within;
            return (int)((within + phase16) & 0xFFFF);
        }

        /// <summary>Non-negative modulo.</summary>
        public static int Mod(int a, int m)
        {
            var r = a % m;
            return r < 0 ? r + m : r;
        }

        public const int Q30 = 1 << 30;

        /// <summary>
        /// sin(2π · phase / 65536) in Q30 (2^30 = 1.0), integers only: a literal quarter-wave table (1025 entries) with
        /// linear interpolation (|error| &lt; 3e-7). Every hazard pose is built on this, so poses are bit-identical on
        /// every platform (no libm, no float contraction).
        /// </summary>
        public static int SinQ30(int phase)
        {
            phase &= 0xFFFF;
            var quadrant = phase >> 14;
            var within = phase & 0x3FFF;
            if ((quadrant & 1) == 1)
                within = 0x4000 - within;
            var i = within >> 4;
            var f = within & 15;
            var v = f == 0 ? SinQ30Quarter[i] : SinQ30Quarter[i] + (int)(((long)(SinQ30Quarter[i + 1] - SinQ30Quarter[i]) * f) >> 4);
            return quadrant >= 2 ? -v : v;
        }

        public static int CosQ30(int phase) => SinQ30(phase + 0x4000);

        /// <summary>sin(2π · phase / 65536) as a float (exact scaling of <see cref="SinQ30"/>).</summary>
        public static float Sin16(int phase) => SinQ30(phase) * (1f / Q30);

        public static float Cos16(int phase) => Sin16(phase + 0x4000);

        /// <summary>round(v · q / 2^30), half away from zero, integers only.</summary>
        public static int MulQ30(long v, int q) => (int)RoundShift(v * q, 30);

        /// <summary>round(a / 2^n), half away from zero.</summary>
        public static long RoundShift(long a, int n)
        {
            var half = 1L << (n - 1);
            return a >= 0 ? (a + half) >> n : -((-a + half) >> n);
        }

        /// <summary>Round half away from zero to an integer (mm / cm/s quantisation).</summary>
        public static int RoundMm(float v) => v >= 0f ? (int)(v + 0.5f) : -(int)(-v + 0.5f);

        /// <summary>round(a / b) half away from zero, integers only (b &gt; 0).</summary>
        public static long RoundDiv(long a, long b) => a >= 0 ? (a * 2 + b) / (2 * b) : -((-a * 2 + b) / (2 * b));

        public static Vec3 MmToVec(int x, int y, int z) => new Vec3(x * 0.001f, y * 0.001f, z * 0.001f);

        // ------------------------------------------------------------------ moving platform

        /// <summary>Platform centre at hazard time <paramref name="t"/>, quantised to whole millimetres.</summary>
        public static void PlatformCenterMm(in PlatformDef d, int t, out int x, out int y, out int z)
        {
            var phase = PhaseAt(t, d.PeriodTicks, d.Phase16);
            var k = d.KnotsMm;
            if (k == null || k.Length < 6)
            {
                var s = SinQ30(phase);
                x = d.CenterXMm + MulQ30(d.TravelXMm, s);
                y = d.CenterYMm + MulQ30(d.TravelYMm, s);
                z = d.CenterZMm + MulQ30(d.TravelZMm, s);
                return;
            }

            var n = k.Length / 3;
            var u = phase * n;              // 16.16: segment index and fraction
            var seg = u >> 16;
            var f = u & 0xFFFF;             // Q16
            var i0 = (seg + n - 1) % n * 3;
            var i1 = seg * 3;
            var i2 = (seg + 1) % n * 3;
            var i3 = (seg + 2) % n * 3;
            x = d.CenterXMm + CatmullRom(k[i0], k[i1], k[i2], k[i3], f);
            y = d.CenterYMm + CatmullRom(k[i0 + 1], k[i1 + 1], k[i2 + 1], k[i3 + 1], f);
            z = d.CenterZMm + CatmullRom(k[i0 + 2], k[i1 + 2], k[i2 + 2], k[i3 + 2], f);
        }

        /// <summary>Uniform Catmull-Rom between p1 and p2 at f (Q16, [0, 1)): integer Horner chain in Q16, result in mm.</summary>
        private static int CatmullRom(int p0, int p1, int p2, int p3, int f)
        {
            long a = 2 * p1;
            long b = p2 - p0;
            long c = 2 * p0 - 5 * p1 + 4 * p2 - p3;
            long e = -p0 + 3 * p1 - 3 * p2 + p3;
            var inner = (c << 16) + f * e;            // Q16
            var mid = (b << 16) + RoundShift(f * inner, 16);
            var v = (a << 16) + RoundShift(f * mid, 16);
            return (int)RoundShift(v, 17);            // × 0.5, Q16 → mm
        }

        public static void PlatformCenterMm(in PlatformDef d, uint tick, out int x, out int y, out int z) =>
            PlatformCenterMm(in d, unchecked((int)tick), out x, out y, out z);

        public static Vec3 PlatformCenter(in PlatformDef d, int t)
        {
            PlatformCenterMm(in d, t, out var x, out var y, out var z);
            return MmToVec(x, y, z);
        }

        public static Vec3 PlatformCenter(in PlatformDef d, uint tick) => PlatformCenter(in d, unchecked((int)tick));

        /// <summary>How far the platform moved from t − 1 to <paramref name="t"/> (what it carries a rider by), in exact mm.</summary>
        public static Vec3 PlatformDelta(in PlatformDef d, int t)
        {
            PlatformCenterMm(in d, t, out var x1, out var y1, out var z1);
            PlatformCenterMm(in d, t - 1, out var x0, out var y0, out var z0);
            return MmToVec(x1 - x0, y1 - y0, z1 - z0);
        }

        public static Vec3 PlatformDelta(in PlatformDef d, uint tick) => PlatformDelta(in d, unchecked((int)tick));

        // ------------------------------------------------------------------ sweeper / spinner bar

        /// <summary>Unit direction of the bar (XZ) at hazard time <paramref name="t"/>.</summary>
        public static void SweeperDir(in SweeperDef d, int t, out float dx, out float dz)
        {
            var p = PhaseAt(t, d.PeriodTicks, d.Phase16);
            dx = Cos16(p);
            dz = Sin16(p);
        }

        /// <summary>Bar angle in degrees about +Y as Unity measures it (for rendering): yaw of the bar's +arm.</summary>
        public static float SweeperYawDegrees(in SweeperDef d, double t)
        {
            // Fractional ticks for smooth rendering only; the simulation uses whole ticks.
            var period = d.PeriodTicks == 0 ? 1.0 : d.PeriodTicks;
            var turns = t / period + d.Phase16 / (double)PhaseOne;
            var rad = turns * 2.0 * Math.PI;
            // Unity yaw rotates +Z toward +X; our angle rotates +X toward +Z: yaw = 90° − angle.
            return (float)(90.0 - rad * 180.0 / Math.PI);
        }

        public static bool SweeperContact(in SweeperDef d, uint tick, Vec3 feet, float r, float h, out float pushX, out float pushZ) =>
            SweeperContact(in d, unchecked((int)tick), feet, r, h, out pushX, out pushZ);

        public static bool SweeperContact(in SweeperDef d, int t, Vec3 feet, float r, float h, out float pushX, out float pushZ) =>
            SweeperContactMm(in d, t, MotorState.QuantizeMm(feet.X), MotorState.QuantizeMm(feet.Y), MotorState.QuantizeMm(feet.Z),
                MotorState.QuantizeMm(r), MotorState.QuantizeMm(h), out pushX, out pushZ);

        /// <summary>
        /// Upright capsule (feet in mm, radius r and height h in mm) against the bar at hazard time <paramref name="t"/>,
        /// in integer mm. On contact, <paramref name="pushX"/>/<paramref name="pushZ"/> is the unit horizontal knockback
        /// direction: away from the bar (M2), or along the bar's direction of travel at the contact point
        /// (<see cref="SweeperDef.Tangential"/>; a player who runs into the back of the bar is pushed away from it instead).
        /// </summary>
        public static bool SweeperContactMm(in SweeperDef d, int t, int fx, int fy, int fz, int r, int h, out float pushX, out float pushZ)
        {
            pushX = pushZ = 0f;
            var p = PhaseAt(t, d.PeriodTicks, d.Phase16);
            var c = CosQ30(p);
            var sn = SinQ30(p);
            var by = d.PivotYMm + d.BarHeightMm;

            // Vertical gap between the bar axis and the capsule's axis segment.
            var y0 = fy + r;
            var y1 = fy + h - r;
            long dv = by < y0 ? y0 - by : by > y1 ? by - y1 : 0;
            long reach = r + d.BarRadiusMm;
            if (dv >= reach)
                return false;

            // Horizontal distance from the capsule axis to the bar segment.
            long qx = fx - d.PivotXMm;
            long qz = fz - d.PivotZMm;
            long tt = RoundShift(qx * c + qz * sn, 30);
            if (tt > d.ArmMm) tt = d.ArmMm;
            if (tt < -d.ArmMm) tt = -d.ArmMm;
            var ex = qx - RoundShift(c * tt, 30);
            var ez = qz - RoundShift(sn * tt, 30);
            var dh2 = ex * ex + ez * ez;
            if (dh2 + dv * dv >= reach * reach)
                return false;

            // Direction the bar moves at the contact point: ±(−sin, cos).
            var sign = (d.PeriodTicks >= 0 ? 1 : -1) * (tt >= 0 ? 1 : -1);
            var tx = -sn * (1f / Q30) * sign;
            var tz = c * (1f / Q30) * sign;
            float nx, nz;
            if (dh2 > 0)
            {
                var dh = (float)Math.Sqrt(dh2);
                nx = ex / dh;
                nz = ez / dh;
            }
            else
            {
                nx = tx;
                nz = tz;
            }

            if (d.Tangential != 0 && ex * (long)(-sn) * sign + ez * (long)c * sign >= 0)
            {
                pushX = tx;
                pushZ = tz;
            }
            else
            {
                pushX = nx;
                pushZ = nz;
            }

            return true;
        }
    }
}
