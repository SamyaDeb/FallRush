using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace FallRush.Client
{
    /// <summary>
    /// G3.1 UI kit (doc 17 §5), built in code: chunky rounded panels with thick Grape outlines and a slight 3D drop,
    /// Sun Yellow / Magenta buttons with a lip that squash on press, pills and chips, TextMeshPro text in our OFL faces
    /// (display: Lilita One, body: Varela Round with tabular figures, symbols: Noto Sans Symbols 2 fallback; static SDF
    /// atlases, so every glyph renders in WebGL). Rounded shapes are one code-generated 9-slice sprite.
    /// Minimum body size 18 px at 1080p = 12 units at the 1280 × 720 reference.
    /// </summary>
    public static class UiKit
    {
        public static readonly Color Grape = Hex(0x7B2FF7);
        public static readonly Color Magenta = Hex(0xFF3EA5);
        public static readonly Color SunYellow = Hex(0xFFD21F);
        public static readonly Color Mint = Hex(0x5BE3B5);
        public static readonly Color Sky = Hex(0x3FA9F5);
        public static readonly Color SkyLight = Hex(0xBFE6FF);
        public static readonly Color CloudWhite = Color.white;
        public static readonly Color Ink = Hex(0x2A1250);
        public static readonly Color Amber = Hex(0xFFA51F);
        public static readonly Color Grey = Hex(0x8C88A6);

        public const float Outline = 5f;   // panel border (reference units)
        public const float Drop = 7f;      // 3D drop under panels and buttons

        private static TMP_FontAsset s_Display, s_Body;
        private static Sprite s_Round, s_Circle;
        private static Material s_DisplayOutlined;

        public static TMP_FontAsset Display => s_Display != null ? s_Display : s_Display = Resources.Load<TMP_FontAsset>("FallRush/Fonts/FR_Display");
        public static TMP_FontAsset Body => s_Body != null ? s_Body : s_Body = Resources.Load<TMP_FontAsset>("FallRush/Fonts/FR_Body");

        /// <summary>Display face with a thick Grape outline + soft drop (banners, big numbers): one shared material.</summary>
        public static Material DisplayOutlined
        {
            get
            {
                if (s_DisplayOutlined != null || Display == null)
                    return s_DisplayOutlined;
                s_DisplayOutlined = new Material(Display.material) { name = "FR_Display Outlined (UI)" };
                s_DisplayOutlined.EnableKeyword("OUTLINE_ON");
                s_DisplayOutlined.SetFloat("_OutlineWidth", 0.24f);
                s_DisplayOutlined.SetColor("_OutlineColor", Ink);
                s_DisplayOutlined.SetFloat("_FaceDilate", 0.12f);
                s_DisplayOutlined.EnableKeyword("UNDERLAY_ON");
                s_DisplayOutlined.SetColor("_UnderlayColor", new Color(Ink.r, Ink.g, Ink.b, 0.55f));
                s_DisplayOutlined.SetFloat("_UnderlayOffsetY", -0.7f);
                s_DisplayOutlined.SetFloat("_UnderlaySoftness", 0.15f);
                return s_DisplayOutlined;
            }
        }

        private static Material s_BodyOutlined;

        /// <summary>Body face with a white outline (nameplates and HUD text over the world).</summary>
        public static Material BodyOutlined
        {
            get
            {
                if (s_BodyOutlined != null || Body == null)
                    return s_BodyOutlined;
                s_BodyOutlined = new Material(Body.material) { name = "FR_Body Outlined (UI)" };
                s_BodyOutlined.EnableKeyword("OUTLINE_ON");
                s_BodyOutlined.SetFloat("_OutlineWidth", 0.28f);
                s_BodyOutlined.SetColor("_OutlineColor", Color.white);
                s_BodyOutlined.SetFloat("_FaceDilate", 0.18f);
                return s_BodyOutlined;
            }
        }

        public static Color Hex(int rgb) =>
            new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

        public static string HexOf(Color c) => ColorUtility.ToHtmlStringRGB(c);

        // ------------------------------------------------------------------ sprites

        /// <summary>A white rounded rectangle (radius 24 px of 64) with 9-slice borders.</summary>
        public static Sprite Round => s_Round != null ? s_Round : s_Round = MakeRound(64, 24, "UiRound");

        /// <summary>A white anti-aliased disc (dots, knobs, badges).</summary>
        public static Sprite Circle => s_Circle != null ? s_Circle : s_Circle = MakeRound(64, 32, "UiCircle");

        private static Sprite MakeRound(int size, int radius, string name)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var cx = Mathf.Clamp(x + 0.5f, radius, size - radius);
                    var cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
                    var d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    var a = Mathf.Clamp01(radius - d + 0.5f);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }

            tex.SetPixels32(px);
            tex.Apply(false, true);
            var b = radius;
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
        }

        // ------------------------------------------------------------------ canvas

        public static RectTransform CreateCanvas(string name, int sortingOrder = 0)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 0.5f;

            if (Object.FindAnyObjectByType<EventSystem>() == null)
                new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

            return (RectTransform)go.transform;
        }

        // ------------------------------------------------------------------ shapes

        /// <summary>A rounded image (ignored by layout groups unless <paramref name="layout"/>).</summary>
        public static Image Shape(Transform parent, string name, Color c, Sprite sprite = null, bool layout = false, bool raycast = false)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.sprite = sprite != null ? sprite : Round;
            img.type = Image.Type.Sliced;
            img.color = c;
            img.raycastTarget = raycast;
            if (!layout)
                go.AddComponent<LayoutElement>().ignoreLayout = true;
            return img;
        }

        public static void Stretch(RectTransform rt, float inset = 0f, Vector2 offset = default)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(inset, inset) + offset;
            rt.offsetMax = new Vector2(-inset, -inset) + offset;
        }

        /// <summary>
        /// The chunky look behind any rect: a 3D drop (Ink, offset down), the thick Grape border, the fill.
        /// Returns the fill image (tint it to recolour the panel).
        /// </summary>
        public static Image Chunk(RectTransform rt, Color fill, Color? border = null, float outline = Outline, float drop = Drop)
        {
            var dropImg = Shape(rt, "Drop", new Color(Ink.r, Ink.g, Ink.b, 0.45f));
            Stretch(dropImg.rectTransform, 0f, new Vector2(0f, -drop));
            var b = Shape(rt, "Border", border ?? Grape);
            Stretch(b.rectTransform);
            var f = Shape(rt, "Fill", fill);
            Stretch(f.rectTransform, outline);
            dropImg.transform.SetAsFirstSibling();
            b.transform.SetSiblingIndex(1);
            f.transform.SetSiblingIndex(2);
            return f;
        }

        public static RectTransform Panel(Transform parent, Vector2 size, Color fill)
        {
            var go = new GameObject("Panel", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = size;
            Chunk(rt, fill);
            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(36, 36, 28, 32);
            layout.spacing = 14;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            return rt;
        }

        /// <summary>A pill (fully rounded) behind HUD text: readable on any background (G2/G3 step 0).</summary>
        public static Image Pill(RectTransform rt, Color fill, Color? border = null)
        {
            var f = Chunk(rt, fill, border, 3f, 4f);
            return f;
        }

        // ------------------------------------------------------------------ text

        public static TMP_Text Label(Transform parent, string text, int size, Color color, FontStyle style = FontStyle.Normal, int minHeight = 0)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<TextMeshProUGUI>();
            var heading = style == FontStyle.Bold && size >= 26;
            t.font = heading ? Display : Body;
            t.text = text;
            t.fontSize = size;
            t.fontStyle = style == FontStyle.Bold && !heading ? FontStyles.Bold : FontStyles.Normal;
            t.color = color;
            t.alignment = TextAlignmentOptions.Center;
            t.textWrappingMode = TextWrappingModes.Normal;
            t.overflowMode = TextOverflowModes.Overflow;
            t.raycastTarget = false;
            t.richText = true;
            go.GetComponent<LayoutElement>().minHeight = minHeight > 0 ? minHeight : size + 8;
            return t;
        }

        /// <summary>A big display-face line with the thick outline (banners, countdown, titles).</summary>
        public static TMP_Text Shout(Transform parent, string text, int size, Color face)
        {
            var t = Label(parent, text, size, face, FontStyle.Bold);
            t.font = Display;
            if (DisplayOutlined != null)
                t.fontSharedMaterial = DisplayOutlined;
            return t;
        }

        public static void Align(TMP_Text t, TextAnchor a)
        {
            t.alignment = a switch
            {
                TextAnchor.UpperLeft => TextAlignmentOptions.TopLeft,
                TextAnchor.UpperCenter => TextAlignmentOptions.Top,
                TextAnchor.UpperRight => TextAlignmentOptions.TopRight,
                TextAnchor.MiddleLeft => TextAlignmentOptions.Left,
                TextAnchor.MiddleRight => TextAlignmentOptions.Right,
                TextAnchor.LowerLeft => TextAlignmentOptions.BottomLeft,
                TextAnchor.LowerCenter => TextAlignmentOptions.Bottom,
                TextAnchor.LowerRight => TextAlignmentOptions.BottomRight,
                _ => TextAlignmentOptions.Center,
            };
        }

        // ------------------------------------------------------------------ buttons

        public static Button Button(Transform parent, string text, Color fill, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject("Button", typeof(RectTransform), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            var le = go.GetComponent<LayoutElement>();
            le.minHeight = 60;
            le.preferredWidth = 420;
            // Visual root that squashes (the layout rect stays put).
            var face = new GameObject("Face", typeof(RectTransform));
            var frt = (RectTransform)face.transform;
            frt.SetParent(rt, false);
            Stretch(frt);
            var img = Chunk(frt, fill, Grape, 4f, 6f);
            img.raycastTarget = true;
            var shine = Shape(img.transform, "Shine", new Color(1f, 1f, 1f, 0.35f));
            var srt = shine.rectTransform;
            srt.anchorMin = new Vector2(0f, 0.55f);
            srt.anchorMax = new Vector2(1f, 1f);
            srt.offsetMin = new Vector2(10f, 0f);
            srt.offsetMax = new Vector2(-10f, -5f);

            var button = go.AddComponent<Button>();
            button.targetGraphic = img;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1f, 1f, 1f, 0.9f);
            colors.pressedColor = new Color(0.88f, 0.88f, 0.92f, 1f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(0.62f, 0.6f, 0.7f, 0.75f);
            button.colors = colors;
            button.onClick.AddListener(onClick);
            button.onClick.AddListener(() => GameAudio.Ui(GameAudio.UiCue.Click));
            go.AddComponent<UiPress>().Target = frt;

            var label = Label(frt, text, 26, Ink, FontStyle.Bold);
            label.font = Display;
            var lrt = (RectTransform)label.transform;
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(12f, 4f);
            lrt.offsetMax = new Vector2(-12f, -2f);
            label.GetComponent<LayoutElement>().ignoreLayout = true;
            return button;
        }

        public static void SetButtonText(Button button, string text) =>
            button.GetComponentInChildren<TMP_Text>().text = text;

        // ------------------------------------------------------------------ rows

        public static RectTransform Row(Transform parent, float height, float spacing = 10f, TextAnchor align = TextAnchor.MiddleLeft)
        {
            var go = new GameObject("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var h = go.GetComponent<HorizontalLayoutGroup>();
            h.spacing = spacing;
            h.childAlignment = align;
            h.childControlWidth = true;
            h.childControlHeight = true;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = true;
            go.GetComponent<LayoutElement>().minHeight = height;
            go.GetComponent<LayoutElement>().preferredHeight = height;
            return (RectTransform)go.transform;
        }

        /// <summary>A fixed-width cell label in a row (tables line up: column widths + tabular body figures).</summary>
        public static TMP_Text Cell(Transform row, string text, float width, int size, Color c, TextAlignmentOptions align = TextAlignmentOptions.Left, bool bold = false)
        {
            var t = Label(row, text, size, c, bold ? FontStyle.Bold : FontStyle.Normal);
            t.font = Body;
            t.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            t.alignment = align;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Ellipsis;
            var le = t.GetComponent<LayoutElement>();
            le.minWidth = le.preferredWidth = width;
            le.flexibleWidth = 0f;
            return t;
        }

        public static Image Dot(Transform row, Color c, float size = 14f)
        {
            var go = new GameObject("Dot", typeof(RectTransform), typeof(LayoutElement));
            go.transform.SetParent(row, false);
            var le = go.GetComponent<LayoutElement>();
            le.minWidth = le.preferredWidth = size + 6f;
            var img = Shape(go.transform, "D", c, Circle);
            var r = img.rectTransform;
            r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
            r.sizeDelta = new Vector2(size, size);
            return img;
        }
    }
}
