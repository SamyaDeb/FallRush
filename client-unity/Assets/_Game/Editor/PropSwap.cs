using System.Collections.Generic;
using FallRush.Net;
using UnityEngine;

/// <summary>
/// G4.9: puts the Blender prop kit (<see cref="PropKit"/>) on the hazard and static render parts, before MapArt merges
/// them. <b>Render only</b>: no collider, transform with a collider, def or hazard pose is touched; colliders keep their
/// GameObjects and scales, a prop that replaces a collider's box goes on a new render-only child (scale undone, so the
/// prop's metres are world metres) and the box's own renderer and mesh filter are removed. Every prop sits within 0.15 m of what it
/// renders (tested: <c>PropTests</c>):
/// <list type="bullet">
/// <item>mallet head (analytic: radius 0.45, half width 0.75): the foam head with its three lemon bands (the hit stripe);</item>
/// <item>bumper (prism r × h): the quilted pillow with two lemon bands (MapArt folds it into the bumper's merged mesh);</item>
/// <item>spinner bar (analytic radius 0.15): the striped segments stay; foam caps at both ends (≤ 0.12 m past the arm) and a hub;</item>
/// <item>gumballs (analytic sphere r): the gumball with a lemon band and a shine band;</item>
/// <item>spring pads (box): the mint pad with a checker border and a coil, inside the collider box;</item>
/// <item>Capsule Run finish arch (boxes): inflatable posts and top segments of the same sizes; mallet frame beams (no collider).</item>
/// </list>
/// </summary>
public static class PropSwap
{
    /// <summary>What was swapped (for the build log and the proof).</summary>
    public static readonly List<string> Log = new List<string>();

    public static void Hazards(MapInfo info, Transform geo)
    {
        Log.Clear();
        if (info == null)
            return;
        foreach (var ml in info.mallets)
            if (ml != null && ml.visual != null)
                Mallet(ml);
        foreach (var sw in info.sweepers)
            if (sw != null && sw.barVisual != null)
                Spinner(sw);
        foreach (var lane in info.rollingBalls)
            if (lane != null && lane.balls != null)
                foreach (var b in lane.balls)
                    if (b != null)
                        Gumball(b, lane.def.RadiusMm / 1000f);
        foreach (var pd in info.pads)
            if (pd != null && pd.motorCollider != null)
                Pad(pd);
        if (geo != null)
            foreach (var t in geo.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "ArchPostL" || t.name == "ArchPostR")
                    OnBox(t, "arch_post", Quaternion.identity);
                else if (t.name.StartsWith("ArchTop_"))
                    OnBox(t, "arch_seg", Quaternion.identity);
                else if (t.name.StartsWith("MalletBeam_"))
                    OnBox(t, "padded_beam", Mathf.Abs(t.lossyScale.x) > Mathf.Abs(t.lossyScale.z) ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity);
            }

        Debug.Log($"[setup] props: {Log.Count} swaps ({string.Join(", ", Count())})");
    }

    private static IEnumerable<string> Count()
    {
        var d = new SortedDictionary<string, int>();
        foreach (var s in Log)
            d[s] = d.TryGetValue(s, out var c) ? c + 1 : 1;
        foreach (var kv in d)
            yield return $"{kv.Key} × {kv.Value}";
    }

    private static GameObject Piece(Transform parent, string name, string prop, Material mat)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = PropKit.Get(prop);
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        go.isStatic = parent.gameObject.isStatic;
        Log.Add(prop);
        return go;
    }

    private static void Mallet(HazardMallet ml)
    {
        var head = ml.visual.Find("Head");
        var body = head != null ? head.Find("HeadBody") : null;
        if (body == null)
            return;
        var mf = body.GetComponent<MeshFilter>();
        mf.sharedMesh = PropKit.Get("mallet_head");
        // The builder's head box is long along z for swing axis 0, along x otherwise; the prop is long along z.
        body.localScale = Vector3.one;
        body.localRotation = ml.def.SwingAxis == 0 ? Quaternion.identity : Quaternion.Euler(0f, 90f, 0f);
        Log.Add("mallet_head");
        for (var i = head.childCount - 1; i >= 0; i--)
            if (head.GetChild(i).name.StartsWith("HeadBand_"))
                Object.DestroyImmediate(head.GetChild(i).gameObject); // the bands are on the prop
    }

    private static void Spinner(HazardSweeper sw)
    {
        var bar = sw.barVisual;
        var arm = sw.def.ArmMm / 1000f;
        var mat = FirstMaterial(bar);
        for (var s = -1; s <= 1; s += 2)
        {
            var cap = Piece(bar, s < 0 ? "CapA" : "CapB", "spinner_cap", mat);
            cap.transform.localPosition = new Vector3(0f, 0f, s * (arm - 0.08f));
        }

        var hub = Piece(bar, "Hub", "spinner_hub", mat);
        hub.transform.localScale = new Vector3(0.89f, 1f, 0.89f); // radius 0.4: 0.1 m past the 0.6 m post
    }

    private static void Gumball(Transform ball, float radius)
    {
        var body = ball.Find("BallBody");
        if (body == null)
            return;
        body.GetComponent<MeshFilter>().sharedMesh = PropKit.Get("gumball");
        body.localScale = Vector3.one * radius;
        body.GetComponent<MeshRenderer>().sharedMaterial = MapArt.VertexColorMaterial();
        Log.Add("gumball");
        var band = ball.Find("BallBand");
        if (band != null)
            Object.DestroyImmediate(band.gameObject);
    }

    private static void Pad(HazardSpringPad pd)
    {
        var col = pd.motorCollider.transform;
        var r = col.GetComponent<MeshRenderer>();
        if (r == null)
            return;
        var s = col.lossyScale;
        var go = Piece(col, "PadProp", "spring_pad", MapArt.VertexColorMaterial());
        go.transform.localPosition = new Vector3(0f, 0.5f, 0f);                  // the pad's top (collider centre + half thickness)
        go.transform.localScale = new Vector3(1f, 1f / Mathf.Abs(s.y), 1f);      // world: (size.x, 1, size.z); the prop is a unit pad
        DropRenderer(col);
    }

    /// <summary>A prop of the same size as a box (collider or visual): a render-only child with the box scale undone.</summary>
    private static void OnBox(Transform box, string prop, Quaternion turn)
    {
        var r = box.GetComponent<MeshRenderer>();
        if (r == null || !r.enabled)
            return;
        var s = box.localScale; // the boxes sit directly under the Geometry root (scale 1)
        var go = Piece(box, "Prop", prop, MapArt.VertexColorMaterial());
        go.transform.localRotation = turn;
        go.transform.localScale = turn == Quaternion.identity
            ? new Vector3(1f / Mathf.Abs(s.x), 1f / Mathf.Abs(s.y), 1f / Mathf.Abs(s.z))
            : new Vector3(1f / Mathf.Abs(s.z), 1f / Mathf.Abs(s.y), 1f / Mathf.Abs(s.x));
        DropRenderer(box);
    }

    /// <summary>The box keeps its collider and transform; its renderer and mesh filter go (the prop child draws it).</summary>
    private static void DropRenderer(Transform t)
    {
        Object.DestroyImmediate(t.GetComponent<MeshRenderer>());
        var mf = t.GetComponent<MeshFilter>();
        if (mf != null)
            Object.DestroyImmediate(mf);
    }

    private static Material FirstMaterial(Transform t)
    {
        var r = t.GetComponentInChildren<MeshRenderer>(true);
        return r != null ? r.sharedMaterial : MapArt.VertexColorMaterial();
    }
}
