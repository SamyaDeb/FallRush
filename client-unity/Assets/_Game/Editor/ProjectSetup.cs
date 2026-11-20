using System;
using System.IO;
using FallRush.Net;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Deterministic, re-runnable project setup for M1: player settings, the NetworkManager prefab, the
/// placeholder Boot/Menu/Lobby scenes and the build scene list. Run from the menu or with
/// <c>-executeMethod ProjectSetup.Run</c>. Client screen types are found by name so this compiles even when the
/// active target is Dedicated Server (where FallRush.Client is excluded by its UNITY_SERVER constraint).
/// </summary>
public static class ProjectSetup
{
    public const string Root = "Assets/_Game";
    public const string ScenesDir = Root + "/Scenes";
    public const string BootScene = ScenesDir + "/Boot.unity";
    public const string MenuScene = ScenesDir + "/Menu.unity";
    public const string LobbyScene = ScenesDir + "/Lobby.unity";
    public const string NetPrefab = Root + "/Prefabs/Net/NetworkManager.prefab";
    public const string ClientConfigAsset = Root + "/ScriptableObjects/Net/NetClientConfig.asset";
    public const string BundleVersion = "0.2.0-m2";

    private static readonly Color BootBg = new Color(0x7B / 255f, 0x2F / 255f, 0xF7 / 255f);   // Grape
    private static readonly Color MenuBg = new Color(0x3F / 255f, 0xA9 / 255f, 0xF5 / 255f);   // Sky
    private static readonly Color LobbyBg = new Color(0xBF / 255f, 0xE6 / 255f, 0xFF / 255f);  // Sky light

    [MenuItem("Fall Rush/Setup/Run M1 Project Setup")]
    public static void Run()
    {
        ApplyPlayerSettings();
        var config = EnsureClientConfig();
        var prefab = EnsureNetworkManagerPrefab(config);
        CreateBootScene(prefab);
        CreateScreenScene(MenuScene, MenuBg, "FallRush.Client.MenuScreen, FallRush.Client", "MenuScreen");
        CreateScreenScene(LobbyScene, LobbyBg, "FallRush.Client.LobbyScreen, FallRush.Client", "LobbyScreen");

        EditorBuildSettings.scenes = new[]
        {
            new EditorBuildSettingsScene(BootScene, true),
            new EditorBuildSettingsScene(MenuScene, true),
            new EditorBuildSettingsScene(LobbyScene, true),
            new EditorBuildSettingsScene(SandboxBuilder.ScenePath, true),
        };

        AssetDatabase.SaveAssets();
        Debug.Log("[setup] M1 project setup complete");
    }

    /// <summary>
    /// TMP Essential Resources (fonts + TMP Settings). Needed by Solana.Unity-SDK's WalletAdapterUI prefab,
    /// which references TMP's LiberationSans SDF. Our own UI uses legacy uGUI Text.
    /// Batch: <c>-executeMethod ProjectSetup.ImportTmpEssentials</c>.
    /// </summary>
    [MenuItem("Fall Rush/Setup/Import TMP Essential Resources")]
    public static void ImportTmpEssentials()
    {
        if (AssetDatabase.IsValidFolder("Assets/TextMesh Pro"))
        {
            Debug.Log("[setup] TMP Essential Resources already present");
            return;
        }

        // ImportPackage completes on a later editor tick, so in batch mode (run WITHOUT -quit) exit from the callbacks.
        AssetDatabase.importPackageCompleted += name =>
        {
            AssetDatabase.Refresh();
            var ok = AssetDatabase.IsValidFolder("Assets/TextMesh Pro");
            Debug.Log($"[setup] TMP Essential Resources imported ({name}): {ok}");
            if (Application.isBatchMode)
                EditorApplication.Exit(ok ? 0 : 1);
        };
        AssetDatabase.importPackageFailed += (name, error) =>
        {
            Debug.LogError($"[setup] TMP import failed ({name}): {error}");
            if (Application.isBatchMode)
                EditorApplication.Exit(1);
        };
        TMPro.TMP_PackageResourceImporter.ImportResources(importEssentials: true, importExamples: false, interactive: false);
    }

    public static void ApplyPlayerSettings()
    {
        PlayerSettings.companyName = "Fall Rush";
        PlayerSettings.productName = "Fall Rush";
        PlayerSettings.bundleVersion = BundleVersion;
        PlayerSettings.runInBackground = true;

        // WebGL: Brotli, served with Content-Encoding: br by nginx → no decompression fallback.
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
        PlayerSettings.WebGL.decompressionFallback = false;
        PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.None; // release default; dev builds override in Build.cs
        PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.WebGL, ManagedStrippingLevel.Medium);

        // Server: Mono (no IL2CPP cross toolchain needed).
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Server, ScriptingImplementation.Mono2x);
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
    }

    private static NetClientConfig EnsureClientConfig()
    {
        var config = AssetDatabase.LoadAssetAtPath<NetClientConfig>(ClientConfigAsset);
        if (config != null)
            return config;
        EnsureFolder(Path.GetDirectoryName(ClientConfigAsset));
        config = ScriptableObject.CreateInstance<NetClientConfig>();
        AssetDatabase.CreateAsset(config, ClientConfigAsset);
        return config;
    }

    private static GameObject EnsureNetworkManagerPrefab(NetClientConfig config)
    {
        EnsureFolder(Path.GetDirectoryName(NetPrefab));
        var go = new GameObject("NetworkManager");
        try
        {
            var boot = go.AddComponent<NetBootstrap>(); // RequireComponent adds NetworkManager + UnityTransport
            go.AddComponent<PingService>();
            var nm = go.GetComponent<NetworkManager>();
            var utp = go.GetComponent<UnityTransport>();

            utp.UseWebSockets = true;
            utp.UseEncryption = false;
            nm.NetworkConfig.NetworkTransport = utp;
            nm.NetworkConfig.TickRate = 30;
            nm.NetworkConfig.ConnectionApproval = true;
            nm.NetworkConfig.EnableSceneManagement = false;

            var so = new SerializedObject(boot);
            so.FindProperty("clientConfig").objectReferenceValue = config;
            so.ApplyModifiedPropertiesWithoutUndo();

            return PrefabUtility.SaveAsPrefabAsset(go, NetPrefab);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    private static void CreateBootScene(GameObject prefab)
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        CreateCamera(BootBg);
        PrefabUtility.InstantiatePrefab(prefab, scene);
        Save(scene, BootScene);
    }

    private static void CreateScreenScene(string path, Color bg, string typeName, string goName)
    {
        var type = Type.GetType(typeName);
        if (type == null)
            throw new InvalidOperationException($"{typeName} not found. Run setup with a client (non-server) build target active.");

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        CreateCamera(bg);
        new GameObject(goName, type);
        Save(scene, path);
    }

    private static void CreateCamera(Color bg)
    {
        var cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
        cam.tag = "MainCamera";
        var c = cam.GetComponent<Camera>();
        c.clearFlags = CameraClearFlags.SolidColor;
        c.backgroundColor = bg;
        cam.transform.position = new Vector3(0, 1, -10);
    }

    private static void Save(Scene scene, string path)
    {
        EnsureFolder(Path.GetDirectoryName(path));
        if (!EditorSceneManager.SaveScene(scene, path))
            throw new IOException($"Failed to save {path}");
    }

    private static void EnsureFolder(string folder)
    {
        folder = folder.Replace('\\', '/');
        if (AssetDatabase.IsValidFolder(folder))
            return;
        var parent = Path.GetDirectoryName(folder)!.Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
    }
}
