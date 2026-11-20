using System;
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
    /// Client view of <c>Map_Sandbox</c> (M2b): Bolt v1 Rushlings for every player (the local one predicted, the rest
    /// interpolated), the Cinemachine follow camera, hazard visuals on the predicted timeline, the F3 netcode HUD, a
    /// "lagging" icon, and the options panel (Esc). Created at runtime by <see cref="ClientMapBootstrap"/> (no client code
    /// in the map scene, so the server build has no missing-script references).
    /// <para>Photo mode for proof screenshots: <c>-fallrushPhoto silhouette|closeup</c> (doc 17 §2.1 silhouette test).</para>
    /// </summary>
    public sealed class SandboxView : MonoBehaviour
    {
        private RushlingVisuals[] _rushlings;
        private TextMesh[] _labels;
        private bool[] _wasRespawning;
        private Transform _cameraTarget;
        private FollowCamera _cam;
        private PlayerInputSampler _sampler;
        private GameObject _hud;
        private Text _hudText;
        private GameObject _overlay;
        private Text _overlayText;
        private GameObject _lagIcon;
        private GameObject _options;
        private Text _optionsText;
        private readonly StringBuilder _sb = new StringBuilder(1024);
        private float _hudNext;
        private float _fpsAcc;
        private int _fpsFrames;
        private float _fps;
        private MapInfo _map;
        private string _photo;
        private Vector3 _lastOwnFeet;
        private bool _hasOwn;
        private HazardViewDriver _hazardViews;

        private static MatchProbeBot s_MatchBot; // WebGL ?bot=match: one bot for the whole match (survives scene loads)

        /// <summary>Spectate: follow this slot instead of our own (MatchHud sets it while spectating); −1 = own.</summary>
        public int FollowSlot = -1;

        /// <summary>M4: the camera looks at this point instead of a player (round-intro fly-through, champion podium).</summary>
        public Vector3? FocusPoint;

        private void Start()
        {
            _map = MapInfo.Current;
            var lit = _map != null ? _map.playerMaterial : null;
            var unlit = _map != null ? _map.unlitMaterial : null;
            _rushlings = new RushlingVisuals[Protocol.MaxPlayers];
            _labels = new TextMesh[Protocol.MaxPlayers];
            _wasRespawning = new bool[Protocol.MaxPlayers];
            for (var i = 0; i < Protocol.MaxPlayers; i++)
            {
                var root = new GameObject($"Rushling{i}").transform;
                root.SetParent(transform, false);
                var vis = root.gameObject.AddComponent<RushlingVisuals>();
                vis.Build(i, lit, unlit);

                var label = new GameObject("Label").AddComponent<TextMesh>();
                label.transform.SetParent(transform, false);
                label.font = UiKit.Font;
                label.GetComponent<MeshRenderer>().sharedMaterial = UiKit.Font.material;
                label.fontSize = 64;
                label.characterSize = 0.035f;
                label.fontStyle = FontStyle.Bold;
                label.color = UiKit.Ink;
                label.anchor = TextAnchor.MiddleCenter;
                label.alignment = TextAlignment.Center;
                _labels[i] = label;

                vis.SetVisible(false);
                label.gameObject.SetActive(false);
                _wasRespawning[i] = true;
                _rushlings[i] = vis;
            }

            _cameraTarget = new GameObject("CameraTarget").transform;
            _cameraTarget.SetParent(transform, false);
            var cam = Camera.main;
            if (cam == null)
            {
                cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)).GetComponent<Camera>();
                cam.tag = "MainCamera";
            }

            _cam = FollowCamera.Create(cam, _cameraTarget, PhysxMotorWorld.GameplayMask());
            _cam.transform.SetParent(transform, false);
            _sampler = gameObject.AddComponent<PlayerInputSampler>();
            _sampler.cameraRig = _cam;

            BuildHud();
            var showHud = NetTargets.GetQueryParam(Application.absoluteURL, "hud") == "1" || Array.Exists(Environment.GetCommandLineArgs(), a => a == "-fallrushHud");
            _hud.SetActive(showHud); // M4: the match HUD is the default; F3 shows the netcode HUD

            var cs = ClientSim.Instance;
            if (cs != null)
            {
                if (NetBootstrap.Instance == null || !NetBootstrap.Instance.IsProbe)
                    cs.InputSource = _sampler; // the probe keeps its scripted input
                cs.Active = true;

                // MEASUREMENT (WebGL gate runs): ?bot=course plays the scripted course; ?gate=1 (or bot) logs [gate] lines.
                var url = Application.absoluteURL;
                var bot = NetTargets.GetQueryParam(url, "bot");
                CourseBot course = null;
                if (bot == "course" && (NetBootstrap.Instance == null || !NetBootstrap.Instance.IsProbe))
                    cs.InputSource = course = new CourseBot(cs);
                RaceProbeBot raceBot = null;
                if (bot == "race" && (NetBootstrap.Instance == null || !NetBootstrap.Instance.IsProbe))
                    cs.InputSource = raceBot = new RaceProbeBot(cs, (uint)System.Environment.TickCount);
                MatchProbeBot matchBot = null;
                if (bot == "match" && (NetBootstrap.Instance == null || !NetBootstrap.Instance.IsProbe))
                    cs.InputSource = matchBot = s_MatchBot ??= new MatchProbeBot(cs, (uint)System.Environment.TickCount);
                if (course != null || raceBot != null || matchBot != null || NetTargets.GetQueryParam(url, "gate") == "1")
                {
                    var gate = gameObject.AddComponent<GateLogger>();
                    gate.Tag = "gate";
                    gate.Course = course;
                    gate.Race = raceBot;
                }
            }

            if (NetBootstrap.Instance != null)
                NetBootstrap.Instance.StateChanged += OnNetState;
            OnNetState();

            _photo = PhotoArg();
            if (_photo != null)
                EnterPhotoMode(cam);
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
            var down = boot == null || boot.State == ClientState.Disconnected && !boot.Reconnecting;
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
            var es = UnityEngine.EventSystems.EventSystem.current;
            var pointerOverUi = _options.activeSelf && es != null && es.IsPointerOverGameObject();
            if (mouse != null && mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked && !_overlay.activeSelf && !pointerOverUi)
                Cursor.lockState = CursorLockMode.Locked; // WebGL: pointer lock needs this user gesture
            var probe = NetBootstrap.Instance != null && NetBootstrap.Instance.IsProbe; // scripted view: no pointer lock
            var showOptions = Cursor.lockState != CursorLockMode.Locked && !_overlay.activeSelf && _photo == null && !probe;
            if (_options.activeSelf != showOptions)
                _options.SetActive(showOptions);

            var cs = ClientSim.Instance;
            if (cs == null)
                return;
            var dt = Time.deltaTime;
            var own = cs.OwnSlot;
            var camT = Camera.main != null ? Camera.main.transform : null;
            for (var i = 0; i < Protocol.MaxPlayers; i++)
            {
                var vis = _rushlings[i];
                if (!cs.TrySample(i, out var feet, out var vel, out var flags, out var yaw, out var stun) ||
                    (flags & MotorState.Respawning) != 0)
                {
                    if (!_wasRespawning[i])
                    {
                        vis.SetVisible(false);
                        _labels[i].gameObject.SetActive(false);
                        _wasRespawning[i] = true;
                    }

                    continue;
                }

                if (_wasRespawning[i])
                {
                    vis.SetVisible(true);
                    _labels[i].gameObject.SetActive(_photo == null);
                    if (_photo == "silhouette")
                        vis.SetSilhouette(true);
                    _wasRespawning[i] = false;
                }

                vis.Drive(feet, vel, flags, yaw, stun, dt);
                var bot = (flags & MotorState.Bot) != 0;
                var text = i == own ? "YOU" : bot ? "BOT" : $"P{i + 1}";
                var label = _labels[i];
                if (label.text != text)
                    label.text = text;
                label.transform.position = feet + Vector3.up * 1.45f;
                if (camT != null)
                    label.transform.rotation = camT.rotation;

                if (i == (FollowSlot >= 0 ? FollowSlot : own))
                {
                    var target = feet + Vector3.up * 0.8f;
                    if (_hasOwn && (feet - _lastOwnFeet).sqrMagnitude > 16f)
                        _cam.Warp(target - _cameraTarget.position); // respawn teleport
                    _cameraTarget.position = target;
                    _lastOwnFeet = feet;
                    _hasOwn = true;
                    if (vis.DiveStartedThisFrame)
                        _cam.DiveKick();
                    if (vis.StunStartedThisFrame)
                        _cam.Shake(0.12f);
                }
            }

            if (FocusPoint.HasValue)
            {
                var target = FocusPoint.Value;
                if ((target - _cameraTarget.position).sqrMagnitude > 400f)
                    _cam.Warp(target - _cameraTarget.position);
                _cameraTarget.position = target;
                _lastOwnFeet = target;
            }

            UpdateHazardVisuals(cs);

            var lagging = cs.Remote.Lagging;
            if (_lagIcon.activeSelf != lagging)
                _lagIcon.SetActive(lagging);

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

        private void LateUpdate()
        {
            if (_photo != null)
                PhotoCamera();
        }

        /// <summary>Client FPS over the last 0.25 s (HUD; logged by the gate script).</summary>
        public float Fps => _fps;

        /// <summary>Hazards are drawn on the local predicted timeline, the same tick the motor collides with.</summary>
        private void UpdateHazardVisuals(ClientSim cs)
        {
            if (_map == null || cs.Prediction == null)
                return;
            var t = cs.HazardRenderTick;
            if (t < 1)
                return;
            _hazardViews ??= new HazardViewDriver(_map);
            var hz = cs.Hazards;
            _hazardViews.Update(hz, t - hz.RoundStartTick, GameSettings.ReducedMotion);
            // TinyTilt (visual-only modifier): FOV −8°, fog in.
            var mod = cs.Round.HasValue ? ModifierEffects.Get(cs.Round.Value.Modifier) : ModifierEffects.Get(0);
            _cam.FovOffset = mod.FovDeltaDeg;
            var fog = mod.FogPermille > 0;
            if (RenderSettings.fog != fog)
            {
                RenderSettings.fog = fog;
                RenderSettings.fogMode = FogMode.Exponential;
                RenderSettings.fogDensity = mod.FogPermille / 1000f;
                RenderSettings.fogColor = UiKit.SkyLight;
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

            var p = cs.Prediction;
            var r = cs.Remote;
            var now = ClientSim.Now;
            _sb.Clear();
            _sb.Append("<b>NET (F3)</b>  M3: predicted (pushes too)\n");
            _sb.Append("RTT p50 ").Append(ping != null ? ping.P50Ms.ToString("F0") : "-").Append(" / p95 ").Append(ping != null ? ping.P95Ms.ToString("F0") : "-").Append(" ms\n");
            _sb.Append("server tick ").Append(serverTick).Append("  predicted ").Append(clock.PredictedTick)
                .Append(" (+").Append((long)clock.PredictedTick - serverTick).Append(")\n");
            _sb.Append("input lead ").Append(clock.LastReportedLead).Append(" / target ").Append(clock.TargetLead)
                .Append(" ticks (").Append((clock.TargetLead * clock.Dt * 1000.0).ToString("F0")).Append(" ms)  rate ").Append(clock.Rate.ToString("F3"))
                .Append("  resyncs ").Append(clock.Resyncs).Append('\n');
            if (p != null)
            {
                _sb.Append("corrections/5s ").Append(p.CorrectionsSince(now, 5.0)).Append("  total ").Append(p.Corrections).Append(" + push ").Append(p.PushCorrections)
                    .Append("  last ").Append((p.LastCorrection * 100f).ToString("F1")).Append(" cm  max ").Append((p.MaxCorrection * 100f).ToString("F1")).Append(" cm\n");
                _sb.Append("replay last ").Append(p.LastReplay).Append(" max ").Append(p.MaxReplay).Append(" ticks  match ").Append(p.Matches)
                    .Append("  silent ").Append(p.SilentResyncs).Append("  hazard mismatch ").Append(p.HazardMismatches).Append('\n');
                _sb.Append("drift mean ").Append((p.DriftSamples > 0 ? p.SumDriftMm / p.DriftSamples : 0).ToString("F2")).Append(" mm  max ").Append(p.MaxDriftMm.ToString("F1"))
                    .Append(" mm  smoothing ").Append((p.RenderOffset.Magnitude * 100f).ToString("F1")).Append(" cm\n");
            }

            _sb.Append("snap ").Append(cs.SnapsPerSec.ToString("F1")).Append(" Hz  age ")
                .Append((cs.LatestSnapshotAge * 1000.0).ToString("F0")).Append(" ms  interp ").Append((r.Delay * 1000f).ToString("F0"))
                .Append(" ms (target ").Append((r.TargetDelay * 1000f).ToString("F0")).Append(", late p95 ").Append(r.LatenessP95Ms.ToString("F0")).Append(")")
                .Append(r.Lagging ? "  LAGGING" : r.Extrapolating ? "  extrap" : "").Append('\n');
            _sb.Append("payload in ").Append((cs.BytesInPerSec / 1024f).ToString("F2")).Append(" KB/s  out ").Append((cs.BytesOutPerSec / 1024f).ToString("F2")).Append(" KB/s\n");
            _sb.Append("players ").Append(players).Append(" (").Append(bots).Append(" BOT)  my slot ").Append(cs.OwnSlot == SnapHeader.NoSlot ? "-" : cs.OwnSlot.ToString())
                .Append("  ack ").Append(cs.LastAckSeq).Append("/").Append(cs.Seq).Append('\n');
            if (cs.Round.HasValue)
            {
                var rp = cs.Round.Value;
                _sb.Append("round ").Append(cs.Phase).Append(" tick ").Append(cs.Prediction != null ? (long)cs.Prediction.Tick - rp.RoundStartTick : 0)
                    .Append("  map ").Append(_map != null ? MapContent.Short(_map.ContentHash) : "-").Append(cs.MapMismatch != null ? " MISMATCH" : "")
                    .Append("  variant ").Append(rp.Variant == 0 ? "A" : "B").Append("  modifier ").Append(ModifierEffects.Get(rp.Modifier).Key).Append('\n');
            }

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
            rt.sizeDelta = new Vector2(640f, 286f);
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

            // "Lagging" icon (doc 04 §4.4): no snapshot for > 150 ms past the interpolation buffer.
            _lagIcon = new GameObject("LagIcon", typeof(RectTransform), typeof(Image));
            var lrt = (RectTransform)_lagIcon.transform;
            lrt.SetParent(canvas, false);
            lrt.anchorMin = lrt.anchorMax = lrt.pivot = new Vector2(1f, 1f);
            lrt.anchoredPosition = new Vector2(-16f, -16f);
            lrt.sizeDelta = new Vector2(150f, 44f);
            _lagIcon.GetComponent<Image>().color = UiKit.SunYellow;
            var lt = UiKit.Label(lrt, "! LAGGING", 22, UiKit.Ink, FontStyle.Bold);
            var ltr = (RectTransform)lt.transform;
            ltr.anchorMin = Vector2.zero;
            ltr.anchorMax = Vector2.one;
            ltr.offsetMin = ltr.offsetMax = Vector2.zero;
            _lagIcon.SetActive(false);

            var hint = UiKit.Label(canvas, "Click to capture mouse · WASD move · Space jump · Shift/LMB dive · Esc options · F3 net HUD · Tab spectate (after finishing)", 16, UiKit.Ink);
            var hrt = (RectTransform)hint.transform;
            hrt.anchorMin = hrt.anchorMax = hrt.pivot = new Vector2(0.5f, 0f);
            hrt.anchoredPosition = new Vector2(0f, 10f);
            hrt.sizeDelta = new Vector2(1000f, 28f);

            BuildOptions(canvas);

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

        private void BuildOptions(Transform canvas)
        {
            var panel = UiKit.Panel(canvas, new Vector2(460f, 400f), UiKit.CloudWhite);
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(1f, 0f);
            panel.anchoredPosition = new Vector2(-16f, 48f);
            _options = panel.gameObject;
            UiKit.Label(panel, "OPTIONS", 24, UiKit.Grape, FontStyle.Bold);
            _optionsText = UiKit.Label(panel, string.Empty, 18, UiKit.Ink, FontStyle.Normal, 60);
            UiKit.Button(panel, "Sensitivity −", UiKit.SkyLight, () => Bump(-0.02f));
            UiKit.Button(panel, "Sensitivity +", UiKit.SkyLight, () => Bump(0.02f));
            UiKit.Button(panel, "Toggle invert Y", UiKit.SunYellow, () =>
            {
                GameSettings.InvertY = !GameSettings.InvertY;
                GameSettings.Save();
                RefreshOptions();
            });
            UiKit.Button(panel, "Toggle reduced motion", UiKit.Mint, () =>
            {
                GameSettings.ReducedMotion = !GameSettings.ReducedMotion;
                GameSettings.Save();
                RefreshOptions();
            });
            foreach (var le in panel.GetComponentsInChildren<LayoutElement>())
                if (le.minHeight >= 64)
                    le.minHeight = 44;
            RefreshOptions();
            _options.SetActive(false);
        }

        private void Bump(float d)
        {
            GameSettings.Sensitivity += d;
            GameSettings.Save();
            RefreshOptions();
        }

        private void RefreshOptions()
        {
            _optionsText.text = $"Sensitivity {GameSettings.Sensitivity:F2} °/count · Invert Y {(GameSettings.InvertY ? "ON" : "off")}\n" +
                                $"Reduced motion {(GameSettings.ReducedMotion ? "ON (no shake, no FOV kick)" : "off")}\nClick the game to play";
        }

        // ------------------------------------------------------------------ photo mode (proof screenshots)

        private static string PhotoArg()
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], "-fallrushPhoto", StringComparison.OrdinalIgnoreCase))
                    return args[i + 1].ToLowerInvariant();
            return null;
        }

        private void EnterPhotoMode(Camera cam)
        {
            var brain = cam.GetComponent<Unity.Cinemachine.CinemachineBrain>();
            if (brain != null)
                brain.enabled = false;
            _hud.SetActive(false);
            cam.fieldOfView = _photo == "silhouette" ? 12f : 35f;
            foreach (var c in GetComponentsInChildren<Canvas>(true))
                c.enabled = false;
            if (_photo != "silhouette")
                return;
            // Pure black figures on white: hide the map.
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.white;
            foreach (var r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                if (!r.transform.IsChildOf(transform))
                    r.enabled = false;
        }

        private void PhotoCamera()
        {
            var cam = Camera.main;
            if (cam == null || !_hasOwn)
                return;
            var c = _lastOwnFeet + Vector3.up * 0.55f;
            // Close-up: three-quarter front view (face, goggles, emblem) relative to the Rushling's facing.
            var own = ClientSim.Instance != null ? ClientSim.Instance.OwnSlot : -1;
            var facing = own >= 0 && own < _rushlings.Length ? _rushlings[own].transform.rotation : Quaternion.identity;
            var off = _photo == "silhouette" ? new Vector3(20f, 1.2f, 0f) : facing * new Vector3(1.3f, 0.75f, 2.4f);
            // Silhouette: side-on from 20 m (the run script moves along Z, so the profile shows arms and key).
            cam.transform.SetPositionAndRotation(c + off, Quaternion.LookRotation(-off));
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
            if (!NetBootstrap.IsMapScene(scene.name))
                return;
            var boot = NetBootstrap.Instance;
            if (boot == null || boot.IsServerRole || (boot.IsProbe && !SimProbe.ViewEnabled))
                return;
            var view = new GameObject("SandboxView").AddComponent<SandboxView>();
            view.gameObject.AddComponent<MatchHud>().View = view; // M4: every match screen (replaces M3's RaceHud)
        }
    }
}
