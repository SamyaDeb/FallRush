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
    /// <para>M2a: samples input at the 30 Hz tick chosen by <see cref="TickSync"/>, sends <c>in</c> (newest 3 commands),
    /// receives <c>snap</c> into a small ring, and serves interpolated states for every player — including the local one,
    /// so in M2a local control feels the full round trip (prediction is M2b).</para>
    /// </summary>
    public sealed class ClientSim : MonoBehaviour
    {
        public const int SnapRing = 32;
        public const float InterpDelaySeconds = 0.1f;
        private const NetworkDelivery InputDelivery = NetworkDelivery.Unreliable;

        public static ClientSim Instance { get; private set; }

        public struct Snap
        {
            public uint Tick;
            public double ReceivedAt;
            public SnapHeader Header;
            public int Count;
            public PlayerState[] Players;
        }

        public readonly TickSync Clock = new TickSync(MovementConfig.Default.TickRate);
        public IInputSource InputSource;

        /// <summary>True while the map is loaded and we should be sending input.</summary>
        public bool Active { get; set; }

        public int OwnSlot { get; private set; } = SnapHeader.NoSlot;
        public uint LastAckSeq { get; private set; }
        public uint Seq { get; private set; }

        // ---- stats (HUD) ----
        public long BytesIn, BytesOut, SnapsIn, InputsOut, BadSnaps, OldSnaps;
        public float BytesInPerSec { get; private set; }
        public float BytesOutPerSec { get; private set; }
        public float SnapsPerSec { get; private set; }
        public float RenderDelayMs { get; private set; }

        private readonly Snap[] _ring = new Snap[SnapRing];
        private int _newest = -1;
        private int _stored;
        private readonly PlayerState[] _scratch = new PlayerState[Protocol.MaxPlayers];
        private readonly byte[] _snapBuf = new byte[NetMessages.SnapMaxBytes + 8];
        private readonly byte[] _inBuf = new byte[NetMessages.InputMaxBytes];
        private readonly InputCmd[] _recent = new InputCmd[NetMessages.MaxInputCmds];
        private int _recentCount;
        private NetworkManager _nm;
        private double _lastNow;
        private double _statsAt;
        private long _statBytesIn, _statBytesOut, _statSnaps;
        private double _offsetEma; // (server tick time − local time), smoothed
        private bool _offsetInit;

        private void Awake()
        {
            Instance = this;
            for (var i = 0; i < SnapRing; i++)
                _ring[i].Players = new PlayerState[Protocol.MaxPlayers];
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
        }

        public void ResetSession()
        {
            Clock.Reset();
            OwnSlot = SnapHeader.NoSlot;
            LastAckSeq = 0;
            Seq = 0;
            _recentCount = 0;
            _newest = -1;
            _stored = 0;
            _offsetInit = false;
        }

        private static double Now => Time.realtimeSinceStartupAsDouble;

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

            if (_newest >= 0 && h.ServerTick <= _ring[_newest].Tick)
            {
                OldSnaps++;
                return;
            }

            var now = Now;
            var next = (_newest + 1) % SnapRing;
            ref var slot = ref _ring[next];
            Array.Copy(_scratch, slot.Players, h.Count);
            slot.Tick = h.ServerTick;
            slot.ReceivedAt = now;
            slot.Header = h;
            slot.Count = h.Count;
            _newest = next;
            if (_stored < SnapRing)
                _stored++;
            SnapsIn++;
            OwnSlot = h.OwnSlot;
            LastAckSeq = h.AckSeq;

            var rtt = PingService.Instance != null ? PingService.Instance.P50Ms : float.NaN;
            Clock.OnSnapshot(h.ServerTick, h.InputLead, rtt, now);

            var offset = h.ServerTick * Clock.Dt - now;
            if (!_offsetInit)
            {
                _offsetEma = offset;
                _offsetInit = true;
            }
            else
            {
                // Follow later (= fresher) arrivals quickly, earlier ones slowly.
                _offsetEma += (offset - _offsetEma) * (offset > _offsetEma ? 0.2 : 0.02);
            }
        }

        private void Update()
        {
            var now = Now;
            var dt = _lastNow > 0 ? now - _lastNow : 0;
            _lastNow = now;

            if (_nm != null && _nm.IsConnectedClient && !_nm.IsServer && Active)
            {
                var n = Clock.Advance(dt);
                for (var i = 0; i < n; i++)
                    SampleAndSend();
            }

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
        }

        public InputCmd LastSentCmd { get; private set; }

        public bool HasSnapshots => _newest >= 0;
        public ref readonly Snap Newest => ref _ring[_newest];

        /// <summary>Local time → server tick (fractional) used for rendering, from the smoothed arrival offset.</summary>
        public double RenderTick(double now, float delaySeconds) => (now + _offsetEma - delaySeconds) / Clock.Dt;

        public double LatestSnapshotAge => _newest >= 0 ? Now - _ring[_newest].ReceivedAt : double.NaN;

        /// <summary>
        /// Interpolated state of <paramref name="slotId"/> at render time (no extrapolation: clamps to the newest snapshot).
        /// </summary>
        public bool TrySample(int slotId, out Vector3 feet, out Vector3 vel, out byte flags, out byte yaw)
        {
            feet = vel = Vector3.zero;
            flags = yaw = 0;
            if (_newest < 0)
                return false;
            var now = Now;
            var rt = RenderTick(now, InterpDelaySeconds);
            RenderDelayMs = (float)((_ring[_newest].Tick - rt) * Clock.Dt * 1000.0);

            // Find a (older) and b (newer) with a.Tick ≤ rt ≤ b.Tick.
            var bIdx = _newest;
            var aIdx = -1;
            for (var k = 1; k < _stored; k++)
            {
                var i = (_newest - k + SnapRing) % SnapRing;
                if (_ring[i].Tick <= rt)
                {
                    aIdx = i;
                    break;
                }

                bIdx = i;
            }

            if (!Find(in _ring[bIdx], slotId, out var pb))
                return false;
            if (aIdx < 0 || rt >= _ring[_newest].Tick || !Find(in _ring[aIdx], slotId, out var pa))
            {
                Write(in pb, out feet, out vel);
                flags = pb.Flags;
                yaw = pb.Yaw;
                return true;
            }

            var span = (double)(_ring[bIdx].Tick - _ring[aIdx].Tick);
            var t = span > 0 ? (float)((rt - _ring[aIdx].Tick) / span) : 1f;
            Write(in pa, out var fa, out var va);
            Write(in pb, out var fb, out var vb);
            // Big jumps (respawn teleports) snap instead of sliding across the map.
            feet = (fb - fa).sqrMagnitude > 4f ? fb : Vector3.LerpUnclamped(fa, fb, t);
            vel = Vector3.LerpUnclamped(va, vb, t);
            flags = t < 0.5f ? pa.Flags : pb.Flags;
            yaw = t < 0.5f ? pa.Yaw : pb.Yaw;
            return true;
        }

        public bool IsSlotPresent(int slotId) => _newest >= 0 && Find(in _ring[_newest], slotId, out _);

        private static bool Find(in Snap s, int slotId, out PlayerState p)
        {
            for (var i = 0; i < s.Count; i++)
            {
                if (s.Players[i].Slot == slotId)
                {
                    p = s.Players[i];
                    return true;
                }
            }

            p = default;
            return false;
        }

        private static void Write(in PlayerState p, out Vector3 feet, out Vector3 vel)
        {
            feet = new Vector3(p.PosX * 0.001f, p.PosY * 0.001f, p.PosZ * 0.001f);
            vel = new Vector3(p.VelX * 0.01f, p.VelY * 0.01f, p.VelZ * 0.01f);
        }
    }
}
