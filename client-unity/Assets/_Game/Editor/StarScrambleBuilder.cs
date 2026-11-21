using System.Collections.Generic;
using FallRush.Net;
using FallRush.Shared;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds <c>Map_StarScramble</c> as <b>Star Fountain</b> (key <c>StarScramble</c>, map id 2, the Skill round; G4.4,
/// 2026-11-20, doc 08 §6). A round toy arena (r 18 m, a 0.3 m rim lip) with a capsule-machine fountain in the middle that
/// bursts a wave of 8 stars every 8 s onto a 9 m turntable round it and onto the outer floor; three low sweepers on the
/// outer ring knock a carried star out of you; three moving islands beyond the rim (reached by mint springs) hold the
/// golden star (+3) every 15 s. Code-built and re-runnable. No in-world text.
/// <list type="bullet">
/// <item><c>ss_fountain</c>: a static column r 1.4 m, 2.6 m tall with a capsule dome (out of hop reach), a crown the
/// client lights 1 s before each wave; the burst arcs (crown → each new star) are view-only (<see cref="StarBurst"/>).</item>
/// <item><c>ss_table</c>: TURN_TABLE r 9 m, top 0.2 m, 900 ticks per turn (rim 1.9 m/s), A clockwise / B the other way.</item>
/// <item><c>ss_sweep_{0..2}</c>: SPINNERs pivoted at r 13.5 m (0°, 120°, 240°), arm 3.5 m, bar 0.35 m (jump it); B: the
/// middle one turns the other way.</item>
/// <item><c>ss_island_{0..2}</c>: 4 × 4 m islands at r 22 m (60°, 180°, 300°; top 0.8 m), A slide along the rim, B in and
/// out; <c>ss_pad_{0..2}</c>: springs at r 15.5 m in front of each island.</item>
/// <item>Stars (<c>ss_stars</c>): 16 anchors on the table (rings r 3.5 / 6.5, riding it: <c>Attach = 1000</c>), 6 on the
/// outer floor at the sweepers' flanks, 3 golden (one per island). B rotates the anchor rings.</item>
/// </list>
/// Batch: <c>-executeMethod StarScrambleBuilder.Run</c>.
/// </summary>
public static class StarScrambleBuilder
{
    public const string ScenePath = ProjectSetup.ScenesDir + "/Map_StarScramble.unity";
    public const float KillY = -10f;
    public const float ArenaR = 18f, IslandR = 22f, TableR = 9f, TableTop = 0.2f;
    public const float FountainR = 1.4f, FountainH = 2.6f;
    public const float LipH = 0.3f, LipW = 0.3f;
    public const float SweepR = 13.5f, SweepArm = 3.5f, SweepBar = 0.35f;
    public const float PadR = 15.5f;
    public static readonly float[] SweepDeg = { 0f, 120f, 240f };
    public static readonly float[] IslandDeg = { 60f, 180f, 300f };
    /// <summary>Burst origin (the crown, world): the arcs start here.</summary>
    public static readonly Vector3 Crown = new Vector3(0f, 3.5f, 0f);

    /// <summary>
    /// G4.6 render-only section tag (doc 17 §3.1 hue table) from the builder's names: the islands, the turntable, the static
    /// centre (the inner floor disc and its ring line, r ≤ 11.2 m), else the outer ring.
    /// </summary>
    public static string SectionAt(Transform t)
    {
        for (var x = t; x != null; x = x.parent)
        {
            var n = x.name;
            if (n.Contains("ss_island"))
                return "islands";
            if (n.Contains("ss_table"))
                return "table";
            if (n == "ArenaRingIn" || n == "ArenaRing")
                return "centre";
        }

        return "outer";
    }

    // ------------------------------------------------------------------ tuning (G4.4c log in docs/proof/G4.4-star-fountain.md)
    private const int TablePeriod = 900;            // 30 s per turn, rim 1.9 m/s (TurboToys 750: 2.3 m/s; cap 4)
    private const int SweepPeriod = 90;             // 3 s per turn, tip 7.3 m/s (TurboToys 75: 8.8 m/s; cap 12). G4.4c: 105 → 90 (normal bots dropped 0.94 per round in A)
    private const int WaveLife = 180;               // a wave's stars live 6 s (M4: 16 s)
    private const int GoldenLife = 360;             // a golden star lives 12 s (every 15 s; G4.4c: 300 → 360, normal bots collected 47 % under MoonBounce in A)
    private const float PadOut = 7.0f;              // spring launch out (m/s), up 10 m/s (G4.4c: 5.8 → 7.0 → 6.5 → 7.0: launched at the pad's near edge, 5.8 landed ≈ r 19.5, short of the island; 6.5 landed on its inner edge (≈ r 20.2); 7.0 ≈ r 21, MoonBounce ≈ r 23 once the bots stopped braking in the air)
    private static readonly float[] TableRings = { 3.5f, 6.5f };
    private static readonly int[] TableRingCount = { 6, 10 };
    private static readonly float[] TableRingOffA = { 0f, 18f }, TableRingOffB = { 30f, 0f };
    private const float OuterR_A = 13f, OuterR_B = 13.5f;    // G4.4c: A 12.5 → 13
    private const float OuterFlankA = 12f, OuterFlankB = 10f; // ° either side of each sweeper (inside its sweep circle; G4.4c: 14 / 12 → 12 / 10, normal bots dropped < 1 per round in A)

    [MenuItem("Fall Rush/Setup/Build Map_StarScramble")]
    public static void Run()
    {
        KitBuilder.Init();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        LobbyBuilder.SetupCameraAndLight(new Vector3(0f, 24f, -34f), Quaternion.Euler(33f, 0f, 0f));
        var geo = new GameObject("Geometry").transform;
        var hz = new GameObject("Hazards").transform;
        var floor = LobbyBuilder.Mat("StarFloor", new Color(0.93f, 0.95f, 1f));
        var rim = LobbyBuilder.Mat("StarRim", new Color(1f, 0.82f, 0.25f));
        var body = LobbyBuilder.Mat("FountainBody", KitBuilder.Hex(0xFF6FB5));
        var dome = LobbyBuilder.Mat("FountainDome", new Color(0.82f, 0.93f, 1f));
        var crown = LobbyBuilder.Mat("FountainCrown", KitBuilder.Hex(0xFFD21F));
        var stripe = LobbyBuilder.Mat("TableStripe", KitBuilder.Hex(0xB89BFF));

        // Arena floor (top y 0) and a 0.3 m rim lip (48 segments: you hop it on purpose, you don't walk off by accident).
        KitBuilder.Prism(geo, "ArenaFloor", Vector3.zero, ArenaR, 1f, floor, collider: true);
        const int lipN = 48;
        for (var i = 0; i < lipN; i++)
        {
            var a = (i + 0.5f) * Mathf.PI * 2f / lipN;
            var r = ArenaR - LipW * 0.5f;
            var c = new Vector3(Mathf.Sin(a) * r, LipH * 0.5f, Mathf.Cos(a) * r);
            var len = 2f * ArenaR * Mathf.Sin(Mathf.PI / lipN) + 0.05f;
            KitBuilder.Box(geo, $"RimLip_{i:00}", c, new Vector3(len, LipH, LipW), rim, Quaternion.Euler(0f, a * Mathf.Rad2Deg, 0f)).isStatic = true; // local X = tangent, Z = radial
        }

        KitBuilder.Prism(geo, "ArenaRing", new Vector3(0f, 0.01f, 0f), 11.2f, 0.01f, LobbyBuilder.Mat("StarRing", new Color(0.86f, 0.92f, 1f)), collider: false).layer = 0;
        KitBuilder.Prism(geo, "ArenaRingIn", new Vector3(0f, 0.015f, 0f), 10.6f, 0.01f, floor, collider: false).layer = 0;

        // Fountain (static): a capsule-machine column, a glass capsule dome (collider: nobody stands on it; out of reach
        // anyway), a crown of capsule halves on top. The burst origin is the crown.
        var fountain = KitBuilder.Prism(geo, "ss_fountain", new Vector3(0f, FountainH, 0f), FountainR, FountainH, body, collider: true);
        fountain.isStatic = true;
        KitBuilder.Prism(geo, "FountainBand", new Vector3(0f, 0.9f, 0f), FountainR * 1.04f, 0.18f, crown, collider: false).layer = 0;
        KitBuilder.Prism(geo, "FountainBand2", new Vector3(0f, 1.8f, 0f), FountainR * 1.04f, 0.18f, crown, collider: false).layer = 0;
        var domeGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        domeGo.name = "FountainDome";
        domeGo.layer = KitBuilder.Layer;
        domeGo.transform.SetParent(geo, false);
        domeGo.transform.position = new Vector3(0f, FountainH, 0f);
        domeGo.transform.localScale = new Vector3(FountainR * 2f, FountainR * 1.3f, FountainR * 2f);
        domeGo.GetComponent<MeshRenderer>().sharedMaterial = dome;
        domeGo.isStatic = true;
        for (var i = 0; i < 6; i++)
        {
            var a = i * Mathf.PI / 3f;
            var cap = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.DestroyImmediate(cap.GetComponent<Collider>());
            cap.name = $"FountainCapsule_{i}";
            cap.transform.SetParent(geo, false);
            cap.transform.position = new Vector3(Mathf.Sin(a) * 0.55f, FountainH + 0.25f + (i % 2) * 0.2f, Mathf.Cos(a) * 0.55f);
            cap.transform.localScale = Vector3.one * 0.42f;
            cap.GetComponent<MeshRenderer>().sharedMaterial = i % 3 == 0 ? KitBuilder.Mint : i % 3 == 1 ? KitBuilder.HazardM : crown;
            cap.isStatic = true;
        }

        for (var i = 0; i < 8; i++)
        {
            var a = i * Mathf.PI / 4f;
            KitBuilder.Visual(geo, $"FountainCrown_{i}", Crown + new Vector3(Mathf.Sin(a) * 0.75f, -0.15f, Mathf.Cos(a) * 0.75f), new Vector3(0.32f, 0.36f, 0.32f), crown, Quaternion.Euler(0f, a * Mathf.Rad2Deg, 0f));
        }

        // Turntable r 9 m round the fountain (A clockwise seen from above = a negative phase rate here; B the other way).
        var table = KitBuilder.TurnTable(hz, "ss_table", new Vector3(0f, TableTop, 0f), TableR, -TablePeriod, TablePeriod);
        // Radial stripes (readable as moving): 12 bars from the fountain to the rim, under the table's visual.
        for (var i = 0; i < 12; i++)
        {
            var a = (i * 30f + 15f) * Mathf.Deg2Rad;
            var mid = (FountainR + 0.3f + TableR - 0.2f) * 0.5f;
            var len = TableR - 0.2f - FountainR - 0.3f;
            KitBuilder.LocalVisual(table.visual, $"Stripe_{i:00}", new Vector3(Mathf.Sin(a) * mid, 0.014f, Mathf.Cos(a) * mid), new Vector3(0.55f, 0.02f, len), i % 2 == 0 ? stripe : KitBuilder.Mint, Quaternion.Euler(0f, a * Mathf.Rad2Deg, 0f));
        }

        // Outer sweepers (low bars, jump them).
        var sweepers = new List<HazardSweeper>();
        for (var i = 0; i < 3; i++)
        {
            var a = SweepDeg[i] * Mathf.Deg2Rad;
            var pivot = new Vector3(Mathf.Sin(a) * SweepR, 0f, Mathf.Cos(a) * SweepR);
            var periodB = i == 1 ? -SweepPeriod : SweepPeriod; // B: alternate directions
            var sw = KitBuilder.Spinner(hz, geo, $"ss_sweep_{i}", pivot, SweepArm, SweepBar, SweepPeriod, periodB);
            sweepers.Add(sw);
        }

        // Islands beyond the rim (top 0.8 m), sliding along the rim (A) or in and out (B).
        var platforms = new List<HazardPlatform>();
        var pads = new List<HazardSpringPad>();
        for (var i = 0; i < 3; i++)
        {
            var a = IslandDeg[i] * Mathf.Deg2Rad;
            var radial = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            var tangent = new Vector3(Mathf.Cos(a), 0f, -Mathf.Sin(a));
            platforms.Add(SinePlatform(hz, $"ss_island_{i}", radial * IslandR + new Vector3(0f, 0.5f, 0f), new Vector3(4f, 0.6f, 4f), tangent * 3f, radial * 1.5f, 360, 300));
            // Spring in front of the island's middle position (PadOut out, 10 up): lands ≈ 0.5–1 m onto it under v2.
            pads.Add(KitBuilder.SpringPad(hz, $"ss_pad_{i}", radial * PadR + new Vector3(0f, 0.02f, 0f), new Vector2(1.6f, 1.6f), radial * PadOut + new Vector3(0f, 10f, 0f)));
        }

        // Star field (B rotates the anchor rings).
        var fieldRoot = new GameObject("StarField").transform;
        fieldRoot.SetParent(hz, false);
        var field = fieldRoot.gameObject.AddComponent<HazardStarField>();
        field.obstacleId = "ss_stars";
        field.phaseMin16 = field.phaseMax16 = 0;
        field.def = StarField(platforms.Count, false);
        field.variantB = StarField(platforms.Count, true);
        field.hasVariantB = true;
        field.burstOrigin = new GameObject("BurstOrigin").transform;
        field.burstOrigin.SetParent(fieldRoot, false);
        field.burstOrigin.position = Crown;

        // Spawns: 12 on the outer ring (r 16), between the sweepers and the springs, facing the fountain.
        var spawnsRoot = new GameObject("Spawns").transform;
        var spawns = new Transform[Protocol.MaxPlayers];
        for (var i = 0; i < Protocol.MaxPlayers; i++)
        {
            var a = (i * 30f + 15f) * Mathf.Deg2Rad;
            var pos = new Vector3(Mathf.Sin(a) * 16f, 0.05f, Mathf.Cos(a) * 16f);
            var t = new GameObject($"Spawn_{i:00}").transform;
            t.SetParent(spawnsRoot, false);
            t.SetPositionAndRotation(pos, Quaternion.LookRotation(new Vector3(-pos.x, 0f, -pos.z)));
            spawns[i] = t;
            KitBuilder.Visual(geo, $"SpawnPad_{i:00}", new Vector3(pos.x, 0.025f, pos.z), new Vector3(1.2f, 0.01f, 1.2f), KitBuilder.Mint);
        }

        var info = new GameObject("MapInfo").AddComponent<MapInfo>();
        var def = SandboxBuilder.MapDefinitionAsset("StarScramble", MapPoolV1.StarScramble, "Skill", KillY, new[] { 0, 1, 3, 4 });
        def.timerCapSeconds = 60;
        def.targetDuration = new Vector2Int(60, 75);
        def.minPlayers = 2;
        EditorUtility.SetDirty(def);
        info.definition = def;
        info.spawns = spawns;
        info.botAreaCenter = Vector3.zero;
        info.botAreaHalfExtents = new Vector2(12f, 12f);
        info.playerMaterial = AssetDatabase.LoadAssetAtPath<Material>(SandboxBuilder.MaterialsDir + "/PlayerPlaceholder.mat");
        info.unlitMaterial = AssetDatabase.LoadAssetAtPath<Material>(SandboxBuilder.MaterialsDir + "/CharacterUnlit.mat");
        info.turnTables = new[] { table };
        info.sweepers = sweepers.ToArray();
        info.platforms = platforms.ToArray();
        info.pads = pads.ToArray();
        info.bumpers = new HazardBumper[0];
        info.starFields = new[] { field };
        LobbyBuilder.Save(scene, ScenePath, info, "Map_StarScramble");
        var problems = new List<string>();
        for (byte v = 0; v < 2; v++)
            foreach (var m in def.allowedModifiers)
                problems.AddRange(info.Validate(new RoundParams { MapId = MapPoolV1.StarScramble, Variant = v, Modifier = (byte)m }));
        Debug.Log($"[setup] Star Fountain G4.4: table r {TableR} m ({TablePeriod} ticks), {sweepers.Count} sweepers, {platforms.Count} islands, {pads.Count} springs, " +
                  $"{field.def.AnchorCount} anchors ({field.def.Golden.Length} golden); allowlist problems: {problems.Count} {string.Join("; ", problems)}");
    }

    /// <summary>The star field def: 16 turntable anchors, 6 outer floor anchors, one golden anchor per island.</summary>
    private static StarFieldDef StarField(int islands, bool variantB)
    {
        var anchors = new List<int>();
        var attach = new List<int>();
        void Anchor(Vector3 p, int att)
        {
            anchors.Add(KitBuilder.Mm(p.x));
            anchors.Add(KitBuilder.Mm(p.y));
            anchors.Add(KitBuilder.Mm(p.z));
            attach.Add(att);
        }

        // Turntable rings (offsets in the table frame, y above its top).
        for (var ring = 0; ring < TableRings.Length; ring++)
            for (var k = 0; k < TableRingCount[ring]; k++)
            {
                var a = (k * 360f / TableRingCount[ring] + (variantB ? TableRingOffB : TableRingOffA)[ring]) * Mathf.Deg2Rad;
                Anchor(new Vector3(Mathf.Sin(a) * TableRings[ring], 0.6f, Mathf.Cos(a) * TableRings[ring]), 1000);
            }

        // Outer floor: two per sweeper, at its flanks (inside its sweep circle: the bar passes over them).
        var outerR = variantB ? OuterR_B : OuterR_A;
        var flank = variantB ? OuterFlankB : OuterFlankA;
        foreach (var s in SweepDeg)
            foreach (var side in new[] { -1f, 1f })
            {
                var a = (s + side * flank) * Mathf.Deg2Rad;
                Anchor(new Vector3(Mathf.Sin(a) * outerR, 0.6f, Mathf.Cos(a) * outerR), -1);
            }

        var golden = new List<int>();
        for (var i = 0; i < islands; i++)
        {
            golden.Add(attach.Count);
            Anchor(new Vector3(0f, 0.6f, 0f), i); // platform i: offset from its top centre
        }

        return new StarFieldDef
        {
            AnchorsMm = anchors.ToArray(), Attach = attach.ToArray(), Golden = golden.ToArray(),
            WaveEveryTicks = 240, WaveSize = 8, WaveLifeTicks = WaveLife, GoldenEveryTicks = 450, GoldenLifeTicks = GoldenLife, GoldenValue = 3,
            PickupRadiusMm = 400, DropPopTicks = 15, DropLifeTicks = 300, DropDistanceMm = 1400,
        };
    }

    /// <summary>A moving platform on the sine path (travel A / travel B), seeded phase.</summary>
    public static HazardPlatform SinePlatform(Transform hz, string id, Vector3 center, Vector3 size, Vector3 travelA, Vector3 travelB, int periodA, int periodB)
    {
        var root = new GameObject($"Platform_{id}").transform;
        root.SetParent(hz, false);
        var pl = root.gameObject.AddComponent<HazardPlatform>();
        pl.obstacleId = id;
        pl.def = new PlatformDef
        {
            CenterXMm = KitBuilder.Mm(center.x), CenterYMm = KitBuilder.Mm(center.y), CenterZMm = KitBuilder.Mm(center.z),
            HalfXMm = KitBuilder.Mm(size.x * 0.5f), HalfYMm = KitBuilder.Mm(size.y * 0.5f), HalfZMm = KitBuilder.Mm(size.z * 0.5f),
            TravelXMm = KitBuilder.Mm(travelA.x), TravelYMm = KitBuilder.Mm(travelA.y), TravelZMm = KitBuilder.Mm(travelA.z), PeriodTicks = periodA, KnotsMm = new int[0],
        };
        pl.variantB = pl.def;
        pl.variantB.TravelXMm = KitBuilder.Mm(travelB.x);
        pl.variantB.TravelYMm = KitBuilder.Mm(travelB.y);
        pl.variantB.TravelZMm = KitBuilder.Mm(travelB.z);
        pl.variantB.PeriodTicks = periodB;
        pl.hasVariantB = true;
        var col = new GameObject("MotorCollider") { layer = KitBuilder.Layer };
        col.transform.SetParent(root, false);
        col.transform.position = center;
        var bc = col.AddComponent<BoxCollider>();
        bc.size = size;
        var rb = col.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;
        var vis = KitBuilder.Visual(root, "Visual", center, size, KitBuilder.Platform);
        KitBuilder.LocalVisual(vis.transform, "MintRim", new Vector3(0f, 0.505f, 0f), new Vector3(0.9f, 0.01f, 0.9f), KitBuilder.Mint).transform.localScale = new Vector3(0.9f, 0.01f, 0.9f);
        pl.motorCollider = bc;
        pl.visual = vis.transform;
        return pl;
    }
}
