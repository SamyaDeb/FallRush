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
/// G4.8z proof: the knock → tumble → get-up of a real <see cref="RushlingVisuals"/> (limbs, face, lid pop, the dizzy swirls)
/// on Capsule Run next to the Mallet Run (hard knock profile) and the Sweep Pad spinner (soft profile), in both looks. The
/// motion is <see cref="KnockPreview"/> (a scripted knock with the profile's push / lift / stun ticks, not a sim replay).
/// Every second tick is one cell of a contact sheet (side camera, 5.5 m); the report lists, per frame, the body's lowest
/// point above the floor and its scale (the same numbers the EditMode stun test asserts).
/// Batch with graphics: <c>scripts/g48-unity-gfx.sh stun -executeMethod StunShots.Run [-fallrushShotDir dir]</c>.
/// </summary>
public static class StunShots
{
    [MenuItem("Fall Rush/Proof/Stun sequence shots")]
    public static void Run()
    {
        var dir = CapsuleRunShots.Arg("-fallrushShotDir") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "../../docs/proof/g4"));
        Directory.CreateDirectory(dir);
        var report = new StringBuilder("# G4.8z stun sequence (Editor, scripted knock; body lowest point above the floor, min scale)\n\n");
        foreach (var look in new[] { LookId.Soft, LookId.Candy })
        {
            LookState.Set(look);
            var name = look == LookId.Soft ? "soft" : "candy";
            report.Append(Shoot(dir, "mallet", true, new Vector3(0f, 0f, 161f), Vector3.back, name)); // just past the first mallet (z 158), knocked back down the run
            report.Append(Shoot(dir, "spinner", false, new Vector3(-6f, 0f, 131f), Vector3.left, name));
        }

        LookState.Set(LookId.Soft);
        File.WriteAllText(Path.Combine(dir, "stun-sequence-report.md"), report.ToString());
        Debug.Log("[shots] stun sequence → " + dir + "\n" + report);
    }

    private static string Shoot(string dir, string label, bool hard, Vector3 at, Vector3 away, string look)
    {
        var scene = EditorSceneManager.OpenScene(BlockPartyDashBuilder.ScenePath, OpenSceneMode.Single);
        MapInfo map = null;
        foreach (var root in scene.GetRootGameObjects())
            map ??= root.GetComponentInChildren<MapInfo>();
        if (map == null)
            throw new IOException("no MapInfo");
        Physics.simulationMode = SimulationMode.Script;
        Physics.SyncTransforms();
        WorldLook.OnMapLoaded();
        var hz = map.BuildHazards(new RoundParams { MapId = map.MapId, Variant = 0, HazardSeed = 20261120 }, out _);
        var view = new HazardViewDriver(map) { DrawInstanced = false };
        CapsuleRunShots._map = map;
        CapsuleRunShots.Pose(view, hz, 790);

        // The walkable top under the sample point.
        var floor = Physics.Raycast(new Vector3(at.x, 9f, at.z), Vector3.down, out var hit, 60f, ~0, QueryTriggerInteraction.Ignore) ? hit.point.y : 0f;
        var start = new Vector3(at.x, floor, at.z);

        var camGo = new GameObject("ShotCamera");
        var cam = camGo.AddComponent<Camera>();
        if (Camera.main != null)
            Camera.main.enabled = false;
        cam.clearFlags = RenderSettings.skybox != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
        cam.farClipPlane = 600f;
        cam.fieldOfView = 40f;
        cam.allowHDR = true;
        GraphicsQuality.PrepareShotCamera(cam);
        GraphicsQuality.ConfigurePipelineForShots(true, out var restore);
        var volume = GraphicsQuality.CreateShotVolume(true);
        GraphicsQuality.ApplyShadows(LookState.Current, true);

        var go = new GameObject("StunRushling");
        var v = go.AddComponent<RushlingVisuals>();
        v.Build(0, 0, map.playerMaterial, map.unlitMaterial);
        v.SetVisible(true);
        var squash = go.transform.Find("Tilt/Squash");
        var path = KnockPreview.Path(hard, start, away);
        CapsuleRunShots.Render(cam, Path.Combine(Path.GetTempPath(), "fallrush-stun-warm.png"), 320, 240); // warm-up: the first batch frame

        const int cw = 320, chh = 240, cols = 6;
        var cells = (path.Length + 1) / 2;
        var rows = (cells + cols - 1) / cols;
        var sheet = new Texture2D(cw * cols, chh * rows, TextureFormat.RGB24, false);
        var tmp = Path.Combine(Path.GetTempPath(), "fallrush-stun-cell.png");
        var sb = new StringBuilder($"## {label} ({(hard ? "hard" : "soft")} knock), {look}\n\n| tick | stun | lowest above floor (m) | min scale | swirls |\n|---|---|---|---|---|\n");
        var worst = 9f;
        var cell = 0;
        // The camera sits to the side of the knock line, framing the whole hop.
        var side = Vector3.Cross(Vector3.up, away).normalized;
        var mid = start + away * (hard ? 1.6f : 1.0f) + Vector3.up * 0.7f;
        var eye = mid - side * 5.5f + Vector3.up * 1.2f - away * 0.3f; // the side away from the hazard bar
        for (var i = 0; i < path.Length; i++)
        {
            var f = path[i];
            v.Drive(f.Feet, f.Vel, f.Flags, (byte)(Mathf.RoundToInt(Mathf.Atan2(-away.x, -away.z) * Mathf.Rad2Deg / (360f / 256f)) & 255), f.Stun, KnockPreview.Dt);
            var sc = squash.lossyScale;
            var a = squash.TransformPoint(new Vector3(0f, ProcMesh.LowerCenter, 0f));
            var b = squash.TransformPoint(new Vector3(0f, ProcMesh.UpperCenter, 0f));
            var lowest = Mathf.Min(a.y, b.y) - ProcMesh.R * Mathf.Min(sc.x, sc.z) - floor;
            worst = Mathf.Min(worst, lowest);
            var swirls = squash.Find("Swirls").gameObject.activeInHierarchy;
            sb.Append($"| {i} | {f.Stun} | {lowest:F3} | {Mathf.Min(sc.x, Mathf.Min(sc.y, sc.z)):F2} | {(swirls ? "on" : "-")} |\n");
            if ((i & 1) != 0)
                continue;
            cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(mid - eye));
            CapsuleRunShots.Render(cam, tmp, cw, chh);
            var t = new Texture2D(2, 2);
            t.LoadImage(File.ReadAllBytes(tmp));
            var cx = cell % cols;
            var cy = rows - 1 - cell / cols;
            sheet.SetPixels(cx * cw, cy * chh, cw, chh, t.GetPixels());
            Object.DestroyImmediate(t);
            cell++;
        }

        sheet.Apply();
        File.WriteAllBytes(Path.Combine(dir, $"stun-sequence-{label}-{look}.png"), sheet.EncodeToPNG());
        Object.DestroyImmediate(sheet);
        sb.Append($"\n- lowest point of the body over the whole sequence: **{worst:F3} m** above the floor ({(worst > -0.06f ? "PASS" : "FAIL")}: never below −0.06 m)\n- sheet `stun-sequence-{label}-{look}.png`: every second tick, left to right, top to bottom\n\n");
        Object.DestroyImmediate(go);
        var limbs = GameObject.Find("Limbs");
        if (limbs != null)
            Object.DestroyImmediate(limbs);
        Object.DestroyImmediate(camGo);
        if (volume != null)
            Object.DestroyImmediate(volume.gameObject);
        restore(); // the Editor quality level / grading mode back as they were (nothing saved to ProjectSettings)
        return sb.ToString();
    }
}
#endif
