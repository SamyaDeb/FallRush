using FallRush.Shared;
using UnityEngine;

namespace FallRush.Client
{
    /// <summary>
    /// P7-UX (OC-48): the character ring on the menu. All 12 cosmetic presets stand on a turntable in front of the menu
    /// camera; the picked one turns to the front, steps forward and cheers. In the play hub the ring slides right and only
    /// the picked Rushling stays (<see cref="Solo"/>). Procedural like every Rushling (no assets); cosmetics only.
    /// </summary>
    public sealed class RushlingRing : MonoBehaviour
    {
        public const float Radius = 2.7f;
        private const float StepDeg = 360f / 12f;

        private readonly RushlingVisuals[] _v = new RushlingVisuals[12];
        private Camera _cam;
        private Vector3 _centre, _centreTarget;
        private float _angle, _angleTarget;
        private int _picked = -1;
        private bool _solo;
        private float _soloT;
        private Transform _table;

        public int Picked => _picked;

        public static RushlingRing Create(Camera cam)
        {
            UiStage.Ensure(cam);
            var go = new GameObject("RushlingRing");
            var r = go.AddComponent<RushlingRing>();
            r._cam = cam;
            var mat = Resources.Load<Material>("FallRush/Character");
            for (var i = 0; i < 12; i++)
            {
                var root = new GameObject($"Rushling{i}").transform;
                root.SetParent(go.transform, false);
                var v = root.gameObject.AddComponent<RushlingVisuals>();
                v.Build(i, i, mat, null);
                v.SetVisible(true);
                r._v[i] = v;
            }

            r._table = BuildTable(go.transform, mat);
            r.Layout(false, true);
            r.Pick(GameSettings.CosmeticPreset, false);
            r._angle = r._angleTarget;
            return r;
        }

        /// <summary>A soft pastel turntable under the ring (a flat cylinder with a mint rim), no colliders.</summary>
        private static Transform BuildTable(Transform parent, Material baseMat)
        {
            var t = new GameObject("Turntable").transform;
            t.SetParent(parent, false);
            Disc(t, "Top", Radius + 0.75f, 0.16f, 0f, Hex(0xF9EDC2), baseMat);
            Disc(t, "Rim", Radius + 0.9f, 0.12f, -0.1f, Hex(0x2FE0A8), baseMat);
            return t;
        }

        private static void Disc(Transform parent, string name, float radius, float height, float y, Color c, Material baseMat)
        {
            var d = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            d.name = name;
            Object.Destroy(d.GetComponent<Collider>());
            d.transform.SetParent(parent, false);
            d.transform.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
            d.transform.localPosition = new Vector3(0f, y - height * 0.5f, 0f);
            var r = d.GetComponent<MeshRenderer>();
            if (baseMat != null)
            {
                var m = new Material(baseMat) { name = "Turntable " + name };
                if (m.HasProperty("_BaseColor"))
                    m.SetColor("_BaseColor", c);
                if (m.HasProperty("_Color"))
                    m.SetColor("_Color", c);
                r.sharedMaterial = m;
            }

            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        private static Color Hex(int rgb) => UiKit.Hex(rgb);

        /// <summary>Ring placement: centred under the title (pick) or right of the hub panel (solo).</summary>
        public void Layout(bool solo, bool instant = false)
        {
            _solo = solo;
            var f = _cam.transform.forward;
            f.y = 0f;
            f = f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
            var right = Vector3.Cross(Vector3.up, f);
            var basePos = _cam.transform.position + f * 8.6f + Vector3.down * 2.3f;
            _centreTarget = solo ? basePos + right * 2.9f + f * 1.2f : basePos;
            if (instant)
                _centre = _centreTarget;
        }

        /// <summary>Turns the ring so <paramref name="preset"/> faces the camera.</summary>
        public void Pick(int preset, bool cheer = true)
        {
            preset = ((preset % 12) + 12) % 12;
            if (preset == _picked)
                return;
            // shortest turn from the current angle
            var target = -preset * StepDeg;
            _angleTarget = _angle + Mathf.DeltaAngle(_angle, target);
            _picked = preset;
            if (cheer)
                _v[preset].PlayEmote(3);
        }

        public void Step(int d) => Pick(_picked + d);

        /// <summary>Drag / scroll: nudges the ring; it snaps to the nearest Rushling on release.</summary>
        public void Nudge(float degrees)
        {
            _angle += degrees;
            _angleTarget = _angle;
        }

        public void Snap()
        {
            var idx = Mathf.RoundToInt(-_angle / StepDeg);
            _picked = -1;
            Pick(idx);
        }

        private void Update()
        {
            var dt = Time.unscaledDeltaTime;
            var k = GameSettings.ReducedMotion ? 1f : 1f - Mathf.Exp(-dt * 7f);
            _angle = Mathf.Lerp(_angle, _angleTarget, k);
            _centre = Vector3.Lerp(_centre, _centreTarget, k);
            _soloT = Mathf.MoveTowards(_soloT, _solo ? 1f : 0f, dt * 3f);
            var f = _cam.transform.forward;
            f.y = 0f;
            f = f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
            var front = -f; // the slot nearest the camera
            var frontYaw = Mathf.Atan2(front.x, front.z) * Mathf.Rad2Deg;
            if (_table != null)
            {
                _table.position = _centre;
                _table.localScale = Vector3.one * Mathf.Lerp(1f, 0.42f, _soloT);
                _table.rotation = Quaternion.Euler(0f, frontYaw + _angle, 0f);
            }

            for (var i = 0; i < 12; i++)
            {
                var a = (frontYaw + _angle + i * StepDeg) * Mathf.Deg2Rad;
                var dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                var picked = i == _picked;
                var r = picked ? Mathf.Lerp(Radius + 0.45f, 0f, _soloT) : Radius;
                var feet = _centre + dir * r;
                var toCam = _cam.transform.position - feet;
                var yaw = Mathf.Atan2(toCam.x, toCam.z) * Mathf.Rad2Deg;
                // the back of the ring faces outwards (a crowd around a turntable), the front ones face the camera
                var facing = Mathf.Abs(Mathf.DeltaAngle(a * Mathf.Rad2Deg, frontYaw)) < 70f ? yaw : a * Mathf.Rad2Deg;
                var visible = !_solo || picked || _soloT < 0.5f;
                if (_v[i].gameObject.activeSelf != visible)
                    _v[i].gameObject.SetActive(visible);
                if (!visible)
                    continue;
                _v[i].transform.localScale = Vector3.one * (picked ? Mathf.Lerp(1.15f, 1.6f, _soloT) : 0.92f);
                _v[i].Drive(feet, Vector3.zero, MotorState.Grounded, (byte)Mathf.RoundToInt(Mathf.Repeat(facing, 360f) / 360f * 256f), 0, dt);
            }
        }
    }
}
