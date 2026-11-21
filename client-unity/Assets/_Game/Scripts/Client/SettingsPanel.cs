using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FallRush.Client
{
    /// <summary>
    /// Settings (menu and in-game Esc): mouse sensitivity, invert Y, FOV, camera distance, reduced motion, nameplates,
    /// graphics High / Low, master / music / SFX volume. G3.1: our own controls from the UI kit (rounded track + Grape
    /// fill + Magenta knob sliders, pill switches) instead of UGUI's default controls. Every change is saved at once.
    /// </summary>
    public static class SettingsPanel
    {
        public static RectTransform Build(Transform canvas, Vector2 size)
        {
            var panel = UiKit.Panel(canvas, size, UiKit.CloudWhite);
            var layout = panel.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(30, 30, 20, 22);
            layout.spacing = 4;
            UiKit.Shout(panel, "SETTINGS", 30, UiKit.SunYellow);
            Slider(panel, "Mouse sensitivity", GameSettings.MinSensitivity, GameSettings.MaxSensitivity, () => GameSettings.Sensitivity, v => GameSettings.Sensitivity = v, v => v.ToString("F2"));
            Toggle(panel, "Invert Y", () => GameSettings.InvertY, v => GameSettings.InvertY = v);
            Slider(panel, "Field of view", GameSettings.MinFovOffset, GameSettings.MaxFovOffset, () => GameSettings.FovOffset, v => GameSettings.FovOffset = Mathf.Round(v), v => (v >= 0 ? "+" : "") + v.ToString("F0") + "°");
            Slider(panel, "Camera distance", GameSettings.MinCamDistance, GameSettings.MaxCamDistance, () => GameSettings.CameraDistance, v => GameSettings.CameraDistance = v, v => "×" + v.ToString("F2"));
            Toggle(panel, "Reduced motion (no shake, FOV kick, tumbles)", () => GameSettings.ReducedMotion, v => GameSettings.ReducedMotion = v);
            Toggle(panel, "Show nameplates", () => GameSettings.ShowNameplates, v => GameSettings.ShowNameplates = v);
            Toggle(panel, "Graphics: High (off = Low)", () => GameSettings.GraphicsHigh, v =>
            {
                GameSettings.GraphicsHigh = v;
                GraphicsQuality.Apply(Camera.main);
            });
            Slider(panel, "Master volume", 0f, 1f, () => GameSettings.MasterVolume, v => GameSettings.MasterVolume = v, Pct);
            Slider(panel, "Music volume", 0f, 1f, () => GameSettings.MusicVolume, v => GameSettings.MusicVolume = v, Pct);
            Slider(panel, "SFX volume", 0f, 1f, () => GameSettings.SfxVolume, v => GameSettings.SfxVolume = v, Pct);
            return panel;
        }

        private static string Pct(float v) => Mathf.RoundToInt(v * 100f) + "%";

        private static void Slider(Transform parent, string name, float min, float max, Func<float> get, Action<float> set, Func<float, string> fmt)
        {
            var row = Row(parent, name, out var value);
            var go = new GameObject("Slider", typeof(RectTransform), typeof(LayoutElement));
            go.transform.SetParent(row, false);
            var le = go.GetComponent<LayoutElement>();
            le.preferredWidth = le.minWidth = 200f;
            le.minHeight = 26f;
            var rt = (RectTransform)go.transform;
            var track = UiKit.Shape(rt, "Track", UiKit.SkyLight, null, false, true);
            track.rectTransform.anchorMin = new Vector2(0f, 0.3f);
            track.rectTransform.anchorMax = new Vector2(1f, 0.7f);
            track.rectTransform.offsetMin = track.rectTransform.offsetMax = Vector2.zero;
            track.pixelsPerUnitMultiplier = 4f;
            var fillArea = new GameObject("FillArea", typeof(RectTransform)).GetComponent<RectTransform>();
            fillArea.SetParent(rt, false);
            fillArea.anchorMin = new Vector2(0f, 0.3f);
            fillArea.anchorMax = new Vector2(1f, 0.7f);
            fillArea.offsetMin = fillArea.offsetMax = Vector2.zero;
            var fill = UiKit.Shape(fillArea, "Fill", UiKit.Grape);
            fill.pixelsPerUnitMultiplier = 4f;
            var handleArea = new GameObject("HandleArea", typeof(RectTransform)).GetComponent<RectTransform>();
            handleArea.SetParent(rt, false);
            UiKit.Stretch(handleArea);
            handleArea.offsetMin = new Vector2(10f, 0f);
            handleArea.offsetMax = new Vector2(-10f, 0f);
            var handle = UiKit.Shape(handleArea, "Knob", UiKit.Grape, UiKit.Circle, false, true);
            handle.rectTransform.sizeDelta = new Vector2(24f, 24f);
            var inner = UiKit.Shape(handle.transform, "In", UiKit.Magenta, UiKit.Circle);
            UiKit.Stretch(inner.rectTransform, 4f);
            value.transform.SetAsLastSibling();
            var s = go.AddComponent<Slider>();
            s.fillRect = fill.rectTransform;
            s.handleRect = handle.rectTransform;
            s.targetGraphic = handle;
            s.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            s.minValue = min;
            s.maxValue = max;
            s.value = get();
            value.text = fmt(get());
            s.onValueChanged.AddListener(v =>
            {
                set(v);
                GameSettings.Save();
                value.text = fmt(get());
            });
        }

        private static void Toggle(Transform parent, string name, Func<bool> get, Action<bool> set)
        {
            var row = Row(parent, name, out var value);
            var go = new GameObject("Switch", typeof(RectTransform), typeof(LayoutElement));
            go.transform.SetParent(row, false);
            var le = go.GetComponent<LayoutElement>();
            le.preferredWidth = le.minWidth = 64f;
            le.minHeight = 30f;
            var track = UiKit.Shape(go.transform, "Track", UiKit.SkyLight, null, false, true);
            UiKit.Stretch(track.rectTransform);
            var knob = UiKit.Shape(track.transform, "Knob", Color.white, UiKit.Circle);
            var krt = knob.rectTransform;
            krt.anchorMin = krt.anchorMax = new Vector2(0f, 0.5f);
            krt.sizeDelta = new Vector2(24f, 24f);
            value.transform.SetAsLastSibling();
            var t = go.AddComponent<Toggle>();
            t.targetGraphic = track;
            t.isOn = get();
            void Show(bool v)
            {
                track.color = v ? UiKit.Mint : UiKit.SkyLight;
                krt.anchorMin = krt.anchorMax = new Vector2(v ? 1f : 0f, 0.5f);
                krt.anchoredPosition = new Vector2(v ? -16f : 16f, 0f);
                value.text = v ? "On" : "Off";
            }

            Show(t.isOn);
            t.onValueChanged.AddListener(v =>
            {
                set(v);
                GameSettings.Save();
                Show(v);
                GameAudio.Ui(GameAudio.UiCue.Tick);
            });
        }

        private static RectTransform Row(Transform parent, string name, out TMP_Text value)
        {
            var row = UiKit.Row(parent, 36f, 12f);
            var label = UiKit.Cell(row, name, 300f, 16, UiKit.Ink);
            label.textWrappingMode = TextWrappingModes.Normal;
            label.overflowMode = TextOverflowModes.Overflow;
            value = UiKit.Cell(row, string.Empty, 60f, 16, UiKit.Grape, TextAlignmentOptions.Right, true);
            return row;
        }
    }
}
