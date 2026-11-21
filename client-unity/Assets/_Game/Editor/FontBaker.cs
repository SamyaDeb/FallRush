using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

/// <summary>
/// G3.1 fonts (OFL, ASSET_LICENSES.md): bakes static TextMeshPro SDF assets so every glyph we use renders in WebGL
/// (the built-in font had no "●", "▼", "–"; there is no OS font fallback in a browser build):
/// <list type="bullet">
/// <item><c>FR_Display</c>: Lilita One (rounded heavy display), headings, banners, signs, the timer.</item>
/// <item><c>FR_Body</c>: Varela Round (clean rounded sans), body text, tables.</item>
/// <item><c>FR_Symbols</c>: Noto Sans Symbols 2, the fallback for ● ▼ ★ ◆ ⬢ ✓ ♛ … (both faces fall back to it).</item>
/// </list>
/// Static atlases (no source font in the build). Batch: <c>-executeMethod FontBaker.Run</c> (needs graphics: no -nographics).
/// </summary>
public static class FontBaker
{
    public const string Dir = ProjectSetup.Root + "/Fonts";
    public const string OutDir = ProjectSetup.Root + "/Resources/FallRush/Fonts";

    /// <summary>Every non-ASCII character the UI and signs use (checked at bake time).</summary>
    public const string Symbols = "●○◉▼▲▶◀◆◇⬢⬡★☆✓✔✕×♛♥♦·•…–—°"; // no arrows: none of the three faces has them

    private static string Ascii()
    {
        var sb = new StringBuilder();
        for (var c = 32; c < 127; c++)
            sb.Append((char)c);
        sb.Append("éèêëáàâäíìîïóòôöúùûüçñ¡¿€£§©®™«»“”‘’…–—·•×°");
        return sb.ToString();
    }

    [MenuItem("Fall Rush/Setup/Bake fonts")]
    public static void Run()
    {
        if (!AssetDatabase.IsValidFolder(ProjectSetup.Root + "/Resources/FallRush"))
            AssetDatabase.CreateFolder(ProjectSetup.Root + "/Resources", "FallRush");
        if (!AssetDatabase.IsValidFolder(OutDir))
            AssetDatabase.CreateFolder(ProjectSetup.Root + "/Resources/FallRush", "Fonts");

        var symbols = Bake("NotoSansSymbols2-Regular.ttf", "FR_Symbols", Symbols + "0123456789", 56, 512, null);
        var display = Bake("LilitaOne-Regular.ttf", "FR_Display", Ascii(), 72, 1024, symbols);
        var body = Bake("VarelaRound-Regular.ttf", "FR_Body", Ascii(), 64, 1024, symbols);

        // The symbols face must cover what the two text faces miss.
        foreach (var fa in new[] { display, body })
        {
            var missing = new StringBuilder();
            foreach (var ch in Symbols)
                if (!fa.HasCharacter(ch, searchFallbacks: true))
                    missing.Append(ch);
            Debug.Log($"[setup] font {fa.name}: symbols missing after fallback: \"{missing}\" ({missing.Length})");
        }

        Digits(display);
        Digits(body);
        AssetDatabase.SaveAssets();
        Debug.Log("[setup] fonts baked");
    }

    private static void Digits(TMP_FontAsset fa)
    {
        var sb = new StringBuilder();
        foreach (var ch in "0123456789")
            if (fa.characterLookupTable.TryGetValue(ch, out var c))
                sb.Append($"{ch}:{c.glyph.metrics.horizontalAdvance:F0} ");
        Debug.Log($"[setup] font {fa.name} digit advances (tabular if equal): {sb}");
    }

    private static TMP_FontAsset Bake(string file, string name, string chars, int pointSize, int atlas, TMP_FontAsset fallback)
    {
        var font = AssetDatabase.LoadAssetAtPath<Font>($"{Dir}/{file}") ?? throw new System.IO.FileNotFoundException(file);
        var path = $"{OutDir}/{name}.asset";
        if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path) != null)
            AssetDatabase.DeleteAsset(path);
        var fa = TMP_FontAsset.CreateFontAsset(font, pointSize, 9, GlyphRenderMode.SDFAA, atlas, atlas, AtlasPopulationMode.Dynamic, false);
        fa.name = name;
        fa.TryAddCharacters(chars, out var missing);
        Debug.Log($"[setup] font {name} from {file}: {fa.characterTable.Count} glyphs, missing \"{missing}\"");

        AssetDatabase.CreateAsset(fa, path);
        foreach (var t in fa.atlasTextures)
        {
            t.name = name + " Atlas";
            AssetDatabase.AddObjectToAsset(t, fa);
        }

        fa.material.name = name + " Material";
        AssetDatabase.AddObjectToAsset(fa.material, fa);
        if (fallback != null)
            fa.fallbackFontAssetTable = new List<TMP_FontAsset> { fallback };
        fa.atlasPopulationMode = AtlasPopulationMode.Static; // no source font at runtime (smaller build, no rasterising in WebGL)
        EditorUtility.SetDirty(fa);
        AssetDatabase.SaveAssets();
        return fa;
    }
}
