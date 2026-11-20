using System;
using System.Diagnostics;
using FallRush.Shared;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FallRush.Net
{
    /// <summary>
    /// Server side of the M2 movement protocol: the 30 Hz authoritative loop (doc 04 §4.2 steps 1, 2, 5) around
    /// <see cref="SimCore"/>. Lives on the NetworkManager object on the server only.
    /// <list type="bullet">
    /// <item><c>in</c> messages arrive in NGO's EarlyUpdate and are validated into each player's tick-keyed buffer.</item>
    /// <item>NGO's <see cref="NetworkTickSystem.Tick"/> (PreUpdate, 30 Hz) runs one <see cref="SimCore.Step"/> per tick.</item>
    /// <item>Every <see cref="SnapshotEveryTicks"/> ticks each human gets a <c>snap</c>; NGO flushes sends in PostLateUpdate.</item>
    /// </list>
    /// Args: <c>-fallrushBots N</c> (fill to N occupants with TEST bots, env <c>GAMESERVER_BOTS</c>),
    /// <c>-fallrushSnapHz 15|30</c> (env <c>GAMESERVER_SNAP_HZ</c>).
    /// </summary>
    public sealed class ServerSim : MonoBehaviour
    {
        public const int ViolationsPerMinuteKick = 50; // doc 11 §2
        private const NetworkDelivery SnapDelivery = NetworkDelivery.UnreliableSequenced;

        public static ServerSim Instance { get; private set; }

        public SimCore Core { get; private set; }
        public int SnapshotEveryTicks { get; private set; } = 2;
        public int BotTarget { get; private set; }

        // ---- stats (cumulative; ServerStats logs deltas) ----
        public long SnapshotsSent;
        public long SnapBytesSent;
        public long InputMessages;
        public long InputBytes;
        public long Kicks;
        /// <summary>Input counters of players who already left (so 10 s deltas never go negative).</summary>
        public InputCounters Retired;
        public readonly RollingSamples StepMs = new RollingSamples(1024);

        private NetworkManager _nm;
        private bool _tickHooked;
        private readonly byte[] _inBuf = new byte[NetMessages.InputMaxBytes + 8];
        private readonly InputCmd[] _inCmds = new InputCmd[NetMessages.MaxInputCmds];
        private readonly byte[] _snapBody = new byte[Protocol.MaxPlayers * PlayerState.Size];
        private readonly byte[] _snapHeader = new byte[SnapHeader.Size];
        private readonly PlayerState[] _players = new PlayerState[Protocol.MaxPlayers];
        private readonly long[] _violationWindowStart = new long[Protocol.MaxPlayers];
        private readonly long[] _violationsAtWindowStart = new long[Protocol.MaxPlayers];

        public static void Install(NetworkManager nm, MapInfo map)
        {
            if (Instance != null)
                return;
            var sim = nm.gameObject.AddComponent<ServerSim>();
            sim.Init(nm, map);
        }

        private void Init(NetworkManager nm, MapInfo map)
        {
            Instance = this;
            _nm = nm;

            Physics.simulationMode = SimulationMode.Script;
            Physics.SyncTransforms();
            var cfg = MovementConfig.Default;
            var world = map.CreateWorld(transform, cfg, out var hazards);
            Core = new SimCore(cfg, world, map.SpawnPoints(), hazards);
            Core.SetBotArea(map.botAreaCenter.x, map.botAreaCenter.z, map.botAreaHalfExtents.x, map.botAreaHalfExtents.y);

            var snapHz = ArgInt("-fallrushSnapHz", "GAMESERVER_SNAP_HZ", 15);
            SnapshotEveryTicks = snapHz >= 30 ? 1 : 2;
            BotTarget = Mathf.Clamp(ArgInt("-fallrushBots", "GAMESERVER_BOTS", 0), 0, Protocol.MaxPlayers);
            FillBots();

            nm.CustomMessagingManager.RegisterNamedMessageHandler(Protocol.Msg.Input, OnInput);
            nm.OnClientConnectedCallback += OnClientConnected;
            nm.OnClientDisconnectCallback += OnClientDisconnected;
            HookTick();
            foreach (var id in nm.ConnectedClientsIds)
                OnClientConnected(id); // anyone who connected while the map was loading

            Debug.Log($"[gs] sim ready map={map.gameObject.scene.name} movement={MovementConfigData.File} sha256={MovementConfigData.Sha256Hex.Substring(0, 16)}… snapHz={30 / SnapshotEveryTicks} hazards={hazards.Sweepers.Length} sweeper(s) + {hazards.Platforms.Length} platform(s) bots={Core.BotCount} (TEST bots, target {BotTarget})");
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            if (_nm == null)
                return;
            if (_tickHooked && _nm.NetworkTickSystem != null)
                _nm.NetworkTickSystem.Tick -= OnTick;
            _nm.OnClientConnectedCallback -= OnClientConnected;
            _nm.OnClientDisconnectCallback -= OnClientDisconnected;
            _nm.CustomMessagingManager?.UnregisterNamedMessageHandler(Protocol.Msg.Input);
        }

        private void HookTick()
        {
            if (_tickHooked || _nm.NetworkTickSystem == null)
                return;
            _nm.NetworkTickSystem.Tick += OnTick;
            _tickHooked = true;
        }

        private void Update() => HookTick();

        private void FillBots()
        {
            while (Core.HumanCount + Core.BotCount < BotTarget && Core.AddBot() >= 0)
            {
            }
        }

        private void OnClientConnected(ulong clientId)
        {
            if (clientId == NetworkManager.ServerClientId)
                return;
            var slot = Core.AddHuman(clientId);
            if (slot < 0)
            {
                Debug.LogWarning($"[gs] no slot for client={clientId}; disconnecting");
                _nm.DisconnectClient(clientId, $"Server full ({Protocol.MaxPlayers}/{Protocol.MaxPlayers}).");
                return;
            }

            _violationWindowStart[slot] = Core.Tick;
            _violationsAtWindowStart[slot] = 0;
            Debug.Log($"[gs] slot {slot} → client={clientId} humans={Core.HumanCount} bots={Core.BotCount}");
        }

        private void OnClientDisconnected(ulong clientId)
        {
            var slot = Core.FindSlot(clientId);
            if (slot < 0)
                return;
            var c = Core.Input(slot).Counters;
            Retired.Messages += c.Messages;
            Retired.Accepted += c.Accepted;
            Retired.Redundant += c.Redundant;
            Retired.Late += c.Late;
            Retired.FarFuture += c.FarFuture;
            Retired.Flood += c.Flood;
            Retired.Burst += c.Burst;
            Retired.Malformed += c.Malformed;
            Retired.Repeated += c.Repeated;
            Retired.Neutral += c.Neutral;
            Retired.Clamped += c.Clamped;
            Core.Remove(slot);
            FillBots();
            Debug.Log($"[gs] slot {slot} freed (client={clientId}) humans={Core.HumanCount} bots={Core.BotCount}");
        }

        private void OnInput(ulong sender, FastBufferReader reader)
        {
            InputMessages++;
            var slot = Core.FindSlot(sender);
            if (slot < 0)
                return;
            var input = Core.Input(slot);
            if (!input.AdmitMessage())
                return; // flood: dropped before decoding

            var len = reader.Length - reader.Position;
            InputBytes += len;
            if (len <= 0 || len > NetMessages.InputMaxBytes)
            {
                input.CountMalformed();
                return;
            }

            var buf = _inBuf;
            reader.ReadBytesSafe(ref buf, len);
            if (!NetMessages.TryDecodeInput(new ReadOnlySpan<byte>(buf, 0, len), _inCmds, out var count, out _))
            {
                input.CountMalformed();
                return;
            }

            // Oldest first, so seq freshness accepts them in order.
            for (var i = count - 1; i >= 0; i--)
                input.Offer(_inCmds[i], Core.NextTick);
        }

        private void OnTick()
        {
            var t0 = Stopwatch.GetTimestamp();
            Core.Step();
            if (Core.Tick % (uint)SnapshotEveryTicks == 0)
                SendSnapshots();
            CheckViolations();
            StepMs.Add((float)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency));
        }

        private void SendSnapshots()
        {
            var count = Core.WritePlayerStates(_players);
            var bodyLen = NetMessages.EncodePlayers(_snapBody, new ReadOnlySpan<PlayerState>(_players, 0, count));
            var size = SnapHeader.Size + bodyLen;

            for (var slot = 0; slot < SimCore.MaxSlots; slot++)
            {
                ref var p = ref Core.Slots[slot];
                if (!p.Active || p.Bot)
                    continue;
                var h = Core.HeaderFor(slot, count);
                NetMessages.EncodeSnapHeader(_snapHeader, h);
                using var w = new FastBufferWriter(size, Allocator.Temp);
                w.WriteBytesSafe(_snapHeader, SnapHeader.Size);
                w.WriteBytesSafe(_snapBody, bodyLen);
                _nm.CustomMessagingManager.SendNamedMessage(Protocol.Msg.Snap, p.ClientId, w, SnapDelivery);
                SnapshotsSent++;
                SnapBytesSent += size;
            }
        }

        private void CheckViolations()
        {
            for (var slot = 0; slot < SimCore.MaxSlots; slot++)
            {
                ref var p = ref Core.Slots[slot];
                if (!p.Active || p.Bot)
                    continue;
                var v = Core.Input(slot).Counters.Violations;
                if (Core.Tick - _violationWindowStart[slot] >= 60 * 30)
                {
                    _violationWindowStart[slot] = Core.Tick;
                    _violationsAtWindowStart[slot] = v;
                }

                if (v - _violationsAtWindowStart[slot] >= ViolationsPerMinuteKick)
                {
                    Kicks++;
                    var c = Core.Input(slot).Counters;
                    Debug.LogWarning($"[gs] kick client={p.ClientId} slot={slot}: {v - _violationsAtWindowStart[slot]} input violations in a minute (far={c.FarFuture} flood={c.Flood} malformed={c.Malformed})");
                    var id = p.ClientId;
                    _violationsAtWindowStart[slot] = v;
                    _nm.DisconnectClient(id, "Disconnected: too many invalid input messages.");
                }
            }
        }

        private static int ArgInt(string arg, string env, int fallback)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], arg, StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out var v))
                    return v;
            var e = Environment.GetEnvironmentVariable(env);
            return int.TryParse(e, out var ev) ? ev : fallback;
        }
    }
}
