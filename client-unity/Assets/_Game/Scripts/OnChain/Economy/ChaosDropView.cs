using FallRush.Client;
using FallRush.Net;
using FallRush.Shared;
using UnityEngine;

namespace FallRush.OnChain
{
    /// <summary>
    /// P4 Chaos Drop telegraphs in the world (on-chain Final; the queue comes from the rush-core World through
    /// <see cref="ChaosDropFx"/>, so every client shows the same thing at the same tick). Reuses the G3 hazard colours:
    /// Lid Frenzy = a pulsing yellow wash over the top shelf during the telegraph (the lids themselves light / open
    /// through LidGridView exactly as the simulation); Bumper Rain = 3 pink bumpers falling onto their lids with
    /// growing shadows, an impact flash; Wind Gust = a big arrow over the shelf, pulsing during the telegraph and solid
    /// while it blows; Sweeper Rush = a red ring on the sweeper hub while the bar runs 1.5×.
    /// </summary>
    public sealed class ChaosDropView : MonoBehaviour
    {
        private static Material _mat;
        private readonly GameObject[] _bumpers = new GameObject[3], _shadows = new GameObject[3];
        private GameObject _wash, _arrow, _ring;
        private readonly int[] _lids = new int[3];
        private MaterialPropertyBlock _mpb;
        private static readonly int ColorId = Shader.PropertyToID("_BaseColor"), ColorId2 = Shader.PropertyToID("_Color");

        public static void Ensure()
        {
            if (FindFirstObjectByType<ChaosDropView>() == null)
            {
                var go = new GameObject("ChaosDropView");
                DontDestroyOnLoad(go); // survives the map scene load that follows the session start
                go.AddComponent<ChaosDropView>();
            }
        }

        private static Material Mat()
        {
            if (_mat != null)
                return _mat;
            var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null)
            {
                var any = FindFirstObjectByType<MeshRenderer>();
                sh = any != null && any.sharedMaterial != null ? any.sharedMaterial.shader : Shader.Find("Sprites/Default");
            }

            _mat = new Material(sh);
            return _mat;
        }

        private GameObject Prim(PrimitiveType t, string name)
        {
            var g = GameObject.CreatePrimitive(t);
            g.name = name;
            Destroy(g.GetComponent<Collider>());
            g.transform.SetParent(transform, false);
            g.GetComponent<MeshRenderer>().sharedMaterial = Mat();
            g.SetActive(false);
            return g;
        }

        private void Tint(GameObject g, Color c)
        {
            _mpb ??= new MaterialPropertyBlock();
            _mpb.SetColor(ColorId, c);
            _mpb.SetColor(ColorId2, c);
            g.GetComponent<MeshRenderer>().SetPropertyBlock(_mpb);
        }

        private void Awake()
        {
            for (var i = 0; i < 3; i++)
            {
                _bumpers[i] = Prim(PrimitiveType.Sphere, "DropBumper");
                _shadows[i] = Prim(PrimitiveType.Cylinder, "DropShadow");
            }

            _wash = Prim(PrimitiveType.Cube, "FrenzyWash");
            _arrow = Prim(PrimitiveType.Cube, "WindArrow");
            _ring = Prim(PrimitiveType.Cylinder, "RushRing");
        }

        private void Update()
        {
            var cs = ClientSim.Instance;
            var hz = cs != null ? cs.Hazards : null;
            var on = ChaosDropFx.Any && hz != null && hz.LidGrids.Length > 0;
            foreach (var g in _bumpers) g.SetActive(false);
            foreach (var g in _shadows) g.SetActive(false);
            _wash.SetActive(false);
            _arrow.SetActive(false);
            _ring.SetActive(false);
            if (!on)
                return;
            var tick = cs.HazardRenderTick;
            ref readonly var top = ref hz.LidGrids[0];
            var pitch = (top.LidMm + top.GapMm) * 0.001f;
            var half = top.N * pitch * 0.5f;
            top.LidCenterMm(top.Count / 2, out var cx, out var cy, out var cz);
            var centre = new Vector3(cx * 0.001f, top.TopMm * 0.001f, cz * 0.001f);
            foreach (var q in ChaosDropFx.Queue)
            {
                if (q.Kind == 0)
                    continue;
                var dt = tick - q.Tick; // < 0: telegraph, ≥ 0: active
                if (dt < -ChaosDropFx.Telegraph - 1 || dt > ChaosDropFx.Duration(q.Kind) + 20)
                    continue;
                var tele = dt < 0;
                var pulse = 0.5f + 0.5f * Mathf.Sin((float)tick * 0.6f);
                switch (q.Kind)
                {
                    case ChaosDropFx.LidFrenzy when tele:
                        _wash.SetActive(true);
                        _wash.transform.position = centre + Vector3.up * 0.25f;
                        _wash.transform.localScale = new Vector3(2f * half, 0.05f, 2f * half);
                        Tint(_wash, new Color(1f, 0.83f, 0.2f, 0.35f + 0.4f * pulse));
                        break;
                    case ChaosDropFx.BumperRain:
                    {
                        ChaosDropFx.BumperLids(in top, q.Arg, ChaosDropFx.HazardSeed, _lids);
                        var u = Mathf.Clamp01((float)((dt + ChaosDropFx.Telegraph) / ChaosDropFx.Telegraph)); // 0 → 1 over the telegraph
                        for (var i = 0; i < 3; i++)
                        {
                            if (_lids[i] < 0)
                                continue;
                            top.LidCenterMm(_lids[i], out var x, out _, out var z);
                            var p = new Vector3(x * 0.001f, top.TopMm * 0.001f, z * 0.001f);
                            _shadows[i].SetActive(dt < 12);
                            _shadows[i].transform.position = p + Vector3.up * 0.03f;
                            var r = 3.0f * (0.3f + 0.7f * u);
                            _shadows[i].transform.localScale = new Vector3(r, 0.01f, r);
                            Tint(_shadows[i], new Color(0.16f, 0.07f, 0.31f, 0.55f));
                            if (dt < 12)
                            {
                                _bumpers[i].SetActive(true);
                                var h = tele ? 10f * (1f - u * u) : 0.6f * Mathf.Max(0f, 1f - (float)dt / 12f);
                                _bumpers[i].transform.position = p + Vector3.up * (0.9f + h);
                                var sq = tele ? 1f : 1f + 0.4f * Mathf.Max(0f, 1f - (float)dt / 6f);
                                _bumpers[i].transform.localScale = new Vector3(1.8f * sq, 1.8f / sq, 1.8f * sq);
                                Tint(_bumpers[i], UiKit.Magenta);
                            }
                        }

                        break;
                    }
                    case ChaosDropFx.WindGust:
                    {
                        var (wx, wz) = ChaosDropFx.WindDir(q.Arg);
                        var dir = new Vector3(wx, 0f, wz);
                        _arrow.SetActive(true);
                        _arrow.transform.position = centre + Vector3.up * 4.5f;
                        _arrow.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
                        var len = tele ? 4f + 2f * pulse : 7f;
                        _arrow.transform.localScale = new Vector3(0.6f, 0.25f, len);
                        Tint(_arrow, tele ? new Color(UiKit.Sky.r, UiKit.Sky.g, UiKit.Sky.b, 0.5f + 0.5f * pulse) : UiKit.Sky);
                        break;
                    }
                    case ChaosDropFx.SweeperRush when hz.Sweepers.Length > 0:
                    {
                        ref readonly var sw = ref hz.Sweepers[0];
                        _ring.SetActive(true);
                        _ring.transform.position = new Vector3(sw.PivotXMm * 0.001f, sw.PivotYMm * 0.001f + 0.1f, sw.PivotZMm * 0.001f);
                        var r = tele ? 2f + 2f * pulse : 3.5f;
                        _ring.transform.localScale = new Vector3(r, 0.04f, r);
                        Tint(_ring, new Color(1f, 0.25f, 0.3f, tele ? 0.4f + 0.5f * pulse : 0.85f));
                        break;
                    }
                }
            }
        }
    }
}
