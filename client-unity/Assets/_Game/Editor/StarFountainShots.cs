#if !UNITY_SERVER // P5: editor tools on the client assembly (excluded from the Server subtarget, P3 proof §5)
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
/// G4.4 proof shots of <c>Map_StarScramble</c> (Star Fountain), rendered in the Editor with the <see cref="CapsuleRunShots"/>
/// helpers. The moments come from a real QA round (<see cref="ModeQa.RunOne"/>, 5 normal bots, variant A, run 0): the
/// tick of a burst mid-arc, the pack on the turntable just after it, the first sweeper hit, a spring flight to an island.
/// Hazards are posed by the client's <see cref="HazardViewDriver"/>; the Rushlings, stars, arcs and the crown glow are
/// runtime client views, so they are drawn here as stand-ins at the simulated positions (capsules, star meshes) — the
/// positions are the sim's, the look is not the game's. Plus the spawn view, an overhead and an Editor count of the
/// renderers the spawn camera sees (a draw-call proxy). Batch (needs graphics): <c>-executeMethod StarFountainShots.Run</c>.
/// </summary>
public static class StarFountainShots
{
    private struct Moment
    {
        public int T;
        public Vector3[] Players;
        public int Slot;
    }

    [MenuItem("Fall Rush/Proof/Star Fountain shots")]
    public static void Run()
    {
        var dir = CapsuleRunShots.Arg("-fallrushShotDir") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "../../docs/proof/g4"));
        Directory.CreateDirectory(dir);
        var scene = EditorSceneManager.OpenScene(StarScrambleBuilder.ScenePath, OpenSceneMode.Single);
        MapInfo map = null;
        foreach (var root in scene.GetRootGameObjects())
            map ??= root.GetComponentInChildren<MapInfo>();
        if (map == null)
            throw new IOException("no MapInfo");
        Physics.simulationMode = SimulationMode.Script;
        Physics.SyncTransforms();

        // 1. Find the moments in a real round (the QA run's seed, so the hazard set below is the same).
        var burstT = 4 * 240 - 7;
        Moment burst = default, pack = default, hit = default, spring = default;
        burst.T = pack.T = hit.T = spring.T = -1;
        var launchedAt = new uint[SimCore.MaxSlots];
        HazardSet runHz = null;
        var probe = PhysxMotorWorld.CreateProbe(map.transform, MovementConfig.Default.Radius, MovementConfig.Default.Height);
        ModeQa.RunOne(map, probe, 0, 0, 0, BotTier.Normal, 5, perTick: (core, rules, hz) =>
        {
            runHz = hz;
            var t = hz.Time(core.Tick);
            Vector3[] All()
            {
                var a = new List<Vector3>();
                for (var i = 0; i < SimCore.MaxSlots; i++)
                    if (core.Slots[i].Active && !core.Slots[i].State.Has(MotorState.Respawning))
                        a.Add(V(core.Slots[i].State));
                return a.ToArray();
            }

            if (t == burstT)
                burst = new Moment { T = t, Players = All() };
            if (t == 4 * 240 + 25)
                pack = new Moment { T = t, Players = All() };
            for (var i = 0; i < SimCore.MaxSlots; i++)
            {
                if (!core.Slots[i].Active)
                    continue;
                var step = core.Slots[i].LastStep;
                if (hit.T < 0 && t > 60 && (step & PlayerStep.Result.HazardHit) != 0 && hz.Sweepers.Length > 0)
                    hit = new Moment { T = t, Players = All(), Slot = i };
                if ((step & PlayerStep.Result.Launched) != 0)
                    launchedAt[i] = core.Tick;
                if (spring.T < 0 && launchedAt[i] != 0 && core.Tick - launchedAt[i] == 14)
                    spring = new Moment { T = t, Players = All(), Slot = i };
            }
        });

        var hz0 = runHz ?? map.BuildHazards(new RoundParams { MapId = map.MapId, Variant = 0, HazardSeed = 1 }, out _);
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
        var standIns = new GameObject("ShotStandIns").transform;
        var starMesh = RushlingParts.Shapes.Polygon(RushlingParts.Shapes.StarOutline(0.5f, 0.22f, 5), 0.22f);
        var starMat = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = new Color(1f, 0.85f, 0.15f) };
        var goldMat = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = new Color(1f, 0.62f, 0.08f) };
        var bodyMat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = new Color(0.36f, 0.55f, 1f) };
        var hitMat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = new Color(1f, 0.25f, 0.6f) };
        var glowMat = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = new Color(1f, 0.9f, 0.4f) };

        void Clear()
        {
            for (var i = standIns.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(standIns.GetChild(i).gameObject);
        }

        void Star(Vector3 p, float scale, Material m)
        {
            var s = new GameObject("Star");
            s.transform.SetParent(standIns, false);
            s.transform.SetPositionAndRotation(p, Quaternion.Euler(0f, 30f, 0f));
            s.transform.localScale = Vector3.one * scale;
            s.AddComponent<MeshFilter>().sharedMesh = starMesh;
            s.AddComponent<MeshRenderer>().sharedMaterial = m;
        }

        void Players(Moment mo, int highlight = -1)
        {
            if (mo.Players == null)
                return;
            for (var i = 0; i < mo.Players.Length; i++)
            {
                var c = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                Object.DestroyImmediate(c.GetComponent<Collider>());
                c.transform.SetParent(standIns, false);
                c.transform.position = mo.Players[i] + Vector3.up * 0.5f;
                c.transform.localScale = new Vector3(0.62f, 0.5f, 0.62f);
                c.GetComponent<MeshRenderer>().sharedMaterial = i == highlight ? hitMat : bodyMat;
            }
        }

        void Stars(HazardSet hz, int t)
        {
            var f = hz.StarFields[0];
            var live = new List<int>();
            // The round's own reactive log: a star taken before this tick is gone, one taken later still shows.
            SkillRules.LiveStars(hz, t, (uint)(hz.RoundStartTick + t), live);
            foreach (var id in live)
            {
                HazardMath.StarSlot(in f, hz.Seed, id, out var a, out _, out _, out var value);
                HazardMath.StarAnchorMm(in f, hz, a, t, out var x, out var y, out var z);
                Star(new Vector3(x, y, z) * 0.001f, value > 1 ? 1f : 0.65f, value > 1 ? goldMat : starMat);
            }

            var c = map.starFields[0].burstOrigin.position;
            int cx = Mathf.RoundToInt(c.x * 1000f), cy = Mathf.RoundToInt(c.y * 1000f), cz = Mathf.RoundToInt(c.z * 1000f);
            var w = StarBurst.NextWave(in f, t, out _);
            for (var k = 0; k < f.WaveSize; k++)
                for (var tr = 0; tr < 3; tr++)
                    if (StarBurst.ArcPosMm(in f, hz, hz.Seed, w * f.WaveSize + k, t - tr * 1.2, cx, cy, cz, out var bx, out var by, out var bz, out var u))
                        Star(new Vector3((float)bx, (float)by, (float)bz) * 0.001f, tr == 0 ? 0.35f + 0.3f * (float)u : 0.22f / tr, starMat);
            var glow = StarBurst.CrownGlow(in f, t);
            if (glow > 0f)
            {
                var g = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Object.DestroyImmediate(g.GetComponent<Collider>());
                g.transform.SetParent(standIns, false);
                g.transform.position = c;
                g.transform.localScale = Vector3.one * (1.3f + glow * 0.9f);
                g.GetComponent<MeshRenderer>().sharedMaterial = glowMat;
            }
        }

        var report = new StringBuilder();
        // Spawn view: the follow camera 6.1 m behind and 2.5 m up at a spawn on the outer ring, facing the fountain.
        var feet = map.spawns[5].position;
        var fwd = (-new Vector3(feet.x, 0f, feet.z)).normalized;
        var target = feet + Vector3.up * 0.9f;
        CapsuleRunShots.Pose(view, hz0, 0);
        Clear();
        Stars(hz0, 0);
        CapsuleRunShots.Shot(cam, dir, "star-fountain-spawn-view", target - fwd * 6.1f + Vector3.up * 2.5f, target + fwd * 3f, 320, 180); // warm-up: the first batch-mode frame draws new materials before their shaders are ready
        CapsuleRunShots.Shot(cam, dir, "star-fountain-spawn-view", target - fwd * 6.1f + Vector3.up * 2.5f, target + fwd * 3f, 1280, 720);
        report.Append(CapsuleRunShots.CountVisible(cam, "spawn view (follow framing, stand-ins included)"));

        // A burst mid-arc (t = wave 4 − 7 ticks).
        if (burst.T >= 0)
        {
            CapsuleRunShots.Pose(view, hz0, burst.T);
            Clear();
            Stars(hz0, burst.T);
            Players(burst);
            CapsuleRunShots.Shot(cam, dir, "star-fountain-burst", new Vector3(0f, 9f, -16f), new Vector3(0f, 2f, 0f), 1280, 720);
            report.Append($"- burst shot: t {burst.T / 30f:F2} s (wave 4 spawns at {4 * 8:F0} s)\n");
        }

        // The pack on the turntable, 0.8 s after the burst.
        if (pack.T >= 0)
        {
            CapsuleRunShots.Pose(view, hz0, pack.T);
            Clear();
            Stars(hz0, pack.T);
            Players(pack);
            CapsuleRunShots.Shot(cam, dir, "star-fountain-pack-on-table", new Vector3(10f, 12f, -12f), new Vector3(0f, 0f, 0f), 1280, 720);
            report.Append($"- pack shot: t {pack.T / 30f:F2} s\n");
        }

        // The first sweeper hit (the hit Rushling magenta).
        if (hit.T >= 0)
        {
            CapsuleRunShots.Pose(view, hz0, hit.T);
            Clear();
            Stars(hz0, hit.T);
            Players(hit, -1); // (the hit Rushling is the one nearest a sweeper pivot)
            var p = hit.Players.Length > 0 ? hit.Players[0] : Vector3.zero;
            var best = float.MaxValue;
            foreach (var q in hit.Players)
                foreach (var d in hz0.Sweepers)
                {
                    var dd = (q - new Vector3(d.PivotXMm, 0f, d.PivotZMm) * 0.001f).sqrMagnitude;
                    if (dd < best)
                    {
                        best = dd;
                        p = q;
                    }
                }

            var dir2 = new Vector3(p.x, 0f, p.z).normalized;
            CapsuleRunShots.Shot(cam, dir, "star-fountain-sweeper-hit", p - dir2 * 6f + Vector3.up * 3.5f, p, 1280, 720);
            report.Append($"- sweeper hit shot: t {hit.T / 30f:F2} s, slot {hit.Slot}\n");
        }

        // A spring flight to an island (14 ticks after the launch).
        if (spring.T >= 0)
        {
            CapsuleRunShots.Pose(view, hz0, spring.T);
            Clear();
            Stars(hz0, spring.T);
            Players(spring);
            var best = Vector3.zero;
            foreach (var q in spring.Players)
                if (q.y > best.y)
                    best = q;
            var radial = new Vector3(best.x, 0f, best.z).normalized;
            var side = Vector3.Cross(Vector3.up, radial);
            CapsuleRunShots.Shot(cam, dir, "star-fountain-spring-to-island", best - radial * 3f + side * 9f + Vector3.up * 3f, best + radial * 2f, 1280, 720);
            report.Append($"- spring shot: t {spring.T / 30f:F2} s, slot {spring.Slot} at y {best.y:F2} m\n");
        }

        // Overhead (orthographic).
        CapsuleRunShots.Pose(view, hz0, pack.T >= 0 ? pack.T : 500);
        Clear();
        Stars(hz0, pack.T >= 0 ? pack.T : 500);
        Players(pack);
        cam.orthographic = true;
        cam.orthographicSize = 26f;
        cam.transform.SetPositionAndRotation(new Vector3(0f, 120f, 0f), Quaternion.LookRotation(Vector3.down, Vector3.forward));
        CapsuleRunShots.Render(cam, Path.Combine(dir, "star-fountain-overhead.png"), 1200, 1200);
        cam.orthographic = false;
        Clear();

        File.WriteAllText(Path.Combine(dir, "star-fountain-drawcalls-editor.md"), report.ToString());
        Debug.Log($"[shots] wrote Star Fountain shots to {dir}\n{report}");
        Object.DestroyImmediate(standIns.gameObject);
        Object.DestroyImmediate(go);
    }

    private static Vector3 V(in MotorState s) => new Vector3(s.PosX * 0.001f, s.PosY * 0.001f, s.PosZ * 0.001f);
}
#endif
