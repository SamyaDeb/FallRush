using System.Collections.Generic;
using FallRush.Net;
using FallRush.Shared;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds <c>Map_StarScramble</c> (doc 08 §6, the Skill round): a round toy-box arena (Ø 36 m) with a central turn table,
/// six bumpers, two spring pads that launch you onto two of the four outer moving islands (the islands float just
/// beyond the rim and slide along it; variant A: along the rim, variant B: in and out). Stars come from a seeded
/// timeline: waves of 8 normal stars on the floor and the turn table every 8 s, a golden star (+3) every 15 s on an
/// island. A knockback drops one star. Falls respawn after 1 s. Batch: <c>-executeMethod StarScrambleBuilder.Run</c>.
/// </summary>
public static class StarScrambleBuilder
{
    public const string ScenePath = ProjectSetup.ScenesDir + "/Map_StarScramble.unity";
    public const float KillY = -10f;
    public const float ArenaR = 18f, IslandR = 22f, TableR = 5f;
    public static readonly float[] IslandDeg = { 45f, 135f, 225f, 315f };

    [MenuItem("Fall Rush/Setup/Build Map_StarScramble")]
    public static void Run()
    {
        KitBuilder.Init();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        LobbyBuilder.SetupCameraAndLight(new Vector3(0f, 22f, -34f), Quaternion.Euler(30f, 0f, 0f));
        var geo = new GameObject("Geometry").transform;
        var hz = new GameObject("Hazards").transform;
        var floor = LobbyBuilder.Mat("StarFloor", new Color(0.93f, 0.95f, 1f));
        var rim = LobbyBuilder.Mat("StarRim", new Color(1f, 0.82f, 0.25f));

        // Arena floor (top at y = 0), a rim band (visual), floor rings (visual).
        KitBuilder.Prism(geo, "ArenaFloor", Vector3.zero, ArenaR, 1f, floor, collider: true);
        KitBuilder.Prism(geo, "ArenaRimBand", new Vector3(0f, 0.005f, 0f), ArenaR, 0.01f, rim, collider: false).layer = 0;
        KitBuilder.Prism(geo, "ArenaInner", new Vector3(0f, 0.01f, 0f), ArenaR - 0.6f, 0.01f, floor, collider: false).layer = 0;
        KitBuilder.Prism(geo, "ArenaRing", new Vector3(0f, 0.015f, 0f), 11f, 0.01f, LobbyBuilder.Mat("StarRing", new Color(0.86f, 0.92f, 1f)), collider: false).layer = 0;
        KitBuilder.Prism(geo, "ArenaRingIn", new Vector3(0f, 0.02f, 0f), 10.4f, 0.01f, floor, collider: false).layer = 0;

        // Central turn table (top 0.2 m: walk on), 20 s per turn (variant B the other way).
        var table = KitBuilder.TurnTable(hz, "ss_table", new Vector3(0f, 0.2f, 0f), TableR, 600, -600);

        // Outer islands: 4 × 4 m, top at 0.8 m, sliding along the rim (A) or in and out (B).
        var platforms = new List<HazardPlatform>();
        for (var i = 0; i < 4; i++)
        {
            var a = IslandDeg[i] * Mathf.Deg2Rad;
            var c = new Vector3(Mathf.Sin(a) * IslandR, 0.5f, Mathf.Cos(a) * IslandR); // G1: top 1.0 → 0.8 m (v2 hop 1.2 m)
            var tangent = new Vector3(Mathf.Cos(a), 0f, -Mathf.Sin(a));
            var radial = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            platforms.Add(SinePlatform(hz, $"ss_island_{i + 1}", c, new Vector3(4f, 0.6f, 4f), tangent * 3f, radial * 1.5f, 360, 300));
        }

        // Spring pads near the rim toward islands 1 and 3 (they ride you up and out over the 2 m gap).
        var pads = new List<HazardSpringPad>();
        foreach (var i in new[] { 0, 2 })
        {
            var a = IslandDeg[i] * Mathf.Deg2Rad;
            var dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            pads.Add(KitBuilder.SpringPad(hz, $"ss_pad_{i + 1}", dir * 15.5f + new Vector3(0f, 0.02f, 0f), new Vector2(1.6f, 1.6f), dir * 5.8f + new Vector3(0f, 10f, 0f))); // G1: 6.5 → 5.8 m/s out: lands ~0.5 m onto the island (r 20.5) under v2 gravity
        }

        // Bumpers between the rings.
        var bumpers = new List<HazardBumper>();
        for (var i = 0; i < 6; i++)
        {
            var a = (i * 60f + 30f) * Mathf.Deg2Rad;
            var r = i % 2 == 0 ? 8f : 13f;
            bumpers.Add(KitBuilder.Bumper(hz, $"ss_bumper_{i + 1}", new Vector3(Mathf.Sin(a) * r, 0f, Mathf.Cos(a) * r), 0.8f, 1.1f));
        }

        // Star field: 24 floor anchors (rings r 7 / 12 / 16), 4 on the turn table (table frame), 4 on the islands (golden).
        var anchors = new List<int>();
        var attach = new List<int>();
        void Anchor(Vector3 p, int att)
        {
            anchors.Add(KitBuilder.Mm(p.x));
            anchors.Add(KitBuilder.Mm(p.y));
            anchors.Add(KitBuilder.Mm(p.z));
            attach.Add(att);
        }

        foreach (var (r, off) in new[] { (7f, 0f), (12f, 22.5f), (16f, 0f) })
            for (var k = 0; k < 8; k++)
            {
                var a = (k * 45f + off) * Mathf.Deg2Rad;
                Anchor(new Vector3(Mathf.Sin(a) * r, 0.6f, Mathf.Cos(a) * r), -1);
            }

        for (var k = 0; k < 4; k++)
        {
            var a = (k * 90f + 45f) * Mathf.Deg2Rad;
            Anchor(new Vector3(Mathf.Sin(a) * 3.2f, 0.6f, Mathf.Cos(a) * 3.2f), 1000); // offset in the table frame, y above its top
        }

        var golden = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            golden.Add(attach.Count);
            Anchor(new Vector3(0f, 0.6f, 0f), i); // platform i: offset from its top centre
        }

        var fieldRoot = new GameObject("StarField").transform;
        fieldRoot.SetParent(hz, false);
        var field = fieldRoot.gameObject.AddComponent<HazardStarField>();
        field.obstacleId = "ss_stars";
        field.phaseMin16 = field.phaseMax16 = 0;
        field.def = new StarFieldDef
        {
            AnchorsMm = anchors.ToArray(), Attach = attach.ToArray(), Golden = golden.ToArray(),
            WaveEveryTicks = 240, WaveSize = 8, WaveLifeTicks = 480, GoldenEveryTicks = 450, GoldenLifeTicks = 300, GoldenValue = 3,
            PickupRadiusMm = 400, DropPopTicks = 15, DropLifeTicks = 300, DropDistanceMm = 1400,
        };
        field.variantB = field.def;

        KitBuilder.Sign(geo, "STAR SCRAMBLE", new Vector3(0f, 6f, ArenaR + 1f), 0.08f);
        KitBuilder.Label(geo, "GRAB STARS · GOLD = +3 · A BONK DROPS ONE", new Vector3(0f, 5f, ArenaR + 1f), 0.03f);

        // Spawns: a ring (r 10 m) facing the middle.
        var spawnsRoot = new GameObject("Spawns").transform;
        var spawns = new Transform[Protocol.MaxPlayers];
        for (var i = 0; i < Protocol.MaxPlayers; i++)
        {
            var a = (i * 30f + 15f) * Mathf.Deg2Rad;
            var pos = new Vector3(Mathf.Sin(a) * 10f, 0.05f, Mathf.Cos(a) * 10f);
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
        info.platforms = platforms.ToArray();
        info.pads = pads.ToArray();
        info.bumpers = bumpers.ToArray();
        info.starFields = new[] { field };
        LobbyBuilder.Save(scene, ScenePath, info, "Map_StarScramble");
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
