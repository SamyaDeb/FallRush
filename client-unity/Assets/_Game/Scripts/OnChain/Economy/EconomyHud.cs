using System.Text;
using FallRush.Client;
using FallRush.Net;
using FallRush.Shared;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace FallRush.OnChain
{
    /// <summary>
    /// P4 economy UI, P7 card redesign (UiKit cards + UiMotion; Classic screens untouched): chips in the HUD corner; the
    /// bet panel (B) as a card with market tabs, rows with animated odds bars, the sealed chip on the Champion market, a
    /// stake stepper with a counting payout preview, confirm bump / refused shake; the Chaos Drop cards (price + cooldown
    /// ring) for eliminated players and spectators during the on-chain Final; the drop banner that slides in; the receipt
    /// card (Rollup ✓ → Solana ✓ → minted asset tiles → badges → explorer / receipt buttons, Claim with Phantom); F3 debug.
    /// </summary>
    public sealed class EconomyHud : MonoBehaviour
    {
        public static EconomyHud Instance { get; private set; }

        private RectTransform _canvas, _bet, _dropBar, _receipt, _banner;
        private TMP_Text _chips, _debug, _betHintText;
        private RectTransform _chipsPill, _debugPanel, _betHint;
        private readonly StringBuilder _sb = new StringBuilder(2048);
        private bool _betOpen;
        private int _betMarket;
        private int _seenDrops;
        private double _bannerUntil;
        private bool _showDebug;
        private string _receiptFor;

        public static readonly string[] Rarity = { "Common", "Rare", "Epic", "Legendary" };
        public static readonly Color[] RarityColour = { new Color(0.62f, 0.66f, 0.76f), UiKit.Sky, UiKit.Grape, UiKit.SunYellow };
        private static readonly string[] Labels = { "", "◆ program-settled", "● server-recorded", "● Classic fallback (refund)", "aborted (refund)", "no winner (refund)" };
        private static readonly string[] MarketNames = { "Champion", "Final winner", "Qualify R1", "Qualify R2", "Qualify R3" };
        private static readonly string[] TabNames = { "Champ", "Final", "Q1", "Q2", "Q3" };
        private static readonly int[] Stakes = { 10, 25, 50, 100, 250 };

        // bet card
        private UiKit.Card _betCard;
        private readonly Button[] _tabs = new Button[5];
        private TMP_Text _betTitle, _betState, _pool, _sealedLine, _payout, _stakeText, _betNote, _betFoot;
        private RectTransform _sealedChip, _confirmRt;
        private Button _confirm;
        private readonly BetRow[] _rows = new BetRow[12];
        private int _pick = -1, _stakeIx = 2;
        private CountUp _poolCount, _payoutCount;
        private string _lastBetSig, _lastBetErr;

        private struct BetRow { public RectTransform Root, Face; public Image Bar, Hi, Rim, Dot; public TMP_Text Name, Odds, Stake; public float Fill; public int Outcome; }

        // drops
        private readonly DropCard[] _drops = new DropCard[5];
        private TMP_Text _dropInfo, _bannerText;
        private Image _bannerIcon;
        private string _lastDropErr;

        private struct DropCard { public RectTransform Root; public Button Button; public Image Ring; public TMP_Text Price; }

        // receipt
        private UiKit.Card _receiptCard;
        private ReceiptCard _steps;
        private TMP_Text _rChamp, _rChips, _rLines, _rWarn;
        private RectTransform _rWarnRow;
        private Button _claim, _backup, _close;
        private CountUp _chipsCount;
        private bool _assetsShown, _closed;
        private string _receiptMatch;

        public static EconomyHud Ensure()
        {
            if (Instance != null)
                return Instance;
            var go = new GameObject("EconomyHud");
            DontDestroyOnLoad(go);
            return go.AddComponent<EconomyHud>();
        }

        private void Awake()
        {
            Instance = this;
            _canvas = UiKit.CreateCanvas("EconomyCanvas", 40);
            DontDestroyOnLoad(_canvas.gameObject); // the on-chain session loads the map scene after this HUD exists
            // P7-UX2 (OC-50): the chips as outlined Sun type left of the timer ring (no pill)
            _chipsPill = Bare(_canvas, new Vector2(1f, 1f), new Vector2(-112f, -16f), new Vector2(260f, 34f), out _chips, 24, UiKit.SunYellow);
            // P7-UX: alive players see a small "[B] Bets" keycap under the chips (the drawer never opens by itself)
            _betHint = Bare(_canvas, new Vector2(1f, 1f), new Vector2(-112f, -54f), new Vector2(110f, 26f), out _betHintText, 17, Color.white);
            _betHintText.text = "BETS";
            _betHintText.alignment = TextAlignmentOptions.Left;
            ((RectTransform)_betHintText.transform).offsetMin = new Vector2(52f, 0f);
            var cap = UiKit.Keycap(_betHint, "B", 12, 22f);
            cap.GetComponent<LayoutElement>().ignoreLayout = true;
            cap.anchorMin = cap.anchorMax = cap.pivot = new Vector2(0f, 0.5f);
            cap.anchoredPosition = new Vector2(24f, 1f);
            _betHint.gameObject.SetActive(false);
            MatchHud.BetClicked = ToggleBet;
            BuildBet();
            BuildDropBar();
            BuildBanner();
            BuildReceipt();
            MatchHud.DealOddsProvider = DealOdds;
            MatchHud.DealProvider = DealLink;
            MatchHud.ChampionExtrasProvider = ChampionExtras;
            _debugPanel = Pill(_canvas, new Vector2(0f, 0.5f), new Vector2(14f, -40f), new Vector2(560f, 300f), new Color(0.08f, 0.04f, 0.16f, 0.88f), out _debug, 13, Color.white);
            _debug.alignment = TextAlignmentOptions.TopLeft;
            _debugPanel.gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            if (_canvas != null)
                Destroy(_canvas.gameObject);
        }

        /// <summary>P7-UX2: outlined game type, right-aligned, no background (HUD chips / hints).</summary>
        private static RectTransform Bare(Transform canvas, Vector2 anchor, Vector2 pos, Vector2 size, out TMP_Text text, int fontSize, Color face)
        {
            var go = new GameObject("Hud", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(canvas, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            text = UiKit.HudText(rt, fontSize, face);
            text.alignment = TextAlignmentOptions.Right;
            text.richText = true;
            UiKit.Stretch((RectTransform)text.transform);
            return rt;
        }

        private static RectTransform Pill(Transform canvas, Vector2 anchor, Vector2 pos, Vector2 size, Color fill, out TMP_Text text, int fontSize, Color textColor)
        {
            var go = new GameObject("EconPill", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(canvas, false);
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            UiKit.Pill(rt, fill);
            text = UiKit.Label(rt, string.Empty, fontSize, textColor, FontStyle.Bold);
            text.alignment = TextAlignmentOptions.Center;
            text.richText = true;
            var trt = (RectTransform)text.transform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            UiKit.Stretch(trt, 8f);
            text.GetComponent<LayoutElement>().ignoreLayout = true;
            return rt;
        }

        private static Button Small(Transform row, string text, Color c, float w, UnityEngine.Events.UnityAction a, int size = 16, float h = 34f)
        {
            var b = UiKit.Button(row, text, c, a);
            foreach (var t in b.GetComponentsInChildren<TMP_Text>(true))
            {
                t.fontSize = size;
                t.enableAutoSizing = false;
                t.textWrappingMode = TextWrappingModes.NoWrap;
            }

            var le = b.GetComponent<LayoutElement>();
            le.preferredWidth = le.minWidth = w;
            le.preferredHeight = le.minHeight = h;
            return b;
        }

        // ------------------------------------------------------------------ build

        /// <summary>Bottom-sheet height (reference units): the spectate bar rides on top of it when it is open.</summary>
        public const float SheetHeight = 284f;

        /// <summary>
        /// P7-UX (OC-44): the bet drawer. A full-width sheet that slides up from the bottom (it was a 470 × 660 card forced open
        /// on the right for every eliminated player): market tabs + state + pool, the players as a 6 × 2 grid of odds chips,
        /// the stake stepper, the payout and PLACE BET. Opened only by B or the spectate bar's BET; closed by B, ✕ or Esc.
        /// </summary>
        private void BuildBet()
        {
            _betCard = UiKit.MakeCard(_canvas, UiKit.CardSize.S, UiKit.CardHue.Soda, "RUSH MARKETS", new Vector2(1040f, SheetHeight), UiIcon.Chip);
            _bet = _betCard.Root;
            _bet.anchorMin = _bet.anchorMax = _bet.pivot = new Vector2(0.5f, 0f);
            _bet.anchoredPosition = new Vector2(0f, 8f);
            var x = Small(_betCard.Ribbon, "✕", UiKit.CardPastel(UiKit.CardHue.Soda), 30f, CloseBet, 16, 24f);
            x.GetComponent<LayoutElement>().ignoreLayout = true;
            var xrt = (RectTransform)x.transform;
            xrt.anchorMin = xrt.anchorMax = xrt.pivot = new Vector2(1f, 0.5f);
            xrt.anchoredPosition = new Vector2(-8f, 0f);
            xrt.sizeDelta = new Vector2(30f, 24f);
            var esc = UiKit.Keycap(_betCard.Ribbon, "B", 11, 18f);
            esc.GetComponent<LayoutElement>().ignoreLayout = true;
            esc.anchorMin = esc.anchorMax = esc.pivot = new Vector2(1f, 0.5f);
            esc.anchoredPosition = new Vector2(-46f, 1f);
            var body = _betCard.Body;
            var v = body.GetComponent<VerticalLayoutGroup>();
            v.spacing = 6;
            v.padding = new RectOffset(14, 14, 6, 6);

            // P7-UX2 (OC-50): row 1 the market tabs; row 2 the market name + state on the left, pool + sealed chip on the
            // right (they ran into each other on one row)
            var head = UiKit.Row(body, 32f, 8f);
            for (var k = 0; k < 5; k++)
            {
                var kk = k;
                _tabs[k] = Small(head, TabNames[k], UiKit.SkyLight, 84f, () => { _betMarket = kk; _pick = -1; GameAudio.Ui(GameAudio.UiCue.Tick); }, 15, 30f);
            }

            var head2 = UiKit.Row(body, 28f, 10f);
            _betTitle = UiKit.Cell(head2, string.Empty, 240f, 21, UiKit.Ink, TextAlignmentOptions.Left, true);
            _betTitle.font = UiKit.Display;
            _betState = UiKit.Cell(head2, string.Empty, 110f, 15, UiKit.Grape, TextAlignmentOptions.Left, true);
            _pool = UiKit.Cell(head2, string.Empty, 470f, 15, UiKit.Ink, TextAlignmentOptions.Right, true);
            _sealedChip = UiKit.Chip(head2, "sealed", UiKit.CardRim(UiKit.CardHue.Grape), Color.white, UiIcon.Lock, 13, 26f);

            // row 2: the players, 6 per row
            var gridGo = new GameObject("Players", typeof(RectTransform), typeof(GridLayoutGroup), typeof(LayoutElement));
            var grid = (RectTransform)gridGo.transform;
            grid.SetParent(body, false);
            var gl = gridGo.GetComponent<GridLayoutGroup>();
            gl.cellSize = new Vector2(162f, 40f); // dot 14 + name 72 + odds 42 + padding/spacing 34 = 162: nothing overflows
            gl.spacing = new Vector2(6f, 6f);
            gl.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            gl.constraintCount = 6;
            gl.childAlignment = TextAnchor.UpperCenter;
            var gle = gridGo.GetComponent<LayoutElement>();
            gle.minHeight = gle.preferredHeight = 86f;
            for (var i = 0; i < _rows.Length; i++)
            {
                var slot = new GameObject("PlayerChip", typeof(RectTransform)).GetComponent<RectTransform>();
                slot.SetParent(grid, false);
                var face = new GameObject("Face", typeof(RectTransform), typeof(HorizontalLayoutGroup)).GetComponent<RectTransform>();
                face.SetParent(slot, false);
                UiKit.Stretch(face);
                var hl = face.GetComponent<HorizontalLayoutGroup>();
                hl.padding = new RectOffset(8, 8, 2, 8);
                hl.spacing = 4;
                hl.childAlignment = TextAnchor.MiddleLeft;
                hl.childControlWidth = hl.childControlHeight = true;
                hl.childForceExpandWidth = hl.childForceExpandHeight = false;
                var rim = UiKit.Shape(face, "Rim", UiKit.CardRim(UiKit.CardHue.Soda));
                UiKit.Stretch(rim.rectTransform);
                var hi = UiKit.Shape(face, "Hi", Color.white);
                UiKit.Stretch(hi.rectTransform, 2f);
                hi.raycastTarget = true;
                rim.transform.SetAsFirstSibling();
                hi.transform.SetSiblingIndex(1);
                var ii = i;
                var btn = face.gameObject.AddComponent<Button>();
                btn.targetGraphic = hi;
                btn.transition = Selectable.Transition.None;
                btn.onClick.AddListener(() => { _pick = ii; GameAudio.Ui(GameAudio.UiCue.Tick); UiMotion.Bump(face); });
                var dot = UiKit.Dot(face, UiKit.Grape, 12f);
                var name = UiKit.Cell(face, string.Empty, 72f, 15, UiKit.Ink, TextAlignmentOptions.Left, true);
                var odds = UiKit.Cell(face, string.Empty, 42f, 15, UiKit.Grape, TextAlignmentOptions.Right, true);
                // the odds bar runs along the chip's foot: its width eases to the outcome's share of the pool
                var track = UiKit.Shape(face, "Track", new Color(UiKit.Ink.r, UiKit.Ink.g, UiKit.Ink.b, 0.1f));
                track.rectTransform.anchorMin = new Vector2(0f, 0f);
                track.rectTransform.anchorMax = new Vector2(1f, 0f);
                track.rectTransform.offsetMin = new Vector2(10f, 4f);
                track.rectTransform.offsetMax = new Vector2(-10f, 8f);
                track.pixelsPerUnitMultiplier = 6f;
                var fill = UiKit.Shape(track.transform, "Fill", UiKit.CardRim(UiKit.CardHue.Soda));
                fill.rectTransform.anchorMin = Vector2.zero;
                fill.rectTransform.anchorMax = new Vector2(0f, 1f);
                fill.rectTransform.offsetMin = fill.rectTransform.offsetMax = Vector2.zero;
                fill.pixelsPerUnitMultiplier = 6f;
                var stake = UiKit.Label(face, string.Empty, 10, UiKit.Magenta, FontStyle.Bold, 12);
                stake.GetComponent<LayoutElement>().ignoreLayout = true;
                var srt = (RectTransform)stake.transform;
                srt.anchorMin = srt.anchorMax = srt.pivot = new Vector2(1f, 1f);
                srt.anchoredPosition = new Vector2(-6f, 2f);
                srt.sizeDelta = new Vector2(70f, 12f);
                stake.alignment = TextAlignmentOptions.TopRight;
                _rows[i] = new BetRow { Root = slot, Face = face, Bar = fill, Hi = hi, Rim = rim, Dot = dot, Name = name, Odds = odds, Stake = stake, Outcome = -1 };
                _rows[i].Root.gameObject.SetActive(false);
            }

            // row 3: sealed / qualify note (one line)
            _sealedLine = UiKit.Label(body, string.Empty, 14, UiKit.Grape, FontStyle.Bold, 18);
            _sealedLine.alignment = TextAlignmentOptions.Left;
            _sealedLine.textWrappingMode = TextWrappingModes.NoWrap;
            _sealedLine.overflowMode = TextOverflowModes.Ellipsis;

            // row 4: stake stepper | payout | PLACE BET | note
            var step = UiKit.Row(body, 44f, 8f);
            Small(step, "–", UiKit.Magenta, 40f, () => StepStake(-1), 22, 38f);
            _stakeText = UiKit.Cell(step, string.Empty, 110f, 20, UiKit.Ink, TextAlignmentOptions.Center, true);
            _stakeText.font = UiKit.Display;
            Small(step, "+", UiKit.Magenta, 40f, () => StepStake(1), 22, 38f);
            _payout = UiKit.Cell(step, string.Empty, 270f, 15, UiKit.Grape, TextAlignmentOptions.Left, true);
            _confirm = Small(step, "Pick a player", UiKit.SunYellow, 300f, Confirm, 18, 42f);
            _confirmRt = (RectTransform)_confirm.transform;
            _betNote = UiKit.Cell(step, string.Empty, 190f, 13, UiKit.Ink, TextAlignmentOptions.Left, true);
            _betNote.textWrappingMode = TextWrappingModes.Normal;
            // the devnet note rides in the ribbon (it sat under the confirm button)
            _betFoot = UiKit.Label(_betCard.Ribbon, "DEVNET TEST CHIPS · NO REAL MONEY", 11, Color.white, FontStyle.Bold, 14);
            _betFoot.GetComponent<LayoutElement>().ignoreLayout = true;
            var frt = (RectTransform)_betFoot.transform;
            frt.anchorMin = frt.anchorMax = frt.pivot = new Vector2(1f, 0.5f);
            frt.anchoredPosition = new Vector2(-80f, 0f);
            frt.sizeDelta = new Vector2(320f, 16f);
            _betFoot.alignment = TextAlignmentOptions.Right;
            _bet.gameObject.SetActive(false);
        }

        private void CloseBet()
        {
            _betOpen = false;
            GameAudio.Ui(GameAudio.UiCue.Tick);
        }

        private void ToggleBet()
        {
            _betOpen = !_betOpen;
            GameAudio.Ui(GameAudio.UiCue.Tick);
        }

        private void StepStake(int d)
        {
            _stakeIx = Mathf.Clamp(_stakeIx + d, 0, Stakes.Length - 1);
            UiMotion.Bump((RectTransform)_stakeText.transform);
            GameAudio.Ui(GameAudio.UiCue.Tick);
        }

        private static UiIcon DropIcon(int k) => k switch
        {
            ChaosDropFx.LidFrenzy => UiIcon.Final,
            ChaosDropFx.BumperRain => UiIcon.Capsule,
            ChaosDropFx.WindGust => UiIcon.Swirl,
            _ => UiIcon.Ring,
        };

        private static UiKit.CardHue DropHue(int k) => k switch
        {
            ChaosDropFx.LidFrenzy => UiKit.CardHue.Lemon,
            ChaosDropFx.BumperRain => UiKit.CardHue.Bubblegum,
            ChaosDropFx.WindGust => UiKit.CardHue.Soda,
            _ => UiKit.CardHue.Tangerine,
        };

        /// <summary>
        /// P7-UX (OC-44): the Chaos Drops dock, bottom-right above the chain chip (it was a 900 × 150 bar over the middle of
        /// the Final): four round buttons with the drop's icon, a cooldown ring and the price.
        /// </summary>
        private void BuildDropBar()
        {
            var bar = UiKit.MakeCard(_canvas, UiKit.CardSize.S, UiKit.CardHue.Grape, "CHAOS DROPS", new Vector2(372f, 148f), UiIcon.Bolt);
            _dropBar = bar.Root;
            _dropBar.anchorMin = _dropBar.anchorMax = _dropBar.pivot = new Vector2(1f, 0f);
            _dropBar.anchoredPosition = new Vector2(-16f, 92f);
            var row = UiKit.Row(bar.Body, 96f, 10f, TextAnchor.MiddleCenter);
            for (var k = 1; k <= 4; k++)
            {
                var kk = k;
                var col = new GameObject("Drop", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement)).GetComponent<RectTransform>();
                col.SetParent(row, false);
                var cle = col.GetComponent<LayoutElement>();
                cle.minWidth = cle.preferredWidth = 78f;
                var vl = col.GetComponent<VerticalLayoutGroup>();
                vl.childAlignment = TextAnchor.UpperCenter;
                vl.childControlWidth = vl.childControlHeight = true;
                vl.childForceExpandWidth = vl.childForceExpandHeight = false;
                vl.spacing = 2;
                var b = UiKit.RoundButton(col, DropIcon(k), null, UiKit.CardPastel(DropHue(k)), 60f, () => OnDrop(kk), UiKit.CardRim(DropHue(k)));
                var face = b.transform.Find("Face");
                var ring = UiKit.Shape(face, "Cooldown", UiKit.CardRim(DropHue(k)), UiIcons.Get(UiIcon.Ring));
                ring.type = Image.Type.Filled;
                ring.fillMethod = Image.FillMethod.Radial360;
                ring.fillOrigin = (int)Image.Origin360.Top;
                ring.fillClockwise = false;
                UiKit.Stretch(ring.rectTransform, -4f);
                var name = UiKit.Label(col, Title(ChaosDropFx.Names[k]), 11, UiKit.Ink, FontStyle.Bold, 13);
                name.textWrappingMode = TextWrappingModes.NoWrap;
                var price = UiKit.Label(col, string.Empty, 14, UiKit.Grape, FontStyle.Bold, 15);
                price.textWrappingMode = TextWrappingModes.NoWrap;
                _drops[k] = new DropCard { Root = (RectTransform)b.transform, Button = b, Ring = ring, Price = price };
            }

            _dropInfo = UiKit.Label(bar.Ribbon, string.Empty, 12, Color.white, FontStyle.Bold, 14);
            _dropInfo.GetComponent<LayoutElement>().ignoreLayout = true;
            var irt = (RectTransform)_dropInfo.transform;
            irt.anchorMin = irt.anchorMax = irt.pivot = new Vector2(1f, 0.5f);
            irt.anchoredPosition = new Vector2(-10f, 0f);
            irt.sizeDelta = new Vector2(180f, 20f);
            _dropInfo.alignment = TextAlignmentOptions.Right;
            _dropBar.gameObject.SetActive(false);
        }

        private void BuildBanner()
        {
            var c = UiKit.MakeCard(_canvas, UiKit.CardSize.S, UiKit.CardHue.Grape, "CHAOS DROP", new Vector2(560f, 78f), UiIcon.Bolt);
            _banner = c.Root;
            _banner.anchorMin = _banner.anchorMax = _banner.pivot = new Vector2(0.5f, 1f);
            _banner.anchoredPosition = new Vector2(0f, -146f); // under the top-centre counter + callout
            var row = UiKit.Row(c.Body, 40f, 8f, TextAnchor.MiddleCenter);
            _bannerIcon = UiKit.IconImage(row, UiIcon.Swirl, UiKit.CardRim(UiKit.CardHue.Grape), 30f);
            _bannerText = UiKit.Cell(row, string.Empty, 470f, 20, UiKit.Ink, TextAlignmentOptions.Left, true);
            _bannerText.font = UiKit.Display;
            _banner.gameObject.SetActive(false);
        }

        private void BuildReceipt()
        {
            // P7-UX2: the on-chain receipt (Rollup ✓ → Solana ✓ → assets), top-left, top-aligned with the standings card
            _receiptCard = UiKit.MakeCard(_canvas, UiKit.CardSize.M, UiKit.CardHue.Tangerine, "ON-CHAIN RECEIPT", new Vector2(480f, 540f), UiIcon.Check);
            _receipt = _receiptCard.Root;
            _receipt.anchorMin = _receipt.anchorMax = _receipt.pivot = new Vector2(0f, 1f);
            _receipt.anchoredPosition = new Vector2(16f, -72f);
            var body = _receiptCard.Body;
            body.GetComponent<VerticalLayoutGroup>().spacing = 3;
            _close = Small(_receiptCard.Ribbon, "✕", UiKit.CardPastel(UiKit.CardHue.Tangerine), 32f, () => { _closed = true; UiMotion.Hide(_receipt); }, 18, 28f);
            var crt = (RectTransform)_close.transform;
            _close.GetComponent<LayoutElement>().ignoreLayout = true;
            crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(1f, 0.5f);
            crt.anchoredPosition = new Vector2(-8f, 0f);
            crt.sizeDelta = new Vector2(32f, 28f);
            _rChamp = UiKit.Label(body, string.Empty, 22, UiKit.Ink, FontStyle.Bold, 30);
            _rChamp.font = UiKit.Display;
            _rChips = UiKit.Label(body, string.Empty, 16, UiKit.Grape, FontStyle.Bold, 22);
            _steps = ReceiptCard.Create(body, 440f, 150f);
            _rLines = UiKit.Label(body, string.Empty, 13, UiKit.Ink, FontStyle.Normal, 20); // P7-UX2: grows with its lines (was a fixed 96 px)
            _rLines.alignment = TextAlignmentOptions.TopLeft;
            _rWarnRow = UiKit.Row(body, 44f, 6f);
            UiKit.IconImage(_rWarnRow, UiIcon.Warning, UiKit.Magenta, 26f);
            _rWarn = UiKit.Cell(_rWarnRow, "Guest wallet: your Crown / capsule lives only in this browser. Back up your key or claim it to Phantom before clearing site data.", 390f, 12, UiKit.Magenta, TextAlignmentOptions.Left, true);
            _rWarn.textWrappingMode = TextWrappingModes.Normal;
            var btns = UiKit.Row(body, 40f, 8f, TextAnchor.MiddleCenter);
            _claim = Small(btns, "Claim with Phantom", UiKit.Grape, 210f, Claim, 15, 38f);
            _claim.GetComponentInChildren<TMP_Text>().color = Color.white;
            // P5 0.4 burner safety: a guest wallet that holds a Crown / capsule lives only in this browser's storage;
            // "Back up your key" opens /me (same origin, same burner) with the export + copy warning
            _backup = Small(btns, "Back up your key", UiKit.Magenta, 190f, BackupKey, 15, 38f);
            _receipt.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------------ helpers

        private static string SlotName(int s)
        {
            var m = ClientSim.Instance != null ? ClientSim.Instance.Match : null;
            if (m != null && m.Slots != null)
                foreach (var x in m.Slots)
                    if (x.Slot == s && !string.IsNullOrEmpty(x.Name))
                        return x.Name;
            return $"Rushling {s + 1}";
        }

        /// <summary>The player's shell colour (cosmetic preset) for the odds chips.</summary>
        private static Color SlotColour(int s)
        {
            var m = ClientSim.Instance != null ? ClientSim.Instance.Match : null;
            if (m != null && m.Slots != null)
                foreach (var x in m.Slots)
                    if (x.Slot == s)
                        return UiKit.Hex(CosmeticPresets.Get(x.Preset).Shell);
            return UiKit.Grape;
        }

        private static string Short(string pk) => string.IsNullOrEmpty(pk) || pk.Length < 8 ? pk : pk.Substring(0, 4) + "…" + pk.Substring(pk.Length - 4);

        private static readonly System.Collections.Generic.List<int> s_Outs = new System.Collections.Generic.List<int>(12);

        /// <summary>The outcomes of the current market, in panel order (slots).</summary>
        private static System.Collections.Generic.List<int> Outcomes(EState s)
        {
            s_Outs.Clear();
            if (s?.match == null)
                return s_Outs;
            for (var i = 0; i < s.match.players && i < s.match.slots.Length; i++)
                if (s.match.slots[i] == 1 || s.match.slots[i] == 2)
                    s_Outs.Add(i);
            return s_Outs;
        }

        private static void BackupKey() => Application.OpenURL("/me?backup=1");

        private static string Explorer(string address) => $"https://explorer.solana.com/address/{address}?cluster=devnet";

        /// <summary>P5 0.6: the Deal screen's market line per capsule (Qualify R1–R3 for rounds 1–3, Champion for the Final).</summary>
        private static string DealOdds(int r, int rounds)
        {
            var st = ChainEconomy.Instance != null ? ChainEconomy.Instance.State : null;
            if (st?.markets == null)
                return string.Empty;
            var k = r == rounds - 1 ? 0 : 2 + r;
            if (k > 4 || k >= st.markets.Length || st.markets[k] == null || st.markets[k].status < 1)
                return string.Empty;
            var mk = st.markets[k];
            var pool = ChainEconomy.Parse(mk.pool);
            long top = 0;
            foreach (var x in mk.stake)
                top = System.Math.Max(top, ChainEconomy.Parse(x));
            var odds = top > 0 ? $" · fav x{pool / (double)top:0.0#}" : string.Empty;
            return $"{(k == 0 ? "Champion" : "Qualify")} market\npool {ChainEconomy.Rush(pool)} RUSH{odds}";
        }

        /// <summary>P7: the match's on-chain VRF deal (the arena keeper's request_deal) for the Deal screen.</summary>
        private static MatchHud.DealLink DealLink()
        {
            var e = ChainEconomy.Instance;
            if (e == null || string.IsNullOrEmpty(e.DealSig))
                return default;
            return new MatchHud.DealLink { Signature = e.DealSig, Url = $"https://explorer.solana.com/tx/{e.DealSig}?cluster=custom&customUrl=https%3A%2F%2Fdevnet-as.magicblock.app" };
        }

        /// <summary>P7: the champion card's season line + badges (P6 season data, UI only).</summary>
        private static MatchHud.ChampionExtras ChampionExtras(int champSlot)
        {
            var e = ChainEconomy.Instance;
            var sp = e?.Season?.player;
            var r = e?.Receipt;
            if (sp == null || string.IsNullOrEmpty(sp.wallet) || r?.receipt == null || e.SeasonFor != r.match)
                return default;
            var me = System.Array.IndexOf(r.receipt.wallets, r.me);
            var x = new MatchHud.ChampionExtras { Achievements = me >= 0 ? sp.achievements : 0 };
            x.SeasonLine = me >= 0 && me == r.champion ? $"+1 crown this season ({sp.crowns} total)" : $"{sp.finals} Final(s) this season";
            return x;
        }

        private void Claim()
        {
            var e = ChainEconomy.Instance;
            var r = e?.Receipt;
            if (r?.receipt == null)
                return;
            var phantom = ChainWallet.PhantomPubkey;
            if (string.IsNullOrEmpty(phantom))
            {
                ChainWallet.Connect();
                return;
            }

            for (var s = 0; s < 12 && s < r.receipt.wallets.Length; s++)
                if (r.receipt.wallets[s] == r.me)
                {
                    if (r.champion == s && (r.receipt.flags & 4) != 0)
                        e.Claim(r.crownAsset, phantom);
                    if (s < r.capsuleAssets.Length && !string.IsNullOrEmpty(r.capsuleAssets[s]) && (r.receipt.mintedMask >> s & 1) == 1)
                        e.Claim(r.capsuleAssets[s], phantom);
                }
        }

        private void OnDrop(int kind)
        {
            var e = ChainEconomy.Instance;
            if (e == null)
                return;
            var rt = _drops[kind].Root;
            e.Drop(kind, ok =>
            {
                if (ok)
                    UiMotion.Bump(rt);
                else
                {
                    UiMotion.Shake(_dropBar);
                    _lastDropErr = e.LastError;
                }
            });
        }

        private void Confirm()
        {
            var e = ChainEconomy.Instance;
            var outs = Outcomes(e?.State);
            if (e == null || _pick < 0 || _pick >= outs.Count || !e.MarketOpen(_betMarket))
            {
                UiMotion.Shake(_bet);
                GameAudio.Ui(GameAudio.UiCue.Click);
                return;
            }

            var amount = Stakes[_stakeIx];
            _lastBetErr = null;
            _betNote.text = "<color=#7B2FF7>placing the bet…</color>";
            e.Bet(_betMarket, outs[_pick], amount, ok =>
            {
                if (ok)
                {
                    UiMotion.Bump(_confirmRt);
                    GameAudio.Ui(GameAudio.UiCue.Coin);
                    _lastBetSig = e.LastSig;
                }
                else
                {
                    UiMotion.Shake(_bet);
                    _lastBetErr = e.LastError;
                }
            });
        }

        // ------------------------------------------------------------------ frame

        private void Update()
        {
            if (_canvas == null)
                return;
            var e = ChainEconomy.Instance;
            var session = OnChainSession.Instance;
            var on = e != null && session != null && session.isActiveAndEnabled && e.Available && session.Cfg.target != "emu";
            MatchHud.RecordedMatch = on;
            _chipsPill.gameObject.SetActive(on);
            if (!on)
            {
                _betOpen = false;
                CursorPolicy.SetOpen("bet", false);
                CursorPolicy.SetOpen("drops", false);
                CursorPolicy.ChainOut = false;
                MatchHud.BetAvailable = false;
                MatchHud.BottomInset = 0f;
                _bet.gameObject.SetActive(false);
                _dropBar.gameObject.SetActive(false);
                _receipt.gameObject.SetActive(false);
                _banner.gameObject.SetActive(false);
                _betHint.gameObject.SetActive(false);
                _debugPanel.gameObject.SetActive(false);
                MatchHud.ExternalReceipt = false;
                return;
            }

            var kb = Keyboard.current;
            if (kb != null && kb.bKey.wasPressedThisFrame)
                ToggleBet();
            if (kb != null && kb.escapeKey.wasPressedThisFrame && _betOpen)
                _betOpen = false; // Esc closes the drawer first (CursorPolicy keeps the settings shut this frame)
            if (kb != null && kb.f3Key.wasPressedThisFrame)
                _showDebug = !_showDebug;
            var s = e.State;
            _chips.text = e.Balance >= 0 ? $"⬡ {ChainEconomy.Rush(e.Balance)} RUSH" : "⬡ chips…";

            // P7 fix: who is spectating. Rounds 1–3 run on the Classic server (the pre-joined on-chain session has no slot
            // yet), so there the bet panel opens by itself only for players who are out (or not in the match); during the
            // on-chain Final, for eliminated players and watchers. An alive player opens it with B.
            var inFinal = s?.match != null && (s.match.flags & 0x40) != 0;
            var cs = ClientSim.Instance;
            var classicOut = cs != null && cs.Match != null && (cs.MatchSlot == 255 || (cs.TryGetSlotInfo(cs.MatchSlot, out var si) && !si.Has(MatchSlotInfo.Alive)));
            var spectating = inFinal ? session.Spectate || session.OwnSlot >= 12 || session.OwnOut : classicOut;
            // the Final is live only while the server's match is still in its last round (the arena World keeps its last
            // RUSH state after the Final until the next one starts: P7 fix, the drop bar stayed up into the next lobby)
            var classicFinal = cs?.Match != null && cs.Match.Phase >= MatchPhase.RoundIntro && cs.Match.Phase <= MatchPhase.RoundActive;
            var finalRush = s?.world != null && inFinal && s.world.round == 4 && s.world.roundPhase == 3 && s.match.status < 5 && classicFinal;
            var anyOpen = false;
            for (var k = 0; k < 5; k++)
                anyOpen |= e.MarketOpen(k);
            // P7-UX (OC-44): the drawer opens only on B / BET (it used to force itself open for every eliminated player and
            // could not be closed); while it is open the cursor is free (CursorPolicy) and the spectate bar rides above it
            var betVisible = _betOpen;
            if (betVisible && !_bet.gameObject.activeSelf)
                UiMotion.SlideIn(_bet, new Vector2(0f, -SheetHeight - 20f));
            else if (!betVisible)
                UiMotion.SetVisible(_bet, false);
            if (betVisible)
                DrawBet(e, s);
            CursorPolicy.SetOpen("bet", betVisible);
            CursorPolicy.ChainOut = inFinal && spectating;
            MatchHud.BetAvailable = anyOpen || betVisible;
            MatchHud.BottomInset = betVisible ? SheetHeight + 8f : 0f;
            UiMotion.SetVisible(_betHint, anyOpen && !betVisible && !spectating);
            var dropsVisible = spectating && finalRush && !betVisible;
            if (dropsVisible && !_dropBar.gameObject.activeSelf)
                UiMotion.SlideIn(_dropBar, new Vector2(200f, 0f));
            else if (!dropsVisible)
                UiMotion.SetVisible(_dropBar, false);
            CursorPolicy.SetOpen("drops", dropsVisible);
            if (_dropBar.gameObject.activeSelf)
                DrawDrops(e, s);
            DrawBanner(s);
            DrawReceipt(e, s);
            _debugPanel.gameObject.SetActive(_showDebug);
            if (_showDebug)
                DrawDebug(e, s);
        }

        private void DrawBet(ChainEconomy e, EState s)
        {
            var now = Time.unscaledTime;
            for (var k = 0; k < 5; k++)
            {
                var img = _tabs[k].GetComponentInChildren<Image>();
                _ = img;
                var sel = k == _betMarket;
                var face = _tabs[k].transform.Find("Face/Fill");
                if (face != null)
                    face.GetComponent<Image>().color = sel ? UiKit.SunYellow : e.MarketOpen(k) ? UiKit.Mint : UiKit.SkyLight;
            }

            var m = s?.markets != null && _betMarket < s.markets.Length ? s.markets[_betMarket] : null;
            var sealedOpen = _betMarket == 0 && e.Sealed != null && (e.Sealed.open || (e.Sealed.escrow != null && e.Sealed.escrow.n > 0));
            _sealedChip.gameObject.SetActive(sealedOpen);
            if (m == null || m.status < 0)
            {
                _betTitle.text = MarketNames[_betMarket];
                _betState.text = string.Empty;
                _pool.text = "loading the market…";
                for (var i = 0; i < _rows.Length; i++)
                    if (_rows[i].Root.gameObject.activeSelf)
                        _rows[i].Root.gameObject.SetActive(false);
                _confirm.interactable = false;
                return;
            }

            var open = e.MarketOpen(_betMarket);
            var pool = ChainEconomy.Parse(m.pool);
            _betTitle.text = MarketNames[_betMarket];
            _betState.text = open ? "<color=#1FA36A>OPEN</color>" : m.status >= 2 ? "SETTLED" : "closed";
            _poolCount.Set(pool / (double)ChainEconomy.One, now);
            var settledBy = m.status >= 2 ? Labels[Mathf.Clamp(m.label, 0, 5)] : _betMarket <= 1 ? "◆ program-settled" : "● server-recorded";
            _pool.text = $"<size=12><color=#7B2FF7>{settledBy}</color></size>  Pool <b>{_poolCount.Value(now):N0} RUSH</b>";
            var outs = Outcomes(s);
            var pickOutcome = -1;
            for (var i = 0; i < _rows.Length; i++)
            {
                ref var row = ref _rows[i];
                var has = i < outs.Count;
                if (!has)
                {
                    if (row.Root.gameObject.activeSelf)
                        row.Root.gameObject.SetActive(false);
                    continue;
                }

                if (!row.Root.gameObject.activeSelf)
                    UiMotion.Stagger(row.Root, i);
                var o = outs[i];
                var st = o < m.stake.Length ? ChainEconomy.Parse(m.stake[o]) : 0;
                var mine = o < m.mine.Length ? ChainEconomy.Parse(m.mine[o]) : 0;
                var win = m.status >= 2 && (m.winners >> o & 1) == 1;
                row.Name.text = (win ? "<color=#1FA36A>★ " : string.Empty) + SlotName(o) + (win ? "</color>" : string.Empty);
                var ox = st > 0 ? pool / (double)st : 0;
                row.Odds.text = st <= 0 ? "new" : ox >= 10 ? $"x{ox:0}" : $"x{ox:0.0}"; // P7-UX2: fits the chip ("x18." was cut)
                row.Stake.text = mine > 0 ? $"you {ChainEconomy.Rush(mine)}" : string.Empty;
                row.Dot.color = SlotColour(o);
                var target = pool > 0 ? Mathf.Clamp01((float)(st / (double)pool)) : 0f;
                row.Fill = GameSettings.ReducedMotion ? target : Mathf.Lerp(row.Fill, target, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 6f));
                row.Bar.rectTransform.anchorMax = new Vector2(Mathf.Max(0.04f, row.Fill), 1f);
                row.Bar.color = win ? UiKit.Mint : UiKit.CardRim(UiKit.CardHue.Soda);
                var picked = i == _pick;
                row.Hi.color = picked ? UiKit.CardPastel(UiKit.CardHue.Lemon) : Color.white;
                row.Rim.color = picked ? UiKit.SunYellow : win ? UiKit.Mint : new Color(UiKit.Ink.r, UiKit.Ink.g, UiKit.Ink.b, 0.18f);
                row.Outcome = o;
                if (i == _pick)
                    pickOutcome = o;
            }

            // sealed line (P6): picks hidden in MagicBlock's private rollup until close
            _sb.Clear();
            if (_betMarket == 0 && e.Sealed?.escrow != null && e.Sealed.escrow.n + (e.Sealed.open ? 1 : 0) > 0)
            {
                var es = e.Sealed.escrow;
                _sb.Append("Sealed picks (MagicBlock private rollup): ").Append(es.n).Append(" · ")
                    .Append(ChainEconomy.Rush(ChainEconomy.Parse(es.pool))).Append(" RUSH · revealed at close");
                if (es.state >= 2)
                    _sb.Append(" · revealed ").Append(es.nRevealed).Append('/').Append(es.n).Append(es.nForfeit > 0 ? $", {es.nForfeit} forfeited" : string.Empty);
                foreach (var mine in e.Sealed.mine)
                    _sb.Append(" · yours: ").Append(ChainEconomy.Rush(ChainEconomy.Parse(mine.amount))).Append(" on ").Append(mine.saved != null && !string.IsNullOrEmpty(mine.saved.amount) ? SlotName(mine.saved.outcome) : "?")
                        .Append(mine.state == 1 ? " ✓" : mine.state == 2 ? " (forfeited)" : string.Empty);
            }
            else if (_betMarket >= 2)
                _sb.Append("Backers of every qualifier share the pool");
            _sealedLine.text = _sb.ToString();

            // stake stepper + payout preview (pari-mutuel: your share of the pool if your pick wins)
            var stake = Stakes[_stakeIx];
            _stakeText.text = $"{stake} RUSH";
            if (pickOutcome >= 0)
            {
                var st = pickOutcome < m.stake.Length ? ChainEconomy.Parse(m.stake[pickOutcome]) / (double)ChainEconomy.One : 0;
                var poolR = pool / (double)ChainEconomy.One;
                var pays = (poolR + stake) * stake / (st + stake);
                _payoutCount.Set(pays, now);
                _payout.text = $"Pays ~<b>{_payoutCount.Value(now):N0}</b> if {SlotName(pickOutcome)} {(_betMarket >= 2 ? "qualifies" : "wins")}";
            }
            else
                _payout.text = "Pick a player above";

            var can = open && pickOutcome >= 0 && e.Balance >= stake * ChainEconomy.One;
            _confirm.interactable = can;
            UiKit.SetButtonText(_confirm, !open ? (m.status >= 2 ? "Settled" : "Closed") : pickOutcome < 0 ? "Pick a player" : $"BET {stake} · {SlotName(pickOutcome).ToUpperInvariant()}{(sealedOpen && e.Sealed.open ? " (sealed)" : string.Empty)}");
            if (!string.IsNullOrEmpty(_lastBetErr))
                _betNote.text = "<color=#FF3EA5>Refused: " + (_lastBetErr.Length > 48 ? _lastBetErr.Substring(0, 48) : _lastBetErr) + "</color>";
            else if (!string.IsNullOrEmpty(_lastBetSig))
                _betNote.text = "<color=#1FA36A>✓ Bet on-chain" + (e.LastSession ? " · no popup" : string.Empty) + "</color>";
            else if (_betNote.text.StartsWith("<color=#7B2FF7>placing") == false)
                _betNote.text = string.Empty;
        }

        private void DrawDrops(ChainEconomy e, EState s)
        {
            var ok = e.DropOpen(out var why);
            var tick = s?.world?.tick ?? 0;
            var last = s?.drops?.lastTick ?? 0;
            var cd = s?.drops != null && s.drops.count > 0 ? Mathf.Clamp01((last + ChainEconomy.DropCooldownTicks - tick) / (float)ChainEconomy.DropCooldownTicks) : 0f;
            for (var k = 1; k <= 4; k++)
            {
                var price = s.prices != null && k < s.prices.Length ? s.prices[k] : 0;
                var d = _drops[k];
                var text = $"⬡{price}";
                if (d.Price.text != text)
                    d.Price.text = text;
                var can = ok && e.KindOnSale(k) && e.Balance >= price * ChainEconomy.One;
                d.Button.interactable = can;
                // the cooldown ring empties as the cooldown runs out; full and coloured when ready
                d.Ring.fillAmount = cd > 0f ? 1f - cd : 1f;
                d.Ring.color = can ? UiKit.CardRim(DropHue(k)) : UiKit.Grey;
            }

            var left = 3 - (s.drops?.count ?? 0);
            _dropInfo.text = !string.IsNullOrEmpty(_lastDropErr) ? "refused" : ok ? $"{left} left" : (why ?? string.Empty).Length > 22 ? why.Substring(0, 22) : why;
        }

        private void DrawBanner(EState s)
        {
            var n = ChaosDropFx.Any ? 0 : -1;
            if (ChaosDropFx.Any)
                foreach (var q in ChaosDropFx.Queue)
                    if (q.Kind != 0)
                        n++;
            var now = Time.realtimeSinceStartupAsDouble;
            if (n > _seenDrops)
            {
                _seenDrops = n;
                var q = ChaosDropFx.Queue[n - 1];
                var by = s?.drops?.buys != null && n - 1 < s.drops.buys.Length ? s.drops.buys[n - 1].by : null;
                var who = by == null ? "A spectator" : by == s.wallet ? "You" : Short(by);
                _bannerText.text = $"{who} dropped {Title(ChaosDropFx.Names[q.Kind])}!  <size=14><color=#7B2FF7>◆ rollup</color></size>";
                _bannerIcon.sprite = UiIcons.Get(DropIcon(q.Kind));
                _bannerUntil = now + 4.0;
                UiMotion.SlideIn(_banner, new Vector2(0f, 160f));
                UiMotion.Bump(_banner);
                GameAudio.Ui(GameAudio.UiCue.Whoosh);
                FollowCamera.Punch(0.12f);
            }
            else if (n < _seenDrops)
                _seenDrops = System.Math.Max(0, n);

            if (now >= _bannerUntil)
                UiMotion.SetVisible(_banner, false);
        }

        private static string Title(string caps) => caps.Length == 0 ? caps : caps[0] + caps.Substring(1).ToLowerInvariant();

        private void DrawReceipt(ChainEconomy e, EState s)
        {
            // P7: the receipt card opens as soon as the on-chain Final is over (then Rollup ✓ → Solana ✓ tick in as they land),
            // not ~40 s later when the Match reaches "completed"
            var cs = ClientSim.Instance;
            var finalOver = s?.match != null && (s.match.flags & 0x40) != 0 && (s.match.status == 5 || (s.world != null && s.world.round == 4 && s.world.roundPhase > 3) ||
                            (cs?.Match != null && cs.Match.Phase >= MatchPhase.Champion && cs.Match.Phase <= MatchPhase.Done));
            var done = finalOver && s.match.status != 6;
            if (!done && e.Receipt == null)
            {
                UiMotion.SetVisible(_receipt, false);
                MatchHud.ExternalReceipt = false;
                return;
            }

            var mpda = s?.match?.pda ?? e.MatchPda;
            if (mpda != _receiptMatch)
            {
                _receiptMatch = mpda;
                _closed = false;
                _assetsShown = false;
                _steps.NewMatch();
            }

            if (mpda != null && _receiptFor != mpda && Time.frameCount % 90 == 0)
                e.FetchReceipt(mpda);
            if (e.Receipt != null && e.Receipt.match == mpda && e.Receipt.onSolana)
                _receiptFor = mpda;
            // P7: the next match's rounds 1–3 have started (deal or a round): the old receipt steps aside
            var nextMatch = cs?.Match != null && cs.Match.Phase >= MatchPhase.RosterLocked && cs.Match.Phase <= MatchPhase.Intermission && cs.Match.Round < cs.Match.RoundCount;
            if (_closed || nextMatch)
            {
                UiMotion.SetVisible(_receipt, false);
                MatchHud.ExternalReceipt = false;
                return;
            }

            UiMotion.SetVisible(_receipt, true);
            MatchHud.ExternalReceipt = true;
            var r = e.Receipt;
            var now = Time.unscaledTime;
            if (r?.receipt == null)
            {
                _claim.gameObject.SetActive(false);
                _backup.gameObject.SetActive(false);
                _rWarnRow.gameObject.SetActive(false);
                _rChamp.text = "Writing the receipt…";
                _rChips.text = string.Empty;
                _rLines.text = "The program writes the receipt on the rollup, then commits it to Solana.";
                _steps.ShowSteps(false, false, null, null);
                UiKit.FitHeight(_receiptCard, 200f, 560f); // P7-UX2: the card fits its content (no empty half)
                return;
            }

            var me = -1;
            for (var i = 0; i < r.receipt.wallets.Length; i++)
                if (r.receipt.wallets[i] == r.me)
                    me = i;
            var champ = r.champion;
            var champName = champ < 12 ? SlotName(champ) : cs?.Match != null && cs.Match.Champion < 12 ? SlotName(cs.Match.Champion) : "…";
            _rChamp.text = $"♛ {champName}{(champ == me && me >= 0 ? " (you)" : string.Empty)}";
            if (e.MatchStartBalance >= 0 && e.Balance >= 0)
            {
                var d = (e.Balance - e.MatchStartBalance) / (double)ChainEconomy.One;
                _chipsCount.Set(d, now);
                var v = _chipsCount.Value(now);
                _rChips.text = $"Chips this match: <b>{(v >= 0 ? "+" : "–")}{System.Math.Abs(v):N0} RUSH</b>";
            }
            else
                _rChips.text = string.Empty;

            _steps.ShowSteps(true, r.onSolana, Explorer(r.match), "/receipt/" + r.match);
            // minted assets pop in after Solana ✓
            if (_steps.SolanaShown && !_assetsShown && me >= 0)
            {
                _assetsShown = true;
                if ((r.receipt.flags & 4) != 0 && champ == me)
                    _steps.AddAsset("CROWN", "Crown Capsule · in your wallet", UiKit.SunYellow, true);
                if (me < r.receipt.rarity.Length && r.receipt.rarity[me] < 4 && (r.receipt.mintedMask >> me & 1) == 1)
                {
                    var rr = r.receipt.rarity[me];
                    _steps.AddAsset(Rarity[rr].ToUpperInvariant(), "Rushling Capsule · in your wallet", RarityColour[rr], false);
                }
            }

            // P6: the season badges (UI only; from the on-chain PlayerSeason via the indexer)
            if (r.onSolana && me >= 0 && e.SeasonFor != r.match && Time.frameCount % 600 == 0)
                e.FetchSeason(r.match);
            var sp = e.Season?.player;
            if (_steps.SolanaShown && me >= 0 && sp != null && !string.IsNullOrEmpty(sp.wallet) && e.SeasonFor == r.match)
                _steps.SetBadges(sp.achievements);

            _sb.Clear();
            for (var k = 0; k < r.receipt.markets.Length && k < MarketNames.Length; k++)
            {
                var mk = r.receipt.markets[k];
                if (mk.status == 0)
                    continue;
                _sb.Append(MarketNames[k]).Append(": pool ").Append(ChainEconomy.Rush(ChainEconomy.Parse(mk.pool))).Append(" · ").Append(Labels[Mathf.Clamp(mk.label, 0, 5)]).Append('\n');
            }

            if (r.receipt.sealedN > 0)
                _sb.Append("Sealed Champion bets: ").Append(r.receipt.sealedN).Append(" · revealed ").Append(r.receipt.sealedRevealed)
                    .Append(r.receipt.sealedForfeits > 0 ? $" · {r.receipt.sealedForfeits} forfeited" : string.Empty).Append(" ◆\n");
            if (sp != null && me >= 0 && e.SeasonFor == r.match)
                _sb.Append("<color=#7B2FF7>").Append(champ == me ? $"+1 crown this season ({sp.crowns} total) · " : string.Empty).Append(sp.finals).Append(" Final(s) this season</color>\n");
            _sb.Append("<color=#8C88A6>Drops ").Append(r.receipt.drops).Append(" · rounds 1–3 ● server-recorded · Final ◆ program-computed · core v").Append(r.receipt.coreVersion).Append("</color>");
            _rLines.text = _sb.ToString();
            var holds = me >= 0 && r.onSolana && (((r.receipt.flags & 4) != 0 && champ == me) || (r.receipt.mintedMask >> me & 1) == 1);
            var guest = holds && string.IsNullOrEmpty(ChainWallet.PhantomPubkey);
            if (_rWarnRow.gameObject.activeSelf != guest)
                _rWarnRow.gameObject.SetActive(guest);
            if (_claim.gameObject.activeSelf != holds)
                _claim.gameObject.SetActive(holds);
            var backup = holds && Application.platform == RuntimePlatform.WebGLPlayer;
            if (_backup.gameObject.activeSelf != backup)
                _backup.gameObject.SetActive(backup);
            UiKit.SetButtonText(_claim, string.IsNullOrEmpty(ChainWallet.PhantomPubkey) ? "Claim with Phantom" : "Claim to " + Short(ChainWallet.PhantomPubkey));
            if (Time.frameCount % 15 == 0)
                UiKit.FitHeight(_receiptCard, 200f, 560f);
        }

        private void DrawDebug(ChainEconomy e, EState s)
        {
            _sb.Clear();
            _sb.Append("<b>ECONOMY (F3)</b>  calls ").Append(e.Calls).Append(" errors ").Append(e.Errors).Append('\n');
            _sb.Append("wallet ").Append(Short(s?.wallet)).Append("  chips ").Append(e.Balance >= 0 ? ChainEconomy.Rush(e.Balance) : "-").Append('\n');
            if (s?.world != null)
                _sb.Append("world tick ").Append(s.world.tick).Append(" round ").Append(s.world.round).Append(" rphase ").Append(s.world.roundPhase).Append(" rush@").Append(s.world.roundStart).Append('\n');
            if (s?.match != null)
                _sb.Append("match ").Append(Short(s.match.pda)).Append(" status ").Append(s.match.status).Append(" round ").Append(s.match.round).Append(" flags 0x").Append(s.match.flags.ToString("x")).Append('\n');
            if (s?.markets != null)
                foreach (var m in s.markets)
                    if (m != null && m.status >= 0)
                        _sb.Append("  k").Append(m.k).Append(' ').Append(m.name).Append(" st ").Append(m.status).Append(" pool ").Append(ChainEconomy.Rush(ChainEconomy.Parse(m.pool))).Append(" bets ").Append(m.nBets).Append(e.MarketOpen(m.k) ? " OPEN" : "").Append('\n');
            if (s?.drops != null)
                _sb.Append("drops ").Append(s.drops.count).Append(" last@").Append(s.drops.lastTick).Append(" pot ").Append(ChainEconomy.Rush(ChainEconomy.Parse(s.drops.pot))).Append('\n');
            for (var i = 0; i < 8; i++)
                if (ChaosDropFx.Queue[i].Kind != 0)
                    _sb.Append("  queued ").Append(ChaosDropFx.Names[ChaosDropFx.Queue[i].Kind]).Append(" @").Append(ChaosDropFx.Queue[i].Tick).Append(" arg ").Append(ChaosDropFx.Queue[i].Arg).Append('\n');
            if (!string.IsNullOrEmpty(e.DealSig))
                _sb.Append("deal ").Append(Short(e.DealSig)).Append('\n');
            foreach (var l in e.Log)
                _sb.Append("<size=11>").Append(l).Append("</size>\n");
            _debug.text = _sb.ToString();
        }
    }
}
