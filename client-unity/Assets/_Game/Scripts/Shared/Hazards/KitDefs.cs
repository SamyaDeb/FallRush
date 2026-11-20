using System;

namespace FallRush.Shared
{
    /// <summary><c>CONVEYOR</c>: a static belt; a player grounded on it is displaced by the belt velocity every tick (<c>surface_push</c>).</summary>
    [Serializable]
    public struct ConveyorDef
    {
        public int CenterXMm, CenterYMm, CenterZMm; // collider box centre
        public int HalfXMm, HalfYMm, HalfZMm;
        public int PitchCdeg;                       // box rotation about +X in centi-degrees (ramps); 0 = flat
        public int BeltXMms, BeltYMms, BeltZMms;    // belt velocity along the surface, mm/s
    }

    /// <summary><c>SPRING_PAD</c>: a static pad; landing on it launches the player with a velocity override, once per landing.</summary>
    [Serializable]
    public struct SpringPadDef
    {
        public int CenterXMm, CenterYMm, CenterZMm; // top-surface centre
        public int HalfXMm, HalfZMm, ThicknessMm;
        public int LaunchXCms, LaunchYCms, LaunchZCms;
    }

    /// <summary><c>TURN_TABLE</c>: a disc rotating about its vertical axis (static round collider); riders are carried around (<c>carry_rot</c>).</summary>
    [Serializable]
    public struct TurnTableDef
    {
        public int CenterXMm, CenterYMm, CenterZMm; // top-surface centre
        public int RadiusMm, ThicknessMm;
        public int PeriodTicks;                     // ticks per turn; negative = clockwise seen from above
        public int Phase16;
    }

    /// <summary>
    /// <c>GATE_DOOR</c>: a block that lifts out of a doorway on a timed cycle closed → opening → open → closing (ticks).
    /// Its collider is posed every tick; it is solid whenever any part of it is in the way. A door that closes onto a
    /// player pushes them out along the door's normal (never stuck). All doors are real and timed (doc 08 §3).
    /// </summary>
    [Serializable]
    public struct GateDoorDef
    {
        public int CenterXMm, CenterYMm, CenterZMm; // closed pose, box centre
        public int HalfXMm, HalfYMm, HalfZMm;       // the thinner of X/Z is the door's normal axis
        public int LiftMm;                          // how far it rises when open
        public int ClosedTicks, OpeningTicks, OpenTicks, ClosingTicks;
        public int OffsetTicks;                     // start offset in the cycle (sweep order)
        public int TelegraphTicks;                  // the door flashes this long before it starts closing
        public int Phase16;                         // extra seeded offset (fraction of the cycle); 0 for sweep doors
    }

    /// <summary><c>MALLET_SWING</c>: a pendulum mallet, <c>angle = A·sin(2π·t/T + φ)</c>; the head is a horizontal cylinder (<c>knock_hard</c>).</summary>
    [Serializable]
    public struct MalletDef
    {
        public int PivotXMm, PivotYMm, PivotZMm;
        public int SwingAxis;                       // 0 = head swings along X, 2 = along Z
        public int ArmMm;                           // pivot → head axis
        public int HeadRadiusMm, HeadHalfWidthMm;   // head cylinder (its axis is horizontal, across the swing)
        public int AmplitudePhase16;                // max angle, 16384 = 90°
        public int PeriodTicks;
        public int Phase16;
        public int TelegraphTicks;                  // wind-up glow before each pass through the bottom
        public int KnockbackCms, KnockUpCms, StunTicks, ImmunityTicks;
    }

    /// <summary><c>BUMPER</c>: a static round post (solid collider); touching it gives a radial <c>knock_soft</c>.</summary>
    [Serializable]
    public struct BumperDef
    {
        public int CenterXMm, CenterYMm, CenterZMm; // base centre
        public int RadiusMm, HeightMm;
        public int ContactMarginMm;                 // contact reach beyond the collider (the motor stops 1 cm away)
        public int KnockbackCms, KnockUpCms, StunTicks, ImmunityTicks;
    }

    public static partial class HazardMath
    {
        /// <summary>Ticks per second, for belt deltas (mm/s → mm per tick).</summary>
        public const int TickRate = 30;

        // ------------------------------------------------------------------ conveyor

        /// <summary>Belt displacement from t − 1 to <paramref name="t"/>, in exact mm (cumulative rounding, so it never drifts).</summary>
        public static void ConveyorDeltaMm(in ConveyorDef d, int t, out int x, out int y, out int z)
        {
            x = (int)(RoundDiv((long)d.BeltXMms * t, TickRate) - RoundDiv((long)d.BeltXMms * (t - 1), TickRate));
            y = (int)(RoundDiv((long)d.BeltYMms * t, TickRate) - RoundDiv((long)d.BeltYMms * (t - 1), TickRate));
            z = (int)(RoundDiv((long)d.BeltZMms * t, TickRate) - RoundDiv((long)d.BeltZMms * (t - 1), TickRate));
        }

        // ------------------------------------------------------------------ turn table

        public static int TurnTablePhase(in TurnTableDef d, int t) => PhaseAt(t, d.PeriodTicks, d.Phase16);

        /// <summary>
        /// Rotational carry from t − 1 to <paramref name="t"/> for feet at <paramref name="feetMmX"/>/<paramref name="feetMmZ"/>.
        /// The rider's offset is taken into the table frame at t − 1 and snapped to a 2 mm grid there, then rotated to t
        /// and rounded to mm. Because the world rounding error (≤ 0.71 mm per axis after rotation) is below half the 2 mm
        /// grid, an idle rider keeps exactly the same table-frame offset every tick: one full turn returns them to the
        /// same millimetre, with no random-walk drift and no flinging.
        /// </summary>
        public static void TurnTableCarryMm(in TurnTableDef d, int t, int feetMmX, int feetMmZ, out int dx, out int dz)
        {
            var p0 = TurnTablePhase(in d, t - 1);
            var p1 = TurnTablePhase(in d, t);
            long fx = feetMmX - d.CenterXMm;
            long fz = feetMmZ - d.CenterZMm;
            long c0 = CosQ30(p0), s0 = SinQ30(p0);
            // local = R(−a0)·offset (R(a) rotates +X toward +Z), snapped to the 2 mm grid: integers only.
            var lx = 2 * RoundShift(fx * c0 + fz * s0, 31);
            var lz = 2 * RoundShift(fz * c0 - fx * s0, 31);
            long c1 = CosQ30(p1), s1 = SinQ30(p1);
            var wx = RoundShift(lx * c1 - lz * s1, 30);
            var wz = RoundShift(lx * s1 + lz * c1, 30);
            dx = (int)(wx - fx);
            dz = (int)(wz - fz);
        }

        /// <summary>Disc yaw for rendering (Unity degrees about +Y), fractional ticks allowed.</summary>
        public static float TurnTableYawDegrees(in TurnTableDef d, double t)
        {
            var period = d.PeriodTicks == 0 ? 1.0 : d.PeriodTicks;
            var turns = t / period + d.Phase16 / (double)PhaseOne;
            return (float)(-turns * 360.0);
        }

        // ------------------------------------------------------------------ gate door

        public static int DoorCycle(in GateDoorDef d) => d.ClosedTicks + d.OpeningTicks + d.OpenTicks + d.ClosingTicks;

        /// <summary>Ticks into the door's cycle at hazard time <paramref name="t"/>.</summary>
        public static int DoorCycleTick(in GateDoorDef d, int t)
        {
            var c = DoorCycle(in d);
            if (c <= 0)
                return 0;
            var seeded = (int)((long)(d.Phase16 & 0xFFFF) * c >> 16);
            return Mod(t + d.OffsetTicks + seeded, c);
        }

        /// <summary>How far the door is lifted (mm) at <paramref name="t"/>: 0 closed, <see cref="GateDoorDef.LiftMm"/> open, integer ramps between.</summary>
        public static int DoorLiftMm(in GateDoorDef d, int t)
        {
            var k = DoorCycleTick(in d, t);
            if (k < d.ClosedTicks)
                return 0;
            k -= d.ClosedTicks;
            if (k < d.OpeningTicks)
                return (int)((long)d.LiftMm * (k + 1) / d.OpeningTicks);
            k -= d.OpeningTicks;
            if (k < d.OpenTicks)
                return d.LiftMm;
            k -= d.OpenTicks;
            return (int)((long)d.LiftMm * (d.ClosingTicks - 1 - k) / d.ClosingTicks);
        }

        /// <summary>True while the door is fully up (the doorway is clear).</summary>
        public static bool DoorOpen(in GateDoorDef d, int t) => DoorLiftMm(in d, t) >= d.LiftMm;

        /// <summary>Ticks until the door next starts closing (0 while closing / closed). For telegraphs and bots.</summary>
        public static int DoorTicksUntilClosing(in GateDoorDef d, int t)
        {
            var k = DoorCycleTick(in d, t);
            var closeAt = d.ClosedTicks + d.OpeningTicks + d.OpenTicks;
            return k < d.ClosedTicks + d.OpeningTicks ? closeAt - k : k < closeAt ? closeAt - k : 0;
        }

        /// <summary>Normal axis of the door slab: 0 (X) or 2 (Z), whichever half extent is thinner.</summary>
        public static int DoorNormalAxis(in GateDoorDef d) => d.HalfXMm <= d.HalfZMm ? 0 : 2;

        // ------------------------------------------------------------------ mallet

        /// <summary>Swing angle at <paramref name="t"/> in phase16 units (16384 = 90°).</summary>
        public static int MalletAngle16(in MalletDef d, int t) => MulQ30(d.AmplitudePhase16, SinQ30(PhaseAt(t, d.PeriodTicks, d.Phase16)));

        /// <summary>Head-axis centre at <paramref name="t"/> in mm, and the sign of the head's velocity along the swing axis (0 at the turning points).</summary>
        public static void MalletHeadMm(in MalletDef d, int t, out int x, out int y, out int z, out int velSign)
        {
            var p = PhaseAt(t, d.PeriodTicks, d.Phase16);
            var a = MulQ30(d.AmplitudePhase16, SinQ30(p));
            var off = MulQ30(d.ArmMm, SinQ30(a));
            x = d.PivotXMm;
            z = d.PivotZMm;
            y = d.PivotYMm - MulQ30(d.ArmMm, CosQ30(a));
            if (d.SwingAxis == 0)
                x += off;
            else
                z += off;
            var cv = CosQ30(p);
            const int eps = Q30 / 1000;
            velSign = (d.PeriodTicks >= 0 ? 1 : -1) * (cv > eps ? 1 : cv < -eps ? -1 : 0);
        }

        public static Vec3 MalletHead(in MalletDef d, int t, out float velSign)
        {
            MalletHeadMm(in d, t, out var x, out var y, out var z, out var vs);
            velSign = vs;
            return MmToVec(x, y, z);
        }

        public static bool MalletContact(in MalletDef d, int t, Vec3 feet, float r, float h, out float pushX, out float pushZ) =>
            MalletContactMm(in d, t, MotorState.QuantizeMm(feet.X), MotorState.QuantizeMm(feet.Y), MotorState.QuantizeMm(feet.Z),
                MotorState.QuantizeMm(r), MotorState.QuantizeMm(h), out pushX, out pushZ);

        /// <summary>
        /// Capsule vs the mallet head (horizontal cylinder across the swing) at <paramref name="t"/>, integer mm. Push
        /// direction: the head's direction of travel along the swing axis (away from the head at the turning points).
        /// </summary>
        public static bool MalletContactMm(in MalletDef d, int t, int fx, int fy, int fz, int r, int h, out float pushX, out float pushZ)
        {
            pushX = pushZ = 0f;
            MalletHeadMm(in d, t, out var hx, out var hy, out var hz, out var vs);
            var y0 = fy + r;
            var y1 = fy + h - r;
            long dv = hy < y0 ? y0 - hy : hy > y1 ? hy - y1 : 0;
            long reach = r + d.HeadRadiusMm;
            if (dv >= reach)
                return false;
            long dw, du;
            if (d.SwingAxis == 0)
            {
                dw = fx - hx;
                du = fz - hz;
            }
            else
            {
                dw = fz - hz;
                du = fx - hx;
            }

            var au = du < 0 ? -du : du;
            var eu = au > d.HeadHalfWidthMm ? au - d.HeadHalfWidthMm : 0;
            if (eu * eu + dw * dw + dv * dv >= reach * reach)
                return false;
            var dir = vs != 0 ? vs : dw >= 0 ? 1 : -1;
            if (d.SwingAxis == 0)
                pushX = dir;
            else
                pushZ = dir;
            return true;
        }

        /// <summary>Wind-up level 0..1 for the telegraph: rises over <see cref="MalletDef.TelegraphTicks"/> before each pass through the bottom.</summary>
        public static float MalletWindup(in MalletDef d, double t)
        {
            var period = Math.Abs(d.PeriodTicks);
            if (period == 0 || d.TelegraphTicks <= 0)
                return 0f;
            // Bottom passes are at phase 0 and 0.5 of the cycle.
            var turns = t / d.PeriodTicks + d.Phase16 / (double)PhaseOne;
            var frac = turns - Math.Floor(turns * 2.0) / 2.0; // 0..0.5 since the last bottom pass
            var untilNext = (0.5 - frac) * period;
            var w = 1.0 - untilNext / d.TelegraphTicks;
            return (float)(w < 0 ? 0 : w > 1 ? 1 : w);
        }

        // ------------------------------------------------------------------ bumper

        public static bool BumperContact(in BumperDef d, Vec3 feet, float r, float h, out float pushX, out float pushZ) =>
            BumperContactMm(in d, MotorState.QuantizeMm(feet.X), MotorState.QuantizeMm(feet.Y), MotorState.QuantizeMm(feet.Z),
                MotorState.QuantizeMm(r), MotorState.QuantizeMm(h), out pushX, out pushZ);

        public static bool BumperContactMm(in BumperDef d, int fx, int fy, int fz, int r, int h, out float pushX, out float pushZ)
        {
            pushX = pushZ = 0f;
            var top = d.CenterYMm + d.HeightMm;
            if (fy >= top || fy + h <= d.CenterYMm)
                return false;
            long dx = fx - d.CenterXMm;
            long dz = fz - d.CenterZMm;
            long reach = d.RadiusMm + d.ContactMarginMm + r;
            var d2 = dx * dx + dz * dz;
            if (d2 >= reach * reach)
                return false;
            if (d2 > 0)
            {
                var l = (float)Math.Sqrt(d2);
                pushX = dx / l;
                pushZ = dz / l;
            }
            else
            {
                pushX = 1f;
            }

            return true;
        }

        // ------------------------------------------------------------------ spring pad

        /// <summary>Feet over the pad's top face (within its footprint, ±5 cm of the top), mm.</summary>
        public static bool OnSpringPad(in SpringPadDef d, int fx, int fy, int fz) =>
            Math.Abs(fx - d.CenterXMm) <= d.HalfXMm && Math.Abs(fz - d.CenterZMm) <= d.HalfZMm && Math.Abs(fy - d.CenterYMm) < 50;
    }
}
