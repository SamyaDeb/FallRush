using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FallRush.Client
{
    /// <summary>
    /// G3.4 the capsule machine drawn in the UI (signature moments 2 and 5): a glass globe full of capsules on a magenta
    /// cabinet with a crank. <see cref="Spin"/> cranks it and tumbles the capsules (the Chaos Capsule machine while the
    /// deal is pending); <see cref="Pop"/> drops one capsule out of the chute and pops it open on a label (a dealt round,
    /// or the countdown's 3-2-1-RUSH!). Reduced motion: no tumbling, the capsule appears open.
    /// </summary>
    public sealed class CapsuleMachineWidget : MonoBehaviour
    {
        private RectTransform _root, _crank, _globe, _cap, _capTop;
        private readonly RectTransform[] _balls = new RectTransform[7];
        private TMP_Text _capLabel;
        private Image _capBottom;
        private bool _spin;
        private float _t, _crankA, _popT = -1f;

        public static CapsuleMachineWidget Create(Transform parent, Vector2 size)
        {
            var go = new GameObject("CapsuleMachine", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = size;
            go.AddComponent<LayoutElement>().ignoreLayout = true;
            var w = go.AddComponent<CapsuleMachineWidget>();
            w.Build(rt, size);
            return w;
        }

        private void Build(RectTransform rt, Vector2 size)
        {
            _root = rt;
            var s = size.y / 300f;
            // Cabinet
            var cab = UiKit.Shape(rt, "Cabinet", UiKit.Magenta);
            Place(cab.rectTransform, new Vector2(0f, -85f * s), new Vector2(190f, 120f) * s);
            var band = UiKit.Shape(rt, "Band", UiKit.SunYellow);
            Place(band.rectTransform, new Vector2(0f, -25f * s), new Vector2(206f, 16f) * s);
            var chute = UiKit.Shape(rt, "Chute", UiKit.Ink);
            Place(chute.rectTransform, new Vector2(-40f * s, -110f * s), new Vector2(64f, 40f) * s);
            // Crank
            var hub = UiKit.Shape(rt, "CrankHub", UiKit.SunYellow, UiKit.Circle);
            Place(hub.rectTransform, new Vector2(50f * s, -82f * s), new Vector2(42f, 42f) * s);
            var crank = new GameObject("Crank", typeof(RectTransform)).GetComponent<RectTransform>();
            crank.SetParent(hub.transform, false);
            crank.anchoredPosition = Vector2.zero;
            var arm = UiKit.Shape(crank, "Arm", UiKit.Grape);
            Place(arm.rectTransform, new Vector2(0f, 20f * s), new Vector2(10f, 42f) * s);
            var knob = UiKit.Shape(crank, "Knob", UiKit.Mint, UiKit.Circle);
            Place(knob.rectTransform, new Vector2(0f, 40f * s), new Vector2(22f, 22f) * s);
            _crank = crank;
            // Globe with capsules
            var ring = UiKit.Shape(rt, "GlobeRing", UiKit.Grape, UiKit.Circle);
            Place(ring.rectTransform, new Vector2(0f, 55f * s), new Vector2(196f, 196f) * s);
            var glass = UiKit.Shape(rt, "Globe", new Color(0.85f, 0.94f, 1f, 1f), UiKit.Circle);
            Place(glass.rectTransform, new Vector2(0f, 55f * s), new Vector2(184f, 184f) * s);
            _globe = glass.rectTransform;
            var cols = new[] { UiKit.Magenta, UiKit.Mint, UiKit.Sky, UiKit.SunYellow, UiKit.Grape, UiKit.Hex(0xFF8A3D), UiKit.Magenta };
            for (var i = 0; i < _balls.Length; i++)
            {
                var b = Capsule(glass.transform, cols[i], 44f * s);
                _balls[i] = b;
            }

            var shine = UiKit.Shape(glass.transform, "Shine", new Color(1f, 1f, 1f, 0.55f), UiKit.Circle);
            Place(shine.rectTransform, new Vector2(-44f * s, 44f * s), new Vector2(40f, 28f) * s);
            var cap = UiKit.Shape(rt, "Cap", UiKit.Grape, UiKit.Circle);
            Place(cap.rectTransform, new Vector2(0f, 150f * s), new Vector2(60f, 26f) * s);
            // The popped capsule (hidden until Pop)
            var pc = new GameObject("Popped", typeof(RectTransform)).GetComponent<RectTransform>();
            pc.SetParent(rt, false);
            pc.sizeDelta = new Vector2(170f, 120f) * s;
            _cap = pc;
            _capBottom = UiKit.Shape(pc, "Bottom", UiKit.Mint);
            _capBottom.pixelsPerUnitMultiplier = 0.4f;
            Place(_capBottom.rectTransform, new Vector2(0f, -22f * s), new Vector2(170f, 76f) * s);
            var top = UiKit.Shape(pc, "Top", Color.white);
            top.pixelsPerUnitMultiplier = 0.4f;
            Place(top.rectTransform, new Vector2(0f, 22f * s), new Vector2(170f, 76f) * s);
            _capTop = top.rectTransform;
            _capLabel = UiKit.Shout(pc, string.Empty, Mathf.RoundToInt(64f * s), Color.white);
            Place((RectTransform)_capLabel.transform, new Vector2(0f, -6f * s), new Vector2(360f, 90f) * s);
            _cap.gameObject.SetActive(false);
        }

        private static RectTransform Capsule(Transform parent, Color c, float d)
        {
            var root = new GameObject("Ball", typeof(RectTransform)).GetComponent<RectTransform>();
            root.SetParent(parent, false);
            root.sizeDelta = new Vector2(d, d);
            var bot = UiKit.Shape(root, "B", c, UiKit.Circle);
            UiKit.Stretch(bot.rectTransform);
            var top = UiKit.Shape(root, "T", Color.white, UiKit.Circle);
            top.rectTransform.anchorMin = new Vector2(0f, 0.5f);
            top.rectTransform.anchorMax = Vector2.one;
            top.rectTransform.offsetMin = top.rectTransform.offsetMax = Vector2.zero;
            top.type = Image.Type.Filled;
            top.fillMethod = Image.FillMethod.Vertical;
            top.fillOrigin = 1;
            top.fillAmount = 1f;
            top.preserveAspect = false;
            return root;
        }

        private static void Place(RectTransform r, Vector2 pos, Vector2 size)
        {
            r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
            r.anchoredPosition = pos;
            r.sizeDelta = size;
        }

        public void Spin(bool on)
        {
            if (on && !_spin)
                GameAudio.Ui(GameAudio.UiCue.Crank);
            _spin = on;
        }

        /// <summary>Drops a capsule out of the chute and pops it open on <paramref name="label"/>.</summary>
        public void Pop(string label, Color color)
        {
            _capLabel.text = label;
            _capBottom.color = color;
            _popT = 0f;
            _cap.gameObject.SetActive(true);
            GameAudio.Ui(GameAudio.UiCue.Pop);
        }

        public void HidePop()
        {
            _popT = -1f;
            _cap.gameObject.SetActive(false);
        }

        private void Update()
        {
            var dt = Time.unscaledDeltaTime;
            var reduced = GameSettings.ReducedMotion;
            _t += dt;
            if (_spin && !reduced)
                _crankA -= dt * 420f;
            _crank.localRotation = Quaternion.Euler(0f, 0f, _crankA);
            var s = _root.sizeDelta.y / 300f;
            for (var i = 0; i < _balls.Length; i++)
            {
                var a = i * 0.9f + (_spin && !reduced ? _t * (2.2f + i * 0.31f) : 0f);
                var r = (_spin && !reduced ? 52f : 40f) * s;
                var settle = _spin && !reduced ? Mathf.Sin(a) * r : -40f * s + (i % 3) * 22f * s;
                var x = _spin && !reduced ? Mathf.Cos(a * 1.3f) * r : -48f * s + (i % 4) * 32f * s;
                _balls[i].anchoredPosition = new Vector2(x, settle);
                _balls[i].localRotation = Quaternion.Euler(0f, 0f, _spin && !reduced ? a * 90f : i * 25f);
            }

            if (_popT >= 0f)
            {
                _popT += dt;
                // Rolls out of the chute to the front, then the top half pops off and fades: the label is revealed.
                var u = reduced ? 1f : Mathf.Clamp01(_popT / 0.3f);
                _cap.anchoredPosition = new Vector2(Mathf.Lerp(-40f, 0f, u) * s, Mathf.Lerp(-110f, -190f, u) * s);
                var open = reduced ? 1f : Mathf.Clamp01((_popT - 0.28f) / 0.22f);
                var pop = 1f + Mathf.Sin(Mathf.Clamp01(_popT / 0.5f) * Mathf.PI) * (reduced ? 0f : 0.15f);
                _cap.localScale = Vector3.one * pop;
                _capTop.anchoredPosition = new Vector2(open * 60f * s, 22f * s + open * 110f * s);
                _capTop.localRotation = Quaternion.Euler(0f, 0f, -open * 50f);
                _capTop.GetComponent<Image>().color = new Color(1f, 1f, 1f, 1f - open);
                _capLabel.alpha = open;
            }
        }
    }
}
