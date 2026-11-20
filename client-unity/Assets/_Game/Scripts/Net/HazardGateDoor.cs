using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary><c>GATE_DOOR</c> (<see cref="GateDoorDef"/>): a timed door that lifts out of its doorway; motorCollider is posed per tick; a closing door pushes a player inside out along its normal. Visuals are client-only and driven by the same pose function.</summary>
    public sealed class HazardGateDoor : HazardAuthoring
    {
        public GateDoorDef def;
        public GateDoorDef variantB;
        public Collider motorCollider;
        [Tooltip("Client-only visual root (no colliders).")]
        public Transform visual;

        public override HazardType Type => HazardType.GateDoor;

        public GateDoorDef Resolve(in RoundParams p, bool zeroPhase = false)
        {
            var d = p.Variant == 1 && hasVariantB ? variantB : def;
            HazardResolve.SetPhase(ref d, zeroPhase ? 0 : PhaseFor(p.HazardSeed));
            HazardTuning.Apply(ref d, ModifierEffects.Get(p.Modifier));
            return d;
        }
    }
}
