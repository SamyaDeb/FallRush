using System.Collections.Generic;
using FallRush.Net;
using UnityEngine;

/// <summary>
/// G4.8 chunky toy geometry for the map art pass (render only; colliders are never touched):
/// <list type="bullet">
/// <item><see cref="RoundBox"/>: a rounded box (top edges radius 0.15–0.25 m, 3 segments; per-side radii so an edge that
/// abuts a neighbour stays tight and shows no groove), optionally extended downward (the ≥ 0.6 m chunky look, never above or
/// beyond the collider top), with padded rim tubes along the exposed top edges (inside the collider footprint + ≤ 0.12 m).</item>
/// <item><see cref="RimRing"/>: the same padded rim around a round prism top.</item>
/// </list>
/// Every vertex carries both looks (decision G4.8-1): COLOR = the soft albedo, uv3 = (candy albedo, 2 + pattern + 8 × role).
/// Roles: 0 none, 1 walkable top (soft pattern), 2 side, 3 rim / padding (gloss), 4 hazard (gloss), 5 prop (soft gloss).
/// Meshes are built in metres in the object's local frame and normalised by its size (the transform's scale restores them).
/// </summary>
public static class ToyMesh
{
    public const int RoleNone = 0, RoleTop = 1, RoleSide = 2, RoleRim = 3, RoleHazard = 4, RoleProp = 5;

    /// <summary>Rim tube: radius, inset of its centre from the side face, its centre below the top (top + 0.08, outer + 0.09).</summary>
    public const float RimRadius = 0.12f, RimInset = 0.03f, RimDrop = 0.04f;

    /// <summary>An exposed stretch of a top edge: side 0 +z, 1 +x, 2 −z, 3 −x; <c>A..B</c> in metres along the edge from its start corner.</summary>
    public struct RimRun
    {
        public int Side;
        public float A, B;
    }

    public sealed class Shape
    {
        public float Radius = 0.2f;
        public float ExtraDown;
        public readonly float[] SideRadius = { -1f, -1f, -1f, -1f }; // < 0: Radius
        public readonly List<RimRun> Rims = new List<RimRun>();
        public int TopRole = RoleTop;
        public int SideRole = RoleSide;

        public string Key()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"{Radius:F2}_{ExtraDown:F2}_{TopRole}{SideRole}");
            foreach (var r in SideRadius)
                sb.Append($"_{r:F2}");
            foreach (var r in Rims)
                sb.Append($"|{r.Side}:{r.A:F1}-{r.B:F1}");
            return sb.ToString();
        }
    }

    /// <summary>Builds per-vertex lists in metres; <see cref="ToMesh"/> normalises and emits.</summary>
    public sealed class Buffers
    {
        public readonly List<Vector3> V = new List<Vector3>();
        public readonly List<Vector3> N = new List<Vector3>();
        public readonly List<Color> C = new List<Color>();
        public readonly List<Vector4> U3 = new List<Vector4>();
        public readonly List<int> T = new List<int>();

        public void Add(Vector3 v, Vector3 n, Color soft, Color candy, int role, int pattern)
        {
            V.Add(v);
            N.Add(n);
            C.Add(soft);
            U3.Add(new Vector4(candy.r, candy.g, candy.b, 2f + pattern + 8f * role));
        }

        public Mesh ToMesh(string name, Vector3 size)
        {
            var inv = new Vector3(1f / Mathf.Max(1e-4f, size.x), 1f / Mathf.Max(1e-4f, size.y), 1f / Mathf.Max(1e-4f, size.z));
            var v = new List<Vector3>(V.Count);
            var n = new List<Vector3>(N.Count);
            for (var i = 0; i < V.Count; i++)
            {
                v.Add(Vector3.Scale(V[i], inv));
                n.Add(Vector3.Scale(N[i], size).normalized); // inverse-transpose of the object scale
            }

            // Flip any triangle whose winding disagrees with its (average) normal: Unity is clockwise-front.
            for (var i = 0; i < T.Count; i += 3)
            {
                var a = V[T[i]];
                var fn = Vector3.Cross(V[T[i + 1]] - a, V[T[i + 2]] - a);
                if (Vector3.Dot(fn, N[T[i]] + N[T[i + 1]] + N[T[i + 2]]) < 0f)
                    (T[i + 1], T[i + 2]) = (T[i + 2], T[i + 1]);
            }

            var mesh = new Mesh { name = name };
            if (v.Count > 65000)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(v);
            mesh.SetNormals(n);
            mesh.SetColors(C);
            mesh.SetUVs(3, U3);
            mesh.SetTriangles(T, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    // ------------------------------------------------------------------ rounded box

    private static List<float> Coords(float lo, float hi, float rlo, float rhi, int slo, int shi)
    {
        var c = new List<float>();
        for (var k = 0; k <= slo; k++)
            c.Add(lo + rlo * k / slo);
        for (var k = 0; k <= shi; k++)
        {
            var x = hi - rhi + rhi * k / shi;
            if (x > c[c.Count - 1] + 1e-5f)
                c.Add(x);
        }

        return c;
    }

    private static int Segs(float r) => r >= 0.15f ? 3 : r >= 0.05f ? 2 : 1;

    /// <summary>
    /// A rounded box of collider size <paramref name="size"/> (centred, as the unit cube it replaces) in both looks' styles.
    /// </summary>
    public static Mesh RoundBox(Vector3 size, MapArt.Style soft, MapArt.Style candy, Shape sh)
    {
        var b = new Buffers();
        float hx = size.x * 0.5f, hy = size.y * 0.5f, hz = size.z * 0.5f;
        var yb = -hy - sh.ExtraDown;
        var height = hy - yb;
        var maxR = 0.45f * Mathf.Min(size.x, Mathf.Min(size.z, height));
        var rTop = Mathf.Min(sh.Radius, maxR);
        var rs = new float[4];
        for (var i = 0; i < 4; i++)
            rs[i] = Mathf.Max(0.004f, Mathf.Min(sh.SideRadius[i] < 0f ? rTop : sh.SideRadius[i], maxR));
        var rBot = Mathf.Max(0.004f, Mathf.Min(0.05f, maxR));
        rTop = Mathf.Max(0.004f, rTop);
        // Per axis: low / high extents and radii (x: −x side 3, +x side 1; z: −z side 2, +z side 0; y: bottom, top).
        var lo = new Vector3(-hx, yb, -hz);
        var hi = new Vector3(hx, hy, hz);
        var rlo = new Vector3(rs[3], rBot, rs[2]);
        var rhi = new Vector3(rs[1], rTop, rs[0]);
        var cx = Coords(lo.x, hi.x, rlo.x, rhi.x, Segs(rlo.x), Segs(rhi.x));
        var cy = Coords(lo.y, hi.y, rlo.y, rhi.y, 1, Segs(rhi.y));
        var cz = Coords(lo.z, hi.z, rlo.z, rhi.z, Segs(rlo.z), Segs(rhi.z));
        var bakedS = new Baked(soft, LookId.Soft);
        var bakedC = new Baked(candy, LookId.Candy);

        void Vertex(Vector3 p)
        {
            var inner = new Vector3(Mathf.Clamp(p.x, lo.x + rlo.x, hi.x - rhi.x), Mathf.Clamp(p.y, lo.y + rlo.y, hi.y - rhi.y), Mathf.Clamp(p.z, lo.z + rlo.z, hi.z - rhi.z));
            var d = p - inner;
            var r = new Vector3(d.x > 0f ? rhi.x : rlo.x, d.y > 0f ? rhi.y : rlo.y, d.z > 0f ? rhi.z : rlo.z);
            var e = new Vector3(d.x / r.x, d.y / r.y, d.z / r.z);
            var len = e.magnitude;
            Vector3 v, n;
            if (len < 1e-6f)
            {
                v = p;
                n = Vector3.up;
            }
            else
            {
                var en = e / len;
                v = inner + Vector3.Scale(en, r);
                n = new Vector3(en.x / r.x, en.y / r.y, en.z / r.z).normalized;
            }

            // Regions: top, the rounded top edge (rim colour in soft), the side band (candy), the side body (AO gradient), bottom.
            int role;
            Color cs, cc;
            if (n.y > 0.9f)
            {
                role = sh.TopRole;
                cs = bakedS.Top;
                cc = bakedC.Top;
            }
            else if (n.y < -0.5f)
            {
                role = sh.SideRole;
                cs = bakedS.Bottom;
                cc = bakedC.Bottom;
            }
            else if (v.y > hy - rTop - 0.005f && n.y > 0.05f)
            {
                role = RoleRim; // the rounded top edge: trim (soft gloss), never floor
                cs = bakedS.Edge;
                cc = bakedC.Edge;
            }
            else
            {
                role = sh.SideRole;
                cs = bakedS.SideAt(v.y - yb, hy - v.y, height);
                cc = bakedC.SideAt(v.y - yb, hy - v.y, height);
            }

            b.Add(v, n, cs, cc, role, soft.Pattern);
        }

        void Face(int axis, bool high)
        {
            var u = (axis + 1) % 3;
            var w = (axis + 2) % 3;
            var cu = u == 0 ? cx : u == 1 ? cy : cz;
            var cw = w == 0 ? cx : w == 1 ? cy : cz;
            var start = b.V.Count;
            for (var j = 0; j < cw.Count; j++)
                for (var i = 0; i < cu.Count; i++)
                {
                    var p = Vector3.zero;
                    p[axis] = high ? hi[axis] : lo[axis];
                    p[u] = cu[i];
                    p[w] = cw[j];
                    Vertex(p);
                }

            for (var j = 0; j < cw.Count - 1; j++)
                for (var i = 0; i < cu.Count - 1; i++)
                {
                    var a = start + j * cu.Count + i;
                    var c = a + cu.Count;
                    b.T.Add(a); b.T.Add(a + 1); b.T.Add(c + 1);
                    b.T.Add(a); b.T.Add(c + 1); b.T.Add(c);
                }
        }

        for (var axis = 0; axis < 3; axis++)
        {
            Face(axis, true);
            Face(axis, false);
        }

        // Padded rims along the exposed top edges.
        if (sh.Rims.Count > 0)
        {
            var cy0 = hy - RimDrop;
            var ends = new HashSet<int>();
            foreach (var run in sh.Rims)
            {
                EdgeFrame(run.Side, hx, hz, out var start, out var dir, out var outward, out var len);
                var a = start + dir * Mathf.Clamp(run.A, 0f, len) - outward * RimInset;
                var bb = start + dir * Mathf.Clamp(run.B, 0f, len) - outward * RimInset;
                // Pull the tube's ends in at the corners so it never leaves the footprint (+ the tube radius).
                if (run.A <= 0.01f)
                    a += dir * RimInset;
                if (run.B >= len - 0.01f)
                    bb -= dir * RimInset;
                a.y = bb.y = cy0;
                Tube(b, a, bb, RimRadius, bakedS.Rim, bakedC.Rim, soft.Pattern);
                if (run.A <= 0.01f)
                    ends.Add(run.Side * 2);
                if (run.B >= len - 0.01f)
                    ends.Add(run.Side * 2 + 1);
            }

            // Corner balls where two exposed edges meet (side s end ↔ side s+1 start).
            for (var s = 0; s < 4; s++)
                if (ends.Contains(s * 2 + 1) && ends.Contains(((s + 1) % 4) * 2))
                {
                    EdgeFrame(s, hx, hz, out var start, out var dir, out var outward, out var len);
                    var p = start + dir * (len - RimInset) - outward * RimInset;
                    p.y = cy0;
                    Ball(b, p, RimRadius, bakedS.Rim, bakedC.Rim, soft.Pattern);
                }
        }

        return b.ToMesh("Toy_" + Mathf.Abs((size.ToString("F3") + sh.Key()).GetHashCode()).ToString("X"), size);
    }

    /// <summary>Top edge of side <paramref name="side"/> (0 +z, 1 +x, 2 −z, 3 −x), walked counter-clockwise seen from above.</summary>
    public static void EdgeFrame(int side, float hx, float hz, out Vector3 start, out Vector3 dir, out Vector3 outward, out float len)
    {
        switch (side)
        {
            case 0: start = new Vector3(-hx, 0f, hz); dir = Vector3.right; outward = Vector3.forward; len = 2f * hx; break;
            case 1: start = new Vector3(hx, 0f, hz); dir = Vector3.back; outward = Vector3.right; len = 2f * hz; break;
            case 2: start = new Vector3(hx, 0f, -hz); dir = Vector3.left; outward = Vector3.back; len = 2f * hx; break;
            default: start = new Vector3(-hx, 0f, -hz); dir = Vector3.forward; outward = Vector3.left; len = 2f * hz; break;
        }
    }

    // ------------------------------------------------------------------ rim primitives

    private const int TubeSides = 8;

    /// <summary>A capped tube from <paramref name="a"/> to <paramref name="b"/> (metres), role rim.</summary>
    public static void Tube(Buffers buf, Vector3 a, Vector3 b, float r, Color soft, Color candy, int pattern)
    {
        var axis = b - a;
        if (axis.sqrMagnitude < 1e-6f)
            return;
        var d = axis.normalized;
        var u = Vector3.Cross(d, Mathf.Abs(d.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
        var w = Vector3.Cross(d, u);
        var start = buf.V.Count;
        for (var i = 0; i <= TubeSides; i++)
        {
            var ang = i * Mathf.PI * 2f / TubeSides;
            var n = u * Mathf.Cos(ang) + w * Mathf.Sin(ang);
            var shade = n.y < -0.3f ? 0.82f : 1f; // the underside sits in the block's shadow
            buf.Add(a + n * r, n, soft * shade, candy * shade, RoleRim, pattern);
            buf.Add(b + n * r, n, soft * shade, candy * shade, RoleRim, pattern);
        }

        for (var i = 0; i < TubeSides; i++)
        {
            var k = start + i * 2;
            buf.T.Add(k); buf.T.Add(k + 1); buf.T.Add(k + 2);
            buf.T.Add(k + 1); buf.T.Add(k + 3); buf.T.Add(k + 2);
        }

        foreach (var (p, nn) in new[] { (a, -d), (b, d) })
        {
            var c0 = buf.V.Count;
            buf.Add(p + nn * (r * 0.35f), nn, soft, candy, RoleRim, pattern);
            for (var i = 0; i < TubeSides; i++)
            {
                var ang = i * Mathf.PI * 2f / TubeSides;
                var n = u * Mathf.Cos(ang) + w * Mathf.Sin(ang);
                buf.Add(p + n * r, (n + nn).normalized, soft, candy, RoleRim, pattern);
            }

            for (var i = 0; i < TubeSides; i++)
            {
                buf.T.Add(c0); buf.T.Add(c0 + 1 + i); buf.T.Add(c0 + 1 + (i + 1) % TubeSides);
            }
        }
    }

    public static void Ball(Buffers buf, Vector3 c, float r, Color soft, Color candy, int pattern, int seg = 8, int rings = 5)
    {
        var start = buf.V.Count;
        for (var y = 0; y <= rings; y++)
        {
            var phi = y / (float)rings * Mathf.PI;
            for (var x = 0; x <= seg; x++)
            {
                var th = x / (float)seg * Mathf.PI * 2f;
                var d = new Vector3(Mathf.Sin(phi) * Mathf.Cos(th), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(th));
                buf.Add(c + d * r, d, soft, candy, RoleRim, pattern);
            }
        }

        for (var y = 0; y < rings; y++)
            for (var x = 0; x < seg; x++)
            {
                var a = start + y * (seg + 1) + x;
                var b2 = a + seg + 1;
                buf.T.Add(a); buf.T.Add(a + 1); buf.T.Add(b2);
                buf.T.Add(a + 1); buf.T.Add(b2 + 1); buf.T.Add(b2);
            }
    }

    /// <summary>
    /// A padded rim around a round prism top (radius <paramref name="radius"/>, metres, top at y 0), over the exposed angular
    /// runs (radians; empty = the whole ring), appended to <paramref name="buf"/> in metres.
    /// </summary>
    public static void RimRing(Buffers buf, float radius, List<Vector2> runs, Color soft, Color candy, int pattern)
    {
        var rc = radius - RimInset;
        const int segs = 40;
        foreach (var run in runs)
        {
            var n = Mathf.Max(2, Mathf.CeilToInt((run.y - run.x) / (Mathf.PI * 2f) * segs));
            Vector3 P(float a) => new Vector3(Mathf.Cos(a) * rc, -RimDrop, Mathf.Sin(a) * rc);
            // One continuous tube: rings along the arc.
            var start = buf.V.Count;
            for (var k = 0; k <= n; k++)
            {
                var a = Mathf.Lerp(run.x, run.y, k / (float)n);
                var c = P(a);
                var radial = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                for (var i = 0; i <= TubeSides; i++)
                {
                    var ang = i * Mathf.PI * 2f / TubeSides;
                    var nn = radial * Mathf.Cos(ang) + Vector3.up * Mathf.Sin(ang);
                    var shade = nn.y < -0.3f ? 0.82f : 1f;
                    buf.Add(c + nn * RimRadius, nn, soft * shade, candy * shade, RoleRim, pattern);
                }
            }

            for (var k = 0; k < n; k++)
                for (var i = 0; i < TubeSides; i++)
                {
                    var a = start + k * (TubeSides + 1) + i;
                    var b2 = a + TubeSides + 1;
                    buf.T.Add(a); buf.T.Add(a + 1); buf.T.Add(b2);
                    buf.T.Add(a + 1); buf.T.Add(b2 + 1); buf.T.Add(b2);
                }

            if (run.y - run.x < Mathf.PI * 2f - 1e-3f)
            {
                Ball(buf, P(run.x), RimRadius, soft, candy, pattern, 6, 4);
                Ball(buf, P(run.y), RimRadius, soft, candy, pattern, 6, 4);
            }
        }
    }

    // ------------------------------------------------------------------ colours per look

    /// <summary>A style's colours baked for one look (linear vertex colours that show the tokens on a lit top after the grade).</summary>
    public readonly struct Baked
    {
        public readonly Color Top, Band, Side, Rim, Bottom, Edge;
        public readonly float BandH, Ao;

        public Baked(MapArt.Style st, LookId look)
        {
            Top = MapArt.Bake(st.Top, look);
            Band = MapArt.Bake(st.Band, look);
            Side = MapArt.Bake(st.Side, look);
            Rim = MapArt.Bake(st.Rim, look);
            Ao = look == LookId.Soft ? 0.64f : 0.74f; // soft: stronger AO toward the bottom and under the lips
            Bottom = Side * (Ao * 0.85f);
            Edge = look == LookId.Soft && st.Walkable ? Rim : Band;
            BandH = look == LookId.Soft ? 0f : st.BandH;
        }

        /// <summary>A side vertex <paramref name="fromBottom"/> m above the mesh bottom, <paramref name="fromTop"/> m below the top.</summary>
        public Color SideAt(float fromBottom, float fromTop, float height)
        {
            if (fromTop < BandH)
                return Band;
            var k = Mathf.Clamp01(fromBottom / Mathf.Max(0.3f, height * 0.8f));
            return Color.Lerp(Side * Ao, Side, Mathf.SmoothStep(0f, 1f, k));
        }
    }
}
