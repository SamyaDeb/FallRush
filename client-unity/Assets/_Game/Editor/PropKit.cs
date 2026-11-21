using System.Collections.Generic;
using System.IO;
using FallRush.Net;
using UnityEditor;
using UnityEngine;

/// <summary>
/// G4.9 prop kit (Blender, <c>tools/blender/props.py</c> → <c>Art/Props/*.fbx</c> + <c>props.json</c>). The FBX corners
/// carry UV0 = (palette slot, soft role) and UV1 = (shade: baked AO × bottom-dark gradient); <see cref="Get"/> turns a prop
/// into a map mesh in the G4.8-1 layout: COLOR = the soft albedo × shade, uv3 = (candy albedo × shade, 2 + 8 × role),
/// uv2.y = 4 on hazard parts (the toon view rim). Colours come from <see cref="PaletteTokens"/> (one source of truth) and
/// are baked per look like every map colour (<see cref="MapArt.Bake"/>). The axis / unit mapping of the FBX is checked
/// against the generator's bounds in props.json (and fixed if the importer mirrored or rescaled an axis). Render only.
/// </summary>
public static class PropKit
{
    public const string Dir = ProjectSetup.Root + "/Art/Props";
    public const int TriCap = 1500;
    public const int RoleHazard = 4;

    private static readonly Dictionary<string, Mesh> Cache = new Dictionary<string, Mesh>();
    private static Json _json;

    [System.Serializable]
    public sealed class Json
    {
        public string[] slots;
        public int triCap;
        public Entry[] props;
    }

    [System.Serializable]
    public sealed class Entry
    {
        public string name;
        public int tris;
        public string pivot;
        public Box box;
        public Bounds3 bounds;
    }

    [System.Serializable]
    public sealed class Box
    {
        public float[] centre, size;
    }

    [System.Serializable]
    public sealed class Bounds3
    {
        public float[] min, max;
    }

    public static Json Manifest
    {
        get
        {
            if (_json == null)
                _json = JsonUtility.FromJson<Json>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", Dir, "props.json")));
            return _json;
        }
    }

    public static Entry EntryOf(string name)
    {
        foreach (var e in Manifest.props)
            if (e.name == name)
                return e;
        throw new IOException("prop not in props.json: " + name);
    }

    /// <summary>Forget the resolved meshes (MapArt.Apply calls it per map: the bake depends on the palette).</summary>
    public static void Clear()
    {
        Cache.Clear();
        _json = null;
    }

    /// <summary>The raw imported FBX mesh (UV0 slot/role, UV1 shade).</summary>
    public static Mesh Raw(string name)
    {
        var path = $"{Dir}/{name}.fbx";
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            if (o is Mesh m)
                return m;
        throw new IOException("no mesh in " + path);
    }

    /// <summary>The map-ready mesh of a prop (cached; name "Prop_" + name).</summary>
    public static Mesh Get(string name)
    {
        if (Cache.TryGetValue(name, out var cached) && cached != null)
            return cached;
        var raw = Raw(name);
        var e = EntryOf(name);
        var v = raw.vertices;
        var n = raw.normals;
        var uv0 = new List<Vector2>();
        var uv1 = new List<Vector2>();
        raw.GetUVs(0, uv0);
        raw.GetUVs(1, uv1);
        if (uv0.Count != v.Length || uv1.Count != v.Length)
            throw new IOException($"{name}: the FBX lost its slot / shade UVs ({uv0.Count}, {uv1.Count} for {v.Length} vertices)");

        // Axis / unit check against the generator's bounds (Unity axes): fix a mirrored or rescaled axis.
        var b = raw.bounds;
        var fix = Vector3.one;
        for (var a = 0; a < 3; a++)
        {
            var wantMin = e.bounds.min[a];
            var wantMax = e.bounds.max[a];
            var want = wantMax - wantMin;
            var got = b.size[a];
            var s = want > 1e-4f && got > 1e-6f ? want / got : 1f;
            if (Mathf.Abs(s - 1f) > 0.02f && Mathf.Abs(s - 0.01f) > 0.0002f && Mathf.Abs(s - 100f) > 2f)
                throw new IOException($"{name}: axis {a} size {got:F4} vs props.json {want:F4} (axes swapped?)");
            var cg = b.center[a] * s;
            var cw = (wantMin + wantMax) * 0.5f;
            var mirrored = Mathf.Abs(cw) > 0.01f && Mathf.Abs(cg + cw) < Mathf.Abs(cg - cw);
            fix[a] = mirrored ? -s : s;
        }

        var P = PaletteTokens.Get();
        var cols = new Color[v.Length];
        var u3 = new List<Vector4>(v.Length);
        var u2 = new List<Vector2>(v.Length);
        var nv = new Vector3[v.Length];
        var nn = new Vector3[v.Length];
        for (var i = 0; i < v.Length; i++)
        {
            nv[i] = Vector3.Scale(v[i], fix);
            nn[i] = Vector3.Scale(n.Length > i ? n[i] : Vector3.up, new Vector3(Mathf.Sign(fix.x), Mathf.Sign(fix.y), Mathf.Sign(fix.z))).normalized;
            var slot = Mathf.RoundToInt(uv0[i].x);
            var role = Mathf.RoundToInt(uv0[i].y);
            var shade = uv1[i].x;
            var sn = Manifest.slots[slot];
            var soft = MapArt.Bake(SlotColour(P, sn, LookId.Soft), LookId.Soft) * shade;
            var candy = MapArt.Bake(SlotColour(P, sn, LookId.Candy), LookId.Candy) * shade;
            soft.a = 0f;
            cols[i] = soft;
            u3.Add(new Vector4(candy.r, candy.g, candy.b, 2f + 8f * role));
            u2.Add(new Vector2(0f, role == RoleHazard ? 4f : 0f));
        }

        var tris = raw.triangles;
        for (var i = 0; i < tris.Length; i += 3)
        {
            var fn = Vector3.Cross(nv[tris[i + 1]] - nv[tris[i]], nv[tris[i + 2]] - nv[tris[i]]);
            if (Vector3.Dot(fn, nn[tris[i]] + nn[tris[i + 1]] + nn[tris[i + 2]]) < 0f)
                (tris[i + 1], tris[i + 2]) = (tris[i + 2], tris[i + 1]);
        }

        var m = new Mesh { name = "Prop_" + name };
        m.SetVertices(nv);
        m.SetNormals(nn);
        m.SetColors(cols);
        m.SetUVs(1, u2);
        m.SetUVs(3, u3);
        m.SetTriangles(tris, 0);
        m.RecalculateBounds();
        Cache[name] = m;
        return m;
    }

    /// <summary>A props.json palette slot → its sRGB token colour in a look.</summary>
    public static Color SlotColour(PaletteTokens p, string slot, LookId look)
    {
        if (slot.StartsWith("rim") && System.Enum.TryParse<SurfaceHue>(slot.Substring(3), out var h))
            return p.Rim(h, look);
        switch (slot)
        {
            case "white": return Color.white;
            case "cream": return PaletteTokens.Hex(0xFFF3E0);
            case "sky": return PaletteTokens.Hex(0x3FA9F5);
        }

        var f = typeof(PaletteTokens).GetField(slot);
        if (f == null || f.FieldType != typeof(Color))
            throw new IOException("unknown palette slot " + slot);
        return (Color)f.GetValue(p);
    }
}
