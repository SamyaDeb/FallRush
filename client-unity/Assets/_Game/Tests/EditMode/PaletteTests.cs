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
            /// <summary>G4.8 role / pattern from uv3 (−1: no dual colour on that mesh).</summary>
            public int Role, Pattern;
        }

        private static List<Top> WalkableTops(string sceneName) => WalkableTops(sceneName, LookId.Candy);

        /// <summary>
        /// G4.8: the walkable tops as one look shows them: the soft look reads the vertex colour, the candy look uv3.rgb (the
        /// dual-colour meshes); each through that look's light and grade. Rim / padding vertices (role 3, the trim) are skipped.
        /// </summary>
        private static List<Top> WalkableTops(string sceneName, LookId look)
        {
            var scene = EditorSceneManager.OpenScene($"Assets/_Game/Scenes/{sceneName}.unity", OpenSceneMode.Single);
            MapInfo info = null;
            foreach (var root in scene.GetRootGameObjects())
                info ??= root.GetComponentInChildren<MapInfo>();
            var tops = new List<Top>();
            var light = GraphicsQuality.TopLight(look);
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
                    var u3 = new List<Vector4>();
                    mesh.GetUVs(3, u3);
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
                        int role = -1, pattern = 0;
                        if (u3.Count > a && u3[a].w > 1.5f)
                        {
                            var code = Mathf.RoundToInt(u3[a].w - 2f);
                            role = code / 8;
                            pattern = code % 8;
                            if (look == LookId.Candy)
                                k = new Color(u3[a].x, u3[a].y, u3[a].z, 1f);
                        }

                        if (role == 3 || role == 4)
                            continue; // the padded rim / rounded edge (trim) and hazard-coded parts (striped hazards), not floor
                        k = GraphicsQuality.Grade(new Color(k.r * baseC.r * light.r, k.g * baseC.g * light.g, k.b * baseC.b * light.b), look);
                        var key = ColorUtility.ToHtmlStringRGB(k) + role + pattern;
                        if (seen.Add(key))
                            tops.Add(new Top { Obj = mf.transform.parent != null && mf.name == "MotorCollider" ? mf.transform.parent.name : mf.name, C = k, Role = role, Pattern = pattern });
                    }
                }
            }

            return tops;
        }

        [Test]
        public void MeaningColours_NeverOnAWalkableTop_AndNoTopNearWhite() => MeaningRule(LookId.Candy);

        /// <summary>
        /// G4.8 soft look on the committed scenes: no meaning colour (hit pink, mint, goo, the soft sky) on a walkable top; no
        /// top near-white (S ≥ 0.1); every patterned section top (role 1, pattern &gt; 0) in the pastel band (S 0.10–0.30, V 0.90–0.99:
        /// the tokens' 0.12–0.28 / 0.93–0.98 after the bake and grade, ±0.02).
        /// </summary>
        [Test]
        public void SoftLook_SectionTopsPastel_MeaningColoursNeverOnATop() => MeaningRule(LookId.Soft);

        private static void MeaningRule(LookId look)
        {
            var p = PaletteTokens.Get();
            var report = new StringBuilder();
            var fails = new List<string>();
            var meaning = look == LookId.Candy
                ? Meaning(p)
                : new[] { ("hit pink", p.hazardMagenta), ("mint", p.mint), ("goo", p.goo), ("sky top", p.SkyTop(LookId.Soft)), ("sky horizon", p.SkyHorizon(LookId.Soft)) };
            var sections = 0;
            foreach (var (scene, _) in Maps)
            {
                var tops = WalkableTops(scene, look);
                var colours = new HashSet<string>();
                foreach (var t in tops)
                {
                    colours.Add(ColorUtility.ToHtmlStringRGB(t.C));
                    Color.RGBToHSV(t.C, out _, out var s, out var v);
                    if (s < (look == LookId.Candy ? 0.4f : 0.1f))
                        fails.Add($"{scene} {t.Obj}: top {ColorUtility.ToHtmlStringRGB(t.C)} near-white (S {s:F2})");
                    if (look == LookId.Soft && t.Role == 1 && t.Pattern > 0)
                    {
                        sections++;
                        if (s < 0.1f || s > 0.3f || v < 0.9f || v > 0.99f)
                            fails.Add($"{scene} {t.Obj}: section top {ColorUtility.ToHtmlStringRGB(t.C)} outside the pastel band (S {s:F2}, V {v:F2})");
                    }

                    foreach (var (name, m) in meaning)
                    {
                        if (name == "mint" && GoalObject(t.Obj))
                            continue;
                        var de = DeltaE(t.C, m);
                        if (de < 15f)
                            fails.Add($"{scene} {t.Obj}: top {ColorUtility.ToHtmlStringRGB(t.C)} is {name} (ΔE {de:F1})");
                    }
                }

                report.Append($"{scene} ({look}): {tops.Count} distinct top colours on walkable objects ({string.Join(" ", colours)})\n");
            }

            if (look == LookId.Soft)
                Assert.That(sections, Is.GreaterThan(10), "soft section tops found (dual-colour meshes)");

            // Lid Flip's lids are drawn by the client (no static tops): its solid colours by the same rules.
            foreach (var c in LidGridView.SolidColoursFor(look))
            {
                Color.RGBToHSV(c, out _, out var s, out _);
                if (s < (look == LookId.Candy ? 0.4f : 0.1f))
                    fails.Add($"LidGridView solid {ColorUtility.ToHtmlStringRGB(c)} near-white");
                foreach (var (name, m) in meaning)
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

        // Machado, Oliveira & Fernandes 2009, severity 1.0 (linear RGB): the matrices of the proof sheets (PaletteShots).
        private static readonly float[] Deutan = { 0.367322f, 0.860646f, -0.227968f, 0.280085f, 0.672501f, 0.047413f, -0.011820f, 0.042940f, 0.968881f };
        private static readonly float[] Protan = { 0.152286f, 1.052583f, -0.204868f, 0.114503f, 0.786281f, 0.099216f, -0.003882f, -0.048116f, 1.051998f };

        private static Color Simulate(Color srgb, float[] m)
        {
            var l = srgb.linear;
            return new Color(Mathf.Clamp01(m[0] * l.r + m[1] * l.g + m[2] * l.b), Mathf.Clamp01(m[3] * l.r + m[4] * l.g + m[5] * l.b),
                Mathf.Clamp01(m[6] * l.r + m[7] * l.g + m[8] * l.b)).gamma;
        }

        /// <summary>
        /// G4.8: Lid Flip in each look: no solid lid reads as mint or Lemon drop; the lit lid stands ≥ 0.15 off every solid lid in
        /// luma for normal, deuteranopia and protanopia vision (the soft look's lit is a deeper amber: pale lids are lighter).
        /// </summary>
        [Test]
        public void LidFlip_LitVsSolid_LumaGap_NormalDeutanProtan_BothLooks()
        {
            var p = PaletteTokens.Get();
            float Luma(Color c) => c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f;
            var report = new StringBuilder();
            foreach (var look in new[] { LookId.Soft, LookId.Candy })
            {
                var lit = LidGridView.LitFor(look);
                foreach (var c in LidGridView.SolidColoursFor(look))
                {
                    Assert.That(DeltaE(c, p.mint), Is.GreaterThan(15f), $"{look}: solid lid {ColorUtility.ToHtmlStringRGB(c)} reads as mint");
                    var n = Mathf.Abs(Luma(c) - Luma(lit));
                    var d = Mathf.Abs(Luma(Simulate(c, Deutan)) - Luma(Simulate(lit, Deutan)));
                    var pr = Mathf.Abs(Luma(Simulate(c, Protan)) - Luma(Simulate(lit, Protan)));
                    report.Append($"{look} lit {ColorUtility.ToHtmlStringRGB(lit)} vs {ColorUtility.ToHtmlStringRGB(c)}: normal {n:F3} deutan {d:F3} protan {pr:F3}\n");
                    Assert.That(n, Is.GreaterThanOrEqualTo(0.15f), $"{look} lit vs {ColorUtility.ToHtmlStringRGB(c)} (normal)");
                    if (look == LookId.Soft) // G4.8 rule; the candy lit also glows (G4.6 proof: measured on shot pixels)
                    {
                        Assert.That(d, Is.GreaterThanOrEqualTo(0.15f), $"{look} lit vs {ColorUtility.ToHtmlStringRGB(c)} (deuteranopia)");
                        Assert.That(pr, Is.GreaterThanOrEqualTo(0.15f), $"{look} lit vs {ColorUtility.ToHtmlStringRGB(c)} (protanopia)");
                    }
                }

                var ring2 = PaletteTokens.HueOf("CrumbleShelf", "ring2");
                Assert.That(LidGridView.TopRingColour(2, look), Is.EqualTo(look == LookId.Candy ? PaletteTokens.DefaultTop(ring2, look) : PaletteTokens.SoftLidTop(ring2)));
            }

            TestContext.WriteLine(report.ToString());
        }

        /// <summary>A soft-look colour seen on a calm lid in full cast shadow: the sun term keeps <see cref="LidGridView.CalmShadowKeep"/>
        /// (sun 0.58 + ambient 0.34 of the Toon light, a lit top), applied in linear light.</summary>
        private static Color InCalmShadow(Color c)
        {
            var k = (LidGridView.CalmShadowKeep * 0.58f + 0.34f) / (0.58f + 0.34f);
            var l = c.linear;
            return new Color(l.r * k, l.g * k, l.b * k).gamma;
        }

        /// <summary>
        /// G4.13 (decision G4.13-2): Lid Flip's soft lids are their own token set: same hue per ring as the map table, clearly more
        /// saturated than the other maps' soft pastel tops, never near-white, and every lit-vs-calm luma rule still holds, also for a
        /// calm lid in a cast shadow (≥ 0.15 normal, ≥ 0.12 deutan / protan); the warn magenta stays ≥ 0.15 off every calm lid too.
        /// </summary>
        [Test]
        public void LidFlip_SoftLids_SaturatedSet_SameHues_LumaGapsHoldInShadow()
        {
            float Luma(Color c) => c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f;
            var lit = LidGridView.LitFor(LookId.Soft);
            var report = new StringBuilder();
            for (var ring = 0; ring <= 4; ring++)
            {
                var hue = LidGridView.RingHue(ring);
                var c = LidGridView.TopRingColour(ring, LookId.Soft);
                Assert.That(c, Is.EqualTo(PaletteTokens.SoftLidTop(hue)), $"ring {ring} uses the Lid Flip soft set");
                Color.RGBToHSV(c, out var h, out var s, out var v);
                Color.RGBToHSV(PaletteTokens.DefaultTop(hue), out var h0, out _, out _);
                Color.RGBToHSV(PaletteTokens.DefaultTop(hue, LookId.Soft), out _, out var sPastel, out _);
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(h * 360f, h0 * 360f)), Is.LessThan(8f), $"ring {ring}: same hue as its section");
                Assert.That(s, Is.GreaterThan(sPastel), $"ring {ring}: more saturated than the map pastel");
                Assert.That(s, Is.InRange(0.17f, 0.6f), $"ring {ring}: S in the Lid Flip band");
                Assert.That(v, Is.GreaterThanOrEqualTo(0.9f), $"ring {ring}: V");
            }

            Color.RGBToHSV(LidGridView.TopRingColour(3, LookId.Soft), out _, out var sTangerine, out _);
            Assert.That(sTangerine, Is.GreaterThanOrEqualTo(0.4f), "Tangerine ring: clearly saturated");
            Assert.That(LidGridView.LowCalmFor(LookId.Soft), Is.EqualTo(PaletteTokens.SoftLidTop(SurfaceHue.GrapeFizz)));

            foreach (var c0 in LidGridView.SolidColoursFor(LookId.Soft))
            {
                foreach (var (tag, c) in new[] { ("lit", c0), ("shadow", InCalmShadow(c0)) })
                {
                    var n = Mathf.Abs(Luma(c) - Luma(lit));
                    var d = Mathf.Abs(Luma(Simulate(c, Deutan)) - Luma(Simulate(lit, Deutan)));
                    var pr = Mathf.Abs(Luma(Simulate(c, Protan)) - Luma(Simulate(lit, Protan)));
                    report.Append($"soft lit vs {ColorUtility.ToHtmlStringRGB(c0)} ({tag}): normal {n:F3} deutan {d:F3} protan {pr:F3}\n");
                    Assert.That(n, Is.GreaterThanOrEqualTo(0.15f), $"{ColorUtility.ToHtmlStringRGB(c0)} {tag} (normal)");
                    Assert.That(d, Is.GreaterThanOrEqualTo(0.12f), $"{ColorUtility.ToHtmlStringRGB(c0)} {tag} (deuteranopia)");
                    Assert.That(pr, Is.GreaterThanOrEqualTo(0.12f), $"{ColorUtility.ToHtmlStringRGB(c0)} {tag} (protanopia)");
                }

                if (c0 != LidGridView.LowEdgeFor(LookId.Soft)) // the lower edge ring (unchanged since G4.8z) is a deep mid tone; its warn pulses to WarnLight
                    Assert.That(Mathf.Abs(Luma(c0) - Luma(LidGridView.WarnMagenta)), Is.GreaterThanOrEqualTo(0.15f), $"warn vs {ColorUtility.ToHtmlStringRGB(c0)}");
            }

            TestContext.WriteLine(report.ToString());
        }

        /// <summary>
        /// G4.13 (bug B): a flipped lid reads as a hole, never black. Soft: the open colour is a dark lavender (luma 0.35–0.55 on a lit
        /// face, its underside vertex tint keeps ≥ 0.22), clearly darker than every calm lid and off the lit / warn colours.
        /// </summary>
        [Test]
        public void LidFlip_OpenLid_DarkLavender_NotBlack()
        {
            float Luma(Color c) => c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f;
            var open = LidGridView.OpenFor(LookId.Soft);
            Assert.That(Luma(open), Is.InRange(0.35f, 0.55f), "soft open lid luma");
            var l = open.linear;
            const float under = 0.36f; // LidMesh underside vertex colour (≈ its red / green), applied to the albedo
            var underside = new Color(l.r * under, l.g * under, l.b * under).gamma;
            Assert.That(Luma(underside), Is.GreaterThanOrEqualTo(0.22f), "soft open lid underside");
            Color.RGBToHSV(open, out var h, out var s, out _);
            Assert.That(h * 360f, Is.InRange(240f, 290f), "lavender hue");
            Assert.That(s, Is.InRange(0.25f, 0.6f), "a soft lavender, not ink");
            foreach (var c in LidGridView.SolidColoursFor(LookId.Soft))
                if (c != LidGridView.LowEdgeFor(LookId.Soft))
                    Assert.That(Luma(c) - Luma(open), Is.GreaterThanOrEqualTo(0.3f), $"open vs calm {ColorUtility.ToHtmlStringRGB(c)}: a hole");
            Assert.That(LidGridView.OpenFor(LookId.Candy), Is.EqualTo(LidGridView.Open), "candy unchanged");
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
