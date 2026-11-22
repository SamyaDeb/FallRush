using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FallRush.Net;
using FallRush.Shared;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// P1-K: exports each map scene (Lobby, the 4 round maps, Sandbox) to <c>maps/&lt;key&gt;.mapjson</c> for rush-core
/// (<c>rush-sim mapblob build</c> turns it into <c>.mapblob</c>). Read-only: scenes are opened and never saved, so the map
/// content hashes cannot change. Walks the <c>Gameplay</c> layer: BoxCollider → OBB (Q15 axes), convex MeshCollider →
/// vertical prism (asserted), SphereCollider → sphere, CapsuleCollider → vertical capsule; the hazard authoring components
/// → def words in rush-core's field order (<c>crates/rush-core/src/hazards/defs.rs</c>), both variants, unresolved (the
/// seeded phase and the modifier tuning are applied at run time from the obstacle id, as <see cref="MapInfo.BuildHazards"/>
/// does); surface ids and moving colliders exactly as <see cref="PhysxMotorWorld"/> maps them.
/// <para>Batch: <c>-executeMethod MapBlobExport.ExportAll</c>. Menu: Fall Rush/P1/Export map blobs.</para>
/// </summary>
public static class MapBlobExport
{
    private const int Q15 = 32768;

    public static string OutDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "maps"));

    [MenuItem("Fall Rush/P1/Export map blobs (mapjson)")]
    public static void ExportAll()
    {
        var ok = true;
        Directory.CreateDirectory(OutDir);
        var summary = new StringBuilder();
        foreach (var path in Build.MapScenes)
        {
            try
            {
                var json = ExportScene(path, out var key, out var counts);
                var file = Path.Combine(OutDir, key + ".mapjson");
                File.WriteAllText(file, json, new UTF8Encoding(false));
                summary.Append($"[p1] {key} {counts} → {file}\n");
            }
            catch (Exception e)
            {
                ok = false;
                Debug.LogError($"[p1] export failed for {path}: {e}");
            }
        }

        Debug.Log(summary.ToString());
        if (Application.isBatchMode)
            EditorApplication.Exit(ok ? 0 : 1);
    }

    /// <summary>The mapjson text of one scene (deterministic: same scene → same bytes).</summary>
    public static string ExportScene(string scenePath, out string key, out string counts)
    {
        var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        Physics.SyncTransforms();
        MapInfo info = null;
        foreach (var root in scene.GetRootGameObjects())
        {
            info = root.GetComponentInChildren<MapInfo>(true);
            if (info != null)
                break;
        }

        if (info == null)
            throw new InvalidOperationException($"{scenePath}: no MapInfo");
        var def = info.definition;
        key = def != null && !string.IsNullOrEmpty(def.mapKey) ? def.mapKey : Path.GetFileNameWithoutExtension(scenePath).Replace("Map_", "");
        return Export(info, scene.GetRootGameObjects(), key, out counts).Replace("{\n\"key\":", "{\n\"scene\":" + Str(Path.GetFileNameWithoutExtension(scenePath)) + ",\"key\":");
    }

    private sealed class ColRef
    {
        public string Dyn = "static";
        public int DynIndex;
        public int Surface = -1;
    }

    public static string Export(MapInfo info, GameObject[] roots, string key, out string counts)
    {
        var layer = LayerMask.NameToLayer(PhysxMotorWorld.GameplayLayerName);
        // ---- what PhysxMotorWorld poses / maps (same order as HazardSet) ----
        var h = info.BuildHazards(default, out var cols, zeroPhase: true);
        var refs = new Dictionary<Collider, ColRef>();
        void Mark(Collider c, string dyn, int idx, int surface)
        {
            if (c == null)
                return;
            if (!refs.TryGetValue(c, out var r))
                refs[c] = r = new ColRef();
            if (dyn != null)
            {
                r.Dyn = dyn;
                r.DynIndex = idx;
            }

            if (surface >= 0)
                r.Surface = surface;
        }

        for (var i = 0; i < cols.Platforms.Length; i++)
            Mark(cols.Platforms[i], "platform", i, h.PlatformSurface(i));
        for (var i = 0; i < cols.TurnTables.Length; i++)
            Mark(cols.TurnTables[i], null, 0, h.TurnTableSurface(i));
        for (var i = 0; i < cols.Conveyors.Length; i++)
            Mark(cols.Conveyors[i], null, 0, h.ConveyorSurface(i));
        for (var i = 0; i < cols.Pads.Length; i++)
            Mark(cols.Pads[i], null, 0, h.PadSurface(i));
        for (var i = 0; i < cols.Doors.Length; i++)
            Mark(cols.Doors[i], "door", i, -1);
        for (var i = 0; i < cols.Lids.Length; i++)
            Mark(cols.Lids[i], "lid", i, -1);
        for (var i = 0; i < cols.Tiles.Length; i++)
            Mark(cols.Tiles[i], "tile", i, -1);
        for (var i = 0; i < cols.Blocks.Length; i++)
            Mark(cols.Blocks[i], "block", i, -1);

        // ---- colliders (hierarchy order: deterministic) ----
        var sb = new StringBuilder(1 << 20);
        var nBox = 0;
        var nPrism = 0;
        var nSphere = 0;
        var nCapsule = 0;
        var parts = new List<string>();
        foreach (var root in roots)
        {
            foreach (var c in root.GetComponentsInChildren<Collider>(true))
            {
                if (c.gameObject.layer != layer || c.isTrigger)
                    continue;
                var r = refs.TryGetValue(c, out var rr) ? rr : new ColRef();
                // Toggled colliders (lids, tiles, blocks) are exported even when switched off at edit time; others must be live.
                var toggled = r.Dyn == "lid" || r.Dyn == "tile" || r.Dyn == "block";
                if (!toggled && (!c.enabled || !c.gameObject.activeInHierarchy))
                    continue;
                // Moving colliders: geometry relative to the collider's transform (rush-core adds PlatformCenter(t) / DoorCenter(t)).
                var rel = r.Dyn == "platform" || r.Dyn == "door" ? c.transform.position : Vector3.zero;
                string shape;
                switch (c)
                {
                    case BoxCollider b:
                        shape = Obb(b, rel);
                        nBox++;
                        break;
                    case MeshCollider m:
                        shape = Prism(m, rel);
                        nPrism++;
                        break;
                    case SphereCollider s:
                        shape = Sphere(s, rel);
                        nSphere++;
                        break;
                    case CapsuleCollider cc:
                        shape = Capsule(cc, rel);
                        nCapsule++;
                        break;
                    default:
                        throw new InvalidOperationException($"{HPath(c.transform)}: unsupported collider {c.GetType().Name}");
                }

                parts.Add($"{{{shape},\"surface\":{r.Surface},\"dyn\":\"{r.Dyn}\",\"dynIndex\":{r.DynIndex},\"name\":{Str(HPath(c.transform))}}}");
            }
        }

        foreach (var c in refs.Keys)
            if (c != null && (c.gameObject.layer != layer || c.isTrigger))
                throw new InvalidOperationException($"{HPath(c.transform)}: hazard collider is not a Gameplay-layer solid");

        counts = $"colliders {parts.Count} (box {nBox}, prism {nPrism}, sphere {nSphere}, capsule {nCapsule})";

        var d = info.definition;
        sb.Append("{\n\"key\":").Append(Str(key)).Append(",\n\"meta\":{");
        sb.Append("\"mapId\":").Append(info.MapId);
        sb.Append(",\"mode\":").Append(ModeId(d != null ? d.mode : "Sandbox", info.MapId));
        sb.Append(",\"variants\":").Append(d != null ? Math.Max(1, d.variants) : 2);
        sb.Append(",\"killY\":").Append(d != null ? info.KillYMm : -20000);
        sb.Append(",\"timerCapS\":").Append(d != null ? d.timerCapSeconds : 0);
        sb.Append(",\"introMs\":").Append(d != null ? Mm(d.introSeconds) : 3000);
        sb.Append(",\"countdownMs\":").Append(d != null ? Mm(d.countdownSeconds) : 3000);
        sb.Append(",\"targetMin\":").Append(d != null ? d.targetDuration.x : 0);
        sb.Append(",\"targetMax\":").Append(d != null ? d.targetDuration.y : 0);
        var ssA = h.SweeperStartT;
        var hB = info.BuildHazards(new RoundParams { Variant = 1 }, out _, zeroPhase: true);
        sb.Append(",\"sweeperStartA\":").Append(ssA == int.MinValue ? "null" : ssA.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"sweeperStartB\":").Append(hB.SweeperStartT == int.MinValue ? "null" : hB.SweeperStartT.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"botArea\":[").Append(Mm(info.botAreaCenter.x)).Append(',').Append(Mm(info.botAreaCenter.z)).Append(',')
            .Append(Mm(info.botAreaHalfExtents.x)).Append(',').Append(Mm(info.botAreaHalfExtents.y)).Append(']');
        sb.Append(",\"version\":").Append(d != null ? d.version : 0).Append("},\n");
        sb.Append("\"colliders\":[\n").Append(string.Join(",\n", parts)).Append("\n],\n");

        var sp = info.SpawnsMm();
        var yaws = info.SpawnYaws();
        sb.Append("\"spawns\":[");
        for (var i = 0; i < Protocol.MaxPlayers; i++)
            sb.Append(i > 0 ? "," : "").Append('[').Append(sp[i * 3]).Append(',').Append(sp[i * 3 + 1]).Append(',').Append(sp[i * 3 + 2]).Append(',').Append(yaws[i]).Append(']');
        sb.Append("],\n\"checkpoints\":[");
        var cps = info.CheckpointDefs();
        for (var i = 0; i < cps.Length; i++)
        {
            var c = cps[i];
            sb.Append(i > 0 ? ",\n" : "\n").Append("{\"center\":[").Append(c.CenterXMm).Append(',').Append(c.CenterYMm).Append(',').Append(c.CenterZMm)
                .Append("],\"half\":[").Append(c.HalfXMm).Append(',').Append(c.HalfYMm).Append(',').Append(c.HalfZMm).Append("],\"courseMm\":").Append(c.CourseMm).Append(",\"respawns\":[");
            var rs = c.RespawnMm ?? Array.Empty<int>();
            for (var k = 0; k + 3 < rs.Length; k += 4)
                sb.Append(k > 0 ? "," : "").Append('[').Append(rs[k]).Append(',').Append(rs[k + 1]).Append(',').Append(rs[k + 2]).Append(',').Append(rs[k + 3]).Append(']');
            sb.Append("]}");
        }

        sb.Append("],\n\"course\":[");
        var cl = info.CourseLineMm();
        for (var i = 0; i + 2 < cl.Length; i += 3)
            sb.Append(i > 0 ? "," : "").Append('[').Append(cl[i]).Append(',').Append(cl[i + 1]).Append(',').Append(cl[i + 2]).Append(']');
        sb.Append("],\n\"hazards\":[\n").Append(Hazards(info, 0)).Append(",\n").Append(Hazards(info, 1)).Append("\n],\n");
        sb.Append("\"botRoutes\":[").Append(BotRouteWords(info)).Append("]\n}\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ hazards (def words in rush-core order)

    private static string Hazards(MapInfo info, int variant)
    {
        var list = new List<string>();
        void Add(HazardAuthoring a, int[] words, bool baked = false)
        {
            list.Add($"{{\"type\":{(byte)a.Type},\"id\":{Str(a.obstacleId ?? "")},\"phaseMin\":{a.phaseMin16},\"phaseMax\":{a.phaseMax16},\"baked\":{(baked ? "true" : "false")},\"def\":[{string.Join(",", words)}]}}");
        }

        bool B(HazardAuthoring a) => variant == 1 && a.hasVariantB;
        foreach (var a in info.sweepers)
        {
            var d = B(a) ? a.variantB : a.def;
            var baked = string.IsNullOrEmpty(a.obstacleId);
            if (baked)
                d.Phase16 = HazardMath.PhaseFromSeed(a.seed);
            Add(a, new[] { d.PivotXMm, d.PivotYMm, d.PivotZMm, d.ArmMm, d.BarRadiusMm, d.BarHeightMm, d.PeriodTicks, d.Phase16, d.KnockbackCms, d.KnockUpCms, d.StunTicks, d.ImmunityTicks, d.Tangential }, baked);
        }

        foreach (var a in info.platforms)
        {
            var d = B(a) ? a.variantB : a.def;
            var baked = string.IsNullOrEmpty(a.obstacleId);
            if (baked)
                d.Phase16 = HazardMath.PhaseFromSeed(a.seed);
            var k = d.KnotsMm ?? Array.Empty<int>();
            if (k.Length > 24)
                throw new InvalidOperationException($"{a.name}: more than 8 platform knots");
            var w = new List<int> { d.CenterXMm, d.CenterYMm, d.CenterZMm, d.HalfXMm, d.HalfYMm, d.HalfZMm, d.TravelXMm, d.TravelYMm, d.TravelZMm, d.PeriodTicks, d.Phase16, k.Length >= 6 ? k.Length / 3 : 0 };
            for (var i = 0; i < 24; i++)
                w.Add(i < k.Length ? k[i] : 0);
            Add(a, w.ToArray(), baked);
        }

        foreach (var a in info.conveyors)
        {
            var d = B(a) ? a.variantB : a.def;
            Add(a, new[] { d.CenterXMm, d.CenterYMm, d.CenterZMm, d.HalfXMm, d.HalfYMm, d.HalfZMm, d.PitchCdeg, d.BeltXMms, d.BeltYMms, d.BeltZMms });
        }

        foreach (var a in info.pads)
        {
            var d = B(a) ? a.variantB : a.def;
            Add(a, new[] { d.CenterXMm, d.CenterYMm, d.CenterZMm, d.HalfXMm, d.HalfZMm, d.ThicknessMm, d.LaunchXCms, d.LaunchYCms, d.LaunchZCms });
        }

        foreach (var a in info.turnTables)
        {
            var d = B(a) ? a.variantB : a.def;
            Add(a, new[] { d.CenterXMm, d.CenterYMm, d.CenterZMm, d.RadiusMm, d.ThicknessMm, d.PeriodTicks, d.Phase16 });
        }

        foreach (var a in info.doors)
        {
            var d = B(a) ? a.variantB : a.def;
            Add(a, new[] { d.CenterXMm, d.CenterYMm, d.CenterZMm, d.HalfXMm, d.HalfYMm, d.HalfZMm, d.LiftMm, d.ClosedTicks, d.OpeningTicks, d.OpenTicks, d.ClosingTicks, d.OffsetTicks, d.TelegraphTicks, d.Phase16, d.Mode });
        }

        foreach (var a in info.mallets)
        {
            var d = B(a) ? a.variantB : a.def;
            Add(a, new[] { d.PivotXMm, d.PivotYMm, d.PivotZMm, d.SwingAxis, d.ArmMm, d.HeadRadiusMm, d.HeadHalfWidthMm, d.AmplitudePhase16, d.PeriodTicks, d.Phase16, d.TelegraphTicks, d.KnockbackCms, d.KnockUpCms, d.StunTicks, d.ImmunityTicks });
        }

        foreach (var a in info.bumpers)
        {
            var d = B(a) ? a.variantB : a.def;
            Add(a, new[] { d.CenterXMm, d.CenterYMm, d.CenterZMm, d.RadiusMm, d.HeightMm, d.ContactMarginMm, d.KnockbackCms, d.KnockUpCms, d.StunTicks, d.ImmunityTicks });
        }

        foreach (var a in info.goo)
        {
            var d = B(a) ? a.variantB : a.def;
            Add(a, new[] { d.Y0Mm, d.RiseMmPerSec, d.DelayTicks, d.MaxYMm, d.CenterXMm, d.CenterZMm, d.HalfXMm, d.HalfZMm, d.WarnTicks });
        }

        foreach (var a in info.grids)
        {
            var d = a.def;
            var tops = d.ShelfTopsMm ?? Array.Empty<int>();
            if (tops.Length > 4)
                throw new InvalidOperationException($"{a.name}: more than 4 shelves");
            var w = new List<int> { d.OriginXMm, d.OriginZMm, d.TileMm, d.GapMm, d.Cols, d.Rows, tops.Length };
            for (var i = 0; i < 4; i++)
                w.Add(i < tops.Length ? tops[i] : 0);
            w.AddRange(new[] { d.ThicknessMm, d.WobbleTicks, d.RespawnTicks, d.RespawnShelves });
            Add(a, w.ToArray());
        }

        foreach (var a in info.starFields)
        {
            var d = variant == 1 && a.hasVariantB ? a.variantB : a.def;
            var n = d.AnchorCount;
            if (n > 64 || (d.Golden?.Length ?? 0) > 16)
                throw new InvalidOperationException($"{a.name}: more than 64 anchors / 16 golden");
            var w = new List<int> { n };
            for (var i = 0; i < 64 * 3; i++)
                w.Add(i < n * 3 ? d.AnchorsMm[i] : 0);
            for (var i = 0; i < 64; i++)
                w.Add(d.Attach != null && i < d.Attach.Length ? d.Attach[i] : -1);
            var g = d.Golden ?? Array.Empty<int>();
            w.Add(g.Length);
            for (var i = 0; i < 16; i++)
                w.Add(i < g.Length ? g[i] : 0);
            w.AddRange(new[] { d.WaveEveryTicks, d.WaveSize, d.WaveLifeTicks, d.GoldenEveryTicks, d.GoldenLifeTicks, d.GoldenValue, d.PickupRadiusMm, d.DropPopTicks, d.DropLifeTicks, d.DropDistanceMm });
            Add(a, w.ToArray());
        }

        foreach (var a in info.fallingBlocks)
        {
            var d = a.def;
            Add(a, new[] { d.CenterXMm, d.CenterYMm, d.CenterZMm, d.HalfXMm, d.HalfYMm, d.HalfZMm, d.DelayTicks, d.GoneTicks });
        }

        foreach (var a in info.rollingBalls)
        {
            var d = B(a) ? a.variantB : a.def;
            Add(a, new[] { d.StartXMm, d.StartYMm, d.StartZMm, d.EndXMm, d.EndYMm, d.EndZMm, d.RadiusMm, d.SpeedMmS, d.PeriodTicks, d.Phase16, d.TelegraphTicks, d.KnockbackCms, d.KnockUpCms, d.StunTicks, d.ImmunityTicks });
        }

        foreach (var a in info.lidGrids)
        {
            var d = B(a) ? a.variantB : a.def;
            var w = new List<int> { d.OriginXMm, d.OriginZMm, d.TopMm, d.N, d.LidMm, d.GapMm, d.ThicknessMm, d.LitTicks, d.FlippedTicks, d.FirstWaveTicks };
            void Arr(int[] x)
            {
                x ??= Array.Empty<int>();
                if (x.Length > 8)
                    throw new InvalidOperationException($"{a.name}: more than 8 stages");
                w.Add(x.Length);
                for (var i = 0; i < 8; i++)
                    w.Add(i < x.Length ? x[i] : 0);
            }

            Arr(d.WaveIntervals);
            w.Add(d.WavesPerStage);
            Arr(d.FlipPermille);
            Arr(d.RingRetireTicks);
            w.AddRange(new[] { d.RetireWarnTicks, d.MinInnerSolid, d.LoopTicks });
            Add(a, w.ToArray());
        }

        return "[" + (list.Count == 0 ? "" : "\n") + string.Join(",\n", list) + "]";
    }

    /// <summary>BOT_ROUTES words: route count, then per route: section, point count, then per point x, y, z, radius, action, arg.</summary>
    private static string BotRouteWords(MapInfo info)
    {
        var w = new List<int>();
        var routes = info.botRoutes ?? Array.Empty<BotRoute>();
        w.Add(routes.Length);
        foreach (var r in routes)
        {
            var pts = r.Points ?? Array.Empty<Waypoint>();
            w.Add(r.Section);
            w.Add(pts.Length);
            foreach (var p in pts)
                w.AddRange(new[] { p.XMm, p.YMm, p.ZMm, p.RadiusMm, (int)p.Action, p.Arg });
        }

        return string.Join(",", w);
    }

    // ------------------------------------------------------------------ shapes

    private static int Mm(float m) => MotorState.QuantizeMm(m);

    private static int Q(float v) => HazardMath.RoundMm(v * Q15);

    private static string V(Vector3 p) => $"[{Mm(p.x)},{Mm(p.y)},{Mm(p.z)}]";

    private static string Ax(Vector3 a) => $"[{Q(a.x)},{Q(a.y)},{Q(a.z)}]";

    private static string Obb(BoxCollider b, Vector3 rel)
    {
        var t = b.transform;
        var c = t.TransformPoint(b.center) - rel;
        var s = t.lossyScale;
        var half = new Vector3(Mathf.Abs(b.size.x * s.x), Mathf.Abs(b.size.y * s.y), Mathf.Abs(b.size.z * s.z)) * 0.5f;
        var r = t.rotation;
        return $"\"shape\":\"obb\",\"center\":{V(c)},\"half\":{V(half)},\"axes\":[{Ax(r * Vector3.right)},{Ax(r * Vector3.up)},{Ax(r * Vector3.forward)}]";
    }

    private static string Prism(MeshCollider m, Vector3 rel)
    {
        if (!m.convex || m.sharedMesh == null)
            throw new InvalidOperationException($"{HPath(m.transform)}: MeshCollider must be convex with a mesh");
        var verts = m.sharedMesh.vertices;
        var t = m.transform;
        var ys = new SortedSet<int>();
        var pts = new List<Vector3>();
        foreach (var v in verts)
        {
            var w = t.TransformPoint(v) - rel;
            pts.Add(w);
            ys.Add(Mm(w.y));
        }

        // Vertical prism: every vertex on one of two levels (±1 mm), and the same XZ outline on both.
        var lo = int.MaxValue;
        var hi = int.MinValue;
        foreach (var y in ys)
        {
            lo = Math.Min(lo, y);
            hi = Math.Max(hi, y);
        }

        var bottom = new HashSet<(int, int)>();
        var top = new HashSet<(int, int)>();
        foreach (var p in pts)
        {
            var y = Mm(p.y);
            var xz = (Mm(p.x), Mm(p.z));
            if (Math.Abs(y - lo) <= 1)
                bottom.Add(xz);
            else if (Math.Abs(y - hi) <= 1)
                top.Add(xz);
            else
                throw new InvalidOperationException($"{HPath(m.transform)}: not a vertical prism (vertex at y {y} between {lo} and {hi})");
        }

        foreach (var xz in bottom)
            if (!Near(top, xz))
                throw new InvalidOperationException($"{HPath(m.transform)}: not a vertical prism (bottom vertex {xz} has no top twin)");
        var sb = new StringBuilder("\"shape\":\"prism\",\"verts\":[");
        var first = true;
        var sorted = new List<(int, int)>(bottom);
        sorted.Sort();
        foreach (var (x, z) in sorted)
        {
            sb.Append(first ? "" : ",").Append('[').Append(x).Append(',').Append(z).Append(']');
            first = false;
        }

        return sb.Append("],\"y0\":").Append(lo).Append(",\"y1\":").Append(hi).ToString();
    }

    private static bool Near(HashSet<(int, int)> set, (int, int) p)
    {
        for (var dx = -1; dx <= 1; dx++)
            for (var dz = -1; dz <= 1; dz++)
                if (set.Contains((p.Item1 + dx, p.Item2 + dz)))
                    return true;
        return false;
    }

    private static string Sphere(SphereCollider s, Vector3 rel)
    {
        var t = s.transform;
        var sc = t.lossyScale;
        var r = s.radius * Mathf.Max(Mathf.Abs(sc.x), Mathf.Max(Mathf.Abs(sc.y), Mathf.Abs(sc.z)));
        return $"\"shape\":\"sphere\",\"center\":{V(t.TransformPoint(s.center) - rel)},\"radius\":{Mm(r)}";
    }

    private static string Capsule(CapsuleCollider c, Vector3 rel)
    {
        var t = c.transform;
        var up = t.TransformDirection(c.direction == 0 ? Vector3.right : c.direction == 1 ? Vector3.up : Vector3.forward);
        if (Mathf.Abs(Mathf.Abs(up.y) - 1f) > 1e-4f)
            throw new InvalidOperationException($"{HPath(t)}: only vertical capsules are supported");
        var sc = t.lossyScale;
        var r = c.radius * Mathf.Max(Mathf.Abs(sc.x), Mathf.Abs(sc.z));
        var hh = Mathf.Max(0f, c.height * Mathf.Abs(sc.y) * 0.5f - r);
        var mid = t.TransformPoint(c.center) - rel;
        return $"\"shape\":\"capsule\",\"a\":{V(mid - Vector3.up * hh)},\"b\":{V(mid + Vector3.up * hh)},\"radius\":{Mm(r)}";
    }

    private static int ModeId(string mode, byte mapId)
    {
        if (mapId == MapPoolV1.Lobby)
            return 5;
        return mode switch
        {
            "Race" => 1,
            "Survival" => 2,
            "Skill" => 3,
            "Final" => 4,
            _ => 0,
        };
    }

    private static string HPath(Transform t) => t.parent == null ? t.name : HPath(t.parent) + "/" + t.name;

    private static string Str(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
