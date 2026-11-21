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
            BuildCrownCapsule(lit);
            Fx.Confetti(_top + Vector3.up * 1.5f, 10);
        }

        private Transform _capTop, _capBottom;
        private bool _popped;

        /// <summary>
        /// G3.4 #4: the golden Crown Capsule (our own design): a gold and white capsule toy drops onto the champion and pops
        /// open; inside is our crown, a gold band with five rounded capsule-shaped prongs tipped with magenta and mint balls
        /// and a little wind-up key on top, which settles on the champion's dome. Confetti and the victory sting.
        /// </summary>
        private void BuildCrownCapsule(Material lit)
        {
            var gold = UiKit.Hex(0xFFC21F);
            var c = new RushlingParts.Combiner();
            c.Add(RushlingParts.Shapes.Cylinder12, Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(0.5f, 0.06f, 0.5f)), gold, RushlingParts.FlagNone);
            c.Add(RushlingParts.Shapes.ThinTorus, Matrix4x4.TRS(new Vector3(0f, -0.04f, 0f), Quaternion.identity, new Vector3(0.26f, 0.6f, 0.26f)), UiKit.Magenta, RushlingParts.FlagNoHull);
            for (var i = 0; i < 5; i++)
            {
                var a = i * 72f * Mathf.Deg2Rad;
                var d = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                c.Add(RushlingParts.Shapes.Puff, Matrix4x4.TRS(d * 0.21f + Vector3.up * 0.13f, Quaternion.identity, new Vector3(0.11f, 0.26f, 0.11f)), gold, RushlingParts.FlagNone);
                c.Add(RushlingParts.Shapes.Puff, Matrix4x4.TRS(d * 0.21f + Vector3.up * 0.28f, Quaternion.identity, Vector3.one * 0.09f), i % 2 == 0 ? UiKit.Magenta : UiKit.Mint, RushlingParts.FlagNoHull);
            }

            c.Add(RushlingParts.Shapes.Cylinder8, Matrix4x4.TRS(Vector3.up * 0.1f, Quaternion.identity, new Vector3(0.04f, 0.08f, 0.04f)), gold, RushlingParts.FlagNoHull);
            c.Add(RushlingParts.Shapes.LoopTorus, Matrix4x4.TRS(Vector3.up * 0.22f + Vector3.left * 0.05f, Quaternion.Euler(0f, 0f, 90f), new Vector3(0.06f, 0.2f, 0.05f)), UiKit.Grape, RushlingParts.FlagNoHull);
            c.Add(RushlingParts.Shapes.LoopTorus, Matrix4x4.TRS(Vector3.up * 0.22f + Vector3.right * 0.05f, Quaternion.Euler(0f, 0f, 90f), new Vector3(0.06f, 0.2f, 0.05f)), UiKit.Grape, RushlingParts.FlagNoHull);
            _crown = Piece("Crown", c.Build("CrownMesh"), lit);
            _crown.localScale = Vector3.zero;

            var t = new RushlingParts.Combiner();
            t.Add(RushlingParts.Shapes.Dome, Matrix4x4.identity, Color.white, RushlingParts.FlagNone);
            t.Add(RushlingParts.Shapes.ThinTorus, Matrix4x4.TRS(new Vector3(0f, ProcMesh.SeamY, 0f), Quaternion.identity, new Vector3(ProcMesh.R + 0.004f, 0.4f, ProcMesh.R + 0.004f)), gold, RushlingParts.FlagNone);
            t.Add(RushlingParts.Shapes.Star5, Matrix4x4.TRS(new Vector3(0f, 0.78f, ProcMesh.RadiusAt(0.78f) + 0.01f), Quaternion.identity, Vector3.one * 1.6f), gold, RushlingParts.FlagNoHull);
            _capTop = Piece("CrownCapsuleTop", t.Build("CrownCapsuleTop"), lit);
            var b = new RushlingParts.Combiner();
            b.Add(RushlingParts.Shapes.Shell, Matrix4x4.identity, gold, RushlingParts.FlagNone);
            _capBottom = Piece("CrownCapsuleBottom", b.Build("CrownCapsuleBottom"), lit);
        }

        private Transform Piece(string name, Mesh mesh, Material lit)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = lit != null ? lit : new Material(Shader.Find("FallRush/Toon"));
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go.transform;
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
            // Crown Capsule: drops in (0.3–0.9 s), pops open at 1.1 s, the crown settles on the dome by 1.8 s.
            var reduced = GameSettings.ReducedMotion;
            var head = _top + new Vector3(0f, 1.15f + hop, 0f);
            var drop = Mathf.Clamp01((age - 0.3f) / 0.6f);
            var capAt = head + Vector3.up * (0.6f + (1f - drop * drop) * 6f);
            var open = Mathf.Clamp01((age - 1.1f) / 0.5f);
            if (age >= 1.1f && !_popped)
            {
                _popped = true;
                Fx.Confetti(head + Vector3.up * 0.6f, 90);
                Fx.Sparkle(head + Vector3.up * 0.6f, true);
                GameAudio.Play2D(GameAudio.Sfx.CapsulePop);
                GameAudio.Play2D(GameAudio.Sfx.Confetti);
            }

            var capScale = Vector3.one * 1.3f * (age < 0.3f ? 0f : 1f);
            _capBottom.SetPositionAndRotation(capAt - Vector3.up * open * 0.6f, Quaternion.Euler(0f, reduced ? 0f : age * 90f, 0f));
            _capBottom.localScale = capScale * Mathf.Max(0.01f, 1f - open);
            _capTop.SetPositionAndRotation(capAt + new Vector3(open * 0.8f, open * 2.2f - open * open * 0.8f, 0f), Quaternion.Euler(0f, reduced ? 0f : age * 90f, -open * 120f));
            _capTop.localScale = capScale * Mathf.Max(0.01f, 1f - open * 0.6f);
            _capTop.gameObject.SetActive(open < 1f);
            _capBottom.gameObject.SetActive(open < 1f);
            var settle = Mathf.Clamp01((age - 1.25f) / 0.55f);
            var s = settle <= 0f ? 0f : 1f + Mathf.Sin(settle * Mathf.PI) * (reduced ? 0f : 0.3f);
            _crown.localScale = Vector3.one * s;
            _crown.position = Vector3.Lerp(capAt, head, settle * settle);
            _crown.rotation = Quaternion.Euler(0f, reduced ? 0f : age * 60f, 0f);
        }
    }
}
