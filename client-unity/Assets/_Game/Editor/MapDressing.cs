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
        var rng = new System.Random(mapKey.GetHashCode() & 0x7FFFFFFF);
        Clouds(root, bounds, rng, mapKey);
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
        var mb = new MeshBuilder();
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

        var m = MapArt.ToonAsset("Cloud", Color.white, mat =>
        {
            mat.SetFloat("_Unlit", 0.55f);
            mat.SetFloat("_RimStrength", 0.5f);
            mat.SetColor("_ShadeColor", new Color(0.86f, 0.88f, 1f));
        });
        mb.Emit(root, "Clouds", m);
    }

    // ------------------------------------------------------------------ mesh helper

    /// <summary>Accumulates simple coloured primitives into one static mesh.</summary>
    public sealed class MeshBuilder
    {
        private readonly List<Vector3> _v = new List<Vector3>();
        private readonly List<Vector3> _n = new List<Vector3>();
        private readonly List<Color> _c = new List<Color>();
        private readonly List<int> _t = new List<int>();

        public int Count => _v.Count;

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
                    _c.Add(Color.Lerp(bottom, top, d.y * 0.5f + 0.5f));
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

        /// <summary>Appends a mesh (e.g. a toy block) transformed by <paramref name="m"/>, optionally tinted.</summary>
        public void Add(Mesh mesh, Matrix4x4 m, Color? tint = null)
        {
            var start = _v.Count;
            var mv = mesh.vertices;
            var mn = mesh.normals;
            var mc = mesh.colors;
            var nm = m.inverse.transpose;
            for (var i = 0; i < mv.Length; i++)
            {
                _v.Add(m.MultiplyPoint3x4(mv[i]));
                _n.Add(nm.MultiplyVector(mn.Length > i ? mn[i] : Vector3.up).normalized);
                var col = mc.Length > i ? mc[i] : Color.white;
                _c.Add(tint.HasValue ? col * tint.Value : col);
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
