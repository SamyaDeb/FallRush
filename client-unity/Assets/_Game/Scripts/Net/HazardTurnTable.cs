using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary><c>TURN_TABLE</c> (<see cref="TurnTableDef"/>): a disc rotating about its axis; the collider is a static round prism (rotation does not change its shape), riders are carried around it point-aware (carry_rot). Visuals are client-only and driven by the same pose function.</summary>
    public sealed class HazardTurnTable : HazardAuthoring
    {
        public TurnTableDef def;
        public TurnTableDef variantB;
        public Collider motorCollider;
        [Tooltip("Client-only visual root (no colliders).")]
        public Transform visual;

        public override HazardType Type => HazardType.TurnTable;

        public TurnTableDef Resolve(in RoundParams p, bool zeroPhase = false)
        {
            var d = p.Variant == 1 && hasVariantB ? variantB : def;
            HazardResolve.SetPhase(ref d, zeroPhase ? 0 : PhaseFor(p.HazardSeed));
            HazardTuning.Apply(ref d, ModifierEffects.Get(p.Modifier));
            return d;
        }
    }
}
