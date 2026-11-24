#if !UNITY_SERVER
using System.IO;
using System.Text;
using FallRush.Net;
using FallRush.Shared;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 2026-11-24 playtest report ("k4 blocks are slippy / sticky, can't jump them"): drives the real <see cref="KinematicMotor"/>
/// over Goo Tower's k4 Narrow Steps slabs (no hazards) and reports (a) drift while standing still on each slab, (b) the
/// run-and-jump from each slab to the next for jump points 0–1.0 m before the edge, and wall-stick ticks (input held,
/// airborne, horizontal speed under 1 m/s). Batch: -executeMethod GooTowerFeelCheck.Run [-fallrushShotDir dir]
/// </summary>
public static class GooTowerFeelCheck
{
    [MenuItem("Fall Rush/Proof/Goo Tower k4 feel check")]
    public static void Run()
    {
        var dir = CapsuleRunShots.Arg("-fallrushShotDir") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "../../docs/proof/p7/goo"));
        Directory.CreateDirectory(dir);
        var scene = EditorSceneManager.OpenScene(ProjectSetup.ScenesDir + "/Map_GooTower.unity", OpenSceneMode.Single);
        MapInfo map = null;
        foreach (var root in scene.GetRootGameObjects())
            map ??= root.GetComponentInChildren<MapInfo>();
        Physics.simulationMode = SimulationMode.Script;
        Physics.SyncTransforms();
        var cfg = MovementConfig.Default;
        var probeParent = new GameObject("Probe").transform;
        var p = RoundParams.FromDevSeed(map.definition.mapId, 1);
        var world = map.CreateWorld(probeParent, cfg, in p, out _);
        var motor = new KinematicMotor(cfg);
        var sb = new StringBuilder("# Goo Tower k4 Narrow Steps: motor check (real KinematicMotor, no hazards)\n\n");
        sb.Append($"- motor: max slope {MovementConfigData.Ground_MaxSlopeDeg}°, step offset {MovementConfigData.Ground_StepOffset} mm, ledge tolerance {MovementConfigData.Ground_LedgeTolerance} mm\n\n");

        foreach (var col in new[] { "In", "Out" })
        {
            var slabs = new Transform[3];
            for (var j = 0; j < 3; j++)
            {
                var go = GameObject.Find($"S4_Col{col}{j}");
                slabs[j] = go != null ? go.transform : null;
            }

            if (slabs[0] == null)
            {
                sb.Append($"- column {col}: slabs not found\n");
                continue;
            }

            sb.Append($"## column {col}\n\n| slab | pitch | top width × length | idle 3 s drift (m) | still grounded |\n|---|---|---|---|---|\n");
            for (var j = 0; j < 3; j++)
            {
                var t = slabs[j];
                var bc = t.GetComponent<BoxCollider>();
                var size = Vector3.Scale(bc != null ? bc.size : Vector3.one, t.lossyScale);
                var top = t.position + t.up * (size.y * 0.5f);
                var pitch = Vector3.Angle(t.up, Vector3.up);
                var s = Place(top + Vector3.up * 0.02f, t.forward);
                var start = V(s.Position);
                uint tick = 1;
                for (var i = 0; i < 90; i++, tick++)
                {
                    var cmd = new InputCmd { Seq = tick, Tick = tick, CamYaw = Yaw16(t.forward) };
                    world.BeginTick(tick);
                    motor.Step(ref s, in cmd, cfg.Dt, world);
                }

                var drift = Vector3.Distance(start, V(s.Position));
                sb.Append($"| {j} | {pitch:F1}° | {size.x:F2} × {size.z:F2} m | {drift:F3} | {(s.Has(MotorState.Grounded) ? "yes" : "NO")} |\n");
            }

            sb.Append("\n| from → to | jump point before the edge (m): 0.0 … 1.0 | best | wall-stick ticks (sum) |\n|---|---|---|---|\n");
            for (var j = 0; j < 2; j++)
            {
                var a = slabs[j];
                var b = slabs[j + 1];
                var sizeA = Vector3.Scale(a.GetComponent<BoxCollider>().size, a.lossyScale);
                var sizeB = Vector3.Scale(b.GetComponent<BoxCollider>().size, b.lossyScale);
                var topA = a.position + a.up * (sizeA.y * 0.5f);
                var topB = b.position + b.up * (sizeB.y * 0.5f);
                var fwd = a.forward;
                var flat = new Vector3(fwd.x, 0f, fwd.z).normalized;
                var edgeA = topA + fwd * (sizeA.z * 0.5f);
                var row = new StringBuilder();
                var ok = 0;
                var stickSum = 0;
                for (var k = 0; k <= 10; k++)
                {
                    var jumpAt = k * 0.1f;
                    var s = Place(topA - fwd * (sizeA.z * 0.5f - 0.4f) + Vector3.up * 0.02f, fwd);
                    uint tick = 1;
                    var jumped = false;
                    var landed = false;
                    var missed = false;
                    var stick = 0;
                    for (var i = 0; i < 75; i++, tick++)
                    {
                        var pos = V(s.Position);
                        var toEdge = Vector3.Dot(edgeA - pos, flat);
                        var press = !jumped && toEdge <= jumpAt && s.Has(MotorState.Grounded);
                        if (press)
                            jumped = true;
                        var cmd = new InputCmd
                        {
                            Seq = tick, Tick = tick, CamYaw = Yaw16(fwd),
                            MoveX = (sbyte)Mathf.RoundToInt(flat.x * 127f), MoveY = (sbyte)Mathf.RoundToInt(flat.z * 127f),
                            Buttons = (byte)((jumped && i < 200 ? InputCmd.JumpHeld : 0) | (press ? InputCmd.JumpPressed : 0)),
                        };
                        world.BeginTick(tick);
                        motor.Step(ref s, in cmd, cfg.Dt, world);
                        var hv = new Vector2(s.Velocity.X, s.Velocity.Z).magnitude;
                        if (jumped && s.Has(MotorState.Grounded))
                        {
                            // Landed: on b's top (over its footprint, at its height) = the jump made it.
                            var lp = b.InverseTransformPoint(V(s.Position));
                            if (Mathf.Abs(lp.x) <= 0.5f + 0.05f / b.lossyScale.x && Mathf.Abs(lp.z) <= 0.5f + 0.05f / b.lossyScale.z && V(s.Position).y > topB.y - 0.3f)
                                landed = true;
                            else if (!landed && i > 3)
                                missed = true;
                        }
                        if (jumped && !s.Has(MotorState.Grounded) && hv < 1f)
                            stick++;
                    }

                    // Landed on b: over its footprint and at (about) its top.
                    var end = V(s.Position);
                    var local = b.InverseTransformPoint(end);
                    var onB = Mathf.Abs(local.x) * b.lossyScale.x <= sizeB.x * 0.5f + 0.05f && Mathf.Abs(local.z) * b.lossyScale.z <= sizeB.z * 0.5f + 0.05f &&
                              end.y > topB.y - sizeB.z * 0.3f && s.Has(MotorState.Grounded);
                    var pastB = Vector3.Dot(end - (topB + fwd * (sizeB.z * 0.5f)), flat) > 0f && end.y > topB.y - 1f;
                    var good = landed || (!missed && (onB || pastB));
                    ok += good ? 1 : 0;
                    stickSum += stick;
                    row.Append(good ? "✓" : "✗");
                }

                sb.Append($"| {j} → {j + 1} | {row} | {ok}/11 | {stickSum} |\n");
            }

            sb.Append('\n');
        }

        Object.DestroyImmediate(probeParent.gameObject);
        File.WriteAllText(Path.Combine(dir, "k4-feel.md"), sb.ToString());
        Debug.Log("[goo] k4 feel → " + dir + "\n" + sb);
    }

    private static MotorState Place(Vector3 feet, Vector3 fwd)
    {
        var s = new MotorState { Position = new Vec3(feet.x, feet.y, feet.z), Yaw = Yaw256.FromCamYaw(Yaw16(fwd)) };
        s.Set(MotorState.Grounded, true);
        return s;
    }

    private static ushort Yaw16(Vector3 f)
    {
        var deg = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        return (ushort)(Mathf.RoundToInt(Mathf.Repeat(deg, 360f) / 360f * 65536f) & 0xFFFF);
    }

    private static Vector3 V(Vec3 v) => new Vector3(v.X, v.Y, v.Z);
    /// <summary>The respawn spots (OC-51) as checkpoint circles, listed and shot from 4 sides. Needs graphics.</summary>
    public static void Spots()
    {
        var dir = CapsuleRunShots.Arg("-fallrushShotDir") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "../../docs/proof/p7/goo"));
        Directory.CreateDirectory(dir);
        var scene = EditorSceneManager.OpenScene(ProjectSetup.ScenesDir + "/Map_GooTower.unity", OpenSceneMode.Single);
        MapInfo map = null;
        foreach (var root in scene.GetRootGameObjects())
            map ??= root.GetComponentInChildren<MapInfo>();
        FallRush.Client.WorldLook.OnMapLoaded();
        var holder = new GameObject("MarkersHolder").transform;
        var m = FallRush.Client.CheckpointMarkers.Build(map, holder);
        var spots = RulesFactory.RespawnSpots(map.botRoutes);
        var sb = new StringBuilder($"# Goo Tower respawn spots (OC-51, from the bot routes): {spots.Length / 4}\n\n| # | x | y | z |\n|---|---|---|---|\n");
        for (var j = 0; j + 3 < spots.Length; j += 4)
            sb.Append($"| {j / 4} | {spots[j] / 1000f:F2} | {spots[j + 1] / 1000f:F2} | {spots[j + 2] / 1000f:F2} |\n");
        File.WriteAllText(Path.Combine(dir, "spots.md"), sb.ToString());
        var camGo = new GameObject("ShotCamera");
        var cam = camGo.AddComponent<Camera>();
        if (Camera.main != null)
            Camera.main.enabled = false;
        cam.clearFlags = RenderSettings.skybox != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
        cam.farClipPlane = 600f;
        cam.fieldOfView = 50f;
        cam.allowHDR = true;
        FallRush.Client.GraphicsQuality.PrepareShotCamera(cam);
        FallRush.Client.GraphicsQuality.ConfigurePipelineForShots(true, out var restore);
        var volume = FallRush.Client.GraphicsQuality.CreateShotVolume(true);
        CapsuleRunShots.Render(cam, Path.Combine(Path.GetTempPath(), "fr-goo-warm.png"), 160, 120);
        var i = 0;
        foreach (var eye in new[] { new Vector3(30f, 20f, 0f), new Vector3(0f, 26f, 30f), new Vector3(-30f, 30f, 0f), new Vector3(0f, 34f, -30f) })
        {
            cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(new Vector3(0f, 10f, 0f) - eye));
            CapsuleRunShots.Render(cam, Path.Combine(dir, $"checkpoints-{i++}.png"), 1280, 800);
        }

        // One close-up on the first spot.
        if (spots.Length >= 4)
        {
            var at = new Vector3(spots[0] / 1000f, spots[1] / 1000f, spots[2] / 1000f);
            var eye = at + new Vector3(3.5f, 2.2f, 3.5f);
            cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(at - eye));
            CapsuleRunShots.Render(cam, Path.Combine(dir, "checkpoint-close.png"), 1280, 800);
        }

        if (volume != null)
            Object.DestroyImmediate(volume.gameObject);
        restore();
        Debug.Log("[goo] spots → " + dir + "\n" + sb);
    }
    /// <summary>The route-cost QA (RouteQa) on Goo Tower from the Editor: -fallrushQaRuns n -fallrushQAOut path.</summary>
    public static void Routes()
    {
        var scene = EditorSceneManager.OpenScene(ProjectSetup.ScenesDir + "/Map_GooTower.unity", OpenSceneMode.Single);
        Physics.simulationMode = SimulationMode.Script;
        Physics.SyncTransforms();
        MapInfo map = null;
        foreach (var root in scene.GetRootGameObjects())
            map ??= root.GetComponentInChildren<MapInfo>();
        var args = System.Environment.GetCommandLineArgs();
        var runs = 40;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "-fallrushQaRuns")
                int.TryParse(args[i + 1], out runs);
        RouteQa.FallLogSection = 4; // k4 Narrow Steps: where do the falls happen?
        RouteQa.FallLog.Clear();
        RouteQa.RunAll(map, runs);
        var outArg = CapsuleRunShots.Arg("-fallrushQAOut");
        if (outArg != null)
            File.WriteAllLines(Path.ChangeExtension(outArg, ".k4-falls.txt"), RouteQa.FallLog);
        RouteQa.FallLogSection = -1;
    }
}
#endif
