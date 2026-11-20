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
/// </list>
/// Menu: Fall Rush/Setup/Build Map_Sandbox · batch: <c>-executeMethod SandboxBuilder.Run</c>.
/// </summary>
public static class SandboxBuilder
{
    public const string ScenePath = ProjectSetup.ScenesDir + "/Map_Sandbox.unity";
    public const string MaterialsDir = ProjectSetup.Root + "/Materials/Sandbox";
    public const int GameplayLayer = 8;

    private static Material _walk, _feature, _block, _mint, _beam, _player, _label;
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

        var info = new GameObject("MapInfo").AddComponent<MapInfo>();
        info.spawns = spawns;
        info.botAreaCenter = new Vector3(0f, 0f, -30f);
        info.botAreaHalfExtents = new Vector2(14f, 8f);
        info.playerMaterial = _player;

        if (!EditorSceneManager.SaveScene(scene, ScenePath))
            throw new IOException($"Failed to save {ScenePath}");

        var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        if (!list.Exists(s => s.path == ScenePath))
        {
            list.Add(new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = list.ToArray();
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[setup] built {ScenePath} (layer {PhysxMotorWorld.GameplayLayerName}={_layer})");
    }

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
        tm.fontSize = 64;
        tm.characterSize = 0.06f;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.color = new Color(0.16f, 0.07f, 0.31f);
        go.GetComponent<MeshRenderer>().sharedMaterial = _font.material;
    }
}
