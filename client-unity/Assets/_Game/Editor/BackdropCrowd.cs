using System.Collections.Generic;
using FallRush.Net;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// G4.10 far stadium life (soft look): a crowd of little two-tone capsule spectators on the backdrop's bleacher tiers and
/// the waving banners / tower flags, all in ONE merged mesh on the <c>FallRush/Crowd</c> shader, which hops, sways and
/// waves them from a per-vertex phase (uv1) — one draw call, no CPU per frame. Our own shapes (capsule-toy motif: white
/// dome over a coloured shell); far beyond any reach (≥ 260 m), no colliders, no shadows.
/// </summary>
public sealed class BackdropCrowd
{
    private readonly List<Vector3> _v = new List<Vector3>();
    private readonly List<Vector3> _n = new List<Vector3>();
    private readonly List<Color> _c = new List<Color>();
    private readonly List<Vector2> _u1 = new List<Vector2>();
    private readonly List<Vector2> _u2 = new List<Vector2>();
    private readonly List<int> _t = new List<int>();

    public int Spectators { get; private set; }
    public int Triangles => _t.Count / 3;

    private const int Sides = 5;

    /// <summary>One capsule spectator standing at <paramref name="foot"/>: a coloured lower shell, a white dome (5 sides, 40 tris).</summary>
    public void Spectator(Vector3 foot, float height, Color shell, float phase)
    {
        var r = height * 0.3f;
        // profile (radius, y, white?) bottom → top
        var prof = new[] { (0f, 0f), (r * 0.8f, 0.06f * height), (r, 0.3f * height), (r, 0.55f * height), (r * 0.85f, 0.82f * height), (0f, height) };
        var start = _v.Count;
        for (var k = 0; k < prof.Length; k++)
        {
            var (pr, py) = prof[k];
            var col = py > 0.56f * height ? Color.white : shell;
            var rings = pr > 0f ? Sides : 1;
            for (var s = 0; s < rings; s++)
            {
                var a = (s + (k & 1) * 0.5f) * Mathf.PI * 2f / Sides;
                var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                _v.Add(foot + d * pr + Vector3.up * py);
                _n.Add(pr > 0f ? (d + Vector3.up * ((py / height) - 0.5f)).normalized : (py > 0f ? Vector3.up : Vector3.down));
                _c.Add(col.linear);
                _u1.Add(new Vector2(phase, py / height));
                _u2.Add(Vector2.zero);
            }
        }

        // indices: ring k starts at offsets
        var off = new int[prof.Length];
        var o = start;
        for (var k = 0; k < prof.Length; k++)
        {
            off[k] = o;
            o += prof[k].Item1 > 0f ? Sides : 1;
        }

        for (var k = 0; k < prof.Length - 1; k++)
        {
            var a0 = prof[k].Item1 > 0f;
            var a1 = prof[k + 1].Item1 > 0f;
            for (var s = 0; s < Sides; s++)
            {
                var s1 = (s + 1) % Sides;
                if (!a0)
                    Tri(off[k], off[k + 1] + s1, off[k + 1] + s);
                else if (!a1)
                    Tri(off[k] + s, off[k] + s1, off[k + 1]);
                else
                {
                    Tri(off[k] + s, off[k] + s1, off[k + 1] + s);
                    Tri(off[k] + s1, off[k + 1] + s1, off[k + 1] + s);
                }
            }
        }

        Spectators++;
    }

    /// <summary>A waving flag / banner triangle (double-sided by the shader); <paramref name="wa"/>.. are the wave weights (0 = fixed edge).</summary>
    public void Flag(Vector3 a, Vector3 b, Vector3 c, float wa, float wb, float wc, Color col, float phase)
    {
        var n = Vector3.Cross(b - a, c - a).normalized;
        var s = _v.Count;
        foreach (var (p, w) in new[] { (a, wa), (b, wb), (c, wc) })
        {
            _v.Add(p);
            _n.Add(n);
            _c.Add(col.linear);
            _u1.Add(new Vector2(phase, w));
            _u2.Add(new Vector2(1f, 0f));
        }

        Tri(s, s + 1, s + 2);
    }

    private void Tri(int a, int b, int c)
    {
        _t.Add(a);
        _t.Add(b);
        _t.Add(c);
    }

    public GameObject Emit(Transform parent)
    {
        var go = new GameObject("Crowd");
        go.transform.SetParent(parent, false);
        go.isStatic = true;
        var m = new Mesh { name = "Dressing_Crowd" };
        if (_v.Count > 65000)
            m.indexFormat = IndexFormat.UInt32;
        m.SetVertices(_v);
        m.SetNormals(_n);
        m.SetColors(_c);
        m.SetUVs(1, _u1);
        m.SetUVs(2, _u2);
        m.SetTriangles(_t, 0);
        m.RecalculateBounds();
        // The vertex shader moves vertices ≤ 1.2 m: pad the bounds so culling never clips a hop.
        var bb = m.bounds;
        bb.Expand(3f);
        m.bounds = bb;
        go.AddComponent<MeshFilter>().sharedMesh = m;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = Material();
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.lightProbeUsage = LightProbeUsage.Off;
        r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        go.AddComponent<FallRush.Client.LookOnly>().look = LookId.Soft;
        return go;
    }

    private static Material Material()
    {
        var path = ProjectSetup.Root + "/Materials/Toon/Crowd.mat";
        var sh = Shader.Find("FallRush/Crowd") ?? throw new System.InvalidOperationException("FallRush/Crowd not found");
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(sh);
            AssetDatabase.CreateAsset(mat, path);
        }

        mat.shader = sh;
        EditorUtility.SetDirty(mat);
        return mat;
    }
}
