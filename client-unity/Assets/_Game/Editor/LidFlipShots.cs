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
/// G4.5 proof shots of <c>Map_CrumbleShelf</c> (Lid Flip), rendered in the Editor with the <see cref="CapsuleRunShots"/>
/// helpers. The moments come from a real QA round (<see cref="ModeQa.RunOne"/>, 3 normal bots, variant A, run 0): the
/// spawn view, the first wave lit yellow, lids mid-swing, ring 4 pulsing magenta before it retires, the sweeper's first hit,
/// a bot on the lower shelf, overheads at 0 / 30 / 80 s. The lids are the client's instanced view (<see cref="LidGridView"/>,
/// the same mesh, materials and matrices, snapshotted into temporary renderers because a manual camera render does not draw
/// <c>Graphics.RenderMeshInstanced</c> calls); the sweeper is posed by <see cref="HazardViewDriver"/> / the bar lift. The
/// Rushlings are runtime views, so they are stand-ins at the simulated positions (blue capsules, magenta = the one in
/// focus): the positions are the sim's, the look is not the game's. Plus a draw-call proxy for the spawn view.
/// Batch (needs graphics): <c>-executeMethod LidFlipShots.Run</c>.
/// </summary>
public static class LidFlipShots
{
    private struct Moment
    {
        public int T;
        public Vector3[] Players;
        public Vector3 Focus;
    }

    [MenuItem("Fall Rush/Proof/Lid Flip shots")]
    public static void Run()
    {
        var dir = CapsuleRunShots.Arg("-fallrushShotDir") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "../../docs/proof/g4"));
        Directory.CreateDirectory(dir);
        var scene = EditorSceneManager.OpenScene(CrumbleShelfBuilder.ScenePath, OpenSceneMode.Single);
        MapInfo map = null;
        foreach (var root in scene.GetRootGameObjects())
            map ??= root.GetComponentInChildren<MapInfo>();
        if (map == null)
            throw new IOException("no MapInfo");
        Physics.simulationMode = SimulationMode.Script;
        Physics.SyncTransforms();

        var moments = new Dictionary<string, Moment>();
        HazardSet runHz = null;
        var probe = PhysxMotorWorld.CreateProbe(map.transform, MovementConfig.Default.Radius, MovementConfig.Default.Height);
        var st = ModeQa.RunOne(map, probe, 0, 0, 0, BotTier.Normal, 3, perTick: (core, rules, hz) =>
        {
            runHz = hz;
            var t = hz.Time(core.Tick);
            Vector3[] All()
            {
                var a = new List<Vector3>();
                for (var i = 0; i < SimCore.MaxSlots; i++)
                    if (core.Slots[i].Active && !core.Slots[i].State.Has(MotorState.Respawning) && rules.InRound(i) && core.Slots[i].State.PosY > -5000)
                        a.Add(V(core.Slots[i].State));
                return a.ToArray();
            }

            void At(string key, Vector3 focus = default)
            {
                if (!moments.ContainsKey(key))
                    moments[key] = new Moment { T = t, Players = All(), Focus = focus };
            }

            var top = hz.LidGrids[0];
            if (t == 0) At("t0");
            if (t == top.FirstWaveTicks + 18) At("lit");
            if (t == top.FirstWaveTicks + top.LitTicks + 3) At("flip");
            if (t == top.RingRetireTicks[0] - 40) At("retire");
            if (t == 900) At("t30");
            if (t == 2400) At("t80");
            if (t == hz.SweeperStartT + 30) At("sweep"); // fallback when nobody is hit
            for (var i = 0; i < SimCore.MaxSlots; i++)
            {
                if (!core.Slots[i].Active)
                    continue;
                ref readonly var s = ref core.Slots[i].State;
                if (t >= hz.SweeperStartT && (core.Slots[i].LastStep & PlayerStep.Result.HazardHit) != 0)
                    At("hit", V(s));
                if (s.Has(MotorState.Grounded) && hz.LidGrids[1].LidUnder(s.PosX, s.PosY, s.PosZ) >= 0 && hz.Time(core.Tick) > 60)
                    At("lower", V(s));
            }
        });

        var hz0 = runHz;
        var view = new HazardViewDriver(map) { DrawInstanced = false };
        var rv = new GameObject("ShotReactiveViews");
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
        var standIns = new GameObject("ShotStandIns").transform;
        var lidShots = new GameObject("ShotLids").transform;
        var bodyMat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = new Color(0.36f, 0.55f, 1f) };
        var focusMat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = new Color(1f, 0.25f, 0.6f) };
        var bar = map.sweepers[0].barVisual;
        var barRest = bar.localPosition;

        void Pose(int t)
        {
            CapsuleRunShots.Pose(view, hz0, t);
            // The bar hangs raised until the sweeper starts (ReactiveViews' lift), as in the client.
            var lift = t >= hz0.SweeperStartT ? 0f : Mathf.Clamp01((hz0.SweeperStartT - t) / 60f) * 4f;
            bar.localPosition = barRest + Vector3.up * lift;
            for (var i = lidShots.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(lidShots.GetChild(i).gameObject);
            view.LidView.Snapshot(lidShots);
            for (var i = standIns.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(standIns.GetChild(i).gameObject);
        }

        void Players(Moment mo)
        {
            if (mo.Players == null)
                return;
            foreach (var p in mo.Players)
            {
                var c = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                Object.DestroyImmediate(c.GetComponent<Collider>());
                c.transform.SetParent(standIns, false);
                c.transform.position = p + Vector3.up * 0.5f;
                c.transform.localScale = new Vector3(0.62f, 0.5f, 0.62f);
                c.GetComponent<MeshRenderer>().sharedMaterial = mo.Focus != default && (p - mo.Focus).sqrMagnitude < 0.01f ? focusMat : bodyMat;
            }
        }

        var report = new StringBuilder();
        report.Append($"Round used: variant A, 3 normal bots, run 0 — {st.Result}\n\n");

        // Spawn view: the follow framing (6.1 m behind, 2.5 m up) at spawn 0, facing the centre.
        var feet = map.spawns[0].position;
        var fwd = (-new Vector3(feet.x, 0f, feet.z)).normalized;
        var target = feet + Vector3.up * 0.9f;
        var m0 = moments.TryGetValue("t0", out var mt0) ? mt0 : new Moment { T = 0 };
        Pose(0);
        Players(m0);
        CapsuleRunShots.Shot(cam, dir, "lid-flip-spawn-view", target - fwd * 6.1f + Vector3.up * 2.5f, target + fwd * 3f, 320, 180); // warm-up frame
        CapsuleRunShots.Shot(cam, dir, "lid-flip-spawn-view", target - fwd * 6.1f + Vector3.up * 2.5f, target + fwd * 3f, 1280, 720);
        // Draw-call proxy: the scene's renderers in view (the lid snapshot excluded) + the instanced lid batches the client issues.
        lidShots.gameObject.SetActive(false);
        report.Append(CapsuleRunShots.CountVisible(cam, "spawn view (follow framing, stand-ins included, lids excluded)"));
        report.Append($"- **instanced lids**: {view.LidView.Batches} `RenderMeshInstanced` draws for {hz0.LidCount} lids at t 0 (one per state colour in use; ≤ 9 at any tick)\n");
        lidShots.gameObject.SetActive(true);

        var eyeHigh = new Vector3(0f, 15f, -17f);
        foreach (var (key, name, eye, at) in new[]
                 {
                     ("lit", "lid-flip-wave-lit", eyeHigh, new Vector3(0f, 5f, 0f)),
                     ("flip", "lid-flip-mid-flip", new Vector3(7f, 9.5f, -11f), new Vector3(0f, 5f, 0f)),
                     ("retire", "lid-flip-ring-retiring", eyeHigh, new Vector3(0f, 5f, 0f)),
                 })
        {
            if (!moments.TryGetValue(key, out var mo))
                continue;
            Pose(mo.T);
            Players(mo);
            CapsuleRunShots.Shot(cam, dir, name, eye, at, 1280, 720);
            report.Append($"- {name}: t {mo.T / 30f:F2} s\n");
        }

        if (!moments.ContainsKey("hit") && moments.TryGetValue("sweep", out var sw))
        {
            Pose(sw.T);
            Players(sw);
            CapsuleRunShots.Shot(cam, dir, "lid-flip-sweeper", new Vector3(9f, 9.5f, -9f), new Vector3(0f, 6.2f, 0f), 1280, 720);
            report.Append($"- lid-flip-sweeper: t {sw.T / 30f:F2} s, the bar 1 s after it starts (no hit in this round)\n");
        }

        if (moments.TryGetValue("hit", out var hit))
        {
            Pose(hit.T);
            Players(hit);
            var p = hit.Focus;
            var radial = new Vector3(p.x, 0f, p.z).sqrMagnitude > 0.01f ? new Vector3(p.x, 0f, p.z).normalized : Vector3.forward;
            CapsuleRunShots.Shot(cam, dir, "lid-flip-sweeper", p + radial * 5f + Vector3.up * 3.2f + Vector3.Cross(Vector3.up, radial) * 2f, p, 1280, 720);
            report.Append($"- lid-flip-sweeper: t {hit.T / 30f:F2} s, the first sweeper hit (magenta stand-in)\n");
        }

        if (moments.TryGetValue("lower", out var low))
        {
            Pose(low.T);
            Players(low);
            var p = low.Focus;
            CapsuleRunShots.Shot(cam, dir, "lid-flip-lower-shelf", new Vector3(p.x * 0.4f + 9f, 3.4f, p.z * 0.4f - 9f), p + Vector3.up * 0.8f, 1280, 720);
            report.Append($"- lid-flip-lower-shelf: t {low.T / 30f:F2} s, a bot on the lower shelf (magenta stand-in)\n");
        }

        cam.orthographic = true;
        cam.orthographicSize = 12f;
        cam.transform.SetPositionAndRotation(new Vector3(0f, 60f, 0f), Quaternion.LookRotation(Vector3.down, Vector3.forward));
        foreach (var (key, name) in new[] { ("t0", "lid-flip-overhead-0s"), ("t30", "lid-flip-overhead-30s"), ("t80", "lid-flip-overhead-80s") })
        {
            if (!moments.TryGetValue(key, out var mo))
                continue;
            Pose(mo.T);
            Players(mo);
            CapsuleRunShots.Render(cam, Path.Combine(dir, name + ".png"), 1000, 1000);
            report.Append($"- {name}: t {mo.T / 30f:F2} s, {mo.Players?.Length ?? 0} players in the round\n");
        }

        cam.orthographic = false;
        bar.localPosition = barRest;
        File.WriteAllText(Path.Combine(dir, "lid-flip-drawcalls-editor.md"), report.ToString());
        Debug.Log($"[shots] wrote Lid Flip shots to {dir}\n{report}");
        Object.DestroyImmediate(standIns.gameObject);
        Object.DestroyImmediate(lidShots.gameObject);
        Object.DestroyImmediate(rv);
        Object.DestroyImmediate(go);
    }

    private static Vector3 V(in MotorState s) => new Vector3(s.PosX * 0.001f, s.PosY * 0.001f, s.PosZ * 0.001f);
}
