using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary><c>MALLET_SWING</c> (<see cref="MalletDef"/>): a pendulum mallet, <c>knock_hard</c> (stun 18 ticks, push 9 m/s, 5 m/s up); analytic head contact, no collider; the head glows during its wind-up (<see cref="MalletDef.TelegraphTicks"/>). Visuals are client-only and driven by the same pose function.</summary>
    public sealed class HazardMallet : HazardAuthoring
    {
        public MalletDef def;
        public MalletDef variantB;
        [Tooltip("Client-only visual root (no colliders).")]
        public Transform visual;

        public override HazardType Type => HazardType.MalletSwing;

        public MalletDef Resolve(in RoundParams p, bool zeroPhase = false)
        {
            var d = p.Variant == 1 && hasVariantB ? variantB : def;
            HazardResolve.SetPhase(ref d, zeroPhase ? 0 : PhaseFor(p.HazardSeed));
            HazardTuning.Apply(ref d, ModifierEffects.Get(p.Modifier));
            return d;
        }
    }
}
