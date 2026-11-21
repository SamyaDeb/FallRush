using System;

namespace FallRush.Shared
{
    /// <summary>
    /// G4.4 Star Fountain burst (doc 08 §6): <b>view only</b>. In the last <see cref="ArcTicks"/> before a wave's spawn tick
    /// each of its stars is drawn flying from the fountain crown to its anchor's posed position <b>at the spawn tick</b>
    /// (anchors on the turntable move, so the end point is posed then); the crown glows from <see cref="GlowTicks"/> before.
    /// Pure functions of the hazard time (no state, no allocation): the client view and the bots read the same thing. The
    /// pickup still starts at the spawn tick (<see cref="SkillRules"/> and the timeline are unchanged).
    /// </summary>
    public static class StarBurst
    {
        public const int ArcTicks = 15;   // 0.5 s
        public const int GlowTicks = 30;  // 1 s
        public const int ArcApexMm = 2500; // above the straight line, at the middle

        /// <summary>The next wave index whose spawn tick is after <paramref name="t"/> (strictly), and that tick.</summary>
        public static int NextWave(in StarFieldDef d, double t, out int spawnT)
        {
            if (d.WaveEveryTicks <= 0)
            {
                spawnT = int.MaxValue;
                return -1;
            }

            var w = t < 0 ? 0 : (int)Math.Floor(t / d.WaveEveryTicks) + 1;
            spawnT = w * d.WaveEveryTicks;
            return w;
        }

        /// <summary>Crown glow 0…1: ramps up over the second before a wave, flashes at the spawn, fades in 0.33 s.</summary>
        public static float CrownGlow(in StarFieldDef d, double t)
        {
            if (d.WaveEveryTicks <= 0)
                return 0f;
            NextWave(in d, t, out var next);
            var before = next - t;
            if (before <= GlowTicks)
                return (float)(1.0 - before / GlowTicks);
            var after = t - (next - d.WaveEveryTicks);
            if (after >= 0 && after < 10)
                return (float)(1.0 - after / 10.0);
            return 0f;
        }

        /// <summary>
        /// Where timeline star <paramref name="id"/> is drawn in its burst at hazard time <paramref name="t"/> (mm), or false
        /// outside its arc window [spawn − <see cref="ArcTicks"/>, spawn). Golden stars and dropped stars have no burst.
        /// <paramref name="u"/> = 0 at the crown … 1 at the anchor.
        /// </summary>
        public static bool ArcPosMm(in StarFieldDef d, HazardSet h, ulong seed, int id, double t, int crownX, int crownY, int crownZ, out double x, out double y, out double z, out double u)
        {
            x = y = z = u = 0;
            if (id < 0 || id >= HazardMath.StarGoldenBase || !HazardMath.StarSlot(in d, seed, id, out var a, out var spawnT, out _, out _))
                return false;
            var start = spawnT - ArcTicks;
            if (t < start || t >= spawnT)
                return false;
            u = (t - start) / ArcTicks;
            HazardMath.StarAnchorMm(in d, h, a, spawnT, out var ex, out var ey, out var ez);
            x = crownX + (ex - crownX) * u;
            z = crownZ + (ez - crownZ) * u;
            y = crownY + (ey - crownY) * u + 4.0 * ArcApexMm * u * (1 - u);
            return true;
        }
    }
}
