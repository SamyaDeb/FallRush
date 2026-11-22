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
        /// <summary>Hybrid pre-join: the Classic match id (hex) whose arena lobby to join.</summary>
        public string MatchHex;
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
        public long Updates, Batches, BatchesRejected, BatchesUnconfirmed, Resyncs, Lagged;
        /// <summary>P3-J: the arena rejected our session key (revoked or expired SessionTokenV2).</summary>
        public bool SessionExpired;
        private long _acceptedBatches;
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
            // P3-G: watch only (late joiners, eliminated players): no slot, no inputs, the same spectate camera (Tab).
            s.Spectate = Array.IndexOf(Environment.GetCommandLineArgs(), "-fallrushOnchainSpectate") >= 0 || NetTargets.GetQueryParam(Application.absoluteURL, "onchainspectate") == "1";
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
            _prevFrameRate = Application.targetFrameRate;
            Application.targetFrameRate = 60; // on-chain play: leave CPU to the chain link (a relay may share the machine)
            _core = RushCoreAbi.Create();
#if UNITY_WEBGL && !UNITY_EDITOR
            _tx = new BundleChainTransport();
            Cfg.world = BundleChainTransport.Layout; // the bundle normalises the account (P2's offsets live in its config)
#else
            _tx = new WsChainTransport();
#endif
            _adapter.Emulator = Cfg.target == "emu";
            _extraLead = Cfg.target == "emu" ? 0 : 2; // rollup: P0.5 saw inputs applied at T+2 (p50) / T+6 (p95)
            _tx.Connect(Cfg);
            _tx.Join(NetBootstrap.ClientName(), WantSlot, Spectate, MatchHex);
            _feed.Base = BaseMatch;
            _panel = NetTargets.GetQueryParam(Application.absoluteURL, "hud") == "1" || Array.Exists(Environment.GetCommandLineArgs(), a => a == "-fallrushHud");
            var auto = OnChainAutoPlay.Arg();
            if (auto > 0)
                OnChainAutoPlay.Attach(this, auto);
            Debug.Log($"[onchain] session: target {Cfg.target} via {_tx.Name} {Cfg.url}, lead T+{Cfg.inputLead}, {Cfg.batchTicks} ticks per tx, core {(_core.Ready ? $"0x{_core.Version:x}" : "loading")}");
        }

        private int _prevFrameRate = -1;

        private void OnDestroy()
        {
            Application.targetFrameRate = _prevFrameRate;
            if (Instance == this)
                Instance = null;
            var cs = ClientSim.Instance;
            if (cs != null && ReferenceEquals(cs.Feed, this))
            {
                if (BaseMatch != null)
                    cs.FeedHandBack(); // the switch: back to the game server's screens (its champion = the arena's)
                else
                {
                    cs.Feed = null;
                    cs.FeedReset();
                }
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
                if (c.Status == 5 || c.Status == 3 && Batches > 10 && SessionExpired == false && _acceptedBatches > 0)
                {
                    // the program refused our session key (revoked / expired token): inputs no longer count
                    SessionExpired = true;
                    Debug.LogWarning("[onchain] session expired / revoked: inputs rejected (status " + c.Status + ")");
                }

                if (c.Status == 0)
                    _acceptedBatches++;
                if (c.Status == 1 || c.Status == 2 || c.Status == 3 || c.Status == 5)
                    BatchesRejected++; // late / out of window / bad slot
                else if (c.Status != 0)
                    BatchesUnconfirmed++; // timeout or a transport error: may still have landed
                _confirmMs[_confirmN++ % _confirmMs.Length] = c.LatencyMs;
            }

            AdaptLead(now);
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
            if (BaseMatch != null)
                SuperviseSwitch(cs, now);
        }

        private double _startedAt = -1, _joinedAt = -1;

        /// <summary>
        /// P3-F: in the Classic → on-chain switch, hand the screens back to the game server once it has the arena's
        /// result (its match reaches CHAMPION: same champion), or when the arena never delivered a World (failure path:
        /// chip ! Pending, then ● Server).
        /// </summary>
        private void SuperviseSwitch(ClientSim cs, double now)
        {
            if (_startedAt < 0)
                _startedAt = now;
            var sm = cs != null ? cs.ServerMatch : null;
            if (sm != null && sm.Phase >= MatchPhase.Champion && sm.Phase != MatchPhase.Aborted)
            {
                Debug.Log($"[onchain] switch done: server match {sm.Phase}, champion {sm.Champion}; handing back");
                End();
                return;
            }

            if (sm != null && (sm.Phase == MatchPhase.LobbyOpen || sm.Phase == MatchPhase.Aborted))
            {
                End();
                return;
            }

            // failure path: joined (the arena started) but no World within the timeout; a pre-join waits for the lobby
            if (!_hasNewest && (_joinedAt >= 0 ? now - _joinedAt : MatchHex == null ? now - _startedAt : 0) > SwitchTimeoutSeconds)
            {
                Debug.LogWarning($"[onchain] switch failed: no World from {Cfg.url} in {SwitchTimeoutSeconds} s; back to the game server");
                End();
            }
        }

        public const float SwitchTimeoutSeconds = 8f;

        private void OnJoined(JoinInfo j)
        {
            _ownSlot = j.slot;
            ChainAdapters.Override = _adapter; // from the join on (a pre-join stays invisible until the Final starts)
            if (_joinedAt < 0)
                _joinedAt = ClientSim.Now;
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
            {
                if (view.Tick >= 30 || _newestTick < 300)
                    return; // a stale read (a slow poll reply / the slower socket), not a restart: a new match restarts at tick 0
                NewArena(cs); // the arena restarted
            }
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
            // inputs are latched in intro / countdown too (the rules neutralise them): measure there, so the lead has
            // settled before RUSH
            var timed = view.Phase == WorldView.PhaseIntro || view.Phase == WorldView.PhaseCountdown || view.Phase == WorldView.PhaseRush;
            if (_ownSlot < 12 && timed && _sentTicks.Contains(view.Tick))
            {
                if (_applied < view.Tick)
                {
                    _lateWindow++;
                    // P3-J: 2 s of our ticks never applied at the maximum lead = the arena refuses our key (a revoked /
                    // expired SessionTokenV2 is not always reported back as a clean confirm status)
                    if (++_lateStreak >= 60 && _extraLead >= MaxExtraLead && !SessionExpired)
                    {
                        SessionExpired = true;
                        Debug.LogWarning($"[onchain] session expired / revoked: no input applied for {_lateStreak} ticks (applied_tick {_applied}, tick {view.Tick})");
                    }
                }
                else
                {
                    _onTimeWindow++;
                    _lateStreak = 0;
                }
            }
            if (key != _arenaKey && view.Phase != WorldView.PhaseSandbox)
                _arenaKey = key;
        }

        private uint _applied;
        private bool _resultLogged;
        // P3-K: adaptive extra input lead (client-side delay tuning): +1 tick when our inputs arrive late (the arena's
        // applied_tick for that slot is behind a tick we sent), −1 after 10 s without any late input; ≤ MaxExtraLead.
        private readonly HashSet<uint> _sentTicks = new HashSet<uint>();
        private int _lateWindow, _onTimeWindow, _extraLead, _lateStreak;
        private double _leadAt, _lastLateAt = -1;
        public int ExtraLead => _extraLead;
        /// <summary>The program accepts stamps ≤ World.tick + 8; a batch ends 2 ticks after its first stamp (T+3+x+2 ≤ +8).</summary>
        public const int MaxExtraLead = 3;

        private void AdaptLead(double now)
        {
            if (now - _leadAt < 1.0)
                return;
            _leadAt = now;
            if (_lateWindow > 0)
            {
                _lastLateAt = now;
                _extraLead = Math.Min(MaxExtraLead, _extraLead + 1);
            }
            else if (_extraLead > 0 && _lastLateAt >= 0 && now - _lastLateAt > 10.0)
            {
                _extraLead--;
                _lastLateAt = now;
            }

            _lateWindow = _onTimeWindow = 0;
            if (_sentTicks.Count > 600)
                _sentTicks.Clear();
        }

        private void NewArena(ClientSim cs)
        {
            Debug.Log("[onchain] new arena: resetting the feed and the prediction");
            _feed.Reset();
            _resultLogged = false;
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

            if (m != null && m.Champion != 255 && !_resultLogged)
            {
                _resultLogged = true;
                var v = new WorldView(_newest, 0, _newestLen);
                var order = new StringBuilder();
                for (var k = 0; k < v.ResultCount; k++)
                    order.Append(k > 0 ? "," : "").Append(v.ResultOrder(k));
                Debug.Log($"[onchain] RESULT champion {m.Champion} order {order} own slot {_ownSlot} arena {_adapter.Arena} tick {_newestTick}");
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
        private double Target(double now) => _clock.ChainNow(now) + Cfg.inputLead + _extraLead + Cfg.batchTicks - 1;

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
                cmd.CamYaw &= 0xFF00; // the program stores cam yaw >> 8 (P2 §10): predict exactly what the arena applies
                _pred.SetInput(tick, in cmd);
                _pred.StepTo(tick);
                if (playing)
                {
                    Queue(tick, in cmd);
                    _sentTicks.Add(tick);
                }
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
            return $"[onchain] tick {_newestTick} upd/s {UpdatesPerSec:F1} batches/s {BatchesPerSec:F1} corr/5s {(p != null ? p.CorrectionsSince(now, 5) : 0)} corr {(p?.Corrections ?? 0)} match {(p?.Matches ?? 0)} late {(p?.LateInputs ?? 0)} replay {(p?.LastReplay ?? 0)}/{(p?.MaxReplay ?? 0)} rtt {_tx.RttMs:F0} confirm p50 {ConfirmP50:F0} p95 {ConfirmP95:F0} gaps {_tx.Gaps} polls {_tx.Polls} resync {Resyncs} lag {Lagged} rejected {BatchesRejected} unconfirmed {BatchesUnconfirmed} lead +{_extraLead}";
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
            if (SessionExpired)
            {
                GUI.color = new Color(1f, 0.75f, 0.2f);
                GUI.Label(new Rect(Screen.width * 0.5f - 220, 96, 440, 24), "Session expired → rejoin (your inputs are no longer accepted)");
                GUI.color = Color.white;
            }
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
                .Append("  phase ").Append(_hasNewest ? w.Phase.ToString() : "-").Append("  lead T+").Append(Cfg.inputLead).Append(" +").Append(_extraLead).Append(" × ").Append(Cfg.batchTicks).Append('\n');
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
