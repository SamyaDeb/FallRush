using FallRush.Net;
using FallRush.Shared;
using UnityEngine;

namespace FallRush.Client
{
    /// <summary>
    /// The champion screen's stage (greybox, M4 §3.6): a three-step toy-block podium high above the map with the winner's
    /// Rushling on top and a simple crown that pops in (placeholder for M7's crown ceremony). Client only.
    /// </summary>
    public sealed class ChampionPodium : MonoBehaviour
    {
        private RushlingVisuals _rushling;
        private Transform _crown;
        private float _born;
        private Vector3 _top;

        public Vector3 FocusPoint => _top + new Vector3(0f, 0.9f, 0f);

        public static ChampionPodium Create(Transform parent, int slot, MapInfo map)
        {
            var go = new GameObject("ChampionPodium");
            go.transform.SetParent(parent, false);
            var p = go.AddComponent<ChampionPodium>();
            p.Build(slot, map);
            return p;
        }

        private void Build(int slot, MapInfo map)
        {
            _born = Time.unscaledTime;
            var basePos = new Vector3(0f, 120f, 0f);
            var lit = map != null ? map.playerMaterial : null;
            var unlit = map != null ? map.unlitMaterial : null;
            Material Mat(Color c)
            {
                var m = lit != null ? new Material(lit) : new Material(Shader.Find("FallRush/Toon"));
                m.SetColor("_BaseColor", c);
                m.SetShaderPassEnabled("SRPDefaultUnlit", false);
                return m;
            }

            Block(basePos + new Vector3(0f, 1.0f, 0f), new Vector3(2.4f, 2f, 2.4f), Mat(UiKit.SunYellow));
            Block(basePos + new Vector3(-2.5f, 0.6f, 0f), new Vector3(2.4f, 1.2f, 2.4f), Mat(UiKit.SkyLight));
            Block(basePos + new Vector3(2.5f, 0.4f, 0f), new Vector3(2.4f, 0.8f, 2.4f), Mat(UiKit.Mint));
            Block(basePos + new Vector3(0f, -0.05f, 0f), new Vector3(14f, 0.1f, 14f), Mat(UiKit.CloudWhite));
            _top = basePos + new Vector3(0f, 2f, 0f);
            var root = new GameObject("Champion").transform;
            root.SetParent(transform, false);
            _rushling = root.gameObject.AddComponent<RushlingVisuals>();
            _rushling.SetPose(RushlingVisuals.Pose.Champion); // G1: crown pose (arms up, happy face)
            var preset = ClientSim.Instance != null && ClientSim.Instance.TryGetSlotInfo(slot, out var info) ? info.Preset : CosmeticPresets.ForBot(slot);
            _rushling.Build(slot, preset, lit, unlit);
            _rushling.SetVisible(true);
            _crown = new GameObject("Crown").transform;
            _crown.SetParent(transform, false);
            var gold = Mat(UiKit.SunYellow);
            var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(ring.GetComponent<Collider>());
            ring.transform.SetParent(_crown, false);
            ring.transform.localScale = new Vector3(0.55f, 0.08f, 0.55f);
            ring.GetComponent<MeshRenderer>().sharedMaterial = gold;
            for (var i = 0; i < 5; i++)
            {
                var a = i * 72f * Mathf.Deg2Rad;
                var spike = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(spike.GetComponent<Collider>());
                spike.transform.SetParent(_crown, false);
                spike.transform.localPosition = new Vector3(Mathf.Sin(a) * 0.22f, 0.14f, Mathf.Cos(a) * 0.22f);
                spike.transform.localRotation = Quaternion.Euler(0f, i * 72f, 45f);
                spike.transform.localScale = new Vector3(0.12f, 0.12f, 0.05f);
                spike.GetComponent<MeshRenderer>().sharedMaterial = Mat(i % 2 == 0 ? UiKit.Magenta : UiKit.SunYellow);
            }
        }

        private void Block(Vector3 c, Vector3 size, Material m)
        {
            var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(b.GetComponent<Collider>());
            b.transform.SetParent(transform, false);
            b.transform.position = c;
            b.transform.localScale = size;
            b.GetComponent<MeshRenderer>().sharedMaterial = m;
        }

        private void Update()
        {
            var age = Time.unscaledTime - _born;
            var hop = GameSettings.ReducedMotion ? 0f : Mathf.Abs(Mathf.Sin(age * 3.2f)) * 0.35f;
            _rushling.Drive(_top + new Vector3(0f, hop, 0f), Vector3.zero, MotorState.Grounded, (byte)128, 0, Time.unscaledDeltaTime);
            // Crown pops in after 0.6 s: drops onto the dome with an overshoot.
            var k = Mathf.Clamp01((age - 0.6f) / 0.5f);
            var s = k <= 0f ? 0f : 1f + Mathf.Sin(k * Mathf.PI) * 0.35f;
            _crown.localScale = Vector3.one * s;
            _crown.position = _top + new Vector3(0f, 1.15f + hop + (1f - k) * 1.5f, 0f);
            _crown.rotation = Quaternion.Euler(0f, GameSettings.ReducedMotion ? 0f : age * 60f, 0f);
        }
    }
}
