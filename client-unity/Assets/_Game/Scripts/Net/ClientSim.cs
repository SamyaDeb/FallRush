using System;
using FallRush.Shared;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FallRush.Net
{
    /// <summary>Fills move/buttons/camYaw of a command for the tick being sampled (keyboard+mouse, or a scripted probe).</summary>
    public interface IInputSource
    {
        void Sample(ref InputCmd cmd);
    }

    /// <summary>
    /// Client side of the M2 movement protocol. Lives on the NetworkManager object on clients.
    /// <list type="bullet">
    /// <item>Samples input at the 30 Hz tick chosen by <see cref="TickSync"/>, sends <c>in</c> (newest 3 commands) and
    /// predicts the local player at once (<see cref="ClientPrediction"/>, M2b).</item>
    /// <item>Receives <c>snap</c>: our own entry reconciles the prediction; every entry goes to the remote-player
    /// <see cref="Interpolation"/>.</item>
    /// <item>The prediction world is the loaded map's PhysX scene with its hazards (<see cref="MapInfo.CreateWorld"/>).</item>
    /// </list>
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class ClientSim : MonoBehaviour
    {
        private const NetworkDelivery InputDelivery = NetworkDelivery.Unreliable;

        public static ClientSim Instance { get; private set; }

        public readonly TickSync Clock = new TickSync(MovementConfig.Default.TickRate);
        public readonly Interpolation Remote = new Interpolation(1.0 / MovementConfig.Default.TickRate);
        public IInputSource InputSource;

        /// <summary>Local prediction; null until the map is loaded.</summary>
        public ClientPrediction Prediction { get; private set; }
        public HazardSet Hazards => Prediction?.Hazards;

        /// <summary>True while the map is loaded and we should be sending input.</summary>
        public bool Active { get; set; }

        public int OwnSlot { get; private set; } = SnapHeader.NoSlot;
        public uint LastAckSeq { get; private set; }
        public uint Seq { get; private set; }
        public SnapHeader LastHeader { get; private set; }

        // ---- stats (HUD) ----
        public long BytesIn, BytesOut, SnapsIn, InputsOut, BadSnaps, OldSnaps;
        public float BytesInPerSec { get; private set; }
        public float BytesOutPerSec { get; private set; }
        public float SnapsPerSec { get; private set; }

        private readonly PlayerState[] _scratch = new PlayerState[Protocol.MaxPlayers];
        private readonly byte[] _snapBuf = new byte[NetMessages.SnapMaxBytes + 8];
        private readonly byte[] _inBuf = new byte[NetMessages.InputMaxBytes];
        private readonly InputCmd[] _recent = new InputCmd[NetMessages.MaxInputCmds];
        private int _recentCount;
        private NetworkManager _nm;
        private double _lastNow;
        private double _statsAt;
        private long _statBytesIn, _statBytesOut, _statSnaps;
        private MapInfo _predictionMap;
        private PhysxMotorWorld _world;

        private void Awake()
        {
            Instance = this;
            _nm = GetComponent<NetworkManager>();
            _nm.OnClientStarted += OnClientStarted;
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            if (_nm != null)
                _nm.OnClientStarted -= OnClientStarted;
        }

        private void OnClientStarted()
        {
            if (_nm.IsServer)
                return;
            ResetSession();
            _nm.CustomMessagingManager.RegisterNamedMessageHandler(Protocol.Msg.Snap, OnSnap);
            _nm.CustomMessagingManager.RegisterNamedMessageHandler(Protocol.Msg.Round, OnRound);
            _nm.CustomMessagingManager.RegisterNamedMessageHandler(Protocol.Msg.RaceState, OnRaceState);
            _nm.CustomMessagingManager.RegisterNamedMessageHandler(Protocol.Msg.Event, OnEvent);
            _nm.CustomMessagingManager.RegisterNamedMessageHandler(Protocol.Msg.Match, OnMatch);
            _nm.CustomMessagingManager.RegisterNamedMessageHandler(Protocol.Msg.MatchEvent, OnMatchEvent);
            _nm.CustomMessagingManager.RegisterNamedMessageHandler(Protocol.Msg.Reactive, OnReactive);
            _nm.CustomMessagingManager.RegisterNamedMessageHandler(Protocol.Msg.Emote, OnEmote);
        }

        // ---------------------------------------------------------------- match (v4)

        /// <summary>The newest full match state (<c>match</c>), or null before the first one.</summary>
        public MatchStateMessage Match { get; private set; }
        /// <summary>Transcript events of the current match (<c>mevt</c>), in seq order.</summary>
        public readonly System.Collections.Generic.List<MatchEvent> MatchEvents = new System.Collections.Generic.List<MatchEvent>();
        public event Action MatchChanged;
        public event Action<MatchEvent> MatchEventReceived;
        /// <summary>Follow the server's scene flow: load the map each <c>round</c> names (menu Play, probe).</summary>
        public bool FollowScenes;
        public long MatchMsgs, MatchBytesIn;
        private byte _readySerial = 0xFF;
        private byte _readyMap = 0xFF;
        private bool _sceneLoading;
        private string _sceneTarget;

        private void OnMatch(ulong sender, FastBufferReader reader)
        {
            var len = reader.Length - reader.Position;
            if (len <= 0 || len > _msgBuf.Length)
                return;
            var buf = _msgBuf;
            reader.ReadBytesSafe(ref buf, len);
            if (!MatchStateMessage.TryDecode(new ReadOnlySpan<byte>(buf, 0, len), out var m))
            {
                Debug.LogWarning($"[net] undecodable match message ({len} B)");
                return;
            }

            MatchMsgs++;
            MatchBytesIn += len;
            var newMatch = Match == null || Match.MatchSerial != m.MatchSerial || m.Phase == MatchPhase.LobbyOpen;
            if (newMatch && (Match == null || m.MatchSerial != Match.MatchSerial))
                MatchEvents.Clear();
            Match = m;
            SessionToken.Save(m.Token);
            MatchChanged?.Invoke();
        }

        /// <summary>G1: a player's emote (slot, id 1–4), relayed by the server. Visual only.</summary>
        public event Action<int, int> EmoteReceived;

        private void OnEmote(ulong sender, FastBufferReader reader)
        {
            var len = reader.Length - reader.Position;
            if (len != EmoteCodec.Size)
                return;
            var buf = new byte[EmoteCodec.Size];
            reader.ReadBytesSafe(ref buf, len);
            if (EmoteCodec.TryDecode(buf, out var slot, out var id))
                EmoteReceived?.Invoke(slot, id);
        }

        private void OnMatchEvent(ulong sender, FastBufferReader reader)
        {
            var len = reader.Length - reader.Position;
            if (len <= 0 || len > _msgBuf.Length)
                return;
            var buf = _msgBuf;
            reader.ReadBytesSafe(ref buf, len);
            if (!MatchEventCodec.TryDecode(new ReadOnlySpan<byte>(buf, 0, len), out var e))
                return;
            if (e.Seq == 0)
                MatchEvents.Clear(); // a new match (or the full log after a reconnect)
            if (e.Seq < MatchEvents.Count)
                return; // duplicate (the full log after a reconnect)
            MatchEvents.Add(e);
            MatchEventReceived?.Invoke(e);
        }

        /// <summary>Loads the map the server's round names, and reports <c>ready</c> once it is loaded with a matching hash (M4-3).</summary>
        private void FollowServerScene()
        {
            if (!FollowScenes || !Round.HasValue || _nm == null || !_nm.IsConnectedClient)
                return;
            var want = SceneFor(Round.Value.MapId);
            var active = SceneManager.GetActiveScene().name;
            if (active != want)
            {
                if (_sceneLoading && _sceneTarget == want)
                    return;
                _sceneLoading = true;
                _sceneTarget = want;
                Debug.Log($"[net] loading {want} (round {Round.Value.RoundIndex})");
                SceneManager.LoadScene(want);
                return;
            }

            _sceneLoading = false;
            var map = MapInfo.Current;
            if (map == null || Match == null || MapMismatch != null || map.MapId != Round.Value.MapId)
                return;
            if (_readySerial == Match.LoadSerial && _readyMap == map.MapId)
                return;
            _readySerial = Match.LoadSerial;
            _readyMap = map.MapId;
            var msg = new ReadyMessage { LoadSerial = Match.LoadSerial, MapHashPrefix = ReadyMessage.Prefix(map.ContentHash) };
            var n = msg.Encode(_msgBuf);
            using var w = new FastBufferWriter(n, Allocator.Temp);
            w.WriteBytesSafe(_msgBuf, n);
            _nm.CustomMessagingManager.SendNamedMessage(Protocol.Msg.Ready, NetworkManager.ServerClientId, w, NetworkDelivery.ReliableSequenced);
        }

        /// <summary>Small command to the server (host start, back to lobby, reactive resend).</summary>
        public void SendControl(ControlCmd cmd, uint arg = 0)
        {
            if (_nm == null || !_nm.IsConnectedClient)
                return;
            var n = ControlCodec.Encode(_msgBuf, cmd, arg);
            using var w = new FastBufferWriter(n, Allocator.Temp);
            w.WriteBytesSafe(_msgBuf, n);
            _nm.CustomMessagingManager.SendNamedMessage(Protocol.Msg.Control, NetworkManager.ServerClientId, w, NetworkDelivery.ReliableSequenced);
        }

        /// <summary>Our slot in the match (from <c>match</c>; 255 = watching).</summary>
        public int MatchSlot => Match != null ? Match.OwnSlot : SnapHeader.NoSlot;

        public bool TryGetSlotInfo(int slot, out MatchSlotInfo info)
        {
            info = default;
            if (Match == null)
                return false;
            foreach (var s in Match.Slots)
                if (s.Slot == slot)
                {
                    info = s;
                    return true;
                }

            return false;
        }

        // ---------------------------------------------------------------- reactive elements (M4 §3.4)

        /// <summary>The round's reactive log as received (full + deltas); replayed into every new prediction world.</summary>
        public readonly System.Collections.Generic.List<RxEntry> RxLog = new System.Collections.Generic.List<RxEntry>();
        private readonly System.Collections.Generic.List<RxEntry> _rxScratch = new System.Collections.Generic.List<RxEntry>();
        private byte _rxEpoch = 0xFF;
        private bool _rxResendAsked;
        private int _rxAppliedToPrediction;
        // ---- stats: changes that arrived after their effective tick in our predicted timeline (M4 §3.4 measurement) ----
        public long RxReceived, RxGaps, RxLate, RxBytesIn;
        public int RxLateMaxTicks;

        private void OnReactive(ulong sender, FastBufferReader reader)
        {
            var len = reader.Length - reader.Position;
            if (len <= 0 || len > _msgBuf.Length)
                return;
            RxBytesIn += len;
            var buf = _msgBuf;
            reader.ReadBytesSafe(ref buf, len);
            if (!ReactiveCodec.TryDecode(new ReadOnlySpan<byte>(buf, 0, len), out var kind, out var epoch, out var first, _rxScratch))
            {
                Debug.LogWarning("[net] undecodable rx");
                return;
            }

            if (kind == ReactiveCodec.Full)
            {
                RxLog.Clear();
                _rxEpoch = epoch;
                _rxResendAsked = false;
                _rxAppliedToPrediction = 0;
                if (Prediction?.Hazards.Reactive != null && Prediction.Hazards.Reactive.Log.Count > 0)
                    Prediction.Hazards.InitReactive(); // start over from the full log
            }
            else if (epoch != _rxEpoch)
            {
                return; // a stale round
            }
            else if (first != RxLog.Count + 1)
            {
                RxGaps++;
                if (!_rxResendAsked)
                {
                    _rxResendAsked = true;
                    SendControl(ControlCmd.RxResend, (uint)RxLog.Count);
                    Debug.LogWarning($"[net] rx gap: have {RxLog.Count}, got {first}; asked for the full state");
                }

                return;
            }

            RxLog.AddRange(_rxScratch);
            RxReceived += _rxScratch.Count;
            ApplyRxToPrediction(measure: true);
        }

        /// <summary>New log entries → the prediction's reactive set (it decides collider state per predicted tick).</summary>
        private void ApplyRxToPrediction(bool measure)
        {
            var rx = Prediction?.Hazards.Reactive;
            if (rx == null)
                return;
            for (; _rxAppliedToPrediction < RxLog.Count; _rxAppliedToPrediction++)
            {
                var e = RxLog[_rxAppliedToPrediction];
                if (!rx.Apply(in e))
                    break;
                if (!measure || !Prediction.HasState)
                    continue;
                var eff = rx.EffectiveTick(in e);
                if (eff <= Prediction.Tick && (e.Type == RxType.TileContact || e.Type == RxType.BlockTrigger))
                {
                    RxLate++;
                    var late = (int)(Prediction.Tick - eff);
                    if (late > RxLateMaxTicks)
                        RxLateMaxTicks = late;
                }
            }
        }

        // ---------------------------------------------------------------- round (v3)

        /// <summary>The current round from the server (<c>round</c> message), or null before the first one.</summary>
        public RoundParams? Round { get; private set; }
        public byte[] ServerMapHash { get; private set; }
        /// <summary>Set when our own map content hash differs from the server's: we refuse to play this round.</summary>
        public string MapMismatch { get; private set; }
        public RaceStateMessage RaceState { get; private set; }
        public bool HasRaceState { get; private set; }
        public uint LastEventSeq { get; private set; }
        public long EventsReceived { get; private set; }
        public event Action<RaceEvent> RaceEventReceived;
        public event Action RoundChanged;
        private readonly byte[] _msgBuf = new byte[8192];
        private RoundParams _predictionRound;

        /// <summary>R14 experiment: <c>-fallrushPredictPush</c> / <c>?pushpred=1</c> (or 0 to force off).</summary>
        public static bool PredictPushesEnabled
        {
            get
            {
                var q = NetTargets.GetQueryParam(Application.absoluteURL, "pushpred");
                if (q == "1") return true;
                if (q == "0") return false;
                foreach (var a in Environment.GetCommandLineArgs())
                    if (string.Equals(a, "-fallrushPredictPush", StringComparison.OrdinalIgnoreCase))
                        return true;
                return DefaultPredictPushes;
            }
        }

        /// <summary>Default for the push prediction (set from the M3 measurement).</summary>
        public const bool DefaultPredictPushes = true; // M3 measurement: localhost crowd race, corrections + pushes per 5 s 2.73 → 0.73 (proof M3 §G)

        /// <summary>Remote feet at <paramref name="tick"/>: the newest snapshot extrapolated with its velocity (≤ 12 ticks).</summary>
        private int RemoteFeetAt(uint tick, Vec3[] buf)
        {
            if (!Remote.HasData)
                return 0;
            ref readonly var n = ref Remote.Newest;
            var ahead = (int)(tick - n.Tick);
            if (ahead < 0) ahead = 0;
            if (ahead > 12) ahead = 12;
            var t = ahead / 30f;
            var c = 0;
            for (var i = 0; i < n.Count; i++)
            {
                ref readonly var p = ref n.Players[i];
                if (p.Slot == OwnSlot || (p.Flags & MotorState.Respawning) != 0 || (p.Race & PlayerState.RaceFinished) != 0)
                    continue;
                buf[c++] = new Vec3(p.PosX * 0.001f + p.VelX * 0.01f * t, p.PosY * 0.001f, p.PosZ * 0.001f + p.VelZ * 0.01f * t);
            }

            return c;
        }

        /// <summary>G4.1c: <see cref="RemoteFeetAt"/> plus each remote's snapshot velocity (cm/s) for the bump stagger prediction.</summary>
        private int RemoteMotionAt(uint tick, Vec3[] buf, Vec3[] vel)
        {
            if (!Remote.HasData)
                return 0;
            ref readonly var n = ref Remote.Newest;
            var ahead = (int)(tick - n.Tick);
            if (ahead < 0) ahead = 0;
            if (ahead > 12) ahead = 12;
            var t = ahead / 30f;
            var c = 0;
            for (var i = 0; i < n.Count; i++)
            {
                ref readonly var p = ref n.Players[i];
                if (p.Slot == OwnSlot || (p.Flags & MotorState.Respawning) != 0 || (p.Race & PlayerState.RaceFinished) != 0)
                    continue;
                vel[c] = new Vec3(p.VelX, p.VelY, p.VelZ);
                buf[c++] = new Vec3(p.PosX * 0.001f + p.VelX * 0.01f * t, p.PosY * 0.001f, p.PosZ * 0.001f + p.VelZ * 0.01f * t);
            }

            return c;
        }

        /// <summary>Scene name for a map id (dev sandbox = 255, lobby = 254).</summary>
        public static string SceneFor(byte mapId) => NetBootstrap.SceneForMap(mapId);

        private void OnRound(ulong sender, FastBufferReader reader)
        {
            var len = reader.Length - reader.Position;
            if (len != RoundMessage.Size)
            {
                Debug.LogWarning($"[net] bad round message ({len} B)");
                return;
            }

            var buf = _msgBuf;
            reader.ReadBytesSafe(ref buf, len);
            if (!RoundMessage.TryDecode(new ReadOnlySpan<byte>(buf, 0, len), out var m))
            {
                Debug.LogWarning("[net] undecodable round message");
                return;
            }

            Round = m.P;
            ServerMapHash = m.MapHash;
            Debug.Log($"[net] round: {m.P} serverMapHash={MapContent.Short(m.MapHash)}");
            CheckMapHash();
            RoundChanged?.Invoke();
        }

        /// <summary>A client whose own map content hash differs from the server's refuses the round (readable error, disconnect).</summary>
        private void CheckMapHash()
        {
            var map = MapInfo.Current;
            if (map == null || ServerMapHash == null || !Round.HasValue || map.MapId != Round.Value.MapId)
                return;
            var mine = map.ContentHash;
            for (var i = 0; i < 32; i++)
            {
                if (mine[i] == ServerMapHash[i])
                    continue;
                MapMismatch = $"Map data mismatch for {map.gameObject.scene.name}: server {MapContent.Short(ServerMapHash)}, this build {MapContent.Short(mine)}. Reload the page to get the current build.";
                Debug.LogError($"[net] REFUSING ROUND: {MapMismatch}");
                Active = false;
                NetBootstrap.Instance?.DisconnectWithReason(MapMismatch);
                return;
            }

            MapMismatch = null;
        }

        private void OnRaceState(ulong sender, FastBufferReader reader)
        {
            var len = reader.Length - reader.Position;
            if (len <= 0 || len > _msgBuf.Length)
                return;
            var buf = _msgBuf;
            reader.ReadBytesSafe(ref buf, len);
            if (!RaceStateMessage.TryDecode(new ReadOnlySpan<byte>(buf, 0, len), out var m))
            {
                Debug.LogWarning("[net] undecodable rstate");
                return;
            }

            RaceState = m;
            HasRaceState = true;
            if (m.LastEventSeq > LastEventSeq)
                LastEventSeq = m.LastEventSeq;
        }

        private void OnEvent(ulong sender, FastBufferReader reader)
        {
            var len = reader.Length - reader.Position;
            if (len != RaceEvent.Size)
                return;
            var buf = _msgBuf;
            reader.ReadBytesSafe(ref buf, len);
            if (!RaceEvent.TryDecode(new ReadOnlySpan<byte>(buf, 0, len), out var e))
                return;
            if (e.Seq <= LastEventSeq && LastEventSeq != 0)
                return; // already covered by an rstate after a reconnect
            LastEventSeq = e.Seq;
            EventsReceived++;
            RaceEventReceived?.Invoke(e);
        }

        public void ResetSession()
        {
            Clock.Reset();
            Remote.Reset();
            Prediction?.Reset();
            OwnSlot = SnapHeader.NoSlot;
            LastAckSeq = 0;
            Seq = 0;
            _recentCount = 0;
            Round = null;
            ServerMapHash = null;
            MapMismatch = null;
            HasRaceState = false;
            LastEventSeq = 0;
            OwnRace = 0;
            OwnOutOfSim = false;
            _readySerial = 0xFF;
            _readyMap = 0xFF;
            _sceneLoading = false;
        }

        public static double Now => Time.realtimeSinceStartupAsDouble;

        /// <summary>Fraction (0..1) of the current client tick elapsed — render interpolation of the predicted player.</summary>
        public float TickFraction => (float)Clock.Fraction;

        private void OnSnap(ulong sender, FastBufferReader reader)
        {
            var len = reader.Length - reader.Position;
            BytesIn += len;
            if (len <= 0 || len > NetMessages.SnapMaxBytes)
            {
                BadSnaps++;
                return;
            }

            var buf = _snapBuf;
            reader.ReadBytesSafe(ref buf, len);
            if (!NetMessages.TryDecodeSnap(new ReadOnlySpan<byte>(buf, 0, len), out var h, _scratch, out var err))
            {
                BadSnaps++;
                Debug.LogWarning($"[net] bad snap: {err}");
                return;
            }

            var now = Now;
            if (!Remote.Add(h.ServerTick, new ReadOnlySpan<PlayerState>(_scratch, 0, h.Count), now))
            {
                OldSnaps++;
                return;
            }

            SnapsIn++;
            OwnSlot = h.OwnSlot;
            LastAckSeq = h.AckSeq;
            LastHeader = h;

            var rtt = PingService.Instance != null ? PingService.Instance.P50Ms : float.NaN;
            Clock.OnSnapshot(h.ServerTick, h.InputLead, rtt, now);

            if (Prediction != null && Active && h.OwnSlot != SnapHeader.NoSlot)
            {
                Prediction.TeleportsExpected = HasRaceState && (RaceState.Phase == RoundPhase.Intro || RaceState.Phase == RoundPhase.Countdown);
                var found = false;
                for (var i = 0; i < h.Count; i++)
                    if (_scratch[i].Slot == h.OwnSlot)
                        found = true;
                if (!found)
                {
                    // M4: we are not in this round (eliminated earlier, late loader): spectate.
                    OwnRace = 0;
                    OwnOutOfSim = true;
                }

                for (var i = 0; i < h.Count; i++)
                {
                    if (_scratch[i].Slot != h.OwnSlot)
                        continue;
                    OwnRace = _scratch[i].Race;
                    // A finished racer leaves the simulation on the server: stop predicting, show the server state.
                    var out_ = (OwnRace & PlayerState.RaceFinished) != 0;
                    if (out_ != OwnOutOfSim)
                    {
                        OwnOutOfSim = out_;
                        if (!out_)
                            Prediction.Reset();
                    }

                    if (!OwnOutOfSim)
                        Prediction.Reconcile(h.ServerTick, in _scratch[i], in h, now);
                    break;
                }
            }
        }

        private void EnsurePrediction()
        {
            var map = MapInfo.Current;
            var round = Round ?? default;
            if (map == _predictionMap && (map == null || Prediction == null || SameRound(round, _predictionRound)))
                return;
            if (map != _predictionMap)
                CheckMapHash();
            _predictionMap = map;
            _predictionRound = round;
            Prediction = null;
            _world = null;
            if (map == null || MapMismatch != null)
                return;
            if (Round.HasValue && Round.Value.MapId != map.MapId)
                return; // wrong map loaded for this round; wait
            Physics.simulationMode = SimulationMode.Script; // queries only; nothing is simulated on clients either
            var cfg = NetBootstrap.MovementFor(round.Modifier);
            _world = map.CreateWorld(transform, cfg, in round, out var hazards);
            Prediction = new ClientPrediction(cfg, _world, hazards) { PredictPushes = PredictPushesEnabled, RemoteFeet = RemoteFeetAt, RemoteMotion = RemoteMotionAt };
            _rxAppliedToPrediction = 0;
            ApplyRxToPrediction(measure: false);
            Debug.Log($"[net] prediction ready map={map.gameObject.scene.name} mapHash={MapContent.Short(map.ContentHash)} round: {round} hazards={hazards.Count}");
        }

        private static bool SameRound(in RoundParams a, in RoundParams b) =>
            a.MapId == b.MapId && a.Variant == b.Variant && a.Modifier == b.Modifier && a.HazardSeed == b.HazardSeed && a.RoundStartTick == b.RoundStartTick;

        /// <summary>The round's current phase as the client knows it (rstate), or Sandbox.</summary>
        public RoundPhase Phase => HasRaceState ? RaceState.Phase : RoundPhase.Sandbox;

        private void Update()
        {
            var now = Now;
            var dt = _lastNow > 0 ? now - _lastNow : 0;
            _lastNow = now;
            FollowServerScene();
            EnsurePrediction();

            if (_nm != null && _nm.IsConnectedClient && !_nm.IsServer)
            {
                // The clock keeps running while we are not sending yet (lobby / map loading), so the first
                // commands are stamped for the right tick instead of a stale one.
                var n = Clock.Advance(dt);
                for (var i = 0; i < n; i++)
                {
                    if (Active)
                        SampleAndSend();
                    else
                        Clock.NextTick();
                }
            }

            Remote.BeginFrame(now, (float)dt);
            Prediction?.UpdateSmoothing((float)dt);

            if (now - _statsAt >= 1.0)
            {
                var span = (float)(now - _statsAt);
                BytesInPerSec = (BytesIn - _statBytesIn) / span;
                BytesOutPerSec = (BytesOut - _statBytesOut) / span;
                SnapsPerSec = (SnapsIn - _statSnaps) / span;
                _statBytesIn = BytesIn;
                _statBytesOut = BytesOut;
                _statSnaps = SnapsIn;
                _statsAt = now;
            }
        }

        private void SampleAndSend()
        {
            var cmd = new InputCmd { Seq = ++Seq, Tick = Clock.NextTick() };
            InputSource?.Sample(ref cmd);
            if (InputLocked(cmd.Tick))
            {
                cmd.MoveX = cmd.MoveY = 0;
                cmd.Buttons = 0; // intro / countdown: the server ignores input for ticks before the RUSH tick
            }

            // newest first, keep 3
            for (var i = Math.Min(_recentCount, NetMessages.MaxInputCmds - 1); i > 0; i--)
                _recent[i] = _recent[i - 1];
            _recent[0] = cmd;
            if (_recentCount < NetMessages.MaxInputCmds)
                _recentCount++;

            var len = NetMessages.EncodeInput(_inBuf, new ReadOnlySpan<InputCmd>(_recent, 0, _recentCount));
            using var w = new FastBufferWriter(len, Allocator.Temp);
            w.WriteBytesSafe(_inBuf, len);
            _nm.CustomMessagingManager.SendNamedMessage(Protocol.Msg.Input, NetworkManager.ServerClientId, w, InputDelivery);
            BytesOut += len;
            InputsOut++;
            LastSentCmd = cmd;

            if (!OwnOutOfSim)
                Prediction?.Predict(in cmd);
        }

        public InputCmd LastSentCmd { get; private set; }

        /// <summary>Our race byte from the newest snapshot (<see cref="PlayerState.Race"/>).</summary>
        public byte OwnRace { get; private set; }

        /// <summary>True once we finished the race: the server no longer simulates us, so we are drawn from snapshots and spectate.</summary>
        public bool OwnOutOfSim { get; private set; }

        /// <summary>Race rule shared with the server: commands for ticks before the round's RUSH tick are neutral.</summary>
        public bool InputLocked(uint tick)
        {
            if (!Round.HasValue || !HasRaceState)
                return false;
            var ph = RaceState.Phase;
            return (ph == RoundPhase.Intro || ph == RoundPhase.Countdown) && tick < Round.Value.RoundStartTick || ph == RoundPhase.Ended;
        }

        public bool HasSnapshots => Remote.HasData;
        public ref readonly Interpolation.Snap Newest => ref Remote.Newest;

        public double LatestSnapshotAge => Remote.HasData ? Now - Remote.Newest.ReceivedAt : double.NaN;

        /// <summary>Interpolation delay currently applied to remote players (ms).</summary>
        public float RenderDelayMs => Remote.Delay * 1000f;

        /// <summary>
        /// Render state of <paramref name="slotId"/>: the local player from prediction (when ready), everyone else
        /// interpolated (doc 04 §4.4).
        /// </summary>
        public bool TrySample(int slotId, out Vector3 feet, out Vector3 vel, out byte flags, out byte yaw, out byte stun)
        {
            feet = vel = Vector3.zero;
            flags = yaw = stun = 0;
            if (slotId == OwnSlot && Prediction != null && Prediction.HasState && Active && !OwnOutOfSim)
            {
                var p = Prediction.RenderPosition(TickFraction);
                var s = Prediction.State;
                feet = new Vector3(p.X, p.Y, p.Z);
                var v = s.Velocity;
                vel = new Vector3(v.X, v.Y, v.Z);
                flags = s.Flags;
                yaw = s.Yaw;
                stun = s.StunTicks;
                return true;
            }

            if (!Remote.Sample(slotId, out var f, out var rv, out var near))
                return false;
            feet = new Vector3(f.X, f.Y, f.Z);
            vel = new Vector3(rv.X, rv.Y, rv.Z);
            flags = near.Flags;
            yaw = near.Yaw;
            stun = near.StunTicks;
            return true;
        }

        /// <summary>Hazard render time: the local predicted timeline (fractional tick), so what you touch is where it is drawn.</summary>
        public double HazardRenderTick => Prediction != null && Prediction.HasState && !OwnOutOfSim ? Prediction.Tick - 1 + TickFraction : Remote.RenderTick;

        /// <summary>The tick the local player's inputs are stamped for now (the predicted present), or the interpolated server time.</summary>
        public double LocalTick => Prediction != null && Prediction.HasState && !OwnOutOfSim ? Prediction.Tick + TickFraction : Remote.RenderTick;
    }
}
