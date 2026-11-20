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

        public const int AtlasCell = 32;
        public enum Glyph
        {
            Idle = 0,      // ^ ^
            Running = 1,   // • •
            Jumping = 2,   // O O
            Diving = 3,    // > <
            Stunned = 4,   // @ @ (spirals)
            Falling = 5,   // O ◡ O (panic)
            Blink = 6,     // – –
        }

        /// <summary>4×4 atlas of 32 px cells: dark screen with mint LED glyphs (the face mesh provides the rounded outline).</summary>
        public static Texture2D FaceAtlas => _atlas != null ? _atlas : _atlas = BuildAtlas();

        /// <summary>_BaseMap_ST for a glyph (scale.xy, offset.xy).</summary>
        public static Vector4 GlyphST(Glyph g)
        {
            var i = (int)g;
            var col = i % 4;
            var row = 3 - i / 4; // texture rows start at the bottom
            return new Vector4(0.25f, 0.25f, col * 0.25f, row * 0.25f);
        }

        private static Texture2D BuildAtlas()
        {
            const int size = AtlasCell * 4;
            var px = new Color32[size * size];
            var white = new Color32(255, 255, 255, 255);
            var screen = new Color32(27, 16, 53, 255);   // dark ink screen
            var glyph = new Color32(91, 227, 181, 255);  // mint LED (doc 17 face theme "green")
            for (var i = 0; i < px.Length; i++)
                px[i] = white;

            for (var g = 0; g < 16; g++)
            {
                var ox = (g % 4) * AtlasCell;
                var oy = (3 - g / 4) * AtlasCell;
                // Whole cell is screen (the face mesh itself is the rounded shape).
                for (var y = 0; y < AtlasCell; y++)
                    for (var x = 0; x < AtlasCell; x++)
                        px[(oy + y) * size + ox + x] = screen;

                if (g > (int)Glyph.Blink)
                    continue;
                void Dot(float cx, float cy, float r) => Disc(px, size, ox, oy, cx, cy, r, glyph);
                void Line(float x0, float y0, float x1, float y1, float w) => Seg(px, size, ox, oy, x0, y0, x1, y1, w, glyph);
                void Ring(float cx, float cy, float r, float w) => RingPx(px, size, ox, oy, cx, cy, r, w, glyph);
                const float lx = 10.5f, rx = 21.5f, ey = 18f;
                switch ((Glyph)g)
                {
                    case Glyph.Idle:
                        Line(lx - 3.5f, ey - 2f, lx, ey + 2f, 1.6f); Line(lx, ey + 2f, lx + 3.5f, ey - 2f, 1.6f);
                        Line(rx - 3.5f, ey - 2f, rx, ey + 2f, 1.6f); Line(rx, ey + 2f, rx + 3.5f, ey - 2f, 1.6f);
                        break;
                    case Glyph.Running:
                        Dot(lx, ey, 2.6f); Dot(rx, ey, 2.6f);
                        break;
                    case Glyph.Jumping:
                        Ring(lx, ey, 3.6f, 1.5f); Ring(rx, ey, 3.6f, 1.5f);
                        break;
                    case Glyph.Diving:
                        Line(lx - 3f, ey + 3f, lx + 2.5f, ey, 1.6f); Line(lx + 2.5f, ey, lx - 3f, ey - 3f, 1.6f);
                        Line(rx + 3f, ey + 3f, rx - 2.5f, ey, 1.6f); Line(rx - 2.5f, ey, rx + 3f, ey - 3f, 1.6f);
                        break;
                    case Glyph.Stunned:
                        Spiral(px, size, ox, oy, lx, ey, glyph);
                        Spiral(px, size, ox, oy, rx, ey, glyph);
                        break;
                    case Glyph.Falling:
                        Ring(lx, ey + 1f, 4f, 1.5f); Ring(rx, ey + 1f, 4f, 1.5f);
                        for (var k = 0; k < 6; k++)
                            Line(12f + k * 1.6f, 9f + (k % 2 == 0 ? 0f : 1.5f), 13.6f + k * 1.6f, 9f + (k % 2 == 0 ? 1.5f : 0f), 1.1f);
                        break;
                    case Glyph.Blink:
                        Line(lx - 3.5f, ey, lx + 3.5f, ey, 1.6f); Line(rx - 3.5f, ey, rx + 3.5f, ey, 1.6f);
                        break;
                }
            }

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "FaceAtlas", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        private static void Plot(Color32[] px, int size, int ox, int oy, int x, int y, Color32 c)
        {
            if (x < 2 || y < 2 || x >= AtlasCell - 2 || y >= AtlasCell - 2)
                return;
            px[(oy + y) * size + ox + x] = c;
        }

        private static void Disc(Color32[] px, int size, int ox, int oy, float cx, float cy, float r, Color32 c)
        {
            for (var y = 0; y < AtlasCell; y++)
                for (var x = 0; x < AtlasCell; x++)
                    if ((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy) <= r * r)
                        Plot(px, size, ox, oy, x, y, c);
        }

        private static void RingPx(Color32[] px, int size, int ox, int oy, float cx, float cy, float r, float w, Color32 c)
        {
            for (var y = 0; y < AtlasCell; y++)
            {
                for (var x = 0; x < AtlasCell; x++)
                {
                    var d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    if (Mathf.Abs(d - r) <= w * 0.5f + 0.25f)
                        Plot(px, size, ox, oy, x, y, c);
                }
            }
        }

        private static void Seg(Color32[] px, int size, int ox, int oy, float x0, float y0, float x1, float y1, float w, Color32 c)
        {
            var dx = x1 - x0;
            var dy = y1 - y0;
            var l2 = dx * dx + dy * dy;
            for (var y = 0; y < AtlasCell; y++)
            {
                for (var x = 0; x < AtlasCell; x++)
                {
                    var px0 = x + 0.5f - x0;
                    var py0 = y + 0.5f - y0;
                    var t = l2 > 0 ? Mathf.Clamp01((px0 * dx + py0 * dy) / l2) : 0f;
                    var ex = px0 - dx * t;
                    var ey = py0 - dy * t;
                    if (ex * ex + ey * ey <= w * w)
                        Plot(px, size, ox, oy, x, y, c);
                }
            }
        }

        private static void Spiral(Color32[] px, int size, int ox, int oy, float cx, float cy, Color32 c)
        {
            for (var k = 0; k < 120; k++)
            {
                var a = k / 120f * Mathf.PI * 4f;
                var r = 0.4f + k / 120f * 4f;
                var x = cx + Mathf.Cos(a) * r;
                var y = cy + Mathf.Sin(a) * r;
                Disc(px, size, ox, oy, x, y, 0.7f, c);
            }
        }
    }
}
