using System.Collections.Generic;
using System.Text;
using FallRush.Net;
using FallRush.Shared;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace FallRush.Client
{
    /// <summary>
    /// Every match screen in greybox (M4 §3.6, doc 17 §5 layout), driven only by server state (<c>match</c>, <c>mevt</c>,
    /// <c>round</c>, <c>rstate</c>, <c>evt</c>, snapshots), so it is right after a reconnect or a late join:
    /// lobby (player list, BOT tags, connection dots, countdown, host Start, "Bots will fill empty slots") → deal (spinning
    /// capsules → the four rounds, "Practice deal (local seed)", seed) → round intro (map, mode, modifier chip, goal line,
    /// fly-through, 3-2-1-RUSH) → HUD (round + modifier top-left, timer top-right, the mode counter top-centre,
    /// ping / reconnecting bottom-left, chain chip bottom-right) → round result (QUALIFIED / ELIMINATED per player) →
    /// intermission (leaderboard, next round, Fan Picks hook) → champion (podium, crown) → receipt → back to the lobby.
    /// Spectating (eliminated, out, finished, viewer): the camera follows alive players, Tab cycles. Subtitles for cues.
    /// Replaces M3's <c>RaceHud</c>.
    /// </summary>
    public sealed class MatchHud : MonoBehaviour
    {
        public SandboxView View;

        private Text _chip, _timer, _counter, _conn, _chain, _center, _sub, _spectate, _banner;
        private RectTransform _lobby, _deal, _results, _inter, _receipt, _bannerPanel;
        private Text _lobbyText, _dealText, _resultsText, _interText, _receiptText, _lobbyTitle;
        private Button _start, _back;
        private MapInfo _map;
        private ReactiveViews _rv;
        private ChampionPodium _podium;
        private readonly StringBuilder _sb = new StringBuilder(2048);
        private float _next;
        private float _bannerUntil;
        private int _spectateSlot = -1;
        private bool _backSent;
        private float _introStart = -1f;
        private RaceRules _course;

        private void Start()
        {
            _map = MapInfo.Current;
            if (_map != null && _map.definition != null && _map.definition.IsRace)
                _course = new RaceRules(_map.CheckpointDefs(), _map.CourseLineMm(), 0);
            _rv = gameObject.AddComponent<ReactiveViews>();
            var canvas = UiKit.CreateCanvas("MatchHudCanvas");
            canvas.SetParent(transform, false);
            _chip = Corner(canvas, new Vector2(0f, 1f), new Vector2(16f, -14f), 22, TextAnchor.UpperLeft, UiKit.Grape, 620f);
            _timer = Corner(canvas, new Vector2(1f, 1f), new Vector2(-16f, -12f), 46, TextAnchor.UpperRight, UiKit.Ink, 300f);
            _counter = Corner(canvas, new Vector2(0.5f, 1f), new Vector2(0f, -10f), 28, TextAnchor.UpperCenter, UiKit.Magenta, 700f);
            _conn = Corner(canvas, new Vector2(0f, 0f), new Vector2(16f, 40f), 18, TextAnchor.LowerLeft, UiKit.Ink, 520f);
            _chain = Corner(canvas, new Vector2(1f, 0f), new Vector2(-16f, 40f), 20, TextAnchor.LowerRight, new Color(0.45f, 0.45f, 0.5f), 360f);
            _center = Corner(canvas, new Vector2(0.5f, 0.5f), new Vector2(0f, 70f), 110, TextAnchor.MiddleCenter, UiKit.Magenta, 1100f);
            _sub = Corner(canvas, new Vector2(0.5f, 0f), new Vector2(0f, 74f), 30, TextAnchor.LowerCenter, UiKit.Grape, 1000f);
            _spectate = Corner(canvas, new Vector2(0.5f, 0f), new Vector2(0f, 118f), 22, TextAnchor.LowerCenter, UiKit.Ink, 1000f);
            _chain.text = "● Server"; // TODO(M6): ◆ Rollup ✓ / ⬢ Solana ✓ / ! Pending from evt.chainStatus

            _bannerPanel = UiKit.Panel(canvas, new Vector2(640f, 120f), UiKit.CloudWhite);
            _bannerPanel.anchoredPosition = new Vector2(0f, 170f);
            _banner = UiKit.Label(_bannerPanel, string.Empty, 60, UiKit.Mint, FontStyle.Bold, 90);
            _bannerPanel.gameObject.SetActive(false);

            _lobby = UiKit.Panel(canvas, new Vector2(430f, 600f), UiKit.CloudWhite);
            _lobby.anchorMin = _lobby.anchorMax = _lobby.pivot = new Vector2(1f, 0.5f);
            _lobby.anchoredPosition = new Vector2(-16f, 0f);
            _lobbyTitle = UiKit.Label(_lobby, "LOBBY", 34, UiKit.Grape, FontStyle.Bold, 44);
            _lobbyText = Body(_lobby, 20, 400);
            _start = UiKit.Button(_lobby, "Start now (host)", UiKit.SunYellow, () => ClientSim.Instance?.SendControl(ControlCmd.Start));

            _deal = UiKit.Panel(canvas, new Vector2(860f, 470f), UiKit.CloudWhite);
            UiKit.Label(_deal, "THE DEAL", 44, UiKit.Grape, FontStyle.Bold, 56);
            _dealText = Body(_deal, 24, 340);
            _dealText.alignment = TextAnchor.UpperCenter;

            _results = UiKit.Panel(canvas, new Vector2(620f, 600f), UiKit.CloudWhite);
            UiKit.Label(_results, "ROUND RESULT", 38, UiKit.Grape, FontStyle.Bold, 50);
            _resultsText = Body(_results, 20, 480);

            _inter = UiKit.Panel(canvas, new Vector2(760f, 620f), UiKit.CloudWhite);
            UiKit.Label(_inter, "INTERMISSION", 38, UiKit.Grape, FontStyle.Bold, 50);
            _interText = Body(_inter, 19, 460);
            var picks = UiKit.Button(_inter, "Fan Picks — coming soon", UiKit.SkyLight, () => { });
            picks.interactable = false; // TODO(P1): Fan Picks (doc 01 §10) — eliminated players pick a winner here

            _receipt = UiKit.Panel(canvas, new Vector2(820f, 660f), UiKit.CloudWhite);
            UiKit.Label(_receipt, "MATCH RECEIPT", 38, UiKit.Grape, FontStyle.Bold, 50);
            _receiptText = Body(_receipt, 18, 470);
            _back = UiKit.Button(_receipt, "Back to lobby", UiKit.Mint, () =>
            {
                _backSent = true;
                ClientSim.Instance?.SendControl(ControlCmd.Back);
            });

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

        private static Text Corner(Transform canvas, Vector2 anchor, Vector2 pos, int size, TextAnchor align, Color color, float width)
        {
            var t = UiKit.Label(canvas, string.Empty, size, color, FontStyle.Bold);
            var rt = (RectTransform)t.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(width, size * 2.6f);
            t.alignment = align;
            t.supportRichText = true;
            // G1: a white outline keeps HUD text readable over busy world (signs, sky, other players).
            var o = t.gameObject.AddComponent<Outline>();
            o.effectColor = new Color(1f, 1f, 1f, 0.9f);
            o.effectDistance = new Vector2(2f, -2f);
            return t;
        }

        private static Text Body(Transform panel, int size, int minHeight)
        {
            var t = UiKit.Label(panel, string.Empty, size, UiKit.Ink, FontStyle.Normal, minHeight);
            t.alignment = TextAnchor.UpperLeft;
            t.supportRichText = true;
            return t;
        }

        // ------------------------------------------------------------------ gameplay events → banners

        private void OnEvent(RaceEvent e)
        {
            var cs = ClientSim.Instance;
            if (cs == null || e.Slot != cs.OwnSlot)
                return;
            switch (e.Type)
            {
                case RaceEventType.Qualified:
                    Banner(Mode() == "Survival" ? "ESCAPED!" : "QUALIFIED!", UiKit.Mint);
                    break;
                case RaceEventType.Eliminated:
                    Banner("ELIMINATED", UiKit.Magenta);
                    break;
                case RaceEventType.Out:
                    Banner((ElimReason)e.Arg == ElimReason.Goo ? "GOOED!" : "OUT!", UiKit.Magenta);
                    break;
                case RaceEventType.Finished when Mode() == "Survival":
                    Banner("ESCAPED!", UiKit.Mint);
                    break;
                case RaceEventType.Dropped:
                    Banner("BONK! −1 star", UiKit.SunYellow, 1.2f);
                    break;
            }
        }

        private void Banner(string text, Color c, float secs = 2.2f)
        {
            _banner.text = text;
            _banner.color = c;
            _bannerUntil = Time.unscaledTime + secs;
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
            _bannerPanel.gameObject.SetActive(Time.unscaledTime < _bannerUntil);
            if (Time.unscaledTime < _next)
                return;
            _next = Time.unscaledTime + 0.1f;
            Refresh(cs);
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
            // Champion: look at the podium.
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
                var start = MapOverview() ;
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
                return (s.Has(MatchSlotInfo.Bot) ? "BOT " : "") + s.Name;
            return $"P{slot + 1}";
        }

        private static string Reason(ElimReason r) => r switch
        {
            ElimReason.Goo => "goo",
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

        private void Refresh(ClientSim cs)
        {
            var m = cs.Match;
            var boot = NetBootstrap.Instance;
            var now = ServerTickNow(cs);
            var ping = PingService.Instance;
            // Bottom-left: connection.
            if (boot != null && boot.Reconnecting)
                _conn.text = $"<color=#FF3EA5>Reconnecting… {Mathf.CeilToInt(boot.ReconnectSecondsLeft)} s</color>";
            else if (boot != null && boot.State == ClientState.Disconnected)
                _conn.text = "<color=#FF3EA5>Disconnected</color>";
            else
                _conn.text = $"<color=#5BE3B5>●</color> ping {(ping != null && ping.SampleCount > 0 ? ping.P50Ms.ToString("F0") : "…")} ms{(cs.Remote.Lagging ? "  <color=#FF3EA5>LAGGING</color>" : "")}";

            foreach (var p in new[] { _lobby, _deal, _results, _inter, _receipt })
                p.gameObject.SetActive(false);
            _center.text = string.Empty;
            _sub.text = string.Empty;
            _counter.text = string.Empty;
            _counter.color = UiKit.Magenta;
            _timer.text = string.Empty;
            _spectate.text = string.Empty;
            if (m == null)
            {
                _chip.text = cs.Round.HasValue ? UiNames.Map(cs.Round.Value.MapId) : string.Empty;
                return;
            }

            var mode = Mode();
            var round = cs.Round;
            _chip.text = m.Phase >= MatchPhase.RoundIntro && m.Phase <= MatchPhase.Intermission && round.HasValue
                ? $"ROUND {m.Round}/{m.RoundCount} · {UiNames.Map(round.Value.MapId)} · {mode}\n<color=#FF3EA5>[{UiNames.Modifier(round.Value.Modifier)}]</color>"
                : m.Phase <= MatchPhase.Countdown ? "PRACTICE LOBBY" : "PRACTICE MATCH";

            switch (m.Phase)
            {
                case MatchPhase.LobbyOpen:
                case MatchPhase.Countdown:
                    ShowLobby(cs, m, now);
                    break;
                case MatchPhase.RosterLocked:
                case MatchPhase.Dealing:
                case MatchPhase.Dealt:
                    ShowDeal(cs, m, now);
                    break;
                case MatchPhase.RoundIntro:
                case MatchPhase.RoundActive:
                    ShowRound(cs, m, mode, now);
                    break;
                case MatchPhase.RoundResult:
                    ShowResults(cs, m);
                    break;
                case MatchPhase.Intermission:
                    ShowIntermission(cs, m, now);
                    break;
                case MatchPhase.Champion:
                case MatchPhase.Finalizing:
                    // G1: the name sits at the top (it covered the podium Rushling in the centre).
                    _counter.text = $"<size=30>CHAMPION</size>\n<size=52>{Name(cs, m.Champion)}</size>";
                    _counter.color = UiKit.SunYellow;
                    if (m.Champion == cs.MatchSlot)
                        _sub.text = "You won the crown!";
                    break;
                case MatchPhase.Done:
                    ShowReceipt(cs, m, now);
                    break;
                case MatchPhase.Aborted:
                    _center.text = "<size=54>MATCH ABORTED</size>";
                    _center.color = UiKit.Magenta;
                    _sub.text = "Back to the lobby in a moment.";
                    break;
            }

            if (m.Phase >= MatchPhase.RoundIntro && m.Phase <= MatchPhase.Intermission && (cs.MatchSlot == 255 || Spectating(cs)))
            {
                var mine = cs.MatchSlot != 255 && cs.TryGetSlotInfo(cs.MatchSlot, out var info) ? info : default;
                var why = cs.MatchSlot == 255 ? "Match in progress: you join the next one" :
                    mine.Has(MatchSlotInfo.GraceExpired) ? "You were eliminated (disconnected)" :
                    !mine.Has(MatchSlotInfo.Alive) ? "Eliminated" : (mine.LateMask & (1 << (m.Round - 1))) != 0 ? "Map loaded too late: you sit this round out (still in)" : "Waiting";
                _spectate.text = $"SPECTATING {(_spectateSlot >= 0 ? Name(cs, _spectateSlot) : "…")} — Tab: next · {why}";
            }
        }

        private void ShowLobby(ClientSim cs, MatchStateMessage m, uint now)
        {
            _lobby.gameObject.SetActive(true);
            _sb.Clear();
            var humans = 0;
            foreach (var s in m.Slots)
            {
                var bot = s.Has(MatchSlotInfo.Bot);
                if (!bot)
                    humans++;
                _sb.Append(s.Has(MatchSlotInfo.Connected) ? "<color=#3FBF8F>●</color> " : "<color=#FF3EA5>○</color> ")
                    .Append(s.Slot == cs.MatchSlot ? "<b>" : "").Append(bot ? "BOT " : "").Append(s.Name).Append(s.Slot == cs.MatchSlot ? " (you)</b>" : "")
                    .Append(s.Has(MatchSlotInfo.Host) ? "  <color=#7B2FF7>host</color>" : "").Append('\n');
            }

            _sb.Append($"\n{humans} / {Protocol.MaxPlayers} players. <b>Bots will fill empty slots.</b>\n");
            if (m.Phase == MatchPhase.Countdown)
                _sb.Append($"\n<size=30><color=#FF3EA5>Starting in {Secs((long)m.PhaseEndTick - now)}</color></size>");
            else
                _sb.Append("\nWaiting for a player…");
            _lobbyText.text = _sb.ToString();
            _sb.Append("\n\n<size=17><b>Controls</b>  WASD move · Space jump · Shift / left click dive (jump right after landing to skip the slide) · mouse look · <b>1-4 emotes</b> · Esc settings</size>");
            _lobbyText.text = _sb.ToString();
            _start.gameObject.SetActive(cs.MatchSlot != 255 && m.HostSlot == cs.MatchSlot);
            _counter.text = "Run, jump and bump while you wait";
        }

        private void ShowDeal(ClientSim cs, MatchStateMessage m, uint now)
        {
            _deal.gameObject.SetActive(true);
            _sb.Clear();
            _sb.Append("<b>Practice deal (local seed)</b> — the same deal algorithm the match will run on verified VRF (M6)\n\n");
            if (m.Phase != MatchPhase.Dealt)
            {
                var spin = "◐◓◑◒"[(int)(Time.unscaledTime * 6f) % 4];
                _sb.Append($"<size=60>{spin}  {spin}  {spin}  {spin}</size>\n\nThe Chaos Capsule machine is spinning…");
            }
            else
            {
                for (var r = 0; r < m.RoundCount; r++)
                {
                    var map = m.Maps[r];
                    _sb.Append($"<b>R{r + 1}</b>  {UiNames.Map(map)} · {MapPoolV1.ModeOf(map)} · <color=#FF3EA5>{UiNames.Modifier(m.Modifiers[r])}</color>  → {m.Targets[r]} advance\n");
                }

                _sb.Append($"\n{m.PlayerCount} players · targets {string.Join(" → ", m.Targets)}");
            }

            _sb.Append($"\n\nseed <size=16>{Sha256.Hex(m.Seed)}</size>\nmatch {Sha256.Hex(m.MatchId).Substring(0, 16)}…");
            _dealText.text = _sb.ToString();
        }

        private void ShowRound(ClientSim cs, MatchStateMessage m, string mode, uint now)
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
                var toRush = (long)round.Value.RoundStartTick - now;
                _center.color = UiKit.Magenta;
                _center.text = rs.Phase == RoundPhase.Intro
                    ? $"<size=58>{UiNames.Map(round.Value.MapId)}</size>\n<size=34>{mode}</size>"
                    : toRush > 60 ? "3" : toRush > 30 ? "2" : toRush > 0 ? "1" : "RUSH!";
                var modHint = UiNames.ModifierHint(round.Value.Modifier);
                _sub.text = ModeGoal(mode, n) + $"\n<size=20>{ModeTip(mode)}{(modHint.Length > 0 ? $"  ·  <color=#FF3EA5>{UiNames.Modifier(round.Value.Modifier)}: {modHint}</color>" : "")}</size>";
                return;
            }

            if (cs.HasRaceState && rs.Phase == RoundPhase.Rush && round.HasValue && now < round.Value.RoundStartTick + 20)
            {
                _center.text = "RUSH!";
                _center.color = UiKit.Mint;
            }

            // Top-right timer.
            if (cs.HasRaceState && rs.RushEndTick > 0)
            {
                var left = (long)rs.RushEndTick - now;
                _timer.text = $"{Secs(left)}";
                _timer.color = left < 300 ? UiKit.Magenta : UiKit.Ink;
            }

            // Top-centre counter per mode.
            switch (mode)
            {
                case "Race":
                    _counter.text = $"QUALIFIED {rs.QualifiedCount} / {rs.QualifyCount}" + RaceExtra(cs);
                    break;
                case "Survival":
                    _counter.text = $"ESCAPED {rs.FinisherCount} / {rs.QualifyCount} · ALIVE {rs.QualifiedCount}";
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

                    _counter.text = $"STARS {mine}  (best {best}) · top {rs.QualifyCount} qualify";
                    break;
                }
                case "Final":
                    _counter.text = $"ALIVE {rs.FinisherCount}";
                    break;
            }

            // Subtitles for cues (accessibility).
            if (_rv != null)
            {
                if (_rv.GooWarning)
                    _sub.text = "Goo rising!";
                else if (_rv.SpinnersSoon)
                    _sub.text = "Spinners incoming!";
                else if (_rv.TilesCrumbling && mode == "Final" && now < round.GetValueOrDefault().RoundStartTick + 150)
                    _sub.text = "Tiles crumbling!";
            }
        }

        private string RaceExtra(ClientSim cs)
        {
            var cp = cs.OwnRace & PlayerState.RaceCheckpointMask;
            var total = _course != null ? _course.Checkpoints.Length : 0;
            return total > 0 && !cs.OwnOutOfSim ? $"\n<size=22><color=#2A1250>checkpoint {cp}/{total}</color></size>" : string.Empty;
        }

        private void RoundRows(ClientSim cs, int round, StringBuilder sb)
        {
            var place = 0;
            foreach (var e in cs.MatchEvents)
            {
                if (e.Round != round || (e.Tag != Transcript.Qualified && e.Tag != Transcript.Eliminated))
                    continue;
                place++;
                var q = e.Tag == Transcript.Qualified;
                sb.Append(e.Slot == cs.MatchSlot ? "<b>" : "")
                    .Append(q ? "<color=#3FBF8F>QUALIFIED</color>  " : "<color=#FF3EA5>ELIMINATED</color> ")
                    .Append(Name(cs, e.Slot))
                    .Append(q ? string.Empty : $"  <size=15>({Reason((ElimReason)(e.Arg != null && e.Arg.Length > 0 ? e.Arg[0] : 0))})</size>")
                    .Append(e.Slot == cs.MatchSlot ? "</b>" : "").Append('\n');
            }
        }

        private void ShowResults(ClientSim cs, MatchStateMessage m)
        {
            _results.gameObject.SetActive(true);
            _sb.Clear();
            RoundRows(cs, m.Round, _sb);
            _resultsText.text = _sb.ToString();
            if (cs.TryGetSlotInfo(cs.MatchSlot, out var me) && me.Has(MatchSlotInfo.Rostered))
            {
                _center.text = me.Has(MatchSlotInfo.Alive) ? "QUALIFIED!" : me.EliminatedRound == m.Round ? "ELIMINATED" : string.Empty;
                _center.color = me.Has(MatchSlotInfo.Alive) ? UiKit.Mint : UiKit.Magenta;
            }
        }

        private void Leaderboard(ClientSim cs, MatchStateMessage m, StringBuilder sb)
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
            sb.Append("<b>Player                         R1  R2  R3  R4</b>\n");
            foreach (var s in rows)
            {
                sb.Append(s.Slot == cs.MatchSlot ? "<b>" : "").Append(s.Has(MatchSlotInfo.Alive) ? "<color=#3FBF8F>●</color> " : "<color=#FF3EA5>×</color> ")
                    .Append(((s.Has(MatchSlotInfo.Bot) ? "BOT " : "") + s.Name).PadRight(26));
                for (var r = 0; r < 4; r++)
                    sb.Append(s.Places[r] == 0 ? "  · " : $"{s.Places[r],3} ");
                sb.Append(s.Has(MatchSlotInfo.Alive) ? "" : $"  <size=14>out R{s.EliminatedRound}: {Reason(s.Reason)}</size>").Append(s.Slot == cs.MatchSlot ? "</b>" : "").Append('\n');
            }
        }

        private void ShowIntermission(ClientSim cs, MatchStateMessage m, uint now)
        {
            _inter.gameObject.SetActive(true);
            _sb.Clear();
            Leaderboard(cs, m, _sb);
            var next = m.Round;
            if (next < m.RoundCount)
            {
                var map = m.Maps[next];
                _sb.Append($"\n<b>Next:</b> round {next + 1} — {UiNames.Map(map)} ({MapPoolV1.ModeOf(map)}) · {UiNames.Modifier(m.Modifiers[next])} · {m.Targets[next]} advance  ·  in {Secs((long)m.PhaseEndTick - now)} s");
            }

            _interText.text = _sb.ToString();
        }

        private void ShowReceipt(ClientSim cs, MatchStateMessage m, uint now)
        {
            _receipt.gameObject.SetActive(true);
            _sb.Clear();
            _sb.Append($"<b>Champion: {Name(cs, m.Champion)}</b>\n\n");
            Leaderboard(cs, m, _sb);
            _sb.Append($"\nmatch {Sha256.Hex(m.MatchId).Substring(0, 16)}…   seed {Sha256.Hex(m.Seed).Substring(0, 16)}…\ntranscript {Sha256.Hex(m.Transcript).Substring(0, 16)}… ({m.EventCount} events)\n");
            _sb.Append("<color=#7B2FF7>Chain: not recorded (practice)</color>   <size=15>TODO(M6): Rollup ✓ / Solana ✓ + explorer links</size>\n");
            _sb.Append(_backSent ? "\nWaiting for the others… (lobby in " + Secs((long)m.PhaseEndTick - now) + " s)" : "\nBack to the lobby automatically in " + Secs((long)m.PhaseEndTick - now) + " s");
            _receiptText.text = _sb.ToString();
            _back.interactable = !_backSent;
        }
    }
}
