#if !UNITY_SERVER
using System.Collections.Generic;
using System.IO;
using System.Text;
using FallRush.Net;
using FallRush.Shared;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 2026-11-24 open item ("jumping onto an up-ramp glides, no grip"): the motor only probes for ground while vel.y ≤ 0, so a
/// jump that comes down on a surface rising under it can skim without landing. Fixed per map (user: no motor change).
/// This drives the real <see cref="KinematicMotor"/> on every tilted walkable box of every match map:
///   hop-up  : stand low on the ramp, run uphill, jump after 3–15 ticks; time to land again;
///   jump-on : run at the ramp's low edge from the floor before it, jump 0–1.2 m before the edge; time to land on it.
/// A case fails when the landing takes longer than flat ground's hop + 8 ticks (0.27 s) or never comes (1.5 s).
/// Batch: -executeMethod RampLandingCheck.Run [-fallrushRampOut file.md]
/// </summary>
public static class RampLandingCheck
{
    private static readonly (string scene, string name)[] Maps =
    {
        (ProjectSetup.ScenesDir + "/Map_BlockPartyDash.unity", "Capsule Run"),
        (ProjectSetup.ScenesDir + "/Map_GooTower.unity", "Goo Tower"),
        (ProjectSetup.ScenesDir + "/Map_StarScramble.unity", "Star Fountain"),
        (ProjectSetup.ScenesDir + "/Map_CrumbleShelf.unity", "Crumble Shelf"),
    };

    private const int MaxAir = 45;

    [MenuItem("Fall Rush/Proof/Ramp landing check (all maps)")]
    public static void Run()
    {
        var outPath = CapsuleRunShots.Arg("-fallrushRampOut") ??
                      Path.GetFullPath(Path.Combine(Application.dataPath, "../../docs/proof/p7/ramps/ramps.md"));
        Directory.CreateDirectory(Path.GetDirectoryName(outPath));
        var sb = new StringBuilder("# Ramp landing check (real KinematicMotor, every tilted walkable box)\n\n");
        sb.Append($"- fail = landing later than flat ground's hop + 8 ticks, or no landing within {MaxAir} ticks (1.5 s); glide = airborne ticks with the feet < 0.2 m above a surface\n\n");
        var totalFail = 0;
        foreach (var (scenePath, label) in Maps)
            totalFail += CheckMap(scenePath, label, sb);
        sb.Append($"\n**{(totalFail == 0 ? "PASS" : "FAIL")}**: {totalFail} failing case(s)\n");
        File.WriteAllText(outPath, sb.ToString());
        Debug.Log("[ramps] → " + outPath + "\n" + sb);
    }

    /// <summary>Tick trace of one hop on a named ramp: -fallrushRampName Geometry/S1Slope -fallrushRampK 6 (Capsule Run unless -fallrushRampScene).</summary>
    public static void Trace()
    {
        var name = CapsuleRunShots.Arg("-fallrushRampName") ?? "Geometry/S1Slope";
        var k = int.Parse(CapsuleRunShots.Arg("-fallrushRampK") ?? "6");
        var scene = EditorSceneManager.OpenScene(CapsuleRunShots.Arg("-fallrushRampScene") ?? Maps[0].scene, OpenSceneMode.Single);
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
        var phys = scene.GetPhysicsScene();
        var mask = PhysxMotorWorld.GameplayMask();
        BoxCollider bc = null;
        foreach (var c in Object.FindObjectsByType<BoxCollider>(FindObjectsSortMode.None))
            if (Name(c.transform) == name) bc = c;
        var t = bc.transform;
        var n = TopNormal(t, out var axisA, out var halfA);
        var top = t.TransformPoint(bc.center) + n * HalfAlong(t, bc, n);
        var uphill = axisA.y >= 0f ? axisA : -axisA;
        var h = new Vector3(uphill.x, 0f, uphill.z).normalized;
        var s = Place(top - uphill * (halfA * 0.75f) + n * 0.03f, h);
        var sb = new StringBuilder($"trace {name} k={k} n={n} uphill={uphill}\n");
        uint tick = 1;
        for (var i = 0; i < k + 40; i++, tick++)
        {
            Step(motor, world, ref s, tick, h, i == k, i >= k);
            var pos = V(s.Position);
            var gap = phys.Raycast(pos + Vector3.up * 0.05f, Vector3.down, out var hit, 5f, mask) ? hit.distance - 0.05f : 99f;
            sb.Append($"{i,3} y={pos.y:F3} vy={s.Velocity.Y:F2} hv={new Vector2(s.Velocity.X, s.Velocity.Z).magnitude:F2} g={(s.Has(MotorState.Grounded) ? 1 : 0)} gap={gap:F3} hit={(gap < 98f ? hit.collider.name : "-")}\n");
        }

        Debug.Log("[ramps] " + sb);
        File.WriteAllText(CapsuleRunShots.Arg("-fallrushRampOut"), sb.ToString());
    }

    private static int CheckMap(string scenePath, string label, StringBuilder sb)
    {
        var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
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
        var phys = scene.GetPhysicsScene();
        var mask = PhysxMotorWorld.GameplayMask();

        // Flat baseline: a hop from the first spawn.
        var sp = map.SpawnPoints()[0];
        var flat = Hop(motor, world, phys, mask, new Vector3(sp.X, sp.Y + 0.02f, sp.Z), Vector3.forward, 4, out _);

        sb.Append($"## {label}\n\n- flat hop: {flat} ticks\n\n");
        var ramps = new List<(BoxCollider bc, float pitch)>();
        foreach (var bc in Object.FindObjectsByType<BoxCollider>(FindObjectsSortMode.None))
        {
            if (!bc.enabled || bc.isTrigger || ((1 << bc.gameObject.layer) & mask) == 0 || bc.transform.IsChildOf(probeParent))
                continue;
            var n = TopNormal(bc.transform, out _, out _);
            var pitch = Vector3.Angle(n, Vector3.up);
            if (pitch < 3f || pitch > MovementConfigData.Ground_MaxSlopeDeg)
                continue;
            ramps.Add((bc, pitch));
        }

        ramps.Sort((x, y) => string.CompareOrdinal(Name(x.bc.transform), Name(y.bc.transform)));
        if (ramps.Count == 0)
        {
            sb.Append("- no tilted walkable boxes\n\n");
            Object.DestroyImmediate(probeParent.gameObject);
            return 0;
        }

        sb.Append("| ramp | pitch | length | hop-up (jump after 3…15 ticks) | jump-on (0…1.2 m before the low edge) | glide ticks | verdict |\n|---|---|---|---|---|---|---|\n");
        var fails = 0;
        foreach (var (bc, pitch) in ramps)
        {
            var t = bc.transform;
            var n = TopNormal(t, out var axisA, out var halfA);
            var size = Vector3.Scale(bc.size, t.lossyScale);
            var centre = t.TransformPoint(bc.center);
            var halfT = HalfAlong(t, bc, n);
            var top = centre + n * halfT;
            var uphill = axisA.y >= 0f ? axisA : -axisA;
            var h = new Vector3(uphill.x, 0f, uphill.z).normalized;
            var low = top - uphill * halfA;

            var hop = new StringBuilder();
            var glide = 0;
            var bad = 0;
            foreach (var k in new[] { 3, 6, 9, 12, 15 })
            {
                var air = Hop(motor, world, phys, mask, top - uphill * (halfA * 0.75f) + n * 0.03f, h, k, out var g);
                glide += g;
                var ok = air >= 0 && air <= flat + 8;
                bad += ok ? 0 : 1;
                hop.Append(ok ? "✓" : "✗");
            }

            var on = new StringBuilder();
            foreach (var d in new[] { 0f, 0.3f, 0.6f, 0.9f, 1.2f })
            {
                var r = JumpOn(motor, world, phys, mask, low, h, d, flat, out var g);
                glide += g;
                if (r == 0)
                    on.Append('·'); // no floor before this ramp's low edge
                else
                {
                    on.Append(r > 0 ? "✓" : "✗");
                    bad += r > 0 ? 0 : 1;
                }
            }

            fails += bad;
            sb.Append($"| {Name(t)} | {pitch:F1}° | {halfA * 2f:F2} m | {hop} | {on} | {glide} | {(bad == 0 ? "ok" : $"**{bad} fail**")} |\n");
        }

        sb.Append('\n');
        Object.DestroyImmediate(probeParent.gameObject);
        return fails;
    }

    /// <summary>Run along h from feet, jump after k ticks; airborne ticks until grounded again (-1 = never).</summary>
    private static int Hop(KinematicMotor motor, PhysxMotorWorld world, PhysicsScene phys, int mask, Vector3 feet, Vector3 h, int k, out int glide)
    {
        var s = Place(feet, h);
        glide = 0;
        uint tick = 1;
        var jumpTick = -1;
        for (var i = 0; i < k + MaxAir + 2; i++, tick++)
        {
            var press = i == k;
            if (press)
                jumpTick = i;
            Step(motor, world, ref s, tick, h, press, jumpTick >= 0);
            if (jumpTick >= 0 && i > jumpTick)
            {
                if (s.Has(MotorState.Grounded))
                    return i - jumpTick;
                if (Near(phys, mask, s))
                    glide++;
            }
        }

        return -1;
    }

    /// <summary>1 = landed on the ramp in time, -1 = failed, 0 = no floor to run from.</summary>
    private static int JumpOn(KinematicMotor motor, PhysxMotorWorld world, PhysicsScene phys, int mask, Vector3 low, Vector3 h, float d, int flat, out int glide)
    {
        glide = 0;
        var start = low - h * 2.6f;
        if (!phys.Raycast(start + Vector3.up * 0.6f, Vector3.down, out var hit, 1.2f, mask) || Mathf.Abs(hit.point.y - low.y) > 0.4f)
            return 0;
        var s = Place(hit.point + Vector3.up * 0.02f, h);
        uint tick = 1;
        var jumpTick = -1;
        for (var i = 0; i < 40 + MaxAir; i++, tick++)
        {
            var pos = V(s.Position);
            var toEdge = Vector3.Dot(low - pos, h);
            var press = jumpTick < 0 && toEdge <= d && s.Has(MotorState.Grounded);
            if (press)
                jumpTick = i;
            Step(motor, world, ref s, tick, h, press, jumpTick >= 0);
            if (jumpTick >= 0 && i > jumpTick)
            {
                if (s.Has(MotorState.Grounded))
                    return i - jumpTick <= flat + 8 && V(s.Position).y > low.y - 0.3f ? 1 : -1;
                if (Near(phys, mask, s))
                    glide++;
            }
        }

        return -1;
    }

    private static void Step(KinematicMotor motor, PhysxMotorWorld world, ref MotorState s, uint tick, Vector3 h, bool press, bool held)
    {
        var cmd = new InputCmd
        {
            Seq = tick, Tick = tick, CamYaw = Yaw16(h),
            MoveX = (sbyte)Mathf.RoundToInt(h.x * 127f), MoveY = (sbyte)Mathf.RoundToInt(h.z * 127f),
            Buttons = (byte)((held ? InputCmd.JumpHeld : 0) | (press ? InputCmd.JumpPressed : 0)),
        };
        world.BeginTick(tick);
        motor.Step(ref s, in cmd, MovementConfig.Default.Dt, world);
    }

    private static bool Near(PhysicsScene phys, int mask, MotorState s) =>
        s.Velocity.Y > -1f && phys.Raycast(V(s.Position) + Vector3.up * 0.05f, Vector3.down, out var hit, 0.25f, mask) && hit.normal.y > 0.5f;

    /// <summary>The box face pointing most up, the in-face axis that climbs most (unit) and its half length.</summary>
    private static Vector3 TopNormal(Transform t, out Vector3 climbAxis, out float half)
    {
        var axes = new[] { t.right, t.up, t.forward };
        var bc = t.GetComponent<BoxCollider>();
        var size = Vector3.Scale(bc != null ? bc.size : Vector3.one, t.lossyScale);
        var best = 0;
        for (var i = 1; i < 3; i++)
            if (Mathf.Abs(axes[i].y) > Mathf.Abs(axes[best].y))
                best = i;
        var n = axes[best] * Mathf.Sign(axes[best].y);
        int a = (best + 1) % 3, b = (best + 2) % 3;
        var pick = Mathf.Abs(axes[a].y) >= Mathf.Abs(axes[b].y) ? a : b;
        climbAxis = axes[pick];
        half = Mathf.Abs(size[pick]) * 0.5f;
        return n;
    }

    private static float HalfAlong(Transform t, BoxCollider bc, Vector3 n)
    {
        var size = Vector3.Scale(bc.size, t.lossyScale);
        var axes = new[] { t.right, t.up, t.forward };
        for (var i = 0; i < 3; i++)
            if (Mathf.Abs(Vector3.Dot(axes[i], n)) > 0.99f)
                return Mathf.Abs(size[i]) * 0.5f;
        return Mathf.Abs(size.y) * 0.5f;
    }

    private static string Name(Transform t) => t.parent != null ? t.parent.name + "/" + t.name : t.name;

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
}
#endif
