using FallRush.Net;
using FallRush.Shared;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Tuning aid: one traced bot race on Map_BlockPartyDash. <c>-executeMethod QaDebug.Run [-fallrushQaVariant 1 -fallrushQaRun 0]</c>.</summary>
public static class QaDebug
{
    public static void Run()
    {
        var scene = EditorSceneManager.OpenScene(BlockPartyDashBuilder.ScenePath, OpenSceneMode.Single);
        Physics.simulationMode = SimulationMode.Script;
        Physics.SyncTransforms();
        MapInfo map = null;
        foreach (var r in scene.GetRootGameObjects())
            map ??= r.GetComponentInChildren<MapInfo>();
        var args = System.Environment.GetCommandLineArgs();
        int Arg(string n, int d) { for (var i = 0; i < args.Length - 1; i++) if (args[i] == n && int.TryParse(args[i + 1], out var v)) return v; return d; }
        MapQa.Trace = true;
        var probe = PhysxMotorWorld.CreateProbe(map.transform, MovementConfig.Default.Radius, MovementConfig.Default.Height);
        var runs = Arg("-fallrushQaRuns", 1);
        for (var k = 0; k < runs; k++)
        {
            var st = MapQa.RunOne(map, probe, Arg("-fallrushQaVariant", 0), Arg("-fallrushQaModifier", 0), Arg("-fallrushQaRun", 0) + k);
            Debug.Log($"[qa-debug] {st.Result} falls={st.Falls} hits={st.Hits} stuck={st.Stuck} soft={st.SoftLocks} {st.SoftLockReasons}");
        }
    }
}
