using System.Collections.Generic;
using System.IO;
using System.Text;
using FallRush.Client;
using FallRush.Net;
using FallRush.Shared;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// G4.6 candy palette proof, rendered in the Editor (batch, needs graphics: no <c>-nographics</c>):
/// <list type="bullet">
/// <item>per map (Capsule Run, Goo Tower, Star Fountain, Lid Flip) + the lobby: the spawn view (follow framing 6.1 m behind,
/// 2.5 m up, FOV 55), one action shot and an orthographic overhead, all through the runtime post volume
/// (<see cref="GraphicsQuality.CreateShotVolume"/>, High) with the client's ambient globals (<see cref="WorldLook.OnMapLoaded"/>)
/// and the skybox. File names carry the tag (<c>-fallrushPaletteTag before|after</c>): "before" was shot on the G4.5 look;</item>
/// <item>an Editor frustum draw-call count of each spawn view (the G4.5 method; the stand-ins hidden);</item>
/// <item>the measured top colour of every section (a raycast finds the walkable top under a sample point, the pixel patch
/// around its projection in the overhead is read back: HSV after lighting and the grade);</item>
/// <item>"after" only: the colourblind sheets (Machado 2009 deuteranopia / protanopia, severity 1, in linear RGB), the Lid Flip
/// lit-vs-ring luminance check from shot pixels, and the palette swatch sheet.</item>
/// </list>
/// Rushlings are real <see cref="RushlingParts.Body"/> meshes on the map's character material at spawn points (no limbs, no
/// face atlas): the positions are spawns, not a simulation. Lids are <see cref="LidGridView"/> snapshots.
/// <c>-executeMethod PaletteShots.Run [-fallrushPaletteTag after] [-fallrushShotDir dir]</c>.
/// </summary>
public static class PaletteShots
{
    private struct MapShot
    {
        public string Key, Name, Scene;
        public int SpawnIndex;
        public bool FaceCentre;            // spawn view faces the map centre (round maps) instead of +z
        public int ActionT;
        public Vector3 ActionEye, ActionAt;
        public Vector3 OverCentre;
        public float OverSize;
        public (string label, Vector3 p)[] Samples; // p = (x, raycast start y or 0 = from above, z)
    }

    private static MapShot[] Maps => new[]
    {
        new MapShot
        {
            Key = "BlockPartyDash", Name = "capsule-run", Scene = BlockPartyDashBuilder.ScenePath, SpawnIndex = -1, ActionT = 745,
            ActionEye = new Vector3(-12f, 15f, 118f), ActionAt = new Vector3(0f, 5f, 135.5f), OverCentre = new Vector3(0f, 0f, 114f), OverSize = 22f,
            Samples = new[]
            {
                ("start funnel", new Vector3(-8.5f, 0f, 7f)), ("Gumball Slope", new Vector3(-3.2f, 0f, 30f)), ("Wave Gates", new Vector3(-4f, 0f, 61f)), ("Hop Field (block)", new Vector3(-4.6f, 0f, 75f)),
                ("Hop Field (CP2 deck)", new Vector3(-5f, 0f, 123.5f)), ("Sweep Pad", new Vector3(-6f, 0f, 131f)), ("Mallet Run", new Vector3(2f, 0f, 157f)), ("Belt Climb (ledge)", new Vector3(-6.6f, 0f, 192f)),
                ("finish deck", new Vector3(-5f, 0f, 228f)),
            },
        },
        new MapShot
        {
            Key = "GooTower", Name = "goo-tower", Scene = GooTowerBuilder.ScenePath, SpawnIndex = 5, ActionT = 300,
            ActionEye = new Vector3(30f, 24f, -30f), ActionAt = new Vector3(0f, 8f, 0f), OverCentre = Vector3.zero, OverSize = 22f,
            Samples = new[]
            {
                ("spawn deck", new Vector3(13.5f, 0f, -24.3f)), ("ground ring", new Vector3(-15f, 0f, 0f)),
                ("k0 (east, lap 1)", new Vector3(10.7f, 4.5f, -5f)), ("k1 (north, lap 1)", new Vector3(5f, 7.5f, 10.7f)),
                ("k2 (west, lap 1)", new Vector3(-10.7f, 10f, 5f)), ("k3 (south, lap 1)", new Vector3(-5f, 12.5f, -10.7f)),
                ("k4 (east, lap 2)", new Vector3(10.7f, 0f, -5f)), ("k5 (north, lap 2)", new Vector3(5f, 0f, 10.7f)),
                ("k6 (west, lap 2)", new Vector3(-10.7f, 0f, 5f)), ("k7 (south, lap 2)", new Vector3(-5f, 0f, -10.7f)),
            },
        },
        new MapShot
        {
            Key = "StarScramble", Name = "star-fountain", Scene = StarScrambleBuilder.ScenePath, SpawnIndex = 5, FaceCentre = true, ActionT = 200,
            ActionEye = new Vector3(10f, 12f, -12f), ActionAt = Vector3.zero, OverCentre = Vector3.zero, OverSize = 26f,
            Samples = new[]
            {
                ("static centre (inner floor)", new Vector3(0f, 0f, -9.8f)), ("turntable", new Vector3(2.5f, 0f, 4.33f)),
                ("outer ring", new Vector3(-9f, 0f, -13f)), ("outer ring 2", new Vector3(10.4f, 0f, 6f)),
                ("island 0", new Vector3(19.05f, 0f, 11f)), ("island 1", new Vector3(0f, 0f, -22f)),
            },
        },
        new MapShot
        {
            Key = "CrumbleShelf", Name = "lid-flip", Scene = CrumbleShelfBuilder.ScenePath, SpawnIndex = 0, FaceCentre = true, ActionT = -1,
            ActionEye = new Vector3(0f, 15f, -17f), ActionAt = new Vector3(0f, 5f, 0f), OverCentre = Vector3.zero, OverSize = 12f,
            Samples = new[]
            {
                ("top ring 4", new Vector3(7.44f, 0f, -3.72f)), ("top ring 3", new Vector3(-5.58f, 0f, 3.72f)), ("top ring 2", new Vector3(-3.72f, 0f, -1.86f)),
                ("top ring 1", new Vector3(1.86f, 0f, -1.86f)), ("top ring 0", new Vector3(0.6f, 0f, -0.6f)),
            },
        },
        new MapShot
        {
            Key = "Lobby", Name = "lobby", Scene = LobbyBuilder.ScenePath, SpawnIndex = 0, FaceCentre = true, ActionT = 120,
            ActionEye = new Vector3(0f, 12f, -26f), ActionAt = Vector3.zero, OverCentre = Vector3.zero, OverSize = 16f,
            Samples = new[] { ("deck", new Vector3(-10f, 0f, -2f)), ("tile A", new Vector3(0f, 0f, -8f)), ("tile B", new Vector3(8f, 0f, 0f)), ("tile C", new Vector3(-8f, 0f, 0f)) },
        },
    };

    /// <summary>G4.8 iteration: rebuild every map (render-only art pass) and shoot (one Unity start, graphics on).</summary>
    public static void RebuildAndShoot()
    {
        AllMapsBuilder.Run();
        Run();
    }

    [MenuItem("Fall Rush/Proof/Palette shots")]
    public static void Run()
    {
        var dir = CapsuleRunShots.Arg("-fallrushShotDir") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "../../docs/proof/g4"));
        var tag = CapsuleRunShots.Arg("-fallrushPaletteTag") ?? "after";
        s_Prefix = CapsuleRunShots.Arg("-fallrushShotPrefix") ?? "palette";
        // G4.8: -fallrushLooks soft,candy shoots each look in turn (tag = the look's name).
        var looks = CapsuleRunShots.Arg("-fallrushLooks");
        if (looks != null)
        {
            Directory.CreateDirectory(dir);
            foreach (var l in looks.Split(','))
            {
                LookState.Set(LookState.Parse(l) ?? LookId.Soft);
                var rep = new StringBuilder($"# G4.8 look shots ({l})\n\n");
                foreach (var m in Maps)
                    if (CapsuleRunShots.Arg("-fallrushPaletteMap") == null || CapsuleRunShots.Arg("-fallrushPaletteMap") == m.Name)
                        rep.Append(ShootMap(m, dir, l));
                if (l == "soft")
                    rep.Append(Colourblind(dir, s_Prefix, l));
                File.WriteAllText(Path.Combine(dir, $"{s_Prefix}-report-{l}.md"), rep.ToString());
                Debug.Log($"[shots] look {l} → {dir}\n{rep}");
            }

            LookState.Set(LookId.Soft);
            return;
        }

        var only = CapsuleRunShots.Arg("-fallrushPaletteMap");
        Directory.CreateDirectory(dir);
        var report = new StringBuilder($"# G4.6 palette shots ({tag})\n\n");
        foreach (var m in Maps)
        {
            if (only != null && only != m.Name)
                continue;
            report.Append(ShootMap(m, dir, tag));
        }

        if (tag == "after" && only == null)
        {
            report.Append(Colourblind(dir));
            PaletteSheet(Path.Combine(dir, "palette-sheet.png"));
            report.Append("\n- palette-sheet.png written\n");
        }

        File.WriteAllText(Path.Combine(dir, $"palette-report-{tag}.md"), report.ToString());
        Debug.Log($"[shots] palette {tag} → {dir}\n{report}");
    }

    // ------------------------------------------------------------------ one map

    /// <summary>G4.8: file name prefix of the shots (palette = G4.6, soft = the G4.8 look comparison).</summary>
    private static string s_Prefix = "palette";

    private static string ShootMap(MapShot ms, string dir, string tag)
    {
        var scene = EditorSceneManager.OpenScene(ms.Scene, OpenSceneMode.Single);
        MapInfo map = null;
        foreach (var root in scene.GetRootGameObjects())
            map ??= root.GetComponentInChildren<MapInfo>();
        if (map == null)
            throw new IOException("no MapInfo in " + ms.Scene);
        Physics.simulationMode = SimulationMode.Script;
        Physics.SyncTransforms();
        WorldLook.OnMapLoaded();
        var sb = new StringBuilder($"## {ms.Name}\n\n");

        var hz = map.BuildHazards(new RoundParams { MapId = map.MapId, Variant = 0, HazardSeed = 20261120 }, out _);
        var view = new HazardViewDriver(map) { DrawInstanced = false };
        CapsuleRunShots._map = map;
        var lidShots = new GameObject("ShotLids").transform;
        var actors = new GameObject("ShotRushlings").transform;

        var go = new GameObject("ShotCamera");
        var cam = go.AddComponent<Camera>();
        var main = Camera.main;
        cam.clearFlags = RenderSettings.skybox != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
        cam.backgroundColor = main != null ? main.backgroundColor : new Color(0.75f, 0.9f, 1f);
        cam.farClipPlane = 600f;
        cam.fieldOfView = FollowCamera.BaseFov;
        cam.allowHDR = true;
        GraphicsQuality.PrepareShotCamera(cam);
        if (main != null)
            main.enabled = false;
        GraphicsQuality.ConfigurePipelineForShots(true, out var restore);
        var volume = GraphicsQuality.CreateShotVolume(true);
        GraphicsQuality.ApplyShadows(LookState.Current, true); // G4.8: the soft look's sun shadows (none in candy)
        sb.Append($"Pipeline: {GraphicsQuality.Describe()}, quality level {QualitySettings.GetQualityLevel()} ({QualitySettings.names[QualitySettings.GetQualityLevel()]})\n\n");

        void Pose(int t)
        {
            CapsuleRunShots.Pose(view, hz, t);
            for (var i = lidShots.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(lidShots.GetChild(i).gameObject);
            if (view.LidView != null)
                view.LidView.Snapshot(lidShots);
        }

        // Spawn framing.
        var feet = ms.SpawnIndex >= 0 ? map.spawns[ms.SpawnIndex].position : new Vector3(0f, 0f, 4f);
        var fwd = ms.FaceCentre ? (-new Vector3(feet.x, 0f, feet.z)).normalized : Vector3.forward;
        var target = feet + Vector3.up * 0.9f;
        var eye = target - fwd * 6.1f + Vector3.up * 2.5f;
        var look = ms.FaceCentre ? target + fwd * 3f : target;

        // Rushlings: the focus one at the framing spawn + up to 4 neighbours.
        var bodyMat = map.playerMaterial;
        var placed = 0;
        foreach (var sp in map.spawns)
        {
            if (placed >= 5)
                break;
            if (sp == null || (sp.position - feet).sqrMagnitude > 40f)
                continue;
            var r = new GameObject("Rushling");
            r.transform.SetParent(actors, false);
            r.transform.SetPositionAndRotation(sp.position, Quaternion.LookRotation(ms.FaceCentre ? (-new Vector3(sp.position.x, 0f, sp.position.z)).normalized : Vector3.forward));
            r.AddComponent<MeshFilter>().sharedMesh = RushlingParts.Body(placed == 0 ? 0 : placed * 2 + 1);
            r.AddComponent<MeshRenderer>().sharedMaterial = bodyMat;
            placed++;
        }

        Pose(0);
        CapsuleRunShots.Shot(cam, dir, $"{s_Prefix}-{ms.Name}-spawn-{tag}", eye, look, 320, 180); // warm-up: the first batch frame
        CapsuleRunShots.Shot(cam, dir, $"{s_Prefix}-{ms.Name}-spawn-{tag}", eye, look, 1280, 720);
        actors.gameObject.SetActive(false);
        lidShots.gameObject.SetActive(false);
        sb.Append(CapsuleRunShots.CountVisible(cam, $"{ms.Name} spawn view ({tag}; Rushlings and lid snapshots hidden)"));
        if (view.LidView != null)
            sb.Append($"- instanced lid batches at t 0: {view.LidView.Batches}\n");
        lidShots.gameObject.SetActive(true);
        actors.gameObject.SetActive(true);

        // Action shot.
        var at = ms.ActionT;
        if (at < 0 && hz.LidGrids.Length > 0)
            at = hz.LidGrids[0].FirstWaveTicks + 18; // the first wave lit yellow
        Pose(at);
        actors.gameObject.SetActive(ms.Key == "Lobby");
        CapsuleRunShots.Shot(cam, dir, $"{s_Prefix}-{ms.Name}-action-{tag}", ms.ActionEye, ms.ActionAt, 1280, 720);
        sb.Append($"- action shot: t {at / 30f:F2} s\n");

        if (hz.LidGrids.Length > 0)
        {
            var rt = hz.LidGrids[0].RingRetireTicks[0] - 8; // ring 4 pulsing magenta just before it retires
            Pose(rt);
            CapsuleRunShots.Shot(cam, dir, $"{s_Prefix}-{ms.Name}-retire-{tag}", ms.ActionEye, ms.ActionAt, 1280, 720);
            sb.Append($"- retire shot: t {rt / 30f:F2} s\n");
            Pose(at);
            cam.transform.SetPositionAndRotation(ms.ActionEye, Quaternion.LookRotation(ms.ActionAt - ms.ActionEye));
        }

        // Lid Flip: lit-vs-ring pixels in the action shot (the lids' states are known from the view).
        if (view.LidView != null && (tag == "after" || tag == "soft" || tag == "candy"))
        {
            // Measured inside the claw machine (the action framing looks through the glass panes): the spawn-height view.
            var me = new Vector3(0f, 9.2f, -7.5f);
            CapsuleRunShots.Shot(cam, dir, $"{s_Prefix}-{ms.Name}-lit-measure-{tag}", me, new Vector3(0f, 6f, 1.5f), 1280, 720);
            sb.Append(LidPixels(cam, view.LidView, Path.Combine(dir, $"{s_Prefix}-{ms.Name}-lit-measure-{tag}.png")));
        }

        // Overhead + measured section tops (t 0).
        Pose(0);
        actors.gameObject.SetActive(false);
        var top = 0f;
        foreach (var rr in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            if (rr.enabled && rr.transform.root.name != "Dressing")
                top = Mathf.Max(top, Mathf.Min(rr.bounds.max.y, 60f));
        cam.orthographic = true;
        cam.orthographicSize = ms.OverSize;
        var up = ms.Key == "BlockPartyDash" ? Vector3.left : Vector3.forward;
        cam.transform.SetPositionAndRotation(new Vector3(ms.OverCentre.x, top + 25f, ms.OverCentre.z), Quaternion.LookRotation(Vector3.down, up));
        var w = ms.Key == "BlockPartyDash" ? 2640 : 1200;
        var h = ms.Key == "BlockPartyDash" ? 500 : 1200;
        var tex = RenderTex(cam, w, h);
        File.WriteAllBytes(Path.Combine(dir, $"{s_Prefix}-{ms.Name}-overhead-{tag}.png"), tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        cam.orthographic = false;
        // Each sample: the walkable top under (x, z) found by a raycast down from y0 (0 = from above everything), seen
        // from 2.5 m at ~60° down (FOV 20, nothing in between); the median of the centre patch = the top after light + grade.
        sb.Append("\n| section | sample (x, y0, z) | top hit | pixel RGB | H° | S | V |\n|---|---|---|---|---|---|---|\n");
        foreach (var (label, sp) in ms.Samples)
        {
            var hitName = "(no collider: lid view)";
            var p = new Vector3(sp.x, ms.Key == "CrumbleShelf" ? CrumbleShelfBuilder.TopY : 0f, sp.z);
            var y0 = sp.y > 0f ? sp.y : top + 20f;
            if (Physics.Raycast(new Vector3(sp.x, y0, sp.z), Vector3.down, out var hit, y0 + 40f, ~0, QueryTriggerInteraction.Ignore))
            {
                p = hit.point;
                hitName = $"{hit.collider.name} y {hit.point.y:F2} n.y {hit.normal.y:F2}";
            }

            cam.fieldOfView = 20f;
            var eyeS = p + new Vector3(0f, 2.2f, -1.3f); // ~60° down from the south: off the sun's specular angle (gloss on the lids)
            cam.transform.SetPositionAndRotation(eyeS, Quaternion.LookRotation(p - eyeS));
            var pt = RenderTex(cam, 64, 64);
            var c = Patch(pt, new Vector3(32f, 32f, 1f), 4);
            Object.DestroyImmediate(pt);
            Color.RGBToHSV(c, out var hh, out var s, out var v);
            sb.Append($"| {label} | ({sp.x:F1}, {sp.y:F1}, {sp.z:F1}) | {hitName} | {ColorUtility.ToHtmlStringRGB(c)} | {hh * 360f:F0} | {s:F2} | {v:F2} |\n");
        }

        cam.fieldOfView = FollowCamera.BaseFov;
        sb.Append('\n');
        restore();
        GraphicsQuality.DestroyShotVolume(volume);
        Object.DestroyImmediate(actors.gameObject);
        Object.DestroyImmediate(lidShots.gameObject);
        Object.DestroyImmediate(go);
        return sb.ToString();
    }

    /// <summary>World point → pixel in a w × h render of <paramref name="cam"/> (viewport based: the camera has no target now).</summary>
    private static Vector3 Vp(Camera cam, Vector3 p, int w, int h)
    {
        cam.aspect = (float)w / h;
        var v = cam.WorldToViewportPoint(p);
        return new Vector3(v.x * w, v.y * h, v.z);
    }

    private static Texture2D RenderTex(Camera cam, int w, int h)
    {
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
        cam.targetTexture = rt;
        cam.aspect = (float)w / h;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        cam.targetTexture = null;
        rt.Release();
        Object.DestroyImmediate(rt);
        return tex;
    }

    /// <summary>Per-channel median of the (2r+1)² patch around a screen point (texture rows are bottom-up, as the screen).</summary>
    private static Color Patch(Texture2D tex, Vector3 sp, int r)
    {
        var rs = new List<float>();
        var gs = new List<float>();
        var bs = new List<float>();
        for (var dy = -r; dy <= r; dy++)
            for (var dx = -r; dx <= r; dx++)
            {
                var x = Mathf.Clamp(Mathf.RoundToInt(sp.x) + dx, 0, tex.width - 1);
                var y = Mathf.Clamp(Mathf.RoundToInt(sp.y) + dy, 0, tex.height - 1);
                var c = tex.GetPixel(x, y);
                rs.Add(c.r);
                gs.Add(c.g);
                bs.Add(c.b);
            }

        rs.Sort();
        gs.Sort();
        bs.Sort();
        var m = rs.Count / 2;
        return new Color(rs[m], gs[m], bs[m]);
    }

    // ------------------------------------------------------------------ colourblind

    // Machado, Oliveira & Fernandes 2009, severity 1.0 (linear RGB).
    private static readonly float[] Deutan = { 0.367322f, 0.860646f, -0.227968f, 0.280085f, 0.672501f, 0.047413f, -0.011820f, 0.042940f, 0.968881f };
    private static readonly float[] Protan = { 0.152286f, 1.052583f, -0.204868f, 0.114503f, 0.786281f, 0.099216f, -0.003882f, -0.048116f, 1.051998f };

    public static Color Simulate(Color srgb, float[] m)
    {
        var l = srgb.linear;
        var o = new Color(
            Mathf.Clamp01(m[0] * l.r + m[1] * l.g + m[2] * l.b),
            Mathf.Clamp01(m[3] * l.r + m[4] * l.g + m[5] * l.b),
            Mathf.Clamp01(m[6] * l.r + m[7] * l.g + m[8] * l.b));
        return o.gamma;
    }

    /// <summary>Luma (Rec. 709 weights on the sRGB-encoded values: the convention of the stripe contrast test).</summary>
    public static float Luma(Color c) => c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f;

    private static string Colourblind(string dir) => Colourblind(dir, "palette", "after");

    private static string Colourblind(string dir, string prefix, string tag)
    {
        var sb = new StringBuilder("\n## Colourblind sheets (Machado 2009, severity 1)\n\n");
        foreach (var m in Maps)
        {
            if (m.Key == "Lobby")
                continue;
            foreach (var kind in new[] { "spawn", "action" })
            {
                var src = Path.Combine(dir, $"{prefix}-{m.Name}-{kind}-{tag}.png");
                if (!File.Exists(src))
                    continue;
                var t = new Texture2D(2, 2, TextureFormat.RGB24, false);
                t.LoadImage(File.ReadAllBytes(src));
                const int sw = 640, sh = 360;
                var sheet = new Texture2D(sw * 3, sh, TextureFormat.RGB24, false);
                for (var y = 0; y < sh; y++)
                    for (var x = 0; x < sw; x++)
                    {
                        var c = t.GetPixelBilinear((x + 0.5f) / sw, (y + 0.5f) / sh);
                        sheet.SetPixel(x, y, c);
                        sheet.SetPixel(sw + x, y, Simulate(c, Deutan));
                        sheet.SetPixel(2 * sw + x, y, Simulate(c, Protan));
                    }

                sheet.Apply();
                var outName = $"{prefix}-{m.Name}-{kind}-colourblind.png";
                File.WriteAllBytes(Path.Combine(dir, outName), sheet.EncodeToPNG());
                sb.Append($"- {outName}: normal | deuteranopia | protanopia\n");
                Object.DestroyImmediate(t);
                Object.DestroyImmediate(sheet);
            }
        }

        // Token-level checks (the same numbers PaletteTests asserts).
        var p = PaletteTokens.Get();
        sb.Append("\n| pair | normal Δluma | deutan Δluma | protan Δluma |\n|---|---|---|---|\n");
        void Pair(string name, Color a, Color b)
        {
            sb.Append($"| {name} | {Mathf.Abs(Luma(a) - Luma(b)):F3} | {Mathf.Abs(Luma(Simulate(a, Deutan)) - Luma(Simulate(b, Deutan))):F3} | {Mathf.Abs(Luma(Simulate(a, Protan)) - Luma(Simulate(b, Protan))):F3} |\n");
        }

        Pair("stripe yellow vs hit pink", p.hazardYellow, p.hazardMagenta);
        var look = LookState.Current;
        var lit = LidGridView.LitFor(look);
        for (var ring = 0; ring <= 4; ring++)
            Pair($"lid lit vs top ring {ring} (tokens, {LookState.Name(look)})", lit, LidGridView.TopRingColour(ring, look));
        Pair($"lid lit vs lower shelf (tokens, {LookState.Name(look)})", lit, LidGridView.LowCalmFor(look));
        Pair($"lid lit vs lower edge (tokens, {LookState.Name(look)})", lit, LidGridView.LowEdgeFor(look));
        return sb.ToString();
    }

    /// <summary>
    /// Lid Flip: per lid state batch in the shot, the median top pixel and the darkest lid (a calm lid in a cast shadow), then
    /// Δluma lit vs each calm batch (normal / deutan / protan; the 10th-percentile lid stands for a lid in a cast shadow). G4.8z: each lid is sampled at two points 0.4 m either side of
    /// its centre (the 3 × 3 centre patch sat on the thin dark capsule seam that crosses every lid, which made calm and lit
    /// read closer than the tops are); the old centre number stays as a column. Rule (G4.8z): every calm batch, median and
    /// 10th-percentile lid, ≥ 0.12 deutan and protan from the lit median.
    /// </summary>
    private static string LidPixels(Camera cam, LidGridView lv, string png)
    {
        var t = new Texture2D(2, 2, TextureFormat.RGB24, false);
        t.LoadImage(File.ReadAllBytes(png));
        cam.transform.SetPositionAndRotation(cam.transform.position, cam.transform.rotation);
        cam.aspect = (float)t.width / t.height;
        var byBatch = new Dictionary<int, List<Color>>();
        var onSeam = new Dictionary<int, List<Color>>();
        for (var i = 0; i < lv.LastBatch.Count; i++)
        {
            var b = lv.LastBatch[i];
            if (b < 0)
                continue;
            var c = lv.LastCenter[i] + Vector3.up * 0.2f; // the lid top
            var sp = Vp(cam, c, t.width, t.height);
            if (sp.z <= 0f || sp.x < 3 || sp.y < 3 || sp.x > t.width - 4 || sp.y > t.height - 4)
                continue;
            if (!onSeam.TryGetValue(b, out var seam))
                onSeam[b] = seam = new List<Color>();
            seam.Add(Patch(t, sp, 1));
            var l = Vp(cam, c + Vector3.left * 0.4f, t.width, t.height);
            var r = Vp(cam, c + Vector3.right * 0.4f, t.width, t.height);
            if (!byBatch.TryGetValue(b, out var list))
                byBatch[b] = list = new List<Color>();
            var pl = Patch(t, l, 1);
            var pr = Patch(t, r, 1);
            // The darker side of the lid (a shadow edge counts against us). A calm lid is pale and low in saturation: a
            // saturated sample on a calm lid is a hazard in front of it (the sweeper's pink / lemon stripes), not the lid.
            var pick = Luma(pl) < Luma(pr) ? pl : pr;
            Color.RGBToHSV(pick, out _, out var sat, out _);
            if (b != LidGridView.LitBatch && sat > 0.45f)
            {
                Color.RGBToHSV(Luma(pl) < Luma(pr) ? pr : pl, out _, out var sat2, out _);
                if (sat2 > 0.45f)
                    continue;
                pick = Luma(pl) < Luma(pr) ? pr : pl;
            }

            list.Add(pick);
        }

        Object.DestroyImmediate(t);
        var sb = new StringBuilder("\n| Lid Flip shot pixels: batch | lids | median top RGB | Δluma vs lit, median (normal / deutan / protan) | 10th-percentile lid RGB | Δluma vs lit, 10th pct (n / d / p) | darkest sample: RGB, Δluma (n / d / p) | old centre-on-seam Δluma (n / d / p) |\n|---|---|---|---|---|---|---|---|\n");
        if (!byBatch.TryGetValue(LidGridView.LitBatch, out var lit) || lit.Count == 0)
            return sb.Append("| (no lit lid in view) | | | | | | | |\n").ToString();
        var litC = Median(lit);
        var litSeam = Median(onSeam[LidGridView.LitBatch]);
        var worst = 9f;
        foreach (var kv in byBatch)
        {
            var c = Median(kv.Value);
            // The 10th-percentile lid by luma (a lid in a cast shadow); the single darkest sample is listed too (it can sit
            // on a seam or a thin cable shadow line that crosses a lid, which no one reads as the lid's colour).
            var byLuma = new List<Color>(kv.Value);
            byLuma.Sort((p, q) => Luma(p).CompareTo(Luma(q)));
            var dark = byLuma[Mathf.FloorToInt(0.1f * (byLuma.Count - 1))];
            var darkest = byLuma[0];
            var (dn, dd, dp) = Gaps(c, litC);
            var (kn, kd, kp) = Gaps(dark, litC);
            var (sn, sd, sp2) = Gaps(Median(onSeam[kv.Key]), litSeam);
            if (kv.Key != LidGridView.LitBatch)
                worst = Mathf.Min(worst, Mathf.Min(Mathf.Min(dd, dp), Mathf.Min(kd, kp)));
            var (xn, xd, xp) = Gaps(darkest, litC);
            sb.Append($"| {LidGridView.BatchName(kv.Key)} | {kv.Value.Count} | {ColorUtility.ToHtmlStringRGB(c)} | {dn:F3} / {dd:F3} / {dp:F3} | {ColorUtility.ToHtmlStringRGB(dark)} | {kn:F3} / {kd:F3} / {kp:F3} | {ColorUtility.ToHtmlStringRGB(darkest)} {xn:F3} / {xd:F3} / {xp:F3} | {sn:F3} / {sd:F3} / {sp2:F3} |\n");
        }

        sb.Append($"\n- worst calm-vs-lit deutan / protan gap (median and 10th-percentile lid): **{worst:F3}** → {(worst >= 0.12f ? "PASS" : "FAIL")} (rule ≥ 0.12)\n");
        return sb.ToString();
    }

    private static (float n, float d, float p) Gaps(Color c, Color lit) => (
        Mathf.Abs(Luma(c) - Luma(lit)),
        Mathf.Abs(Luma(Simulate(c, Deutan)) - Luma(Simulate(lit, Deutan))),
        Mathf.Abs(Luma(Simulate(c, Protan)) - Luma(Simulate(lit, Protan))));

    private static Color Median(List<Color> l)
    {
        var r = new List<float>();
        var g = new List<float>();
        var b = new List<float>();
        foreach (var c in l)
        {
            r.Add(c.r);
            g.Add(c.g);
            b.Add(c.b);
        }

        r.Sort();
        g.Sort();
        b.Sort();
        var m = r.Count / 2;
        return new Color(r[m], g[m], b[m]);
    }

    // ------------------------------------------------------------------ swatch sheet

    private static void PaletteSheet(string path)
    {
        var p = PaletteTokens.Get();
        const int cell = 120, gap = 12;
        var rows = new List<(Color a, Color b)>();
        for (var i = 0; i < p.hueTops.Length; i++)
            rows.Add((p.hueTops[i], p.hueSides[i]));
        var meaning = new[] { p.hazardMagenta, p.hazardYellow, p.mint, p.goo, p.skyTop, p.skyHorizon, p.ink };
        var w = gap + (cell + gap) * Mathf.Max(rows.Count, meaning.Length);
        var h = gap + (cell + gap) * 3;
        var t = new Texture2D(w, h, TextureFormat.RGB24, false);
        var bg = new Color(0.97f, 0.97f, 0.99f);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                t.SetPixel(x, y, bg);
        void Rect(int x0, int y0, int ww, int hh, Color c, bool stripe = false, Color sc = default)
        {
            for (var y = y0; y < y0 + hh; y++)
                for (var x = x0; x < x0 + ww; x++)
                    t.SetPixel(x, h - 1 - y, stripe && ((x + y) / 14) % 2 == 0 ? sc : c);
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var x = gap + i * (cell + gap);
            Rect(x, gap, cell, cell, rows[i].a);                              // row 1: tops
            Rect(x, gap + cell + gap, cell, cell, rows[i].b);                 // row 2: sides
        }

        for (var i = 0; i < meaning.Length; i++)
        {
            var x = gap + i * (cell + gap);
            var y = gap + 2 * (cell + gap);
            if (i == 0)
                Rect(x, y, cell, cell, p.hazardYellow, true, p.hazardMagenta); // hit: the stripe
            else if (i == 4)
                for (var yy = 0; yy < cell; yy++)
                    Rect(x, y + yy, cell, 1, Color.Lerp(p.skyTop, p.skyHorizon, yy / (float)cell));
            else
                Rect(x, y, cell, cell, meaning[i]);
        }

        t.Apply();
        File.WriteAllBytes(path, t.EncodeToPNG());
        Object.DestroyImmediate(t);
    }

    // ------------------------------------------------------------------ Low-level post cost (Editor proxy)

    /// <summary>
    /// Editor GPU-time proxy for the Low-graphics choice: the Capsule Run spawn view at 1152 × 720 (0.8 × 1440 × 900) rendered
    /// 300 times each with no post, Low (grade only) and High (grade + bloom + vignette), each frame synced by a 1-pixel read.
    /// Metal on the M2 in the Editor, not WebGL: a relative number. <c>-executeMethod PaletteShots.Cost</c>.
    /// </summary>
    public static void Cost()
    {
        var dir = CapsuleRunShots.Arg("-fallrushShotDir") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "../../docs/proof/g4"));
        EditorSceneManager.OpenScene(BlockPartyDashBuilder.ScenePath, OpenSceneMode.Single);
        WorldLook.OnMapLoaded();
        GraphicsQuality.ConfigurePipelineForShots(true, out var restore);
        var go = new GameObject("CostCamera");
        var cam = go.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.Skybox;
        cam.fieldOfView = FollowCamera.BaseFov;
        cam.farClipPlane = 600f;
        if (Camera.main != null && Camera.main != cam)
            Camera.main.enabled = false;
        cam.transform.SetPositionAndRotation(new Vector3(0f, 3.4f, -2.1f), Quaternion.LookRotation(new Vector3(0f, -0.35f, 1f)));
        GraphicsQuality.PrepareShotCamera(cam);
        var rt = new RenderTexture(1152, 720, 24, RenderTextureFormat.ARGB32);
        cam.targetTexture = rt;
        var px = new Texture2D(1, 1, TextureFormat.RGB24, false);
        var sb = new StringBuilder("# G4.6 post cost, Editor proxy (Metal, M2, 1152 × 720, Capsule Run spawn view, 300 frames each)\n\n| mode | ms / frame (median of 3 runs) |\n|---|---|\n");
        double Time(bool post, GameObject vol)
        {
            GraphicsQuality.SetPostOnCamera(cam, post);
            var runs = new List<double>();
            for (var r = 0; r < 3; r++)
            {
                for (var w = 0; w < 20; w++)
                    cam.Render();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (var i = 0; i < 300; i++)
                {
                    cam.Render();
                    RenderTexture.active = rt;
                    px.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
                    RenderTexture.active = null;
                }

                runs.Add(sw.Elapsed.TotalMilliseconds / 300.0);
            }

            runs.Sort();
            return runs[1];
        }

        var none = Time(false, null);
        var low = GraphicsQuality.CreateShotVolume(false);
        var tLow = Time(true, low);
        GraphicsQuality.DestroyShotVolume(low);
        var high = GraphicsQuality.CreateShotVolume(true);
        var tHigh = Time(true, high);
        GraphicsQuality.DestroyShotVolume(high);
        sb.Append($"| no post (the G2–G4.5 Low) | {none:F3} |\n| Low (G4.6: tonemap + colour adjustments) | {tLow:F3} |\n| High (+ bloom + vignette) | {tHigh:F3} |\n");
        sb.Append($"\nLow − no post: {tLow - none:F3} ms; High − no post: {tHigh - none:F3} ms. Includes the 1-pixel sync read each frame (same in every mode).\n");
        cam.targetTexture = null;
        rt.Release();
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(px);
        Object.DestroyImmediate(go);
        restore();
        File.WriteAllText(Path.Combine(dir, "palette-post-cost-editor.md"), sb.ToString());
        Debug.Log("[shots] " + sb);
    }

    /// <summary>
    /// G4.8 Editor proxy of the soft look's cost (Metal on the M2, 1152 × 720, the Capsule Run spawn and action views, High
    /// volume, 300 frames × 3 runs): candy (no shadows), soft without shadows, soft with shadows at 1024 and 2048. Writes
    /// <c>soft-shadow-cost-editor.md</c>. A proxy only: the WebGL numbers come from the release build.
    /// </summary>
    public static void ShadowCost()
    {
        var dir = CapsuleRunShots.Arg("-fallrushShotDir") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "../../docs/proof/g4"));
        EditorSceneManager.OpenScene(BlockPartyDashBuilder.ScenePath, OpenSceneMode.Single);
        GraphicsQuality.ConfigurePipelineForShots(true, out var restore);
        var res0 = GraphicsQuality.ShadowMapResolution;
        var go = new GameObject("CostCamera");
        var cam = go.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.Skybox;
        cam.fieldOfView = FollowCamera.BaseFov;
        cam.farClipPlane = 600f;
        if (Camera.main != null && Camera.main != cam)
            Camera.main.enabled = false;
        GraphicsQuality.PrepareShotCamera(cam);
        var rt = new RenderTexture(1152, 720, 24, RenderTextureFormat.ARGB32);
        cam.targetTexture = rt;
        var px = new Texture2D(1, 1, TextureFormat.RGB24, false);
        var vol = GraphicsQuality.CreateShotVolume(true);
        var sb = new StringBuilder("# G4.8 soft look cost, Editor proxy (Metal, M2, 1152 × 720, High volume, 300 frames × 3 runs, median)\n\n" +
                                   "| view | candy (G4.6, no shadows) | soft, shadows off | soft, shadows 1024 | soft, shadows 2048 |\n|---|---|---|---|---|\n");
        double Time()
        {
            var runs = new List<double>();
            for (var r = 0; r < 3; r++)
            {
                for (var w = 0; w < 20; w++)
                    cam.Render();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (var i = 0; i < 300; i++)
                {
                    cam.Render();
                    RenderTexture.active = rt;
                    px.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
                    RenderTexture.active = null;
                }

                runs.Add(sw.Elapsed.TotalMilliseconds / 300.0);
            }

            runs.Sort();
            return runs[1];
        }

        var sun = RenderSettings.sun;
        foreach (var (name, eye, at) in new[] { ("spawn", new Vector3(0f, 3.4f, -2.1f), new Vector3(0f, 2.05f, 1.75f)), ("action", Maps[0].ActionEye, Maps[0].ActionAt) })
        {
            cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(at - eye));
            LookState.Set(LookId.Candy);
            var tc = Time();
            LookState.Set(LookId.Soft);
            if (sun != null)
                sun.shadows = LightShadows.None;
            var t0 = Time();
            if (sun != null)
                sun.shadows = LightShadows.Soft;
            GraphicsQuality.ShadowMapResolution = 1024;
            var t1 = Time();
            GraphicsQuality.ShadowMapResolution = 2048;
            var t2 = Time();
            sb.Append($"| Capsule Run {name} | {tc:F3} ms | {t0:F3} ms | {t1:F3} ms | {t2:F3} ms |\n");
        }

        sb.Append("\nIncludes the 1-pixel sync read each frame (same in every column).\n");
        GraphicsQuality.ShadowMapResolution = res0;
        GraphicsQuality.DestroyShotVolume(vol);
        cam.targetTexture = null;
        rt.Release();
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(px);
        Object.DestroyImmediate(go);
        restore();
        File.WriteAllText(Path.Combine(dir, "soft-shadow-cost-editor.md"), sb.ToString());
        Debug.Log("[shots] " + sb);
    }

    // ------------------------------------------------------------------ inventory (investigation)

    /// <summary>Lists every renderer under Geometry / Hazards of each map: name, material, mesh, bounds, collider.</summary>
    public static void Dump()
    {
        var outPath = CapsuleRunShots.Arg("-fallrushDumpOut") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "../../.m1-logs/palette-dump.txt"));
        var sb = new StringBuilder();
        foreach (var m in Maps)
        {
            var scene = EditorSceneManager.OpenScene(m.Scene, OpenSceneMode.Single);
            sb.Append($"=== {m.Key}\n");
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name != "Geometry" && root.name != "Hazards" && root.name != "MergedStatic")
                    continue;
                foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var mf = r.GetComponent<MeshFilter>();
                    var col = r.GetComponentInParent<Collider>();
                    sb.Append($"{root.name}\t{PathOf(r.transform)}\t{(r.sharedMaterial != null ? r.sharedMaterial.name : "-")}\t{(mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : "-")}\t{r.enabled}\tc{r.bounds.center:F1} s{r.bounds.size:F1}\t{(col != null ? col.GetType().Name : "-")}\n");
                }
            }
        }

        File.WriteAllText(outPath, sb.ToString());
        Debug.Log($"[shots] dump → {outPath}");
    }

    private static string PathOf(Transform t)
    {
        var s = t.name;
        while (t.parent != null)
        {
            t = t.parent;
            s = t.name + "/" + s;
        }

        return s;
    }
}
