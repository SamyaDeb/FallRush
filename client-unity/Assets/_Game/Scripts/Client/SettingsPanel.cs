using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FallRush.Client
{
    /// <summary>
    /// Settings (menu and in-game Esc): mouse sensitivity, invert Y, FOV, camera distance, reduced motion, nameplates,
    /// graphics High / Low, master / music / SFX volume. Our own controls from the UI kit (rounded track + Grape fill +
    /// Magenta knob sliders, pill switches). Every change is saved at once.
    /// 2026-11-27 fix: the slider fills kept the default 100 × 100 rect size (Slider only drives anchors), so every fill
    /// spilled 100 units past its track as a big purple block over the title, switches and values; slider rows were
    /// wider than the panel; there was no way to close it but the top button / Esc. Now: fixed fill / knob geometry,
    /// rows that fit, sections, Reset defaults + Done, and the panel sizes itself to its content (centred by callers).
    /// </summary>
    public static class SettingsPanel
    {
        public const float Width = 560f;
        private const float ControlWidth = 190f;
        private const float ValueWidth = 58f;
        private const float RowHeight = 32f;
        private const float KnobSize = 22f;
        private const float KnobInset = 12f; // half the knob: the knob centre travels the track minus this at each end

        /// <summary>Builds the panel (centred, hidden state is the caller's). <paramref name="onClose"/> runs on Done.</summary>
        public static RectTransform Build(Transform canvas, Action onClose)
        {
            var panel = UiKit.Panel(canvas, new Vector2(Width, 100f), UiKit.CloudWhite);
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
            panel.anchoredPosition = Vector2.zero;
            var layout = panel.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(30, 30, 18, 24);
            layout.spacing = 2;
            layout.childForceExpandWidth = true;
            var fit = panel.gameObject.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var refresh = new List<Action>();
            UiKit.Shout(panel, "SETTINGS", 30, UiKit.SunYellow).GetComponent<LayoutElement>().minHeight = 40f;

            Section(panel, "CONTROLS");
            Slider(panel, refresh, "Mouse sensitivity", GameSettings.MinSensitivity, GameSettings.MaxSensitivity, () => GameSettings.Sensitivity, v => GameSettings.Sensitivity = v, v => v.ToString("F2"));
            Toggle(panel, refresh, "Invert Y", () => GameSettings.InvertY, v => GameSettings.InvertY = v);

            Section(panel, "CAMERA & DISPLAY");
            Slider(panel, refresh, "Field of view", GameSettings.MinFovOffset, GameSettings.MaxFovOffset, () => GameSettings.FovOffset, v => GameSettings.FovOffset = Mathf.Round(v), v => (v > 0 ? "+" : "") + v.ToString("F0") + "°");
            Slider(panel, refresh, "Camera distance", GameSettings.MinCamDistance, GameSettings.MaxCamDistance, () => GameSettings.CameraDistance, v => GameSettings.CameraDistance = v, v => "×" + v.ToString("F2"));
            Toggle(panel, refresh, "Reduced motion", () => GameSettings.ReducedMotion, v => GameSettings.ReducedMotion = v);
            Toggle(panel, refresh, "Show nameplates", () => GameSettings.ShowNameplates, v => GameSettings.ShowNameplates = v);
            Toggle(panel, refresh, "High-quality graphics", () => GameSettings.GraphicsHigh, v =>
            {
                GameSettings.GraphicsHigh = v;
                GraphicsQuality.Apply(Camera.main);
            });

            Section(panel, "AUDIO");
            Slider(panel, refresh, "Master volume", 0f, 1f, () => GameSettings.MasterVolume, v => GameSettings.MasterVolume = v, Pct, true);
            Slider(panel, refresh, "Music", 0f, 1f, () => GameSettings.MusicVolume, v => GameSettings.MusicVolume = v, Pct);
            Slider(panel, refresh, "Sound effects", 0f, 1f, () => GameSettings.SfxVolume, v => GameSettings.SfxVolume = v, Pct, true);

            Spacer(panel, 10f);
            var buttons = UiKit.Row(panel, 46f, 14f, TextAnchor.MiddleCenter);
            SmallButton(buttons, "Reset defaults", UiKit.SkyLight, () =>
            {
                GameSettings.ResetToDefaults();
                GraphicsQuality.Apply(Camera.main);
                foreach (var r in refresh)
                    r();
            });
            SmallButton(buttons, "Done", UiKit.SunYellow, () => onClose?.Invoke());
            return panel;
        }

        private static string Pct(float v) => Mathf.RoundToInt(v * 100f) + "%";

        private static void Section(Transform parent, string title)
        {
            Spacer(parent, 6f);
            var t = UiKit.Label(parent, title, 13, UiKit.Grape, FontStyle.Bold, 20);
            t.font = UiKit.Body;
            t.fontStyle = FontStyles.Bold;
            t.characterSpacing = 6f;
            t.alignment = TextAlignmentOptions.Left;
        }

        private static void Spacer(Transform parent, float h)
        {
            var go = new GameObject("Spacer", typeof(RectTransform), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var le = go.GetComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = h;
        }

        private static float s_NextPreview;

        private static void Slider(Transform parent, List<Action> refresh, string name, float min, float max, Func<float> get, Action<float> set, Func<float, string> fmt, bool previewSound = false)
        {
            var row = Row(parent, name, out var value);
            var go = new GameObject("Slider", typeof(RectTransform), typeof(LayoutElement));
            go.transform.SetParent(row, false);
            go.transform.SetSiblingIndex(1);
            var le = go.GetComponent<LayoutElement>();
            le.preferredWidth = le.minWidth = ControlWidth;
            le.flexibleWidth = 0f;
            var rt = (RectTransform)go.transform;

            // track: a rounded bar through the middle (also the click / drag area)
            var track = UiKit.Shape(rt, "Track", UiKit.SkyLight, null, false, true);
            Band(track.rectTransform, 0f, 0f);
            track.pixelsPerUnitMultiplier = 4f;

            // fill: Slider drives only the anchors (0 → value), so the rect offsets must be set here. It starts at the
            // track's left edge and ends under the knob centre.
            var fillArea = new GameObject("FillArea", typeof(RectTransform)).GetComponent<RectTransform>();
            fillArea.SetParent(rt, false);
            Band(fillArea, KnobInset, -KnobInset);
            var fill = UiKit.Shape(fillArea, "Fill", UiKit.Grape);
            fill.rectTransform.offsetMin = new Vector2(-KnobInset, 0f);
            fill.rectTransform.offsetMax = Vector2.zero;
            fill.pixelsPerUnitMultiplier = 4f;

            // knob: the handle is an invisible full-height strip (Slider stretches it vertically); the round knob sits
            // centred in it at a fixed size so it is always a circle.
            var handleArea = new GameObject("HandleArea", typeof(RectTransform)).GetComponent<RectTransform>();
            handleArea.SetParent(rt, false);
            UiKit.Stretch(handleArea);
            handleArea.offsetMin = new Vector2(KnobInset, 0f);
            handleArea.offsetMax = new Vector2(-KnobInset, 0f);
            var handle = UiKit.Shape(handleArea, "Handle", new Color(1f, 1f, 1f, 0f), null, false, true);
            handle.type = Image.Type.Simple;
            handle.rectTransform.sizeDelta = new Vector2(KnobSize + 6f, 0f);
            var knob = UiKit.Shape(handle.transform, "Knob", UiKit.Grape, UiKit.Circle);
            knob.type = Image.Type.Simple;
            var krt = knob.rectTransform;
            krt.anchorMin = krt.anchorMax = new Vector2(0.5f, 0.5f);
            krt.sizeDelta = new Vector2(KnobSize, KnobSize);
            var inner = UiKit.Shape(knob.transform, "In", UiKit.Magenta, UiKit.Circle);
            inner.type = Image.Type.Simple;
            UiKit.Stretch(inner.rectTransform, 4f);

            var s = go.AddComponent<Slider>();
            s.fillRect = fill.rectTransform;
            s.handleRect = handle.rectTransform;
            s.targetGraphic = knob;
            s.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            s.minValue = min;
            s.maxValue = max;
            s.SetValueWithoutNotify(get());
            value.text = fmt(get());
            s.onValueChanged.AddListener(v =>
            {
                set(v);
                GameSettings.Save();
                value.text = fmt(get());
                if (previewSound && Time.unscaledTime >= s_NextPreview)
                {
                    s_NextPreview = Time.unscaledTime + 0.18f; // a tick at the new level while dragging, not a buzz
                    GameAudio.Ui(GameAudio.UiCue.Tick);
                }
            });
            refresh.Add(() =>
            {
                s.SetValueWithoutNotify(get());
                value.text = fmt(get());
            });
        }

        private static void Toggle(Transform parent, List<Action> refresh, string name, Func<bool> get, Action<bool> set)
        {
            var row = Row(parent, name, out var value);
            // right-align the switch inside the control column so it lines up with the slider ends
            var cell = new GameObject("SwitchCell", typeof(RectTransform), typeof(LayoutElement));
            cell.transform.SetParent(row, false);
            cell.transform.SetSiblingIndex(1);
            var cle = cell.GetComponent<LayoutElement>();
            cle.preferredWidth = cle.minWidth = ControlWidth;
            cle.flexibleWidth = 0f;
            var go = new GameObject("Switch", typeof(RectTransform));
            go.transform.SetParent(cell.transform, false);
            var srt = (RectTransform)go.transform;
            srt.anchorMin = srt.anchorMax = srt.pivot = new Vector2(1f, 0.5f);
            srt.sizeDelta = new Vector2(54f, 28f);
            srt.anchoredPosition = Vector2.zero;
            var track = UiKit.Shape(go.transform, "Track", UiKit.SkyLight, null, false, true);
            UiKit.Stretch(track.rectTransform);
            track.pixelsPerUnitMultiplier = 2f;
            var knob = UiKit.Shape(track.transform, "Knob", Color.white, UiKit.Circle);
            knob.type = Image.Type.Simple;
            var krt = knob.rectTransform;
            krt.sizeDelta = new Vector2(22f, 22f);
            var t = go.AddComponent<Toggle>();
            t.targetGraphic = track;
            void Show(bool v)
            {
                track.color = v ? UiKit.Mint : UiKit.SkyLight;
                krt.anchorMin = krt.anchorMax = new Vector2(v ? 1f : 0f, 0.5f);
                krt.anchoredPosition = new Vector2(v ? -14f : 14f, 0f);
                value.text = v ? "On" : "Off";
                value.color = v ? UiKit.Grape : UiKit.Grey;
            }

            t.SetIsOnWithoutNotify(get());
            Show(t.isOn);
            t.onValueChanged.AddListener(v =>
            {
                set(v);
                GameSettings.Save();
                Show(v);
                GameAudio.Ui(GameAudio.UiCue.Tick);
            });
            refresh.Add(() =>
            {
                t.SetIsOnWithoutNotify(get());
                Show(t.isOn);
            });
        }

        /// <summary>label (flexible) · control (fixed, added by the caller at index 1) · value (fixed, right).</summary>
        private static RectTransform Row(Transform parent, string name, out TMP_Text value)
        {
            var row = UiKit.Row(parent, RowHeight, 12f);
            var label = UiKit.Cell(row, name, 120f, 16, UiKit.Ink);
            var lle = label.GetComponent<LayoutElement>();
            lle.flexibleWidth = 1f;
            value = UiKit.Cell(row, string.Empty, ValueWidth, 16, UiKit.Grape, TextAlignmentOptions.Right, true);
            return row;
        }

        /// <summary>A horizontal band through the vertical middle of the control (track / fill height).</summary>
        private static void Band(RectTransform rt, float left, float right)
        {
            rt.anchorMin = new Vector2(0f, 0.34f);
            rt.anchorMax = new Vector2(1f, 0.66f);
            rt.offsetMin = new Vector2(left, 0f);
            rt.offsetMax = new Vector2(right, 0f);
        }

        private static void SmallButton(Transform row, string text, Color fill, UnityEngine.Events.UnityAction onClick)
        {
            var b = UiKit.Button(row, text, fill, onClick);
            var le = b.GetComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = 44f;
            le.minWidth = 160f;
            le.preferredWidth = 200f;
            b.GetComponentInChildren<TMP_Text>().fontSize = 20;
        }
    }
}
