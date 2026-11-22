using System;
using System.IO;
using System.Text;
using FallRush.Client;
using FallRush.Net;
using FallRush.Shared;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FallRush.FeelLab
{
    /// <summary>
    /// P1-N Feel Lab (dev only, offline): the same Rushling visuals, camera, audio and local hazards as the practice mode,
    /// with the simulation engine switchable live for the OC-3 A/B.
    /// <list type="bullet">
    /// <item><b>F5</b> engine: Classic (<see cref="KinematicMotor"/> + <see cref="PhysxMotorWorld"/>, 30 Hz) ↔ rush-core
    /// (<c>rush_core.bundle</c> on the exported map blob, interpolated for rendering). The player's state carries over.</item>
    /// <item><b>F8</b> rush-core tick rate 15 / 20 / 30 Hz. <b>F10</b> overlay. <b>F4</b> respawn at the map start.
    /// <b>F2</b> next map. <b>B</b> rush-core bots 0 / 5 / 11.</item>
    /// </list>
    /// Hazards use seed 1, variant A, no modifier, hazard time 0 at the start, on both engines. <c>-autotest</c> runs a
    /// scripted jump / dive / run under both engines, logs the overlay, saves one screenshot per engine and quits (or
    /// moves to the next map with <c>-map all</c>).
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class FeelLabMode : MonoBehaviour
    {
        private static bool _rushCore = true;
        private static int _rate = 20;
        private static int _bots;
        private static bool _overlay = true;
        private static int _autoMapIndex;

        private MapInfo _map;
        private string _key;
        private PhysxMotorWorld _world;
        private HazardSet _hz;
        private KinematicMotor _motor;
        private MotorState _s, _prev;
        private uint _tick;
        private int _respawnIn;
        private double _acc;
        private Vec3 _spawn;
        private byte _spawnYaw;

        private byte[] _blob;
        private int _h = -1;
        private readonly RushCoreNative.Player[] _rp = new RushCoreNative.Player[12];
        private readonly RushCoreNative.Player[] _rpPrev = new RushCoreNative.Player[12];
        private readonly byte[] _inputs = new byte[72];
        private readonly byte[] _buf = new byte[36];
        private ulong _rcTick;

        private RushlingVisuals _vis;
        private readonly RushlingVisuals[] _botVis = new RushlingVisuals[12];
        private FollowCamera _cam;
        private Transform _camTarget;
        private PlayerInputSampler _sampler;
        private HazardViewDriver _views;
        private string _error;

        // overlay readouts
        private float _apex, _airStartY, _groundY, _lastApex, _frameMs;
        private bool _wasGrounded = true;

        // autotest
        private bool _auto;
        private float _autoT;
        private int _autoPhase;
        private readonly StringBuilder _autoLog = new StringBuilder();

        private void Start()
        {
            foreach (var pm in FindObjectsByType<PracticeMode>(FindObjectsSortMode.None))
                DestroyImmediate(pm.gameObject); // the Feel Lab replaces the practice component (before its Start runs)
            _map = MapInfo.Current;
            if (_map == null)
            {
                _error = "no MapInfo";
                enabled = false;
                return;
            }

            _key = _map.gameObject.scene.name.Replace("Map_", "");
            _auto = FeelLabBoot.HasArg("-autotest");
            var cfg = MovementConfig.Default;
            _motor = new KinematicMotor(cfg);
            var p = new RoundParams { MapId = _map.MapId, HazardSeed = 1, Variant = 0, Modifier = 0, RoundStartTick = 0 };
            _world = _map.CreateWorld(transform, cfg, in p, out _hz);
            _spawn = _map.SpawnPoints()[0];
            _spawnYaw = _map.SpawnYaws()[0];
            _s = new MotorState { Position = _spawn, Yaw = _spawnYaw };
            _prev = _s;

            var asset = Resources.Load<TextAsset>("feellab/" + _key);
            if (asset == null)
                _error = $"no map blob Resources/feellab/{_key}.bytes (run scripts/p1-build-mac.sh)";
            else
                _blob = asset.bytes;
            try
            {
                var v = RushCoreNative.rc_version();
                Debug.Log($"[feellab] rush_core version 0x{v:x}");
            }
            catch (Exception e)
            {
                _error = "rush_core.bundle not loaded: " + e.Message;
                _rushCore = false;
            }

            if (_blob != null && _error == null)
                InitRushCore();
            else
                _rushCore = false;

            var root = new GameObject("FeelLabRushling").transform;
            root.SetParent(transform, false);
            _vis = root.gameObject.AddComponent<RushlingVisuals>();
            _vis.Build(0, _map.playerMaterial, _map.unlitMaterial);
            _vis.SetVisible(true);
            for (var i = 1; i < 12; i++)
            {
                var b = new GameObject($"FeelLabBot_{i:00}").transform;
                b.SetParent(transform, false);
                _botVis[i] = b.gameObject.AddComponent<RushlingVisuals>();
                _botVis[i].Build(i, _map.playerMaterial, _map.unlitMaterial);
                _botVis[i].SetVisible(false);
            }

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
            if (_map.starFields.Length > 0)
                gameObject.AddComponent<ReactiveViews>().PracticeSource = () => (_hz, HazardTime());
            Debug.Log($"[feellab] {_key}: engine {(_rushCore ? "rush-core" : "Classic")} rate {_rate} bots {_bots}{(_error != null ? " — " + _error : "")}");
        }

        private void InitRushCore()
        {
            if (_h >= 0)
                RushCoreNative.rc_free(_h);
            uint active = 1, bots = 0;
            for (var i = 1; i <= _bots && i < 12; i++)
            {
                active |= 1u << i;
                bots |= 1u << i;
            }

            _h = RushCoreNative.rc_init(_blob, (uint)_blob.Length, RushCoreNative.Cfg(_rate, active, bots, 1));
            if (_h < 0)
            {
                _error = "rc_init failed (blob / rate)";
                _rushCore = false;
                return;
            }

            ReadRush();
            Array.Copy(_rp, _rpPrev, 12);
        }

        private void OnDestroy()
        {
            if (_h >= 0)
                RushCoreNative.rc_free(_h);
        }

        private double HazardTime() => _rushCore ? (_rcTick + Alpha()) * 30.0 / _rate : _tick + Alpha();

        private float Alpha() => (float)(_acc / Dt());

        private double Dt() => _rushCore ? 1.0 / _rate : _motor.Cfg.Dt;

        // ------------------------------------------------------------------ keys

        private void Update()
        {
            var t0 = Time.realtimeSinceStartupAsDouble;
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.f5Key.wasPressedThisFrame)
                    SwitchEngine();
                if (kb.f8Key.wasPressedThisFrame && _h >= 0)
                {
                    _rate = _rate == 15 ? 20 : _rate == 20 ? 30 : 15;
                    RushCoreNative.rc_set_rate(_h, (uint)_rate);
                    _acc = 0;
                }

                if (kb.f10Key.wasPressedThisFrame)
                    _overlay = !_overlay;
                if (kb.f4Key.wasPressedThisFrame)
                    Respawn();
                if (kb.f2Key.wasPressedThisFrame)
                    FeelLabBoot.Enter(FeelLabBoot.Maps[(Array.IndexOf(FeelLabBoot.Maps, _key) + 1) % FeelLabBoot.Maps.Length]);
                if (kb.bKey.wasPressedThisFrame && _blob != null)
                {
                    _bots = _bots == 0 ? 5 : _bots == 5 ? 11 : 0;
                    var me = _rp[0];
                    InitRushCore();
                    RushCoreNative.rc_set_player(_h, 0, RushCoreNative.Encode(in me));
                }

                if (kb.escapeKey.wasPressedThisFrame)
                    Cursor.lockState = CursorLockMode.None;
            }

            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked)
                Cursor.lockState = CursorLockMode.Locked;
            if (_auto)
                AutoUpdate();

            _acc += Time.deltaTime;
            var dt = Dt();
            var n = 0;
            while (_acc >= dt && n++ < 6)
            {
                _acc -= dt;
                if (_rushCore)
                    TickRush();
                else
                    TickClassic();
            }

            Render();
            _frameMs = (float)((Time.realtimeSinceStartupAsDouble - t0) * 1000.0);
        }

        private void SwitchEngine()
        {
            if (_h < 0)
                return;
            if (_rushCore)
            {
                // rush-core → Classic: mm/s → cm/s
                var p = _rp[0];
                _s = new MotorState
                {
                    PosX = p.X, PosY = p.Y, PosZ = p.Z, VelX = (short)(p.VX / 10), VelY = (short)(p.VY / 10), VelZ = (short)(p.VZ / 10),
                    Yaw = p.Yaw, Flags = (byte)(p.Flags & 0x1F), StunTicks = p.Stun, CoyoteTicks = p.Coyote, JumpBufferTicks = p.Buffer,
                    DiveCooldownTicks = p.DiveCd, LandTicks = p.Land, ImmunityTicks = p.Immunity, Surface = p.Surface,
                };
                _prev = _s;
                _tick = (uint)(_rcTick * 30 / (ulong)_rate); // keep hazard time continuous
            }
            else
            {
                var p = new RushCoreNative.Player
                {
                    X = _s.PosX, Y = _s.PosY, Z = _s.PosZ, VX = _s.VelX * 10, VY = _s.VelY * 10, VZ = _s.VelZ * 10, Yaw = _s.Yaw, Flags = _s.Flags,
                    Stun = _s.StunTicks, Coyote = _s.CoyoteTicks, Buffer = _s.JumpBufferTicks, DiveCd = _s.DiveCooldownTicks, Land = _s.LandTicks,
                    Immunity = _s.ImmunityTicks, Surface = _s.Surface,
                };
                RushCoreNative.rc_set_player(_h, 0, RushCoreNative.Encode(in p));
                ReadRush();
                Array.Copy(_rp, _rpPrev, 12);
            }

            _rushCore = !_rushCore;
            _acc = 0;
            Debug.Log($"[feellab] engine → {(_rushCore ? "rush-core " + _rate + " Hz" : "Classic 30 Hz")}");
        }

        private void Respawn()
        {
            _s = new MotorState { Position = _spawn, Yaw = _spawnYaw };
            _prev = _s;
            _respawnIn = 0;
            if (_h >= 0)
            {
                RushCoreNative.rc_respawn(_h, 0);
                ReadRush();
                Array.Copy(_rp, _rpPrev, 12);
            }
        }

        // ------------------------------------------------------------------ engines

        private InputCmd Sample(uint tick)
        {
            var cmd = new InputCmd { Seq = tick, Tick = tick };
            if (_auto)
                AutoInput(ref cmd);
            else
                _sampler.Sample(ref cmd);
            return cmd;
        }

        private void TickClassic()
        {
            _tick++;
            _prev = _s;
            if (_s.Has(MotorState.Respawning))
            {
                if (++_respawnIn >= _motor.Cfg.RespawnTicks)
                {
                    _s = new MotorState { Position = _spawn, Yaw = _spawnYaw };
                    _respawnIn = 0;
                }

                return;
            }

            var cmd = Sample(_tick);
            _world.BeginTick(_tick);
            var r = PlayerStep.Run(_motor, ref _s, in cmd, _tick, _world, _hz);
            if ((r & PlayerStep.Result.HazardHit) != 0)
                _cam.Shake(0.12f);
            if (_motor.Last.Dived)
                _cam.DiveKick();
            Readouts(_s.Has(MotorState.Grounded), _s.PosY);
        }

        private void TickRush()
        {
            Array.Copy(_rp, _rpPrev, 12);
            var cmd = Sample((uint)_rcTick + 1);
            Array.Clear(_inputs, 0, _inputs.Length);
            RushCoreNative.PutInput(_inputs, 0, cmd.MoveX, cmd.MoveY, cmd.Buttons, cmd.CamYaw);
            RushCoreNative.rc_step(_h, _inputs);
            var wasStun = _rp[0].Stun;
            ReadRush();
            if (_rp[0].Stun > wasStun)
                _cam.Shake(0.12f);
            if ((_rp[0].Flags & MotorState.Diving) != 0 && (_rpPrev[0].Flags & MotorState.Diving) == 0)
                _cam.DiveKick();
            Readouts((_rp[0].Flags & MotorState.Grounded) != 0, _rp[0].Y);
        }

        private void ReadRush()
        {
            for (var i = 0; i < 12; i++)
            {
                RushCoreNative.rc_player(_h, (uint)i, _buf);
                _rp[i] = RushCoreNative.Decode(_buf);
            }

            _rcTick = RushCoreNative.rc_clock(_h) & 0xFFFFFFFF;
        }

        private void Readouts(bool grounded, int yMm)
        {
            var y = yMm * 0.001f;
            if (grounded)
                _groundY = y;
            // Apex from the last grounded height (not the first airborne sample, which is already one tick up and would
            // read lower at lower tick rates).
            if (_wasGrounded && !grounded)
            {
                _airStartY = _groundY;
                _apex = 0f;
            }

            if (!grounded)
                _apex = Mathf.Max(_apex, y - _airStartY);
            if (!_wasGrounded && grounded)
                _lastApex = _apex;
            _wasGrounded = grounded;
        }

        // ------------------------------------------------------------------ render

        private void Render()
        {
            var a = Alpha();
            Vector3 feet;
            if (_rushCore)
            {
                var p0 = _rpPrev[0];
                var p1 = _rp[0];
                feet = Vector3.Lerp(Mm(p0.X, p0.Y, p0.Z), Mm(p1.X, p1.Y, p1.Z), a);
                if ((p1.Flags & MotorState.Respawning) == 0)
                    _vis.Drive(feet, Mm(p1.VX, p1.VY, p1.VZ), p1.Flags, p1.Yaw, p1.Stun, Time.deltaTime);
                for (var i = 1; i < 12; i++)
                {
                    var on = (_rp[i].SlotFlags & 1) != 0 && (_rp[i].Flags & MotorState.Respawning) == 0;
                    _botVis[i].SetVisible(on);
                    if (on)
                        _botVis[i].Drive(Vector3.Lerp(Mm(_rpPrev[i].X, _rpPrev[i].Y, _rpPrev[i].Z), Mm(_rp[i].X, _rp[i].Y, _rp[i].Z), a),
                            Mm(_rp[i].VX, _rp[i].VY, _rp[i].VZ), _rp[i].Flags, _rp[i].Yaw, _rp[i].Stun, Time.deltaTime);
                }
            }
            else
            {
                feet = Vector3.Lerp(V(_prev.Position), V(_s.Position), a);
                if (!_s.Has(MotorState.Respawning))
                    _vis.Drive(feet, V(_s.Velocity), _s.Flags, _s.Yaw, _s.StunTicks, Time.deltaTime);
                for (var i = 1; i < 12; i++)
                    _botVis[i].SetVisible(false);
            }

            _camTarget.position = feet + Vector3.up * 0.8f;
            _views.Update(_hz, HazardTime(), GameSettings.ReducedMotion);
        }

        private string OverlayText()
        {
            float vx, vz;
            bool g;
            if (_rushCore)
            {
                vx = _rp[0].VX * 0.001f;
                vz = _rp[0].VZ * 0.001f;
                g = (_rp[0].Flags & MotorState.Grounded) != 0;
            }
            else
            {
                vx = _s.VelX * 0.01f;
                vz = _s.VelZ * 0.01f;
                g = _s.Has(MotorState.Grounded);
            }

            var eng = _rushCore ? $"rush-core {_rate} Hz" : "Classic 30 Hz";
            return $"FEEL LAB · {_key} · engine {eng} · speed {Mathf.Sqrt(vx * vx + vz * vz):F2} m/s · grounded {(g ? "yes" : "no")} · last apex {_lastApex:F2} m · frame {_frameMs:F2} ms · bots {(_rushCore ? _bots : 0)}";
        }

        private void OnGUI()
        {
            if (_error != null)
                GUI.Label(new Rect(10, 34, 900, 22), "<color=red>" + _error + "</color>");
            if (!_overlay)
                return;
            GUI.Box(new Rect(8, 8, 980, 26), GUIContent.none);
            GUI.Label(new Rect(14, 11, 970, 22), OverlayText());
            GUI.Label(new Rect(14, Screen.height - 26, 980, 22), "F5 engine · F8 rate 15/20/30 · F4 respawn · F10 overlay · F2 next map · B bots 0/5/11");
        }

        private static Vector3 Mm(int x, int y, int z) => new Vector3(x * 0.001f, y * 0.001f, z * 0.001f);

        private static Vector3 V(Vec3 v) => new Vector3(v.X, v.Y, v.Z);

        // ------------------------------------------------------------------ autotest (-autotest)

        private void AutoInput(ref InputCmd cmd)
        {
            var t = _autoT;
            cmd.MoveY = t < 3.0f ? (sbyte)127 : (sbyte)0;
            if (t > 1.0f && t < 1.05f)
                cmd.Buttons |= InputCmd.JumpPressed | InputCmd.JumpHeld;
            if (t > 2.0f && t < 2.05f)
                cmd.Buttons |= InputCmd.DivePressed | InputCmd.DiveHeld;
        }

        private void AutoUpdate()
        {
            _autoT += Time.deltaTime;
            if (_autoT >= 0.5f * (_autoLog.Length == 0 ? 1 : 1) && Mathf.FloorToInt(_autoT) != Mathf.FloorToInt(_autoT - Time.deltaTime))
                _autoLog.Append($"[feellab-auto] t={_autoT:F1}s {OverlayText()}\n");
            if (_autoT < 4.0f)
                return;
            var outDir = FeelLabBoot.GetArg("-autotestOut") ?? Path.Combine(Application.persistentDataPath, "feellab");
            Directory.CreateDirectory(outDir);
            var file = Path.Combine(outDir, $"{_key}-{(_rushCore ? "rushcore" : "classic")}.png");
            ScreenCapture.CaptureScreenshot(file); // written at the end of this frame: change nothing until it is
            _autoLog.Append($"[feellab-auto] screenshot {file}\n");
            Debug.Log(_autoLog.ToString());
            File.AppendAllText(Path.Combine(outDir, "autotest.log"), _autoLog.ToString());
            _autoLog.Clear();
            _autoT = 0f;
            _autoPhase++;
            _auto = false; // pause the script; AutoAfterShot resumes it once the screenshot frame has rendered
            Invoke(nameof(AutoAfterShot), 0.5f);
        }

        private void AutoAfterShot()
        {
            if (_autoPhase == 1)
            {
                // second pass with the other engine from the spawn
                SwitchEngine();
                Respawn();
                _auto = true;
                return;
            }

            Invoke(nameof(AutoNext), 0.5f); // let the screenshot write
        }

        private void AutoNext()
        {
            if (FeelLabBoot.GetArg("-map") == "all" && ++_autoMapIndex < FeelLabBoot.Maps.Length)
            {
                _rushCore = true;
                FeelLabBoot.Enter(FeelLabBoot.Maps[_autoMapIndex]);
                return;
            }

            Application.Quit(0);
        }
    }
}
