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
    /// Race HUD and screens (M3 §5.5, greybox UiKit): round chip (map, variant, modifier), timer, QUALIFIED x/N, checkpoint
    /// ticks, live place, "3-2-1-RUSH!", QUALIFIED! / ELIMINATED banners, the end-of-round results list, and a spectate
    /// follow (Tab cycles) once you have finished. Driven by <c>round</c> / <c>rstate</c> / <c>evt</c> and the snapshot race
    /// bytes, so it is right after a reconnect. Reduced motion: no pulsing or scaling.
    /// </summary>
    public sealed class RaceHud : MonoBehaviour
    {
        public SandboxView View;

        private Text _chip, _timer, _qualified, _checkpoints, _place, _center, _banner, _results, _variantBanner;
        private GameObject _resultsPanel, _bannerPanel;
        private MapInfo _map;
        private RaceRules _course;
        private readonly Dictionary<int, int> _finalPlace = new Dictionary<int, int>();
        private readonly List<int> _timerQualified = new List<int>();
        private readonly StringBuilder _sb = new StringBuilder(512);
        private int _spectate = -1;
        private int _finishers;
        private float _bannerUntil, _variantUntil;
        private uint _lastRoundStart;

        private void Start()
        {
            _map = MapInfo.Current;
            if (_map != null)
                _course = new RaceRules(_map.CheckpointDefs(), _map.CourseLineMm(), 0);
            var canvas = UiKit.CreateCanvas("RaceHudCanvas");
            canvas.SetParent(transform, false);
            _chip = Corner(canvas, new Vector2(0f, 1f), new Vector2(16f, -300f), 22, TextAnchor.UpperLeft, UiKit.Grape);
            _timer = Corner(canvas, new Vector2(1f, 1f), new Vector2(-16f, -64f), 44, TextAnchor.UpperRight, UiKit.Ink);
            _qualified = Corner(canvas, new Vector2(0.5f, 1f), new Vector2(0f, -14f), 34, TextAnchor.UpperCenter, UiKit.Magenta);
            _checkpoints = Corner(canvas, new Vector2(0.5f, 1f), new Vector2(0f, -58f), 24, TextAnchor.UpperCenter, UiKit.Ink);
            _place = Corner(canvas, new Vector2(1f, 1f), new Vector2(-16f, -118f), 30, TextAnchor.UpperRight, UiKit.Grape);
            _center = Corner(canvas, new Vector2(0.5f, 0.5f), new Vector2(0f, 60f), 120, TextAnchor.MiddleCenter, UiKit.Magenta);
            _variantBanner = Corner(canvas, new Vector2(0.5f, 0.5f), new Vector2(0f, -60f), 34, TextAnchor.MiddleCenter, UiKit.Grape);

            var bp = UiKit.Panel(canvas, new Vector2(620f, 120f), UiKit.CloudWhite);
            bp.anchoredPosition = new Vector2(0f, 160f);
            _bannerPanel = bp.gameObject;
            _banner = UiKit.Label(bp, string.Empty, 64, UiKit.Mint, FontStyle.Bold, 90);
            _bannerPanel.SetActive(false);

            var rp = UiKit.Panel(canvas, new Vector2(560f, 560f), UiKit.CloudWhite);
            _resultsPanel = rp.gameObject;
            UiKit.Label(rp, "RESULTS", 40, UiKit.Grape, FontStyle.Bold, 56);
            _results = UiKit.Label(rp, string.Empty, 22, UiKit.Ink, FontStyle.Normal, 440);
            _results.alignment = TextAnchor.UpperLeft;
            _results.supportRichText = true;
            _resultsPanel.SetActive(false);

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

        private static Text Corner(Transform canvas, Vector2 anchor, Vector2 pos, int size, TextAnchor align, Color color)
        {
            var t = UiKit.Label(canvas, string.Empty, size, color, FontStyle.Bold);
            var rt = (RectTransform)t.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(900f, size * 1.4f);
            t.alignment = align;
            t.supportRichText = true;
            var outline = t.gameObject.AddComponent<Outline>();
            outline.effectColor = Color.white;
            outline.effectDistance = new Vector2(2f, -2f);
            return t;
        }

        private void OnEvent(RaceEvent e)
        {
            var cs = ClientSim.Instance;
            switch (e.Type)
            {
                case RaceEventType.Phase when (RoundPhase)e.Arg == RoundPhase.Intro:
                    _finalPlace.Clear();
                    _timerQualified.Clear();
                    _finishers = 0;
                    _resultsPanel.SetActive(false);
                    _spectate = -1;
                    _variantUntil = Time.unscaledTime + 4f;
                    break;
                case RaceEventType.Finished:
                    _finalPlace[e.Slot] = e.Arg;
                    _finishers++;
                    break;
                case RaceEventType.Qualified when !_finalPlace.ContainsKey(e.Slot):
                    _timerQualified.Add(e.Slot);
                    break;
                case RaceEventType.Eliminated:
                    _finalPlace[e.Slot] = e.Arg;
                    if (cs != null && e.Slot == cs.OwnSlot)
                        Banner("ELIMINATED", UiKit.Magenta);
                    break;
            }

            if (e.Type == RaceEventType.Qualified && cs != null && e.Slot == cs.OwnSlot)
                Banner("QUALIFIED!", UiKit.Mint);
            if (e.Type == RaceEventType.Checkpoint && cs != null && e.Slot == cs.OwnSlot)
                Banner($"CHECKPOINT {e.Arg}", UiKit.Mint, 1.0f);
        }

        private void Banner(string text, Color c, float secs = 2.5f)
        {
            _banner.text = text;
            _banner.color = c;
            _bannerPanel.SetActive(true);
            _bannerUntil = Time.unscaledTime + secs;
        }

        private void Update()
        {
            var cs = ClientSim.Instance;
            if (cs == null || !cs.Round.HasValue)
                return;
            var r = cs.Round.Value;
            if (r.RoundStartTick != _lastRoundStart)
            {
                _lastRoundStart = r.RoundStartTick;
                _variantUntil = Time.unscaledTime + 4f;
            }

            var phase = cs.Phase;
            var tick = cs.LocalTick;
            var serverTick = cs.HasSnapshots ? cs.Newest.Tick : 0u;
            var mod = ModifierEffects.Get(r.Modifier);
            var reduced = GameSettings.ReducedMotion;

            _chip.text = $"BLOCK PARTY DASH  ·  variant {(r.Variant == 0 ? "A" : "B")}  ·  {(mod.Id == 0 ? "no modifier" : mod.Key.ToUpperInvariant())}";
            var st = cs.HasRaceState ? cs.RaceState : default;
            var qualified = 0;
            var ownRace = cs.OwnRace;
            if (cs.HasSnapshots)
            {
                ref readonly var n = ref cs.Newest;
                for (var i = 0; i < n.Count; i++)
                    if ((n.Players[i].Race & PlayerState.RaceQualified) != 0)
                        qualified++;
            }

            _qualified.text = phase == RoundPhase.Waiting ? "WAITING FOR PLAYERS" : $"QUALIFIED {qualified}/{(r.QualifyCount > 0 ? r.QualifyCount : st.QualifyCount)}";
            var total = _map != null ? _map.checkpoints.Length : 4;
            var cp = ownRace & PlayerState.RaceCheckpointMask;
            _sb.Clear();
            for (var i = 1; i <= total; i++)
                _sb.Append(i <= cp ? "<color=#5BE3B5>■</color>" : "□").Append(i < total ? " " : "");
            _checkpoints.text = $"{_sb}  CP {Mathf.Min(cp, total - 1)}/{total - 1}";

            // Timer: time left to the cap after RUSH.
            if (phase == RoundPhase.Rush && st.RushEndTick > 0)
            {
                var left = Mathf.Max(0f, (st.RushEndTick - serverTick) / 30f);
                _timer.text = $"{(int)left / 60}:{(int)left % 60:00}";
                _timer.color = left < 10f ? UiKit.Magenta : UiKit.Ink;
            }
            else
            {
                _timer.text = string.Empty;
            }

            // Countdown 3-2-1-RUSH! on the local timeline (when our inputs start to count).
            var toRush = (r.RoundStartTick - tick) / 30.0;
            if ((phase == RoundPhase.Intro || phase == RoundPhase.Countdown) && toRush > 0)
            {
                _center.text = toRush > 3.0 ? "GET READY" : Mathf.CeilToInt((float)toRush).ToString();
                _center.transform.localScale = reduced ? Vector3.one : Vector3.one * (1f + 0.25f * (float)(toRush - System.Math.Floor(toRush)));
            }
            else if (phase == RoundPhase.Rush && tick - r.RoundStartTick < 30)
            {
                _center.text = "RUSH!";
                _center.transform.localScale = Vector3.one;
            }
            else
            {
                _center.text = phase == RoundPhase.Waiting ? "Start gate opens when the round starts" : string.Empty;
                _center.transform.localScale = Vector3.one * 0.3f;
            }

            _variantBanner.text = Time.unscaledTime < _variantUntil && phase != RoundPhase.Ended
                ? $"VARIANT {(r.Variant == 0 ? "A" : "B")}  ·  CHAOS CAPSULE: {(mod.Id == 0 ? "NONE" : mod.Key.ToUpperInvariant())}"
                : string.Empty;

            // Live place: finished place, else ranked by (checkpoint ↓, distance to the next checkpoint ↑).
            _place.text = phase == RoundPhase.Rush || phase == RoundPhase.Ended ? $"PLACE {LivePlace(cs)}/{Mathf.Max(1, (int)r.PlayerCount)}" : string.Empty;

            if (_bannerPanel.activeSelf && Time.unscaledTime > _bannerUntil)
                _bannerPanel.SetActive(false);

            var showResults = phase == RoundPhase.Ended;
            if (_resultsPanel.activeSelf != showResults)
                _resultsPanel.SetActive(showResults);
            if (showResults)
                _results.text = ResultsText(cs);

            // Spectate once finished (Tab cycles).
            if (cs.OwnOutOfSim && View != null)
            {
                var kb = Keyboard.current;
                if (_spectate < 0 || (kb != null && kb.tabKey.wasPressedThisFrame))
                    _spectate = NextSpectate(cs, _spectate);
                View.FollowSlot = _spectate;
            }
            else if (View != null)
            {
                View.FollowSlot = -1;
                _spectate = -1;
            }
        }

        private int NextSpectate(ClientSim cs, int from)
        {
            if (!cs.HasSnapshots)
                return -1;
            ref readonly var n = ref cs.Newest;
            for (var k = 1; k <= Protocol.MaxPlayers; k++)
            {
                var slot = (from + k + Protocol.MaxPlayers) % Protocol.MaxPlayers;
                if (slot == cs.OwnSlot)
                    continue;
                for (var i = 0; i < n.Count; i++)
                    if (n.Players[i].Slot == slot && (n.Players[i].Race & PlayerState.RaceFinished) == 0)
                        return slot;
            }

            return -1;
        }

        private int LivePlace(ClientSim cs)
        {
            if (!cs.HasSnapshots || _course == null)
                return 0;
            if (_finalPlace.TryGetValue(cs.OwnSlot, out var fp))
                return fp;
            ref readonly var n = ref cs.Newest;
            int ownCp = -1;
            float ownDist = 0f;
            var finished = 0;
            for (var i = 0; i < n.Count; i++)
            {
                if ((n.Players[i].Race & PlayerState.RaceFinished) != 0)
                    finished++;
                if (n.Players[i].Slot != cs.OwnSlot)
                    continue;
                ownCp = n.Players[i].Race & PlayerState.RaceCheckpointMask;
                ownDist = Remaining(in n.Players[i]);
            }

            if (ownCp < 0)
                return 0;
            var ahead = 0;
            for (var i = 0; i < n.Count; i++)
            {
                ref readonly var p = ref n.Players[i];
                if (p.Slot == cs.OwnSlot || (p.Race & PlayerState.RaceFinished) != 0)
                    continue;
                var c = p.Race & PlayerState.RaceCheckpointMask;
                if (c > ownCp || (c == ownCp && Remaining(in p) < ownDist))
                    ahead++;
            }

            return finished + ahead + 1;
        }

        private float Remaining(in PlayerState p)
        {
            var s = new MotorState { PosX = p.PosX, PosY = p.PosY, PosZ = p.PosZ };
            return _course.RemainingToNext(p.Race & PlayerState.RaceCheckpointMask, in s);
        }

        private string ResultsText(ClientSim cs)
        {
            var rows = new List<(int place, int slot)>();
            foreach (var kv in _finalPlace)
                rows.Add((kv.Value, kv.Key));
            // Timer qualifiers that did not finish: right after the finishers, in the server's ranking order.
            for (var i = 0; i < _timerQualified.Count; i++)
                if (!_finalPlace.ContainsKey(_timerQualified[i]))
                    rows.Add((_finishers + i + 1, _timerQualified[i]));
            rows.Sort((a, b) => a.place.CompareTo(b.place));
            _sb.Clear();
            var bots = new HashSet<int>();
            var race = new Dictionary<int, byte>();
            if (cs.HasSnapshots)
            {
                ref readonly var n = ref cs.Newest;
                for (var i = 0; i < n.Count; i++)
                {
                    if ((n.Players[i].Flags & MotorState.Bot) != 0)
                        bots.Add(n.Players[i].Slot);
                    race[n.Players[i].Slot] = n.Players[i].Race;
                }
            }

            foreach (var (place, slot) in rows)
            {
                var q = race.TryGetValue(slot, out var rb) && (rb & PlayerState.RaceQualified) != 0;
                var name = slot == cs.OwnSlot ? "<b>YOU</b>" : $"P{slot + 1}";
                _sb.Append($"{place,2}.  {name,-8} {(bots.Contains(slot) ? "BOT " : "    ")}  {(q ? "<color=#5BE3B5>QUALIFIED</color>" : "<color=#FF3EA5>eliminated</color>")}\n");
            }

            return _sb.ToString();
        }
    }
}
