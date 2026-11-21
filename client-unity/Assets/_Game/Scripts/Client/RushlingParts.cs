using System.Collections.Generic;
using FallRush.Shared;
using UnityEngine;

namespace FallRush.Client
{
    /// <summary>
    /// G2.2 Bolt final: the Rushling's parts merged into two static, vertex-coloured meshes per cosmetic preset (cached,
    /// shared by every Rushling with that preset), so a Rushling costs 3 renderers instead of ~20:
    /// <list type="bullet">
    /// <item><b>Body</b> (squash node space): glossy coloured bottom shell, equator seam, hinge, emblem decal, wind-up key
    /// (key vertices flagged uv2.y = 2: the toon shader spins them about the shaft).</item>
    /// <item><b>Lid</b> (lid hinge space): smooth white dome, the LED screen (uv2.y = 1: emissive glyph atlas) and the topper.</item>
    /// <item><b>Limbs</b>: noodle arms, mittens and peg feet, rewritten in world space every frame by <see cref="RushlingVisuals"/>.</item>
    /// </list>
    /// uv2.y = 3 marks small parts that get no outline hull of their own (seam, emblem, goggles, face).
    /// Budget (doc 17 §3.2): ≤ 2.5k triangles per Rushling including the arms (asserted by a test).
    /// </summary>
    public static class RushlingParts
    {
        public const float FlagNone = 0f, FlagFace = 1f, FlagKey = 2f, FlagNoHull = 3f;
        public static readonly Vector3 KeyPivot = new Vector3(0f, 0.5f, -ProcMesh.R - 0.005f);

        private static readonly Dictionary<int, Mesh> BodyCache = new Dictionary<int, Mesh>();
        private static readonly Dictionary<int, Mesh> LidCache = new Dictionary<int, Mesh>();
        private static Mesh _stars;

        private static readonly Color Ink = UiKit.Hex(0x2A1250);
        private static readonly Color DomeWhite = new Color(0.98f, 0.98f, 1f);
        private static readonly Color Strap = UiKit.Hex(0x5A3A1E);
        private static readonly Color Gold = UiKit.Hex(0xFFD21F);
        private static readonly Color Lens = UiKit.Hex(0x3FA9F5);

        /// <summary>LED glyph colour per face theme (doc 17 §2.4: green, cyan, amber, pink).</summary>
        public static Color FaceTint(CosmeticPresets.FaceTheme t) => t switch
        {
            CosmeticPresets.FaceTheme.Cyan => UiKit.Hex(0x58E1FF),
            CosmeticPresets.FaceTheme.Amber => UiKit.Hex(0xFFB547),
            CosmeticPresets.FaceTheme.Pink => UiKit.Hex(0xFF7AC6),
            _ => UiKit.Hex(0x5BE3B5),
        };

        // ------------------------------------------------------------------ combiner

        public sealed class Combiner
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Vector3> N = new List<Vector3>();
            public readonly List<Color> C = new List<Color>();
            public readonly List<Vector2> U = new List<Vector2>();
            public readonly List<Vector2> U2 = new List<Vector2>();
            public readonly List<int> T = new List<int>();

            public void Add(Mesh m, Matrix4x4 xf, Color col, float flag)
            {
                var start = V.Count;
                var mv = m.vertices;
                var mn = m.normals;
                var mu = m.uv;
                var nm = xf.inverse.transpose;
                for (var i = 0; i < mv.Length; i++)
                {
                    V.Add(xf.MultiplyPoint3x4(mv[i]));
                    N.Add(nm.MultiplyVector(i < mn.Length ? mn[i] : Vector3.up).normalized);
                    C.Add(col);
                    U.Add(i < mu.Length ? mu[i] : Vector2.zero);
                    U2.Add(new Vector2(0f, flag));
                }

                var tris = m.triangles;
                var flip = xf.determinant < 0f;
                for (var i = 0; i < tris.Length; i += 3)
                {
                    T.Add(start + tris[i]);
                    T.Add(start + (flip ? tris[i + 2] : tris[i + 1]));
                    T.Add(start + (flip ? tris[i + 1] : tris[i + 2]));
                }
            }

            public Mesh Build(string name)
            {
                var m = new Mesh { name = name };
                m.SetVertices(V);
                m.SetNormals(N);
                m.SetColors(C);
                m.SetUVs(0, U);
                m.SetUVs(1, U2);
                m.SetTriangles(T, 0);
                m.RecalculateBounds();
                m.UploadMeshData(true); // static: free the CPU copy
                return m;
            }
        }

        private static Matrix4x4 Trs(Vector3 p, Quaternion r, Vector3 s) => Matrix4x4.TRS(p, r, s);

        // ------------------------------------------------------------------ body

        public static Mesh Body(int presetId)
        {
            var id = CosmeticPresets.Clamp(presetId);
            if (BodyCache.TryGetValue(id, out var cached) && cached != null)
                return cached;
            var p = CosmeticPresets.Get(id);
            var shell = UiKit.Hex(p.Shell);
            var c = new Combiner();
            c.Add(Shapes.Shell, Matrix4x4.identity, shell, FlagNone);
            c.Add(Shapes.ThinTorus, Trs(new Vector3(0f, ProcMesh.SeamY, 0f), Quaternion.identity, new Vector3(ProcMesh.R + 0.003f, 0.32f, ProcMesh.R + 0.003f)), Ink, FlagNoHull);
            c.Add(Shapes.Box, Trs(new Vector3(0f, ProcMesh.SeamY, -ProcMesh.R - 0.008f), Quaternion.identity, new Vector3(0.1f, 0.035f, 0.03f)), Ink, FlagNoHull);
            c.Add(Shapes.Emblem(p.Emblem), Trs(new Vector3(0f, 0.43f, ProcMesh.R + 0.012f), Quaternion.identity, Vector3.one * 0.8f), UiKit.Hex(p.EmblemColor), FlagNoHull);
            Key(c, p.Key, UiKit.Hex(p.KeyColor));
            var m = c.Build("RushlingBody_" + p.Name);
            BodyCache[id] = m;
            return m;
        }

        private static void Key(Combiner c, CosmeticPresets.KeyStyle style, Color col)
        {
            var o = KeyPivot;
            c.Add(Shapes.Cylinder8, Trs(o + new Vector3(0f, 0f, -0.05f), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.04f, 0.05f, 0.04f)), col, FlagKey);
            switch (style)
            {
                case CosmeticPresets.KeyStyle.Star:
                    c.Add(Shapes.Star5, Trs(o + new Vector3(0f, 0f, -0.11f), Quaternion.identity, Vector3.one * 2.4f), col, FlagKey);
                    break;
                case CosmeticPresets.KeyStyle.Gear:
                    c.Add(Shapes.Gear, Trs(o + new Vector3(0f, 0f, -0.11f), Quaternion.identity, Vector3.one), col, FlagKey);
                    break;
                default:
                    for (var side = -1; side <= 1; side += 2)
                        c.Add(Shapes.LoopTorus, Trs(o + new Vector3(side * 0.085f, 0f, -0.11f), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.075f, 0.2f, 0.06f)), col, FlagKey);
                    break;
            }
        }

        // ------------------------------------------------------------------ lid (dome + screen + topper)

        /// <summary>Lid node offset: the lid hinges at the back of the seam; parts are built at body height then offset.</summary>
        public static readonly Vector3 LidOffset = new Vector3(0f, -ProcMesh.SeamY, ProcMesh.R);

        public static Mesh Lid(int presetId)
        {
            var id = CosmeticPresets.Clamp(presetId);
            if (LidCache.TryGetValue(id, out var cached) && cached != null)
                return cached;
            var p = CosmeticPresets.Get(id);
            var c = new Combiner();
            var off = Matrix4x4.Translate(LidOffset);
            c.Add(Shapes.Dome, off, DomeWhite, FlagNone);
            c.Add(ProcMesh.Face, off, Color.white, FlagFace);
            switch (p.Topper)
            {
                case CosmeticPresets.Topper.Goggles:
                {
                    var strapY = 0.84f;
                    var strapR = ProcMesh.RadiusAt(strapY) + 0.006f;
                    c.Add(Shapes.ThinTorus, off * Trs(new Vector3(0f, strapY, 0f), Quaternion.Euler(-12f, 0f, 0f), new Vector3(strapR, 0.22f, strapR)), Strap, FlagNoHull);
                    for (var side = -1; side <= 1; side += 2)
                    {
                        var ly = 0.9f;
                        var lz = ProcMesh.RadiusAt(ly) * 0.86f;
                        var lp = new Vector3(side * 0.085f, ly, lz);
                        var lr = Quaternion.Euler(-50f, side * 18f, 0f) * Quaternion.Euler(90f, 0f, 0f);
                        c.Add(Shapes.LoopTorus, off * Trs(lp, lr, new Vector3(0.055f, 0.15f, 0.055f)), Gold, FlagNoHull);
                        c.Add(Shapes.Cylinder12, off * Trs(lp, lr, new Vector3(0.1f, 0.008f, 0.1f)), Lens, FlagNoHull);
                    }

                    break;
                }
                case CosmeticPresets.Topper.Propeller:
                {
                    var top = new Vector3(0f, 1.0f, 0f);
                    c.Add(Shapes.Cylinder8, off * Trs(top + new Vector3(0f, 0.035f, 0f), Quaternion.identity, new Vector3(0.025f, 0.035f, 0.025f)), Gold, FlagNoHull);
                    c.Add(Shapes.Box, off * Trs(top + new Vector3(0f, 0.075f, 0f), Quaternion.Euler(0f, 30f, 0f), new Vector3(0.34f, 0.012f, 0.06f)), UiKit.Hex(0xFF3EA5), FlagNone);
                    c.Add(Shapes.Box, off * Trs(top + new Vector3(0f, 0.077f, 0f), Quaternion.Euler(0f, 120f, 0f), new Vector3(0.34f, 0.012f, 0.06f)), UiKit.Hex(0x3FA9F5), FlagNone);
                    break;
                }
                case CosmeticPresets.Topper.ChefHat:
                {
                    var top = new Vector3(0f, 0.97f, -0.02f);
                    c.Add(Shapes.Cylinder12, off * Trs(top + new Vector3(0f, 0.04f, 0f), Quaternion.identity, new Vector3(0.24f, 0.04f, 0.24f)), Color.white, FlagNone);
                    c.Add(Shapes.Puff, off * Trs(top + new Vector3(0f, 0.13f, 0f), Quaternion.identity, new Vector3(0.3f, 0.17f, 0.3f)), Color.white, FlagNone);
                    break;
                }
                case CosmeticPresets.Topper.Headphones:
                {
                    var bandY = 0.92f;
                    c.Add(Shapes.HalfTorus, off * Trs(new Vector3(0f, 0.74f, 0f), Quaternion.Euler(0f, 0f, 0f), new Vector3(ProcMesh.R + 0.02f, 0.25f, ProcMesh.R + 0.02f)), UiKit.Hex(0x2A1250), FlagNone);
                    for (var side = -1; side <= 1; side += 2)
                        c.Add(Shapes.Cylinder12, off * Trs(new Vector3(side * (ProcMesh.R + 0.015f), bandY - 0.18f, 0f), Quaternion.Euler(0f, 0f, 90f), new Vector3(0.13f, 0.03f, 0.13f)), UiKit.Hex(0xFF3EA5), FlagNone);
                    break;
                }
            }

            var m = c.Build("RushlingLid_" + p.Name);
            LidCache[id] = m;
            return m;
        }

        /// <summary>Three stun stars in a ring (one renderer; the ring spins).</summary>
        public static Mesh StunStars()
        {
            if (_stars != null)
                return _stars;
            var c = new Combiner();
            for (var i = 0; i < 3; i++)
            {
                var a = i * Mathf.PI * 2f / 3f;
                c.Add(Shapes.Star5, Trs(new Vector3(Mathf.Cos(a) * 0.26f, 0f, Mathf.Sin(a) * 0.26f), Quaternion.Euler(0f, -a * Mathf.Rad2Deg + 90f, 0f), Vector3.one * 1.3f), Gold, FlagNoHull);
            }

            return _stars = c.Build("StunStars");
        }

        /// <summary>Triangle count of one Rushling with this preset (body + lid + limbs), for the budget test.</summary>
        public static int Triangles(int presetId) =>
            (int)(Body(presetId).GetIndexCount(0) / 3 + Lid(presetId).GetIndexCount(0) / 3) + LimbTriangles;

        public const int ArmRings = 9, ArmSides = 6, MittSeg = 8, MittRings = 5, FootSides = 8;
        public static int LimbTriangles => 2 * ((ArmRings - 1) * ArmSides * 2) + 2 * (MittSeg * MittRings * 2) + 2 * (FootSides * 4);

        // ------------------------------------------------------------------ low-poly shapes (budgeted)

        /// <summary>Generated once; budgeted to keep a Rushling ≤ 2.5k triangles.</summary>
        public static class Shapes
        {
            private static Mesh _shell, _dome, _thin, _loop, _half, _cyl8, _cyl12, _box, _star5, _gear, _puff;
            private static readonly Dictionary<CosmeticPresets.Emblem, Mesh> Emblems = new Dictionary<CosmeticPresets.Emblem, Mesh>();

            public static Mesh Shell => _shell != null ? _shell : _shell = HalfCapsule(false, 24, 8);
            public static Mesh Dome => _dome != null ? _dome : _dome = HalfCapsule(true, 24, 8);
            public static Mesh ThinTorus => _thin != null ? _thin : _thin = Torus(1f, 0.05f, 24, 4, 1f);
            public static Mesh LoopTorus => _loop != null ? _loop : _loop = Torus(1f, 0.14f, 12, 4, 1f);
            public static Mesh HalfTorus => _half != null ? _half : _half = Torus(1f, 0.06f, 12, 4, 0.5f);
            public static Mesh Cylinder8 => _cyl8 != null ? _cyl8 : _cyl8 = Cylinder(8);
            public static Mesh Cylinder12 => _cyl12 != null ? _cyl12 : _cyl12 = Cylinder(12);
            public static Mesh Box => _box != null ? _box : _box = BoxMesh();
            public static Mesh Star5 => _star5 != null ? _star5 : _star5 = Polygon(StarOutline(0.06f, 0.026f, 5), 0.02f);
            public static Mesh Gear => _gear != null ? _gear : _gear = Polygon(GearOutline(0.075f, 0.055f, 8), 0.025f);
            public static Mesh Puff => _puff != null ? _puff : _puff = Sphere(10, 6);

            public static Mesh Emblem(CosmeticPresets.Emblem e)
            {
                if (Emblems.TryGetValue(e, out var m) && m != null)
                    return m;
                Vector2[] o = e switch
                {
                    CosmeticPresets.Emblem.Star => StarOutline(0.085f, 0.037f, 5),
                    CosmeticPresets.Emblem.Heart => HeartOutline(0.08f),
                    CosmeticPresets.Emblem.Flame => FlameOutline(0.1f),
                    CosmeticPresets.Emblem.Diamond => new[] { new Vector2(0f, 0.1f), new Vector2(0.065f, 0f), new Vector2(0f, -0.1f), new Vector2(-0.065f, 0f) },
                    CosmeticPresets.Emblem.Moon => MoonOutline(0.085f),
                    _ => new[] // our own 8-point zig-zag bolt (as M2b)
                    {
                        new Vector2(0.02f, 0.10f), new Vector2(0.06f, 0.10f), new Vector2(0.01f, 0.01f), new Vector2(0.05f, 0.01f),
                        new Vector2(-0.05f, -0.11f), new Vector2(-0.01f, -0.02f), new Vector2(-0.05f, -0.02f), new Vector2(-0.01f, 0.10f),
                    },
                };
                m = Polygon(o, 0.015f);
                Emblems[e] = m;
                return m;
            }

            private static Mesh HalfCapsule(bool top, int sides, int capRings)
            {
                var profile = new List<Vector2> { new Vector2(ProcMesh.R, ProcMesh.SeamY) };
                var centre = top ? ProcMesh.UpperCenter : ProcMesh.LowerCenter;
                profile.Add(new Vector2(ProcMesh.R, centre));
                for (var i = 1; i <= capRings; i++)
                {
                    var a = i / (float)capRings * Mathf.PI * 0.5f;
                    profile.Add(new Vector2(ProcMesh.R * Mathf.Cos(a), centre + (top ? 1f : -1f) * ProcMesh.R * Mathf.Sin(a)));
                }

                var rings = profile.Count;
                var v = new List<Vector3>();
                var n = new List<Vector3>();
                var t = new List<int>();
                for (var r = 0; r < rings; r++)
                {
                    var pr = profile[r];
                    // Smooth normals of the capsule surface (sphere caps, vertical sides).
                    for (var s = 0; s <= sides; s++)
                    {
                        var a = s / (float)sides * Mathf.PI * 2f;
                        var dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                        var p = dir * pr.x + Vector3.up * pr.y;
                        v.Add(p);
                        var ny = (pr.y - centre) / ProcMesh.R;
                        if (r <= 1)
                            ny = 0f;
                        n.Add((dir * Mathf.Sqrt(Mathf.Max(0f, 1f - ny * ny)) + Vector3.up * ny).normalized);
                    }
                }

                for (var r = 0; r < rings - 1; r++)
                    for (var s = 0; s < sides; s++)
                    {
                        var a = r * (sides + 1) + s;
                        var b = a + sides + 1;
                        if (top)
                        {
                            t.Add(a); t.Add(a + 1); t.Add(b);
                            t.Add(a + 1); t.Add(b + 1); t.Add(b);
                        }
                        else
                        {
                            t.Add(a); t.Add(b); t.Add(a + 1);
                            t.Add(a + 1); t.Add(b); t.Add(b + 1);
                        }
                    }

                return Fix(Make("Half", v, n, t));
            }

            private static Mesh Torus(float major, float minor, int segs, int sides, float arc)
            {
                var v = new List<Vector3>();
                var n = new List<Vector3>();
                var t = new List<int>();
                for (var i = 0; i <= segs; i++)
                {
                    var u = i / (float)segs * arc * Mathf.PI * 2f;
                    var c = arc < 1f ? new Vector3(Mathf.Cos(u), Mathf.Sin(u), 0f) : new Vector3(Mathf.Sin(u), 0f, Mathf.Cos(u));
                    var up = arc < 1f ? Vector3.forward : Vector3.up;
                    for (var j = 0; j <= sides; j++)
                    {
                        var a = j / (float)sides * Mathf.PI * 2f;
                        var nn = c * Mathf.Cos(a) + up * Mathf.Sin(a);
                        v.Add(c * major + nn * minor);
                        n.Add(nn);
                    }
                }

                for (var i = 0; i < segs; i++)
                    for (var j = 0; j < sides; j++)
                    {
                        var a = i * (sides + 1) + j;
                        var b = a + sides + 1;
                        t.Add(a); t.Add(b); t.Add(a + 1);
                        t.Add(a + 1); t.Add(b); t.Add(b + 1);
                    }

                return Fix(Make("Torus", v, n, t));
            }

            private static Mesh Cylinder(int sides)
            {
                // Unit cylinder like Unity's primitive: radius 0.5, y −1..1.
                var v = new List<Vector3>();
                var n = new List<Vector3>();
                var t = new List<int>();
                for (var cap = 0; cap < 2; cap++)
                {
                    var y = cap == 0 ? 1f : -1f;
                    var c0 = v.Count;
                    v.Add(new Vector3(0f, y, 0f));
                    n.Add(Vector3.up * y);
                    for (var i = 0; i < sides; i++)
                    {
                        var a = i * Mathf.PI * 2f / sides;
                        v.Add(new Vector3(Mathf.Cos(a) * 0.5f, y, Mathf.Sin(a) * 0.5f));
                        n.Add(Vector3.up * y);
                    }

                    for (var i = 0; i < sides; i++)
                    {
                        t.Add(c0); t.Add(c0 + 1 + i); t.Add(c0 + 1 + (i + 1) % sides);
                    }
                }

                for (var i = 0; i < sides; i++)
                {
                    var a0 = i * Mathf.PI * 2f / sides;
                    var a1 = (i + 1) * Mathf.PI * 2f / sides;
                    var d0 = new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0));
                    var d1 = new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1));
                    var s = v.Count;
                    v.Add(d0 * 0.5f + Vector3.up); v.Add(d1 * 0.5f + Vector3.up); v.Add(d1 * 0.5f + Vector3.down); v.Add(d0 * 0.5f + Vector3.down);
                    n.Add(d0); n.Add(d1); n.Add(d1); n.Add(d0);
                    t.Add(s); t.Add(s + 1); t.Add(s + 2);
                    t.Add(s); t.Add(s + 2); t.Add(s + 3);
                }

                return Fix(Make("Cyl", v, n, t));
            }

            private static Mesh BoxMesh()
            {
                var v = new List<Vector3>();
                var n = new List<Vector3>();
                var t = new List<int>();
                var dirs = new[] { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
                foreach (var d in dirs)
                {
                    var a = Vector3.Cross(d, Mathf.Abs(d.y) > 0.5f ? Vector3.forward : Vector3.up) * 0.5f;
                    var b = Vector3.Cross(d, a) * 2f * 0.5f;
                    var s = v.Count;
                    var c = d * 0.5f;
                    v.Add(c - a - b); v.Add(c + a - b); v.Add(c + a + b); v.Add(c - a + b);
                    for (var k = 0; k < 4; k++)
                        n.Add(d);
                    t.Add(s); t.Add(s + 1); t.Add(s + 2);
                    t.Add(s); t.Add(s + 2); t.Add(s + 3);
                }

                return Fix(Make("Box", v, n, t));
            }

            public static Mesh Sphere(int seg, int rings)
            {
                var v = new List<Vector3>();
                var n = new List<Vector3>();
                var t = new List<int>();
                for (var y = 0; y <= rings; y++)
                {
                    var phi = y / (float)rings * Mathf.PI;
                    for (var x = 0; x <= seg; x++)
                    {
                        var th = x / (float)seg * Mathf.PI * 2f;
                        var d = new Vector3(Mathf.Sin(phi) * Mathf.Cos(th), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(th));
                        v.Add(d * 0.5f);
                        n.Add(d);
                    }
                }

                for (var y = 0; y < rings; y++)
                    for (var x = 0; x < seg; x++)
                    {
                        var a = y * (seg + 1) + x;
                        var b = a + seg + 1;
                        t.Add(a); t.Add(a + 1); t.Add(b);
                        t.Add(a + 1); t.Add(b + 1); t.Add(b);
                    }

                return Fix(Make("Sphere", v, n, t));
            }

            /// <summary>An extruded flat outline (XY, front toward +Z, depth toward −Z), ear-clipped (handles concave outlines).</summary>
            public static Mesh Polygon(Vector2[] outline, float depth)
            {
                var v = new List<Vector3>();
                var n = new List<Vector3>();
                var t = new List<int>();
                foreach (var p in outline)
                {
                    v.Add(new Vector3(p.x, p.y, 0f));
                    n.Add(Vector3.forward);
                }

                foreach (var i in EarClip(outline))
                    t.Add(i);
                for (var i = 0; i < outline.Length; i++)
                {
                    var a = outline[i];
                    var b = outline[(i + 1) % outline.Length];
                    var e = (b - a).normalized;
                    var side = new Vector3(e.y, -e.x, 0f);
                    if (SignedArea(outline) < 0f)
                        side = -side;
                    var s = v.Count;
                    v.Add(new Vector3(a.x, a.y, 0f)); v.Add(new Vector3(b.x, b.y, 0f));
                    v.Add(new Vector3(a.x, a.y, -depth)); v.Add(new Vector3(b.x, b.y, -depth));
                    for (var k = 0; k < 4; k++)
                        n.Add(side);
                    t.Add(s); t.Add(s + 2); t.Add(s + 1);
                    t.Add(s + 1); t.Add(s + 2); t.Add(s + 3);
                }

                return Fix(Make("Poly", v, n, t));
            }

            private static float SignedArea(Vector2[] o)
            {
                var a = 0f;
                for (var i = 0; i < o.Length; i++)
                {
                    var p = o[i];
                    var q = o[(i + 1) % o.Length];
                    a += p.x * q.y - q.x * p.y;
                }

                return a * 0.5f;
            }

            private static List<int> EarClip(Vector2[] o)
            {
                var idx = new List<int>();
                for (var i = 0; i < o.Length; i++)
                    idx.Add(i);
                var ccw = SignedArea(o) > 0f;
                var res = new List<int>();
                var guard = 0;
                while (idx.Count > 3 && guard++ < 1000)
                {
                    var clipped = false;
                    for (var i = 0; i < idx.Count; i++)
                    {
                        var ia = idx[(i + idx.Count - 1) % idx.Count];
                        var ib = idx[i];
                        var ic = idx[(i + 1) % idx.Count];
                        var a = o[ia];
                        var b = o[ib];
                        var c = o[ic];
                        var cross = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
                        if (ccw ? cross <= 0f : cross >= 0f)
                            continue; // reflex
                        var inside = false;
                        foreach (var j in idx)
                        {
                            if (j == ia || j == ib || j == ic)
                                continue;
                            if (InTri(o[j], a, b, c))
                            {
                                inside = true;
                                break;
                            }
                        }

                        if (inside)
                            continue;
                        res.Add(ia); res.Add(ib); res.Add(ic);
                        idx.RemoveAt(i);
                        clipped = true;
                        break;
                    }

                    if (!clipped)
                        break;
                }

                if (idx.Count == 3)
                {
                    res.Add(idx[0]); res.Add(idx[1]); res.Add(idx[2]);
                }

                return res;
            }

            private static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
            {
                float S(Vector2 p1, Vector2 p2, Vector2 p3) => (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
                var d1 = S(p, a, b);
                var d2 = S(p, b, c);
                var d3 = S(p, c, a);
                var neg = d1 < 0 || d2 < 0 || d3 < 0;
                var pos = d1 > 0 || d2 > 0 || d3 > 0;
                return !(neg && pos);
            }

            public static Vector2[] StarOutline(float r0, float r1, int points)
            {
                var o = new Vector2[points * 2];
                for (var i = 0; i < o.Length; i++)
                {
                    var a = Mathf.PI * 0.5f + i * Mathf.PI / points;
                    var r = i % 2 == 0 ? r0 : r1;
                    o[i] = new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
                }

                return o;
            }

            private static Vector2[] GearOutline(float r0, float r1, int teeth)
            {
                var o = new List<Vector2>();
                for (var i = 0; i < teeth; i++)
                {
                    var a = i * Mathf.PI * 2f / teeth;
                    var w = Mathf.PI / teeth * 0.5f;
                    o.Add(new Vector2(Mathf.Cos(a - w), Mathf.Sin(a - w)) * r1);
                    o.Add(new Vector2(Mathf.Cos(a - w * 0.6f), Mathf.Sin(a - w * 0.6f)) * r0);
                    o.Add(new Vector2(Mathf.Cos(a + w * 0.6f), Mathf.Sin(a + w * 0.6f)) * r0);
                    o.Add(new Vector2(Mathf.Cos(a + w), Mathf.Sin(a + w)) * r1);
                }

                return o.ToArray();
            }

            private static Vector2[] HeartOutline(float s)
            {
                var o = new List<Vector2>();
                for (var i = 0; i < 20; i++)
                {
                    var t = i / 20f * Mathf.PI * 2f;
                    var x = 16f * Mathf.Pow(Mathf.Sin(t), 3f);
                    var y = 13f * Mathf.Cos(t) - 5f * Mathf.Cos(2f * t) - 2f * Mathf.Cos(3f * t) - Mathf.Cos(4f * t);
                    o.Add(new Vector2(x, y) * (s / 16f));
                }

                return o.ToArray();
            }

            private static Vector2[] FlameOutline(float s)
            {
                return new[]
                {
                    new Vector2(0f, 1f), new Vector2(0.25f, 0.55f), new Vector2(0.45f, 0.7f), new Vector2(0.55f, 0.15f),
                    new Vector2(0.4f, -0.45f), new Vector2(0f, -0.65f), new Vector2(-0.4f, -0.45f), new Vector2(-0.55f, 0.05f),
                    new Vector2(-0.35f, 0.45f), new Vector2(-0.2f, 0.2f),
                }.Select(p => p * s);
            }

            private static Vector2[] MoonOutline(float s)
            {
                var o = new List<Vector2>();
                for (var i = 0; i <= 10; i++)
                {
                    var a = Mathf.Lerp(-140f, 140f, i / 10f) * Mathf.Deg2Rad;
                    o.Add(new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * s);
                }

                for (var i = 10; i >= 0; i--)
                {
                    var a = Mathf.Lerp(-110f, 110f, i / 10f) * Mathf.Deg2Rad;
                    o.Add(new Vector2(Mathf.Cos(a) * 0.7f + 0.38f, Mathf.Sin(a) * 0.78f) * s);
                }

                return o.ToArray();
            }

            private static Mesh Make(string name, List<Vector3> v, List<Vector3> n, List<int> t)
            {
                var m = new Mesh { name = name };
                m.SetVertices(v);
                m.SetNormals(n);
                m.SetTriangles(t, 0);
                m.RecalculateBounds();
                return m;
            }

            /// <summary>Makes every triangle's winding agree with its vertex normals (Unity: clockwise front).</summary>
            private static Mesh Fix(Mesh m)
            {
                var v = m.vertices;
                var n = m.normals;
                var t = m.triangles;
                for (var i = 0; i < t.Length; i += 3)
                {
                    var fn = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]);
                    if (Vector3.Dot(fn, n[t[i]] + n[t[i + 1]] + n[t[i + 2]]) < 0f)
                        (t[i + 1], t[i + 2]) = (t[i + 2], t[i + 1]);
                }

                m.triangles = t;
                return m;
            }
        }
    }

    internal static class Vec2Ext
    {
        public static Vector2[] Select(this Vector2[] a, System.Func<Vector2, Vector2> f)
        {
            var r = new Vector2[a.Length];
            for (var i = 0; i < a.Length; i++)
                r[i] = f(a[i]);
            return r;
        }
    }
}
