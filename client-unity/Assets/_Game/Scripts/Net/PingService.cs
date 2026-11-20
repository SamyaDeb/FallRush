using FallRush.Shared;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// M1's only custom message pair. Client sends <c>ping</c> (client timestamp) every 500 ms;
    /// the server echoes it in <c>pong</c> together with the connected-player count.
    /// RTT p50/p95 are computed over the last 60 samples.
    /// </summary>
    [RequireComponent(typeof(NetBootstrap))]
    public sealed class PingService : MonoBehaviour
    {
        public const float IntervalSeconds = 0.5f;
        public const int WindowSize = 60;

        // Unreliable: one datagram, no resend logic in the measurement (over WebSockets it is TCP anyway).
        private const NetworkDelivery Delivery = NetworkDelivery.Unreliable;

        public static PingService Instance { get; private set; }

        private readonly RollingSamples _rttMs = new RollingSamples(WindowSize);
        private NetworkManager _nm;
        private double _nextPingAt;

        public int SampleCount => _rttMs.Count;
        public float LastRttMs { get; private set; } = float.NaN;
        public float P50Ms { get; private set; } = float.NaN;
        public float P95Ms { get; private set; } = float.NaN;
        public int ServerPlayerCount { get; private set; }

        private void Awake()
        {
            Instance = this;
            _nm = GetComponent<NetworkManager>();
            _nm.OnServerStarted += RegisterServer;
            _nm.OnClientStarted += RegisterClient;
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            if (_nm == null)
                return;
            _nm.OnServerStarted -= RegisterServer;
            _nm.OnClientStarted -= RegisterClient;
        }

        private void RegisterServer()
        {
            _nm.CustomMessagingManager.RegisterNamedMessageHandler(Protocol.Msg.Ping, OnPingOnServer);
        }

        private void RegisterClient()
        {
            if (_nm.IsServer)
                return;
            _rttMs.Clear();
            LastRttMs = P50Ms = P95Ms = float.NaN;
            ServerPlayerCount = 0;
            _nextPingAt = 0;
            _nm.CustomMessagingManager.RegisterNamedMessageHandler(Protocol.Msg.Pong, OnPongOnClient);
        }

        private void OnPingOnServer(ulong senderClientId, FastBufferReader reader)
        {
            if (!reader.TryBeginRead(sizeof(double)))
                return; // malformed; drop
            reader.ReadValue(out double clientTime);

            var players = NetBootstrap.Instance != null ? NetBootstrap.Instance.ServerConnectedCount : 0;
            using var writer = new FastBufferWriter(sizeof(double) + sizeof(byte), Allocator.Temp);
            writer.WriteValueSafe(clientTime);
            writer.WriteValueSafe((byte)Mathf.Clamp(players, 0, 255));
            _nm.CustomMessagingManager.SendNamedMessage(Protocol.Msg.Pong, senderClientId, writer, Delivery);
        }

        private void OnPongOnClient(ulong senderClientId, FastBufferReader reader)
        {
            if (!reader.TryBeginRead(sizeof(double) + sizeof(byte)))
                return;
            reader.ReadValue(out double sentAt);
            reader.ReadValue(out byte players);

            var rtt = (float)((Time.realtimeSinceStartupAsDouble - sentAt) * 1000.0);
            if (rtt < 0f || float.IsNaN(rtt))
                return;
            LastRttMs = rtt;
            ServerPlayerCount = players;
            _rttMs.Add(rtt);
            _rttMs.Percentiles(0.5f, 0.95f, out var p50, out var p95);
            P50Ms = p50;
            P95Ms = p95;
        }

        private void Update()
        {
            if (_nm == null || _nm.IsServer || !_nm.IsConnectedClient)
                return;

            var now = Time.realtimeSinceStartupAsDouble;
            if (now < _nextPingAt)
                return;
            _nextPingAt = now + IntervalSeconds;

            using var writer = new FastBufferWriter(sizeof(double), Allocator.Temp);
            writer.WriteValueSafe(now);
            _nm.CustomMessagingManager.SendNamedMessage(Protocol.Msg.Ping, NetworkManager.ServerClientId, writer, Delivery);
        }
    }
}
