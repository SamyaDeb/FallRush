using FallRush.Shared;
using UnityEngine;
using TMPro;
using UnityEngine.UI;

namespace FallRush.Client
{
    /// <summary>
    /// G1 §3.5.1 screen-space nameplates (replaces the M2–M4 world-space TextMesh labels that grew huge near the camera,
    /// overlapped and stacked "BOT BOT"): a constant-size label above each Rushling's head, faded with distance (full to
    /// 10 m, gone at 32 m), hidden when the head is behind level geometry (one raycast per player every third frame),
    /// a farther label that overlaps a nearer one is dimmed, the name carries a small BOT tag, and your own marker is a
    /// distinct magenta "▼ YOU" (G3: TMP body face with a white outline; the symbol font renders ▼ in WebGL). Off with Settings → Show nameplates (your marker stays). Allocation-free per frame.
    /// </summary>
    public sealed class Nameplates
    {
        private const float FullAlphaDist = 10f, ZeroAlphaDist = 32f;
        private readonly TMP_Text[] _labels = new TMP_Text[Protocol.MaxPlayers];
        private readonly RectTransform[] _rts = new RectTransform[Protocol.MaxPlayers];
        private readonly string[] _text = new string[Protocol.MaxPlayers];
        private readonly bool[] _occluded = new bool[Protocol.MaxPlayers];
        private readonly Vector3[] _screen = new Vector3[Protocol.MaxPlayers];
        private readonly bool[] _on = new bool[Protocol.MaxPlayers];
        private readonly float[] _alpha = new float[Protocol.MaxPlayers];
        private readonly int _mask;
        private readonly RectTransform _canvas;
        private int _frame;
        private readonly int[] _order = new int[Protocol.MaxPlayers];

        private float Depth(int i, int own) => i == own ? -1f : _screen[i].z;

        public Nameplates(RectTransform canvas, int occluderMask)
        {
            _canvas = canvas;
            _mask = occluderMask;
            for (var i = 0; i < _labels.Length; i++)
            {
                var go = new GameObject($"Nameplate{i}", typeof(RectTransform), typeof(TextMeshProUGUI));
                var rt = (RectTransform)go.transform;
                rt.SetParent(canvas, false);
                rt.anchorMin = rt.anchorMax = Vector2.zero;
                rt.pivot = new Vector2(0.5f, 0f);
                rt.sizeDelta = new Vector2(220f, 26f);
                var t = go.GetComponent<TextMeshProUGUI>();
                t.font = UiKit.Body;
                if (UiKit.BodyOutlined != null)
                    t.fontSharedMaterial = UiKit.BodyOutlined;
                t.fontSize = 17;
                t.fontStyle = FontStyles.Bold;
                t.alignment = TextAlignmentOptions.Bottom;
                t.richText = true;
                t.raycastTarget = false;
                t.textWrappingMode = TextWrappingModes.NoWrap;
                go.SetActive(false);
                _labels[i] = t;
                _rts[i] = rt;
            }
        }

        public static string LabelFor(string name, bool bot, bool own) =>
            own ? "<size=21><color=#FF3EA5>▼ YOU</color></size>" : bot ? $"<size=12><color=#7B2FF7>BOT</color></size> {name}" : name;

        /// <summary>Call once per frame per slot, before <see cref="Layout"/>.</summary>
        public void Set(int slot, bool visible, Vector3 headWorld, string text)
        {
            _on[slot] = visible;
            if (!visible)
                return;
            _screen[slot] = headWorld;
            if (!ReferenceEquals(_text[slot], text))
            {
                _text[slot] = text;
                _labels[slot].text = text;
            }
        }

        public void Layout(Camera cam, int ownSlot)
        {
            _frame++;
            var show = GameSettings.ShowNameplates;
            var scale = _canvas.lossyScale.x > 0f ? 1f / _canvas.lossyScale.x : 1f;
            var camPos = cam != null ? cam.transform.position : Vector3.zero;
            for (var i = 0; i < _labels.Length; i++)
            {
                var own = i == ownSlot;
                var a = 0f;
                if (_on[i] && cam != null && (show || own))
                {
                    var world = _screen[i];
                    var sp = cam.WorldToScreenPoint(world);
                    var dist = (world - camPos).magnitude;
                    if (sp.z > 0.3f)
                    {
                        // Occlusion: refresh a third of the players each frame.
                        if ((i + _frame) % 3 == 0)
                        {
                            var d = world - camPos;
                            _occluded[i] = !own && Physics.Raycast(camPos, d / Mathf.Max(0.01f, dist), dist - 0.4f, _mask, QueryTriggerInteraction.Ignore);
                        }

                        a = own ? 1f : _occluded[i] ? 0f : 1f - Mathf.Clamp01((dist - FullAlphaDist) / (ZeroAlphaDist - FullAlphaDist));
                        _rts[i].anchoredPosition = new Vector2(sp.x * scale, sp.y * scale + 6f);
                        _screen[i] = new Vector3(sp.x, sp.y, dist);
                    }
                }

                _alpha[i] = a;
            }

            // A label overlapping a nearer one (or your own marker, which counts as nearest) is hidden: no "BOT BOT" stacks.
            // Nearest first, so a hidden label never hides another one.
            for (var i = 0; i < _labels.Length; i++)
                _order[i] = i;
            for (var a = 1; a < _order.Length; a++) // insertion sort by depth (12 items, no allocation)
            {
                var v = _order[a];
                var d = Depth(v, ownSlot);
                var b = a - 1;
                while (b >= 0 && Depth(_order[b], ownSlot) > d)
                {
                    _order[b + 1] = _order[b];
                    b--;
                }

                _order[b + 1] = v;
            }
            for (var k = 0; k < _order.Length; k++)
            {
                var i = _order[k];
                if (_alpha[i] <= 0f || i == ownSlot)
                    continue;
                for (var m = 0; m < k; m++)
                {
                    var j = _order[m];
                    if (_alpha[j] <= 0f)
                        continue;
                    if (Mathf.Abs(_screen[j].x - _screen[i].x) < 110f && Mathf.Abs(_screen[j].y - _screen[i].y) < 24f)
                    {
                        _alpha[i] = 0f;
                        break;
                    }
                }
            }

            for (var i = 0; i < _labels.Length; i++)
            {
                var on = _alpha[i] > 0.02f;
                var go = _labels[i].gameObject;
                if (go.activeSelf != on)
                    go.SetActive(on);
                if (!on)
                    continue;
                var c = _labels[i].color;
                if (!Mathf.Approximately(c.a, _alpha[i]))
                {
                    c = i == ownSlot ? Color.white : UiKit.Ink;
                    c.a = _alpha[i];
                    _labels[i].color = c;
                }
            }
        }
    }
}
