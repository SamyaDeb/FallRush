using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Batch-mode build entry points (doc 04 §10). Use with the Editor closed:
/// <code>
/// Unity -batchmode -nographics -quit -projectPath client-unity -buildTarget WebGL -executeMethod Build.WebGL [-fallrushRelease]
/// Unity -batchmode -nographics -quit -projectPath client-unity -buildTarget Linux64 -standaloneBuildSubtarget Server -executeMethod Build.Server
/// </code>
/// Each exits non-zero on failure.
/// </summary>
public static class Build
{
    public const string WebGLOut = "Builds/WebGL";
    public const string ServerOut = "Builds/Server";
    public const string ServerExe = "FallRushServer.x86_64";
    public const string ReleaseArg = "-fallrushRelease";

    /// <summary>Fixed scene lists. The server never loads client screens.</summary>
    public static readonly string[] ClientScenes = { ProjectSetup.BootScene, ProjectSetup.MenuScene, ProjectSetup.LobbyScene };
    public static readonly string[] ServerScenes = { ProjectSetup.BootScene };

    [MenuItem("Fall Rush/Build/WebGL (development)")]
    public static void WebGLDevMenu() => BuildWebGL(release: false, exitWhenDone: false);

    [MenuItem("Fall Rush/Build/WebGL (release)")]
    public static void WebGLReleaseMenu() => BuildWebGL(release: true, exitWhenDone: false);

    [MenuItem("Fall Rush/Build/Linux Dedicated Server")]
    public static void ServerMenu() => BuildServer(exitWhenDone: false);

    /// <summary>Batch entry: development by default, release with <c>-fallrushRelease</c>.</summary>
    public static void WebGL() => BuildWebGL(HasArg(ReleaseArg), exitWhenDone: true);

    /// <summary>Batch entry: Linux64, Dedicated Server subtarget, Mono.</summary>
    public static void Server() => BuildServer(exitWhenDone: true);

    private static void BuildWebGL(bool release, bool exitWhenDone)
    {
        var previousExceptions = PlayerSettings.WebGL.exceptionSupport;
        var ok = false;
        try
        {
            ProjectSetup.ApplyPlayerSettings();
            PlayerSettings.WebGL.exceptionSupport = release
                ? WebGLExceptionSupport.None
                : WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;

            var options = new BuildPlayerOptions
            {
                scenes = ClientScenes,
                locationPathName = WebGLOut,
                target = BuildTarget.WebGL,
                targetGroup = BuildTargetGroup.WebGL,
                options = release ? BuildOptions.None : BuildOptions.Development,
            };
            ok = Run($"webgl-{(release ? "release" : "dev")}", options, WebGLOut);
        }
        finally
        {
            // Keep ProjectSettings.asset stable in git: the committed value is the release one.
            // (Must run before EditorApplication.Exit, which never returns.)
            PlayerSettings.WebGL.exceptionSupport = previousExceptions;
            SavePlayerSettings();
        }

        Finish(ok, exitWhenDone);
    }

    private static void BuildServer(bool exitWhenDone)
    {
        ProjectSetup.ApplyPlayerSettings();
        var options = new BuildPlayerOptions
        {
            scenes = ServerScenes,
            locationPathName = Path.Combine(ServerOut, ServerExe),
            target = BuildTarget.StandaloneLinux64,
            targetGroup = BuildTargetGroup.Standalone,
            subtarget = (int)StandaloneBuildSubtarget.Server,
            options = BuildOptions.None,
        };
        var ok = Run("server", options, ServerOut);
        SavePlayerSettings();
        Finish(ok, exitWhenDone);
    }

    private static bool Run(string label, BuildPlayerOptions options, string outDir)
    {
        foreach (var scene in options.scenes)
        {
            if (!File.Exists(scene))
            {
                Debug.LogError($"[build] FAILED {label}: missing scene {scene} (run ProjectSetup.Run)");
                return false;
            }
        }

        Debug.Log($"[build] start {label} target={options.target} subtarget={options.subtarget} scenes={string.Join(",", options.scenes)} out={options.locationPathName}");
        var report = BuildPipeline.BuildPlayer(options);
        var s = report.summary;
        var outBytes = DirSize(outDir);
        Debug.Log($"[build] result {label} {s.result} time={s.totalTime.TotalSeconds:F1}s reportedSize={s.totalSize} outDirBytes={outBytes} errors={s.totalErrors} warnings={s.totalWarnings}");

        if (s.result == BuildResult.Succeeded)
            return true;

        foreach (var step in report.steps)
            foreach (var m in step.messages.Where(m => m.type == LogType.Error || m.type == LogType.Exception))
                Debug.LogError($"[build] {step.name}: {m.content}");
        Debug.LogError($"[build] FAILED {label}: {s.result}");
        return false;
    }

    private static void Finish(bool ok, bool exitWhenDone)
    {
        if (exitWhenDone)
            EditorApplication.Exit(ok ? 0 : 1);
        else if (!ok)
            throw new BuildFailedException("Fall Rush build failed (see [build] lines in the console).");
    }

    /// <summary>PlayerSettings are not flushed by AssetDatabase.SaveAssets unless the settings object is dirty.</summary>
    private static void SavePlayerSettings()
    {
        foreach (var ps in Resources.FindObjectsOfTypeAll<PlayerSettings>())
            EditorUtility.SetDirty(ps);
        AssetDatabase.SaveAssets();
    }

    private static long DirSize(string dir)
    {
        if (!Directory.Exists(dir))
            return 0;
        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
    }

    private static bool HasArg(string arg) =>
        Environment.GetCommandLineArgs().Any(a => string.Equals(a, arg, StringComparison.OrdinalIgnoreCase));
}
