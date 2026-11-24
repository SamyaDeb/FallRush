using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FallRush.Client
{
    /// <summary>
    /// G3.5 our own result banner, P7-UX2: the word as a big outlined stamp in the result colour; it pops in with an
    /// overshoot and a burst of little stars. Used for QUALIFIED!,
    /// ESCAPED!, ELIMINATED, GOOED!, OUT!, BONK!. Reduced motion: no pop, no star burst.
    /// </summary>
    public sealed class ResultBanner : MonoBehaviour
    {
        private RectTransform _root;
        private Image _bottom;
        private TMP_Text _text;
        private readonly RectTransform[] _stars = new RectTransform[8];
        private float _t = -1f, _until;

        public static ResultBanner Create(Transform parent, Vector2 pos)
        {
            var go = new GameObject("ResultBanner", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(660f, 130f);
            var b = go.AddComponent<ResultBanner>();
            b._root = rt;
            // P7-UX2 (OC-50): a word stamp in outlined game type (it was a 660 px split-capsule slab over the round result)
            b._bottom = UiKit.Shape(rt, "Tint", new Color(0f, 0f, 0f, 0f));
            b._text = UiKit.Shout(rt, string.Empty, 84, Color.white);
            b._text.characterSpacing = 4f;
            UiKit.Stretch((RectTransform)b._text.transform);
            for (var i = 0; i < b._stars.Length; i++)
            {
                var st = UiKit.Label(rt, "★", 30, UiKit.SunYellow, FontStyle.Bold);
                st.fontSharedMaterial = UiKit.DisplayOutlined;
                var r = (RectTransform)st.transform;
                r.sizeDelta = new Vector2(40f, 40f);
                b._stars[i] = r;
            }

            go.SetActive(false);
            return b;
        }

        public void Show(string text, Color color, float seconds = 1.5f)
        {
            _text.text = text;
            _text.color = color;
            _t = 0f;
            _until = Time.unscaledTime + seconds;
            gameObject.SetActive(true);
        }

        public void Hide()
        {
            _t = -1f;
            gameObject.SetActive(false);
        }

        public bool Visible => gameObject.activeSelf;

        private void Update()
        {
            if (_t < 0f)
                return;
            if (Time.unscaledTime >= _until)
            {
                Hide();
                return;
            }

            _t += Time.unscaledDeltaTime;
            var reduced = GameSettings.ReducedMotion;
            var u = Mathf.Clamp01(_t / 0.35f);
            var pop = reduced ? 1f : 1f + Mathf.Sin(u * Mathf.PI) * 0.18f - (1f - u) * 0.6f;
            _root.localScale = Vector3.one * (0.85f * Mathf.Max(0.01f, pop));
            _root.localRotation = Quaternion.Euler(0f, 0f, reduced ? 0f : Mathf.Sin(_t * 2.2f) * 2f);
            for (var i = 0; i < _stars.Length; i++)
            {
                var a = i * Mathf.PI * 2f / _stars.Length + _t * 0.8f;
                var r = reduced ? 0f : Mathf.Lerp(20f, 380f, Mathf.Clamp01(_t / 0.7f));
                _stars[i].anchoredPosition = new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r * 0.35f);
                _stars[i].gameObject.SetActive(!reduced && _t < 1.1f);
            }
        }
    }
}
