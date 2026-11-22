using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FallRush.Net;
using FallRush.Shared;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// P1-L: C# reference traces. Runs scripted input timelines through today's <see cref="KinematicMotor"/> +
/// <see cref="PhysxMotorWorld"/> (+ hazards via <see cref="PlayerStep.Run"/>, <see cref="SimCore"/> separation for the
/// 3-player push) at 30 Hz on the map scenes, and writes <c>docs/proof/p1/traces/&lt;map&gt;-&lt;script&gt;.json</c> with the
/// script (so <c>rush-sim trace replay</c> re-times the same inputs at 15 / 20 / 30 Hz) and the per-tick state.
/// Read-only: scenes are opened, never saved. Batch: <c>-executeMethod P1Traces.RunAll</c>.
/// <para>Input timeline rule (both sides): tick k (1-based) uses the input at time (k − 1) / rate; a jump / dive press
/// at time T is sent on the first tick whose time is ≥ T (held for 100 ms).</para>
/// </summary>
public static class P1Traces
{
    public const ulong Seed = 1;

    public sealed class Script
    {
        public string Scene, Name;
        public Vector3 Start;      // feet (m); with PlatformTop / TableTop the y is replaced at run time
        public byte Yaw;
        public Vector3 StartVel;   // m/s
        public float Seconds;
        public List<(int t0, int t1, int mx, int my)> Moves = new List<(int, int, int, int)>();
        public List<int> Jumps = new List<int>(), Dives = new List<int>();
        public Vector3[] Extra = Array.Empty<Vector3>(); // extra players (push)
        public Func<MapInfo, HazardSet, Vector3> Place; // computed start (on a moving platform …)

        public Script Move(int t0, int t1, int mx, int my)
        {
            Moves.Add((t0, t1, mx, my));
            return this;
        }
    }

    private const string Sbx = "Map_Sandbox";

    private static Script S(string scene, string name, float x, float y, float z, float secs) =>
        new Script { Scene = scene, Name = name, Start = new Vector3(x, y, z), Seconds = secs };

    public static List<Script> Scripts()
    {
        var l = new List<Script>
        {
            S(Sbx, "run-straight", 0f, 0.05f, -38f, 2f).Move(0, 2000, 0, 127),
            S(Sbx, "run-jump", 0f, 0.05f, -38f, 2f).Move(0, 2000, 0, 127),
            S(Sbx, "jump-buffer", -3f, 1.05f, -36f, 2f),
            new Script { Scene = Sbx, Name = "coyote-ledge", Start = new Vector3(23f, 1.05f, -6.8f), StartVel = new Vector3(0, 0, 5f), Seconds = 2f }.Move(0, 2000, 0, 127),
            S(Sbx, "dive-slide-cancel", 0f, 0.05f, -38f, 2.5f).Move(0, 2500, 0, 127),
            S(Sbx, "slope-15", -30f, 0.05f, -14f, 3f).Move(0, 3000, 0, 127),
            S(Sbx, "slope-30", -22f, 0.05f, -14f, 3f).Move(0, 3000, 0, 127),
            S(Sbx, "slope-44", -14f, 0.05f, -14f, 3f).Move(0, 3000, 0, 127),
            S(Sbx, "slope-50", -6f, 0.05f, -14f, 3f).Move(0, 3000, 0, 127),
            S(Sbx, "step-0.2", 6.5f, 0.05f, -13f, 2f).Move(0, 2000, 0, 127),
            S(Sbx, "step-0.3", 13.5f, 0.05f, -13f, 2f).Move(0, 2000, 0, 127),
            new Script { Scene = Sbx, Name = "gap-jump-1.0", Start = new Vector3(23f, 1.05f, -9f), StartVel = new Vector3(0, 0, 5f), Seconds = 2f }.Move(0, 2000, 0, 127),
            S(Sbx, "wall-slide", 30.5f, 0.05f, -3f, 2f).Move(0, 2000, 40, 120),
            S(Sbx, "hard-landing", 0f, 4f, -30f, 2f),
            S(Sbx, "platform-ride", 0f, 0f, 0f, 5f),
            S(Sbx, "turn-table", 0f, 0f, 0f, 5f),
            S(Sbx, "conveyor", -11f, 0.05f, 54f, 3f),
            S(Sbx, "spring-pad", -6.5f, 0.15f, 54f, 3f).Move(0, 3000, 127, 0),
            S(Sbx, "sweeper-hit", -27f, 0.05f, -31f, 5f),
            S(Sbx, "mallet", 23f, 0.05f, 54f, 4f),
            S(Sbx, "bumper", 27.5f, 0.05f, 52.4f, 2f).Move(0, 2000, 127, 0),
            S(Sbx, "rolling-ball", 54f, 0.05f, -20f, 5f),
            S(Sbx, "lid-fall", 54f - 3f * 1.86f, 0.05f, -36.52f, 12f),
            S(Sbx, "respawn", 38f, 0.05f, 0f, 4f).Move(0, 4000, 127, 0),
            new Script { Scene = Sbx, Name = "push-3", Start = new Vector3(0f, 0.05f, 0f), Seconds = 2f, Extra = new[] { new Vector3(0.2f, 0.05f, 0f), new Vector3(-0.1f, 0.05f, 0.15f) } },
            S("Map_GooTower", "goo-death", 0f, 0f, 0f, 16f),
        };
        // Script details that need more than a straight move.
        Find(l, "run-jump").Jumps.Add(500);
        Find(l, "jump-buffer").Jumps.Add(200);
        Find(l, "coyote-ledge").Jumps.Add(200);
        var d = Find(l, "dive-slide-cancel");
        d.Dives.Add(600);
        d.Jumps.Add(860);
        Find(l, "gap-jump-1.0").Jumps.Add(470);
        Find(l, "platform-ride").Place = (info, hz) =>
        {
            var c = HazardMath.PlatformCenter(in hz.Platforms[0], 0);
            return new Vector3(c.X, c.Y + hz.Platforms[0].HalfYMm * 0.001f + 0.05f, c.Z);
        };
        Find(l, "turn-table").Place = (info, hz) =>
        {
            var t = hz.TurnTables[0];
            return new Vector3(t.CenterXMm * 0.001f + 2.5f, t.CenterYMm * 0.001f + 0.05f, t.CenterZMm * 0.001f);
        };
        // Round maps: run + jump from spawn 0 on each.
        foreach (var m in new[] { "Map_BlockPartyDash", "Map_GooTower", "Map_StarScramble", "Map_CrumbleShelf" })
        {
            var s = new Script { Scene = m, Name = "spawn-run-jump", Seconds = 3f, Place = (info, hz) => { var p = info.SpawnPoints()[0]; return new Vector3(p.X, p.Y, p.Z); } };
            s.Move(0, 3000, 0, 127).Jumps.Add(800);
            l.Add(s);
        }

        // goo: stand at spawn 0
        Find(l, "goo-death").Place = (info, hz) => { var p = info.SpawnPoints()[0]; return new Vector3(p.X, p.Y, p.Z); };
        return l;
    }

    private static Script Find(List<Script> l, string n) => l.Find(s => s.Name == n);

    public static string OutDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "docs", "proof", "p1", "traces"));

    [MenuItem("Fall Rush/P1/Write reference traces")]
    public static void RunAll()
    {
        Directory.CreateDirectory(OutDir);
        var ok = true;
        foreach (var s in Scripts())
        {
            try
            {
                var json = Run(s);
                var key = s.Scene.Replace("Map_", "");
                File.WriteAllText(Path.Combine(OutDir, $"{key}-{s.Name}.json"), json, new UTF8Encoding(false));
                Debug.Log($"[p1] trace {key}-{s.Name}");
            }
            catch (Exception e)
            {
                ok = false;
                Debug.LogError($"[p1] trace {s.Scene} {s.Name} failed: {e}");
            }
        }

        if (Application.isBatchMode)
            EditorApplication.Exit(ok ? 0 : 1);
    }

    /// <summary>Input of tick <paramref name="k"/> (1-based) at <paramref name="rate"/> Hz (the shared timeline rule).</summary>
    public static InputCmd InputAt(Script s, int k, int rate)
    {
        var t = (k - 1) * 1000 / rate; // ms, floor
        var t1 = k * 1000 / rate;
        var cmd = new InputCmd { Tick = (uint)k, Seq = (uint)k };
        foreach (var (a, b, mx, my) in s.Moves)
            if (t >= a && t < b)
            {
                cmd.MoveX = (sbyte)mx;
                cmd.MoveY = (sbyte)my;
            }

        foreach (var j in s.Jumps)
        {
            if (FirstTickAtOrAfter(j, rate) == k)
                cmd.Buttons |= InputCmd.JumpPressed;
            if (t >= j && t < j + 100)
                cmd.Buttons |= InputCmd.JumpHeld;
        }

        foreach (var j in s.Dives)
        {
            if (FirstTickAtOrAfter(j, rate) == k)
                cmd.Buttons |= InputCmd.DivePressed;
            if (t >= j && t < j + 100)
                cmd.Buttons |= InputCmd.DiveHeld;
        }

        _ = t1;
        return cmd;
    }

    /// <summary>The first tick k with (k − 1)/rate ≥ T ms: k = ⌈T·rate/1000⌉ + 1.</summary>
    public static int FirstTickAtOrAfter(int ms, int rate) => (ms * rate + 999) / 1000 + 1;

    public static string Run(Script s)
    {
        var scene = EditorSceneManager.OpenScene($"Assets/_Game/Scenes/{s.Scene}.unity", OpenSceneMode.Single);
        Physics.SyncTransforms();
        MapInfo info = null;
        foreach (var r in scene.GetRootGameObjects())
            info ??= r.GetComponentInChildren<MapInfo>(true);
        var cfg = MovementConfig.Default;
        var p = new RoundParams { HazardSeed = Seed, RoundStartTick = 0, Variant = 0, Modifier = 0 };
        var probeParent = new GameObject("P1TraceProbe");
        SceneManager_Move(probeParent, scene);
        var world = info.CreateWorld(probeParent.transform, cfg, in p, out var hz);
        var start = s.Place != null ? s.Place(info, hz) : s.Start;
        var players = 1 + s.Extra.Length;
        var spawns = info.SpawnPoints();
        var sim = new SimCore(cfg, world, spawns, hz, info.SpawnYaws());
        for (var i = 0; i < players; i++)
        {
            sim.AddHumanAt(i, (ulong)(100 + i));
            var pos = i == 0 ? start : s.Extra[i - 1];
            sim.Place(i, new Vec3(pos.x, pos.y, pos.z), s.Yaw);
            var st = sim.Slots[i].State;
            st.Velocity = new Vec3(s.StartVel.x, s.StartVel.y, s.StartVel.z);
            sim.Slots[i].State = st;
        }

        var ticks = Mathf.RoundToInt(s.Seconds * cfg.TickRate);
        var sb = new StringBuilder(1 << 16);
        sb.Append("{\n\"scene\":\"").Append(s.Scene).Append("\",\"script\":\"").Append(s.Name).Append("\",\"rate\":").Append(cfg.TickRate)
            .Append(",\"seed\":").Append(Seed).Append(",\"ticks\":").Append(ticks).Append(",\n\"start\":[");
        for (var i = 0; i < players; i++)
        {
            var st = sim.Slots[i].State;
            sb.Append(i > 0 ? "," : "").Append($"[{st.PosX},{st.PosY},{st.PosZ},{st.Yaw},{st.VelX * 10},{st.VelY * 10},{st.VelZ * 10}]");
        }

        sb.Append("],\n\"moves\":[");
        for (var i = 0; i < s.Moves.Count; i++)
            sb.Append(i > 0 ? "," : "").Append($"[{s.Moves[i].t0},{s.Moves[i].t1},{s.Moves[i].mx},{s.Moves[i].my}]");
        sb.Append("],\"jumps\":[").Append(string.Join(",", s.Jumps)).Append("],\"dives\":[").Append(string.Join(",", s.Dives)).Append("],\n\"frames\":[\n");
        // frame: tick, then per player: x, y, z (mm), vx, vy, vz (mm/s), flags, stun, stepResult
        for (var k = 1; k <= ticks; k++)
        {
            var cmd = InputAt(s, k, cfg.TickRate);
            for (var i = 0; i < players; i++)
            {
                var c = i == 0 ? cmd : new InputCmd { Tick = (uint)k, Seq = (uint)k };
                sim.Input(i).Offer(c, sim.NextTick);
            }

            sim.Step();
            sb.Append(k > 1 ? ",\n" : "").Append('[').Append(k);
            for (var i = 0; i < players; i++)
            {
                var st = sim.Slots[i].State;
                sb.Append($",{st.PosX},{st.PosY},{st.PosZ},{st.VelX * 10},{st.VelY * 10},{st.VelZ * 10},{st.Flags},{st.StunTicks},{(int)sim.Slots[i].LastStep}");
            }

            sb.Append(']');
        }

        sb.Append("\n]}\n");
        UnityEngine.Object.DestroyImmediate(probeParent);
        return sb.ToString();
    }

    private static void SceneManager_Move(GameObject go, UnityEngine.SceneManagement.Scene scene) =>
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
}
