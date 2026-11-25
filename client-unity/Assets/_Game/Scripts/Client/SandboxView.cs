using System;
using System.Text;
using FallRush.Net;
using FallRush.Shared;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using TMPro;
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
        private CheckpointMarkers _markers;
        private Nameplates _plates;
        private readonly string[] _plateText = new string[Protocol.MaxPlayers];
        private readonly string[] _plateName = new string[Protocol.MaxPlayers];
        private readonly byte[] _plateKind = new byte[Protocol.MaxPlayers];
        private readonly Vector3[] _feetNow = new Vector3[Protocol.MaxPlayers];
        private readonly bool[] _visibleNow = new bool[Protocol.MaxPlayers];
        private GameObject _hint;
        private int _followedSlot = -2;
        private Vector3 _blendFrom;
        private float _blendT = 1f;
        private float _hintUntil;
        private bool[] _wasRespawning;
        private Transform _cameraTarget;
        private FollowCamera _cam;
        private PlayerInputSampler _sampler;
        private GameObject _hud;
        private TMP_Text _hudText;
        private GameObject _overlay;
        private TMP_Text _overlayText;
        private GameObject _lagIcon;
        private GameObject _options;
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
        private BlobShadows _blobs;
        private Renderer[] _signs;
        private float _signsAt;

        /// <summary>In-world signs (TMP) hide when the camera is closer than 8 m: never a wall of giant letters.</summary>
        private void FadeSigns()
        {
            if (Time.unscaledTime < _signsAt)
                return;
            _signsAt = Time.unscaledTime + 0.15f;
            if (_signs == null)
            {
                var t = FindObjectsByType<TMPro.TextMeshPro>(FindObjectsSortMode.None);
                _signs = new Renderer[t.Length];
                for (var i = 0; i < t.Length; i++)
                    _signs[i] = t[i].GetComponent<Renderer>();
            }

            var cam = Camera.main;
            if (cam == null)
                return;
            var cp = cam.transform.position;
            var cs = ClientSim.Instance;
            var intro = cs != null && cs.Phase == RoundPhase.Intro;
            foreach (var r in _signs)
                if (r != null)
                {
                    // Step 0: a map's title sign shows during the intro fly-through only, so it never sits behind the
                    // HUD's top-centre counter during play.
                    var title = r.name == "Label_STAR SCRAMBLE" || r.name == "Label_GOO TOWER" || r.name == "Label_CRUMBLE SHELF" || r.name == "Label_BLOCK PARTY DASH";
                    var on = (r.bounds.ClosestPoint(cp) - cp).sqrMagnitude > 8f * 8f && (!title || intro);
                    if (r.enabled != on)
                        r.enabled = on;
                }
        }
        private ClawPresenter _claw;

        private static MatchProbeBot s_MatchBot; // WebGL ?bot=match: one bot for the whole match (survives scene loads)

        /// <summary>Spectate: follow this slot instead of our own (MatchHud sets it while spectating); −1 = own.</summary>
        public int FollowSlot = -1;

        /// <summary>M4: the camera looks at this point instead of a player (round-intro fly-through, champion podium).</summary>
        public Vector3? FocusPoint;

        private void Start()
        {
            _map = MapInfo.Current;
            WorldLook.OnMapLoaded();
            _markers = CheckpointMarkers.Build(_map, transform); // 2026-11-24: flags on Goo Tower's respawn spots (visual only)
            var lit = _map != null ? _map.playerMaterial : null;
            var unlit = _map != null ? _map.unlitMaterial : null;
            _rushlings = new RushlingVisuals[Protocol.MaxPlayers];
            _wasRespawning = new bool[Protocol.MaxPlayers];
            for (var i = 0; i < Protocol.MaxPlayers; i++)
            {
                var root = new GameObject($"Rushling{i}").transform;
                root.SetParent(transform, false);
                var vis = root.gameObject.AddComponent<RushlingVisuals>();
                vis.Build(i, lit, unlit);

                vis.SetVisible(false);
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
            GraphicsQuality.Apply(cam); // G2.1 High / Low
            _blobs = new BlobShadows(PhysxMotorWorld.GameplayMask());
            _claw = gameObject.AddComponent<ClawPresenter>();
            FxDirector.Get();
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
            if (cs != null)
                cs.EmoteReceived += OnEmote;
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
                cs.EmoteReceived -= OnEmote;
            if (cs != null)
            {
                cs.Active = false;
                if (ReferenceEquals(cs.InputSource, _sampler))
                    cs.InputSource = null;
            }

            Cursor.lockState = CursorLockMode.None;
            CursorPolicy.Reset();
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
            // G4.8: F7 toggles the map look (soft / candy) so the user can A/B it in play; render only.
            if (kb != null && kb.f7Key.wasPressedThisFrame)
                LookState.Toggle();
            // G1: the controls bar shows for a few seconds after a map loads, then on F1.
            if (kb != null && kb.f1Key.wasPressedThisFrame)
                _hintUntil = _hint.activeSelf ? 0f : float.MaxValue;
            var hintOn = Time.unscaledTime < _hintUntil && _photo == null;
            if (_hint.activeSelf != hintOn)
                _hint.SetActive(hintOn);
            // P7-UX (OC-44): CursorPolicy owns the pointer lock. An open interactive panel or spectating frees the cursor;
            // only an alive player's click on the game (never on UI) takes the lock. Esc closes the top panel first.
            var probe = NetBootstrap.Instance != null && NetBootstrap.Instance.IsProbe; // scripted view: no pointer lock
            var escPressed = kb != null && kb.escapeKey.wasPressedThisFrame;
            if (escPressed)
                Cursor.lockState = CursorLockMode.None;
            CursorPolicy.SetOpen("settings", _optionsOpen);
            CursorPolicy.Apply(_overlay.activeSelf);
            var clicked = mouse != null && mouse.leftButton.wasPressedThisFrame;
            var pointerOverUi = clicked && CursorPolicy.OverUi(mouse.position.ReadValue());
            var otherPanel = CursorPolicy.AnyOpenExcept("settings");
            if (CursorPolicy.MayLock(clicked && Cursor.lockState != CursorLockMode.Locked, pointerOverUi,
                    otherPanel, CursorPolicy.Spectating, _overlay.activeSelf, probe))
            {
                Cursor.lockState = CursorLockMode.Locked; // WebGL: pointer lock needs this user gesture
                _optionsOpen = false;
            }

            // G1: settings open on Esc only; a click on the game closes them.
            var locked = Cursor.lockState == CursorLockMode.Locked;
            var policyFreed = Time.frameCount - CursorPolicy.PolicyFreeFrame <= 1;
            if (_wasLocked && !locked && !policyFreed && !CursorPolicy.JustClosed)
                _optionsOpen = true; // Esc while playing (the browser releases pointer lock itself)
            else if (escPressed && !locked && !CursorPolicy.JustClosed && !otherPanel)
                _optionsOpen = !_optionsOpen;
            if (locked)
                _optionsOpen = false;
            _wasLocked = locked;
            var showOptions = _optionsOpen && !_overlay.activeSelf && _photo == null && !probe;
            CursorPolicy.SetOpen("settings", showOptions);
            UiMotion.SetVisible((RectTransform)_options.transform, showOptions); // P7: settings open / close with UiMotion

            var cs = ClientSim.Instance;
            if (cs == null)
                return;
            var dt = Time.deltaTime;
            var own = cs.OwnSlot;
            var match = cs.Match;
            if (kb != null && match != null && EmoteCodec.PhaseAllows(match.Phase) && Cursor.lockState == CursorLockMode.Locked)
            {
                var e = kb.digit1Key.wasPressedThisFrame ? 1 : kb.digit2Key.wasPressedThisFrame ? 2 : kb.digit3Key.wasPressedThisFrame ? 3 : kb.digit4Key.wasPressedThisFrame ? 4 : 0;
                if (e != 0 && Time.unscaledTime >= _emoteNext)
                {
                    _emoteNext = Time.unscaledTime + 1.05f;
                    if (own >= 0 && own < _rushlings.Length)
                        _rushlings[own].PlayEmote(e); // at once; the server's echo for our slot is ignored
                    cs.SendControl(ControlCmd.Emote, (uint)e);
                }
            }
            _blobs.Begin();
            for (var i = 0; i < Protocol.MaxPlayers; i++)
            {
                var vis = _rushlings[i];
                var sampled = cs.TrySample(i, out var feet, out var vel, out var flags, out var yaw, out var stun);
                var capsuled = ReactiveViews.Current != null && ReactiveViews.Current.CapsuleHides(i);
                if (!sampled || (flags & MotorState.Respawning) != 0 || capsuled)
                {
                    // Dizzy hits (2026-11-24): falling off the course no longer vanishes in a poof. The Rushling keeps
                    // tumbling down (stars on, visual only: the sim froze it at the fall line) until the respawn moves it.
                    // dizzy2 (2026-11-25, user: "got hit … it vanishes"): also in the lobby / sandbox (free play respawns
                    // too, it used to vanish there), and for a player who drops out of the snapshot mid-fall (knocked
                    // out in an on-chain round: out players leave the World's player list at once) — it still tumbles
                    // down from where it was instead of blinking out.
                    var phaseFalls = cs.Phase == RoundPhase.Rush || cs.Phase == RoundPhase.Sandbox || cs.Phase == RoundPhase.Waiting;
                    var droppedMidFall = !sampled && (_fallT[i] >= 0f || _wasVisible[i] && _lastVelS[i].y < -1.5f);
                    var falling = !capsuled && phaseFalls && (sampled ? (flags & MotorState.Respawning) != 0 : droppedMidFall);
                    if (!_wasRespawning[i])
                    {
                        _wasRespawning[i] = true;
                        _fallT[i] = -1f;
                        if (_wasVisible[i] && (cs.Phase == RoundPhase.Rush || falling))
                        {
                            var gooed = _map != null && _map.goo.Length > 0;
                            GameAudio.Hits.KnockedOut(i == own, gooed); // own only
                            if (gooed)
                                Fx.Splash(_lastFeet[i]); // goo eliminates: it still sinks out of sight
                            else if (falling)
                            {
                                _fallT[i] = 0f;
                                _fallFeet[i] = _lastFeet[i];
                                _fallVel[i] = _lastVelS[i];
                            }
                        }

                        if (_fallT[i] < 0f)
                            vis.SetVisible(false);
                    }

                    if (_fallT[i] >= 0f)
                    {
                        if (!falling || _fallT[i] > KnockOffFallSecs)
                        {
                            _fallT[i] = -1f;
                            vis.SetVisible(false); // out for good (an elimination), or the respawn is late
                        }
                        else
                        {
                            _fallT[i] += dt;
                            _fallVel[i] += Vector3.down * (22f * dt);
                            _fallFeet[i] += _fallVel[i] * dt;
                            vis.Drive(_fallFeet[i], _fallVel[i], 0, sampled ? yaw : _lastYaw[i], 30, dt);
                        }
                    }

                    _wasVisible[i] = false;

                    _visibleNow[i] = false;
                    _plates.Set(i, false, default, null);
                    continue;
                }

                if (_wasRespawning[i])
                {
                    var fell = _fallT[i] >= 0f;
                    _fallT[i] = -1f;
                    if (fell)
                        vis.SetVisible(false); // reset the pose: it reappears upright at the checkpoint
                    vis.SetVisible(true);
                    if (fell)
                        vis.Dizzy(RushlingVisuals.DizzyTail); // still seeing stars
                    if (_photo == "silhouette")
                        vis.SetSilhouette(true);
                    _wasRespawning[i] = false;
                }

                vis.SetPreset(cs.TryGetSlotInfo(i, out var slotInfo) ? slotInfo.Preset : CosmeticPresets.ForBot(i)); // G2.2 (cheap when unchanged)
                vis.SetPose(PoseFor(cs, match, i));
                vis.SetGrey(_claw != null && _claw.IsGrey(i));
                if (_claw != null && _claw.Holding(i, out var held))
                    feet = held; // the claw carries it (visual only)
                if (Time.unscaledTime >= _hitStopUntil[i])
                    vis.Drive(feet, vel, flags, yaw, stun, dt);
                    if (i == own && (flags & MotorState.Grounded) != 0)
                        _markers?.Touch(feet);
                var camT = Camera.main != null ? Camera.main.transform.position : feet;
                var d2 = (camT - feet).sqrMagnitude;
                vis.SetOutline(i == own || d2 < (vis.Preset >= 0 ? 22f * 22f : 0f)); // G2.4: hull only near the camera
                Juice(i, vis, feet, vel, i == own, cs);
                _wasVisible[i] = true;
                _lastFeet[i] = feet;
                _lastVelS[i] = vel;
                _lastYaw[i] = yaw;
                _feetNow[i] = feet;
                _visibleNow[i] = true;
                if (_photo == null)
                    _blobs.Add(feet);
                var bot = (flags & MotorState.Bot) != 0;
                _plates.Set(i, _photo == null, feet + Vector3.up * 1.3f, PlateText(cs, i, bot, i == own));

                if (i == (FollowSlot >= 0 ? FollowSlot : own))
                {
                    var target = feet + Vector3.up * 0.8f;
                    var followed = FollowSlot >= 0 ? FollowSlot : own;
                    if (followed != _followedSlot)
                    {
                        // G1: spectate switch → glide to the new player over 0.45 s (a cut if it is very far away).
                        _followedSlot = followed;
                        _blendFrom = _cameraTarget.position;
                        _blendT = _hasOwn && (target - _blendFrom).sqrMagnitude < 60f * 60f ? 0f : 1f;
                    }
                    else if (_hasOwn && (feet - _lastOwnFeet).sqrMagnitude > 16f)
                        _cam.Warp(target - _cameraTarget.position); // respawn teleport

                    if (_blendT < 1f)
                    {
                        _blendT = Mathf.Min(1f, _blendT + dt / 0.45f);
                        target = Vector3.Lerp(_blendFrom, target, Mathf.SmoothStep(0f, 1f, _blendT));
                    }

                    _cameraTarget.position = target;
                    _lastOwnFeet = feet;
                    _hasOwn = true;
                    if (vis.DiveStartedThisFrame)
                        _cam.DiveKick();
                    if (vis.StunStartedThisFrame)
                        _cam.Shake(0.12f);
                }
            }

            // Crowd bumps (pushes are server-only; this is only the wobble you see).
            for (var i = 0; i < Protocol.MaxPlayers; i++)
            {
                if (!_visibleNow[i])
                    continue;
                for (var j = i + 1; j < Protocol.MaxPlayers; j++)
                {
                    if (!_visibleNow[j])
                        continue;
                    var d = _feetNow[i] - _feetNow[j];
                    if (Mathf.Abs(d.y) > 0.8f)
                        continue;
                    d.y = 0f;
                    var m2 = d.sqrMagnitude;
                    if (m2 > 0.7f * 0.7f || m2 < 1e-6f)
                        continue;
                    var k = (0.7f - Mathf.Sqrt(m2)) / 0.7f * dt * 6f;
                    var n = d.normalized;
                    _rushlings[i].Bump(n, k);
                    _rushlings[j].Bump(-n, k);
                }
            }

            _blobs.Draw();
            _plates.Layout(Camera.main, own);

            if (FocusPoint.HasValue)
            {
                var target = FocusPoint.Value;
                if ((target - _cameraTarget.position).sqrMagnitude > 400f)
                    _cam.Warp(target - _cameraTarget.position);
                _cameraTarget.position = target;
                _lastOwnFeet = target;
            }

            UpdateHazardVisuals(cs);
            FadeSigns();

            // P7: during the on-chain Final the Classic server sends no snapshots on purpose; its lag is not the player's
            var lagging = cs.Remote.Lagging && ChainAdapters.Override == null;
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

        /// <summary>The followed Rushling's feet (effects anchor for HUD events).</summary>
        public Vector3 OwnFeet => _lastOwnFeet;

        // G3.3 juice state (client only).
        private readonly float[] _hitStopUntil = new float[Protocol.MaxPlayers];
        private readonly float[] _prevVy = new float[Protocol.MaxPlayers];
        private readonly float[] _slideFxAt = new float[Protocol.MaxPlayers];
        private readonly bool[] _wasVisible = new bool[Protocol.MaxPlayers];
        private readonly Vector3[] _lastFeet = new Vector3[Protocol.MaxPlayers];
        private readonly Vector3[] _lastVelS = new Vector3[Protocol.MaxPlayers];
        private readonly byte[] _lastYaw = new byte[Protocol.MaxPlayers]; // dizzy2: an unsampled faller keeps its facing
        private readonly float[] _landSfxAt = new float[Protocol.MaxPlayers]; // dizzy2: landing-sound cooldown per Rushling
        public const float LandSfxGap = 0.3f;
        private readonly float[] _jumpSfxAt = new float[Protocol.MaxPlayers];

        // Dizzy hits: the visual fall after leaving the course (−1 = none).
        public const float KnockOffFallSecs = 1.4f;
        private readonly float[] _fallT = NewFallTimers();
        private readonly Vector3[] _fallFeet = new Vector3[Protocol.MaxPlayers];
        private readonly Vector3[] _fallVel = new Vector3[Protocol.MaxPlayers];

        private static float[] NewFallTimers()
        {
            var a = new float[Protocol.MaxPlayers];
            for (var i = 0; i < a.Length; i++)
                a[i] = -1f;
            return a;
        }
        private int _stepParity;

        /// <summary>Per-Rushling effects and sounds from the G1 juice hooks (visual / audio only).</summary>
        private void Juice(int i, RushlingVisuals vis, Vector3 feet, Vector3 vel, bool own, ClientSim cs)
        {
            var cam = Camera.main;
            var near = own || cam != null && (cam.transform.position - feet).sqrMagnitude < 14f * 14f;
            var vol = own ? 1f : 0.55f;
            // dizzy2 (2026-11-25, user: "a creepy vibration sound … when the char is vanished"): a Rushling pinned on a
            // hazard after a hit flickers grounded / airborne every tick or two, and each flicker replayed the low landing
            // thud — 10–30 thuds a second read as a buzz. One landing sound per 0.3 s per Rushling, none while staggering.
            if (vis.LandedThisFrame && vis.LandImpact > 0.06f && Time.unscaledTime >= _landSfxAt[i])
            {
                _landSfxAt[i] = Time.unscaledTime + LandSfxGap;
                Fx.Dust(feet, vis.LandImpact);
                if (near && !vis.Staggering)
                {
                    GameAudio.Play(GameAudio.Sfx.Land, feet, vol * Mathf.Lerp(0.4f, 1f, vis.LandImpact), 0.9f + UnityEngine.Random.value * 0.2f);
                    if (vis.Sliding)
                        GameAudio.Play(GameAudio.Sfx.Slide, feet, vol);
                }
            }

            if (vis.StepThisFrame && near && (own || (++_stepParity & 1) == 0))
                GameAudio.Play(GameAudio.Sfx.Step, feet, vol * 0.35f, 0.85f + UnityEngine.Random.value * 0.3f);
            if (vis.JumpedThisFrame && near && !vis.Staggering && Time.unscaledTime >= _jumpSfxAt[i]) // dizzy2: a knock-up is not a jump
            {
                _jumpSfxAt[i] = Time.unscaledTime + LandSfxGap;
                GameAudio.Play(GameAudio.Sfx.Jump, feet, vol * 0.7f, 0.95f + UnityEngine.Random.value * 0.12f);
            }
            if (vis.DiveStartedThisFrame)
            {
                Fx.Streaks(feet, vel.sqrMagnitude > 0.01f ? new Vector3(vel.x, 0f, vel.z).normalized : vis.transform.forward);
                if (near)
                    GameAudio.Play(GameAudio.Sfx.Dive, feet, vol);
            }

            if (vis.Sliding && Time.unscaledTime >= _slideFxAt[i])
            {
                _slideFxAt[i] = Time.unscaledTime + 0.08f;
                Fx.SlideDust(feet);
            }

            if (vis.StunStartedThisFrame)
            {
                Fx.Bonk(feet);
                _hazardViews?.SquashBumperNear(feet); // G4.9: the pillow bumper squashes and wobbles (client-only)
                GameAudio.Hits.Stunned(own, feet); // dizzy hits: boing + birds, your own hits only
                if (own)
                    _cam?.Knocked(); // dizzy2: the camera tracks the knock at once and stays out of the body
                // Hit-stop: the visuals freeze ~60 ms (the sim never does), except inside a rolling ball (no collider):
                // there a frozen body hides in the 2 m ball and reads as gone.
                if (_hazardViews == null || !_hazardViews.BallNear(feet + Vector3.up * 0.6f, 0.6f))
                    _hitStopUntil[i] = Time.unscaledTime + 0.06f;
            }

            if (vis.StumbleStartedThisFrame)
            {
                Fx.Dust(feet, 0.7f);
                if (near && !vis.Dazed) // dizzy2: a knocked Rushling's hard landing is part of the hit, not a warble
                    GameAudio.Play(GameAudio.Sfx.Stumble, feet, vol);
            }

            if (vis.GetUpThisFrame)
                GameAudio.Hits.GotUp(own, feet);
            if (vis.EmoteStartedThisFrame && near)
            {
                GameAudio.Play(GameAudio.Sfx.Emote, feet, vol);
                GameAudio.Chirp(vis.Emote == 4 ? 2 : 0, feet);
            }

            // Spring pads: a sudden upward launch (visual check only).
            if (vel.y - _prevVy[i] > 6f && vel.y > 7f)
            {
                if (near)
                    GameAudio.Play(GameAudio.Sfx.Spring, feet, vol);
                _hazardViews?.SquashPadNear(feet);
                if (own)
                    _cam.DiveKick();
            }

            _prevVy[i] = vel.y;
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
            WorldLook.SetModifierFog(mod.FogPermille); // G2: base fog from the map art pass; TinyTilt adds its own
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
            _sb.Append("<b>NET (F3)</b>  predicted (pushes too)\n");
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
            var t = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
            var trt = (RectTransform)t.transform;
            trt.SetParent(rt, false);
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(10f, 8f);
            trt.offsetMax = new Vector2(-10f, -8f);
            t.font = UiKit.Body;
            t.fontSize = 14;
            t.color = Color.white;
            t.richText = true;
            t.alignment = TextAlignmentOptions.TopLeft;
            t.raycastTarget = false;
            _hudText = t;

            // "Lagging" icon (doc 04 §4.4): no snapshot for > 150 ms past the interpolation buffer.
            _lagIcon = new GameObject("LagIcon", typeof(RectTransform), typeof(Image));
            var lrt = (RectTransform)_lagIcon.transform;
            lrt.SetParent(canvas, false);
            lrt.anchorMin = lrt.anchorMax = lrt.pivot = new Vector2(1f, 1f);
            lrt.anchoredPosition = new Vector2(-18f, -98f); // P7-UX2: outlined amber type under the timer (no pill)
            lrt.sizeDelta = new Vector2(160f, 26f);
            _lagIcon.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
            var lt = UiKit.HudText(lrt, 18, UiKit.Amber);
            lt.text = "! LAGGING";
            lt.alignment = TextAlignmentOptions.Right;
            UiKit.Stretch((RectTransform)lt.transform);
            _lagIcon.SetActive(false);

            // Nameplates on their own canvas, sorted under every HUD / match panel (they drew over the result panels).
            var plateCanvas = UiKit.CreateCanvas("NameplateCanvas");
            plateCanvas.SetParent(transform, false);
            plateCanvas.GetComponent<Canvas>().sortingOrder = -10;
            _plates = new Nameplates(plateCanvas, PhysxMotorWorld.GameplayMask());
            // P7-UX (OC-45): the controls as a keycap card bottom-left (was a 1010 px caption strip): shown for 10 s in the
            // first two matches, then on F1 only.
            var hintCard = UiKit.MakeCard(canvas, UiKit.CardSize.S, UiKit.CardHue.Grape, "CONTROLS", new Vector2(196f, 226f), UiIcon.None);
            var hrt = hintCard.Root;
            hrt.anchorMin = hrt.anchorMax = hrt.pivot = new Vector2(0f, 0f);
            hrt.anchoredPosition = new Vector2(16f, 90f);
            hintCard.Body.GetComponent<VerticalLayoutGroup>().childAlignment = TextAnchor.UpperLeft;
            UiKit.KeyHint(hintCard.Body, "WASD", "Move", 14, 24f);
            UiKit.KeyHint(hintCard.Body, "Space", "Jump", 14, 24f);
            UiKit.KeyHint(hintCard.Body, "Shift", "Dive", 14, 24f);
            UiKit.KeyHint(hintCard.Body, "1-4", "Emote", 14, 24f);
            UiKit.KeyHint(hintCard.Body, "Q E", "Watch next", 14, 24f);
            UiKit.KeyHint(hintCard.Body, "B", "Bets", 14, 24f);
            UiKit.KeyHint(hintCard.Body, "Esc", "Menu", 14, 24f);
            _hint = hrt.gameObject;
            var seen = PlayerPrefs.GetInt("fr.controlsSeen", 0);
            _hintUntil = seen < 2 ? Time.unscaledTime + 10f : 0f;
            PlayerPrefs.SetInt("fr.controlsSeen", seen + 1);
            _hint.SetActive(false);

            BuildOptions(canvas);

            var panel = UiKit.Panel(canvas, new Vector2(620f, 340f), UiKit.CloudWhite);
            _overlay = panel.gameObject;
            _overlayText = UiKit.Label(panel, string.Empty, 26, UiKit.Magenta, FontStyle.Bold, 120);
            _overlayText.font = UiKit.Display;
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
            var panel = SettingsPanel.Build(canvas, new Vector2(600f, 600f));
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(1f, 0.5f);
            panel.anchoredPosition = new Vector2(-16f, 0f);
            UiKit.Label(panel, "Click the game to play · F1 controls · F3 net HUD", 14, UiKit.Grape);
            _options = panel.gameObject;
            _options.SetActive(false);
        }

        private float _emoteNext;
        private bool _optionsOpen, _wasLocked;

        private void OnEmote(int slot, int id)
        {
            var cs = ClientSim.Instance;
            if (slot < 0 || slot >= _rushlings.Length || (cs != null && slot == cs.OwnSlot))
                return;
            _rushlings[slot].PlayEmote(id);
        }

        /// <summary>Celebration poses: qualified / eliminated during the round result, the champion afterwards.</summary>
        private static RushlingVisuals.Pose PoseFor(ClientSim cs, MatchStateMessage m, int slot)
        {
            if (m == null || !cs.TryGetSlotInfo(slot, out var info))
                return RushlingVisuals.Pose.None;
            if (m.Phase == MatchPhase.RoundResult)
                return info.Has(MatchSlotInfo.Alive) ? RushlingVisuals.Pose.Qualified : info.EliminatedRound == m.Round ? RushlingVisuals.Pose.Eliminated : RushlingVisuals.Pose.None;
            if (m.Phase >= MatchPhase.Champion && m.Phase <= MatchPhase.Done && slot == m.Champion)
                return RushlingVisuals.Pose.Champion;
            return RushlingVisuals.Pose.None;
        }

        private string PlateText(ClientSim cs, int slot, bool bot, bool own)
        {
            var name = cs.TryGetSlotInfo(slot, out var info) && !string.IsNullOrEmpty(info.Name) ? info.Name : $"P{slot + 1}";
            var kind = (byte)((bot ? 1 : 0) | (own ? 2 : 0));
            if (_plateText[slot] == null || _plateKind[slot] != kind || !ReferenceEquals(_plateName[slot], name) && _plateName[slot] != name)
            {
                _plateName[slot] = name;
                _plateKind[slot] = kind;
                _plateText[slot] = Nameplates.LabelFor(name, bot, own);
            }

            return _plateText[slot];
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

        private RushlingVisuals[] _gallery;
        private Vector3 _galleryAt;

        /// <summary>G2.2 proof: every cosmetic preset in two rows of six, idling, face to the camera.</summary>
        private void PresetGallery(Camera cam)
        {
            if (_gallery == null)
            {
                _galleryAt = _lastOwnFeet + new Vector3(0f, 0f, 0f);
                _gallery = new RushlingVisuals[CosmeticPresets.Count];
                for (var i = 0; i < _gallery.Length; i++)
                {
                    var go = new GameObject($"Gallery{i}");
                    go.transform.SetParent(transform, false);
                    var v = go.AddComponent<RushlingVisuals>();
                    v.Build(i, i, _map != null ? _map.playerMaterial : null, _map != null ? _map.unlitMaterial : null);
                    v.SetVisible(true);
                    _gallery[i] = v;
                }

                foreach (var r in _rushlings)
                    r.SetVisible(false);
            }

            for (var i = 0; i < _gallery.Length; i++)
            {
                var row = i / 6;
                var col = i % 6;
                var p = _galleryAt + new Vector3((col - 2.5f) * 1.05f, 0f, row * 1.4f);
                _gallery[i].Drive(p, Vector3.zero, MotorState.Grounded, 128, 0, Time.deltaTime);
            }

            var c = _galleryAt + new Vector3(0f, 0.9f, 0.7f);
            cam.fieldOfView = 40f;
            cam.transform.SetPositionAndRotation(c + new Vector3(0f, 1.6f, -6.4f), Quaternion.LookRotation(new Vector3(0f, -1.25f, 6.4f)));
        }

        private void PhotoCamera()
        {
            var cam = Camera.main;
            if (cam == null || !_hasOwn)
                return;
            foreach (var cv in GetComponentsInChildren<Canvas>(true))
                if (cv.enabled)
                    cv.enabled = false; // the match HUD builds its canvas after photo mode starts
            if (_photo == "presets")
            {
                PresetGallery(cam);
                return;
            }
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
            if (NetBootstrap.IsPractice)
            {
                new GameObject("PracticeMode").AddComponent<PracticeMode>(); // DEV only (G1 §3.7)
                return;
            }

            if (boot == null || boot.IsServerRole || (boot.IsProbe && !SimProbe.ViewEnabled))
                return;
            var view = new GameObject("SandboxView").AddComponent<SandboxView>();
            view.gameObject.AddComponent<MatchHud>().View = view; // M4: every match screen (replaces M3's RaceHud)
        }
    }
}
