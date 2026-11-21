using FallRush.Net;
using FallRush.Shared;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FallRush.Client
{
    /// <summary>
    /// G1 §3.7 DEV ONLY local practice / tuning mode: runs the shared motor (<see cref="PlayerStep.Run"/>) and the map's
    /// hazards on the client, with no server, so movement feel can be tuned live. F6 opens a panel to edit the movement
    /// values (<see cref="MovementTuning"/>), F7 prints them as JSON (log + clipboard) to paste into
    /// <c>packages/shared/config/movement_v2.json</c>. Reactive elements (crumble tiles, falling blocks, stars) stay solid /
    /// idle here; the goo does not rise.
    /// <para>Entry: Editor / development builds only — <c>-fallrushPractice BlockPartyDash</c> (desktop) or
    /// <c>?practice=BlockPartyDash</c> (WebGL dev), or the menu's "Practice (dev)" button. Release builds compile the entry
    /// out (<see cref="NetBootstrap.PracticeMapArg"/>), so it never ships enabled.</para>
    /// </summary>
    public sealed class PracticeMode : MonoBehaviour
    {
        private MapInfo _map;
        private PhysxMotorWorld _world;
        private HazardSet _hz;
        private KinematicMotor _motor;
        private MovementTuning _tuning;
        private MotorState _s, _prev;
        private uint _tick;
        private double _acc;
        private int _respawnIn;
        private RushlingVisuals _vis;
        private FollowCamera _cam;
        private Transform _camTarget;
        private PlayerInputSampler _sampler;
        private HazardViewDriver _views;
        private bool _panel = true;
        private int _sel;
        private Vec3 _spawn;
        private byte _spawnYaw;
        // live readouts
        private float _airStartY, _apex, _airTime, _airDist;
        private Vector3 _airStartPos;
        private bool _wasGrounded = true;
        private float _renderAlpha;
        private string _lastJump = "–";

        private static readonly string[] Fields =
        {
            "run.maxSpeed (mm/s)", "run.groundAccel (mm/s²)", "run.groundDecel (mm/s²)", "run.airControlPermille",
            "gravity.accel (mm/s²)", "gravity.fallMultiplierPermille", "jump.apexHeight (mm)",
            "dive.horizontalSpeed (mm/s)", "dive.verticalSpeed (mm/s)", "dive.landStunMs (slide)", "dive.slideDecel (mm/s²)",
            "stumble.minFallHeight (mm)", "stumble.durationMs", "stumble.speedPermille",
        };

        private static readonly int[] Steps = { 250, 2000, 2000, 25, 1000, 50, 50, 250, 250, 50, 1000, 250, 50, 50 };

        private void Start()
        {
            _map = MapInfo.Current;
            if (_map == null)
            {
                Debug.LogError("[practice] no MapInfo in this scene");
                enabled = false;
                return;
            }

            _tuning = MovementTuning.Generated;
            var cfg = MovementConfig.FromTuning(_tuning, 0);
            _motor = new KinematicMotor(cfg);
            var p = RoundParams.FromDevSeed(_map.definition != null ? _map.definition.mapId : MapPoolV1.Sandbox, 1);
            p.RoundStartTick = 90; // start gates open after 3 s
            _world = _map.CreateWorld(transform, cfg, in p, out _hz);
            var spawns = _map.SpawnPoints();
            var yaws = _map.SpawnYaws();
            _spawn = spawns[0];
            _spawnYaw = yaws[0];
            Respawn();

            var root = new GameObject("PracticeRushling").transform;
            root.SetParent(transform, false);
            _vis = root.gameObject.AddComponent<RushlingVisuals>();
            _vis.Build(0, _map.playerMaterial, _map.unlitMaterial);
            _vis.SetVisible(true);
            _camTarget = new GameObject("CameraTarget").transform;
            _camTarget.SetParent(transform, false);
            var cam = Camera.main;
            if (cam == null)
            {
                cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)).GetComponent<Camera>();
                cam.tag = "MainCamera";
            }

            _cam = FollowCamera.Create(cam, _camTarget, PhysxMotorWorld.GameplayMask());
            _sampler = gameObject.AddComponent<PlayerInputSampler>();
            _sampler.cameraRig = _cam;
            _views = new HazardViewDriver(_map);
            // G4.4: stars, the fountain burst and the other reactive views on the local hazard clock (stars are not collectable here).
            if (_map.starFields.Length > 0)
                gameObject.AddComponent<ReactiveViews>().PracticeSource = () => (_hz, _tick + _renderAlpha);
            Debug.Log($"[practice] DEV practice mode on {_map.gameObject.scene.name} (F6 panel, F7 print JSON, R respawn)");
        }

        private void Respawn()
        {
            _s = new MotorState { Position = _spawn, Yaw = _spawnYaw };
            _prev = _s;
            _respawnIn = 0;
        }

        private void Rebuild()
        {
            _motor = new KinematicMotor(MovementConfig.FromTuning(_tuning, 0));
        }

        private void Update()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb != null)
            {
                if (kb.f6Key.wasPressedThisFrame)
                    _panel = !_panel;
                if (kb.f7Key.wasPressedThisFrame)
                    PrintJson();
                if (kb.rKey.wasPressedThisFrame)
                    Respawn();
                if (kb.escapeKey.wasPressedThisFrame)
                    Cursor.lockState = CursorLockMode.None;
                if (_panel)
                {
                    if (kb.pageUpKey.wasPressedThisFrame)
                        _sel = (_sel + Fields.Length - 1) % Fields.Length;
                    if (kb.pageDownKey.wasPressedThisFrame)
                        _sel = (_sel + 1) % Fields.Length;
                    if (kb.equalsKey.wasPressedThisFrame || kb.numpadPlusKey.wasPressedThisFrame)
                        Change(_sel, Steps[_sel]);
                    if (kb.minusKey.wasPressedThisFrame || kb.numpadMinusKey.wasPressedThisFrame)
                        Change(_sel, -Steps[_sel]);
                }
            }

            if (mouse != null && mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked)
                Cursor.lockState = CursorLockMode.Locked;

            // 30 Hz fixed ticks, rendered with interpolation.
            _acc += Time.deltaTime;
            var dt = _motor.Cfg.Dt;
            var n = 0;
            while (_acc >= dt && n++ < 5)
            {
                _acc -= dt;
                Tick();
            }

            var a = (float)(_acc / dt);
            _renderAlpha = a;
            var feet = Vector3.Lerp(V(_prev.Position), V(_s.Position), a);
            if (!_s.Has(MotorState.Respawning))
                _vis.Drive(feet, V(_s.Velocity), _s.Flags, _s.Yaw, _s.StunTicks, Time.deltaTime);
            _camTarget.position = feet + Vector3.up * 0.8f;
            _views.Update(_hz, _tick + a - _hz.RoundStartTick, GameSettings.ReducedMotion);
        }

        private void Tick()
        {
            _tick++;
            _prev = _s;
            if (_s.Has(MotorState.Respawning))
            {
                if (++_respawnIn >= _motor.Cfg.RespawnTicks)
                    Respawn();
                return;
            }

            var cmd = new InputCmd { Seq = _tick, Tick = _tick };
            _sampler.Sample(ref cmd);
            _world.BeginTick(_tick);
            var r = PlayerStep.Run(_motor, ref _s, in cmd, _tick, _world, _hz);
            if ((r & (PlayerStep.Result.FellOut | PlayerStep.Result.Gooed)) != 0)
                _respawnIn = 0;
            if ((r & PlayerStep.Result.HazardHit) != 0)
                _cam.Shake(0.12f);
            if (_motor.Last.Dived)
                _cam.DiveKick();

            // Readouts: last airborne arc (height above take-off, airtime, horizontal distance).
            var g = _s.Has(MotorState.Grounded);
            var pos = V(_s.Position);
            if (_wasGrounded && !g)
            {
                _airStartY = pos.y;
                _airStartPos = pos;
                _apex = 0f;
                _airTime = 0f;
            }

            if (!g)
            {
                _apex = Mathf.Max(_apex, pos.y - _airStartY);
                _airTime += _motor.Cfg.Dt;
            }

            if (!_wasGrounded && g)
            {
                var d = pos - _airStartPos;
                d.y = 0f;
                _airDist = d.magnitude;
                _lastJump = $"apex {_apex:F2} m · air {_airTime:F2} s · dist {_airDist:F2} m{(_s.Has(MotorState.Stumbling) ? " · STUMBLE" : "")}{(_s.Has(MotorState.DiveLanding) ? " · SLIDE" : "")}";
            }

            _wasGrounded = g;
        }

        private ref int Field(int i)
        {
            switch (i)
            {
                case 0: return ref _tuning.MaxSpeed;
                case 1: return ref _tuning.GroundAccel;
                case 2: return ref _tuning.GroundDecel;
                case 3: return ref _tuning.AirControlPermille;
                case 4: return ref _tuning.Gravity;
                case 5: return ref _tuning.FallMultiplierPermille;
                case 6: return ref _tuning.JumpApex;
                case 7: return ref _tuning.DiveHorizontal;
                case 8: return ref _tuning.DiveVertical;
                case 9: return ref _tuning.DiveLandMs;
                case 10: return ref _tuning.SlideDecel;
                case 11: return ref _tuning.StumbleMinFall;
                case 12: return ref _tuning.StumbleMs;
                default: return ref _tuning.StumbleSpeedPermille;
            }
        }

        private void Change(int i, int delta)
        {
            ref var f = ref Field(i);
            f = Mathf.Max(0, f + delta);
            Rebuild();
        }

        private void PrintJson()
        {
            var json = _tuning.ToJson();
            GUIUtility.systemCopyBuffer = json;
            Debug.Log($"[practice] movement tuning ({(_tuning.IsGenerated ? "unchanged" : "EDITED")}):\n{json}");
        }

        private void OnGUI()
        {
            if (!_panel)
            {
                GUI.Label(new Rect(10, 10, 600, 22), "PRACTICE (dev) · F6 tuning panel · F7 print JSON · R respawn");
                return;
            }

            GUILayout.BeginArea(new Rect(10, 10, 430, 520), GUI.skin.box);
            GUILayout.Label("<b>PRACTICE / TUNING (dev only)</b>  F6 hide · F7 print JSON · R respawn");
            GUILayout.Label("PgUp/PgDn select · +/− change (or the buttons) · values in movement_v2.json units");
            for (var i = 0; i < Fields.Length; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label((i == _sel ? "▶ " : "   ") + Fields[i], GUILayout.Width(250));
                GUILayout.Label(Field(i).ToString(), GUILayout.Width(60));
                if (GUILayout.Button("−", GUILayout.Width(28)))
                    Change(i, -Steps[i]);
                if (GUILayout.Button("+", GUILayout.Width(28)))
                    Change(i, Steps[i]);
                GUILayout.EndHorizontal();
            }

            var h = new Vector2(_s.VelX, _s.VelZ).magnitude * 0.01f;
            GUILayout.Label($"speed {h:F2} m/s · last arc: {_lastJump}");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Print JSON (F7)"))
                PrintJson();
            if (GUILayout.Button("Reset to config"))
            {
                _tuning = MovementTuning.Generated;
                Rebuild();
            }

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private static Vector3 V(Vec3 v) => new Vector3(v.X, v.Y, v.Z);
    }
}
