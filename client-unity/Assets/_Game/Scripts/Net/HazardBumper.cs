using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary><c>BUMPER</c> (<see cref="BumperDef"/>): a static round post (solid collider) with a radial knock_soft on touch. Visuals are client-only and driven by the same pose function.</summary>
    public sealed class HazardBumper : HazardAuthoring
    {
        public BumperDef def;
        public BumperDef variantB;
        public Collider motorCollider;
        [Tooltip("Client-only visual root (no colliders).")]
        public Transform visual;

        public override HazardType Type => HazardType.Bumper;

        public BumperDef Resolve(in RoundParams p, bool zeroPhase = false)
        {
            var d = p.Variant == 1 && hasVariantB ? variantB : def;
            HazardResolve.SetPhase(ref d, zeroPhase ? 0 : PhaseFor(p.HazardSeed));
            HazardTuning.Apply(ref d, ModifierEffects.Get(p.Modifier));
            return d;
        }
    }
}
