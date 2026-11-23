#if !UNITY_SERVER // P5: editor tools on the client assembly (excluded from the Server subtarget, P3 proof §5)
using System.IO;
using System.Text;
using FallRush.Client;
using FallRush.Net;
using FallRush.Shared;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// G4.3 proof shots of <c>Map_GooTower</c>, rendered in the Editor with the <see cref="CapsuleRunShots"/> helpers: the
/// spawn view (follow-camera framing) and a look-up from the spawn (the summit is 22 m above the start corner), one shot
/// per side with the hazards posed by the client's <see cref="HazardViewDriver"/>, an orthographic overhead and a side
/// elevation, the goo mid-round; plus an Editor count of the renderers the spawn camera sees (a draw-call proxy).
/// Batch (needs graphics): <c>-executeMethod GooTowerShots.Run -fallrushShotDir &lt;dir&gt;</c>.
/// </summary>
public static class GooTowerShots
{
    [MenuItem("Fall Rush/Proof/Goo Tower shots")]
    public static void Run()
    {
        var dir = CapsuleRunShots.Arg("-fallrushShotDir") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "../../docs/proof/g4"));
        Directory.CreateDirectory(dir);
        var scene = EditorSceneManager.OpenScene(GooTowerBuilder.ScenePath, OpenSceneMode.Single);
        MapInfo map = null;
        foreach (var root in scene.GetRootGameObjects())
            map ??= root.GetComponentInChildren<MapInfo>();
        if (map == null)
            throw new IOException("no MapInfo");
        var hz = map.BuildHazards(new RoundParams { MapId = map.MapId, Variant = 0, HazardSeed = 20261120 }, out _);
        var view = new HazardViewDriver(map);
        CapsuleRunShots._map = map;
        var go = new GameObject("ShotCamera");
        var cam = go.AddComponent<Camera>();
        var main = Camera.main;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = main != null ? main.backgroundColor : new Color(0.75f, 0.9f, 1f);
        cam.farClipPlane = 600f;
        cam.fieldOfView = FollowCamera.BaseFov;
        if (main != null)
            main.enabled = false;

        var report = new StringBuilder();
        // Spawn view: a Rushling at a middle spawn, the follow camera 6.1 m behind and 2.5 m up, facing north at corner 0.
        var feet = map.spawns[5].position;
        var target = feet + Vector3.up * 0.9f;
        var eye = target + new Vector3(0f, 2.5f, -6.1f);
        CapsuleRunShots.Pose(view, hz, 0);
        CapsuleRunShots.Shot(cam, dir, "goo-tower-spawn-view", eye, target, 1280, 720);
        report.Append(CapsuleRunShots.CountVisible(cam, "spawn view (follow framing)"));
        // The same spot with the orbit raised to look up the tower (mouse look): the summit beacon above the start corner.
        CapsuleRunShots.Shot(cam, dir, "goo-tower-spawn-lookup", target + new Vector3(0f, 0.6f, -6.5f), GooTowerBuilder.Corner(GooTowerBuilder.Sides) + Vector3.up * 2f, 1280, 720);
        report.Append(CapsuleRunShots.CountVisible(cam, "spawn look-up"));

        // One shot per side (hazard ticks mid-motion): the camera outside the side, looking in and slightly down.
        for (var k = 0; k < GooTowerBuilder.Sides; k++)
        {
            CapsuleRunShots.Pose(view, hz, 300 + 37 * k);
            var mid = GooTowerBuilder.P(k, 0f, 0f, GooTowerBuilder.H(k) + 1.2f);
            var from = GooTowerBuilder.P(k, -9f, 12f, GooTowerBuilder.H(k) + 7f);
            CapsuleRunShots.Shot(cam, dir, $"goo-tower-k{k}", from, mid, 1280, 720);
        }

        // Overhead (orthographic) and side elevation (from the south-east, orthographic).
        CapsuleRunShots.Pose(view, hz, 500);
        cam.orthographic = true;
        cam.orthographicSize = 21f;
        cam.transform.SetPositionAndRotation(new Vector3(0f, 120f, 0f), Quaternion.LookRotation(Vector3.down, Vector3.forward));
        CapsuleRunShots.Render(cam, Path.Combine(dir, "goo-tower-overhead.png"), 1200, 1200);
        cam.orthographicSize = 16f;
        cam.transform.SetPositionAndRotation(new Vector3(60f, 12f, -60f), Quaternion.LookRotation(new Vector3(-1f, 0f, 1f)));
        CapsuleRunShots.Render(cam, Path.Combine(dir, "goo-tower-elevation.png"), 1280, 1100);
        cam.orthographic = false;

        // The goo mid-round (t ≈ 45 s: y ≈ 6 m, lap 1 half under).
        // The goo plane is a client view built at runtime (ReactiveViews): a stand-in at the pure goo height for this shot.
        CapsuleRunShots.Pose(view, hz, 45 * 30);
        var gooY = HazardMath.GooYMm(in hz.Goo[0], 45 * 30) * 0.001f;
        var goo = GameObject.CreatePrimitive(PrimitiveType.Cube);
        goo.name = "ShotGooStandIn";
        goo.transform.SetPositionAndRotation(new Vector3(0f, gooY - 0.15f, 0f), Quaternion.identity);
        goo.transform.localScale = new Vector3(44f, 0.3f, 44f);
        var gm = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = new Color(0.61f, 0.24f, 1f) };
        goo.GetComponent<Renderer>().sharedMaterial = gm;
        CapsuleRunShots.Shot(cam, dir, "goo-tower-goo-mid-round", new Vector3(28f, 16f, -28f), new Vector3(0f, 7f, 0f), 1280, 720);
        Object.DestroyImmediate(goo);
        Object.DestroyImmediate(gm);
        report.Append($"- goo mid-round shot: t 45 s, goo y {gooY:F2} m (stand-in plane at the pure GooYMm height)\n");

        File.WriteAllText(Path.Combine(dir, "goo-tower-drawcalls-editor.md"), report.ToString());
        Debug.Log($"[shots] wrote Goo Tower shots to {dir}\n{report}");
        Object.DestroyImmediate(go);
    }
}
#endif
