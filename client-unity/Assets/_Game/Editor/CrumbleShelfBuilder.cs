using System.Collections.Generic;
using FallRush.Net;
using FallRush.Shared;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds <c>Map_CrumbleShelf</c> (doc 08 §7, the Final): three stacked shelves (tile tops y = 12 / 6 / 0) of 9 × 9 square
/// toy-block tiles (1.6 m pitch, 4 cm gaps, rounded-corner look and alternating colours drawn by the client with GPU
/// instancing), inside an open claw-machine frame. A tile wobbles for 0.6 s after its first contact, then drops (reactive).
/// From 60 s two foam spinner bars hang from the claw and sweep the top shelf (a low one to jump, a high one to dive under;
/// variant = their timing). Falling below y = −6 m knocks you out. Square shelves, spinners and the claw frame are our own
/// design (no hex tiles). Batch: <c>-executeMethod CrumbleShelfBuilder.Run</c>.
/// </summary>
public static class CrumbleShelfBuilder
{
    public const string ScenePath = ProjectSetup.ScenesDir + "/Map_CrumbleShelf.unity";
    public const float KillY = -6f;
    public static readonly float[] ShelfTops = { 12f, 6f, 0f };
    public const float Tile = 1.6f, Gap = 0.04f, Thick = 0.4f;
    public const int N = 9;
    public const float Origin = -N * Tile * 0.5f; // −7.2 m

    [MenuItem("Fall Rush/Setup/Build Map_CrumbleShelf")]
    public static void Run()
    {
        KitBuilder.Init();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        LobbyBuilder.SetupCameraAndLight(new Vector3(0f, 20f, -22f), Quaternion.Euler(32f, 0f, 0f));
        var geo = new GameObject("Geometry").transform;
        var hz = new GameObject("Hazards").transform;
        var frame = LobbyBuilder.Mat("CrumbleFrame", new Color(0.55f, 0.36f, 0.95f));

        // ---------------- tiles (colliders only; the client draws them instanced)
        var gridRoot = new GameObject("CrumbleGrid").transform;
        gridRoot.SetParent(hz, false);
        var grid = gridRoot.gameObject.AddComponent<HazardCrumbleGrid>();
        grid.obstacleId = "cs_tiles";
        grid.phaseMin16 = grid.phaseMax16 = 0;
        grid.def = new TileGridDef
        {
            OriginXMm = KitBuilder.Mm(Origin), OriginZMm = KitBuilder.Mm(Origin), TileMm = KitBuilder.Mm(Tile), GapMm = KitBuilder.Mm(Gap), Cols = N, Rows = N,
            ShelfTopsMm = new[] { KitBuilder.Mm(ShelfTops[0]), KitBuilder.Mm(ShelfTops[1]), KitBuilder.Mm(ShelfTops[2]) }, ThicknessMm = KitBuilder.Mm(Thick), WobbleTicks = 18,
        };
        grid.spinnerStartA = 1800;
        grid.spinnerStartB = 1800;
        var cols = new Collider[grid.def.Count];
        for (var id = 0; id < cols.Length; id++)
        {
            grid.def.Center(id, out var x, out var y, out var z);
            var go = new GameObject($"Tile_{id:000}") { layer = KitBuilder.Layer };
            go.transform.SetParent(gridRoot, false);
            go.transform.position = new Vector3(x * 0.001f, y * 0.001f, z * 0.001f);
            var bc = go.AddComponent<BoxCollider>();
            bc.size = new Vector3(Tile - Gap, Thick, Tile - Gap);
            go.isStatic = false; // switched off when the tile drops
            cols[id] = bc;
        }

        grid.tiles = cols;

        // ---------------- the claw-machine frame (visual only) + the two spinner bars hanging from the claw
        const float F = 9.6f;
        foreach (var (px, pz) in new[] { (-F, -F), (F, -F), (F, F), (-F, F) })
            KitBuilder.Visual(geo, $"FramePost_{px}_{pz}", new Vector3(px, 7f, pz), new Vector3(0.5f, 26f, 0.5f), frame);
        foreach (var y in new[] { 19.8f, -0.8f })
        {
            KitBuilder.Visual(geo, $"FrameN_{y}", new Vector3(0f, y, F), new Vector3(2 * F + 0.5f, 0.4f, 0.4f), frame);
            KitBuilder.Visual(geo, $"FrameS_{y}", new Vector3(0f, y, -F), new Vector3(2 * F + 0.5f, 0.4f, 0.4f), frame);
            KitBuilder.Visual(geo, $"FrameE_{y}", new Vector3(F, y, 0f), new Vector3(0.4f, 0.4f, 2 * F + 0.5f), frame);
            KitBuilder.Visual(geo, $"FrameW_{y}", new Vector3(-F, y, 0f), new Vector3(0.4f, 0.4f, 2 * F + 0.5f), frame);
        }

        KitBuilder.Visual(geo, "ClawRail", new Vector3(0f, 19.8f, 0f), new Vector3(2 * F, 0.3f, 0.6f), KitBuilder.Grape);
        KitBuilder.Visual(geo, "ClawCable", new Vector3(0f, 16.4f, 0f), new Vector3(0.12f, 6.6f, 0.12f), KitBuilder.Ink);
        KitBuilder.Visual(geo, "ClawHub", new Vector3(0f, 13.1f, 0f), new Vector3(0.9f, 0.5f, 0.9f), KitBuilder.Grape);
        for (var i = 0; i < 3; i++)
        {
            var a = i * 120f * Mathf.Deg2Rad;
            KitBuilder.Visual(geo, $"ClawFinger_{i}", new Vector3(Mathf.Sin(a) * 0.55f, 12.95f, Mathf.Cos(a) * 0.55f), new Vector3(0.12f, 0.6f, 0.12f), KitBuilder.Grape, Quaternion.Euler(Mathf.Cos(a) * 25f, 0f, -Mathf.Sin(a) * 25f));
        }

        var sweepers = new List<HazardSweeper>
        {
            // Low bar (jump it), variant B faster; high bar (dive under it), the other way.
            KitBuilder.Spinner(hz, geo, "cs_spinner_low", new Vector3(0f, ShelfTops[0], 0f), 6.6f, 0.35f, 330, 270),
            KitBuilder.Spinner(hz, geo, "cs_spinner_high", new Vector3(0f, ShelfTops[0], 0f), 6.6f, 0.95f, -400, -300),
        };
        // The kit's spinner post would stand on a tile: the bars hang from the claw instead.
        foreach (var post in new[] { "SpinnerPost_cs_spinner_low", "SpinnerPost_cs_spinner_high" })
        {
            var t = geo.Find(post);
            if (t != null)
                Object.DestroyImmediate(t.gameObject);
        }

        KitBuilder.Label(geo, "CRUMBLE SHELF", new Vector3(0f, 21f, -F), 0.07f);
        KitBuilder.Label(geo, "LAST ONE STANDING WINS", new Vector3(0f, 20.3f, -F), 0.03f);
        KitBuilder.Visual(geo, "KillPlaneMarker", new Vector3(0f, KillY, 0f), new Vector3(30f, 0.02f, 30f), KitBuilder.Mint).GetComponent<MeshRenderer>().enabled = false;

        // ---------------- spawns: 12 on the top shelf, one per tile on a ring, facing the middle
        var spawnsRoot = new GameObject("Spawns").transform;
        var spawns = new Transform[Protocol.MaxPlayers];
        var ring = new[] { (1, 1), (4, 0), (7, 1), (8, 4), (7, 7), (4, 8), (1, 7), (0, 4), (2, 3), (6, 3), (6, 5), (2, 5) };
        for (var i = 0; i < Protocol.MaxPlayers; i++)
        {
            var (c, r) = ring[i];
            var pos = new Vector3(Origin + (c + 0.5f) * Tile, ShelfTops[0] + 0.05f, Origin + (r + 0.5f) * Tile);
            var t = new GameObject($"Spawn_{i:00}").transform;
            t.SetParent(spawnsRoot, false);
            var look = new Vector3(-pos.x, 0f, -pos.z);
            t.SetPositionAndRotation(pos, look.sqrMagnitude > 0.01f ? Quaternion.LookRotation(look) : Quaternion.identity);
            spawns[i] = t;
        }

        var info = new GameObject("MapInfo").AddComponent<MapInfo>();
        var def = SandboxBuilder.MapDefinitionAsset("CrumbleShelf", MapPoolV1.CrumbleShelf, "Final", KillY, new[] { 0, 1, 2 });
        def.timerCapSeconds = 120;
        def.targetDuration = new Vector2Int(60, 120);
        def.minPlayers = 2;
        EditorUtility.SetDirty(def);
        info.definition = def;
        info.spawns = spawns;
        info.botAreaCenter = new Vector3(0f, 0f, 0f);
        info.botAreaHalfExtents = new Vector2(6f, 6f);
        info.playerMaterial = AssetDatabase.LoadAssetAtPath<Material>(SandboxBuilder.MaterialsDir + "/PlayerPlaceholder.mat");
        info.unlitMaterial = AssetDatabase.LoadAssetAtPath<Material>(SandboxBuilder.MaterialsDir + "/CharacterUnlit.mat");
        info.sweepers = sweepers.ToArray();
        info.grids = new[] { grid };
        LobbyBuilder.Save(scene, ScenePath, info, "Map_CrumbleShelf");
    }
}
