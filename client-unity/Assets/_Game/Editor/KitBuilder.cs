using FallRush.Net;
using FallRush.Shared;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Code-built greybox pieces of the M3 obstacle kit, shared by <see cref="SandboxBuilder"/> (kit row) and
/// <see cref="BlockPartyDashBuilder"/>. Every motor-relevant collider is on the <c>Gameplay</c> layer; every visual mesh
/// is on layer 0 with no collider (collision = visuals, doc 08 §1.2). Hazards that hurt are yellow/magenta striped
/// (doc 17 §3.1); walkable surfaces are light; goals are mint.
/// </summary>
public static class KitBuilder
{
    public const string MeshDir = ProjectSetup.Root + "/Meshes";
    public static int Layer;
    public static Material Walk, Feature, Mint, HazardY, HazardM, Platform, Belt, BeltArrow, Spring, Grape, Door, Checker, Ink;
    private static Mesh _prism;
    private static Font _font;

    public static void Init()
    {
        Layer = LayerMask.NameToLayer(PhysxMotorWorld.GameplayLayerName);
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var dir = SandboxBuilder.MaterialsDir;
        Walk = Mat(dir, "Walkable", new Color(0.91f, 0.93f, 0.97f));
        Feature = Mat(dir, "Feature", new Color(0.80f, 0.88f, 0.97f));
        Mint = Mat(dir, "SpawnMint", Hex(0x5BE3B5));
        HazardY = Mat(dir, "HazardYellow", Hex(0xFFD21F));
        HazardM = Mat(dir, "HazardMagenta", Hex(0xFF3EA5));
        Platform = Mat(dir, "Platform", new Color(0.86f, 0.95f, 0.93f));
        Belt = Mat(dir, "Belt", new Color(0.62f, 0.66f, 0.78f));
        BeltArrow = Mat(dir, "BeltArrow", new Color(0.96f, 0.97f, 1f));
        Spring = Mat(dir, "Spring", Hex(0x5BE3B5));
        Grape = Mat(dir, "Grape", Hex(0x7B2FF7));
        Door = Mat(dir, "Door", new Color(0.97f, 0.88f, 0.55f));
        Checker = Mat(dir, "Checker", new Color(0.16f, 0.07f, 0.31f));
        Ink = Mat(dir, "Ink", new Color(0.16f, 0.07f, 0.31f));
        _prism = PrismMesh();
    }

    public static Color Hex(int rgb) => new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);

    public static int Mm(float m) => Mathf.RoundToInt(m * 1000f);

    private static Material Mat(string dir, string name, Color color)
    {
        var path = $"{dir}/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(m, path);
        }

        m.SetColor("_BaseColor", color);
        m.SetFloat("_Smoothness", 0.15f);
        m.enableInstancing = true;
        EditorUtility.SetDirty(m);
        return m;
    }

    /// <summary>A 32-sided unit prism (radius 1, top at y = 0, bottom at y = −1), saved as an asset (turn tables, bumpers).</summary>
    private static Mesh PrismMesh()
    {
        const string path = MeshDir + "/Prism32.asset";
        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (existing != null)
            return existing;
        if (!AssetDatabase.IsValidFolder(MeshDir))
            AssetDatabase.CreateFolder(ProjectSetup.Root, "Meshes");
        const int n = 32;
        var v = new System.Collections.Generic.List<Vector3>();
        var nr = new System.Collections.Generic.List<Vector3>();
        var tr = new System.Collections.Generic.List<int>();
        // Top and bottom caps.
        for (var cap = 0; cap < 2; cap++)
        {
            var y = cap == 0 ? 0f : -1f;
            var c = v.Count;
            v.Add(new Vector3(0, y, 0));
            nr.Add(cap == 0 ? Vector3.up : Vector3.down);
            for (var i = 0; i < n; i++)
            {
                var a = i * Mathf.PI * 2f / n;
                v.Add(new Vector3(Mathf.Cos(a), y, Mathf.Sin(a)));
                nr.Add(cap == 0 ? Vector3.up : Vector3.down);
            }

            for (var i = 0; i < n; i++)
            {
                var a = c + 1 + i;
                var b = c + 1 + (i + 1) % n;
                if (cap == 0) { tr.Add(c); tr.Add(b); tr.Add(a); }
                else { tr.Add(c); tr.Add(a); tr.Add(b); }
            }
        }

        // Sides (flat-shaded quads).
        for (var i = 0; i < n; i++)
        {
            var a0 = i * Mathf.PI * 2f / n;
            var a1 = (i + 1) * Mathf.PI * 2f / n;
            var p0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0));
            var p1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
            var nn = ((p0 + p1) * 0.5f).normalized;
            var c = v.Count;
            v.Add(p0); v.Add(p1); v.Add(p1 + Vector3.down); v.Add(p0 + Vector3.down);
            for (var k = 0; k < 4; k++)
                nr.Add(nn);
            tr.Add(c); tr.Add(c + 1); tr.Add(c + 2);
            tr.Add(c); tr.Add(c + 2); tr.Add(c + 3);
        }

        var mesh = new Mesh { name = "Prism32" };
        mesh.SetVertices(v);
        mesh.SetNormals(nr);
        mesh.SetTriangles(tr, 0);
        mesh.RecalculateBounds();
        AssetDatabase.CreateAsset(mesh, path);
        return mesh;
    }

    // ------------------------------------------------------------------ primitives

    public static GameObject Box(Transform parent, string name, Vector3 center, Vector3 size, Material mat, Quaternion? rot = null)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.layer = Layer;
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(center, rot ?? Quaternion.identity);
        go.transform.localScale = size;
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        go.isStatic = true;
        return go;
    }

    /// <summary>Render-only box (no collider, layer 0).</summary>
    public static GameObject Visual(Transform parent, string name, Vector3 center, Vector3 size, Material mat, Quaternion? rot = null)
    {
        var go = Box(parent, name, center, size, mat, rot);
        Object.DestroyImmediate(go.GetComponent<BoxCollider>());
        go.layer = 0;
        go.isStatic = false;
        return go;
    }

    /// <summary>Render-only box in local coordinates of <paramref name="parent"/>.</summary>
    public static GameObject LocalVisual(Transform parent, string name, Vector3 localPos, Vector3 size, Material mat, Quaternion? localRot = null)
    {
        var go = Visual(parent, name, Vector3.zero, size, mat);
        go.transform.localPosition = localPos;
        go.transform.localRotation = localRot ?? Quaternion.identity;
        return go;
    }

    public static GameObject Prism(Transform parent, string name, Vector3 top, float radius, float height, Material mat, bool collider)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.position = top;
        go.transform.localScale = new Vector3(radius, height, radius);
        go.AddComponent<MeshFilter>().sharedMesh = _prism;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        if (collider)
        {
            go.layer = Layer;
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = _prism;
            mc.convex = true;
            go.isStatic = true;
        }

        return go;
    }

    /// <summary>
    /// Developer label (section names, tuning notes). G1: tagged <c>EditorOnly</c>, so Unity strips it from every player
    /// build (WebGL, servers); it still shows in the Editor while building maps. Player-facing text uses <see cref="Sign"/>.
    /// </summary>
    public static void Label(Transform parent, string text, Vector3 pos, float size = 0.035f)
    {
        var go = MakeText(parent, text, pos, size);
        go.tag = "EditorOnly";
    }

    /// <summary>A player-facing in-world sign (map title, CHECKPOINT, FINISH): kept in builds. G2 restyles these.</summary>
    public static void Sign(Transform parent, string text, Vector3 pos, float size = 0.035f) => MakeText(parent, text, pos, size);

    private static GameObject MakeText(Transform parent, string text, Vector3 pos, float size)
    {
        var go = new GameObject($"Label_{text}");
        go.transform.SetParent(parent, false);
        go.transform.position = pos;
        var tm = go.AddComponent<TextMesh>();
        tm.text = text;
        tm.font = _font;
        tm.fontSize = 96;
        tm.characterSize = size;
        tm.fontStyle = FontStyle.Bold;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.color = new Color(0.16f, 0.07f, 0.31f);
        go.GetComponent<MeshRenderer>().sharedMaterial = _font.material;
        return go;
    }

    /// <summary>Yellow/magenta striped visual bar along local +Z (hazard stripes, doc 17 §3.1).</summary>
    public static void StripedBar(Transform parent, float length, float thickness, int segments)
    {
        for (var i = 0; i < segments; i++)
        {
            var z = -length * 0.5f + (i + 0.5f) * (length / segments);
            LocalVisual(parent, $"Seg_{i:00}", new Vector3(0f, 0f, z), new Vector3(thickness, thickness, length / segments), i % 2 == 0 ? HazardY : HazardM);
        }
    }

    // ------------------------------------------------------------------ hazards

    public static HazardSweeper Spinner(Transform hz, Transform geo, string id, Vector3 pivot, float arm, float barHeight, int periodTicks, int periodB, int phaseMin = 0, int phaseMax = 65535)
    {
        var root = new GameObject($"Spinner_{id}").transform;
        root.SetParent(hz, false);
        root.position = pivot;
        var sw = root.gameObject.AddComponent<HazardSweeper>();
        sw.obstacleId = id;
        sw.phaseMin16 = phaseMin;
        sw.phaseMax16 = phaseMax;
        sw.def = new SweeperDef
        {
            PivotXMm = Mm(pivot.x), PivotYMm = Mm(pivot.y), PivotZMm = Mm(pivot.z), ArmMm = Mm(arm), BarRadiusMm = 150, BarHeightMm = Mm(barHeight),
            PeriodTicks = periodTicks, Tangential = 1,
            KnockbackCms = KnockProfile.SoftPushCms, KnockUpCms = KnockProfile.SoftUpCms, StunTicks = KnockProfile.SoftStunTicks,
            ImmunityTicks = KnockProfile.ImmunityFor(KnockProfile.SoftStunTicks),
        };
        sw.variantB = sw.def;
        sw.variantB.PeriodTicks = periodB;
        sw.hasVariantB = periodB != periodTicks;
        // The post is static geometry; the bar is analytic (no collider). Post height clears the bar.
        Box(geo, $"SpinnerPost_{id}", pivot + new Vector3(0f, (barHeight + 0.4f) * 0.5f, 0f), new Vector3(0.6f, barHeight + 0.4f, 0.6f), HazardM);
        var bar = new GameObject("BarVisual").transform;
        bar.SetParent(root, false);
        bar.localPosition = new Vector3(0f, barHeight, 0f);
        StripedBar(bar, arm * 2f, 0.3f, Mathf.Max(6, Mathf.RoundToInt(arm * 2.6f)));
        sw.barVisual = bar;
        return sw;
    }

    /// <summary>Loop platform (closed Catmull-Rom through the knots, relative to <paramref name="center"/>).</summary>
    public static HazardPlatform LoopPlatform(Transform hz, string id, Vector3 center, Vector3 size, Vector3[] knotsA, Vector3[] knotsB, int periodTicks)
    {
        var root = new GameObject($"Platform_{id}").transform;
        root.SetParent(hz, false);
        var pl = root.gameObject.AddComponent<HazardPlatform>();
        pl.obstacleId = id;
        pl.def = new PlatformDef
        {
            CenterXMm = Mm(center.x), CenterYMm = Mm(center.y), CenterZMm = Mm(center.z),
            HalfXMm = Mm(size.x * 0.5f), HalfYMm = Mm(size.y * 0.5f), HalfZMm = Mm(size.z * 0.5f),
            PeriodTicks = periodTicks, KnotsMm = Knots(knotsA),
        };
        pl.variantB = pl.def;
        pl.variantB.KnotsMm = Knots(knotsB ?? knotsA);
        pl.hasVariantB = knotsB != null;

        var col = new GameObject("MotorCollider");
        col.layer = Layer;
        col.transform.SetParent(root, false);
        col.transform.position = center;
        var bc = col.AddComponent<BoxCollider>();
        bc.size = size;
        var rb = col.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;

        var vis = Visual(root, "Visual", center, size, Platform);
        var rim = LocalVisual(vis.transform, "MintRim", new Vector3(0f, 0.505f, 0f), new Vector3(0.9f, 0.01f, 0.9f), Mint);
        rim.transform.localScale = new Vector3(0.9f, 0.01f, 0.9f);
        pl.motorCollider = bc;
        pl.visual = vis.transform;
        return pl;
    }

    private static int[] Knots(Vector3[] k)
    {
        var r = new int[k.Length * 3];
        for (var i = 0; i < k.Length; i++)
        {
            r[i * 3] = Mm(k[i].x);
            r[i * 3 + 1] = Mm(k[i].y);
            r[i * 3 + 2] = Mm(k[i].z);
        }

        return r;
    }

    /// <summary>Belt box (top face is the belt) moving riders at <paramref name="beltMs"/> (m/s, along the surface). <paramref name="pitchDeg"/> tilts it about X (ramp).</summary>
    public static HazardConveyor Conveyor(Transform hz, string id, Vector3 center, Vector3 size, Vector3 beltMs, float pitchDeg = 0f)
    {
        var root = new GameObject($"Conveyor_{id}").transform;
        root.SetParent(hz, false);
        var cv = root.gameObject.AddComponent<HazardConveyor>();
        cv.obstacleId = id;
        cv.phaseMin16 = cv.phaseMax16 = 0;
        cv.def = new ConveyorDef
        {
            CenterXMm = Mm(center.x), CenterYMm = Mm(center.y), CenterZMm = Mm(center.z),
            HalfXMm = Mm(size.x * 0.5f), HalfYMm = Mm(size.y * 0.5f), HalfZMm = Mm(size.z * 0.5f), PitchCdeg = Mathf.RoundToInt(pitchDeg * 100f),
            BeltXMms = Mm(beltMs.x), BeltYMms = Mm(beltMs.y), BeltZMms = Mm(beltMs.z),
        };
        cv.variantB = cv.def;
        var rot = Quaternion.Euler(-pitchDeg, 0f, 0f);
        var col = Box(root, "MotorCollider", center, size, Belt, rot);
        col.isStatic = true;
        cv.motorCollider = col.GetComponent<BoxCollider>();
        // Visual chevrons on the belt (moved by the client along the belt direction).
        var vis = new GameObject("Chevrons").transform;
        vis.SetParent(root, false);
        vis.SetPositionAndRotation(center + rot * new Vector3(0f, size.y * 0.5f + 0.01f, 0f), rot);
        var dir = Quaternion.Inverse(rot) * beltMs;
        var flat = new Vector3(dir.x, 0f, dir.z);
        var yaw = flat.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(flat.normalized, Vector3.up) : Quaternion.identity;
        var along = flat.sqrMagnitude > 1e-6f ? Mathf.Abs(Vector3.Dot(flat.normalized, Vector3.right)) > 0.5f ? size.x : size.z : size.z;
        var across = Mathf.Abs(Vector3.Dot(flat.normalized, Vector3.right)) > 0.5f ? size.z : size.x;
        var n = Mathf.Max(2, Mathf.RoundToInt(along / 1.5f));
        for (var i = 0; i < n; i++)
        {
            var off = yaw * new Vector3(0f, 0f, -along * 0.5f + (i + 0.5f) * along / n);
            LocalVisual(vis, $"Chevron_{i:00}_L", off + yaw * new Vector3(-across * 0.18f, 0f, 0f), new Vector3(0.18f, 0.02f, across * 0.42f), BeltArrow, yaw * Quaternion.Euler(0f, 45f, 0f));
            LocalVisual(vis, $"Chevron_{i:00}_R", off + yaw * new Vector3(across * 0.18f, 0f, 0f), new Vector3(0.18f, 0.02f, across * 0.42f), BeltArrow, yaw * Quaternion.Euler(0f, -45f, 0f));
        }

        cv.visual = vis;
        return cv;
    }

    public static HazardSpringPad SpringPad(Transform hz, string id, Vector3 top, Vector2 size, Vector3 launchMs)
    {
        var root = new GameObject($"SpringPad_{id}").transform;
        root.SetParent(hz, false);
        var sp = root.gameObject.AddComponent<HazardSpringPad>();
        sp.obstacleId = id;
        sp.phaseMin16 = sp.phaseMax16 = 0;
        const float thick = 0.3f;
        sp.def = new SpringPadDef
        {
            CenterXMm = Mm(top.x), CenterYMm = Mm(top.y), CenterZMm = Mm(top.z), HalfXMm = Mm(size.x * 0.5f), HalfZMm = Mm(size.y * 0.5f), ThicknessMm = Mm(thick),
            LaunchXCms = Mathf.RoundToInt(launchMs.x * 100f), LaunchYCms = Mathf.RoundToInt(launchMs.y * 100f), LaunchZCms = Mathf.RoundToInt(launchMs.z * 100f),
        };
        sp.variantB = sp.def;
        var col = Box(root, "MotorCollider", top - new Vector3(0f, thick * 0.5f, 0f), new Vector3(size.x, thick, size.y), Spring);
        sp.motorCollider = col.GetComponent<BoxCollider>();
        var vis = new GameObject("Arrow").transform;
        vis.SetParent(root, false);
        vis.position = top + new Vector3(0f, 0.02f, 0f);
        var flat = new Vector3(launchMs.x, 0f, launchMs.z);
        var yaw = flat.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(flat.normalized) : Quaternion.identity;
        LocalVisual(vis, "ArrowL", yaw * new Vector3(-0.18f, 0f, 0f), new Vector3(0.14f, 0.03f, size.y * 0.5f), HazardY, yaw * Quaternion.Euler(0f, 40f, 0f));
        LocalVisual(vis, "ArrowR", yaw * new Vector3(0.18f, 0f, 0f), new Vector3(0.14f, 0.03f, size.y * 0.5f), HazardY, yaw * Quaternion.Euler(0f, -40f, 0f));
        sp.visual = vis;
        return sp;
    }

    public static HazardTurnTable TurnTable(Transform hz, string id, Vector3 top, float radius, int periodTicks, int periodB)
    {
        var root = new GameObject($"TurnTable_{id}").transform;
        root.SetParent(hz, false);
        var tt = root.gameObject.AddComponent<HazardTurnTable>();
        tt.obstacleId = id;
        const float thick = 0.4f;
        tt.def = new TurnTableDef { CenterXMm = Mm(top.x), CenterYMm = Mm(top.y), CenterZMm = Mm(top.z), RadiusMm = Mm(radius), ThicknessMm = Mm(thick), PeriodTicks = periodTicks };
        tt.variantB = tt.def;
        tt.variantB.PeriodTicks = periodB;
        tt.hasVariantB = periodB != periodTicks;
        // Collider: a static round prism (rotation never changes its shape); no renderer on it.
        var col = Prism(root, "MotorCollider", top, radius, thick, Walk, collider: true);
        Object.DestroyImmediate(col.GetComponent<MeshRenderer>());
        Object.DestroyImmediate(col.GetComponent<MeshFilter>());
        tt.motorCollider = col.GetComponent<MeshCollider>();
        // Visual: same prism + spokes, rotated by the client.
        var vis = new GameObject("Visual").transform;
        vis.SetParent(root, false);
        vis.position = top;
        var disc = Prism(vis, "Disc", top, radius, thick, Feature, collider: false);
        disc.layer = 0;
        for (var i = 0; i < 4; i++)
            LocalVisual(vis, $"Spoke_{i}", new Vector3(0f, 0.012f, 0f), new Vector3(0.25f, 0.02f, radius * 1.9f), i % 2 == 0 ? Mint : Grape, Quaternion.Euler(0f, i * 45f, 0f));
        tt.visual = vis;
        return tt;
    }

    /// <summary>A lifting door in a doorway (closed pose centre <paramref name="center"/>, size = the opening). Normal axis = the thinner of X/Z.</summary>
    public static HazardGateDoor GateDoor(Transform hz, string id, Vector3 center, Vector3 size, int closed, int opening, int open, int closing, int offsetA, int offsetB, float lift = 2.9f)
    {
        var root = new GameObject($"GateDoor_{id}").transform;
        root.SetParent(hz, false);
        var gd = root.gameObject.AddComponent<HazardGateDoor>();
        gd.obstacleId = id;
        gd.phaseMin16 = gd.phaseMax16 = 0;
        gd.def = new GateDoorDef
        {
            CenterXMm = Mm(center.x), CenterYMm = Mm(center.y), CenterZMm = Mm(center.z), HalfXMm = Mm(size.x * 0.5f), HalfYMm = Mm(size.y * 0.5f), HalfZMm = Mm(size.z * 0.5f),
            LiftMm = Mm(lift), ClosedTicks = closed, OpeningTicks = opening, OpenTicks = open, ClosingTicks = closing, OffsetTicks = offsetA, TelegraphTicks = 15,
        };
        gd.variantB = gd.def;
        gd.variantB.OffsetTicks = offsetB;
        gd.hasVariantB = offsetB != offsetA;
        var col = new GameObject("MotorCollider");
        col.layer = Layer;
        col.transform.SetParent(root, false);
        col.transform.position = center;
        var bc = col.AddComponent<BoxCollider>();
        bc.size = size;
        var rb = col.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;
        gd.motorCollider = bc;
        var vis = Visual(root, "Visual", center, size, Door);
        // Stripes on both faces (they flash during the closing telegraph).
        var thinX = size.x <= size.z;
        for (var i = 0; i < 4; i++)
        {
            var off = (i - 1.5f) / 4f;
            var stripe = LocalVisual(vis.transform, $"Stripe_{i}", thinX ? new Vector3(0f, off, 0f) : new Vector3(0f, off, 0f), thinX ? new Vector3(1.04f, 0.1f, 1f) : new Vector3(1f, 0.1f, 1.04f), i % 2 == 0 ? HazardY : HazardM);
            stripe.transform.localScale = thinX ? new Vector3(1.04f, 0.1f, 1f) : new Vector3(1f, 0.1f, 1.04f);
        }

        gd.visual = vis.transform;
        return gd;
    }

    public static HazardMallet Mallet(Transform hz, Transform geo, string id, Vector3 pivot, int swingAxis, float arm, float ampDeg, int periodTicks, int periodB)
    {
        var root = new GameObject($"Mallet_{id}").transform;
        root.SetParent(hz, false);
        root.position = pivot;
        var ml = root.gameObject.AddComponent<HazardMallet>();
        ml.obstacleId = id;
        ml.def = new MalletDef
        {
            PivotXMm = Mm(pivot.x), PivotYMm = Mm(pivot.y), PivotZMm = Mm(pivot.z), SwingAxis = swingAxis, ArmMm = Mm(arm), HeadRadiusMm = 450, HeadHalfWidthMm = 750,
            AmplitudePhase16 = Mathf.RoundToInt(ampDeg / 360f * 65536f), PeriodTicks = periodTicks, TelegraphTicks = 15,
            KnockbackCms = KnockProfile.HardPushCms, KnockUpCms = KnockProfile.HardUpCms, StunTicks = KnockProfile.HardStunTicks,
            ImmunityTicks = KnockProfile.ImmunityFor(KnockProfile.HardStunTicks),
        };
        ml.variantB = ml.def;
        ml.variantB.PeriodTicks = periodB;
        ml.hasVariantB = periodB != periodTicks;
        // Frame: two posts and a beam (static, out of the swing).
        var across = swingAxis == 0 ? Vector3.forward : Vector3.right;
        Box(geo, $"MalletPostA_{id}", new Vector3(pivot.x, pivot.y * 0.5f, pivot.z) + across * 1.6f, new Vector3(0.4f, pivot.y, 0.4f), Grape);
        Box(geo, $"MalletPostB_{id}", new Vector3(pivot.x, pivot.y * 0.5f, pivot.z) - across * 1.6f, new Vector3(0.4f, pivot.y, 0.4f), Grape);
        Visual(geo, $"MalletBeam_{id}", pivot + Vector3.up * 0.2f, swingAxis == 0 ? new Vector3(0.4f, 0.4f, 3.6f) : new Vector3(3.6f, 0.4f, 0.4f), Grape);
        // Visual: a pivot transform rotated by the client; the handle and head hang below it.
        var swing = new GameObject("SwingVisual").transform;
        swing.SetParent(root, false);
        swing.localPosition = Vector3.zero;
        LocalVisual(swing, "Handle", new Vector3(0f, -arm * 0.5f, 0f), new Vector3(0.18f, arm, 0.18f), Grape);
        var head = new GameObject("Head").transform;
        head.SetParent(swing, false);
        head.localPosition = new Vector3(0f, -arm, 0f);
        // Head cylinder axis is across the swing (as a long box with striped bands).
        var headSize = swingAxis == 0 ? new Vector3(0.9f, 0.9f, 1.5f) : new Vector3(1.5f, 0.9f, 0.9f);
        LocalVisual(head, "HeadBody", Vector3.zero, headSize, HazardM);
        for (var i = 0; i < 3; i++)
        {
            var o = (i - 1) * 0.5f;
            LocalVisual(head, $"HeadBand_{i}", swingAxis == 0 ? new Vector3(0f, 0f, o) : new Vector3(o, 0f, 0f), swingAxis == 0 ? new Vector3(0.94f, 0.94f, 0.16f) : new Vector3(0.16f, 0.94f, 0.94f), HazardY);
        }

        ml.visual = swing;
        return ml;
    }

    public static HazardBumper Bumper(Transform hz, string id, Vector3 basePos, float radius, float height)
    {
        var root = new GameObject($"Bumper_{id}").transform;
        root.SetParent(hz, false);
        var bm = root.gameObject.AddComponent<HazardBumper>();
        bm.obstacleId = id;
        bm.phaseMin16 = bm.phaseMax16 = 0;
        bm.def = new BumperDef
        {
            CenterXMm = Mm(basePos.x), CenterYMm = Mm(basePos.y), CenterZMm = Mm(basePos.z), RadiusMm = Mm(radius), HeightMm = Mm(height), ContactMarginMm = 40,
            KnockbackCms = KnockProfile.SoftPushCms, KnockUpCms = KnockProfile.SoftUpCms, StunTicks = KnockProfile.SoftStunTicks,
            ImmunityTicks = KnockProfile.ImmunityFor(KnockProfile.SoftStunTicks),
        };
        bm.variantB = bm.def;
        var col = Prism(root, "MotorCollider", basePos + Vector3.up * height, radius, height, HazardM, collider: true);
        bm.motorCollider = col.GetComponent<MeshCollider>();
        var vis = new GameObject("Visual").transform;
        vis.SetParent(root, false);
        vis.position = basePos;
        // Striped bands (visual only, slightly larger than the collider).
        for (var i = 0; i < 2; i++)
        {
            var band = Prism(vis, $"Band_{i}", basePos + Vector3.up * (height * (0.35f + i * 0.4f)), radius * 1.02f, 0.12f, HazardY, collider: false);
            band.layer = 0;
        }

        bm.visual = vis;
        return bm;
    }
}
