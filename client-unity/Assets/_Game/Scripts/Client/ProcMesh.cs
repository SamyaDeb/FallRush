using UnityEngine;

namespace FallRush.Client
{
    /// <summary>
    /// Procedural meshes and the screen-face glyph atlas for Rushling v1 (doc 17 §2), generated once at start-up and
    /// shared by every Rushling. Original shapes only (capsule halves, tori, a lightning-bolt plate, a 5-point star).
    /// </summary>
    public static class ProcMesh
    {
        // Rushling body profile (doc 17 §2.1): radius 0.31, top at 1.0, bottom of the shell at 0.08 (peg feet below it),
        // so the whole figure stays inside the 0.31 × 1.0 collision capsule.
        public const float R = 0.31f;
        public const float BodyBottom = 0.08f;
        public const float LowerCenter = BodyBottom + R;   // 0.39
        public const float UpperCenter = 1f - R;           // 0.69
        public const float SeamY = 0.54f;

        private static Mesh _thinTorus;
        private static Mesh _dome, _shell, _torus, _bolt, _star, _sphere, _cylinder, _cube, _face;
        private static Texture2D _atlas;

        /// <summary>Upper half: cylinder SeamY → UpperCenter, hemisphere to 1.0. Origin at the feet.</summary>
        public static Mesh Dome => _dome != null ? _dome : _dome = BuildHalf(top: true);

        /// <summary>Lower half: cylinder SeamY → LowerCenter, hemisphere down to BodyBottom.</summary>
        public static Mesh Shell => _shell != null ? _shell : _shell = BuildHalf(top: false);

        /// <summary>Unit torus (major radius 1, minor 0.12) in the XZ plane; scale it.</summary>
        public static Mesh Torus => _torus != null ? _torus : _torus = BuildTorus(1f, 0.12f, 24, 8);

        /// <summary>Unit torus with a thin tube (minor 0.05): seam and strap rings.</summary>
        public static Mesh ThinTorus => _thinTorus != null ? _thinTorus : _thinTorus = BuildTorus(1f, 0.05f, 32, 6);

        public static Mesh Bolt => _bolt != null ? _bolt : _bolt = BuildBolt();
        public static Mesh Star => _star != null ? _star : _star = BuildStar();
        public static Mesh Sphere => _sphere != null ? _sphere : _sphere = Builtin(PrimitiveType.Sphere);
        public static Mesh Cylinder => _cylinder != null ? _cylinder : _cylinder = Builtin(PrimitiveType.Cylinder);
        public static Mesh Cube => _cube != null ? _cube : _cube = Builtin(PrimitiveType.Cube);

        /// <summary>Screen patch on the front of the dome (follows the capsule surface), UV 0..1.</summary>
        public static Mesh Face => _face != null ? _face : _face = BuildFace();

        /// <summary>Body radius at height y (capsule surface).</summary>
        public static float RadiusAt(float y)
        {
            if (y > UpperCenter)
            {
                var d = y - UpperCenter;
                return Mathf.Sqrt(Mathf.Max(0f, R * R - d * d));
            }

            if (y < LowerCenter)
            {
                var d = LowerCenter - y;
                return Mathf.Sqrt(Mathf.Max(0f, R * R - d * d));
            }

            return R;
        }

        private static Mesh Builtin(PrimitiveType t)
        {
            var go = GameObject.CreatePrimitive(t);
            var m = go.GetComponent<MeshFilter>().sharedMesh;
            Object.Destroy(go);
            return m;
        }

        // ------------------------------------------------------------------ lathe

        private static Mesh BuildHalf(bool top)
        {
            const int sides = 28;
            const int capRings = 8;
            var profile = new System.Collections.Generic.List<Vector2>(); // (radius, y), from the seam outward to the pole
            profile.Add(new Vector2(R, SeamY));
            if (top)
            {
                profile.Add(new Vector2(R, UpperCenter));
                for (var i = 1; i <= capRings; i++)
                {
                    var a = i / (float)capRings * Mathf.PI * 0.5f;
                    profile.Add(new Vector2(R * Mathf.Cos(a), UpperCenter + R * Mathf.Sin(a)));
                }
            }
            else
            {
                profile.Add(new Vector2(R, LowerCenter));
                for (var i = 1; i <= capRings; i++)
                {
                    var a = i / (float)capRings * Mathf.PI * 0.5f;
                    profile.Add(new Vector2(R * Mathf.Cos(a), LowerCenter - R * Mathf.Sin(a)));
                }
            }

            return Lathe(profile, sides, flip: !top);
        }

        private static Mesh Lathe(System.Collections.Generic.List<Vector2> profile, int sides, bool flip)
        {
            var rings = profile.Count;
            var verts = new Vector3[rings * (sides + 1)];
            var normals = new Vector3[verts.Length];
            var uv = new Vector2[verts.Length];
            for (var r = 0; r < rings; r++)
            {
                var p = profile[r];
                var prev = profile[Mathf.Max(0, r - 1)];
                var next = profile[Mathf.Min(rings - 1, r + 1)];
                var tangent = (next - prev).normalized;
                var n2 = new Vector2(tangent.y, -tangent.x); // outward for an upward profile
                if (flip)
                    n2 = -n2;
                if (n2.x < 0f)
                    n2 = -n2;
                for (var s = 0; s <= sides; s++)
                {
                    var a = s / (float)sides * Mathf.PI * 2f;
                    float sin = Mathf.Sin(a), cos = Mathf.Cos(a);
                    var i = r * (sides + 1) + s;
                    verts[i] = new Vector3(p.x * sin, p.y, p.x * cos);
                    normals[i] = new Vector3(n2.x * sin, n2.y, n2.x * cos).normalized;
                    uv[i] = new Vector2(s / (float)sides, r / (float)(rings - 1));
                }
            }

            var tris = new int[(rings - 1) * sides * 6];
            var t = 0;
            for (var r = 0; r < rings - 1; r++)
            {
                for (var s = 0; s < sides; s++)
                {
                    var a = r * (sides + 1) + s;
                    var b = a + sides + 1;
                    // Unity front faces: cross(v1 − v0, v2 − v0) points outward.
                    if (!flip)
                    {
                        tris[t++] = a; tris[t++] = a + 1; tris[t++] = b;
                        tris[t++] = a + 1; tris[t++] = b + 1; tris[t++] = b;
                    }
                    else
                    {
                        tris[t++] = a; tris[t++] = b; tris[t++] = a + 1;
                        tris[t++] = a + 1; tris[t++] = b; tris[t++] = b + 1;
                    }
                }
            }

            var m = new Mesh { name = "Lathe" };
            m.vertices = verts;
            m.normals = normals;
            m.uv = uv;
            m.triangles = tris;
            m.RecalculateBounds();
            return m;
        }

        private static Mesh BuildTorus(float major, float minor, int segs, int sides)
        {
            var verts = new Vector3[(segs + 1) * (sides + 1)];
            var normals = new Vector3[verts.Length];
            for (var i = 0; i <= segs; i++)
            {
                var u = i / (float)segs * Mathf.PI * 2f;
                var c = new Vector3(Mathf.Sin(u), 0f, Mathf.Cos(u));
                for (var j = 0; j <= sides; j++)
                {
                    var v = j / (float)sides * Mathf.PI * 2f;
                    var n = c * Mathf.Cos(v) + Vector3.up * Mathf.Sin(v);
                    verts[i * (sides + 1) + j] = c * major + n * minor;
                    normals[i * (sides + 1) + j] = n;
                }
            }

            var tris = new int[segs * sides * 6];
            var t = 0;
            for (var i = 0; i < segs; i++)
            {
                for (var j = 0; j < sides; j++)
                {
                    var a = i * (sides + 1) + j;
                    var b = a + sides + 1;
                    tris[t++] = a; tris[t++] = b; tris[t++] = a + 1;
                    tris[t++] = a + 1; tris[t++] = b; tris[t++] = b + 1;
                }
            }

            var m = new Mesh { name = "Torus" };
            m.vertices = verts;
            m.normals = normals;
            m.triangles = tris;
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Extruded flat polygon (XY outline, depth along −Z..0); front face toward +Z.</summary>
        private static Mesh Extrude(Vector2[] outline, int[] frontTris, float depth, string name)
        {
            var n = outline.Length;
            var verts = new System.Collections.Generic.List<Vector3>();
            var normals = new System.Collections.Generic.List<Vector3>();
            var tris = new System.Collections.Generic.List<int>();
            for (var i = 0; i < n; i++)
            {
                verts.Add(new Vector3(outline[i].x, outline[i].y, 0f));
                normals.Add(Vector3.forward);
            }

            // frontTris are counter-clockwise in XY, i.e. their normal is +Z = the front.
            for (var i = 0; i < frontTris.Length; i++)
                tris.Add(frontTris[i]);

            for (var i = 0; i < n; i++)
            {
                var a = outline[i];
                var b = outline[(i + 1) % n];
                var e = (b - a).normalized;
                var side = new Vector3(e.y, -e.x, 0f);
                var s = verts.Count;
                verts.Add(new Vector3(a.x, a.y, 0f));
                verts.Add(new Vector3(b.x, b.y, 0f));
                verts.Add(new Vector3(a.x, a.y, -depth));
                verts.Add(new Vector3(b.x, b.y, -depth));
                for (var k = 0; k < 4; k++)
                    normals.Add(side);
                tris.Add(s); tris.Add(s + 2); tris.Add(s + 1);
                tris.Add(s + 1); tris.Add(s + 2); tris.Add(s + 3);
            }

            var m = new Mesh { name = name };
            m.SetVertices(verts);
            m.SetNormals(normals);
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return m;
        }

        private static Mesh BuildBolt()
        {
            // Our own zig-zag bolt (8 points), ~0.12 × 0.2 m.
            var o = new[]
            {
                new Vector2(0.02f, 0.10f), new Vector2(0.06f, 0.10f), new Vector2(0.01f, 0.01f), new Vector2(0.05f, 0.01f),
                new Vector2(-0.05f, -0.11f), new Vector2(-0.01f, -0.02f), new Vector2(-0.05f, -0.02f), new Vector2(-0.01f, 0.10f),
            };
            // Triangulate as two quads + connecting triangles (the outline is not convex).
            var tris = new[] { 0, 1, 2, 0, 2, 7, 7, 2, 6, 6, 2, 5, 2, 3, 5, 5, 3, 4 };
            for (var i = 0; i < tris.Length; i += 3)
            {
                // make each triangle counter-clockwise in XY
                var a = o[tris[i]];
                var b = o[tris[i + 1]];
                var c = o[tris[i + 2]];
                if ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x) < 0f)
                    (tris[i + 1], tris[i + 2]) = (tris[i + 2], tris[i + 1]);
            }

            return Extrude(o, tris, 0.015f, "Bolt");
        }

        private static Mesh BuildStar()
        {
            var o = new Vector2[10];
            for (var i = 0; i < 10; i++)
            {
                var a = Mathf.PI * 0.5f + i * Mathf.PI / 5f;
                var r = i % 2 == 0 ? 0.06f : 0.026f;
                o[i] = new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
            }

            // Fan from the inner pentagon's centre is not available without a centre vertex: use the 5 inner points + tips.
            var tris = new System.Collections.Generic.List<int>();
            for (var i = 0; i < 5; i++)
            {
                tris.Add(i * 2); tris.Add((i * 2 + 1) % 10); tris.Add((i * 2 + 9) % 10); // tip
            }

            tris.Add(1); tris.Add(3); tris.Add(5);
            tris.Add(1); tris.Add(5); tris.Add(7);
            tris.Add(1); tris.Add(7); tris.Add(9);
            var arr = tris.ToArray();
            for (var i = 0; i < arr.Length; i += 3)
            {
                var a = o[arr[i]];
                var b = o[arr[i + 1]];
                var c = o[arr[i + 2]];
                if ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x) < 0f)
                    (arr[i + 1], arr[i + 2]) = (arr[i + 2], arr[i + 1]);
            }

            return Extrude(o, arr, 0.02f, "Star");
        }

        private static Mesh BuildFace()
        {
            // Patch over ±38° of the front, y 0.60 → 0.86, lifted 4 mm off the dome.
            const int cols = 10, rows = 6;
            const float span = 38f * Mathf.Deg2Rad;
            const float y0 = 0.60f, y1 = 0.86f, lift = 0.004f;
            var verts = new Vector3[(cols + 1) * (rows + 1)];
            var normals = new Vector3[verts.Length];
            var uv = new Vector2[verts.Length];
            for (var r = 0; r <= rows; r++)
            {
                var v = r / (float)rows;
                for (var c = 0; c <= cols; c++)
                {
                    var u = c / (float)cols;
                    // Rounded-square outline: pull the grid onto a squircle (|x|⁴ + |y|⁴ ≤ 1), so the screen's corners
                    // are geometry, not texture (unlit corner texels never match the lit dome).
                    float qx = u * 2f - 1f, qy = v * 2f - 1f;
                    var k = Mathf.Max(Mathf.Abs(qx), Mathf.Abs(qy));
                    var n4 = Mathf.Pow(qx * qx * qx * qx + qy * qy * qy * qy, 0.25f);
                    if (n4 > 1e-5f)
                    {
                        qx *= k / n4;
                        qy *= k / n4;
                    }

                    var y = Mathf.Lerp(y0, y1, (qy + 1f) * 0.5f);
                    var rad = RadiusAt(y) + lift;
                    var a = Mathf.Lerp(-span, span, (qx + 1f) * 0.5f);
                    var i = r * (cols + 1) + c;
                    verts[i] = new Vector3(Mathf.Sin(a) * rad, y, Mathf.Cos(a) * rad);
                    var ny = y > UpperCenter ? (y - UpperCenter) / R : 0f;
                    normals[i] = new Vector3(Mathf.Sin(a) * (1f - ny), ny, Mathf.Cos(a) * (1f - ny)).normalized;
                    uv[i] = new Vector2(1f - u, v); // seen from the front, −X is on the right
                }
            }

            var tris = new int[cols * rows * 6];
            var t = 0;
            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < cols; c++)
                {
                    var a = r * (cols + 1) + c;
                    var b = a + cols + 1;
                    tris[t++] = a; tris[t++] = a + 1; tris[t++] = b;
                    tris[t++] = a + 1; tris[t++] = b + 1; tris[t++] = b;
                }
            }

            var m = new Mesh { name = "Face" };
            m.vertices = verts;
            m.normals = normals;
            m.uv = uv;
            m.triangles = tris;
            m.RecalculateBounds();
            return m;
        }

        // ------------------------------------------------------------------ face atlas

        public const int AtlasCell = 64;
        public enum Glyph
        {
            Idle = 0,      // ^ ^
            Running = 1,   // • •
            Jumping = 2,   // O O
            Diving = 3,    // > <
            Stunned = 4,   // @ @ (spirals)
            Falling = 5,   // O ◡ O (panic)
            Blink = 6,     // – –
            Happy = 7,     // ^‿^ (emotes)
            Sad = 8,       // x x with tears (eliminated, doc 17 §2.2)
            Qualified = 9, // ★ ★
            Champion = 10, // a crown across the screen (our own shape)
            Wink = 11,     // ^ – with a smile
            Angry = 12,    // slanted brows
            Love = 13,     // ♥ ♥
            Sleepy = 14,   // ︶ ︶ z
            Smug = 15,     // half-lidded with a side smile
        }

        /// <summary>
        /// G2.2: 4×4 atlas of 64 px cells (crisp at 1080p): white anti-aliased LED glyphs with a soft glow in the alpha
        /// channel over a transparent cell; the toon shader draws the dark screen and tints the glyph per face theme.
        /// </summary>
        public static Texture2D FaceAtlas => _atlas != null ? _atlas : _atlas = BuildAtlas();

        /// <summary>Face atlas ST for a glyph (scale.xy, offset.xy).</summary>
        public static Vector4 GlyphST(Glyph g)
        {
            var i = (int)g;
            var col = i % 4;
            var row = 3 - i / 4; // texture rows start at the bottom
            return new Vector4(0.25f, 0.25f, col * 0.25f, row * 0.25f);
        }

        // Glyph shapes as distance fields in cell units (0..1, y up). Each returns the distance to the shape's ink.
        private delegate float Sdf(Vector2 p);

        private static float Seg(Vector2 p, Vector2 a, Vector2 b, float w)
        {
            var pa = p - a;
            var ba = b - a;
            var h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Mathf.Max(1e-6f, Vector2.Dot(ba, ba)));
            return (pa - ba * h).magnitude - w;
        }

        private static float DiscD(Vector2 p, Vector2 c, float r) => (p - c).magnitude - r;
        private static float RingD(Vector2 p, Vector2 c, float r, float w) => Mathf.Abs((p - c).magnitude - r) - w;

        private static float Arc(Vector2 p, Vector2 c, float r, float a0, float a1, float w)
        {
            var d = p - c;
            var a = Mathf.Atan2(d.y, d.x);
            if (a < a0) a += Mathf.PI * 2f;
            if (a >= a0 && a <= a1)
                return Mathf.Abs(d.magnitude - r) - w;
            var e0 = c + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r;
            var e1 = c + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * r;
            return Mathf.Min((p - e0).magnitude, (p - e1).magnitude) - w;
        }

        private static float StarD(Vector2 p, Vector2 c, float r)
        {
            // Inside test against a 5-point star polygon; distance approximated by the nearest edge.
            var q = new Vector2[10];
            for (var i = 0; i < 10; i++)
            {
                var a = Mathf.PI * 0.5f + i * Mathf.PI / 5f;
                var rr = i % 2 == 0 ? r : r * 0.45f;
                q[i] = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rr;
            }

            return PolyD(p, q);
        }

        private static float HeartD(Vector2 p, Vector2 c, float s)
        {
            var q = new Vector2[24];
            for (var i = 0; i < q.Length; i++)
            {
                var t = i / (float)q.Length * Mathf.PI * 2f;
                var x = 16f * Mathf.Pow(Mathf.Sin(t), 3f);
                var y = 13f * Mathf.Cos(t) - 5f * Mathf.Cos(2f * t) - 2f * Mathf.Cos(3f * t) - Mathf.Cos(4f * t);
                q[i] = c + new Vector2(x, y) * (s / 16f);
            }

            return PolyD(p, q);
        }

        private static float PolyD(Vector2 p, Vector2[] q)
        {
            var d = float.MaxValue;
            var inside = false;
            for (int i = 0, j = q.Length - 1; i < q.Length; j = i++)
            {
                d = Mathf.Min(d, Seg(p, q[j], q[i], 0f));
                if ((q[i].y > p.y) != (q[j].y > p.y) && p.x < (q[j].x - q[i].x) * (p.y - q[i].y) / (q[j].y - q[i].y) + q[i].x)
                    inside = !inside;
            }

            return inside ? -d : d;
        }

        private static float Min(params float[] d)
        {
            var m = float.MaxValue;
            foreach (var x in d)
                m = Mathf.Min(m, x);
            return m;
        }

        private const float W = 0.045f; // LED stroke half-width (cell units)
        private static readonly Vector2 L = new Vector2(0.31f, 0.56f), Rr = new Vector2(0.69f, 0.56f);

        private static float Chevron(Vector2 p, Vector2 c, bool up) => Min(
            Seg(p, c + new Vector2(-0.1f, up ? -0.06f : 0.06f), c + new Vector2(0f, up ? 0.06f : -0.06f), W),
            Seg(p, c + new Vector2(0f, up ? 0.06f : -0.06f), c + new Vector2(0.1f, up ? -0.06f : 0.06f), W));

        private static float Cross(Vector2 p, Vector2 c) => Min(
            Seg(p, c + new Vector2(-0.08f, -0.08f), c + new Vector2(0.08f, 0.08f), W),
            Seg(p, c + new Vector2(-0.08f, 0.08f), c + new Vector2(0.08f, -0.08f), W));

        private static float Spiral(Vector2 p, Vector2 c)
        {
            var d = p - c;
            var r = d.magnitude;
            var a = Mathf.Atan2(d.y, d.x);
            var best = float.MaxValue;
            for (var k = 0; k < 3; k++)
            {
                var rr = 0.025f * (a + Mathf.PI + k * Mathf.PI * 2f) / Mathf.PI;
                if (rr < 0.13f)
                    best = Mathf.Min(best, Mathf.Abs(r - rr));
            }

            return Mathf.Min(best - W * 0.8f, r > 0.14f ? r - 0.14f : float.MaxValue);
        }

        private static float SmileD(Vector2 p, float w = 0.11f) => Arc(p, new Vector2(0.5f, 0.36f), w, -Mathf.PI * 0.85f, -Mathf.PI * 0.15f, W * 0.85f);

        private static float GlyphD(int g, Vector2 p)
        {
            switch ((Glyph)g)
            {
                case Glyph.Idle: return Min(Chevron(p, L, true), Chevron(p, Rr, true));
                case Glyph.Running: return Min(DiscD(p, L, 0.075f), DiscD(p, Rr, 0.075f));
                case Glyph.Jumping: return Min(RingD(p, L, 0.085f, W * 0.8f), RingD(p, Rr, 0.085f, W * 0.8f));
                case Glyph.Diving: return Min(Seg(p, L + new Vector2(-0.08f, 0.08f), L + new Vector2(0.07f, 0f), W), Seg(p, L + new Vector2(0.07f, 0f), L + new Vector2(-0.08f, -0.08f), W),
                    Seg(p, Rr + new Vector2(0.08f, 0.08f), Rr + new Vector2(-0.07f, 0f), W), Seg(p, Rr + new Vector2(-0.07f, 0f), Rr + new Vector2(0.08f, -0.08f), W));
                case Glyph.Stunned: return Min(Spiral(p, L), Spiral(p, Rr));
                case Glyph.Falling: return Min(RingD(p, L + new Vector2(0f, 0.04f), 0.08f, W * 0.8f), RingD(p, Rr + new Vector2(0f, 0.04f), 0.08f, W * 0.8f),
                    RingD(p, new Vector2(0.5f, 0.3f), 0.055f, W * 0.8f));
                case Glyph.Blink: return Min(Seg(p, L + new Vector2(-0.09f, 0f), L + new Vector2(0.09f, 0f), W), Seg(p, Rr + new Vector2(-0.09f, 0f), Rr + new Vector2(0.09f, 0f), W));
                case Glyph.Happy: return Min(Chevron(p, L + new Vector2(0f, 0.04f), true), Chevron(p, Rr + new Vector2(0f, 0.04f), true), SmileD(p));
                case Glyph.Sad: return Min(Cross(p, L + new Vector2(0f, 0.04f)), Cross(p, Rr + new Vector2(0f, 0.04f)),
                    Seg(p, L + new Vector2(0f, -0.08f), L + new Vector2(0f, -0.2f), W * 0.7f), Seg(p, Rr + new Vector2(0f, -0.08f), Rr + new Vector2(0f, -0.2f), W * 0.7f));
                case Glyph.Qualified: return Min(StarD(p, L, 0.13f), StarD(p, Rr, 0.13f));
                case Glyph.Champion:
                {
                    var crown = new[]
                    {
                        new Vector2(0.2f, 0.36f), new Vector2(0.8f, 0.36f), new Vector2(0.84f, 0.7f), new Vector2(0.67f, 0.52f), new Vector2(0.5f, 0.76f),
                        new Vector2(0.33f, 0.52f), new Vector2(0.16f, 0.7f),
                    };
                    return Mathf.Max(PolyD(p, crown), -DiscD(p, new Vector2(0.5f, 0.45f), 0.045f));
                }
                case Glyph.Wink: return Min(Chevron(p, L + new Vector2(0f, 0.04f), true), Seg(p, Rr + new Vector2(-0.09f, 0.04f), Rr + new Vector2(0.09f, 0.04f), W), SmileD(p));
                case Glyph.Angry: return Min(Seg(p, L + new Vector2(-0.1f, 0.1f), L + new Vector2(0.09f, 0.02f), W), Seg(p, Rr + new Vector2(-0.09f, 0.02f), Rr + new Vector2(0.1f, 0.1f), W),
                    DiscD(p, L + new Vector2(0.02f, -0.07f), 0.04f), DiscD(p, Rr + new Vector2(-0.02f, -0.07f), 0.04f),
                    Arc(p, new Vector2(0.5f, 0.2f), 0.09f, Mathf.PI * 0.2f, Mathf.PI * 0.8f, W * 0.8f));
                case Glyph.Love: return Min(HeartD(p, L, 0.12f), HeartD(p, Rr, 0.12f));
                case Glyph.Sleepy: return Min(Arc(p, L + new Vector2(0f, 0.06f), 0.09f, -Mathf.PI * 0.9f, -Mathf.PI * 0.1f, W * 0.8f), Arc(p, Rr + new Vector2(0f, 0.06f), 0.09f, -Mathf.PI * 0.9f, -Mathf.PI * 0.1f, W * 0.8f),
                    Seg(p, new Vector2(0.72f, 0.86f), new Vector2(0.84f, 0.86f), W * 0.6f), Seg(p, new Vector2(0.84f, 0.86f), new Vector2(0.72f, 0.74f), W * 0.6f), Seg(p, new Vector2(0.72f, 0.74f), new Vector2(0.84f, 0.74f), W * 0.6f));
                case Glyph.Smug: return Min(Seg(p, L + new Vector2(-0.09f, 0f), L + new Vector2(0.09f, 0f), W), Seg(p, Rr + new Vector2(-0.09f, 0f), Rr + new Vector2(0.09f, 0f), W),
                    DiscD(p, L + new Vector2(0.03f, -0.05f), 0.04f), DiscD(p, Rr + new Vector2(0.03f, -0.05f), 0.04f),
                    Arc(p, new Vector2(0.58f, 0.36f), 0.09f, -Mathf.PI * 0.75f, -Mathf.PI * 0.1f, W * 0.8f));
                default: return 1f;
            }
        }

        private static Texture2D BuildAtlas()
        {
            const int size = AtlasCell * 4;
            var px = new Color32[size * size];
            const float pxu = 1f / AtlasCell;
            for (var g = 0; g < 16; g++)
            {
                var ox = (g % 4) * AtlasCell;
                var oy = (3 - g / 4) * AtlasCell;
                for (var y = 0; y < AtlasCell; y++)
                    for (var x = 0; x < AtlasCell; x++)
                    {
                        var p = new Vector2((x + 0.5f) * pxu, (y + 0.5f) * pxu);
                        var d = GlyphD(g, p);
                        var ink = Mathf.Clamp01(0.5f - d / pxu);              // anti-aliased stroke
                        var glow = Mathf.Clamp01(1f - Mathf.Max(0f, d) / 0.06f) * 0.35f; // soft LED glow
                        var a = Mathf.Max(ink, glow);
                        var edge = x < 1 || y < 1 || x >= AtlasCell - 1 || y >= AtlasCell - 1;
                        var v = (byte)Mathf.RoundToInt(255f * Mathf.Lerp(0.75f, 1f, ink));
                        px[(oy + y) * size + ox + x] = edge ? new Color32(0, 0, 0, 0) : new Color32(v, v, v, (byte)Mathf.RoundToInt(a * 255f));
                    }
            }

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "FaceAtlas", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }
    }
}
