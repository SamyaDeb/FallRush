using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary><c>SPRING_PAD</c> (<see cref="SpringPadDef"/>): a static pad; landing on it launches the player (velocity override), once per landing. Visuals are client-only and driven by the same pose function.</summary>
    public sealed class HazardSpringPad : HazardAuthoring
    {
        public SpringPadDef def;
        public SpringPadDef variantB;
        public Collider motorCollider;
        [Tooltip("Client-only visual root (no colliders).")]
        public Transform visual;

        public override HazardType Type => HazardType.SpringPad;

        public SpringPadDef Resolve(in RoundParams p, bool zeroPhase = false)
        {
            var d = p.Variant == 1 && hasVariantB ? variantB : def;
            HazardResolve.SetPhase(ref d, zeroPhase ? 0 : PhaseFor(p.HazardSeed));
            HazardTuning.Apply(ref d, ModifierEffects.Get(p.Modifier));
            return d;
        }
    }
}
