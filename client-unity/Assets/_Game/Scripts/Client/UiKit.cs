using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace FallRush.Client
{
    /// <summary>
    /// Placeholder UI built in code: flat shapes in the Fall Rush palette (doc 17 §3/§5), Unity's built-in
    /// LegacyRuntime font. No third-party art or fonts.
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

        private static Font s_Font;

        public static Font Font => s_Font != null ? s_Font : s_Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        public static Color Hex(int rgb) =>
            new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

        public static RectTransform CreateCanvas(string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 0.5f;

            if (Object.FindAnyObjectByType<EventSystem>() == null)
                new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

            return (RectTransform)go.transform;
        }

        public static RectTransform Panel(Transform parent, Vector2 size, Color fill)
        {
            var go = new GameObject("Panel", typeof(RectTransform), typeof(Image), typeof(Outline));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = size;
            go.GetComponent<Image>().color = fill;
            var outline = go.GetComponent<Outline>();
            outline.effectColor = Grape;
            outline.effectDistance = new Vector2(6, -6);
            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(40, 40, 32, 32);
            layout.spacing = 16;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            return rt;
        }

        public static Text Label(Transform parent, string text, int size, Color color, FontStyle style = FontStyle.Normal, int minHeight = 0)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(Text), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = Font;
            t.text = text;
            t.fontSize = size;
            t.fontStyle = style;
            t.color = color;
            t.alignment = TextAnchor.MiddleCenter;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            go.GetComponent<LayoutElement>().minHeight = minHeight > 0 ? minHeight : size + 8;
            return t;
        }

        public static Button Button(Transform parent, string text, Color fill, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = fill;
            var outline = go.GetComponent<Outline>();
            outline.effectColor = Grape;
            outline.effectDistance = new Vector2(4, -4);
            var le = go.GetComponent<LayoutElement>();
            le.minHeight = 64;
            le.preferredWidth = 420;

            var button = go.GetComponent<Button>();
            var colors = button.colors;
            colors.highlightedColor = new Color(1f, 1f, 1f, 0.92f);
            colors.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            colors.disabledColor = new Color(0.6f, 0.6f, 0.6f, 0.6f);
            button.colors = colors;
            button.onClick.AddListener(onClick);

            var label = Label(go.transform, text, 26, Ink, FontStyle.Bold);
            var lrt = (RectTransform)label.transform;
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = lrt.offsetMax = Vector2.zero;
            return button;
        }

        public static void SetButtonText(Button button, string text) =>
            button.GetComponentInChildren<Text>().text = text;
    }
}
