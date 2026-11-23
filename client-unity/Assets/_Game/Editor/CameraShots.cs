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
/// G4.10 camera proof (Editor, batch with graphics): per map the spawn view and one action view, each with the
/// <see cref="CameraRig.Classic"/> and <see cref="CameraRig.Close"/> presets, in both looks. A real
/// <see cref="RushlingVisuals"/> (body, lid, limbs) stands at the follow target; the camera is the preset's orbit pose (no
/// damping / look-ahead). Measured from the camera: the Rushling's share of the screen height (rule 12–15 %), Capsule
/// Run's finish arch in the spawn view, the Goo Tower summit beacon's look-up angle, the Lid Flip top shelf's corners in
/// view, and the first obstacle in the spawn view. Shots <c>cam-&lt;map&gt;-&lt;view&gt;-&lt;cam&gt;-&lt;look&gt;.png</c>, report
/// <c>cam-report.md</c>. <c>scripts/g48-unity-gfx.sh cam -executeMethod CameraShots.Run</c>.
/// </summary>
public static class CameraShots
{
    private struct View
    {
        public string Name, Scene;
        public int Spawn;               // spawn index (−1: Capsule Run's middle start (0, 0, 4))
        public bool FaceCentre;
        public Vector3 ActionFeet;      // a walkable spot near a hazard (raycast down from 40 m)
        public float ActionYaw;
    }

    private static readonly View[] Views =
    {
        new View { Name = "capsule-run", Scene = BlockPartyDashBuilder.ScenePath, Spawn = -1, ActionFeet = new Vector3(0f, 0f, 156f), ActionYaw = 0f },
        new View { Name = "goo-tower", Scene = GooTowerBuilder.ScenePath, Spawn = 5, ActionFeet = new Vector3(8f, 0f, 10.7f), ActionYaw = -90f },
        new View { Name = "star-fountain", Scene = StarScrambleBuilder.ScenePath, Spawn = 5, FaceCentre = true, ActionFeet = new Vector3(-9f, 0f, -13f), ActionYaw = 35f },
        new View { Name = "lid-flip", Scene = CrumbleShelfBuilder.ScenePath, Spawn = 0, FaceCentre = true, ActionFeet = new Vector3(-3.7f, 0f, -1.9f), ActionYaw = 60f },
    };

    [MenuItem("Fall Rush/Proof/Camera shots")]
    public static void Run()
    {
        var dir = CapsuleRunShots.Arg("-fallrushShotDir") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "../../docs/proof/g4"));
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder("# G4.10 camera (Editor: the preset's orbit pose, no damping; Rushling = body + lid + limbs)\n\n");
        sb.Append($"- classic: distance {CameraRig.Classic.Distance} m, elevation {CameraRig.Classic.Elevation}°, look-at +{CameraRig.Classic.LookOffsetY} m, FOV {CameraRig.Classic.Fov}°\n");
        sb.Append($"- close: distance {CameraRig.Close.Distance} m, elevation {CameraRig.Close.Elevation}°, look-at +{CameraRig.Close.LookOffsetY} m, FOV {CameraRig.Close.Fov}°\n\n");
        sb.Append("| map | view | cam | Rushling height (feet → dome top, share of the screen height) | check |\n|---|---|---|---|---|\n");
        foreach (var look in new[] { LookId.Soft, LookId.Candy })
        {
            LookState.Set(look);
            foreach (var v in Views)
                sb.Append(Shoot(v, dir, look == LookId.Soft ? "soft" : "candy", look == LookId.Soft));
        }

        LookState.Set(LookId.Soft);
        BoltCloseup(Path.GetFullPath(Path.Combine(dir, "../g2g3/bolt-closeup.png")));
        File.WriteAllText(Path.Combine(dir, "cam-report.md"), sb.ToString());
        Debug.Log("[shots] camera → " + dir + "\n" + sb);
    }

    private static string Shoot(View vw, string dir, string look, bool report)
    {
        var scene = EditorSceneManager.OpenScene(vw.Scene, OpenSceneMode.Single);
        MapInfo map = null;
        foreach (var root in scene.GetRootGameObjects())
            map ??= root.GetComponentInChildren<MapInfo>();
        if (map == null)
            throw new IOException("no MapInfo in " + vw.Scene);
        Physics.simulationMode = SimulationMode.Script;
        Physics.SyncTransforms();
        WorldLook.OnMapLoaded();
        var hz = map.BuildHazards(new RoundParams { MapId = map.MapId, Variant = 0, HazardSeed = 20261120 }, out _);
        var hv = new HazardViewDriver(map) { DrawInstanced = false };
        CapsuleRunShots._map = map;
        CapsuleRunShots.Pose(hv, hz, 0);
        var lids = new GameObject("ShotLids").transform;
        hv.LidView?.Snapshot(lids);

        var camGo = new GameObject("ShotCamera");
        var cam = camGo.AddComponent<Camera>();
        if (Camera.main != null)
            Camera.main.enabled = false;
        cam.clearFlags = RenderSettings.skybox != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
        cam.farClipPlane = 600f;
        cam.allowHDR = true;
        GraphicsQuality.PrepareShotCamera(cam);
        GraphicsQuality.ConfigurePipelineForShots(true, out var restore);
        var volume = GraphicsQuality.CreateShotVolume(true);
        GraphicsQuality.ApplyShadows(LookState.Current, true);

        CapsuleRunShots.Render(cam, Path.Combine(Path.GetTempPath(), "fallrush-cam-warm.png"), 320, 180); // warm-up: the first batch frame
        var go = new GameObject("CamRushling");
        var rv = go.AddComponent<RushlingVisuals>();
        rv.Build(0, 0, map.playerMaterial, map.unlitMaterial);
        rv.SetVisible(true);

        var sb = new StringBuilder();
        foreach (var view in new[] { "spawn", "action" })
        {
            Vector3 feet;
            float yaw;
            if (view == "spawn")
            {
                feet = vw.Spawn >= 0 ? map.spawns[vw.Spawn].position : new Vector3(0f, 0f, 4f);
                var fwd = vw.FaceCentre ? (-new Vector3(feet.x, 0f, feet.z)).normalized : (vw.Spawn >= 0 ? map.spawns[vw.Spawn].forward : Vector3.forward);
                yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            }
            else
            {
                feet = Physics.Raycast(new Vector3(vw.ActionFeet.x, 40f, vw.ActionFeet.z), Vector3.down, out var hit, 80f, ~0, QueryTriggerInteraction.Ignore) ? hit.point : vw.ActionFeet;
                yaw = vw.ActionYaw;
            }

            for (var k = 0; k < 4; k++)
                rv.Drive(feet, Vector3.zero, MotorState.Grounded, (byte)(Mathf.RoundToInt(yaw / (360f / 256f)) & 255), 0, 1f / 30f);
            foreach (var id in new[] { CameraRig.Id.Classic, CameraRig.Id.Close })
            {
                var p = CameraRig.Get(id);
                CameraRig.Pose(p, feet, yaw, out var eye, out var at);
                cam.fieldOfView = p.Fov;
                cam.aspect = 16f / 9f;
                cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(at - eye));
                CapsuleRunShots.Render(cam, Path.Combine(dir, $"cam-{vw.Name}-{view}-{CameraRig.Name(id)}-{look}.png"), 1280, 720);
                cam.aspect = 16f / 9f;
                if (!report)
                    continue;
                // The Rushling's height on screen: its feet to the top of its dome (1.0 m), on its centre line.
                var share = cam.WorldToViewportPoint(feet + Vector3.up * 1.0f).y - cam.WorldToViewportPoint(feet).y;
                var check = view == "spawn" ? SpawnCheck(vw, map, cam, feet, yaw) : "";
                sb.Append($"| {vw.Name} | {view} | {CameraRig.Name(id)} | {share * 100f:F1} % | {check} |\n");
            }
        }

        Object.DestroyImmediate(go);
        var limbs = GameObject.Find("Limbs");
        if (limbs != null)
            Object.DestroyImmediate(limbs);
        Object.DestroyImmediate(lids.gameObject);
        Object.DestroyImmediate(camGo);
        if (volume != null)
            Object.DestroyImmediate(volume);
        restore(); // the Editor quality level / grading mode back as they were (nothing saved to ProjectSettings)
        return sb.ToString();
    }

    /// <summary>G4.10: the G2/G3 Bolt close-up, re-shot (the old file showed a bumper): Bolt (preset 0) in the lobby, face on.</summary>
    private static void BoltCloseup(string path)
    {
        var scene = EditorSceneManager.OpenScene(LobbyBuilder.ScenePath, OpenSceneMode.Single);
        MapInfo map = null;
        foreach (var root in scene.GetRootGameObjects())
            map ??= root.GetComponentInChildren<MapInfo>();
        if (map == null)
            return;
        WorldLook.OnMapLoaded();
        var camGo = new GameObject("ShotCamera");
        var cam = camGo.AddComponent<Camera>();
        if (Camera.main != null)
            Camera.main.enabled = false;
        cam.clearFlags = RenderSettings.skybox != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
        cam.farClipPlane = 600f;
        cam.fieldOfView = 35f;
        cam.allowHDR = true;
        GraphicsQuality.PrepareShotCamera(cam);
        GraphicsQuality.ConfigurePipelineForShots(true, out var restore);
        var volume = GraphicsQuality.CreateShotVolume(true);
        GraphicsQuality.ApplyShadows(LookState.Current, true);
        var go = new GameObject("Bolt");
        var rv = go.AddComponent<RushlingVisuals>();
        rv.Build(0, 0, map.playerMaterial, map.unlitMaterial);
        rv.SetVisible(true);
        var feet = new Vector3(-6f, 0f, -6f);
        if (Physics.Raycast(feet + Vector3.up * 5f, Vector3.down, out var hit, 20f, ~0, QueryTriggerInteraction.Ignore))
            feet = hit.point;
        for (var k = 0; k < 6; k++)
            rv.Drive(feet, Vector3.zero, MotorState.Grounded, 128, 0, 1f / 30f); // facing −z (towards the camera)
        var eye = feet + new Vector3(0.7f, 1.05f, -2.4f);
        cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(feet + Vector3.up * 0.6f - eye));
        CapsuleRunShots.Render(cam, Path.Combine(Path.GetTempPath(), "fallrush-bolt-warm.png"), 320, 180);
        CapsuleRunShots.Render(cam, path, 960, 540);
        Object.DestroyImmediate(go);
        var limbs = GameObject.Find("Limbs");
        if (limbs != null)
            Object.DestroyImmediate(limbs);
        Object.DestroyImmediate(camGo);
        if (volume != null)
            Object.DestroyImmediate(volume);
        restore();
    }

    private static string Vp(Camera cam, Vector3 p)
    {
        var v = cam.WorldToViewportPoint(p);
        var inView = v.z > 0f && v.x >= 0f && v.x <= 1f && v.y >= 0f && v.y <= 1f;
        return $"({v.x:F2}, {v.y:F2}){(inView ? "" : " OUT")}";
    }

    private static string SpawnCheck(View vw, MapInfo map, Camera cam, Vector3 feet, float yaw)
    {
        var sb = new StringBuilder();
        switch (vw.Name)
        {
            case "capsule-run":
            {
                var top = GameObject.Find("ArchTop_4");
                if (top != null)
                    sb.Append($"finish arch top at viewport {Vp(cam, top.transform.position + Vector3.up * 0.7f)}; ");
                break;
            }
            case "goo-tower":
            {
                var flag = GameObject.Find("SummitBeaconFlag");
                if (flag != null)
                {
                    var v = cam.WorldToViewportPoint(flag.transform.position);
                    var dir = flag.transform.position - cam.transform.position;
                    var up = Vector3.Angle(new Vector3(dir.x, 0f, dir.z), dir) * Mathf.Sign(dir.y);
                    var pitch = -cam.transform.eulerAngles.x;
                    if (pitch < -180f)
                        pitch += 360f;
                    var need = up - (pitch + cam.fieldOfView * 0.5f * 0.9f);
                    sb.Append($"summit beacon at viewport ({v.x:F2}, {v.y:F2}); look-up needed ≈ {Mathf.Max(0f, need):F0}°; ");
                }

                break;
            }
            case "lid-flip":
            {
                var ok = 0;
                const float h = 8.37f;
                foreach (var c in new[] { new Vector3(-h, 6f, -h), new Vector3(h, 6f, -h), new Vector3(-h, 6f, h), new Vector3(h, 6f, h) })
                {
                    var v = cam.WorldToViewportPoint(c);
                    if (v.z > 0f && v.x >= 0f && v.x <= 1f && v.y >= 0f && v.y <= 1f)
                        ok++;
                }

                sb.Append($"top shelf corners in view: {ok}/4; ");
                break;
            }
        }

        // The first obstacle ahead: the hazard whose drawn parts' centre is nearest the spawn, in front of it.
        Transform first = null;
        var firstAt = Vector3.zero;
        var best = float.MaxValue;
        var fwd = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
        foreach (var h in Object.FindObjectsByType<HazardAuthoring>(FindObjectsSortMode.None))
        {
            var rs = h.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0)
                continue;
            var bb = rs[0].bounds;
            foreach (var r in rs)
                bb.Encapsulate(r.bounds);
            var d = bb.center - feet;
            if (Vector3.Dot(new Vector3(d.x, 0f, d.z), fwd) <= 1f)
                continue;
            if (d.sqrMagnitude < best)
            {
                best = d.sqrMagnitude;
                first = h.transform;
                firstAt = bb.center;
            }
        }

        if (first != null)
            sb.Append($"first obstacle {first.name} at viewport {Vp(cam, firstAt)}");
        return sb.ToString();
    }
}
#endif
