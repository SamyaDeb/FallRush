using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// <c>SPINNER_BAR</c> (and the M2 sweeper): a bar rotating about a vertical post (<see cref="SweeperDef"/>). The bar has
    /// no collider: contact is the analytic <see cref="HazardMath.SweeperContact"/> on both sides. <see cref="barVisual"/>
    /// is rotated by the client at the rendered tick. Profile <c>knock_soft</c>: stun 11 ticks, push 6 m/s along the bar's
    /// travel. An empty <see cref="HazardAuthoring.obstacleId"/> keeps the M2 behaviour (phase from <see cref="seed"/>).
    /// </summary>
    public sealed class HazardSweeper : HazardAuthoring
    {
        public SweeperDef def;
        public SweeperDef variantB;
        [Tooltip("M2 only (no obstacleId): the start angle is HazardMath.PhaseFromSeed(seed).")]
        public uint seed;
        [Tooltip("Rotated about Y by the client at the rendered tick (visual only).")]
        public Transform barVisual;

        public override HazardType Type => HazardType.SpinnerBar;

        /// <summary>M2 form: variant A, no modifier, phase from <see cref="seed"/>.</summary>
        public SweeperDef Resolve()
        {
            var d = def;
            d.Phase16 = HazardMath.PhaseFromSeed(seed);
            return d;
        }

        public SweeperDef Resolve(in RoundParams p, bool zeroPhase = false)
        {
            var d = p.Variant == 1 && hasVariantB ? variantB : def;
            d.Phase16 = zeroPhase ? 0 : string.IsNullOrEmpty(obstacleId) ? HazardMath.PhaseFromSeed(seed) : PhaseFor(p.HazardSeed);
            HazardTuning.Apply(ref d, ModifierEffects.Get(p.Modifier));
            return d;
        }
    }
}
