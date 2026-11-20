using Unity.Cinemachine;
using Unity.Cinemachine.TargetTracking;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FallRush.Client
{
    /// <summary>
    /// Third-person orbit camera on Cinemachine 3.1.7 (doc 17 §4): about 7 m behind and 3 m above the player, mouse orbit
    /// (pointer locked), soft collision (<see cref="CinemachineDeoccluder"/> against the Gameplay layer), look-ahead along
    /// velocity (<see cref="CinemachineRotationComposer.Lookahead"/>), +6° FOV kick on dive and a small shake on hazard hits —
    /// both off with <see cref="GameSettings.ReducedMotion"/>.
    /// <para>Built at runtime: a <see cref="CinemachineBrain"/> on the main camera and one <see cref="CinemachineCamera"/>
    /// with <see cref="CinemachineOrbitalFollow"/> (Sphere, WorldSpace binding: its horizontal axis is the world yaw we
    /// rotate the move input by) + <see cref="CinemachineRotationComposer"/> + Deoccluder + <see cref="CinemachineCameraOffset"/> (shake).</para>
    /// </summary>
    public sealed class FollowCamera : MonoBehaviour
    {
        public const float Distance = 7.6f;         // √(7² + 3²)
        public const float Elevation = 23.2f;       // atan(3 / 7)
        public const float BaseFov = 55f;
        public const float DiveFovKick = 6f;

        private CinemachineCamera _vcam;
        private CinemachineOrbitalFollow _orbit;
        private CinemachineCameraOffset _offset;
        private float _kick;
        private float _shake;
        private float _shakeT;

        /// <summary>Camera yaw in degrees (the orbit's horizontal axis, world space).</summary>
        public float Yaw => _orbit != null ? _orbit.HorizontalAxis.Value : 0f;

        public Transform Target { get; private set; }

        public static FollowCamera Create(Camera main, Transform target, int gameplayMask)
        {
            if (main.GetComponent<CinemachineBrain>() == null)
                main.gameObject.AddComponent<CinemachineBrain>();

            var go = new GameObject("FollowCam");
            var fc = go.AddComponent<FollowCamera>();
            fc.Target = target;

            var vcam = go.AddComponent<CinemachineCamera>();
            vcam.Target = new CameraTarget { TrackingTarget = target, LookAtTarget = target, CustomLookAtTarget = false };
            var lens = LensSettings.Default;
            lens.FieldOfView = BaseFov;
            lens.NearClipPlane = 0.1f;
            lens.FarClipPlane = 400f;
            vcam.Lens = lens;

            var orbit = go.AddComponent<CinemachineOrbitalFollow>();
            orbit.OrbitStyle = CinemachineOrbitalFollow.OrbitStyles.Sphere;
            orbit.Radius = Distance;
            var ts = TrackerSettings.Default;
            ts.BindingMode = BindingMode.WorldSpace;
            ts.PositionDamping = new Vector3(0.12f, 0.3f, 0.12f);
            orbit.TrackerSettings = ts;
            orbit.HorizontalAxis = new InputAxis { Value = 0f, Range = new Vector2(-180f, 180f), Wrap = true, Center = 0f };
            orbit.VerticalAxis = new InputAxis { Value = Elevation, Range = new Vector2(-10f, 65f), Wrap = false, Center = Elevation };
            orbit.RadialAxis = new InputAxis { Value = 1f, Range = new Vector2(1f, 1f), Wrap = false, Center = 1f };

            var composer = go.AddComponent<CinemachineRotationComposer>();
            composer.TargetOffset = new Vector3(0f, 0.4f, 0f);
            composer.Damping = new Vector2(0.15f, 0.25f);
            composer.Lookahead = new LookaheadSettings { Enabled = true, Time = 0.2f, Smoothing = 8f, IgnoreY = true };

            var deocc = go.AddComponent<CinemachineDeoccluder>();
            deocc.CollideAgainst = gameplayMask;
            deocc.MinimumDistanceFromTarget = 0.8f;
            deocc.AvoidObstacles = new CinemachineDeoccluder.ObstacleAvoidance
            {
                Enabled = true,
                DistanceLimit = 0f,
                MinimumOcclusionTime = 0f,
                CameraRadius = 0.3f,
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
            if (mouse != null && Cursor.lockState == CursorLockMode.Locked && _orbit != null)
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
            lens.FieldOfView = BaseFov + FovOffset + DiveFovKick * Mathf.SmoothStep(0f, 1f, _kick);
            _vcam.Lens = lens;

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
