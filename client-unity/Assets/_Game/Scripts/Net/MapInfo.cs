using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// Per-map data both sides read from the loaded scene: 12 spawn points (feet positions) and the test-bot roaming area.
    /// (M3/M4 replace this with MapDefinition ScriptableObjects; kept minimal for M2.)
    /// </summary>
    public sealed class MapInfo : MonoBehaviour
    {
        public Transform[] spawns = new Transform[Protocol.MaxPlayers];
        public Vector3 botAreaCenter = Vector3.zero;
        public Vector2 botAreaHalfExtents = new Vector2(10f, 10f);

        [Tooltip("Client-only placeholder material for player capsules (URP Lit; colour set per slot with a property block).")]
        public Material playerMaterial;

        [Tooltip("Client-only unlit material (URP Unlit) for the Rushling screen face and effects.")]
        public Material unlitMaterial;

        public HazardSweeper[] sweepers = new HazardSweeper[0];
        public HazardPlatform[] platforms = new HazardPlatform[0];

        public static MapInfo Current { get; private set; }

        private void OnEnable() => Current = this;

        private void OnDisable()
        {
            if (Current == this)
                Current = null;
        }

        /// <summary>The map's hazards (same order on both sides) and the platform colliders the motor world moves.</summary>
        public HazardSet BuildHazards(out Collider[] platformColliders)
        {
            var sw = new SweeperDef[sweepers?.Length ?? 0];
            for (var i = 0; i < sw.Length; i++)
                sw[i] = sweepers[i].Resolve();
            var pl = new PlatformDef[platforms?.Length ?? 0];
            platformColliders = new Collider[pl.Length];
            for (var i = 0; i < pl.Length; i++)
            {
                pl[i] = platforms[i].Resolve();
                platformColliders[i] = platforms[i].motorCollider;
            }

            return new HazardSet(sw, pl);
        }

        /// <summary>PhysX-backed motor world for this map (server simulation or client prediction).</summary>
        public PhysxMotorWorld CreateWorld(Transform probeParent, MovementConfig cfg, out HazardSet hazards)
        {
            hazards = BuildHazards(out var cols);
            var probe = PhysxMotorWorld.CreateProbe(probeParent, cfg.Radius, cfg.Height);
            return new PhysxMotorWorld(gameObject.scene.GetPhysicsScene(), PhysxMotorWorld.GameplayMask(), probe, hazards, cols);
        }

        public Vec3[] SpawnPoints()
        {
            var r = new Vec3[Protocol.MaxPlayers];
            for (var i = 0; i < r.Length; i++)
            {
                var p = spawns != null && i < spawns.Length && spawns[i] != null ? spawns[i].position : new Vector3(-11f + 2f * i, 0.1f, 0f);
                r[i] = new Vec3(p.x, p.y, p.z);
            }

            return r;
        }
    }
}
