using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// <c>MOVING_PLATFORM</c> (<see cref="PlatformDef"/>): the M2 sine shuttle, or a closed Catmull-Rom loop through
    /// integer knots. <see cref="motorCollider"/> (Gameplay layer, kinematic) is posed per simulated tick by
    /// <see cref="PhysxMotorWorld.BeginTick"/>; <see cref="visual"/> (no collider) is posed by the client at render time.
    /// Riders are carried in exact mm and keep the platform's horizontal velocity when they jump or dive off.
    /// </summary>
    public sealed class HazardPlatform : HazardAuthoring
    {
        public PlatformDef def;
        public PlatformDef variantB;
        public uint seed;
        public Collider motorCollider;
        public Transform visual;

        public override HazardType Type => HazardType.MovingPlatform;

        public PlatformDef Resolve()
        {
            var d = def;
            d.Phase16 = HazardMath.PhaseFromSeed(seed);
            return d;
        }

        public PlatformDef Resolve(in RoundParams p, bool zeroPhase = false)
        {
            var d = p.Variant == 1 && hasVariantB ? variantB : def;
            d.Phase16 = zeroPhase ? 0 : string.IsNullOrEmpty(obstacleId) ? HazardMath.PhaseFromSeed(seed) : PhaseFor(p.HazardSeed);
            HazardTuning.Apply(ref d, ModifierEffects.Get(p.Modifier));
            return d;
        }
    }
}
