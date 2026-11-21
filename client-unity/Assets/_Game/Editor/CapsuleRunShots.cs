using System.Collections.Generic;
using System.IO;
using System.Text;
using FallRush.Client;
using FallRush.Net;
using FallRush.Shared;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// G4.2 proof shots of <c>Map_BlockPartyDash</c> (Capsule Run), rendered in the Editor: the spawn view (follow-camera
/// framing: 6.1 m behind, 2.5 m up, FOV 55), one action shot per beat with the hazards posed by the client's own
/// <see cref="HazardViewDriver"/> at a hazard tick, and an orthographic overhead of the whole course. Also writes an
/// Editor count of the renderers the spawn camera sees (a draw-call proxy, not a GPU capture).
/// Batch (needs graphics, so without -nographics): <c>-executeMethod CapsuleRunShots.Run -fallrushShotDir &lt;dir&gt;</c>.
/// </summary>
public static class CapsuleRunShots
{
    [MenuItem("Fall Rush/Proof/Capsule Run shots")]
    public static void Run()
    {
        var dir = Arg("-fallrushShotDir") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "../../docs/proof/g4"));
        Directory.CreateDirectory(dir);
        var scene = EditorSceneManager.OpenScene(BlockPartyDashBuilder.ScenePath, OpenSceneMode.Single);
        MapInfo map = null;
        foreach (var root in scene.GetRootGameObjects())
            map ??= root.GetComponentInChildren<MapInfo>();
        if (map == null)
            throw new IOException("no MapInfo");
        var hz = map.BuildHazards(new RoundParams { MapId = map.MapId, Variant = 0, HazardSeed = 20261119 }, out _);
        var view = new HazardViewDriver(map);
        _map = map;
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
        // Spawn view: a Rushling at the middle spawn, the follow camera behind and above it, gate closed (t 0).
        var feet = new Vector3(0f, 0f, 4f);
        var target = feet + Vector3.up * 0.9f;
        var eye = target + new Vector3(0f, 2.5f, -6.1f);
        Pose(view, hz, 0);
        Shot(cam, dir, "capsule-run-spawn-view", eye, target, 1280, 720);
        report.Append(CountVisible(cam, "spawn view (gate closed)"));
        Pose(view, hz, 400);
        Shot(cam, dir, "capsule-run-spawn-view-rush", eye, target, 1280, 720);
        report.Append(CountVisible(cam, "spawn view (after RUSH, hazards moving)"));

        // One action shot per beat (hazard tick chosen so balls / heads / doors are mid-motion).
        var beats = new (string name, int t, Vector3 eye, Vector3 at)[]
        {
            ("beat1-gumball-slope", 610, new Vector3(-7f, 6.5f, 10f), new Vector3(0f, 2.2f, 34f)),
            ("beat2-wave-gates", 655, new Vector3(-6f, 8.5f, 47f), new Vector3(0f, 5.5f, 58.6f)),
            ("beat3-hop-field", 700, new Vector3(-11f, 11f, 74f), new Vector3(0f, 5.5f, 92f)),
            ("beat4-sweep-pad", 745, new Vector3(-12f, 15f, 118f), new Vector3(0f, 5f, 135.5f)),
            ("beat5-mallet-run", 790, new Vector3(7f, 10.5f, 150f), new Vector3(0f, 6f, 166f)),
            ("beat6-belt-climb", 835, new Vector3(-12f, 13f, 180f), new Vector3(0f, 8f, 203f)),
            ("beat7-finish", 880, new Vector3(5f, 15f, 206f), new Vector3(0f, 14f, 222f)),
        };
        foreach (var b in beats)
        {
            Pose(view, hz, b.t);
            Shot(cam, dir, $"capsule-run-{b.name}", b.eye, b.at, 1280, 720);
        }

        // Overhead: orthographic, +z to the right, the left of the course (−x) at the top.
        Pose(view, hz, 700);
        cam.orthographic = true;
        cam.orthographicSize = 22f;
        cam.transform.SetPositionAndRotation(new Vector3(0f, 150f, 114f), Quaternion.LookRotation(Vector3.down, Vector3.left));
        Render(cam, Path.Combine(dir, "capsule-run-overhead.png"), 2640, 500);
        cam.orthographic = false;

        File.WriteAllText(Path.Combine(dir, "capsule-run-drawcalls-editor.md"), report.ToString());
        Debug.Log($"[shots] wrote Capsule Run shots to {dir}\n{report}");
        Object.DestroyImmediate(go);
    }

    private static MapInfo _map;

    private static void Pose(HazardViewDriver view, HazardSet hz, double t)
    {
        view.Update(hz, t, false);
        if (_map == null)
            return;
        int active = 0, total = 0;
        string first = "-";
        foreach (var lane in _map.rollingBalls)
            foreach (var b in lane.balls)
            {
                total++;
                if (b != null && b.gameObject.activeSelf)
                {
                    active++;
                    if (first == "-")
                        first = $"{b.position} renderers={b.GetComponentsInChildren<Renderer>(true).Length}";
                }
            }

        Debug.Log($"[shots] t={t}: balls active {active}/{total}, first {first}, hz lanes {hz.RollingBalls.Length}");
    }

    private static void Shot(Camera cam, string dir, string name, Vector3 eye, Vector3 at, int w, int h)
    {
        cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(at - eye));
        Render(cam, Path.Combine(dir, name + ".png"), w, h);
    }

    private static void Render(Camera cam, string path, int w, int h)
    {
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        cam.targetTexture = rt;
        cam.aspect = (float)w / h;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        RenderTexture.active = null;
        cam.targetTexture = null;
        Object.DestroyImmediate(tex);
        rt.Release();
        Object.DestroyImmediate(rt);
    }

    /// <summary>Enabled renderers whose bounds meet the camera frustum, × their sub-meshes (an Editor proxy for draw calls).</summary>
    private static string CountVisible(Camera cam, string label)
    {
        var planes = GeometryUtility.CalculateFrustumPlanes(cam);
        int renderers = 0, draws = 0, chunks = 0, hazards = 0, dressing = 0;
        var byRoot = new Dictionary<string, int>();
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            if (!r.enabled || !r.gameObject.activeInHierarchy || !GeometryUtility.TestPlanesAABB(planes, r.bounds))
                continue;
            renderers++;
            draws += Mathf.Max(1, r.sharedMaterials.Length);
            var root = r.transform.root.name;
            byRoot[root] = byRoot.TryGetValue(root, out var n) ? n + 1 : 1;
            if (root == "MergedStatic") chunks++;
            else if (root == "Hazards") hazards++;
            else if (root == "Dressing") dressing++;
        }

        var sb = new StringBuilder($"- **{label}**: {renderers} visible renderers, {draws} renderer × material draws (MergedStatic {chunks}, Hazards {hazards}, Dressing {dressing}; by root:");
        foreach (var kv in byRoot)
            sb.Append($" {kv.Key} {kv.Value};");
        sb.Append(") — Editor frustum count, no Rushlings, no UI; not a GPU capture.\n");
        return sb.ToString();
    }

    private static string Arg(string name)
    {
        var a = System.Environment.GetCommandLineArgs();
        for (var i = 0; i < a.Length - 1; i++)
            if (a[i] == name)
                return a[i + 1];
        return null;
    }
}
