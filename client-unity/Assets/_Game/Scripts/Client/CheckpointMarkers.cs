using System.Collections.Generic;
using FallRush.Net;
using FallRush.Shared;
using UnityEngine;

namespace FallRush.Client
{
    /// <summary>
    /// 2026-11-24 playtest ("Goo Tower has no checkpoints"): a floor circle with a zebra border on each Survival respawn spot (the OC-51 tower
    /// spots, <see cref="RulesFactory.RespawnSpots"/>), so you can see where a fall puts you back. Visual only: built at run
    /// time, no colliders, not in the map content hash; the respawn itself is the sim's (server) / the practice mode's.
    /// </summary>
    public sealed class CheckpointMarkers : MonoBehaviour
    {
        // 2026-11-24 playtest 2: no flag; a checkpoint is a floor circle with a green/black zebra border (reads at a glance).
        private static readonly Color Disc = new Color(0.62f, 0.93f, 0.80f);   // soft mint fill
        private static readonly Color Green = new Color(0.12f, 0.78f, 0.36f);  // zebra stripe A
        private static readonly Color Black = new Color(0.06f, 0.06f, 0.07f);  // zebra stripe B
        private static readonly Color Hit = new Color(1f, 0.85f, 0.25f);       // green stripes turn yellow once reached

        private const float R = 0.8f;       // the fill circle (1.6 m wide, as before)
        private const float Border = 0.2f;  // zebra ring width
        private const float Height = 0.035f;
        private const int Stripes = 24;     // 12 green + 12 black

        private readonly List<Transform> _roots = new List<Transform>();
        private readonly List<Renderer> _green = new List<Renderer>();
        private Material _hit;

        /// <summary>Circles on a Survival map's respawn spots; null on other maps (race maps already show their checkpoints).</summary>
        public static CheckpointMarkers Build(MapInfo map, Transform parent)
        {
            if (map == null || map.definition == null || map.definition.mode != "Survival")
                return null;
            var spots = RulesFactory.RespawnSpots(map.botRoutes);
            if (spots.Length < 4)
                return null;
            var go = new GameObject("CheckpointMarkers");
            go.transform.SetParent(parent, false);
            var m = go.AddComponent<CheckpointMarkers>();
            var src = map.unlitMaterial != null ? map.unlitMaterial : map.playerMaterial;
            Material Mat(Color c)
            {
                var mat = src != null ? new Material(src) : new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                mat.SetShaderPassEnabled("SRPDefaultUnlit", false); // no Toon outline hull on these (Rushling-only pass)
                if (mat.HasProperty("_BaseColor"))
                    mat.SetColor("_BaseColor", c);
                mat.color = c;
                return mat;
            }

            var disc = Mat(Disc);
            var green = Mat(Green);
            var black = Mat(Black);
            m._hit = Mat(Hit);
            var greenMesh = Ring(0);
            var blackMesh = Ring(1);
            // One circle per landing: the 2–3 spots of a corner (within 3 m, same height) share it, at their middle.
            var groups = new List<(Vector3 sum, int n)>();
            for (var j = 0; j + 3 < spots.Length; j += 4)
            {
                var p = new Vector3(spots[j] * 0.001f, spots[j + 1] * 0.001f, spots[j + 2] * 0.001f);
                var g = groups.FindIndex(x =>
                {
                    var c = x.sum / x.n;
                    return Mathf.Abs(c.y - p.y) < 1f && (c.x - p.x) * (c.x - p.x) + (c.z - p.z) * (c.z - p.z) < 9f;
                });
                if (g < 0)
                    groups.Add((p, 1));
                else
                    groups[g] = (groups[g].sum + p, groups[g].n + 1);
            }

            foreach (var grp in groups)
            {
                var root = new GameObject($"Checkpoint{m._roots.Count}").transform;
                root.SetParent(go.transform, false);
                root.position = grp.sum / grp.n;
                Part(root, PrimitiveType.Cylinder, new Vector3(0f, 0.012f, 0f), new Vector3(2f * R, 0.01f, 2f * R), disc);
                var ring = new GameObject("ZebraRing").transform;
                ring.SetParent(root, false);
                ring.localPosition = new Vector3(0f, 0.005f, 0f);
                MeshPart(ring, "Green", greenMesh, green, m._green);
                MeshPart(ring, "Black", blackMesh, black, null);
                m._roots.Add(root);
            }

            return m;
        }

        /// <summary>Every other stripe (parity 0 = green, 1 = black) of a flat raised ring, R..R+Border, double-sided.</summary>
        private static Mesh Ring(int parity)
        {
            var v = new List<Vector3>();
            var t = new List<int>();
            const int sub = 3; // arc steps per stripe
            for (var k = parity; k < Stripes; k += 2)
            {
                for (var q = 0; q < sub; q++)
                {
                    var a0 = (k + q / (float)sub) * Mathf.PI * 2f / Stripes;
                    var a1 = (k + (q + 1) / (float)sub) * Mathf.PI * 2f / Stripes;
                    Vector3 P(float a, float r, float y) => new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r);
                    Quad(P(a0, R, Height), P(a0, R + Border, Height), P(a1, R + Border, Height), P(a1, R, Height)); // top
                    Quad(P(a0, R + Border, 0f), P(a1, R + Border, 0f), P(a1, R + Border, Height), P(a0, R + Border, Height)); // outer edge
                    Quad(P(a0, R, 0f), P(a1, R, 0f), P(a1, R, Height), P(a0, R, Height)); // inner edge
                }
            }

            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                var i = v.Count;
                v.Add(a); v.Add(b); v.Add(c); v.Add(d);
                t.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3, i, i + 2, i + 1, i, i + 3, i + 2 }); // both sides
            }

            var mesh = new Mesh { name = parity == 0 ? "ZebraGreen" : "ZebraBlack" };
            mesh.SetVertices(v);
            mesh.SetTriangles(t, 0);
            var n = new Vector3[v.Count]; // both windings share vertices: point every normal up so it lights like the floor
            for (var i = 0; i < n.Length; i++)
                n[i] = Vector3.up;
            mesh.normals = n;
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void MeshPart(Transform parent, string name, Mesh mesh, Material mat, List<Renderer> into)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            into?.Add(r);
        }

        private static Renderer Part(Transform parent, PrimitiveType type, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            var col = go.GetComponent<Collider>();
            if (col != null)
                DestroyImmediate(col); // visual only: never part of the motor world
            go.layer = 0;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return r;
        }

        /// <summary>The own Rushling's feet: a circle's green stripes turn yellow once you have stood in it.</summary>
        public void Touch(Vector3 feet)
        {
            for (var i = 0; i < _roots.Count; i++)
            {
                var d = feet - _roots[i].position;
                if (Mathf.Abs(d.y) < 1.2f && d.x * d.x + d.z * d.z < 2.5f * 2.5f)
                    Reached(i);
            }
        }

        /// <summary>Marks checkpoint <paramref name="i"/> as reached (yellow stripes).</summary>
        public void Reached(int i)
        {
            if (i >= 0 && i < _green.Count && _hit != null)
                _green[i].sharedMaterial = _hit;
        }

        private void Update()
        {
            // The zebra ring turns slowly so it catches the eye (still with reduced motion).
            if (GameSettings.ReducedMotion)
                return;
            var a = Time.time * 25f;
            for (var i = 0; i < _roots.Count; i++)
                _roots[i].GetChild(1).localRotation = Quaternion.Euler(0f, a, 0f);
        }
    }
}
