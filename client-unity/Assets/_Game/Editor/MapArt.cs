using System.Collections.Generic;
using FallRush.Net;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// G2 art pass for the code-built maps (doc 17 §3), run by every map builder right before it saves the scene, so a
/// rebuild reproduces the look exactly. It never touches a collider, a hazard definition or a transform the motor uses:
/// <list type="number">
/// <item>Materials: every builder material is retoned onto <c>FallRush/Toon</c> by its role (palette tokens).</item>
/// <item>Blocks: every render cube under <c>Geometry</c> / <c>Hazards</c> gets a chamfered, vertex-coloured toy-block mesh
/// of its own size (light top, colour band, pastel sides, fake AO toward the bottom) on one shared material, so static
/// batching merges a whole map into a few draws. The BoxCollider is independent of the mesh and stays as built.</item>
/// <item>Hazards: every rigid hazard visual group (spinner bar, mallet, door, turn table, platform, bumper, belt chevrons,
/// pad arrows, falling block) is merged into one vertex-coloured mesh (1 draw each). Vertex alpha marks the part that
/// flashes for a telegraph; uv2.x numbers the belt chevrons for the travelling light.</item>
/// <item>Look: gradient skybox, trilight ambient, light distance fog in the horizon colour, the sun.</item>
/// <item>Dressing: <see cref="MapDressing"/> (props, clouds, signs; layer 0, no colliders).</item>
/// </list>
/// </summary>
public static class MapArt
{
    public const string ToonDir = ProjectSetup.Root + "/Materials/Toon";
    private static PaletteTokens P => PaletteTokens.Get();
    private static Mesh _cube;
    private static readonly Dictionary<string, Mesh> BevelCache = new Dictionary<string, Mesh>();

    // ------------------------------------------------------------------ entry

    public static void Apply(Scene scene, MapInfo info, string mapKey)
    {
        EnsurePaletteAsset();
        EnsureRuntimeAssets();
        BevelCache.Clear();
        BakeCache.Clear();
        _cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        var geo = Find(scene, "Geometry");
        var hz = Find(scene, "Hazards");
        var vc = VertexColorMaterial();

        // 2. hazard groups first (they read the original materials' colours), then 3. blocks.
        MapDressing.DecorateHazards(info, mapKey);
        if (info != null)
            MergeHazards(info, vc, mapKey);
        if (geo != null)
            Blocks(geo, vc, mapKey);
        if (hz != null)
            Blocks(hz, vc, mapKey);

        // 1. every remaining builder material → toon by role.
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (r.GetComponent<TextMesh>() != null || r.GetComponent<TMPro.TextMeshPro>() != null)
                continue;
            foreach (var m in r.sharedMaterials)
                if (m != null && AssetDatabase.GetAssetPath(m).StartsWith(SandboxBuilder.MaterialsDir))
                    Retone(m);
        }

        if (info != null)
        {
            info.playerMaterial = CharacterMaterial();
            info.unlitMaterial = UnlitMaterial();
        }

        Look(scene, mapKey);
        MapDressing.Dress(scene, info, mapKey, vc);
        if (System.Environment.GetEnvironmentVariable("FALLRUSH_NO_MERGE") != "1")
            MergeStatic(scene);
        ShadowCasters(scene);
        AssetDatabase.SaveAssets();
    }

    /// <summary>
    /// G2.4: merges every static, enabled renderer under Geometry / Hazards into one mesh per (material, 32 m cell), so a
    /// map draws as a handful of chunks instead of many static-batch ranges. The originals keep their colliders and
    /// transforms; only their renderers are switched off (render-only boxes keep the GameObject, nothing moves).
    /// </summary>
    private static void MergeStatic(Scene scene)
    {
        const float cell = 32f;
        var groups = new Dictionary<(Material, int, int, int), List<MeshRenderer>>();
        foreach (var root in scene.GetRootGameObjects())
        {
            if (root.name != "Geometry" && root.name != "Hazards")
                continue;
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mf = r.GetComponent<MeshFilter>();
                if (!r.enabled || !r.gameObject.isStatic || mf == null || mf.sharedMesh == null || r.sharedMaterial == null || r.sharedMaterials.Length != 1)
                    continue;
                if (r.GetComponent<TMPro.TextMeshPro>() != null || r.sharedMaterial.renderQueue >= (int)RenderQueue.Transparent)
                    continue;
                var c = r.bounds.center;
                var key = (r.sharedMaterial, Mathf.FloorToInt(c.x / cell), Mathf.FloorToInt(c.y / cell), Mathf.FloorToInt(c.z / cell));
                if (!groups.TryGetValue(key, out var list))
                    groups[key] = list = new List<MeshRenderer>();
                list.Add(r);
            }
        }

        var parent = new GameObject("MergedStatic").transform;
        parent.gameObject.isStatic = true;
        var n = 0;
        foreach (var kv in groups)
        {
            if (kv.Value.Count < 2)
                continue;
            var mb = new MapDressing.MeshBuilder();
            foreach (var r in kv.Value)
            {
                var mesh = r.GetComponent<MeshFilter>().sharedMesh;
                if (!mesh.isReadable)
                    continue;
                mb.Add(mesh, r.transform.localToWorldMatrix);
                r.enabled = false;
            }

            var go = mb.Emit(parent, $"Chunk_{kv.Key.Item1.name}_{kv.Key.Item2}_{kv.Key.Item3}_{kv.Key.Item4}", kv.Key.Item1);
            go.isStatic = true;
            n++;
        }

        Debug.Log($"[setup] merged static renderers into {n} chunks");
    }

    /// <summary>
    /// G4.8: every opaque map renderer (chunks, hazards, props) casts and receives the soft look's sun shadows; clouds,
    /// glass, text and transparent parts never cast. The candy look's light casts none, so this costs nothing there.
    /// </summary>
    private static void ShadowCasters(Scene scene)
    {
        foreach (var root in scene.GetRootGameObjects())
        {
            if (root.name != "Geometry" && root.name != "Hazards" && root.name != "MergedStatic" && root.name != "Dressing")
                continue;
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var m = r.sharedMaterial;
                var cast = m != null && m.renderQueue < (int)RenderQueue.Transparent && (!m.HasProperty("_ZWrite") || m.GetFloat("_ZWrite") > 0.5f) && r.GetComponent<TMPro.TextMeshPro>() == null &&
                           r.GetComponent<TextMesh>() == null && !r.name.StartsWith("Clouds") && r.name != "Glass" && !r.name.StartsWith("Backdrop") && (!m.HasProperty("_Unlit") || m.GetFloat("_Unlit") < 0.5f);
                r.shadowCastingMode = cast ? ShadowCastingMode.On : ShadowCastingMode.Off;
                r.receiveShadows = cast;
            }
        }
    }

    private static Transform Find(Scene scene, string name)
    {
        foreach (var go in scene.GetRootGameObjects())
            if (go.name == name)
                return go.transform;
        return null;
    }

    // ------------------------------------------------------------------ palette + materials

    /// <summary>Runtime-loaded client materials (Resources, so they ship): the blob shadow.</summary>
    public static void EnsureRuntimeAssets()
    {
        const string path = ProjectSetup.Root + "/Resources/FallRush/BlobShadow.mat";
        var sh = Shader.Find("FallRush/BlobShadow") ?? throw new System.InvalidOperationException("FallRush/BlobShadow not found");
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(sh) { enableInstancing = true };
            AssetDatabase.CreateAsset(m, path);
        }

        m.shader = sh;
        m.enableInstancing = true;
        m.SetColor("_Color", new Color(P.ink.r, P.ink.g, P.ink.b, 0.5f));
        EditorUtility.SetDirty(m);

        // G3.3 effects: one material + a generated 2×2 sheet (soft dot, star, confetti square, ring).
        const string sheetPath = ProjectSetup.Root + "/Resources/FallRush/FxSheet.png";
        if (!System.IO.File.Exists(sheetPath))
        {
            System.IO.File.WriteAllBytes(sheetPath, FxSheet().EncodeToPNG());
            AssetDatabase.ImportAsset(sheetPath);
        }

        var fxPath = ProjectSetup.Root + "/Resources/FallRush/Fx.mat";
        var fxSh = Shader.Find("FallRush/Fx") ?? throw new System.InvalidOperationException("FallRush/Fx not found");
        var fx = AssetDatabase.LoadAssetAtPath<Material>(fxPath);
        if (fx == null)
        {
            fx = new Material(fxSh);
            AssetDatabase.CreateAsset(fx, fxPath);
        }

        fx.shader = fxSh;
        fx.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(sheetPath));
        EditorUtility.SetDirty(fx);
    }

    private static Texture2D FxSheet()
    {
        const int n = 128, c = 64;
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var cell = (x / c) + (1 - y / c) * 2; // 0 TL soft dot, 1 TR star, 2 BL square, 3 BR ring
                var u = (x % c + 0.5f) / c * 2f - 1f;
                var v = (y % c + 0.5f) / c * 2f - 1f;
                var r = Mathf.Sqrt(u * u + v * v);
                float a;
                switch (cell)
                {
                    case 0: a = Mathf.Clamp01(1f - r) * Mathf.Clamp01(1f - r) * 1.4f; break;
                    case 1:
                    {
                        var ang = Mathf.Atan2(v, u) + Mathf.PI * 0.5f;
                        var k = Mathf.Abs(Mathf.Cos(ang * 2.5f));
                        var edge = Mathf.Lerp(0.42f, 0.95f, Mathf.Pow(k, 3f));
                        a = Mathf.Clamp01((edge - r) * 18f);
                        break;
                    }
                    case 2: a = Mathf.Clamp01((0.8f - Mathf.Max(Mathf.Abs(u), Mathf.Abs(v) * 1.6f)) * 20f); break;
                    default: a = Mathf.Clamp01((0.12f - Mathf.Abs(r - 0.78f)) * 14f); break;
                }

                t.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(a)));
            }

        t.Apply();
        return t;
    }

    public static void EnsurePaletteAsset()
    {
        const string dir = ProjectSetup.Root + "/Resources/FallRush";
        const string path = dir + "/Palette.asset";
        if (!AssetDatabase.IsValidFolder(ProjectSetup.Root + "/Resources"))
            AssetDatabase.CreateFolder(ProjectSetup.Root, "Resources");
        if (!AssetDatabase.IsValidFolder(dir))
            AssetDatabase.CreateFolder(ProjectSetup.Root + "/Resources", "FallRush");
        var fresh = ScriptableObject.CreateInstance<PaletteTokens>(); // code defaults = doc 17 §3.1 (the source of truth)
        var a = AssetDatabase.LoadAssetAtPath<PaletteTokens>(path);
        if (a == null)
            AssetDatabase.CreateAsset(fresh, path);
        else
        {
            EditorUtility.CopySerialized(fresh, a); // regenerate: the asset always equals the code tokens
            EditorUtility.SetDirty(a);
            Object.DestroyImmediate(fresh);
        }
    }

    private static Shader ToonShader => Shader.Find("FallRush/Toon") ?? throw new System.InvalidOperationException("FallRush/Toon shader not found");

    public static Material ToonAsset(string name, Color color, System.Action<Material> setup = null)
    {
        if (!AssetDatabase.IsValidFolder(ToonDir))
            AssetDatabase.CreateFolder(ProjectSetup.Root + "/Materials", "Toon");
        return ToonAssetAt($"{ToonDir}/{name}.mat", color, setup);
    }

    public static Material ToonAssetAt(string path, Color color, System.Action<Material> setup = null)
    {
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(ToonShader);
            AssetDatabase.CreateAsset(m, path);
        }

        m.shader = ToonShader;
        Defaults(m, color);
        setup?.Invoke(m);
        EditorUtility.SetDirty(m);
        return m;
    }

    private static void Defaults(Material m, Color color)
    {
        m.SetColor("_BaseColor", color);
        m.SetColor("_ShadeColor", P.shade);
        m.SetFloat("_RimStrength", 0.3f);
        m.SetFloat("_ViewRim", 0f);
        m.SetFloat("_RimAll", 0f);
        m.SetFloat("_Gloss", 0f);
        m.SetFloat("_Unlit", 0f);
        m.SetColor("_Emission", Color.black);
        m.SetFloat("_Stripes", 0f);
        m.SetColor("_FlashColor", new Color(1, 1, 1, 0));
        m.SetFloat("_Scroll", -1000f);
        m.SetFloat("_FaceOn", 0f);
        m.SetFloat("_RushlingMesh", 0f);
        m.SetFloat("_SrcBlend", 1f);
        m.SetFloat("_DstBlend", 0f);
        m.SetFloat("_ZWrite", 1f);
        m.SetFloat("_Cull", 2f);
        m.renderQueue = -1;
        m.enableInstancing = true;
        m.SetShaderPassEnabled("SRPDefaultUnlit", false); // outline: Rushlings only
    }

    public static void Transparent(Material m, float alpha)
    {
        var c = m.GetColor("_BaseColor");
        c.a = alpha;
        m.SetColor("_BaseColor", c);
        m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0f);
        m.renderQueue = (int)RenderQueue.Transparent;
    }

    /// <summary>White base × vertex colours: every block and merged hazard of a map shares it.</summary>
    public static Material VertexColorMaterial() => ToonAsset("VertexColor", Color.white, m =>
    {
        m.SetFloat("_RimStrength", 0.22f);
        m.SetFloat("_ViewRim", 0.38f); // G4.6: hit-hazard parts only (uv2.y = 4), never up-facing faces
    });

    /// <summary>Rushling body material (cloned per colour at runtime is no longer needed: the parts carry vertex colours).</summary>
    public static Material CharacterMaterial() => ToonAssetAt(ProjectSetup.Root + "/Resources/FallRush/Character.mat", Color.white, m =>
    {
        m.SetFloat("_Gloss", 0.55f);
        m.SetFloat("_RimStrength", 0.42f);
        m.SetFloat("_ViewRim", 0.35f); // G4.6: Rushlings keep a rim in shade too
        m.SetFloat("_RimAll", 1f);
        m.SetColor("_OutlineColor", P.ink);
        m.SetFloat("_OutlineWidth", 0.016f);
        m.SetFloat("_RushlingMesh", 1f); // uv2.x = lid on Rushling meshes
        m.SetShaderPassEnabled("SRPDefaultUnlit", true);
    });

    public static Material UnlitMaterial() => ToonAsset("Unlit", Color.white, m => m.SetFloat("_Unlit", 1f));

    /// <summary>Static hazard posts: saturated yellow with diagonal magenta world-space stripes.</summary>
    public static Material StripeMaterial() => ToonAsset("HazardStripes", P.hazardYellow, m =>
    {
        m.SetFloat("_Stripes", 1f);
        m.SetColor("_StripeColor", P.hazardMagenta);
        m.SetFloat("_StripeWidth", 0.28f);
        m.SetFloat("_ViewRim", 0.38f); // G4.6: static hazard posts get the view rim too
        m.SetFloat("_RimAll", 1f);
    });

    /// <summary>Retones a greybox builder material in place (same asset, so scene references stay) by its name.</summary>
    private static void Retone(Material m)
    {
        var c = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : Color.white;
        m.shader = ToonShader;
        Defaults(m, c);
        switch (m.name)
        {
            case "HazardYellow": m.SetColor("_BaseColor", P.hazardYellow); break;
            case "HazardMagenta": m.SetColor("_BaseColor", P.hazardMagenta); break;
            case "SpawnMint":
            case "Spring": m.SetColor("_BaseColor", P.mint); break;
            case "Grape": m.SetColor("_BaseColor", P.grape); break;
            case "Checker":
            case "Ink": m.SetColor("_BaseColor", P.ink); break;
            case "CharacterUnlit": m.SetFloat("_Unlit", 1f); break;
            case "LobbyGlass":
            case "GooDome":
                Transparent(m, 0.35f);
                m.SetFloat("_Gloss", 0.8f);
                m.SetFloat("_RimStrength", 0.7f);
                break;
        }

        EditorUtility.SetDirty(m);
    }

    // ------------------------------------------------------------------ block styles

    public struct Style
    {
        public Color Top, Band, Side, Rim;
        public float BandH, Bevel;
        public bool Stripes;
        /// <summary>G4.8: a walkable section surface (pastel top, padded rims, soft pattern <see cref="Pattern"/>).</summary>
        public bool Walkable;
        public int Pattern;
    }

    private static bool IsCube(Mesh m) => m != null && (m == _cube || m.name == "Cube" && AssetDatabase.GetAssetPath(m) == "Library/unity default resources");

    private static bool IsCube(MeshFilter mf) => mf != null && IsCube(mf.sharedMesh);

    /// <summary>Materials that keep their own look (transparent glass, thin decals): not turned into toy blocks.</summary>
    private static bool KeepsOwnMaterial(string name) => name == "LobbyGlass" || name == "GooDome";

    /// <summary>
    /// G4.6: the surface hue of a built object = its section (doc 17 §3.1 hue table, <see cref="PaletteTokens.Sections"/>).
    /// The section comes from the builder (its layout constants / object names), never from the colour of a material.
    /// </summary>
    public static SurfaceHue HueFor(string mapKey, Transform t)
    {
        string section = null;
        switch (mapKey)
        {
            case "BlockPartyDash":
            {
                var r = t.GetComponent<Renderer>();
                section = BlockPartyDashBuilder.SectionAt(r != null && r.enabled ? r.bounds.center : t.position);
                break;
            }
            case "GooTower": section = GooTowerBuilder.SectionAt(t); break;
            case "StarScramble": section = StarScrambleBuilder.SectionAt(t); break;
            case "Lobby": section = LobbySection(t); break;
        }

        return PaletteTokens.HueOf(mapKey, section);
    }

    /// <summary>Lobby: the plaza deck Soda blue, its checker tiles (<c>Tile_x_z</c>) alternating Bubblegum / Tangerine.</summary>
    private static string LobbySection(Transform t)
    {
        var parts = t.name.Split('_');
        if (parts.Length == 3 && parts[0] == "Tile" && int.TryParse(parts[1], out var x) && int.TryParse(parts[2], out var z))
            return ((x - z) / 2 & 1) == 0 ? "tileA" : "tileB";
        return "deck";
    }

    /// <summary>Walkable surface materials: their top is the section hue, their sides the hue's side.</summary>
    private static bool IsSurface(string m) => m == "Walkable" || m == "BpdDeck" || m == "LobbyDeck" || m == "StarFloor" || m == "GooLedge" ||
                                              m == "GooStep" || m == "Feature" || m == "BpdBlock" || m == "GooBlock" || m == "ShouldBlock" ||
                                              m == "Platform" || m == "LobbyTile";

    private static bool IsWall(string m) => m == "BpdWall" || m == "LobbyWall" || m == "GooRail" || m == "Beam";

    /// <summary>The belt body: the section hue at low saturation (darker than the decks, so the light chevrons read as moving).</summary>
    public static Color BeltTop(Color hueTop)
    {
        Color.RGBToHSV(hueTop, out var h, out _, out _);
        return Color.HSVToRGB(h, 0.5f, 0.78f);
    }

    /// <summary>Turntable stripes / spokes: the section hue darkened toward ink (readable as moving, never mint on a walkable top).</summary>
    public static Color MoverStripe(Color hueTop) => Color.Lerp(hueTop, PaletteTokens.Get().ink, 0.35f);

    private static readonly Dictionary<(Color, LookId), Color> BakeCache = new Dictionary<(Color, LookId), Color>();

    /// <summary>
    /// G4.6-3 / G4.8: an sRGB palette colour → the vertex colour that shows it on a lit top after the grade of
    /// <paramref name="look"/> (<see cref="FallRush.Client.GraphicsQuality.AlbedoFor(Color, LookId)"/>: its sun, sky ambient and grade).
    /// </summary>
    public static Color Bake(Color srgb, LookId look)
    {
        if (BakeCache.TryGetValue((srgb, look), out var c))
            return c;
        c = FallRush.Client.GraphicsQuality.AlbedoFor(srgb, look);
        BakeCache[(srgb, look)] = c;
        return c;
    }

    /// <summary>The tone-on-tone pattern of a section hue (soft look, walkable tops): one per hue, so one per section.</summary>
    public static int PatternFor(SurfaceHue h)
    {
        switch (h)
        {
            case SurfaceHue.SodaBlue: return 1;   // wide soft stripes
            case SurfaceHue.Tangerine: return 2;  // polka dots
            case SurfaceHue.SourApple: return 3;  // checks
            case SurfaceHue.Bubblegum: return 4;  // chevrons
            case SurfaceHue.GrapeFizz: return 2;
            default: return 1;
        }
    }

    private static Style StyleFor(string matName, string mapKey, SurfaceHue hue, LookId look)
    {
        var p = P;
        var top = p.Top(hue, look);
        var side = p.Side(hue, look);
        var s = new Style
        {
            Top = top, Band = Color.Lerp(top, Color.white, 0.3f), Side = side, Rim = p.Rim(hue, look), BandH = 0.14f, Bevel = 0.05f,
            Walkable = IsSurface(matName), Pattern = PatternFor(hue),
        };
        if (IsSurface(matName))
            return s;
        s.Pattern = 0;
        s.Rim = s.Band;
        if (IsWall(matName))
        {
            s.Side = Color.Lerp(side, p.ink, 0.12f); // rails / walls a shade deeper than the decks
            return s;
        }

        switch (matName)
        {
            case "SpawnMint":
            case "Spring":
                s.Top = s.Band = s.Rim = Color.Lerp(p.mint, Color.white, 0.15f);
                s.Side = p.mint;
                s.Bevel = 0.03f;
                break;
            case "Grape":
            case "CrumbleFrame":
                // G4.6: machine purple, deepened toward ink (the lighter grape top sat within ΔE 10 of goo and Grape fizz).
                s.Top = s.Band = s.Rim = Color.Lerp(p.grape, p.ink, 0.35f);
                s.Side = Color.Lerp(p.grape, p.ink, 0.5f);
                break;
            case "Checker":
            case "Ink":
                s.Top = s.Band = s.Side = s.Rim = p.ink;
                s.Bevel = 0.02f;
                break;
            case "HazardYellow":
            case "HazardMagenta":
                s.Top = s.Band = s.Side = s.Rim = Color.white;
                s.Stripes = true;
                break;
            case "Belt":
                s.Top = s.Band = s.Rim = BeltTop(p.HueTop(hue));
                s.Side = Color.Lerp(BeltTop(p.HueTop(hue)), p.ink, 0.25f);
                break;
            case "BeltArrow":
                s.Top = s.Band = s.Side = s.Rim = new Color(0.97f, 0.97f, 1f);
                s.Bevel = 0f;
                break;
            case "Door":
                s.Top = s.Band = s.Side = s.Rim = new Color(1f, 0.93f, 0.66f);
                break;
            case "GooCore":
                s.Top = s.Band = s.Rim = Color.Lerp(p.goo, Color.white, 0.55f);
                s.Side = Color.Lerp(p.goo, Color.white, 0.35f);
                break;
            case "StarRim":
                s.Top = s.Band = s.Side = s.Rim = p.sunYellow;
                break;
            case "StarRing":
                s.Top = s.Band = s.Side = s.Rim = side; // the static centre's edge line (its hue's side)
                break;
        }

        return s;
    }

    /// <summary>Round prisms (floors, discs, hubs) that take a block style: re-coloured by vertex like the toy blocks.</summary>
    private static bool StyledPrism(string m) => IsSurface(m) || IsWall(m) || m == "Grape" || m == "StarRing";

    /// <summary>
    /// A copy of a prism mesh (top cap at local y 0, body down to −1) in a block style, both looks: top cap, sides with AO,
    /// dark bottom. G4.8: <paramref name="scale"/> (radius, height, radius in metres) lets it extend down to
    /// <paramref name="extraDown"/> more metres and carry a padded rim ring over the exposed arcs <paramref name="rimRuns"/>.
    /// </summary>
    private static Mesh StyledMesh(Mesh src, Style soft, Style candy, Vector3 scale, float extraDown, List<Vector2> rimRuns, int topRole, int sideRole)
    {
        var key = $"{src.name}_{ColorUtility.ToHtmlStringRGB(soft.Top)}{ColorUtility.ToHtmlStringRGB(soft.Side)}{ColorUtility.ToHtmlStringRGB(candy.Top)}{ColorUtility.ToHtmlStringRGB(candy.Side)}_{scale.x:F2}_{scale.y:F2}_{extraDown:F2}_{topRole}{sideRole}_{RunsKey(rimRuns)}";
        if (BevelCache.TryGetValue(key, out var cached))
            return cached;
        var v = src.vertices;
        var n = src.normals;
        var tri = src.triangles;
        var bs = new ToyMesh.Baked(soft, LookId.Soft);
        var bc = new ToyMesh.Baked(candy, LookId.Candy);
        var buf = new ToyMesh.Buffers();
        var hm = Mathf.Max(1e-3f, scale.y);
        var height = hm + extraDown;
        for (var i = 0; i < v.Length; i++)
        {
            // metres, top at 0; the body bottom (y −1) moves down by extraDown.
            var y = v[i].y < -0.5f ? -height : v[i].y * hm;
            var p = new Vector3(v[i].x * scale.x, y, v[i].z * scale.z);
            var nn = n.Length > i ? Vector3.Scale(n[i], new Vector3(1f / scale.x, 1f / hm, 1f / scale.z)).normalized : Vector3.up;
            var ny = nn.y;
            Color cs, cc;
            int role;
            if (ny > 0.7f)
            {
                cs = bs.Top;
                cc = bc.Top;
                role = topRole;
            }
            else if (ny < -0.7f)
            {
                cs = bs.Bottom;
                cc = bc.Bottom;
                role = sideRole;
            }
            else
            {
                cs = bs.SideAt(y + height, -y, height);
                cc = bc.SideAt(y + height, -y, height);
                role = sideRole;
            }

            buf.Add(p, nn, cs, cc, role, soft.Pattern);
        }

        buf.T.AddRange(tri);
        if (rimRuns != null && rimRuns.Count > 0)
            ToyMesh.RimRing(buf, scale.x, rimRuns, bs.Rim, bc.Rim, soft.Pattern);
        var m = buf.ToMesh("Styled_" + Mathf.Abs(key.GetHashCode()).ToString("X"), new Vector3(scale.x, hm, scale.z));
        BevelCache[key] = m;
        return m;
    }

    private static string RunsKey(List<Vector2> runs)
    {
        if (runs == null)
            return "";
        var sb = new System.Text.StringBuilder();
        foreach (var r in runs)
            sb.Append($"{r.x:F2}:{r.y:F2},");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ G4.8 neighbour analysis (render only)

    /// <summary>The top rounding radius of a walkable block (0.15–0.25 m with its size).</summary>
    public const float TopRadius = 0.2f, TightRadius = 0.03f, MinThickness = 0.6f;

    /// <summary>
    /// Is the point just outside a top edge covered by another solid at about the same height or higher (an abutting deck, a
    /// wall)? Then that edge gets no rim and a tight radius (no groove between two decks).
    /// </summary>
    private static bool Covered(Vector3 world, float topY, Collider self)
    {
        foreach (var c in Physics.OverlapSphere(world, 0.06f, ~0, QueryTriggerInteraction.Ignore))
            if (c != self && c.bounds.max.y >= topY - 0.2f)
                return true;
        return false;
    }

    /// <summary>The block's top edges: exposed runs get rims, mostly covered sides a tight radius.</summary>
    private static ToyMesh.Shape BlockShape(Transform t, Vector3 size, bool walkable, bool staticGeo)
    {
        var sh = new ToyMesh.Shape { Radius = walkable ? Mathf.Clamp(Mathf.Min(size.x, size.z) * 0.08f, 0.15f, 0.25f) : 0.12f };
        sh.TopRole = walkable ? ToyMesh.RoleTop : ToyMesh.RoleSide;
        var col = t.GetComponent<Collider>();
        if (!walkable || col == null || col.isTrigger)
            return sh;
        var hx = size.x * 0.5f;
        var hy = size.y * 0.5f;
        var hz = size.z * 0.5f;
        var l2w = t.localToWorldMatrix;
        var inv = new Vector3(1f / size.x, 1f / size.y, 1f / size.z);
        Vector3 W(Vector3 q) => l2w.MultiplyPoint3x4(Vector3.Scale(q, inv));
        var topY = W(new Vector3(0f, hy, 0f)).y;
        var rims = Mathf.Min(size.x, size.z) >= 0.9f;
        for (var side = 0; side < 4; side++)
        {
            ToyMesh.EdgeFrame(side, hx, hz, out var start, out var dir, out var outward, out var len);
            var steps = Mathf.Max(2, Mathf.CeilToInt(len / 0.4f));
            var covered = 0;
            var runStart = -1f;
            for (var k = 0; k <= steps; k++)
            {
                var u = len * k / steps;
                var q = start + dir * u + outward * 0.25f + Vector3.up * (hy - 0.08f);
                var cov = Covered(W(q), topY, col);
                covered += cov ? 1 : 0;
                if (!cov && runStart < 0f)
                    runStart = Mathf.Max(0f, u - len / steps * 0.5f);
                if ((cov || k == steps) && runStart >= 0f)
                {
                    var end = cov ? Mathf.Max(runStart, u - len / steps * 0.5f) : len;
                    if (k == steps && !cov)
                        end = len;
                    if (end - runStart > 0.3f && rims)
                        sh.Rims.Add(new ToyMesh.RimRun { Side = side, A = runStart <= len / steps * 0.5f + 1e-3f ? 0f : runStart, B = end });
                    runStart = -1f;
                }
            }

            if (covered * 2 > steps + 1)
                sh.SideRadius[side] = TightRadius;
        }

        // Chunky: extend the render mesh down to ≥ 0.6 m, only where nothing sits under it (2 m clear: no walk volume filled).
        if (staticGeo && size.y < MinThickness)
        {
            var extra = MinThickness - size.y;
            var c = W(new Vector3(0f, -hy - 0.02f - (extra + 2f) * 0.5f, 0f));
            var half = new Vector3(Mathf.Max(0.05f, hx - 0.1f), (extra + 2f) * 0.5f, Mathf.Max(0.05f, hz - 0.1f));
            var hits = Physics.OverlapBox(c, half, t.rotation, ~0, QueryTriggerInteraction.Ignore);
            var free = true;
            foreach (var h in hits)
                if (h != col)
                    free = false;
            if (free)
                sh.ExtraDown = extra;
        }

        return sh;
    }

    /// <summary>The exposed arcs (radians) of a round top of radius <paramref name="radius"/> (prism, top at the transform).</summary>
    private static List<Vector2> RingRuns(Transform t, float radius)
    {
        var runs = new List<Vector2>();
        var col = t.GetComponent<Collider>();
        if (col == null || radius < 0.9f)
            return runs;
        const int n = 48;
        var cov = new bool[n];
        var any = false;
        for (var k = 0; k < n; k++)
        {
            var a = k * Mathf.PI * 2f / n;
            var w = t.position + t.rotation * new Vector3(Mathf.Cos(a) * (radius + 0.25f), -0.08f, Mathf.Sin(a) * (radius + 0.25f));
            cov[k] = Covered(w, t.position.y, col);
            any |= cov[k];
        }

        if (!any)
        {
            runs.Add(new Vector2(0f, Mathf.PI * 2f));
            return runs;
        }

        // Runs of uncovered samples, starting after a covered one (wraps around).
        var first = System.Array.IndexOf(cov, true);
        var i = 0;
        while (i < n)
        {
            var k = (first + i) % n;
            if (cov[k])
            {
                i++;
                continue;
            }

            var s0 = first + i;
            while (i < n && !cov[(first + i) % n])
                i++;
            var a0 = (s0 - 0.5f) * Mathf.PI * 2f / n;
            var a1 = (first + i - 0.5f) * Mathf.PI * 2f / n;
            if (a1 - a0 > 0.3f / radius)
                runs.Add(new Vector2(a0, a1));
        }

        return runs;
    }

    private static float PrismExtra(Transform t, Vector3 scale)
    {
        if (scale.y >= MinThickness)
            return 0f;
        var col = t.GetComponent<Collider>();
        var extra = MinThickness - scale.y;
        var c = t.position + Vector3.down * (scale.y + 0.02f + (extra + 2f) * 0.5f);
        foreach (var h in Physics.OverlapBox(c, new Vector3(scale.x * 0.65f, (extra + 2f) * 0.5f, scale.z * 0.65f), t.rotation, ~0, QueryTriggerInteraction.Ignore))
            if (h != col)
                return 0f;
        return extra;
    }

    private static void Blocks(Transform root, Material vc, string mapKey)
    {
        var stripes = StripeMaterial();
        var staticGeo = root.name == "Geometry";
        Physics.SyncTransforms();
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
        {
            var r = mf.GetComponent<MeshRenderer>();
            if (r == null || r.sharedMaterial == null || KeepsOwnMaterial(r.sharedMaterial.name))
                continue;
            var t = mf.transform;
            var matName = r.sharedMaterial.name;
            var hue = HueFor(mapKey, t);
            var soft = StyleFor(matName, mapKey, hue, LookId.Soft);
            var candy = StyleFor(matName, mapKey, hue, LookId.Candy);
            if (!IsCube(mf))
            {
                // G4.6: round floors / discs / hubs (static prisms) take the section hue in vertex colours on the shared material.
                if (mf.sharedMesh != null && mf.sharedMesh.name.StartsWith("Prism") && StyledPrism(matName))
                {
                    var sc = Abs(t.lossyScale);
                    var walk = soft.Walkable && t.GetComponent<Collider>() != null;
                    mf.sharedMesh = StyledMesh(mf.sharedMesh, soft, candy, sc, walk && staticGeo ? PrismExtra(t, sc) : 0f, walk ? RingRuns(t, sc.x) : null,
                        walk ? ToyMesh.RoleTop : ToyMesh.RoleSide, ToyMesh.RoleSide);
                    r.sharedMaterial = vc;
                }

                continue;
            }

            var size = Abs(t.lossyScale);
            var sh = BlockShape(t, size, soft.Walkable, staticGeo);
            if (soft.Stripes)
                sh.SideRole = sh.TopRole = ToyMesh.RoleHazard;
            mf.sharedMesh = RoundBox(size, soft, candy, sh);
            r.sharedMaterial = soft.Stripes ? stripes : vc;
        }
    }

    private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

    // ------------------------------------------------------------------ toy blocks (G4.8: rounded, see ToyMesh)

    public static Mesh RoundBox(Vector3 size, Style soft, Style candy, ToyMesh.Shape sh)
    {
        var key = $"RB_{size.x:F3}_{size.y:F3}_{size.z:F3}_{ColorUtility.ToHtmlStringRGB(soft.Top)}{ColorUtility.ToHtmlStringRGB(soft.Side)}{ColorUtility.ToHtmlStringRGB(soft.Rim)}" +
                  $"{ColorUtility.ToHtmlStringRGB(candy.Top)}{ColorUtility.ToHtmlStringRGB(candy.Band)}{ColorUtility.ToHtmlStringRGB(candy.Side)}_{soft.Pattern}{(soft.Walkable ? 1 : 0)}_{candy.BandH:F2}_{sh.Key()}";
        if (BevelCache.TryGetValue(key, out var cached))
            return cached;
        var m = ToyMesh.RoundBox(size, soft, candy, sh);
        BevelCache[key] = m;
        return m;
    }

    /// <summary>
    /// A rounded toy block in normalised space (unit cube, for <c>localScale = size</c>) with one set of colours in both looks
    /// (props, dressing; only the bake differs per look). <paramref name="bevel"/> ≤ 0.01 keeps thin decals flat-edged; above
    /// it the corners round at ≥ 0.14 m (G4.8: chunky toy props). Role prop (soft gloss).
    /// </summary>
    public static Mesh BevelBoxRaw(Vector3 size, Color top, Color band, Color side, float bandH, float bevel)
    {
        var st = new Style { Top = top, Band = band, Side = side, Rim = band, BandH = bandH, Bevel = bevel };
        var sh = new ToyMesh.Shape { Radius = bevel <= 0.01f ? bevel : Mathf.Max(bevel, 0.14f), TopRole = ToyMesh.RoleProp, SideRole = ToyMesh.RoleProp };
        return RoundBox(size, st, st, sh);
    }

    // ------------------------------------------------------------------ hazard groups → one mesh each

    private static void MergeHazards(MapInfo info, Material vc, string mapKey)
    {
        foreach (var sw in info.sweepers)
            if (sw != null && sw.barVisual != null)
                Merge(sw.barVisual, vc, null, mapKey, hit: true);
        foreach (var pl in info.platforms)
            if (pl != null && pl.visual != null)
                Merge(pl.visual, vc, null, mapKey, mover: true); // G4.6: the mint rim → the hue's mover stripe
        foreach (var tt in info.turnTables)
            if (tt != null && tt.visual != null)
                Merge(tt.visual, vc, null, mapKey, mover: true);
        foreach (var d in info.doors)
            if (d != null && d.visual != null)
                Merge(d.visual, vc, t => t == d.visual || t.name.StartsWith("Bar_") ? 1f : 0f, mapKey, hit: true); // the door body (G4.2: or its grille bars) flashes, the stripes stay
        foreach (var ml in info.mallets)
            if (ml != null && ml.visual != null)
                Merge(ml.visual, vc, t => t.name == "HeadBody" ? 1f : 0f, mapKey, hit: true); // the head glows during the wind-up
        foreach (var bm in info.bumpers)
            if (bm != null && bm.visual != null)
            {
                // G2.4: the bumper body is drawn by its collider object; fold it into the bands' mesh (1 draw per bumper).
                // The MeshCollider stays exactly as built.
                var cr = bm.motorCollider != null ? bm.motorCollider.GetComponent<MeshRenderer>() : null;
                var cf = cr != null ? cr.GetComponent<MeshFilter>() : null;
                if (cr != null && cf != null)
                {
                    var body = new GameObject("Body");
                    body.transform.SetParent(bm.visual, false);
                    body.transform.SetPositionAndRotation(cr.transform.position, cr.transform.rotation);
                    body.transform.localScale = Vector3.Scale(cr.transform.lossyScale, new Vector3(1f / bm.visual.lossyScale.x, 1f / bm.visual.lossyScale.y, 1f / bm.visual.lossyScale.z));
                    body.AddComponent<MeshFilter>().sharedMesh = cf.sharedMesh;
                    body.AddComponent<MeshRenderer>().sharedMaterial = cr.sharedMaterial;
                    Object.DestroyImmediate(cr);
                    Object.DestroyImmediate(cf);
                }

                Merge(bm.visual, vc, null, mapKey, hit: true);
                bm.visual.gameObject.isStatic = true; // bumpers never move: static batching
            }
        foreach (var pd in info.pads)
            if (pd != null && pd.visual != null)
                Merge(pd.visual, vc, null, mapKey);
        foreach (var fb in info.fallingBlocks)
            if (fb != null && fb.visual != null)
                Merge(fb.visual, vc, null, mapKey, mover: true);
        foreach (var cv in info.conveyors)
            if (cv != null && cv.visual != null)
                Merge(cv.visual, vc, null, mapKey, chevrons: true);
    }

    /// <summary>
    /// Merges every MeshRenderer under <paramref name="root"/> (inclusive) into one mesh on the root, in its local space,
    /// with each part's material colour (and its block style) baked into vertex colours. Parts are removed; the root
    /// transform the client moves is kept. G4.6: walkable parts take the group's section hue; <paramref name="hit"/> groups
    /// (sweepers, mallets, doors, bumpers) flag uv2.y = 4 for the toon view rim; <paramref name="mover"/> (turntables) turns
    /// its stripes / spokes (and a moving platform its mint rim) into the hue's darker stripe (never mint on a walkable top).
    /// </summary>
    private static void Merge(Transform root, Material vc, System.Func<Transform, float> mask, string mapKey, bool chevrons = false, bool hit = false, bool mover = false)
    {
        var hue = HueFor(mapKey, root);
        var parts = root.GetComponentsInChildren<MeshRenderer>(true);
        if (parts.Length == 0)
            return;
        var v = new List<Vector3>();
        var n = new List<Vector3>();
        var c = new List<Color>();
        var u2 = new List<Vector2>();
        var u3 = new List<Vector4>();
        var tris = new List<int>();
        var toLocal = root.worldToLocalMatrix;
        var chevron = 0;
        foreach (var r in parts)
        {
            var mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null || r.sharedMaterial == null)
                continue;
            var mat = r.sharedMaterial;
            var col = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : Color.white;
            var mesh = mf.sharedMesh;
            var isCube = IsCube(mesh);
            var matName = mat.name;
            var stripes = matName == "HazardYellow" || matName == "HazardMagenta";
            var st = StyleFor(matName, mapKey, hue, LookId.Soft);
            var stc = StyleFor(matName, mapKey, hue, LookId.Candy);
            if (mover && (matName == "SpawnMint" || matName == "Grape" || matName == "TableStripe"))
            {
                // Soft: the hue's mid-tone side, a touch deeper (a pastel lerped toward ink reads grey on the pale decks).
                st.Top = st.Band = st.Side = st.Rim = Color.Lerp(P.Side(hue, LookId.Soft), P.ink, 0.3f);
                stc.Top = stc.Band = stc.Side = stc.Rim = MoverStripe(P.HueTop(hue));
                st.Bevel = stc.Bevel = 0f;
                st.Walkable = false;
            }

            // G4.8: hit hazards gloss (role 4); walkable mover parts (platform decks, turntable discs, falling blocks) take
            // the pastel top, its pattern-free top role and padded rims all round; everything else is a side.
            var walkPart = mover && st.Walkable;
            var partRole = hit ? ToyMesh.RoleHazard : ToyMesh.RoleSide;
            st.Pattern = walkPart ? 0 : st.Pattern;
            var tint = (Color?)null;
            var tintC = (Color?)null;
            var styled = false;
            if (isCube && !stripes)
            {
                var size = Abs(r.transform.lossyScale);
                var sh = new ToyMesh.Shape { Radius = walkPart ? Mathf.Clamp(Mathf.Min(size.x, size.z) * 0.08f, 0.15f, 0.25f) : 0.12f, TopRole = walkPart ? ToyMesh.RoleTop : partRole, SideRole = partRole };
                if (walkPart && Mathf.Min(size.x, size.z) >= 0.9f)
                    for (var side = 0; side < 4; side++)
                        sh.Rims.Add(new ToyMesh.RimRun { Side = side, A = 0f, B = side % 2 == 0 ? size.x : size.z });
                mesh = RoundBox(size, st, stc, sh);
                styled = true;
            }
            else if (!isCube && !stripes && mesh.name.StartsWith("Prism") && StyledPrism(matName))
            {
                var sc = Abs(r.transform.lossyScale);
                mesh = StyledMesh(mesh, st, stc, sc, 0f, walkPart && sc.x >= 0.9f ? new List<Vector2> { new Vector2(0f, Mathf.PI * 2f) } : null,
                    walkPart ? ToyMesh.RoleTop : partRole, partRole);
                styled = true;
            }
            else if (mover && (matName == "TableStripe" || matName == "SpawnMint" || matName == "Grape"))
            {
                tint = st.Top;
                tintC = stc.Top;
            }

            var m = toLocal * r.transform.localToWorldMatrix;
            var mv = mesh.vertices;
            var mn = mesh.normals;
            var mc = mesh.colors;
            var mu3 = new List<Vector4>();
            mesh.GetUVs(3, mu3);
            var a = mask != null ? mask(r.transform) : 0f;
            var idx = chevrons && r.transform.name.StartsWith("Chevron_") ? 1 + chevron / 2 : 0;
            if (idx > 0)
                chevron++;
            var baseIndex = v.Count;
            for (var i = 0; i < mv.Length; i++)
            {
                v.Add(m.MultiplyPoint3x4(mv[i]));
                n.Add(m.inverse.transpose.MultiplyVector(mn.Length > i ? mn[i] : Vector3.up).normalized);
                // Styled meshes are already baked (RoundBox / StyledMesh, both looks); material and token colours are sRGB: bake them per look.
                Color k, kc;
                var code = 2f + 8f * (stripes ? ToyMesh.RoleHazard : partRole);
                if (styled)
                {
                    k = mc.Length > i ? mc[i] : Color.white;
                    var d = mu3.Count > i ? mu3[i] : new Vector4(k.r, k.g, k.b, code);
                    kc = new Color(d.x, d.y, d.z, 1f);
                    code = d.w;
                }
                else
                {
                    k = Bake(col, LookId.Soft);
                    kc = Bake(col, LookId.Candy);
                }

                if (stripes)
                {
                    var sc = matName == "HazardYellow" ? P.hazardYellow : P.hazardMagenta;
                    k = Bake(sc, LookId.Soft);
                    kc = Bake(sc, LookId.Candy);
                }

                if (tint.HasValue)
                {
                    k = Bake(tint.Value, LookId.Soft);
                    kc = Bake(tintC.Value, LookId.Candy);
                }

                k.a = a;
                c.Add(k);
                u3.Add(new Vector4(kc.r, kc.g, kc.b, code));
                u2.Add(new Vector2(idx, hit ? 4f : 0f));
            }

            foreach (var t in mesh.triangles)
                tris.Add(baseIndex + t);
        }

        // Remove the parts (children first), then put the merged mesh on the root.
        foreach (var r in parts)
        {
            if (r == null)
                continue;
            if (r.transform == root)
            {
                Object.DestroyImmediate(r);
                var f = root.GetComponent<MeshFilter>();
                if (f != null)
                    Object.DestroyImmediate(f);
                continue;
            }

            var go = r.gameObject;
            Object.DestroyImmediate(r);
            var f2 = go.GetComponent<MeshFilter>();
            if (f2 != null)
                Object.DestroyImmediate(f2);
        }

        // Drop now-empty helper objects (no components, no children) under the root.
        for (var pass = 0; pass < 3; pass++)
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t != root && t.childCount == 0 && t.GetComponents<Component>().Length == 1)
                    Object.DestroyImmediate(t.gameObject);

        var merged = new Mesh { name = "Merged_" + root.parent.name + "_" + root.name };
        if (v.Count > 65000)
            merged.indexFormat = IndexFormat.UInt32;
        merged.SetVertices(v);
        merged.SetNormals(n);
        merged.SetColors(c);
        merged.SetUVs(1, u2);
        merged.SetUVs(3, u3);
        merged.SetTriangles(tris, 0);
        merged.RecalculateBounds();
        var mfr = root.gameObject.AddComponent<MeshFilter>();
        mfr.sharedMesh = merged;
        var mr = root.gameObject.AddComponent<MeshRenderer>();
        mr.sharedMaterial = vc;
        mr.shadowCastingMode = ShadowCastingMode.On; // G4.8: hazards cast the soft look's sun shadows (the candy light casts none)
        mr.receiveShadows = true;
        mr.lightProbeUsage = LightProbeUsage.Off;
        mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
        if (chevrons)
            mr.gameObject.name = root.name; // the driver finds the chevron count from the mesh (uv2.x max)
    }

    // ------------------------------------------------------------------ sky, light, fog

    public static Material SkyMaterial()
    {
        var path = ProjectSetup.Root + "/Resources/FallRush/Sky.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        var sh = Shader.Find("FallRush/SkyGradient") ?? throw new System.InvalidOperationException("FallRush/SkyGradient not found");
        if (m == null)
        {
            if (!AssetDatabase.IsValidFolder(ToonDir))
                AssetDatabase.CreateFolder(ProjectSetup.Root + "/Materials", "Toon");
            m = new Material(sh);
            AssetDatabase.CreateAsset(m, path);
        }

        m.shader = sh;
        m.SetColor("_Top", P.skyTop);
        m.SetColor("_Horizon", P.skyHorizon);
        EditorUtility.SetDirty(m);
        return m;
    }

    private static void Look(Scene scene, string mapKey)
    {
        RenderSettings.skybox = SkyMaterial();
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = P.ambientSky;
        RenderSettings.ambientEquatorColor = Color.Lerp(P.ambientSky, P.ambientGround, 0.5f);
        RenderSettings.ambientGroundColor = P.ambientGround;
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = P.skyHorizon;
        RenderSettings.fogStartDistance = mapKey == "BlockPartyDash" ? 90f : 70f;
        RenderSettings.fogEndDistance = mapKey == "BlockPartyDash" ? 380f : 300f;
        foreach (var go in scene.GetRootGameObjects())
        {
            var cam = go.GetComponent<Camera>();
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.Skybox;
                cam.farClipPlane = Mathf.Max(cam.farClipPlane, 420f);
            }

            var l = go.GetComponent<Light>();
            if (l != null && l.type == LightType.Directional)
            {
                l.color = P.sun;      // G4.6: warm sun #FFF1DC
                l.intensity = FallRush.Client.GraphicsQuality.SunIntensity;
                l.shadows = LightShadows.None; // G4.8: the client switches the soft look's shadows on (GraphicsQuality.ApplyShadows)
                l.transform.rotation = Quaternion.Euler(48f, -35f, 0f);
                RenderSettings.sun = l;
            }
        }
    }
}
