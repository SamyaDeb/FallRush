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
        /// <summary>Snapshots not sent because the client stopped sending input (hidden tab / stalled, R12).</summary>
        public long SnapshotsPausedIdle;
        /// <summary>After this many ticks without input from a client that did send before, pause its snapshots (R12).</summary>
        public const int PauseSnapshotsAfterTicks = 30;
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
        private readonly uint[] _lastInputTick = new uint[Protocol.MaxPlayers]; // 0 = no input yet

        public static void Install(NetworkManager nm, MapInfo map)
        {
            if (Instance != null)
                return;
            var sim = nm.gameObject.AddComponent<ServerSim>();
            sim.Init(nm, map);
        }

        public MapInfo Map { get; private set; }
        public RoundParams Round { get; private set; }
        public byte[] MapHash { get; private set; }
        /// <summary>Non-null when the map's resolved defs failed the allowlist: the round is refused (nothing is simulated).</summary>
        public string Refused { get; private set; }
        private PhysxMotorWorld _world;
        private CapsuleCollider _probe;
        private readonly byte[] _roundBuf = new byte[RoundMessage.Size];
        /// <summary>M4: the match (lobby → deal → rounds → champion → receipt); null in free play (<c>-fallrushMap Sandbox</c>).</summary>
        public ServerMatch Match { get; private set; }

        private void Init(NetworkManager nm, MapInfo map)
        {
            Instance = this;
            _nm = nm;
            Map = map;

            Physics.simulationMode = SimulationMode.Script;
            Physics.SyncTransforms();
            MapHash = map.ContentHash;
            var matchMode = NetBootstrap.MatchMode;
            var p = matchMode ? new RoundParams { MapId = map.MapId } : ChooseRoundParams(map, out _, out _);
            // Free play and the lobby: hazard time = server tick. Round maps get their RUSH tick from the match director.
            p.RoundStartTick = 0;
            var cfg = NetBootstrap.MovementFor(p.Modifier);
            _probe = PhysxMotorWorld.CreateProbe(transform, cfg.Radius, cfg.Height);
            _world = map.CreateWorld(_probe, cfg, in p, out var hazards);
            Core = new SimCore(cfg, _world, map.SpawnPoints(), hazards, map.SpawnYaws());
            Core.SetBotArea(map.botAreaCenter.x, map.botAreaCenter.z, map.botAreaHalfExtents.x, map.botAreaHalfExtents.y);
            // MEASUREMENT: -fallrushBotArea cx,cz,hx,hz (e.g. 0,54,36,3 = the sandbox kit row).
            var area = ArgStr("-fallrushBotArea", "GAMESERVER_BOT_AREA");
            if (area != null)
            {
                var a = area.Split(',');
                if (a.Length == 4 && float.TryParse(a[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cx) &&
                    float.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cz) &&
                    float.TryParse(a[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hx) &&
                    float.TryParse(a[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hzz))
                    Core.SetBotArea(cx, cz, hx, hzz);
            }

            Round = p;
            var problems = HazardAllowlist.Validate(hazards, map.SpawnsMm());
            if (problems.Count > 0)
            {
                Refused = string.Join("; ", problems);
                Debug.LogError($"[gs] ROUND REFUSED map={map.gameObject.scene.name} {p}: {problems.Count} hazard parameter(s) outside the allowlist: {Refused}");
            }

            var snapHz = ArgInt("-fallrushSnapHz", "GAMESERVER_SNAP_HZ", 15);
            SnapshotEveryTicks = snapHz >= 30 ? 1 : 2;
            BotTarget = Mathf.Clamp(ArgInt("-fallrushBots", "GAMESERVER_BOTS", 0), 0, Protocol.MaxPlayers);
            if (!matchMode)
                FillBots(); // free play: TEST bots now; in a match bots fill the roster at the lock (M4-2)

            nm.CustomMessagingManager.RegisterNamedMessageHandler(Protocol.Msg.Input, OnInput);
            nm.OnClientConnectedCallback += OnClientConnected;
            nm.OnClientDisconnectCallback += OnClientDisconnected;
            if (matchMode)
                Match = new ServerMatch(this, nm);
            HookTick();
            foreach (var id in nm.ConnectedClientsIds)
                OnClientConnected(id); // anyone who connected while the map was loading

            Debug.Log($"[gs] sim ready map={map.gameObject.scene.name} mapHash={MapContent.Short(MapHash)} ({Sha256.Hex(MapHash)}) round: {p} movement={MovementConfigData.File} sha256={MovementConfigData.Sha256Hex.Substring(0, 16)}… modifiers={ModifiersData.Sha256Hex.Substring(0, 16)}… snapHz={30 / SnapshotEveryTicks} hazards={hazards.Count} ({hazards.Sweepers.Length} spinner, {hazards.Platforms.Length} platform, {hazards.Conveyors.Length} conveyor, {hazards.Pads.Length} pad, {hazards.TurnTables.Length} turn table, {hazards.Doors.Length} door, {hazards.Mallets.Length} mallet, {hazards.Bumpers.Length} bumper) bots={Core.BotCount} (TEST bots, target {BotTarget})");
        }

        /// <summary>
        /// Round parameter source. M3: the dev seed (<c>-fallrushSeed</c>, <c>-fallrushVariant</c>, <c>-fallrushModifier</c>;
        /// env <c>GAMESERVER_SEED</c>/<c>_VARIANT</c>/<c>_MODIFIER</c>); unset variant/modifier are derived from the seed.
        /// TODO(M6): one-line swap to the VRF deal for this round.
        /// </summary>
        public static RoundParams ChooseRoundParams(MapInfo map) => ChooseRoundParams(map, out _, out _);

        public static RoundParams ChooseRoundParams(MapInfo map, out bool fixedVariant, out bool fixedModifier)
        {
            var seedArg = ArgStr("-fallrushSeed", "GAMESERVER_SEED");
            ulong seed = 0x00C0FFEE;
            if (!string.IsNullOrEmpty(seedArg))
            {
                if (seedArg.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    ulong.TryParse(seedArg.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out seed);
                else
                    ulong.TryParse(seedArg, out seed);
            }

            var variant = -1;
            var v = ArgStr("-fallrushVariant", "GAMESERVER_VARIANT");
            if (!string.IsNullOrEmpty(v))
                variant = v == "B" || v == "b" || v == "1" ? 1 : 0;
            var modifier = -1;
            var m = ArgStr("-fallrushModifier", "GAMESERVER_MODIFIER");
            if (!string.IsNullOrEmpty(m) && ModifierEffects.TryParse(m, out var mid))
                modifier = mid;
            fixedVariant = variant >= 0;
            fixedModifier = modifier >= 0;
            var p = RoundParams.FromDevSeed(map.MapId, seed, variant, modifier);
            if (modifier < 0 && (map.definition == null || !map.definition.IsRace))
                p.Modifier = 0; // sandbox: no random modifier unless asked
            return p;
        }

        /// <summary>
        /// New round parameters (race rounds): modifier → motor config, variant/seed → hazards, allowlist check. Slots and
        /// connections stay; everyone is put back on their spawn. Returns the allowlist problems (round refused) or null.
        /// </summary>
        public System.Collections.Generic.List<string> ApplyRound(ref RoundParams p, uint roundStartTick)
        {
            p.RoundStartTick = roundStartTick;
            var cfg = NetBootstrap.MovementFor(p.Modifier);
            _world = Map.CreateWorld(_probe, cfg, in p, out var hazards);
            var problems = HazardAllowlist.Validate(hazards, Map.SpawnsMm());
            if (problems.Count > 0)
            {
                Refused = string.Join("; ", problems);
                Debug.LogError($"[gs] ROUND REFUSED {p}: {Refused}");
                return problems;
            }

            Core.Reconfigure(cfg, _world, hazards, Map.SpawnPoints(), Map.SpawnYaws());
            Round = p;
            return null;
        }

        public void SetRound(in RoundParams p) => Round = p;

        /// <summary>
        /// M4 scene flow: the match director's next map is loaded (or the same scene gets new round params). Rebuilds the
        /// motor world and hazards for <paramref name="p"/>, keeps slots and connections, puts everyone on their spawn.
        /// </summary>
        public void SwitchMap(MapInfo map, RoundParams p)
        {
            Map = map;
            MapHash = map.ContentHash;
            Physics.SyncTransforms();
            var cfg = NetBootstrap.MovementFor(p.Modifier);
            _world = map.CreateWorld(_probe, cfg, in p, out var hazards);
            Refused = null;
            var problems = HazardAllowlist.Validate(hazards, map.SpawnsMm());
            if (problems.Count > 0)
            {
                Refused = string.Join("; ", problems);
                Debug.LogError($"[gs] ROUND REFUSED map={map.gameObject.scene.name} {p}: {Refused}");
            }

            Core.Reconfigure(cfg, _world, hazards, map.SpawnPoints(), map.SpawnYaws());
            Core.SetBotArea(map.botAreaCenter.x, map.botAreaCenter.z, map.botAreaHalfExtents.x, map.botAreaHalfExtents.y);
            Round = p;
            OnWorldChanged(hazards);
            Debug.Log($"[gs] map {map.gameObject.scene.name} mapHash={MapContent.Short(MapHash)} {p} hazards={hazards.Count} reactive={hazards.ReactiveCount}");
        }

        /// <summary>Called by NetBootstrap when a map scene finished loading (after the first one).</summary>
        public void OnMapLoaded(MapInfo map)
        {
            if (Match != null)
                Match.OnMapLoaded(map);
        }

        /// <summary>A human (re)took a slot: fresh flood window and snapshot-pause state.</summary>
        public void OnSlotAttached(int slot)
        {
            _violationWindowStart[slot] = Core.Tick;
            _violationsAtWindowStart[slot] = 0;
            _lastInputTick[slot] = 0;
        }

        public void BroadcastRound()
        {
            foreach (var id in _nm.ConnectedClientsIds)
                if (id != NetworkManager.ServerClientId)
                    SendRound(id);
        }

        /// <summary>Sends the round message (params + map hash) to one client (reliable).</summary>
        public void SendRound(ulong clientId)
        {
            var msg = new RoundMessage { P = Round, MapHash = MapHash };
            var n = msg.Encode(_roundBuf);
            using var w = new FastBufferWriter(n, Allocator.Temp);
            w.WriteBytesSafe(_roundBuf, n);
            _nm.CustomMessagingManager.SendNamedMessage(Protocol.Msg.Round, clientId, w, NetworkDelivery.ReliableSequenced);
        }

        public static string ArgStr(string arg, string env)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], arg, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            var e = Environment.GetEnvironmentVariable(env);
            return string.IsNullOrEmpty(e) ? null : e;
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
            Match?.Dispose();
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
            if (Match != null)
            {
                Match.OnClientConnected(clientId);
                return;
            }

            NetBootstrap.TakeHello(clientId);
            var slot = Core.AddHuman(clientId);
            if (slot < 0)
            {
                Debug.LogWarning($"[gs] no slot for client={clientId}; disconnecting");
                _nm.DisconnectClient(clientId, $"Server full ({Protocol.MaxPlayers}/{Protocol.MaxPlayers}).");
                return;
            }

            _violationWindowStart[slot] = Core.Tick;
            _violationsAtWindowStart[slot] = 0;
            _lastInputTick[slot] = 0;
            SendRound(clientId);
            Debug.Log($"[gs] slot {slot} → client={clientId} humans={Core.HumanCount} bots={Core.BotCount}");
        }

        private void OnClientDisconnected(ulong clientId)
        {
            var slot = Core.FindSlot(clientId);
            if (slot < 0)
            {
                Match?.OnClientDisconnected(clientId, -1);
                return;
            }

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
            Retired.Early += c.Early;
            Retired.Backlog += c.Backlog;
            Retired.Clamped += c.Clamped;
            if (Match != null)
            {
                Match.OnClientDisconnected(clientId, slot);
                return;
            }

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
            _lastInputTick[slot] = Core.Tick == 0 ? 1u : Core.Tick;
        }

        private void OnTick()
        {
            if (Refused != null && Match == null)
                return; // allowlist failure: the round does not start (free play)
            var t0 = Stopwatch.GetTimestamp();
            // Soak: with nobody watching, several ticks per network tick (the same code path, faster than real time).
            var steps = Match != null && Match.SimSpeed > 1 && _nm.ConnectedClientsIds.Count <= (_nm.IsHost ? 1 : 0) ? Match.SimSpeed : 1;
            for (var k = 0; k < steps; k++)
            {
                if (Match != null && !Match.BeforeStep())
                {
                    Core.SkipTick(); // the server is loading the next map this frame
                    break;
                }

                if (Refused != null && Match == null)
                    return;
                var ts = Stopwatch.GetTimestamp();
                Core.Step();
                Match?.AfterStep();
                AfterStepReactive();
                if (steps > 1)
                    StepMs.Add((float)((Stopwatch.GetTimestamp() - ts) * 1000.0 / Stopwatch.Frequency)); // soak: per simulated tick
            }

            if (Core.Tick % (uint)SnapshotEveryTicks == 0 || steps > 1)
                SendSnapshots();
            CheckViolations();
            if (steps == 1)
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
                // R12: a client that was sending input but went quiet (hidden browser tab: no frames at all) would
                // otherwise come back to a backlog of hundreds of snapshots that overflows its transport receive queue.
                // Pause until its input resumes; its first new command restarts the stream on the next snapshot tick.
                var last = _lastInputTick[slot];
                if (last != 0 && Core.Tick - last > PauseSnapshotsAfterTicks)
                {
                    SnapshotsPausedIdle++;
                    continue;
                }

                var h = Core.HeaderFor(slot, count);
                NetMessages.EncodeSnapHeader(_snapHeader, h);
                using var w = new FastBufferWriter(size, Allocator.Temp);
                w.WriteBytesSafe(_snapHeader, SnapHeader.Size);
                w.WriteBytesSafe(_snapBody, bodyLen);
                _nm.CustomMessagingManager.SendNamedMessage(Protocol.Msg.Snap, p.ClientId, w, SnapDelivery);
                SnapshotsSent++;
                SnapBytesSent += size;
            }

            if (Match == null)
                return;
            foreach (var id in Match.Viewers)
            {
                var h = Core.HeaderFor(-1, count);
                NetMessages.EncodeSnapHeader(_snapHeader, h);
                using var w = new FastBufferWriter(size, Allocator.Temp);
                w.WriteBytesSafe(_snapHeader, SnapHeader.Size);
                w.WriteBytesSafe(_snapBody, bodyLen);
                _nm.CustomMessagingManager.SendNamedMessage(Protocol.Msg.Snap, id, w, SnapDelivery);
                SnapshotsSent++;
                SnapBytesSent += size;
            }
        }

        // ------------------------------------------------------------------ reactive elements (M4; filled in by step 3)

        // State: the round's ReactiveSet log (Core.Hazards.Reactive); deltas go out after every step, the full log on
        // join / reconnect / a client's gap report. Chunks of ≤ 80 entries (1,128 B) stay under NGO's 1,296 B
        // non-fragmented message size; a "full" chunk resets the client's log, the chunks after it continue as deltas.
        public const int RxChunk = 80;
        private int _rxSent;
        private readonly byte[] _rxBuf = new byte[ReactiveCodec.HeaderSize + RxChunk * RxEntry.Size];
        public long RxBytesSent, RxMsgsSent, RxEntriesSent;

        private byte RxEpoch => Match != null ? Match.Director.LoadSerial : (byte)0;

        private void OnWorldChanged(HazardSet hazards)
        {
            _rxSent = 0;
            if (_nm == null)
                return;
            foreach (var id in _nm.ConnectedClientsIds)
                if (id != NetworkManager.ServerClientId)
                    SendReactiveFull(id); // a new round: every client starts from an empty log
        }

        private void AfterStepReactive()
        {
            var rx = Core.Hazards?.Reactive;
            if (rx == null || rx.Log.Count <= _rxSent)
                return;
            foreach (var id in _nm.ConnectedClientsIds)
                if (id != NetworkManager.ServerClientId)
                    SendRx(id, ReactiveCodec.Delta, rx.Log, _rxSent, rx.Log.Count - _rxSent);
            _rxSent = rx.Log.Count;
        }

        /// <summary>The full reactive state of the current round to one client (join, reconnect, seq gap).</summary>
        public void SendReactiveFull(ulong clientId)
        {
            var rx = Core?.Hazards?.Reactive;
            var log = rx != null ? rx.Log : new System.Collections.Generic.List<RxEntry>();
            SendRx(clientId, ReactiveCodec.Full, log, 0, log.Count);
        }

        private void SendRx(ulong clientId, byte kind, System.Collections.Generic.List<RxEntry> log, int from, int count)
        {
            var first = true;
            do
            {
                var n = Math.Min(RxChunk, count);
                var len = ReactiveCodec.Encode(_rxBuf, first ? kind : ReactiveCodec.Delta, RxEpoch, log, from, n);
                using var w = new FastBufferWriter(len, Allocator.Temp);
                w.WriteBytesSafe(_rxBuf, len);
                _nm.CustomMessagingManager.SendNamedMessage(Protocol.Msg.Reactive, clientId, w, NetworkDelivery.ReliableSequenced);
                RxBytesSent += len;
                RxMsgsSent++;
                RxEntriesSent += n;
                from += n;
                count -= n;
                first = false;
            } while (count > 0);
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

        public static int ArgInt(string arg, string env, int fallback)
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
