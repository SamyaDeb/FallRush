using System;

namespace FallRush.Shared
{
    /// <summary>An ordered race checkpoint: a trigger box (server-side) plus the respawn spots behind it.</summary>
    [Serializable]
    public struct CheckpointDef
    {
        public int CenterXMm, CenterYMm, CenterZMm;
        public int HalfXMm, HalfYMm, HalfZMm;
        /// <summary>Respawn spots as x,y,z,yaw256 quadruples (mm; yaw faces the course).</summary>
        public int[] RespawnMm;
        /// <summary>Distance along the course polyline from the start to this checkpoint, mm (race ranking, GDD §5.1).</summary>
        public int CourseMm;

        public bool Contains(int x, int y, int z) =>
            Math.Abs(x - CenterXMm) <= HalfXMm && Math.Abs(y - CenterYMm) <= HalfYMm && Math.Abs(z - CenterZMm) <= HalfZMm;
    }

    /// <summary>
    /// Everything about a map that the simulation depends on, in one place, so it can be hashed (§3.3 of the M3 prompt):
    /// both variants' resolved hazard defs (phase excluded: it comes from the round's seed, so the phase <b>ranges</b> are
    /// hashed instead), checkpoints, respawn spots, spawns and the kill-Y. Server and client compute the hash from their
    /// own scene; a client whose hash differs refuses to play the round instead of desyncing silently.
    /// </summary>
    public sealed class MapContent
    {
        public const string Magic = "fallrush-map-v1";

        public int MapId;
        public int Version;
        public int KillYMm;
        /// <summary>12 × (x, y, z, yaw256), mm.</summary>
        public int[] SpawnsMm = Array.Empty<int>();
        public CheckpointDef[] Checkpoints = Array.Empty<CheckpointDef>();
        /// <summary>The hazard defs of variant A and B, resolved with no modifier and phase 0.</summary>
        public HazardSet[] Variants = Array.Empty<HazardSet>();
        /// <summary>Stable obstacle ids in authoring order (type byte + id), and each one's phase range (min16, max16).</summary>
        public string[] ObstacleIds = Array.Empty<string>();
        public int[] PhaseRanges = Array.Empty<int>();
        /// <summary>Course centre line, x,y,z mm triplets (race ranking distance, GDD §5.1).</summary>
        public int[] CourseLineMm = Array.Empty<int>();

        public byte[] Hash() => Sha256.Hash(CanonicalBytes());

        /// <summary>The canonical little-endian encoding that <see cref="Hash"/> digests.</summary>
        public byte[] CanonicalBytes()
        {
            var w = new CanonicalWriter();
            w.Str(Magic).I32(MapId).I32(Version).I32(KillYMm).Ints(SpawnsMm);
            w.I32(Checkpoints.Length);
            foreach (var c in Checkpoints)
                w.I32(c.CenterXMm).I32(c.CenterYMm).I32(c.CenterZMm).I32(c.HalfXMm).I32(c.HalfYMm).I32(c.HalfZMm).I32(c.CourseMm).Ints(c.RespawnMm);
            w.I32(ObstacleIds.Length);
            foreach (var id in ObstacleIds)
                w.Str(id);
            w.Ints(PhaseRanges);
            w.Ints(CourseLineMm);
            w.I32(Variants.Length);
            foreach (var v in Variants)
                HazardEncoding.Write(w, v, zeroPhase: true);
            return w.Bytes.ToArray();
        }

        public static string Short(byte[] hash) => hash == null || hash.Length < 4 ? "--------" : Sha256.Hex(hash).Substring(0, 8);
    }

    /// <summary>Fixed little-endian field order of every hazard def (map content hash and hazard vectors).</summary>
    public static class HazardEncoding
    {
        public static void Write(CanonicalWriter w, HazardSet h, bool zeroPhase)
        {
            w.I32(h.Sweepers.Length);
            foreach (var d in h.Sweepers)
                w.I32(d.PivotXMm).I32(d.PivotYMm).I32(d.PivotZMm).I32(d.ArmMm).I32(d.BarRadiusMm).I32(d.BarHeightMm).I32(d.PeriodTicks)
                    .I32(zeroPhase ? 0 : d.Phase16).I32(d.KnockbackCms).I32(d.KnockUpCms).I32(d.StunTicks).I32(d.ImmunityTicks).I32(d.Tangential);
            w.I32(h.Platforms.Length);
            foreach (var d in h.Platforms)
                w.I32(d.CenterXMm).I32(d.CenterYMm).I32(d.CenterZMm).I32(d.HalfXMm).I32(d.HalfYMm).I32(d.HalfZMm)
                    .I32(d.TravelXMm).I32(d.TravelYMm).I32(d.TravelZMm).I32(d.PeriodTicks).I32(zeroPhase ? 0 : d.Phase16).Ints(d.KnotsMm);
            w.I32(h.Conveyors.Length);
            foreach (var d in h.Conveyors)
                w.I32(d.CenterXMm).I32(d.CenterYMm).I32(d.CenterZMm).I32(d.HalfXMm).I32(d.HalfYMm).I32(d.HalfZMm).I32(d.PitchCdeg)
                    .I32(d.BeltXMms).I32(d.BeltYMms).I32(d.BeltZMms);
            w.I32(h.Pads.Length);
            foreach (var d in h.Pads)
                w.I32(d.CenterXMm).I32(d.CenterYMm).I32(d.CenterZMm).I32(d.HalfXMm).I32(d.HalfZMm).I32(d.ThicknessMm)
                    .I32(d.LaunchXCms).I32(d.LaunchYCms).I32(d.LaunchZCms);
            w.I32(h.TurnTables.Length);
            foreach (var d in h.TurnTables)
                w.I32(d.CenterXMm).I32(d.CenterYMm).I32(d.CenterZMm).I32(d.RadiusMm).I32(d.ThicknessMm).I32(d.PeriodTicks).I32(zeroPhase ? 0 : d.Phase16);
            w.I32(h.Doors.Length);
            foreach (var d in h.Doors)
                w.I32(d.CenterXMm).I32(d.CenterYMm).I32(d.CenterZMm).I32(d.HalfXMm).I32(d.HalfYMm).I32(d.HalfZMm).I32(d.LiftMm)
                    .I32(d.ClosedTicks).I32(d.OpeningTicks).I32(d.OpenTicks).I32(d.ClosingTicks).I32(d.OffsetTicks).I32(d.TelegraphTicks).I32(zeroPhase ? 0 : d.Phase16);
            w.I32(h.Mallets.Length);
            foreach (var d in h.Mallets)
                w.I32(d.PivotXMm).I32(d.PivotYMm).I32(d.PivotZMm).I32(d.SwingAxis).I32(d.ArmMm).I32(d.HeadRadiusMm).I32(d.HeadHalfWidthMm)
                    .I32(d.AmplitudePhase16).I32(d.PeriodTicks).I32(zeroPhase ? 0 : d.Phase16).I32(d.TelegraphTicks)
                    .I32(d.KnockbackCms).I32(d.KnockUpCms).I32(d.StunTicks).I32(d.ImmunityTicks);
            w.I32(h.Bumpers.Length);
            foreach (var d in h.Bumpers)
                w.I32(d.CenterXMm).I32(d.CenterYMm).I32(d.CenterZMm).I32(d.RadiusMm).I32(d.HeightMm).I32(d.ContactMarginMm)
                    .I32(d.KnockbackCms).I32(d.KnockUpCms).I32(d.StunTicks).I32(d.ImmunityTicks);
        }
    }
}
