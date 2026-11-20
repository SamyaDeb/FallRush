using System;

namespace FallRush.Shared
{
    /// <summary>
    /// A bar rotating about a vertical axis (it spans both sides of the pivot). Touching it knocks the player back and
    /// stuns them. Integer data (mm, cm/s, ticks) so both sides start from bit-identical parameters.
    /// </summary>
    [Serializable]
    public struct SweeperDef
    {
        public int PivotXMm, PivotYMm, PivotZMm; // ground point under the axis
        public int ArmMm;                        // axis → tip; the bar spans 2 × arm through the axis
        public int BarRadiusMm;
        public int BarHeightMm;                  // bar axis height above the pivot point
        public int PeriodTicks;                  // ticks per revolution; negative turns the other way
        public int Phase16;                      // start angle, 0..65535 = one turn (derive from a seed)
        public int KnockbackCms;                 // horizontal knockback speed
        public int KnockUpCms;
        public int StunTicks;
        public int ImmunityTicks;
    }

    /// <summary>A box moving on a sine path: <c>centre(tick) = Centre + Travel · sin(2π·tick/Period + phase)</c>, in mm.</summary>
    [Serializable]
    public struct PlatformDef
    {
        public int CenterXMm, CenterYMm, CenterZMm;
        public int HalfXMm, HalfYMm, HalfZMm;
        public int TravelXMm, TravelYMm, TravelZMm; // amplitude vector
        public int PeriodTicks;
        public int Phase16;
    }

    /// <summary>
    /// Deterministic hazard poses and effects (doc 04 §4.3 "LocalWorld(tick)"): every pose is
    /// <c>f(params, seed, tick)</c> computed with integer phase and float polynomials only (no <c>Math.Sin</c>, no time,
    /// no randomness), so the server and the predicting client evaluate the same pose for the same tick.
    /// M3 extends this into the full obstacle kit; new hazard types add a def struct and a pose/effect function here.
    /// </summary>
    public static class HazardMath
    {
        public const int PhaseOne = 65536;
        private const float TwoPi = 6.28318530718f;
        private const float HalfPi = 1.57079632679f;

        /// <summary>Start phase (0..65535) from a hazard seed (e.g. map seed × hazard index); a fixed integer hash.</summary>
        public static int PhaseFromSeed(uint seed)
        {
            var x = seed * 0x9E3779B1u;
            x ^= x >> 15;
            x *= 0x85EBCA77u;
            x ^= x >> 13;
            return (int)(x & 0xFFFF);
        }

        /// <summary>Phase (0..65535) at <paramref name="tick"/> for a cycle of |<paramref name="periodTicks"/>| ticks.</summary>
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

        /// <summary>sin(2π · phase / 65536) from a 9th-order odd polynomial on one quadrant (|error| &lt; 4e-6). Float ops only.</summary>
        public static float Sin16(int phase)
        {
            phase &= 0xFFFF;
            var quadrant = phase >> 14;
            var within = phase & 0x3FFF;
            if ((quadrant & 1) == 1)
                within = 0x4000 - within;
            var x = within * (HalfPi / 16384f);
            var x2 = x * x;
            var s = x * (1f + x2 * (-1f / 6f + x2 * (1f / 120f + x2 * (-1f / 5040f + x2 * (1f / 362880f)))));
            return quadrant >= 2 ? -s : s;
        }

        public static float Cos16(int phase) => Sin16(phase + 0x4000);

        // ------------------------------------------------------------------ moving platform

        /// <summary>Platform centre at <paramref name="tick"/>, quantised to whole millimetres.</summary>
        public static void PlatformCenterMm(in PlatformDef d, uint tick, out int x, out int y, out int z)
        {
            var s = Sin16(Phase(tick, d.PeriodTicks, d.Phase16));
            x = d.CenterXMm + RoundMm(d.TravelXMm * s);
            y = d.CenterYMm + RoundMm(d.TravelYMm * s);
            z = d.CenterZMm + RoundMm(d.TravelZMm * s);
        }

        public static Vec3 PlatformCenter(in PlatformDef d, uint tick)
        {
            PlatformCenterMm(in d, tick, out var x, out var y, out var z);
            return new Vec3(x * 0.001f, y * 0.001f, z * 0.001f);
        }

        /// <summary>How far the platform moved from tick − 1 to <paramref name="tick"/> (what it carries a rider by).</summary>
        public static Vec3 PlatformDelta(in PlatformDef d, uint tick)
        {
            PlatformCenterMm(in d, tick, out var x1, out var y1, out var z1);
            PlatformCenterMm(in d, tick - 1, out var x0, out var y0, out var z0);
            return new Vec3((x1 - x0) * 0.001f, (y1 - y0) * 0.001f, (z1 - z0) * 0.001f);
        }

        private static int RoundMm(float v) => v >= 0f ? (int)(v + 0.5f) : -(int)(-v + 0.5f);

        // ------------------------------------------------------------------ sweeper

        /// <summary>Unit direction of the bar (XZ) at <paramref name="tick"/>.</summary>
        public static void SweeperDir(in SweeperDef d, uint tick, out float dx, out float dz)
        {
            var p = Phase(tick, d.PeriodTicks, d.Phase16);
            dx = Cos16(p);
            dz = Sin16(p);
        }

        /// <summary>Bar angle in degrees about +Y as Unity measures it (for rendering): yaw of the bar's +arm.</summary>
        public static float SweeperYawDegrees(in SweeperDef d, double tick)
        {
            // Fractional ticks for smooth rendering only; the simulation uses whole ticks.
            var period = d.PeriodTicks == 0 ? 1.0 : d.PeriodTicks;
            var turns = tick / period + d.Phase16 / (double)PhaseOne;
            var rad = turns * 2.0 * Math.PI;
            // Unity yaw rotates +Z toward +X; our angle rotates +X toward +Z: yaw = 90° − angle.
            return (float)(90.0 - rad * 180.0 / Math.PI);
        }

        /// <summary>
        /// Upright capsule (feet, r, h) against the bar at <paramref name="tick"/>. On contact, <paramref name="pushX"/>/<paramref name="pushZ"/>
        /// is the unit horizontal direction away from the bar (on the bar's leading side that is its direction of travel).
        /// </summary>
        public static bool SweeperContact(in SweeperDef d, uint tick, Vec3 feet, float r, float h, out float pushX, out float pushZ)
        {
            pushX = pushZ = 0f;
            SweeperDir(in d, tick, out var dx, out var dz);
            var px = d.PivotXMm * 0.001f;
            var pz = d.PivotZMm * 0.001f;
            var by = (d.PivotYMm + d.BarHeightMm) * 0.001f;
            var arm = d.ArmMm * 0.001f;
            var br = d.BarRadiusMm * 0.001f;

            // Vertical gap between the bar axis and the capsule's axis segment.
            var y0 = feet.Y + r;
            var y1 = feet.Y + h - r;
            var dv = by < y0 ? y0 - by : by > y1 ? by - y1 : 0f;
            var reach = r + br;
            if (dv >= reach)
                return false;

            // Horizontal distance from the capsule axis to the bar segment.
            var qx = feet.X - px;
            var qz = feet.Z - pz;
            var t = qx * dx + qz * dz;
            if (t > arm) t = arm;
            if (t < -arm) t = -arm;
            var ex = qx - dx * t;
            var ez = qz - dz * t;
            var dh2 = ex * ex + ez * ez;
            if (dh2 + dv * dv >= reach * reach)
                return false;

            var dh = (float)Math.Sqrt(dh2);
            if (dh > 1e-4f)
            {
                pushX = ex / dh;
                pushZ = ez / dh;
            }
            else
            {
                // On the bar's axis line: push along its direction of travel.
                var sign = (d.PeriodTicks >= 0 ? 1f : -1f) * (t >= 0f ? 1f : -1f);
                pushX = -dz * sign;
                pushZ = dx * sign;
            }

            return true;
        }
    }

    /// <summary>
    /// The hazards of one map, evaluated by tick. Shared by the server (<see cref="SimCore"/>) and client prediction,
    /// so a hit is predicted exactly when the server will apply it.
    /// </summary>
    public sealed class HazardSet
    {
        public readonly SweeperDef[] Sweepers;
        public readonly PlatformDef[] Platforms;

        public HazardSet(SweeperDef[] sweepers, PlatformDef[] platforms)
        {
            Sweepers = sweepers ?? Array.Empty<SweeperDef>();
            Platforms = platforms ?? Array.Empty<PlatformDef>();
        }

        public static readonly HazardSet Empty = new HazardSet(null, null);

        /// <summary>
        /// Applies sweeper hits after the motor step of <paramref name="tick"/>: knockback velocity, stun, and
        /// immunity (no re-hit while it lasts). Returns true on a new hit.
        /// </summary>
        public bool ApplySweepers(ref MotorState s, uint tick, MovementConfig cfg)
        {
            if (s.ImmunityTicks > 0 || s.Has(MotorState.Respawning))
                return false;
            var feet = s.Position;
            for (var i = 0; i < Sweepers.Length; i++)
            {
                ref readonly var d = ref Sweepers[i];
                if (!HazardMath.SweeperContact(in d, tick, feet, cfg.Radius, cfg.Height, out var nx, out var nz))
                    continue;

                var k = d.KnockbackCms * 0.01f;
                s.Velocity = new Vec3(nx * k, d.KnockUpCms * 0.01f, nz * k);
                s.StunTicks = (byte)Math.Min(255, d.StunTicks);
                s.ImmunityTicks = (byte)Math.Min(255, d.ImmunityTicks);
                s.Set(MotorState.Grounded, false);
                s.Set(MotorState.Diving, false);
                s.Set(MotorState.DiveLanding, false);
                s.LandTicks = 0;
                s.CoyoteTicks = 0;
                s.JumpBufferTicks = 0;
                s.Surface = 0;
                return true;
            }

            return false;
        }

        public bool PlatformDelta(int index, uint tick, out Vec3 delta)
        {
            if ((uint)index >= (uint)Platforms.Length)
            {
                delta = Vec3.Zero;
                return false;
            }

            delta = HazardMath.PlatformDelta(in Platforms[index], tick);
            return true;
        }
    }

    /// <summary>
    /// One simulated tick of one player, identical on the server and in client prediction (doc 04 §4.2/§4.3):
    /// <c>world.BeginTick(tick)</c> (the caller, once per tick) → motor step (carried by moving platforms through
    /// <see cref="IMotorWorld.SurfaceDelta"/>) → sweeper hits → fall volume.
    /// Player–player separation is server-only and not part of this.
    /// </summary>
    public static class PlayerStep
    {
        [Flags]
        public enum Result : byte
        {
            None = 0,
            HazardHit = 1,
            FellOut = 2,
        }

        public static Result Run(KinematicMotor motor, ref MotorState s, in InputCmd cmd, uint tick, IMotorWorld world, HazardSet hazards)
        {
            if (s.Has(MotorState.Respawning))
                return Result.None;
            var cfg = motor.Cfg;
            motor.Step(ref s, in cmd, cfg.Dt, world);
            var r = Result.None;
            if (hazards != null && hazards.ApplySweepers(ref s, tick, cfg))
                r |= Result.HazardHit;
            if (s.PosY * 0.001f < cfg.KillY)
            {
                s.Velocity = Vec3.Zero;
                s.Flags = MotorState.Respawning;
                s.Surface = 0;
                r |= Result.FellOut;
            }

            return r;
        }
    }
}
