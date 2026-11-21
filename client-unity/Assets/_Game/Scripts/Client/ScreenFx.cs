using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace FallRush.Client
{
    /// <summary>
    /// G3.1 screen transitions: a "capsule pop". Every scene load opens like a gacha capsule: a white top half and a
    /// magenta bottom half with a Grape seam split apart (0.5 s, with a pop sound). Reduced motion: a short fade.
    /// One persistent overlay canvas (sorted above everything), created on first use.
    /// </summary>
    public sealed class ScreenFx : MonoBehaviour
    {
        private static ScreenFx s_Instance;
        private RectTransform _top, _bottom;
        private Image _fade;
        private CanvasGroup _group;
        private float _t = 1f;      // 0 closed → 1 open
        private float _dir = 1f;

        private static ScreenFx Get()
        {
            if (s_Instance != null)
                return s_Instance;
            var canvas = UiKit.CreateCanvas("ScreenFxCanvas", 500);
            DontDestroyOnLoad(canvas.gameObject);
            canvas.GetComponent<GraphicRaycaster>().enabled = false;
            s_Instance = canvas.gameObject.AddComponent<ScreenFx>();
            s_Instance.Build(canvas);
            SceneManager.sceneLoaded += (_, _) => Open();
            return s_Instance;
        }

        private void Build(RectTransform canvas)
        {
            _group = gameObject.AddComponent<CanvasGroup>();
            _group.blocksRaycasts = false;
            _top = Half(canvas, "Top", Color.white, new Vector2(0f, 0.5f), new Vector2(1f, 1.5f));
            _bottom = Half(canvas, "Bottom", UiKit.Magenta, new Vector2(0f, -0.5f), new Vector2(1f, 0.5f));
            var seam = UiKit.Shape(_bottom, "Seam", UiKit.Grape);
            var srt = seam.rectTransform;
            srt.anchorMin = new Vector2(-0.1f, 1f);
            srt.anchorMax = new Vector2(1.1f, 1f);
            srt.sizeDelta = new Vector2(0f, 18f);
            var logo = UiKit.Shout(_top, "FALL RUSH", 70, UiKit.Magenta);
            var lrt = (RectTransform)logo.transform;
            lrt.anchorMin = lrt.anchorMax = new Vector2(0.5f, 0.12f);
            lrt.sizeDelta = new Vector2(800f, 100f);
            _fade = UiKit.Shape(canvas, "Fade", UiKit.SkyLight, null);
            UiKit.Stretch(_fade.rectTransform);
            _fade.sprite = null;
            _fade.type = Image.Type.Simple;
            SetT(1f);
        }

        private static RectTransform Half(RectTransform canvas, string name, Color c, Vector2 aMin, Vector2 aMax)
        {
            var img = UiKit.Shape(canvas, name, c);
            img.pixelsPerUnitMultiplier = 0.25f; // big round ends: a capsule
            var rt = img.rectTransform;
            rt.anchorMin = aMin;
            rt.anchorMax = aMax;
            rt.offsetMin = new Vector2(-40f, 0f);
            rt.offsetMax = new Vector2(40f, 0f);
            return rt;
        }

        /// <summary>Open from closed (scene loaded).</summary>
        public static void Open()
        {
            var fx = Get();
            fx._t = 0f;
            fx._dir = 1f;
            fx.SetT(0f);
            GameAudio.Ui(GameAudio.UiCue.Pop);
        }

        /// <summary>Close (before a scene change that is not instant).</summary>
        public static void Close()
        {
            var fx = Get();
            fx._dir = -1f;
        }

        private void Update()
        {
            var speed = GameSettings.ReducedMotion ? 4f : 2.2f;
            var nt = Mathf.Clamp01(_t + _dir * Time.unscaledDeltaTime * speed);
            if (!Mathf.Approximately(nt, _t))
                SetT(nt);
        }

        private void SetT(float t)
        {
            _t = t;
            var reduced = GameSettings.ReducedMotion;
            var e = t * t * (3f - 2f * t);
            var hold = Mathf.Clamp01((t - 0.15f) / 0.85f); // a beat closed, then the pop
            var o = reduced ? 0f : hold * hold * 1.1f;
            _top.anchoredPosition = new Vector2(0f, o * 720f * 0.6f);
            _bottom.anchoredPosition = new Vector2(0f, -o * 720f * 0.6f);
            _top.gameObject.SetActive(!reduced && t < 1f);
            _bottom.gameObject.SetActive(!reduced && t < 1f);
            _fade.gameObject.SetActive(reduced && t < 1f);
            _fade.color = new Color(UiKit.SkyLight.r, UiKit.SkyLight.g, UiKit.SkyLight.b, 1f - e);
            _group.alpha = t >= 1f ? 0f : 1f;
        }
    }
}
