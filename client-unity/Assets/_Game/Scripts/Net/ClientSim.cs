using System;
using FallRush.Shared;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

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
        private readonly byte[] _msgBuf = new byte[512];
        private RoundParams _predictionRound;

        /// <summary>Scene name for a map id (dev sandbox = 255).</summary>
        public static string SceneFor(byte mapId) => mapId == MapPoolV1.BlockPartyDash ? NetBootstrap.BlockPartyDashScene : NetBootstrap.SandboxScene;

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
                for (var i = 0; i < h.Count; i++)
                {
                    if (_scratch[i].Slot != h.OwnSlot)
                        continue;
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
            var cfg = MovementConfig.For(round.Modifier);
            _world = map.CreateWorld(transform, cfg, in round, out var hazards);
            Prediction = new ClientPrediction(cfg, _world, hazards);
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

            Prediction?.Predict(in cmd);
        }

        public InputCmd LastSentCmd { get; private set; }

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
            if (slotId == OwnSlot && Prediction != null && Prediction.HasState && Active)
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
        public double HazardRenderTick => Prediction != null && Prediction.HasState ? Prediction.Tick - 1 + TickFraction : Remote.RenderTick;
    }
}
