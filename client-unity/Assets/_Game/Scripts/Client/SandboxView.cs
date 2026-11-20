using System.Text;
using FallRush.Net;
using FallRush.Shared;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace FallRush.Client
{
    /// <summary>
    /// M2a client view of <c>Map_Sandbox</c>: placeholder capsules (r 0.31, h 1.0 = the collision capsule) for every
    /// player in the snapshots, interpolated 100 ms behind the server, the local one included (no prediction yet).
    /// F3 toggles the netcode HUD. Created at runtime by <see cref="ClientMapBootstrap"/> (no client code in the map scene,
    /// so the server build has no missing-script references).
    /// </summary>
    public sealed class SandboxView : MonoBehaviour
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        // Placeholder per-slot colours (palette-ish; doc 17 §3.1). No gameplay meaning.
        private static readonly Color[] SlotColors =
        {
            UiKit.Hex(0xFF3EA5), UiKit.Hex(0x3FA9F5), UiKit.Hex(0xFFD21F), UiKit.Hex(0x5BE3B5),
            UiKit.Hex(0x7B2FF7), UiKit.Hex(0xFF8A3D), UiKit.Hex(0x2EC4B6), UiKit.Hex(0xE84855),
            UiKit.Hex(0x9B5CFF), UiKit.Hex(0x8AC926), UiKit.Hex(0x1982C4), UiKit.Hex(0xF4A261),
        };

        private Transform[] _bodies;
        private TextMesh[] _labels;
        private Transform _cameraTarget;
        private OrbitCameraPlaceholder _rig;
        private PlayerInputSampler _sampler;
        private GameObject _hud;
        private Text _hudText;
        private GameObject _overlay;
        private Text _overlayText;
        private readonly StringBuilder _sb = new StringBuilder(1024);
        private float _hudNext;
        private float _fpsAcc;
        private int _fpsFrames;
        private float _fps;

        private void Start()
        {
            var map = MapInfo.Current;
            var mat = map != null ? map.playerMaterial : null;
            _bodies = new Transform[Protocol.MaxPlayers];
            _labels = new TextMesh[Protocol.MaxPlayers];
            var mpb = new MaterialPropertyBlock();
            for (var i = 0; i < Protocol.MaxPlayers; i++)
            {
                var root = new GameObject($"Player{i}").transform;
                root.SetParent(transform, false);
                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                Destroy(body.GetComponent<Collider>());
                body.name = "Capsule";
                body.transform.SetParent(root, false);
                body.transform.localPosition = new Vector3(0f, 0.5f, 0f);
                body.transform.localScale = new Vector3(0.62f, 0.5f, 0.62f); // Unity capsule r 0.5 h 2 → r 0.31 h 1.0
                var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(nose.GetComponent<Collider>());
                nose.transform.SetParent(root, false);
                nose.transform.localPosition = new Vector3(0f, 0.72f, 0.3f);
                nose.transform.localScale = new Vector3(0.36f, 0.18f, 0.06f);
                foreach (var r in root.GetComponentsInChildren<Renderer>())
                {
                    if (mat != null)
                        r.sharedMaterial = mat;
                    mpb.SetColor(BaseColorId, r.transform == nose.transform ? UiKit.Ink : SlotColors[i]);
                    r.SetPropertyBlock(mpb);
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }

                var label = new GameObject("Label").AddComponent<TextMesh>();
                label.transform.SetParent(root, false);
                label.transform.localPosition = new Vector3(0f, 1.45f, 0f);
                label.font = UiKit.Font;
                label.GetComponent<MeshRenderer>().sharedMaterial = UiKit.Font.material;
                label.fontSize = 48;
                label.characterSize = 0.05f;
                label.anchor = TextAnchor.MiddleCenter;
                label.alignment = TextAlignment.Center;
                _labels[i] = label;

                root.gameObject.SetActive(false);
                _bodies[i] = root;
            }

            _cameraTarget = new GameObject("CameraTarget").transform;
            _cameraTarget.SetParent(transform, false);
            var cam = Camera.main;
            if (cam == null)
            {
                cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)).GetComponent<Camera>();
                cam.tag = "MainCamera";
            }

            _rig = cam.gameObject.AddComponent<OrbitCameraPlaceholder>();
            _rig.target = _cameraTarget;
            _sampler = gameObject.AddComponent<PlayerInputSampler>();
            _sampler.cameraRig = _rig;

            BuildHud();

            var cs = ClientSim.Instance;
            if (cs != null)
            {
                cs.InputSource = _sampler;
                cs.Active = true;
            }

            if (NetBootstrap.Instance != null)
                NetBootstrap.Instance.StateChanged += OnNetState;
            OnNetState();
        }

        private void OnDestroy()
        {
            if (NetBootstrap.Instance != null)
                NetBootstrap.Instance.StateChanged -= OnNetState;
            var cs = ClientSim.Instance;
            if (cs != null)
            {
                cs.Active = false;
                if (ReferenceEquals(cs.InputSource, _sampler))
                    cs.InputSource = null;
            }

            Cursor.lockState = CursorLockMode.None;
        }

        private void OnNetState()
        {
            var boot = NetBootstrap.Instance;
            var down = boot == null || boot.State == ClientState.Disconnected;
            _overlay.SetActive(down);
            if (down)
            {
                _overlayText.text = boot != null ? $"Disconnected\n{boot.LastDisconnectReason}" : "No NetBootstrap";
                Cursor.lockState = CursorLockMode.None;
            }

        }

        private void Update()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb != null && kb.f3Key.wasPressedThisFrame)
                _hud.SetActive(!_hud.activeSelf);
            if (kb != null && kb.escapeKey.wasPressedThisFrame)
                Cursor.lockState = CursorLockMode.None;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked && !_overlay.activeSelf)
                Cursor.lockState = CursorLockMode.Locked; // WebGL: pointer lock needs this user gesture

            var cs = ClientSim.Instance;
            if (cs == null)
                return;
            var own = cs.OwnSlot;
            for (var i = 0; i < Protocol.MaxPlayers; i++)
            {
                var root = _bodies[i];
                if (!cs.TrySample(i, out var feet, out _, out var flags, out var yaw))
                {
                    if (root.gameObject.activeSelf)
                        root.gameObject.SetActive(false);
                    continue;
                }

                if (!root.gameObject.activeSelf)
                    root.gameObject.SetActive(true);
                root.SetPositionAndRotation(feet, Quaternion.Euler(0f, yaw * (360f / 256f), 0f));
                var bot = (flags & MotorState.Bot) != 0;
                var text = i == own ? "YOU" : bot ? "BOT" : $"P{i + 1}";
                if (_labels[i].text != text)
                    _labels[i].text = text;
                if (Camera.main != null)
                    _labels[i].transform.rotation = Camera.main.transform.rotation;
                if (i == own)
                    _cameraTarget.position = feet;
            }

            _fpsAcc += Time.unscaledDeltaTime;
            _fpsFrames++;
            if (Time.unscaledTime >= _hudNext)
            {
                _fps = _fpsFrames / Mathf.Max(1e-4f, _fpsAcc);
                _fpsAcc = 0f;
                _fpsFrames = 0;
                _hudNext = Time.unscaledTime + 0.25f;
                if (_hud.activeSelf)
                    RefreshHud(cs);
            }
        }

        private void RefreshHud(ClientSim cs)
        {
            var ping = PingService.Instance;
            var clock = cs.Clock;
            var serverTick = cs.HasSnapshots ? cs.Newest.Tick : 0u;
            var bots = 0;
            var players = 0;
            if (cs.HasSnapshots)
            {
                ref readonly var n = ref cs.Newest;
                players = n.Count;
                for (var i = 0; i < n.Count; i++)
                    if ((n.Players[i].Flags & MotorState.Bot) != 0)
                        bots++;
            }

            _sb.Clear();
            _sb.Append("<b>NET (F3)</b>  M2a: no prediction\n");
            _sb.Append("RTT p50 ").Append(ping != null ? ping.P50Ms.ToString("F0") : "-").Append(" / p95 ").Append(ping != null ? ping.P95Ms.ToString("F0") : "-").Append(" ms\n");
            _sb.Append("server tick ").Append(serverTick).Append("  predicted ").Append(clock.PredictedTick)
                .Append(" (+").Append((long)clock.PredictedTick - serverTick).Append(")\n");
            _sb.Append("input lead ").Append(clock.LastReportedLead).Append(" / target ").Append(clock.TargetLead)
                .Append(" ticks (").Append((clock.TargetLead * clock.Dt * 1000.0).ToString("F0")).Append(" ms)  rate ").Append(clock.Rate.ToString("F3"))
                .Append("  resyncs ").Append(clock.Resyncs).Append('\n');
            _sb.Append("corrections/5s: n/a (M2b)  replay: n/a\n");
            _sb.Append("snap ").Append(cs.SnapsPerSec.ToString("F1")).Append(" Hz  age ")
                .Append((cs.LatestSnapshotAge * 1000.0).ToString("F0")).Append(" ms  interp delay ").Append(cs.RenderDelayMs.ToString("F0")).Append(" ms\n");
            _sb.Append("payload in ").Append((cs.BytesInPerSec / 1024f).ToString("F2")).Append(" KB/s  out ").Append((cs.BytesOutPerSec / 1024f).ToString("F2")).Append(" KB/s\n");
            _sb.Append("players ").Append(players).Append(" (").Append(bots).Append(" BOT)  my slot ").Append(cs.OwnSlot == SnapHeader.NoSlot ? "-" : cs.OwnSlot.ToString())
                .Append("  ack ").Append(cs.LastAckSeq).Append("/").Append(cs.Seq).Append('\n');
            _sb.Append("FPS ").Append(_fps.ToString("F0"));
            _hudText.text = _sb.ToString();
        }

        private void BuildHud()
        {
            var canvas = UiKit.CreateCanvas("HudCanvas");
            canvas.SetParent(transform, false);

            _hud = new GameObject("NetHud", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)_hud.transform;
            rt.SetParent(canvas, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(12f, -12f);
            rt.sizeDelta = new Vector2(560f, 250f);
            _hud.GetComponent<Image>().color = new Color(0.08f, 0.03f, 0.18f, 0.72f);
            var t = new GameObject("Text", typeof(RectTransform), typeof(Text)).GetComponent<Text>();
            var trt = (RectTransform)t.transform;
            trt.SetParent(rt, false);
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(10f, 8f);
            trt.offsetMax = new Vector2(-10f, -8f);
            t.font = UiKit.Font;
            t.fontSize = 17;
            t.color = Color.white;
            t.supportRichText = true;
            t.alignment = TextAnchor.UpperLeft;
            _hudText = t;

            var hint = UiKit.Label(canvas, "Click to capture mouse · WASD move · Space jump · Shift/LMB dive · Esc release · F3 net HUD", 16, UiKit.Ink);
            var hrt = (RectTransform)hint.transform;
            hrt.anchorMin = hrt.anchorMax = hrt.pivot = new Vector2(0.5f, 0f);
            hrt.anchoredPosition = new Vector2(0f, 10f);
            hrt.sizeDelta = new Vector2(1000f, 28f);

            var panel = UiKit.Panel(canvas, new Vector2(620f, 320f), UiKit.CloudWhite);
            _overlay = panel.gameObject;
            _overlayText = UiKit.Label(panel, string.Empty, 26, UiKit.Magenta, FontStyle.Bold, 120);
            UiKit.Button(panel, "Reconnect", UiKit.SunYellow, () => NetBootstrap.Instance?.Reconnect());
            UiKit.Button(panel, "Back to Menu", UiKit.SkyLight, () =>
            {
                NetBootstrap.Instance?.Disconnect();
                SceneManager.LoadScene(NetBootstrap.MenuScene);
            });
            _overlay.SetActive(false);
        }
    }

    /// <summary>Spawns <see cref="SandboxView"/> when a client loads the sandbox map (not on the server, not in the headless probe).</summary>
    public static class ClientMapBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= OnLoaded;
            SceneManager.sceneLoaded += OnLoaded;
        }

        private static void OnLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != NetBootstrap.SandboxScene)
                return;
            var boot = NetBootstrap.Instance;
            if (boot == null || boot.IsServerRole || boot.IsProbe)
                return;
            new GameObject("SandboxView").AddComponent<SandboxView>();
        }
    }
}
