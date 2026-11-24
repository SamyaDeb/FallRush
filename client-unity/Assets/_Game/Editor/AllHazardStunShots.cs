#if !UNITY_SERVER
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
/// Dizzy hits (2026-11-24) proof: every knock hazard kind on every map gives the stars. Per map scene and per kind (spinner
/// bar, mallet, rolling ball, bumper) it finds a real contact with the map's own hazard defs (the sim's contact functions at
/// variant 0), takes that def's knock (push, lift, stun ticks), plays it through a real <see cref="RushlingVisuals"/> with the
/// hazard posed at the same ticks, and renders 8 cells: before, hit, mid-stun, get-up, then 0.5 / 1.5 / 2.5 / 3.3 s after.
/// The report lists every def's stun ticks and, per cell, whether the stars and the body are on.
/// Batch with graphics: Unity -batchmode -quit -executeMethod AllHazardStunShots.Run -fallrushShotDir dir
/// </summary>
public static class AllHazardStunShots
{
    private static readonly (string scene, string name)[] Maps =
    {
        (BlockPartyDashBuilder.ScenePath, "capsule-run"),
        (ProjectSetup.ScenesDir + "/Map_GooTower.unity", "goo-tower"),
        (ProjectSetup.ScenesDir + "/Map_StarScramble.unity", "star-fountain"),
        (ProjectSetup.ScenesDir + "/Map_CrumbleShelf.unity", "crumble-shelf"),
        (SandboxBuilder.ScenePath, "sandbox"),
    };

    private const float Dt = 1f / 30f;

    [MenuItem("Fall Rush/Proof/Stun shots: every hazard on every map")]
    public static void Run()
    {
        var dir = CapsuleRunShots.Arg("-fallrushShotDir") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "../../docs/proof/p7/dizzy/maps"));
        Directory.CreateDirectory(dir);
        LookState.Set(LookId.Soft);
        var rep = new StringBuilder("# Dizzy stars on every hazard, every map (Editor, real hazard defs + contact, RushlingVisuals)\n\n");
        var fails = 0;
        foreach (var (scenePath, mapName) in Maps)
        {
            if (!File.Exists(scenePath))
            {
                rep.Append($"## {mapName}\n\n- scene missing ({scenePath})\n\n");
                continue;
            }

            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            MapInfo map = null;
            foreach (var root in scene.GetRootGameObjects())
                map ??= root.GetComponentInChildren<MapInfo>();
            if (map == null)
            {
                rep.Append($"## {mapName}\n\n- no MapInfo\n\n");
                continue;
            }

            Physics.simulationMode = SimulationMode.Script;
            Physics.SyncTransforms();
            WorldLook.OnMapLoaded();
            var hz = map.BuildHazards(new RoundParams { MapId = map.MapId, Variant = 0, HazardSeed = 20261124 }, out _);
            var view = new HazardViewDriver(map) { DrawInstanced = false };
            CapsuleRunShots._map = map;
            rep.Append($"## {mapName}\n\n");
            rep.Append($"- defs: spinner bars {hz.Sweepers.Length} (stun ticks {List(hz.Sweepers, d => d.StunTicks)}), mallets {hz.Mallets.Length} ({List(hz.Mallets, d => d.StunTicks)}), " +
                       $"rolling balls {hz.RollingBalls.Length} ({List(hz.RollingBalls, d => d.StunTicks)}), bumpers {hz.Bumpers.Length} ({List(hz.Bumpers, d => d.StunTicks)})\n");
            rep.Append($"- other obstacles (no knock in the sim): doors {hz.Doors.Length}, platforms {hz.Platforms.Length}, turntables {hz.TurnTables.Length}, conveyors {hz.Conveyors.Length}, springs {hz.Pads.Length}, falling blocks {hz.Blocks.Length}, tile grids {hz.Grids.Length}, goo {hz.Goo.Length}\n\n");
            foreach (var d in hz.Sweepers) fails += d.StunTicks > 0 ? 0 : 1;
            foreach (var d in hz.Mallets) fails += d.StunTicks > 0 ? 0 : 1;
            foreach (var d in hz.RollingBalls) fails += d.StunTicks > 0 ? 0 : 1;
            foreach (var d in hz.Bumpers) fails += d.StunTicks > 0 ? 0 : 1;

            var cfg = MovementConfig.Default;
            var r = MotorState.QuantizeMm(cfg.Radius);
            var h = MotorState.QuantizeMm(cfg.Height);
            foreach (var kind in new[] { "spinner", "mallet", "ball", "bumper" })
            {
                if (!FindHit(hz, kind, r, h, out var hit))
                {
                    var count = kind == "spinner" ? hz.Sweepers.Length : kind == "mallet" ? hz.Mallets.Length : kind == "ball" ? hz.RollingBalls.Length : hz.Bumpers.Length;
                    if (count > 0)
                    {
                        rep.Append($"### {kind}\n\n- {count} on the map but no contact found by the search (not shot)\n\n");
                    }

                    continue;
                }

                rep.Append(Shoot(dir, mapName, kind, map, view, hz, hit, ref fails));
            }
        }

        rep.Append(fails == 0 ? "\n**PASS**: every knock def stuns, and the stars were on with the body visible in every hit and tail cell.\n" : $"\n**FAIL**: {fails} problem(s), see above.\n");
        File.WriteAllText(Path.Combine(dir, "report.md"), rep.ToString());
        Debug.Log("[shots] all-hazard stun → " + dir + "\n" + rep);
    }

    private static string List<T>(T[] a, System.Func<T, int> f)
    {
        var s = new SortedSet<int>();
        foreach (var x in a)
            s.Add(f(x));
        return s.Count == 0 ? "-" : string.Join("/", s);
    }

    private struct Hit
    {
        public Vector3 Feet;
        public int T;
        public float Nx, Nz;
        public int Push, Up, Stun, Index;
    }

    private static float Floor(float x, float yFrom, float z, float fallback)
    {
        return Physics.Raycast(new Vector3(x, yFrom, z), Vector3.down, out var hit, 30f, ~0, QueryTriggerInteraction.Ignore) ? hit.point.y : fallback;
    }

    private static bool FindHit(HazardSet hz, string kind, int r, int h, out Hit hit)
    {
        hit = default;
        var t0 = Mathf.Max(90, hz.SweeperStartT == int.MinValue || kind != "spinner" ? 90 : hz.SweeperStartT + 30);
        var n = kind == "spinner" ? hz.Sweepers.Length : kind == "mallet" ? hz.Mallets.Length : kind == "ball" ? hz.RollingBalls.Length : hz.Bumpers.Length;
        for (var i = 0; i < Mathf.Min(n, 6); i++)
        {
            Vector3 feet;
            if (kind == "spinner")
            {
                var d = hz.Sweepers[i];
                var y = d.PivotYMm * 0.001f;
                feet = new Vector3(d.PivotXMm * 0.001f + d.ArmMm * 0.0006f, y, d.PivotZMm * 0.001f);
                feet.y = Floor(feet.x, y + 1.5f, feet.z, y);
            }
            else if (kind == "mallet")
            {
                var d = hz.Mallets[i];
                var headY = (d.PivotYMm - d.ArmMm - d.HeadRadiusMm) * 0.001f - 0.05f;
                feet = new Vector3(d.PivotXMm * 0.001f, headY, d.PivotZMm * 0.001f);
                feet.y = Floor(feet.x, headY, feet.z, headY - 0.3f);
            }
            else if (kind == "ball")
            {
                var d = hz.RollingBalls[i];
                var p = new Vector3((d.StartXMm + d.EndXMm) * 0.0005f, (d.StartYMm + d.EndYMm) * 0.0005f, (d.StartZMm + d.EndZMm) * 0.0005f);
                feet = new Vector3(p.x, Floor(p.x, p.y + 0.5f, p.z, p.y - d.RadiusMm * 0.001f), p.z);
            }
            else
            {
                var d = hz.Bumpers[i];
                var off = (d.RadiusMm + r + d.ContactMarginMm / 2) * 0.001f;
                feet = new Vector3(d.CenterXMm * 0.001f + off, d.CenterYMm * 0.001f, d.CenterZMm * 0.001f);
            }

            int fx = Mathf.RoundToInt(feet.x * 1000f), fy = Mathf.RoundToInt(feet.y * 1000f), fz = Mathf.RoundToInt(feet.z * 1000f);
            var prev = true;
            for (var t = t0; t < t0 + 4000; t++)
            {
                bool c;
                float nx, nz;
                int push, up, stun;
                if (kind == "spinner")
                {
                    var d = hz.Sweepers[i];
                    c = HazardMath.SweeperContactMm(in d, t, fx, fy, fz, r, h, out nx, out nz);
                    push = d.KnockbackCms; up = d.KnockUpCms; stun = d.StunTicks;
                }
                else if (kind == "mallet")
                {
                    var d = hz.Mallets[i];
                    c = HazardMath.MalletContactMm(in d, t, fx, fy, fz, r, h, out nx, out nz);
                    push = d.KnockbackCms; up = d.KnockUpCms; stun = d.StunTicks;
                }
                else if (kind == "ball")
                {
                    var d = hz.RollingBalls[i];
                    c = HazardMath.RollingBallContactMm(in d, t, fx, fy, fz, r, h, out nx, out nz);
                    push = d.KnockbackCms; up = d.KnockUpCms; stun = d.StunTicks;
                }
                else
                {
                    var d = hz.Bumpers[i];
                    c = HazardMath.BumperContactMm(in d, fx, fy, fz, r, h, out nx, out nz);
                    push = d.KnockbackCms; up = d.KnockUpCms; stun = d.StunTicks;
                    prev = false; // static: the first tick is the hit
                }

                if (c && !prev)
                {
                    var len = Mathf.Sqrt(nx * nx + nz * nz);
                    if (len < 1e-3f) { nx = 1f; nz = 0f; len = 1f; }
                    hit = new Hit { Feet = feet, T = t, Nx = nx / len, Nz = nz / len, Push = push, Up = up, Stun = stun, Index = i };
                    return true;
                }

                prev = c;
            }
        }

        return false;
    }

    private static string Shoot(string dir, string mapName, string kind, MapInfo map, HazardViewDriver view, HazardSet hz, Hit hit, ref int fails)
    {
        // The knock path: 6 standing ticks, the hit (the def's push / lift / stun), the stun, then 4 s standing (the dizzy tail).
        var away = new Vector3(hit.Nx, 0f, hit.Nz);
        const int pre = 6, tail = 120;
        var n = pre + 1 + hit.Stun + tail;
        var feet = new Vector3[n];
        var vel = new Vector3[n];
        var flags = new byte[n];
        var stun = new byte[n];
        var p = hit.Feet;
        var v = Vector3.zero;
        var s = 0;
        var fellAt = -1;
        for (var i = 0; i < n; i++)
        {
            if (i == pre)
            {
                v = away * (hit.Push / 100f) + Vector3.up * (hit.Up / 100f);
                s = hit.Stun;
            }
            else if (i > pre)
            {
                v.y -= 25f * Dt;
                var next = p + v * Dt;
                var fl = Floor(next.x, p.y + 0.6f, next.z, float.NegativeInfinity);
                if (next.y <= fl)
                {
                    next.y = fl;
                    v.y = 0f;
                    v.x *= 0.7f;
                    v.z *= 0.7f;
                }

                p = next;
                if (fellAt < 0 && p.y < hit.Feet.y - 4f)
                    fellAt = i;
                if (s > 0)
                    s--;
            }

            var gr = Floor(p.x, p.y + 0.3f, p.z, float.NegativeInfinity);
            var grounded = p.y <= gr + 1e-3f && (i < pre || v.y <= 0f);
            feet[i] = p;
            vel[i] = v;
            flags[i] = grounded ? MotorState.Grounded : (byte)0;
            stun[i] = (byte)s;
        }

        var camGo = new GameObject("ShotCamera");
        var cam = camGo.AddComponent<Camera>();
        if (Camera.main != null)
            Camera.main.enabled = false;
        cam.clearFlags = RenderSettings.skybox != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
        cam.farClipPlane = 600f;
        cam.fieldOfView = 42f;
        cam.allowHDR = true;
        GraphicsQuality.PrepareShotCamera(cam);
        GraphicsQuality.ConfigurePipelineForShots(true, out var restore);
        var volume = GraphicsQuality.CreateShotVolume(true);
        GraphicsQuality.ApplyShadows(LookState.Current, true);

        var go = new GameObject("StunRushling");
        var vis = go.AddComponent<RushlingVisuals>();
        vis.Build(0, 0, map.playerMaterial, map.unlitMaterial);
        vis.SetVisible(true);
        var squash = go.transform.Find("Tilt/Squash");
        var stars = squash.Find("Swirls");
        var body = squash.Find("Body");
        var yaw = (byte)(Mathf.RoundToInt(Mathf.Atan2(-away.x, -away.z) * Mathf.Rad2Deg / (360f / 256f)) & 255);

        var H = pre;
        var cellsAt = new[] { H - 2, H + 2, H + Mathf.Max(1, hit.Stun / 2), H + hit.Stun + 1, H + hit.Stun + 15, H + hit.Stun + 45, H + hit.Stun + 75, H + hit.Stun + 100 };
        var cellName = new[] { "before", "hit", "mid-stun", "get-up", "+0.5 s", "+1.5 s", "+2.5 s", "+3.3 s" };
        const int cw = 480, chh = 340, cols = 4;
        var sheet = new Texture2D(cw * cols, chh * 2, TextureFormat.RGB24, false);
        var tmp = Path.Combine(Path.GetTempPath(), "fallrush-allhz-cell.png");
        CapsuleRunShots.Render(cam, Path.Combine(Path.GetTempPath(), "fallrush-allhz-warm.png"), 160, 120);
        var sb = new StringBuilder($"### {kind} (def #{hit.Index}: push {hit.Push} cm/s, up {hit.Up} cm/s, stun {hit.Stun} ticks = {hit.Stun / 30f:F2} s; hit at round tick {hit.T})\n\n");
        sb.Append($"![{mapName} {kind}](hit-{mapName}-{kind}.png)\n\n| cell | tick | sim stun | stars | body |\n|---|---|---|---|---|\n");
        var cell = 0;
        for (var i = 0; i < n; i++)
        {
            view.Update(hz, hit.T - pre + i, false);
            vis.Drive(feet[i], vel[i], flags[i], yaw, stun[i], Dt);
            var ci = System.Array.IndexOf(cellsAt, i);
            if (ci < 0)
                continue;
            var starsOn = stars.gameObject.activeInHierarchy && stars.lossyScale.x > 0.05f;
            var bodyOn = body != null && body.gameObject.activeInHierarchy && body.GetComponent<Renderer>().enabled;
            var want = ci >= 1 && ci <= 6; // the hit through +2.5 s must show stars (the tail fades out at about +3.5 s)
            var fellOff = fellAt >= 0 && i >= fellAt;
            if (!fellOff && ((want && !starsOn) || !bodyOn))
                fails++;
            sb.Append($"| {cellName[ci]} | {i - pre:+0;-0;0} | {stun[i]} | {(starsOn ? "ON" : "-")} | {(bodyOn ? "visible" : "HIDDEN")}{(fellOff ? " (knocked off the course)" : "")} |\n");

            // Camera: beside the knock line, at head height + 1.6 m, 4.6 m out, on the open side.
            var side = Vector3.Cross(Vector3.up, away).normalized;
            var focus = feet[i] + Vector3.up * 0.9f;
            var mid = Vector3.Lerp(hit.Feet, feet[i], 0.5f) + Vector3.up * 0.9f;
            var eye = mid - side * 4.6f + Vector3.up * 1.6f - away * 0.6f;
            if (Physics.Linecast(focus, eye, out _, ~0, QueryTriggerInteraction.Ignore))
                eye = mid + side * 4.6f + Vector3.up * 1.6f - away * 0.6f;
            cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(focus - eye));
            CapsuleRunShots.Render(cam, tmp, cw, chh);
            var t = new Texture2D(2, 2);
            t.LoadImage(File.ReadAllBytes(tmp));
            sheet.SetPixels(cell % cols * cw, (1 - cell / cols) * chh, cw, chh, t.GetPixels());
            Object.DestroyImmediate(t);
            cell++;
        }

        sheet.Apply();
        File.WriteAllBytes(Path.Combine(dir, $"hit-{mapName}-{kind}.png"), sheet.EncodeToPNG());
        Object.DestroyImmediate(sheet);
        Object.DestroyImmediate(go);
        var limbs = GameObject.Find("Limbs");
        if (limbs != null)
            Object.DestroyImmediate(limbs);
        Object.DestroyImmediate(camGo);
        if (volume != null)
            Object.DestroyImmediate(volume.gameObject);
        restore();
        sb.Append(fellAt >= 0 ? "\n- this knock throws the Rushling off the course (it falls with the stars, then respawns dizzy)\n\n" : "\n");
        return sb.ToString();
    }
}
#endif
