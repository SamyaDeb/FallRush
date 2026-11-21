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
        private Transform _goo;
        private Mesh _tileMesh;
        private Material[] _tileMats;
        private Material _wobbleMat;
        private readonly List<Matrix4x4>[] _batches = { new List<Matrix4x4>(), new List<Matrix4x4>(), new List<Matrix4x4>(), new List<Matrix4x4>(), new List<Matrix4x4>(), new List<Matrix4x4>(), new List<Matrix4x4>() };
        private Matrix4x4[] _scratch = new Matrix4x4[256];
        private readonly List<Transform> _stars = new List<Transform>();
        private Material _starMat, _goldMat, _gooMat, _greyMat;
        private Vector3[] _blockRest;
        private readonly List<int> _live = new List<int>();
        private readonly bool[] _seen = new bool[Protocol.MaxPlayers];
        private readonly List<(Transform t, float born, Vector3 at)> _drops = new List<(Transform, float, Vector3)>();
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
                var def = _map.goo[0].def;
                _gooMat = _map.unlitMaterial != null ? new Material(_map.unlitMaterial) : Tinted(lit, UiKit.Hex(0x9B5CFF));
                _gooMat.SetColor("_BaseColor", UiKit.Hex(0x9B5CFF));
                var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(g.GetComponent<Collider>());
                g.name = "GooPlane";
                g.transform.SetParent(transform, false);
                g.transform.localScale = new Vector3(def.HalfXMm * 0.002f, 0.3f, def.HalfZMm * 0.002f);
                g.GetComponent<MeshRenderer>().sharedMaterial = _gooMat;
                _goo = g.transform;
                var speck = Tinted(lit, UiKit.Hex(0xE7D2FF));
                for (var i = 0; i < 60; i++)
                {
                    var sp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    Destroy(sp.GetComponent<Collider>());
                    sp.transform.SetParent(_goo, false);
                    var r = new System.Random(i * 7 + 3);
                    sp.transform.localPosition = new Vector3((float)r.NextDouble() - 0.5f, 0.55f, (float)r.NextDouble() - 0.5f);
                    sp.transform.localScale = new Vector3(0.006f, 0.4f, 0.006f);
                    sp.GetComponent<MeshRenderer>().sharedMaterial = speck;
                }
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
                _goo.position = new Vector3(d.CenterXMm * 0.001f, y - 0.15f, d.CenterZMm * 0.001f);
                GooWarning = t >= d.DelayTicks - d.WarnTicks && t < d.DelayTicks + 90;
                GooRising = t >= d.DelayTicks;
                if (!reduced)
                    _goo.rotation = Quaternion.Euler(0f, (float)(t * 0.05), 0f);
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
                    var s = Star(n++, value > 1);
                    s.position = new Vector3(x * 0.001f, y * 0.001f + (reduced ? 0f : Mathf.Sin((float)t * 0.12f + id) * 0.08f), z * 0.001f);
                    s.rotation = Quaternion.Euler(0f, reduced ? 0f : (float)(t * 6.0 + id * 40), 0f);
                }

                for (var d = 0; d < rx.Drops.Count; d++)
                {
                    var e = rx.Drops[d];
                    var age = (int)(whole - e.Tick);
                    if (age < 0 || age >= f.DropLifeTicks || rx.StarTaken(HazardMath.StarDropBase + d, whole))
                        continue;
                    HazardMath.DropPosMm(in f, e.Xcm * 10, e.Ycm * 10, e.Zcm * 10, (uint)(d + 1), age, out var x, out var y, out var z);
                    var s = Star(n++, false);
                    s.position = new Vector3(x * 0.001f, y * 0.001f, z * 0.001f);
                    s.rotation = Quaternion.Euler(0f, age * 12f, 0f);
                }

                for (var i = n; i < _stars.Count; i++)
                    if (_stars[i].gameObject.activeSelf)
                        _stars[i].gameObject.SetActive(false);
            }

            LobbyCapsules(cs);
        }

        private Transform Star(int i, bool gold)
        {
            while (_stars.Count <= i)
            {
                var root = new GameObject("Star").transform;
                root.SetParent(transform, false);
                for (var k = 0; k < 3; k++)
                {
                    var arm = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    Destroy(arm.GetComponent<Collider>());
                    arm.transform.SetParent(root, false);
                    arm.transform.localRotation = Quaternion.Euler(0f, 0f, k * 60f);
                    arm.transform.localScale = new Vector3(0.12f, 0.6f, 0.12f);
                }

                _stars.Add(root);
            }

            var s = _stars[i];
            if (!s.gameObject.activeSelf)
                s.gameObject.SetActive(true);
            s.localScale = Vector3.one * (gold ? 1.5f : 1f);
            var mat = gold ? _goldMat : _starMat;
            foreach (var r in s.GetComponentsInChildren<MeshRenderer>())
                if (r.sharedMaterial != mat)
                    r.sharedMaterial = mat;
            return s;
        }

        /// <summary>Lobby: a placeholder capsule drops onto a newly joined player's spawn (M4-11).</summary>
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
                var cap = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                Destroy(cap.GetComponent<Collider>());
                cap.transform.SetParent(transform, false);
                cap.transform.localScale = new Vector3(0.9f, 0.7f, 0.9f);
                cap.GetComponent<MeshRenderer>().sharedMaterial = Tinted(_map.playerMaterial, UiKit.Magenta);
                _drops.Add((cap.transform, Time.time, new Vector3(spawns[slot].X, spawns[slot].Y, spawns[slot].Z)));
            }

            for (var i = _drops.Count - 1; i >= 0; i--)
            {
                var (t, born, at) = _drops[i];
                var age = Time.time - born;
                if (age > 1.1f)
                {
                    Destroy(t.gameObject);
                    _drops.RemoveAt(i);
                    continue;
                }

                var fall = Mathf.Clamp01(age / 0.7f);
                t.position = at + Vector3.up * (8f * (1f - fall * fall) + 0.6f);
                if (age > 0.7f)
                    t.localScale = Vector3.one * Mathf.Lerp(0.9f, 1.8f, (age - 0.7f) / 0.4f); // pops open
            }
        }
    }
}
