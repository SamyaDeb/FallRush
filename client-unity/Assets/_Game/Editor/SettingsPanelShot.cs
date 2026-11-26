#if !UNITY_SERVER // editor tools on the client assembly (excluded from the Server subtarget)
using System.IO;
using FallRush.Client;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 2026-11-27 settings fix proof: renders the real <see cref="SettingsPanel"/> (menu variant + in-game variant with its
/// footer line) at the 1280 × 720 reference into PNGs. Batch, needs graphics (no <c>-nographics</c>):
/// <c>-executeMethod SettingsPanelShot.Run [-fallrushShotDir dir]</c>.
/// </summary>
public static class SettingsPanelShot
{
    public static void Run()
    {
        var dir = Arg("-fallrushShotDir") ?? Path.Combine(Directory.GetCurrentDirectory(), "../docs/proof/settings-fix");
        Directory.CreateDirectory(dir);
        var code = 0;
        try
        {
            Shot(Path.Combine(dir, "settings-menu.png"), false);
            Shot(Path.Combine(dir, "settings-ingame.png"), true);
            Debug.Log("[settings-shot] saved to " + Path.GetFullPath(dir));
        }
        catch (System.Exception e)
        {
            Debug.LogException(e);
            code = 1;
        }

        EditorApplication.Exit(code);
    }

    private static void Shot(string path, bool inGame)
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
        var cam = new GameObject("Cam").AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = UiKit.Hex(0xA9D8F5);
        cam.orthographic = true;
        cam.targetTexture = rt;
        var canvasRt = UiKit.CreateCanvas("ShotCanvas");
        var canvas = canvasRt.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = cam;
        canvas.planeDistance = 5f;
        var panel = SettingsPanel.Build(canvasRt, null);
        if (inGame)
            UiKit.Label(panel, "Esc or Done to close · click the game to play · F1 controls", 13, UiKit.Grey);
        for (var i = 0; i < 4; i++)
            Canvas.ForceUpdateCanvases();
        cam.Render();
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Debug.Log($"[settings-shot] {path} panel {panel.rect.size}");
    }

    private static string Arg(string name)
    {
        var a = System.Environment.GetCommandLineArgs();
        for (var i = 0; i < a.Length - 1; i++)
            if (a[i] == name)
                return a[i + 1];
        return null;
    }
}
#endif
