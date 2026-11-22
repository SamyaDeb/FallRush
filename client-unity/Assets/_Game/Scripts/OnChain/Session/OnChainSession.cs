using System;
using System.Collections.Generic;
using System.Text;
using FallRush.Client;
using FallRush.Net;
using FallRush.Shared;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace FallRush.OnChain
{
    /// <summary>
    /// P3-D: on-chain play. Joins an arena (the local chain emulator, P2's local stack or devnet-as), loads its map,
    /// and drives the Classic screens through <see cref="ClientSim.Feed"/> (<see cref="IChainFeed"/>): every World →
    /// snapshot (remote players interpolated 100–150 ms by <see cref="Interpolation"/>), round, events, match state; the
    /// own player is predicted on rush-core (<see cref="OnChainPrediction"/>) with inputs stamped T+3 in batches of 3.
    /// The chain chip reads <see cref="OnChainChainAdapter"/>. F3 (with the netcode HUD) shows tick health.
    /// Entry: menu "Play On-Chain (preview)", <c>?onchain=emu</c> / <c>-fallrushOnchain emu</c>, or the Classic →
    /// on-chain switch at the Final (<see cref="StartFinal"/>).
    /// </summary>
    [DefaultExecutionOrder(-60)] // before ClientSim (-50): its Remote.BeginFrame sees this frame's Worlds
    public sealed class OnChainSession : MonoBehaviour, IChainFeed
    {
        public static OnChainSession Instance { get; private set; }

        public ChainConfig Cfg;
        /// <summary>Switch mode: the Classic match this Final continues (names, places), the slot to take, the arena.</summary>
        public MatchStateMessage BaseMatch;
        public int WantSlot = -1;
        public bool Spectate;

        private IRushCore _core;
        private IChainTransport _tx;
        private OnChainClock _clock;
        private OnChainPrediction _pred;
        private readonly OnChainMatchFeed _feed = new OnChainMatchFeed();
        private readonly OnChainChainAdapter _adapter = new OnChainChainAdapter();
        private readonly List<WorldMsg> _worlds = new List<WorldMsg>(64);
        private readonly List<InputConfirm> _confirms = new List<InputConfirm>(64);
        private byte[] _map;
        private string _mapKey;
        private bool _sceneRequested;
        private byte[] _newest = new byte[4096];
        private int _newestLen;
        private uint _newestTick;
        private bool _hasNewest;
        private int _ownSlot = 255;
        private uint _arenaKey;
        private double _frac;
        private string _status = "connecting …";
        private bool _panel;
        private float _matchFedAt;
        private MatchPhase _lastMatchPhase = (MatchPhase)255;

        // input batches (wire-v0 §2)
        private readonly byte[] _batch = new byte[6 + 8 * 5];
        private int _pending;
        private uint _pendingFirst;

        // stats
        public long Updates, Batches, BatchesRejected, Resyncs, Lagged;
        private long _updatesMark, _batchesMark;
        public float UpdatesPerSec, BatchesPerSec, ConfirmP50, ConfirmP95;
        private double _statsAt;
        private readonly float[] _confirmMs = new float[128];
        private int _confirmN;
        private double _lastLagAt = -1;

        public IChainTransport Transport => _tx;
        public OnChainPrediction Prediction => _pred;
        public OnChainClock Clock => _clock;
        public string Status => _status;
        public int OwnSlot => _ownSlot;
        public uint NewestTick => _newestTick;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            OnChainEntry.Play = () => Begin(ChainConfig.FromArgs());
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            OnSceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
        }

        private static bool _autoStarted;

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // ?onchain=emu|local|devnet (-fallrushOnchain …): straight into on-chain play from the menu.
            if (_autoStarted || Instance != null || string.IsNullOrEmpty(ChainConfig.TargetArg()) || RcSelfTest.Requested)
                return;
            if (scene.name.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) < 0)
                return;
            _autoStarted = true;
            Begin(ChainConfig.FromArgs());
        }

        /// <summary>Starts a standalone on-chain session (menu entry / URL arg).</summary>
        public static OnChainSession Begin(ChainConfig cfg)
        {
            if (Instance != null)
                return Instance;
            var go = new GameObject("OnChainSession");
            DontDestroyOnLoad(go);
            var s = go.AddComponent<OnChainSession>();
            s.Cfg = cfg;
            return s;
        }

        /// <summary>The Classic → on-chain switch (task F): continue <paramref name="classic"/> with its Final on the arena.</summary>
        public static OnChainSession StartFinal(ChainConfig cfg, MatchStateMessage classic, int slot)
        {
            var s = Begin(cfg);
            s.BaseMatch = classic;
            s.WantSlot = slot;
            s.Spectate = slot < 0 || slot >= 12;
            return s;
        }

        private void Awake() => Instance = this;

        private void Start()
        {
            Cfg ??= ChainConfig.FromArgs();
            _clock = new OnChainClock(Cfg.rate) { LagMs = Cfg.lagMs };
            _core = RushCoreAbi.Create();
#if UNITY_WEBGL && !UNITY_EDITOR
            _tx = new BundleChainTransport();
            Cfg.world = BundleChainTransport.Layout; // the bundle normalises the account (P2's offsets live in its config)
#else
            _tx = new WsChainTransport();
#endif
            _adapter.Emulator = Cfg.target == "emu";
            _tx.Connect(Cfg);
            _tx.Join(NetBootstrap.ClientName(), WantSlot, Spectate);
            ChainAdapters.Override = _adapter;
            _feed.Base = BaseMatch;
            _panel = NetTargets.GetQueryParam(Application.absoluteURL, "hud") == "1" || Array.Exists(Environment.GetCommandLineArgs(), a => a == "-fallrushHud");
            var auto = OnChainAutoPlay.Arg();
            if (auto > 0)
                OnChainAutoPlay.Attach(this, auto);
            Debug.Log($"[onchain] session: target {Cfg.target} via {_tx.Name} {Cfg.url}, lead T+{Cfg.inputLead}, {Cfg.batchTicks} ticks per tx, core {(_core.Ready ? $"0x{_core.Version:x}" : "loading")}");
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            var cs = ClientSim.Instance;
            if (cs != null && ReferenceEquals(cs.Feed, this))
            {
                cs.Feed = null;
                cs.FeedReset();
            }

            if (ReferenceEquals(ChainAdapters.Override, _adapter))
                ChainAdapters.Override = null;
            _pred?.Dispose();
            _tx?.Dispose();
        }

        /// <summary>Ends on-chain play (back to the game server's state, or the menu).</summary>
        public void End() => Destroy(gameObject);

        // ------------------------------------------------------------------ IChainFeed

        public bool OverridesServer => isActiveAndEnabled;

        public double PresentTick => _pred != null && _pred.HasState && OwnPlaying ? _pred.Present + _frac : double.NaN;

        private bool OwnPlaying
        {
            get
            {
                var cs = ClientSim.Instance;
                return _ownSlot < 12 && cs != null && !cs.OwnOutOfSim && _hasNewest;
            }
        }

        public bool TrySampleOwn(out Vector3 feet, out Vector3 vel, out byte flags, out byte yaw, out byte stun)
        {
            feet = vel = Vector3.zero;
            flags = yaw = stun = 0;
            if (_pred == null || !_pred.HasState || !OwnPlaying)
                return false;
            feet = _pred.RenderFeet((float)_frac) + _pred.RenderOffset;
            _pred.Newest(out vel, out flags, out yaw, out stun, out _);
            return true;
        }

        // ------------------------------------------------------------------ frame

        private void Update()
        {
            var now = ClientSim.Now;
            var kb = Keyboard.current;
            if (kb != null && kb.f3Key.wasPressedThisFrame)
                _panel = !_panel;
            _tx.Pump(now);
            _clock.RttMs = _tx.RttMs;
            if (_tx.Error != null)
                _status = _tx.Error;
            if (!_core.Ready)
            {
                _status = _core.Error ?? "loading rush-core …";
                return;
            }

            var j = _tx.Joined;
            if (j == null)
                return;
            if (!_sceneRequested || j.slot != _ownSlot && j.slot < 255)
                OnJoined(j);

            var cs = ClientSim.Instance;
            if (cs != null && !ReferenceEquals(cs.Feed, this))
                cs.Feed = this;

            _worlds.Clear();
            _tx.DrainWorlds(_worlds);
            foreach (var w in _worlds)
                OnWorldMsg(in w, cs);
            if (_worlds.Count > 0 && cs != null && _hasNewest)
                AfterWorlds(cs, now);

            _confirms.Clear();
            _tx.DrainConfirms(_confirms);
            foreach (var c in _confirms)
            {
                if (c.Status != 0)
                    BatchesRejected++;
                _confirmMs[_confirmN++ % _confirmMs.Length] = c.LatencyMs;
            }

            AdvanceAndSend(cs, now);
            _pred?.UpdateSmoothing(Time.deltaTime);

            var lag = _clock.Lagging(now);
            if (lag && _hasNewest && _lastLagAt < 0)
            {
                Lagged++;
                _lastLagAt = now;
            }
            else if (!lag)
                _lastLagAt = -1;
            _adapter.Live = _hasNewest && !lag;
            Stats(now);
        }

        private void OnJoined(JoinInfo j)
        {
            _ownSlot = j.slot;
            _feed.OwnSlot = j.slot;
            _adapter.Arena = j.arena;
            for (var i = 0; i < 12; i++)
            {
                _feed.Names[i] = null;
                _feed.Bots[i] = false;
            }

            foreach (var r in j.roster ?? Array.Empty<RosterEntry>())
                if (r.slot >= 0 && r.slot < 12)
                {
                    _feed.Bots[r.slot] = r.kind == "bot";
                    _feed.Names[r.slot] = !string.IsNullOrEmpty(r.name) ? r.name : r.kind == "bot" ? $"Bot {r.slot}" : $"Player {r.slot}";
                }

            if (_sceneRequested)
                return;
            _sceneRequested = true;
            _mapKey = string.IsNullOrEmpty(j.map) ? "CrumbleShelf" : j.map;
            var asset = Resources.Load<TextAsset>("feellab/" + _mapKey);
            if (asset == null)
            {
                _status = $"no map blob for {_mapKey}";
                return;
            }

            _map = asset.bytes;
            if (j.coreVersion != 0 && j.coreVersion != _core.Version)
                Debug.LogWarning($"[onchain] arena core 0x{j.coreVersion:x} != client core 0x{_core.Version:x}");
            _status = $"joined {j.arena} slot {(j.slot < 12 ? j.slot.ToString() : "spectator")}";
            Debug.Log($"[onchain] {_status}, map {_mapKey}");
            var scene = "Map_" + _mapKey;
            if (SceneManager.GetActiveScene().name != scene)
                SceneManager.LoadScene(scene);
        }

        private void OnWorldMsg(in WorldMsg m, ClientSim cs)
        {
            var lay = Cfg.world;
            if (m.Len < lay.offset + WorldView.Size)
                return;
            var tick = lay.Tick(m.Acc);
            var wl = lay.WorldLen(m.Acc, m.Len);
            var view = new WorldView(m.Acc, lay.offset, wl);
            if (!view.Valid)
            {
                _status = $"bad World ({wl} B)";
                return;
            }

            Updates++;
            _clock.OnWorld(view.Tick, m.RecvAt);
            var key = view.RushTick ^ (uint)view.HazardSeed;
            if (_hasNewest && view.Tick + 30 < _newestTick)
                NewArena(cs); // the arena restarted
            if (_hasNewest && view.Tick <= _newestTick)
                return; // old / duplicate (a poll reply)
            _feed.OnWorld(in view, m.RecvAt);
            if (cs != null)
            {
                cs.FeedSnapshot(view.Tick, new ReadOnlySpan<PlayerState>(_feed.Players, 0, _feed.PlayerCount), _ownSlot, m.RecvAt);
                foreach (var e in _feed.NewEvents)
                    cs.FeedRaceEvent(in e);
            }

            Buffer.BlockCopy(m.Acc, lay.offset, _newest, 0, wl);
            _newestLen = wl;
            _newestTick = view.Tick;
            _hasNewest = true;
            _applied = _ownSlot < 12 ? lay.Applied(m.Acc, _ownSlot) : 0;
            if (key != _arenaKey && view.Phase != WorldView.PhaseSandbox)
                _arenaKey = key;
        }

        private uint _applied;

        private void NewArena(ClientSim cs)
        {
            Debug.Log("[onchain] new arena: resetting the feed and the prediction");
            _feed.Reset();
            _pred?.Dispose();
            _pred = null;
            _pending = 0;
            _hasNewest = false;
            _newestTick = 0;
            _clock.Reset();
            cs?.FeedReset();
        }

        private void AfterWorlds(ClientSim cs, double now)
        {
            cs.FeedRaceState(in _feed.Race);
            if (_feed.HasRound && (!cs.Round.HasValue || !SameRound(cs.Round.Value, _feed.Round)))
                cs.FeedRound(in _feed.Round);
            // match state: on a phase change, else at 5 Hz (the HUD refreshes at 10 Hz)
            var m = _feed.Match;
            if (m != null && (m.Phase != _lastMatchPhase || Time.unscaledTime - _matchFedAt > 0.2f))
            {
                _lastMatchPhase = m.Phase;
                _matchFedAt = Time.unscaledTime;
                cs.FeedMatch(m);
            }

            // own prediction: rebase on the newest World
            var view = new WorldView(_newest, 0, _newestLen);
            if (_ownSlot >= 12 || !view.Active(_ownSlot) || _map == null)
                return;
            if (_pred == null)
            {
                var bots = 0u;
                for (var s = 0; s < 12; s++)
                    if (view.IsBot(s))
                        bots |= 1u << s;
                _pred = new OnChainPrediction(_core, _map, view.Rate, view.ActiveMask, bots, _ownSlot, Cfg.smoothingMs, Cfg.snapMeters);
            }

            var present = _pred.HasState ? _pred.Present : (uint)Math.Max(_newestTick, Math.Floor(Target(now)));
            _pred.OnAuthoritative(_newest, 0, _newestLen, _newestTick, present, now, _applied);
        }

        private static bool SameRound(in RoundParams a, in RoundParams b) =>
            a.MapId == b.MapId && a.HazardSeed == b.HazardSeed && a.RoundStartTick == b.RoundStartTick && a.Variant == b.Variant && a.Modifier == b.Modifier;

        /// <summary>The predicted present: chain now + lead + batch − 1 (fractional).</summary>
        private double Target(double now) => _clock.ChainNow(now) + Cfg.inputLead + Cfg.batchTicks - 1;

        private void AdvanceAndSend(ClientSim cs, double now)
        {
            if (_pred == null || !_pred.HasState || !_clock.HasData)
                return;
            var target = Target(now);
            var whole = (uint)Math.Max(0, Math.Floor(target));
            if (whole > _pred.Present + 8 || whole + 8 < _pred.Present)
            {
                // Far off (a stall, a tab in the background): jump; the next World rebases exactly.
                Resyncs++;
                FlushBatch();
                _pred.StepTo(Math.Max(_pred.Present, whole));
                _frac = 0;
                return;
            }

            var playing = OwnPlaying;
            var steps = 0;
            while (_pred.Present < whole && steps++ < 8)
            {
                var tick = _pred.Present + 1;
                var cmd = new InputCmd { Tick = tick };
                if (playing)
                    cs?.InputSource?.Sample(ref cmd);
                _pred.SetInput(tick, in cmd);
                _pred.StepTo(tick);
                if (playing)
                    Queue(tick, in cmd);
            }

            _frac = _pred.Present < whole ? 0 : Math.Min(1.0, target - whole);
            if (_pred.Present > whole)
                _frac = 1.0; // ahead of the target: hold the newest tick
        }

        private void Queue(uint tick, in InputCmd c)
        {
            if (_pending > 0 && tick != _pendingFirst + (uint)_pending)
                FlushBatch(); // a gap: send what we have
            if (_pending == 0)
                _pendingFirst = tick;
            var o = 6 + _pending * 5;
            _batch[o] = (byte)c.MoveX;
            _batch[o + 1] = (byte)c.MoveY;
            _batch[o + 2] = c.Buttons;
            _batch[o + 3] = (byte)c.CamYaw;
            _batch[o + 4] = (byte)(c.CamYaw >> 8);
            _pending++;
            if (_pending >= Math.Max(1, Cfg.batchTicks))
                FlushBatch();
        }

        private void FlushBatch()
        {
            if (_pending == 0 || _ownSlot >= 12)
            {
                _pending = 0;
                return;
            }

            _batch[0] = (byte)_ownSlot;
            BitConverter.GetBytes(_pendingFirst).CopyTo(_batch, 1);
            _batch[5] = (byte)_pending;
            _tx.SendInputs(_batch, 6 + _pending * 5);
            Batches++;
            _pending = 0;
        }

        private void Stats(double now)
        {
            if (now - _statsAt < 1.0)
                return;
            var span = (float)(now - _statsAt);
            _statsAt = now;
            UpdatesPerSec = (Updates - _updatesMark) / span;
            BatchesPerSec = (Batches - _batchesMark) / span;
            _updatesMark = Updates;
            _batchesMark = Batches;
            var n = Math.Min(_confirmN, _confirmMs.Length);
            if (n > 0)
            {
                var a = new float[n];
                Array.Copy(_confirmMs, a, n);
                Array.Sort(a);
                ConfirmP50 = a[n / 2];
                ConfirmP95 = a[Math.Min(n - 1, (int)(n * 0.95f))];
            }
        }

        /// <summary>One line for logs / measurement scripts.</summary>
        public string StatsLine(double now)
        {
            var p = _pred;
            return $"[onchain] tick {_newestTick} upd/s {UpdatesPerSec:F1} batches/s {BatchesPerSec:F1} corr/5s {(p != null ? p.CorrectionsSince(now, 5) : 0)} corr {(p?.Corrections ?? 0)} match {(p?.Matches ?? 0)} late {(p?.LateInputs ?? 0)} replay {(p?.LastReplay ?? 0)}/{(p?.MaxReplay ?? 0)} rtt {_tx.RttMs:F0} confirm p50 {ConfirmP50:F0} p95 {ConfirmP95:F0} gaps {_tx.Gaps} polls {_tx.Polls} resync {Resyncs} lag {Lagged} rejected {BatchesRejected}";
        }

        private readonly StringBuilder _sb = new StringBuilder(512);

        private void OnGUI()
        {
            var now = ClientSim.Now;
            if (_hasNewest && _clock.Lagging(now))
            {
                GUI.color = new Color(1f, 0.75f, 0.2f);
                GUI.Label(new Rect(Screen.width * 0.5f - 60, 70, 200, 24), "! LAGGING");
                GUI.color = Color.white;
            }

            if (!_hasNewest)
                GUI.Label(new Rect(16, Screen.height - 80, 900, 24), $"On-chain (preview): {_status}");
            if (!_panel)
                return;
            var p = _pred;
            _sb.Clear();
            _sb.Append("<b>ON-CHAIN TICK HEALTH (F3)</b>  ").Append(Cfg.target).Append(" via ").Append(_tx.Name).Append('\n');
            _sb.Append("arena ").Append(_adapter.Arena).Append("  slot ").Append(_ownSlot < 12 ? _ownSlot.ToString() : "spectator").Append("  world tick ").Append(_newestTick)
                .Append("  age ").Append(_clock.AgeMs(now).ToString("F0")).Append(" ms").Append(_clock.Lagging(now) ? "  ! LAGGING" : "").Append('\n');
            _sb.Append("updates/s ").Append(UpdatesPerSec.ToString("F1")).Append("  input batches/s ").Append(BatchesPerSec.ToString("F1"))
                .Append("  gaps ").Append(_tx.Gaps).Append("  polls ").Append(_tx.Polls).Append("  reconnects ").Append(_tx.Reconnects).Append('\n');
            _sb.Append("RTT ").Append(float.IsNaN(_tx.RttMs) ? "-" : _tx.RttMs.ToString("F0")).Append(" ms  confirm p50 ").Append(ConfirmP50.ToString("F0")).Append(" / p95 ")
                .Append(ConfirmP95.ToString("F0")).Append(" ms  rejected ").Append(BatchesRejected).Append('\n');
            if (p != null)
            {
                _sb.Append("corrections/5s ").Append(p.CorrectionsSince(now, 5)).Append("  total ").Append(p.Corrections).Append("  match ").Append(p.Matches)
                    .Append("  late inputs ").Append(p.LateInputs).Append("  last ").Append((p.LastCorrectionM * 100f).ToString("F1")).Append(" cm  max ")
                    .Append((p.MaxCorrectionM * 100f).ToString("F1")).Append(" cm\n");
                _sb.Append("present ").Append(p.Present).Append(" (+").Append((long)p.Present - _newestTick).Append(")  replay ").Append(p.LastReplay).Append(" / max ")
                    .Append(p.MaxReplay).Append("  smoothing ").Append((p.RenderOffset.magnitude * 100f).ToString("F1")).Append(" cm  snaps ").Append(p.Snaps)
                    .Append("  resyncs ").Append(Resyncs).Append('\n');
            }

            var w = new WorldView(_newest, 0, _newestLen);
            _sb.Append("core 0x").Append(_core.Version.ToString("x")).Append("  World hash ").Append(_hasNewest ? Fnv(_newest, _newestLen).ToString("x16") : "-")
                .Append("  phase ").Append(_hasNewest ? w.Phase.ToString() : "-").Append("  lead T+").Append(Cfg.inputLead).Append(" × ").Append(Cfg.batchTicks).Append('\n');
            GUI.Box(new Rect(Screen.width - 620, 120, 610, 150), GUIContent.none);
            GUI.Label(new Rect(Screen.width - 612, 124, 600, 145), _sb.ToString());
        }

        /// <summary>FNV-1a 64 over the canonical World bytes (= <c>rc_hash</c>).</summary>
        public static ulong Fnv(byte[] b, int len)
        {
            var h = 0xcbf29ce484222325UL;
            for (var i = 0; i < len; i++)
            {
                h ^= b[i];
                h *= 0x100000001b3UL;
            }

            return h;
        }
    }
}
