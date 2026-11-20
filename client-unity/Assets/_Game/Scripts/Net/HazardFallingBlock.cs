using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary>Occupancy-triggered falling block (<see cref="FallingBlockDef"/>, Goo Tower): reactive; its collider is off while it has fallen.</summary>
    public sealed class HazardFallingBlock : HazardAuthoring
    {
        public FallingBlockDef def;
        public Collider motorCollider;
        [Tooltip("Client-only visual (shakes, falls, comes back).")]
        public Transform visual;

        public override HazardType Type => HazardType.FallingBlock;
    }
}
