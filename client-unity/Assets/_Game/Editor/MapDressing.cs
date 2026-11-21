using System.Collections.Generic;
using FallRush.Net;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// G2.3 set dressing (Toybox Skyworks, doc 17 §3), re-run by every map build through <see cref="MapArt.Apply"/>.
/// Everything lives under a <c>Dressing</c> root: layer 0, static, no colliders, merged into few meshes, and placed
/// where no Rushling can stand or jump (collision = visuals, doc 08 §1.2): far clouds, islands below the kill plane,
/// props outside the course edges.
/// </summary>
public static class MapDressing
{
    private static PaletteTokens P => PaletteTokens.Get();

    public static void Dress(Scene scene, MapInfo info, string mapKey, Material vc)
    {
        foreach (var go in scene.GetRootGameObjects())
            if (go.name == "Dressing")
                Object.DestroyImmediate(go);
        var root = new GameObject("Dressing").transform;
        root.gameObject.isStatic = true;
        var bounds = GeometryBounds(scene);
        var rng = new System.Random(StableSeed(mapKey));
        Clouds(root, bounds, rng, mapKey);
        // G4.8 soft look: bigger, fluffier clouds and a far stadium silhouette (own seed: the G4.6 dressing keeps its rng).
        var softRng = new System.Random(StableSeed(mapKey) ^ 0x2B1D);
        SoftClouds(root, bounds, softRng, mapKey);
        Backdrop(root, bounds, softRng, mapKey);
        var props = new MeshBuilder();
        var glass = new MeshBuilder();
        switch (mapKey)
        {
            case "Lobby": Lobby(props, glass, rng); break;
            case "BlockPartyDash": BlockPartyDash(scene, props, rng); break;
            case "GooTower": GooTower(scene, props, glass, rng); break;
            case "StarScramble": StarScramble(props, rng); break;
            case "CrumbleShelf": CrumbleShelf(props, glass, rng); break;
        }

        props.Emit(root, "Props", vc);
        glass.Emit(root, "Glass", GlassMaterial());
        Fixups(scene, mapKey);
    }

    private static int StableSeed(string s)
    {
        var h = 17;
        foreach (var ch in s)
            h = h * 31 + ch;
        return h & 0x7FFFFFFF;
    }

    public static Material GlassMaterial() => MapArt.ToonAsset("Glass", new Color(0.78f, 0.92f, 1f, 1f), m =>
    {
        MapArt.Transparent(m, 0.28f);
        m.SetFloat("_Gloss", 0.9f);
        m.SetFloat("_RimStrength", 0.8f);
        m.SetFloat("_Unlit", 0.25f);
    });

    /// <summary>Hazard visuals that get a themed look before they are merged (MapArt runs this first).</summary>
    public static void DecorateHazards(MapInfo info, string mapKey)
    {
        if (info == null)
            return;
        // G4.4 Star Fountain: the 9 m turntable carries the builder's radial stripes (readable as moving); the M4 record
        // grooves (RecordTop) are no longer used on it.
        if (mapKey == "StarScramble")
            foreach (var pl in info.platforms)
                if (pl != null && pl.visual != null)
                    IslandUnderside(pl.visual);
    }

    /// <summary>A floating-island underside (a rounded toy rock) below a moving island platform; moves with it.</summary>
    private static void IslandUnderside(Transform vis)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(SandboxBuilder.MaterialsDir + "/ShouldBlock.mat");
        var s = vis.lossyScale;
        for (var i = 0; i < 3; i++)
        {
            var k = 1f - i * 0.28f;
            // G4.4 fix: the size is local to the scaled island visual (it was given in world metres: a ≈ 15 m slab at floor height that read as a bridge across the gap).
            var go = KitBuilder.Visual(vis, $"Under_{i}", vis.position + new Vector3(0f, -s.y * 0.5f - 0.35f - i * 0.55f, 0f), new Vector3(k * 0.92f, 0.55f / s.y, k * 0.92f), mat);
            go.isStatic = false;
        }
    }

    /// <summary>Named greybox pieces that broke a readability rule.</summary>
    private static void Fixups(Scene scene, string mapKey)
    {
        foreach (var go in scene.GetRootGameObjects())
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.StartsWith("Label_CHECKPOINT"))
                    t.position += new Vector3(0f, 2.1f, -0.25f); // in front of the raised banner, centred on it
                else if (t.name == "Label_CRUMBLE SHELF")
                    t.position = new Vector3(0f, 21.4f, -9.6f - 0.75f);
                else if (t.name == "Label_FALL RUSH LOBBY")
                    t.position = new Vector3(0f, 6.6f, 0f);
                else if (t.name == "MachineGlass" && mapKey == "Lobby")
                {
                    // The greybox glass box had no collider: the capsule machine prop replaces it, with a collider
                    // of the same footprint (collision = visuals, doc 08 §1.2; decision G2-6).
                    var col = new GameObject("CapsuleMachineCollider") { layer = KitBuilder.Layer };
                    col.transform.SetParent(t.parent, false);
                    col.transform.position = new Vector3(0f, 0.5f + 1.9f, 0f);
                    col.AddComponent<BoxCollider>().size = new Vector3(1.9f, 3.8f, 1.9f);
                    col.isStatic = true;
                    Object.DestroyImmediate(t.gameObject);
                    break;
                }
            }

        foreach (var go in scene.GetRootGameObjects())
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
                if (r.name.StartsWith("TowerBand_")) // Goo Tower core bands were hazard yellow on safe geometry (doc 17 §3.1)
                {
                    r.GetComponent<MeshFilter>().sharedMesh = MapArt.BevelBoxRaw(r.transform.lossyScale, P.grape, P.grape, Color.Lerp(P.grape, Color.white, 0.2f), 0f, 0.03f);
                    r.sharedMaterial = MapArt.VertexColorMaterial();
                }
    }

    public static Bounds GeometryBounds(Scene scene)
    {
        var b = new Bounds(Vector3.zero, Vector3.zero);
        var any = false;
        foreach (var go in scene.GetRootGameObjects())
        {
            if (go.name != "Geometry")
                continue;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled)
                    continue;
                if (!any)
                {
                    b = r.bounds;
                    any = true;
                }
                else
                    b.Encapsulate(r.bounds);
            }
        }

        return b;
    }

    // ------------------------------------------------------------------ clouds

    /// <summary>Soft stylised clouds: clusters of flattened puffs, all in one static mesh (1 draw), far and few.</summary>
    private static void Clouds(Transform root, Bounds b, System.Random rng, string mapKey)
    {
        var mb = new MeshBuilder { Role = ToyMesh.RoleNone };
        var count = mapKey == "BlockPartyDash" ? 22 : 16;
        var reach = Mathf.Max(b.extents.x, b.extents.z);
        for (var i = 0; i < count; i++)
        {
            var a = (float)(i / (double)count * Mathf.PI * 2.0 + rng.NextDouble() * 0.4);
            var dist = reach + 45f + (float)rng.NextDouble() * 110f;
            var below = rng.NextDouble() < 0.7;
            var y = below ? b.min.y - 18f - (float)rng.NextDouble() * 30f : b.center.y + (float)rng.NextDouble() * 25f - 5f;
            var c = new Vector3(b.center.x + Mathf.Cos(a) * dist, y, b.center.z + Mathf.Sin(a) * dist);
            var scale = 5f + (float)rng.NextDouble() * 9f;
            var puffs = 4 + rng.Next(4);
            for (var k = 0; k < puffs; k++)
            {
                var off = new Vector3(((float)rng.NextDouble() - 0.5f) * 2.4f, (float)rng.NextDouble() * 0.5f, ((float)rng.NextDouble() - 0.5f) * 1.4f) * scale;
                var r = scale * (0.45f + (float)rng.NextDouble() * 0.4f);
                mb.Sphere(c + off, new Vector3(r, r * 0.62f, r), Color.white, new Color(0.84f, 0.88f, 1f), 10, 6);
            }
        }

        var m = CloudMaterial();
        var go = mb.Emit(root, "Clouds", m);
        go.AddComponent<FallRush.Client.LookOnly>().look = LookId.Candy; // G4.8: the G4.6 clouds belong to the candy look
    }

    private static Material CloudMaterial() => MapArt.ToonAsset("Cloud", Color.white, mat =>
    {
        mat.SetFloat("_Unlit", 0.55f);
        mat.SetFloat("_RimStrength", 0.5f);
        mat.SetColor("_ShadeColor", new Color(0.86f, 0.88f, 1f));
    });

    /// <summary>
    /// G4.8 soft look: bigger, fluffier cloud banks (1.5× the puffs, more of them, flat-bottomed), mostly below the course
    /// and around it, so the course floats over a soft cloud floor. 1 draw; out of every reach.
    /// </summary>
    private static void SoftClouds(Transform root, Bounds b, System.Random rng, string mapKey)
    {
        var mb = new MeshBuilder { Role = ToyMesh.RoleNone };
        var count = mapKey == "BlockPartyDash" ? 30 : 22;
        var reach = Mathf.Max(b.extents.x, b.extents.z);
        for (var i = 0; i < count; i++)
        {
            var a = (float)(i / (double)count * Mathf.PI * 2.0 + rng.NextDouble() * 0.5);
            var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            var below = rng.NextDouble() < 0.75;
            var dist = RectReach(b, dir) + (below ? 35f : 80f) + (float)rng.NextDouble() * 110f;
            var y = below ? b.min.y - 18f - (float)rng.NextDouble() * 26f : b.center.y + (float)rng.NextDouble() * 16f - 4f;
            var c = new Vector3(b.center.x, y, b.center.z) + dir * dist;
            var scale = 7f + (float)rng.NextDouble() * 9f;
            var puffs = 6 + rng.Next(5);
            for (var k = 0; k < puffs; k++)
            {
                var off = new Vector3(((float)rng.NextDouble() - 0.5f) * 2.8f, (float)rng.NextDouble() * 0.6f, ((float)rng.NextDouble() - 0.5f) * 1.6f) * scale;
                var r = scale * (0.4f + (float)rng.NextDouble() * 0.45f);
                mb.Sphere(c + off, new Vector3(r, r * 0.66f, r), Color.white, new Color(0.86f, 0.89f, 1f), 12, 7);
            }

            // A flat soft base under the bank (clouds sit on an even underside).
            mb.Sphere(c + Vector3.down * scale * 0.15f, new Vector3(scale * 1.7f, scale * 0.28f, scale * 1.1f), new Color(0.95f, 0.96f, 1f), new Color(0.84f, 0.87f, 1f), 14, 6);
        }

        var go = mb.Emit(root, "CloudsSoft", CloudMaterial());
        go.AddComponent<FallRush.Client.LookOnly>().look = LookId.Soft;
    }

    /// <summary>Distance from the bounds centre to the bounds rectangle's edge along the horizontal <paramref name="dir"/>.</summary>
    private static float RectReach(Bounds b, Vector3 dir)
    {
        var ax = Mathf.Abs(dir.x) > 1e-4f ? b.extents.x / Mathf.Abs(dir.x) : float.MaxValue;
        var az = Mathf.Abs(dir.z) > 1e-4f ? b.extents.z / Mathf.Abs(dir.z) : float.MaxValue;
        return Mathf.Min(ax, az);
    }

    /// <summary>
    /// G4.8 soft look: a far stadium silhouette around the course (our own shapes): stepped bleacher arcs rising out of the
    /// clouds, inflatable capsule towers with flags between them and hanging banners in the rim hues. Low-poly, one mesh, one
    /// material, no shadows, far beyond any reach; the horizon fog hazes it.
    /// </summary>
    private static void Backdrop(Transform root, Bounds b, System.Random rng, string mapKey)
    {
        var mb = new MeshBuilder { Role = ToyMesh.RoleNone };
        const float margin = 260f; // ≥ 260 m beyond the course on every side: a hazy band on the horizon, never a wall next to it
        var baseY = b.center.y - 34f; // the cloud floor hides the rest
        var topY = b.center.y + 4f;
        const int segs = 14;
        const int tiers = 4;
        var tierH = (topY - baseY) / tiers;
        var hues = P.rims;
        for (var i = 0; i < segs; i++)
        {
            var a0 = i * Mathf.PI * 2f / segs;
            var a1 = a0 + Mathf.PI * 2f / segs * 0.72f;
            var am = (a0 + a1) * 0.5f;
            var dirM = new Vector3(Mathf.Cos(am), 0f, Mathf.Sin(am));
            var radius = RectReach(b, dirM) + margin;
            var rot = Quaternion.LookRotation(-dirM);
            var hue = hues[(i * 2 + 1) % hues.Length];
            var pale = Color.Lerp(hue, Color.white, 0.78f);
            var paler = Color.Lerp(hue, Color.white, 0.9f);
            for (var k = 0; k < tiers; k++)
            {
                var r = radius + k * 7f;
                var chord = 2f * r * Mathf.Sin((a1 - a0) * 0.5f);
                var h = tierH * (k + 1);
                var c = new Vector3(b.center.x, baseY + h * 0.5f, b.center.z) + dirM * (r * Mathf.Cos((a1 - a0) * 0.5f));
                Block(mb, c, new Vector3(chord, h, 7f), rot, k % 2 == 0 ? paler : pale, pale);
                // Seat stripe on each tier's front edge.
                Block(mb, c + Vector3.up * (h * 0.5f - 0.6f) - dirM * 3.6f, new Vector3(chord * 0.98f, 1.2f, 0.4f), rot, Color.Lerp(hue, Color.white, 0.35f), Color.Lerp(hue, Color.white, 0.35f));
            }

            // Banners hanging on the front of the top tier.
            for (var k = 0; k < 3; k++)
            {
                var ab = Mathf.Lerp(a0, a1, (k + 1) / 4f);
                var d = new Vector3(Mathf.Cos(ab), 0f, Mathf.Sin(ab));
                var p = new Vector3(b.center.x, topY - 2f, b.center.z) + d * (RectReach(b, d) + margin + 24f);
                var col = hues[(i + k) % hues.Length];
                mb.Triangle(p - Vector3.Cross(Vector3.up, d) * 2.2f, p + Vector3.Cross(Vector3.up, d) * 2.2f, p + Vector3.down * 6f, col);
            }

            // An inflatable capsule tower in the gap after this arc, a flag on top.
            var ag = a1 + (Mathf.PI * 2f / segs - (a1 - a0)) * 0.5f;
            var dg = new Vector3(Mathf.Cos(ag), 0f, Mathf.Sin(ag));
            var tp = new Vector3(b.center.x, baseY, b.center.z) + dg * (RectReach(b, dg) + margin + 10f);
            var th = topY - baseY + 6f + (float)rng.NextDouble() * 6f;
            var tc = hues[(i * 3) % hues.Length];
            mb.Cylinder(tp, tp + Vector3.up * (th - 6f), 5f, Color.Lerp(tc, Color.white, 0.5f), 12);
            mb.Sphere(tp + Vector3.up * (th - 6f), new Vector3(5f, 5f, 5f), Color.Lerp(tc, Color.white, 0.35f), Color.Lerp(tc, Color.white, 0.5f), 12, 6);
            for (var k = 1; k <= 3; k++)
                mb.Sphere(tp + Vector3.up * (th * k / 4f), new Vector3(5.6f, 1.4f, 5.6f), tc, tc, 12, 4); // inflatable rings
            var top = tp + Vector3.up * (th - 1f);
            mb.Cylinder(top, top + Vector3.up * 7f, 0.3f, Color.white, 6);
            mb.Triangle(top + Vector3.up * 7f, top + Vector3.up * 4f, top + Vector3.up * 5.5f + Vector3.Cross(Vector3.up, dg) * 5f, hues[(i * 3 + 2) % hues.Length]);
        }

        var go = mb.Emit(root, "Backdrop", MapArt.ToonAsset("Backdrop", Color.white, m =>
        {
            m.SetFloat("_Unlit", 0.45f);
            m.SetFloat("_RimStrength", 0.15f);
            m.SetColor("_ShadeColor", new Color(0.86f, 0.86f, 0.98f));
        }));
        go.AddComponent<FallRush.Client.LookOnly>().look = LookId.Soft;
    }

    // ------------------------------------------------------------------ props (all visual, no colliders)

    private static readonly Color[] Party = { PaletteTokens.Hex(0xFF3EA5), PaletteTokens.Hex(0x5BE3B5), PaletteTokens.Hex(0x3FA9F5), PaletteTokens.Hex(0x7B2FF7), PaletteTokens.Hex(0xFFFFFF) };

    private static void Block(MeshBuilder mb, Vector3 c, Vector3 size, Color top, Color side, float band = 0.14f) =>
        mb.Add(MapArt.BevelBoxRaw(size, top, Color.Lerp(top, Color.white, 0.5f), side, band, 0.08f), Matrix4x4.TRS(c, Quaternion.identity, size));

    private static void Block(MeshBuilder mb, Vector3 c, Vector3 size, Quaternion rot, Color top, Color side) =>
        mb.Add(MapArt.BevelBoxRaw(size, top, top, side, 0f, 0.06f), Matrix4x4.TRS(c, rot, size));

    /// <summary>A floating island of stacked toy blocks with a cloud puff underneath.</summary>
    private static void Island(MeshBuilder mb, Vector3 top, float w, System.Random rng)
    {
        var side = P.blockSides[rng.Next(P.blockSides.Length)];
        Block(mb, top - new Vector3(0f, 0.6f, 0f), new Vector3(w, 1.2f, w * 0.85f), P.walkTop, side);
        Block(mb, top - new Vector3(0.1f * w, 1.75f, 0f), new Vector3(w * 0.7f, 1.1f, w * 0.6f), Color.Lerp(side, Color.white, 0.4f), P.blockSides[rng.Next(P.blockSides.Length)]);
        Block(mb, top - new Vector3(-0.08f * w, 2.7f, 0.05f * w), new Vector3(w * 0.42f, 0.8f, w * 0.36f), side, Color.Lerp(side, P.grape, 0.25f));
        for (var i = 0; i < 3; i++)
            mb.Sphere(top + new Vector3((i - 1) * w * 0.35f, -3.1f, (float)rng.NextDouble() * w * 0.2f), new Vector3(w * 0.32f, w * 0.18f, w * 0.3f), Color.white, new Color(0.84f, 0.88f, 1f), 10, 6);
    }

    private static void Balloon(MeshBuilder mb, Vector3 p, Color c)
    {
        mb.Sphere(p, new Vector3(0.55f, 0.68f, 0.55f), Color.Lerp(c, Color.white, 0.25f), c * 0.85f, 10, 7);
        mb.Cylinder(p + new Vector3(0f, -0.68f, 0f), p + new Vector3(0f, -0.82f, 0f), 0.09f, c * 0.8f, 6);
        mb.Cylinder(p + new Vector3(0f, -0.82f, 0f), p + new Vector3(0.15f, -3.2f, 0.05f), 0.015f, new Color(0.95f, 0.95f, 1f), 4);
    }

    /// <summary>Bunting: a sagging string with triangular party flags (no yellow-magenta pairs, which mean "hazard").</summary>
    private static void Bunting(MeshBuilder mb, Vector3 a, Vector3 b, float sag, int flags)
    {
        Vector3 At(float t) => Vector3.Lerp(a, b, t) + Vector3.down * (sag * 4f * t * (1f - t));
        const int segs = 12;
        for (var i = 0; i < segs; i++)
            mb.Cylinder(At(i / (float)segs), At((i + 1) / (float)segs), 0.02f, new Color(0.95f, 0.95f, 1f), 4);
        var dir = (b - a).normalized;
        for (var i = 0; i < flags; i++)
        {
            var t = (i + 0.5f) / flags;
            var p = At(t);
            var c = Party[i % Party.Length];
            mb.Triangle(p - dir * 0.28f, p + dir * 0.28f, p + Vector3.down * 0.62f, c);
        }
    }

    private static void Pennant(MeshBuilder mb, Vector3 baseTop, float h, Color c)
    {
        mb.Cylinder(baseTop, baseTop + Vector3.up * h, 0.05f, new Color(0.95f, 0.95f, 1f), 6);
        var top = baseTop + Vector3.up * h;
        mb.Triangle(top, top + Vector3.down * 0.7f, top + new Vector3(1.1f, -0.35f, 0f), c);
        mb.Sphere(top + Vector3.up * 0.08f, Vector3.one * 0.1f, P.sunYellow, P.sunYellow, 6, 4);
    }

    /// <summary>A gacha capsule machine: magenta cabinet, glass globe full of two-tone capsules, a crank, a chute.</summary>
    private static void CapsuleMachine(MeshBuilder mb, MeshBuilder glass, Vector3 basePos, float s, System.Random rng)
    {
        Block(mb, basePos + new Vector3(0f, 0.9f * s, 0f), new Vector3(1.8f, 1.8f, 1.8f) * s, Color.Lerp(P.magenta, Color.white, 0.3f), P.magenta);
        Block(mb, basePos + new Vector3(0f, 1.85f * s, 0f), new Vector3(2.0f, 0.12f, 2.0f) * s, P.sunYellow, P.sunYellow);
        var globe = basePos + new Vector3(0f, 2.9f * s, 0f);
        glass.Sphere(globe, Vector3.one * 1.0f * s, Color.white, new Color(0.8f, 0.9f, 1f), 14, 10);
        for (var i = 0; i < 14; i++)
        {
            var d = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() * 0.55f - 0.6f, (float)rng.NextDouble() - 0.5f).normalized * 0.62f * s;
            if (d.y > 0.1f * s)
                d.y = -d.y;
            var c = P.blockSides[rng.Next(P.blockSides.Length)];
            var cp = globe + d;
            mb.Sphere(cp + Vector3.up * 0.06f * s, Vector3.one * 0.2f * s, Color.white, Color.white, 8, 4);
            mb.Sphere(cp - Vector3.up * 0.06f * s, Vector3.one * 0.2f * s, c, c * 0.9f, 8, 4);
        }

        mb.Sphere(globe + Vector3.up * 1.0f * s, new Vector3(0.35f, 0.18f, 0.35f) * s, P.grape, P.grape, 10, 5);
        // crank on the front, chute below it
        var front = basePos + new Vector3(0f, 1.2f * s, -0.92f * s);
        mb.Cylinder(front, front + new Vector3(0f, 0f, -0.25f * s), 0.22f * s, P.sunYellow, 12);
        mb.Cylinder(front + new Vector3(0f, 0f, -0.25f * s), front + new Vector3(0.38f * s, 0.2f * s, -0.3f * s), 0.06f * s, P.grape, 6);
        mb.Sphere(front + new Vector3(0.38f * s, 0.2f * s, -0.32f * s), Vector3.one * 0.18f * s, P.mint, P.mint, 8, 5);
        Block(mb, basePos + new Vector3(0f, 0.45f * s, -0.92f * s), new Vector3(0.7f, 0.5f, 0.12f) * s, P.ink, P.ink);
    }

    private static void Gear(MeshBuilder mb, Vector3 c, Vector3 normal, float r, Color col)
    {
        var rot = Quaternion.FromToRotation(Vector3.up, normal);
        mb.Cylinder(c, c + normal * 0.03f, r * 0.82f, col, 20);
        mb.Cylinder(c, c + normal * 0.045f, r * 0.3f, Color.Lerp(col, Color.white, 0.4f), 12);
        for (var i = 0; i < 10; i++)
        {
            var a = i * 36f;
            var off = rot * (Quaternion.Euler(0f, a, 0f) * new Vector3(0f, 0.015f, r * 0.88f));
            Block(mb, c + off, new Vector3(r * 0.22f, 0.03f, r * 0.24f), rot * Quaternion.Euler(0f, a, 0f), col, col * 0.9f);
        }
    }

    // ------------------------------------------------------------------ per map

    /// <summary>Lobby: a toy plaza in the sky. The capsule machine on the plinth, four machines and a podium on islands
    /// out of reach (≥ 24 m from the centre: beyond any jump from the lookout), bunting over the plaza at 6 m.</summary>
    private static void Lobby(MeshBuilder mb, MeshBuilder glass, System.Random rng)
    {
        CapsuleMachine(mb, glass, new Vector3(0f, 0.5f, 0f), 1.05f, rng);
        foreach (var (x, z) in new[] { (-27f, -27f), (27f, -27f), (-27f, 27f), (27f, 27f) })
        {
            Island(mb, new Vector3(x, -1.5f, z), 7f, rng);
            CapsuleMachine(mb, glass, new Vector3(x, -1.5f, z), 1.4f, rng);
        }

        // Podium (1st / 2nd / 3rd) on its own island south of the plaza.
        var pz = new Vector3(0f, -2f, -30f);
        Island(mb, pz, 10f, rng);
        Block(mb, pz + new Vector3(0f, 1.0f, 0f), new Vector3(2.4f, 2f, 2.4f), P.sunYellow, Color.Lerp(P.sunYellow, P.magenta, 0.2f));
        Block(mb, pz + new Vector3(-2.5f, 0.6f, 0f), new Vector3(2.4f, 1.2f, 2.4f), P.walkTop, P.blockSides[1]);
        Block(mb, pz + new Vector3(2.5f, 0.4f, 0f), new Vector3(2.4f, 0.8f, 2.4f), P.walkTop, P.blockSides[2]);
        foreach (var (x, z) in new[] { (-15f, -15f), (15f, -15f), (-15f, 15f) })
            Pennant(mb, new Vector3(x, 1.2f, z), 5.6f, Party[rng.Next(Party.Length)]);
        Bunting(mb, new Vector3(-15f, 6.6f, -15f), new Vector3(15f, 6.6f, -15f), 0.8f, 18);
        Bunting(mb, new Vector3(-15f, 6.6f, -15f), new Vector3(-15f, 6.6f, 15f), 0.8f, 18);
        Bunting(mb, new Vector3(-15f, 6.6f, 15f), new Vector3(15f, 6.6f, -15f), 1.4f, 24);
        for (var i = 0; i < 10; i++)
        {
            var a = i * 0.63f;
            Balloon(mb, new Vector3(Mathf.Cos(a) * (22f + i % 3 * 4f), 4f + (i % 4) * 2.2f, Mathf.Sin(a) * (22f + i % 3 * 4f)), Party[i % Party.Length]);
        }
    }

    /// <summary>Capsule Run (key BlockPartyDash): banners over the checkpoints, flags on the finish arch, toy pillars under the decks,
    /// floating islands and balloons beside and below the course (never inside its reach).</summary>
    private static void BlockPartyDash(Scene scene, MeshBuilder mb, System.Random rng)
    {
        foreach (var go in scene.GetRootGameObjects())
        {
            if (go.name != "Geometry")
                continue;
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.StartsWith("CPPostL_"))
                {
                    var r = t.parent.Find("CPPostR_" + t.name.Substring(8));
                    if (r == null)
                        continue;
                    // The banner hangs above the follow camera (≈ 3.3 m over the feet) so it never fills the view; the
                    // posts continue up to it visually (above any jump: nothing there to land on).
                    var top = (t.position + r.position) * 0.5f + Vector3.up * 3.9f;
                    var w = Mathf.Abs(r.position.x - t.position.x) + 0.6f;
                    Block(mb, top, new Vector3(w, 0.9f, 0.3f), P.walkTop, P.mint);
                    foreach (var post in new[] { t, r })
                        Block(mb, post.position + Vector3.up * 2.7f, new Vector3(0.42f, 2.6f, 0.42f), P.mint, P.mint);
                    Pennant(mb, t.position + Vector3.up * 4.3f, 1.2f, P.mint);
                    Pennant(mb, r.position + Vector3.up * 4.3f, 1.2f, P.mint);
                }
                else if (t.name == "ArchPostL" || t.name == "ArchPostR")
                    Pennant(mb, t.position + Vector3.up * (t.lossyScale.y * 0.5f), 2.2f, t.name == "ArchPostL" ? P.magenta : P.grape);
                else if (t.name.EndsWith("Deck") && t.GetComponent<BoxCollider>() != null)
                {
                    // A toy pillar down into the clouds, well inside the deck's footprint.
                    var b = t.GetComponent<Renderer>().bounds;
                    if (b.size.x < 8f || b.size.z < 6f)
                        continue;
                    var c = new Vector3(b.center.x, b.min.y - 6f, b.center.z);
                    Block(mb, c, new Vector3(Mathf.Min(4f, b.size.x * 0.35f), 12f, Mathf.Min(4f, b.size.z * 0.35f)), P.walkTop, P.blockSides[rng.Next(P.blockSides.Length)]);
                }
            }
        }

        // Floating islands below and beside the course, balloons in the air at a distance (G4.2: Capsule Run is ≈ 229 m).
        for (var i = 0; i < 16; i++)
        {
            var z = -20f + i * 18f + (float)rng.NextDouble() * 6f;
            var side = i % 2 == 0 ? -1f : 1f;
            Island(mb, new Vector3(side * (26f + (float)rng.NextDouble() * 22f), -16f - (float)rng.NextDouble() * 14f, z), 6f + (float)rng.NextDouble() * 6f, rng);
        }

        for (var i = 0; i < 17; i++)
        {
            var side = i % 2 == 0 ? -1f : 1f;
            Balloon(mb, new Vector3(side * (19f + (float)rng.NextDouble() * 10f), 4f + (float)rng.NextDouble() * 9f + i * 0.5f, -10f + i * 15f), Party[i % Party.Length]);
        }
    }

    /// <summary>Goo Tower: a giant capsule machine. Flat capsule panels and gears on the core walls (decals: nothing
    /// proud of the wall), a chute and a glass globe on the dome, islands far out.</summary>
    private static void GooTower(Scene scene, MeshBuilder mb, MeshBuilder glass, System.Random rng)
    {
        // G4.3: the 14 m core (2 laps, 22 m). Flat panels on the core faces only (out of every walk volume).
        const float core = GooTowerBuilder.Core;
        var faces = new[] { (Vector3.forward, Vector3.right), (Vector3.back, Vector3.left), (Vector3.right, Vector3.back), (Vector3.left, Vector3.forward) };
        foreach (var (n, t) in faces)
        {
            for (var row = 0; row < 3; row++)
            {
                for (var col = -1; col <= 1; col++)
                {
                    var c = n * (core + 0.01f) + t * (col * 4.4f) + Vector3.up * (4.2f + row * 7.3f);
                    if ((row + col) % 2 == 0)
                    {
                        // Capsule window: a two-tone rounded panel (flat).
                        var rot = Quaternion.LookRotation(n);
                        var colr = P.blockSides[(row * 3 + col + 4) % P.blockSides.Length];
                        mb.Add(MapArt.BevelBoxRaw(new Vector3(2.6f, 1.6f, 0.02f), Color.white, Color.white, Color.white, 0f, 0.01f), Matrix4x4.TRS(c + Vector3.up * 0.8f, rot, new Vector3(2.6f, 1.6f, 0.02f)));
                        mb.Add(MapArt.BevelBoxRaw(new Vector3(2.6f, 1.6f, 0.02f), colr, colr, colr, 0f, 0.01f), Matrix4x4.TRS(c - Vector3.up * 0.8f, rot, new Vector3(2.6f, 1.6f, 0.02f)));
                        mb.Add(MapArt.BevelBoxRaw(new Vector3(2.8f, 0.16f, 0.025f), P.ink, P.ink, P.ink, 0f, 0.005f), Matrix4x4.TRS(c, rot, new Vector3(2.8f, 0.16f, 0.025f)));
                    }
                    else
                        Gear(mb, c, n, 1.1f, row % 2 == 0 ? P.sunYellow : P.mint);
                }
            }
        }

        // Dome: a glass globe of capsules on the core top (y 23) and a spiral chute above it, out of reach (summit y 22).
        var domeTop = new Vector3(0f, GooTowerBuilder.Height + 2.2f, 0f);
        glass.Sphere(domeTop + Vector3.up * 3.2f, Vector3.one * 6.4f, Color.white, new Color(0.8f, 0.9f, 1f), 18, 12);
        for (var i = 0; i < 20; i++)
        {
            var d = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() * 0.5f - 0.65f, (float)rng.NextDouble() - 0.5f).normalized * 2.2f;
            var c = P.blockSides[rng.Next(P.blockSides.Length)];
            mb.Sphere(domeTop + Vector3.up * 3.2f + d + Vector3.up * 0.35f, Vector3.one * 1.1f, Color.white, Color.white, 8, 4);
            mb.Sphere(domeTop + Vector3.up * 3.2f + d - Vector3.up * 0.35f, Vector3.one * 1.1f, c, c * 0.9f, 8, 4);
        }

        for (var i = 0; i < 20; i++)
        {
            var a0 = i * 0.32f;
            var a1 = (i + 1) * 0.32f;
            var p0 = new Vector3(Mathf.Cos(a0) * 5f, domeTop.y + 9f - i * 0.18f, Mathf.Sin(a0) * 5f);
            var p1 = new Vector3(Mathf.Cos(a1) * 5f, domeTop.y + 9f - (i + 1) * 0.18f, Mathf.Sin(a1) * 5f);
            mb.Cylinder(p0, p1, 0.7f, i % 2 == 0 ? P.magenta : Color.Lerp(P.magenta, Color.white, 0.3f), 10);
        }

        for (var i = 0; i < 8; i++)
        {
            var a = i * Mathf.PI / 4f + 0.3f;
            Island(mb, new Vector3(Mathf.Cos(a) * 40f, -8f - i % 3 * 6f, Mathf.Sin(a) * 40f), 7f, rng);
        }
    }

    /// <summary>Star Scramble: a round toy arena on a big drum base, balloons and islands around it.</summary>
    private static void StarScramble(MeshBuilder mb, System.Random rng)
    {
        for (var i = 0; i < 4; i++)
            mb.Cylinder(new Vector3(0f, -1f - i * 1.4f, 0f), new Vector3(0f, -2.4f - i * 1.4f, 0f), 16.5f - i * 1.6f, i % 2 == 0 ? P.blockSides[i] : Color.Lerp(P.blockSides[i + 1], Color.white, 0.3f), 40);
        for (var i = 0; i < 12; i++)
        {
            var a = i * Mathf.PI / 6f + 0.26f;
            Balloon(mb, new Vector3(Mathf.Cos(a) * 32f, 3f + (i % 3) * 3f, Mathf.Sin(a) * 32f), Party[i % Party.Length]);
            if (i % 2 == 0)
                Island(mb, new Vector3(Mathf.Cos(a + 0.2f) * 44f, -10f - (i % 4) * 4f, Mathf.Sin(a + 0.2f) * 44f), 6f, rng);
        }
    }

    /// <summary>Lid Flip (G4.5, map key CrumbleShelf): the two lid shelves stand inside a giant claw machine (posts at
    /// ±10.6 m, outside the 16.7 m top shelf). Glass panes only above hop and knock reach (y ≥ 10), the cabinet below the
    /// kill plane (y −6), a header ring (no ceiling slab over the shelves: the camera looks down through it).</summary>
    private static void CrumbleShelf(MeshBuilder mb, MeshBuilder glass, System.Random rng)
    {
        const float f = CrumbleShelfBuilder.Frame;
        foreach (var (n, t) in new[] { (Vector3.forward, Vector3.right), (Vector3.back, Vector3.right), (Vector3.right, Vector3.forward), (Vector3.left, Vector3.forward) })
            glass.Add(MapArt.BevelBoxRaw(new Vector3(2f * f, 9.6f, 0.05f), Color.white, Color.white, Color.white, 0f, 0f), Matrix4x4.TRS(n * f + Vector3.up * 14.9f, Quaternion.LookRotation(n), new Vector3(2f * f, 9.6f, 0.05f)));
        // Cabinet under the kill plane (y −6): magenta body, a prize door, a coin slot and buttons.
        // Its top is dark ink (a void below the kill plane, never read as a floor to land on; the lower shelf's edge stands out over it).
        Block(mb, new Vector3(0f, -11.2f, 0f), new Vector3(2f * f + 1f, 9f, 2f * f + 1f), P.ink, P.magenta);
        Block(mb, new Vector3(0f, -6.8f, 0f), new Vector3(2f * f + 1.6f, 0.6f, 2f * f + 1.6f), P.ink, P.sunYellow);
        Block(mb, new Vector3(5f, -11.2f, -f - 0.55f), new Vector3(4f, 3.2f, 0.2f), P.ink, P.ink);
        for (var i = 0; i < 3; i++)
            mb.Cylinder(new Vector3(-5f + i * 1.6f, -9.2f, -f - 0.5f), new Vector3(-5f + i * 1.6f, -9.2f, -f - 0.9f), 0.5f, Party[i], 12);
        // Header ring with lights (four beams, open in the middle).
        foreach (var (c, sz) in new[] { (new Vector3(0f, 21.4f, f), new Vector3(2f * f + 1.2f, 2.6f, 1.2f)), (new Vector3(0f, 21.4f, -f), new Vector3(2f * f + 1.2f, 2.6f, 1.2f)),
                     (new Vector3(f, 21.4f, 0f), new Vector3(1.2f, 2.6f, 2f * f + 1.2f)), (new Vector3(-f, 21.4f, 0f), new Vector3(1.2f, 2.6f, 2f * f + 1.2f)) })
            Block(mb, c, sz, P.walkTop, P.grape);
        for (var i = 0; i < 16; i++)
        {
            var x = -f + i * (2f * f / 15f);
            mb.Sphere(new Vector3(x, 21.4f, -f - 0.62f), Vector3.one * 0.32f, Party[i % Party.Length], Party[i % Party.Length], 8, 4);
        }

        for (var i = 0; i < 8; i++)
        {
            var a = i * Mathf.PI / 4f;
            Island(mb, new Vector3(Mathf.Cos(a) * 36f, -4f - i % 3 * 6f, Mathf.Sin(a) * 36f), 6f, rng);
        }
    }

    // ------------------------------------------------------------------ mesh helper

    /// <summary>Accumulates simple coloured primitives into one static mesh.</summary>
    public sealed class MeshBuilder
    {
        private readonly List<Vector3> _v = new List<Vector3>();
        private readonly List<Vector3> _n = new List<Vector3>();
        private readonly List<Color> _c = new List<Color>();
        private readonly List<Vector4> _u3 = new List<Vector4>(); // G4.8: (candy albedo, 2 + pattern + 8 × role); COLOR = soft albedo
        private readonly List<Vector2> _u2 = new List<Vector2>(); // G4.6: uv2 of added meshes (hazard view-rim flag y = 4); padded with 0

        /// <summary>G4.8: the soft-look role of the primitives added next (props gloss softly; clouds none).</summary>
        public int Role = ToyMesh.RoleProp;
        private readonly List<int> _t = new List<int>();

        public int Count => _v.Count;

        /// <summary>G4.6: primitive colours are sRGB palette colours: bake them like the blocks (<see cref="MapArt.Bake"/>), per look (G4.8).</summary>
        private void AddC(Color c)
        {
            _c.Add(MapArt.Bake(c, LookId.Soft));
            var k = MapArt.Bake(c, LookId.Candy);
            _u3.Add(new Vector4(k.r, k.g, k.b, 2f + 8f * Role));
        }

        /// <summary>A UV sphere (ellipsoid with radii <paramref name="r"/>), colour blended from <paramref name="top"/> to <paramref name="bottom"/>.</summary>
        public void Sphere(Vector3 c, Vector3 r, Color top, Color bottom, int seg = 12, int rings = 8)
        {
            var start = _v.Count;
            for (var y = 0; y <= rings; y++)
            {
                var v = y / (float)rings;
                var phi = v * Mathf.PI;
                for (var x = 0; x <= seg; x++)
                {
                    var th = x / (float)seg * Mathf.PI * 2f;
                    var d = new Vector3(Mathf.Sin(phi) * Mathf.Cos(th), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(th));
                    _v.Add(c + Vector3.Scale(d, r));
                    _n.Add(new Vector3(d.x / r.x, d.y / r.y, d.z / r.z).normalized);
                    AddC(Color.Lerp(bottom, top, d.y * 0.5f + 0.5f));
                }
            }

            for (var y = 0; y < rings; y++)
                for (var x = 0; x < seg; x++)
                {
                    var a = start + y * (seg + 1) + x;
                    var b2 = a + seg + 1;
                    _t.Add(a); _t.Add(a + 1); _t.Add(b2);
                    _t.Add(a + 1); _t.Add(b2 + 1); _t.Add(b2);
                }
        }

        /// <summary>A capped cylinder from <paramref name="a"/> to <paramref name="b"/>.</summary>
        public void Cylinder(Vector3 a, Vector3 b, float r, Color col, int sides)
        {
            var axis = b - a;
            if (axis.sqrMagnitude < 1e-8f)
                return;
            var d = axis.normalized;
            var u = Vector3.Cross(d, Mathf.Abs(d.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var w = Vector3.Cross(d, u);
            var start = _v.Count;
            for (var i = 0; i <= sides; i++)
            {
                var ang = i * Mathf.PI * 2f / sides;
                var n = u * Mathf.Cos(ang) + w * Mathf.Sin(ang);
                _v.Add(a + n * r); _n.Add(n); AddC(col * 0.9f);
                _v.Add(b + n * r); _n.Add(n); AddC(col);
            }

            for (var i = 0; i < sides; i++)
            {
                var k = start + i * 2;
                _t.Add(k); _t.Add(k + 1); _t.Add(k + 2);
                _t.Add(k + 1); _t.Add(k + 3); _t.Add(k + 2);
            }

            foreach (var (p, nn) in new[] { (a, -d), (b, d) })
            {
                var c0 = _v.Count;
                _v.Add(p); _n.Add(nn); AddC(col);
                for (var i = 0; i < sides; i++)
                {
                    var ang = i * Mathf.PI * 2f / sides;
                    _v.Add(p + (u * Mathf.Cos(ang) + w * Mathf.Sin(ang)) * r); _n.Add(nn); AddC(col);
                }

                for (var i = 0; i < sides; i++)
                {
                    _t.Add(c0); _t.Add(c0 + 1 + i); _t.Add(c0 + 1 + (i + 1) % sides);
                }
            }
        }

        /// <summary>A double-sided flat triangle (flags).</summary>
        public void Triangle(Vector3 a, Vector3 b, Vector3 c, Color col)
        {
            var n = Vector3.Cross(b - a, c - a).normalized;
            for (var side = 0; side < 2; side++)
            {
                var s = _v.Count;
                var nn = side == 0 ? n : -n;
                _v.Add(a); _v.Add(b); _v.Add(c);
                for (var k = 0; k < 3; k++)
                {
                    _n.Add(nn);
                    AddC(col);
                }

                _t.Add(s); _t.Add(s + 1); _t.Add(s + 2);
            }
        }

        /// <summary>Appends a mesh (e.g. a toy block) transformed by <paramref name="m"/>, optionally tinted.</summary>
        public void Add(Mesh mesh, Matrix4x4 m, Color? tint = null)
        {
            var start = _v.Count;
            var mv = mesh.vertices;
            var mn = mesh.normals;
            var mc = mesh.colors;
            var mu = new List<Vector2>();
            mesh.GetUVs(1, mu);
            var m3 = new List<Vector4>();
            mesh.GetUVs(3, m3);
            var nm = m.inverse.transpose;
            while (_u2.Count < start)
                _u2.Add(Vector2.zero);
            for (var i = 0; i < mv.Length; i++)
            {
                _u2.Add(mu.Count > i ? mu[i] : Vector2.zero);
                _v.Add(m.MultiplyPoint3x4(mv[i]));
                _n.Add(nm.MultiplyVector(mn.Length > i ? mn[i] : Vector3.up).normalized);
                var col = mc.Length > i ? mc[i] : Color.white;
                _c.Add(tint.HasValue ? col * tint.Value.linear : col); // mesh colours are linear already
                // G4.8: the candy colour + code from the mesh (no uv3: the same colour, role as the builder's).
                var d = m3.Count > i ? m3[i] : new Vector4(col.r, col.g, col.b, 2f + 8f * Role);
                if (tint.HasValue)
                    d = new Vector4(d.x * tint.Value.linear.r, d.y * tint.Value.linear.g, d.z * tint.Value.linear.b, d.w);
                _u3.Add(d);
            }

            foreach (var t in mesh.triangles)
                _t.Add(start + t);
        }

        public GameObject Emit(Transform parent, string name, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.isStatic = true;
            if (_v.Count == 0)
                return go;
            var mesh = new Mesh { name = "Dressing_" + name };
            if (_v.Count > 65000)
                mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(_v);
            mesh.SetNormals(_n);
            mesh.SetColors(_c);
            mesh.SetUVs(3, _u3);
            if (_u2.Exists(u => u != Vector2.zero))
            {
                while (_u2.Count < _v.Count)
                    _u2.Add(Vector2.zero);
                mesh.SetUVs(1, _u2);
            }

            mesh.SetTriangles(FixWinding(_t), 0);
            mesh.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return go;
        }

        private List<int> FixWinding(List<int> t)
        {
            for (var i = 0; i < t.Count; i += 3)
            {
                var a = _v[t[i]];
                var fn = Vector3.Cross(_v[t[i + 1]] - a, _v[t[i + 2]] - a);
                var avgN = _n[t[i]] + _n[t[i + 1]] + _n[t[i + 2]];
                if (Vector3.Dot(fn, avgN) < 0f)
                    (t[i + 1], t[i + 2]) = (t[i + 2], t[i + 1]);
            }

            return t;
        }
    }
}
