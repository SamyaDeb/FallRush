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
            if (d.Mode == 1)
            {
                // Start gate: opens once at RUSH; never closes on anyone.
                Range(e, who, "openingTicks", d.OpeningTicks, 3, 60);
                Range(e, who, "liftMm", d.LiftMm, 1300, 6000);
                return;
            }

            if (d.Mode != 0)
                e.Add($"{who}: mode={d.Mode} must be 0 (cycle) or 1 (start gate)");
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

        /// <summary>
        /// G4.1a <c>ROLLING_BALL</c>: r 0.6–1.2 m, 2–7 m/s, a release every 1–10 s, balls never overlapping in a lane, a real
        /// telegraph, the knock rules; and the path must not start inside a spawn volume (feet mm triplets in
        /// <paramref name="spawnsMm"/>, a 0.5 m margin around the standing capsule).
        /// </summary>
        public static void Check(in RollingBallDef d, string who, List<string> e, int[] spawnsMm = null)
        {
            Range(e, who, "radiusMm", d.RadiusMm, 600, 1200);
            Range(e, who, "speedMmS", d.SpeedMmS, 2000, 7000);
            Range(e, who, "periodTicks", d.PeriodTicks, 30, 300);
            Range(e, who, "telegraphTicks", d.TelegraphTicks, 9, 90);
            var len = HazardMath.BallLenMm(in d);
            Range(e, who, "pathLenMm", len, 2000, 250000);
            // Spacing between consecutive balls (speed × period) ≥ one diameter: balls of a lane never overlap.
            Range(e, who, "spacingMm", (long)d.SpeedMmS * Math.Max(1, d.PeriodTicks) / HazardMath.TickRate, 2L * d.RadiusMm, long.MaxValue);
            if (spawnsMm != null)
            {
                const int capsuleR = 310, capsuleH = 1000, margin = 500;
                for (var i = 0; i + 2 < spawnsMm.Length; i += 3)
                {
                    long dx = d.StartXMm - spawnsMm[i], dz = d.StartZMm - spawnsMm[i + 2];
                    long reach = d.RadiusMm + capsuleR + margin;
                    var dy = d.StartYMm - spawnsMm[i + 1];
                    if (dx * dx + dz * dz < reach * reach && dy > -d.RadiusMm - margin && dy < capsuleH + d.RadiusMm + margin)
                        e.Add($"{who}: the path starts inside spawn volume {i / 3} (ball r {d.RadiusMm} mm at ({d.StartXMm},{d.StartYMm},{d.StartZMm}))");
                }
            }

            Knock(e, who, d.KnockbackCms, d.KnockUpCms, d.StunTicks, d.ImmunityTicks);
        }

        /// <summary>
        /// G4.1b <c>LID_FLIP</c>: N odd 5–11, lids 1.2–2.2 m, a real telegraph (lit ≥ 0.5 s), waves 1–10 s apart, at most half
        /// the alive lids per wave, rings retiring in order and never the inner 3 × 3, and the fairness floor (≥ 5 of the
        /// inner 3 × 3 solid at every tick, enforced by <see cref="HazardMath.LidWaveSet"/>).
        /// </summary>
        public static void Check(in LidGridDef d, string who, List<string> e)
        {
            if (d.N % 2 == 0)
                e.Add($"{who}: n={d.N} must be odd (a centre lid)");
            Range(e, who, "n", d.N, 5, 11);
            Range(e, who, "lidMm", d.LidMm, 1200, 2200);
            Range(e, who, "gapMm", d.GapMm, 0, 300);
            Range(e, who, "thicknessMm", d.ThicknessMm, 100, 1000);
            Range(e, who, "litTicks", d.LitTicks, 15, 90);
            Range(e, who, "flippedTicks", d.FlippedTicks, 15, 150);
            Range(e, who, "firstWaveTicks", d.FirstWaveTicks, 0, 900);
            Range(e, who, "wavesPerStage", d.WavesPerStage, 1, 64);
            Range(e, who, "minInnerSolid", d.MinInnerSolid, 5, 9);
            Range(e, who, "retireWarnTicks", d.RetireWarnTicks, 30, 300);
            if (d.WaveIntervals == null || d.WaveIntervals.Length == 0 || d.WaveIntervals.Length > 8)
                e.Add($"{who}: waveIntervals needs 1–8 stages");
            else
                foreach (var iv in d.WaveIntervals)
                    Range(e, who, "waveInterval", iv, 30, 300);
            if (d.FlipPermille == null || d.FlipPermille.Length == 0 || d.FlipPermille.Length > 8)
                e.Add($"{who}: flipPermille needs 1–8 stages");
            else
                foreach (var pm in d.FlipPermille)
                    Range(e, who, "flipPermille", pm, 0, 500);
            var rings = d.RingRetireTicks ?? Array.Empty<int>();
            if (d.LoopTicks != 0)
                Range(e, who, "loopTicks", d.LoopTicks, (rings.Length > 0 ? rings[rings.Length - 1] : 0) + 300, 36000);
            if (rings.Length > Math.Max(0, d.Mid - 1))
                e.Add($"{who}: {rings.Length} ring retire ticks for n={d.N}: the inner 3 × 3 never retires (at most {Math.Max(0, d.Mid - 1)})");
            for (var i = 0; i < rings.Length; i++)
            {
                Range(e, who, "ringRetireTicks", rings[i], d.RetireWarnTicks, 36000);
                if (i > 0 && rings[i] <= rings[i - 1])
                    e.Add($"{who}: ringRetireTicks must increase (outermost ring first)");
            }
        }

        /// <summary>Every def of a resolved set; returns the problems (empty = valid). <paramref name="spawnsMm"/>: x,y,z feet triplets (G4 spawn checks).</summary>
        public static List<string> Validate(HazardSet h, int[] spawnsMm)
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
            for (var i = 0; i < h.RollingBalls.Length; i++) Check(in h.RollingBalls[i], $"ROLLING_BALL[{i}]", e, spawnsMm);
            for (var i = 0; i < h.LidGrids.Length; i++) Check(in h.LidGrids[i], $"LID_FLIP[{i}]", e);
            return e;
        }

        /// <summary>Every def of a resolved set; returns the problems (empty = valid). G4 kit included, without the spawn checks.</summary>
        public static List<string> Validate(HazardSet h) => Validate(h, null);
    }
}
