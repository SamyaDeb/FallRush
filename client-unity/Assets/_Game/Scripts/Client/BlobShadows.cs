using UnityEngine;
using UnityEngine.Rendering;

namespace FallRush.Client
{
    /// <summary>
    /// G2.1 blob shadows: a soft round contact shadow straight below each visible Rushling's capsule, on the first
    /// gameplay surface under it (one raycast per Rushling per frame), drawn in one instanced call. A gameplay aid for
    /// judging jumps, so it is exact: it sits where the capsule would land if it fell straight down, shrinks and fades
    /// with height. No realtime shadows anywhere else (doc 17 §3.2). Allocation-free per frame.
    /// </summary>
    public sealed class BlobShadows
    {
        private const float MaxDrop = 40f;
        private readonly Matrix4x4[] _m = new Matrix4x4[Shared.Protocol.MaxPlayers];
        private readonly int _mask;
        private readonly Mesh _quad;
        private readonly Material _mat;
        private int _n;
        private RenderParams _rp;

        public BlobShadows(int groundMask)
        {
            _mask = groundMask;
            _mat = Resources.Load<Material>("FallRush/BlobShadow");
            _quad = Quad();
            _rp = new RenderParams(_mat) { shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false };
        }

        public void Begin() => _n = 0;

        /// <summary>Adds the shadow for a capsule whose bottom is at <paramref name="feet"/>.</summary>
        public void Add(Vector3 feet)
        {
            if (_n >= _m.Length || _mat == null)
                return;
            var from = feet + Vector3.up * 0.45f;
            if (!Physics.Raycast(from, Vector3.down, out var hit, MaxDrop, _mask, QueryTriggerInteraction.Ignore))
                return;
            var h = Mathf.Max(0f, feet.y - hit.point.y);
            var size = 0.66f * Mathf.Lerp(1f, 0.55f, Mathf.Clamp01(h / 6f));
            var alpha = Mathf.Lerp(1f, 0.35f, Mathf.Clamp01(h / 8f));
            var rot = Quaternion.FromToRotation(Vector3.up, hit.normal);
            _m[_n++] = Matrix4x4.TRS(hit.point + hit.normal * 0.025f, rot, new Vector3(size, alpha, size)); // Y scale = opacity
        }

        public void Draw()
        {
            if (_n > 0 && _mat != null)
                Graphics.RenderMeshInstanced(_rp, _quad, 0, _m, _n);
        }

        private static Mesh Quad()
        {
            var m = new Mesh { name = "BlobQuad" };
            m.vertices = new[] { new Vector3(-0.5f, 0f, -0.5f), new Vector3(-0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, -0.5f) };
            m.uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
            m.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
            return m;
        }
    }
}
