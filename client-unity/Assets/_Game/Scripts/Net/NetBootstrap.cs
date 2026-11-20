using System;
using System.Collections;
using System.Collections.Generic;
using FallRush.Shared;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FallRush.Net
{
    public enum ClientState
    {
        Idle,
        Connecting,
        Connected,
        Disconnected,
    }

    /// <summary>
    /// Lives on the NetworkManager prefab in the Boot scene (persisted by NetworkManager's own DontDestroyOnLoad).
    /// Picks the role, configures UnityTransport for WebSockets, runs connection approval on the server,
    /// and exposes connect / reconnect + state to client UI.
    /// </summary>
    [RequireComponent(typeof(NetworkManager), typeof(UnityTransport))]
    [DisallowMultipleComponent]
    public sealed class NetBootstrap : MonoBehaviour
    {
        public const string MenuScene = "Menu";
        public const string LobbyScene = "Lobby";
        public const string SandboxScene = "Map_Sandbox";
        public const string BlockPartyDashScene = "Map_BlockPartyDash";
        public const string LobbyMapScene = "Map_Lobby";
        public const string GooTowerScene = "Map_GooTower";
        public const string StarScrambleScene = "Map_StarScramble";
        public const string CrumbleShelfScene = "Map_CrumbleShelf";
        // server: unset = a full match (Map_Lobby first); Sandbox = M2 free play; a pool map = a one-round dev match on it.
        private const string MapArg = "-fallrushMap"; // env GAMESERVER_MAP

        /// <summary>Scene of a map id (pool ids, <see cref="MapPoolV1.Lobby"/>, <see cref="MapPoolV1.Sandbox"/>).</summary>
        public static string SceneForMap(byte mapId) => mapId switch
        {
            MapPoolV1.BlockPartyDash => BlockPartyDashScene,
            MapPoolV1.GooTower => GooTowerScene,
            MapPoolV1.StarScramble => StarScrambleScene,
            MapPoolV1.CrumbleShelf => CrumbleShelfScene,
            MapPoolV1.Lobby => LobbyMapScene,
            _ => SandboxScene,
        };

        private static string MapArgValue() => GetArgValue(MapArg) ?? Environment.GetEnvironmentVariable("GAMESERVER_MAP");

        /// <summary>Map id named by <c>-fallrushMap</c> (scene name, key or short alias), or −1.</summary>
        public static int MapIdFromArg(string v)
        {
            if (string.IsNullOrEmpty(v))
                return -1;
            if (v.StartsWith("Map_", StringComparison.Ordinal))
                v = v.Substring(4);
            for (var i = 0; i < MapPoolV1.Keys.Length; i++)
                if (string.Equals(v, MapPoolV1.Keys[i], StringComparison.OrdinalIgnoreCase))
                    return i;
            switch (v.ToLowerInvariant())
            {
                case "bpd": return MapPoolV1.BlockPartyDash;
                case "goo": return MapPoolV1.GooTower;
                case "stars": return MapPoolV1.StarScramble;
                case "crumble": return MapPoolV1.CrumbleShelf;
                case "lobby": return MapPoolV1.Lobby;
                case "sandbox": return MapPoolV1.Sandbox;
                default: return -1;
            }
        }

        /// <summary>M4: the server runs matches (the director) unless <c>-fallrushMap Sandbox</c> asks for M2 free play.</summary>
        public static bool MatchMode => MapIdFromArg(MapArgValue()) != MapPoolV1.Sandbox;

        /// <summary>Dev plan: <c>-fallrushMap GooTower</c> = a one-round match on that map (it crowns the winner). Null = the deal.</summary>
        public static byte[] PlanFromArgs()
        {
            var id = MapIdFromArg(MapArgValue());
            return id >= 0 && id < MapPoolV1.Keys.Length ? new[] { (byte)id } : null;
        }

        /// <summary>True for a loadable round map scene (<c>Map_*</c>).</summary>
        public static bool IsMapScene(string sceneName) => sceneName != null && sceneName.StartsWith("Map_", StringComparison.Ordinal);

        /// <summary>The server's map scene from <c>-fallrushMap</c> / <c>GAMESERVER_MAP</c>.</summary>
        public static string ServerMapScene() => MatchMode ? LobbyMapScene : SandboxScene;

        /// <summary>The map the offline QA runs on (<c>-fallrushMap</c>; Block Party Dash by default).</summary>
        public static string QaMapScene()
        {
            var id = MapIdFromArg(MapArgValue());
            return id >= 0 ? SceneForMap((byte)id) : BlockPartyDashScene;
        }

        // ---- approval → connect: the hello (name, reconnect token) of each pending client ----
        private static readonly Dictionary<ulong, Protocol.Hello> s_Hellos = new Dictionary<ulong, Protocol.Hello>();

        /// <summary>The approved hello of <paramref name="clientId"/> (removed). Default if none.</summary>
        public static Protocol.Hello TakeHello(ulong clientId)
        {
            if (s_Hellos.TryGetValue(clientId, out var h))
            {
                s_Hellos.Remove(clientId);
                return h;
            }

            return default;
        }

        /// <summary>Client: the display name sent in hello (desktop: <c>-fallrushName</c>; WebGL: <c>?name=</c>).</summary>
        public static string ClientName()
        {
            var n = GetArgValue("-fallrushName") ?? NetTargets.GetQueryParam(Application.absoluteURL, "name");
            return string.IsNullOrEmpty(n) ? null : n;
        }
        public const int ServerDisconnectTimeoutMs = 90000;
        public const int ClientPacketQueueSize = 1024;
        private const string ServerFpsArg = "-fallrushServerFps"; // server frame cap override (30–240)
        public const int DefaultServerFps = 60;
        private const string ServerArg = "-fallrushServer";
        private const string ListenArg = "-fallrushListen"; // dev only: e.g. 127.0.0.1 for the local Mac dev server
        private const string ProtoOverrideQueryKey = "proto";

        [SerializeField] private NetClientConfig clientConfig;

        public static NetBootstrap Instance { get; private set; }

        /// <summary>True in Linux Dedicated Server builds (UNITY_SERVER), or with <c>-fallrushServer</c> on desktop.</summary>
        public bool IsServerRole { get; private set; }

        public NetworkManager Manager { get; private set; }
        public UnityTransport Transport { get; private set; }

        // ---- client-facing state ----
        public ClientState State { get; private set; } = ClientState.Idle;
        public ServerTarget Target { get; private set; }
        public TargetSource TargetSource { get; private set; }
        public string LastDisconnectReason { get; private set; }
        public ushort ClientProtocolVersion { get; private set; } = Protocol.Version;
        public event Action StateChanged;

        /// <summary>Set by the headless probe (<c>-fallrushProbe url</c>); wins over the query string and config.</summary>
        public string OverrideServerUrl { get; set; }

        public bool IsProbe { get; private set; }

        // ---- server-side ----
        private readonly HashSet<ulong> _serverClients = new HashSet<ulong>();
        public int ServerConnectedCount => _serverClients.Count;
        public ushort ListenPort { get; private set; }

        private string _pendingDisconnectReason;
        private bool _quitting;
        private int _qaRuns;
        private Coroutine _reconnect;

        public static bool IsDedicatedServerBuild
        {
            get
            {
#if UNITY_SERVER
                return true;
#else
                return false;
#endif
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            if (SelfTest.Requested())
            {
                var ok = SelfTest.Run(out _);
                if (Application.platform != RuntimePlatform.WebGLPlayer)
                {
                    _quitting = true;
                    Application.Quit(ok ? 0 : 4);
                    return;
                }
            }

            Manager = GetComponent<NetworkManager>();
            Transport = GetComponent<UnityTransport>();
            Transport.UseWebSockets = true;
            // R12: same driver as NGO's, with a TCP-appropriate reliable resend timer (see WebSocketDriverConstructor).
            UnityTransport.s_DriverConstructor = new WebSocketDriverConstructor();
            Application.runInBackground = true;

            IsServerRole = IsDedicatedServerBuild || HasArg(ServerArg);

            Manager.NetworkConfig.NetworkTransport = Transport;
            Manager.NetworkConfig.ConnectionApproval = true;
            Manager.NetworkConfig.EnableSceneManagement = false; // M1: client navigates Menu/Lobby locally.
            Manager.NetworkConfig.TickRate = 30;

            // Movement queries only; nothing is ever simulated by PhysX (doc 04 §5).
            Physics.simulationMode = SimulationMode.Script;

            if (!IsServerRole)
            {
                gameObject.AddComponent<ClientSim>();
                var probeUrl = SimProbe.TargetFromArgs();
                if (!string.IsNullOrEmpty(probeUrl))
                {
                    IsProbe = true;
                    OverrideServerUrl = probeUrl;
                }
            }

            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!IsMapScene(scene.name))
                return;
            Physics.SyncTransforms();
            if (_qaRuns > 0)
            {
                var qaMap = MapInfo.Current;
                // M4: every mode through ModeQa (BotBrain tiers); the M3 race QA (waypoint bots, route table) with -fallrushQATier m3.
                if (qaMap.definition != null && qaMap.definition.IsRace && ModeQa.TierArg() == "m3")
                    MapQa.RunAll(qaMap, _qaRuns);
                else
                    ModeQa.RunAll(qaMap, _qaRuns);
                Application.Quit(0);
                return;
            }

            if (IsServerRole && Manager.IsServer)
            {
                var map = MapInfo.Current;
                if (map == null)
                {
                    Debug.LogError($"[gs] {scene.name} has no MapInfo");
                    return;
                }

                if (ServerSim.Instance != null)
                    ServerSim.Instance.OnMapLoaded(map); // M4: the next map of the match
                else
                    ServerSim.Install(Manager, map);
            }
        }

        private void Start()
        {
            if (_quitting)
                return;
            _qaRuns = MapQa.Runs();
            if (_qaRuns > 0)
            {
                // Map QA (doc 08 §9): no networking; load the map and run the bots offline, then quit.
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
                SceneManager.LoadScene(QaMapScene());
                return;
            }

            if (IsServerRole)
                StartDedicatedServer();
            else if (IsProbe)
                ResolveTargetAndStartProbe();
            else
                StartCoroutine(LoadMenuNextFrame());
        }

        private void ResolveTargetAndStartProbe()
        {
            ResolveTarget();
            gameObject.AddComponent<SimProbe>();
        }

        private static IEnumerator LoadMenuNextFrame()
        {
            yield return null;
            SceneManager.LoadScene(MenuScene);
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (Manager == null)
                return;
            Manager.OnClientConnectedCallback -= HandleClientConnected;
            Manager.OnClientDisconnectCallback -= HandleClientDisconnected;
            Manager.OnClientStopped -= HandleClientStopped;
            Manager.ConnectionApprovalCallback = null;
        }

        // =====================================================================
        // Server
        // =====================================================================

        private void StartDedicatedServer()
        {
            QualitySettings.vSyncCount = 0;
            var fps = GetArgValue(ServerFpsArg);
            // M2 measured (docs/proof/M2-netcode.md): a 60 fps cap halves localhost RTT (33 → 17 ms p50) for +0.7 % CPU.
            Application.targetFrameRate = int.TryParse(fps, out var f) && f >= 30 && f <= 240 ? f : DefaultServerFps;

            ListenPort = NetTargets.ResolvePort(Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable, out var portSource, out var warning);
            if (warning != null)
                Debug.LogWarning($"[gs] {warning}");

            var listen = GetArgValue(ListenArg) ?? "0.0.0.0";

            // forceOverrideCommandLineArgs: true — we already resolved -port ourselves (arg → env → default).
            Transport.SetConnectionData(true, listen, ListenPort, listen);
            Transport.UseWebSockets = true;
            Transport.UseEncryption = false; // TLS terminates at nginx (doc 04 §2).
            // R12 (hidden browser tab): a hidden tab runs no frames, so the client sends nothing. UTP's default 30 s
            // disconnect timeout would drop it; 90 s keeps a tab hidden for a minute. A closed tab or a dead browser
            // still disconnects at once (the WebSocket's TCP connection closes).
            Transport.DisconnectTimeoutMS = ServerDisconnectTimeoutMs;
            // After a TCP stall a client's backlog (~30 input messages per stalled second) lands in one server frame;
            // with UTP's 128-packet receive queue the server's WebSocket layer aborts that connection (ProtocolError).
            Transport.MaxPacketQueueSize = ClientPacketQueueSize;

            Manager.ConnectionApprovalCallback = ApproveConnection;
            Manager.OnClientConnectedCallback += HandleClientConnected;
            Manager.OnClientDisconnectCallback += HandleClientDisconnected;

            var id = Environment.GetEnvironmentVariable("GAMESERVER_ID");
            if (string.IsNullOrEmpty(id))
                id = "gs-local";

            if (Manager.StartServer())
            {
                SceneManager.LoadScene(ServerMapScene());
                Debug.Log($"[gs] listening id={id} ws://{listen}:{ListenPort}{Transport.ConnectionData.WebSocketPath ?? "/"} (port from {portSource}) protocol={Protocol.Version} build={Application.version} tickRate={Manager.NetworkConfig.TickRate} targetFps={Application.targetFrameRate}");
            }
            else
            {
                Debug.LogError($"[gs] StartServer failed on port {ListenPort}");
                Application.Quit(1);
            }
        }

        private void ApproveConnection(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            response.CreatePlayerObject = false;
            response.Pending = false;

            var ok = Protocol.TryDecodeHello(request.Payload, out var hello, out var error);

            // Report a version mismatch even if the rest of a future-format hello does not parse.
            if (request.Payload != null && request.Payload.Length >= 2 && hello.ProtocolVersion != Protocol.Version)
            {
                Reject(response, request.ClientNetworkId,
                    $"Protocol mismatch: server v{Protocol.Version}, client v{hello.ProtocolVersion}. Reload the page to get the current build.");
                return;
            }

            if (!ok)
            {
                Reject(response, request.ClientNetworkId, $"Malformed hello: {error}");
                return;
            }

            if (_serverClients.Count >= Protocol.MaxPlayers)
            {
                Reject(response, request.ClientNetworkId, $"Server full ({Protocol.MaxPlayers}/{Protocol.MaxPlayers}).");
                return;
            }

            response.Approved = true;
            s_Hellos[request.ClientNetworkId] = hello;
            Debug.Log($"[gs] approve client={request.ClientNetworkId} build={hello.BuildVersion} cosmetic={hello.CosmeticPreset} name=\"{hello.Name}\" token={(hello.Token != null && hello.Token[0] + hello.Token[15] != 0 ? "yes" : "no")}");
        }

        private static void Reject(NetworkManager.ConnectionApprovalResponse response, ulong clientId, string reason)
        {
            response.Approved = false;
            response.Reason = reason;
            Debug.Log($"[gs] reject client={clientId} reason=\"{reason}\"");
        }

        private void HandleClientConnected(ulong clientId)
        {
            if (Manager.IsServer)
            {
                _serverClients.Add(clientId);
                Debug.Log($"[gs] connect client={clientId} total={_serverClients.Count}");
                return;
            }

            if (clientId == Manager.LocalClientId)
                SetState(ClientState.Connected);
        }

        private void HandleClientDisconnected(ulong clientId)
        {
            if (Manager.IsServer)
            {
                if (_serverClients.Remove(clientId))
                    Debug.Log($"[gs] disconnect client={clientId} total={_serverClients.Count}");
                return;
            }

            // Client side: read the reason now; it is cleared once the NetworkManager shuts down.
            if (!string.IsNullOrEmpty(Manager.DisconnectReason) || _pendingDisconnectReason == null)
                _pendingDisconnectReason = Manager.DisconnectReason;
        }

        // =====================================================================
        // Client
        // =====================================================================

        /// <summary>Resolves the target (query → config → dev default) without connecting.</summary>
        public void ResolveTarget()
        {
            var configured = clientConfig != null ? clientConfig.defaultServerUrl : null;
            if (!string.IsNullOrEmpty(OverrideServerUrl))
                configured = OverrideServerUrl;
            Target = NetTargets.ResolveServerTarget(Application.absoluteURL, configured, out var source, out var warning);
            TargetSource = source;
            if (warning != null)
                Debug.LogWarning($"[net] {warning}");

            ClientProtocolVersion = Protocol.Version;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            // Dev-only test hook for the protocol-mismatch check (M1 §4.5 test 3): ?proto=999
            var proto = NetTargets.GetQueryParam(Application.absoluteURL, ProtoOverrideQueryKey);
            if (!string.IsNullOrEmpty(proto) && ushort.TryParse(proto, out var p))
            {
                ClientProtocolVersion = p;
                Debug.LogWarning($"[net] DEV protocol override: sending protocolVersion={p}");
            }
#endif
        }

        public void Connect()
        {
            _userLeft = false;
            if (IsServerRole)
                return;
            if (State == ClientState.Connecting || State == ClientState.Connected)
                return;

            if (_reconnect != null)
                StopCoroutine(_reconnect);
            _reconnect = StartCoroutine(ConnectRoutine());
        }

        /// <summary>Tears down any previous session (without reloading the page) and connects again.</summary>
        public void Reconnect()
        {
            if (IsServerRole)
                return;
            if (_reconnect != null)
                StopCoroutine(_reconnect);
            _reconnect = StartCoroutine(ConnectRoutine());
        }

        private IEnumerator ConnectRoutine()
        {
            if (Manager.IsListening || Manager.ShutdownInProgress)
            {
                Manager.Shutdown();
                while (Manager.ShutdownInProgress || Manager.IsListening)
                    yield return null;
            }

            ResolveTarget();
            var t = Target;

            Transport.UseWebSockets = true;
            // R12: after a hidden tab (or any long stall) the whole backlog of snapshots arrives in one frame. With the
            // default 128-packet queue UTP's desktop WebSocket layer aborts ("Insufficient space in receive queue",
            // ProtocolError) — measured with a 10 s main-thread stall. 1024 drains ~60 s of backlog (≈ 3 MB).
            Transport.MaxPacketQueueSize = ClientPacketQueueSize;
            // wss: UTP's WebGL WebSocket interface uses "wss://" when a secure hostname is set
            // (SecureNetworkProtocolParameter.Hostname via SetClientSecrets); the browser performs TLS.
            Transport.UseEncryption = t.Secure;
            Transport.SetConnectionData(true, t.Host, t.Port);
            // SetConnectionData replaces ConnectionData and drops WebSocketPath, so set it afterwards.
            var data = Transport.ConnectionData;
            data.WebSocketPath = t.Path;
            Transport.ConnectionData = data;
            if (t.Secure)
                Transport.SetClientSecrets(t.Host);

            if (clientConfig != null)
            {
                Transport.ConnectTimeoutMS = clientConfig.connectTimeoutMs;
                Transport.MaxConnectAttempts = clientConfig.maxConnectAttempts;
            }

            var hello = new Protocol.Hello
            {
                ProtocolVersion = ClientProtocolVersion,
                BuildVersion = Application.version,
                CosmeticPreset = clientConfig != null ? clientConfig.cosmeticPreset : (byte)0,
                Name = ClientName() ?? string.Empty,
                Token = SessionToken.Load(), // M4-6: a reconnect within 20 s takes the same slot
            };
            Manager.NetworkConfig.ConnectionData = Protocol.EncodeHello(hello);

            Manager.OnClientConnectedCallback -= HandleClientConnected;
            Manager.OnClientDisconnectCallback -= HandleClientDisconnected;
            Manager.OnClientStopped -= HandleClientStopped;
            Manager.OnClientConnectedCallback += HandleClientConnected;
            Manager.OnClientDisconnectCallback += HandleClientDisconnected;
            Manager.OnClientStopped += HandleClientStopped;

            _pendingDisconnectReason = null;
            LastDisconnectReason = null;
            SetState(ClientState.Connecting);
            Debug.Log($"[net] connecting to {t.Url} (from {TargetSource}) protocol={ClientProtocolVersion} build={Application.version}");

            if (!Manager.StartClient())
            {
                LastDisconnectReason = $"Could not start client for {t.Url}.";
                SetState(ClientState.Disconnected);
            }

            _reconnect = null;
        }

        // ---- M4-6: automatic reconnect with the token (every 2 s for up to 20 s) after an unexpected drop in a match ----
        public const float ReconnectWindowSeconds = 20f, ReconnectEverySeconds = 2f;
        public bool Reconnecting { get; private set; }
        public float ReconnectSecondsLeft => Reconnecting ? Mathf.Max(0f, _reconnectUntil - Time.realtimeSinceStartup) : 0f;
        private float _reconnectUntil;
        private bool _userLeft;
        private Coroutine _auto;

        /// <summary>Was the client in a match (or its lobby) when the connection dropped? Then it retries with its token.</summary>
        private bool ShouldAutoReconnect() =>
            !_userLeft && !IsProbe && ClientSim.Instance != null && ClientSim.Instance.Match != null && SessionToken.Load() != null &&
            ClientSim.Instance.MapMismatch == null;

        private IEnumerator AutoReconnect()
        {
            Reconnecting = true;
            _reconnectUntil = Time.realtimeSinceStartup + ReconnectWindowSeconds;
            StateChanged?.Invoke();
            while (Time.realtimeSinceStartup < _reconnectUntil)
            {
                yield return new WaitForSecondsRealtime(ReconnectEverySeconds);
                if (State == ClientState.Connected)
                    break;
                Debug.Log($"[net] reconnecting with the token ({ReconnectSecondsLeft:F0} s left)");
                Reconnect();
                var wait = Time.realtimeSinceStartup + ReconnectEverySeconds;
                while (State != ClientState.Connected && Time.realtimeSinceStartup < wait)
                    yield return null;
                if (State == ClientState.Connected)
                    break;
            }

            Reconnecting = false;
            _auto = null;
            if (State != ClientState.Connected)
                LastDisconnectReason = "Could not reconnect within 20 s: you were eliminated (disconnected). Back to the menu to play again.";
            StateChanged?.Invoke();
        }

        private void HandleClientStopped(bool wasHost)
        {
            if (Manager.IsServer)
                return;

            var reason = _pendingDisconnectReason;
            if (string.IsNullOrEmpty(reason))
                reason = State == ClientState.Connecting
                    ? $"Could not reach {Target.Url}."
                    : "Connection to the server was lost.";
            LastDisconnectReason = reason;
            Debug.Log($"[net] disconnected: {reason}");
            SetState(ClientState.Disconnected);
            if (_auto == null && !Reconnecting && ShouldAutoReconnect())
                _auto = StartCoroutine(AutoReconnect());
        }

        /// <summary>Client-side refusal (e.g. map hash mismatch): disconnect and show <paramref name="reason"/>.</summary>
        public void DisconnectWithReason(string reason)
        {
            _pendingDisconnectReason = reason;
            if (Manager.IsListening)
                Manager.Shutdown();
        }

        public void Disconnect()
        {
            _userLeft = true;
            if (_auto != null)
            {
                StopCoroutine(_auto);
                _auto = null;
                Reconnecting = false;
            }

            if (Manager.IsListening)
                Manager.Shutdown();
        }

        private void SetState(ClientState s)
        {
            if (State == s)
                return;
            State = s;
            StateChanged?.Invoke();
        }

        private static string GetArgValue(string arg)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], arg, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        private static bool HasArg(string arg)
        {
            foreach (var a in Environment.GetCommandLineArgs())
                if (string.Equals(a, arg, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}
