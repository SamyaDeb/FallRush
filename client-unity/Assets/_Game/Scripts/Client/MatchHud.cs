using System.Collections.Generic;
using System.Text;
using FallRush.Net;
using FallRush.Shared;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace FallRush.Client
{
    /// <summary>
    /// Every match screen, driven only by server state (<c>match</c>, <c>mevt</c>, <c>round</c>, <c>rstate</c>, <c>evt</c>,
    /// snapshots), so it is right after a reconnect or a late join. G3.5 restyle on the UI kit (doc 17 §5):
    /// <list type="bullet">
    /// <item>HUD corners: top-left round + modifier chip, top-right timer badge, top-centre the mode counter in a pill
    /// (readable on any background: step 0), bottom-left ping / connection, bottom-right the chain chip (G3.6).</item>
    /// <item>Lobby roster (preset swatch, BOT tag, host), the Deal screen with the Chaos Capsule machine and the VRF slot,
    /// the round intro with the countdown capsule machine (3-2-1-RUSH!), our result banner, aligned leaderboard tables
    /// (fixed columns + tabular figures), the champion banner, the receipt with explorer slots (G3.6).</item>
    /// </list>
    /// Music per phase and the countdown / result sounds are cued here (client only).
    /// </summary>
    public sealed class MatchHud : MonoBehaviour
    {
        public SandboxView View;

        // corners
        private TMP_Text _round, _modifier, _timer, _counter, _conn, _chainText, _sub, _center;
        private RectTransform _roundPill, _modPill, _timerBadge, _counterPill, _connPill, _chainPill, _subPill;
        private Image _timerFill;
        private int _lastTimerSec = -1;
        private string _lastCounter = string.Empty;
        // P7 spectate card: who you watch, "Tab: next", players left
        private UiKit.Card _spectateCard;
        private TMP_Text _spectateName, _spectateWhy, _spectateLeft;
        private Image _chainFill;
        private ResultBanner _banner;
        private CapsuleMachineWidget _countdown;
        // panels
        private RectTransform _lobby, _deal, _results, _inter, _receipt, _champ;
        private TMP_Text _lobbyStatus, _lobbyCount, _dealHead, _dealSeed, _interNext, _receiptHead, _receiptIds;
        private Button _start, _back, _vrfButton, _interLive;
        private RectTransform _vrfChip;
        private TMP_Text _vrfText;
        private string _vrfUrl;
        private UiKit.Card _lobbyCard, _dealCard, _resultsCard, _interCard, _receiptCard, _champCard, _mineCard, _nextCard;
        private RectTransform _mine, _nextRt;
        private TMP_Text _mineHead, _mineStat, _mineLine, _nextMap, _nextInfo, _champName, _champStats, _champSeason;
        private Image _nextIcon;
        private RectTransform _champBadges;
        private int _champBadgeBits = -1;
        private float _champAt = -1f, _resultsAt = -1f;
        private bool _champLanded;
        private uint _finalStartTick;
        private string _finalTime = string.Empty;
        private ReceiptCard _practiceReceipt;
        private int _lobbyShown;
        private readonly int[] _prevRank = new int[Protocol.MaxPlayers];
        private readonly CountUp[] _survived = new CountUp[Protocol.MaxPlayers];
        private readonly List<MatchSlotInfo> _rows = new List<MatchSlotInfo>(Protocol.MaxPlayers);
        private CapsuleMachineWidget _dealMachine;
        private readonly LobbyRow[] _lobbyRows = new LobbyRow[Protocol.MaxPlayers];
        private readonly TableRow[] _interRows = new TableRow[Protocol.MaxPlayers];
        private readonly TableRow[] _receiptRows = new TableRow[Protocol.MaxPlayers];
        private readonly ResultRow[] _resultRows = new ResultRow[Protocol.MaxPlayers];
        private readonly DealCard[] _cards = new DealCard[4];

        private MapInfo _map;
        private ReactiveViews _rv;
        private ChampionPodium _podium;
        private readonly StringBuilder _sb = new StringBuilder(512);
        private float _next;
        private int _spectateSlot = -1;
        private bool _backSent;
        private float _introStart = -1f;
        private RaceRules _course;
        private static IChainAdapter _chain => ChainAdapters.Current; // P3: the on-chain session's adapter while it runs
        private int _lastCount = -1;
        private int _dealtShown;
        private float _dealtAt = -1f;
        private MatchPhase _lastPhase = (MatchPhase)255;
        private bool _resultShown;

        private struct LobbyRow { public RectTransform Root; public Image Conn, Swatch; public TMP_Text Name, Tag; }
        private struct TableRow { public RectTransform Root, Face; public Image Dot, Hi; public TMP_Text Name, R1, R2, R3, R4, Survived, Note; public int Slot; }
        private struct ResultRow { public RectTransform Root; public Image Hi; public TMP_Text Word, Name; }
        private struct DealCard { public RectTransform Slot, Root; public UiKit.Card Card; public Image ModeIcon; public TMP_Text Map, Mode, Target, Odds; public bool Shown; }

        private void Start()
        {
            _map = MapInfo.Current;
            if (_map != null && _map.definition != null && _map.definition.IsRace)
                _course = new RaceRules(_map.CheckpointDefs(), _map.CourseLineMm(), 0);
            _rv = gameObject.AddComponent<ReactiveViews>();
            for (var i = 0; i < _prevRank.Length; i++)
                _prevRank[i] = -1;
            var canvas = UiKit.CreateCanvas("MatchHudCanvas");
            canvas.SetParent(transform, false);

            // ---- corners
            _roundPill = Pill(canvas, new Vector2(0f, 1f), new Vector2(14f, -12f), new Vector2(470f, 40f), Color.white, out _round, 19, UiKit.Grape, TextAlignmentOptions.Left);
            _modPill = Pill(canvas, new Vector2(0f, 1f), new Vector2(14f, -58f), new Vector2(300f, 30f), UiKit.Magenta, out _modifier, 15, Color.white, TextAlignmentOptions.Left);
            _timerBadge = Pill(canvas, new Vector2(1f, 1f), new Vector2(-14f, -10f), new Vector2(120f, 64f), Color.white, out _timer, 44, UiKit.Ink, TextAlignmentOptions.Center);
            _timer.font = UiKit.Body; // tabular figures: the timer does not jitter
            _timer.fontStyle = FontStyles.Bold;
            _counterPill = Pill(canvas, new Vector2(0.5f, 1f), new Vector2(0f, -10f), new Vector2(420f, 50f), Color.white, out _counter, 26, UiKit.Magenta, TextAlignmentOptions.Center);
            _counter.font = UiKit.Display;
            _connPill = Pill(canvas, new Vector2(0f, 0f), new Vector2(14f, 44f), new Vector2(240f, 32f), Color.white, out _conn, 15, UiKit.Ink, TextAlignmentOptions.Left);
            _chainPill = Pill(canvas, new Vector2(1f, 0f), new Vector2(-14f, 44f), new Vector2(170f, 34f), UiKit.Grey, out _chainText, 16, Color.white, TextAlignmentOptions.Center);
            _chainText.font = UiKit.Display;
            _chainFill = _chainPill.Find("Fill").GetComponent<Image>();
            _subPill = Pill(canvas, new Vector2(0.5f, 0f), new Vector2(0f, 48f), new Vector2(900f, 64f), new Color(1f, 1f, 1f, 0.95f), out _sub, 22, UiKit.Grape, TextAlignmentOptions.Center);
            _timerFill = _timerBadge.Find("Fill").GetComponent<Image>();
            BuildSpectate(canvas);

            _center = UiKit.Shout(canvas, string.Empty, 96, UiKit.Magenta);
            var crt = (RectTransform)_center.transform;
            crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(0.5f, 0.5f);
            crt.anchoredPosition = new Vector2(0f, 90f);
            crt.sizeDelta = new Vector2(1100f, 200f);
            _banner = ResultBanner.Create(canvas, new Vector2(110f, 205f));
            _banner.transform.localScale = Vector3.one * 0.85f;
            _countdown = CapsuleMachineWidget.Create(canvas, new Vector2(150f, 170f));
            var cdr = (RectTransform)_countdown.transform;
            cdr.anchorMin = cdr.anchorMax = new Vector2(0.5f, 0.5f);
            cdr.anchoredPosition = new Vector2(0f, 150f);
            _countdown.gameObject.SetActive(false);

            BuildLobby(canvas);
            BuildDeal(canvas);
            BuildResults(canvas);
            BuildIntermission(canvas);
            BuildReceipt(canvas);
            BuildChampion(canvas);
            foreach (var p in new[] { _lobby, _deal, _results, _inter, _receipt, _champ, _mine })
                p.gameObject.SetActive(false);
            var cs = ClientSim.Instance;
            if (cs != null)
                cs.RaceEventReceived += OnEvent;
        }

        private void OnDestroy()
        {
            var cs = ClientSim.Instance;
            if (cs != null)
                cs.RaceEventReceived -= OnEvent;
        }

        private static RectTransform Pill(Transform canvas, Vector2 anchor, Vector2 pos, Vector2 size, Color fill, out TMP_Text text, int fontSize, Color textColor, TextAlignmentOptions align)
        {
            var go = new GameObject("Pill", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(canvas, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            UiKit.Pill(rt, fill);
            text = UiKit.Label(rt, string.Empty, fontSize, textColor, FontStyle.Bold);
            text.alignment = align;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            var trt = (RectTransform)text.transform;
            UiKit.Stretch(trt);
            trt.offsetMin = new Vector2(14f, 2f);
            trt.offsetMax = new Vector2(-14f, -2f);
            text.GetComponent<LayoutElement>().ignoreLayout = true;
            go.SetActive(false);
            return rt;
        }

        private static void SetPill(RectTransform pill, TMP_Text t, string s)
        {
            var on = !string.IsNullOrEmpty(s);
            if (pill.gameObject.activeSelf != on)
                pill.gameObject.SetActive(on);
            if (on && t.text != s)
                t.text = s;
        }

        // ------------------------------------------------------------------ panels

        private void BuildLobby(RectTransform canvas)
        {
            _lobbyCard = UiKit.MakeCard(canvas, UiKit.CardSize.M, UiKit.CardHue.Soda, "LOBBY", new Vector2(420f, 640f), UiIcon.Capsule);
            _lobby = _lobbyCard.Root;
            _lobby.anchorMin = _lobby.anchorMax = _lobby.pivot = new Vector2(1f, 0.5f);
            _lobby.anchoredPosition = new Vector2(-18f, 0f);
            var body = _lobbyCard.Body;
            body.GetComponent<VerticalLayoutGroup>().spacing = 1;
            _lobbyCount = UiKit.Label(body, string.Empty, 22, UiKit.Ink, FontStyle.Bold, 30);
            _lobbyCount.font = UiKit.Display;
            for (var i = 0; i < _lobbyRows.Length; i++)
            {
                var face = UiKit.FaceRow(body, 30f, 8f);
                var r = new LobbyRow { Root = (RectTransform)face.parent };
                r.Conn = UiKit.Dot(face, UiKit.Mint, 10f);
                r.Swatch = UiKit.Dot(face, Color.white, 18f);
                r.Name = UiKit.Cell(face, string.Empty, 230f, 17, UiKit.Ink);
                r.Tag = UiKit.Cell(face, string.Empty, 70f, 13, UiKit.Grape, TextAlignmentOptions.Right, true);
                r.Root.gameObject.SetActive(false);
                _lobbyRows[i] = r;
            }

            _lobbyStatus = UiKit.Label(body, string.Empty, 17, UiKit.Ink, FontStyle.Normal, 64);
            _start = UiKit.Button(body, "Start now (host)", UiKit.SunYellow, () => ClientSim.Instance?.SendControl(ControlCmd.Start));
        }

        private static UiKit.CardHue ModeHue(string mode) => mode switch
        {
            "Race" => UiKit.CardHue.Soda,
            "Survival" => UiKit.CardHue.Grape,
            "Skill" => UiKit.CardHue.Lemon,
            "Final" => UiKit.CardHue.Bubblegum,
            _ => UiKit.CardHue.Apple,
        };

        private void BuildDeal(RectTransform canvas)
        {
            _dealCard = UiKit.MakeCard(canvas, UiKit.CardSize.L, UiKit.CardHue.Lemon, "THE DEAL", new Vector2(980f, 560f), UiIcon.Capsule);
            _deal = _dealCard.Root;
            var v = _dealCard.Body.GetComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(300, 26, 14, 16);
            v.spacing = 10;
            v.childAlignment = TextAnchor.UpperLeft;
            _dealMachine = CapsuleMachineWidget.Create(_dealCard.Body, new Vector2(250f, 312f));
            var mrt = (RectTransform)_dealMachine.transform;
            mrt.anchorMin = mrt.anchorMax = new Vector2(0f, 0.5f);
            mrt.anchoredPosition = new Vector2(152f, 14f);
            _dealHead = UiKit.Label(_dealCard.Body, string.Empty, 20, UiKit.Ink, FontStyle.Bold, 30);
            _dealHead.alignment = TextAlignmentOptions.Left;
            var row = UiKit.Row(_dealCard.Body, 236f, 12f);
            for (var i = 0; i < _cards.Length; i++)
            {
                var slot = new GameObject("RoundSlot", typeof(RectTransform), typeof(LayoutElement)).GetComponent<RectTransform>();
                slot.SetParent(row, false);
                var le = slot.GetComponent<LayoutElement>();
                le.minWidth = le.preferredWidth = 146f;
                var card = UiKit.MakeCard(slot, UiKit.CardSize.S, UiKit.CardHue.Apple, $"ROUND {i + 1}", new Vector2(146f, 230f));
                UiKit.Stretch(card.Root);
                var b = card.Body;
                b.GetComponent<VerticalLayoutGroup>().childAlignment = TextAnchor.UpperCenter;
                var dc = new DealCard { Slot = slot, Root = card.Root, Card = card };
                dc.ModeIcon = UiKit.IconImage(b, UiIcon.Race, UiKit.CardRim(UiKit.CardHue.Apple), 46f);
                dc.Map = UiKit.Label(b, string.Empty, 17, UiKit.Ink, FontStyle.Bold, 44);
                dc.Map.font = UiKit.Display;
                dc.Mode = UiKit.Label(b, string.Empty, 13, UiKit.Grape, FontStyle.Bold, 18);
                dc.Target = UiKit.Label(b, string.Empty, 14, UiKit.Ink, FontStyle.Bold, 20);
                dc.Odds = UiKit.Label(b, string.Empty, 11, UiKit.Grape, FontStyle.Normal, 30);
                card.Root.gameObject.SetActive(false);
                _cards[i] = dc;
            }

            // VRF / deal provenance: a chip with the real explorer link on a recorded match, "Classic deal" in practice
            var vrow = UiKit.Row(_dealCard.Body, 40f, 10f);
            _vrfChip = UiKit.Chip(vrow, "Classic deal", UiKit.CardPastel(UiKit.CardHue.Apple), UiKit.Ink, UiIcon.Capsule, 15, 34f);
            _vrfText = _vrfChip.GetComponentInChildren<TMP_Text>();
            _vrfButton = UiKit.Button(vrow, "View the VRF request", UiKit.Mint, () =>
            {
                if (!string.IsNullOrEmpty(_vrfUrl))
                    Application.OpenURL(_vrfUrl);
            });
            var vle = _vrfButton.GetComponent<LayoutElement>();
            vle.preferredWidth = vle.minWidth = 230f;
            vle.minHeight = vle.preferredHeight = 38f;
            _vrfButton.GetComponentInChildren<TMP_Text>().fontSize = 18;
            _vrfButton.gameObject.SetActive(false);
            _dealSeed = UiKit.Label(_dealCard.Body, string.Empty, 12, UiKit.Grey, FontStyle.Normal, 20);
            _dealSeed.alignment = TextAlignmentOptions.Left;
        }

        private void BuildResults(RectTransform canvas)
        {
            _resultsCard = UiKit.MakeCard(canvas, UiKit.CardSize.M, UiKit.CardHue.Apple, "ROUND RESULT", new Vector2(500f, 500f), UiIcon.Check);
            _results = _resultsCard.Root;
            _results.anchorMin = _results.anchorMax = _results.pivot = new Vector2(0f, 0.5f);
            _results.anchoredPosition = new Vector2(18f, -40f);
            _resultsCard.Body.GetComponent<VerticalLayoutGroup>().spacing = 0;
            for (var i = 0; i < _resultRows.Length; i++)
            {
                var face = UiKit.FaceRow(_resultsCard.Body, 30f, 10f);
                var hi = UiKit.Shape(face, "Hi", UiKit.CardPastel(UiKit.CardHue.Lemon));
                UiKit.Stretch(hi.rectTransform, 0f);
                hi.transform.SetAsFirstSibling();
                _resultRows[i] = new ResultRow
                {
                    Root = (RectTransform)face.parent,
                    Hi = hi,
                    Word = UiKit.Cell(face, string.Empty, 130f, 15, UiKit.Mint, TextAlignmentOptions.Left, true),
                    Name = UiKit.Cell(face, string.Empty, 300f, 16, UiKit.Ink),
                };
                _resultRows[i].Word.font = UiKit.Display;
                _resultRows[i].Root.gameObject.SetActive(false);
            }

            // your own result: a card that lands after the banner
            _mineCard = UiKit.MakeCard(canvas, UiKit.CardSize.M, UiKit.CardHue.Apple, "QUALIFIED", new Vector2(400f, 200f), UiIcon.Check);
            _mine = _mineCard.Root;
            _mine.anchorMin = _mine.anchorMax = _mine.pivot = new Vector2(1f, 0.5f);
            _mine.anchoredPosition = new Vector2(-28f, -60f);
            _mineHead = UiKit.Label(_mineCard.Body, string.Empty, 34, UiKit.Ink, FontStyle.Bold, 44);
            _mineHead.font = UiKit.Display;
            _mineStat = UiKit.Label(_mineCard.Body, string.Empty, 18, UiKit.Grape, FontStyle.Bold, 26);
            _mineLine = UiKit.Label(_mineCard.Body, string.Empty, 15, UiKit.Ink, FontStyle.Normal, 40);
        }

        private TableRow[] Table(Transform panel, TableRow[] rows)
        {
            var head = UiKit.Row(panel, 24f, 6f);
            head.GetComponent<HorizontalLayoutGroup>().padding = new RectOffset(8, 8, 0, 0);
            UiKit.Cell(head, string.Empty, 20f, 13, UiKit.Grape);
            UiKit.Cell(head, "PLAYER", 236f, 13, UiKit.Grape, TextAlignmentOptions.Left, true);
            for (var r = 1; r <= 4; r++)
                UiKit.Cell(head, "R" + r, 50f, 13, UiKit.Grape, TextAlignmentOptions.Center, true);
            UiKit.Cell(head, "ROUNDS", 66f, 13, UiKit.Grape, TextAlignmentOptions.Center, true);
            UiKit.Cell(head, string.Empty, 150f, 13, UiKit.Grape);
            for (var i = 0; i < rows.Length; i++)
            {
                var face = UiKit.FaceRow(panel, 27f, 6f);
                var hi = UiKit.Shape(face, "Hi", UiKit.CardPastel(UiKit.CardHue.Lemon));
                UiKit.Stretch(hi.rectTransform, 0f);
                hi.transform.SetAsFirstSibling();
                rows[i] = new TableRow
                {
                    Root = (RectTransform)face.parent,
                    Face = face,
                    Hi = hi,
                    Dot = UiKit.Dot(face, UiKit.Mint, 11f),
                    Name = UiKit.Cell(face, string.Empty, 236f, 16, UiKit.Ink),
                    R1 = UiKit.Cell(face, string.Empty, 50f, 16, UiKit.Ink, TextAlignmentOptions.Center),
                    R2 = UiKit.Cell(face, string.Empty, 50f, 16, UiKit.Ink, TextAlignmentOptions.Center),
                    R3 = UiKit.Cell(face, string.Empty, 50f, 16, UiKit.Ink, TextAlignmentOptions.Center),
                    R4 = UiKit.Cell(face, string.Empty, 50f, 16, UiKit.Ink, TextAlignmentOptions.Center),
                    Survived = UiKit.Cell(face, string.Empty, 66f, 17, UiKit.Grape, TextAlignmentOptions.Center, true),
                    Note = UiKit.Cell(face, string.Empty, 150f, 13, UiKit.Magenta),
                    Slot = -1,
                };
                rows[i].Root.gameObject.SetActive(false);
            }

            return rows;
        }

        private void BuildIntermission(RectTransform canvas)
        {
            _interCard = UiKit.MakeCard(canvas, UiKit.CardSize.L, UiKit.CardHue.Grape, "LEADERBOARD", new Vector2(780f, 640f), UiIcon.Trophy);
            _inter = _interCard.Root;
            _inter.anchoredPosition = new Vector2(0f, 10f);
            _interCard.Body.GetComponent<VerticalLayoutGroup>().spacing = 0;
            Table(_interCard.Body, _interRows);
            // next round: a mini card of the next map + a working link for spectators (/live)
            var foot = UiKit.Row(_interCard.Body, 74f, 12f, TextAnchor.MiddleCenter);
            var slot = new GameObject("NextSlot", typeof(RectTransform), typeof(LayoutElement)).GetComponent<RectTransform>();
            slot.SetParent(foot, false);
            var le = slot.GetComponent<LayoutElement>();
            le.minWidth = le.preferredWidth = 470f;
            _nextCard = UiKit.MakeCard(slot, UiKit.CardSize.S, UiKit.CardHue.Soda, "NEXT ROUND", new Vector2(470f, 72f));
            UiKit.Stretch(_nextCard.Root);
            _nextRt = _nextCard.Root;
            var nb = _nextCard.Body;
            DestroyImmediate(nb.GetComponent<VerticalLayoutGroup>());
            var h = nb.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.padding = new RectOffset(10, 10, 2, 2);
            h.spacing = 10;
            h.childAlignment = TextAnchor.MiddleLeft;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = false;
            _nextIcon = UiKit.IconImage(nb, UiIcon.Race, UiKit.CardRim(UiKit.CardHue.Soda), 30f);
            _nextMap = UiKit.Cell(nb, string.Empty, 150f, 18, UiKit.Ink, TextAlignmentOptions.Left, true);
            _nextMap.font = UiKit.Display;
            _nextInfo = UiKit.Cell(nb, string.Empty, 250f, 13, UiKit.Grape, TextAlignmentOptions.Left, true);
            _nextInfo.textWrappingMode = TextWrappingModes.Normal;
            _interLive = UiKit.Button(foot, "Bet on /live", UiKit.Mint, () => Application.OpenURL("/live"));
            var lle = _interLive.GetComponent<LayoutElement>();
            lle.preferredWidth = lle.minWidth = 200f;
            lle.minHeight = lle.preferredHeight = 46f;
            _interLive.gameObject.SetActive(Application.platform == RuntimePlatform.WebGLPlayer);
            _interNext = UiKit.Label(_interCard.Body, string.Empty, 14, UiKit.Ink, FontStyle.Bold, 20);
        }

        private void BuildReceipt(RectTransform canvas)
        {
            _receiptCard = UiKit.MakeCard(canvas, UiKit.CardSize.L, UiKit.CardHue.Tangerine, "MATCH RECEIPT", new Vector2(820f, 690f), UiIcon.Check);
            _receipt = _receiptCard.Root;
            var body = _receiptCard.Body;
            body.GetComponent<VerticalLayoutGroup>().spacing = 0;
            _receiptHead = UiKit.Label(body, string.Empty, 22, UiKit.Ink, FontStyle.Bold, 30);
            _receiptHead.font = UiKit.Display;
            Table(body, _receiptRows);
            _receiptIds = UiKit.Label(body, string.Empty, 12, UiKit.Grey, FontStyle.Normal, 32);
            _practiceReceipt = ReceiptCard.Create(body, 760f, 92f);
            _back = UiKit.Button(body, "Back to lobby", UiKit.Mint, () =>
            {
                _backSent = true;
                ClientSim.Instance?.SendControl(ControlCmd.Back);
            });
            _back.GetComponent<LayoutElement>().minHeight = 50;
        }

        /// <summary>P7: the CHAMPION card that lands after the crown drop (replaces the old "CHAMPION: name" counter line).</summary>
        private void BuildChampion(RectTransform canvas)
        {
            _champCard = UiKit.MakeCard(canvas, UiKit.CardSize.L, UiKit.CardHue.Lemon, "CHAMPION", new Vector2(460f, 300f), UiIcon.Crown);
            _champ = _champCard.Root;
            _champ.anchorMin = _champ.anchorMax = _champ.pivot = new Vector2(0f, 0.5f);
            _champ.anchoredPosition = new Vector2(28f, -20f);
            var b = _champCard.Body;
            _champName = UiKit.Label(b, string.Empty, 40, UiKit.Ink, FontStyle.Bold, 50);
            _champName.font = UiKit.Display;
            _champName.fontSharedMaterial = UiKit.DisplayOutlined;
            _champName.color = UiKit.SunYellow;
            _champStats = UiKit.Label(b, string.Empty, 18, UiKit.Ink, FontStyle.Bold, 26);
            _champSeason = UiKit.Label(b, string.Empty, 16, UiKit.Grape, FontStyle.Bold, 24);
            _champBadges = UiKit.Row(b, 30f, 6f, TextAnchor.MiddleCenter);
        }

        private void BuildSpectate(RectTransform canvas)
        {
            _spectateCard = UiKit.MakeCard(canvas, UiKit.CardSize.S, UiKit.CardHue.Grape, "SPECTATING", new Vector2(420f, 96f), UiIcon.Eye);
            var r = _spectateCard.Root;
            r.anchorMin = r.anchorMax = r.pivot = new Vector2(0.5f, 0f);
            r.anchoredPosition = new Vector2(0f, 118f);
            var b = _spectateCard.Body;
            DestroyImmediate(b.GetComponent<VerticalLayoutGroup>());
            var h = b.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.padding = new RectOffset(12, 12, 4, 4);
            h.spacing = 8;
            h.childAlignment = TextAnchor.MiddleLeft;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = h.childForceExpandHeight = false;
            _spectateName = UiKit.Cell(b, string.Empty, 170f, 20, UiKit.Ink, TextAlignmentOptions.Left, true);
            _spectateName.font = UiKit.Display;
            UiKit.Chip(b, "Tab: next", UiKit.CardRim(UiKit.CardHue.Grape), Color.white, UiIcon.None, 13, 26f);
            _spectateLeft = UiKit.Chip(b, "12 left", UiKit.Ink, Color.white, UiIcon.None, 13, 26f).GetComponentInChildren<TMP_Text>();
            _spectateWhy = UiKit.Label(r, string.Empty, 13, UiKit.Ink, FontStyle.Bold, 18);
            var wrt = (RectTransform)_spectateWhy.transform;
            _spectateWhy.GetComponent<LayoutElement>().ignoreLayout = true;
            wrt.anchorMin = new Vector2(0f, 0f);
            wrt.anchorMax = new Vector2(1f, 0f);
            wrt.pivot = new Vector2(0.5f, 1f);
            wrt.anchoredPosition = new Vector2(0f, -4f);
            wrt.sizeDelta = new Vector2(0f, 20f);
            r.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------------ gameplay events → banners + sounds

        private void OnEvent(RaceEvent e)
        {
            var cs = ClientSim.Instance;
            if (cs == null)
                return;
            if (e.Slot != cs.OwnSlot)
                return;
            switch (e.Type)
            {
                case RaceEventType.Qualified:
                    _banner.Show(Mode() == "Survival" ? "ESCAPED!" : "QUALIFIED!", UiKit.Mint);
                    GameAudio.Play2D(GameAudio.Sfx.Qualify);
                    Fx.Confetti(View != null ? View.OwnFeet + Vector3.up : Vector3.zero);
                    FollowCamera.Punch(0.07f);
                    break;
                case RaceEventType.Eliminated:
                    _banner.Show("ELIMINATED", UiKit.Magenta);
                    GameAudio.Play2D(GameAudio.Sfx.Eliminated);
                    GameAudio.Duck(1.4f);
                    FollowCamera.Punch(0.14f);
                    break;
                case RaceEventType.Out:
                    _banner.Show((ElimReason)e.Arg == ElimReason.Goo ? "GOOED!" : "OUT!", UiKit.Magenta);
                    GameAudio.Play2D(GameAudio.Sfx.Eliminated);
                    GameAudio.Duck(1.4f);
                    FollowCamera.Punch(0.14f);
                    break;
                case RaceEventType.Finished when Mode() == "Survival":
                    _banner.Show("ESCAPED!", UiKit.Mint);
                    GameAudio.Play2D(GameAudio.Sfx.Qualify);
                    break;
                case RaceEventType.Dropped:
                    _banner.Show("BONK! -1 ★", UiKit.SunYellow, 1.2f);
                    break;
                case RaceEventType.Checkpoint:
                    GameAudio.Play2D(GameAudio.Sfx.Checkpoint);
                    if (View != null)
                        Fx.Ring(View.OwnFeet);
                    break;
                case RaceEventType.Scored:
                    GameAudio.Play2D(GameAudio.Sfx.Star, 0.8f, 1f + Mathf.Min(0.4f, e.Arg * 0.02f));
                    break;
            }
        }

        // ------------------------------------------------------------------ frame

        private string Mode() => _map != null && _map.definition != null ? _map.definition.mode : "Sandbox";

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.tabKey.wasPressedThisFrame)
                _spectateSlot = NextAlive(_spectateSlot);
            var cs = ClientSim.Instance;
            if (cs == null)
                return;
            UpdateCamera(cs);
            Chain();
            Countdown(cs);
            Champion(cs);
            if (Time.unscaledTime < _next)
                return;
            _next = Time.unscaledTime + 0.1f;
            Refresh(cs);
        }

        private static readonly string[] ChipText = { ChainChipStyle.Text(ChainState.Server), ChainChipStyle.Text(ChainState.RollupConfirmed), ChainChipStyle.Text(ChainState.SolanaConfirmed), ChainChipStyle.Text(ChainState.Pending) };
        private RectTransform[] _panels;
        private RectTransform _want;

        /// <summary>Shows only the wanted panel; touches SetActive only on a change (no layout rebuild every refresh).</summary>
        private void ApplyPanels()
        {
            foreach (var p in _panels)
                UiMotion.SetVisible(p, p == _want);
        }

        private void Chain()
        {
            var st = _chain.State;
            SetPill(_chainPill, _chainText, ChipText[(int)st]); // cached strings: no per-frame allocation
            _chainFill.color = ChainChipStyle.Color(st);
            _chainText.color = st == ChainState.Server ? Color.white : UiKit.Ink;
        }

        /// <summary>G3.4 #5: the countdown is a capsule machine cranking out 3, 2, 1, RUSH!</summary>
        private void Countdown(ClientSim cs)
        {
            var m = cs.Match;
            var rs = cs.RaceState;
            var round = cs.Round;
            var on = m != null && m.Phase >= MatchPhase.RoundIntro && m.Phase <= MatchPhase.RoundActive && cs.HasRaceState && round.HasValue &&
                     (rs.Phase == RoundPhase.Countdown || rs.Phase == RoundPhase.Rush && ServerTickNow(cs) < round.Value.RoundStartTick + 25);
            if (_countdown.gameObject.activeSelf != on)
                _countdown.gameObject.SetActive(on);
            if (!on)
            {
                _lastCount = -1;
                return;
            }

            var toRush = (long)round.Value.RoundStartTick - ServerTickNow(cs);
            var n = toRush > 60 ? 3 : toRush > 30 ? 2 : toRush > 0 ? 1 : 0;
            _countdown.Spin(n > 0);
            if (n != _lastCount)
            {
                _lastCount = n;
                if (n > 0)
                {
                    _countdown.Pop(n.ToString(), n == 3 ? UiKit.Magenta : n == 2 ? UiKit.Sky : UiKit.SunYellow);
                    GameAudio.Play2D(GameAudio.Sfx.Count, 1f, n == 1 ? 1.25f : 1f);
                    GameAudio.Duck(0.5f, 0.5f);
                }
                else
                {
                    _countdown.Pop("RUSH!", UiKit.Mint);
                    GameAudio.Play2D(GameAudio.Sfx.Rush);
                }
            }
        }

        private uint ServerTickNow(ClientSim cs) => cs.HasSnapshots ? cs.Newest.Tick + (uint)Mathf.Max(0f, (float)(ClientSim.Now - cs.Newest.ReceivedAt) * 30f) : 0u;

        private static string Secs(long ticks) => ticks <= 0 ? "0" : ((ticks + 29) / 30).ToString();

        private bool Spectating(ClientSim cs) => cs.MatchSlot == SnapHeader.NoSlot || cs.OwnOutOfSim || !SlotAliveInSnapshot(cs, cs.OwnSlot);

        private static bool SlotAliveInSnapshot(ClientSim cs, int slot)
        {
            if (!cs.HasSnapshots || slot < 0)
                return false;
            ref readonly var n = ref cs.Newest;
            for (var i = 0; i < n.Count; i++)
                if (n.Players[i].Slot == slot)
                    return (n.Players[i].Flags & MotorState.Respawning) == 0 || cs.Phase == RoundPhase.Sandbox || cs.Phase == RoundPhase.Waiting || cs.Phase == RoundPhase.Rush && (n.Players[i].Race & PlayerState.RaceEliminated) == 0 && Mode2(cs) == "Race";
            return false;
        }

        private static string Mode2(ClientSim cs) => MapInfo.Current != null && MapInfo.Current.definition != null ? MapInfo.Current.definition.mode : "Sandbox";

        private int NextAlive(int from)
        {
            var cs = ClientSim.Instance;
            if (cs == null || !cs.HasSnapshots)
                return -1;
            ref readonly var n = ref cs.Newest;
            for (var k = 1; k <= Protocol.MaxPlayers; k++)
            {
                var s = ((from < 0 ? -1 : from) + k + Protocol.MaxPlayers) % Protocol.MaxPlayers;
                for (var i = 0; i < n.Count; i++)
                    if (n.Players[i].Slot == s && s != cs.OwnSlot && (n.Players[i].Flags & MotorState.Respawning) == 0 && (n.Players[i].Race & PlayerState.RaceFinished) == 0)
                        return s;
            }

            return -1;
        }

        private void UpdateCamera(ClientSim cs)
        {
            if (View == null)
                return;
            var m = cs.Match;
            // Champion: look at the podium (G3.4 crown ceremony).
            if (m != null && (m.Phase == MatchPhase.Champion || m.Phase == MatchPhase.Finalizing) && m.Champion != 255)
            {
                _podium ??= ChampionPodium.Create(transform, m.Champion, _map);
                View.FocusPoint = _podium.FocusPoint;
                View.FollowSlot = -1;
                return;
            }

            if (_podium != null && (m == null || m.Phase < MatchPhase.Champion))
            {
                Destroy(_podium.gameObject);
                _podium = null;
            }

            // Round intro: a fly-through from high over the map down to your spawn.
            if (cs.Phase == RoundPhase.Intro && m != null && m.Phase == MatchPhase.RoundIntro && _map != null)
            {
                if (_introStart < 0f)
                    _introStart = Time.unscaledTime;
                var f = Mathf.Clamp01((Time.unscaledTime - _introStart) / 3f);
                var own = cs.OwnSlot >= 0 && cs.OwnSlot < Protocol.MaxPlayers ? _map.SpawnPoints()[cs.OwnSlot] : new Vec3(0, 0, 0);
                var end = new Vector3(own.X, own.Y + 0.8f, own.Z);
                var start = MapOverview();
                var e = f * f * (3f - 2f * f);
                View.FocusPoint = GameSettings.ReducedMotion ? end : Vector3.Lerp(start, end, e);
                return;
            }

            _introStart = cs.Phase == RoundPhase.Intro ? _introStart : -1f;
            View.FocusPoint = null;
            if (Spectating(cs) && cs.MatchSlot != 255 || cs.MatchSlot == 255)
            {
                if (_spectateSlot < 0 || !SlotAliveInSnapshot(cs, _spectateSlot))
                    _spectateSlot = NextAlive(_spectateSlot);
                View.FollowSlot = _spectateSlot;
            }
            else
            {
                View.FollowSlot = -1;
                _spectateSlot = -1;
            }
        }

        private Vector3 MapOverview()
        {
            var sp = _map.SpawnPoints();
            var c = Vector3.zero;
            foreach (var s in sp)
                c += new Vector3(s.X, s.Y, s.Z);
            c /= sp.Length;
            if (_map.courseLine != null && _map.courseLine.Length > 1)
                c = _map.courseLine[_map.courseLine.Length - 1];
            return c + new Vector3(0f, 18f, 0f);
        }

        private string Name(ClientSim cs, int slot)
        {
            if (cs.TryGetSlotInfo(slot, out var s))
                return s.Name;
            return $"P{slot + 1}";
        }

        private static string Reason(ElimReason r) => r switch
        {
            ElimReason.Goo => "gooed",
            ElimReason.FellOut => "fell out",
            ElimReason.LowerScore => "fewer stars",
            ElimReason.TimeoutRank => "time ran out",
            ElimReason.Disconnect => "disconnected",
            _ => "not qualified",
        };

        private static string ModeGoal(string mode, int n) => mode switch
        {
            "Race" => $"First {n} to the finish qualify",
            "Survival" => $"Climb! First {n} to the summit escape the goo",
            "Skill" => $"Grab stars! Top {n} after 60 s qualify (gold = +3, a bonk drops one)",
            "Final" => "Last one standing wins the crown",
            _ => string.Empty,
        };

        /// <summary>G1 §3.6: one-line hint per mode on the round intro.</summary>
        private static string ModeTip(string mode) => mode switch
        {
            "Race" => "Tip: dive (Shift) to reach further; jump right after a belly-flop to skip the slide",
            "Survival" => "Tip: the inner lanes are safe and slow, the outer lanes fast and risky",
            "Skill" => "Tip: spring pads launch you to the golden-star islands",
            "Final" => "Tip: yellow lids flip open 1 s later: step off! A fall lands on the lower shelf once. Still standing at 120 s? Top shelf beats the lower one",
            _ => string.Empty,
        };

        private void Music(MatchStateMessage m)
        {
            if (m == null)
            {
                GameAudio.SetMusic(GameAudio.Music.Lobby);
                return;
            }

            var mode = Mode();
            GameAudio.SetMusic(m.Phase switch
            {
                MatchPhase.Champion or MatchPhase.Finalizing => GameAudio.Music.Victory,
                MatchPhase.RoundIntro or MatchPhase.RoundActive or MatchPhase.RoundResult => mode == "Final" ? GameAudio.Music.Final : GameAudio.Music.Race,
                MatchPhase.Done or MatchPhase.Intermission => AudioDirector.Get() != null && AudioDirector.Get().VictoryPlaying ? GameAudio.Music.Victory : GameAudio.Music.Lobby,
                _ => GameAudio.Music.Lobby,
            });
        }

        private void Refresh(ClientSim cs)
        {
            var m = cs.Match;
            _chain.OnMatchState(m);
            Music(m);
            var boot = NetBootstrap.Instance;
            var now = ServerTickNow(cs);
            var ping = PingService.Instance;
            // Bottom-left: connection.
            string conn;
            if (boot != null && boot.Reconnecting)
                conn = $"<color=#FF3EA5>● Reconnecting… {Mathf.CeilToInt(boot.ReconnectSecondsLeft)} s</color>";
            else if (boot != null && boot.State == ClientState.Disconnected)
                conn = "<color=#FF3EA5>● Disconnected</color>";
            else
                conn = $"<color=#5BE3B5>●</color> {(ping != null && ping.SampleCount > 0 ? ping.P50Ms.ToString("F0") : "…")} ms{(cs.Remote.Lagging && ChainAdapters.Override == null ? "  <color=#FFA51F>LAGGING</color>" : "")}";
            SetPill(_connPill, _conn, conn);

            _panels ??= new[] { _lobby, _deal, _results, _inter, _receipt }; // the CHAMPION card has its own timing (Champion())
            _want = null;
            if (_center.text.Length > 0)
                _center.text = string.Empty;
            var sub = string.Empty;
            var counter = string.Empty;
            var timer = string.Empty;
            var spect = false;
            _counter.color = UiKit.Magenta;
            if (m == null)
            {
                SetPill(_roundPill, _round, cs.Round.HasValue ? UiNames.Map(cs.Round.Value.MapId) : string.Empty);
                SetPill(_modPill, _modifier, string.Empty);
                SetPill(_counterPill, _counter, string.Empty);
                SetPill(_timerBadge, _timer, string.Empty);
                SetPill(_subPill, _sub, string.Empty);
                UiMotion.SetVisible(_spectateCard.Root, false);
                UiMotion.SetVisible(_mine, false);
                ApplyPanels();
                return;
            }

            var mode = Mode();
            var round = cs.Round;
            var inRound = m.Phase >= MatchPhase.RoundIntro && m.Phase <= MatchPhase.Intermission && round.HasValue;
            SetPill(_roundPill, _round, inRound ? $"ROUND {m.Round}/{m.RoundCount} · {UiNames.Map(round.Value.MapId)} · {mode}" : m.Phase <= MatchPhase.Countdown ? (IsPracticeMatch(m) ? "PRACTICE LOBBY" : "LOBBY") : IsPracticeMatch(m) ? "PRACTICE MATCH" : "MATCH");
            SetPill(_modPill, _modifier, inRound ? UiNames.Modifier(round.Value.Modifier) : string.Empty);
            if (inRound && m.Round == m.RoundCount && round.Value.RoundStartTick > 0)
                _finalStartTick = round.Value.RoundStartTick;

            if (m.Phase != _lastPhase)
            {
                _lastPhase = m.Phase;
                _resultShown = false;
                if (m.Phase == MatchPhase.Dealt)
                {
                    _dealtAt = Time.unscaledTime;
                    _dealtShown = 0;
                }

                if (m.Phase == MatchPhase.RoundResult)
                    _resultsAt = Time.unscaledTime;
                if (m.Phase == MatchPhase.Intermission)
                    StaggerTable(_interRows);
                if (m.Phase == MatchPhase.Done)
                    StaggerTable(_receiptRows);
                if (m.Phase == MatchPhase.Champion || m.Phase == MatchPhase.Finalizing)
                {
                    if (_champAt < 0f)
                    {
                        _champAt = Time.unscaledTime;
                        _champLanded = false;
                        _finalTime = _finalStartTick > 0 && m.PhaseStartTick > _finalStartTick ? $"{(m.PhaseStartTick - _finalStartTick) / 30f:0.0} s" : string.Empty;
                    }
                }
                else if (m.Phase < MatchPhase.Champion)
                    _champAt = -1f;
            }

            switch (m.Phase)
            {
                case MatchPhase.LobbyOpen:
                case MatchPhase.Countdown:
                    ShowLobby(cs, m, now);
                    break;
                case MatchPhase.RosterLocked:
                case MatchPhase.Dealing:
                case MatchPhase.Dealt:
                    ShowDeal(cs, m);
                    break;
                case MatchPhase.RoundIntro:
                case MatchPhase.RoundActive:
                    ShowRound(cs, m, mode, now, ref sub, ref counter, ref timer);
                    break;
                case MatchPhase.RoundResult:
                    ShowResults(cs, m);
                    break;
                case MatchPhase.Intermission:
                    ShowIntermission(cs, m, now);
                    break;
                case MatchPhase.Champion:
                case MatchPhase.Finalizing:
                    // P7: the CHAMPION card (Champion()) lands after the crown drop; the counter line is gone
                    if (m.Champion == cs.MatchSlot)
                        sub = "You won the crown!";
                    break;
                case MatchPhase.Done:
                    ShowReceipt(cs, m, now);
                    break;
                case MatchPhase.Aborted:
                    _center.text = "<size=54>MATCH ABORTED</size>";
                    _center.color = UiKit.Magenta;
                    sub = "Every stake is refunded. Back to the lobby in a moment.";
                    break;
            }

            if (m.Phase >= MatchPhase.RoundIntro && m.Phase <= MatchPhase.Intermission && (cs.MatchSlot == 255 || Spectating(cs)))
            {
                var mine = cs.MatchSlot != 255 && cs.TryGetSlotInfo(cs.MatchSlot, out var info) ? info : default;
                var why = cs.MatchSlot == 255 ? (ChainAdapters.Override != null && mode == "Final" ? "The Final runs on the MagicBlock rollup: bet (B) or drop chaos" : "Match in progress: you join the next one") :
                    mine.Has(MatchSlotInfo.GraceExpired) ? "You were eliminated (disconnected)" :
                    !mine.Has(MatchSlotInfo.Alive) ? "You're out: watch, bet (B) or drop chaos in the Final" : (mine.LateMask & (1 << (m.Round - 1))) != 0 ? "Map loaded too late: you sit this round out (still in)" : "Waiting";
                spect = m.Phase <= MatchPhase.RoundActive;
                if (spect)
                {
                    var name = _spectateSlot >= 0 ? Name(cs, _spectateSlot) : "…";
                    if (_spectateName.text != name)
                        _spectateName.text = name;
                    if (_spectateWhy.text != why)
                        _spectateWhy.text = why;
                    var left = $"{AliveCount(m)} left";
                    if (_spectateLeft.text != left)
                        _spectateLeft.text = left;
                }
            }

            ApplyPanels();
            UiMotion.SetVisible(_spectateCard.Root, spect);
            UiMotion.SetVisible(_mine, m.Phase == MatchPhase.RoundResult && _resultsAt >= 0f && Time.unscaledTime - _resultsAt > 1.1f && _mineHead.text.Length > 0);
            // counter bump on change (same phase)
            if (counter != _lastCounter)
            {
                if (counter.Length > 0 && _lastCounter.Length > 0 && _counterPill.gameObject.activeSelf)
                    UiMotion.Bump(_counterPill);
                _lastCounter = counter;
            }

            SetPill(_counterPill, _counter, counter);
            SetPill(_timerBadge, _timer, timer);
            SetPill(_subPill, _sub, sub);
            var w = Mathf.Clamp(_counter.preferredWidth + 44f, 220f, 900f);
            _counterPill.sizeDelta = new Vector2(w, counter.Contains("\n") ? 76f : 50f);
            _subPill.sizeDelta = new Vector2(Mathf.Clamp(_sub.preferredWidth + 40f, 300f, 1150f), sub.Contains("\n") ? 74f : 44f);
        }

        /// <summary>P7: true while the on-chain layer runs for this client (the public hybrid match: the arena keeper records
        /// it on-chain and the Final runs on the rollup). Set every frame by FallRush.OnChain's economy HUD.</summary>
        public static bool RecordedMatch;

        /// <summary>A practice match: the server's practice flag and no on-chain recording (the public Classic server runs
        /// its matches with the practice flag; the arena keeper records them, so those are not practice).</summary>
        private static bool IsPracticeMatch(MatchStateMessage m) => (m.Flags & MatchStateMessage.FlagPractice) != 0 && !RecordedMatch;

        private static int AliveCount(MatchStateMessage m)
        {
            var n = 0;
            foreach (var s in m.Slots)
                if (s.Has(MatchSlotInfo.Rostered) && s.Has(MatchSlotInfo.Alive))
                    n++;
            return n;
        }

        private void ShowLobby(ClientSim cs, MatchStateMessage m, uint now)
        {
            _want = _lobby;
            var humans = 0;
            var k = 0;
            var shown = 0;
            for (var i = 0; i < _lobbyRows.Length; i++)
            {
                var r = _lobbyRows[i];
                while (k < m.Slots.Length && !m.Slots[k].Has(MatchSlotInfo.Active) && !m.Slots[k].Has(MatchSlotInfo.Rostered))
                    k++;
                var on = k < m.Slots.Length;
                if (on && !r.Root.gameObject.activeSelf)
                    UiMotion.Stagger(r.Root, Mathf.Max(0, i - _lobbyShown)); // a player joined: their row pops in
                else if (!on && r.Root.gameObject.activeSelf)
                    r.Root.gameObject.SetActive(false);
                if (!on)
                    continue;
                shown++;
                var s = m.Slots[k++];
                var bot = s.Has(MatchSlotInfo.Bot);
                if (!bot)
                    humans++;
                r.Conn.color = s.Has(MatchSlotInfo.Connected) ? UiKit.Mint : UiKit.Magenta;
                r.Swatch.color = UiKit.Hex(CosmeticPresets.Get(s.Preset).Shell);
                r.Name.text = s.Slot == cs.MatchSlot ? $"<b>{s.Name}</b> <color=#FF3EA5>(you)</color>" : s.Name;
                r.Tag.text = s.Has(MatchSlotInfo.Host) ? "♛ HOST" : bot ? "BOT" : string.Empty;
            }

            var count = $"{humans} / {Protocol.MaxPlayers} players";
            if (_lobbyCount.text != count)
            {
                if (_lobbyCount.text.Length > 0)
                    UiMotion.Bump((RectTransform)_lobbyCount.transform);
                _lobbyCount.text = count;
            }

            _lobbyShown = shown;
            _sb.Clear();
            _sb.Append("<b>Bots fill the empty slots</b>\n");
            _sb.Append(m.Phase == MatchPhase.Countdown ? $"<size=26><color=#FF3EA5>Starting in {Secs((long)m.PhaseEndTick - now)}</color></size>" : "Waiting for a player…");
            _lobbyStatus.text = _sb.ToString();
            _start.gameObject.SetActive(cs.MatchSlot != 255 && m.HostSlot == cs.MatchSlot);
        }

        /// <summary>
        /// The Deal (G3.4 #2, P7 A1.3): the Chaos Capsule machine spins while the deal is pending, then pops one round card
        /// per round (map, mode icon, qualify target, the round's market). The provenance chip links the on-chain VRF deal
        /// when the arena keeper has one (<see cref="DealProvider"/>), else "Classic deal".
        /// </summary>
        private void ShowDeal(ClientSim cs, MatchStateMessage m)
        {
            _want = _deal;
            var dealt = m.Phase == MatchPhase.Dealt;
            _dealMachine.Spin(!dealt);
            _dealHead.text = dealt ? $"{m.PlayerCount} players · {m.RoundCount} rounds · one crown" : "The Chaos Capsule machine is dealing…";
            var reveal = dealt ? Mathf.Clamp(Mathf.FloorToInt((Time.unscaledTime - _dealtAt) / 0.4f) + 1, 0, m.RoundCount) : 0; // the Classic deal phase is short: 0.4 s apart
            while (_dealtShown < reveal)
            {
                var r = _dealtShown++;
                _dealMachine.Pop($"R{r + 1}", r == m.RoundCount - 1 ? UiKit.SunYellow : UiKit.Mint);
            }

            for (var r = 0; r < _cards.Length; r++)
            {
                ref var c = ref _cards[r];
                var inMatch = r < m.RoundCount;
                if (c.Slot.gameObject.activeSelf != inMatch)
                    c.Slot.gameObject.SetActive(inMatch);
                var shown = inMatch && r < reveal;
                if (shown && !c.Shown)
                {
                    var map = m.Maps[r];
                    var mode = MapPoolV1.ModeOf(map).ToString();
                    c.Card.SetHue(ModeHue(mode));
                    c.ModeIcon.sprite = UiIcons.Get(UiIcons.ForMode(mode));
                    c.ModeIcon.color = UiKit.CardRim(ModeHue(mode));
                    c.Map.text = UiNames.Map(map);
                    c.Mode.text = (mode == "Final" ? "FINAL · on-chain" : mode.ToUpperInvariant()) + (m.Modifiers[r] != 0 ? $"\n<color=#FF3EA5>{UiNames.Modifier(m.Modifiers[r])}</color>" : string.Empty);
                    c.Target.text = mode == "Final" ? "1 wins the crown" : $"{m.Targets[r]} advance";
                    c.Card.Title.text = r == m.RoundCount - 1 ? "FINAL" : $"ROUND {r + 1}";
                    UiMotion.SlideIn(c.Root, new Vector2(-70f, -24f));
                    c.Shown = true;
                }
                else if (!shown && c.Shown)
                {
                    c.Shown = false;
                    c.Root.gameObject.SetActive(false);
                }

                if (c.Shown)
                {
                    var odds = DealOdds(r, m.RoundCount);
                    if (c.Odds.text != odds)
                        c.Odds.text = odds;
                }
            }

            var deal = _chain.Deal;
            var p = DealProvider != null ? DealProvider() : default;
            if (!string.IsNullOrEmpty(p.Url))
            {
                _vrfUrl = p.Url;
                SetChip(_vrfText, $"VRF ✓ MagicBlock VRF seeds the on-chain Final · {Short(p.Signature)}");
                _vrfChip.GetComponentInChildren<Image>().color = UiKit.CardPastel(UiKit.CardHue.Apple);
                _vrfButton.gameObject.SetActive(Application.platform == RuntimePlatform.WebGLPlayer || Application.isEditor);
            }
            else
            {
                _vrfUrl = null;
                SetChip(_vrfText, IsPracticeMatch(m) ? "Practice deal (local seed)" : dealt ? "Rounds 1–3: Classic deal (game server seed)" : "Dealing…");
                _vrfButton.gameObject.SetActive(false);
            }

            _dealSeed.text = $"seed {deal.SeedHex.Substring(0, 16)}…  ·  match {Sha256.Hex(m.MatchId).Substring(0, 16)}…";
        }

        private static void SetChip(TMP_Text t, string s)
        {
            if (t.text != s)
                t.text = s;
        }

        private static string Short(string s) => string.IsNullOrEmpty(s) || s.Length < 12 ? s ?? string.Empty : s.Substring(0, 6) + "…" + s.Substring(s.Length - 4);

        /// <summary>The on-chain deal for this match (signature + explorer link) when the arena keeper dealt it with VRF.</summary>
        public struct DealLink { public string Signature, Url; }

        /// <summary>P7: set by the on-chain layer (FallRush.OnChain) when it knows the match's VRF deal; null otherwise.</summary>
        public static System.Func<DealLink> DealProvider;

        /// <summary>
        /// P5 0.6 (UI only): the Rush Market of each capsule on the Deal screen (round index, round count → a line with
        /// the live pool + the favourite's odds). Set by the on-chain economy HUD (FallRush.OnChain), null otherwise.
        /// </summary>
        public static System.Func<int, int, string> DealOddsProvider;

        private static string DealOdds(int r, int rounds)
        {
            try { return DealOddsProvider != null ? DealOddsProvider(r, rounds) ?? string.Empty : string.Empty; }
            catch { return string.Empty; }
        }

        private void ShowRound(ClientSim cs, MatchStateMessage m, string mode, uint now, ref string sub, ref string counter, ref string timer)
        {
            var round = cs.Round;
            var n = round.HasValue ? round.Value.QualifyCount : 0;
            if (m.Phase == MatchPhase.RoundIntro && (m.Flags & MatchStateMessage.FlagLoading) != 0)
            {
                _center.text = $"<size=48>Loading {(round.HasValue ? UiNames.Map(round.Value.MapId) : "…")}</size>";
                _center.color = UiKit.Grape;
                return;
            }

            var rs = cs.RaceState;
            if (cs.HasRaceState && (rs.Phase == RoundPhase.Intro || rs.Phase == RoundPhase.Countdown) && round.HasValue)
            {
                _center.color = UiKit.Magenta;
                if (rs.Phase == RoundPhase.Intro)
                    _center.text = $"<size=72>{UiNames.Map(round.Value.MapId)}</size>\n<size=34><color=#FFD21F>{mode}</color></size>";
                var modHint = UiNames.ModifierHint(round.Value.Modifier);
                sub = ModeGoal(mode, n) + $"\n<size=15>{ModeTip(mode)}{(modHint.Length > 0 ? $"  ·  <color=#FF3EA5>{UiNames.Modifier(round.Value.Modifier)}: {modHint}</color>" : "")}</size>";
                return;
            }

            // Top-right timer: pulses and turns amber at ≤ 10 s, red at ≤ 5 s, with a tick each second from 5
            if (cs.HasRaceState && rs.RushEndTick > 0)
            {
                var left = (long)rs.RushEndTick - now;
                timer = Secs(left);
                var sec = left <= 0 ? 0 : (int)((left + 29) / 30);
                _timer.color = sec <= 5 ? Color.white : UiKit.Ink;
                _timerFill.color = sec <= 5 ? UiKit.Magenta : sec <= 10 ? UiKit.Amber : Color.white;
                if (sec != _lastTimerSec)
                {
                    if (sec <= 10 && sec > 0 && _lastTimerSec > 0)
                    {
                        UiMotion.Bump(_timerBadge);
                        if (sec <= 5)
                            GameAudio.Ui(GameAudio.UiCue.Tick);
                    }

                    _lastTimerSec = sec;
                }
            }
            else
                _lastTimerSec = -1;

            // Top-centre counter per mode.
            switch (mode)
            {
                case "Race":
                    counter = $"QUALIFIED {rs.QualifiedCount} / {rs.QualifyCount}" + RaceExtra(cs);
                    break;
                case "Survival":
                    counter = $"ESCAPED {rs.FinisherCount} / {rs.QualifyCount} · ALIVE {rs.QualifiedCount}";
                    break;
                case "Skill":
                {
                    var mine = 0;
                    var best = 0;
                    for (var i = 0; i < rs.FinisherCount; i++)
                    {
                        if (rs.FinisherSlots[i] == cs.OwnSlot)
                            mine = (int)rs.FinisherTicks[i];
                        best = Mathf.Max(best, (int)rs.FinisherTicks[i]);
                    }

                    counter = $"★ {mine}  <size=16>(best {best} · top {rs.QualifyCount} qualify)</size>";
                    break;
                }
                case "Final":
                    counter = $"ALIVE {rs.FinisherCount}";
                    break;
            }

            // Subtitles for cues (accessibility).
            if (_rv != null)
            {
                if (_rv.GooWarning)
                    sub = "<color=#9B5CFF>Goo rising!</color>";
                else if (_rv.SpinnersSoon)
                    sub = "Spinners incoming!";
                else if (_rv.TilesCrumbling && mode == "Final" && now < round.GetValueOrDefault().RoundStartTick + 150)
                    sub = "Tiles crumbling!";
            }
        }

        private string RaceExtra(ClientSim cs)
        {
            var cp = cs.OwnRace & PlayerState.RaceCheckpointMask;
            var total = _course != null ? _course.Checkpoints.Length : 0;
            return total > 0 && !cs.OwnOutOfSim ? $"\n<size=15><color=#2A1250>checkpoint {cp}/{total}</color></size>" : string.Empty;
        }

        private static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

        private void ShowResults(ClientSim cs, MatchStateMessage m)
        {
            _want = _results;
            var mode = Mode();
            var k = 0;
            var qualified = 0;
            var myPlace = 0;
            var myQ = false;
            var myReason = ElimReason.NotQualified;
            foreach (var e in cs.MatchEvents)
            {
                if (e.Round != m.Round || (e.Tag != Transcript.Qualified && e.Tag != Transcript.Eliminated) || k >= _resultRows.Length)
                    continue;
                var q = e.Tag == Transcript.Qualified;
                if (q)
                    qualified++;
                var row = _resultRows[k++];
                if (!row.Root.gameObject.activeSelf)
                    UiMotion.Stagger(row.Root, k - 1);
                row.Word.text = q ? "QUALIFIED" : "OUT";
                row.Word.color = q ? UiKit.Hex(0x3FBF8F) : UiKit.Magenta;
                var me = e.Slot == cs.MatchSlot;
                row.Hi.enabled = me;
                if (me)
                {
                    myPlace = k;
                    myQ = q;
                    myReason = (ElimReason)(e.Arg != null && e.Arg.Length > 0 ? e.Arg[0] : 0);
                }

                row.Name.text = (me ? "<b>" : "") + Name(cs, e.Slot) + (me ? " (you)</b>" : "") + (q ? string.Empty : $"  <size=12><color=#8C88A6>{Reason((ElimReason)(e.Arg != null && e.Arg.Length > 0 ? e.Arg[0] : 0))}</color></size>");
            }

            if (k == 0)
                _want = null; // nothing to list (the on-chain Final's results live in its receipt)
            for (; k < _resultRows.Length; k++)
                if (_resultRows[k].Root.gameObject.activeSelf)
                    _resultRows[k].Root.gameObject.SetActive(false);
            _resultsCard.Title.text = $"ROUND {m.Round} RESULT";

            // your own card: place, time or score, one stat line
            if (myPlace > 0)
            {
                _mineCard.SetHue(myQ ? UiKit.CardHue.Apple : UiKit.CardHue.Bubblegum);
                _mineCard.Title.text = myQ ? (mode == "Survival" ? "ESCAPED" : "QUALIFIED") : "ELIMINATED";
                _mineCard.Icon.sprite = UiIcons.Get(myQ ? UiIcon.Check : UiIcon.Cross);
                _mineHead.text = myQ ? $"{Ordinal(myPlace)} place" : "Out this round";
                var stat = MyStat(cs, mode);
                _mineStat.text = myQ ? stat : $"{Reason(myReason)}{(stat.Length > 0 ? " · " + stat : string.Empty)}";
                _mineLine.text = m.Round < m.RoundCount ? $"{qualified} of {m.PlayerCount} advance to round {m.Round + 1}" : "The Final decides the crown";
            }
            else
                _mineHead.text = string.Empty;

            if (!_resultShown && cs.TryGetSlotInfo(cs.MatchSlot, out var mine) && mine.Has(MatchSlotInfo.Rostered))
            {
                _resultShown = true;
                if (mine.Has(MatchSlotInfo.Alive))
                {
                    _banner.Show(Mode() == "Survival" ? "ESCAPED!" : "QUALIFIED!", UiKit.Mint, 4f);
                    FollowCamera.Punch(0.06f);
                }
                else if (mine.EliminatedRound == m.Round)
                {
                    _banner.Show("ELIMINATED", UiKit.Magenta, 4f);
                    FollowCamera.Punch(0.12f); // the claw grab
                }
            }
        }

        /// <summary>Your time (race / survival) or score (skill) for the round just played, from the last round state.</summary>
        private string MyStat(ClientSim cs, string mode)
        {
            if (!cs.HasRaceState || !cs.Round.HasValue)
                return string.Empty;
            var rs = cs.RaceState;
            for (var i = 0; i < rs.FinisherCount; i++)
            {
                if (rs.FinisherSlots[i] != cs.OwnSlot)
                    continue;
                var v = rs.FinisherTicks[i];
                if (mode == "Skill")
                    return $"★ {v} stars";
                var start = cs.Round.Value.RoundStartTick;
                return v > start ? $"time {(v - start) / 30f:0.0} s" : string.Empty;
            }

            return string.Empty;
        }

        private void StaggerTable(TableRow[] table)
        {
            for (var i = 0; i < table.Length; i++)
                if (table[i].Root.gameObject.activeSelf)
                {
                    table[i].Root.gameObject.SetActive(false);
                    table[i].Slot = -1;
                }
        }

        private void Leaderboard(ClientSim cs, MatchStateMessage m, TableRow[] table)
        {
            _rows.Clear();
            foreach (var s in m.Slots)
                if (s.Has(MatchSlotInfo.Rostered))
                    _rows.Add(s);
            _rows.Sort((a, b) =>
            {
                var aa = a.Has(MatchSlotInfo.Alive);
                var ba = b.Has(MatchSlotInfo.Alive);
                if (aa != ba)
                    return aa ? -1 : 1;
                if (a.EliminatedRound != b.EliminatedRound)
                    return b.EliminatedRound.CompareTo(a.EliminatedRound);
                var r = Mathf.Max(0, a.EliminatedRound - 1);
                return a.Places[r].CompareTo(b.Places[r]);
            });
            var now = Time.unscaledTime;
            for (var i = 0; i < table.Length; i++)
            {
                ref var t = ref table[i];
                var on = i < _rows.Count;
                if (!on)
                {
                    if (t.Root.gameObject.activeSelf)
                        t.Root.gameObject.SetActive(false);
                    continue;
                }

                var s = _rows[i];
                if (!t.Root.gameObject.activeSelf)
                    UiMotion.Stagger(t.Root, i);
                else if (t.Slot != s.Slot && s.Slot < _prevRank.Length && _prevRank[s.Slot] >= 0 && _prevRank[s.Slot] != i)
                    UiMotion.SlideFrom(t.Face, new Vector2(0f, (i - _prevRank[s.Slot]) * 27f)); // a changed rank slides to its new place
                if (s.Slot < _prevRank.Length)
                    _prevRank[s.Slot] = i;
                t.Slot = s.Slot;
                var me = s.Slot == cs.MatchSlot;
                var alive = s.Has(MatchSlotInfo.Alive);
                t.Hi.enabled = me;
                t.Dot.color = alive ? UiKit.Mint : UiKit.Magenta;
                t.Name.text = (me ? "<b>" : "") + (s.Has(MatchSlotInfo.Bot) ? "<size=11><color=#7B2FF7>BOT</color></size> " : "") + s.Name + (me ? " (you)</b>" : "");
                t.R1.text = Place(s.Places[0]);
                t.R2.text = Place(s.Places[1]);
                t.R3.text = Place(s.Places[2]);
                t.R4.text = Place(s.Places[3]);
                var survived = alive ? Mathf.Min(m.Round, m.RoundCount) : Mathf.Max(0, s.EliminatedRound - 1);
                if (s.Slot < _survived.Length)
                {
                    _survived[s.Slot].Set(survived, now);
                    t.Survived.text = Mathf.RoundToInt((float)_survived[s.Slot].Value(now)).ToString();
                }

                t.Note.text = alive ? (m.Champion == s.Slot ? "♛ champion" : string.Empty) : $"out R{s.EliminatedRound}: {Reason(s.Reason)}";
            }
        }

        private static string Place(byte p) => p == 0 ? "·" : p.ToString();

        private void ShowIntermission(ClientSim cs, MatchStateMessage m, uint now)
        {
            _want = _inter;
            Leaderboard(cs, m, _interRows);
            var next = m.Round;
            var has = next < m.RoundCount;
            if (_nextRt.gameObject.activeSelf != has)
                _nextRt.gameObject.SetActive(has);
            if (has)
            {
                var mode = MapPoolV1.ModeOf(m.Maps[next]).ToString();
                _nextCard.SetHue(ModeHue(mode));
                _nextCard.Title.text = next == m.RoundCount - 1 ? "NEXT: THE FINAL" : $"NEXT: ROUND {next + 1}";
                _nextIcon.sprite = UiIcons.Get(UiIcons.ForMode(mode));
                _nextIcon.color = UiKit.CardRim(ModeHue(mode));
                _nextMap.text = UiNames.Map(m.Maps[next]);
                _nextInfo.text = $"{mode} · {(mode == "Final" ? "1 wins" : m.Targets[next] + " advance")}{(m.Modifiers[next] != 0 ? $" · <color=#FF3EA5>{UiNames.Modifier(m.Modifiers[next])}</color>" : string.Empty)}";
                _interNext.text = $"Starts in {Secs((long)m.PhaseEndTick - now)} s";
            }
            else
                _interNext.text = string.Empty;
        }

        /// <summary>
        /// The match receipt (Classic / practice). A recorded match shows its on-chain receipt in the economy HUD; here a
        /// practice match shows a clear "Practice: not recorded" card instead of placeholder dashes.
        /// </summary>
        /// <summary>P7: true while the economy HUD shows the on-chain receipt card (left); this receipt then moves right.</summary>
        public static bool ExternalReceipt;

        private void ShowReceipt(ClientSim cs, MatchStateMessage m, uint now)
        {
            _want = _receipt;
            var ext = ExternalReceipt;
            var pivot = ext ? new Vector2(1f, 0.5f) : new Vector2(0.5f, 0.5f);
            if (_receipt.pivot != pivot)
            {
                _receipt.anchorMin = _receipt.anchorMax = _receipt.pivot = pivot;
                _receipt.anchoredPosition = ext ? new Vector2(-14f, -10f) : Vector2.zero;
                _receipt.sizeDelta = ext ? new Vector2(760f, 640f) : new Vector2(820f, 690f);
            }

            _practiceReceipt.gameObject.SetActive(!ext);
            _receiptHead.text = $"♛ Champion: {Name(cs, m.Champion)}";
            Leaderboard(cs, m, _receiptRows);
            _receiptIds.text = $"match {Sha256.Hex(m.MatchId).Substring(0, 16)}…   seed {Sha256.Hex(m.Seed).Substring(0, 16)}…   transcript {Sha256.Hex(m.Transcript).Substring(0, 16)}… ({m.EventCount} events)";
            var links = _chain.Receipt;
            var recorded = !_chain.IsPractice && (!string.IsNullOrEmpty(links.SolanaUrl) || !string.IsNullOrEmpty(links.RollupUrl));
            if (ext)
            {
            }
            else if (recorded)
                _practiceReceipt.ShowSteps(true, _chain.State == ChainState.SolanaConfirmed, links.SolanaUrl ?? links.RollupUrl, null);
            else
                _practiceReceipt.ShowPractice(_chain.IsPractice ? "Practice: not recorded on chain" : "Classic match: results on the game server",
                    IsPracticeMatch(m) ? "Ranked matches write a receipt to Solana (rollup, then Solana) with an explorer link." : "The on-chain receipt appears when the Final ran on the rollup.");
            _back.interactable = !_backSent;
            UiKit.SetButtonText(_back, _backSent ? $"Waiting… (lobby in {Secs((long)m.PhaseEndTick - now)} s)" : $"Back to lobby ({Secs((long)m.PhaseEndTick - now)} s)");
        }

        // ------------------------------------------------------------------ champion card

        /// <summary>The champion's extras from the on-chain layer: the season line and the achievement bits (UI only).</summary>
        public struct ChampionExtras { public string SeasonLine; public int Achievements; }

        /// <summary>P7: set by FallRush.OnChain (P6 season data via the indexer); null in Classic / practice.</summary>
        public static System.Func<int, ChampionExtras> ChampionExtrasProvider;

        private static readonly string[] BadgeNames = { "FIRST CROWN", "3-ROUND SURVIVOR", "PERFECT PREDICTOR", "CHAOS AGENT" };
        private static readonly UiIcon[] BadgeIcons = { UiIcon.Crown, UiIcon.Trophy, UiIcon.Eye, UiIcon.Bolt };

        /// <summary>The CHAMPION card lands 1.9 s after the podium (the crown settles at 1.8 s); confetti and a camera punch.</summary>
        private void Champion(ClientSim cs)
        {
            var m = cs.Match;
            var on = m != null && (m.Phase == MatchPhase.Champion || m.Phase == MatchPhase.Finalizing) && m.Champion != 255 && _champAt >= 0f;
            if (!on)
            {
                if (_champ.gameObject.activeSelf && (m == null || m.Phase != MatchPhase.Done))
                    UiMotion.Hide(_champ);
                return;
            }

            if (!_champLanded && Time.unscaledTime - _champAt >= 1.9f)
            {
                _champLanded = true;
                _champName.text = Name(cs, m.Champion);
                var you = m.Champion == cs.MatchSlot;
                _champCard.Title.text = you ? "CHAMPION: YOU!" : "CHAMPION";
                _champStats.text = $"Survived {m.RoundCount} rounds" + (_finalTime.Length > 0 ? $" · Final {_finalTime}" : string.Empty);
                _champSeason.text = string.Empty;
                _champBadgeBits = -1;
                UiMotion.SlideIn(_champ, new Vector2(0f, 260f));
                UiMotion.Bump(_champ);
                GameAudio.Ui(GameAudio.UiCue.Whoosh);
                FollowCamera.Punch(0.1f);
                if (_podium != null)
                    Fx.Confetti(_podium.FocusPoint + Vector3.up * 1.2f, GameSettings.ReducedMotion ? 30 : 110);
            }

            if (!_champLanded || ChampionExtrasProvider == null)
                return;
            ChampionExtras x;
            try { x = ChampionExtrasProvider(m.Champion); }
            catch { return; }
            if (!string.IsNullOrEmpty(x.SeasonLine) && _champSeason.text != x.SeasonLine)
                _champSeason.text = x.SeasonLine;
            if (x.Achievements != _champBadgeBits)
            {
                _champBadgeBits = x.Achievements;
                for (var i = _champBadges.childCount - 1; i >= 0; i--)
                    Destroy(_champBadges.GetChild(i).gameObject);
                for (var b = 0; b < BadgeNames.Length; b++)
                    if ((x.Achievements >> b & 1) != 0)
                        UiMotion.Stagger(UiKit.Chip(_champBadges, BadgeNames[b], UiKit.CardRim(UiKit.CardHue.Grape), Color.white, BadgeIcons[b], 12, 26f), b);
            }
        }
    }
}
