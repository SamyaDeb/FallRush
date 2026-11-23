using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FallRush.Client
{
    /// <summary>
    /// P7 A1.3: the receipt's verification strip, used by the match HUD (Classic / practice) and the economy HUD (the
    /// on-chain receipt). In sequence: "Rollup ✓" ticks in → a connector line draws → "Solana ✓" ticks in → the minted
    /// asset tiles pop in (rarity colour + name, "in your wallet") → badges → the buttons. A practice match shows a clear
    /// "Practice: not recorded" card instead. Everything is driven by the caller's state; the sequence plays once per match.
    /// </summary>
    public sealed class ReceiptCard : MonoBehaviour
    {
        private RectTransform _root, _steps, _practice, _assets, _badges, _line, _lineFill;
        private RectTransform _rollup, _solana;
        private Image _rollupDot, _solanaDot;
        private TMP_Text _rollupText, _solanaText, _practiceTitle, _practiceLine;
        private Button _explorer, _open;
        private string _explorerUrl, _receiptUrl;
        private bool _rollupOn, _solanaOn;
        private float _lineT = -1f;
        private int _assetCount, _badgeBits = -1;

        public static ReceiptCard Create(Transform parent, float width, float height)
        {
            var go = new GameObject("ReceiptCard", typeof(RectTransform), typeof(LayoutElement), typeof(VerticalLayoutGroup));
            go.transform.SetParent(parent, false);
            var le = go.GetComponent<LayoutElement>();
            le.preferredWidth = width;
            le.minHeight = height;
            var v = go.GetComponent<VerticalLayoutGroup>();
            v.spacing = 6;
            v.childAlignment = TextAnchor.UpperCenter;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandWidth = true;
            v.childForceExpandHeight = false;
            var c = go.AddComponent<ReceiptCard>();
            c.Build((RectTransform)go.transform);
            return c;
        }

        private void Build(RectTransform rt)
        {
            _root = rt;
            // steps: [Rollup ✓] ——— [Solana ✓]
            _steps = UiKit.Row(rt, 40f, 0f, TextAnchor.MiddleCenter);
            _rollup = Step(_steps, "Rollup", out _rollupDot, out _rollupText);
            var lineSlot = new GameObject("Line", typeof(RectTransform), typeof(LayoutElement)).GetComponent<RectTransform>();
            lineSlot.SetParent(_steps, false);
            var lle = lineSlot.GetComponent<LayoutElement>();
            lle.minWidth = lle.preferredWidth = 120f;
            lle.flexibleWidth = 1f;
            var track = UiKit.Shape(lineSlot, "Track", new Color(UiKit.Ink.r, UiKit.Ink.g, UiKit.Ink.b, 0.15f));
            Bar(track.rectTransform);
            var fill = UiKit.Shape(lineSlot, "Fill", UiKit.Mint);
            Bar(fill.rectTransform);
            _line = lineSlot;
            _lineFill = fill.rectTransform;
            _lineFill.anchorMax = new Vector2(0f, 0.5f);
            _solana = Step(_steps, "Solana", out _solanaDot, out _solanaText);
            _assets = UiKit.Row(rt, 64f, 8f, TextAnchor.MiddleCenter);
            _badges = UiKit.Row(rt, 28f, 6f, TextAnchor.MiddleCenter);
            var buttons = UiKit.Row(rt, 44f, 10f, TextAnchor.MiddleCenter);
            _explorer = Small(buttons, "View on explorer", UiKit.SkyLight, () => { if (!string.IsNullOrEmpty(_explorerUrl)) Application.OpenURL(_explorerUrl); });
            _open = Small(buttons, "Open receipt", UiKit.Mint, () => { if (!string.IsNullOrEmpty(_receiptUrl)) Application.OpenURL(_receiptUrl); });
            // practice variant
            var pc = UiKit.MakeCard(rt, UiKit.CardSize.S, UiKit.CardHue.Soda, "PRACTICE", new Vector2(0f, 84f), UiIcon.Capsule);
            pc.Root.gameObject.AddComponent<LayoutElement>().minHeight = 84f;
            _practice = pc.Root;
            _practiceTitle = UiKit.Label(pc.Body, string.Empty, 17, UiKit.Ink, FontStyle.Bold, 22);
            _practiceLine = UiKit.Label(pc.Body, string.Empty, 13, UiKit.Grape, FontStyle.Normal, 18);
            Reset();
        }

        private static void Bar(RectTransform r)
        {
            r.anchorMin = new Vector2(0f, 0.5f);
            r.anchorMax = new Vector2(1f, 0.5f);
            r.sizeDelta = new Vector2(-12f, 6f);
            r.anchoredPosition = Vector2.zero;
        }

        private static RectTransform Step(Transform row, string label, out Image dot, out TMP_Text text)
        {
            var chip = UiKit.Chip(row, label + " …", Color.white, UiKit.Ink, UiIcon.Check, 17, 36f);
            dot = chip.Find("Icon").GetComponent<Image>();
            dot.color = UiKit.Grey;
            text = chip.GetComponentInChildren<TMP_Text>();
            text.font = UiKit.Display;
            return chip;
        }

        private static Button Small(Transform row, string text, Color c, UnityEngine.Events.UnityAction a)
        {
            var b = UiKit.Button(row, text, c, a);
            var le = b.GetComponent<LayoutElement>();
            le.preferredWidth = le.minWidth = 210f;
            le.preferredHeight = le.minHeight = 40f;
            var t = b.GetComponentInChildren<TMP_Text>();
            t.fontSize = 18;
            return b;
        }

        /// <summary>Back to the initial state (a new match).</summary>
        public void Reset()
        {
            _rollupOn = _solanaOn = false;
            _lineT = -1f;
            _assetCount = 0;
            _badgeBits = -1;
            _rollupDot.color = _solanaDot.color = UiKit.Grey;
            _rollupText.text = "Rollup …";
            _solanaText.text = "Solana …";
            _lineFill.anchorMax = new Vector2(0f, 0.5f);
            Clear(_assets);
            Clear(_badges);
            _explorer.gameObject.SetActive(false);
            _open.gameObject.SetActive(false);
            _practice.gameObject.SetActive(false);
            _steps.gameObject.SetActive(true);
        }

        private static void Clear(RectTransform r)
        {
            for (var i = r.childCount - 1; i >= 0; i--)
                Destroy(r.GetChild(i).gameObject);
        }

        /// <summary>The verification steps: <paramref name="rollup"/> = receipt on the rollup, <paramref name="solana"/> = committed to Solana.</summary>
        public void ShowSteps(bool rollup, bool solana, string explorerUrl, string receiptUrl)
        {
            _practice.gameObject.SetActive(false);
            _steps.gameObject.SetActive(true);
            if (rollup && !_rollupOn)
            {
                _rollupOn = true;
                _rollupDot.color = UiKit.Hex(0x3FBF8F);
                _rollupText.text = "Rollup ✓";
                UiMotion.Bump(_rollup);
                GameAudio.Ui(GameAudio.UiCue.Tick);
            }

            if (solana && _rollupOn && _lineT < 0f)
                _lineT = Time.unscaledTime;
            _explorerUrl = explorerUrl;
            _receiptUrl = receiptUrl;
            var showButtons = _solanaOn || !solana;
            Visible(_explorer, !string.IsNullOrEmpty(explorerUrl) && showButtons);
            Visible(_open, !string.IsNullOrEmpty(receiptUrl) && showButtons && Application.platform == RuntimePlatform.WebGLPlayer);
        }

        public bool SolanaShown => _solanaOn;

        /// <summary>Adds a minted asset tile (pops in after Solana ✓).</summary>
        public void AddAsset(string name, string sub, Color rarity, bool crown)
        {
            var c = UiKit.MakeCard(_assets, UiKit.CardSize.S, crown ? UiKit.CardHue.Lemon : UiKit.CardHue.Soda, name, new Vector2(196f, 60f), crown ? UiIcon.Crown : UiIcon.Capsule);
            var le = c.Root.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = le.minWidth = 196f;
            le.minHeight = 60f;
            c.Rim.color = rarity;
            c.RibbonFill.color = rarity;
            var t = UiKit.Label(c.Body, sub, 12, UiKit.Ink, FontStyle.Bold, 16);
            t.textWrappingMode = TextWrappingModes.NoWrap;
            UiMotion.Stagger(c.Root, _assetCount++);
            GameAudio.Ui(GameAudio.UiCue.Pop);
        }

        public int AssetCount => _assetCount;

        /// <summary>The achievement badges (bits: 1 first crown, 2 3-round survivor, 4 perfect predictor, 8 chaos agent).</summary>
        public void SetBadges(int bits)
        {
            if (bits == _badgeBits)
                return;
            _badgeBits = bits;
            Clear(_badges);
            string[] names = { "FIRST CROWN", "3-ROUND SURVIVOR", "PERFECT PREDICTOR", "CHAOS AGENT" };
            UiIcon[] icons = { UiIcon.Crown, UiIcon.Trophy, UiIcon.Eye, UiIcon.Bolt };
            for (var b = 0; b < names.Length; b++)
                if ((bits >> b & 1) != 0)
                    UiMotion.Stagger(UiKit.Chip(_badges, names[b], UiKit.CardRim(UiKit.CardHue.Grape), Color.white, icons[b], 12, 26f), b);
        }

        /// <summary>A practice / Classic match: a clear "not recorded" card instead of the steps.</summary>
        public void ShowPractice(string title, string line)
        {
            _steps.gameObject.SetActive(false);
            _explorer.gameObject.SetActive(false);
            _open.gameObject.SetActive(false);
            if (_practiceTitle.text != title)
                _practiceTitle.text = title;
            if (_practiceLine.text != line)
                _practiceLine.text = line;
            if (!_practice.gameObject.activeSelf)
                UiMotion.Show(_practice);
        }

        private static void Visible(Button b, bool on)
        {
            if (b.gameObject.activeSelf != on)
            {
                if (on)
                    UiMotion.Show((RectTransform)b.transform);
                else
                    b.gameObject.SetActive(false);
            }
        }

        private void Update()
        {
            if (_lineT < 0f || _solanaOn)
                return;
            var u = GameSettings.ReducedMotion ? 1f : Mathf.Clamp01((Time.unscaledTime - _lineT) / 0.45f);
            _lineFill.anchorMax = new Vector2(UiMotion.EaseOut(u), 0.5f);
            if (u < 1f)
                return;
            _solanaOn = true;
            _solanaDot.color = UiKit.Hex(0x3FBF8F);
            _solanaText.text = "Solana ✓";
            UiMotion.Bump(_solana);
            GameAudio.Ui(GameAudio.UiCue.Coin);
        }
    }
}
