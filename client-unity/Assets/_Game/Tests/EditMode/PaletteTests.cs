using System.Collections.Generic;
using System.Text;
using FallRush.Client;
using FallRush.Net;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>
    /// G4.6 candy palette on the committed scenes (doc 17 §3.1). "Walkable top" = an up-facing triangle (world n.y &gt; 0.9) of
    /// (1) any Geometry renderer mesh on an object with a solid collider (the merged chunks keep the originals' meshes; render
    /// state does not matter), except hazard stripe posts (striped on purpose), and (2) the walkable hazard visuals (moving
    /// platforms, turntables, falling blocks; belt bodies are colliders in (1); their chevrons, uv2.x &gt; 0, are the light
    /// "it moves" marks and are skipped). The Goo Tower core and dome (above the summit, out of reach) are skipped. Colours are
    /// the on-screen colour: (vertex colour × material colour) under the map light through the grade model
    /// (<see cref="GraphicsQuality.Grade"/>, checked against Editor shot pixels in the G4.6 proof).
    /// Rule (a): CIE76 ΔE ≥ 15 from hit pink, mint, goo and both sky colours (mint is allowed on goal / checkpoint / spawn /
    /// spring objects: <c>Finish*</c>, <c>Summit*</c>, <c>SpawnPad*</c>, <c>CPPost*</c>, <c>Arch*</c>, <c>SpringPad_*</c>). Rule (b): HSV S ≥ 0.4 (no near-white top).
    /// </summary>
    public sealed class PaletteTests
    {
        private static readonly (string scene, string key)[] Maps =
        {
            ("Map_BlockPartyDash", "BlockPartyDash"), ("Map_GooTower", "GooTower"), ("Map_StarScramble", "StarScramble"),
            ("Map_CrumbleShelf", "CrumbleShelf"), ("Map_Lobby", "Lobby"),
        };

        // ------------------------------------------------------------------ colour maths

        public static Vector3 Lab(Color c)
        {
            var l = c.linear;
            var x = 0.4124f * l.r + 0.3576f * l.g + 0.1805f * l.b;
            var y = 0.2126f * l.r + 0.7152f * l.g + 0.0722f * l.b;
            var z = 0.0193f * l.r + 0.1192f * l.g + 0.9505f * l.b;
            float F(float t) => t > 0.008856f ? Mathf.Pow(t, 1f / 3f) : 7.787f * t + 16f / 116f;
            float fx = F(x / 0.95047f), fy = F(y), fz = F(z / 1.08883f);
            return new Vector3(116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
        }

        public static float DeltaE(Color a, Color b) => Vector3.Distance(Lab(a), Lab(b));

        private static (string name, Color c)[] Meaning(PaletteTokens p) => new[]
        {
            ("hit pink", p.hazardMagenta), ("mint", p.mint), ("goo", p.goo), ("sky top", p.skyTop), ("sky horizon", p.skyHorizon),
        };

        /// <summary>Solid but never stood on: the Goo Tower machine core and its dome rise above the summit (goo-tinted on purpose).</summary>
        private static bool OutOfReach(string n) => n == "TowerCore" || n == "TowerDome";

        /// <summary>Goal / checkpoint / spawn / spring objects (mint is their meaning): finish deck, summit, spawn pads, checkpoint posts, the finish arch, springs.</summary>
        private static bool GoalObject(string n) => n.StartsWith("Finish") || n.StartsWith("Summit") || n.StartsWith("SpawnPad") || n.StartsWith("CPPost") ||
                                                   n.StartsWith("Arch") || n.StartsWith("SpringPad_");

        // ------------------------------------------------------------------ walkable tops of a scene

        private struct Top
        {
            public string Obj;
            public Color C;
        }

        private static List<Top> WalkableTops(string sceneName)
        {
            var scene = EditorSceneManager.OpenScene($"Assets/_Game/Scenes/{sceneName}.unity", OpenSceneMode.Single);
            MapInfo info = null;
            foreach (var root in scene.GetRootGameObjects())
                info ??= root.GetComponentInChildren<MapInfo>();
            var tops = new List<Top>();
            var pal = PaletteTokens.Get();
            var light = GraphicsQuality.TopLight(pal.sun, pal.ambientSky);
            var walkHazards = new HashSet<Transform>();
            if (info != null)
            {
                foreach (var pl in info.platforms)
                    if (pl != null && pl.visual != null)
                        walkHazards.Add(pl.visual);
                foreach (var tt in info.turnTables)
                    if (tt != null && tt.visual != null)
                        walkHazards.Add(tt.visual);
                foreach (var fb in info.fallingBlocks)
                    if (fb != null && fb.visual != null)
                        walkHazards.Add(fb.visual);
            }

            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name != "Geometry" && root.name != "Hazards")
                    continue;
                foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    var r = mf.GetComponent<MeshRenderer>();
                    if (mf.sharedMesh == null || r == null || r.sharedMaterial == null || r.sharedMaterial.name == "HazardStripes")
                        continue;
                    if (OutOfReach(mf.name))
                        continue;
                    var col = mf.GetComponent<Collider>();
                    var walk = walkHazards.Contains(mf.transform) || (root.name == "Geometry" || mf.name == "MotorCollider") && col != null && !col.isTrigger;
                    if (!walk)
                        continue;
                    var mesh = mf.sharedMesh;
                    var v = mesh.vertices;
                    var n = mesh.normals;
                    var c = mesh.colors;
                    var u2 = new List<Vector2>();
                    mesh.GetUVs(1, u2);
                    var baseC = (r.sharedMaterial.HasProperty("_BaseColor") ? r.sharedMaterial.GetColor("_BaseColor") : Color.white).linear;
                    var tri = mesh.triangles;
                    var nm = mf.transform.localToWorldMatrix.inverse.transpose;
                    var seen = new HashSet<string>();
                    for (var i = 0; i < tri.Length; i += 3)
                    {
                        var a = tri[i];
                        if (n.Length <= a || nm.MultiplyVector(n[a]).normalized.y < 0.9f)
                            continue;
                        if (u2.Count > a && u2[a].x > 0.5f)
                            continue; // belt chevrons
                        // On screen: (linear vertex colour × linearised material colour) × the map light on a top, then the grade.
                        var k = c.Length > a ? c[a] : Color.white;
                        k = GraphicsQuality.Grade(new Color(k.r * baseC.r * light.r, k.g * baseC.g * light.g, k.b * baseC.b * light.b));
                        var key = ColorUtility.ToHtmlStringRGB(k);
                        if (seen.Add(key))
                            tops.Add(new Top { Obj = mf.transform.parent != null && mf.name == "MotorCollider" ? mf.transform.parent.name : mf.name, C = k });
                    }
                }
            }

            return tops;
        }

        [Test]
        public void MeaningColours_NeverOnAWalkableTop_AndNoTopNearWhite()
        {
            var p = PaletteTokens.Get();
            var report = new StringBuilder();
            var fails = new List<string>();
            foreach (var (scene, _) in Maps)
            {
                var tops = WalkableTops(scene);
                var colours = new HashSet<string>();
                foreach (var t in tops)
                {
                    colours.Add(ColorUtility.ToHtmlStringRGB(t.C));
                    Color.RGBToHSV(t.C, out _, out var s, out _);
                    if (s < 0.4f)
                        fails.Add($"{scene} {t.Obj}: top {ColorUtility.ToHtmlStringRGB(t.C)} near-white (S {s:F2})");
                    foreach (var (name, m) in Meaning(p))
                    {
                        if (name == "mint" && GoalObject(t.Obj))
                            continue;
                        var de = DeltaE(t.C, m);
                        if (de < 15f)
                            fails.Add($"{scene} {t.Obj}: top {ColorUtility.ToHtmlStringRGB(t.C)} is {name} (ΔE {de:F1})");
                    }
                }

                report.Append($"{scene}: {tops.Count} distinct top colours on walkable objects ({string.Join(" ", colours)})\n");
            }

            // Lid Flip's lids are drawn by the client (no static tops): its solid colours by the same rules.
            foreach (var c in LidGridView.SolidColours)
            {
                Color.RGBToHSV(c, out _, out var s, out _);
                if (s < 0.4f)
                    fails.Add($"LidGridView solid {ColorUtility.ToHtmlStringRGB(c)} near-white");
                foreach (var (name, m) in Meaning(p))
                    if (DeltaE(c, m) < 15f)
                        fails.Add($"LidGridView solid {ColorUtility.ToHtmlStringRGB(c)} is {name}");
            }

            TestContext.WriteLine(report.ToString());
            Assert.That(fails, Is.Empty, string.Join("\n", fails));
        }

        [Test]
        public void LemonDrop_NeverOnLidFlip_NorWithAYellowTelegraph_AtMostOncePerMap()
        {
            var perMap = new Dictionary<string, int>();
            foreach (var s in PaletteTokens.Sections)
            {
                if (s.Hue != SurfaceHue.LemonDrop)
                    continue;
                Assert.That(s.Map, Is.Not.EqualTo("CrumbleShelf"), "Lemon drop on Lid Flip");
                Assert.That(s.YellowTelegraph, Is.False, $"{s.Map}/{s.Name}: Lemon drop beside a yellow telegraph");
                perMap[s.Map] = perMap.TryGetValue(s.Map, out var n) ? n + 1 : 1;
            }

            foreach (var kv in perMap)
                Assert.That(kv.Value, Is.LessThanOrEqualTo(1), $"{kv.Key}: Lemon drop on {kv.Value} sections");
            var lemon = PaletteTokens.DefaultTop(SurfaceHue.LemonDrop);
            foreach (var c in LidGridView.SolidColours)
                Assert.That(DeltaE(c, lemon), Is.GreaterThan(15f), "a Lid Flip solid lid is Lemon drop");
        }

        [Test]
        public void GrapeFizz_NeverOnGooTower()
        {
            foreach (var s in PaletteTokens.Sections)
                if (s.Map == "GooTower")
                    Assert.That(s.Hue, Is.Not.EqualTo(SurfaceHue.GrapeFizz), $"GooTower/{s.Name}");
            var fizz = PaletteTokens.DefaultTop(SurfaceHue.GrapeFizz);
            foreach (var t in WalkableTops("Map_GooTower"))
                Assert.That(DeltaE(t.C, fizz), Is.GreaterThan(10f), $"{t.Obj}: a Grape fizz top on Goo Tower");
        }

        [Test]
        public void LidFlip_SolidLids_NotMint_AndRingsAsTabled()
        {
            var p = PaletteTokens.Get();
            foreach (var c in LidGridView.SolidColours)
                Assert.That(DeltaE(c, p.mint), Is.GreaterThan(15f), $"solid lid {ColorUtility.ToHtmlStringRGB(c)} reads as mint");
            Assert.That(LidGridView.TopRingColour(4), Is.EqualTo(PaletteTokens.DefaultTop(PaletteTokens.HueOf("CrumbleShelf", "ring4"))));
            Assert.That(LidGridView.TopRingColour(3), Is.EqualTo(PaletteTokens.DefaultTop(PaletteTokens.HueOf("CrumbleShelf", "ring3"))));
            Assert.That(LidGridView.TopRingColour(2), Is.EqualTo(PaletteTokens.DefaultTop(PaletteTokens.HueOf("CrumbleShelf", "ring2"))));
            Assert.That(LidGridView.TopRingColour(1), Is.EqualTo(PaletteTokens.DefaultTop(PaletteTokens.HueOf("CrumbleShelf", "ring1"))));
            Assert.That(LidGridView.TopRingColour(0), Is.EqualTo(PaletteTokens.DefaultTop(PaletteTokens.HueOf("CrumbleShelf", "ring0"))));
            // The lit (lemon) lid stands off every solid lid in luma (G4.6 colour-blind rule; the Deutan / Protan values from
            // shot pixels are in the proof: the lit lid also glows).
            float Luma(Color c) => c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f;
            foreach (var c in LidGridView.SolidColours)
                Assert.That(Mathf.Abs(Luma(c) - Luma(LidGridView.LitYellow)), Is.GreaterThanOrEqualTo(0.15f), $"lit vs solid {ColorUtility.ToHtmlStringRGB(c)}");
            Assert.That(LidGridView.LowCalm, Is.EqualTo(PaletteTokens.DefaultTop(PaletteTokens.HueOf("CrumbleShelf", "lower"))));
        }

        [Test]
        public void EachSection_OneHue_AndEveryMapUsesSeveral()
        {
            var seen = new HashSet<string>();
            var hues = new Dictionary<string, HashSet<SurfaceHue>>();
            foreach (var s in PaletteTokens.Sections)
            {
                Assert.That(seen.Add(s.Map + "/" + s.Name), Is.True, $"duplicate section {s.Map}/{s.Name}");
                if (!hues.TryGetValue(s.Map, out var set))
                    hues[s.Map] = set = new HashSet<SurfaceHue>();
                set.Add(s.Hue);
            }

            foreach (var (_, key) in Maps)
                Assert.That(hues.TryGetValue(key, out var h) ? h.Count : 0, Is.GreaterThanOrEqualTo(3), key);
        }
    }
}
