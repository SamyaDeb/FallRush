using System.Collections.Generic;
using System.IO;
using FallRush.Net;
using FallRush.Shared;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// Builds the M2 greybox test map <c>Map_Sandbox</c> (re-runnable; overwrites the scene). Every collider is a
/// BoxCollider on the <c>Gameplay</c> layer, which the motor queries. Layout (+Z forward from the spawn row):
/// <list type="bullet">
/// <item>Spawn row for 12 at z = −40.</item>
/// <item>Ramps 15° / 30° / 44° / 50° (50° must block) rising to 2 m, at x = −30 / −22 / −14 / −6.</item>
/// <item>Steps 0.2 m (climbable) and 0.3 m (blocks) at x = 6.5 / 13.5.</item>
/// <item>Ledge course at 1 m with gaps 1.0 / 1.4 / 1.8 m at x = 23.</item>
/// <item>Jump-height walls 1.5 m (clearable) and 1.7 m (not) at x = 32.</item>
/// <item>Fall-off edge at x = +40 (fall → respawn after 1 s) and a 0.3 m-wide, 12 m narrow beam out to an island.</item>
/// <item>M2b hazard course along z = −28 (west of the spawn): a rotating sweeper bar (pivot x = −30, z = −31, arm 4.5 m,
/// bar 0.3–0.6 m high, 5 s per turn) and a moving platform over the void past the −X edge (x = −46 ± 3.5 m, 6 s period)
/// shuttling to an island at x = −56. Hazard poses are <c>HazardMath</c> functions of the tick.</item>
/// </list>
/// Menu: Fall Rush/Setup/Build Map_Sandbox · batch: <c>-executeMethod SandboxBuilder.Run</c>.
/// </summary>
public static class SandboxBuilder
{
    public const string ScenePath = ProjectSetup.ScenesDir + "/Map_Sandbox.unity";
    public const string MaterialsDir = ProjectSetup.Root + "/Materials/Sandbox";
    public const int GameplayLayer = 8;

    private static Material _walk, _feature, _block, _mint, _beam, _player, _label, _hazardY, _hazardM, _unlit, _platform;
    private static Font _font;
    private static int _layer;

    [MenuItem("Fall Rush/Setup/Build Map_Sandbox")]
    public static void Run()
    {
        EnsureGameplayLayer();
        _layer = LayerMask.NameToLayer(PhysxMotorWorld.GameplayLayerName);
        CreateMaterials();
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
        cam.tag = "MainCamera";
        var c = cam.GetComponent<Camera>();
        c.clearFlags = CameraClearFlags.SolidColor;
        c.backgroundColor = new Color(0xBF / 255f, 0xE6 / 255f, 0xFF / 255f);
        c.farClipPlane = 400f;
        cam.transform.SetPositionAndRotation(new Vector3(0, 6, -50), Quaternion.Euler(20, 0, 0));

        var light = new GameObject("Sun", typeof(Light)).GetComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.1f;
        light.shadows = LightShadows.None;
        light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.62f, 0.68f, 0.78f);

        var geo = new GameObject("Geometry").transform;

        // Main slab: x [-40, 40], z [-50, 50], top at y = 0. The +X edge is the fall-off edge.
        Box(geo, "Ground", new Vector3(0f, -0.5f, 0f), new Vector3(80f, 1f, 100f), _walk);

        // Spawn row (pads are visual only, flush with the ground).
        var spawnsRoot = new GameObject("Spawns").transform;
        var spawns = new Transform[Protocol.MaxPlayers];
        for (var i = 0; i < Protocol.MaxPlayers; i++)
        {
            var x = -11f + 2f * i;
            var t = new GameObject($"Spawn_{i:00}").transform;
            t.SetParent(spawnsRoot, false);
            t.position = new Vector3(x, 0.05f, -40f);
            spawns[i] = t;
            Visual(geo, $"SpawnPad_{i:00}", new Vector3(x, 0.005f, -40f), new Vector3(1.4f, 0.01f, 1.4f), _mint);
        }

        Label(geo, "SPAWN (12)", new Vector3(0f, 2.2f, -42f));

        // Ramps rising toward +Z from z0 = -10 to 2 m, each with a top block.
        float[] angles = { 15f, 30f, 44f, 50f };
        float[] lanes = { -30f, -22f, -14f, -6f };
        for (var i = 0; i < angles.Length; i++)
            Ramp(geo, lanes[i], -10f, angles[i], 2f, angles[i] > 45f ? _block : _feature);

        // Steps.
        Box(geo, "Step_0.20", new Vector3(6.5f, 0.1f, -8f), new Vector3(5f, 0.2f, 4f), _feature);
        Label(geo, "STEP 0.2 m (climb)", new Vector3(6.5f, 1.6f, -10.5f));
        Box(geo, "Step_0.30", new Vector3(13.5f, 0.15f, -8f), new Vector3(5f, 0.3f, 4f), _block);
        Label(geo, "STEP 0.3 m (blocks)", new Vector3(13.5f, 1.6f, -10.5f));

        // Ledge course at 1 m: 15° ramp up, then platforms with gaps 1.0 / 1.4 / 1.8 m.
        const float lx = 23f, lh = 1f;
        Ramp(geo, lx, -14f, 15f, lh, _feature, label: false);
        var run = lh / Mathf.Tan(15f * Mathf.Deg2Rad);
        var z = -14f + run;
        float[] gaps = { 1.0f, 1.4f, 1.8f };
        Block(geo, "Ledge_0", lx, z, z + 4f, lh, 5f, _feature);
        z += 4f;
        for (var i = 0; i < gaps.Length; i++)
        {
            Label(geo, $"GAP {gaps[i]:0.0} m", new Vector3(lx, lh + 1.6f, z + gaps[i] * 0.5f));
            z += gaps[i];
            Block(geo, $"Ledge_{i + 1}", lx, z, z + 4f, lh, 5f, _feature);
            z += 4f;
        }

        // Jump-height walls.
        Block(geo, "Wall_1.5", 32f, -10f, -8f, 1.5f, 5f, _feature);
        Label(geo, "WALL 1.5 m (jump onto)", new Vector3(32f, 3.2f, -11f));
        Block(geo, "Wall_1.7", 32f, 0f, 2f, 1.7f, 5f, _block);
        Label(geo, "WALL 1.7 m (too high)", new Vector3(32f, 3.4f, -1f));

        // Narrow beam over the void from the fall-off edge (x = 40) to an island.
        Box(geo, "Beam_0.3x12", new Vector3(46f, -0.25f, 20f), new Vector3(12f, 0.5f, 0.3f), _beam);
        Box(geo, "Island", new Vector3(56f, -0.5f, 20f), new Vector3(8f, 1f, 8f), _walk);
        Label(geo, "BEAM 0.3 m · FALL-OFF EDGE → respawn", new Vector3(42f, 2f, 20f));

        var hz = new GameObject("Hazards").transform;
        var sweeper = BuildSweeper(hz, geo);
        var platform = BuildPlatform(hz, geo);
        Label(geo, "SWEEPER (jump the bar)", new Vector3(-30f, 2.6f, -36f));
        Label(geo, "MOVING PLATFORM → ISLAND", new Vector3(-42f, 2.4f, -24f));

        // M3a: the obstacle kit row along z = 54 (walkway north of the slab): one of each new hazard type.
        KitBuilder.Init();
        var kit = BuildKitRow(hz, geo);

        var info = new GameObject("MapInfo").AddComponent<MapInfo>();
        info.definition = MapDefinitionAsset("Sandbox", MapPoolV1.Sandbox, "Sandbox", -20f, new[] { 0, 1, 2, 3, 4, 5 });
        info.spawns = spawns;
        info.botAreaCenter = new Vector3(0f, 0f, -30f);
        info.botAreaHalfExtents = new Vector2(14f, 8f);
        info.playerMaterial = _player;
        info.unlitMaterial = _unlit;
        info.sweepers = new[] { sweeper, kit.spinner };
        info.platforms = new[] { platform, kit.platform };
        info.conveyors = new[] { kit.conveyor };
        info.pads = new[] { kit.pad };
        info.turnTables = new[] { kit.turnTable };
        info.doors = new[] { kit.door };
        info.mallets = new[] { kit.mallet };
        info.bumpers = kit.bumpers;

        if (!EditorSceneManager.SaveScene(scene, ScenePath))
            throw new IOException($"Failed to save {ScenePath}");

        var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        if (!list.Exists(s => s.path == ScenePath))
        {
            list.Add(new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = list.ToArray();
        }

        AssetDatabase.SaveAssets();
        MapHashTool.Dump("Map_Sandbox-build", info);
        var hash = info.ContentHash;
        Debug.Log($"[setup] built {ScenePath} (layer {PhysxMotorWorld.GameplayLayerName}={_layer}) mapHash={Sha256.Hex(hash)}");
        RecordMapHash("Map_Sandbox", hash);
    }

    // ------------------------------------------------------------------ M3a kit row

    public const float KitLaneZ = 54f;

    public struct Kit
    {
        public HazardSweeper spinner;
        public HazardPlatform platform;
        public HazardConveyor conveyor;
        public HazardSpringPad pad;
        public HazardTurnTable turnTable;
        public HazardGateDoor door;
        public HazardMallet mallet;
        public HazardBumper[] bumpers;
    }

    /// <summary>
    /// Kit row on a walkway z ∈ [50, 58] north of the slab (walk north from the spawn, then +X): high spinner bar (dive
    /// under) → loop platform over a gap → conveyor against you → spring pad over a 5 m gap → turn table → gate door →
    /// mallet → bumpers → end. Each station has a label; falls respawn at the spawn.
    /// </summary>
    private static Kit BuildKitRow(Transform hz, Transform geo)
    {
        const float z = KitLaneZ, z0 = 50f, z1 = 58f;
        var k = new Kit();
        void Walk(float x0, float x1) => Box(geo, $"KitWalk_{x0:0}_{x1:0}", new Vector3((x0 + x1) * 0.5f, -0.5f, (z0 + z1) * 0.5f), new Vector3(x1 - x0, 1f, z1 - z0), _walk);
        Walk(-40f, -25f);
        Walk(-17f, -14f);
        Walk(-8f, -2.5f);
        Walk(2.5f, 40f);
        Label(geo, "OBSTACLE KIT ROW (M3) → walk +X", new Vector3(-36f, 2.4f, z));

        // 1. SPINNER_BAR, high bar: dive under it (variant B turns the other way, faster).
        k.spinner = KitBuilder.Spinner(hz, geo, "kit_spinner_high", new Vector3(-31f, 0f, z + 3.5f), 4f, 0.95f, 150, -120);
        Label(geo, "SPINNER BAR (high: DIVE under)", new Vector3(-31f, 2.6f, z - 3f));

        // 2. MOVING_PLATFORM, a diamond loop over the gap x ∈ [-25, -17] (variant B: a wider, flatter loop).
        var pc = new Vector3(-21f, -0.25f, z);
        k.platform = KitBuilder.LoopPlatform(hz, "kit_platform_loop", pc, new Vector3(3f, 0.5f, 3f),
            new[] { new Vector3(-2.5f, 0f, 0f), new Vector3(0f, 0.3f, -1.5f), new Vector3(2.5f, 0f, 0f), new Vector3(0f, 0.3f, 1.5f) },
            new[] { new Vector3(-2.5f, 0f, 0f), new Vector3(0f, 0f, -2.5f), new Vector3(2.5f, 0f, 0f), new Vector3(0f, 0f, 2.5f) }, 240);
        Label(geo, "MOVING PLATFORM (loop; jump off keeps its speed)", new Vector3(-21f, 2.4f, z - 3f));

        // 3. CONVEYOR against you.
        k.conveyor = KitBuilder.Conveyor(hz, "kit_conveyor", new Vector3(-11f, -0.5f, (z0 + z1) * 0.5f), new Vector3(6f, 1f, z1 - z0), new Vector3(-2.5f, 0f, 0f));
        Label(geo, "CONVEYOR (2.5 m/s against you)", new Vector3(-11f, 2.4f, z - 3f));

        // 4. SPRING_PAD over a 5 m gap (a normal running jump is 4.7 m).
        k.pad = KitBuilder.SpringPad(hz, "kit_spring", new Vector3(-4f, 0.1f, z), new Vector2(1.6f, 1.6f), new Vector3(8f, 11.5f, 0f));
        Label(geo, "SPRING PAD (5 m gap)", new Vector3(-1f, 2.8f, z - 3f));

        // 5. TURN_TABLE (raised 0.15 m; variant B spins the other way).
        k.turnTable = KitBuilder.TurnTable(hz, "kit_turntable", new Vector3(8f, 0.15f, z), 3.5f, 360, -300);
        Label(geo, "TURN TABLE", new Vector3(8f, 2.4f, z - 4f));

        // 6. GATE_DOOR in a wall across the walkway.
        const float wx = 15f, doorW = 3f, wallH = 3f;
        Box(geo, "KitDoorWall_S", new Vector3(wx, wallH * 0.5f, (z0 + (z - doorW * 0.5f)) * 0.5f), new Vector3(0.6f, wallH, z - doorW * 0.5f - z0), _block);
        Box(geo, "KitDoorWall_N", new Vector3(wx, wallH * 0.5f, ((z + doorW * 0.5f) + z1) * 0.5f), new Vector3(0.6f, wallH, z1 - (z + doorW * 0.5f)), _block);
        Box(geo, "KitDoorLintel", new Vector3(wx, wallH + 2.25f, z), new Vector3(0.6f, 1.5f, doorW + 0.2f), _block);
        k.door = KitBuilder.GateDoor(hz, "kit_door", new Vector3(wx, 1.5f, z), new Vector3(0.4f, 3f, doorW), 60, 6, 75, 15, 0, 45);
        Label(geo, "GATE DOOR (timed; flashes before closing)", new Vector3(wx, 4.2f, z - 3.2f));

        // 7. MALLET_SWING across the lane (head swings along Z).
        k.mallet = KitBuilder.Mallet(hz, geo, "kit_mallet", new Vector3(23f, 5.5f, z), 2, 4.9f, 60f, 90, 75);
        Label(geo, "MALLET (wind-up glow)", new Vector3(23f, 6.4f, z - 3.5f));

        // 8. BUMPERs.
        k.bumpers = new[]
        {
            KitBuilder.Bumper(hz, "kit_bumper_a", new Vector3(30f, 0f, z - 1.6f), 0.7f, 1.0f),
            KitBuilder.Bumper(hz, "kit_bumper_b", new Vector3(32f, 0f, z + 1.4f), 0.7f, 1.0f),
        };
        Label(geo, "BUMPERS", new Vector3(31f, 2.4f, z - 3.2f));
        Visual(geo, "KitEnd", new Vector3(38f, 0.005f, z), new Vector3(2f, 0.01f, 8f), _mint);
        Label(geo, "END ← back", new Vector3(38f, 2f, z));
        return k;
    }

    /// <summary>Creates or updates <c>ScriptableObjects/Maps/{name}.asset</c>.</summary>
    public static MapDefinition MapDefinitionAsset(string name, byte id, string mode, float killY, int[] modifiers)
    {
        const string dir = ProjectSetup.Root + "/ScriptableObjects/Maps";
        if (!AssetDatabase.IsValidFolder(ProjectSetup.Root + "/ScriptableObjects"))
            AssetDatabase.CreateFolder(ProjectSetup.Root, "ScriptableObjects");
        if (!AssetDatabase.IsValidFolder(dir))
            AssetDatabase.CreateFolder(ProjectSetup.Root + "/ScriptableObjects", "Maps");
        var path = $"{dir}/{name}.asset";
        var d = AssetDatabase.LoadAssetAtPath<MapDefinition>(path);
        if (d == null)
        {
            d = ScriptableObject.CreateInstance<MapDefinition>();
            AssetDatabase.CreateAsset(d, path);
        }

        d.mapId = id;
        d.mapKey = name;
        d.mode = mode;
        d.killY = killY;
        d.allowedModifiers = modifiers;
        d.variants = 2;
        EditorUtility.SetDirty(d);
        return d;
    }

    /// <summary>Writes the map content hash next to the hazard vectors (EditMode test: the committed scene still hashes to it).</summary>
    public static void RecordMapHash(string scene, byte[] hash)
    {
        var path = Path.Combine(Application.dataPath, "_Game", "Tests", "Vectors", "map_hashes_v1.txt");
        var lines = new List<string>();
        if (File.Exists(path))
            foreach (var l in File.ReadAllLines(path))
                if (!l.StartsWith(scene + " "))
                    lines.Add(l);
        lines.Add($"{scene} {Sha256.Hex(hash)}");
        lines.Sort(System.StringComparer.Ordinal);
        File.WriteAllLines(path, lines);
    }

    // ------------------------------------------------------------------ M2b hazards

    public static readonly Vector3 SweeperPivot = new Vector3(-30f, 0f, -31f);
    public static readonly Vector3 PlatformCenter = new Vector3(-46f, -0.25f, -28f);

    private static HazardSweeper BuildSweeper(Transform hz, Transform geo)
    {
        var root = new GameObject("Sweeper").transform;
        root.SetParent(hz, false);
        root.position = SweeperPivot;
        var sw = root.gameObject.AddComponent<HazardSweeper>();
        sw.seed = 1;
        sw.def = new SweeperDef
        {
            PivotXMm = Mm(SweeperPivot.x), PivotYMm = Mm(SweeperPivot.y), PivotZMm = Mm(SweeperPivot.z),
            ArmMm = 4500, BarRadiusMm = 150, BarHeightMm = 450, PeriodTicks = 150,
            KnockbackCms = 900, KnockUpCms = 500, StunTicks = 15, ImmunityTicks = 30,
        };

        // The post is static geometry (the motor collides with it); the bar is analytic (no collider).
        Box(geo, "SweeperPost", SweeperPivot + new Vector3(0f, 0.6f, 0f), new Vector3(0.6f, 1.2f, 0.6f), _hazardM);

        var bar = new GameObject("BarVisual").transform;
        bar.SetParent(root, false);
        bar.localPosition = new Vector3(0f, 0.45f, 0f);
        // Hazard stripes: alternating yellow / magenta segments along the 9 m bar (its +arm is local +Z).
        const int segments = 12;
        const float length = 9f;
        for (var i = 0; i < segments; i++)
        {
            var z = -length * 0.5f + (i + 0.5f) * (length / segments);
            var seg = Box(bar, $"Seg_{i:00}", Vector3.zero, new Vector3(0.3f, 0.3f, length / segments), i % 2 == 0 ? _hazardY : _hazardM);
            Object.DestroyImmediate(seg.GetComponent<BoxCollider>());
            seg.layer = 0;
            seg.isStatic = false;
            seg.transform.localPosition = new Vector3(0f, 0f, z);
        }

        sw.barVisual = bar;
        return sw;
    }

    private static HazardPlatform BuildPlatform(Transform hz, Transform geo)
    {
        var size = new Vector3(3f, 0.5f, 3f);
        var root = new GameObject("MovingPlatform").transform;
        root.SetParent(hz, false);
        var pl = root.gameObject.AddComponent<HazardPlatform>();
        pl.seed = 2;
        pl.def = new PlatformDef
        {
            CenterXMm = Mm(PlatformCenter.x), CenterYMm = Mm(PlatformCenter.y), CenterZMm = Mm(PlatformCenter.z),
            HalfXMm = Mm(size.x * 0.5f), HalfYMm = Mm(size.y * 0.5f), HalfZMm = Mm(size.z * 0.5f),
            TravelXMm = 3500, PeriodTicks = 180,
        };

        // Motor collider: Gameplay layer, kinematic body, moved per simulated tick by PhysxMotorWorld.BeginTick.
        var col = new GameObject("MotorCollider");
        col.layer = _layer;
        col.transform.SetParent(root, false);
        col.transform.position = PlatformCenter;
        var bc = col.AddComponent<BoxCollider>();
        bc.size = size;
        var rb = col.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;

        // Visual: no collider, posed by the client at render time.
        var vis = Box(root, "Visual", PlatformCenter, size, _platform);
        Object.DestroyImmediate(vis.GetComponent<BoxCollider>());
        vis.layer = 0;
        vis.isStatic = false;
        var rim = Box(vis.transform, "MintRim", Vector3.zero, Vector3.one, _mint);
        Object.DestroyImmediate(rim.GetComponent<BoxCollider>());
        rim.layer = 0;
        rim.isStatic = false;
        rim.transform.localPosition = new Vector3(0f, 0.505f, 0f);
        rim.transform.localScale = new Vector3(0.9f, 0.01f, 0.9f);

        pl.motorCollider = bc;
        pl.visual = vis.transform;

        // Island the platform shuttles to.
        Box(geo, "PlatformIsland", new Vector3(-56f, -0.5f, -28f), new Vector3(8f, 1f, 8f), _walk);
        return pl;
    }

    private static int Mm(float m) => Mathf.RoundToInt(m * 1000f);

    private static void EnsureGameplayLayer()
    {
        var tm = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layers = tm.FindProperty("layers");
        var p = layers.GetArrayElementAtIndex(GameplayLayer);
        if (p.stringValue == PhysxMotorWorld.GameplayLayerName)
            return;
        if (!string.IsNullOrEmpty(p.stringValue))
            throw new System.InvalidOperationException($"Layer {GameplayLayer} is already '{p.stringValue}'");
        p.stringValue = PhysxMotorWorld.GameplayLayerName;
        tm.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
    }

    private static void CreateMaterials()
    {
        if (!AssetDatabase.IsValidFolder(MaterialsDir))
        {
            if (!AssetDatabase.IsValidFolder(ProjectSetup.Root + "/Materials"))
                AssetDatabase.CreateFolder(ProjectSetup.Root, "Materials");
            AssetDatabase.CreateFolder(ProjectSetup.Root + "/Materials", "Sandbox");
        }

        // doc 17 §3.1: walkable surfaces light and desaturated; goals mint. No hazard stripes on safe geometry.
        _walk = Mat("Walkable", new Color(0.91f, 0.93f, 0.97f));
        _feature = Mat("Feature", new Color(0.80f, 0.88f, 0.97f));
        _block = Mat("ShouldBlock", new Color(0.93f, 0.84f, 0.92f));
        _mint = Mat("SpawnMint", UiColor(0x5BE3B5));
        _beam = Mat("Beam", new Color(0.82f, 0.78f, 0.97f));
        _player = Mat("PlayerPlaceholder", Color.white);
        // doc 17 §3.1: everything that hurts is yellow/magenta striped; the platform is walkable (light).
        _hazardY = Mat("HazardYellow", UiColor(0xFFD21F));
        _hazardM = Mat("HazardMagenta", UiColor(0xFF3EA5));
        _platform = Mat("Platform", new Color(0.86f, 0.95f, 0.93f));
        _unlit = UnlitMat("CharacterUnlit", Color.white);
    }

    private static Color UiColor(int rgb) => new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);

    private static Material Mat(string name, Color color)
    {
        var path = $"{MaterialsDir}/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
                throw new System.InvalidOperationException("URP Lit shader not found");
            m = new Material(shader);
            AssetDatabase.CreateAsset(m, path);
        }

        m.SetColor("_BaseColor", color);
        m.SetFloat("_Smoothness", 0.15f);
        EditorUtility.SetDirty(m);
        return m;
    }

    private static Material UnlitMat(string name, Color color)
    {
        var path = $"{MaterialsDir}/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                throw new System.InvalidOperationException("URP Unlit shader not found");
            m = new Material(shader);
            AssetDatabase.CreateAsset(m, path);
        }

        m.SetColor("_BaseColor", color);
        EditorUtility.SetDirty(m);
        return m;
    }

    private static GameObject Box(Transform parent, string name, Vector3 center, Vector3 size, Material mat, Quaternion? rot = null)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.layer = _layer;
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(center, rot ?? Quaternion.identity);
        go.transform.localScale = size;
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        go.isStatic = true;
        return go;
    }

    /// <summary>Render-only (no collider), e.g. spawn pads.</summary>
    private static void Visual(Transform parent, string name, Vector3 center, Vector3 size, Material mat)
    {
        var go = Box(parent, name, center, size, mat);
        Object.DestroyImmediate(go.GetComponent<BoxCollider>());
        go.layer = 0;
    }

    /// <summary>Solid block from the ground up to <paramref name="height"/>, z in [z0, z1], centred on x.</summary>
    private static void Block(Transform parent, string name, float x, float z0, float z1, float height, float width, Material mat) =>
        Box(parent, name, new Vector3(x, height * 0.5f - 0.25f, (z0 + z1) * 0.5f), new Vector3(width, height + 0.5f, z1 - z0), mat);

    /// <summary>Ramp whose top surface starts at (x, 0, z0) and rises toward +Z at <paramref name="degrees"/> to <paramref name="height"/>, plus a top block.</summary>
    private static void Ramp(Transform parent, float x, float z0, float degrees, float height, Material mat, bool label = true)
    {
        const float thickness = 0.4f, width = 5f;
        var a = degrees * Mathf.Deg2Rad;
        var length = height / Mathf.Sin(a);
        var run = height / Mathf.Tan(a);
        var rot = Quaternion.Euler(-degrees, 0f, 0f);
        var n = rot * Vector3.up;
        var mid = new Vector3(x, height * 0.5f, z0 + run * 0.5f);
        Box(parent, $"Ramp_{degrees:0}", mid - n * (thickness * 0.5f), new Vector3(width, thickness, length), mat, rot);
        Block(parent, $"RampTop_{degrees:0}", x, z0 + run, z0 + run + 4f, height, width, mat);
        if (label)
            Label(parent, degrees > 45f ? $"{degrees:0}° (blocks)" : $"{degrees:0}°", new Vector3(x, height + 1.8f, z0 - 1f));
    }

    private static void Label(Transform parent, string text, Vector3 pos)
    {
        var go = new GameObject($"Label_{text}");
        go.transform.SetParent(parent, false);
        go.transform.position = pos;
        var tm = go.AddComponent<TextMesh>();
        tm.text = text;
        tm.font = _font;
        tm.fontSize = 96;
        tm.characterSize = 0.035f;
        tm.fontStyle = FontStyle.Bold;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.color = new Color(0.16f, 0.07f, 0.31f);
        go.GetComponent<MeshRenderer>().sharedMaterial = _font.material;
    }
}
