using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// <see cref="IMotorWorld"/> backed by PhysX scene queries on the map's <see cref="PhysicsScene"/>, <c>Gameplay</c> layer only,
    /// triggers ignored. Physics simulation is never stepped (<see cref="SimulationMode.Script"/>); only queries are used.
    /// <para>Note: Unity sweeps do not report colliders the capsule already overlaps at the start; the motor
    /// depenetrates first (<see cref="ComputePenetration"/>) so that case does not arise in normal play.</para>
    /// </summary>
    public sealed class PhysxMotorWorld : IMotorWorld
    {
        public const string GameplayLayerName = "Gameplay";

        private readonly PhysicsScene _scene;
        private readonly int _mask;
        private readonly CapsuleCollider _probe;
        private readonly Collider[] _overlaps = new Collider[16];
        private readonly HazardSet _hazards;
        private readonly Collider[] _platforms;
        private uint _tick;
        private bool _posed;

        /// <param name="platforms">Collider of each <see cref="HazardSet.Platforms"/> entry (same order), moved by <see cref="BeginTick"/>.</param>
        public PhysxMotorWorld(PhysicsScene scene, int layerMask, CapsuleCollider probe, HazardSet hazards = null, Collider[] platforms = null)
        {
            _scene = scene;
            _mask = layerMask;
            _probe = probe;
            _hazards = hazards ?? HazardSet.Empty;
            _platforms = platforms ?? System.Array.Empty<Collider>();
            if (_platforms.Length != _hazards.Platforms.Length)
                throw new System.ArgumentException($"{_platforms.Length} platform colliders for {_hazards.Platforms.Length} platform defs");
        }

        public uint PosedTick => _tick;

        /// <summary>Moves the platform colliders to their pose at <paramref name="tick"/> and syncs PhysX (queries only; nothing is simulated).</summary>
        public void BeginTick(uint tick)
        {
            if (_posed && tick == _tick)
                return;
            _tick = tick;
            _posed = true;
            if (_platforms.Length == 0)
                return;
            for (var i = 0; i < _platforms.Length; i++)
            {
                var c = HazardMath.PlatformCenter(in _hazards.Platforms[i], tick);
                _platforms[i].transform.position = V(c);
            }

            Physics.SyncTransforms();
        }

        public bool SurfaceDelta(int surfaceId, out Vec3 delta) => _hazards.PlatformDelta(surfaceId, _tick, out delta);

        private int SurfaceOf(Collider c)
        {
            for (var i = 0; i < _platforms.Length; i++)
                if (ReferenceEquals(_platforms[i], c))
                    return i;
            return -1;
        }

        /// <summary>Creates the hidden capsule used by <see cref="Physics.ComputePenetration"/> (layer Ignore Raycast, outside the query mask).</summary>
        public static CapsuleCollider CreateProbe(Transform parent, float radius, float height)
        {
            var go = new GameObject("MotorProbe") { layer = 2 };
            go.transform.SetParent(parent, false);
            var c = go.AddComponent<CapsuleCollider>();
            c.radius = radius;
            c.height = height;
            c.direction = 1;
            c.center = new Vector3(0f, height * 0.5f, 0f);
            c.isTrigger = true;
            return c;
        }

        public static int GameplayMask()
        {
            var layer = LayerMask.NameToLayer(GameplayLayerName);
            return layer >= 0 ? 1 << layer : Physics.DefaultRaycastLayers;
        }

        private static Vector3 V(Vec3 v) => new Vector3(v.X, v.Y, v.Z);
        private static Vec3 V(Vector3 v) => new Vec3(v.x, v.y, v.z);

        public bool CapsuleSweep(Vec3 feet, float radius, float height, Vec3 dir, float maxDistance, out MotorHit hit)
        {
            var f = V(feet);
            var p1 = new Vector3(f.x, f.y + radius, f.z);
            var p2 = new Vector3(f.x, f.y + height - radius, f.z);
            if (_scene.CapsuleCast(p1, p2, radius, V(dir), out var rh, maxDistance, _mask, QueryTriggerInteraction.Ignore))
            {
                hit = new MotorHit { Distance = rh.distance, Point = V(rh.point), Normal = V(rh.normal), SurfaceId = SurfaceOf(rh.collider) };
                return true;
            }

            hit = default;
            return false;
        }

        public bool Raycast(Vec3 origin, Vec3 dir, float maxDistance, out MotorHit hit)
        {
            if (_scene.Raycast(V(origin), V(dir), out var rh, maxDistance, _mask, QueryTriggerInteraction.Ignore))
            {
                hit = new MotorHit { Distance = rh.distance, Point = V(rh.point), Normal = V(rh.normal), SurfaceId = SurfaceOf(rh.collider) };
                return true;
            }

            hit = default;
            return false;
        }

        public bool ComputePenetration(Vec3 feet, float radius, float height, out Vec3 correction)
        {
            correction = Vec3.Zero;
            var f = V(feet);
            var p1 = new Vector3(f.x, f.y + radius, f.z);
            var p2 = new Vector3(f.x, f.y + height - radius, f.z);
            var n = _scene.OverlapCapsule(p1, p2, radius, _overlaps, _mask, QueryTriggerInteraction.Ignore);
            if (n <= 0)
                return false;

            var total = Vector3.zero;
            var any = false;
            for (var i = 0; i < n; i++)
            {
                var col = _overlaps[i];
                var t = col.transform;
                if (Physics.ComputePenetration(_probe, f, Quaternion.identity, col, t.position, t.rotation, out var dir, out var dist) && dist > 0f)
                {
                    total += dir * (dist + 0.0005f);
                    any = true;
                }
            }

            correction = V(total);
            return any;
        }
    }
}
