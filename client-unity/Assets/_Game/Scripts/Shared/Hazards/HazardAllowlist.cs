using System;
using System.Collections.Generic;

namespace FallRush.Shared
{
    /// <summary>
    /// Parameter allowlists (doc 08 §2 <c>allowedRanges</c>). The server checks every resolved def (variant, seed and
    /// modifier applied) when it loads the map; anything out of range refuses to start the round with a readable log.
    /// The ranges bound what the motor, the stun rules and the prediction were tested with.
    /// </summary>
    public static class HazardAllowlist
    {
        private static void Range(List<string> e, string who, string field, long v, long min, long max)
        {
            if (v < min || v > max)
                e.Add($"{who}: {field}={v} outside [{min}, {max}]");
        }

        private static void Knock(List<string> e, string who, int push, int up, int stun, int imm)
        {
            Range(e, who, "knockbackCms", push, 0, 1400);
            Range(e, who, "knockUpCms", up, 0, 900);
            Range(e, who, "stunTicks", stun, 0, 24);
            if (stun > 0 && imm < stun + KnockProfile.ImmunityAfterStunTicks)
                e.Add($"{who}: immunityTicks={imm} < stunTicks + {KnockProfile.ImmunityAfterStunTicks} (0.5 s immunity after the stun, doc 08 §2)");
            Range(e, who, "immunityTicks", imm, 0, 60);
        }

        public static void Check(in SweeperDef d, string who, List<string> e)
        {
            Range(e, who, "|periodTicks|", Math.Abs((long)d.PeriodTicks), 60, 600);
            Range(e, who, "armMm", d.ArmMm, 500, 9000);
            Range(e, who, "barRadiusMm", d.BarRadiusMm, 50, 400);
            Range(e, who, "barHeightMm", d.BarHeightMm, 100, 2500);
            // Tip speed ≤ 12 m/s: 2π·arm/T.
            Range(e, who, "tipSpeedMms", (long)(2 * Math.PI * d.ArmMm * 30 / Math.Max(1, Math.Abs(d.PeriodTicks))), 0, 12000);
            Knock(e, who, d.KnockbackCms, d.KnockUpCms, d.StunTicks, d.ImmunityTicks);
        }

        public static void Check(in PlatformDef d, string who, List<string> e)
        {
            Range(e, who, "|periodTicks|", Math.Abs((long)d.PeriodTicks), 60, 1200);
            Range(e, who, "halfXMm", d.HalfXMm, 250, 8000);
            Range(e, who, "halfYMm", d.HalfYMm, 50, 2000);
            Range(e, who, "halfZMm", d.HalfZMm, 250, 8000);
            if (d.KnotsMm != null && d.KnotsMm.Length > 0)
            {
                if (d.KnotsMm.Length % 3 != 0 || d.KnotsMm.Length < 6 || d.KnotsMm.Length > 24)
                    e.Add($"{who}: knots must be 2–8 x,y,z triplets (got {d.KnotsMm.Length} values)");
            }

            // Carry speed ≤ 6 m/s (≤ 200 mm per tick) over one full period.
            var max = 0;
            if (d.PeriodTicks != 0 && (d.KnotsMm == null || d.KnotsMm.Length % 3 == 0))
            {
                for (var t = 1; t <= Math.Abs(d.PeriodTicks); t++)
                {
                    HazardMath.PlatformCenterMm(in d, t, out var x1, out var y1, out var z1);
                    HazardMath.PlatformCenterMm(in d, t - 1, out var x0, out var y0, out var z0);
                    max = Math.Max(max, Math.Max(Math.Abs(x1 - x0), Math.Max(Math.Abs(y1 - y0), Math.Abs(z1 - z0))));
                }
            }

            Range(e, who, "maxMmPerTick", max, 0, 200);
        }

        public static void Check(in ConveyorDef d, string who, List<string> e)
        {
            var v = Math.Sqrt((double)d.BeltXMms * d.BeltXMms + (double)d.BeltYMms * d.BeltYMms + (double)d.BeltZMms * d.BeltZMms);
            Range(e, who, "beltSpeedMms", (long)v, 0, 5000);
            Range(e, who, "pitchCdeg", Math.Abs((long)d.PitchCdeg), 0, 4000);
            Range(e, who, "halfYMm", d.HalfYMm, 50, 2000);
        }

        public static void Check(in SpringPadDef d, string who, List<string> e)
        {
            Range(e, who, "launchYCms", d.LaunchYCms, 300, 2000);
            Range(e, who, "|launchXZ|Cms", (long)Math.Sqrt((double)d.LaunchXCms * d.LaunchXCms + (double)d.LaunchZCms * d.LaunchZCms), 0, 1200);
            Range(e, who, "halfXMm", d.HalfXMm, 300, 4000);
            Range(e, who, "halfZMm", d.HalfZMm, 300, 4000);
        }

        public static void Check(in TurnTableDef d, string who, List<string> e)
        {
            Range(e, who, "|periodTicks|", Math.Abs((long)d.PeriodTicks), 90, 1800);
            Range(e, who, "radiusMm", d.RadiusMm, 1000, 9000);
            // Rim speed ≤ 4 m/s, so a rider is never flung by carry alone.
            Range(e, who, "rimSpeedMms", (long)(2 * Math.PI * d.RadiusMm * 30 / Math.Max(1, Math.Abs(d.PeriodTicks))), 0, 4000);
        }

        public static void Check(in GateDoorDef d, string who, List<string> e)
        {
            Range(e, who, "cycleTicks", HazardMath.DoorCycle(in d), 60, 900);
            Range(e, who, "openTicks", d.OpenTicks, 15, 600);
            Range(e, who, "openingTicks", d.OpeningTicks, 3, 60);
            Range(e, who, "closingTicks", d.ClosingTicks, 6, 60);
            Range(e, who, "closedTicks", d.ClosedTicks, 0, 600);
            Range(e, who, "telegraphTicks", d.TelegraphTicks, 9, 90);
            Range(e, who, "liftMm", d.LiftMm, 1300, 6000);
            // Closing speed ≤ 6 m/s so the push-out is a nudge, not a launch.
            Range(e, who, "closeSpeedMmPerTick", d.LiftMm / Math.Max(1, d.ClosingTicks), 0, 200);
        }

        public static void Check(in MalletDef d, string who, List<string> e)
        {
            Range(e, who, "|periodTicks|", Math.Abs((long)d.PeriodTicks), 60, 600);
            Range(e, who, "amplitudePhase16", d.AmplitudePhase16, 1820, 13653); // 10°..75°
            Range(e, who, "armMm", d.ArmMm, 1000, 9000);
            Range(e, who, "headRadiusMm", d.HeadRadiusMm, 150, 900);
            Range(e, who, "headHalfWidthMm", d.HeadHalfWidthMm, 150, 2500);
            Range(e, who, "telegraphTicks", d.TelegraphTicks, 9, 90);
            if (d.SwingAxis != 0 && d.SwingAxis != 2)
                e.Add($"{who}: swingAxis={d.SwingAxis} must be 0 (X) or 2 (Z)");
            Knock(e, who, d.KnockbackCms, d.KnockUpCms, d.StunTicks, d.ImmunityTicks);
        }

        public static void Check(in BumperDef d, string who, List<string> e)
        {
            Range(e, who, "radiusMm", d.RadiusMm, 300, 2500);
            Range(e, who, "heightMm", d.HeightMm, 500, 3000);
            Range(e, who, "contactMarginMm", d.ContactMarginMm, 10, 100);
            Knock(e, who, d.KnockbackCms, d.KnockUpCms, d.StunTicks, d.ImmunityTicks);
        }

        /// <summary>Every def of a resolved set; returns the problems (empty = valid).</summary>
        public static List<string> Validate(HazardSet h)
        {
            var e = new List<string>();
            for (var i = 0; i < h.Sweepers.Length; i++) Check(in h.Sweepers[i], $"SPINNER_BAR[{i}]", e);
            for (var i = 0; i < h.Platforms.Length; i++) Check(in h.Platforms[i], $"MOVING_PLATFORM[{i}]", e);
            for (var i = 0; i < h.Conveyors.Length; i++) Check(in h.Conveyors[i], $"CONVEYOR[{i}]", e);
            for (var i = 0; i < h.Pads.Length; i++) Check(in h.Pads[i], $"SPRING_PAD[{i}]", e);
            for (var i = 0; i < h.TurnTables.Length; i++) Check(in h.TurnTables[i], $"TURN_TABLE[{i}]", e);
            for (var i = 0; i < h.Doors.Length; i++) Check(in h.Doors[i], $"GATE_DOOR[{i}]", e);
            for (var i = 0; i < h.Mallets.Length; i++) Check(in h.Mallets[i], $"MALLET_SWING[{i}]", e);
            for (var i = 0; i < h.Bumpers.Length; i++) Check(in h.Bumpers[i], $"BUMPER[{i}]", e);
            return e;
        }
    }
}
