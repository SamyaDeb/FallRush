using System.Collections.Generic;
using System.IO;
using FallRush.Net;
using FallRush.Shared;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds <c>Map_Lobby</c> (M4-11): a small playable toy-block plaza where players run, jump and bump while they wait for
/// the match. 12 spawns in a ring around a central capsule-machine plinth (capsules "drop in" there on the client),
/// four bumpers, a spring pad onto a lookout block, low walls so nobody falls off by accident; no eliminations
/// (a fall respawns you on your spawn after 1 s). Batch: <c>-executeMethod LobbyBuilder.Run</c>.
/// </summary>
public static class LobbyBuilder
{
    public const string ScenePath = ProjectSetup.ScenesDir + "/Map_Lobby.unity";
    public const float KillY = -10f;
    public const float Half = 14f; // 28 × 28 m plaza

    [MenuItem("Fall Rush/Setup/Build Map_Lobby")]
    public static void Run()
    {
        KitBuilder.Init();
        var deck = Mat("LobbyDeck", new Color(0.94f, 0.95f, 0.99f));
        var tile = Mat("LobbyTile", new Color(0.86f, 0.92f, 1f));
        var wall = Mat("LobbyWall", new Color(0.88f, 0.86f, 0.97f));

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SetupCameraAndLight(new Vector3(0f, 12f, -26f), Quaternion.Euler(24f, 0f, 0f));

        var geo = new GameObject("Geometry").transform;
        var hz = new GameObject("Hazards").transform;

        // Plaza: a 1 m thick deck with a checker of slightly bluer tiles (visual only), low walls (0.6 m, jumpable from inside? no: 1.2 m keeps you in).
        KitBuilder.Box(geo, "PlazaDeck", new Vector3(0f, -0.5f, 0f), new Vector3(Half * 2f, 1f, Half * 2f), deck);
        for (var x = -3; x <= 3; x++)
            for (var z = -3; z <= 3; z++)
                if (((x + z) & 1) == 0)
                    KitBuilder.Visual(geo, $"Tile_{x}_{z}", new Vector3(x * 4f, 0.004f, z * 4f), new Vector3(3.9f, 0.01f, 3.9f), tile);
        KitBuilder.Box(geo, "WallN", new Vector3(0f, 0.6f, Half + 0.25f), new Vector3(Half * 2f + 1f, 1.2f, 0.5f), wall);
        KitBuilder.Box(geo, "WallS", new Vector3(0f, 0.6f, -Half - 0.25f), new Vector3(Half * 2f + 1f, 1.2f, 0.5f), wall);
        KitBuilder.Box(geo, "WallE", new Vector3(Half + 0.25f, 0.6f, 0f), new Vector3(0.5f, 1.2f, Half * 2f), wall);
        KitBuilder.Box(geo, "WallW", new Vector3(-Half - 0.25f, 0.6f, 0f), new Vector3(0.5f, 1.2f, Half * 2f), wall);

        // Central plinth (the capsule machine stands on it; visual dome is client-side greybox).
        KitBuilder.Box(geo, "Plinth", new Vector3(0f, 0.25f, 0f), new Vector3(3f, 0.5f, 3f), KitBuilder.Grape);
        KitBuilder.Visual(geo, "MachineGlass", new Vector3(0f, 2.1f, 0f), new Vector3(2.4f, 3.2f, 2.4f), Mat("LobbyGlass", new Color(0.75f, 0.9f, 1f)));
        KitBuilder.Sign(geo, "FALL RUSH LOBBY", new Vector3(0f, 4.6f, 0f), 0.06f);

        // Spring pad onto a lookout block (NE corner), bumpers on the diagonals.
        var pads = new List<HazardSpringPad> { KitBuilder.SpringPad(hz, "lobby_pad_1", new Vector3(6.5f, 0.02f, 6.5f), new Vector2(1.6f, 1.6f), new Vector3(1.8f, 11f, 1.8f)) };
        KitBuilder.Box(geo, "Lookout", new Vector3(10.5f, 1.25f, 10.5f), new Vector3(5f, 2.5f, 5f), wall);
        var bumpers = new List<HazardBumper>();
        foreach (var p in new[] { new Vector2(-7f, 7f), new Vector2(-7f, -7f), new Vector2(7f, -7f), new Vector2(0f, 9.5f) })
            bumpers.Add(KitBuilder.Bumper(hz, $"lobby_bumper_{bumpers.Count + 1}", new Vector3(p.x, 0f, p.y), 0.7f, 1.05f));

        // 12 spawns on a ring (r 6 m) facing the machine; a mint pad under each.
        var spawnsRoot = new GameObject("Spawns").transform;
        var spawns = new Transform[Protocol.MaxPlayers];
        for (var i = 0; i < Protocol.MaxPlayers; i++)
        {
            var a = i * Mathf.PI * 2f / Protocol.MaxPlayers + Mathf.PI * 0.5f / Protocol.MaxPlayers;
            var pos = new Vector3(Mathf.Cos(a) * 6f, 0.05f, Mathf.Sin(a) * 6f);
            var t = new GameObject($"Spawn_{i:00}").transform;
            t.SetParent(spawnsRoot, false);
            t.SetPositionAndRotation(pos, Quaternion.LookRotation(new Vector3(-pos.x, 0f, -pos.z)));
            spawns[i] = t;
            KitBuilder.Visual(geo, $"SpawnPad_{i:00}", new Vector3(pos.x, 0.008f, pos.z), new Vector3(1.2f, 0.01f, 1.2f), KitBuilder.Mint);
        }

        var info = new GameObject("MapInfo").AddComponent<MapInfo>();
        var def = SandboxBuilder.MapDefinitionAsset("Lobby", MapPoolV1.Lobby, "Lobby", KillY, new[] { 0 });
        def.variants = 1;
        def.timerCapSeconds = 0;
        def.targetDuration = new Vector2Int(0, 0);
        def.minPlayers = 1;
        EditorUtility.SetDirty(def);
        info.definition = def;
        info.spawns = spawns;
        info.botAreaCenter = Vector3.zero;
        info.botAreaHalfExtents = new Vector2(Half - 2f, Half - 2f);
        info.playerMaterial = AssetDatabase.LoadAssetAtPath<Material>(SandboxBuilder.MaterialsDir + "/PlayerPlaceholder.mat");
        info.unlitMaterial = AssetDatabase.LoadAssetAtPath<Material>(SandboxBuilder.MaterialsDir + "/CharacterUnlit.mat");
        info.pads = pads.ToArray();
        info.bumpers = bumpers.ToArray();
        Save(scene, ScenePath, info, "Map_Lobby");
    }

    // ------------------------------------------------------------------ shared by the M4 builders

    public static void SetupCameraAndLight(Vector3 camPos, Quaternion camRot)
    {
        var cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
        cam.tag = "MainCamera";
        var c = cam.GetComponent<Camera>();
        c.clearFlags = CameraClearFlags.SolidColor;
        c.backgroundColor = KitBuilder.Hex(0xBFE6FF);
        c.farClipPlane = 450f;
        cam.transform.SetPositionAndRotation(camPos, camRot);
        var light = new GameObject("Sun", typeof(Light)).GetComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.15f;
        light.shadows = LightShadows.None;
        light.transform.rotation = Quaternion.Euler(52f, -25f, 0f);
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.64f, 0.70f, 0.80f);
        RenderSettings.fog = false;
    }

    public static Material Mat(string name, Color color)
    {
        var path = $"{SandboxBuilder.MaterialsDir}/{name}.mat";
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

    /// <summary>Saves the scene, adds it to the build settings, records the map content hash, checks the allowlist for every variant × allowed modifier.</summary>
    public static void Save(UnityEngine.SceneManagement.Scene scene, string path, MapInfo info, string sceneName)
    {
        if (!EditorSceneManager.SaveScene(scene, path))
            throw new IOException($"Failed to save {path}");
        var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        if (!list.Exists(s => s.path == path))
        {
            list.Add(new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = list.ToArray();
        }

        AssetDatabase.SaveAssets();
        Physics.SyncTransforms();
        MapHashTool.Dump(sceneName + "-build", info);
        var hash = info.ContentHash;
        SandboxBuilder.RecordMapHash(sceneName, hash);
        var problems = new List<string>();
        var def = info.definition;
        for (byte v = 0; v < Mathf.Max(1, def.variants); v++)
            foreach (var m in def.allowedModifiers)
                problems.AddRange(info.Validate(new RoundParams { MapId = def.mapId, Variant = v, Modifier = (byte)m }));
        Debug.Log($"[setup] built {path} mapHash={Sha256.Hex(hash)} hazards={info.BuildHazards(default, out _).Count} allowlist problems: {problems.Count} {string.Join("; ", problems)}");
    }
}
