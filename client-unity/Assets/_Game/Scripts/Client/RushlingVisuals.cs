using FallRush.Shared;
using UnityEngine;
using UnityEngine.Rendering;

namespace FallRush.Client
{
    /// <summary>
    /// Rushling v1 "Bolt" (doc 17 §2), built from code-generated shapes and animated procedurally. Client only: the
    /// FallRush.Client assembly is excluded from server builds (<c>!UNITY_SERVER</c>), so none of this exists there.
    /// <para>Driven once per frame by <see cref="Drive"/> from the predicted (local) or interpolated (remote) state. The
    /// animation loop allocates nothing: buffers, property blocks and meshes are created in <see cref="Build"/>.</para>
    /// <list type="bullet">
    /// <item>Body: white dome (lid on a back hinge) + seam ring + coloured shell with an emblem; screen face from a 4×4
    /// glyph atlas (UV offset per expression, blink); aviator goggles; wind-up key (spins with speed, ×3 diving); peg feet
    /// with two-point stepping.</item>
    /// <item>Noodle arms: 6-point verlet chains rendered as Catmull-Rom tubes (8 segments × 6 sides), trailing, flailing
    /// in the air and reaching forward on a dive. Visual only.</item>
    /// <item>Motion: landing squash (1.15, 0.8) / jump stretch (0.9, 1.12) on a spring (~0.18 s), lean toward acceleration
    /// (≤ 18°, critically damped), wobble spring on impacts, stun pop (lid opens 25°, stars orbit 0.6 s), dive pitch 80°.</item>
    /// </list>
    /// Collision fairness: everything stays inside the 0.31 × 1.0 capsule except the arms (visual only, doc 17 §2.1).
    /// </summary>
    public sealed class RushlingVisuals : MonoBehaviour
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int BaseMapStId = Shader.PropertyToID("_BaseMap_ST");

        /// <summary>Per-slot cosmetic colour presets (bottom shell, emblem). No gameplay effect. Preset 0 = Bolt.</summary>
        private static readonly int[] ShellColors = { 0xFF3EA5, 0x3FA9F5, 0xFFD21F, 0x5BE3B5, 0x7B2FF7, 0xFF8A3D, 0x2EC4B6, 0xE84855 };
        private static readonly int[] EmblemColors = { 0xFFD21F, 0xFFD21F, 0xFF3EA5, 0xFFD21F, 0xFFD21F, 0xFFFFFF, 0xFFD21F, 0xFFD21F };
        private static readonly Color DomeWhite = Color.white;
        private static readonly Color SeamColor = UiKit.Hex(0x2A1250);
        private static readonly Color KeyPurple = UiKit.Hex(0x7B2FF7);
        private static readonly Color GoggleStrap = UiKit.Hex(0x5A3A1E);
        private static readonly Color GoggleRim = UiKit.Hex(0xFFD21F);
        private static readonly Color GoggleLens = UiKit.Hex(0x3FA9F5);
        private static readonly Color FootColor = UiKit.Hex(0x2A1250);
        private static readonly Color StarColor = UiKit.Hex(0xFFD21F);

        private const int ArmPoints = 6;
        private const float ArmSegment = 0.1f;
        private const int TubeSegments = 8;
        private const int TubeSides = 6;
        private const float TubeRadius = 0.035f;

        public bool Silhouette { get; private set; }

        private Transform _tilt, _squash, _lid, _key, _faceT, _starsRoot;
        private readonly Transform[] _feet = new Transform[2];
        private readonly Transform[] _stars = new Transform[3];
        private readonly Transform[] _mitts = new Transform[2];
        private Renderer[] _renderers;
        private Material[] _baseMats;
        private Renderer _face;
        private MaterialPropertyBlock _mpb;
        private Material _lit, _unlit;

        // arms
        private readonly Vector3[,] _arm = new Vector3[2, ArmPoints];
        private readonly Vector3[,] _armPrev = new Vector3[2, ArmPoints];
        private readonly Mesh[] _armMesh = new Mesh[2];
        private readonly Vector3[][] _armVerts = new Vector3[2][];
        private readonly Vector3[][] _armNormals = new Vector3[2][];
        private readonly Vector3[] _spline = new Vector3[TubeSegments + 1];
        private bool _armsInit;

        // feet
        private readonly Vector3[] _footPlant = new Vector3[2];
        private readonly Vector3[] _footFrom = new Vector3[2];
        private readonly float[] _footT = { 1f, 1f };
        private bool _feetInit;

        // springs / state
        private float _sq, _sqV;               // squash: +stretch / −squash (y-scale delta)
        private Vector2 _lean, _leanV;         // degrees about (x = pitch fwd, y = roll)
        private Vector2 _wob, _wobV;
        private float _dive, _diveV;           // 0..1 dive pose
        private float _lidA, _lidV;            // degrees open
        private float _keyAngle;
        private float _yaw;
        private Vector3 _lastVel;
        private Vector3 _accel;
        private bool _wasGrounded = true;
        private int _lastStun;
        private float _starsLeft;
        private float _blinkAt, _blinkUntil;
        private ProcMesh.Glyph _glyph = (ProcMesh.Glyph)255;
        private float _time;
        private bool _hasPose;

        /// <summary>Fired when a stun starts (for camera shake on the local player).</summary>
        public bool StunStartedThisFrame { get; private set; }
        public bool DiveStartedThisFrame { get; private set; }

        public void Build(int slot, Material lit, Material unlit)
        {
            _lit = lit;
            _unlit = unlit;
            _mpb = new MaterialPropertyBlock();
            var preset = slot % ShellColors.Length;
            var shell = UiKit.Hex(ShellColors[preset]);
            var emblem = UiKit.Hex(EmblemColors[preset]);
            var phase = slot * 0.37f;
            _blinkAt = 1.5f + phase;

            _tilt = Node("Tilt", transform, new Vector3(0f, 0.5f, 0f));
            _squash = Node("Squash", _tilt, new Vector3(0f, -0.5f, 0f));

            Part("Shell", _squash, ProcMesh.Shell, Vector3.zero, Quaternion.identity, Vector3.one, shell);
            Part("Seam", _squash, ProcMesh.ThinTorus, new Vector3(0f, ProcMesh.SeamY, 0f), Quaternion.identity,
                new Vector3(ProcMesh.R + 0.002f, 0.3f, ProcMesh.R + 0.002f), SeamColor);
            Part("Emblem", _squash, ProcMesh.Bolt, new Vector3(0f, 0.45f, ProcMesh.R + 0.012f), Quaternion.identity, Vector3.one * 0.8f, emblem);
            Part("Hinge", _squash, ProcMesh.Cube, new Vector3(0f, ProcMesh.SeamY, -ProcMesh.R - 0.01f), Quaternion.identity,
                new Vector3(0.1f, 0.035f, 0.03f), SeamColor);

            // Lid: everything above the seam, hinged at the back so the stun pop opens it.
            _lid = Node("Lid", _squash, new Vector3(0f, ProcMesh.SeamY, -ProcMesh.R));
            var lidOff = new Vector3(0f, -ProcMesh.SeamY, ProcMesh.R);
            Part("Dome", _lid, ProcMesh.Dome, lidOff, Quaternion.identity, Vector3.one, DomeWhite);
            _face = Part("Face", _lid, ProcMesh.Face, lidOff, Quaternion.identity, Vector3.one, Color.white, unlitMat: true);
            _faceT = _face.transform;

            // Aviator goggles pushed up on the forehead: strap ring + two lenses.
            var strapY = 0.84f;
            var strapR = ProcMesh.RadiusAt(strapY) + 0.006f;
            Part("GoggleStrap", _lid, ProcMesh.ThinTorus, lidOff + new Vector3(0f, strapY, 0f), Quaternion.Euler(-12f, 0f, 0f),
                new Vector3(strapR, 0.2f, strapR), GoggleStrap);
            for (var side = -1; side <= 1; side += 2)
            {
                var ly = 0.9f;
                var lz = ProcMesh.RadiusAt(ly) * 0.86f;
                var lp = lidOff + new Vector3(side * 0.085f, ly, lz);
                var lr = Quaternion.Euler(-50f, side * 18f, 0f) * Quaternion.Euler(90f, 0f, 0f);
                Part("GoggleRim", _lid, ProcMesh.Torus, lp, lr, new Vector3(0.055f, 0.15f, 0.055f), GoggleRim);
                Part("GoggleLens", _lid, ProcMesh.Cylinder, lp, lr, new Vector3(0.1f, 0.008f, 0.1f), GoggleLens);
            }

            // Wind-up key on the back: shaft along −Z, two loops in the XY plane; spins about the shaft.
            _key = Node("Key", _squash, new Vector3(0f, 0.5f, -ProcMesh.R - 0.005f));
            Part("KeyShaft", _key, ProcMesh.Cylinder, new Vector3(0f, 0f, -0.05f), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.04f, 0.05f, 0.04f), KeyPurple);
            for (var side = -1; side <= 1; side += 2)
                Part("KeyLoop", _key, ProcMesh.Torus, new Vector3(side * 0.085f, 0f, -0.11f), Quaternion.Euler(90f, 0f, 0f),
                    new Vector3(0.075f, 0.2f, 0.06f), KeyPurple);

            // Peg feet: children of the root (world-planted while stepping), not of the squash node.
            for (var f = 0; f < 2; f++)
            {
                var foot = Node(f == 0 ? "FootL" : "FootR", transform, Vector3.zero);
                Part("Peg", foot, ProcMesh.Cylinder, new Vector3(0f, 0.05f, 0f), Quaternion.identity, new Vector3(0.1f, 0.05f, 0.12f), FootColor);
                _feet[f] = foot;
            }

            // Stun stars (hidden until a stun starts).
            _starsRoot = Node("Stars", _squash, new Vector3(0f, 1.18f, 0f));
            for (var i = 0; i < _stars.Length; i++)
            {
                var st = Part("Star", _starsRoot, ProcMesh.Star, Vector3.zero, Quaternion.identity, Vector3.one, StarColor, unlitMat: true).transform;
                _stars[i] = st;
            }

            _starsRoot.gameObject.SetActive(false);

            // Noodle arms: world-space tube meshes (their own GameObjects at the origin so vertices are world positions).
            for (var a = 0; a < 2; a++)
            {
                var go = new GameObject(a == 0 ? "ArmL" : "ArmR");
                go.transform.SetParent(transform.parent, false);
                go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var mesh = new Mesh { name = "NoodleArm" };
                mesh.MarkDynamic();
                var rings = TubeSegments + 1;
                _armVerts[a] = new Vector3[rings * TubeSides];
                _armNormals[a] = new Vector3[rings * TubeSides];
                mesh.vertices = _armVerts[a];
                mesh.normals = _armNormals[a];
                mesh.triangles = TubeTriangles(rings);
                _armMesh[a] = mesh;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                Setup(mr, DomeWhite, false);
                var mitt = Part("Mitten", go.transform, ProcMesh.Sphere, Vector3.zero, Quaternion.identity, Vector3.one * 0.1f, DomeWhite).transform;
                _mitts[a] = mitt;
                _armGo[a] = go;
            }

            _renderers = GetComponentsInChildren<Renderer>(true);
            var armR0 = _armGo[0].GetComponentsInChildren<Renderer>(true);
            var armR1 = _armGo[1].GetComponentsInChildren<Renderer>(true);
            var all = new Renderer[_renderers.Length + armR0.Length + armR1.Length];
            _renderers.CopyTo(all, 0);
            armR0.CopyTo(all, _renderers.Length);
            armR1.CopyTo(all, _renderers.Length + armR0.Length);
            _renderers = all;
            _baseMats = new Material[_renderers.Length];
            for (var i = 0; i < _renderers.Length; i++)
                _baseMats[i] = _renderers[i].sharedMaterial;
            SetGlyph(ProcMesh.Glyph.Idle);
        }

        private readonly GameObject[] _armGo = new GameObject[2];

        // One material per (colour, lit/unlit), shared by every Rushling: keeps the SRP Batcher working (no per-renderer
        // property blocks except on the screen face, which needs its own atlas offset).
        private static readonly System.Collections.Generic.Dictionary<long, Material> MatCache = new System.Collections.Generic.Dictionary<long, Material>();

        private Material MatFor(Color c, bool unlit)
        {
            var c32 = (Color32)c;
            var key = ((long)c32.r << 24) | ((long)c32.g << 16) | ((long)c32.b << 8) | c32.a | (unlit ? 1L << 40 : 0L);
            if (MatCache.TryGetValue(key, out var m) && m != null)
                return m;
            m = new Material(unlit ? _unlit : _lit) { name = $"Rushling_{(unlit ? "U" : "L")}_{ColorUtility.ToHtmlStringRGB(c)}" };
            m.SetColor(BaseColorId, c);
            MatCache[key] = m;
            return m;
        }

        public void SetVisible(bool on)
        {
            if (gameObject.activeSelf != on)
                gameObject.SetActive(on);
            for (var a = 0; a < 2; a++)
                if (_armGo[a].activeSelf != on)
                    _armGo[a].SetActive(on);
            if (!on)
            {
                _armsInit = false;
                _feetInit = false;
                _hasPose = false;
            }
        }

        private void OnDestroy()
        {
            for (var a = 0; a < 2; a++)
            {
                if (_armGo[a] != null)
                    Destroy(_armGo[a]);
                if (_armMesh[a] != null)
                    Destroy(_armMesh[a]);
            }
        }

        /// <summary>Unlit black override for the silhouette test (doc 17 §2.1).</summary>
        public void SetSilhouette(bool on)
        {
            Silhouette = on;
            for (var i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r == _face)
                {
                    r.sharedMaterial = _unlit;
                    ApplyFace();
                    continue;
                }

                r.sharedMaterial = on ? MatFor(Color.black, true) : _baseMats[i];
            }
        }

        // ------------------------------------------------------------------ per frame

        /// <summary>Poses the Rushling for this frame.</summary>
        /// <param name="feet">Capsule bottom (world).</param>
        /// <param name="vel">Velocity (m/s).</param>
        public void Drive(Vector3 feet, Vector3 vel, byte flags, byte yaw256, byte stun, float dt)
        {
            StunStartedThisFrame = DiveStartedThisFrame = false;
            if (dt <= 0f)
                return;
            dt = Mathf.Min(dt, 1f / 20f);
            _time += dt;

            var grounded = (flags & MotorState.Grounded) != 0;
            var diving = (flags & MotorState.Diving) != 0;
            var diveLanding = (flags & MotorState.DiveLanding) != 0;
            var targetYaw = yaw256 * (360f / 256f);
            if (!_hasPose)
            {
                _yaw = targetYaw;
                _lastVel = vel;
                _wasGrounded = grounded;
                _hasPose = true;
            }

            // Yaw arrives in 30 Hz steps (1.4° each); ease it so the body turns smoothly.
            _yaw = Mathf.LerpAngle(_yaw, targetYaw, 1f - Mathf.Exp(-dt * 20f));
            transform.SetPositionAndRotation(feet, Quaternion.Euler(0f, _yaw, 0f));

            // ---- events ----
            if (grounded && !_wasGrounded)
            {
                var impact = Mathf.Clamp01(-_lastVel.y / 12f);
                _sq = -0.2f * Mathf.Lerp(0.5f, 1f, impact);
                _sqV = 0f;
                _wobV += new Vector2(18f + 40f * impact, 0f);
            }

            if (!grounded && _wasGrounded && vel.y > 4f && !diving)
            {
                _sq = 0.12f;
                _sqV = 0f;
            }

            if (diving && _dive < 0.05f && _diveV <= 0.01f)
                DiveStartedThisFrame = true;

            if (stun > 0 && _lastStun == 0)
            {
                StunStartedThisFrame = true;
                _lidV += 600f;
                _starsLeft = 0.6f;
                var away = transform.InverseTransformDirection(vel);
                _wobV += new Vector2(Mathf.Clamp(away.z, -1f, 1f) * -120f, Mathf.Clamp(away.x, -1f, 1f) * 120f);
            }

            _lastStun = stun;
            _wasGrounded = grounded;

            // ---- acceleration (smoothed) for lean ----
            var rawA = (vel - _lastVel) / dt;
            _lastVel = vel;
            rawA.y = 0f;
            _accel = Vector3.Lerp(_accel, rawA, 1f - Mathf.Exp(-dt * 8f));
            var aLocal = transform.InverseTransformDirection(_accel);
            var leanTarget = grounded && !diving ? new Vector2(aLocal.z, -aLocal.x) * 0.6f : Vector2.zero;
            if (leanTarget.sqrMagnitude > 18f * 18f)
                leanTarget = leanTarget.normalized * 18f;
            Spring2(ref _lean, ref _leanV, leanTarget, 14f, 1f, dt);

            // ---- squash / wobble / dive / lid springs ----
            Spring(ref _sq, ref _sqV, 0f, 26f, 0.55f, dt);
            Spring2(ref _wob, ref _wobV, Vector2.zero, 16f, 0.22f, dt);
            Spring(ref _dive, ref _diveV, diving ? 1f : diveLanding ? 0.75f : 0f, 16f, 1f, dt);
            if (_starsLeft > 0f)
                _starsLeft -= dt;
            var lidTarget = _starsLeft > 0f ? 25f : 0f;
            Spring(ref _lidA, ref _lidV, lidTarget, 22f, 0.6f, dt);

            var pitch = _lean.x + _wob.x + _dive * 80f;
            var roll = _lean.y + _wob.y;
            _tilt.localRotation = Quaternion.Euler(pitch, 0f, roll);
            var sy = 1f + _sq;
            var sxz = 1f - 0.75f * _sq;
            _squash.localScale = new Vector3(sxz, sy, sxz);
            _lid.localRotation = Quaternion.Euler(-Mathf.Max(0f, _lidA), 0f, 0f);

            // ---- key ----
            var speed = new Vector2(vel.x, vel.z).magnitude;
            _keyAngle += (60f + speed * 110f) * (diving ? 3f : 1f) * dt;
            _key.localRotation = Quaternion.Euler(0f, 0f, _keyAngle);

            // ---- stars ----
            var starsOn = _starsLeft > 0f || stun > 0;
            if (_starsRoot.gameObject.activeSelf != starsOn)
                _starsRoot.gameObject.SetActive(starsOn);
            if (starsOn)
            {
                for (var i = 0; i < _stars.Length; i++)
                {
                    var a = _time * 7f + i * (Mathf.PI * 2f / _stars.Length);
                    _stars[i].localPosition = new Vector3(Mathf.Cos(a) * 0.26f, Mathf.Sin(a * 2f) * 0.03f, Mathf.Sin(a) * 0.26f);
                    _stars[i].localRotation = Quaternion.Euler(0f, -a * Mathf.Rad2Deg, 0f);
                }
            }

            // ---- face ----
            ProcMesh.Glyph g;
            if (stun > 0 || _starsLeft > 0f)
                g = ProcMesh.Glyph.Stunned;
            else if (diving || diveLanding)
                g = ProcMesh.Glyph.Diving;
            else if (!grounded && vel.y < -7f)
                g = ProcMesh.Glyph.Falling;
            else if (!grounded)
                g = ProcMesh.Glyph.Jumping;
            else if (speed > 1f)
                g = ProcMesh.Glyph.Running;
            else
                g = ProcMesh.Glyph.Idle;
            if ((g == ProcMesh.Glyph.Idle || g == ProcMesh.Glyph.Running) && _time >= _blinkAt)
            {
                _blinkUntil = _time + 0.12f;
                _blinkAt = _time + 2.6f + Mathf.Repeat(_time * 1.7f, 2f);
            }

            if (_time < _blinkUntil && (g == ProcMesh.Glyph.Idle || g == ProcMesh.Glyph.Running))
                g = ProcMesh.Glyph.Blink;
            SetGlyph(g);

            UpdateFeet(feet, vel, grounded, diving, dt);
            UpdateArms(vel, grounded, diving, dt);
        }

        // ------------------------------------------------------------------ feet

        private void UpdateFeet(Vector3 feet, Vector3 vel, bool grounded, bool diving, float dt)
        {
            const float stride = 0.2f;
            const float stepTime = 0.09f;
            var rot = transform.rotation;
            for (var f = 0; f < 2; f++)
            {
                var side = f == 0 ? -0.12f : 0.12f;
                var rest = feet + rot * new Vector3(side, 0f, 0f);
                if (!_feetInit)
                {
                    _footPlant[f] = _footFrom[f] = rest;
                    _footT[f] = 1f;
                }

                if (!grounded || diving)
                {
                    // Tucked under the body in the air.
                    _footPlant[f] = _footFrom[f] = rest;
                    _footT[f] = 1f;
                    _feet[f].SetPositionAndRotation(rest + Vector3.up * 0.02f, rot);
                    continue;
                }

                var desired = rest + new Vector3(vel.x, 0f, vel.z) * 0.045f;
                var other = 1 - f;
                if (_footT[f] >= 1f && _footT[other] >= 1f && (desired - _footPlant[f]).sqrMagnitude > stride * stride)
                {
                    _footFrom[f] = _footPlant[f];
                    _footPlant[f] = desired;
                    _footT[f] = 0f;
                }

                // Planted feet never drift far from the capsule (e.g. on a moving platform).
                if ((_footPlant[f] - rest).sqrMagnitude > 0.3f * 0.3f)
                {
                    _footPlant[f] = _footFrom[f] = desired;
                    _footT[f] = 1f;
                }

                Vector3 p;
                if (_footT[f] < 1f)
                {
                    _footT[f] = Mathf.Min(1f, _footT[f] + dt / stepTime);
                    var t = _footT[f];
                    p = Vector3.Lerp(_footFrom[f], _footPlant[f], t) + Vector3.up * (Mathf.Sin(t * Mathf.PI) * 0.06f);
                }
                else
                {
                    p = _footPlant[f];
                }

                p.y = Mathf.Max(p.y, feet.y);
                _feet[f].SetPositionAndRotation(p, rot);
            }

            _feetInit = true;
        }

        // ------------------------------------------------------------------ arms

        private void UpdateArms(Vector3 vel, bool grounded, bool diving, float dt)
        {
            var body = _squash;
            var fwd = transform.forward;
            var up = Vector3.up;
            for (var a = 0; a < 2; a++)
            {
                var side = a == 0 ? -1f : 1f;
                var shoulder = body.TransformPoint(new Vector3(side * (ProcMesh.R - 0.01f), 0.5f, 0.02f));

                // Pose the chain wants: hanging out and down; flail in the air; straight ahead on a dive.
                Vector3 dir;
                if (diving)
                    dir = (fwd * 1f + up * 0.05f + transform.right * side * 0.12f).normalized;
                else if (!grounded)
                {
                    var f = Mathf.Sin(_time * 13f + a * 2.1f);
                    dir = (transform.right * side * 0.7f + up * (0.35f + 0.6f * f) + fwd * 0.25f * Mathf.Cos(_time * 9f + a)).normalized;
                }
                else
                    dir = (transform.right * side * 0.42f - up * 0.9f + fwd * 0.08f).normalized;

                if (!_armsInit)
                {
                    for (var i = 0; i < ArmPoints; i++)
                        _arm[a, i] = _armPrev[a, i] = shoulder + dir * (i * ArmSegment);
                }

                // Verlet integration with damping, gravity and a spring toward the pose.
                var k = diving ? 900f : grounded ? 140f : 260f;
                var dt2 = dt * dt;
                for (var i = 1; i < ArmPoints; i++)
                {
                    var p = _arm[a, i];
                    var target = shoulder + dir * (i * ArmSegment);
                    var accel = (target - p) * k + Vector3.down * 6f;
                    var next = p + (p - _armPrev[a, i]) * 0.86f + accel * dt2;
                    _armPrev[a, i] = p;
                    _arm[a, i] = next;
                }

                _arm[a, 0] = _armPrev[a, 0] = shoulder;
                for (var it = 0; it < 3; it++)
                {
                    for (var i = 1; i < ArmPoints; i++)
                    {
                        var d = _arm[a, i] - _arm[a, i - 1];
                        var l = d.magnitude;
                        if (l < 1e-5f)
                            continue;
                        var corr = d * ((l - ArmSegment) / l);
                        if (i == 1)
                            _arm[a, i] -= corr;
                        else
                        {
                            _arm[a, i] -= corr * 0.5f;
                            _arm[a, i - 1] += corr * 0.5f;
                        }
                    }

                    _arm[a, 0] = shoulder;
                }

                // Keep the noodles above the floor the Rushling stands on.
                var floor = transform.position.y + 0.02f;
                for (var i = 1; i < ArmPoints; i++)
                    if (_arm[a, i].y < floor)
                        _arm[a, i].y = floor;

                BuildTube(a);
                _mitts[a].position = _arm[a, ArmPoints - 1];
            }

            _armsInit = true;
        }

        private void BuildTube(int a)
        {
            // Catmull-Rom through the 6 chain points → 9 samples.
            for (var s = 0; s <= TubeSegments; s++)
            {
                var u = s / (float)TubeSegments * (ArmPoints - 1);
                var i = Mathf.Min((int)u, ArmPoints - 2);
                var t = u - i;
                var p0 = _arm[a, Mathf.Max(0, i - 1)];
                var p1 = _arm[a, i];
                var p2 = _arm[a, i + 1];
                var p3 = _arm[a, Mathf.Min(ArmPoints - 1, i + 2)];
                var t2 = t * t;
                var t3 = t2 * t;
                _spline[s] = 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
            }

            var verts = _armVerts[a];
            var normals = _armNormals[a];
            var reference = Vector3.up;
            var min = _spline[0];
            var max = _spline[0];
            for (var s = 0; s <= TubeSegments; s++)
            {
                var tangent = _spline[Mathf.Min(TubeSegments, s + 1)] - _spline[Mathf.Max(0, s - 1)];
                if (tangent.sqrMagnitude < 1e-8f)
                    tangent = Vector3.down;
                tangent.Normalize();
                var n = Vector3.Cross(tangent, reference);
                if (n.sqrMagnitude < 1e-4f)
                    n = Vector3.Cross(tangent, Vector3.right);
                n.Normalize();
                var b = Vector3.Cross(tangent, n);
                reference = b; // parallel-transport-ish: carry the frame along
                var radius = TubeRadius * (s == 0 ? 0.9f : 1f);
                for (var k = 0; k < TubeSides; k++)
                {
                    var ang = k * (Mathf.PI * 2f / TubeSides);
                    var dir = n * Mathf.Cos(ang) + b * Mathf.Sin(ang);
                    var v = _spline[s] + dir * radius;
                    verts[s * TubeSides + k] = v;
                    normals[s * TubeSides + k] = dir;
                }

                min = Vector3.Min(min, _spline[s]);
                max = Vector3.Max(max, _spline[s]);
            }

            var mesh = _armMesh[a];
            mesh.vertices = verts;   // assigning the same reused array: no managed allocation
            mesh.normals = normals;
            mesh.bounds = new Bounds((min + max) * 0.5f, max - min + Vector3.one * 0.2f);
        }

        private static int[] TubeTriangles(int rings)
        {
            var tris = new int[(rings - 1) * TubeSides * 6];
            var t = 0;
            for (var s = 0; s < rings - 1; s++)
            {
                for (var k = 0; k < TubeSides; k++)
                {
                    var a = s * TubeSides + k;
                    var b = s * TubeSides + (k + 1) % TubeSides;
                    var c = a + TubeSides;
                    var d = b + TubeSides;
                    tris[t++] = a; tris[t++] = c; tris[t++] = b;
                    tris[t++] = b; tris[t++] = c; tris[t++] = d;
                }
            }

            return tris;
        }

        // ------------------------------------------------------------------ helpers

        private void SetGlyph(ProcMesh.Glyph g)
        {
            if (g == _glyph)
                return;
            _glyph = g;
            ApplyFace();
        }

        private void ApplyFace()
        {
            if (_face == null || _mpb == null)
                return;
            _mpb.Clear();
            _mpb.SetTexture(BaseMapId, ProcMesh.FaceAtlas);
            _mpb.SetVector(BaseMapStId, ProcMesh.GlyphST(_glyph == (ProcMesh.Glyph)255 ? ProcMesh.Glyph.Idle : _glyph));
            _mpb.SetColor(BaseColorId, Silhouette ? Color.black : Color.white);
            _face.SetPropertyBlock(_mpb);
        }

        private static void Spring(ref float x, ref float v, float target, float omega, float zeta, float dt)
        {
            // Semi-implicit Euler, sub-stepped for stiff springs.
            const int steps = 2;
            var h = dt / steps;
            for (var i = 0; i < steps; i++)
            {
                var a = -omega * omega * (x - target) - 2f * zeta * omega * v;
                v += a * h;
                x += v * h;
            }
        }

        private static void Spring2(ref Vector2 x, ref Vector2 v, Vector2 target, float omega, float zeta, float dt)
        {
            Spring(ref x.x, ref v.x, target.x, omega, zeta, dt);
            Spring(ref x.y, ref v.y, target.y, omega, zeta, dt);
        }

        private static Transform Node(string name, Transform parent, Vector3 localPos)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = localPos;
            return t;
        }

        private Renderer Part(string name, Transform parent, Mesh mesh, Vector3 pos, Quaternion rot, Vector3 scale, Color color, bool unlitMat = false)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot;
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            Setup(r, color, unlitMat);
            return r;
        }

        private void Setup(Renderer r, Color color, bool unlitMat)
        {
            r.sharedMaterial = MatFor(color, unlitMat);
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }
    }
}
