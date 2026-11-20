using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary><c>GOO_PLANE</c> (<see cref="GooDef"/>): the rising kill plane of Goo Tower. Pure; the client draws the translucent purple plane at <see cref="HazardMath.GooYMm"/>.</summary>
    public sealed class HazardGoo : HazardAuthoring
    {
        public GooDef def;
        public GooDef variantB;
        [Tooltip("Client-only visual (the glitter plane, no collider).")]
        public Transform visual;

        public override HazardType Type => HazardType.GooPlane;

        public GooDef Resolve(in RoundParams p) => p.Variant == 1 && hasVariantB ? variantB : def;
    }
}
