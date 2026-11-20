using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// Scene data for one moving platform (<see cref="PlatformDef"/>). <see cref="motorCollider"/> (Gameplay layer, kinematic)
    /// is posed per simulated tick by <see cref="PhysxMotorWorld.BeginTick"/>; <see cref="visual"/> (no collider) is posed by
    /// the client at render time.
    /// </summary>
    public sealed class HazardPlatform : MonoBehaviour
    {
        public PlatformDef def;
        public uint seed;
        public Collider motorCollider;
        public Transform visual;

        public PlatformDef Resolve()
        {
            var d = def;
            d.Phase16 = HazardMath.PhaseFromSeed(seed);
            return d;
        }
    }
}
