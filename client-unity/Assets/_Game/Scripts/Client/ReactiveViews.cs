using System.Collections.Generic;
using FallRush.Net;
using FallRush.Shared;
using UnityEngine;

namespace FallRush.Client
{
    /// <summary>
    /// Client visuals of the M4 kit (greybox), driven by the same pure functions and replicated reactive state the motor
    /// uses, at the hazard render tick:
    /// <list type="bullet">
    /// <item>GOO_PLANE: a glossy purple glitter plane at <see cref="HazardMath.GooYMm"/> with drifting specks.</item>
    /// <item>CRUMBLE_TILE: 243 rounded-corner tiles in alternating colours per shelf, drawn with GPU instancing
    /// (<see cref="Graphics.RenderMeshInstanced"/>); a touched tile shakes and turns yellow (the 0.6 s telegraph), then falls away.</item>
    /// <item>Falling blocks: shake, drop, come back.</item>
    /// <item>STAR_PICKUP: spinning stars (gold = bigger) from the timeline, hidden once collected; dropped stars hop.</item>
    /// <item>Crumble Shelf spinners: hang raised and grey until they start at 60 s.</item>
    /// <item>Lobby: a capsule "drops in" on every new player's spawn.</item>
    /// </list>
    /// </summary>
    public sealed class ReactiveViews : MonoBehaviour
    {
        private MapInfo _map;
        private Transform _goo, _gooSpecks, _gooFoam;
        private Mesh _tileMesh;
        private Material[] _tileMats;
        private Material _wobbleMat;
        private readonly List<Matrix4x4>[] _batches = { new List<Matrix4x4>(), new List<Matrix4x4>(), new List<Matrix4x4>(), new List<Matrix4x4>(), new List<Matrix4x4>(), new List<Matrix4x4>(), new List<Matrix4x4>() };
        private Matrix4x4[] _scratch = new Matrix4x4[256];
        private Material _starMat, _goldMat, _gooMat, _greyMat;
        private Vector3[] _blockRest;
        private readonly List<int> _live = new List<int>();
        private readonly bool[] _seen = new bool[Protocol.MaxPlayers];
        private Vector3[] _barRest;
        private int _sweeperStart = int.MinValue;

        /// <summary>Set when the goo starts rising / the first tile drops / the spinners are about to start (subtitles).</summary>
        public bool GooRising { get; private set; }
        public bool GooWarning { get; private set; }
        public bool TilesCrumbling { get; private set; }
        public bool SpinnersSoon { get; private set; }

        private void Start()
        {
            _map = MapInfo.Current;
            if (_map == null)
                return;
            var lit = _map.playerMaterial;
            _starMat = Tinted(lit, UiKit.SunYellow);
            _goldMat = Tinted(lit, UiKit.Hex(0xFFA51F));
            _greyMat = Tinted(lit, new Color(0.7f, 0.7f, 0.75f));
            if (_map.goo.Length > 0)
            {
                // G2.3: glossy purple glitter goo: a glossy toon surface, emissive glitter specks in one mesh (1 draw,
                // drifting), and a bright foam lip along every wall it climbs, so the rising edge reads from anywhere.
                var def = _map.goo[0].def;
                _gooMat = Tinted(lit, UiKit.Hex(0x9B5CFF));
                _gooMat.SetFloat("_Gloss", 0.95f);
                _gooMat.SetFloat("_RimStrength", 0.65f);
                _gooMat.SetColor("_RimColor", UiKit.Hex(0xE7D2FF));
                _gooMat.SetColor("_Emission", new Color(0.12f, 0.04f, 0.22f));
                var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(g.GetComponent<Collider>());
                g.name = "GooPlane";
                g.transform.SetParent(transform, false);
                g.transform.localScale = new Vector3(def.HalfXMm * 0.002f, 0.3f, def.HalfZMm * 0.002f);
                g.GetComponent<MeshRenderer>().sharedMaterial = _gooMat;
                _goo = g.transform;
                var speckMat = Tinted(lit, UiKit.Hex(0xF4E9FF));
                speckMat.SetFloat("_Unlit", 1f);
                speckMat.SetColor("_Emission", new Color(0.55f, 0.45f, 0.75f));
                _gooSpecks = new GameObject("GooGlitter").transform;
                _gooSpecks.SetParent(transform, false);
                _gooSpecks.gameObject.AddComponent<MeshFilter>().sharedMesh = GlitterMesh(def.HalfXMm * 0.001f, def.HalfZMm * 0.001f, 420);
                _gooSpecks.gameObject.AddComponent<MeshRenderer>().sharedMaterial = speckMat;
                var foamMat = Tinted(lit, UiKit.Hex(0xD9C2FF));
                foamMat.SetColor("_Emission", new Color(0.22f, 0.12f, 0.35f));
                _gooFoam = new GameObject("GooFoam").transform;
                _gooFoam.SetParent(transform, false);
                _gooFoam.gameObject.AddComponent<MeshFilter>().sharedMesh = FoamMesh(12f, def.HalfXMm * 0.001f);
                _gooFoam.gameObject.AddComponent<MeshRenderer>().sharedMaterial = foamMat;
            }

            if (_map.grids.Length > 0)
            {
                _tileMesh = RoundedTile();
                var cols = new[] { UiKit.Hex(0xFFB3D9), UiKit.CloudWhite, UiKit.Hex(0xA8F0D6), UiKit.CloudWhite, UiKit.Hex(0xFFE58A), UiKit.CloudWhite };
                _tileMats = new Material[6];
                for (var i = 0; i < 6; i++)
                {
                    _tileMats[i] = Tinted(lit, cols[i]);
                    _tileMats[i].enableInstancing = true;
                }

                _wobbleMat = Tinted(lit, UiKit.SunYellow);
                _wobbleMat.enableInstancing = true;
                _sweeperStart = _map.grids[0].spinnerStartA;
            }

            if (_map.fallingBlocks.Length > 0)
            {
                _blockRest = new Vector3[_map.fallingBlocks.Length];
                for (var i = 0; i < _blockRest.Length; i++)
                    _blockRest[i] = _map.fallingBlocks[i].visual != null ? _map.fallingBlocks[i].visual.position : Vector3.zero;
            }

            if (_map.sweepers.Length > 0)
            {
                _barRest = new Vector3[_map.sweepers.Length];
                for (var i = 0; i < _barRest.Length; i++)
                    _barRest[i] = _map.sweepers[i].barVisual != null ? _map.sweepers[i].barVisual.localPosition : Vector3.zero;
            }
        }

        private static Material Tinted(Material src, Color c)
        {
            var m = src != null ? new Material(src) : new Material(Shader.Find("FallRush/Toon"));
            m.SetColor("_BaseColor", c);
            m.SetShaderPassEnabled("SRPDefaultUnlit", false); // G2: the outline pass is for Rushlings only
            m.SetFloat("_RushlingMesh", 0f);
            return m;
        }

        /// <summary>A 1 × 1 × 1 tile with chamfered vertical edges (the rounded-corner look), centred.</summary>
        private static Mesh RoundedTile()
        {
            const float h = 0.5f, c = 0.12f;
            var ring = new[]
            {
                new Vector2(-h + c, -h), new Vector2(h - c, -h), new Vector2(h, -h + c), new Vector2(h, h - c),
                new Vector2(h - c, h), new Vector2(-h + c, h), new Vector2(-h, h - c), new Vector2(-h, -h + c),
            };
            var v = new List<Vector3>();
            var t = new List<int>();
            // Top and bottom caps (fans).
            foreach (var (y, up) in new[] { (h, true), (-h, false) })
            {
                var start = v.Count;
                v.Add(new Vector3(0f, y, 0f));
                foreach (var p in ring)
                    v.Add(new Vector3(p.x, y, p.y));
                for (var i = 0; i < 8; i++)
                {
                    var a = start + 1 + i;
                    var b = start + 1 + (i + 1) % 8;
                    if (up)
                        t.AddRange(new[] { start, b, a });
                    else
                        t.AddRange(new[] { start, a, b });
                }
            }

            // Sides.
            for (var i = 0; i < 8; i++)
            {
                var p = ring[i];
                var q = ring[(i + 1) % 8];
                var s = v.Count;
                v.Add(new Vector3(p.x, h, p.y));
                v.Add(new Vector3(q.x, h, q.y));
                v.Add(new Vector3(q.x, -h, q.y));
                v.Add(new Vector3(p.x, -h, p.y));
                t.AddRange(new[] { s, s + 1, s + 2, s, s + 2, s + 3 });
            }

            var m = new Mesh { name = "RoundedTile" };
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        private void LateUpdate()
        {
            var cs = ClientSim.Instance;
            if (cs == null || _map == null)
                return;
            var hz = cs.Hazards;
            if (hz == null)
                return;
            var tick = cs.HazardRenderTick;
            if (tick < 1)
                return;
            var t = tick - hz.RoundStartTick;
            var whole = (uint)System.Math.Max(0, (long)System.Math.Floor(tick));
            var rx = hz.Reactive;
            var reduced = GameSettings.ReducedMotion;

            // Goo.
            if (_goo != null && hz.Goo.Length > 0)
            {
                var d = hz.Goo[0];
                var y = HazardMath.GooYMm(in d, (int)System.Math.Floor(t)) * 0.001f;
                var gp = new Vector3(d.CenterXMm * 0.001f, y - 0.15f, d.CenterZMm * 0.001f);
                _goo.position = gp;
                _gooFoam.position = gp + new Vector3(0f, 0.16f, 0f);
                _gooSpecks.position = gp + new Vector3(0f, 0.155f + (reduced ? 0f : Mathf.Sin((float)t * 0.11f) * 0.02f), 0f);
                if (!reduced)
                    _gooSpecks.rotation = Quaternion.Euler(0f, (float)(t * 0.03), 0f);
                GooWarning = t >= d.DelayTicks - d.WarnTicks && t < d.DelayTicks + 90;
                GooRising = t >= d.DelayTicks;
            }

            // Crumble tiles (instanced).
            if (_tileMesh != null && hz.Grids.Length > 0 && rx != null)
            {
                var g = hz.Grids[0];
                foreach (var b in _batches)
                    b.Clear();
                var crumbling = false;
                for (var id = 0; id < g.Count; id++)
                {
                    g.Center(id, out var x, out var yy, out var z);
                    var c = rx.TileContactAt(id, (uint)tick);
                    var pos = new Vector3(x * 0.001f, yy * 0.001f, z * 0.001f);
                    var size = new Vector3((g.TileMm - g.GapMm) * 0.001f, g.ThicknessMm * 0.001f, (g.TileMm - g.GapMm) * 0.001f);
                    var batch = g.Shelf(id) * 2 + ((id % g.PerShelf % g.Cols + id % g.PerShelf / g.Cols) & 1);
                    if (c != 0)
                    {
                        crumbling = true;
                        var age = (float)(tick - c);
                        var back = rx.TileReturnsIn(id, (uint)tick);
                        if (back >= 0 && back <= 45)
                        {
                            // G1: a top-shelf tile coming back — blinks in place for the last 1.5 s (telegraph), solid at 0.
                            if (!reduced && (back / 5) % 2 == 1)
                                continue;
                            _batches[6].Add(Matrix4x4.TRS(pos, Quaternion.identity, size * (back > 0 ? 0.92f : 1f)));
                            continue;
                        }

                        if (age >= g.WobbleTicks + 24)
                            continue; // gone
                        if (age >= g.WobbleTicks)
                        {
                            var fall = (age - g.WobbleTicks) / 30f;
                            pos.y -= 20f * fall * fall; // falls away
                        }
                        else if (!reduced)
                        {
                            pos.x += Mathf.Sin(age * 2.1f) * 0.05f;
                            pos.z += Mathf.Cos(age * 2.7f) * 0.05f;
                        }

                        batch = 6;
                    }

                    _batches[batch].Add(Matrix4x4.TRS(pos, Quaternion.identity, size));
                }

                TilesCrumbling = crumbling;
                for (var b = 0; b < _batches.Length; b++)
                {
                    var list = _batches[b];
                    if (list.Count == 0)
                        continue;
                    if (_scratch.Length < list.Count)
                        _scratch = new Matrix4x4[list.Count];
                    list.CopyTo(_scratch);
                    var rp = new RenderParams(b == 6 ? _wobbleMat : _tileMats[b]) { shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off };
                    Graphics.RenderMeshInstanced(rp, _tileMesh, 0, _scratch, list.Count);
                }
            }

            // Crumble Shelf spinners: raised and grey until they start.
            if (_barRest != null && hz.SweeperStartT != int.MinValue)
            {
                var armed = t >= hz.SweeperStartT;
                SpinnersSoon = t >= hz.SweeperStartT - 120 && t < hz.SweeperStartT + 60;
                for (var i = 0; i < _map.sweepers.Length; i++)
                {
                    var bar = _map.sweepers[i].barVisual;
                    if (bar == null)
                        continue;
                    var lift = armed ? 0f : Mathf.Clamp01((float)(hz.SweeperStartT - t) / 60f) * 4f;
                    bar.localPosition = _barRest[i] + Vector3.up * lift;
                }
            }

            // Falling blocks.
            if (_blockRest != null && rx != null)
                for (var i = 0; i < _map.fallingBlocks.Length; i++)
                {
                    var v = _map.fallingBlocks[i].visual;
                    if (v == null)
                        continue;
                    var trig = rx.BlockActiveTrigger(i, whole);
                    var p = _blockRest[i];
                    if (trig != 0)
                    {
                        var d = hz.Blocks[i];
                        var age = (float)(tick - trig);
                        if (age < d.DelayTicks)
                        {
                            if (!reduced)
                                p += new Vector3(Mathf.Sin(age * 2.3f) * 0.06f, 0f, Mathf.Cos(age * 1.9f) * 0.06f);
                        }
                        else
                        {
                            var f = (age - d.DelayTicks) / 30f;
                            p.y -= Mathf.Min(30f, 20f * f * f);
                        }
                    }

                    v.position = p;
                }

            // Stars.
            if (hz.StarFields.Length > 0 && rx != null)
            {
                var f = hz.StarFields[0];
                var ti = (int)System.Math.Floor(t);
                _live.Clear();
                SkillRules.LiveStars(hz, ti, whole, _live);
                var n = 0;
                foreach (var id in _live)
                {
                    HazardMath.StarSlot(in f, hz.Seed, id, out var a, out _, out _, out var value);
                    HazardMath.StarAnchorMm(in f, hz, a, ti, out var x, out var y, out var z);
                    var sp = new Vector3(x * 0.001f, y * 0.001f + (reduced ? 0f : Mathf.Sin((float)t * 0.12f + id) * 0.08f), z * 0.001f);
                    var sr = Quaternion.Euler(0f, reduced ? 0f : (float)(t * 6.0 + id * 40), 0f);
                    AddStar(sp, sr, value > 1, (float)t + id * 3.1f, reduced);
                    n++;
                }

                for (var d = 0; d < rx.Drops.Count; d++)
                {
                    var e = rx.Drops[d];
                    var age = (int)(whole - e.Tick);
                    if (age < 0 || age >= f.DropLifeTicks || rx.StarTaken(HazardMath.StarDropBase + d, whole))
                        continue;
                    HazardMath.DropPosMm(in f, e.Xcm * 10, e.Ycm * 10, e.Zcm * 10, (uint)(d + 1), age, out var x, out var y, out var z);
                    AddStar(new Vector3(x * 0.001f, y * 0.001f, z * 0.001f), Quaternion.Euler(0f, age * 12f, 0f), false, age, reduced);
                    n++;
                }

                DrawStars();
            }

            LobbyCapsules(cs);
        }

        // G2.3 stars: a chunky extruded star, drawn instanced: normal = sun yellow 0.65 m, golden = bigger, deeper gold,
        // emissive, with a spinning halo ring (clearly different in shape, size and glow, not only in colour).
        private Mesh _starMesh, _haloMesh;
        private Material _starInst, _goldInst, _haloInst;
        private Matrix4x4[] _starM = new Matrix4x4[64], _goldM = new Matrix4x4[16], _haloM = new Matrix4x4[16];
        private int _nStar, _nGold;

        private void AddStar(Vector3 p, Quaternion rot, bool gold, float phase, bool reduced)
        {
            if (_starMesh == null)
            {
                _starMesh = RushlingParts.Shapes.Polygon(RushlingParts.Shapes.StarOutline(0.5f, 0.22f, 5), 0.22f);
                _haloMesh = RushlingParts.Shapes.Polygon(RushlingParts.Shapes.StarOutline(0.62f, 0.56f, 12), 0.04f);
                _starInst = Tinted(_map.playerMaterial, UiKit.SunYellow);
                _starInst.SetColor("_Emission", new Color(0.18f, 0.14f, 0.02f));
                _starInst.enableInstancing = true;
                _goldInst = Tinted(_map.playerMaterial, UiKit.Hex(0xFFB020));
                _goldInst.SetColor("_Emission", new Color(0.55f, 0.32f, 0.02f));
                _goldInst.SetFloat("_Gloss", 0.9f);
                _goldInst.enableInstancing = true;
                _haloInst = Tinted(_map.playerMaterial, UiKit.Hex(0xFFF2B0));
                _haloInst.SetFloat("_Unlit", 1f);
                _haloInst.SetColor("_Emission", new Color(0.6f, 0.5f, 0.2f));
                _haloInst.enableInstancing = true;
            }

            var face = rot * Quaternion.Euler(0f, 0f, 0f);
            if (gold)
            {
                if (_nGold >= _goldM.Length)
                    return;
                var pulse = reduced ? 1f : 1f + Mathf.Sin(phase * 0.25f) * 0.06f;
                _goldM[_nGold] = Matrix4x4.TRS(p + Vector3.up * 0.15f, face, Vector3.one * 1.0f * pulse);
                _haloM[_nGold] = Matrix4x4.TRS(p + Vector3.up * 0.15f, Quaternion.Euler(90f, reduced ? 0f : phase * 4f, 0f), Vector3.one * 1.5f * pulse);
                _nGold++;
            }
            else
            {
                if (_nStar >= _starM.Length)
                    return;
                _starM[_nStar++] = Matrix4x4.TRS(p, face, Vector3.one * 0.65f);
            }
        }

        private void DrawStars()
        {
            if (_starMesh != null)
            {
                if (_nStar > 0)
                    Graphics.RenderMeshInstanced(new RenderParams(_starInst) { shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off }, _starMesh, 0, _starM, _nStar);
                if (_nGold > 0)
                {
                    Graphics.RenderMeshInstanced(new RenderParams(_goldInst) { shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off }, _starMesh, 0, _goldM, _nGold);
                    Graphics.RenderMeshInstanced(new RenderParams(_haloInst) { shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off }, _haloMesh, 0, _haloM, _nGold);
                }
            }

            _nStar = _nGold = 0;
        }

        /// <summary>Glitter specks: tiny diamonds scattered over the goo surface, one mesh.</summary>
        private static Mesh GlitterMesh(float hx, float hz, int count)
        {
            var rnd = new System.Random(91);
            var v = new List<Vector3>();
            var t = new List<int>();
            for (var i = 0; i < count; i++)
            {
                var c = new Vector3(((float)rnd.NextDouble() * 2f - 1f) * hx, 0f, ((float)rnd.NextDouble() * 2f - 1f) * hz);
                if (Mathf.Abs(c.x) < 12f && Mathf.Abs(c.z) < 12f)
                    continue; // inside the tower core
                var s = 0.05f + (float)rnd.NextDouble() * 0.07f;
                var a = (float)rnd.NextDouble() * Mathf.PI;
                var dx = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * s;
                var dz = new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a)) * s;
                var k = v.Count;
                v.Add(c + dx); v.Add(c + dz); v.Add(c - dx); v.Add(c - dz);
                t.Add(k); t.Add(k + 1); t.Add(k + 2);
                t.Add(k); t.Add(k + 2); t.Add(k + 3);
            }

            var m = new Mesh { name = "GooGlitter" };
            m.SetVertices(v);
            var n = new Vector3[v.Count];
            for (var i = 0; i < n.Length; i++)
                n[i] = Vector3.up;
            m.normals = n;
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }

        /// <summary>The goo's foam lip: a raised bright band hugging the tower core's four walls and the outer edge.</summary>
        private static Mesh FoamMesh(float core, float outer)
        {
            var v = new List<Vector3>();
            var t = new List<int>();
            void Ring(float r, float w)
            {
                var corners = new[] { new Vector2(-r, -r), new Vector2(r, -r), new Vector2(r, r), new Vector2(-r, r) };
                for (var i = 0; i < 4; i++)
                {
                    var a = corners[i];
                    var b = corners[(i + 1) % 4];
                    var n2 = new Vector2(a.y - b.y, b.x - a.x).normalized * (w * (r > core + 1f ? -1f : 1f));
                    var k = v.Count;
                    v.Add(new Vector3(a.x, 0f, a.y)); v.Add(new Vector3(b.x, 0f, b.y));
                    v.Add(new Vector3(b.x + n2.x, 0f, b.y + n2.y)); v.Add(new Vector3(a.x + n2.x, 0f, a.y + n2.y));
                    t.Add(k); t.Add(k + 2); t.Add(k + 1);
                    t.Add(k); t.Add(k + 3); t.Add(k + 2);
                    t.Add(k); t.Add(k + 1); t.Add(k + 2);
                    t.Add(k); t.Add(k + 2); t.Add(k + 3);
                }
            }

            Ring(core, 0.55f);
            var m = new Mesh { name = "GooFoam" };
            m.SetVertices(v);
            var n = new Vector3[v.Count];
            for (var i = 0; i < n.Length; i++)
                n[i] = Vector3.up;
            m.normals = n;
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }

        // ------------------------------------------------------------------ G3.4 #1: lobby capsules

        /// <summary>The live instance (the map view asks whether a capsule still hides a Rushling).</summary>
        public static ReactiveViews Current { get; private set; }

        private void OnEnable() => Current = this;

        private void OnDisable()
        {
            if (Current == this)
                Current = null;
        }

        private readonly float[] _capsuleBorn = new float[Protocol.MaxPlayers];
        private readonly Transform[] _capTop = new Transform[Protocol.MaxPlayers], _capBottom = new Transform[Protocol.MaxPlayers];
        private readonly Vector3[] _capAt = new Vector3[Protocol.MaxPlayers];
        private const float CapDrop = 0.75f, CapPop = 0.5f;

        /// <summary>True while a joining player's capsule has not popped open yet (the Rushling inside is not shown).</summary>
        public bool CapsuleHides(int slot) => slot >= 0 && slot < _capsuleBorn.Length && _capTop[slot] != null && Time.time - _capsuleBorn[slot] < CapDrop + 0.08f;

        /// <summary>
        /// Lobby (signature moment 1): each joining player arrives in a giant capsule toy in their preset's colours that
        /// drops onto their spawn, bounces, and pops open (the top flies off spinning) to reveal their Rushling.
        /// </summary>
        private void LobbyCapsules(ClientSim cs)
        {
            if (_map.MapId != MapPoolV1.Lobby || !cs.HasSnapshots)
                return;
            ref readonly var n = ref cs.Newest;
            var spawns = _map.SpawnPoints();
            for (var i = 0; i < n.Count; i++)
            {
                var slot = n.Players[i].Slot;
                if (_seen[slot])
                    continue;
                _seen[slot] = true;
                var preset = cs.TryGetSlotInfo(slot, out var info) ? info.Preset : CosmeticPresets.ForBot(slot);
                var (top, bottom) = CapsuleHalves(UiKit.Hex(CosmeticPresets.Get(preset).Shell));
                _capTop[slot] = top;
                _capBottom[slot] = bottom;
                _capsuleBorn[slot] = Time.time;
                _capAt[slot] = new Vector3(spawns[slot].X, spawns[slot].Y, spawns[slot].Z);
            }

            var reduced = GameSettings.ReducedMotion;
            for (var slot = 0; slot < _capTop.Length; slot++)
            {
                var top = _capTop[slot];
                if (top == null)
                    continue;
                var age = Time.time - _capsuleBorn[slot];
                var at = _capAt[slot];
                if (age > CapDrop + CapPop + 0.3f)
                {
                    Destroy(top.gameObject);
                    Destroy(_capBottom[slot].gameObject);
                    _capTop[slot] = _capBottom[slot] = null;
                    continue;
                }

                if (age < CapDrop)
                {
                    var f = reduced ? 1f : Mathf.Clamp01(age / (CapDrop * 0.8f));
                    var y = 9f * (1f - f * f);
                    var bounce = f >= 1f ? Mathf.Sin((age - CapDrop * 0.8f) / (CapDrop * 0.2f) * Mathf.PI) * 0.25f : 0f;
                    var squash = f >= 1f ? 1f - Mathf.Sin((age - CapDrop * 0.8f) / (CapDrop * 0.2f) * Mathf.PI) * 0.12f : 1f;
                    var p = at + Vector3.up * (y + bounce);
                    top.SetPositionAndRotation(p, Quaternion.Euler(0f, age * 200f, 0f));
                    _capBottom[slot].SetPositionAndRotation(p, Quaternion.Euler(0f, age * 200f, 0f));
                    top.localScale = _capBottom[slot].localScale = new Vector3(1.45f * (2f - squash), 1.45f * squash, 1.45f * (2f - squash));
                }
                else
                {
                    if (!top.gameObject.name.EndsWith("!"))
                    {
                        top.gameObject.name += "!";
                        Fx.Confetti(at + Vector3.up * 0.6f, 24);
                        Fx.Poof(at);
                        GameAudio.Play(GameAudio.Sfx.CapsulePop, at, 0.8f);
                    }

                    var u = Mathf.Clamp01((age - CapDrop) / CapPop);
                    top.position = at + new Vector3(0f, 0.2f + u * 3.5f - u * u * 1.5f, -u * 1.2f);
                    top.rotation = Quaternion.Euler(-u * 220f, age * 200f, 0f);
                    top.localScale = Vector3.one * 1.45f * (1f - u * 0.7f);
                    _capBottom[slot].position = at - Vector3.up * u * 0.5f;
                    _capBottom[slot].localScale = Vector3.one * 1.45f * Mathf.Max(0.01f, 1f - u);
                }
            }
        }

        private static Mesh _capTopMesh, _capBottomMesh;

        /// <summary>A capsule toy as two halves: a white dome on top, the player's colour below (our Rushling shell at 1.45×).</summary>
        private (Transform top, Transform bottom) CapsuleHalves(Color c)
        {
            if (_capTopMesh == null)
            {
                var t = new RushlingParts.Combiner();
                t.Add(RushlingParts.Shapes.Dome, Matrix4x4.identity, Color.white, RushlingParts.FlagNone);
                t.Add(RushlingParts.Shapes.ThinTorus, Matrix4x4.TRS(new Vector3(0f, ProcMesh.SeamY, 0f), Quaternion.identity, new Vector3(ProcMesh.R + 0.004f, 0.4f, ProcMesh.R + 0.004f)), UiKit.Ink, RushlingParts.FlagNone);
                _capTopMesh = t.Build("CapsuleTop");
                var b = new RushlingParts.Combiner();
                b.Add(RushlingParts.Shapes.Shell, Matrix4x4.identity, Color.white, RushlingParts.FlagNone);
                _capBottomMesh = b.Build("CapsuleBottom");
            }

            var mat = _map.playerMaterial;
            Transform Part(string name, Mesh mesh, Color tint)
            {
                var go = new GameObject(name);
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                var mpb = new MaterialPropertyBlock();
                mpb.SetColor("_BaseColor", tint);
                r.SetPropertyBlock(mpb);
                return go.transform;
            }

            return (Part("CapsuleTop", _capTopMesh, Color.white), Part("CapsuleBottom", _capBottomMesh, c));
        }
    }
}
