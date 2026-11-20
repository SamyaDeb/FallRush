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

        public PhysxMotorWorld(PhysicsScene scene, int layerMask, CapsuleCollider probe)
        {
            _scene = scene;
            _mask = layerMask;
            _probe = probe;
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
                hit = new MotorHit { Distance = rh.distance, Point = V(rh.point), Normal = V(rh.normal), SurfaceId = -1 };
                return true;
            }

            hit = default;
            return false;
        }

        public bool Raycast(Vec3 origin, Vec3 dir, float maxDistance, out MotorHit hit)
        {
            if (_scene.Raycast(V(origin), V(dir), out var rh, maxDistance, _mask, QueryTriggerInteraction.Ignore))
            {
                hit = new MotorHit { Distance = rh.distance, Point = V(rh.point), Normal = V(rh.normal), SurfaceId = -1 };
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
