using Unity.Cinemachine;
using Unity.Cinemachine.TargetTracking;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FallRush.Client
{
    /// <summary>
    /// Third-person orbit camera on Cinemachine 3.1.7 (doc 17 §4): G1: about 6.1 m behind and 2.5 m above the player (closer for
    /// the slower v2 run; × the Settings camera distance), mouse orbit (pointer locked), soft collision
    /// (<see cref="CinemachineDecollider"/> pushes the camera out of geometry, <see cref="CinemachineDeoccluder"/> pulls it in
    /// front of anything between it and the player; Gameplay layer), look-ahead along
    /// velocity (<see cref="CinemachineRotationComposer.Lookahead"/>), +6° FOV kick on dive and a small shake on hazard hits —
    /// both off with <see cref="GameSettings.ReducedMotion"/>.
    /// <para>Built at runtime: a <see cref="CinemachineBrain"/> on the main camera and one <see cref="CinemachineCamera"/>
    /// with <see cref="CinemachineOrbitalFollow"/> (Sphere, WorldSpace binding: its horizontal axis is the world yaw we
    /// rotate the move input by) + <see cref="CinemachineRotationComposer"/> + Deoccluder + <see cref="CinemachineCameraOffset"/> (shake).</para>
    /// </summary>
    public sealed class FollowCamera : MonoBehaviour
    {
        // G4.10: the rig's numbers live in CameraRig (classic = these G1 values; close = the default).
        public const float Distance = 6.6f;         // classic. G1 (v2 run 5 m/s): √(6.1² + 2.5²); M2–M4: 7.6 m
        public const float Elevation = 22.5f;       // classic: atan(2.5 / 6.1)
        public const float BaseFov = 55f;
        public const float DiveFovKick = 6f;

        private CinemachineCamera _vcam;
        private CameraRig.Preset _rig = CameraRig.Classic;
        private CinemachineOrbitalFollow _orbit;
        private CinemachineCameraOffset _offset;
        private float _kick;
        private float _shake;
        private float _shakeT;

        /// <summary>Camera yaw in degrees (the orbit's horizontal axis, world space).</summary>
        public float Yaw => _orbit != null ? _orbit.HorizontalAxis.Value : 0f;

        public Transform Target { get; private set; }

        /// <summary>P7: the live gameplay camera (UI juice asks it for small punches; visual only).</summary>
        public static FollowCamera Active { get; private set; }

        /// <summary>P7 A1.5: a small camera punch from UI moments (eliminated, qualified, Chaos Drop, crown); reduced motion: none.</summary>
        public static void Punch(float amplitude)
        {
            if (Active != null && Active.isActiveAndEnabled)
                Active.Shake(amplitude);
        }

        public static FollowCamera Create(Camera main, Transform target, int gameplayMask)
        {
            if (main.GetComponent<CinemachineBrain>() == null)
                main.gameObject.AddComponent<CinemachineBrain>();

            var go = new GameObject("FollowCam");
            var fc = go.AddComponent<FollowCamera>();
            fc.Target = target;
            Active = fc;

            var vcam = go.AddComponent<CinemachineCamera>();
            vcam.Target = new CameraTarget { TrackingTarget = target, LookAtTarget = target, CustomLookAtTarget = false };
            var rig = CameraRig.Active;
            fc._rig = rig;
            var lens = LensSettings.Default;
            lens.FieldOfView = rig.Fov;
            lens.NearClipPlane = 0.1f;
            lens.FarClipPlane = 400f;
            vcam.Lens = lens;

            var orbit = go.AddComponent<CinemachineOrbitalFollow>();
            orbit.OrbitStyle = CinemachineOrbitalFollow.OrbitStyles.Sphere;
            orbit.Radius = rig.Distance;
            var ts = TrackerSettings.Default;
            ts.BindingMode = BindingMode.WorldSpace;
            ts.PositionDamping = new Vector3(0.12f, 0.3f, 0.12f);
            orbit.TrackerSettings = ts;
            orbit.HorizontalAxis = new InputAxis { Value = 0f, Range = new Vector2(-180f, 180f), Wrap = true, Center = 0f };
            orbit.VerticalAxis = new InputAxis { Value = rig.Elevation, Range = new Vector2(-10f, 65f), Wrap = false, Center = rig.Elevation };
            orbit.RadialAxis = new InputAxis { Value = 1f, Range = new Vector2(1f, 1f), Wrap = false, Center = 1f };

            var composer = go.AddComponent<CinemachineRotationComposer>();
            composer.TargetOffset = new Vector3(0f, rig.LookOffsetY, 0f);
            composer.Damping = new Vector2(0.15f, 0.25f);
            composer.Lookahead = new LookaheadSettings { Enabled = true, Time = 0.2f, Smoothing = 8f, IgnoreY = true };

            // G1 §3.5.4: Goo Tower's laps above and the core behind put the camera inside geometry. The decollider
            // pushes it out of anything it overlaps first; the deoccluder then clears the line to the player.
            var decol = go.AddComponent<CinemachineDecollider>();
            decol.CameraRadius = 0.35f;
            decol.Decollision = new CinemachineDecollider.DecollisionSettings
            {
                Enabled = true,
                ObstacleLayers = gameplayMask,
                Damping = 0.4f,
                SmoothingTime = 0.1f,
            };

            var deocc = go.AddComponent<CinemachineDeoccluder>();
            deocc.CollideAgainst = gameplayMask;
            deocc.MinimumDistanceFromTarget = 0.8f;
            deocc.AvoidObstacles = new CinemachineDeoccluder.ObstacleAvoidance
            {
                Enabled = true,
                DistanceLimit = 0f,
                MinimumOcclusionTime = 0f,
                CameraRadius = 0.35f,
                Strategy = CinemachineDeoccluder.ObstacleAvoidance.ResolutionStrategy.PullCameraForward,
                MaximumEffort = 4,
                SmoothingTime = 0.15f,
                Damping = 0.5f,
                DampingWhenOccluded = 0.1f,
            };

            fc._offset = go.AddComponent<CinemachineCameraOffset>();
            fc._offset.ApplyAfter = CinemachineCore.Stage.Aim;
            fc._vcam = vcam;
            fc._orbit = orbit;
            return fc;
        }

        /// <summary>Starts the +6° FOV kick (ignored with reduced motion).</summary>
        /// <summary>Modifier FOV change (TinyTilt −8°, visual only).</summary>
        public float FovOffset;

        public void DiveKick()
        {
            if (!GameSettings.ReducedMotion)
                _kick = 1f;
        }

        /// <summary>Short camera shake (hazard hit); ignored with reduced motion.</summary>
        public void Shake(float amplitude)
        {
            if (!GameSettings.ReducedMotion)
            {
                _shake = Mathf.Max(_shake, amplitude);
                _shakeT = 0f;
            }
        }

        private void Update()
        {
            var mouse = Mouse.current;
            // P7-UX: a spectator (free cursor) orbits by holding the right button over the game
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (mouse != null && _orbit != null && CursorPolicy.Orbit(Cursor.lockState == CursorLockMode.Locked, CursorPolicy.Spectating && !CursorPolicy.AnyPanelOpen,
                    mouse.rightButton.isPressed, es != null && es.IsPointerOverGameObject()))
            {
                var d = mouse.delta.ReadValue();
                var sens = GameSettings.Sensitivity;
                var h = _orbit.HorizontalAxis;
                h.Value = Mathf.Repeat(h.Value + d.x * sens + 180f, 360f) - 180f;
                _orbit.HorizontalAxis = h;
                var v = _orbit.VerticalAxis;
                v.Value = Mathf.Clamp(v.Value + (GameSettings.InvertY ? d.y : -d.y) * sens, v.Range.x, v.Range.y);
                _orbit.VerticalAxis = v;
            }

            var dt = Time.unscaledDeltaTime;
            _kick = Mathf.MoveTowards(_kick, 0f, dt / 0.6f);
            if (GameSettings.ReducedMotion)
            {
                _kick = 0f;
                _shake = 0f;
            }

            var lens = _vcam.Lens;
            lens.FieldOfView = _rig.Fov + FovOffset + GameSettings.FovOffset + DiveFovKick * Mathf.SmoothStep(0f, 1f, _kick);
            _vcam.Lens = lens;
            _orbit.Radius = _rig.Distance * GameSettings.CameraDistance;

            if (_shake > 0f)
            {
                _shakeT += dt;
                var decay = Mathf.Clamp01(1f - _shakeT / 0.25f);
                var a = _shake * decay;
                _offset.Offset = new Vector3(Mathf.Sin(_shakeT * 71f) * a, Mathf.Sin(_shakeT * 53f + 1.3f) * a, 0f);
                if (decay <= 0f)
                {
                    _shake = 0f;
                    _offset.Offset = Vector3.zero;
                }
            }
        }

        /// <summary>Instant reposition after a teleport (respawn) so the camera does not swoop across the map.</summary>
        public void Warp(Vector3 delta)
        {
            if (Target != null)
                CinemachineCore.OnTargetObjectWarped(Target, delta);
        }
    }
}
