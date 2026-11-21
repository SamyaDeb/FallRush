using System;
using System.Collections.Generic;
using FallRush.Net;
using FallRush.Shared;
using UnityEngine;
using UnityEngine.Rendering;

namespace FallRush.Client
{
    /// <summary>
    /// G4.5 Lid Flip: the client view of every <c>LID_FLIP</c> grid built without per-lid GameObjects (the Final's 9 × 9 top
    /// shelf and 5 × 5 lower shelf). All lids are drawn with GPU instancing (<see cref="Graphics.RenderMeshInstanced"/>), one
    /// batch per state colour, from the pure lid state at the hazard tick (the motor's colliders follow the same states):
    /// <list type="bullet">
    /// <item>solid = the ring's surface hue (G4.6, doc 17 §3.1: top shelf ring 4 Bubblegum, ring 3 Tangerine, ring 2 Soda blue, rings 1–0
    /// Bubblegum (G4.6-5: Sour apple read too close to the lit lemon in luminance); the lower shelf Grape fizz with a Grape-fizz-side outer ring so its edge reads from above). Never mint
    /// (reserved for goal / spring / spawn), never Lemon drop (the lit telegraph is lemon);</item>
    /// <item>lit = sun yellow for the whole lit window (≥ 1 s, no blink: the telegraph never goes dark);</item>
    /// <item>flipped = the lid swings down on a hinge along its edge facing the grid centre (90° over 6 ticks, back over the
    /// last 6), showing its dark underside and the gap;</item>
    /// <item>retire warn = pulses magenta from the warning tick; retired = drops away (1.5 s).</item>
    /// </list>
    /// The lid mesh is a round-cornered capsule lid with a seam across the top (vertex colours; dark underside). No colliders here.
    /// </summary>
    public sealed class LidGridView
    {
        public const int FlipTicks = 6;

        private enum B
        {
            TopPink, Top3, Top2, Low, LowEdge, Lit, Warn, WarnLight, Open, Count,
        }

        private readonly MapInfo _map;
        private readonly Mesh _mesh;
        private readonly Material[] _mats = new Material[(int)B.Count];
        private readonly List<Matrix4x4>[] _batches = new List<Matrix4x4>[(int)B.Count];
        private Matrix4x4[] _scratch = new Matrix4x4[128];
        private LidState[] _states = Array.Empty<LidState>();
        private int[] _lidScratch = Array.Empty<int>();

        public static readonly Color TopRing4 = PaletteTokens.DefaultTop(SurfaceHue.Bubblegum);
        public static readonly Color TopRing3 = PaletteTokens.DefaultTop(SurfaceHue.Tangerine);
        public static readonly Color TopRing2 = PaletteTokens.DefaultTop(SurfaceHue.SodaBlue);
        public static readonly Color TopRing10 = PaletteTokens.DefaultTop(SurfaceHue.Bubblegum); // G4.6-5 (Sour apple: Δluma vs lit < 0.15)
        public static readonly Color LowCalm = PaletteTokens.DefaultTop(SurfaceHue.GrapeFizz);
        public static readonly Color LowEdge = PaletteTokens.DefaultSide(SurfaceHue.GrapeFizz);   // the lower shelf's outer ring
        public static readonly Color LitYellow = new Color(1f, 0.83f, 0.2f);
        public static readonly Color WarnMagenta = new Color(1f, 0.24f, 0.65f);
        public static readonly Color WarnLight = new Color(1f, 0.62f, 0.85f);
        public static readonly Color Open = new Color(0.32f, 0.24f, 0.5f);

        /// <summary>The calm colour of a top-shelf lid in ring <paramref name="ring"/> (candy, G4.6).</summary>
        public static Color TopRingColour(int ring) => ring >= 4 ? TopRing4 : ring == 3 ? TopRing3 : ring == 2 ? TopRing2 : TopRing10;

        /// <summary>Every solid (calm) lid colour: top rings 4 / 3 / 2 / 1–0, lower shelf, lower edge (candy, tests).</summary>
        public static Color[] SolidColours => new[] { TopRing4, TopRing3, TopRing2, TopRing10, LowCalm, LowEdge };

        /// <summary>G4.8: the calm lid colours of a look (same ring hues; soft = the pastel tops, the lower edge its mid-tone side).</summary>
        public static Color TopRingColour(int ring, LookId look) => PaletteTokens.DefaultTop(PaletteTokens.HueOf("CrumbleShelf", ring >= 4 ? "ring4" : ring == 3 ? "ring3" : ring == 2 ? "ring2" : ring == 1 ? "ring1" : "ring0"), look);

        public static Color LowCalmFor(LookId look) => PaletteTokens.DefaultTop(SurfaceHue.GrapeFizz, look);

        /// <summary>The lower shelf's outer ring: candy the Grape fizz side; soft its mid tone deepened 30 % toward ink (luma gap to the lit amber).</summary>
        public static Color LowEdgeFor(LookId look) => look == LookId.Candy
            ? PaletteTokens.DefaultSide(SurfaceHue.GrapeFizz, look)
            : Color.Lerp(PaletteTokens.DefaultSide(SurfaceHue.GrapeFizz, look), PaletteTokens.Hex(0x2A1250), 0.3f);

        public static Color[] SolidColoursFor(LookId look) => look == LookId.Candy
            ? SolidColours
            : new[] { TopRingColour(4, look), TopRingColour(3, look), TopRingColour(2, look), TopRingColour(1, look), LowCalmFor(look), LowEdgeFor(look) };

        /// <summary>The lit telegraph: candy sun yellow; soft a deeper amber (pale lids are lighter than the G4.6 yellow).</summary>
        public static Color LitFor(LookId look) => look == LookId.Candy ? LitYellow : PaletteTokens.Get().softLit;

        private LookId _look;

        public static string BatchName(int b) => ((B)b).ToString();

        public LidGridView(MapInfo map)
        {
            _map = map;
            _mesh = LidMesh();
            var src = map != null ? map.playerMaterial : null;
            for (var i = 0; i < (int)B.Count; i++)
            {
                _mats[i] = Tinted(src, Color.white);
                _batches[i] = new List<Matrix4x4>(96);
            }

            Recolour(LookState.Current);
        }

        /// <summary>The batch colours of a look (rings 4 and 1–0 share Bubblegum: one batch).</summary>
        public static Color[] BatchColours(LookId look) => new[]
        {
            TopRingColour(4, look), TopRingColour(3, look), TopRingColour(2, look), LowCalmFor(look), LowEdgeFor(look), LitFor(look), WarnMagenta, WarnLight, Open,
        };

        /// <summary>G4.8: recolours the batches for a look (the colours are on-screen colours: baked through that look's grade).</summary>
        private void Recolour(LookId look)
        {
            _look = look;
            var cols = BatchColours(look);
            for (var i = 0; i < cols.Length; i++)
            {
                var a = GraphicsQuality.AlbedoFor(cols[i], look);
                _mats[i].SetColor("_BaseColor", new Color(Mathf.LinearToGammaSpace(a.r), Mathf.LinearToGammaSpace(a.g), Mathf.LinearToGammaSpace(a.b), 1f));
                // Soft look: the foam sheen on lid tops is off (walkable), the soft ramp and shadows come from the globals.
            }

            // G4.8z: a calm lid in a cast shadow (the sweeper, the claw pole, a Rushling) keeps 85 % of the sun, so it stays ≥ 0.12
            // luma above the lit amber for deutan / protan eyes (measured: at 0.6 a pink lid in full shadow fell to 0.013).
            // The shadow still shows as a faint lavender patch (~7 % darker); lit / warning lids keep the normal shadow.
            for (var i = 0; i <= (int)B.LowEdge; i++)
                _mats[i].SetFloat(ShadowKeepId, look == LookId.Soft ? CalmShadowKeep : 0.25f);

            // The telegraph colours glow a little so they read in shade too.
            _mats[(int)B.Lit].SetColor("_Emission", look == LookId.Candy ? new Color(0.28f, 0.2f, 0.02f) : new Color(0.3f, 0.15f, 0.01f));
            _mats[(int)B.Warn].SetColor("_Emission", new Color(0.25f, 0.04f, 0.14f));
        }

        /// <summary>G4.8z: share of the sun a calm lid keeps in full cast shadow, soft look (the shader default is 0.25).</summary>
        public const float CalmShadowKeep = 0.85f;

        private static readonly int ShadowKeepId = Shader.PropertyToID("_ShadowKeep");

        /// <summary>True if <paramref name="g"/> is drawn here (no per-lid visuals).</summary>
        public static bool Instanced(HazardLidGrid g) => g != null && (g.visuals == null || g.visuals.Length == 0);

        private static Material Tinted(Material src, Color c)
        {
            var m = src != null ? new Material(src) : new Material(Shader.Find("FallRush/Toon"));
            // G4.6-3: the colours are on-screen (graded) colours; the material gets the albedo that shows them on a lit top (Recolour).
            m.SetColor("_BaseColor", c);
            m.SetShaderPassEnabled("SRPDefaultUnlit", false);
            m.SetFloat("_RushlingMesh", 0f);
            m.SetFloat("_RimAll", 0f); // lid tops are walkable: no view rim
            m.SetFloat("_Gloss", 0f);  // G4.6: no specular glint washing a lid top toward white
            m.enableInstancing = true;
            return m;
        }

        /// <summary>Draw calls this view issues at most per frame (one per non-empty batch).</summary>
        public int Batches
        {
            get
            {
                var n = 0;
                foreach (var b in _batches)
                    n += b.Count > 0 ? 1 : 0;
                return n;
            }
        }

        /// <summary>Tests: per lid, in <see cref="HazardSet.LidStatesAt"/> order (grid by grid), the batch it was drawn in (−1 = hidden) and its centre.</summary>
        public readonly List<int> LastBatch = new List<int>();
        public readonly List<Vector3> LastCenter = new List<Vector3>();

        /// <summary>Batch index of the lit (yellow) state.</summary>
        public const int LitBatch = (int)B.Lit;

        /// <summary>Fills the batches for hazard time <paramref name="t"/> (fractional ticks since the round start).</summary>
        public void Pose(HazardSet hz, double t, bool reduced)
        {
            foreach (var b in _batches)
                b.Clear();
            LastBatch.Clear();
            LastCenter.Clear();
            var grids = _map != null ? _map.lidGrids : null;
            if (grids == null || hz == null)
                return;
            var t0 = (int)Math.Floor(t);
            var f = (float)(t - t0);
            for (var g = 0; g < grids.Length && g < hz.LidGrids.Length; g++)
            {
                if (!Instanced(grids[g]))
                    continue;
                ref readonly var d = ref hz.LidGrids[g];
                if (_states.Length < d.Count)
                {
                    _states = new LidState[d.Count];
                    _lidScratch = new int[d.Count];
                }

                HazardMath.LidStates(in d, t0, _states, _lidScratch);
                var lid = d.LidMm * 0.001f;
                var th = d.ThicknessMm * 0.001f;
                var size = new Vector3(lid, th, lid);
                var top = d.TopMm * 0.001f;
                var lower = g > 0;
                for (var i = 0; i < d.Count; i++)
                {
                    d.LidCenterMm(i, out var x, out var y, out var z);
                    var pos = new Vector3(x * 0.001f, y * 0.001f, z * 0.001f);
                    var ring = d.Ring(i);
                    var calm = lower ? (ring == d.Mid ? B.LowEdge : B.Low) : ring == 3 ? B.Top3 : ring == 2 ? B.Top2 : B.TopPink;
                    var batch = calm;
                    var angle = 0f;
                    switch (_states[i])
                    {
                        case LidState.Lit:
                            batch = B.Lit;
                            break;
                        case LidState.RetireWarn:
                            batch = reduced || ((int)(t / 6.0) & 1) == 0 ? B.Warn : B.WarnLight;
                            break;
                        case LidState.Flipped:
                        {
                            var into = HazardMath.LidFlippedFor(in d, i, t0, _lidScratch) + f;
                            var left = d.FlippedTicks - into;
                            var u = Mathf.Clamp01(Mathf.Min(into, left) / FlipTicks);
                            angle = 90f * (reduced ? (u > 0.5f ? 1f : 0f) : u);
                            batch = into < 2f ? B.Lit : B.Open; // still yellow for the first 2 ticks of the swing
                            break;
                        }
                        case LidState.Retired:
                        {
                            var since = (float)(HazardMath.LidLocalT(in d, t0) + f - d.RetireTick(i)) / 30f;
                            if (since >= 1.5f)
                            {
                                LastBatch.Add(-1);
                                LastCenter.Add(pos);
                                continue;
                            }

                            pos.y -= 0.5f * 20f * since * since;
                            batch = B.Warn;
                            break;
                        }
                    }

                    _batches[(int)batch].Add(LidMatrix(in d, i, pos, size, top, angle));
                    LastBatch.Add((int)batch);
                    LastCenter.Add(pos);
                }
            }
        }

        /// <summary>The lid's matrix: centred at <paramref name="pos"/>, swung down by <paramref name="angleDeg"/> about its hinge.</summary>
        public static Matrix4x4 LidMatrix(in LidGridDef d, int lid, Vector3 pos, Vector3 size, float top, float angleDeg)
        {
            var m = Matrix4x4.TRS(pos, Quaternion.identity, size);
            if (angleDeg <= 0f)
                return m;
            var n = HingeNormal(in d, lid);
            var hinge = new Vector3(pos.x + n.x * size.x * 0.5f, top, pos.z + n.z * size.z * 0.5f);
            var rot = Quaternion.AngleAxis(angleDeg, Vector3.Cross(n, Vector3.up));
            return Matrix4x4.Translate(hinge) * Matrix4x4.Rotate(rot) * Matrix4x4.Translate(-hinge) * m;
        }

        /// <summary>Unit vector (x or z) from the lid's centre to its hinge edge: the edge facing the grid centre (the centre lid: −z).</summary>
        public static Vector3 HingeNormal(in LidGridDef d, int lid)
        {
            var c = lid % d.N - d.Mid;
            var r = lid / d.N - d.Mid;
            if (c == 0 && r == 0)
                return Vector3.back;
            return Math.Abs(c) >= Math.Abs(r) ? new Vector3(-Math.Sign(c), 0f, 0f) : new Vector3(0f, 0f, -Math.Sign(r));
        }

        public void Draw()
        {
            if (_look != LookState.Current)
                Recolour(LookState.Current);
            var soft = _look == LookId.Soft;
            for (var b = 0; b < _batches.Length; b++)
            {
                var list = _batches[b];
                if (list.Count == 0)
                    continue;
                if (_scratch.Length < list.Count)
                    _scratch = new Matrix4x4[list.Count];
                list.CopyTo(_scratch);
                // G4.8: soft look: the lids cast onto the lower shelf and receive the sun shadows.
                var rp = new RenderParams(_mats[b]) { shadowCastingMode = soft ? ShadowCastingMode.On : ShadowCastingMode.Off, receiveShadows = soft };
                Graphics.RenderMeshInstanced(rp, _mesh, 0, _scratch, list.Count);
            }
        }

        /// <summary>Editor shots: the posed lids as temporary renderers under <paramref name="parent"/> (same mesh, materials and matrices).</summary>
        public void Snapshot(Transform parent)
        {
            for (var b = 0; b < _batches.Length; b++)
                foreach (var m in _batches[b])
                {
                    var go = new GameObject($"LidShot_{(B)b}");
                    go.transform.SetParent(parent, false);
                    go.transform.position = m.GetColumn(3);
                    go.transform.rotation = m.rotation;
                    go.transform.localScale = m.lossyScale;
                    go.AddComponent<MeshFilter>().sharedMesh = _mesh;
                    var r = go.AddComponent<MeshRenderer>();
                    r.sharedMaterial = _mats[b];
                    r.shadowCastingMode = ShadowCastingMode.Off;
                }
        }

        // ------------------------------------------------------------------ mesh

        /// <summary>
        /// A 1 × 1 × 1 capsule lid, centred: round corners (r 0.2, 5 segments), a chamfered top edge, a seam across the top
        /// (a thin dark strip along z) and a dark underside, all in vertex colours (alpha 0: no flash mask).
        /// </summary>
        public static Mesh LidMesh()
        {
            const float h = 0.5f, cr = 0.2f, ch = 0.06f, inset = 0.035f;
            var outline = new List<Vector2>();
            foreach (var (cx, cz, a0) in new[] { (h - cr, h - cr, 0f), (-h + cr, h - cr, 90f), (-h + cr, -h + cr, 180f), (h - cr, -h + cr, 270f) })
                for (var k = 0; k <= 4; k++)
                {
                    var a = (a0 + k * 22.5f) * Mathf.Deg2Rad;
                    outline.Add(new Vector2(cx + Mathf.Cos(a) * cr, cz + Mathf.Sin(a) * cr));
                }

            var v = new List<Vector3>();
            var n = new List<Vector3>();
            var col = new List<Color>();
            var tri = new List<int>();
            var top = new Color(1f, 1f, 1f, 0f);
            var side = new Color(0.86f, 0.86f, 0.9f, 0f);
            var under = new Color(0.36f, 0.32f, 0.45f, 0f);
            var seam = new Color(0.6f, 0.6f, 0.7f, 0f);
            var count = outline.Count;

            // Top cap (inset, a fan).
            var c0 = v.Count;
            v.Add(new Vector3(0f, h, 0f));
            n.Add(Vector3.up);
            col.Add(top);
            foreach (var p in outline)
            {
                var q = p * (1f - inset * 2f);
                v.Add(new Vector3(q.x, h, q.y));
                n.Add(Vector3.up);
                col.Add(top);
            }

            for (var i = 0; i < count; i++)
                tri.AddRange(new[] { c0, c0 + 1 + (i + 1) % count, c0 + 1 + i });

            // Chamfer ring: inset top → full outline at h − ch.
            for (var i = 0; i < count; i++)
            {
                var p = outline[i];
                var q = outline[(i + 1) % count];
                var s = v.Count;
                var pi = p * (1f - inset * 2f);
                var qi = q * (1f - inset * 2f);
                var nn = new Vector3(p.x + q.x, 0f, p.y + q.y).normalized;
                var cn = (nn + Vector3.up).normalized;
                v.Add(new Vector3(pi.x, h, pi.y));
                v.Add(new Vector3(qi.x, h, qi.y));
                v.Add(new Vector3(q.x, h - ch, q.y));
                v.Add(new Vector3(p.x, h - ch, p.y));
                for (var k = 0; k < 4; k++)
                {
                    n.Add(cn);
                    col.Add(top);
                }

                tri.AddRange(new[] { s, s + 1, s + 2, s, s + 2, s + 3 });
            }

            // Sides.
            for (var i = 0; i < count; i++)
            {
                var p = outline[i];
                var q = outline[(i + 1) % count];
                var s = v.Count;
                var nn = new Vector3(p.x + q.x, 0f, p.y + q.y).normalized;
                v.Add(new Vector3(p.x, h - ch, p.y));
                v.Add(new Vector3(q.x, h - ch, q.y));
                v.Add(new Vector3(q.x, -h, q.y));
                v.Add(new Vector3(p.x, -h, p.y));
                for (var k = 0; k < 4; k++)
                {
                    n.Add(nn);
                    col.Add(k < 2 ? side : Color.Lerp(side, under, 0.5f));
                }

                tri.AddRange(new[] { s, s + 1, s + 2, s, s + 2, s + 3 });
            }

            // Underside (dark, a fan facing down).
            var b0 = v.Count;
            v.Add(new Vector3(0f, -h, 0f));
            n.Add(Vector3.down);
            col.Add(under);
            foreach (var p in outline)
            {
                v.Add(new Vector3(p.x, -h, p.y));
                n.Add(Vector3.down);
                col.Add(under);
            }

            for (var i = 0; i < count; i++)
                tri.AddRange(new[] { b0, b0 + 1 + i, b0 + 1 + (i + 1) % count });

            // The capsule seam: a thin strip across the top along z (a hair above the cap).
            var sv = v.Count;
            const float sw = 0.014f, sl = 0.43f, sy = h + 0.004f;
            v.Add(new Vector3(-sw, sy, -sl));
            v.Add(new Vector3(-sw, sy, sl));
            v.Add(new Vector3(sw, sy, sl));
            v.Add(new Vector3(sw, sy, -sl));
            for (var k = 0; k < 4; k++)
            {
                n.Add(Vector3.up);
                col.Add(seam);
            }

            tri.AddRange(new[] { sv, sv + 1, sv + 2, sv, sv + 2, sv + 3 });

            var m = new Mesh { name = "CapsuleLid" };
            m.SetVertices(v);
            m.SetNormals(n);
            m.SetColors(col);
            m.SetTriangles(tri, 0);
            m.RecalculateBounds();
            return m;
        }
    }
}
