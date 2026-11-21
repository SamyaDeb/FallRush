using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// <c>ROLLING_BALL</c> (<see cref="RollingBallDef"/>, G4.1a): a lane of balls rolling start → end, <c>knock_hard</c> along the
    /// roll direction. Analytic contact, no collider (a ball is a knock volume, not a surface). <see cref="balls"/> is a
    /// pool of client-only ball visuals (at least <see cref="HazardMath.BallMaxAlive"/>), posed and spun from the pure pose
    /// by <c>HazardViewDriver</c>; <see cref="chute"/> is the release light (telegraph). Visual only.
    /// </summary>
    public sealed class HazardRollingBall : HazardAuthoring
    {
        public RollingBallDef def;
        public RollingBallDef variantB;
        [Tooltip("Client-only ball visuals (no colliders), one per ball that can be alive at once.")]
        public Transform[] balls = new Transform[0];
        [Tooltip("Client-only chute light renderer (telegraph flash).")]
        public Renderer chute;

        public override HazardType Type => HazardType.RollingBall;

        public RollingBallDef Resolve(in RoundParams p, bool zeroPhase = false)
        {
            var d = p.Variant == 1 && hasVariantB ? variantB : def;
            HazardResolve.SetPhase(ref d, zeroPhase ? 0 : PhaseFor(p.HazardSeed));
            HazardTuning.Apply(ref d, ModifierEffects.Get(p.Modifier));
            return d;
        }
    }
}
