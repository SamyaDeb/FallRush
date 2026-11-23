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

        /// <summary>P7: the warm cream body of plain panels (menus, settings): soft look, never pure white.</summary>
        public static readonly Color Cream = Hex(0xFFF6E6);

        /// <summary>P7 soft panel: a soft drop shadow, a saturated rim, the body, a light top bevel (no hard Ink drop).</summary>
        public static Image SoftChunk(RectTransform rt, Color fill, Color rim, float width = Outline)
        {
            var sh = Shape(rt, "Shadow", new Color(Ink.r, Ink.g, Ink.b, 0.28f), Soft);
            Stretch(sh.rectTransform, -14f, new Vector2(0f, -7f));
            var b = Shape(rt, "Border", rim);
            Stretch(b.rectTransform);
            var f = Shape(rt, "Fill", fill);
            Stretch(f.rectTransform, width);
            var bevel = Shape(f.transform, "Bevel", new Color(1f, 1f, 1f, 0.55f));
            var brt = bevel.rectTransform;
            brt.anchorMin = new Vector2(0f, 1f);
            brt.anchorMax = Vector2.one;
            brt.pivot = new Vector2(0.5f, 1f);
            brt.offsetMin = new Vector2(18f, -8f);
            brt.offsetMax = new Vector2(-18f, -3f);
            bevel.pixelsPerUnitMultiplier = 6f;
            sh.transform.SetAsFirstSibling();
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
            SoftChunk(rt, fill == CloudWhite ? Cream : fill, CardRim(CardHue.Grape));
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
            go.GetComponent<LayoutElement>().flexibleHeight = 0f; // P7: a row never takes the card body's spare height
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

        // ------------------------------------------------------------------ P7 cards (soft toy look, doc 17 §3.0)

        /// <summary>The card hues: the six soft-look section hues (PaletteTokens: pastel top, mid side, saturated rim).</summary>
        public enum CardHue : byte { Bubblegum = 0, Lemon = 1, Soda = 2, Apple = 3, Grape = 4, Tangerine = 5 }

        public enum CardSize : byte { S, M, L }

        public static Color CardPastel(CardHue h) => Hex(FallRush.Net.PaletteTokens.SoftTopHex[(int)h]);
        public static Color CardSide(CardHue h) => Hex(FallRush.Net.PaletteTokens.SoftSideHex[(int)h]);
        public static Color CardRim(CardHue h) => Hex(FallRush.Net.PaletteTokens.RimHex[(int)h]);

        /// <summary>Ribbon height, title size, padding and rim width per card size (reference units at 1280 × 720).</summary>
        public static float CardRibbon(CardSize s) => s == CardSize.S ? 28f : s == CardSize.M ? 40f : 50f;
        public static int CardTitleSize(CardSize s) => s == CardSize.S ? 17 : s == CardSize.M ? 24 : 32;
        public static float CardRimWidth(CardSize s) => s == CardSize.S ? 3f : 5f;
        public static int CardPad(CardSize s) => s == CardSize.S ? 8 : s == CardSize.M ? 16 : 22;

        /// <summary>A built card: <see cref="Body"/> is a vertical layout below the ribbon; <see cref="Footer"/> a chip row.</summary>
        public sealed class Card
        {
            public RectTransform Root, Body, Ribbon;
            public Image Rim, Fill, RibbonFill, Icon, Shadow;
            public TMP_Text Title;
            public CardHue Hue;

            /// <summary>Recolours the card (rim, ribbon, pastel body).</summary>
            public void SetHue(CardHue h)
            {
                Hue = h;
                Rim.color = CardRim(h);
                RibbonFill.color = CardRim(h);
                Fill.color = CardPastel(h);
            }
        }

        private static Sprite s_Soft;

        /// <summary>A soft (blurred) rounded rectangle for drop shadows, 9-sliced.</summary>
        public static Sprite Soft => s_Soft != null ? s_Soft : s_Soft = MakeSoft(96, 22, 16, "UiSoft");

        private static Sprite MakeSoft(int size, int radius, int blur, string name)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            var inner = radius + blur;
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var cx = Mathf.Clamp(x + 0.5f, inner, size - inner);
                    var cy = Mathf.Clamp(y + 0.5f, inner, size - inner);
                    var d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy)) - radius;
                    var a = Mathf.Clamp01(1f - d / blur);
                    a = a * a * (3f - 2f * a);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }

            tex.SetPixels32(px);
            tex.Apply(false, true);
            var b = inner;
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
        }

        /// <summary>
        /// P7 A1.2: one card builder used everywhere. Layers: a soft drop shadow, the saturated rim, the pastel body, a 2-tone
        /// bevel (a lighter top edge, a mid-tone lower lip), a rim-coloured header ribbon with an icon and the title in the
        /// outlined display face. Our own shape (doc 17 §1): no copied banner or result-screen layout.
        /// </summary>
        public static Card MakeCard(Transform parent, CardSize size, CardHue hue, string title, Vector2 dims, UiIcon icon = UiIcon.None)
        {
            var go = new GameObject("Card", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = dims;
            var c = new Card { Root = rt, Hue = hue };
            var rim = CardRimWidth(size);
            c.Shadow = Shape(rt, "Shadow", new Color(Ink.r, Ink.g, Ink.b, 0.28f), Soft);
            Stretch(c.Shadow.rectTransform, -14f, new Vector2(0f, -7f));
            c.Rim = Shape(rt, "Rim", CardRim(hue));
            Stretch(c.Rim.rectTransform);
            c.Fill = Shape(rt, "Fill", CardPastel(hue));
            Stretch(c.Fill.rectTransform, rim);
            // bevel: a mid-tone lip along the bottom, a light band along the top
            var lip = Shape(c.Fill.transform, "Lip", new Color(1f, 1f, 1f, 0f));
            lip.color = Color.Lerp(CardSide(hue), CardPastel(hue), 0.55f);
            var lrt = lip.rectTransform;
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = new Vector2(1f, 0f);
            lrt.offsetMin = Vector2.zero;
            lrt.offsetMax = new Vector2(0f, size == CardSize.S ? 6f : 9f);
            var lipTop = Shape(c.Fill.transform, "LipCover", CardPastel(hue));
            var lct = lipTop.rectTransform;
            lct.anchorMin = Vector2.zero;
            lct.anchorMax = Vector2.one;
            lct.offsetMin = new Vector2(0f, size == CardSize.S ? 4f : 6f);
            lct.offsetMax = Vector2.zero;
            // the cover takes the body colour; SetHue keeps it in step through Fill's colour (see Retint)
            lipTop.gameObject.AddComponent<CardTint>().Source = c.Fill;
            var ribbonH = CardRibbon(size);
            var rib = new GameObject("Ribbon", typeof(RectTransform)).GetComponent<RectTransform>();
            rib.SetParent(c.Fill.transform, false);
            rib.anchorMin = new Vector2(0f, 1f);
            rib.anchorMax = Vector2.one;
            rib.pivot = new Vector2(0.5f, 1f);
            rib.offsetMin = new Vector2(0f, -ribbonH);
            rib.offsetMax = Vector2.zero;
            c.Ribbon = rib;
            c.RibbonFill = Shape(rib, "RibbonFill", CardRim(hue));
            Stretch(c.RibbonFill.rectTransform);
            var shine = Shape(rib, "Bevel", new Color(1f, 1f, 1f, 0.38f));
            var srt = shine.rectTransform;
            srt.anchorMin = new Vector2(0f, 0.58f);
            srt.anchorMax = Vector2.one;
            srt.offsetMin = new Vector2(8f, 0f);
            srt.offsetMax = new Vector2(-8f, -3f);
            // square off the ribbon's lower corners against the body
            var foot = Shape(rib, "RibbonFoot", CardRim(hue));
            foot.gameObject.AddComponent<CardTint>().Source = c.RibbonFill;
            var frt = foot.rectTransform;
            frt.anchorMin = Vector2.zero;
            frt.anchorMax = new Vector2(1f, 0.5f);
            frt.offsetMin = frt.offsetMax = Vector2.zero;
            foot.transform.SetSiblingIndex(0);
            var iconSize = ribbonH - 10f;
            float textLeft = 12f;
            if (icon != UiIcon.None)
            {
                c.Icon = Shape(rib, "Icon", Color.white, UiIcons.Get(icon));
                c.Icon.type = Image.Type.Simple;
                c.Icon.preserveAspect = true;
                var irt = c.Icon.rectTransform;
                irt.anchorMin = irt.anchorMax = new Vector2(0f, 0.5f);
                irt.pivot = new Vector2(0f, 0.5f);
                irt.anchoredPosition = new Vector2(10f, 0f);
                irt.sizeDelta = new Vector2(iconSize, iconSize);
                textLeft = 16f + iconSize;
            }

            c.Title = Shout(rib, title, CardTitleSize(size), Color.white);
            c.Title.alignment = TextAlignmentOptions.Left;
            c.Title.textWrappingMode = TextWrappingModes.NoWrap;
            c.Title.overflowMode = TextOverflowModes.Ellipsis;
            c.Title.GetComponent<LayoutElement>().ignoreLayout = true;
            var trt = (RectTransform)c.Title.transform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(textLeft, 0f);
            trt.offsetMax = new Vector2(-10f, 0f);

            var body = new GameObject("Body", typeof(RectTransform), typeof(VerticalLayoutGroup)).GetComponent<RectTransform>();
            body.SetParent(c.Fill.transform, false);
            body.anchorMin = Vector2.zero;
            body.anchorMax = Vector2.one;
            var pad = CardPad(size);
            body.offsetMin = new Vector2(0f, size == CardSize.S ? 6f : 9f);
            body.offsetMax = new Vector2(0f, -ribbonH);
            var v = body.GetComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(pad, pad, pad / 2 + 2, pad / 2 + 2);
            v.spacing = size == CardSize.S ? 2 : 6;
            v.childAlignment = TextAnchor.UpperCenter;
            v.childControlHeight = v.childControlWidth = true;
            v.childForceExpandHeight = false;
            v.childForceExpandWidth = true;
            c.Body = body;
            return c;
        }

        /// <summary>A small rounded chip (icon + label) for card footers, HUD tags and badges.</summary>
        public static RectTransform Chip(Transform parent, string text, Color fill, Color textColor, UiIcon icon = UiIcon.None, int fontSize = 13, float height = 24f)
        {
            var go = new GameObject("Chip", typeof(RectTransform), typeof(LayoutElement), typeof(HorizontalLayoutGroup));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            var bg = Shape(rt, "Bg", fill);
            bg.pixelsPerUnitMultiplier = 2.4f;
            Stretch(bg.rectTransform);
            var h = go.GetComponent<HorizontalLayoutGroup>();
            h.padding = new RectOffset(9, 10, 2, 2);
            h.spacing = 4;
            h.childAlignment = TextAnchor.MiddleCenter;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = h.childForceExpandHeight = false;
            var le = go.GetComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = height;
            le.flexibleHeight = 0f;
            if (icon != UiIcon.None)
                IconImage(rt, icon, textColor, height - 8f);
            var t = Label(rt, text, fontSize, textColor, FontStyle.Bold, (int)height - 4);
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Overflow;
            return rt;
        }

        /// <summary>An icon from the UI atlas as a fixed-size layout child.</summary>
        public static Image IconImage(Transform parent, UiIcon icon, Color tint, float size)
        {
            var go = new GameObject("Icon", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.sprite = UiIcons.Get(icon);
            img.color = tint;
            img.preserveAspect = true;
            img.raycastTarget = false;
            var le = go.GetComponent<LayoutElement>();
            le.minWidth = le.preferredWidth = size;
            le.minHeight = le.preferredHeight = size;
            ((RectTransform)go.transform).sizeDelta = new Vector2(size, size);
            return img;
        }

        /// <summary>
        /// A layout row whose visible content lives in a child "Face" (stretched, not layout-controlled), so stagger,
        /// rank slides and highlights animate the face while the layout keeps the slot. Returns the face; the slot is its parent.
        /// </summary>
        public static RectTransform FaceRow(Transform parent, float height, float spacing = 8f, TextAnchor align = TextAnchor.MiddleLeft)
        {
            var slot = new GameObject("Slot", typeof(RectTransform), typeof(LayoutElement));
            slot.transform.SetParent(parent, false);
            var le = slot.GetComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = height;
            var face = new GameObject("Face", typeof(RectTransform), typeof(HorizontalLayoutGroup)).GetComponent<RectTransform>();
            face.SetParent(slot.transform, false);
            Stretch(face);
            var h = face.GetComponent<HorizontalLayoutGroup>();
            h.spacing = spacing;
            h.childAlignment = align;
            h.padding = new RectOffset(8, 8, 0, 0);
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = true;
            return face;
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
