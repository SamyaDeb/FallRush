using System;
using UnityEngine;
using UnityEngine.UI;

namespace FallRush.Client
{
    /// <summary>
    /// G1 §3.6 settings panel (menu and in-game Esc): mouse sensitivity, invert Y, FOV, camera distance, reduced motion,
    /// master / music / SFX volume, nameplates on/off. Built from UGUI <see cref="DefaultControls"/>; every change is saved
    /// at once (<see cref="GameSettings"/>). Placeholder look: G3 restyles it.
    /// </summary>
    public static class SettingsPanel
    {
        private static readonly DefaultControls.Resources Res = default;

        public static RectTransform Build(Transform canvas, Vector2 size)
        {
            var panel = UiKit.Panel(canvas, size, UiKit.CloudWhite);
            var layout = panel.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(28, 28, 18, 18);
            layout.spacing = 6;
            UiKit.Label(panel, "SETTINGS", 24, UiKit.Grape, FontStyle.Bold);
            Slider(panel, "Mouse sensitivity", GameSettings.MinSensitivity, GameSettings.MaxSensitivity, () => GameSettings.Sensitivity, v => GameSettings.Sensitivity = v, v => v.ToString("F2"));
            Toggle(panel, "Invert Y", () => GameSettings.InvertY, v => GameSettings.InvertY = v);
            Slider(panel, "Field of view", GameSettings.MinFovOffset, GameSettings.MaxFovOffset, () => GameSettings.FovOffset, v => GameSettings.FovOffset = Mathf.Round(v), v => (v >= 0 ? "+" : "") + v.ToString("F0") + "°");
            Slider(panel, "Camera distance", GameSettings.MinCamDistance, GameSettings.MaxCamDistance, () => GameSettings.CameraDistance, v => GameSettings.CameraDistance = v, v => "×" + v.ToString("F2"));
            Toggle(panel, "Reduced motion (no shake / FOV kick / tumbles)", () => GameSettings.ReducedMotion, v => GameSettings.ReducedMotion = v);
            Toggle(panel, "Show nameplates", () => GameSettings.ShowNameplates, v => GameSettings.ShowNameplates = v);
            Toggle(panel, "Graphics: High (off = Low)", () => GameSettings.GraphicsHigh, v => { GameSettings.GraphicsHigh = v; GraphicsQuality.Apply(Camera.main); });
            Slider(panel, "Master volume", 0f, 1f, () => GameSettings.MasterVolume, v => GameSettings.MasterVolume = v, Pct);
            Slider(panel, "Music volume", 0f, 1f, () => GameSettings.MusicVolume, v => GameSettings.MusicVolume = v, Pct);
            Slider(panel, "SFX volume", 0f, 1f, () => GameSettings.SfxVolume, v => GameSettings.SfxVolume = v, Pct);
            return panel;
        }

        private static string Pct(float v) => Mathf.RoundToInt(v * 100f) + "%";

        private static void Slider(Transform parent, string name, float min, float max, Func<float> get, Action<float> set, Func<float, string> fmt)
        {
            var row = Row(parent, out var label);
            var go = DefaultControls.CreateSlider(Res);
            go.transform.SetParent(row, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = 220f;
            le.minHeight = 22f;
            var s = go.GetComponent<Slider>();
            s.minValue = min;
            s.maxValue = max;
            s.value = get();
            Tint(go);
            label.text = $"{name}  <b>{fmt(get())}</b>";
            s.onValueChanged.AddListener(v =>
            {
                set(v);
                GameSettings.Save();
                label.text = $"{name}  <b>{fmt(get())}</b>";
            });
        }

        private static void Toggle(Transform parent, string name, Func<bool> get, Action<bool> set)
        {
            var row = Row(parent, out var label);
            label.text = name;
            var go = DefaultControls.CreateToggle(Res);
            go.transform.SetParent(row, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = 220f;
            le.minHeight = 22f;
            var t = go.GetComponent<Toggle>();
            t.isOn = get();
            foreach (var txt in go.GetComponentsInChildren<Text>(true))
            {
                txt.font = UiKit.Font;
                txt.text = t.isOn ? "on" : "off";
                txt.color = UiKit.Ink;
            }

            Tint(go);
            t.onValueChanged.AddListener(v =>
            {
                set(v);
                GameSettings.Save();
                foreach (var txt in go.GetComponentsInChildren<Text>(true))
                    txt.text = v ? "on" : "off";
            });
        }

        private static RectTransform Row(Transform parent, out Text label)
        {
            var go = new GameObject("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var h = go.GetComponent<HorizontalLayoutGroup>();
            h.spacing = 12;
            h.childAlignment = TextAnchor.MiddleLeft;
            h.childControlWidth = true;
            h.childControlHeight = true;
            h.childForceExpandWidth = false;
            go.GetComponent<LayoutElement>().minHeight = 28f;
            label = UiKit.Label(go.transform, string.Empty, 17, UiKit.Ink);
            label.alignment = TextAnchor.MiddleLeft;
            label.supportRichText = true;
            label.GetComponent<LayoutElement>().preferredWidth = 330f;
            return (RectTransform)go.transform;
        }

        private static void Tint(GameObject go)
        {
            foreach (var img in go.GetComponentsInChildren<Image>(true))
            {
                var n = img.gameObject.name;
                img.color = n == "Fill" || n == "Checkmark" ? UiKit.Grape : n == "Handle" ? UiKit.Magenta : UiKit.SkyLight;
            }
        }
    }
}
