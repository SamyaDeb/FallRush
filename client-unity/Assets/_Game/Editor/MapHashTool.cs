using System.IO;
using FallRush.Net;
using FallRush.Shared;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Prints (and dumps) the map content hash of a committed scene: <c>-executeMethod MapHashTool.Run -fallrushScene Map_Sandbox</c>.</summary>
public static class MapHashTool
{
    public static void Run()
    {
        var args = System.Environment.GetCommandLineArgs();
        var name = "Map_Sandbox";
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "-fallrushScene")
                name = args[i + 1];
        var scene = EditorSceneManager.OpenScene($"Assets/_Game/Scenes/{name}.unity", OpenSceneMode.Single);
        Physics.SyncTransforms();
        foreach (var root in scene.GetRootGameObjects())
        {
            var info = root.GetComponentInChildren<MapInfo>();
            if (info == null)
                continue;
            Dump(name + "-open", info);
        }
    }

    public static void Dump(string label, MapInfo info)
    {
        var bytes = info.BuildContent().CanonicalBytes();
        var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".m1-logs"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, $"maphash-{label}.bin"), bytes);
        Debug.Log($"[setup] maphash {label} {Sha256.Hex(Sha256.Hash(bytes))} bytes={bytes.Length}");
    }
}
