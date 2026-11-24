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
    /// (≤ 18°, critically damped), wobble spring on impacts, stun pop (lid opens 25°, dizzy swirls orbit 0.6 s), dive pitch 80°.</item>
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
        private const float TubeRadius = 0.045f;

        public bool Silhouette { get; private set; }

        private Transform _tilt, _squash, _starsRoot;
        private readonly Transform[] _feet = new Transform[2];
        private MaterialPropertyBlock _mpb;
        private Material _lit, _unlit;

        // arms
        private readonly Vector3[,] _arm = new Vector3[2, ArmPoints];
        private readonly Vector3[,] _armPrev = new Vector3[2, ArmPoints];
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

        // G3 juice hooks (client only): set for one frame by Drive.
        public bool LandedThisFrame { get; private set; }
        public float LandImpact { get; private set; }          // 0..1
        public bool StumbleStartedThisFrame { get; private set; }
        public bool GetUpThisFrame { get; private set; }
        public bool EmoteStartedThisFrame { get; private set; }
        public bool StepThisFrame { get; private set; }
        public bool JumpedThisFrame { get; private set; }
        public bool Sliding { get; private set; }
        private bool _emoteStarted;

        // G2.2: three renderers (body, lid + LED screen, limbs) sharing one toon material with the outline pass; the key
        // spin and the face glyph are per-renderer property-block values the shader applies (RushlingParts).
        private static readonly int KeyAngleId = Shader.PropertyToID("_KeyAngle");
        private static readonly int KeyPivotId = Shader.PropertyToID("_KeyPivot");
        private static readonly int FaceMapId = Shader.PropertyToID("_FaceMap");
        private static readonly int FaceStId = Shader.PropertyToID("_FaceST");
        private static readonly int FaceOnId = Shader.PropertyToID("_FaceOn");
        private static readonly int FaceTintId = Shader.PropertyToID("_FaceTint");
        private static readonly int UnlitId = Shader.PropertyToID("_Unlit");
        private static readonly int OutlineWidthId = Shader.PropertyToID("_OutlineWidth");
        private const float LimbOutline = 0.006f; // the noodles are 4.5 cm thick: a thin hull keeps them white
        private MeshRenderer _bodyR, _limbsR, _starsR;
        private MeshFilter _bodyF;
        private MaterialPropertyBlock _bodyMpb, _plainMpb;
        private static readonly int LidAngleId = Shader.PropertyToID("_LidAngle");
        private static readonly int LidPivotId = Shader.PropertyToID("_LidPivot");
        private int _preset = -1;
        private Color _faceTint = Color.white;

        /// <summary>The cosmetic preset currently shown (G2.2 "My Rushling").</summary>
        public int Preset => _preset;

        public void Build(int slot, Material lit, Material unlit) => Build(slot, CosmeticPresets.ForBot(slot), lit, unlit);

        public void Build(int slot, int preset, Material lit, Material unlit)
        {
            _lit = lit;
            _unlit = unlit;
            _mpb = new MaterialPropertyBlock();
            _bodyMpb = new MaterialPropertyBlock();
            _plainMpb = new MaterialPropertyBlock();
            var phase = slot * 0.37f;
            _blinkAt = 1.5f + phase;

            _tilt = Node("Tilt", transform, new Vector3(0f, 0.5f, 0f));
            _squash = Node("Squash", _tilt, new Vector3(0f, -0.5f, 0f));
            _bodyR = Renderer("Body", _squash, out _bodyF);
            // The lid (dome, screen, topper) is part of the body mesh; the shader hinges it at the back of the seam.

            // Peg feet: empty nodes under the root (world-planted while stepping); their pegs live in the limbs mesh.
            for (var f = 0; f < 2; f++)
                _feet[f] = Node(f == 0 ? "FootL" : "FootR", transform, Vector3.zero);

            // Dizzy stars (2026-11-24; were the G4.8z swirls): one mesh, hidden until a stun starts; the ring spins and bobs.
            // The node keeps the name "Swirls" (Editor shots and tests look it up).
            _starsRoot = Node("Swirls", _squash, new Vector3(0f, 1.2f, 0f));
            _starsR = Renderer("DizzyStars", _starsRoot, out var sf);
            sf.sharedMesh = RushlingParts.DizzyStars();
            _starsR.shadowCastingMode = ShadowCastingMode.Off;
            StarsBright();
            _starsRoot.gameObject.SetActive(false);

            // Noodle arms + mittens + peg feet: one world-space dynamic mesh (its own GameObject at the origin).
            BuildLimbs();
            SetPreset(preset);
            SetGlyph(ProcMesh.Glyph.Idle);
        }

        private MeshRenderer Renderer(string name, Transform parent, out MeshFilter mf)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            mf = go.AddComponent<MeshFilter>();
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = _lit != null ? _lit : MatFor(Color.white, false);
            r.shadowCastingMode = ShadowCastingMode.On; // G4.8: Rushlings cast the soft look's sun shadow (the blob stays: it reads the landing spot)
            r.receiveShadows = true;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return r;
        }

        /// <summary>Shows cosmetic preset <paramref name="preset"/> (shell, emblem, topper, LED colour, key). Cheap: cached meshes.</summary>
        public void SetPreset(int preset)
        {
            preset = CosmeticPresets.Clamp(preset);
            if (preset == _preset)
                return;
            _preset = preset;
            _bodyF.sharedMesh = RushlingParts.Body(preset);
            _faceTint = RushlingParts.FaceTint(CosmeticPresets.Get(preset).Face);
            ApplyBody();
        }

        // ---- limbs mesh layout: [arm0][arm1][mitt0][mitt1][foot0][foot1]
        private Mesh _limbMesh;
        private Vector3[] _lv, _ln;
        private Vector3[] _mittV, _mittN, _footV, _footN;
        private int _mittOff0, _footOff0;
        private const float MittRadius = 0.052f;

        // G2.4 outline LOD: the hull pass only on Rushlings near the camera; the thin limbs never get one.
        private static Material s_NoHull;
        private Material _hullMat;
        private bool _outline = true;

        private Material NoHull()
        {
            if (s_NoHull != null)
                return s_NoHull;
            var src = _lit != null ? _lit : MatFor(Color.white, false);
            s_NoHull = new Material(src) { name = src.name + " (no outline)" };
            s_NoHull.SetShaderPassEnabled("SRPDefaultUnlit", false);
            return s_NoHull;
        }

        /// <summary>Turns the outline hull on / off (the map view does it by camera distance: draw calls).</summary>
        public void SetOutline(bool on)
        {
            if (on == _outline || _bodyR == null)
                return;
            _outline = on;
            _hullMat ??= _bodyR.sharedMaterial;
            _bodyR.sharedMaterial = on ? _hullMat : NoHull();
        }

        private void BuildLimbs()
        {
            var go = new GameObject("Limbs");
            go.transform.SetParent(transform.parent, false);
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var mitt = RushlingParts.Shapes.Sphere(RushlingParts.MittSeg, RushlingParts.MittRings);
            var foot = ProcMeshCylinder8();
            _mittV = mitt.vertices;
            _mittN = mitt.normals;
            _footV = foot.vertices;
            _footN = foot.normals;
            var rings = TubeSegments + 1;
            var armN = rings * TubeSides;
            _mittOff0 = 2 * armN;
            _footOff0 = _mittOff0 + 2 * _mittV.Length;
            var total = _footOff0 + 2 * _footV.Length;
            _lv = new Vector3[total];
            _ln = new Vector3[total];
            var cols = new Color[total];
            for (var i = 0; i < total; i++)
                cols[i] = i < _footOff0 ? DomeWhite : FootColor;
            var tris = new System.Collections.Generic.List<int>();
            var tube = TubeTriangles(rings);
            for (var a = 0; a < 2; a++)
                foreach (var t in tube)
                    tris.Add(a * armN + t);
            var mt = mitt.triangles;
            for (var a = 0; a < 2; a++)
                foreach (var t in mt)
                    tris.Add(_mittOff0 + a * _mittV.Length + t);
            var ft = foot.triangles;
            for (var a = 0; a < 2; a++)
                foreach (var t in ft)
                    tris.Add(_footOff0 + a * _footV.Length + t);
            _limbMesh = new Mesh { name = "Limbs" };
            _limbMesh.MarkDynamic();
            _limbMesh.vertices = _lv;
            _limbMesh.normals = _ln;
            _limbMesh.colors = cols;
            _limbMesh.SetTriangles(tris, 0);
            go.AddComponent<MeshFilter>().sharedMesh = _limbMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = NoHull();
            mr.shadowCastingMode = ShadowCastingMode.On;
            mr.receiveShadows = true;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            _limbsR = mr;
            _plainMpb.SetFloat(OutlineWidthId, LimbOutline);
            mr.SetPropertyBlock(_plainMpb);
            _armGo[0] = go;
        }

        private static Mesh ProcMeshCylinder8() => RushlingParts.Shapes.Cylinder8;

        private void WriteMitt(int a, Vector3 c)
        {
            var o = _mittOff0 + a * _mittV.Length;
            var d = MittRadius * 2f;
            for (var i = 0; i < _mittV.Length; i++)
            {
                _lv[o + i] = c + _mittV[i] * d;
                _ln[o + i] = _mittN[i];
            }
        }

        private void WriteFoot(int f)
        {
            var o = _footOff0 + f * _footV.Length;
            var t = _feet[f];
            var pos = t.position;
            var rot = t.rotation;
            var scale = new Vector3(0.1f, 0.05f, 0.12f);
            for (var i = 0; i < _footV.Length; i++)
            {
                var v = _footV[i];
                _lv[o + i] = pos + rot * new Vector3(v.x * scale.x, v.y * scale.y + 0.05f, v.z * scale.z);
                _ln[o + i] = rot * _footN[i];
            }
        }

        private void UploadLimbs()
        {
            _limbMesh.vertices = _lv; // the same reused arrays: no managed allocation
            _limbMesh.normals = _ln;
            var min = _lv[0];
            var max = _lv[0];
            for (var i = 1; i < _lv.Length; i += 7)
            {
                min = Vector3.Min(min, _lv[i]);
                max = Vector3.Max(max, _lv[i]);
            }

            _limbMesh.bounds = new Bounds((min + max) * 0.5f, max - min + Vector3.one * 0.3f);
        }

        private readonly GameObject[] _armGo = new GameObject[1];

        // Fallback material when a map has none (tools): one per colour, shared.
        private static readonly System.Collections.Generic.Dictionary<long, Material> MatCache = new System.Collections.Generic.Dictionary<long, Material>();

        private Material MatFor(Color c, bool unlit)
        {
            var c32 = (Color32)c;
            var key = ((long)c32.r << 24) | ((long)c32.g << 16) | ((long)c32.b << 8) | c32.a | (unlit ? 1L << 40 : 0L);
            if (MatCache.TryGetValue(key, out var m) && m != null)
                return m;
            var sh = Shader.Find("FallRush/Toon");
            m = sh != null ? new Material(sh) : new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            m.name = $"Rushling_{(unlit ? "U" : "L")}_{ColorUtility.ToHtmlStringRGB(c)}";
            m.SetColor(BaseColorId, c);
            if (unlit)
                m.SetFloat(UnlitId, 1f);
            m.SetFloat("_RushlingMesh", 1f);
            MatCache[key] = m;
            return m;
        }

        public void SetVisible(bool on)
        {
            if (gameObject.activeSelf != on)
                gameObject.SetActive(on);
            if (_armGo[0].activeSelf != on)
                _armGo[0].SetActive(on);
            if (!on)
            {
                _armsInit = false;
                _feetInit = false;
                _hasPose = false;
            }
        }

        private void OnDestroy()
        {
            if (_armGo[0] != null)
                Destroy(_armGo[0]);
            if (_limbMesh != null)
                Destroy(_limbMesh);
        }

        private bool _grey;

        /// <summary>G3.4 the claw moment: an eliminated Rushling turns into a grey capsule.</summary>
        public void SetGrey(bool on)
        {
            if (_grey == on)
                return;
            _grey = on;
            _plainMpb.Clear();
            _plainMpb.SetFloat(OutlineWidthId, LimbOutline);
            if (on)
                _plainMpb.SetFloat("_Desat", 1f);
            _limbsR.SetPropertyBlock(_plainMpb);
            ApplyBody();
        }

        /// <summary>Unlit black override for the silhouette test (doc 17 §2.1).</summary>
        public void SetSilhouette(bool on)
        {
            Silhouette = on;
            _plainMpb.Clear();
            _plainMpb.SetFloat(OutlineWidthId, LimbOutline);
            if (on)
            {
                _plainMpb.SetColor(BaseColorId, Color.black);
                _plainMpb.SetFloat(UnlitId, 1f);
            }

            _limbsR.SetPropertyBlock(_plainMpb);
            if (on)
                _starsR.SetPropertyBlock(_plainMpb);
            else
                StarsBright();
            ApplyBody();
        }

        /// <summary>Dizzy stars draw unlit (always bright lemon, never shaded olive) with a thin ink outline.</summary>
        private void StarsBright()
        {
            var b = new MaterialPropertyBlock();
            b.SetFloat(UnlitId, 1f);
            b.SetFloat(OutlineWidthId, 0.004f);
            _starsR.SetPropertyBlock(b);
        }

        // ------------------------------------------------------------------ per frame (G1 animation set)

        /// <summary>Celebration / end poses driven by the match screens (client only).</summary>
        public enum Pose : byte { None, Qualified, Eliminated, Champion }

        /// <summary>Emotes 1–4 (GDD §6): wave, dance, cheer, flop.</summary>
        public const int EmoteCount = 4;
        private const float EmoteSeconds = 2.2f;
        private const float TurnRateDeg = 720f;     // visual facing turn rate (doc 04 §6, G1)

        private Pose _pose;
        private int _emote;                           // 1..4 while playing
        private float _emoteT;
        private float _gait;                          // radians; π per step
        private float _cadence;
        private float _idleT, _look, _lookTarget, _lookNext, _fidgetNext;
        private float _stumbleT = -1f, _scrambleT = -1f, _getUpT = -1f;
        private float _tumbleT = -1f, _tumbleDur, _tumbleRoll;
        private Vector3 _tumbleAxis = Vector3.right;
        private float _hop;                           // visual hop height (celebrations, emotes)
        private float _yawRate;
        private bool _wasStumbling, _wasSliding;
        private float _flail;

        /// <summary>Sets a celebration / end pose (None = gameplay).</summary>
        public void SetPose(Pose p) => _pose = p;

        /// <summary>Plays emote 1–4 (it ends by itself; moving cancels it).</summary>
        public void PlayEmote(int id)
        {
            if (id < 1 || id > EmoteCount)
                return;
            _emote = id;
            _emoteT = 0f;
            _emoteStarted = true;
        }

        public int Emote => _emote;

        /// <summary>Crowd bump (pushes stay server-only; this is the wobble you see). <paramref name="worldDir"/> points away from the other player.</summary>
        /// <summary>Seconds a stun stays "on" for the visuals after its last non-zero frame.</summary>
        public const float StunHoldSeconds = 0.15f;
        private float _stunHold;

        /// <summary>
        /// Beta audit (2026-11-25, user: "why still showing stars?"): the own Rushling's stun comes from prediction, which
        /// does not know a hit by another player until the next snapshot, so during one hit the value flickers
        /// on/off at the snapshot rate. Each 0 → stun edge restarted the hit (5 bonk stars, the flop pose, the sound): one
        /// hit became a spray of 30–40 stars with the body face-down out of sight. Holding the stun for a few frames makes
        /// one hit one hit. Visual only: the sim and the server never see this.
        /// </summary>
        private byte HoldStun(byte stun, float dt)
        {
            if (stun > 0)
            {
                _stunHold = StunHoldSeconds;
                return stun;
            }

            if (_stunHold <= 0f)
                return 0;
            _stunHold -= dt;
            return _lastStun > 0 ? (byte)1 : (byte)0;
        }

        public void Bump(Vector3 worldDir, float strength)
        {
            if (!_hasPose)
                return;
            var l = transform.InverseTransformDirection(worldDir);
            _wobV += new Vector2(l.z, -l.x) * (90f * strength);
        }

        /// <summary>Poses the Rushling for this frame.</summary>
        /// <param name="feet">Capsule bottom (world).</param>
        /// <param name="vel">Velocity (m/s).</param>
        public void Drive(Vector3 feet, Vector3 vel, byte flags, byte yaw256, byte stun, float dt)
        {
            stun = HoldStun(stun, dt);
            StunStartedThisFrame = DiveStartedThisFrame = LandedThisFrame = StumbleStartedThisFrame = GetUpThisFrame = StepThisFrame = JumpedThisFrame = false;
            EmoteStartedThisFrame = _emoteStarted;
            _emoteStarted = false;
            if (dt <= 0f)
                return;
            dt = Mathf.Min(dt, 1f / 20f);
            _time += dt;
            var reduced = GameSettings.ReducedMotion;

            var grounded = (flags & MotorState.Grounded) != 0;
            var diving = (flags & MotorState.Diving) != 0;
            var sliding = (flags & MotorState.DiveLanding) != 0;
            var stumbling = (flags & MotorState.Stumbling) != 0;
            var hspeed = new Vector2(vel.x, vel.z).magnitude;
            var speedN = Mathf.Clamp01(hspeed / 5f);
            var targetYaw = yaw256 * (360f / 256f);
            if (!_hasPose)
            {
                _yaw = targetYaw;
                _lastVel = vel;
                _wasGrounded = grounded;
                _hasPose = true;
                _tumbleT = _stumbleT = _scrambleT = _getUpT = -1f;
                _stunHold = 0f;
                _lookNext = _time + 2f;
                _fidgetNext = _time + 6f;
            }

            // Moving (or any action) cancels an emote.
            if (_emote != 0 && (hspeed > 0.6f || !grounded || stun > 0 || diving))
                _emote = 0;

            // ---- facing: rate-limited turn (weight), eased ----
            var prevYaw = _yaw;
            var rate = diving ? TurnRateDeg * 2.5f : TurnRateDeg;
            var stepYaw = Mathf.MoveTowardsAngle(_yaw, targetYaw, rate * dt);
            _yaw = Mathf.LerpAngle(_yaw, stepYaw, 1f - Mathf.Exp(-dt * 30f));
            _yawRate = Mathf.Lerp(_yawRate, Mathf.DeltaAngle(prevYaw, _yaw) / dt, 1f - Mathf.Exp(-dt * 10f));

            // Idle look-around (small yaw offsets while standing still).
            var idle = grounded && hspeed < 0.3f && stun == 0 && !sliding && _emote == 0 && _pose == Pose.None;
            _idleT = idle ? _idleT + dt : 0f;
            if (_idleT > 1.5f && _time >= _lookNext)
            {
                _lookTarget = Mathf.Repeat(_time * 37.1f, 50f) - 25f;
                _lookNext = _time + 2.2f + Mathf.Repeat(_time * 1.3f, 2.5f);
            }

            if (_idleT <= 1.5f)
                _lookTarget = 0f;
            _look = Mathf.Lerp(_look, _lookTarget, 1f - Mathf.Exp(-dt * 4f));
            var spin = _emote == 2 ? Mathf.SmoothStep(0f, 360f, Mathf.Clamp01((_emoteT - 0.3f) / 1.4f)) : 0f;
            transform.SetPositionAndRotation(feet, Quaternion.Euler(0f, _yaw + _look + spin, 0f));

            // ---- events ----
            if (grounded && !_wasGrounded)
            {
                var impact = Mathf.Clamp01(-_lastVel.y / 12f);
                LandedThisFrame = true;
                LandImpact = impact;
                _sq = -0.22f * Mathf.Lerp(0.45f, 1f, impact);
                _sqV = 0f;
                _wobV += new Vector2(18f + 40f * impact, 0f);
                if (sliding)
                    _diveV -= 2.5f; // belly-flop slap: a little bounce on the belly
            }

            if (!grounded && _wasGrounded && vel.y > 3f && !diving)
            {
                // Take-off: a quick anticipation squash that springs into a stretch.
                JumpedThisFrame = true;
                _sq = -0.1f;
                _sqV = 7f;
            }

            if (diving && _dive < 0.05f && _diveV <= 0.01f)
                DiveStartedThisFrame = true;

            if (stumbling && !_wasStumbling)
            {
                StumbleStartedThisFrame = true;
                _stumbleT = 0f;
                _wobV += new Vector2(-60f, (Mathf.Repeat(_time * 7.3f, 1f) > 0.5f ? 1f : -1f) * 50f);
            }

            if (_wasSliding && !sliding && grounded && stun == 0)
                _scrambleT = 0f; // scramble back up after the belly slide

            if (stun > 0 && _lastStun == 0)
            {
                StunStartedThisFrame = true;
                _lidV += 600f;
                _starsLeft = 0.6f;
                // Tumble: flop over in the knock direction (horizontal velocity right after the hit), roll, then get up.
                var away = transform.InverseTransformDirection(new Vector3(vel.x, 0f, vel.z));
                if (away.sqrMagnitude < 0.01f)
                    away = Vector3.back;
                away.Normalize();
                _tumbleAxis = Vector3.Cross(Vector3.up, away).normalized;
                _tumbleDur = Mathf.Max(0.25f, stun / 30f);
                _tumbleRoll = reduced || _tumbleDur < 0.45f ? 0f : 360f;
                _tumbleT = 0f;
                _getUpT = -1f;
            }

            if (stun == 0 && _lastStun > 0 && _tumbleT >= 0f)
            {
                _tumbleT = -1f;
                _getUpT = 0f; // "get up" pop
                GetUpThisFrame = true;
                _sq = -0.12f;
                _sqV = 8f;
                _starsLeft = Mathf.Max(_starsLeft, DizzyTail); // woozy for a beat after getting up (visual only)
            }

            _lastStun = stun;
            _wasGrounded = grounded;
            _wasStumbling = stumbling;
            _wasSliding = sliding;

            // ---- timers ----
            if (_stumbleT >= 0f && (_stumbleT += dt) > 0.6f)
                _stumbleT = -1f;
            if (_scrambleT >= 0f && (_scrambleT += dt) > 0.3f)
                _scrambleT = -1f;
            if (_getUpT >= 0f && (_getUpT += dt) > 0.45f)
                _getUpT = -1f;
            if (_tumbleT >= 0f)
                _tumbleT += dt;
            if (_emote != 0 && (_emoteT += dt) > EmoteSeconds)
                _emote = 0;

            // ---- gait (steps in time with ground speed) ----
            var walking = grounded && !sliding && hspeed > 0.25f && _tumbleT < 0f;
            _cadence = 3.2f + 0.95f * hspeed; // steps per second: 8 at 5 m/s (≈ 0.6 m per step)
            if (walking)
            {
                var before = Mathf.Floor(_gait / Mathf.PI);
                _gait += Mathf.PI * _cadence * dt;
                StepThisFrame = Mathf.Floor(_gait / Mathf.PI) != before; // a foot plants
                if (_gait > Mathf.PI * 600f)
                    _gait -= Mathf.PI * 600f; // keep the phase small (an even number of steps: the swing foot is kept)
            }

            Sliding = sliding;
            var gaitK = walking ? Mathf.Clamp01(hspeed / 2.5f) : 0f;

            // ---- acceleration (smoothed) for lean ----
            var rawA = (vel - _lastVel) / dt;
            _lastVel = vel;
            rawA.y = 0f;
            _accel = Vector3.Lerp(_accel, rawA, 1f - Mathf.Exp(-dt * 8f));
            var aLocal = transform.InverseTransformDirection(_accel);
            var leanTarget = grounded && !diving && !sliding ? new Vector2(aLocal.z * 0.9f + speedN * 6f, -aLocal.x * 0.9f) : Vector2.zero;
            if (grounded && !sliding)
                leanTarget.y -= Mathf.Clamp(_yawRate * speedN * 0.03f, -12f, 12f); // lean into turns
            if (leanTarget.sqrMagnitude > 20f * 20f)
                leanTarget = leanTarget.normalized * 20f;
            Spring2(ref _lean, ref _leanV, leanTarget, 14f, 1f, dt);

            // ---- squash / wobble / dive / lid springs ----
            var sqTarget = 0f;
            if (idle)
                sqTarget = Mathf.Sin(_time * 2.3f) * 0.014f; // breathing
            if (idle && _idleT > 3f && _time >= _fidgetNext)
            {
                _fidgetNext = _time + 5f + Mathf.Repeat(_time * 2.9f, 4f);
                _sq = -0.07f; // fidget: a little bounce on the spot
                _sqV = 4f;
                _keyBoost = 1.2f;
                _smugUntil = _time + 0.9f;
            }

            // Dizzy tail: after the get-up the stars keep circling and the Rushling droops a little (control is back: the
            // sim's stun is over, this is only the look).
            var woozy = stun == 0 && _tumbleT < 0f && _starsLeft > 0f ? Mathf.Clamp01(_starsLeft / 0.8f) : 0f; // full until the last 0.8 s
            sqTarget -= 0.07f * woozy;
            Spring(ref _sq, ref _sqV, sqTarget, 26f, 0.55f, dt);
            Spring2(ref _wob, ref _wobV, Vector2.zero, 16f, 0.22f, dt);
            var diveTarget = diving ? 1f : sliding ? 1.08f : 0f;
            Spring(ref _dive, ref _diveV, diveTarget, sliding || diving ? 16f : 13f, diving || sliding ? 1f : 0.42f, dt); // underdamped getting up: pops past upright
            if (_starsLeft > 0f)
                _starsLeft -= dt;
            // Tired "run-down capsule": the lid hangs open and flaps loosely while the stars circle.
            var lidTarget = woozy > 0f ? 34f + (reduced ? 0f : Mathf.Sin(_time * 3.1f) * 7f) : _starsLeft > 0f ? 25f : 0f;
            Spring(ref _lidA, ref _lidV, lidTarget, 22f, 0.6f, dt);

            // ---- body pose ----
            var bob = walking ? -Mathf.Abs(Mathf.Cos(_gait)) * 0.04f * gaitK + 0.02f * gaitK : 0f;
            var waddleRoll = Mathf.Sin(_gait) * 7f * gaitK;
            var waddleYaw = Mathf.Sin(_gait) * 5f * gaitK;
            var pitch = _lean.x + _wob.x + _dive * 82f;
            var roll = _lean.y + _wob.y + waddleRoll;
            if (woozy > 0f && !reduced)
            {
                roll += Mathf.Sin(_time * Mathf.PI * 2f * 0.9f) * 8f * woozy; // slow, tired sway
                pitch += 7f * woozy + Mathf.Sin(_time * Mathf.PI * 2f * 0.45f) * 3f * woozy; // slumped forward
            }
            _hop = 0f;

            if (_stumbleT >= 0f && !reduced)
            {
                var k = 1f - _stumbleT / 0.6f;
                pitch += 24f * Mathf.Sin(Mathf.Min(1f, _stumbleT / 0.12f) * Mathf.PI * 0.5f) * k;
                roll += Mathf.Sin(_stumbleT * 22f) * 13f * k;
            }

            if (!grounded && !diving && stun == 0)
                pitch -= Mathf.Clamp(vel.y, -8f, 8f) * 0.8f; // lean back rising, forward falling

            if (_scrambleT >= 0f)
                roll += Mathf.Sin(_scrambleT * 40f) * 6f * (1f - _scrambleT / 0.3f);

            // Celebrations and emotes.
            switch (_pose)
            {
                case Pose.Qualified:
                    _hop = Mathf.Abs(Mathf.Sin(_time * 6.5f)) * 0.28f;
                    roll += Mathf.Sin(_time * 6.5f) * 6f;
                    break;
                case Pose.Eliminated:
                    pitch += 22f;
                    sqTarget = -0.08f;
                    _sq = Mathf.Lerp(_sq, -0.08f, 1f - Mathf.Exp(-dt * 6f));
                    break;
                case Pose.Champion: // arms up; the podium drives the hop (the crown follows it)
                    roll += Mathf.Sin(_time * 1.6f) * 5f;
                    pitch -= 8f;
                    break;
            }

            switch (_emote)
            {
                case 2: // dance: side-to-side waddle
                    roll += Mathf.Sin(_emoteT * 12f) * 14f;
                    _hop = Mathf.Abs(Mathf.Sin(_emoteT * 6f)) * 0.08f;
                    break;
                case 3: // cheer: jumping for joy
                    _hop = Mathf.Abs(Mathf.Sin(_emoteT * 7f)) * 0.3f;
                    break;
                case 4: // flop: belly-flop on the spot, then pop up
                {
                    var f = _emoteT < 0.25f ? _emoteT / 0.25f : _emoteT < 1.5f ? 1f : Mathf.Max(0f, 1f - (_emoteT - 1.5f) / 0.25f);
                    pitch += 88f * Mathf.SmoothStep(0f, 1f, f);
                    break;
                }
            }

            if (reduced)
                _hop *= 0.3f;

            var rot = Quaternion.Euler(pitch, waddleYaw, roll);
            var lie = Mathf.Abs(Mathf.Sin(Mathf.Clamp(pitch, -90f, 90f) * Mathf.Deg2Rad));
            var tumbleAngle = 0f;
            if (_tumbleT >= 0f)
            {
                // Flop over (0 → 90° in 0.12 s), roll once on a long knock, rest on the side, bounce on the first contact.
                var u = Mathf.Clamp01(_tumbleT / _tumbleDur);
                tumbleAngle = Mathf.Min(1f, _tumbleT / 0.12f) * 90f + _tumbleRoll * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((u - 0.15f) / 0.6f));
                rot = Quaternion.AngleAxis(tumbleAngle, _tumbleAxis) * rot;
                lie = Mathf.Max(lie, Mathf.Abs(Mathf.Sin(tumbleAngle * Mathf.Deg2Rad)));
                _hop += Mathf.Abs(Mathf.Sin(u * Mathf.PI * 2f)) * 0.12f * (1f - u);
            }
            else if (_getUpT >= 0f)
            {
                // Get-up pop: from lying on the side back to upright with an overshoot.
                var u = Mathf.Clamp01(_getUpT / 0.45f);
                var a = 90f * (1f - u) * (1f - u) - Mathf.Sin(u * Mathf.PI) * 12f;
                rot = Quaternion.AngleAxis(a, _tumbleAxis) * rot;
                lie = Mathf.Max(lie, Mathf.Abs(Mathf.Sin(a * Mathf.Deg2Rad)));
                _hop += Mathf.Sin(u * Mathf.PI) * 0.18f;
            }

            _tilt.localRotation = rot;
            // Lying down lowers the body centre (0.5 m standing → ~0.32 m on its side) so it rests on the floor.
            _tilt.localPosition = new Vector3(0f, 0.5f - 0.18f * lie + bob + _hop, 0f);
            var sy = 1f + _sq;
            var sxz = 1f - 0.75f * _sq;
            _squash.localScale = new Vector3(sxz, sy, sxz);
            _lidRad = Mathf.Max(0f, _lidA) * Mathf.Deg2Rad;

            // ---- key ----
            _keyBoost = Mathf.Max(0f, _keyBoost - dt);
            _keyAngle = Mathf.Repeat(_keyAngle + (60f + hspeed * 110f + _keyBoost * 900f) * (diving || sliding ? 3f : 1f) * (1f - 0.9f * woozy) * dt, 360f); // tired: the wind-up key runs down
            ApplyBody();

            // ---- dizzy swirls ----
            var starsOn = _starsLeft > 0f || stun > 0;
            if (_starsRoot.gameObject.activeSelf != starsOn)
                _starsRoot.gameObject.SetActive(starsOn);
            if (starsOn)
            {
                // Classic orbit: the ring spins and wobbles, and bobs a little (reduced motion: still, no spin).
                _starsRoot.localRotation = reduced ? Quaternion.Euler(8f, 0f, 0f) : Quaternion.Euler(10f + Mathf.Sin(_time * 5f) * 7f, -_time * 6f * Mathf.Rad2Deg, 0f);
                _starsRoot.localPosition = new Vector3(0f, 1.2f + (reduced ? 0f : Mathf.Sin(_time * 9f) * 0.02f), 0f);
                var outro = stun > 0 ? 1f : Mathf.Clamp01(_starsLeft / 0.3f); // shrink away in the last 0.3 s
                var twinkle = reduced ? 1f : 1f + Mathf.Sin(_time * 14f) * 0.08f;
                _starsRoot.localScale = Vector3.one * (Mathf.SmoothStep(0f, 1f, outro) * twinkle);
            }

            // ---- face ----
            ProcMesh.Glyph g;
            if (stun > 0 || _tumbleT >= 0f || _starsLeft > DizzyTail - 0.8f)
                g = ProcMesh.Glyph.Stunned;
            else if (_starsLeft > 0f)
                g = ProcMesh.Glyph.Sleepy; // tired, half-closed eyes while the stars wind down
            else if (_pose == Pose.Champion)
                g = ProcMesh.Glyph.Champion;
            else if (_pose == Pose.Qualified)
                g = ProcMesh.Glyph.Qualified;
            else if (_emote == 1)
                g = ProcMesh.Glyph.Wink;
            else if (_emote == 2)
                g = ProcMesh.Glyph.Love;
            else if (_emote == 3)
                g = ProcMesh.Glyph.Happy;
            else if (_pose == Pose.Eliminated)
                g = ProcMesh.Glyph.Sad;
            else if (_getUpT >= 0f || _angryUntil > _time)
                g = ProcMesh.Glyph.Angry; // a grumpy beat after getting up from a hit
            else if (diving || sliding || _emote == 4)
                g = ProcMesh.Glyph.Diving;
            else if (stumbling || _stumbleT >= 0f || (!grounded && vel.y < -7f))
                g = ProcMesh.Glyph.Falling;
            else if (!grounded)
                g = ProcMesh.Glyph.Jumping;
            else if (hspeed > 1f)
                g = ProcMesh.Glyph.Running;
            else if (idle && _idleT > 9f)
                g = ProcMesh.Glyph.Sleepy;
            else if (idle && _smugUntil > _time)
                g = ProcMesh.Glyph.Smug;
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

            _flail = !grounded && !diving && stun == 0 ? Mathf.MoveTowards(_flail, 1f, dt * 4f) : Mathf.MoveTowards(_flail, 0f, dt * 6f);
            var bodyPose = !grounded || diving || sliding || _tumbleT >= 0f || _getUpT >= 0f || _hop > 0.01f || _emote == 4;
            UpdateFeet(feet, vel, grounded && !bodyPose, walking, hspeed, dt);
            UpdateArms(vel, grounded, diving || sliding, stumbling || _stumbleT >= 0f, dt);
            WriteFoot(0);
            WriteFoot(1);
            UploadLimbs();
        }

        private float _keyBoost;
        private float _angryUntil, _smugUntil;

        /// <summary>Seconds the stars and woozy look stay after the get-up (visual only; the sim stun is 0.35–0.6 s).</summary>
        public const float DizzyTail = 3.5f; // + the 0.35–0.6 s stun ≈ 4 s tired (user ask, 2026-11-24)

        /// <summary>Starts (or extends) the dizzy stars for <paramref name="seconds"/> (respawn after a knock-off fall).</summary>
        public void Dizzy(float seconds) => _starsLeft = Mathf.Max(_starsLeft, seconds);

        // ------------------------------------------------------------------ feet

        /// <summary>
        /// Peg feet. Walking: alternate steps timed by the gait phase (one step per π), the stance foot stays planted in the
        /// world (no sliding), the swing foot travels to half a step ahead. Not walking: settle under the body. Airborne,
        /// diving, tumbling, hopping: tucked under the body (they follow its pose).
        /// </summary>
        private void UpdateFeet(Vector3 feet, Vector3 vel, bool planted, bool walking, float hspeed, float dt)
        {
            var rot = transform.rotation;
            var stepLen = hspeed / Mathf.Max(1f, _cadence);
            var swingIdx = ((int)Mathf.Floor(_gait / Mathf.PI)) & 1;
            var u = Mathf.Repeat(_gait / Mathf.PI, 1f);
            for (var f = 0; f < 2; f++)
            {
                var side = f == 0 ? -0.12f : 0.12f;
                var rest = feet + rot * new Vector3(side, 0f, 0f);
                if (!_feetInit)
                {
                    _footPlant[f] = _footFrom[f] = rest;
                    _footT[f] = 1f;
                }

                if (!planted)
                {
                    // Tucked under the body; a little kick while falling or scrambling.
                    var kick = (_flail > 0f ? Mathf.Sin(_time * 16f + f * Mathf.PI) * 0.05f * _flail : 0f) +
                               (_scrambleT >= 0f ? Mathf.Sin(_scrambleT * 50f + f * Mathf.PI) * 0.06f : 0f);
                    var p = _squash.TransformPoint(new Vector3(side, 0.02f, kick));
                    _footPlant[f] = _footFrom[f] = p;
                    _footT[f] = 1f;
                    _feet[f].SetPositionAndRotation(p, _squash.rotation);
                    continue;
                }

                Vector3 pos;
                var hv = new Vector3(vel.x, 0f, vel.z);
                if (walking && f == swingIdx)
                {
                    if (_footT[f] >= 1f)
                    {
                        _footFrom[f] = _footPlant[f]; // lift off from where it stood
                        _footT[f] = 0f;
                    }

                    _footT[f] = Mathf.Max(_footT[f], Mathf.Min(0.999f, u));
                    var target = rest + (hspeed > 0.01f ? hv / hspeed : Vector3.zero) * (stepLen * 0.5f);
                    var t = Mathf.SmoothStep(0f, 1f, _footT[f]);
                    pos = Vector3.Lerp(_footFrom[f], target, t) + Vector3.up * (Mathf.Sin(_footT[f] * Mathf.PI) * (0.05f + 0.03f * Mathf.Clamp01(hspeed / 5f)));
                    _footPlant[f] = target;
                }
                else
                {
                    if (_footT[f] < 1f)
                    {
                        _footT[f] = 1f; // planted where the swing ended
                    }

                    if (!walking)
                    {
                        // Settle: a quick shuffle step back under the body if far from rest.
                        if ((_footPlant[f] - rest).sqrMagnitude > 0.07f * 0.07f)
                            _footPlant[f] = Vector3.MoveTowards(_footPlant[f], rest, dt * 2.5f);
                    }

                    pos = _footPlant[f];
                }

                // Planted feet never drift far from the capsule (moving platforms, teleports).
                if ((pos - rest).sqrMagnitude > 0.5f * 0.5f)
                {
                    pos = _footPlant[f] = _footFrom[f] = rest;
                    _footT[f] = 1f;
                }

                pos.y = Mathf.Max(pos.y, feet.y);
                _feet[f].SetPositionAndRotation(pos, rot);
            }

            _feetInit = true;
        }

        // ------------------------------------------------------------------ arms

        private void UpdateArms(Vector3 vel, bool grounded, bool flat, bool stumbling, float dt)
        {
            var body = _squash;
            var fwd = body.forward;
            var up = body.up;
            var right = body.right;
            var walking = grounded && !flat && new Vector2(vel.x, vel.z).sqrMagnitude > 0.25f * 0.25f;
            var swingK = walking ? Mathf.Clamp01(new Vector2(vel.x, vel.z).magnitude / 4f) : 0f;
            for (var a = 0; a < 2; a++)
            {
                var side = a == 0 ? -1f : 1f;
                var shoulder = body.TransformPoint(new Vector3(side * (ProcMesh.R - 0.01f), 0.5f, 0.02f));

                // Pose the chain wants (loose; the verlet springs lag behind it).
                Vector3 dir;
                if (flat)
                    dir = (fwd * 1f + up * 0.05f + right * side * 0.12f).normalized; // superhero reach
                else if (_tumbleT >= 0f)
                    dir = (right * side * 0.9f + up * Mathf.Sin(_time * 18f + a * 2f) * 0.6f).normalized; // ragdoll flop
                else if (_pose == Pose.Qualified || _pose == Pose.Champion || _emote == 3)
                    dir = (right * side * 0.35f + up * 1f + fwd * 0.1f * Mathf.Sin(_time * 9f + a * 3f)).normalized; // arms up
                else if (_emote == 1 && a == 1)
                    dir = (right * 0.5f + up * 1f + fwd * Mathf.Sin(_emoteT * 14f) * 0.45f).normalized; // wave
                else if (_emote == 2)
                    dir = (right * side * 0.6f + up * (a == 0 ? Mathf.Sin(_emoteT * 6f) : -Mathf.Sin(_emoteT * 6f)) * 0.8f).normalized;
                else if (_pose == Pose.Eliminated)
                    dir = (right * side * 0.12f - up * 1f + fwd * 0.15f).normalized; // slump
                else if (stumbling)
                {
                    var w = _time * 15f + a * Mathf.PI; // windmill
                    dir = (right * side * 0.6f + up * Mathf.Sin(w) + fwd * Mathf.Cos(w)).normalized;
                }
                else if (!grounded)
                {
                    var f = Mathf.Sin(_time * (vel.y < -5f ? 19f : 13f) + a * 2.1f);
                    var upAmt = vel.y < -5f ? 1.0f : 0.45f; // falling: arms up, flailing
                    dir = (right * side * 0.7f + up * (upAmt + 0.55f * f) + fwd * 0.25f * Mathf.Cos(_time * 9f + a)).normalized;
                }
                else
                {
                    // Hanging out and down, swinging opposite the legs.
                    var swing = Mathf.Cos(_gait) * side * swingK;
                    dir = (right * side * 0.42f - up * 0.9f + fwd * (0.08f + swing * 0.7f)).normalized;
                }

                if (!_armsInit)
                {
                    for (var i = 0; i < ArmPoints; i++)
                        _arm[a, i] = _armPrev[a, i] = shoulder + dir * (i * ArmSegment);
                }

                // Verlet integration with damping, gravity and a spring toward the pose.
                var k = flat ? 900f : grounded && !stumbling ? 160f : 260f;
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
                WriteMitt(a, _arm[a, ArmPoints - 1]);
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

            var baseIdx = a * (TubeSegments + 1) * TubeSides;
            var n = Vector3.zero;
            for (var s = 0; s <= TubeSegments; s++)
            {
                var tangent = _spline[Mathf.Min(TubeSegments, s + 1)] - _spline[Mathf.Max(0, s - 1)];
                if (tangent.sqrMagnitude < 1e-8f)
                    tangent = Vector3.down;
                tangent.Normalize();
                // Parallel transport: carry the previous ring's normal along the curve (no 90° flips → no twisting).
                if (s == 0)
                {
                    n = Vector3.Cross(tangent, Vector3.up);
                    if (n.sqrMagnitude < 1e-4f)
                        n = Vector3.Cross(tangent, Vector3.right);
                }
                else
                {
                    n -= tangent * Vector3.Dot(n, tangent);
                    if (n.sqrMagnitude < 1e-8f)
                        n = Vector3.Cross(tangent, Vector3.right);
                }

                n.Normalize();
                var b = Vector3.Cross(tangent, n);
                var radius = TubeRadius * (s == 0 ? 0.9f : 1f);
                for (var k = 0; k < TubeSides; k++)
                {
                    var ang = k * (Mathf.PI * 2f / TubeSides);
                    var dir = n * Mathf.Cos(ang) + b * Mathf.Sin(ang);
                    var v = _spline[s] + dir * radius;
                    _lv[baseIdx + s * TubeSides + k] = v;
                    _ln[baseIdx + s * TubeSides + k] = dir;
                }
            }
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
                    // Unity front faces are clockwise: cross(v1 − v0, v2 − v0) must point out of the tube (G2 fix: the
                    // M2 order faced inward, which the outline hull exposed).
                    tris[t++] = a; tris[t++] = b; tris[t++] = c;
                    tris[t++] = b; tris[t++] = d; tris[t++] = c;
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
            ApplyBody();
        }

        private float _lidRad;

        /// <summary>Per-Rushling shader values on the one body renderer: face glyph + tint, key spin, lid hinge.</summary>
        private void ApplyBody()
        {
            if (_bodyR == null || _bodyMpb == null)
                return;
            _bodyMpb.Clear();
            _bodyMpb.SetTexture(FaceMapId, ProcMesh.FaceAtlas);
            _bodyMpb.SetVector(FaceStId, ProcMesh.GlyphST(_glyph == (ProcMesh.Glyph)255 ? ProcMesh.Glyph.Idle : _glyph));
            _bodyMpb.SetFloat(FaceOnId, Silhouette ? 0f : 1f);
            _bodyMpb.SetColor(FaceTintId, _faceTint);
            _bodyMpb.SetFloat(KeyAngleId, _keyAngle * Mathf.Deg2Rad);
            _bodyMpb.SetVector(KeyPivotId, RushlingParts.KeyPivot);
            _bodyMpb.SetFloat(LidAngleId, _lidRad);
            _bodyMpb.SetVector(LidPivotId, RushlingParts.LidPivot);
            if (Silhouette)
            {
                _bodyMpb.SetColor(BaseColorId, Color.black);
                _bodyMpb.SetFloat(UnlitId, 1f);
            }
            else if (_grey)
            {
                _bodyMpb.SetFloat("_Desat", 1f); // a grey capsule (the claw moment)
                _bodyMpb.SetColor(BaseColorId, new Color(0.82f, 0.82f, 0.86f));
                _bodyMpb.SetColor(FaceTintId, new Color(0.7f, 0.7f, 0.75f));
            }

            _bodyR.SetPropertyBlock(_bodyMpb);
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
    }
}
