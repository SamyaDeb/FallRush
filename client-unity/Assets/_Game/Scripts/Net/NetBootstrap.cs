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
        private const string MapArg = "-fallrushMap"; // server: Sandbox (default) | BlockPartyDash; env GAMESERVER_MAP

        /// <summary>True for a loadable round map scene (<c>Map_*</c>).</summary>
        public static bool IsMapScene(string sceneName) => sceneName != null && sceneName.StartsWith("Map_", StringComparison.Ordinal);

        /// <summary>The server's map scene from <c>-fallrushMap</c> / <c>GAMESERVER_MAP</c>.</summary>
        public static string ServerMapScene()
        {
            var v = GetArgValue(MapArg) ?? Environment.GetEnvironmentVariable("GAMESERVER_MAP");
            if (string.IsNullOrEmpty(v))
                return SandboxScene;
            if (v.StartsWith("Map_", StringComparison.Ordinal))
                return v;
            return string.Equals(v, "BlockPartyDash", StringComparison.OrdinalIgnoreCase) || v == "bpd" ? BlockPartyDashScene : SandboxScene;
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
            if (IsServerRole && Manager.IsServer)
            {
                var map = MapInfo.Current;
                if (map == null)
                {
                    Debug.LogError($"[gs] {scene.name} has no MapInfo");
                    return;
                }

                ServerSim.Install(Manager, map);
            }
        }

        private void Start()
        {
            if (_quitting)
                return;
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
            Debug.Log($"[gs] approve client={request.ClientNetworkId} build={hello.BuildVersion} cosmetic={hello.CosmeticPreset}");
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
