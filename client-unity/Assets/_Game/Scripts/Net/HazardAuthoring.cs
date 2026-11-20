using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// Scene data for one rotating sweeper (<see cref="SweeperDef"/>). No behaviour of its own: the simulation reads the
    /// def through <see cref="MapInfo.BuildHazards"/>; the client animates <see cref="barVisual"/> at render time.
    /// The bar has no collider — contact is the analytic <see cref="HazardMath.SweeperContact"/> on both sides.
    /// </summary>
    public sealed class HazardSweeper : MonoBehaviour
    {
        public SweeperDef def;
        [Tooltip("Hazard seed; the start angle is HazardMath.PhaseFromSeed(seed).")]
        public uint seed;
        [Tooltip("Rotated about Y by the client at the rendered tick (visual only).")]
        public Transform barVisual;

        public SweeperDef Resolve()
        {
            var d = def;
            d.Phase16 = HazardMath.PhaseFromSeed(seed);
            return d;
        }
    }

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
