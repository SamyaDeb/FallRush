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
        private TMP_Text _round, _modifier, _timer, _counter, _conn, _chainText, _sub, _spectate, _center;
        private RectTransform _roundPill, _modPill, _timerBadge, _counterPill, _connPill, _chainPill, _subPill, _spectatePill;
        private Image _chainFill;
        private ResultBanner _banner;
        private CapsuleMachineWidget _countdown;
        // panels
        private RectTransform _lobby, _deal, _results, _inter, _receipt;
        private TMP_Text _lobbyStatus, _dealHead, _dealVrf, _dealSeed, _interNext, _receiptHead, _receiptIds, _receiptChain;
        private readonly TMP_Text[] _receiptLinks = new TMP_Text[3];
        private Button _start, _back;
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
        private IChainAdapter _chain;
        private int _lastCount = -1;
        private int _dealtShown;
        private float _dealtAt = -1f;
        private MatchPhase _lastPhase = (MatchPhase)255;
        private bool _resultShown;

        private struct LobbyRow { public RectTransform Root; public Image Conn, Swatch; public TMP_Text Name, Tag; }
        private struct TableRow { public RectTransform Root; public Image Dot; public TMP_Text Name, R1, R2, R3, R4, Note; }
        private struct ResultRow { public RectTransform Root; public TMP_Text Word, Name; }
        private struct DealCard { public RectTransform Root; public Image Fill; public TMP_Text Round, Map, Mod; }

        private void Start()
        {
            _map = MapInfo.Current;
            _chain = PracticeChainAdapter.Current; // TODO(M6): the real adapter (evt.chainStatus, VRF deal, receipt links)
            if (_map != null && _map.definition != null && _map.definition.IsRace)
                _course = new RaceRules(_map.CheckpointDefs(), _map.CourseLineMm(), 0);
            _rv = gameObject.AddComponent<ReactiveViews>();
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
            _spectatePill = Pill(canvas, new Vector2(0.5f, 0f), new Vector2(0f, 124f), new Vector2(760f, 36f), UiKit.Ink, out _spectate, 16, Color.white, TextAlignmentOptions.Center);

            _center = UiKit.Shout(canvas, string.Empty, 96, UiKit.Magenta);
            var crt = (RectTransform)_center.transform;
            crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(0.5f, 0.5f);
            crt.anchoredPosition = new Vector2(0f, 90f);
            crt.sizeDelta = new Vector2(1100f, 200f);
            _banner = ResultBanner.Create(canvas, new Vector2(110f, 205f));
            _banner.transform.localScale = Vector3.one * 0.85f;
            _countdown = CapsuleMachineWidget.Create(canvas, new Vector2(220f, 250f));
            var cdr = (RectTransform)_countdown.transform;
            cdr.anchorMin = cdr.anchorMax = new Vector2(0.5f, 0.5f);
            cdr.anchoredPosition = new Vector2(0f, 60f);
            _countdown.gameObject.SetActive(false);

            BuildLobby(canvas);
            BuildDeal(canvas);
            BuildResults(canvas);
            BuildIntermission(canvas);
            BuildReceipt(canvas);
            foreach (var p in new[] { _lobby, _deal, _results, _inter, _receipt })
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
            _lobby = UiKit.Panel(canvas, new Vector2(420f, 620f), UiKit.CloudWhite);
            _lobby.anchorMin = _lobby.anchorMax = _lobby.pivot = new Vector2(1f, 0.5f);
            _lobby.anchoredPosition = new Vector2(-16f, 0f);
            var v = _lobby.GetComponent<VerticalLayoutGroup>();
            v.spacing = 2;
            v.padding = new RectOffset(26, 26, 18, 22);
            UiKit.Shout(_lobby, "LOBBY", 38, UiKit.SunYellow).GetComponent<LayoutElement>().minHeight = 48;
            for (var i = 0; i < _lobbyRows.Length; i++)
            {
                var row = UiKit.Row(_lobby, 28f, 8f);
                var r = new LobbyRow { Root = row };
                r.Conn = UiKit.Dot(row, UiKit.Mint, 10f);
                r.Swatch = UiKit.Dot(row, Color.white, 18f);
                r.Name = UiKit.Cell(row, string.Empty, 220f, 17, UiKit.Ink);
                r.Tag = UiKit.Cell(row, string.Empty, 70f, 13, UiKit.Grape, TextAlignmentOptions.Right, true);
                _lobbyRows[i] = r;
            }

            _lobbyStatus = UiKit.Label(_lobby, string.Empty, 17, UiKit.Ink, FontStyle.Normal, 70);
            _start = UiKit.Button(_lobby, "Start now (host)", UiKit.SunYellow, () => ClientSim.Instance?.SendControl(ControlCmd.Start));
        }

        private void BuildDeal(RectTransform canvas)
        {
            _deal = UiKit.Panel(canvas, new Vector2(900f, 540f), UiKit.CloudWhite);
            var v = _deal.GetComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(300, 30, 22, 22);
            v.spacing = 8;
            v.childAlignment = TextAnchor.UpperLeft;
            UiKit.Shout(_deal, "THE DEAL", 46, UiKit.SunYellow).alignment = TextAlignmentOptions.Left;
            _dealMachine = CapsuleMachineWidget.Create(_deal, new Vector2(240f, 300f));
            var mrt = (RectTransform)_dealMachine.transform;
            mrt.anchorMin = mrt.anchorMax = new Vector2(0f, 0.5f);
            mrt.anchoredPosition = new Vector2(150f, 10f);
            _dealHead = UiKit.Label(_deal, string.Empty, 17, UiKit.Grape, FontStyle.Bold, 26);
            _dealHead.alignment = TextAlignmentOptions.Left;
            for (var i = 0; i < _cards.Length; i++)
            {
                var row = UiKit.Row(_deal, 54f, 10f);
                var card = new DealCard { Root = row };
                var bg = UiKit.Shape(row, "Card", new Color(0.95f, 0.96f, 1f));
                UiKit.Stretch(bg.rectTransform);
                bg.transform.SetAsFirstSibling();
                card.Fill = bg;
                card.Round = UiKit.Cell(row, $"R{i + 1}", 48f, 22, UiKit.Grape, TextAlignmentOptions.Center, true);
                card.Round.font = UiKit.Display;
                card.Map = UiKit.Cell(row, string.Empty, 360f, 18, UiKit.Ink, TextAlignmentOptions.Left, true);
                card.Mod = UiKit.Cell(row, string.Empty, 150f, 15, UiKit.Magenta, TextAlignmentOptions.Left, true);
                _cards[i] = card;
            }

            _dealVrf = UiKit.Label(_deal, string.Empty, 16, UiKit.Ink, FontStyle.Bold, 24);
            _dealVrf.alignment = TextAlignmentOptions.Left;
            _dealSeed = UiKit.Label(_deal, string.Empty, 12, UiKit.Grey, FontStyle.Normal, 34);
            _dealSeed.alignment = TextAlignmentOptions.Left;
        }

        private void BuildResults(RectTransform canvas)
        {
            _results = UiKit.Panel(canvas, new Vector2(520f, 470f), UiKit.CloudWhite);
            _results.anchorMin = _results.anchorMax = _results.pivot = new Vector2(0f, 0.5f);
            _results.anchoredPosition = new Vector2(16f, -50f);
            var v = _results.GetComponent<VerticalLayoutGroup>();
            v.spacing = 0;
            v.padding = new RectOffset(24, 24, 16, 18);
            UiKit.Shout(_results, "ROUND RESULT", 32, UiKit.SunYellow).GetComponent<LayoutElement>().minHeight = 42;
            for (var i = 0; i < _resultRows.Length; i++)
            {
                var row = UiKit.Row(_results, 29f, 10f);
                _resultRows[i] = new ResultRow
                {
                    Root = row,
                    Word = UiKit.Cell(row, string.Empty, 130f, 15, UiKit.Mint, TextAlignmentOptions.Left, true),
                    Name = UiKit.Cell(row, string.Empty, 300f, 16, UiKit.Ink),
                };
                _resultRows[i].Word.font = UiKit.Display;
            }
        }

        private TableRow[] Table(Transform panel, TableRow[] rows)
        {
            var head = UiKit.Row(panel, 26f, 6f);
            UiKit.Cell(head, string.Empty, 20f, 13, UiKit.Grape);
            UiKit.Cell(head, "PLAYER", 250f, 13, UiKit.Grape, TextAlignmentOptions.Left, true);
            for (var r = 1; r <= 4; r++)
                UiKit.Cell(head, "R" + r, 54f, 13, UiKit.Grape, TextAlignmentOptions.Center, true);
            UiKit.Cell(head, string.Empty, 170f, 13, UiKit.Grape);
            for (var i = 0; i < rows.Length; i++)
            {
                var row = UiKit.Row(panel, 27f, 6f);
                rows[i] = new TableRow
                {
                    Root = row,
                    Dot = UiKit.Dot(row, UiKit.Mint, 11f),
                    Name = UiKit.Cell(row, string.Empty, 250f, 16, UiKit.Ink),
                    R1 = UiKit.Cell(row, string.Empty, 54f, 16, UiKit.Ink, TextAlignmentOptions.Center),
                    R2 = UiKit.Cell(row, string.Empty, 54f, 16, UiKit.Ink, TextAlignmentOptions.Center),
                    R3 = UiKit.Cell(row, string.Empty, 54f, 16, UiKit.Ink, TextAlignmentOptions.Center),
                    R4 = UiKit.Cell(row, string.Empty, 54f, 16, UiKit.Ink, TextAlignmentOptions.Center),
                    Note = UiKit.Cell(row, string.Empty, 170f, 13, UiKit.Magenta),
                };
            }

            return rows;
        }

        private void BuildIntermission(RectTransform canvas)
        {
            _inter = UiKit.Panel(canvas, new Vector2(760f, 600f), UiKit.CloudWhite);
            var v = _inter.GetComponent<VerticalLayoutGroup>();
            v.spacing = 0;
            v.padding = new RectOffset(28, 28, 16, 18);
            UiKit.Shout(_inter, "LEADERBOARD", 34, UiKit.SunYellow).GetComponent<LayoutElement>().minHeight = 44;
            Table(_inter, _interRows);
            _interNext = UiKit.Label(_inter, string.Empty, 16, UiKit.Ink, FontStyle.Bold, 44);
            var picks = UiKit.Button(_inter, "Fan Picks: coming soon", UiKit.SkyLight, () => { });
            picks.GetComponent<LayoutElement>().minHeight = 44;
            picks.interactable = false; // TODO(P1): Fan Picks (doc 01 §10)
        }

        private void BuildReceipt(RectTransform canvas)
        {
            _receipt = UiKit.Panel(canvas, new Vector2(820f, 680f), UiKit.CloudWhite);
            var v = _receipt.GetComponent<VerticalLayoutGroup>();
            v.spacing = 0;
            v.padding = new RectOffset(28, 28, 14, 16);
            UiKit.Shout(_receipt, "MATCH RECEIPT", 34, UiKit.SunYellow).GetComponent<LayoutElement>().minHeight = 42;
            _receiptHead = UiKit.Label(_receipt, string.Empty, 20, UiKit.Ink, FontStyle.Bold, 28);
            Table(_receipt, _receiptRows);
            _receiptIds = UiKit.Label(_receipt, string.Empty, 12, UiKit.Grey, FontStyle.Normal, 34);
            _receiptChain = UiKit.Label(_receipt, string.Empty, 15, UiKit.Grape, FontStyle.Bold, 22);
            for (var i = 0; i < _receiptLinks.Length; i++)
            {
                _receiptLinks[i] = UiKit.Label(_receipt, string.Empty, 13, UiKit.Ink, FontStyle.Normal, 18);
                _receiptLinks[i].alignment = TextAlignmentOptions.Left;
            }

            _back = UiKit.Button(_receipt, "Back to lobby", UiKit.Mint, () =>
            {
                _backSent = true;
                ClientSim.Instance?.SendControl(ControlCmd.Back);
            });
            _back.GetComponent<LayoutElement>().minHeight = 50;
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
                    break;
                case RaceEventType.Eliminated:
                    _banner.Show("ELIMINATED", UiKit.Magenta);
                    GameAudio.Play2D(GameAudio.Sfx.Eliminated);
                    GameAudio.Duck(1.4f);
                    break;
                case RaceEventType.Out:
                    _banner.Show((ElimReason)e.Arg == ElimReason.Goo ? "GOOED!" : "OUT!", UiKit.Magenta);
                    GameAudio.Play2D(GameAudio.Sfx.Eliminated);
                    GameAudio.Duck(1.4f);
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
            if (Time.unscaledTime < _next)
                return;
            _next = Time.unscaledTime + 0.1f;
            Refresh(cs);
        }

        private void Chain()
        {
            var st = _chain.State;
            SetPill(_chainPill, _chainText, ChainChipStyle.Text(st));
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
            "Final" => "Tip: tiles drop 1 s after you touch them; top-shelf tiles come back",
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
                conn = $"<color=#5BE3B5>●</color> {(ping != null && ping.SampleCount > 0 ? ping.P50Ms.ToString("F0") : "…")} ms{(cs.Remote.Lagging ? "  <color=#FFA51F>LAGGING</color>" : "")}";
            SetPill(_connPill, _conn, conn);

            foreach (var p in new[] { _lobby, _deal, _results, _inter, _receipt })
                p.gameObject.SetActive(false);
            _center.text = string.Empty;
            var sub = string.Empty;
            var counter = string.Empty;
            var timer = string.Empty;
            var spect = string.Empty;
            _counter.color = UiKit.Magenta;
            if (m == null)
            {
                SetPill(_roundPill, _round, cs.Round.HasValue ? UiNames.Map(cs.Round.Value.MapId) : string.Empty);
                SetPill(_modPill, _modifier, string.Empty);
                SetPill(_counterPill, _counter, string.Empty);
                SetPill(_timerBadge, _timer, string.Empty);
                SetPill(_subPill, _sub, string.Empty);
                SetPill(_spectatePill, _spectate, string.Empty);
                return;
            }

            var mode = Mode();
            var round = cs.Round;
            var inRound = m.Phase >= MatchPhase.RoundIntro && m.Phase <= MatchPhase.Intermission && round.HasValue;
            SetPill(_roundPill, _round, inRound ? $"ROUND {m.Round}/{m.RoundCount} · {UiNames.Map(round.Value.MapId)} · {mode}" : m.Phase <= MatchPhase.Countdown ? "PRACTICE LOBBY" : "PRACTICE MATCH");
            SetPill(_modPill, _modifier, inRound ? UiNames.Modifier(round.Value.Modifier) : string.Empty);

            if (m.Phase != _lastPhase)
            {
                _lastPhase = m.Phase;
                _resultShown = false;
                if (m.Phase == MatchPhase.Dealt)
                {
                    _dealtAt = Time.unscaledTime;
                    _dealtShown = 0;
                }
            }

            switch (m.Phase)
            {
                case MatchPhase.LobbyOpen:
                case MatchPhase.Countdown:
                    ShowLobby(cs, m, now);
                    counter = "Run, jump and bump while you wait";
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
                    counter = $"CHAMPION: {Name(cs, m.Champion)}";
                    _counter.color = UiKit.Grape;
                    if (m.Champion == cs.MatchSlot)
                        sub = "You won the crown!";
                    break;
                case MatchPhase.Done:
                    ShowReceipt(cs, m, now);
                    break;
                case MatchPhase.Aborted:
                    _center.text = "<size=54>MATCH ABORTED</size>";
                    _center.color = UiKit.Magenta;
                    sub = "Back to the lobby in a moment.";
                    break;
            }

            if (m.Phase >= MatchPhase.RoundIntro && m.Phase <= MatchPhase.Intermission && (cs.MatchSlot == 255 || Spectating(cs)))
            {
                var mine = cs.MatchSlot != 255 && cs.TryGetSlotInfo(cs.MatchSlot, out var info) ? info : default;
                var why = cs.MatchSlot == 255 ? "Match in progress: you join the next one" :
                    mine.Has(MatchSlotInfo.GraceExpired) ? "You were eliminated (disconnected)" :
                    !mine.Has(MatchSlotInfo.Alive) ? "Eliminated" : (mine.LateMask & (1 << (m.Round - 1))) != 0 ? "Map loaded too late: you sit this round out (still in)" : "Waiting";
                spect = $"SPECTATING {(_spectateSlot >= 0 ? Name(cs, _spectateSlot) : "…")} · Tab: next · {why}";
            }

            SetPill(_counterPill, _counter, counter);
            SetPill(_timerBadge, _timer, timer);
            SetPill(_subPill, _sub, sub);
            SetPill(_spectatePill, _spectate, spect);
            var w = Mathf.Clamp(_counter.preferredWidth + 44f, 220f, 900f);
            _counterPill.sizeDelta = new Vector2(w, counter.Contains("\n") ? 76f : 50f);
            _subPill.sizeDelta = new Vector2(Mathf.Clamp(_sub.preferredWidth + 40f, 300f, 1150f), sub.Contains("\n") ? 74f : 44f);
        }

        private void ShowLobby(ClientSim cs, MatchStateMessage m, uint now)
        {
            _lobby.gameObject.SetActive(true);
            var humans = 0;
            var k = 0;
            for (var i = 0; i < _lobbyRows.Length; i++)
            {
                var r = _lobbyRows[i];
                while (k < m.Slots.Length && !m.Slots[k].Has(MatchSlotInfo.Active) && !m.Slots[k].Has(MatchSlotInfo.Rostered))
                    k++;
                var on = k < m.Slots.Length;
                if (r.Root.gameObject.activeSelf != on)
                    r.Root.gameObject.SetActive(on);
                if (!on)
                    continue;
                var s = m.Slots[k++];
                var bot = s.Has(MatchSlotInfo.Bot);
                if (!bot)
                    humans++;
                r.Conn.color = s.Has(MatchSlotInfo.Connected) ? UiKit.Mint : UiKit.Magenta;
                r.Swatch.color = UiKit.Hex(CosmeticPresets.Get(s.Preset).Shell);
                r.Name.text = s.Slot == cs.MatchSlot ? $"<b>{s.Name}</b> <color=#FF3EA5>(you)</color>" : s.Name;
                r.Tag.text = s.Has(MatchSlotInfo.Host) ? "♛ HOST" : bot ? "BOT" : string.Empty;
            }

            _sb.Clear();
            _sb.Append($"{humans} / {Protocol.MaxPlayers} players · <b>bots fill empty slots</b>\n");
            _sb.Append(m.Phase == MatchPhase.Countdown ? $"<size=26><color=#FF3EA5>Starting in {Secs((long)m.PhaseEndTick - now)}</color></size>" : "Waiting for a player…");
            _lobbyStatus.text = _sb.ToString();
            _start.gameObject.SetActive(cs.MatchSlot != 255 && m.HostSlot == cs.MatchSlot);
        }

        /// <summary>G3.4 #2 / G3.6: the Chaos Capsule machine spins while the deal is pending, then pops one capsule per round.</summary>
        private void ShowDeal(ClientSim cs, MatchStateMessage m)
        {
            _deal.gameObject.SetActive(true);
            var deal = _chain.Deal;
            var dealt = m.Phase == MatchPhase.Dealt;
            _dealMachine.Spin(!dealt);
            _dealHead.text = dealt ? $"{m.PlayerCount} players · {m.RoundCount} rounds" : "The Chaos Capsule machine is dealing…";
            // One capsule per round, 0.7 s apart.
            var reveal = dealt ? Mathf.Clamp(Mathf.FloorToInt((Time.unscaledTime - _dealtAt) / 0.7f) + 1, 0, m.RoundCount) : 0;
            while (_dealtShown < reveal)
            {
                var r = _dealtShown++;
                _dealMachine.Pop($"R{r + 1}", r == m.RoundCount - 1 ? UiKit.SunYellow : UiKit.Mint);
            }

            for (var r = 0; r < _cards.Length; r++)
            {
                var c = _cards[r];
                var on = r < m.RoundCount;
                if (c.Root.gameObject.activeSelf != on)
                    c.Root.gameObject.SetActive(on);
                if (!on)
                    continue;
                var shown = r < reveal;
                var map = m.Maps[r];
                c.Map.text = shown ? $"{UiNames.Map(map)} <size=13><color=#8C88A6>{MapPoolV1.ModeOf(map)} · {m.Targets[r]} advance</color></size>" : "?";
                c.Mod.text = shown ? UiNames.Modifier(m.Modifiers[r]) : string.Empty;
                c.Fill.color = shown ? (r == m.RoundCount - 1 ? new Color(1f, 0.96f, 0.8f) : new Color(0.9f, 0.98f, 0.95f)) : new Color(0.95f, 0.96f, 1f);
            }

            // VRF slot (G3.6): practice shows the local seed; M6 shows "VRF ✓" + the callback signature + explorer link.
            _dealVrf.text = deal.Verified ? $"<color=#3FBF8F>VRF ✓</color> {deal.Headline}  <size=13>{deal.VrfSignature}</size>" : $"<color=#8C88A6>●</color> {deal.Headline}";
            _dealSeed.text = $"seed {deal.SeedHex}\nmatch {Sha256.Hex(m.MatchId).Substring(0, 16)}…  ·  {(deal.ExplorerUrl ?? "explorer link: on ranked matches (M6)")}"; // TODO(M6): clickable explorer link
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

            // Top-right timer.
            if (cs.HasRaceState && rs.RushEndTick > 0)
            {
                var left = (long)rs.RushEndTick - now;
                timer = Secs(left);
                _timer.color = left < 300 ? UiKit.Magenta : UiKit.Ink;
            }

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

        private void ShowResults(ClientSim cs, MatchStateMessage m)
        {
            _results.gameObject.SetActive(true);
            var k = 0;
            foreach (var e in cs.MatchEvents)
            {
                if (e.Round != m.Round || (e.Tag != Transcript.Qualified && e.Tag != Transcript.Eliminated) || k >= _resultRows.Length)
                    continue;
                var q = e.Tag == Transcript.Qualified;
                var row = _resultRows[k++];
                row.Root.gameObject.SetActive(true);
                row.Word.text = q ? "QUALIFIED" : "OUT";
                row.Word.color = q ? UiKit.Hex(0x3FBF8F) : UiKit.Magenta;
                var me = e.Slot == cs.MatchSlot;
                row.Name.text = (me ? "<b>" : "") + Name(cs, e.Slot) + (me ? " (you)</b>" : "") + (q ? string.Empty : $"  <size=12><color=#8C88A6>{Reason((ElimReason)(e.Arg != null && e.Arg.Length > 0 ? e.Arg[0] : 0))}</color></size>");
            }

            for (; k < _resultRows.Length; k++)
                if (_resultRows[k].Root.gameObject.activeSelf)
                    _resultRows[k].Root.gameObject.SetActive(false);

            if (!_resultShown && cs.TryGetSlotInfo(cs.MatchSlot, out var mine) && mine.Has(MatchSlotInfo.Rostered))
            {
                _resultShown = true;
                if (mine.Has(MatchSlotInfo.Alive))
                    _banner.Show(Mode() == "Survival" ? "ESCAPED!" : "QUALIFIED!", UiKit.Mint, 4f);
                else if (mine.EliminatedRound == m.Round)
                    _banner.Show("ELIMINATED", UiKit.Magenta, 4f);
            }
        }

        private void Leaderboard(ClientSim cs, MatchStateMessage m, TableRow[] table)
        {
            var rows = new List<MatchSlotInfo>();
            foreach (var s in m.Slots)
                if (s.Has(MatchSlotInfo.Rostered))
                    rows.Add(s);
            rows.Sort((a, b) =>
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
            for (var i = 0; i < table.Length; i++)
            {
                var t = table[i];
                var on = i < rows.Count;
                if (t.Root.gameObject.activeSelf != on)
                    t.Root.gameObject.SetActive(on);
                if (!on)
                    continue;
                var s = rows[i];
                var me = s.Slot == cs.MatchSlot;
                var alive = s.Has(MatchSlotInfo.Alive);
                t.Dot.color = alive ? UiKit.Mint : UiKit.Magenta;
                t.Name.text = (me ? "<b>" : "") + (s.Has(MatchSlotInfo.Bot) ? "<size=11><color=#7B2FF7>BOT</color></size> " : "") + s.Name + (me ? " (you)</b>" : "");
                t.R1.text = Place(s.Places[0]);
                t.R2.text = Place(s.Places[1]);
                t.R3.text = Place(s.Places[2]);
                t.R4.text = Place(s.Places[3]);
                t.Note.text = alive ? (m.Champion == s.Slot ? "♛ champion" : string.Empty) : $"out R{s.EliminatedRound}: {Reason(s.Reason)}";
            }
        }

        private static string Place(byte p) => p == 0 ? "·" : p.ToString();

        private void ShowIntermission(ClientSim cs, MatchStateMessage m, uint now)
        {
            _inter.gameObject.SetActive(true);
            Leaderboard(cs, m, _interRows);
            var next = m.Round;
            _interNext.text = next < m.RoundCount
                ? $"<b>Next:</b> round {next + 1}: {UiNames.Map(m.Maps[next])} ({MapPoolV1.ModeOf(m.Maps[next])}) · <color=#FF3EA5>{UiNames.Modifier(m.Modifiers[next])}</color> · {m.Targets[next]} advance · in {Secs((long)m.PhaseEndTick - now)} s"
                : string.Empty;
        }

        /// <summary>G3.6: the receipt is laid out for explorer links (Rollup tx, Solana tx, Match account); practice shows placeholders.</summary>
        private void ShowReceipt(ClientSim cs, MatchStateMessage m, uint now)
        {
            _receipt.gameObject.SetActive(true);
            _receiptHead.text = $"♛ Champion: {Name(cs, m.Champion)}";
            Leaderboard(cs, m, _receiptRows);
            _receiptIds.text = $"match {Sha256.Hex(m.MatchId).Substring(0, 16)}…   seed {Sha256.Hex(m.Seed).Substring(0, 16)}…\ntranscript {Sha256.Hex(m.Transcript).Substring(0, 16)}… ({m.EventCount} events)";
            var st = _chain.State;
            _receiptChain.text = $"<color=#{UiKit.HexOf(ChainChipStyle.Color(st))}>{ChainChipStyle.Text(st)}</color>  {(_chain.IsPractice ? _chain.Detail : string.Empty)}";
            var links = _chain.Receipt;
            _receiptLinks[0].text = Link("Rollup tx", links.RollupTx, links.RollupUrl);     // TODO(M6): ER transaction + explorer
            _receiptLinks[1].text = Link("Solana tx", links.SolanaTx, links.SolanaUrl);     // TODO(M6): commit / undelegate on Solana
            _receiptLinks[2].text = Link("Match account", links.MatchAccount, links.AccountUrl); // TODO(M6): the Match PDA
            _back.interactable = !_backSent;
            UiKit.SetButtonText(_back, _backSent ? $"Waiting… (lobby in {Secs((long)m.PhaseEndTick - now)} s)" : $"Back to lobby ({Secs((long)m.PhaseEndTick - now)} s)");
        }

        private static string Link(string label, string value, string url) =>
            $"<b>{label}</b>  " + (string.IsNullOrEmpty(value) ? "<color=#8C88A6>— (practice: not recorded)</color>" : $"<color=#3FA9F5>{value}</color>{(string.IsNullOrEmpty(url) ? "" : "  <size=11>" + url + "</size>")}");
    }
}
