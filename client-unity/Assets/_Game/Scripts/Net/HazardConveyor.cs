using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary><c>CONVEYOR</c> (<see cref="ConveyorDef"/>): a static belt; grounded riders are displaced by the belt velocity every tick (surface_push), through the same surface carry as platforms. Visuals are client-only and driven by the same pose function.</summary>
    public sealed class HazardConveyor : HazardAuthoring
    {
        public ConveyorDef def;
        public ConveyorDef variantB;
        public Collider motorCollider;
        [Tooltip("Client-only visual root (no colliders).")]
        public Transform visual;

        public override HazardType Type => HazardType.Conveyor;

        public ConveyorDef Resolve(in RoundParams p, bool zeroPhase = false)
        {
            var d = p.Variant == 1 && hasVariantB ? variantB : def;
            HazardResolve.SetPhase(ref d, zeroPhase ? 0 : PhaseFor(p.HazardSeed));
            HazardTuning.Apply(ref d, ModifierEffects.Get(p.Modifier));
            return d;
        }
    }
}
