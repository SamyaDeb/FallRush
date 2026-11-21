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
        _cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        var geo = Find(scene, "Geometry");
        var hz = Find(scene, "Hazards");
        var vc = VertexColorMaterial();

        // 2. hazard groups first (they read the original materials' colours), then 3. blocks.
        if (info != null)
            MergeHazards(info, vc);
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
        AssetDatabase.SaveAssets();
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
    }

    public static void EnsurePaletteAsset()
    {
        const string dir = ProjectSetup.Root + "/Resources/FallRush";
        const string path = dir + "/Palette.asset";
        if (!AssetDatabase.IsValidFolder(ProjectSetup.Root + "/Resources"))
            AssetDatabase.CreateFolder(ProjectSetup.Root, "Resources");
        if (!AssetDatabase.IsValidFolder(dir))
            AssetDatabase.CreateFolder(ProjectSetup.Root + "/Resources", "FallRush");
        var a = AssetDatabase.LoadAssetAtPath<PaletteTokens>(path);
        if (a == null)
        {
            a = ScriptableObject.CreateInstance<PaletteTokens>(); // code defaults = doc 17 §3.1
            AssetDatabase.CreateAsset(a, path);
        }
    }

    private static Shader ToonShader => Shader.Find("FallRush/Toon") ?? throw new System.InvalidOperationException("FallRush/Toon shader not found");

    public static Material ToonAsset(string name, Color color, System.Action<Material> setup = null)
    {
        if (!AssetDatabase.IsValidFolder(ToonDir))
            AssetDatabase.CreateFolder(ProjectSetup.Root + "/Materials", "Toon");
        var path = $"{ToonDir}/{name}.mat";
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
        m.SetFloat("_Gloss", 0f);
        m.SetFloat("_Unlit", 0f);
        m.SetColor("_Emission", Color.black);
        m.SetFloat("_Stripes", 0f);
        m.SetColor("_FlashColor", new Color(1, 1, 1, 0));
        m.SetFloat("_Scroll", -1000f);
        m.SetFloat("_FaceOn", 0f);
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
    public static Material VertexColorMaterial() => ToonAsset("VertexColor", Color.white, m => m.SetFloat("_RimStrength", 0.22f));

    /// <summary>Rushling body material (cloned per colour at runtime is no longer needed: the parts carry vertex colours).</summary>
    public static Material CharacterMaterial() => ToonAsset("Character", Color.white, m =>
    {
        m.SetFloat("_Gloss", 0.55f);
        m.SetFloat("_RimStrength", 0.42f);
        m.SetColor("_OutlineColor", P.ink);
        m.SetFloat("_OutlineWidth", 0.016f);
        m.SetShaderPassEnabled("SRPDefaultUnlit", true);
    });

    public static Material UnlitMaterial() => ToonAsset("Unlit", Color.white, m => m.SetFloat("_Unlit", 1f));

    /// <summary>Static hazard posts: saturated yellow with diagonal magenta world-space stripes.</summary>
    public static Material StripeMaterial() => ToonAsset("HazardStripes", P.hazardYellow, m =>
    {
        m.SetFloat("_Stripes", 1f);
        m.SetColor("_StripeColor", P.hazardMagenta);
        m.SetFloat("_StripeWidth", 0.28f);
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
        public Color Top, Band, Side;
        public float BandH, Bevel;
        public bool Stripes;
    }

    private static bool IsCube(Mesh m) => m != null && (m == _cube || m.name == "Cube" && AssetDatabase.GetAssetPath(m) == "Library/unity default resources");

    private static bool IsCube(MeshFilter mf) => mf != null && IsCube(mf.sharedMesh);

    /// <summary>Materials that keep their own look (transparent glass, thin decals): not turned into toy blocks.</summary>
    private static bool KeepsOwnMaterial(string name) => name == "LobbyGlass" || name == "GooDome" || name == "StarRing";

    private static Style StyleFor(string matName, Vector3 pos, string mapKey)
    {
        var p = P;
        var hash = Mathf.Abs(Mathf.RoundToInt(pos.x * 0.37f) * 7 + Mathf.RoundToInt(pos.z * 0.29f) * 13 + Mathf.RoundToInt(pos.y) * 3);
        var pastel = p.blockSides[hash % p.blockSides.Length];
        var s = new Style { Top = p.walkTop, Band = p.walkBand, Side = p.walkSide, BandH = 0.14f, Bevel = 0.05f };
        switch (matName)
        {
            case "Walkable":
            case "BpdDeck":
            case "LobbyDeck":
            case "StarFloor":
            case "GooLedge":
            case "GooStep":
                s.Side = mapKey == "GooTower" ? Color.Lerp(p.walkSide, p.goo, 0.18f) : p.walkSide;
                break;
            case "Feature":
            case "BpdBlock":
            case "GooBlock":
            case "ShouldBlock":
            case "Platform":
            case "LobbyTile":
                s.Side = pastel;
                break;
            case "BpdWall":
            case "LobbyWall":
            case "GooRail":
            case "Beam":
                s.Side = p.wallSide;
                s.Top = Color.Lerp(p.walkTop, p.wallSide, 0.25f);
                break;
            case "SpawnMint":
            case "Spring":
                s.Top = s.Band = Color.Lerp(p.mint, Color.white, 0.15f);
                s.Side = p.mint;
                s.Bevel = 0.03f;
                break;
            case "Grape":
            case "CrumbleFrame":
                s.Top = s.Band = Color.Lerp(p.grape, Color.white, 0.25f);
                s.Side = p.grape;
                break;
            case "Checker":
            case "Ink":
                s.Top = s.Band = s.Side = p.ink;
                s.Bevel = 0.02f;
                break;
            case "HazardYellow":
            case "HazardMagenta":
                s.Top = s.Band = s.Side = Color.white;
                s.Stripes = true;
                break;
            case "Belt":
                s.Top = s.Band = new Color(0.66f, 0.64f, 0.86f);
                s.Side = new Color(0.55f, 0.52f, 0.78f);
                break;
            case "BeltArrow":
                s.Top = s.Band = s.Side = new Color(0.97f, 0.97f, 1f);
                s.Bevel = 0f;
                break;
            case "Door":
                s.Top = s.Band = s.Side = new Color(1f, 0.93f, 0.66f);
                break;
            case "GooCore":
                s.Top = s.Band = Color.Lerp(p.goo, Color.white, 0.55f);
                s.Side = Color.Lerp(p.goo, Color.white, 0.35f);
                break;
            case "StarRim":
                s.Top = s.Band = s.Side = p.sunYellow;
                break;
            default:
                return s;
        }

        return s;
    }

    private static void Blocks(Transform root, Material vc, string mapKey)
    {
        var stripes = StripeMaterial();
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (!IsCube(mf))
                continue;
            var r = mf.GetComponent<MeshRenderer>();
            if (r == null || r.sharedMaterial == null || KeepsOwnMaterial(r.sharedMaterial.name))
                continue;
            var t = mf.transform;
            var size = t.lossyScale;
            var st = StyleFor(r.sharedMaterial.name, t.position, mapKey);
            mf.sharedMesh = BevelBox(Abs(size), st);
            r.sharedMaterial = st.Stripes ? stripes : vc;
        }
    }

    private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

    // ------------------------------------------------------------------ chamfered toy block

    /// <summary>
    /// A chamfered box in normalised space (unit cube, for <c>localScale = size</c>) whose bevel, colour band and AO are
    /// sized in metres for <paramref name="size"/>. Flat-shaded faces + 45° chamfers + corner triangles.
    /// </summary>
    public static Mesh BevelBox(Vector3 size, Style st) => BevelBoxRaw(size, st.Top, st.Band, st.Side, st.BandH, st.Bevel);

    public static Mesh BevelBoxRaw(Vector3 size, Color top, Color band, Color side, float bandH, float bevel)
    {
        var key = $"{size.x:F3}_{size.y:F3}_{size.z:F3}_{ColorUtility.ToHtmlStringRGB(top)}{ColorUtility.ToHtmlStringRGB(band)}{ColorUtility.ToHtmlStringRGB(side)}_{bandH:F2}_{bevel:F3}";
        if (BevelCache.TryGetValue(key, out var cached))
            return cached;
        var b = Mathf.Min(bevel, Mathf.Min(size.x, Mathf.Min(size.y, size.z)) * 0.25f);
        var hx = size.x * 0.5f;
        var hy = size.y * 0.5f;
        var hz = size.z * 0.5f;
        var band0 = Mathf.Min(bandH, size.y * 0.3f);
        var v = new List<Vector3>();
        var n = new List<Vector3>();
        var c = new List<Color>();
        var tris = new List<int>();
        var ao = 0.74f;

        Color SideAt(float y)
        {
            // y in metres from the bottom: fake AO toward the bottom, the band at the top.
            var k = Mathf.Clamp01(y / Mathf.Max(0.3f, size.y * 0.8f));
            return Color.Lerp(side * ao, side, Mathf.SmoothStep(0f, 1f, k));
        }

        void Quad(Vector3 a, Vector3 bb, Vector3 cc, Vector3 d, Vector3 nn, Color ca, Color cb, Color ccl, Color cd)
        {
            var i = v.Count;
            v.Add(a); v.Add(bb); v.Add(cc); v.Add(d);
            for (var k = 0; k < 4; k++)
                n.Add(nn);
            c.Add(ca); c.Add(cb); c.Add(ccl); c.Add(cd);
            tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
            tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
        }

        void Tri(Vector3 a, Vector3 bb, Vector3 cc, Vector3 nn, Color col)
        {
            var i = v.Count;
            v.Add(a); v.Add(bb); v.Add(cc);
            for (var k = 0; k < 3; k++)
            {
                n.Add(nn);
                c.Add(col);
            }

            tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
        }

        // Top (y = hy), inset by b.
        Quad(new Vector3(-hx + b, hy, -hz + b), new Vector3(-hx + b, hy, hz - b), new Vector3(hx - b, hy, hz - b), new Vector3(hx - b, hy, -hz + b), Vector3.up, top, top, top, top);
        // Bottom.
        var bot = side * (ao * 0.85f);
        Quad(new Vector3(-hx + b, -hy, -hz + b), new Vector3(hx - b, -hy, -hz + b), new Vector3(hx - b, -hy, hz - b), new Vector3(-hx + b, -hy, hz - b), Vector3.down, bot, bot, bot, bot);

        // Four sides, each split into the lower body and the top band; chamfers between faces.
        var dirs = new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left };
        for (var f = 0; f < 4; f++)
        {
            var nn = dirs[f];
            var tangent = Vector3.Cross(Vector3.up, nn); // along the face, left → right seen from outside
            var halfW = Mathf.Abs(nn.z) > 0.5f ? hx : hz;
            var depth = Mathf.Abs(nn.z) > 0.5f ? hz : hx;
            var o = nn * depth;
            var y0 = -hy + b;
            var y1 = hy - b - band0;
            var y2 = hy - b;
            Vector3 P2(float u, float y) => o + tangent * u + Vector3.up * y;
            var w = halfW - b;
            if (y1 > y0)
                Quad(P2(-w, y0), P2(-w, y1), P2(w, y1), P2(w, y0), nn, SideAt(b), SideAt(y1 + hy), SideAt(y1 + hy), SideAt(b));
            var yb = Mathf.Max(y0, y1);
            Quad(P2(-w, yb), P2(-w, y2), P2(w, y2), P2(w, yb), nn, band, band, band, band);

            // Top chamfer (face ↔ top) and bottom chamfer (face ↔ bottom).
            var nTop = (nn + Vector3.up).normalized;
            var tIn = o - nn * b;
            Quad(P2(-w, y2), tIn + tangent * -w + Vector3.up * hy, tIn + tangent * w + Vector3.up * hy, P2(w, y2), nTop, band, top, top, band);
            var nBot = (nn + Vector3.down).normalized;
            Quad(tIn + tangent * -w - Vector3.up * hy, P2(-w, y0), P2(w, y0), tIn + tangent * w - Vector3.up * hy, nBot, bot, SideAt(b), SideAt(b), bot);

            // Vertical chamfer to the next face (at +tangent end) and the two corner triangles.
            var nx = dirs[(f + 1) % 4];
            var nCorner = (nn + nx).normalized;
            var nxDepth = Mathf.Abs(nx.z) > 0.5f ? hz : hx;
            Vector3 Q(float y) => nn * depth + tangent * w + Vector3.up * y;               // this face's right edge
            Vector3 R(float y) => nx * nxDepth - nn * (depth - b) + Vector3.up * y;     // next face's left edge
            if (y1 > y0)
                Quad(Q(y0), Q(y1), R(y1), R(y0), nCorner, SideAt(b), SideAt(y1 + hy), SideAt(y1 + hy), SideAt(b));
            Quad(Q(yb), Q(y2), R(y2), R(yb), nCorner, band, band, band, band);
            var topCorner = nn * (depth - b) + tangent * w + Vector3.up * hy;
            Tri(Q(y2), topCorner, R(y2), (nn + nx + Vector3.up).normalized, band);
            var botCorner = nn * (depth - b) + tangent * w - Vector3.up * hy;
            Tri(Q(y0), R(y0), botCorner, (nn + nx + Vector3.down).normalized, bot);
        }

        // Normalise to the unit cube; normals pre-multiplied by the scale (inverse-transpose of the object scale).
        var inv = new Vector3(1f / Mathf.Max(1e-4f, size.x), 1f / Mathf.Max(1e-4f, size.y), 1f / Mathf.Max(1e-4f, size.z));
        for (var i = 0; i < v.Count; i++)
        {
            v[i] = Vector3.Scale(v[i], inv);
            n[i] = Vector3.Scale(n[i], size).normalized;
        }

        var mesh = new Mesh { name = "ToyBlock_" + key.Substring(0, Mathf.Min(24, key.Length)) };
        mesh.SetVertices(v);
        mesh.SetNormals(n);
        mesh.SetColors(c);
        mesh.SetTriangles(FixWinding(v, n, tris), 0);
        mesh.RecalculateBounds();
        BevelCache[key] = mesh;
        return mesh;
    }

    /// <summary>Flips any triangle whose winding disagrees with its normal (the quads above are built by hand).</summary>
    private static List<int> FixWinding(List<Vector3> v, List<Vector3> n, List<int> t)
    {
        for (var i = 0; i < t.Count; i += 3)
        {
            var a = v[t[i]];
            var fn = Vector3.Cross(v[t[i + 1]] - a, v[t[i + 2]] - a);
            // Unity is clockwise-front (left-handed): the front face (clockwise) has its cross product along the normal.
            if (Vector3.Dot(fn, n[t[i]]) < 0f)
                (t[i + 1], t[i + 2]) = (t[i + 2], t[i + 1]);
        }

        return t;
    }

    // ------------------------------------------------------------------ hazard groups → one mesh each

    private static void MergeHazards(MapInfo info, Material vc)
    {
        foreach (var sw in info.sweepers)
            if (sw != null && sw.barVisual != null)
                Merge(sw.barVisual, vc, null);
        foreach (var pl in info.platforms)
            if (pl != null && pl.visual != null)
                Merge(pl.visual, vc, null);
        foreach (var tt in info.turnTables)
            if (tt != null && tt.visual != null)
                Merge(tt.visual, vc, null);
        foreach (var d in info.doors)
            if (d != null && d.visual != null)
                Merge(d.visual, vc, t => t == d.visual ? 1f : 0f); // the door body flashes, the stripes stay
        foreach (var ml in info.mallets)
            if (ml != null && ml.visual != null)
                Merge(ml.visual, vc, t => t.name == "HeadBody" ? 1f : 0f); // the head glows during the wind-up
        foreach (var bm in info.bumpers)
            if (bm != null && bm.visual != null)
                Merge(bm.visual, vc, null);
        foreach (var pd in info.pads)
            if (pd != null && pd.visual != null)
                Merge(pd.visual, vc, null);
        foreach (var fb in info.fallingBlocks)
            if (fb != null && fb.visual != null)
                Merge(fb.visual, vc, null);
        foreach (var cv in info.conveyors)
            if (cv != null && cv.visual != null)
                Merge(cv.visual, vc, null, chevrons: true);
    }

    /// <summary>
    /// Merges every MeshRenderer under <paramref name="root"/> (inclusive) into one mesh on the root, in its local space,
    /// with each part's material colour (and its block style) baked into vertex colours. Parts are removed; the root
    /// transform the client moves is kept.
    /// </summary>
    private static void Merge(Transform root, Material vc, System.Func<Transform, float> mask, bool chevrons = false)
    {
        var parts = root.GetComponentsInChildren<MeshRenderer>(true);
        if (parts.Length == 0)
            return;
        var v = new List<Vector3>();
        var n = new List<Vector3>();
        var c = new List<Color>();
        var u2 = new List<Vector2>();
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
            if (isCube && !stripes)
                mesh = BevelBox(Abs(r.transform.lossyScale), StyleFor(matName, r.transform.position, ""));
            var m = toLocal * r.transform.localToWorldMatrix;
            var mv = mesh.vertices;
            var mn = mesh.normals;
            var mc = mesh.colors;
            var a = mask != null ? mask(r.transform) : 0f;
            var idx = chevrons && r.transform.name.StartsWith("Chevron_") ? 1 + chevron / 2 : 0;
            if (idx > 0)
                chevron++;
            var baseIndex = v.Count;
            for (var i = 0; i < mv.Length; i++)
            {
                v.Add(m.MultiplyPoint3x4(mv[i]));
                n.Add(m.inverse.transpose.MultiplyVector(mn.Length > i ? mn[i] : Vector3.up).normalized);
                var k = isCube && !stripes ? (mc.Length > i ? mc[i] : Color.white) : col;
                if (stripes)
                    k = matName == "HazardYellow" ? P.hazardYellow : P.hazardMagenta;
                k.a = a;
                c.Add(k);
                u2.Add(new Vector2(idx, 0f));
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
        merged.SetTriangles(tris, 0);
        merged.RecalculateBounds();
        var mfr = root.gameObject.AddComponent<MeshFilter>();
        mfr.sharedMesh = merged;
        var mr = root.gameObject.AddComponent<MeshRenderer>();
        mr.sharedMaterial = vc;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.lightProbeUsage = LightProbeUsage.Off;
        mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
        if (chevrons)
            mr.gameObject.name = root.name; // the driver finds the chevron count from the mesh (uv2.x max)
    }

    // ------------------------------------------------------------------ sky, light, fog

    public static Material SkyMaterial()
    {
        var path = $"{ToonDir}/Sky.mat";
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
                l.color = P.sun;
                l.intensity = 1.2f;
                l.shadows = LightShadows.None;
                l.transform.rotation = Quaternion.Euler(48f, -35f, 0f);
                RenderSettings.sun = l;
            }
        }
    }
}
